using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.Controllers
{
	// ==========================================================================================
	// HR6 — THE MONTHLY SOCIAL-INSURANCE STATEMENT (prototype #/payroll, 2026-11-08). Payroll-view.
	// Per employee: the wage in force at the month's end, each rule-pack insurance line that applies to them (nationals-only
	// lines only for nationals), the employee's and the employer's share. Computed by HrInsurance.Lines — the function payroll
	// deducts with — so the statement and the payroll entry cannot disagree.
	// ==========================================================================================
	public partial class AdminController
	{
		public sealed record InsuranceRow(int EmployeeId, string Name, bool National, decimal Wage, IReadOnlyList<CrossBuy.BL.Hr.InsuranceLineAmount> Lines)
		{
			public decimal Employee => Lines.Sum(l => l.Employee);
			public decimal Employer => Lines.Sum(l => l.Employer);
		}
		public sealed record InsuranceLineTotal(string NameAr, string NameEn, decimal EmployeePct, decimal EmployerPct, bool NationalsOnly, int People, decimal Employee, decimal Employer);
		public sealed record InsurancePageModel(DateTime Month, string? PackNameAr, string? PackNameEn, string Currency, string? NationalCountry,
			IReadOnlyList<InsuranceLineTotal> Totals, int People, int Nationals, decimal Employee, decimal Employer);
		public sealed record InsuranceRowsModel(IReadOnlyList<InsuranceRow> Rows, Dictionary<int, string> Photos, string Currency);

		// The computation lives in HrInsuranceStatement, shared with the contributions file (BankFile, kind "insurance").
		private async Task<(List<InsuranceRow> Rows, CrossBuy.BL.Hr.HrRules? Pack, CrossBuy.Models.Context.Admin.HrRulePack? PackRow)> LoadInsuranceAsync(int company, DateTime month, CancellationToken ct)
		{
			var (rows, pack, packRow) = await CrossBuy.BL.Hr.HrInsuranceStatement.RowsAsync(Db, company, month, ct);
			return (rows.Select(r => new InsuranceRow(r.EmployeeId, r.Name, r.National, r.Wage, r.Lines)).ToList(), pack, packRow);
		}

		[HttpGet]
		public async Task<IActionResult> SocialInsurance(string? month, CancellationToken ct = default)
		{
			var gate = await HrGateAsync(CrossBuy.BL.HrActions.PayrollView);
			if (!gate.Ok) return HrDenied(nameof(Index));
			var m = MonthOf(month);
			var (rows, pack, packRow) = await LoadInsuranceAsync(gate.CompanyId, m, ct);
			var totals = (pack?.Insurance ?? new()).Select(l => new InsuranceLineTotal(l.NameAr, l.NameEn, l.EmployeePct, l.EmployerPct, l.ForNationalsOnly,
				rows.Count(r => r.Lines.Any(x => x.NameEn == l.NameEn && x.NameAr == l.NameAr)),
				rows.Sum(r => r.Lines.Where(x => x.NameEn == l.NameEn && x.NameAr == l.NameAr).Sum(x => x.Employee)),
				rows.Sum(r => r.Lines.Where(x => x.NameEn == l.NameEn && x.NameAr == l.NameAr).Sum(x => x.Employer)))).ToList();
			return View(new InsurancePageModel(m, packRow?.NameAr, packRow?.NameEn, pack?.Currency ?? "", pack?.NationalCountryName, totals,
				rows.Count, rows.Count(r => r.National), rows.Sum(r => r.Employee), rows.Sum(r => r.Employer)));
		}

		[HttpGet]
		public async Task<IActionResult> SocialInsuranceData(string? month, string? q, string? scope, int page = 1, int pageSize = 25, CancellationToken ct = default)
		{
			var gate = await HrGateAsync(CrossBuy.BL.HrActions.PayrollView);
			if (!gate.Ok) return StatusCode(403);
			var (rows, pack, _) = await LoadInsuranceAsync(gate.CompanyId, MonthOf(month), ct);
			var terms = SearchTerms(q);
			var filtered = rows.Where(r => AllTermsIn(terms, r.Name) && (scope != "nationals" || r.National) && (scope != "others" || !r.National)).ToList();
			var pageRows = Page(filtered, ref page, ref pageSize);
			var ctx = await BusinessContexts.TryGetCurrentAsync();
			var photos = ctx == null ? new Dictionary<int, string>() : await CrossBuy.BL.Platform.EmployeePhotos.ResolveAsync(Db, ctx, pageRows.Select(r => r.EmployeeId).Distinct(), ct);
			return PartialView("_InsuranceRows", new InsuranceRowsModel(pageRows, photos, pack?.Currency ?? ""));
		}
	}
}
