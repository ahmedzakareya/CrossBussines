using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Hospital;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL.Hospital
{
	// ==========================================================================================
	// HOSPITAL H63 — charity cases (HospitalCharityGrant). The billing desk (`bill`) requests a waiver on a closed bill's
	// outstanding patient balance from a fund; the hospital administrator (`manage`), who is not the requester, approves
	// or refuses it once. Approving posts Dr fund expense / Cr patients' receivable through the hospital ledger, in the
	// same transaction as the decision. A foreign-currency bill is not waived here (its receivable stands at a rate).
	// ==========================================================================================

	public sealed class HospitalCharityFundInput
	{
		public string? Code { get; set; } public string? Name { get; set; } public string? NameEn { get; set; } public int? AccountId { get; set; } public bool IsActive { get; set; } = true;
	}

	public sealed class HospitalCharityRequestInput { public int? FundId { get; set; } public decimal Amount { get; set; } public string? Reason { get; set; } }

	public sealed record HospitalCharityRow(HospitalCharityGrant Grant, string BillNo, string PatientName, string PatientNameEn, string Mrn, string? PatientPhoto,
		string Fund, string FundEn, string RequestedBy, string RequestedByEn, string? DecidedBy, string? DecidedByEn);

	public static class HospitalCharityRules
	{
		public static string? FundRefusal(HospitalCharityFundInput i)
		{
			if (string.IsNullOrWhiteSpace(i.Code) || i.Code.Trim().Length > 20) return "Give the fund a code of up to 20 characters";
			if (string.IsNullOrWhiteSpace(i.Name) || string.IsNullOrWhiteSpace(i.NameEn)) return "Give the name in Arabic and English";
			if (i.Name!.Trim().Length > 150 || i.NameEn!.Trim().Length > 150) return "A text is too long";
			if (i.IsActive && i.AccountId is not > 0) return "Choose the fund's expense account";
			return null;
		}

		/// Why a waiver cannot be requested on this bill (null = it can). `pending` = what other requests already ask.
		public static string? RequestRefusal(HospitalCharityRequestInput i, string billStatus, bool foreign, decimal due, decimal pending, bool fundUsable)
		{
			if (billStatus != HospitalBillStatus.Closed) return "Charity is granted on a closed bill";
			if (foreign) return "A foreign-currency bill is not waived here";
			if (!fundUsable) return "Choose the charity fund";
			if (i.Amount <= 0) return "Enter an amount above zero";
			if (string.IsNullOrWhiteSpace(i.Reason)) return "Write why the patient needs the help";
			if ((i.Reason?.Trim().Length ?? 0) > 500) return "A text is too long";
			if (i.Amount > due - pending) return "The waiver is more than the patient still owes ({0})";
			return null;
		}

		/// Why the decision cannot be made (null = it can).
		public static string? DecideRefusal(string status, int requestedBy, int me, bool approve, decimal amount, decimal dueNow)
		{
			if (status != HospitalCharityStatus.Requested) return "This request was already decided";
			if (requestedBy == me) return "The person who asked cannot approve their own request";
			if (approve && amount > dueNow) return "The patient now owes less than the waiver ({0}): refuse it and ask again";
			return null;
		}
	}

	public sealed class HospitalCharityService
	{
		public const string NoTables = "Charity cases are not installed yet (hospital_h63_charity.sql).";
		private readonly CrossDbContext _db;
		private readonly IHospitalAccessService _access;
		private readonly IHospitalLedger _ledger;
		private readonly HospitalBillingService _billing;
		public HospitalCharityService(CrossDbContext db, IHospitalAccessService access, IHospitalLedger ledger, HospitalBillingService billing)
		{ _db = db; _access = access; _ledger = ledger; _billing = billing; }
		private Task<bool> Can(BusinessContext ctx, string action, CancellationToken ct) => _access.CanAsync(ctx, action, null, ct);
		private IQueryable<HospitalCharityGrant> Grants(BusinessContext ctx) => _db.Set<HospitalCharityGrant>().Where(g => g.CompanyId == ctx.CompanyId);
		private IQueryable<HospitalCharityFund> Funds(BusinessContext ctx) => _db.Set<HospitalCharityFund>().Where(f => f.CompanyId == ctx.CompanyId);
		private static bool Missing(Microsoft.Data.SqlClient.SqlException ex) => ex.Number is 207 or 208;
		private static string? Clip(string? s, int max) => string.IsNullOrWhiteSpace(s) ? null : s.Trim()[..Math.Min(max, s.Trim().Length)];

		// ---------------- funds ----------------

		public async Task<(IReadOnlyList<HospitalCharityFund> Funds, bool Ready)> FundsAsync(BusinessContext ctx, bool activeOnly, CancellationToken ct = default)
		{
			try { return (await Funds(ctx).AsNoTracking().Where(f => !activeOnly || f.IsActive).OrderBy(f => f.Code).ToListAsync(ct), true); }
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex)) { return (Array.Empty<HospitalCharityFund>(), false); }
		}

		public async Task<HospitalResult> SaveFundAsync(BusinessContext ctx, int? id, HospitalCharityFundInput input, int? employeeId, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await Can(ctx, HospitalActions.Manage, ct)) return HospitalResult.Fail("forbidden");
			if (HospitalCharityRules.FundRefusal(input) is string why) return HospitalResult.Fail(why);
			if (input.AccountId is int acc && !await _db.Accounts.AnyAsync(a => a.ID == acc && a.CompanyID == ctx.CompanyId, ct)) return HospitalResult.Fail("not_found");
			var code = input.Code!.Trim().ToUpperInvariant();
			try
			{
				if (await Funds(ctx).AnyAsync(f => f.Code == code && f.ID != (id ?? 0), ct)) return HospitalResult.Fail("Another fund has this code");
				HospitalCharityFund row;
				if (id is int fid) { var f = await Funds(ctx).FirstOrDefaultAsync(x => x.ID == fid, ct); if (f == null) return HospitalResult.Fail("not_found"); row = f; }
				else { row = new HospitalCharityFund { CompanyId = ctx.CompanyId }; _db.Add(row); }
				row.Code = code; row.Name = input.Name!.Trim(); row.NameEn = input.NameEn!.Trim(); row.AccountId = input.AccountId; row.IsActive = input.IsActive;
				row.UpdatedAt = nowUtc; row.UpdatedByEmployeeId = employeeId;
				await _db.SaveChangesAsync(ct);
				return HospitalResult.Success(row.ID);
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex)) { _db.ChangeTracker.Clear(); return HospitalResult.Fail(NoTables); }
			catch (DbUpdateException) { _db.ChangeTracker.Clear(); return HospitalResult.Fail("Another fund has this code"); }
		}

		// ---------------- grants ----------------

		/// What approved waivers take off each bill's amount due (empty when charity is not installed).
		public static async Task<decimal> WaivedAsync(CrossDbContext db, int companyId, int billId, CancellationToken ct)
		{
			try { return await db.Set<HospitalCharityGrant>().AsNoTracking().Where(g => g.CompanyId == companyId && g.BillId == billId && g.Status == HospitalCharityStatus.Approved).SumAsync(g => (decimal?)g.Amount, ct) ?? 0; }
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex)) { return 0; }
		}

		private async Task<List<HospitalCharityRow>> RowsAsync(BusinessContext ctx, IQueryable<HospitalCharityGrant> q, CancellationToken ct)
		{
			var rows = await (from g in q.AsNoTracking()
							  join b in _db.HospitalBills.AsNoTracking() on g.BillId equals b.ID
							  join p in _db.HospitalPatients.AsNoTracking() on g.PatientId equals p.ID
							  join f in Funds(ctx).AsNoTracking() on g.FundId equals f.ID
							  select new { g, b.BillNo, p.Name, p.NameEn, p.Mrn, p.PhotoPath, Fund = f.Name, FundEn = f.NameEn }).ToListAsync(ct);
			var eids = rows.SelectMany(r => new int?[] { r.g.RequestedByEmployeeId, r.g.DecidedByEmployeeId }).Where(x => x != null).Select(x => x!.Value).Distinct().ToList();
			var people = await _db.Employee.AsNoTracking().Where(e => eids.Contains(e.ID) && e.EmpCompanyID == ctx.CompanyId).Select(e => new { e.ID, e.FullName, e.FullNameEn }).ToDictionaryAsync(e => e.ID, ct);
			(string, string) Who(int? id) => id is int i && people.TryGetValue(i, out var e) ? (e.FullName ?? "", e.FullNameEn ?? e.FullName ?? "") : ("", "");
			return rows.Select(r =>
			{
				var (q1, q1e) = Who(r.g.RequestedByEmployeeId); var (d, de) = Who(r.g.DecidedByEmployeeId);
				return new HospitalCharityRow(r.g, r.BillNo, r.Name, r.NameEn, r.Mrn, r.PhotoPath, r.Fund, r.FundEn, q1, q1e, r.g.DecidedByEmployeeId == null ? null : d, r.g.DecidedByEmployeeId == null ? null : de);
			}).ToList();
		}

		public async Task<(IReadOnlyList<HospitalCharityRow> Rows, bool Ready)> ForBillAsync(BusinessContext ctx, int billId, CancellationToken ct = default)
		{
			try { return ((await RowsAsync(ctx, Grants(ctx).Where(g => g.BillId == billId), ct)).OrderByDescending(r => r.Grant.ID).ToList(), true); }
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex)) { return (Array.Empty<HospitalCharityRow>(), false); }
		}

		/// The charity screen's list: requests first (oldest first), then decisions (newest first), searched by
		/// patient, MRN, bill or number; one page of it and the total.
		public async Task<(IReadOnlyList<HospitalCharityRow> Rows, int Total, int Pending, decimal ApprovedSum)> ListAsync(BusinessContext ctx, string? status, string? q, int page, int pageSize, CancellationToken ct = default)
		{
			var all = await RowsAsync(ctx, Grants(ctx), ct);
			int pending = all.Count(r => r.Grant.Status == HospitalCharityStatus.Requested);
			decimal approved = all.Where(r => r.Grant.Status == HospitalCharityStatus.Approved).Sum(r => r.Grant.Amount);
			var terms = (q ?? "").Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries);
			var hit = all.Where(r => (string.IsNullOrEmpty(status) || r.Grant.Status == status)
				&& terms.All(t => r.PatientName.Contains(t, StringComparison.OrdinalIgnoreCase) || r.PatientNameEn.Contains(t, StringComparison.OrdinalIgnoreCase)
					|| r.Mrn.Contains(t, StringComparison.OrdinalIgnoreCase) || r.BillNo.Contains(t, StringComparison.OrdinalIgnoreCase) || r.Grant.GrantNo.Contains(t, StringComparison.OrdinalIgnoreCase)))
				.OrderBy(r => r.Grant.Status == HospitalCharityStatus.Requested ? 0 : 1)
				.ThenBy(r => r.Grant.Status == HospitalCharityStatus.Requested ? r.Grant.RequestedAt : DateTime.MaxValue)
				.ThenByDescending(r => r.Grant.DecidedAt).ToList();
			return (hit.Skip((page - 1) * pageSize).Take(pageSize).ToList(), hit.Count, pending, approved);
		}

		public async Task<HospitalResult> RequestAsync(BusinessContext ctx, int billId, HospitalCharityRequestInput input, int? employeeId, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await Can(ctx, HospitalActions.Bill, ct) || employeeId is not int me) return HospitalResult.Fail("forbidden");
			var v = await _billing.BillAsync(ctx, billId, nowUtc, ct);
			if (v == null) return HospitalResult.Fail("not_found");
			try
			{
				bool fundOk = input.FundId is int fid && await Funds(ctx).AnyAsync(f => f.ID == fid && f.IsActive && f.AccountId != null, ct);
				decimal pending = await Grants(ctx).Where(g => g.BillId == billId && g.Status == HospitalCharityStatus.Requested).SumAsync(g => (decimal?)g.Amount, ct) ?? 0;
				if (HospitalCharityRules.RequestRefusal(input, v.Bill.Status, v.Foreign, v.Totals.Due, pending, fundOk) is string why)
					return HospitalResult.Fail(why, (v.Totals.Due - pending).ToString("0.###"));
				for (int attempt = 0; attempt < 3; attempt++)
				{
					var nos = await Grants(ctx).Select(g => g.GrantNo).ToListAsync(ct);
					var row = new HospitalCharityGrant
					{
						CompanyId = ctx.CompanyId, GrantNo = HospitalDentalPracticeRules.NextNo(nos, "CH-", 6), BillId = billId, PatientId = v.Bill.PatientId, FundId = input.FundId!.Value,
						Amount = Math.Round(input.Amount, 4), Reason = Clip(input.Reason, 500)!, RequestedAt = nowUtc, RequestedByEmployeeId = me,
					};
					_db.Add(row);
					try { await _db.SaveChangesAsync(ct); return HospitalResult.Success(row.ID); }
					catch (DbUpdateException) { _db.ChangeTracker.Clear(); }
				}
				return HospitalResult.Fail(HospitalNursingService.ConcurrentChange);
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex)) { _db.ChangeTracker.Clear(); return HospitalResult.Fail(NoTables); }
		}

		/// Approves (posting the waiver) or refuses a request, once, by the administrator who did not ask for it.
		public async Task<HospitalResult> DecideAsync(BusinessContext ctx, int grantId, bool approve, string? note, int? employeeId, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await Can(ctx, HospitalActions.Manage, ct) || employeeId is not int me) return HospitalResult.Fail("forbidden");
			HospitalCharityGrant? g;
			try { g = await Grants(ctx).AsNoTracking().FirstOrDefaultAsync(x => x.ID == grantId, ct); }
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex)) { return HospitalResult.Fail(NoTables); }
			if (g == null) return HospitalResult.Fail("not_found");
			var v = await _billing.BillAsync(ctx, g.BillId, nowUtc, ct);
			if (v == null) return HospitalResult.Fail("not_found");
			if (HospitalCharityRules.DecideRefusal(g.Status, g.RequestedByEmployeeId, me, approve, g.Amount, v.Totals.Due) is string why) return HospitalResult.Fail(why, v.Totals.Due.ToString("0.###"));
			if (!approve && string.IsNullOrWhiteSpace(note)) return HospitalResult.Fail("Write why the request is refused");

			int? entryId = null;
			await using var tx = await ScopedTx.BeginOrJoinAsync(_db);
			if (approve)
			{
				var fund = await Funds(ctx).AsNoTracking().FirstAsync(f => f.ID == g.FundId, ct);
				var s = await _billing.SettingsAsync(ctx, ct);
				if (fund.AccountId is not int expense || s.ReceivableAccountId is not int ar) { await tx.RollbackAsync(); return HospitalResult.Fail("Map the fund's expense account and the patients' receivable first"); }
				var lines = new[] { new HospitalLedgerLine(expense, g.Amount, 0, g.GrantNo), new HospitalLedgerLine(ar, 0, g.Amount, g.GrantNo) };
				var (ok, error, id) = await _ledger.PostAsync(ctx, nowUtc, "إعفاء خيري " + g.GrantNo + " — " + v.Bill.BillNo, "Charity waiver " + g.GrantNo + " — " + v.Bill.BillNo, "HospitalCharityGrant", g.ID, lines, ct);
				if (!ok) { await tx.RollbackAsync(); return HospitalResult.Fail("The ledger refused the posting: {0}", error ?? ""); }
				entryId = id;
			}
			string to = approve ? HospitalCharityStatus.Approved : HospitalCharityStatus.Refused;
			int n = await Grants(ctx).Where(x => x.ID == grantId && x.Status == HospitalCharityStatus.Requested).ExecuteUpdateAsync(u => u
				.SetProperty(x => x.Status, to).SetProperty(x => x.DecidedAt, nowUtc).SetProperty(x => x.DecidedByEmployeeId, me)
				.SetProperty(x => x.DecisionNote, Clip(note, 300)).SetProperty(x => x.JournalEntryId, entryId), ct);
			if (n != 1) { await tx.RollbackAsync(); return HospitalResult.Fail("This request was already decided"); }
			await tx.CommitAsync();
			return HospitalResult.Success(grantId);
		}
	}
}
