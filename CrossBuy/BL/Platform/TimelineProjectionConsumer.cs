using CrossBuy.Models.Platform;
using Microsoft.Extensions.Logging;

namespace CrossBuy.BL.Platform
{
    // Platform Kernel — the only consumer registered in slice 1.
    //
    // The timeline projection is built at READ time in this slice (ITimelineProjectionService queries the
    // event log directly), so this consumer does not write a projection table. What it does instead is the
    // work that read-time projection cannot do for itself: it VALIDATES that each event is actually
    // renderable, at write time, once.
    //
    // Why that matters: a read-time projection meets an unrenderable event in a user's browser, where the
    // only available behaviours are "show a blank row" or "break the document screen". Validating here
    // moves that discovery into the outbox, where a failure lands on the dispatch row with its reason,
    // gets retried under the backoff policy, and shows up for an operator. Concretely it catches:
    //   * an entity type whose SupportsTimeline was turned off after events had been recorded;
    //   * a payload written by a newer build than the one now rendering (PayloadVersion ahead of this code);
    //   * a payload whose shape does not match the version it declares.
    //
    // When the persisted projection table arrives this class is where it gets written — the contract above
    // it does not change.
    public class TimelineProjectionConsumer : IBusinessEventConsumer
    {
        private readonly IEntityRegistry _registry;
        private readonly ILogger<TimelineProjectionConsumer> _log;

        public TimelineProjectionConsumer(IEntityRegistry registry, ILogger<TimelineProjectionConsumer> log)
        { _registry = registry; _log = log; }

        public string Consumer => BusinessEventConsumers.TimelineProjection;

        public Task HandleAsync(BusinessEventEnvelope envelope, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(envelope);

            if (!_registry.TryGetDefinition(envelope.Entity.Code, out var definition))
                throw new BusinessEventContractException(
                    $"Event {envelope.EventUid} targets entity code '{envelope.Entity.Code}', which is no longer registered.");

            if (!definition!.SupportsTimeline)
                throw new BusinessEventContractException(
                    $"Event {envelope.EventUid} targets '{definition.Code}', whose SupportsTimeline is false — " +
                    "the event would never be shown. Either enable the capability or stop producing the event.");

            if (!BusinessEventVisibility.IsValid(envelope.Visibility))
                throw new BusinessEventContractException(
                    $"Event {envelope.EventUid} carries visibility '{envelope.Visibility}', which is outside the frozen vocabulary.");

            // Render it exactly as the read path will, strictly. Throwing here is the point.
            var payloadJson = envelope.Payload?.ToJsonString();
            if (!TimelineEventPresenter.TryPresent(envelope.EventType, envelope.PayloadVersion, payloadJson, out _, out var error))
                throw new BusinessEventContractException(error!);

            _log.LogDebug("Timeline projection validated event {EventUid} ({EventType}) on {Entity}/{EntityId}",
                envelope.EventUid, envelope.EventType, envelope.Entity.Code, envelope.Entity.Id);

            return Task.CompletedTask;
        }
    }
}