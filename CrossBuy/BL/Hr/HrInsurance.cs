using CrossBuy.Models.Context;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL.Hr
{
	// ==========================================================================================
	// HR6 — SOCIAL INSURANCE, ONE COMPUTATION (2026-11-08).
	//
	// The rule pack's insurance lines on the wage, each up to its own cap, for everyone or for nationals only. Payroll
	// posts the totals and the monthly statement shows the lines — both from THIS function, so the statement can never
	// disagree with what was deducted.
	// ==========================================================================================
	public sealed record InsuranceLineAmount(string NameAr, string NameEn, decimal Wage, decimal EmployeePct, decimal EmployerPct,
		decimal Employee, decimal Employer, bool NationalsOnly);

	public static class HrInsurance
	{
		public static List<InsuranceLineAmount> Lines(HrRules pack, decimal gross, bool national)
		{
			var list = new List<InsuranceLineAmount>();
			foreach (var line in pack.Insurance.Where(l => !l.ForNationalsOnly || national))
			{
				var wage = line.WageCap is > 0 ? Math.Min(gross, line.WageCap.Value) : gross;
				list.Add(new InsuranceLineAmount(line.NameAr, line.NameEn, wage, line.EmployeePct, line.EmployerPct,
					wage * line.EmployeePct / 100m, wage * line.EmployerPct / 100m, line.ForNationalsOnly));
			}
			return list;
		}

		/// The ids of CountriesLookup rows whose name is the pack's national country (an employee with one is a national).
		public static async Task<HashSet<int>> NationalCountryIdsAsync(CrossDbContext db, HrRules? pack, CancellationToken ct = default)
			=> pack?.NationalCountryName is { Length: > 0 } ncn
				? (await db.Set<CrossBuy.Models.Context.Admin.CountriesLookup>().AsNoTracking().Where(c => c.CountryName == ncn).Select(c => c.ID).ToListAsync(ct)).ToHashSet()
				: new HashSet<int>();
	}
}
