using CrossBuy.Models.Context;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL.Hr
{
	// ==========================================================================================
	// THE MONTHLY SOCIAL-INSURANCE STATEMENT AND ITS FILE (2026-11-09).
	//
	// One computation, two readers: the statement screen (AdminController.SocialInsurance) and the contributions file a
	// company uploads to the insurance authority (Kuwait: PIFSS). Per active employee with a wage: the wage in force at
	// the month's end and each rule-pack insurance line that applies (nationals-only lines only for nationals), through
	// HrInsurance.Lines — the function payroll deducts with — so the statement, the file and the payroll entry agree.
	//
	// The file carries only employees with a contribution, its shape is the company's "Insurance" HrBankFileFormat (the
	// same engine and editor as the bank file), and a line without a civil ID is left out and listed — the authority keys
	// on it.
	// ==========================================================================================
	public sealed record InsuranceStatementRow(int EmployeeId, string Name, string NameAr, string? NameEn, string? CivilId, bool National, decimal Wage,
		IReadOnlyList<InsuranceLineAmount> Lines)
	{
		public decimal Employee => Lines.Sum(l => l.Employee);
		public decimal Employer => Lines.Sum(l => l.Employer);
	}

	public static class HrInsuranceStatement
	{
		public static async Task<(List<InsuranceStatementRow> Rows, HrRules? Pack, Models.Context.Admin.HrRulePack? PackRow)> RowsAsync(
			CrossDbContext db, int company, DateTime month, CancellationToken ct = default)
		{
			var eff = await new HrRulePackService(db).EffectiveAsync(company, ct);
			var pack = eff?.Rules;
			var emps = await db.Employee.AsNoTracking().Where(e => e.EmpCompanyID == company && e.IsActive)
				.Select(e => new { e.ID, e.FullName, e.FullNameEn, e.CountryID, e.CivilIdNumber }).ToListAsync(ct);
			var monthEnd = new DateTime(month.Year, month.Month, 1).AddMonths(1).AddDays(-1);
			var pay = await new HrSalaryService(db).ForManyAsync(emps.Select(e => e.ID), monthEnd, ct);
			var nationals = await HrInsurance.NationalCountryIdsAsync(db, pack, ct);
			var rows = new List<InsuranceStatementRow>();
			foreach (var e in emps)
			{
				if (!pay.TryGetValue(e.ID, out var p) || p.Total <= 0) continue;   // no wage, no contribution
				bool national = e.CountryID is int c && nationals.Contains(c);
				var lines = pack == null ? new List<InsuranceLineAmount>() : HrInsurance.Lines(pack, p.Total, national);
				rows.Add(new InsuranceStatementRow(e.ID, EmployeeNames.Of(e.FullName, e.FullNameEn) ?? "", e.FullName, e.FullNameEn, e.CivilIdNumber, national, p.Total, lines));
			}
			return (rows.OrderBy(r => r.Name).ToList(), pack, eff?.Pack);
		}

		/// Whether the month's payroll is posted (any payslip exists for it).
		public static Task<bool> PostedAsync(CrossDbContext db, int company, DateTime month, CancellationToken ct = default) =>
			db.Payslips.AsNoTracking().AnyAsync(p => p.CompanyID == company && p.Year == month.Year && p.Month == month.Month, ct);

		/// The file's lines: employees with a contribution, as export lines (Net = the total contribution, which the trailer
		/// totals; Wage / EmployeeShare / EmployerShare / TotalShare as named amounts).
		///
		/// A POSTED month reports what was posted (owner, 2026-11-10): each payslip's SiEmployee / SiCompany and the wage payroll
		/// insured (basic + allowances), so the authority sees exactly what was deducted. A month not yet posted reports the
		/// statement's computation, i.e. what payroll will deduct.
		public static async Task<List<HrBankFileLine>> FileLinesAsync(CrossDbContext db, int company, DateTime month, CancellationToken ct = default)
		{
			if (await PostedAsync(db, company, month, ct))
			{
				var pack = (await new HrRulePackService(db).EffectiveAsync(company, ct))?.Rules;
				var nationals = await HrInsurance.NationalCountryIdsAsync(db, pack, ct);
				var slips = await db.Payslips.AsNoTracking()
					.Where(p => p.CompanyID == company && p.Year == month.Year && p.Month == month.Month && p.SiEmployee + p.SiCompany > 0)
					.Join(db.Employee.AsNoTracking(), p => p.EmployeeID, e => e.ID, (p, e) => new
					{
						p.EmployeeID, e.FullName, e.FullNameEn, e.CivilIdNumber, e.CountryID, Wage = p.BaseSalary + p.Allowances, p.SiEmployee, p.SiCompany,
					})
					.OrderBy(x => x.FullName).ToListAsync(ct);
				int n = 0;
				return slips.Select(x =>
				{
					var total = x.SiEmployee + x.SiCompany;
					bool national = x.CountryID is int c && nationals.Contains(c);
					return new HrBankFileLine(++n, x.EmployeeID, x.FullName, x.FullNameEn, x.CivilIdNumber, null, null, x.Wage, 0, x.Wage, x.SiEmployee, total,
						string.IsNullOrWhiteSpace(x.CivilIdNumber) ? "NoCivilId" : null,
						new Dictionary<string, decimal> { ["Wage"] = x.Wage, ["EmployeeShare"] = x.SiEmployee, ["EmployerShare"] = x.SiCompany, ["TotalShare"] = total },
						national ? "1" : "0");
				}).ToList();
			}

			var (rows, _, _) = await RowsAsync(db, company, month, ct);
			int seq = 0;
			return rows.Where(r => r.Employee + r.Employer > 0).OrderBy(r => r.NameAr).Select(r =>
			{
				var total = r.Employee + r.Employer;
				return new HrBankFileLine(++seq, r.EmployeeId, r.NameAr, r.NameEn, r.CivilId, null, null, r.Wage, 0, r.Wage, r.Employee, total,
					string.IsNullOrWhiteSpace(r.CivilId) ? "NoCivilId" : null,
					new Dictionary<string, decimal> { ["Wage"] = r.Wage, ["EmployeeShare"] = r.Employee, ["EmployerShare"] = r.Employer, ["TotalShare"] = total },
					r.National ? "1" : "0");
			}).ToList();
		}
	}
}
