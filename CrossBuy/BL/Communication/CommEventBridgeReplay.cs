using CrossBuy.BL.Platform;
using CrossBuy.Models.Communication;
using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Communication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CrossBuy.BL.Communication
{
    // =============================================================================================
    // Communication Platform — BRIDGE REPLAY (Phase 2).
    //
    // WHY THIS EXISTS. CommEventPublisher's isolated mode keeps the user's comment when the kernel
    // is unavailable (HM-D44's missing BusinessEvents table, HM-D53's no-swallow RecordAsync). That
    // protects availability, but on its own it would trade a failed comment for a lost event. This
    // service is the other half: it re-offers events the inline bridge could not deliver, so the
    // shared timeline converges once the kernel comes back.
    //
    // WHY IT NEEDS NO NEW TABLE. CommAuditEntries is already a faithful outbox. It is written FIRST
    // and ALWAYS, inside the comment's own transaction, and CommAuditWriter.AppendForEvent copies
    // the event verbatim — including DedupKey, CorrelationId, Visibility and the serialized payload
    // (Detail = detailJson ?? SerializeOrNull(commEvent.Payload)). Everything the bridge sends can
    // therefore be rebuilt from the audit row, which is why Phase 2 required no migration.
    //
    // WHY A WINDOW SCAN IS SAFE. This re-offers events it has already delivered. That is sound only
    // because the kernel is idempotent on DedupKey — BusinessEventService.RecordAsync: "A duplicate
    // key returns the original id and writes nothing" — and because PlatformBusinessEventBridge
    // namespaces the key as "comm:{DedupKey}" from the SAME value the audit row preserved. A second
    // pass therefore matches the first pass's row instead of writing a second event.
    //
    // KNOWN INEFFICIENCY, DELIBERATELY NOT FIXED HERE. The precise form is a nullable BridgedAt
    // column on CommAuditEntries: "pending" would be an index seek rather than inferred from a time
    // window, and already-delivered rows would not be re-offered at all. That is a MIGRATION, which
    // is outside what this work was authorized to do. Recorded for the owner rather than taken.
    //
    // ONE TRANSACTION PER ROW, NOT ONE PER BATCH. RecordAsync refuses to run without an ambient
    // transaction, so each row gets its own. Per-row rather than per-batch so one poisoned event
    // cannot roll back the events that were forwarded successfully beside it.
    // =============================================================================================
    public sealed class CommBridgeReplayReport
    {
        public required int Scanned { get; init; }
        public required int Forwarded { get; init; }

        // The bridge declined it — not bridgeable, code not registered, or SupportsTimeline is false.
        // A skip is a decision, not a fault, and is never retried into a growing error count.
        public required int Skipped { get; init; }

        public required int Failed { get; init; }

        public static CommBridgeReplayReport Empty =>
            new() { Scanned = 0, Forwarded = 0, Skipped = 0, Failed = 0 };
    }

    public interface ICommEventBridgeReplayService
    {
        Task<CommBridgeReplayReport> ReplayAsync(int companyId, CancellationToken cancellationToken = default);
    }

    public sealed class CommEventBridgeReplayService : ICommEventBridgeReplayService
    {
        private readonly CommDb _db;
        private readonly ICommBusinessEventBridge _bridge;
        private readonly CommunicationPlatformOptions _options;
        private readonly ILogger<CommEventBridgeReplayService> _log;

        public CommEventBridgeReplayService(
            CrossDbContext db,
            ICommBusinessEventBridge bridge,
            IOptions<CommunicationPlatformOptions> options,
            ILogger<CommEventBridgeReplayService> log)
        {
            _db = new CommDb(db);
            _bridge = bridge;
            _options = options.Value;
            _log = log;
        }

        public async Task<CommBridgeReplayReport> ReplayAsync(
            int companyId, CancellationToken cancellationToken = default)
        {
            // The company is a REQUIRED argument rather than something read from an ambient context.
            // A replay pass runs outside a request, and a resolver that answers 0 for a background
            // scope would otherwise scan every tenant's audit rows under one company's authority.
            if (companyId <= 0)
                throw new ArgumentOutOfRangeException(nameof(companyId),
                    "Replay runs for one explicitly named company: a background scope resolves no company of its own.");

            var since = DateTime.UtcNow.AddHours(-Math.Abs(_options.BridgeReplayLookbackHours));

            // CompanyID is compared on the ROW. These tables carry no global query filter, so this
            // predicate is the only thing keeping one tenant's events out of another's replay.
            //
            // BridgeFailed is excluded in SQL as well as by the canonical check below. It names the event
            // it could not deliver, so it carries that EventType — and replaying the marker alongside the
            // event it marks would forward everything twice, which is precisely the duplicate delivery
            // this service exists to avoid. (Found by its own test, not by reasoning.)
            var rows = await _db.Audit.AsNoTracking()
                .Where(a => a.CompanyID == companyId
                            && a.EventType != null
                            && a.Action != CommAuditActions.BridgeFailed
                            && a.CreatedAt >= since)
                .OrderBy(a => a.Id)
                .Take(Math.Max(1, _options.DispatchBatchSize))
                .ToListAsync(cancellationToken);

            int forwarded = 0, skipped = 0, failed = 0;

            foreach (var row in rows)
            {
                if (!CommEventTypes.IsBridgeable(row.EventType)) { skipped++; continue; }

                // Only the row that IS the event's own record may be replayed. Stated as "this row's action
                // is the canonical action for its event type" rather than as a list of actions to exclude,
                // so a future audit action that borrows an EventType cannot quietly become a second copy of
                // an event. A denylist would have to be remembered; this cannot be forgotten.
                if (!string.Equals(row.Action, CommAuditWriter.ActionForEvent(row.EventType!), StringComparison.Ordinal))
                {
                    skipped++;
                    continue;
                }

                var commEvent = Rebuild(row);

                try
                {
                    await using var tx = await CommTransaction.BeginAsync(_db.Context, cancellationToken);
                    long? kernelEventId = await _bridge.ForwardAsync(commEvent, cancellationToken);
                    await tx.CommitAsync(cancellationToken);

                    if (kernelEventId == null) skipped++; else forwarded++;
                }
                catch (Exception ex)
                {
                    // Not rethrown: one undeliverable event must not stop the batch behind it, and the
                    // row stays inside the window so the next pass tries again. The transaction above
                    // was never committed, so nothing partial survives.
                    failed++;
                    _log.LogError(ex,
                        "Bridge replay failed for audit row {AuditId} ({EventType}) in company {CompanyId}.",
                        row.Id, row.EventType, companyId);
                }
            }

            if (failed > 0 || forwarded > 0)
                _log.LogInformation(
                    "Bridge replay for company {CompanyId}: scanned {Scanned}, forwarded {Forwarded}, skipped {Skipped}, failed {Failed}.",
                    companyId, rows.Count, forwarded, skipped, failed);

            return new CommBridgeReplayReport
            {
                Scanned = rows.Count,
                Forwarded = forwarded,
                Skipped = skipped,
                Failed = failed,
            };
        }

        // The audit row back into the event it recorded. Payload is handed over as the stored JSON
        // STRING: BusinessEventService.SerializePayload passes a string through unchanged (after
        // validating it parses), so the kernel receives the same bytes the inline bridge would have
        // sent rather than a re-serialized approximation of them.
        private static CommEvent Rebuild(CommAuditEntry row) => new()
        {
            EventType = row.EventType!,
            Entity = new CommEntityRef(row.EntityType, row.EntityId),
            ThreadId = row.ThreadId,
            CommentId = row.CommentId,
            MentionId = row.MentionId,
            CompanyId = row.CompanyID,
            BranchId = row.BranchID,
            ActorEmployeeId = row.ActorEmployeeId,
            Visibility = row.Visibility ?? CommVisibility.Internal,
            Payload = row.DetailJson,
            PayloadVersion = 1,

            // Verbatim from the audit row. This is the whole basis of "no duplicate delivery": the
            // bridge namespaces it to "comm:{DedupKey}", and a repeat pass therefore presents the
            // kernel with the key it already holds.
            DedupKey = row.DedupKey,
            CorrelationId = row.CorrelationId,
            OccurredAt = row.CreatedAt,
        };
    }

    // =============================================================================================
    // The replay worker. Deliberately the SAME shape as CommNotificationHostedService: it is only
    // registered when the configuration flag is on, it refuses to run without an explicit positive
    // company allow-list, and it pins a scope per company through WorkerScope rather than trusting a
    // background scope to resolve a company of its own — a background DI scope resolves company 0,
    // and a company-filtered read under company 0 returns an empty table that looks like clean data.
    // =============================================================================================
    public sealed class CommBridgeReplayHostedService : BackgroundService
    {
        private readonly IServiceScopeFactory _scopes;
        private readonly IWorkerGate _gate;
        private readonly CommunicationPlatformOptions _options;
        private readonly ILogger<CommBridgeReplayHostedService> _log;

        public CommBridgeReplayHostedService(IServiceScopeFactory scopes, IWorkerGate gate,
            IOptions<CommunicationPlatformOptions> options, ILogger<CommBridgeReplayHostedService> log)
        { _scopes = scopes; _gate = gate; _options = options.Value; _log = log; }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            if (!_options.BridgeReplayEnabled) return;
            using var timer = new PeriodicTimer(
                TimeSpan.FromSeconds(Math.Clamp(_options.BridgeReplayPollSeconds, 1, 300)));
            do
            {
                try { await RunPassAsync(stoppingToken); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
                catch (Exception ex) { _log.LogError(ex, "Communication bridge replay pass failed"); }
            } while (await timer.WaitForNextTickAsync(stoppingToken));
        }

        internal async Task RunPassAsync(CancellationToken cancellationToken)
        {
            if (!_options.BridgeReplayEnabled) return;
            if (_options.BridgeReplayCompanyIds.Count == 0 || _options.BridgeReplayCompanyIds.Any(id => id <= 0))
                throw new InvalidOperationException(
                    "An explicit positive company allow-list is required for bridge-replay activation.");
            if (!await _gate.WaitUntilAllowedAsync(nameof(CommBridgeReplayHostedService), cancellationToken)) return;

            foreach (var company in _options.BridgeReplayCompanyIds.Distinct().OrderBy(id => id))
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    using var scope = WorkerScope.ForCompany(_scopes, company);
                    var replay = scope.ServiceProvider.GetRequiredService<ICommEventBridgeReplayService>();
                    await replay.ReplayAsync(company, cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch (Exception ex)
                {
                    // One company's kernel trouble must not stop the next company's replay.
                    _log.LogError(ex, "Communication bridge replay failed for company {CompanyId}", company);
                }
            }
        }
    }
}
