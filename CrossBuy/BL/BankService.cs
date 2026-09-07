using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Accounting;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL
{
	public class ReconLine
	{
		public int LineId { get; set; }
		public string? EntryNo { get; set; }
		public DateTime Date { get; set; }
		public string? Description { get; set; }
		public decimal Debit { get; set; }
		public decimal Credit { get; set; }
		public bool Cleared { get; set; }
	}

	public interface IBankService
	{
		Task<List<BankAccount>> GetBankAccountsAsync(int companyId);
		Task<BankAccount> CreateBankAccountAsync(int companyId, string bankName, string? bankNameEn, string? accountNumber, string? iban, int glAccountId, decimal openingBalance, int? currencyId = null, decimal? foreignBalance = null);
		Task<List<CashBox>> GetCashBoxesAsync(int companyId);
		Task<CashBox> CreateCashBoxAsync(int companyId, string name, string? nameEn, int glAccountId, int? custodianEmployeeId);

		/// Internal transfer between two cash/bank GL accounts → Dr destination / Cr source.
		Task<(bool ok, string? error)> TransferAsync(int companyId, int fromGlAccountId, int toGlAccountId, decimal amount, DateTime date, string? notes, int? userId);

		/// Posted GL lines for a bank's linked account up to a date, with cleared flag (for reconciliation).
		Task<(BankAccount? bank, decimal bookBalance, List<ReconLine> lines)> GetReconciliationViewAsync(int companyId, int bankAccountId, DateTime asOf);

		Task<(bool ok, string? error, int? reconId)> ReconcileAsync(int companyId, int bankAccountId, DateTime statementDate, decimal statementBalance, List<int> clearedLineIds);
		Task<List<BankReconciliation>> GetReconciliationsAsync(int companyId);
	}

	public class BankService : IBankService
	{
		private readonly CrossDbContext _context;
		private readonly IJournalEntryService _journals;
		public BankService(CrossDbContext context, IJournalEntryService journals) { _context = context; _journals = journals; }

		private static decimal R(decimal v) => Math.Round(v, 2, MidpointRounding.AwayFromZero);

		public async Task<List<BankAccount>> GetBankAccountsAsync(int companyId) =>
			await _context.BankAccounts.AsNoTracking().Where(b => b.CompanyID == companyId).OrderBy(b => b.BankName).ToListAsync();

		public async Task<BankAccount> CreateBankAccountAsync(int companyId, string bankName, string? bankNameEn, string? accountNumber, string? iban, int glAccountId, decimal openingBalance, int? currencyId = null, decimal? foreignBalance = null)
		{
			var b = new BankAccount { CompanyID = companyId, BankName = bankName, BankNameEn = bankNameEn, AccountNumber = accountNumber, IBAN = iban, GlAccountId = glAccountId, OpeningBalance = openingBalance, CurrencyId = currencyId, ForeignBalance = foreignBalance, IsActive = true, CreatedAt = DateTime.UtcNow };
			_context.BankAccounts.Add(b);
			await _context.SaveChangesAsync();
			return b;
		}

		public async Task<List<CashBox>> GetCashBoxesAsync(int companyId) =>
			await _context.CashBoxes.AsNoTracking().Where(c => c.CompanyID == companyId).OrderBy(c => c.Name).ToListAsync();

		public async Task<CashBox> CreateCashBoxAsync(int companyId, string name, string? nameEn, int glAccountId, int? custodianEmployeeId)
		{
			var c = new CashBox { CompanyID = companyId, Name = name, NameEn = nameEn, GlAccountId = glAccountId, CustodianEmployeeId = custodianEmployeeId, IsActive = true, CreatedAt = DateTime.UtcNow };
			_context.CashBoxes.Add(c);
			await _context.SaveChangesAsync();
			return c;
		}

		public async Task<(bool ok, string? error)> TransferAsync(int companyId, int fromGlAccountId, int toGlAccountId, decimal amount, DateTime date, string? notes, int? userId)
		{
			if (fromGlAccountId == toGlAccountId) return (false, "You cannot transfer to the same account");
			if (amount <= 0) return (false, "The amount must be greater than zero");
			var (ok, err, _) = await _journals.CreateAndPostAsync(new JournalEntryInput
			{
				CompanyID = companyId, EntryDate = date, JournalType = "Auto", SourceType = "Transfer",
				Description = "تحويل نقدي بين الحسابات" + (string.IsNullOrWhiteSpace(notes) ? "" : $" — {notes}"),
				DescriptionEn = "Internal cash transfer",
				Lines = new List<JournalLineInput>
				{
					new() { AccountId = toGlAccountId, Debit = R(amount), Credit = 0, Description = "تحويل وارد" },
					new() { AccountId = fromGlAccountId, Debit = 0, Credit = R(amount), Description = "Outgoing transfer" },
				},
			}, userId);
			return (ok, err);
		}

		public async Task<(BankAccount? bank, decimal bookBalance, List<ReconLine> lines)> GetReconciliationViewAsync(int companyId, int bankAccountId, DateTime asOf)
		{
			var bank = await _context.BankAccounts.AsNoTracking().FirstOrDefaultAsync(b => b.ID == bankAccountId && b.CompanyID == companyId);
			if (bank == null) return (null, 0, new());

			// culture-aware line description: English (DescriptionEn) when UI is not Arabic
			var isEn = System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName != "ar";
			var rows = await (from l in _context.JournalEntryLines.AsNoTracking()
							  join e in _context.JournalEntries.AsNoTracking() on l.JournalEntryId equals e.ID
							  where e.CompanyID == companyId && (e.Status == "Posted" || e.Status == "Reversed")
									&& l.AccountId == bank.GlAccountId && e.EntryDate <= asOf.Date
							  orderby e.EntryDate, e.EntryNo
							  select new { l.ID, e.EntryNo, e.EntryDate, l.Description, l.DescriptionEn, l.Debit, l.Credit }).ToListAsync();

			// already-cleared lines from prior reconciliations of this bank
			var reconIds = await _context.BankReconciliations.AsNoTracking().Where(r => r.BankAccountId == bankAccountId).Select(r => r.ID).ToListAsync();
			var clearedSet = reconIds.Count == 0 ? new HashSet<int>()
				: (await _context.BankReconciliationLines.AsNoTracking().Where(x => reconIds.Contains(x.BankReconciliationId) && x.Cleared).Select(x => x.JournalEntryLineId).ToListAsync()).ToHashSet();

			var lines = rows.Select(r => new ReconLine { LineId = r.ID, EntryNo = r.EntryNo, Date = r.EntryDate, Description = (isEn && !string.IsNullOrWhiteSpace(r.DescriptionEn)) ? r.DescriptionEn! : r.Description, Debit = r.Debit, Credit = r.Credit, Cleared = clearedSet.Contains(r.ID) }).ToList();
			var bookBalance = R(bank.OpeningBalance + rows.Sum(r => r.Debit - r.Credit));
			return (bank, bookBalance, lines);
		}

		public async Task<(bool ok, string? error, int? reconId)> ReconcileAsync(int companyId, int bankAccountId, DateTime statementDate, decimal statementBalance, List<int> clearedLineIds)
		{
			var (bank, bookBalance, lines) = await GetReconciliationViewAsync(companyId, bankAccountId, statementDate);
			if (bank == null) return (false, "Bank account not found", null);

			var cleared = (clearedLineIds ?? new()).ToHashSet();
			var clearedBalance = R(bank.OpeningBalance + lines.Where(l => cleared.Contains(l.LineId)).Sum(l => l.Debit - l.Credit));
			var diff = R(statementBalance - clearedBalance);

			var recon = new BankReconciliation
			{
				CompanyID = companyId, BankAccountId = bankAccountId, StatementDate = statementDate.Date,
				StatementBalance = R(statementBalance), BookBalance = bookBalance, ClearedBalance = clearedBalance,
				Difference = diff, Status = diff == 0 ? "Reconciled" : "Pending", CreatedAt = DateTime.UtcNow,
			};
			_context.BankReconciliations.Add(recon);
			await _context.SaveChangesAsync();

			foreach (var id in cleared)
				_context.BankReconciliationLines.Add(new BankReconciliationLine { BankReconciliationId = recon.ID, JournalEntryLineId = id, Cleared = true });
			await _context.SaveChangesAsync();
			return (true, null, recon.ID);
		}

		public async Task<List<BankReconciliation>> GetReconciliationsAsync(int companyId) =>
			await _context.BankReconciliations.AsNoTracking().Where(r => r.CompanyID == companyId).OrderByDescending(r => r.ID).ToListAsync();
	}
}
