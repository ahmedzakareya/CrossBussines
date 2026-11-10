using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Hospital;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL.Hospital
{
	// ==========================================================================================
	// HOSPITAL H20 — insurance accounting, fail-closed (the owner's rulings D2 and G1 on H18).
	//
	//   · HospitalClaimAccounts (static) is the ONE place an insurance account is resolved: the payer's receivable
	//     (its override, else the company's), the rejection and adjustment accounts, each checked to be this
	//     company's, active and postable. A database without the mapping tables reads as unmapped: the caller
	//     refuses, and nothing else is disturbed. It is read BEFORE any transaction opens.
	//   · HospitalClaimAccountingService posts a payer's answers (`bill`) — each event once (a unique index on the
	//     line and event, or the batch and remittance reference), through IHospitalLedger (the one door into the
	//     ledger), carrying the bill, the batch and the bill's closing journal. An answer whose event cannot post is
	//     NOT recorded: the caller's transaction rolls back.
	//   · A bill whose payer share was posted somewhere other than the payer receivable (closed before H20) is
	//     refused: it is settled by hand, never re-guessed.
	// ==========================================================================================

	public sealed class HospitalInsuranceAccountsInput
	{
		public int? PayerReceivableAccountId { get; set; }
		public int? ClaimRejectionAccountId { get; set; }
		public int? ClaimAdjustmentAccountId { get; set; }
	}

	public static class HospitalClaimAccounts
	{
		public const string NotSetUp = "Insurance accounting is not set up: map the payer receivable, rejection and adjustment accounts";

		/// The company's mapping, or the refusal when the mapping does not exist (no row, or no table yet).
		public static async Task<(HospitalInsuranceAccounts? Row, string? Refusal)> CompanyAsync(CrossDbContext db, BusinessContext ctx, CancellationToken ct)
		{
			try
			{
				var row = await db.Set<HospitalInsuranceAccounts>().AsNoTracking().FirstOrDefaultAsync(a => a.CompanyId == ctx.CompanyId, ct);
				return row == null ? (null, NotSetUp) : (row, null);
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number == 208)   // the mapping tables are not created yet
			{
				return (null, NotSetUp);
			}
		}

		/// The payer's receivable: its override, else the company's — validated, or the refusal.
		public static async Task<(int? Account, string? Refusal)> PayerReceivableAsync(CrossDbContext db, BusinessContext ctx, int payerId, CancellationToken ct)
		{
			var (row, refusal) = await CompanyAsync(db, ctx, ct);
			if (refusal != null) return (null, refusal);
			int? own = await db.Set<HospitalPayerAccount>().AsNoTracking().Where(p => p.CompanyId == ctx.CompanyId && p.PayerId == payerId).Select(p => (int?)p.ReceivableAccountId).FirstOrDefaultAsync(ct);
			var id = HospitalClaimAccountingRules.PayerReceivable(own, row!.PayerReceivableAccountId);
			if (id is not int a) return (null, "Map the payer receivable account first: closing an insured bill is refused until it is");
			if (!await OfKindAsync(db, ctx, a, ct, Asset)) return (null, "The payer receivable account is not an active asset posting account of this company");
			// the payer's receivable is never the patients' (D2), checked here so every path (close, answer, remit) gets it
			if (a == await PatientReceivableAsync(db, ctx, ct)) return (null, "The payer receivable cannot be the patients' receivable account");
			return (a, null);
		}

		public const string Asset = "ASSET", Revenue = "REV", Expense = "EXP";

		public static Task<bool> PostableAsync(CrossDbContext db, BusinessContext ctx, int accountId, CancellationToken ct) =>
			db.Accounts.AsNoTracking().AnyAsync(x => x.ID == accountId && x.CompanyID == ctx.CompanyId && x.IsActive && x.IsPostable, ct);

		/// An active posting account of this company whose type is one of `kinds` (AccountTypes.Code).
		public static Task<bool> OfKindAsync(CrossDbContext db, BusinessContext ctx, int accountId, CancellationToken ct, params string[] kinds) =>
			(from x in db.Accounts.AsNoTracking() join t in db.AccountTypes.AsNoTracking() on x.AccountTypeId equals t.ID
			 where x.ID == accountId && x.CompanyID == ctx.CompanyId && x.IsActive && x.IsPostable && kinds.Contains(t.Code) select x.ID).AnyAsync(ct);

		public static Task<int?> PatientReceivableAsync(CrossDbContext db, BusinessContext ctx, CancellationToken ct) =>
			db.HospitalBillingSettings.AsNoTracking().Where(s => s.CompanyId == ctx.CompanyId).Select(s => s.ReceivableAccountId).FirstOrDefaultAsync(ct);
	}

	public sealed class HospitalClaimAccountingService
	{
		private readonly CrossDbContext _db;
		private readonly IHospitalAccessService _access;
		private readonly IHospitalLedger _ledger;

		public HospitalClaimAccountingService(CrossDbContext db, IHospitalAccessService access, IHospitalLedger ledger) { _db = db; _access = access; _ledger = ledger; }

		public const string ConcurrentChange = HospitalInsuranceService.ConcurrentChange;
		private Task<bool> Can(BusinessContext ctx, string action, CancellationToken ct) => _access.CanAsync(ctx, action, null, ct);

		// ---------------- setup ----------------

		public async Task<HospitalResult> SaveAccountsAsync(BusinessContext ctx, HospitalInsuranceAccountsInput input, CancellationToken ct = default)
		{
			if (!await Can(ctx, HospitalActions.Manage, ct)) return HospitalResult.Fail("forbidden");
			// each role in its kind: the receivable an asset, the adjustments and rejections revenue (a contra) or expense
			if (input.PayerReceivableAccountId is int ar && !await HospitalClaimAccounts.OfKindAsync(_db, ctx, ar, ct, HospitalClaimAccounts.Asset))
				return HospitalResult.Fail("The payer receivable must be an active asset posting account");
			foreach (var a in new[] { input.ClaimRejectionAccountId, input.ClaimAdjustmentAccountId })
				if (a is int id && !await HospitalClaimAccounts.OfKindAsync(_db, ctx, id, ct, HospitalClaimAccounts.Revenue, HospitalClaimAccounts.Expense))
					return HospitalResult.Fail("Claim adjustments and rejections post to an active revenue or expense account");
			var refusal = HospitalClaimAccountingRules.MappingRefusal(input.PayerReceivableAccountId, input.ClaimRejectionAccountId, input.ClaimAdjustmentAccountId,
				await HospitalClaimAccounts.PatientReceivableAsync(_db, ctx, ct));
			if (refusal != null) return HospitalResult.Fail(refusal);
			try
			{
				var row = await _db.Set<HospitalInsuranceAccounts>().FirstOrDefaultAsync(x => x.CompanyId == ctx.CompanyId, ct);
				if (row == null) { row = new HospitalInsuranceAccounts { CompanyId = ctx.CompanyId }; _db.Add(row); }
				row.PayerReceivableAccountId = input.PayerReceivableAccountId; row.ClaimRejectionAccountId = input.ClaimRejectionAccountId; row.ClaimAdjustmentAccountId = input.ClaimAdjustmentAccountId;
				await _db.SaveChangesAsync(ct);
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number == 208) { _db.ChangeTracker.Clear(); return HospitalResult.Fail("The insurance accounting tables are not created yet (hospital_h20_claim_accounting.sql)"); }
			catch (DbUpdateException) { _db.ChangeTracker.Clear(); return HospitalResult.Fail(ConcurrentChange); }
			return HospitalResult.Success();
		}

		/// A payer's own receivable (null removes the override: the company's applies again).
		public async Task<HospitalResult> SavePayerAccountAsync(BusinessContext ctx, int payerId, int? accountId, CancellationToken ct = default)
		{
			if (!await Can(ctx, HospitalActions.Manage, ct)) return HospitalResult.Fail("forbidden");
			if (!await _db.Set<HospitalPayer>().AnyAsync(p => p.ID == payerId && p.CompanyId == ctx.CompanyId, ct)) return HospitalResult.Fail("not_found");
			if (accountId is int a)
			{
				if (!await HospitalClaimAccounts.OfKindAsync(_db, ctx, a, ct, HospitalClaimAccounts.Asset)) return HospitalResult.Fail("The payer receivable must be an active asset posting account");
				var (company, _) = await HospitalClaimAccounts.CompanyAsync(_db, ctx, ct);
				var refusal = HospitalClaimAccountingRules.MappingRefusal(a, company?.ClaimRejectionAccountId, company?.ClaimAdjustmentAccountId, await HospitalClaimAccounts.PatientReceivableAsync(_db, ctx, ct));
				if (refusal != null) return HospitalResult.Fail(refusal);
			}
			try
			{
				var row = await _db.Set<HospitalPayerAccount>().FirstOrDefaultAsync(x => x.CompanyId == ctx.CompanyId && x.PayerId == payerId, ct);
				if (accountId is not int acc) { if (row != null) _db.Remove(row); }
				else if (row == null) _db.Add(new HospitalPayerAccount { CompanyId = ctx.CompanyId, PayerId = payerId, ReceivableAccountId = acc });
				else row.ReceivableAccountId = acc;
				await _db.SaveChangesAsync(ct);
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number == 208) { _db.ChangeTracker.Clear(); return HospitalResult.Fail("The insurance accounting tables are not created yet (hospital_h20_claim_accounting.sql)"); }
			catch (DbUpdateException) { _db.ChangeTracker.Clear(); return HospitalResult.Fail(ConcurrentChange); }
			return HospitalResult.Success(payerId);
		}

		/// The company's mapping and the payers' overrides; Ready = false when the tables are not created yet.
		public async Task<(bool Ready, HospitalInsuranceAccounts? Company, IReadOnlyDictionary<int, int> Payers)> MappingAsync(BusinessContext ctx, CancellationToken ct = default)
		{
			try
			{
				var row = await _db.Set<HospitalInsuranceAccounts>().AsNoTracking().FirstOrDefaultAsync(a => a.CompanyId == ctx.CompanyId, ct);
				var payers = await _db.Set<HospitalPayerAccount>().AsNoTracking().Where(p => p.CompanyId == ctx.CompanyId).ToDictionaryAsync(p => p.PayerId, p => p.ReceivableAccountId, ct);
				return (true, row, payers);
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number == 208) { return (false, null, new Dictionary<int, int>()); }
		}

		/// A batch's ledger: what the payer still owes on it, and what posted (null when the tables are not created yet).
		/// `onBills` narrows what is owed to those bills' lines: a payment clears only the bills whose payer share sits on
		/// the receivable it credits (answers and payments never post for the others, so their postings need no filter).
		public async Task<(decimal Outstanding, IReadOnlyList<HospitalClaimPosting> Postings)?> BatchLedgerAsync(BusinessContext ctx, int batchId, CancellationToken ct = default,
			IReadOnlyCollection<int>? onBills = null)
		{
			try
			{
				var lines = from l in _db.Set<HospitalClaimLine>() join c in _db.Set<HospitalClaim>() on l.ClaimId equals c.ID
							where c.BatchId == batchId && l.CompanyId == ctx.CompanyId && l.ResubmitsLineId == null select new { l.InsurerAmount, c.BillId };
				if (onBills != null) lines = lines.Where(x => onBills.Contains(x.BillId));
				var original = await lines.SumAsync(x => (decimal?)x.InsurerAmount, ct) ?? 0;
				var posted = await _db.Set<HospitalClaimPosting>().AsNoTracking().Where(p => p.CompanyId == ctx.CompanyId && p.BatchId == batchId).OrderBy(p => p.ID).ToListAsync(ct);
				return (HospitalClaimAccountingRules.Outstanding(original, posted.Select(p => (p.Event, p.Amount))), posted);
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number == 208) { return null; }
		}

		// ---------------- the payer's answers ----------------

		/// Posts what a payer's answer on a claim line calls for (nothing, when the receivable simply stands).
		/// Called by HospitalInsuranceService.RecordResponseAsync BEFORE it records the answer, inside its transaction:
		/// a refusal here means the answer is not recorded.
		public async Task<HospitalResult> PostAnswerAsync(BusinessContext ctx, HospitalClaimLine line, HospitalClaim claim, HospitalClaimBatch batch, bool accept, decimal? acceptedAmount,
			int? employeeId, DateTime nowUtc, HospitalInsuranceAccounts? accounts, int? payerReceivable, CancellationToken ct = default)
		{
			var plan = HospitalClaimAccountingRules.Plan(accept, line.InsurerAmount, acceptedAmount, line.ResubmitsLineId != null);
			if (plan == null) return HospitalResult.Success();
			if (payerReceivable is not int ar || accounts == null) return HospitalResult.Fail(HospitalClaimAccounts.NotSetUp);
			int? counter = plan.Event is HospitalClaimEvent.Rejection or HospitalClaimEvent.Recovery ? accounts.ClaimRejectionAccountId : accounts.ClaimAdjustmentAccountId;
			if (counter is not int cn) return HospitalResult.Fail(plan.Event == HospitalClaimEvent.Adjustment ? "Map the claim adjustment account first" : "Map the claim rejection account first");
			if (!await HospitalClaimAccounts.OfKindAsync(_db, ctx, cn, ct, HospitalClaimAccounts.Revenue, HospitalClaimAccounts.Expense))
				return HospitalResult.Fail("Claim adjustments and rejections post to an active revenue or expense account");
			if (plan.Event == HospitalClaimEvent.Recovery)
			{
				// only what was written off can be recovered: walk back to the original line and find its rejection
				int root = line.ResubmitsLineId!.Value;
				for (int hop = 0; hop < 20; hop++)
				{
					var prev = await _db.Set<HospitalClaimLine>().AsNoTracking().Where(x => x.ID == root && x.CompanyId == ctx.CompanyId).Select(x => x.ResubmitsLineId).FirstOrDefaultAsync(ct);
					if (prev is not int p) break;
					root = p;
				}
				if (!await _db.Set<HospitalClaimPosting>().AnyAsync(x => x.CompanyId == ctx.CompanyId && x.ClaimLineId == root && x.Event == HospitalClaimEvent.Rejection, ct))
					return HospitalResult.Fail("The original rejection was never written off in the ledger: settle this line by hand");
			}
			var bill = await _db.HospitalBills.AsNoTracking().FirstAsync(b => b.ID == claim.BillId && b.CompanyId == ctx.CompanyId, ct);
			var origin = await OriginRefusalAsync(ctx, bill, ar, ct);
			if (origin != null) return HospitalResult.Fail(origin);
			string memo = bill.BillNo + " · " + batch.BatchNo + " · " + line.Code;
			return await PostAsync(ctx, batch.ID, line.ID, plan.Event, null, plan.Amount, bill, HospitalClaimAccountingRules.Lines(plan.Event, plan.Amount, ar, cn, memo),
				"تسوية مطالبة " + batch.BatchNo, "Claim answer " + batch.BatchNo + " (" + plan.Event + ")", employeeId, nowUtc, ct);
		}

		/// H82: a recoupment — the payer takes back part or all of an accepted line. Dr claim rejections / Cr payer receivable
		/// (the receivable falls by what the payer no longer owes, or turns to its credit when it had already paid).
		public async Task<HospitalResult> PostRecoupAsync(BusinessContext ctx, HospitalClaimLine line, HospitalClaim claim, HospitalClaimBatch batch, decimal amount,
			int? employeeId, DateTime nowUtc, HospitalInsuranceAccounts accounts, int payerReceivable, CancellationToken ct = default)
		{
			if (accounts.ClaimRejectionAccountId is not int cn) return HospitalResult.Fail("Map the claim rejection account first");
			if (!await HospitalClaimAccounts.OfKindAsync(_db, ctx, cn, ct, HospitalClaimAccounts.Revenue, HospitalClaimAccounts.Expense))
				return HospitalResult.Fail("Claim adjustments and rejections post to an active revenue or expense account");
			var bill = await _db.HospitalBills.AsNoTracking().FirstAsync(b => b.ID == claim.BillId && b.CompanyId == ctx.CompanyId, ct);
			if (await OriginRefusalAsync(ctx, bill, payerReceivable, ct) is string origin) return HospitalResult.Fail(origin);
			string memo = bill.BillNo + " · " + batch.BatchNo + " · " + line.Code;
			return await PostAsync(ctx, batch.ID, line.ID, HospitalClaimEvent.Recoupment, null, amount, bill, HospitalClaimAccountingRules.Lines(HospitalClaimEvent.Recoupment, amount, payerReceivable, cn, memo),
				"استرداد شركة التأمين " + batch.BatchNo, "Payer recoupment " + batch.BatchNo, employeeId, nowUtc, ct);
		}

		/// A payer's payment against a batch, full or partial: Dr bank / Cr payer receivable, once per reference.
		public async Task<HospitalResult> RemitAsync(BusinessContext ctx, int batchId, decimal amount, string? reference, int? employeeId, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await Can(ctx, HospitalActions.Bill, ct)) return HospitalResult.Fail("forbidden");
			var batch = await _db.Set<HospitalClaimBatch>().AsNoTracking().FirstOrDefaultAsync(b => b.ID == batchId && b.CompanyId == ctx.CompanyId, ct);
			if (batch == null) return HospitalResult.Fail("not_found");
			if (batch.Status == HospitalClaimBatchStatus.Draft) return HospitalResult.Fail("Submit the batch before recording the payer's payment");
			var (ar, why) = await HospitalClaimAccounts.PayerReceivableAsync(_db, ctx, batch.PayerId, ct);
			if (why != null) return HospitalResult.Fail(why);
			var bank = await _db.HospitalBillingSettings.AsNoTracking().Where(s => s.CompanyId == ctx.CompanyId).Select(s => s.BankAccountId).FirstOrDefaultAsync(ct);
			if (bank is not int bk || !await HospitalClaimAccounts.PostableAsync(_db, ctx, bk, ct)) return HospitalResult.Fail("Map the bank account in billing settings first");
			// the payment clears only the bills that put their payer share on this receivable. A bill closed before
			// insurance accounting (or in a foreign currency) is settled by hand and no longer blocks the rest of its batch;
			// a batch with no such bill at all is refused with the reason.
			var billIds = await _db.Set<HospitalClaim>().AsNoTracking().Where(c => c.BatchId == batchId && c.CompanyId == ctx.CompanyId).Select(c => c.BillId).Distinct().ToListAsync(ct);
			var onBills = new List<int>(); string? firstWhy = null;
			foreach (var bid in billIds)
			{
				var b = await _db.HospitalBills.AsNoTracking().FirstAsync(x => x.ID == bid && x.CompanyId == ctx.CompanyId, ct);
				if (await OriginRefusalAsync(ctx, b, ar!.Value, ct) is string o) firstWhy ??= o; else onBills.Add(bid);
			}
			if (onBills.Count == 0) return HospitalResult.Fail(firstWhy ?? "not_found");
			// what is still owed on those bills — a first look before anything is locked; the decisive check repeats under the lock below
			if (await BatchLedgerAsync(ctx, batchId, ct, onBills) is not { } ledger) return HospitalResult.Fail(HospitalClaimAccounts.NotSetUp);
			var refusal = HospitalClaimAccountingRules.RemittanceRefusal(amount, ledger.Outstanding, reference);
			if (refusal != null) return HospitalResult.Fail(refusal, ledger.Outstanding.ToString("0.###"));
			string refNo = reference!.Trim()[..Math.Min(60, reference.Trim().Length)];
			string memo = batch.BatchNo + " · " + refNo;
			await using var tx = await ScopedTx.BeginOrJoinAsync(_db);
			// two remittances with different references must not both pass "not more than owed": touching the batch row
			// holds its lock until commit, so a second remittance waits, then reads the first one's posting
			await _db.Set<HospitalClaimBatch>().Where(b => b.ID == batchId && b.CompanyId == ctx.CompanyId).ExecuteUpdateAsync(u => u.SetProperty(b => b.Status, b => b.Status), ct);
			var locked = await BatchLedgerAsync(ctx, batchId, ct, onBills);
			var again = locked == null ? HospitalClaimAccounts.NotSetUp : HospitalClaimAccountingRules.RemittanceRefusal(amount, locked.Value.Outstanding, reference);
			if (again != null) { await tx.RollbackAsync(); return HospitalResult.Fail(again, (locked?.Outstanding ?? 0).ToString("0.###")); }
			var r = await PostAsync(ctx, batchId, null, HospitalClaimEvent.Remittance, refNo, amount, null,
				new[] { new HospitalLedgerLine(bk, amount, 0, memo), new HospitalLedgerLine(ar!.Value, 0, amount, memo) },
				"تحصيل مطالبة " + batch.BatchNo, "Claim remittance " + batch.BatchNo, employeeId, nowUtc, ct);
			if (!r.Ok) { await tx.RollbackAsync(); return r; }
			await tx.CommitAsync();
			return r;
		}

		/// The payer's share of this bill must have been posted to the payer receivable being credited now.
		private async Task<string?> OriginRefusalAsync(BusinessContext ctx, HospitalBill bill, int payerReceivable, CancellationToken ct)
		{
			if (bill.JournalEntryId is not int je) return "This bill has no closing journal to settle against";
			// a foreign-currency bill's journal holds the facility-currency amount while its claim is in the bill's
			// currency: its answers and payments cannot be matched safely yet, so they are refused by name
			if (bill.CurrencyId != null) return "A foreign-currency bill's payer share is settled by hand for now";
			var cov = await HospitalCoverage.FrozenAsync(_db, ctx, bill.ID, ct);
			if (cov == null) return "This bill was not split when it closed: its payer share was never posted";
			bool there = await _db.JournalEntryLines.AsNoTracking().AnyAsync(l => l.JournalEntryId == je && l.AccountId == payerReceivable && l.Debit == cov.Value.Head.InsurerShare, ct);
			return there ? null : "This bill's payer share was not posted to the payer receivable (closed before insurance accounting): settle it by hand";
		}

		private async Task<HospitalResult> PostAsync(BusinessContext ctx, int batchId, int? lineId, string ev, string? reference, decimal amount, HospitalBill? bill,
			IReadOnlyList<HospitalLedgerLine> lines, string description, string descriptionEn, int? employeeId, DateTime nowUtc, CancellationToken ct)
		{
			// once: the index refuses a second posting of the same line and event, or the same remittance reference
			if (await _db.Set<HospitalClaimPosting>().AnyAsync(p => p.CompanyId == ctx.CompanyId && p.BatchId == batchId
					&& (lineId != null ? p.ClaimLineId == lineId && p.Event == ev : p.Reference == reference), ct))
				return HospitalResult.Fail("This answer is already posted");
			var (ok, error, entryId) = await _ledger.PostAsync(ctx, nowUtc, description, descriptionEn, "HospitalClaimPosting", lineId ?? batchId, lines, ct);
			if (!ok || entryId is not int jid) return HospitalResult.Fail("The ledger refused the posting: {0}", error ?? "");
			_db.Add(new HospitalClaimPosting
			{
				CompanyId = ctx.CompanyId, BatchId = batchId, ClaimLineId = lineId, Event = ev, Reference = reference, Amount = amount, BillId = bill?.ID,
				OriginJournalEntryId = bill?.JournalEntryId, JournalEntryId = jid, PostedAt = nowUtc, PostedByEmployeeId = employeeId,
			});
			try { await _db.SaveChangesAsync(ct); }
			catch (DbUpdateException) { _db.ChangeTracker.Clear(); return HospitalResult.Fail("This answer is already posted"); }
			return HospitalResult.Success(jid);
		}
	}
}
