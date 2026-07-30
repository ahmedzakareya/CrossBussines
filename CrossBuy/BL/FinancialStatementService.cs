using CrossBuy.Models.Context;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL
{
	public class StatementLine
	{
		public int AccountId { get; set; }
		public string Code { get; set; } = "";
		public string NameAr { get; set; } = "";
		public string NameEn { get; set; } = "";
		public decimal Amount { get; set; }
	}

	public class StatementGroup
	{
		public string Key { get; set; } = "";
		public string TitleAr { get; set; } = "";
		public string TitleEn { get; set; } = "";
		public List<StatementLine> Lines { get; set; } = new();
		public decimal Total => Math.Round(Lines.Sum(l => l.Amount), 2);
	}

	public class IncomeStatement
	{
		public DateTime From { get; set; }
		public DateTime To { get; set; }
		public StatementGroup Revenue { get; set; } = new() { Key = "REV", TitleAr = "الإيرادات", TitleEn = "Revenue" };
		public StatementGroup Expenses { get; set; } = new() { Key = "EXP", TitleAr = "المصروفات", TitleEn = "Expenses" };
		public decimal NetProfit => Math.Round(Revenue.Total - Expenses.Total, 2);
	}

	public class BalanceSheet
	{
		public DateTime AsOf { get; set; }
		public StatementGroup Assets { get; set; } = new() { Key = "ASSET", TitleAr = "الأصول", TitleEn = "Assets" };
		public StatementGroup Liabilities { get; set; } = new() { Key = "LIAB", TitleAr = "الالتزامات", TitleEn = "Liabilities" };
		public StatementGroup Equity { get; set; } = new() { Key = "EQUITY", TitleAr = "حقوق الملكية", TitleEn = "Equity" };
		public decimal NetProfit { get; set; }   // current-period result, folded into equity
		public decimal TotalAssets => Assets.Total;
		public decimal TotalLiabAndEquity => Math.Round(Liabilities.Total + Equity.Total + NetProfit, 2);
		public bool IsBalanced => Math.Round(TotalAssets - TotalLiabAndEquity, 2) == 0;
	}

	public class CashFlowStatement
	{
		public DateTime From { get; set; }
		public DateTime To { get; set; }
		public decimal Operating { get; set; }
		public decimal Investing { get; set; }
		public decimal Financing { get; set; }
		public decimal BeginningCash { get; set; }
		public decimal NetChange => Math.Round(Operating + Investing + Financing, 2);
		public decimal EndingCash => Math.Round(BeginningCash + NetChange, 2);
	}

	public interface IFinancialStatementService
	{
		Task<IncomeStatement> IncomeStatementAsync(int companyId, DateTime from, DateTime to);
		Task<BalanceSheet> BalanceSheetAsync(int companyId, DateTime asOf);
		Task<CashFlowStatement> CashFlowAsync(int companyId, DateTime from, DateTime to);
	}

	public class FinancialStatementService : IFinancialStatementService
	{
		private readonly CrossDbContext _context;
		public FinancialStatementService(CrossDbContext context) { _context = context; }

		private static decimal R(decimal v) => Math.Round(v, 2, MidpointRounding.AwayFromZero);

		// per-account net movement (Debit - Credit) over an optional date window, posted/reversed only
		private async Task<List<(int AccountId, decimal Net)>> MovementAsync(int companyId, DateTime? fromDate, DateTime? toDate)
		{
			var rows = await (from l in _context.JournalEntryLines.AsNoTracking()
							  join e in _context.JournalEntries.AsNoTracking() on l.JournalEntryId equals e.ID
							  where e.CompanyID == companyId && (e.Status == "Posted" || e.Status == "Reversed")
									&& (fromDate == null || e.EntryDate >= fromDate) && (toDate == null || e.EntryDate <= toDate)
							  select new { l.AccountId, l.Debit, l.Credit }).ToListAsync();
			return rows.GroupBy(r => r.AccountId)
					   .Select(g => (g.Key, R(g.Sum(x => x.Debit) - g.Sum(x => x.Credit))))
					   .ToList();
		}

		public async Task<IncomeStatement> IncomeStatementAsync(int companyId, DateTime from, DateTime to)
		{
			var stmt = new IncomeStatement { From = from.Date, To = to.Date };
			var move = await MovementAsync(companyId, from.Date, to.Date);
			var accounts = await _context.Accounts.AsNoTracking().Where(a => a.CompanyID == companyId).ToListAsync();
			var types = await _context.AccountTypes.AsNoTracking().ToDictionaryAsync(t => t.ID, t => t.Code);

			foreach (var m in move)
			{
				var a = accounts.FirstOrDefault(x => x.ID == m.AccountId);
				if (a == null || !types.TryGetValue(a.AccountTypeId, out var tc)) continue;
				if (tc == "REV")
					stmt.Revenue.Lines.Add(new StatementLine { AccountId = a.ID, Code = a.Code, NameAr = a.Name, NameEn = a.NameEn, Amount = -m.Net });   // revenue is credit-normal
				else if (tc == "EXP")
					stmt.Expenses.Lines.Add(new StatementLine { AccountId = a.ID, Code = a.Code, NameAr = a.Name, NameEn = a.NameEn, Amount = m.Net });    // expense is debit-normal
			}
			stmt.Revenue.Lines = stmt.Revenue.Lines.Where(l => l.Amount != 0).OrderBy(l => l.Code).ToList();
			stmt.Expenses.Lines = stmt.Expenses.Lines.Where(l => l.Amount != 0).OrderBy(l => l.Code).ToList();
			return stmt;
		}

		public async Task<BalanceSheet> BalanceSheetAsync(int companyId, DateTime asOf)
		{
			var bs = new BalanceSheet { AsOf = asOf.Date };
			var move = await MovementAsync(companyId, null, asOf.Date);
			var accounts = await _context.Accounts.AsNoTracking().Where(a => a.CompanyID == companyId).ToListAsync();
			var types = await _context.AccountTypes.AsNoTracking().ToDictionaryAsync(t => t.ID, t => t.Code);

			decimal rev = 0, exp = 0;
			foreach (var m in move)
			{
				var a = accounts.FirstOrDefault(x => x.ID == m.AccountId);
				if (a == null || !types.TryGetValue(a.AccountTypeId, out var tc)) continue;
				var line = new StatementLine { AccountId = a.ID, Code = a.Code, NameAr = a.Name, NameEn = a.NameEn };
				switch (tc)
				{
					case "ASSET": line.Amount = m.Net; bs.Assets.Lines.Add(line); break;          // debit-normal
					case "LIAB": line.Amount = -m.Net; bs.Liabilities.Lines.Add(line); break;      // credit-normal
					case "EQUITY": line.Amount = -m.Net; bs.Equity.Lines.Add(line); break;         // credit-normal
					case "REV": rev += -m.Net; break;
					case "EXP": exp += m.Net; break;
				}
			}
			bs.NetProfit = R(rev - exp);   // result for the period up to AsOf → retained in equity
			bs.Assets.Lines = bs.Assets.Lines.Where(l => l.Amount != 0).OrderBy(l => l.Code).ToList();
			bs.Liabilities.Lines = bs.Liabilities.Lines.Where(l => l.Amount != 0).OrderBy(l => l.Code).ToList();
			bs.Equity.Lines = bs.Equity.Lines.Where(l => l.Amount != 0).OrderBy(l => l.Code).ToList();
			return bs;
		}

		public async Task<CashFlowStatement> CashFlowAsync(int companyId, DateTime fromDate, DateTime toDate)
		{
			var start = fromDate.Date; var end = toDate.Date;
			var cf = new CashFlowStatement { From = start, To = end };
			var cashIds = await _context.Accounts.AsNoTracking()
				.Where(a => a.CompanyID == companyId && a.Code.StartsWith("1101"))
				.Select(a => a.ID).ToListAsync();
			if (cashIds.Count == 0) return cf;

			// beginning cash = net movement of cash accounts before the period start
			var before = await (from l in _context.JournalEntryLines.AsNoTracking()
								join e in _context.JournalEntries.AsNoTracking() on l.JournalEntryId equals e.ID
								where e.CompanyID == companyId && (e.Status == "Posted" || e.Status == "Reversed")
									  && e.EntryDate < start && cashIds.Contains(l.AccountId)
								select l.Debit - l.Credit).ToListAsync();
			cf.BeginningCash = R(before.Sum());

			// for every entry in the period that moves cash, classify the cash effect by the
			// CashFlowCategory of the contra (non-cash) accounts in the same entry.
			var entryIds = await (from l in _context.JournalEntryLines.AsNoTracking()
								  join e in _context.JournalEntries.AsNoTracking() on l.JournalEntryId equals e.ID
								  where e.CompanyID == companyId && (e.Status == "Posted" || e.Status == "Reversed")
										&& e.EntryDate >= start && e.EntryDate <= end && cashIds.Contains(l.AccountId)
								  select l.JournalEntryId).Distinct().ToListAsync();
			if (entryIds.Count == 0) return cf;

			var lines = await _context.JournalEntryLines.AsNoTracking().Where(l => entryIds.Contains(l.JournalEntryId)).ToListAsync();
			var cashSet = cashIds.ToHashSet();
			var cats = await _context.Accounts.AsNoTracking().Where(a => a.CompanyID == companyId)
				.ToDictionaryAsync(a => a.ID, a => a.CashFlowCategory);

			foreach (var l in lines)
			{
				if (cashSet.Contains(l.AccountId)) continue;   // skip the cash legs themselves
				var effect = l.Credit - l.Debit;               // cash effect attributable to this contra account
				var cat = (cats.TryGetValue(l.AccountId, out var c) ? c : null) ?? "Operating";
				switch (cat)
				{
					case "Investing": cf.Investing += effect; break;
					case "Financing": cf.Financing += effect; break;
					default: cf.Operating += effect; break;
				}
			}
			cf.Operating = R(cf.Operating); cf.Investing = R(cf.Investing); cf.Financing = R(cf.Financing);
			return cf;
		}
	}
}
