using CrossBuy.Models.Platform;

namespace CrossBuy.BL.Platform
{
    // Platform Kernel (ADR-003) — one outbox consumer.
    //
    // Contract: HandleAsync either succeeds or THROWS. It must not swallow its own failure, because the
    // dispatch row is what makes a failure retryable and visible; a consumer that catches everything and
    // returns turns a lost event into a silent success. Handling must also be idempotent — a row can be
    // redelivered after a stale claim or a retry.
    public interface IBusinessEventConsumer
    {
        // Must equal one of BusinessEventConsumers.Registered.
        string Consumer { get; }

        Task HandleAsync(BusinessEventEnvelope envelope, CancellationToken cancellationToken = default);
    }
}