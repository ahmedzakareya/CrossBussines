using CrossBuy.Models.Context;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL
{
	public class AccMonthPoint
	{
		public int Year { get; set; }
		public int Month { get; set; }
		public decimal Revenue { get; set; }
		public decimal Expense { get; set; }
	}

	public class AccRecentEntry
	{
		public string? EntryNo { get; set; }
		public DateTime Date { get; set; }
		public string? Description { get; set; }
		public string JournalType { get; set; } = "";
		public string Status { get; set; } = "";
		public decimal Amount { get; set; }
	}

	public class AccTopAccount
	{
		public string Code { get; set; } = "";
		public string NameAr { get; set; } = "";
		public string NameEn { get; set; } = "";
		public decimal Amount { get; set; }
		public int Pct { get; set; }
	}

	public class AccountingDashboardDto
	{
		public decimal Cash { get; set; }
		public decimal Receivables { get; set; }
		public decimal Payables { get; set; }
		public decimal Revenue { get; set; }       // current year
		public decimal Expense { get; set; }        // current year
		public decimal NetIncome => Revenue - Expense;
		public int PostedCount { get; set; }
		public int DraftCount { get; set; }
		public List<AccMonthPoint> Months { get; set; } = new();
		public List<AccRecentEntry> Recent { get; set; } = new();
		public List<AccTopAccount> TopExpenses { get; set; } = new();
		public int Year { get; set; }

		// Phase 8 — balance-sheet snapshot (all-time, as of now)
		public decimal TotalAssets { get; set; }
		public decimal TotalLiabilities { get; set; }
		public decimal TotalEquity { get; set; }   // equity accounts + retained result
		public bool IsBalanced => Math.Round(TotalAssets - (TotalLiabilities + TotalEquity), 2) == 0;

		// Phase 6 — fixed assets
		public int AssetCount { get; set; }
		public decimal AssetCost { get; set; }
		public decimal AssetAccumDep { get; set; }
		public decimal AssetNbv => Math.Round(AssetCost - AssetAccumDep, 2);

		// Phase 7 — VAT (current year movement)
		public decimal VatOutput { get; set; }
		public decimal VatInput { get; set; }
		public decimal VatNetDue => Math.Round(VatOutput - VatInput, 2);

		// Phase 9 — fiscal year
		public string FiscalYearName { get; set; } = "";
		public string FiscalYearStatus { get; set; } = "Open";
	}

	public interface IAccountingDashboardService
	{
		Task<AccountingDashboardDto> BuildAsync(int companyId);
	}

	public class AccountingDashboardService : IAccountingDashboardService
	{
		private readonly CrossDbContext _context;
		public AccountingDashboardService(CrossDbContext context) { _context = context; }

		public async Task<AccountingDashboardDto> BuildAsync(int companyId)
		{
			var year = DateTime.UtcNow.Year;
			var dto = new AccountingDashboardDto { Year = year };

			var accounts = await _context.Accounts.AsNoTracking().Where(a => a.CompanyID == companyId).ToListAsync();
			var types = await _context.AccountTypes.AsNoTracking().ToDictionaryAsync(t => t.ID);
			var accById = accounts.ToDictionary(a => a.ID);

			// posted (and reversed — their lines are real history) lines joined with the entry header
			var lines = await (from l in _context.JournalEntryLines.AsNoTracking()
							   join e in _context.JournalEntries.AsNoTracking() on l.JournalEntryId equals e.ID
							   where e.CompanyID == companyId && (e.Status == "Posted" || e.Status == "Reversed")
							   select new { l.AccountId, l.Debit, l.Credit, e.EntryDate }).ToListAsync();

			string? TypeCode(int accId) => accById.TryGetValue(accId, out var a) && types.TryGetValue(a.AccountTypeId, out var t) ? t.Code : null;
			string? Code(int accId) => accById.TryGetValue(accId, out var a) ? a.Code : null;

			// balances
			decimal assetT = 0, liabT = 0, eqT = 0, revAll = 0, expAll = 0;
			foreach (var l in lines)
			{
				var code = Code(l.AccountId) ?? "";
				var tc = TypeCode(l.AccountId);
				if (code.StartsWith("1101")) dto.Cash += l.Debit - l.Credit;          // cash & banks
				if (code == "1102") dto.Receivables += l.Debit - l.Credit;            // AR control
				if (code == "2101") dto.Payables += l.Credit - l.Debit;               // AP control

				// all-time balance-sheet totals (Phase 8 snapshot)
				switch (tc)
				{
					case "ASSET": assetT += l.Debit - l.Credit; break;
					case "LIAB": liabT += l.Credit - l.Debit; break;
					case "EQUITY": eqT += l.Credit - l.Debit; break;
					case "REV": revAll += l.Credit - l.Debit; break;
					case "EXP": expAll += l.Debit - l.Credit; break;
				}

				if (l.EntryDate.Year == year)
				{
					if (tc == "REV") dto.Revenue += l.Credit - l.Debit;
					if (tc == "EXP") dto.Expense += l.Debit - l.Credit;
					if (code == "210201") dto.VatOutput += l.Credit - l.Debit;        // VAT output payable
					if (code == "110401") dto.VatInput += l.Debit - l.Credit;         // VAT input
				}
			}
			dto.Cash = Math.Round(dto.Cash, 2);
			dto.Receivables = Math.Round(dto.Receivables, 2);
			dto.Payables = Math.Round(dto.Payables, 2);
			dto.Revenue = Math.Round(dto.Revenue, 2);
			dto.Expense = Math.Round(dto.Expense, 2);
			dto.VatOutput = Math.Round(dto.VatOutput, 2);
			dto.VatInput = Math.Round(dto.VatInput, 2);
			dto.TotalAssets = Math.Round(assetT, 2);
			dto.TotalLiabilities = Math.Round(liabT, 2);
			dto.TotalEquity = Math.Round(eqT + (revAll - expAll), 2);   // fold retained result into equity

			// last 6 months revenue/expense trend
			var now = DateTime.UtcNow;
			for (var i = 5; i >= 0; i--)
			{
				var m = new DateTime(now.Year, now.Month, 1).AddMonths(-i);
				var rev = lines.Where(l => TypeCode(l.AccountId) == "REV" && l.EntryDate.Year == m.Year && l.EntryDate.Month == m.Month).Sum(l => l.Credit - l.Debit);
				var exp = lines.Where(l => TypeCode(l.AccountId) == "EXP" && l.EntryDate.Year == m.Year && l.EntryDate.Month == m.Month).Sum(l => l.Debit - l.Credit);
				dto.Months.Add(new AccMonthPoint { Year = m.Year, Month = m.Month, Revenue = Math.Round(rev, 2), Expense = Math.Round(exp, 2) });
			}

			// top expense accounts (distribution)
			var expBy = lines.Where(l => TypeCode(l.AccountId) == "EXP")
				.GroupBy(l => l.AccountId)
				.Select(g => new { AccountId = g.Key, Amount = g.Sum(x => x.Debit - x.Credit) })
				.Where(x => x.Amount > 0)
				.OrderByDescending(x => x.Amount).Take(5).ToList();
			var expTotal = expBy.Sum(x => x.Amount);
			foreach (var x in expBy)
			{
				accById.TryGetValue(x.AccountId, out var a);
				dto.TopExpenses.Add(new AccTopAccount
				{
					Code = a?.Code ?? "", NameAr = a?.Name ?? "", NameEn = a?.NameEn ?? "",
					Amount = Math.Round(x.Amount, 2), Pct = expTotal > 0 ? (int)Math.Round(100m * x.Amount / expTotal) : 0,
				});
			}

			// counts + recent entries
			dto.PostedCount = await _context.JournalEntries.CountAsync(e => e.CompanyID == companyId && e.Status == "Posted");
			dto.DraftCount = await _context.JournalEntries.CountAsync(e => e.CompanyID == companyId && e.Status == "Draft");
			var recent = await _context.JournalEntries.AsNoTracking()
				.Where(e => e.CompanyID == companyId && (e.Status == "Posted" || e.Status == "Reversed"))
				.OrderByDescending(e => e.ID).Take(8)
				.Select(e => new { e.ID, e.EntryNo, e.EntryDate, e.Description, e.DescriptionEn, e.JournalType, e.Status })
				.ToListAsync();
			bool isAr = System.Globalization.CultureInfo.CurrentUICulture
				.TwoLetterISOLanguageName.Equals("ar", System.StringComparison.OrdinalIgnoreCase);
			foreach (var e in recent)
			{
				var amt = await _context.JournalEntryLines.AsNoTracking().Where(l => l.JournalEntryId == e.ID).SumAsync(l => (decimal?)l.Debit) ?? 0;
				dto.Recent.Add(new AccRecentEntry
				{
					EntryNo = e.EntryNo,
					Date = e.EntryDate,
					// The description the reader's language asks for. /Accounting/JournalEntry and
					// /Accounting/Journals already did this; this list did not, so the same entry read
					// English on one screen and Arabic on the dashboard beside it.
					Description = isAr || string.IsNullOrWhiteSpace(e.DescriptionEn) ? e.Description : e.DescriptionEn,
					JournalType = e.JournalType,
					Status = e.Status,
					Amount = Math.Round(amt, 2),
				});
			}

			// Phase 6 — fixed-asset summary (active assets only)
			var assets = await _context.FixedAssets.AsNoTracking()
				.Where(a => a.CompanyID == companyId && a.Status != "Disposed").ToListAsync();
			dto.AssetCount = assets.Count;
			dto.AssetCost = Math.Round(assets.Sum(a => a.Cost), 2);
			dto.AssetAccumDep = Math.Round(assets.Sum(a => a.AccumulatedDepreciation), 2);

			// Phase 9 — current fiscal year status
			var fy = await _context.FiscalYears.AsNoTracking()
				.Where(y => y.CompanyID == companyId)
				.OrderByDescending(y => y.StartDate).FirstOrDefaultAsync();
			if (fy != null) { dto.FiscalYearName = fy.Name; dto.FiscalYearStatus = fy.Status; }

			return dto;
		}
	}
}
