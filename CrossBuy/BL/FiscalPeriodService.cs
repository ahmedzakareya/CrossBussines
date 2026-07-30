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

		/// Change a period status: Open / SoftClosed / Closed.
		Task<(bool ok, string? error)> SetStatusAsync(int periodId, string status);
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

		public async Task<(bool ok, string? error)> SetStatusAsync(int periodId, string status)
		{
			if (status != "Open" && status != "SoftClosed" && status != "Closed")
				return (false, "حالة غير صحيحة");
			var p = await _context.FiscalPeriods.FirstOrDefaultAsync(x => x.ID == periodId);
			if (p == null) return (false, "الفترة غير موجودة");
			p.Status = status;
			await _context.SaveChangesAsync();
			return (true, null);
		}
	}
}
