using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Reporting;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL.Reporting
{
    // ============================================================================================
    // Reporting Platform (ADR-037) — REPORT HISTORY.
    //
    // An APPEND-ONLY record of every generation attempt: who, what, which parameters, which template version,
    // how many rows, how long, and what the outcome was.
    //
    // Two design rules that make it trustworthy rather than decorative:
    //
    //   1. A DENIAL IS RECORDED. A refused report is precisely what an auditor asks about, and a history that
    //      only contained successes would be a success log wearing an audit log's name.
    //   2. NOTHING IS UPDATED AFTER COMPLETION and nothing is deleted — there is no DeletedAt on ReportRun. The
    //      bytes are what expire (via the archive's retention), not the record that a report was produced. Same
    //      instinct as our ledger rule: corrections are new facts, never edits to old ones.
    //
    // It is also NOT a business event. CLAUDE.md records the standing decision not to raise platform business
    // events from a parallel track until the kernel's event work stabilises in git, and a report run is not a
    // financial transition in any case. If reporting is ever onboarded to the event platform, this is the
    // producer that would call RecordAsync — one place, not fifty call sites.
    // ============================================================================================

    public sealed class ReportRunRecord
    {
        public required string ReportCode { get; init; }
        public int? TemplateId { get; init; }
        public int? TemplateVersionNo { get; init; }
        public ReportRunKind Kind { get; init; }
        public ReportRunStatus Status { get; init; }
        public ReportOutputFormat Format { get; init; }
        public string? ParametersJson { get; init; }
        public string? ParametersHash { get; init; }
        public int RowCount { get; init; }
        public int DurationMs { get; init; }
        public long? ArchiveEntryId { get; init; }
        public string? ErrorCode { get; init; }
        public string? ErrorMessage { get; init; }
        public DateTime StartedAt { get; init; }
        public Guid? CorrelationId { get; init; }
    }

    public sealed class ReportHistoryQuery
    {
        public string? ReportCode { get; init; }
        public int? EmployeeId { get; init; }
        public ReportRunStatus? Status { get; init; }
        public ReportRunKind? Kind { get; init; }
        public DateTime? From { get; init; }
        public DateTime? To { get; init; }

        // Capped at 500 by the service. A history screen that could ask for everything would be a way to pull the
        // whole table into memory.
        public int Take { get; init; } = 100;
        public int Skip { get; init; }
    }

    public sealed class ReportHistoryRow
    {
        public required long Id { get; init; }
        public required string ReportCode { get; init; }
        public string? ReportTitleAr { get; init; }
        public string? ReportTitleEn { get; init; }
        public int? TemplateId { get; init; }
        public int? TemplateVersionNo { get; init; }
        public int? EmployeeId { get; init; }
        public string? EmployeeName { get; init; }
        public ReportRunKind Kind { get; init; }
        public ReportRunStatus Status { get; init; }
        public required string Format { get; init; }
        public int RowCount { get; init; }
        public int DurationMs { get; init; }
        public long? ArchiveEntryId { get; init; }
        public string? ErrorCode { get; init; }
        public DateTime StartedAt { get; init; }
        public DateTime? CompletedAt { get; init; }
    }

    public interface IReportHistoryService
    {
        // Writes one history line. Returns its id so the engine can put it on the run summary.
        //
        // NEVER THROWS: a history-write failure must not turn a successfully produced report into an error for
        // the user. It swallows and returns 0. That is a considered trade — see the comment on the catch.
        Task<long> RecordAsync(ReportRunRecord record, BusinessContext context,
            CancellationToken cancellationToken = default);

        Task<IReadOnlyList<ReportHistoryRow>> QueryAsync(ReportHistoryQuery query, BusinessContext context,
            CancellationToken cancellationToken = default);

        // How many rows QueryAsync would return if it were not paged. Same filter, same visibility rules -
        // a screen that pages this list needs a page count, and a count computed any other way would
        // eventually advertise a page that does not exist.
        Task<int> CountAsync(ReportHistoryQuery query, BusinessContext context,
            CancellationToken cancellationToken = default);

        Task<ReportHistoryRow?> GetAsync(long runId, BusinessContext context,
            CancellationToken cancellationToken = default);

        // The parameters a past run used, so a user can re-run "the same report as last month".
        Task<IReadOnlyDictionary<string, string?>?> GetParametersAsync(long runId, BusinessContext context,
            CancellationToken cancellationToken = default);
    }

    public class ReportHistoryService : IReportHistoryService
    {
        private const int MaxTake = 500;

        private readonly CrossDbContext _db;
        private readonly IReportCatalog _catalog;
        private readonly IReportAuthorizationService _authorization;
        private readonly IReportClock _clock;
        private readonly ILogger<ReportHistoryService> _logger;

        public ReportHistoryService(CrossDbContext db, IReportCatalog catalog,
            IReportAuthorizationService authorization, IReportClock clock, ILogger<ReportHistoryService> logger)
        {
            _db = db;
            _catalog = catalog;
            _authorization = authorization;
            _clock = clock;
            _logger = logger;
        }

        public async Task<long> RecordAsync(ReportRunRecord record, BusinessContext context,
            CancellationToken cancellationToken = default)
        {
            if (context.CompanyId <= 0) return 0;

            var row = new ReportRun
            {
                CompanyID = context.CompanyId,
                ReportCode = record.ReportCode,
                TemplateId = record.TemplateId,
                TemplateVersionNo = record.TemplateVersionNo,
                EmployeeId = context.EmployeeId,
                Kind = record.Kind,
                Status = record.Status,
                Format = record.Format.ToString(),
                ParametersJson = record.ParametersJson,
                ParametersHash = record.ParametersHash,
                RowCount = record.RowCount,
                DurationMs = record.DurationMs,
                ArchiveEntryId = record.ArchiveEntryId,
                ErrorCode = record.ErrorCode,

                // Truncated: an exception message can be arbitrarily long and this column is NVARCHAR(1000). A
                // history insert must never fail because a stack trace was verbose.
                ErrorMessage = Trim(record.ErrorMessage, 1000),
                StartedAt = record.StartedAt,
                CompletedAt = _clock.LocalNow,
                CorrelationId = record.CorrelationId,
                CreatedBy = context.EmployeeId,
                CreatedAt = _clock.LocalNow,
            };

            try
            {
                _db.ReportRuns.Add(row);
                await _db.SaveChangesAsync(cancellationToken);
                return row.Id;
            }
            catch (Exception ex)
            {
                // DELIBERATE SWALLOW, and the only one in this platform.
                //
                // The alternative — letting it throw — means an unapplied deploy/sql, a full disk or a lock
                // timeout turns every working report into a 500. History is an observability record, not part of
                // the report's correctness, so the report wins and the failure is logged loudly.
                //
                // This is the OPPOSITE of the kernel's RecordAsync rule (in-transaction, no swallowing catch), and
                // the difference is the point: a business EVENT is part of the financial transaction it belongs to,
                // while a report run is an observation ABOUT a read-only operation. Reporting writes no financial
                // data and takes part in no financial transaction, so it has nothing to be atomic with.
                _logger.LogError(ex,
                    "Report history could not be written for {ReportCode} (company {CompanyId}). The report " +
                    "itself was unaffected; check that deploy/sql/reporting_platform.sql is applied.",
                    record.ReportCode, context.CompanyId);

                // The tracked entity is detached so a later SaveChanges in the same scope does not retry it and
                // fail again — a failed history row must not poison an unrelated save.
                _db.Entry(row).State = EntityState.Detached;
                return 0;
            }
        }

        public async Task<IReadOnlyList<ReportHistoryRow>> QueryAsync(ReportHistoryQuery query,
            BusinessContext context, CancellationToken cancellationToken = default)
        {
            if (context.CompanyId <= 0) return Array.Empty<ReportHistoryRow>();

            var q = Filter(query, context,
                await _authorization.IsAdministratorAsync(context, cancellationToken));

            var take = Math.Clamp(query.Take, 1, MaxTake);

            var rows = await q
                .OrderByDescending(r => r.Id)
                .Skip(Math.Max(0, query.Skip))
                .Take(take)
                .ToListAsync(cancellationToken);

            return await ProjectAsync(rows, cancellationToken);
        }

        public async Task<int> CountAsync(ReportHistoryQuery query, BusinessContext context,
            CancellationToken cancellationToken = default)
        {
            if (context.CompanyId <= 0) return 0;

            return await Filter(query, context,
                    await _authorization.IsAdministratorAsync(context, cancellationToken))
                .CountAsync(cancellationToken);
        }

        // THE ONE FILTER, shared by QueryAsync and CountAsync. Shared rather than repeated because the
        // interesting clause here is a permission rule, not a convenience: duplicate it and the day someone
        // changes who may see whose runs, one of the two copies is missed and the count starts describing
        // rows the query refuses to return.
        private IQueryable<ReportRun> Filter(ReportHistoryQuery query, BusinessContext context, bool isAdmin)
        {
            var q = _db.ReportRuns.AsNoTracking().Where(r => r.CompanyID == context.CompanyId);

            // Without administration you see YOUR OWN runs only. A run row carries the parameters someone used,
            // which can itself be sensitive ("who ran the payroll summary for branch 3?"), so it is not a shared
            // feed by default.
            if (!isAdmin)
                q = q.Where(r => r.EmployeeId == context.EmployeeId);
            else if (query.EmployeeId is > 0)
                q = q.Where(r => r.EmployeeId == query.EmployeeId);

            if (!string.IsNullOrWhiteSpace(query.ReportCode)) q = q.Where(r => r.ReportCode == query.ReportCode);
            if (query.Status.HasValue) q = q.Where(r => r.Status == query.Status.Value);
            if (query.Kind.HasValue) q = q.Where(r => r.Kind == query.Kind.Value);
            if (query.From.HasValue) q = q.Where(r => r.StartedAt >= query.From.Value);
            if (query.To.HasValue) q = q.Where(r => r.StartedAt <= query.To.Value);

            return q;
        }

        public async Task<ReportHistoryRow?> GetAsync(long runId, BusinessContext context,
            CancellationToken cancellationToken = default)
        {
            var row = await LoadOwnAsync(runId, context, cancellationToken);
            if (row == null) return null;
            return (await ProjectAsync(new[] { row }, cancellationToken)).FirstOrDefault();
        }

        public async Task<IReadOnlyDictionary<string, string?>?> GetParametersAsync(long runId,
            BusinessContext context, CancellationToken cancellationToken = default)
        {
            var row = await LoadOwnAsync(runId, context, cancellationToken);
            if (row?.ParametersJson == null) return null;

            try
            {
                return System.Text.Json.JsonSerializer
                    .Deserialize<Dictionary<string, string?>>(row.ParametersJson);
            }
            catch (System.Text.Json.JsonException)
            {
                // A malformed stored parameter blob returns null rather than throwing: "I cannot reconstruct that
                // run" is a usable answer; a 500 on a history screen is not.
                return null;
            }
        }

        // The company + ownership filter for a single row, in one place so no read path forgets either half.
        private async Task<ReportRun?> LoadOwnAsync(long runId, BusinessContext context,
            CancellationToken cancellationToken)
        {
            if (context.CompanyId <= 0) return null;

            var row = await _db.ReportRuns.AsNoTracking()
                .FirstOrDefaultAsync(r => r.Id == runId && r.CompanyID == context.CompanyId, cancellationToken);
            if (row == null) return null;

            if (row.EmployeeId == context.EmployeeId) return row;
            return await _authorization.IsAdministratorAsync(context, cancellationToken) ? row : null;
        }

        private async Task<IReadOnlyList<ReportHistoryRow>> ProjectAsync(IReadOnlyList<ReportRun> rows,
            CancellationToken cancellationToken)
        {
            if (rows.Count == 0) return Array.Empty<ReportHistoryRow>();

            var employeeIds = rows.Where(r => r.EmployeeId != null).Select(r => r.EmployeeId!.Value)
                .Distinct().ToList();

            // One round trip for every name on the page, not one per row.
            //
            // BOTH NAMES, resolved by the UI language. This column used to select FullName alone, which put
            // "احمد زكريا" in the By column of an English screen. The English twin is optional on Employee, so
            // an employee who has none still shows the Arabic name - a name is better than a blank.
            bool arabic = System.Globalization.CultureInfo.CurrentUICulture
                .TwoLetterISOLanguageName.Equals("ar", StringComparison.OrdinalIgnoreCase);

            var names = employeeIds.Count == 0
                ? new Dictionary<int, string>()
                : await _db.Employee.AsNoTracking()
                    .Where(e => employeeIds.Contains(e.ID))
                    .Select(e => new { e.ID, e.FullName, e.FullNameEn })
                    .ToDictionaryAsync(e => e.ID,
                        e => arabic || string.IsNullOrWhiteSpace(e.FullNameEn) ? e.FullName : e.FullNameEn!,
                        cancellationToken);

            return rows.Select(r =>
            {
                _catalog.TryGetDefinition(r.ReportCode, out var definition);
                return new ReportHistoryRow
                {
                    Id = r.Id,
                    ReportCode = r.ReportCode,
                    ReportTitleAr = definition?.TitleAr,
                    ReportTitleEn = definition?.TitleEn,
                    TemplateId = r.TemplateId,
                    TemplateVersionNo = r.TemplateVersionNo,
                    EmployeeId = r.EmployeeId,
                    EmployeeName = r.EmployeeId != null ? names.GetValueOrDefault(r.EmployeeId.Value) : null,
                    Kind = r.Kind,
                    Status = r.Status,
                    Format = r.Format,
                    RowCount = r.RowCount,
                    DurationMs = r.DurationMs,
                    ArchiveEntryId = r.ArchiveEntryId,
                    ErrorCode = r.ErrorCode,
                    StartedAt = r.StartedAt,
                    CompletedAt = r.CompletedAt,
                };
            }).ToList();
        }

        private static string? Trim(string? value, int max) =>
            value == null ? null : (value.Length <= max ? value : value[..max]);
    }
}