using CrossBuy.BL.Platform;
using CrossBuy.Models.Communication;
using CrossBuy.Models.Platform;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CrossBuy.BL.Communication
{
    // =============================================================================================
    // Communication Platform (ADR-030 §7) — COMMUNICATION EVENTS (module 26) and the PLATFORM KERNEL BRIDGE
    // (module 27).
    //
    // Every mutating service in this platform ends with one call to PublishAsync. That call does two things,
    // and the split between them is the whole design:
    //
    //   ALWAYS   append a durable communication event to CommAuditEntries, inside the caller's transaction.
    //   OPTIONAL forward a translated copy to the platform kernel's IBusinessEventService, when the
    //            deployment has turned the bridge on AND the event type is bridgeable.
    //
    // WHY THE FORWARD IS OPTIONAL AND OFF BY DEFAULT — three reasons, each with a cost already paid somewhere
    // in this repository:
    //
    //   1. RecordAsync has NO swallowing catch. A missing BusinessEvents table fails the caller's transaction
    //      with SQL-208. CLAUDE.md records this as a live production coupling for the sale/purchase path
    //      (HM-D44/D45) and as the reason reversal now depends on the event platform (HM-D53). Wiring it into
    //      commenting would extend that blast radius to every screen in the product.
    //   2. Bridging means the kernel's registry must have the anchor entity onboarded with SupportsTimeline,
    //      or the kernel's own timeline read refuses it. Only four codes qualify today.
    //   3. CLAUDE.md's standing decision, taken in another track for exactly this reason: adopting the event
    //      platform binds this work to a queue, dispatcher and consumer set that are uncommitted in git.
    //
    // THE ORDER MATTERS AND IS NOT NEGOTIABLE. The audit row is appended FIRST. If the bridge then throws,
    // the whole transaction rolls back and neither row exists — which is correct. Bridging first would allow a
    // kernel event with no matching audit row, i.e. a fact in the shared log that this platform cannot explain.
    // =============================================================================================
    public interface ICommEventPublisher
    {
        Task<CommPublishResult> PublishAsync(CommEvent commEvent, CancellationToken cancellationToken = default);
    }

    public sealed class CommPublishResult
    {
        public required string EventType { get; init; }
        public required bool Audited { get; init; }

        // Non-null only when the bridge ran and the kernel accepted the event.
        public long? KernelEventId { get; init; }

        public required bool Bridged { get; init; }

        // Why the bridge did not run. Always populated when Bridged is false, so "the timeline is missing my
        // comment" has an answer that does not require reading this file.
        public string? BridgeSkipReason { get; init; }
    }

    // ---------------------------------------------------------------------------------------------
    // The bridge, behind its own interface so a deployment can replace it, and so the tests can assert what
    // WOULD have been sent to the kernel without a kernel schema present.
    // ---------------------------------------------------------------------------------------------
    public interface ICommBusinessEventBridge
    {
        // Returns the kernel EventId, or null when the event is not bridgeable. Throws on a kernel failure —
        // deliberately: a bridge that swallows turns a lost event into a silent success, which is the exact
        // failure mode IBusinessEventConsumer's contract warns about.
        Task<long?> ForwardAsync(CommEvent commEvent, CancellationToken cancellationToken = default);
    }

    public sealed class PlatformBusinessEventBridge : ICommBusinessEventBridge
    {
        private readonly IBusinessEventService _events;
        private readonly IEntityRegistry _registry;
        private readonly ILogger<PlatformBusinessEventBridge> _log;

        public PlatformBusinessEventBridge(
            IBusinessEventService events, IEntityRegistry registry, ILogger<PlatformBusinessEventBridge> log)
        { _events = events; _registry = registry; _log = log; }

        public async Task<long?> ForwardAsync(CommEvent commEvent, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(commEvent);

            var action = CommEventTypes.KernelActionFor(commEvent.EventType);
            if (action == null) return null;   // deliberately not bridgeable — see CommEventTypes

            // The kernel would throw EntityCodeNotRegisteredException; checking first turns a programming
            // error into a reported skip, because an unregistered anchor is a configuration state this
            // platform tolerates (its own tables accept the code via the surface allow list) while the kernel
            // does not.
            if (!_registry.TryGetDefinition(commEvent.Entity.EntityCode, out var definition) || definition == null)
            {
                _log.LogInformation(
                    "Communication event {EventType} not bridged: '{Code}' is not registered in IEntityRegistry.",
                    commEvent.EventType, commEvent.Entity.EntityCode);
                return null;
            }

            // ITimelineProjectionService REFUSES an entity whose SupportsTimeline is false, so bridging one
            // would write events nothing can ever read — invisible rows in the largest table in the database.
            if (!definition.SupportsTimeline)
            {
                _log.LogInformation(
                    "Communication event {EventType} not bridged: '{Code}' has SupportsTimeline = false, so the " +
                    "kernel timeline would refuse to read what we wrote.",
                    commEvent.EventType, definition.Code);
                return null;
            }

            var record = new BusinessEventRecord
            {
                EntityCode = definition.Code,
                EntityId = commEvent.Entity.EntityId,
                EventType = BusinessEventTypes.Build(definition.Code, action),
                Payload = commEvent.Payload,
                PayloadVersion = commEvent.PayloadVersion,

                // Public collapses to Internal: the kernel has no external tier, and narrowing is the safe
                // direction. See CommVisibility.ToBusinessEventVisibility.
                Visibility = CommVisibility.ToBusinessEventVisibility(commEvent.Visibility),

                // Namespaced so a communication-originated event can never collide with a kernel producer's
                // own dedup key for the same entity.
                DedupKey = string.IsNullOrWhiteSpace(commEvent.DedupKey) ? null : "comm:" + commEvent.DedupKey,

                CorrelationId = commEvent.CorrelationId,
                OccurredAt = commEvent.OccurredAt,
            };

            // No try/catch. RecordAsync's contract is that a failure fails the caller's transaction, and
            // catching here would leave a comment committed while the shared timeline silently missed it.
            return await _events.RecordAsync(record, cancellationToken);
        }
    }

    // ---------------------------------------------------------------------------------------------
    public sealed class CommEventPublisher : ICommEventPublisher
    {
        private readonly ICommAuditWriter _audit;
        private readonly ICommBusinessEventBridge _bridge;
        private readonly CommDb _db;
        private readonly CommunicationPlatformOptions _options;
        private readonly ILogger<CommEventPublisher> _log;

        public CommEventPublisher(
            CrossBuy.Models.Context.CrossDbContext db,
            ICommAuditWriter audit,
            ICommBusinessEventBridge bridge,
            IOptions<CommunicationPlatformOptions> options,
            ILogger<CommEventPublisher> log)
        {
            _db = new CommDb(db);
            _audit = audit;
            _bridge = bridge;
            _options = options.Value;
            _log = log;
        }

        public async Task<CommPublishResult> PublishAsync(CommEvent commEvent, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(commEvent);

            if (!CommEventTypes.IsValid(commEvent.EventType))
                throw new InvalidOperationException(
                    $"'{commEvent.EventType}' is not a CommEventTypes value. The vocabulary is frozen so a name " +
                    "cannot fork the way three entity-type vocabularies did before ADR-002.");

            // ---- 1. the durable communication event. ALWAYS, and FIRST.
            _audit.AppendForEvent(commEvent);

            // ---- 2. the optional kernel forward.
            var (shouldBridge, skipReason) = ShouldBridge(commEvent);
            if (!shouldBridge)
                return new CommPublishResult
                {
                    EventType = commEvent.EventType,
                    Audited = true,
                    Bridged = false,
                    BridgeSkipReason = skipReason,
                };

            long? kernelEventId = await _bridge.ForwardAsync(commEvent, cancellationToken);

            if (kernelEventId == null)
                return new CommPublishResult
                {
                    EventType = commEvent.EventType,
                    Audited = true,
                    Bridged = false,
                    BridgeSkipReason = "the bridge declined the event (unregistered code, or SupportsTimeline is false)",
                };

            _log.LogDebug("Communication event {EventType} bridged to kernel event {EventId}.",
                commEvent.EventType, kernelEventId);

            return new CommPublishResult
            {
                EventType = commEvent.EventType,
                Audited = true,
                Bridged = true,
                KernelEventId = kernelEventId,
            };
        }

        private (bool ShouldBridge, string? Reason) ShouldBridge(CommEvent commEvent)
        {
            if (!_options.BridgeToBusinessEvents)
                return (false, "CommunicationPlatform:BridgeToBusinessEvents is off (the default).");

            if (!CommEventTypes.IsBridgeable(commEvent.EventType))
                return (false, $"'{commEvent.EventType}' is deliberately not bridgeable — see CommEventTypes.KernelActionFor.");

            if (_options.BridgedEventTypes.Count > 0
                && !_options.BridgedEventTypes.Contains(commEvent.EventType, StringComparer.Ordinal))
                return (false, $"'{commEvent.EventType}' is not in CommunicationPlatform:BridgedEventTypes.");

            // RecordAsync THROWS without an ambient transaction. Every write path in this platform opens one
            // (CommTransaction), so reaching here without one means a caller bypassed the service layer —
            // report it as a skip rather than letting the kernel throw a message about a rule that is ours
            // to satisfy, not the caller's to debug.
            if (!_db.HasAmbientTransaction)
                return (false,
                    "no ambient transaction: IBusinessEventService.RecordAsync requires one and would throw. " +
                    "Publish from inside a CommTransaction.");

            return (true, null);
        }
    }

    // ---------------------------------------------------------------------------------------------
    // The null bridge — REGISTERED BY DEFAULT.
    //
    // It exists so that ICommEventPublisher has a dependency to resolve without the platform kernel's event
    // service being part of this platform's required DI graph. A deployment that turns the bridge on swaps
    // this for PlatformBusinessEventBridge in one registration line (see AddCommunicationPlatform).
    //
    // It returns null rather than throwing: with BridgeToBusinessEvents off, ForwardAsync is never called at
    // all, and if a deployment turns the flag on WITHOUT swapping the implementation, the honest outcome is a
    // reported skip — not a broken comment.
    // ---------------------------------------------------------------------------------------------
    public sealed class NullCommBusinessEventBridge : ICommBusinessEventBridge
    {
        public Task<long?> ForwardAsync(CommEvent commEvent, CancellationToken cancellationToken = default)
            => Task.FromResult<long?>(null);
    }
}