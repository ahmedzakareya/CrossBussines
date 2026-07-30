using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Accounting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace CrossBuy.BL
{
	public class JournalLineInput
	{
		public int AccountId { get; set; }
		public decimal Debit { get; set; }
		public decimal Credit { get; set; }
		public int? CostCenterId { get; set; }
		public int? ProjectId { get; set; }
		public int? EmployeeId { get; set; }
		public string? Description { get; set; }
		public string? DescriptionEn { get; set; }
	}

	public class JournalEntryInput
	{
		public int CompanyID { get; set; } = 1;
		public DateTime EntryDate { get; set; }
		public string JournalType { get; set; } = "Manual";
		public string? Description { get; set; }
		public string? DescriptionEn { get; set; }
		public int CurrencyId { get; set; }
		public string? SourceType { get; set; }
		public int? SourceId { get; set; }
		public List<JournalLineInput> Lines { get; set; } = new();
	}

	public interface IJournalEntryService
	{
		Task<(bool ok, string? error, JournalEntry? entry)> CreateDraftAsync(JournalEntryInput input, int? userId);
		Task<(bool ok, string? error)> PostAsync(int entryId, int? userId);
		Task<(bool ok, string? error, JournalEntry? entry)> CreateAndPostAsync(JournalEntryInput input, int? userId);
		// posts WITHOUT opening its own transaction — for callers that already own one (e.g. StockService)
		Task<(bool ok, string? error, JournalEntry? entry)> CreateAndPostNoTxAsync(JournalEntryInput input, int? userId);
		Task<(bool ok, string? error, int? reversalId)> ReverseAsync(int entryId, int? userId, string? reason);
	}

	/// The ONLY component that writes to the GL. Every posting path goes through here so the rules
	/// (balance, postable accounts, open period, numbering, audit) are enforced in one place.
	public class JournalEntryService : IJournalEntryService
	{
#if DEBUG
		internal static string? _testJvModeOverride;   // Phase د measurement ONLY (Isolated/Ambient). Compiled out of Release.
#endif
		private readonly CrossDbContext _context;
		private readonly IFiscalPeriodService _periods;
		private readonly IServiceScopeFactory _scopes;   // HM-1-أ ب-3: isolated JV allocation (short-lived context)
		private readonly IConfiguration _config;
		public JournalEntryService(CrossDbContext context, IFiscalPeriodService periods, IServiceScopeFactory scopes, IConfiguration config)
		{
			_context = context; _periods = periods; _scopes = scopes; _config = config;
		}

		private static decimal R(decimal v) => Math.Round(v, 2, MidpointRounding.AwayFromZero);

		private async Task<int> ResolveCurrencyAsync(int currencyId, int companyId)
		{
			if (currencyId > 0) return currencyId;
			var egp = await _context.Currencies.AsNoTracking().FirstOrDefaultAsync(c => c.Code == "EGP");
			return egp?.ID ?? 0;
		}

		private static JournalEntry BuildEntry(JournalEntryInput input, int currencyId, int periodId, string status, int? userId)
		{
			var e = new JournalEntry
			{
				CompanyID = input.CompanyID,
				EntryNo = null!,   // assigned on post; keep NULL as draft so the filtered unique index ignores it
				EntryDate = input.EntryDate.Date,
				FiscalPeriodId = periodId,
				JournalType = string.IsNullOrWhiteSpace(input.JournalType) ? "Manual" : input.JournalType,
				SourceType = input.SourceType,
				SourceId = input.SourceId,
				CurrencyId = currencyId,
				Description = input.Description,
				DescriptionEn = input.DescriptionEn,
				Status = status,
				CreatedBy = userId,
				CreatedAt = DateTime.UtcNow,
			};
			var i = 1;
			foreach (var l in input.Lines)
				e.Lines.Add(new JournalEntryLine
				{
					LineNo = i++, AccountId = l.AccountId,
					Debit = R(l.Debit), Credit = R(l.Credit),
					CostCenterId = l.CostCenterId, ProjectId = l.ProjectId, EmployeeId = l.EmployeeId,
					CurrencyId = currencyId, Description = l.Description, DescriptionEn = l.DescriptionEn,
				});
			return e;
		}

		public async Task<(bool ok, string? error, JournalEntry? entry)> CreateDraftAsync(JournalEntryInput input, int? userId)
		{
			if (input == null || input.Lines == null || input.Lines.Count == 0)
				return (false, "القيد يجب أن يحتوي على سطر واحد على الأقل", null);
			foreach (var l in input.Lines)
				if (l.Debit > 0 && l.Credit > 0)
					return (false, "السطر لا يمكن أن يكون مدينًا ودائنًا في آن واحد", null);

			var curId = await ResolveCurrencyAsync(input.CurrencyId, input.CompanyID);
			var period = await _periods.ResolveAsync(input.CompanyID, input.EntryDate);
			var entry = BuildEntry(input, curId, period?.ID ?? 0, "Draft", userId);
			_context.JournalEntries.Add(entry);
			await _context.SaveChangesAsync();
			return (true, null, entry);
		}

		public async Task<(bool ok, string? error)> PostAsync(int entryId, int? userId)
		{
			await using var tx = await ScopedTx.BeginOrJoinAsync(_context);
			var entry = await _context.JournalEntries.Include(e => e.Lines).FirstOrDefaultAsync(e => e.ID == entryId);
			if (entry == null) return (false, "القيد غير موجود");
			if (entry.Status != "Draft" && entry.Status != "Submitted") return (false, "لا يمكن ترحيل قيد مُرحَّل أو مُلغى");

			var (ok, err) = await PostInternalAsync(entry, userId);
			if (!ok) return (false, err);
			await tx.CommitAsync();
			return (true, null);
		}

		public async Task<(bool ok, string? error, JournalEntry? entry)> CreateAndPostAsync(JournalEntryInput input, int? userId)
		{
			var (cok, cerr, entry) = await CreateDraftAsync(input, userId);
			if (!cok) return (false, cerr, null);

			await using var tx = await ScopedTx.BeginOrJoinAsync(_context);
			var (ok, err) = await PostInternalAsync(entry!, userId);
			if (!ok)
			{
				// Roll back: when we OWN the tx this undoes the DB AND clears the tracker (so the orphan draft — Added or
				// already-saved — leaves nothing behind and is not re-written by a later SaveChanges in this request).
				// When JOINED it is a no-op; the outer owner rolls back + clears everything (incl. this draft). Either way
				// we must NOT Remove()+SaveChanges() here: after a rollback+Clear that would DELETE a non-existent row → DbUpdateConcurrencyException.
				await tx.RollbackAsync();
				return (false, err, null);
			}
			await tx.CommitAsync();
			return (true, null, entry);
		}

		// same as CreateAndPostAsync but assumes the CALLER already opened a transaction (no nested tx).
		public async Task<(bool ok, string? error, JournalEntry? entry)> CreateAndPostNoTxAsync(JournalEntryInput input, int? userId)
		{
			var (cok, cerr, entry) = await CreateDraftAsync(input, userId);
			if (!cok) return (false, cerr, null);
			var (ok, err) = await PostInternalAsync(entry!, userId);
			if (!ok) return (false, err, null);   // caller's transaction is responsible for rollback
			return (true, null, entry);
		}

		// validate → resolve/lock period → reserve number → mark posted. Caller owns the transaction.
		private async Task<(bool ok, string? error)> PostInternalAsync(JournalEntry entry, int? userId)
		{
			var lines = entry.Lines.ToList();
			if (lines.Count < 2) return (false, "القيد يجب أن يحتوي على سطرين على الأقل");

			foreach (var l in lines)
			{
				if (l.Debit < 0 || l.Credit < 0) return (false, "لا يُسمح بقيم سالبة");
				if ((l.Debit > 0) == (l.Credit > 0)) return (false, "كل سطر يجب أن يكون مدينًا أو دائنًا (وليس الاثنين أو لا شيء)");
			}

			var totalD = R(lines.Sum(l => l.Debit));
			var totalC = R(lines.Sum(l => l.Credit));
			if (totalD != totalC) return (false, $"القيد غير متوازن: مدين {totalD} ≠ دائن {totalC}");
			if (totalD <= 0) return (false, "إجمالي القيد يجب أن يكون أكبر من صفر");

			// accounts: must exist in the company, be active and postable (control/header accounts blocked)
			var accIds = lines.Select(l => l.AccountId).Distinct().ToList();
			var accounts = await _context.Accounts.AsNoTracking()
				.Where(a => a.CompanyID == entry.CompanyID && accIds.Contains(a.ID))
				.ToListAsync();
			var byId = accounts.ToDictionary(a => a.ID);
			// system-generated entries (with a SourceType) may post to control accounts (e.g. AR/AP)
			// through their sub-ledger; manual entries are still blocked from non-postable accounts.
			var isSystem = !string.IsNullOrWhiteSpace(entry.SourceType);
			foreach (var id in accIds)
			{
				if (!byId.TryGetValue(id, out var a)) return (false, $"الحساب رقم {id} غير موجود في هذه الشركة");
				if (!a.IsActive) return (false, $"الحساب {a.Code} غير نشط");
				if (!a.IsPostable && !isSystem) return (false, $"الحساب {a.Code} ({a.Name}) تجميعي/تحكّم — لا يُرحَّل عليه مباشرة");
			}
			// cost-center requirement (cost centers arrive in Phase 2; enforced here for correctness)
			foreach (var l in lines)
				if (byId[l.AccountId].RequireCostCenter && l.CostCenterId == null)
					return (false, $"الحساب {byId[l.AccountId].Code} يتطلب مركز تكلفة");

			// fiscal period must exist and not be Closed
			var period = await _periods.ResolveAsync(entry.CompanyID, entry.EntryDate);
			if (period == null) return (false, "لا توجد فترة مالية تشمل تاريخ القيد");
			if (period.Status == "Closed") return (false, "الفترة المالية مقفولة — لا يمكن الترحيل فيها");
			entry.FiscalPeriodId = period.ID;

			// reserve the entry number (per company per fiscal year). In Isolated mode this opens a short-lived separate
			// connection; a pool-exhaustion / connectivity failure there must surface as a CLEAN error (not a raw
			// SqlException bubbling to a 500) so the ambient sale transaction rolls back gracefully with a message.
			try
			{
				entry.EntryNo = await ReserveEntryNoAsync(entry.CompanyID, period.FiscalYearId, entry.EntryDate.Year);
			}
			catch (Exception)
			{
				return (false, "تعذّر حجز رقم القيد المحاسبي (تسلسل الترقيم أو الاتصال بقاعدة البيانات) — لم يُرحَّل القيد، أعد المحاولة");
			}
			entry.Status = "Posted";
			entry.PostedBy = userId;
			entry.PostedAt = DateTime.UtcNow;
			entry.ModifiedBy = userId;
			entry.ModifiedAt = DateTime.UtcNow;
			await _context.SaveChangesAsync();
			return (true, null);
		}

		// HM-1-أ (ب-1-1): ATOMIC entry-number allocation. The old read-then-write (SELECT NextNumber → +1 → UPDATE)
		// let two concurrent transactions read the same number (the unique index UX_JE_Company_EntryNo then failed the
		// loser). Replaced by ONE `UPDATE … OUTPUT deleted.NextNumber` — an exclusive row lock + pre-increment value in a
		// single statement, no race. It runs on the AMBIENT transaction, so (as today) the number stays inside the
		// caller's transaction and is rolled back with it (gaps only on rollback — acceptable; duplicates impossible).
		private async Task<string> ReserveEntryNoAsync(int companyId, int fiscalYearId, int year)
		{
			// HM-1-أ (ب-1-1 + ب-2-3): ensure-then-allocate in ONE atomic batch. NumberSequences has no unique key on
			// (CompanyID,SequenceKey,FiscalYearId), so a plain "if not exists → insert" races at fiscal-year rollover
			// (two concurrent creators → duplicate rows). UPDLOCK+HOLDLOCK on the existence probe takes a key-range lock
			// (within the ambient transaction) so exactly one inserts; then a single UPDATE … OUTPUT allocates atomically.
			const string sql = @"SET NOCOUNT ON;
IF NOT EXISTS (SELECT 1 FROM dbo.NumberSequences WITH (UPDLOCK, HOLDLOCK) WHERE CompanyID = {0} AND SequenceKey = {1} AND FiscalYearId = {2})
    INSERT INTO dbo.NumberSequences (CompanyID, SequenceKey, FiscalYearId, Prefix, NextNumber, PadLength) VALUES ({0}, {1}, {2}, 'JV', 1, 6);
UPDATE dbo.NumberSequences SET NextNumber = NextNumber + 1 OUTPUT deleted.NextNumber AS [Value] WHERE CompanyID = {0} AND SequenceKey = {1} AND FiscalYearId = {2};";
			// HM-1-أ ب-3 (Decision 1): DEFAULT "Isolated" allocates on a SHORT-LIVED separate context committing in ms
			// (releases the single JV row lock immediately; a rolled-back sale leaves an accepted gap). "Ambient" keeps
			// allocation in the sale's transaction (serializes the whole system) — a TEMPORARY measurement switch.
			int used; string prefix; int pad;
			string? _jvMode = _config["Numbering:JvAllocationMode"];
#if DEBUG
			if (_testJvModeOverride != null) _jvMode = _testJvModeOverride;   // Phase د measurement toggle (Debug builds only)
#endif
			if (string.Equals(_jvMode, "Ambient", StringComparison.OrdinalIgnoreCase))
			{
				used = (await _context.Database.SqlQueryRaw<int>(sql, companyId, "JV", fiscalYearId).ToListAsync()).Single();
				var cfg = await _context.NumberSequences.AsNoTracking().FirstAsync(s => s.CompanyID == companyId && s.SequenceKey == "JV" && s.FiscalYearId == fiscalYearId);
				prefix = cfg.Prefix; pad = cfg.PadLength;
			}
			else
			{
				using var scope = _scopes.CreateScope();
				var db = scope.ServiceProvider.GetRequiredService<CrossDbContext>();
				await using var tx = await db.Database.BeginTransactionAsync();
				used = (await db.Database.SqlQueryRaw<int>(sql, companyId, "JV", fiscalYearId).ToListAsync()).Single();
				var cfg = await db.NumberSequences.AsNoTracking().FirstAsync(s => s.CompanyID == companyId && s.SequenceKey == "JV" && s.FiscalYearId == fiscalYearId);
				prefix = cfg.Prefix; pad = cfg.PadLength;
				await tx.CommitAsync();
			}
			return $"{prefix}-{year}-{used.ToString().PadLeft(pad, '0')}";
		}

		public async Task<(bool ok, string? error, int? reversalId)> ReverseAsync(int entryId, int? userId, string? reason)
		{
			await using var tx = await ScopedTx.BeginOrJoinAsync(_context);
			var entry = await _context.JournalEntries.Include(e => e.Lines).FirstOrDefaultAsync(e => e.ID == entryId);
			if (entry == null) return (false, "القيد غير موجود", null);
			if (entry.Status != "Posted") return (false, "لا يمكن عكس قيد غير مُرحَّل", null);

			// build the mirror entry (debit ↔ credit swapped), dated today (current open period)
			var reversal = new JournalEntry
			{
				CompanyID = entry.CompanyID,
				EntryDate = DateTime.UtcNow.Date,
				JournalType = "Reversing",
				SourceType = "Reversal",
				SourceId = entry.ID,
				CurrencyId = entry.CurrencyId,
				Description = $"عكس القيد {entry.EntryNo}" + (string.IsNullOrWhiteSpace(reason) ? "" : $" — {reason}"),
				DescriptionEn = $"Reversal of {entry.EntryNo}",
				Status = "Draft",
				CreatedBy = userId,
				CreatedAt = DateTime.UtcNow,
			};
			var i = 1;
			foreach (var l in entry.Lines.OrderBy(l => l.LineNo))
				reversal.Lines.Add(new JournalEntryLine
				{
					LineNo = i++, AccountId = l.AccountId,
					Debit = l.Credit, Credit = l.Debit,           // swap
					CostCenterId = l.CostCenterId, ProjectId = l.ProjectId, EmployeeId = l.EmployeeId,
					CurrencyId = l.CurrencyId, Description = l.Description, DescriptionEn = l.DescriptionEn,
				});
			_context.JournalEntries.Add(reversal);
			await _context.SaveChangesAsync();

			var (ok, err) = await PostInternalAsync(reversal, userId);
			if (!ok) { await tx.RollbackAsync(); return (false, $"تعذّر ترحيل قيد العكس: {err}", null); }

			entry.Status = "Reversed";
			entry.ReversedByEntryId = reversal.ID;
			entry.ModifiedBy = userId;
			entry.ModifiedAt = DateTime.UtcNow;
			await _context.SaveChangesAsync();
			await tx.CommitAsync();
			return (true, null, reversal.ID);
		}
	}
}
