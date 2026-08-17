using CrossBuy.Models.Platform;

namespace CrossBuy.BL.Platform
{
    // Platform Kernel (ADR-003) — the outbox work queue, behind an interface so the SQL table can later be
    // replaced by a real queue (Service Bus / Rabbit) without touching a single consumer.
    //
    // The contract deliberately exposes NO cursor, NO MAX(EventId) and NO global watermark. Work is
    // identified by per-consumer STATUS only, because EventId ordering is unsafe: identity values are
    // assigned at INSERT and become visible at COMMIT, so an event can appear "in the past" after a
    // higher-numbered one has already been processed.
    public interface IEventDispatchStore
    {
        // Non-mutating peek at the work waiting for a consumer. Diagnostics and tests use this; the worker
        // uses ClaimPendingAsync so two workers cannot pick up the same row.
        Task<IReadOnlyList<DispatchWorkItem>> GetPendingAsync(string consumer, int batchSize, CancellationToken cancellationToken = default);

        // ATOMICALLY takes up to batchSize rows for this consumer, flipping them to Claimed and
        // incrementing Attempts in the same statement. Rows another worker already holds are skipped, not
        // waited on. Returns the rows this caller now owns.
        Task<IReadOnlyList<DispatchWorkItem>> ClaimPendingAsync(string consumer, int batchSize, CancellationToken cancellationToken = default);

        // Closes the ONE hole the claim loop structurally cannot reach: a claim abandoned on its LAST attempt.
        //
        // ClaimPendingAsync already reclaims a stale Claimed row — but every branch of its WHERE sits under
        // `AND d.Attempts < @maxAttempts`. A worker that dies mid-batch on the final attempt therefore leaves
        // the row Claimed with Attempts = MaxAttempts, which no predicate can ever match again. It is not
        // retryable and it is not terminal: it reads as work in progress forever, and BusinessEventMonitor
        // cannot tell it apart from a row a worker is holding right now. 23 such rows sat in CrossBuyDev from
        // 2026-08-03, two of them SalesInvoice.Created.
        //
        // The recovery is a CLASSIFICATION, not a retry: the row moves to the same terminal Failed state an
        // attempts-exhausted row reaches by the normal route, with Attempts left exactly as it was. Nothing is
        // marked Done, nothing is deleted, no attempt budget is refunded, and a row with attempts REMAINING is
        // deliberately not touched here — that one is already the claim loop's job, and doing it in both places
        // would be two owners for one transition.
        //
        // Returns the number of rows released, so a worker can log a real figure instead of "did something".
        Task<int> ReleaseAbandonedClaimsAsync(string consumer, CancellationToken cancellationToken = default);

        Task MarkDoneAsync(long dispatchId, CancellationToken cancellationToken = default);

        // Records the failure and returns the row to Failed so it becomes retry-eligible after the backoff
        // (until MaxAttempts). The error text is truncated to fit the nvarchar(400) column.
        Task MarkFailedAsync(long dispatchId, string error, CancellationToken cancellationToken = default);

        Task IncrementAttemptsAsync(long dispatchId, CancellationToken cancellationToken = default);

        // Stamps BusinessEvents.CompletedAt when every registered consumer for that event reached Done.
        // Reporting roll-up only — never used to decide what to dispatch.
        Task TryCompleteEventAsync(long eventId, CancellationToken cancellationToken = default);

        // Stage 0 Batch B — OPERATOR-INITIATED retry of ONE consumer's dispatch row.
        //
        // This is the only sanctioned way to re-queue work from outside the dispatcher. It lives here, not in a
        // controller, because the eligibility rules and the race with a live worker are storage concerns:
        //   * only Failed or STALE Claimed rows are eligible;
        //   * Done is refused — replaying a completed consumer would duplicate its side effects (a second email);
        //   * Pending is refused — it is already queued, so "retry" would be a no-op that looks like an action;
        //   * a FRESH Claimed row is refused — a worker is holding it right now;
        //   * Attempts/MaxAttempts are respected unless an elevated override is supplied WITH a reason;
        //   * only the named consumer's row is reset. Sibling consumers that already reached Done are untouched.
        Task<DispatchRetryResult> RetryAsync(
            long dispatchId,
            BusinessContext context,
            string reason,
            bool elevatedOverride,
            CancellationToken cancellationToken = default);
    }

    // Why a retry was accepted or refused. Every outcome is user-safe text — no internal state is echoed.
    public enum DispatchRetryOutcome
    {
        Requeued = 0,
        NotFound,
        AlreadyDone,
        AlreadyPending,
        HeldByWorker,
        AttemptsExhausted,
        ReasonRequired,
        NotAuthorized,
        RaceLost,
    }

    public sealed class DispatchRetryResult
    {
        public required DispatchRetryOutcome Outcome { get; init; }
        public required string Message { get; init; }
        public long DispatchId { get; init; }
        public long EventId { get; init; }
        public string? Consumer { get; init; }
        public int Attempts { get; init; }
        public bool Success => Outcome == DispatchRetryOutcome.Requeued;

        public static DispatchRetryResult Fail(DispatchRetryOutcome outcome, string message, long dispatchId = 0)
            => new() { Outcome = outcome, Message = message, DispatchId = dispatchId };
    }

    // One unit of outbox work. Carries no payload: the consumer loads the event it needs, so a queue-based
    // store can keep messages small.
    public sealed class DispatchWorkItem
    {
        public required long DispatchId { get; init; }
        public required long EventId { get; init; }
        public required string Consumer { get; init; }
        public required int Attempts { get; init; }
    }

    // Dispatcher tuning. Defaults are deliberately conservative: this queue carries audit fan-out, not
    // interactive work, so latency matters far less than never losing or double-processing a row.
    public sealed class BusinessEventDispatchOptions
    {
        // Rows claimed per poll, per consumer.
        public int BatchSize { get; set; } = 50;

        // How often the worker polls when the queue was empty.
        public int PollSeconds { get; set; } = 15;

        // A row that has failed this many times stops being retried and stays Failed for an operator to
        // inspect. It is never deleted and never silently dropped.
        public int MaxAttempts { get; set; } = 5;

        // Minimum wait before a Failed row is retried.
        public int RetryBackoffSeconds { get; set; } = 30;

        // A Claimed row whose owner died is released back for reclaim after this long.
        public int StaleClaimMinutes { get; set; } = 10;
    }
}