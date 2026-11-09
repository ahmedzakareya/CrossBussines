using CrossBuy.Models.Context;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL.Hr
{
	// ==========================================================================================
	// THE END-OF-SERVICE GRATUITY PROVISION (2026-11-08).
	//
	// The liability a company carries for every active employee's gratuity, booked as it is earned instead of landing in one
	// month's expense when someone leaves. Measured the same way the settlement screen measures it — the country rule pack
	// (HrGratuity.Compute) on the employee's own wage in force (HrSalaryService) and their years of service — on the
	// termination basis, the full amount the company could owe.
	//
	// A TRUE-UP, like the leave provision: required − the provision account's current balance is posted (Dr expense /
	// Cr provision, or the reverse to release a surplus). Posting the same date twice posts nothing the second time, a raise
	// or a new joiner increases it, and a leaver's share is released by the next run.
	// ==========================================================================================
	public sealed record GratuityProvisionRow(int EmployeeId, string Name, DateTime Joined, decimal Years, decimal Wage, decimal Amount);
	public sealed record GratuityProvisionPreview(DateTime AsOf, IReadOnlyList<GratuityProvisionRow> Rows, decimal Required, decimal Booked, bool HasAccounts,
		string Currency, bool WageIsBasic, string? PackNameAr, string? PackNameEn)
	{
		public decimal Adjustment => Math.Round(Required - Booked, 3, MidpointRounding.AwayFromZero);
	}

	public sealed class HrGratuityProvisionService
	{
		public const string ExpenseCode = "520107", ProvisionCode = "2106", SourceType = "GratuityProvision";
		private readonly CrossDbContext _db;
		public HrGratuityProvisionService(CrossDbContext db) { _db = db; }

		private Task<int?> AccountAsync(int companyId, string code, CancellationToken ct) =>
			_db.Accounts.AsNoTracking().Where(a => a.CompanyID == companyId && a.Code == code && a.IsPostable).Select(a => (int?)a.ID).FirstOrDefaultAsync(ct);

		public async Task<GratuityProvisionPreview> PreviewAsync(int companyId, DateTime asOf, CancellationToken ct = default)
		{
			var day = asOf.Date;
			var eff = await new HrRulePackService(_db).EffectiveAsync(companyId, ct);
			var emps = await _db.Employee.AsNoTracking().Where(e => e.EmpCompanyID == companyId && e.IsActive && e.DateOfJoining <= day)
				.Select(e => new { e.ID, e.FullName, e.FullNameEn, e.DateOfJoining }).ToListAsync(ct);
			var pay = await new HrSalaryService(_db).ForManyAsync(emps.Select(e => e.ID), day, ct);
			var rows = new List<GratuityProvisionRow>();
			if (eff is { } pack)
				foreach (var e in emps)
				{
					if (!pay.TryGetValue(e.ID, out var p)) continue;
					var wage = HrRules.IsBasic(pack.Rules) ? p.Basic : p.Total;
					if (wage <= 0) continue;
					var years = (decimal)Math.Max(0, (day - e.DateOfJoining.Date).TotalDays / 365.25);
					var g = HrGratuity.Compute(pack.Rules, years, wage);
					rows.Add(new GratuityProvisionRow(e.ID, EmployeeNames.Of(e.FullName, e.FullNameEn) ?? "", e.DateOfJoining, Math.Round(years, 2), wage, g.OnTermination));
				}

			var prov = await AccountAsync(companyId, ProvisionCode, ct);
			var exp = await AccountAsync(companyId, ExpenseCode, ct);
			decimal booked = 0;
			if (prov != null)
			{
				var agg = await _db.JournalEntryLines.AsNoTracking()
					.Join(_db.JournalEntries.AsNoTracking().Where(j => j.CompanyID == companyId && j.Status == "Posted" && j.EntryDate <= day),
						l => l.JournalEntryId, j => j.ID, (l, j) => l)
					.Where(l => l.AccountId == prov.Value)
					.GroupBy(l => 1).Select(g => new { cr = g.Sum(x => x.Credit), dr = g.Sum(x => x.Debit) }).FirstOrDefaultAsync(ct);
				booked = agg == null ? 0 : agg.cr - agg.dr;
			}
			return new GratuityProvisionPreview(day, rows.OrderByDescending(r => r.Amount).ToList(), Math.Round(rows.Sum(r => r.Amount), 3), Math.Round(booked, 3),
				prov != null && exp != null, eff?.Rules.Currency ?? "", eff is { } ep && HrRules.IsBasic(ep.Rules), eff?.Pack.NameAr, eff?.Pack.NameEn);
		}

		public async Task<(bool Ok, string? Ar, string? En)> PostAsync(int companyId, DateTime asOf, int? userId, IJournalEntryService journals, CancellationToken ct = default)
		{
			var pv = await PreviewAsync(companyId, asOf, ct);
			if (!pv.HasAccounts) return (false, $"الحسابان {ExpenseCode} و{ProvisionCode} غير معرّفين لهذه الشركة", $"Accounts {ExpenseCode} and {ProvisionCode} are not defined for this company");
			var adj = pv.Adjustment;
			if (adj == 0) return (false, "المخصص المسجل يطابق المطلوب؛ لا فرق يُرحَّل", "The booked provision already matches the required one; nothing to post");
			var exp = (await AccountAsync(companyId, ExpenseCode, ct))!.Value;
			var prov = (await AccountAsync(companyId, ProvisionCode, ct))!.Value;
			var amount = Math.Abs(adj);
			var lines = adj > 0
				? new List<JournalLineInput> { new() { AccountId = exp, Debit = amount, Credit = 0, Description = "End-of-service gratuity expense" }, new() { AccountId = prov, Debit = 0, Credit = amount, Description = "Increase in the gratuity provision" } }
				: new List<JournalLineInput> { new() { AccountId = prov, Debit = amount, Credit = 0, Description = "Decrease in the gratuity provision" }, new() { AccountId = exp, Debit = 0, Credit = amount, Description = "Gratuity provision release" } };
			var (ok, err, _) = await journals.CreateAndPostAsync(new JournalEntryInput
			{
				CompanyID = companyId, EntryDate = pv.AsOf, JournalType = "Auto", SourceType = SourceType,
				Description = $"تسوية مخصص مكافأة نهاية الخدمة حتى {pv.AsOf:yyyy-MM-dd}", DescriptionEn = $"End-of-service gratuity provision as of {pv.AsOf:yyyy-MM-dd}",
				Lines = lines,
			}, userId);
			return ok ? (true, null, null) : (false, err, err);
		}
	}
}
