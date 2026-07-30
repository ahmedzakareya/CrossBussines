using CrossBuy.Models.Context;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL
{
	// Projects & Contracting — P2 (contract: advance + retention).
	// Advance received = a LIABILITY (2104), NOT revenue. Retention = an ASSET balance (1104), NOT an expense.
	// All money posted via the EXISTING JournalEntryService.CreateAndPostAsync (no new writer), tagged with ProjectId.
	// Balances are read straight from the GL (single source of truth — no extra table).
	public class ContractSummary
	{
		public decimal? ContractValue { get; set; }
		public decimal? AdvancePercent { get; set; }
		public decimal? RetentionPercent { get; set; }
		public decimal AdvanceExpected => Math.Round((ContractValue ?? 0m) * (AdvancePercent ?? 0m) / 100m, 2);   // default advance
		public decimal AdvanceBalance { get; set; }     // Cr balance on 2104 for this project (received − recovered)
		public decimal RetentionBalance { get; set; }   // Dr balance on 1104 for this project (withheld − released)
	}

	public interface IContractService
	{
		Task<ContractSummary> GetSummaryAsync(int companyId, int projectId);
		// record an advance received: Dr cash/bank · Cr «Advances from customers» (2104), both tagged ProjectId.
		Task<(bool ok, string? error, int? entryId)> ReceiveAdvanceAsync(int companyId, int projectId, decimal amount, int cashAccountId, DateTime date, int? userId);
		// P6-ب: release retention at handover — collect the withheld balance: Dr cash/bank · Cr «Retention» (1104), both ProjectId.
		Task<(bool ok, string? error, int? entryId)> ReleaseRetentionAsync(int companyId, int projectId, decimal amount, int cashAccountId, DateTime date, int? userId);
		// P6-ج-2: release subcontractor retention — pay the withheld balance: Dr «Subcontractor retention» (2105) · Cr cash/bank, both ProjectId.
		Task<decimal> SubRetentionBalanceAsync(int companyId, int projectId);
		Task<(bool ok, string? error, int? entryId)> ReleaseSubRetentionAsync(int companyId, int projectId, decimal amount, int cashAccountId, DateTime date, int? userId);
	}

	public class ContractService : IContractService
	{
		public const string AdvanceAccountCode = "2104";     // liability — advances from customers
		public const string RetentionAccountCode = "1104";   // asset — retention receivable
		public const string SubRetentionAccountCode = "2105"; // liability — subcontractor retention payable
		private readonly CrossDbContext _db;
		private readonly IJournalEntryService _je;
		public ContractService(CrossDbContext db, IJournalEntryService je) { _db = db; _je = je; }

		private Task<int> AccountIdByCodeAsync(int companyId, string code) =>
			_db.Accounts.Where(a => a.CompanyID == companyId && a.Code == code).Select(a => a.ID).FirstOrDefaultAsync();

		public async Task<ContractSummary> GetSummaryAsync(int companyId, int projectId)
		{
			var p = await _db.Projects.AsNoTracking().FirstOrDefaultAsync(x => x.ID == projectId && x.CompanyID == companyId);
			var s = new ContractSummary { ContractValue = p?.ContractValue, AdvancePercent = p?.AdvancePercent, RetentionPercent = p?.RetentionPercent };

			// balances from the GL (posted lines tagged with this project on the two accounts)
			var advAcc = await AccountIdByCodeAsync(companyId, AdvanceAccountCode);
			var retAcc = await AccountIdByCodeAsync(companyId, RetentionAccountCode);
			var lines = await (from l in _db.JournalEntryLines.AsNoTracking()
							   join en in _db.JournalEntries.AsNoTracking() on l.JournalEntryId equals en.ID
							   where en.CompanyID == companyId && l.ProjectId == projectId && en.Status == "Posted"
									 && (l.AccountId == advAcc || l.AccountId == retAcc)
							   select new { l.AccountId, l.Debit, l.Credit }).ToListAsync();
			s.AdvanceBalance = Math.Round(lines.Where(x => x.AccountId == advAcc).Sum(x => x.Credit - x.Debit), 2);   // liability = credit-normal
			s.RetentionBalance = Math.Round(lines.Where(x => x.AccountId == retAcc).Sum(x => x.Debit - x.Credit), 2); // asset = debit-normal
			return s;
		}

		public async Task<(bool ok, string? error, int? entryId)> ReceiveAdvanceAsync(int companyId, int projectId, decimal amount, int cashAccountId, DateTime date, int? userId)
		{
			if (amount <= 0) return (false, "المبلغ يجب أن يكون أكبر من صفر", null);
			var prj = await _db.Projects.AsNoTracking().FirstOrDefaultAsync(p => p.ID == projectId && p.CompanyID == companyId);
			if (prj == null) return (false, "المشروع غير موجود", null);
			var cash = await _db.Accounts.AsNoTracking().FirstOrDefaultAsync(a => a.ID == cashAccountId && a.CompanyID == companyId && a.IsPostable && a.IsActive);
			if (cash == null) return (false, "حساب النقدية/البنك غير صالح", null);
			var advAcc = await AccountIdByCodeAsync(companyId, AdvanceAccountCode);
			if (advAcc == 0) return (false, "حساب «دفعات مقدمة من عملاء» غير موجود", null);
			var curId = await _db.Currencies.Select(c => c.ID).FirstOrDefaultAsync();   // functional currency (single-currency books)

			var input = new JournalEntryInput
			{
				CompanyID = companyId,
				EntryDate = date,
				JournalType = "Manual",
				CurrencyId = curId,
				SourceType = "ProjectAdvance",
				SourceId = projectId,
				Description = "دفعة مقدمة — مشروع " + prj.Code,
				DescriptionEn = "Advance received — project " + prj.Code,
				Lines = new List<JournalLineInput>
				{
					new() { AccountId = cashAccountId, Debit = amount, Credit = 0, ProjectId = projectId, Description = "استلام دفعة مقدمة" },
					new() { AccountId = advAcc,        Debit = 0, Credit = amount, ProjectId = projectId, Description = "دفعة مقدمة من العميل (التزام)" },
				}
			};
			var (ok, err, entry) = await _je.CreateAndPostAsync(input, userId);
			return (ok, err, entry?.ID);
		}

		// P6-ب: release (collect) accumulated retention for a project. 1104 held as Dr in P4 → now Cr 1104 to reduce it.
		// Dr cash/bank · Cr 1104, both tagged ProjectId. Does NOT touch 1102 (AR) → ar_sub unaffected → direct JE is safe.
		public async Task<(bool ok, string? error, int? entryId)> ReleaseRetentionAsync(int companyId, int projectId, decimal amount, int cashAccountId, DateTime date, int? userId)
		{
			if (amount <= 0) return (false, "المبلغ يجب أن يكون أكبر من صفر", null);
			var prj = await _db.Projects.AsNoTracking().FirstOrDefaultAsync(p => p.ID == projectId && p.CompanyID == companyId);
			if (prj == null) return (false, "المشروع غير موجود", null);
			var cash = await _db.Accounts.AsNoTracking().FirstOrDefaultAsync(a => a.ID == cashAccountId && a.CompanyID == companyId && a.IsPostable && a.IsActive);
			if (cash == null) return (false, "حساب النقدية/البنك غير صالح", null);
			var retAcc = await AccountIdByCodeAsync(companyId, RetentionAccountCode);
			if (retAcc == 0) return (false, "حساب «أرصدة محتجزة لدى العملاء» غير موجود", null);
			var balance = (await GetSummaryAsync(companyId, projectId)).RetentionBalance;
			if (amount > balance) return (false, $"المبلغ ({amount:N2}) يتجاوز رصيد المحتجز للمشروع ({balance:N2})", null);
			var curId = await _db.Currencies.Select(c => c.ID).FirstOrDefaultAsync();

			var input = new JournalEntryInput
			{
				CompanyID = companyId, EntryDate = date, JournalType = "Manual", CurrencyId = curId,
				SourceType = "RetentionRelease", SourceId = projectId,
				Description = "رد محتجز — مشروع " + prj.Code, DescriptionEn = "Retention released — project " + prj.Code,
				Lines = new List<JournalLineInput>
				{
					new() { AccountId = cashAccountId, Debit = amount, Credit = 0, ProjectId = projectId, Description = "تحصيل المحتجز" },
					new() { AccountId = retAcc, Debit = 0, Credit = amount, ProjectId = projectId, Description = "رد رصيد محتجز" },
				}
			};
			var (ok, err, entry) = await _je.CreateAndPostAsync(input, userId);
			return (ok, err, entry?.ID);
		}

		// P6-ج-2: subcontractor retention held as Cr on 2105 (liability) in P6-ج → balance = Cr−Dr on 2105 for the project.
		public async Task<decimal> SubRetentionBalanceAsync(int companyId, int projectId)
		{
			var acc = await AccountIdByCodeAsync(companyId, SubRetentionAccountCode);
			var lines = await (from l in _db.JournalEntryLines.AsNoTracking()
							   join en in _db.JournalEntries.AsNoTracking() on l.JournalEntryId equals en.ID
							   where en.CompanyID == companyId && l.ProjectId == projectId && en.Status == "Posted" && l.AccountId == acc
							   select new { l.Debit, l.Credit }).ToListAsync();
			return Math.Round(lines.Sum(x => x.Credit - x.Debit), 2);   // liability = credit-normal
		}

		// release (pay) accumulated subcontractor retention: Dr 2105 · Cr cash/bank, both ProjectId.
		// Does NOT touch the vendor control (AP) → ap_sub unaffected → direct JE is safe (mirror P6-ب for the customer side).
		public async Task<(bool ok, string? error, int? entryId)> ReleaseSubRetentionAsync(int companyId, int projectId, decimal amount, int cashAccountId, DateTime date, int? userId)
		{
			if (amount <= 0) return (false, "المبلغ يجب أن يكون أكبر من صفر", null);
			var prj = await _db.Projects.AsNoTracking().FirstOrDefaultAsync(p => p.ID == projectId && p.CompanyID == companyId);
			if (prj == null) return (false, "المشروع غير موجود", null);
			var cash = await _db.Accounts.AsNoTracking().FirstOrDefaultAsync(a => a.ID == cashAccountId && a.CompanyID == companyId && a.IsPostable && a.IsActive);
			if (cash == null) return (false, "حساب النقدية/البنك غير صالح", null);
			var retAcc = await AccountIdByCodeAsync(companyId, SubRetentionAccountCode);
			if (retAcc == 0) return (false, "حساب «محتجزات مقاولي الباطن» غير موجود", null);
			var balance = await SubRetentionBalanceAsync(companyId, projectId);
			if (amount > balance) return (false, $"المبلغ ({amount:N2}) يتجاوز رصيد محتجز الباطن للمشروع ({balance:N2})", null);
			var curId = await _db.Currencies.Select(c => c.ID).FirstOrDefaultAsync();

			var input = new JournalEntryInput
			{
				CompanyID = companyId, EntryDate = date, JournalType = "Manual", CurrencyId = curId,
				SourceType = "SubRetentionRelease", SourceId = projectId,
				Description = "رد محتجز باطن — مشروع " + prj.Code, DescriptionEn = "Subcontractor retention released — project " + prj.Code,
				Lines = new List<JournalLineInput>
				{
					new() { AccountId = retAcc, Debit = amount, Credit = 0, ProjectId = projectId, Description = "رد محتجز مقاول الباطن" },
					new() { AccountId = cashAccountId, Debit = 0, Credit = amount, ProjectId = projectId, Description = "دفع محتجز الباطن" },
				}
			};
			var (ok, err, entry) = await _je.CreateAndPostAsync(input, userId);
			return (ok, err, entry?.ID);
		}
	}
}
