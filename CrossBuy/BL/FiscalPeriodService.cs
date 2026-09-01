using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Accounting;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL
{
	public class FiscalPeriodRow
	{
		public int Id { get; set; }
		public int FiscalYearId { get; set; }
		public string YearName { get; set; } = "";
		public int PeriodNo { get; set; }
		public DateTime StartDate { get; set; }
		public DateTime EndDate { get; set; }
		public string Status { get; set; } = "Open";
	}

	public interface IFiscalPeriodService
	{
		/// The fiscal period (monthly, PeriodNo 1..12 preferred) of a company that contains the date.
		Task<FiscalPeriod?> ResolveAsync(int companyId, DateTime date);

		/// All periods of a company (with year name), ordered.
		Task<List<FiscalPeriodRow>> ListAsync(int companyId);

		// RETIRED: SetStatusAsync.
		//
		// It took NO company and NO actor - it loaded the period by id alone - so a chief accountant in
		// company 1 could close company 2's period, and nothing recorded who did it, when, or why. It
		// also answered no readiness question, so a period could be sealed over an out-of-balance journal.
		//
		// It is REMOVED rather than narrowed. Of the three period states, two block posting, so every
		// transition this method could still legally perform is a governed one - narrowing it would have
		// left an inert method that only looked like a way to change a period.
		//
		// Close, soft-close and reopen now live on IAccountingPeriodControlService, which resolves the
		// company from the period's own fiscal year, demands period-close/period-reopen authority, gates
		// on readiness, requires a reason to reopen, and writes an audit row for every transition.
	}

	public class FiscalPeriodService : IFiscalPeriodService
	{
		private readonly CrossDbContext _context;
		public FiscalPeriodService(CrossDbContext context) { _context = context; }

		public async Task<FiscalPeriod?> ResolveAsync(int companyId, DateTime date)
		{
			var d = date.Date;
			var yearIds = await _context.FiscalYears.AsNoTracking()
				.Where(y => y.CompanyID == companyId)
				.Select(y => y.ID).ToListAsync();
			if (yearIds.Count == 0) return null;

			var matches = await _context.FiscalPeriods.AsNoTracking()
				.Where(p => yearIds.Contains(p.FiscalYearId) && p.StartDate <= d && p.EndDate >= d)
				.ToListAsync();

			// prefer the monthly period (1..12) over the adjustment period (13)
			return matches.OrderBy(p => p.PeriodNo).FirstOrDefault(p => p.PeriodNo <= 12)
				?? matches.FirstOrDefault();
		}

		public async Task<List<FiscalPeriodRow>> ListAsync(int companyId)
		{
			var years = await _context.FiscalYears.AsNoTracking()
				.Where(y => y.CompanyID == companyId).ToDictionaryAsync(y => y.ID, y => y.Name);
			if (years.Count == 0) return new();
			var yearIds = years.Keys.ToList();
			var periods = await _context.FiscalPeriods.AsNoTracking()
				.Where(p => yearIds.Contains(p.FiscalYearId))
				.ToListAsync();
			return periods
				.Select(p => new FiscalPeriodRow
				{
					Id = p.ID, FiscalYearId = p.FiscalYearId,
					YearName = years.TryGetValue(p.FiscalYearId, out var n) ? n : "",
					PeriodNo = p.PeriodNo, StartDate = p.StartDate, EndDate = p.EndDate, Status = p.Status,
				})
				.OrderBy(p => p.YearName).ThenBy(p => p.PeriodNo).ToList();
		}
	}
}
