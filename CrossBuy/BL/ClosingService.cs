using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Accounting;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL
{
	public class YearClosePreview
	{
		public FiscalYear? Year { get; set; }
		public decimal TotalRevenue { get; set; }
		public decimal TotalExpense { get; set; }
		public decimal NetResult => Math.Round(TotalRevenue - TotalExpense, 2);
		public bool AlreadyClosed { get; set; }
		public int PnlAccountCount { get; set; }
	}

	public interface IClosingService
	{
		Task<List<FiscalYear>> GetYearsAsync(int companyId);
		Task<List<YearEndClosing>> GetClosingsAsync(int companyId);
		Task<YearEndClosing?> GetClosingAsync(int companyId, int id);
		Task<YearClosePreview> PreviewAsync(int companyId, int fiscalYearId);
		Task<(bool ok, string? error)> CloseYearAsync(int companyId, int fiscalYearId, int? userId);
		Task<(bool ok, string? error)> ReopenYearAsync(int companyId, int closingId, int? userId);
	}

	public class ClosingService : IClosingService
	{
		private readonly CrossDbContext _context;
		private readonly IJournalEntryService _journals;
		public ClosingService(CrossDbContext context, IJournalEntryService journals) { _context = context; _journals = journals; }

		private static decimal R(decimal v) => Math.Round(v, 2, MidpointRounding.AwayFromZero);

		public async Task<List<FiscalYear>> GetYearsAsync(int companyId) =>
			await _context.FiscalYears.AsNoTracking().Where(y => y.CompanyID == companyId).OrderByDescending(y => y.StartDate).ToListAsync();

		public async Task<List<YearEndClosing>> GetClosingsAsync(int companyId) =>
			await _context.YearEndClosings.AsNoTracking().Where(c => c.CompanyID == companyId).OrderByDescending(c => c.ID).ToListAsync();

		public async Task<YearEndClosing?> GetClosingAsync(int companyId, int id) =>
			await _context.YearEndClosings.AsNoTracking().FirstOrDefaultAsync(c => c.ID == id && c.CompanyID == companyId);

		// P&L movement grouped by (account, cost center) up to a date — keeps cost-center integrity on close
		private async Task<List<(int AccountId, int? CostCenterId, string TypeCode, decimal NetDebit)>> PnlMovementAsync(int companyId, DateTime upTo)
		{
			var rows = await (from l in _context.JournalEntryLines.AsNoTracking()
							  join e in _context.JournalEntries.AsNoTracking() on l.JournalEntryId equals e.ID
							  join a in _context.Accounts.AsNoTracking() on l.AccountId equals a.ID
							  join t in _context.AccountTypes.AsNoTracking() on a.AccountTypeId equals t.ID
							  where e.CompanyID == companyId && (e.Status == "Posted" || e.Status == "Reversed")
									&& e.EntryDate <= upTo && (t.Code == "REV" || t.Code == "EXP")
							  select new { l.AccountId, l.CostCenterId, t.Code, l.Debit, l.Credit }).ToListAsync();
			return rows.GroupBy(r => new { r.AccountId, r.CostCenterId, r.Code })
					   .Select(g => (g.Key.AccountId, g.Key.CostCenterId, g.Key.Code, R(g.Sum(x => x.Debit) - g.Sum(x => x.Credit))))
					   .Where(x => x.Item4 != 0)
					   .ToList();
		}

		public async Task<YearClosePreview> PreviewAsync(int companyId, int fiscalYearId)
		{
			var year = await _context.FiscalYears.AsNoTracking().FirstOrDefaultAsync(y => y.ID == fiscalYearId && y.CompanyID == companyId);
			var p = new YearClosePreview { Year = year };
			if (year == null) return p;
			var move = await PnlMovementAsync(companyId, year.EndDate);
			p.TotalRevenue = R(move.Where(m => m.TypeCode == "REV").Sum(m => -m.NetDebit));
			p.TotalExpense = R(move.Where(m => m.TypeCode == "EXP").Sum(m => m.NetDebit));
			p.PnlAccountCount = move.Select(m => m.AccountId).Distinct().Count();
			p.AlreadyClosed = await _context.YearEndClosings.AnyAsync(c => c.CompanyID == companyId && c.FiscalYearId == fiscalYearId && c.Status == "Closed");
			return p;
		}

		public async Task<(bool ok, string? error)> CloseYearAsync(int companyId, int fiscalYearId, int? userId)
		{
			var year = await _context.FiscalYears.FirstOrDefaultAsync(y => y.ID == fiscalYearId && y.CompanyID == companyId);
			if (year == null) return (false, "Fiscal year not found");
			if (year.Status == "Closed") return (false, "The fiscal year is already closed");
			if (await _context.YearEndClosings.AnyAsync(c => c.CompanyID == companyId && c.FiscalYearId == fiscalYearId && c.Status == "Closed"))
				return (false, "This year has already been closed");

			var retained = await _context.Accounts.Where(a => a.CompanyID == companyId && a.Code == "3201").Select(a => (int?)a.ID).FirstOrDefaultAsync();
			if (retained == null) return (false, "The retained-earnings account (3201) does not exist");

			var move = await PnlMovementAsync(companyId, year.EndDate);
			if (move.Count == 0) return (false, "There are no revenue or expense accounts to close");

			var lines = new List<JournalLineInput>();
			foreach (var m in move)
			{
				// zero each P&L account: revenue (net credit) → debit it; expense (net debit) → credit it
				var debit = m.NetDebit < 0 ? -m.NetDebit : 0;   // close credit-balance (revenue) with a debit
				var credit = m.NetDebit > 0 ? m.NetDebit : 0;   // close debit-balance (expense) with a credit
				lines.Add(new JournalLineInput { AccountId = m.AccountId, Debit = debit, Credit = credit, CostCenterId = m.CostCenterId, Description = "Closing of the result for the period" });
			}

			var totalDr = R(lines.Sum(l => l.Debit));
			var totalCr = R(lines.Sum(l => l.Credit));
			var diff = R(totalDr - totalCr);   // = net profit (>0) or net loss (<0)
			if (diff > 0) lines.Add(new JournalLineInput { AccountId = retained.Value, Debit = 0, Credit = diff, Description = "Transfer of net profit to retained earnings" });
			else if (diff < 0) lines.Add(new JournalLineInput { AccountId = retained.Value, Debit = -diff, Credit = 0, Description = "Transfer of net loss to retained earnings" });
			if (lines.Count < 2) return (false, "There are no balances to close");

			var (ok, err, entry) = await _journals.CreateAndPostAsync(new JournalEntryInput
			{
				CompanyID = companyId, EntryDate = year.EndDate, JournalType = "Closing", SourceType = "YearClose", SourceId = fiscalYearId,
				Description = $"قيد إقفال السنة المالية {year.Name}", DescriptionEn = $"Year-end closing {year.Name}", Lines = lines,
			}, userId);
			if (!ok) return (false, err);

			var totalRev = R(move.Where(m => m.TypeCode == "REV").Sum(m => -m.NetDebit));
			var totalExp = R(move.Where(m => m.TypeCode == "EXP").Sum(m => m.NetDebit));
			_context.YearEndClosings.Add(new YearEndClosing
			{
				CompanyID = companyId, FiscalYearId = fiscalYearId, CloseDate = year.EndDate, TotalRevenue = totalRev,
				TotalExpense = totalExp, NetResult = R(totalRev - totalExp), RetainedEarningsAccountId = retained.Value,
				JournalEntryId = entry!.ID, Status = "Closed", CreatedAt = DateTime.UtcNow,
			});

			// lock the year and all its periods
			year.Status = "Closed";
			var periods = await _context.FiscalPeriods.Where(p => p.FiscalYearId == fiscalYearId).ToListAsync();
			foreach (var p in periods) p.Status = "Closed";
			await _context.SaveChangesAsync();
			return (true, null);
		}

		public async Task<(bool ok, string? error)> ReopenYearAsync(int companyId, int closingId, int? userId)
		{
			var closing = await _context.YearEndClosings.FirstOrDefaultAsync(c => c.ID == closingId && c.CompanyID == companyId);
			if (closing == null) return (false, "Closing record not found");
			if (closing.Status == "Reopened") return (false, "The year has already been reopened");
			var year = await _context.FiscalYears.FirstOrDefaultAsync(y => y.ID == closing.FiscalYearId && y.CompanyID == companyId);
			if (year == null) return (false, "Fiscal year not found");

			// reopen periods/year first so the reversing entry can post on the close date
			year.Status = "Open";
			var periods = await _context.FiscalPeriods.Where(p => p.FiscalYearId == year.ID).ToListAsync();
			foreach (var p in periods) p.Status = "Open";
			await _context.SaveChangesAsync();

			if (closing.JournalEntryId.HasValue)
			{
				var (ok, err, _) = await _journals.ReverseAsync(closing.JournalEntryId.Value, userId, "Fiscal year reopening");
				if (!ok) return (false, err);
			}
			closing.Status = "Reopened";
			await _context.SaveChangesAsync();
			return (true, null);
		}
	}
}
