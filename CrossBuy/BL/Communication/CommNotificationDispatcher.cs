using CrossBuy.Models.Communication;
using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Communication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CrossBuy.BL.Communication
{
    // =============================================================================================
    // Communication Platform (ADR-034 §6) — THE DELIVER STEP (modules 17-21).
    //
    // WHY THERE IS NO HOSTED SERVICE IN THIS PHASE
    //
    // A BackgroundService would be production integration, which this phase excludes — and it would come with
    // obligations this platform cannot discharge alone: CLAUDE.md requires every hosted service to take
    // IServiceScopeFactory (Batch C's own PermissionScopeStartupValidator broke this and stopped the app from
    // starting), to bind an explicit company scope (an unbound worker "reads nothing and passes by examining
    // zero rows"), and to be added to Stage1DiWiringTests.
    //
    // So the drain is a CALLABLE method with the worker-facing shape already correct: claim a batch by STATUS,
    // deliver, release. Wrapping it in a BackgroundService is a small, reviewable step for the phase that owns
    // production wiring — and the claim semantics it depends on are already proven by tests here.
    //
    // THE CLAIM RULE, taken verbatim from ADR-003/ADR-007 because it was learned expensively:
    //
    //     CLAIM BY STATUS ONLY. NEVER a cursor, NEVER MAX(Id), NEVER "Id > lastSeen".
    //
    // Ids are assigned at INSERT and become visible at COMMIT, so a cursor skips a late-committing lower id
    // permanently. The kernel's dispatcher documents this; a second queue in the same product must not
    // rediscover it.
    //
    // ON CONCURRENCY, stated honestly: the claim below is EF-based (read candidates, stamp Claimed, save) and
    // relies on the unique (NotificationId, Channel) index plus a stale-claim clock. It is correct for a single
    // worker and SAFE-BUT-LOSSY-IN-THROUGHPUT for several: two workers can select the same row and one loses on
    // save. The production shape is a SQL-Server store mirroring SqlEventDispatchStore's
    // `UPDATE TOP(n) ... OUTPUT ... WITH (ROWLOCK, READPAST, UPDLOCK)`, which cannot be expressed in
    // provider-neutral EF and cannot be exercised by the SQLite test host. ICommDeliveryClaimStore is that seam,
    // and the limitation is recorded in CPS-001 §9 rather than left for someone to discover under load.
    // =============================================================================================
    public interface ICommNotificationDispatcher
    {
        // Claims and delivers one batch. Returns what happened, per channel. Safe to call repeatedly; returns
        // Claimed = 0 when the queue is empty.
        Task<CommDispatchReport> DispatchPendingAsync(
            int? companyId = null, int? batchSize = null, CancellationToken cancellationToken = default);
    }

    public sealed class CommDispatchReport
    {
        public required int Claimed { get; init; }
        public required int Delivered { get; init; }
        public required int Skipped { get; init; }
        public required int Failed { get; init; }
        public required int Abandoned { get; init; }
        public required IReadOnlyDictionary<string, int> ByChannel { get; init; }
        public required IReadOnlyList<string> Notes { get; init; }

        public static CommDispatchReport Nothing(string note) => new()
        {
            Claimed = 0, Delivered = 0, Skipped = 0, Failed = 0, Abandoned = 0,
            ByChannel = new Dictionary<string, int>(StringComparer.Ordinal),
            Notes = new[] { note },
        };
    }

    // ---------------------------------------------------------------------------------------------
    // THE CHANNEL EXTENSION POINT (modules 18-21).
    //
    // A channel that is not registered produces Skipped delivery rows with a reason — never Pending forever.
    // That is the inverse of the kernel's rule for consumers ("a name in this list with no IBusinessEventConsumer
    // registered accumulates dispatch rows nothing drains"), applied to channels: the vocabulary may name a
    // channel before an adapter exists, and the dispatcher parks its rows honestly.
    // ---------------------------------------------------------------------------------------------
    public interface ICommNotificationChannel
    {
        string Channel { get; }

        // False parks rows as Skipped without an attempt — used by a channel that is configured but has no
        // credentials, so the operator sees a reason rather than a growing Pending queue.
        bool IsEnabled { get; }

        // Must either return a result or THROW. A channel that swallows its own failure turns a lost
        // notification into a silent success — the same contract IBusinessEventConsumer states.
        Task<CommChannelResult> SendAsync(CommChannelMessage message, CancellationToken cancellationToken = default);
    }

    // The in-app channel. It writes NOTHING external: the CommNotification row IS the in-app notification, so
    // delivery is simply acknowledging that the inbox row exists. That is why it is the only channel enabled by
    // default and the only one that can never fail.
    public sealed class InAppCommNotificationChannel : ICommNotificationChannel
    {
        public string Channel => CommChannel.InApp;
        public bool IsEnabled => true;

        public Task<CommChannelResult> SendAsync(CommChannelMessage message, CancellationToken cancellationToken = default)
            => Task.FromResult(CommChannelResult.Ok("inbox:" + message.NotificationId));
    }

    // ---------------------------------------------------------------------------------------------
    // Email / Push / WhatsApp — DECLARED, NOT WIRED.
    //
    // These are NOT registered by AddCommunicationPlatform. They exist so the shape a future adapter must
    // satisfy is frozen and reviewable now, and so the delivery path can be tested end to end against a channel
    // that reports Skipped for a real reason.
    //
    // Email deliberately does NOT bridge to the existing CommService/CommMessages outbox. That queue is the
    // parallel team's, drained by their CommMessageDispatcherHostedService, and writing into it would make this
    // platform a second producer for a queue with its own status vocabulary and retry policy — the "second
    // notification path bypasses the outbox" conflict CLAUDE.md already records (HM-D46). Bridging is a decision
    // for the phase that owns production wiring, taken with that team.
    // ---------------------------------------------------------------------------------------------
    public sealed class UnwiredCommNotificationChannel : ICommNotificationChannel
    {
        private readonly string _reason;

        public UnwiredCommNotificationChannel(string channel, string reason)
        { Channel = channel; _reason = reason; }

        public string Channel { get; }
        public bool IsEnabled => false;

        public Task<CommChannelResult> SendAsync(CommChannelMessage message, CancellationToken cancellationToken = default)
            => Task.FromResult(CommChannelResult.Skip(_reason));

        // Factories, so the reason for each unwired channel is stated once and identically everywhere.
        public static UnwiredCommNotificationChannel Email() => new(CommChannel.Email,
            "no email adapter is registered; bridging to CommMessages is a production-wiring decision (ADR-034 §6)");

        public static UnwiredCommNotificationChannel Push() => new(CommChannel.Push,
            "no push adapter is registered; ICommPushTokenStore has no implementation (module 21 is contracts-only)");

        public static UnwiredCommNotificationChannel WhatsApp() => new(CommChannel.WhatsApp,
            "no WhatsApp adapter is registered; the channel is declared-not-wired (module 20)");
    }

    // ---------------------------------------------------------------------------------------------
    // The claim seam. One implementation ships (EF); a SQL-Server one mirroring SqlEventDispatchStore is the
    // production upgrade, and it is an interface so that upgrade touches nothing else.
    // ---------------------------------------------------------------------------------------------
    public interface ICommDeliveryClaimStore
    {
        Task<IReadOnlyList<CommNotificationDelivery>> ClaimAsync(
            int? companyId, int batchSize, CancellationToken cancellationToken = default);
    }

    public sealed class EfCommDeliveryClaimStore : ICommDeliveryClaimStore
    {
        private readonly CommDb _db;
        private readonly CommunicationPlatformOptions _options;

        public EfCommDeliveryClaimStore(CrossDbContext db, IOptions<CommunicationPlatformOptions> options)
        {
            _db = new CommDb(db);
            _options = options.Value;
        }

        public async Task<IReadOnlyList<CommNotificationDelivery>> ClaimAsync(
            int? companyId, int batchSize, CancellationToken cancellationToken = default)
        {
            var staleBefore = DateTime.UtcNow.AddMinutes(-Math.Max(1, _options.StaleClaimMinutes));

            // THE PREDICATE IS PURELY STATUS-BASED. No cursor, no MAX(Id) — see the file header for why.
            //
            // Three eligible states:
            //   Pending                              never attempted
            //   Failed   under the attempt cap        retryable
            //   Claimed  older than the stale clock   the worker holding it died
            var candidates = await _db.Deliveries
                .Where(d => (companyId == null || d.CompanyID == companyId.Value)
                            && d.Attempts < _options.MaxDeliveryAttempts
                            && (d.Status == CommDeliveryStatus.Pending
                                || d.Status == CommDeliveryStatus.Failed
                                || (d.Status == CommDeliveryStatus.Claimed
                                    && d.ClaimedAt != null && d.ClaimedAt < staleBefore)))

                // Oldest first so a row can never be starved by newer traffic. This is an ORDERING, not a
                // cursor: nothing is remembered between batches.
                .OrderBy(d => d.Id)
                .Take(Math.Max(1, batchSize))
                .ToListAsync(cancellationToken);

            if (candidates.Count == 0) return Array.Empty<CommNotificationDelivery>();

            var now = DateTime.UtcNow;
            foreach (var row in candidates)
            {
                row.Status = CommDeliveryStatus.Claimed;
                row.ClaimedAt = now;
                row.UpdatedAt = now;
            }

            // Stamping the claim in its own SaveChanges is what makes it a claim rather than an intention: the
            // rows are visibly Claimed before any adapter is called, so a crash mid-batch leaves them reclaimable
            // by the stale clock instead of Pending and delivered twice.
            await _db.SaveAsync(cancellationToken);
            return candidates;
        }
    }

    // ---------------------------------------------------------------------------------------------
    public sealed class CommNotificationDispatcher : ICommNotificationDispatcher
    {
        private readonly CommDb _db;
        private readonly ICommDeliveryClaimStore _claims;
        private readonly Dictionary<string, ICommNotificationChannel> _channels;
        private readonly ICommAuditWriter _audit;
        private readonly CommunicationPlatformOptions _options;
        private readonly ILogger<CommNotificationDispatcher> _log;

        public CommNotificationDispatcher(
            CrossDbContext db,
            ICommDeliveryClaimStore claims,
            IEnumerable<ICommNotificationChannel> channels,
            ICommAuditWriter audit,
            IOptions<CommunicationPlatformOptions> options,
            ILogger<CommNotificationDispatcher> log)
        {
            _db = new CommDb(db);
            _claims = claims;

            // LAST registration wins per channel, so a deployment replaces the unwired Email placeholder by
            // registering a real adapter after ours.
            _channels = new Dictionary<string, ICommNotificationChannel>(StringComparer.Ordinal);
            foreach (var channel in channels) _channels[channel.Channel] = channel;

            _audit = audit;
            _options = options.Value;
            _log = log;
        }

        public async Task<CommDispatchReport> DispatchPendingAsync(
            int? companyId = null, int? batchSize = null, CancellationToken cancellationToken = default)
        {
            int size = batchSize is > 0 ? batchSize.Value : _options.DispatchBatchSize;

            var claimed = await _claims.ClaimAsync(companyId, size, cancellationToken);
            if (claimed.Count == 0) return CommDispatchReport.Nothing("queue empty");

            var notificationIds = claimed.Select(d => d.NotificationId).Distinct().ToList();
            var notifications = await _db.Notifications.AsNoTracking()
                .Where(n => notificationIds.Contains(n.Id))
                .ToDictionaryAsync(n => n.Id, cancellationToken);

            int delivered = 0, skipped = 0, failed = 0, abandoned = 0;
            var byChannel = new Dictionary<string, int>(StringComparer.Ordinal);
            var notes = new List<string>();

            foreach (var row in claimed)
            {
                row.Attempts += 1;
                row.UpdatedAt = DateTime.UtcNow;

                if (!notifications.TryGetValue(row.NotificationId, out var notification))
                {
                    // The parent is gone. Skipped, not Failed: retrying cannot make it reappear, and five
                    // attempts against a missing row is pure noise in the queue.
                    Park(row, CommDeliveryStatus.Skipped, "parent notification row is missing");
                    skipped++;
                    continue;
                }

                if (!_channels.TryGetValue(row.Channel, out var channel))
                {
                    Park(row, CommDeliveryStatus.Skipped, $"no adapter is registered for channel '{row.Channel}'");
                    skipped++;
                    Audit(notification, row, CommAuditActions.NotificationSkipped, row.Error);
                    continue;
                }

                if (!channel.IsEnabled)
                {
                    var result = await channel.SendAsync(BuildMessage(notification, row), cancellationToken);
                    Park(row, CommDeliveryStatus.Skipped, result.Reason ?? $"channel '{row.Channel}' is disabled");
                    skipped++;
                    Audit(notification, row, CommAuditActions.NotificationSkipped, row.Error);
                    continue;
                }

                try
                {
                    var result = await channel.SendAsync(BuildMessage(notification, row), cancellationToken);

                    if (result.Delivered)
                    {
                        row.Status = CommDeliveryStatus.Sent;
                        row.SentAt = DateTime.UtcNow;
                        row.Error = null;
                        row.ExternalReference = result.ExternalReference;
                        row.ClaimedAt = null;
                        delivered++;
                        Bump(byChannel, row.Channel);
                        Audit(notification, row, CommAuditActions.NotificationDelivered, null);
                    }
                    else if (result.Skipped)
                    {
                        // A PERMANENT condition ("this recipient has no push token"). Never retried — the
                        // distinction from Failed is the whole reason CommChannelResult has two negative cases.
                        Park(row, CommDeliveryStatus.Skipped, result.Reason ?? "channel declined");
                        skipped++;
                        Audit(notification, row, CommAuditActions.NotificationSkipped, row.Error);
                    }
                    else
                    {
                        failed++;
                        if (MarkFailed(row, result.Reason ?? "channel reported failure")) abandoned++;
                        Audit(notification, row, CommAuditActions.NotificationFailed, row.Error);
                    }
                }
                catch (Exception ex)
                {
                    // A throwing adapter is a FAILURE, i.e. retryable — an SMTP timeout is not a permanent
                    // condition. One bad row must not abort the batch: the remaining claimed rows still get their
                    // attempt, and this one is retried until the cap.
                    _log.LogWarning(ex, "Channel {Channel} threw delivering notification {NotificationId}.",
                        row.Channel, row.NotificationId);

                    failed++;
                    if (MarkFailed(row, ex.GetType().Name + ": " + ex.Message)) abandoned++;
                    Audit(notification, row, CommAuditActions.NotificationFailed, row.Error);
                    notes.Add($"delivery {row.Id} ({row.Channel}): {ex.GetType().Name}");
                }
            }

            // ONE save for the whole batch, including the audit rows appended above — the same
            // one-SaveChanges-per-operation rule the writers follow.
            await _db.SaveAsync(cancellationToken);

            if (abandoned > 0)
                // Worth a warning, not a debug line: rows that hit the attempt cap are notifications nobody will
                // ever receive, and silently leaving them in the table is how a broken channel goes unnoticed.
                _log.LogWarning(
                    "{Abandoned} delivery row(s) reached the {Cap}-attempt cap and will not be retried.",
                    abandoned, _options.MaxDeliveryAttempts);

            return new CommDispatchReport
            {
                Claimed = claimed.Count,
                Delivered = delivered,
                Skipped = skipped,
                Failed = failed,
                Abandoned = abandoned,
                ByChannel = byChannel,
                Notes = notes,
            };
        }

        // ---------------------------------------------------------------------------------------------
        private static void Park(CommNotificationDelivery row, string status, string reason)
        {
            row.Status = status;
            row.Error = Cap(reason, 900);
            row.ClaimedAt = null;
            row.UpdatedAt = DateTime.UtcNow;
        }

        // Returns true when this attempt exhausted the cap, so the caller can count it as abandoned. The row
        // stays Failed rather than becoming a new status: an operator retrying by hand (raising the cap, or
        // resetting Attempts) is the intended recovery, exactly as the kernel's dispatch monitor allows.
        private bool MarkFailed(CommNotificationDelivery row, string reason)
        {
            row.Status = CommDeliveryStatus.Failed;
            row.Error = Cap(reason, 900);
            row.ClaimedAt = null;
            row.UpdatedAt = DateTime.UtcNow;
            return row.Attempts >= _options.MaxDeliveryAttempts;
        }

        private static CommChannelMessage BuildMessage(CommNotification notification, CommNotificationDelivery row)
            => new()
            {
                NotificationId = notification.Id,
                DeliveryId = row.Id,
                Channel = row.Channel,
                RecipientEmployeeId = notification.RecipientEmployeeId,
                CompanyId = notification.CompanyID,
                BranchId = notification.BranchID,
                ActorEmployeeId = notification.ActorEmployeeId,
                TemplateKey = notification.TemplateKey,
                Entity = new CommEntityRef(notification.EntityType, notification.EntityId),
                Rendered = new CommRenderedNotification
                {
                    TitleAr = notification.TitleAr,
                    TitleEn = notification.TitleEn,
                    BodyAr = notification.BodyAr,
                    BodyEn = notification.BodyEn,
                    Category = notification.Category,
                    Priority = notification.Priority,
                    LegacyNotificationType = notification.LegacyType,
                    Url = notification.Url,
                },

                // The row's own dedup key, handed to the downstream provider so a redelivered row cannot produce
                // a second email or push.
                DedupKey = row.DedupKey,
            };

        private void Audit(CommNotification notification, CommNotificationDelivery row, string action, string? error)
            => _audit.Append(new CommAuditRequest
            {
                Action = action,
                Entity = new CommEntityRef(notification.EntityType, notification.EntityId),
                CompanyId = notification.CompanyID,
                BranchId = notification.BranchID,
                ThreadId = notification.ThreadId,
                CommentId = notification.CommentId,
                NotificationId = notification.Id,

                // No ACTOR: delivery is machine work, not a person's act. SubjectEmployeeId carries who it was
                // for, which is the question an investigation actually asks.
                SubjectEmployeeId = notification.RecipientEmployeeId,

                Detail = new { channel = row.Channel, attempts = row.Attempts, error },

                // Attempt number in the key so each retry is its own audit row — otherwise the unique dedup
                // index would collapse five attempts into one and hide that four failed.
                DedupKey = $"delivery:{row.Id}:{action}:{row.Attempts}",
            });

        private static void Bump(Dictionary<string, int> counters, string key)
            => counters[key] = counters.TryGetValue(key, out var current) ? current + 1 : 1;

        private static string Cap(string value, int max)
            => string.IsNullOrEmpty(value) || value.Length <= max ? value : value.Substring(0, max);
    }
}
