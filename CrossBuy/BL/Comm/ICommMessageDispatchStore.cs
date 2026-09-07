namespace CrossBuy.BL.Comm
{
    // Stage 0 (Slice-003) — the email outbox work queue.
    //
    // Deliberately a SEPARATE store from IEventDispatchStore. CommMessage owns email-delivery state and always
    // has (Status/Attempts/Error/SentAt predate the Platform Kernel); routing mail through BusinessEventDispatch
    // would put two unrelated lifecycles in one table and make "retry this email" mean "replay this event".
    // What IS reused is the PATTERN: claim-by-status, never a cursor; atomic UPDATE ... OUTPUT with
    // UPDLOCK/READPAST so two workers cannot take the same row (ADR-003, ADR-007).
    public interface ICommMessageDispatchStore
    {
        // ATOMICALLY takes up to batchSize messages, flipping them to Claimed and incrementing Attempts in the
        // same statement. Rows another worker holds are skipped, not waited on.
        Task<IReadOnlyList<CommMessageWorkItem>> ClaimPendingAsync(int batchSize, CancellationToken cancellationToken = default);

        // Non-mutating peek — diagnostics and tests. The dispatcher uses ClaimPendingAsync.
        Task<IReadOnlyList<CommMessageWorkItem>> GetPendingAsync(int batchSize, CancellationToken cancellationToken = default);

        // Terminal success. Sent is never reclaimed.
        Task MarkSentAsync(int messageId, CancellationToken cancellationToken = default);

        // Returns the row to Failed so it becomes retry-eligible after the backoff, until MaxAttempts.
        // The error text is truncated to fit the column.
        Task MarkFailedAsync(int messageId, string error, CancellationToken cancellationToken = default);
    }

    // One unit of outbox work. Carries no body and no attachment bytes — the sender loads what it needs, so a
    // claim never materialises message content (and nothing sensitive can leak through a log of this type).
    public sealed class CommMessageWorkItem
    {
        public required int MessageId { get; init; }
        public required int CompanyId { get; init; }
        public required int Attempts { get; init; }
    }

    // Dispatcher tuning, bound from the "CommMessageDispatch" configuration section.
    // Defaults are conservative: this queue carries customer-facing mail, so not sending twice matters far more
    // than sending fast.
    public sealed class CommMessageDispatchOptions
    {
        // Let the app finish starting before the first pass. SMTP availability must never gate startup.
        public int InitialDelaySeconds { get; set; } = 45;

        // How often to poll.
        public int PollSeconds { get; set; } = 30;

        // Messages claimed per pass.
        public int BatchSize { get; set; } = 20;

        // A message that has failed this many times stops being retried and stays Failed WITH its error for an
        // operator. It is never deleted and never silently dropped.
        public int MaxAttempts { get; set; } = 5;

        // Minimum wait before a Failed message is retried.
        public int RetryBackoffSeconds { get; set; } = 120;

        // A Claimed message whose worker died is released for reclaim after this long.
        public int StaleClaimMinutes { get; set; } = 10;
    }
}
