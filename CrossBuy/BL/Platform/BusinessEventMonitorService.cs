using System.Text.Json;
using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Platform;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CrossBuy.BL.Platform
{
    // Stage 0 Batch B — the Business Event Monitor's only data path.
    //
    // Every rule that protects the operator screen lives here, not in the controller and never in the view:
    //   * company isolation (a caller sees only their company unless they hold the cross-company right);
    //   * payload visibility (Restricted/System payloads are MASKED, not merely un-linked);
    //   * server-side paging (no query ever materialises the whole table);
    //   * retry eligibility (computed server-side and surfaced as flags the view renders);
    //   * retry itself delegates to IEventDispatchStore — this service never writes dispatch state.
    public interface IBusinessEventMonitorService
    {
        Task<BusinessEventMonitorPage> SearchAsync(BusinessEventMonitorFilter filter, BusinessContext context, bool crossCompany, CancellationToken cancellationToken = default);
        Task<BusinessEventDetailsViewModel?> GetDetailsAsync(long eventId, BusinessContext context, bool crossCompany, bool maySeeRestricted, CancellationToken cancellationToken = default);
        Task<DispatchRetryResult> RetryAsync(long dispatchId, BusinessContext context, string reason, bool elevatedOverride, CancellationToken cancellationToken = default);
        IReadOnlyList<string> KnownEntityTypes();
        IReadOnlyList<string> KnownConsumers();
        IReadOnlyList<string> KnownStatuses();
    }

    public class BusinessEventMonitorService : IBusinessEventMonitorService
    {
        // Payload preview cap. A payload is capped at 64 KB on write, but rendering that into a details drawer is
        // still hostile; the operator gets a prefix plus an explicit "truncated" flag.
        public const int PayloadPreviewBytes = 8 * 1024;
        private const int ErrorSummaryLength = 160;

        private readonly CrossDbContext _db;
        private readonly IEntityRegistry _registry;
        private readonly IEventDispatchStore _dispatch;
        private readonly BusinessEventDispatchOptions _options;
        private readonly ICompanyIsolationBypass _bypass;
        private readonly ILogger<BusinessEventMonitorService> _log;

        // Deliberately does NOT depend on IBusinessEventService: the monitor is a read model over the event stream,
        // and recording an event about reading events would feed the queue it exists to observe. The retry action is
        // audited through the logger instead.
        public BusinessEventMonitorService(
            CrossDbContext db, IEntityRegistry registry, IEventDispatchStore dispatch,
            IOptions<BusinessEventDispatchOptions> options,
            ICompanyIsolationBypass bypass,
            ILogger<BusinessEventMonitorService> log)
        { _db = db; _registry = registry; _dispatch = dispatch; _options = options.Value; _bypass = bypass; _log = log; }

        public IReadOnlyList<string> KnownEntityTypes() => _registry.GetDefinitions().Select(d => d.Code).ToList();
        public IReadOnlyList<string> KnownConsumers() => BusinessEventConsumers.Registered;
        public IReadOnlyList<string> KnownStatuses() => new[]
        {
            BusinessEventDispatchStatus.Pending, BusinessEventDispatchStatus.Claimed,
            BusinessEventDispatchStatus.Done, BusinessEventDispatchStatus.Failed,
        };

        public async Task<BusinessEventMonitorPage> SearchAsync(
            BusinessEventMonitorFilter raw, BusinessContext context, bool crossCompany, CancellationToken cancellationToken = default)
        {
            var f = raw.Normalised();

            // Stage 1 Batch B / B3 — the elevated cross-company view is a SHIPPED FEATURE, so it needs the
            // audited bypass. Without it, Batch B's filter on BusinessEvent would silently narrow the elevated
            // view to the operator's own company and the feature would lie about what it is showing.
            //
            // PlatformMonitoring rather than CrossCompanyAdministration: this is read-only observation of the
            // event stream, and it must be revocable on its own without also revoking business administration.
            // The policy requires a platform-admin role, which is the same line PlatformOpsAttribute.IsElevated
            // already draws for this screen — so a caller who reached crossCompany: true will pass, and one who
            // somehow did not will be refused loudly rather than shown a narrowed grid.
            using var monitorScope = crossCompany
                ? _bypass.Begin(CompanyBypassKind.PlatformMonitoring, context,
                    "Business Event Monitor elevated cross-company view.")
                : null;

            // The join is the grid's unit of work: one row per (event, consumer).
            var query = from e in _db.BusinessEvents.AsNoTracking()
                        join d in _db.BusinessEventDispatches.AsNoTracking() on e.EventId equals d.EventId
                        select new { e, d };

            // ---- company isolation. A caller without the cross-company right is pinned to their own company and
            // CANNOT widen it by passing a CompanyId, which is why the filter value is only honoured when allowed.
            if (crossCompany)
            {
                if (f.CompanyId is > 0) query = query.Where(x => x.e.CompanyID == f.CompanyId!.Value);
            }
            else
            {
                query = query.Where(x => x.e.CompanyID == context.CompanyId);
            }

            if (f.BranchId is > 0) query = query.Where(x => x.e.BranchID == f.BranchId!.Value);
            // Entity type is validated, so an unregistered value returns nothing rather than probing the table.
            if (f.EntityType != null)
                query = _registry.IsValid(f.EntityType) ? query.Where(x => x.e.EntityType == f.EntityType) : query.Where(x => false);
            if (f.EntityId is > 0) query = query.Where(x => x.e.EntityId == f.EntityId!.Value);
            if (f.EventType != null) query = query.Where(x => x.e.EventType.Contains(f.EventType));
            if (f.Consumer != null)
                query = BusinessEventConsumers.IsRegistered(f.Consumer) ? query.Where(x => x.d.Consumer == f.Consumer) : query.Where(x => false);
            if (f.DispatchStatus != null)
                query = KnownStatuses().Contains(f.DispatchStatus) ? query.Where(x => x.d.Status == f.DispatchStatus) : query.Where(x => false);
            if (f.DateFrom.HasValue) query = query.Where(x => x.e.CreatedAt >= f.DateFrom!.Value);
            if (f.DateTo.HasValue) query = query.Where(x => x.e.CreatedAt < f.DateTo!.Value.AddDays(1));
            if (f.CorrelationId.HasValue) query = query.Where(x => x.e.CorrelationId == f.CorrelationId!.Value);
            if (f.EventUid.HasValue) query = query.Where(x => x.e.EventUid == f.EventUid!.Value);
            if (f.HasError == true) query = query.Where(x => x.d.Error != null && x.d.Error != "");
            if (f.HasError == false) query = query.Where(x => x.d.Error == null || x.d.Error == "");
            if (f.MinAttempts is > 0) query = query.Where(x => x.d.Attempts >= f.MinAttempts!.Value);

            // ---- summary over the SAME predicate, computed in the database. Never by loading rows.
            var statusCounts = await query
                .GroupBy(x => x.d.Status)
                .Select(g => new { Status = g.Key, Count = g.Count() })
                .ToListAsync(cancellationToken);
            int exhausted = await query.CountAsync(
                x => x.d.Status == BusinessEventDispatchStatus.Failed && x.d.Attempts >= _options.MaxAttempts, cancellationToken);
            int distinctEvents = await query.Select(x => x.e.EventId).Distinct().CountAsync(cancellationToken);
            int Count(string s) => statusCounts.FirstOrDefault(c => c.Status == s)?.Count ?? 0;

            int total = statusCounts.Sum(c => c.Count);

            // ---- the page. Ordering is stable (CreatedAt desc, then two unique keys) so paging cannot repeat or
            // skip a row when many events share a timestamp.
            var page = await query
                .OrderByDescending(x => x.e.CreatedAt)
                .ThenByDescending(x => x.e.EventId)
                .ThenBy(x => x.d.ID)
                .Skip((f.Page - 1) * f.PageSize)
                .Take(f.PageSize)
                .Select(x => new
                {
                    x.e.EventId, x.e.EventUid, x.e.EventType, x.e.EntityType, x.e.EntityId, x.e.CompanyID,
                    x.e.BranchID, x.e.ActorEmployeeId, x.e.CreatedAt, x.e.CompletedAt, x.e.Visibility,
                    DispatchId = x.d.ID, x.d.Consumer, Status = x.d.Status, x.d.Attempts, x.d.UpdatedAt, x.d.Error,
                })
                .ToListAsync(cancellationToken);

            var actorNames = await ResolveActorNamesAsync(page.Select(p => p.ActorEmployeeId), cancellationToken);
            var staleBefore = DateTime.UtcNow.AddMinutes(-_options.StaleClaimMinutes);

            var rows = page.Select(p =>
            {
                var (canRetry, needsOverride, blocked, blockedCode) = RetryEligibility(p.Status, p.Attempts, p.UpdatedAt, staleBefore);
                return new BusinessEventMonitorRow
                {
                    EventId = p.EventId, EventUid = p.EventUid, EventType = p.EventType,
                    EntityType = p.EntityType, EntityId = p.EntityId,
                    EntityUrl = _registry.IsValid(p.EntityType) ? _registry.BuildUrl(p.EntityType, p.EntityId) : null,
                    EntityLabel = _registry.TryGetDefinition(p.EntityType, out var def)
                        ? (System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ar" ? def!.DisplayNameAr : def!.DisplayNameEn)
                        : p.EntityType,
                    CompanyId = p.CompanyID, BranchId = p.BranchID,
                    ActorEmployeeId = p.ActorEmployeeId,
                    ActorName = p.ActorEmployeeId.HasValue ? actorNames.GetValueOrDefault(p.ActorEmployeeId.Value) : null,
                    CreatedAt = p.CreatedAt, CompletedAt = p.CompletedAt, Visibility = p.Visibility,
                    DispatchId = p.DispatchId, Consumer = p.Consumer, DispatchStatus = p.Status,
                    Attempts = p.Attempts, UpdatedAt = p.UpdatedAt,
                    ErrorSummary = Summarise(p.Error),
                    CanRetry = canRetry, RetryNeedsOverride = needsOverride, RetryBlockedReason = blocked, RetryBlockedCode = blockedCode,
                };
            }).ToList();

            return new BusinessEventMonitorPage
            {
                Rows = rows, Total = total, Page = f.Page, PageSize = f.PageSize, MaxAttempts = _options.MaxAttempts,
                Summary = new BusinessEventMonitorSummary
                {
                    Pending = Count(BusinessEventDispatchStatus.Pending),
                    Claimed = Count(BusinessEventDispatchStatus.Claimed),
                    Failed = Count(BusinessEventDispatchStatus.Failed),
                    Done = Count(BusinessEventDispatchStatus.Done),
                    Exhausted = exhausted, TotalEvents = distinctEvents,
                },
            };
        }

        public async Task<BusinessEventDetailsViewModel?> GetDetailsAsync(
            long eventId, BusinessContext context, bool crossCompany, bool maySeeRestricted, CancellationToken cancellationToken = default)
        {
            // Same reasoning as SearchAsync: an elevated details view reads another company's event by id.
            using var detailsScope = crossCompany
                ? _bypass.Begin(CompanyBypassKind.PlatformMonitoring, context,
                    $"Business Event Monitor elevated details view for event {eventId}.")
                : null;

            var ev = await _db.BusinessEvents.AsNoTracking().FirstOrDefaultAsync(e => e.EventId == eventId, cancellationToken);
            if (ev == null) return null;
            // Same company check, same indistinguishable answer as "not found".
            if (!crossCompany && ev.CompanyID != context.CompanyId) return null;

            var dispatches = await _db.BusinessEventDispatches.AsNoTracking()
                .Where(d => d.EventId == eventId).OrderBy(d => d.Consumer).ToListAsync(cancellationToken);

            var actorNames = await ResolveActorNamesAsync(new[] { ev.ActorEmployeeId }, cancellationToken);
            var staleBefore = DateTime.UtcNow.AddMinutes(-_options.StaleClaimMinutes);

            BusinessEventMonitorRow ToRow(BusinessEventDispatch? d)
            {
                var (canRetry, needsOverride, blocked, blockedCode) = d == null
                    ? (false, false, (string?)null, (string?)null)
                    : RetryEligibility(d.Status, d.Attempts, d.UpdatedAt, staleBefore);
                return new BusinessEventMonitorRow
                {
                    EventId = ev.EventId, EventUid = ev.EventUid, EventType = ev.EventType,
                    EntityType = ev.EntityType, EntityId = ev.EntityId,
                    EntityUrl = _registry.IsValid(ev.EntityType) ? _registry.BuildUrl(ev.EntityType, ev.EntityId) : null,
                    EntityLabel = _registry.TryGetDefinition(ev.EntityType, out var def)
                        ? (System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ar" ? def!.DisplayNameAr : def!.DisplayNameEn)
                        : ev.EntityType,
                    CompanyId = ev.CompanyID, BranchId = ev.BranchID,
                    ActorEmployeeId = ev.ActorEmployeeId,
                    ActorName = ev.ActorEmployeeId.HasValue ? actorNames.GetValueOrDefault(ev.ActorEmployeeId.Value) : null,
                    CreatedAt = ev.CreatedAt, CompletedAt = ev.CompletedAt, Visibility = ev.Visibility,
                    DispatchId = d?.ID, Consumer = d?.Consumer, DispatchStatus = d?.Status,
                    Attempts = d?.Attempts ?? 0, UpdatedAt = d?.UpdatedAt,
                    ErrorSummary = Summarise(d?.Error),
                    CanRetry = canRetry, RetryNeedsOverride = needsOverride, RetryBlockedReason = blocked, RetryBlockedCode = blockedCode,
                };
            }

            // ---- payload visibility. Opening the monitor does NOT grant sight of every payload: Restricted and
            // System events are machine/sensitive facts, so their payload is masked unless the caller holds the
            // elevated right. The metadata still shows, so the row remains diagnosable.
            string? payload = null; bool masked = false, truncated = false; string? maskReason = null, maskVisibility = null;
            bool restrictedTier = ev.Visibility is BusinessEventVisibility.Restricted or BusinessEventVisibility.System;
            if (restrictedTier && !maySeeRestricted)
            {
                masked = true;
                maskReason = $"Payload hidden: this event is {ev.Visibility} and requires elevated platform rights.";
                maskVisibility = ev.Visibility;   // the view localises from this, not from the sentence above
            }
            else if (!string.IsNullOrWhiteSpace(ev.Payload))
            {
                var bytes = System.Text.Encoding.UTF8.GetByteCount(ev.Payload);
                var text = ev.Payload;
                if (bytes > PayloadPreviewBytes)
                {
                    text = ev.Payload.Substring(0, Math.Min(ev.Payload.Length, PayloadPreviewBytes));
                    truncated = true;
                }
                payload = Prettify(text, truncated);
            }

            return new BusinessEventDetailsViewModel
            {
                Header = ToRow(dispatches.FirstOrDefault()),
                Consumers = dispatches.Select(ToRow).ToList(),
                Payload = payload, PayloadMasked = masked, PayloadMaskReason = maskReason, PayloadMaskVisibility = maskVisibility,
                PayloadTruncated = truncated,
                PayloadBytes = string.IsNullOrEmpty(ev.Payload) ? 0 : System.Text.Encoding.UTF8.GetByteCount(ev.Payload),
                FullError = dispatches.FirstOrDefault(d => !string.IsNullOrEmpty(d.Error))?.Error,
            };
        }

        // Retry always goes through the store. This service NEVER writes dispatch state, and it records an
        // operational BusinessEvent so the action itself is auditable.
        public async Task<DispatchRetryResult> RetryAsync(
            long dispatchId, BusinessContext context, string reason, bool elevatedOverride, CancellationToken cancellationToken = default)
        {
            var result = await _dispatch.RetryAsync(dispatchId, context, reason, elevatedOverride, cancellationToken);

            // Audit: log unconditionally (success and refusal), because a refused elevated attempt is exactly the
            // thing an auditor wants to see. The reason is operator-supplied text and is recorded verbatim.
            _log.LogInformation(
                "Business Event Monitor retry: dispatch={DispatchId} event={EventId} consumer={Consumer} " +
                "outcome={Outcome} override={Override} actor={ActorEmployeeId} company={CompanyId} reason={Reason}",
                dispatchId, result.EventId, result.Consumer, result.Outcome, elevatedOverride,
                context.EmployeeId, context.CompanyId, reason);

            return result;
        }

        // One place decides whether a row is retryable, so the grid, the details drawer and the store agree.
        //
        // Returns a stable CODE alongside the English prose. The prose is for logs and the diagnostics JSON; the
        // screen renders the code through SharedResources (`evt_block_<code>`), because these strings used to be
        // English sentences echoed straight onto an Arabic-first UI.
        private (bool canRetry, bool needsOverride, string? blocked, string? code) RetryEligibility(
            string status, int attempts, DateTime? updatedAt, DateTime staleBefore)
        {
            switch (status)
            {
                case BusinessEventDispatchStatus.Done:
                    return (false, false, "Completed — replaying could duplicate its side effects.", "Done");
                case BusinessEventDispatchStatus.Pending:
                    return (false, false, "Already queued.", "Pending");
                case BusinessEventDispatchStatus.Claimed when updatedAt != null && updatedAt > staleBefore:
                    return (false, false, "A worker is processing this row.", "HeldByWorker");
            }
            bool exhausted = attempts >= _options.MaxAttempts;
            return exhausted
                ? (true, true, $"All {_options.MaxAttempts} attempts used — needs an elevated override.", "AttemptsExhausted")
                : (true, false, null, null);
        }

        private async Task<Dictionary<int, string>> ResolveActorNamesAsync(IEnumerable<int?> ids, CancellationToken cancellationToken)
        {
            var list = ids.Where(i => i.HasValue).Select(i => i!.Value).Distinct().ToList();
            if (list.Count == 0) return new Dictionary<int, string>();
            bool isArabic = System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ar";
            var rows = await _db.Employee.AsNoTracking().Where(e => list.Contains(e.ID))
                .Select(e => new { e.ID, e.FullName, e.FullNameEn }).ToListAsync(cancellationToken);
            return rows.ToDictionary(r => r.ID,
                r => (isArabic ? (r.FullName ?? r.FullNameEn) : (r.FullNameEn ?? r.FullName)) ?? ("#" + r.ID));
        }

        private static string? Summarise(string? error)
        {
            if (string.IsNullOrWhiteSpace(error)) return null;
            var firstLine = error.Split('\n', '\r').FirstOrDefault(l => !string.IsNullOrWhiteSpace(l))?.Trim() ?? error.Trim();
            return firstLine.Length <= ErrorSummaryLength ? firstLine : firstLine.Substring(0, ErrorSummaryLength) + "…";
        }

        // Pretty-print for display. A truncated payload is no longer valid JSON, so it is shown raw with a marker
        // rather than silently failing to parse.
        private static string Prettify(string json, bool truncated)
        {
            if (truncated) return json + "\n\n/* … truncated for display … */";
            try
            {
                using var doc = JsonDocument.Parse(json);
                return JsonSerializer.Serialize(doc.RootElement, new JsonSerializerOptions { WriteIndented = true });
            }
            catch (JsonException)
            {
                return json;   // corrupt payload: show it as stored, which is what an operator needs to see
            }
        }
    }
}
