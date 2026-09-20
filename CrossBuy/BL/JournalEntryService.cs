using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Accounting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging;

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
		// A DRAFT MANUAL entry is the only thing that may be changed after the fact. Everything else is
		// corrected by a reversal, which is why these two take the same guard (see LoadEditableDraftAsync).
		Task<(bool ok, string? error)> UpdateDraftAsync(int entryId, int companyId, JournalEntryInput input, int? userId);
		Task<(bool ok, string? error)> DeleteDraftAsync(int entryId, int companyId, int? userId);
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
		private readonly ICurrencyRounding _rounding;    // HM-2: functional-currency rounding
		private readonly IStringLocalizer<CrossBuy.SharedResources> L;
		private readonly ILogger<JournalEntryService> _logger;
		// HM-2: counted (not failing) — how many times a rounding remainder was loaded onto an eligible P&L line. Surfaced in inv-test-integrity.
		public static long RoundingDiffLoads;
		// HM-2: the CLOSED explicit forbidden P&L set (accounts externally-reconciled or feeding a control report): FX gain/loss
		// (4902/4903/5902/5903), cash over/short = drawer reconciliation (520111), production cost variance = variance report (520109).
		// Census verified: no "discount allowed" account exists; no P&L-type tax account (VAT is a liability). All balance-sheet
		// accounts (AR/AP/inventory/GRNI/tax/cash/bank) are auto-excluded by the "P&L only" (AccountType 4/5) eligibility rule.
		private static readonly HashSet<string> ForbiddenDiffAccounts = new() { "4902", "4903", "5902", "5903", "520111", "520109" };
		private readonly CrossBuy.BL.Platform.IBusinessEventService _events;   // Platform Kernel: durable business facts (in-transaction)
		public JournalEntryService(CrossDbContext context, IFiscalPeriodService periods, IServiceScopeFactory scopes, IConfiguration config, ICurrencyRounding rounding, IStringLocalizer<CrossBuy.SharedResources> localizer, ILogger<JournalEntryService> logger, CrossBuy.BL.Platform.IBusinessEventService events)
		{
			_context = context; _periods = periods; _scopes = scopes; _config = config; _rounding = rounding; L = localizer; _logger = logger; _events = events;
		}

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
					Debit = l.Debit, Credit = l.Credit,   // HM-2: raw (functional) — rounded to functional dp in ApplyCurrencyRoundingAsync
					CostCenterId = l.CostCenterId, ProjectId = l.ProjectId, EmployeeId = l.EmployeeId,
					CurrencyId = currencyId, Description = l.Description, DescriptionEn = l.DescriptionEn,
				});
			return e;
		}

		public async Task<(bool ok, string? error, JournalEntry? entry)> CreateDraftAsync(JournalEntryInput input, int? userId)
		{
			if (input == null || input.Lines == null || input.Lines.Count == 0)
				return (false, "The entry must contain at least one line", null);

			var curId = await ResolveCurrencyAsync(input.CurrencyId, input.CompanyID);
			var period = await _periods.ResolveAsync(input.CompanyID, input.EntryDate);
			var entry = BuildEntry(input, curId, period?.ID ?? 0, "Draft", userId);   // raw (functional) Debit/Credit
			// HM-2: round to functional dp + load the rounding remainder on an eligible P&L line (or reject) — CENTRALIZED here.
			var (rok, rerr) = await ApplyCurrencyRoundingAsync(entry);
			if (!rok) return (false, rerr, null);
			// the both-debit-and-credit sanity now runs on the ROUNDED lines (moved from raw, so a caller error is caught at draft time on final values)
			foreach (var l in entry.Lines)
				if (l.Debit > 0 && l.Credit > 0)
					return (false, "A line cannot be both a debit and a credit", null);
			_context.JournalEntries.Add(entry);
			await _context.SaveChangesAsync();
			return (true, null, entry);
		}

		/// EVERY reason a draft may not be changed, answered in ONE place so the edit path and the delete
		/// path cannot drift apart. The list's buttons only HINT at editability from what the row carries;
		/// this is the gate, and it re-checks all of it.
		private async Task<(bool ok, string? error, JournalEntry? entry)> LoadEditableDraftAsync(int entryId, int companyId)
		{
			// Company first, and a wrong company reads as "not found" rather than "forbidden": a refusal
			// that distinguishes the two tells a caller whether an id exists in another tenant.
			var entry = await _context.JournalEntries.Include(e => e.Lines)
				.FirstOrDefaultAsync(e => e.ID == entryId && e.CompanyID == companyId);
			if (entry == null) return (false, "Journal entry not found", null);

			if (entry.Status != "Draft")
				return (false, "Only a draft entry can be changed — a posted entry is corrected by a reversal", null);

			// Auto / Recurring / Reversing / Opening / Closing are DERIVED: each mirrors a document or a
			// routine, so editing one makes the ledger disagree with whatever produced it.
			if (!string.Equals(entry.JournalType, "Manual", StringComparison.Ordinal))
				return (false, "Only a manual entry can be changed — this entry was generated by the system", null);

			// A second belt over the type. The type is a string and can be wrong in imported data; the link
			// to a source document cannot.
			if (!string.IsNullOrWhiteSpace(entry.SourceType) || entry.SourceId != null)
				return (false, "This entry belongs to a source document and cannot be changed on its own", null);

			if (entry.ReversedByEntryId != null)
				return (false, "This entry already has a reversal attached", null);

			// The period the entry SITS in must still be open — closing a period must freeze what is in it,
			// including its drafts.
			var period = await _periods.ResolveAsync(entry.CompanyID, entry.EntryDate);
			if (period == null) return (false, "No fiscal period covers the entry date", null);
			if (AccountingPeriodStatuses.BlocksPosting(period.Status))
				return (false, "The fiscal period is closed — posting into it is not allowed", null);

			return (true, null, entry);
		}

		public async Task<(bool ok, string? error)> UpdateDraftAsync(int entryId, int companyId, JournalEntryInput input, int? userId)
		{
			if (input == null || input.Lines == null || input.Lines.Count == 0)
				return (false, "The entry must contain at least one line");

			await using var tx = await ScopedTx.BeginOrJoinAsync(_context);
			var (gok, gerr, entry) = await LoadEditableDraftAsync(entryId, companyId);
			if (!gok) return (false, gerr);

			// The NEW date's period is a SECOND check, not the same one: an edit can move an entry into a
			// period the original never touched, and that period may be closed even when the original's is not.
			var newPeriod = await _periods.ResolveAsync(companyId, input.EntryDate);
			if (newPeriod == null) return (false, "No fiscal period covers the entry date");
			if (AccountingPeriodStatuses.BlocksPosting(newPeriod.Status))
				return (false, "The fiscal period is closed — posting into it is not allowed");

			// REBUILT through the same path the create uses, never patched in place: ApplyCurrencyRoundingAsync
			// loads the rounding remainder onto a line, so patching accumulates remainders edit after edit, and a
			// second validator written here would drift from the one in CreateDraftAsync.
			input.CompanyID = companyId;
			var curId = await ResolveCurrencyAsync(input.CurrencyId, companyId);
			var rebuilt = BuildEntry(input, curId, newPeriod.ID, "Draft", entry!.CreatedBy);
			var (rok, rerr) = await ApplyCurrencyRoundingAsync(rebuilt);
			if (!rok) return (false, rerr);
			foreach (var l in rebuilt.Lines)
				if (l.Debit > 0 && l.Credit > 0)
					return (false, "A line cannot be both a debit and a credit");

			// Deleted and re-inserted in two saves, not one: EF does not guarantee that deletes run before
			// inserts inside a single SaveChanges, and the lines carry a per-entry line number.
			_context.JournalEntryLines.RemoveRange(entry.Lines);
			entry.Lines.Clear();
			await _context.SaveChangesAsync();

			foreach (var l in rebuilt.Lines) { l.ID = 0; l.JournalEntryId = entry.ID; entry.Lines.Add(l); }
			entry.EntryDate = rebuilt.EntryDate;
			entry.FiscalPeriodId = newPeriod.ID;
			entry.CurrencyId = curId;
			entry.Description = rebuilt.Description;
			entry.DescriptionEn = rebuilt.DescriptionEn;
			// EntryNo stays NULL. A draft never holds a number and an edit must not reserve one — the filtered
			// unique index depends on drafts staying NULL.
			entry.ModifiedBy = userId;
			entry.ModifiedAt = DateTime.UtcNow;

			await _context.SaveChangesAsync();
			await tx.CommitAsync();
			return (true, null);
		}

		public async Task<(bool ok, string? error)> DeleteDraftAsync(int entryId, int companyId, int? userId)
		{
			await using var tx = await ScopedTx.BeginOrJoinAsync(_context);
			var (gok, gerr, entry) = await LoadEditableDraftAsync(entryId, companyId);
			if (!gok) return (false, gerr);

			var total = entry!.Lines.Sum(l => l.Debit);
			_context.JournalEntryLines.RemoveRange(entry.Lines);
			_context.JournalEntries.Remove(entry);
			await _context.SaveChangesAsync();
			await tx.CommitAsync();
			// The row is gone, so ModifiedBy cannot record who did it — the log is the only trace left.
			_logger?.LogInformation("Draft journal entry {Id} (company {Co}, total {Total}) deleted by user {User}", entryId, companyId, total, userId);
			return (true, null);
		}

		// HM-2 (centralized): round every line to the JE's FUNCTIONAL currency dp (Debit/Credit are functional amounts), then
		// make the entry balance by construction. A CALLER imbalance (rawDiff) is REJECTED, never swallowed; only the pure
		// rounding remainder is loaded — on the largest eligible P&L line, within the guard — counted + logged (visible, not silent).
		private async Task<(bool ok, string? error)> ApplyCurrencyRoundingAsync(JournalEntry entry)
		{
			int dp = await _rounding.DecimalsAsync(entry.CompanyID, null);   // functional dp (Debit/Credit are functional)
			decimal unit = 1m; for (int i = 0; i < dp; i++) unit /= 10m;     // 10^(-dp) exactly, no double/Pow

			// 1) CALLER imbalance on the RAW (pre-rounding) lines ⇒ rejected. A caller bug is never absorbed as a rounding diff.
			decimal rawDiff = entry.Lines.Sum(l => l.Debit) - entry.Lines.Sum(l => l.Credit);
			decimal rawEps = unit / 10000m;   // 4 orders of magnitude below the functional unit (see HM-2 report) — never masks a real imbalance
			if (Math.Abs(rawDiff) > rawEps)
				return (false, L["Journal entry is unbalanced by {0} before rounding — rejected (a caller imbalance is never absorbed as a rounding difference).", rawDiff]);

			// 2) round each line to the functional dp.
			foreach (var l in entry.Lines)
			{
				l.Debit = Math.Round(l.Debit, dp, MidpointRounding.AwayFromZero);
				l.Credit = Math.Round(l.Credit, dp, MidpointRounding.AwayFromZero);
			}

			// 3) the pure ROUNDING remainder.
			decimal roundingDiff = entry.Lines.Sum(l => l.Debit) - entry.Lines.Sum(l => l.Credit);
			if (roundingDiff == 0m) return (true, null);

			decimal maxAllowed = entry.Lines.Count * unit;   // at most one functional unit of rounding error per line
			if (Math.Abs(roundingDiff) > maxAllowed)
				return (false, L["Rounding remainder {0} exceeds the guard ({1}) — rejected as a real imbalance, not rounding.", roundingDiff, maxAllowed]);

			// eligible = P&L (AccountType Revenue=4 / Expenses=5) minus the explicit externally-reconciled P&L accounts.
			var accIds = entry.Lines.Select(l => l.AccountId).Distinct().ToList();
			var eligibleSet = (await _context.Accounts.AsNoTracking()
				.Where(a => accIds.Contains(a.ID) && (a.AccountTypeId == 4 || a.AccountTypeId == 5) && !ForbiddenDiffAccounts.Contains(a.Code))
				.Select(a => a.ID).ToListAsync()).ToHashSet();
			var target = entry.Lines.Where(l => eligibleSet.Contains(l.AccountId))
				.OrderByDescending(l => Math.Abs(l.Debit - l.Credit)).ThenBy(l => l.AccountId).ThenBy(l => l.LineNo)
				.FirstOrDefault();
			if (target == null)
				return (false, L["No eligible P&L line to bear the {0} rounding remainder — rejected.", roundingDiff]);

			// deterministic direction: a debit line reduces its debit by the remainder; a credit line increases its credit by it.
			if (target.Debit > 0) target.Debit -= roundingDiff; else target.Credit += roundingDiff;
			if (target.Debit < 0 || target.Credit < 0)
				return (false, L["Loading the {0} rounding remainder would drive a line negative — rejected.", roundingDiff]);

			System.Threading.Interlocked.Increment(ref RoundingDiffLoads);
			_logger?.LogInformation("HM-2 rounding remainder {Diff} loaded on account {Acct} (line {Line}) of JE for company {Co}", roundingDiff, target.AccountId, target.LineNo, entry.CompanyID);
			return (true, null);
		}

		public async Task<(bool ok, string? error)> PostAsync(int entryId, int? userId)
		{
			await using var tx = await ScopedTx.BeginOrJoinAsync(_context);
			var entry = await _context.JournalEntries.Include(e => e.Lines).FirstOrDefaultAsync(e => e.ID == entryId);
			if (entry == null) return (false, "Journal entry not found");
			if (entry.Status != "Draft" && entry.Status != "Submitted") return (false, "An entry that is already posted or cancelled cannot be posted");

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
			if (lines.Count < 2) return (false, "The entry must contain at least two lines");

			foreach (var l in lines)
			{
				if (l.Debit < 0 || l.Credit < 0) return (false, "Negative values are not allowed");
				if ((l.Debit > 0) == (l.Credit > 0)) return (false, "Every line must be either a debit or a credit (not both, and not neither)");
			}

			// HM-2: lines are already rounded to functional dp AND balanced by ApplyCurrencyRoundingAsync (at draft build),
			// so this is the pure sum of rounded values — zero tolerance stays absolute.
			var totalD = lines.Sum(l => l.Debit);
			var totalC = lines.Sum(l => l.Credit);
			if (totalD != totalC) return (false, $"The entry is out of balance: debit {totalD} ≠ credit {totalC}");
			if (totalD <= 0) return (false, "The entry total must be greater than zero");

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
				if (!byId.TryGetValue(id, out var a)) return (false, $"Account {id} does not exist in this company");
				if (!a.IsActive) return (false, $"Account {a.Code} is not active");
				if (!a.IsPostable && !isSystem) return (false, $"Account {a.Code} ({a.Name}) is a group/control account — it cannot be posted to directly");
			}
			// cost-center requirement (cost centers arrive in Phase 2; enforced here for correctness)
			foreach (var l in lines)
				if (byId[l.AccountId].RequireCostCenter && l.CostCenterId == null)
					return (false, $"Account {byId[l.AccountId].Code} requires a cost centre");

			// fiscal period must exist and not be Closed
			var period = await _periods.ResolveAsync(entry.CompanyID, entry.EntryDate);
			if (period == null) return (false, "No fiscal period covers the entry date");
			// BlocksPosting, not a literal: SoftClosed was accepted by FiscalPeriodService.SetStatusAsync and
			// honoured by NOTHING, so the middle state existed in name only and a soft-closed period still took
			// postings. One predicate now answers for both closed states, so a future caller cannot test for one
			// and miss the other.
			if (AccountingPeriodStatuses.BlocksPosting(period.Status))
				return (false, "The fiscal period is closed — posting into it is not allowed");
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
				return (false, "Could not reserve the journal entry number (numbering sequence or database connection) — the entry was not posted, please try again");
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
			if (entry == null) return (false, "Journal entry not found", null);
			if (entry.Status != "Posted") return (false, "An entry that is not posted cannot be reversed", null);

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
			if (!ok) { await tx.RollbackAsync(); return (false, $"Could not post the reversing entry: {err}", null); }

			entry.Status = "Reversed";
			entry.ReversedByEntryId = reversal.ID;
			entry.ModifiedBy = userId;
			entry.ModifiedAt = DateTime.UtcNow;
			await _context.SaveChangesAsync();

			// Platform Kernel (ADR-001): reversal is the most audit-relevant operation in the system and until
			// Stage 0 it left no durable trace beyond the two entry rows. Recorded INSIDE this transaction and
			// BEFORE the commit, so the event shares the fate of the mirror entry AND the original's status flip.
			// No try/catch: if the event cannot be written, the reversal must not stand.
			//
			// Visibility = Confidential (not Internal): the payload carries the entry TOTAL, a monetary fact, so
			// it sits behind the accounting "post" right via AccountingPermissionAdapter (ViewConfidential -> post).
			// This is the first non-Internal event the platform produces.
			//
			// DedupKey: a posted entry can only be reversed once (the Status guard above blocks a repeat), so the
			// key is pinned and a retried command cannot double-record it.
			await _events.RecordAsync(new CrossBuy.Models.Platform.BusinessEventRecord
			{
				EntityCode = CrossBuy.BL.Platform.EntityRegistry.JournalEntry,
				EntityId = entry.ID,
				EventType = CrossBuy.BL.Platform.JournalEntryEvents.Reversed,
				PayloadVersion = CrossBuy.Models.Platform.JournalEntryEventPayload.Version,
				Visibility = CrossBuy.Models.Platform.BusinessEventVisibility.Confidential,
				DedupKey = $"JournalEntry.Reversed:{entry.ID}",
				CompanyIdOverride = entry.CompanyID,
				Payload = new CrossBuy.Models.Platform.JournalEntryEventPayload
				{
					OriginalJournalEntryId = entry.ID,
					OriginalJournalNumber = entry.EntryNo,
					ReversingJournalEntryId = reversal.ID,
					ReversingJournalNumber = reversal.EntryNo,
					OriginalSourceType = entry.SourceType,
					OriginalSourceId = entry.SourceId,
					ReversalReason = string.IsNullOrWhiteSpace(reason) ? null : reason,
					OriginalAmount = entry.Lines.Sum(l => l.Debit),
					ReversedAt = entry.ModifiedAt,
				},
			});

			await tx.CommitAsync();
			return (true, null, reversal.ID);
		}
	}
}
