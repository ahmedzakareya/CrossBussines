using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Hospital;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL.Hospital
{
	// ==========================================================================================
	// HOSPITAL H143 — the patient's deposit at the counter (see HospitalPatientDeposits.cs for the model and the postings).
	//   · TAKE: into the cashier's open shift; cash or bank against the patient-deposits account.
	//   · USE on a bill: drawn from the patient's oldest deposits first, never more than what is left nor more than the
	//     bill's due; each draw is a payment of method "Deposit" on the bill, posted against patient receivables. Each
	//     draw is a conditional update (only while that much is left), so two counters cannot spend one deposit twice.
	//   · REFUND: from the cashier's open shift, with a reason; the patient-deposits account against cash or bank.
	// A foreign-currency bill is not paid from a deposit (the deposit is in the facility's currency; the bill's exchange
	// difference is collection's business). Every posting goes through the hospital ledger seam, never around it.
	// ==========================================================================================

	public sealed record HospitalDepositView(HospitalPatientDeposit Deposit, decimal Left);

	public static class HospitalDepositRules
	{
		public static decimal Left(HospitalPatientDeposit d) => Math.Max(0, d.Amount - d.Used - d.Refunded);

		public static string? RefuseTake(decimal amount, string? method, string? purpose)
		{
			if (amount <= 0) return "Enter an amount above zero";
			if (!HospitalPaymentMethods.IsKnown(method)) return "Unknown payment method";
			if (!HospitalDepositPurpose.IsKnown(purpose)) return "Unknown deposit purpose";
			return null;
		}

		/// How much each deposit gives to a use of `amount`, the oldest first. Null when they do not hold that much.
		public static IReadOnlyList<(int DepositId, decimal Amount)>? Draw(IEnumerable<(int Id, DateTime At, decimal Left)> deposits, decimal amount)
		{
			var plan = new List<(int, decimal)>(); decimal need = amount;
			foreach (var d in deposits.Where(x => x.Left > 0).OrderBy(x => x.At).ThenBy(x => x.Id))
			{
				if (need <= 0) break;
				var take = Math.Min(need, d.Left); plan.Add((d.Id, take)); need -= take;
			}
			return need > 0 ? null : plan;
		}

		public static string? RefuseRefund(decimal amount, decimal left, string? method, string? reason)
		{
			if (amount <= 0) return "Enter an amount above zero";
			if (amount > left) return "The refund is more than is left on the deposit";
			if (!HospitalPaymentMethods.IsKnown(method)) return "Unknown payment method";
			if (string.IsNullOrWhiteSpace(reason) || reason.Trim().Length < 3) return "Write the reason for the refund";
			return null;
		}
	}

	public sealed class HospitalDepositService
	{
		private readonly CrossDbContext _db;
		private readonly IHospitalAccessService _access;
		private readonly IHospitalLedger _ledger;
		private readonly HospitalBillingService _billing;

		public HospitalDepositService(CrossDbContext db, IHospitalAccessService access, IHospitalLedger ledger, HospitalBillingService billing)
		{ _db = db; _access = access; _ledger = ledger; _billing = billing; }

		private IQueryable<HospitalPatientDeposit> Deposits(BusinessContext ctx) => _db.Set<HospitalPatientDeposit>().Where(d => d.CompanyId == ctx.CompanyId);

		private Task<HospitalCashierShift?> ShiftAsync(BusinessContext ctx, int employeeId, CancellationToken ct)
			=> _db.HospitalCashierShifts.AsNoTracking().FirstOrDefaultAsync(s => s.CompanyId == ctx.CompanyId && s.CashierEmployeeId == employeeId && s.Status == HospitalShiftStatus.Open, ct);

		private static async Task<string> NextAsync(IQueryable<string> issued, string prefix, CancellationToken ct)
		{
			var nums = await issued.Where(n => n.StartsWith(prefix)).ToListAsync(ct);
			int max = 0;
			foreach (var n in nums) if (int.TryParse(n.AsSpan(prefix.Length), out var v) && v > max) max = v;
			return prefix + (max + 1).ToString("000000");
		}

		public async Task<IReadOnlyList<HospitalDepositView>> ListAsync(BusinessContext ctx, int patientId, CancellationToken ct = default)
			=> (await Deposits(ctx).AsNoTracking().Where(d => d.PatientId == patientId).OrderByDescending(d => d.ReceivedAt).ToListAsync(ct))
				.Select(d => new HospitalDepositView(d, HospitalDepositRules.Left(d))).ToList();

		public async Task<decimal> LeftAsync(BusinessContext ctx, int patientId, CancellationToken ct = default)
		{
			try { return await Deposits(ctx).Where(d => d.PatientId == patientId).SumAsync(d => d.Amount - d.Used - d.Refunded, ct); }
			catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number == 208) { return 0; }
		}

		public async Task<HospitalResult> TakeAsync(BusinessContext ctx, int patientId, decimal amount, string? method, string? purpose, string? note, int employeeId, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await _access.CanAsync(ctx, HospitalActions.Bill, null, ct)) return HospitalResult.Fail("forbidden");
			amount = Math.Round(amount, 4);
			var why = HospitalDepositRules.RefuseTake(amount, method, purpose);
			if (why != null) return HospitalResult.Fail(why);
			if (!await _db.HospitalPatients.AnyAsync(p => p.ID == patientId && p.CompanyId == ctx.CompanyId && p.MergedIntoPatientId == null, ct)) return HospitalResult.Fail("not_found");
			var shift = await ShiftAsync(ctx, employeeId, ct);
			if (shift == null) return HospitalResult.Fail("Open your cashier shift before taking a payment");
			var s = await _billing.SettingsAsync(ctx, ct);
			int? debit = method == HospitalPaymentMethods.Cash ? s.CashAccountId : s.BankAccountId;
			if (debit == null) return HospitalResult.Fail("Map the cash, bank and receivable accounts in billing settings");
			if (s.PatientDepositAccountId == null) return HospitalResult.Fail("Map the patient deposits account in billing settings");
			note = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
			if (note?.Length > 300) note = note[..300];

			await using var tx = await _db.Database.BeginTransactionAsync(ct);
			var d = new HospitalPatientDeposit
			{
				CompanyId = ctx.CompanyId, PatientId = patientId, Purpose = purpose!, Note = note, Amount = amount, Method = method!, ShiftId = shift.ID,
				CashierEmployeeId = employeeId, ReceivedAt = nowUtc, DepositNo = await NextAsync(Deposits(ctx).Select(x => x.DepositNo), "DEP-", ct),
			};
			_db.Add(d);
			await _db.SaveChangesAsync(ct);
			var (ok, error, entryId) = await _ledger.PostAsync(ctx, nowUtc, "عربون " + d.DepositNo, "Patient deposit " + d.DepositNo, "HospitalPatientDeposit", d.ID,
				new[] { new HospitalLedgerLine(debit.Value, amount, 0, d.DepositNo), new HospitalLedgerLine(s.PatientDepositAccountId.Value, 0, amount, d.DepositNo) }, ct);
			if (!ok) return HospitalResult.Fail("The ledger refused the posting: {0}", error ?? "");
			d.JournalEntryId = entryId;
			await _db.SaveChangesAsync(ct);
			await tx.CommitAsync(ct);
			return HospitalResult.Success(d.ID);
		}

		/// Pays `amount` of a bill from the patient's deposits, the oldest first.
		public async Task<HospitalResult> UseAsync(BusinessContext ctx, int billId, decimal amount, int employeeId, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await _access.CanAsync(ctx, HospitalActions.Bill, null, ct)) return HospitalResult.Fail("forbidden");
			amount = Math.Round(amount, 4);
			if (amount <= 0) return HospitalResult.Fail("Enter an amount above zero");
			var v = await _billing.BillAsync(ctx, billId, nowUtc, ct);
			if (v == null) return HospitalResult.Fail("not_found");
			if (v.Foreign) return HospitalResult.Fail("A deposit pays only a bill in the facility's currency");
			if (amount > v.Totals.Due) return HospitalResult.Fail("The amount is more than the patient owes ({0})", v.Totals.Due.ToString("0.###"));
			var shift = await ShiftAsync(ctx, employeeId, ct);
			if (shift == null) return HospitalResult.Fail("Open your cashier shift before taking a payment");
			var s = await _billing.SettingsAsync(ctx, ct);
			if (s.PatientDepositAccountId == null) return HospitalResult.Fail("Map the patient deposits account in billing settings");
			if (s.ReceivableAccountId == null) return HospitalResult.Fail("Map the cash, bank and receivable accounts in billing settings");
			var held = await Deposits(ctx).AsNoTracking().Where(d => d.PatientId == v.Bill.PatientId).Select(d => new { d.ID, d.ReceivedAt, Left = d.Amount - d.Used - d.Refunded }).ToListAsync(ct);
			var plan = HospitalDepositRules.Draw(held.Select(h => (h.ID, h.ReceivedAt, h.Left)), amount);
			if (plan == null) return HospitalResult.Fail("The deposits hold less than that ({0})", held.Sum(h => Math.Max(0, h.Left)).ToString("0.###"));

			await using var tx = await _db.Database.BeginTransactionAsync(ct);
			foreach (var (depositId, take) in plan)
			{
				// only while that much is still left: a deposit spent by another counter meanwhile refuses here
				int changed = await Deposits(ctx).Where(d => d.ID == depositId && d.Amount - d.Used - d.Refunded >= take)
					.ExecuteUpdateAsync(u => u.SetProperty(d => d.Used, d => d.Used + take), ct);
				if (changed != 1) return HospitalResult.Fail("The deposit changed meanwhile; try again");
				var depNo = await Deposits(ctx).Where(d => d.ID == depositId).Select(d => d.DepositNo).FirstAsync(ct);
				var pay = new HospitalPayment
				{
					CompanyId = ctx.CompanyId, BillId = v.Bill.ID, ShiftId = shift.ID, Amount = take, Method = HospitalPaymentMethods.Deposit, ReceivedAt = nowUtc, CashierEmployeeId = employeeId,
					ReceiptNo = await NextAsync(_db.HospitalPayments.Where(p => p.CompanyId == ctx.CompanyId).Select(p => p.ReceiptNo), "RCPT-", ct),
				};
				_db.HospitalPayments.Add(pay);
				await _db.SaveChangesAsync(ct);
				var (ok, error, entryId) = await _ledger.PostAsync(ctx, nowUtc, "استخدام العربون " + depNo + " — " + v.Bill.BillNo, "Deposit " + depNo + " used on " + v.Bill.BillNo, "HospitalPayment", pay.ID,
					new[] { new HospitalLedgerLine(s.PatientDepositAccountId.Value, take, 0, pay.ReceiptNo), new HospitalLedgerLine(s.ReceivableAccountId.Value, 0, take, pay.ReceiptNo) }, ct);
				if (!ok) return HospitalResult.Fail("The ledger refused the posting: {0}", error ?? "");
				pay.JournalEntryId = entryId;
				_db.Add(new HospitalDepositMove { CompanyId = ctx.CompanyId, DepositId = depositId, Kind = HospitalDepositMoveKind.Use, Amount = take, BillId = v.Bill.ID, PaymentId = pay.ID, ShiftId = shift.ID, At = nowUtc, ByEmployeeId = employeeId, JournalEntryId = entryId });
				await _db.SaveChangesAsync(ct);
			}
			await tx.CommitAsync(ct);
			return HospitalResult.Success(v.Bill.ID);
		}

		public async Task<HospitalResult> RefundAsync(BusinessContext ctx, int depositId, decimal amount, string? method, string? reason, int employeeId, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await _access.CanAsync(ctx, HospitalActions.Bill, null, ct)) return HospitalResult.Fail("forbidden");
			amount = Math.Round(amount, 4);
			var d = await Deposits(ctx).AsNoTracking().FirstOrDefaultAsync(x => x.ID == depositId, ct);
			if (d == null) return HospitalResult.Fail("not_found");
			var why = HospitalDepositRules.RefuseRefund(amount, HospitalDepositRules.Left(d), method, reason);
			if (why != null) return HospitalResult.Fail(why);
			var shift = await ShiftAsync(ctx, employeeId, ct);
			if (shift == null) return HospitalResult.Fail("Open your cashier shift before taking a payment");
			var s = await _billing.SettingsAsync(ctx, ct);
			int? credit = method == HospitalPaymentMethods.Cash ? s.CashAccountId : s.BankAccountId;
			if (credit == null) return HospitalResult.Fail("Map the cash, bank and receivable accounts in billing settings");
			if (s.PatientDepositAccountId == null) return HospitalResult.Fail("Map the patient deposits account in billing settings");
			reason = reason!.Trim(); if (reason.Length > 300) reason = reason[..300];

			await using var tx = await _db.Database.BeginTransactionAsync(ct);
			int changed = await Deposits(ctx).Where(x => x.ID == depositId && x.Amount - x.Used - x.Refunded >= amount)
				.ExecuteUpdateAsync(u => u.SetProperty(x => x.Refunded, x => x.Refunded + amount), ct);
			if (changed != 1) return HospitalResult.Fail("The deposit changed meanwhile; try again");
			var move = new HospitalDepositMove { CompanyId = ctx.CompanyId, DepositId = depositId, Kind = HospitalDepositMoveKind.Refund, Amount = amount, Method = method, ShiftId = shift.ID, Reason = reason, At = nowUtc, ByEmployeeId = employeeId };
			_db.Add(move);
			await _db.SaveChangesAsync(ct);
			var (ok, error, entryId) = await _ledger.PostAsync(ctx, nowUtc, "رد العربون " + d.DepositNo, "Deposit " + d.DepositNo + " refunded", "HospitalDepositMove", move.ID,
				new[] { new HospitalLedgerLine(s.PatientDepositAccountId.Value, amount, 0, d.DepositNo), new HospitalLedgerLine(credit.Value, 0, amount, d.DepositNo) }, ct);
			if (!ok) return HospitalResult.Fail("The ledger refused the posting: {0}", error ?? "");
			move.JournalEntryId = entryId;
			await _db.SaveChangesAsync(ct);
			await tx.CommitAsync(ct);
			return HospitalResult.Success(move.ID);
		}

		/// What a shift took and gave back in deposits, by method (for the till).
		public static async Task<(Dictionary<string, decimal> In, Dictionary<string, decimal> Out, int Count)> ShiftMovesAsync(CrossDbContext db, int companyId, int shiftId, CancellationToken ct)
		{
			try
			{
				var taken = await db.Set<HospitalPatientDeposit>().AsNoTracking().Where(d => d.CompanyId == companyId && d.ShiftId == shiftId).Select(d => new { d.Method, d.Amount }).ToListAsync(ct);
				var back = await db.Set<HospitalDepositMove>().AsNoTracking().Where(m => m.CompanyId == companyId && m.ShiftId == shiftId && m.Kind == HospitalDepositMoveKind.Refund).Select(m => new { m.Method, m.Amount }).ToListAsync(ct);
				return (taken.GroupBy(x => x.Method).ToDictionary(g => g.Key, g => g.Sum(x => x.Amount)),
					back.Where(x => x.Method != null).GroupBy(x => x.Method!).ToDictionary(g => g.Key, g => g.Sum(x => x.Amount)), taken.Count);
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number == 208) { return (new(), new(), 0); }
		}
	}
}
