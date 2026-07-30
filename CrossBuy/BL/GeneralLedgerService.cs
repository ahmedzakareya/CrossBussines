using CrossBuy.Models.Context;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL
{
	public class TrialBalanceRow
	{
		public int AccountId { get; set; }
		public string Code { get; set; } = "";
		public string NameAr { get; set; } = "";
		public string NameEn { get; set; } = "";
		public string NormalBalance { get; set; } = "D";
		public decimal Debit { get; set; }
		public decimal Credit { get; set; }
		public decimal Balance { get; set; }   // signed per the account's normal balance
	}

	public class TrialBalanceResult
	{
		public List<TrialBalanceRow> Rows { get; set; } = new();
		public decimal TotalDebit { get; set; }
		public decimal TotalCredit { get; set; }
		public bool IsBalanced => Math.Round(TotalDebit - TotalCredit, 2) == 0;
	}

	public class LedgerLine
	{
		public DateTime Date { get; set; }
		public string? EntryNo { get; set; }
		public string? Description { get; set; }
		public decimal Debit { get; set; }
		public decimal Credit { get; set; }
		public decimal Running { get; set; }
	}

	public class AccountStatement
	{
		public int AccountId { get; set; }
		public string Code { get; set; } = "";
		public string NameAr { get; set; } = "";
		public string NameEn { get; set; } = "";
		public string NormalBalance { get; set; } = "D";
		public decimal Opening { get; set; }
		public decimal Closing { get; set; }
		public List<LedgerLine> Lines { get; set; } = new();
	}

	public interface IGeneralLedgerService
	{
		Task<TrialBalanceResult> TrialBalanceAsync(int companyId, DateTime? from, DateTime? to, int? costCenterId = null);
		Task<AccountStatement?> AccountStatementAsync(int companyId, int accountId, DateTime? from, DateTime? to, int? costCenterId = null);
	}

	public class GeneralLedgerService : IGeneralLedgerService
	{
		private readonly CrossDbContext _context;
		public GeneralLedgerService(CrossDbContext context) { _context = context; }

		public async Task<TrialBalanceResult> TrialBalanceAsync(int companyId, DateTime? fromDate, DateTime? toDate, int? costCenterId = null)
		{
			var rows = await (from l in _context.JournalEntryLines.AsNoTracking()
							  join e in _context.JournalEntries.AsNoTracking() on l.JournalEntryId equals e.ID
							  where e.CompanyID == companyId && (e.Status == "Posted" || e.Status == "Reversed")
									&& (fromDate == null || e.EntryDate >= fromDate) && (toDate == null || e.EntryDate <= toDate)
									&& (costCenterId == null || l.CostCenterId == costCenterId)
							  select new { l.AccountId, l.Debit, l.Credit }).ToListAsync();

			var grouped = rows.GroupBy(r => r.AccountId)
				.Select(g => new { AccountId = g.Key, D = g.Sum(x => x.Debit), C = g.Sum(x => x.Credit) })
				.ToList();

			var accIds = grouped.Select(g => g.AccountId).ToList();
			var accounts = await _context.Accounts.AsNoTracking().Where(a => accIds.Contains(a.ID)).ToListAsync();
			var types = await _context.AccountTypes.AsNoTracking().ToDictionaryAsync(t => t.ID);

			var result = new TrialBalanceResult();
			foreach (var g in grouped)
			{
				var a = accounts.FirstOrDefault(x => x.ID == g.AccountId);
				if (a == null) continue;
				var nb = types.TryGetValue(a.AccountTypeId, out var t) ? t.NormalBalance : "D";
				var bal = nb == "D" ? g.D - g.C : g.C - g.D;
				result.Rows.Add(new TrialBalanceRow
				{
					AccountId = a.ID, Code = a.Code, NameAr = a.Name, NameEn = a.NameEn,
					NormalBalance = nb, Debit = Math.Round(g.D, 2), Credit = Math.Round(g.C, 2),
					Balance = Math.Round(bal, 2),
				});
			}
			result.Rows = result.Rows.OrderBy(r => r.Code).ToList();
			result.TotalDebit = Math.Round(result.Rows.Sum(r => r.Debit), 2);
			result.TotalCredit = Math.Round(result.Rows.Sum(r => r.Credit), 2);
			return result;
		}

		public async Task<AccountStatement?> AccountStatementAsync(int companyId, int accountId, DateTime? fromDate, DateTime? toDate, int? costCenterId = null)
		{
			var a = await _context.Accounts.AsNoTracking().FirstOrDefaultAsync(x => x.ID == accountId && x.CompanyID == companyId);
			if (a == null) return null;
			var types = await _context.AccountTypes.AsNoTracking().ToDictionaryAsync(t => t.ID);
			var nb = types.TryGetValue(a.AccountTypeId, out var t) ? t.NormalBalance : "D";

			var all = await (from l in _context.JournalEntryLines.AsNoTracking()
							 join e in _context.JournalEntries.AsNoTracking() on l.JournalEntryId equals e.ID
							 where e.CompanyID == companyId && (e.Status == "Posted" || e.Status == "Reversed") && l.AccountId == accountId
									&& (costCenterId == null || l.CostCenterId == costCenterId)
							 select new { l.Debit, l.Credit, e.EntryDate, e.EntryNo, l.Description })
							 .ToListAsync();

			decimal Signed(decimal d, decimal c) => nb == "D" ? d - c : c - d;

			var opening = fromDate == null ? 0m
				: all.Where(x => x.EntryDate < fromDate).Sum(x => Signed(x.Debit, x.Credit));

			var inRange = all
				.Where(x => (fromDate == null || x.EntryDate >= fromDate) && (toDate == null || x.EntryDate <= toDate))
				.OrderBy(x => x.EntryDate).ThenBy(x => x.EntryNo)
				.ToList();

			var st = new AccountStatement
			{
				AccountId = a.ID, Code = a.Code, NameAr = a.Name, NameEn = a.NameEn,
				NormalBalance = nb, Opening = Math.Round(opening, 2),
			};
			var running = opening;
			foreach (var x in inRange)
			{
				running += Signed(x.Debit, x.Credit);
				st.Lines.Add(new LedgerLine
				{
					Date = x.EntryDate, EntryNo = x.EntryNo, Description = x.Description,
					Debit = Math.Round(x.Debit, 2), Credit = Math.Round(x.Credit, 2), Running = Math.Round(running, 2),
				});
			}
			st.Closing = Math.Round(running, 2);
			return st;
		}
	}
}
