using CrossBuy.BL.Platform;
using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Accounting;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL
{
    // ================================================================================================
    // ACCOUNTING PERIOD CONTROL — close, soft-close and reopen.
    //
    // WHAT ALREADY EXISTED, and this service deliberately does NOT rebuild it:
    //
    //   The posting guard. JournalEntryService.PostAsync already resolves the period for the entry's
    //   company and date and refuses a Closed one, and JournalEntryService is the ONLY writer of
    //   JournalEntries in the repository - twenty-one services post through it. So the single canonical
    //   guard this batch was asked for is already in place and already universal. What this service adds
    //   is the CONTROL AROUND it: who may move a period, on what evidence, and with what history.
    //
    // WHAT WAS BROKEN, and it is why this file exists:
    //
    //   FiscalPeriodService.SetStatusAsync(int periodId, string status) takes NO company and NO actor.
    //   It loads the period by id alone, so a chief accountant in company 1 could close company 2's
    //   period, and nothing recorded who did it, when, or why. The one caller
    //   (AccountingController.SetPeriodStatus) is gated on AccPerm("manage") - which is the
    //   ROLE-MANAGEMENT right - so closing a period required the power to grant accounting roles.
    //
    //   SoftClosed was accepted by that setter and honoured by nothing: the guard tested only for
    //   "Closed". The middle state existed in name and had no effect.
    // ================================================================================================
    // How much a finding matters. Three levels, because two is not enough to be useful: a period with
    // an out-of-balance journal must not close at all, while a period with a large late accrual is
    // something a controller wants to SEE and may legitimately close over.
    public static class PeriodIssueSeverity
    {
        public const string Blocking = "Blocking";            // close refused, never overridable
        public const string Warning = "Warning";              // close refused unless explicitly overridden, and the override is recorded
        public const string Informational = "Informational";  // shown, never refuses

        public static readonly IReadOnlyList<string> All = new[] { Blocking, Warning, Informational };
    }

    // A financial-control exception, in the shape a screen and a report can both consume.
    //
    // MessageKey is a LOCALIZATION KEY, not prose. The English and Arabic text travel beside it for a
    // caller that has no localizer, but the key is the authority - a report that renders these must not
    // be pinned to one language by the service that produced them.
    public sealed record PeriodExceptionRow(
        string Code,
        string Severity,
        string MessageKey,
        string MessageAr,
        string MessageEn,
        int Count,
        string? EntityType = null,
        int? EntityId = null,
        DateTime? AccountingDate = null,
        decimal? Amount = null,
        // Where a finance user goes to deal with it. The MODULE names its own route; this service
        // never invents one.
        string? NavigateController = null,
        string? NavigateAction = null);

    public sealed record PeriodReadiness(
        int FiscalPeriodId,
        bool Ready,
        IReadOnlyList<PeriodExceptionRow> Issues)
    {
        public IEnumerable<PeriodExceptionRow> Blocking =>
            Issues.Where(i => i.Severity == PeriodIssueSeverity.Blocking);
        public IEnumerable<PeriodExceptionRow> Warnings =>
            Issues.Where(i => i.Severity == PeriodIssueSeverity.Warning);
        public IEnumerable<PeriodExceptionRow> Informational =>
            Issues.Where(i => i.Severity == PeriodIssueSeverity.Informational);

        /// Ready means nothing BLOCKS. Warnings do not make a period ready - they make it closable only
        /// by someone who says so explicitly, which is what the override flag on CloseAsync is for.
        public bool HasBlocking => Blocking.Any();
        public bool HasWarnings => Warnings.Any();

        public static PeriodReadiness Blocked(int periodId, params PeriodExceptionRow[] issues)
            => new(periodId, false, issues);
    }

    public sealed record PeriodControlRow(
        int FiscalPeriodId, int PeriodNo, string YearName, DateTime StartDate, DateTime EndDate,
        string Status, int? ClosedBy, DateTime? ClosedAt, int? ReopenedBy, DateTime? ReopenedAt, string? ReopenReason);

    public interface IAccountingPeriodControlService
    {
        Task<IReadOnlyList<PeriodControlRow>> ListAsync(BusinessContext context, CancellationToken ct = default);

        /// What stands between this period and a clean close. Never fabricated: every issue below is
        /// derived from authoritative rows, and a check the model cannot support is simply absent.
        Task<PeriodReadiness> ReadinessAsync(BusinessContext context, int fiscalPeriodId, CancellationToken ct = default);

        Task<(bool ok, string? error)> SoftCloseAsync(BusinessContext context, int fiscalPeriodId, CancellationToken ct = default);

        /// <param name="overrideWarnings">
        /// Closes over WARNINGS only, never over blocking findings, and the override is written into the
        /// period history with the actor who chose it. Silently ignorable warnings would make the whole
        /// severity model decorative.
        /// </param>
        Task<(bool ok, string? error)> CloseAsync(BusinessContext context, int fiscalPeriodId, bool overrideWarnings = false, CancellationToken ct = default);
        Task<(bool ok, string? error)> ReopenAsync(BusinessContext context, int fiscalPeriodId, string reason, CancellationToken ct = default);

        Task<IReadOnlyList<AccountingPeriodAudit>> HistoryAsync(BusinessContext context, int fiscalPeriodId, CancellationToken ct = default);

        /// THE CLOSED-PERIOD REVERSAL CONTRACT, for AR receipt reversal and Project Billing reversal to
        /// share rather than each deciding for itself.
        ///
        /// Answers one question: given an original entry dated <paramref name="originalDate"/>, on what
        /// date may its COMPENSATING entry be posted? It never mutates the original period and never
        /// rewrites a historical date.
        Task<(bool ok, DateTime postingDate, string? error)> ResolveCompensatingPostingDateAsync(
            BusinessContext context, DateTime originalDate, CancellationToken ct = default);
    }

    public sealed class AccountingPeriodControlService : IAccountingPeriodControlService
    {
        // One refusal for every reason a caller may not act on a period: no such period, another
        // company's period, or no authority. They must be indistinguishable, or a period id becomes a
        // probe for what exists in other companies.
        public const string Refused = "لا تملك صلاحية تنفيذ هذا الإجراء على هذه الفترة";
        public const string ReasonRequired = "سبب إعادة الفتح مطلوب";

        /// The width deploy/sql/accounting_period_control.sql declares for both reason columns. Stated
        /// here so the code that WRITES them knows the limit, rather than discovering it as a
        /// truncation error while somebody is closing a month.
        public const int ReasonMaxLength = 500;
        public const string ReasonTooLong = "سبب إعادة الفتح طويل جداً";

        private readonly CrossDbContext _db;
        private readonly AccountingAccessService _accounting;
        // The platform's EXISTING reconciliation engine - AR/AP subledger agreement, trial balance,
        // stock-to-GL, GRNI and twenty-six others. Readiness CONSUMES it rather than re-deriving any of
        // it here: a second definition of "the AR control reconciles" is how two answers appear.
        private readonly IIntegrityCheckService _integrity;

        public AccountingPeriodControlService(
            CrossDbContext db, AccountingAccessService accounting, IIntegrityCheckService integrity)
        {
            _db = db;
            _accounting = accounting;
            _integrity = integrity;
        }

        // FiscalPeriod carries no CompanyID of its own - the company comes from its FiscalYear. Every
        // read and write in this service goes through this one join, so there is a single definition of
        // "this company's period" rather than one per method.
        private IQueryable<FiscalPeriod> CompanyPeriods(int companyId) =>
            from p in _db.FiscalPeriods
            join y in _db.FiscalYears on p.FiscalYearId equals y.ID
            where y.CompanyID == companyId
            select p;

        public async Task<IReadOnlyList<PeriodControlRow>> ListAsync(
            BusinessContext context, CancellationToken ct = default)
        {
            if (!Valid(context)) return Array.Empty<PeriodControlRow>();
            if (!await _accounting.CanAsync(context, "read", null, ct)) return Array.Empty<PeriodControlRow>();

            return await (from p in _db.FiscalPeriods.AsNoTracking()
                          join y in _db.FiscalYears.AsNoTracking() on p.FiscalYearId equals y.ID
                          where y.CompanyID == context.CompanyId
                          orderby y.Name, p.PeriodNo
                          select new PeriodControlRow(
                              p.ID, p.PeriodNo, y.Name, p.StartDate, p.EndDate, p.Status,
                              p.ClosedBy, p.ClosedAt, p.ReopenedBy, p.ReopenedAt, p.ReopenReason))
                         .ToListAsync(ct);
        }

        // ============================================================================================
        // READINESS
        //
        // Every check below reads authoritative rows and nothing else. There is no bank-statement
        // import in this repository, so there is no reconciliation check here - inventing one would
        // produce a green light that means nothing.
        // ============================================================================================
        public async Task<PeriodReadiness> ReadinessAsync(
            BusinessContext context, int fiscalPeriodId, CancellationToken ct = default)
        {
            if (!Valid(context)) return PeriodReadiness.Blocked(fiscalPeriodId);
            var period = await CompanyPeriods(context.CompanyId).AsNoTracking()
                .FirstOrDefaultAsync(p => p.ID == fiscalPeriodId, ct);
            if (period == null) return PeriodReadiness.Blocked(fiscalPeriodId);

            var issues = new List<PeriodExceptionRow>();

            // ---- 1. BLOCKING: posted journals that do not balance -------------------------------
            //
            // Posting cannot normally produce one, so a non-zero count is a genuine integrity finding
            // rather than housekeeping - and closing over it freezes the imbalance permanently. Never
            // overridable, which is why it is Blocking rather than a loud Warning.
            var unbalanced = await (
                from e in _db.JournalEntries.AsNoTracking()
                where e.CompanyID == context.CompanyId && e.Status == "Posted"
                   && e.EntryDate >= period.StartDate && e.EntryDate <= period.EndDate
                let d = e.Lines.Sum(l => (decimal?)l.Debit) ?? 0m
                let c = e.Lines.Sum(l => (decimal?)l.Credit) ?? 0m
                where d != c
                select e.ID).CountAsync(ct);
            if (unbalanced > 0)
                issues.Add(new PeriodExceptionRow(
                    "out-of-balance", PeriodIssueSeverity.Blocking,
                    "PeriodClose.Exception.OutOfBalance",
                    "قيود مرحّلة غير متوازنة", "Posted journals that do not balance",
                    unbalanced, "JournalEntry", null, period.EndDate, null,
                    "Accounting", "JournalEntries"));

            // ---- 2. BLOCKING: posted journals with no fiscal period ------------------------------
            //
            // They fall outside every period-based control, including this one. Closing the period they
            // belong to would not govern them, so the close would be telling the truth about nothing.
            var orphaned = await _db.JournalEntries.AsNoTracking().CountAsync(
                e => e.CompanyID == context.CompanyId && e.Status == "Posted"
                  && e.EntryDate >= period.StartDate && e.EntryDate <= period.EndDate
                  && (e.FiscalPeriodId == null || e.FiscalPeriodId == 0), ct);
            if (orphaned > 0)
                issues.Add(new PeriodExceptionRow(
                    "no-period", PeriodIssueSeverity.Blocking,
                    "PeriodClose.Exception.NoPeriod",
                    "قيود مرحّلة بلا فترة مالية", "Posted journals with no fiscal period",
                    orphaned, "JournalEntry", null, period.EndDate, null,
                    "Accounting", "JournalEntries"));

            // ---- 3. WARNING: drafts inside the period -------------------------------------------
            //
            // A journal someone still intends to post. Closing STRANDS it - it can never be posted into
            // this period again - but that is a business judgement a controller is entitled to make at
            // month end, so it warns and can be overridden explicitly rather than blocking outright.
            var drafts = await _db.JournalEntries.AsNoTracking().CountAsync(
                e => e.CompanyID == context.CompanyId && e.Status == "Draft"
                  && e.EntryDate >= period.StartDate && e.EntryDate <= period.EndDate, ct);
            if (drafts > 0)
                issues.Add(new PeriodExceptionRow(
                    "unposted-journals", PeriodIssueSeverity.Warning,
                    "PeriodClose.Exception.UnpostedJournals",
                    "قيود غير مرحّلة داخل الفترة", "Unposted journals inside the period",
                    drafts, "JournalEntry", null, period.EndDate, null,
                    "Accounting", "JournalEntries"));

            // ---- 4. WARNING: the platform's own reconciliation findings --------------------------
            //
            // IntegrityCheckService is COMPANY-WIDE and not period-scoped, so a failure cannot be
            // attributed to this period and must not block its close. It is surfaced as a warning
            // because a finance user closing a month should see that the AR subledger disagrees with its
            // control account - and should have to say so out loud before closing anyway.
            var failing = (await _integrity.RunAsync(context.CompanyId)).Where(c => !c.Ok).ToList();
            foreach (var f in failing)
                issues.Add(new PeriodExceptionRow(
                    "integrity:" + f.Key, PeriodIssueSeverity.Warning,
                    "PeriodClose.Exception.Integrity",
                    f.NameAr, f.NameEn,
                    1, "IntegrityCheck", null, period.EndDate, f.Diff,
                    "Accounting", "Integrity"));

            // Ready means nothing BLOCKS. Warnings leave it not-ready but overridable - see CloseAsync.
            return new PeriodReadiness(
                fiscalPeriodId,
                Ready: !issues.Any(i => i.Severity == PeriodIssueSeverity.Blocking)
                       && !issues.Any(i => i.Severity == PeriodIssueSeverity.Warning),
                issues);
        }

        // ============================================================================================
        // TRANSITIONS
        // ============================================================================================

        public Task<(bool ok, string? error)> SoftCloseAsync(BusinessContext context, int fiscalPeriodId, CancellationToken ct = default)
            => MoveAsync(context, fiscalPeriodId, AccountingPeriodStatuses.SoftClosed, AccountingActions.PeriodClose, null, ct);

        public Task<(bool ok, string? error)> CloseAsync(BusinessContext context, int fiscalPeriodId, bool overrideWarnings = false, CancellationToken ct = default)
            => MoveAsync(context, fiscalPeriodId, AccountingPeriodStatuses.Closed, AccountingActions.PeriodClose, null, ct, overrideWarnings);

        public Task<(bool ok, string? error)> ReopenAsync(BusinessContext context, int fiscalPeriodId, string reason, CancellationToken ct = default)
            => MoveAsync(context, fiscalPeriodId, AccountingPeriodStatuses.Open, AccountingActions.PeriodReopen, reason, ct);

        // ONE transition path, so the company check, the authority check, the readiness gate, the
        // evidence and the history cannot differ between close and reopen.
        private async Task<(bool ok, string? error)> MoveAsync(
            BusinessContext context, int fiscalPeriodId, string target, string action, string? reason,
            CancellationToken ct, bool overrideWarnings = false)
        {
            if (!Valid(context)) return (false, Refused);

            // Reopen is the stronger act and it must explain itself. Checked BEFORE authority so a
            // caller who is entitled still cannot reopen silently.
            bool reopening = target == AccountingPeriodStatuses.Open;
            if (reopening && string.IsNullOrWhiteSpace(reason)) return (false, ReasonRequired);

            // REFUSED, not truncated. This is the field that records why a sealed month was
            // re-opened; storing the first 500 characters of a longer sentence would put words in the
            // actor's mouth in the one place that must not be paraphrased.
            if (reason != null && reason.Trim().Length > ReasonMaxLength) return (false, ReasonTooLong);

            if (!await _accounting.CanAsync(context, action, null, ct)) return (false, Refused);

            await using var tx = await ScopedTx.BeginOrJoinAsync(_db);

            // Locked for the life of the transaction: two concurrent closes must not both read Open and
            // both write history. SQLite (tests) has a single writer, where the race cannot arise.
            if (_db.Database.IsSqlServer())
            {
                var ids = await CompanyPeriods(context.CompanyId).Select(p => p.ID).ToListAsync(ct);
                if (!ids.Contains(fiscalPeriodId)) return (false, Refused);
                _ = (await _db.FiscalPeriods
                        .FromSqlInterpolated($"SELECT * FROM FiscalPeriods WITH (UPDLOCK) WHERE ID = {fiscalPeriodId}")
                        .AsTracking().ToListAsync(ct)).FirstOrDefault();
            }

            // The company comes from the period's own fiscal year, never from the request. A period in
            // another company is refused exactly like one that does not exist.
            var period = await CompanyPeriods(context.CompanyId)
                .FirstOrDefaultAsync(p => p.ID == fiscalPeriodId, ct);
            if (period == null) return (false, Refused);

            var from = period.Status;
            if (from == target) return (false, "الفترة في هذه الحالة بالفعل");

            // Closing over known problems is the failure this control exists to prevent.
            string? overrideNote = null;
            if (!reopening)
            {
                var readiness = await ReadinessAsync(context, fiscalPeriodId, ct);

                // BLOCKING is never overridable. An out-of-balance posted journal frozen into a closed
                // period is not a judgement call.
                if (readiness.HasBlocking)
                    return (false, "لا يمكن الإقفال: توجد استثناءات مالية مانعة");

                // Warnings refuse the close UNLESS the caller says so, and saying so is recorded.
                if (readiness.HasWarnings)
                {
                    if (!overrideWarnings)
                        return (false, "لا يمكن الإقفال: توجد تحذيرات - يلزم تجاوز صريح");
                    // SUMMARISED to fit the column, and it says so. Unlike the reopen reason this is a
                    // list this code generated, so shortening it loses no testimony - but silently
                    // dropping codes would, which is why the count of the dropped ones travels.
                    overrideNote = FitWarnings(readiness.Warnings.Select(w => w.Code));
                }
            }

            period.Status = target;
            var now = DateTime.UtcNow;
            if (reopening)
            {
                period.ReopenedBy = context.EmployeeId!.Value;
                period.ReopenedAt = now;
                period.ReopenReason = reason!.Trim();
            }
            else
            {
                period.ClosedBy = context.EmployeeId!.Value;
                period.ClosedAt = now;
            }

            // History is rows, not the latest values: a period that was closed, reopened, corrected and
            // closed again has a story, and this control is only worth having if that story survives.
            _db.AccountingPeriodAudits.Add(new AccountingPeriodAudit
            {
                CompanyID = context.CompanyId,
                FiscalPeriodId = period.ID,
                FromStatus = from,
                ToStatus = target,
                ActorEmployeeId = context.EmployeeId!.Value,
                OccurredAt = now,
                // Either the reopen reason or the warnings that were overridden - the history row always
                // carries WHY this transition was allowed when something stood in its way.
                Reason = !string.IsNullOrWhiteSpace(reason) ? reason.Trim() : overrideNote,
            });

            await _db.SaveChangesAsync(ct);
            await tx.CommitAsync();
            return (true, null);
        }

        public async Task<IReadOnlyList<AccountingPeriodAudit>> HistoryAsync(
            BusinessContext context, int fiscalPeriodId, CancellationToken ct = default)
        {
            if (!Valid(context)) return Array.Empty<AccountingPeriodAudit>();
            if (!await _accounting.CanAsync(context, "read", null, ct)) return Array.Empty<AccountingPeriodAudit>();

            return await _db.AccountingPeriodAudits.AsNoTracking()
                .Where(a => a.CompanyID == context.CompanyId && a.FiscalPeriodId == fiscalPeriodId)
                .OrderByDescending(a => a.OccurredAt).ThenByDescending(a => a.ID)
                .ToListAsync(ct);
        }

        // ============================================================================================
        // CLOSED-PERIOD REVERSAL
        //
        // THE CANONICAL RULE, and it is the one the platform ALREADY follows rather than a new invention:
        // JournalEntryService.ReverseAsync dates its mirror entry DateTime.UtcNow.Date and posts it
        // through the ordinary guard. So a reversal of something in a closed period succeeds - because
        // the compensating entry lands in TODAY's period, not the original's.
        //
        // That is the correct accounting treatment. An error found after a close is corrected by an entry
        // in the current open period; you do not reopen a sealed month to edit history. The original
        // evidence stays exactly where it was posted.
        //
        // What this method adds is the REFUSAL the platform did not state: if TODAY's period is itself
        // closed or missing, there is nowhere legitimate to put the compensation, and the caller must be
        // told that BEFORE it starts writing financial rows rather than discovering it at the GL.
        public async Task<(bool ok, DateTime postingDate, string? error)> ResolveCompensatingPostingDateAsync(
            BusinessContext context, DateTime originalDate, CancellationToken ct = default)
        {
            var today = DateTime.UtcNow.Date;
            if (!Valid(context)) return (false, today, Refused);

            var target = await CompanyPeriods(context.CompanyId).AsNoTracking()
                .Where(p => p.StartDate <= today && p.EndDate >= today)
                .OrderBy(p => p.PeriodNo)
                .FirstOrDefaultAsync(ct);

            if (target == null)
                return (false, today, "لا توجد فترة مالية مفتوحة لتاريخ اليوم - تعذّر ترحيل قيد التسوية");
            if (AccountingPeriodStatuses.BlocksPosting(target.Status))
                return (false, today, "الفترة المالية الحالية مقفولة - تعذّر ترحيل قيد التسوية");

            // The ORIGINAL period's state is deliberately not consulted. Refusing to compensate because
            // the original month is closed would be exactly backwards: a closed month is the normal case
            // for a correction, and refusing would leave the error uncorrectable.
            return (true, today, null);
        }

        /// "تجاوز تحذيرات: a, b, c" - as many codes as fit, then how many were left out.
        public static string FitWarnings(IEnumerable<string> codes)
        {
            const string prefix = "تجاوز تحذيرات: ";
            var all = codes.ToList();
            var kept = new List<string>();
            int used = prefix.Length;

            foreach (var code in all)
            {
                // 20 characters held back for the "(+N أخرى)" tail, so adding the tail can never be
                // what pushes the string over the limit.
                int cost = code.Length + (kept.Count == 0 ? 0 : 2);
                if (used + cost > ReasonMaxLength - 20) break;
                kept.Add(code);
                used += cost;
            }

            var text = prefix + string.Join(", ", kept);
            int dropped = all.Count - kept.Count;
            if (dropped > 0) text += $" (+{dropped} أخرى)";
            return text.Length > ReasonMaxLength ? text[..ReasonMaxLength] : text;
        }

        private static bool Valid(BusinessContext? c)
            => c != null && c.CompanyId > 0 && c.EmployeeId is > 0;
    }
}
