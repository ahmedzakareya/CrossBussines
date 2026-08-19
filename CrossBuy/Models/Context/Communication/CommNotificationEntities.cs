namespace CrossBuy.Models.Context.Communication
{
    // =============================================================================================
    // Communication Platform (ADR-034) — notification fact, per-channel delivery state, preferences.
    // Company-isolation notes: see CommThreadEntities.cs.
    //
    // THE TWO-TABLE SHAPE IS THE POINT
    //
    // CommNotification is the DECISION ("Ahmed should be told he was mentioned in comment 91").
    // CommNotificationDelivery is the ATTEMPT, one row per channel.
    //
    // Collapsing them into one row with a Status column is the mistake this shape avoids: a notification
    // delivered in-app but failing over email has no single status, and the retry that fixes email must not
    // resend the in-app one. This is the same per-consumer split ADR-003 chose for BusinessEventDispatch,
    // for the same reason — and it is why the dispatcher can claim by Status alone, with no cursor.
    // =============================================================================================

    public class CommNotification : BaseEntity
    {
        public long Id { get; set; }
        public int CompanyID { get; set; }
        public int? BranchID { get; set; }

        public string TemplateKey { get; set; } = "";     // CommTemplateKeys
        public string Category { get; set; } = "";        // CommNotificationCategories
        public string Priority { get; set; } = "";

        // NotificationTypes catalog key, carried so an in-app render reuses the icon/priority the rest of the
        // product already shows for this kind of thing.
        public string LegacyType { get; set; } = "";

        public string EntityType { get; set; } = "";
        public int EntityId { get; set; }
        public long? ThreadId { get; set; }
        public long? CommentId { get; set; }
        public long? MentionId { get; set; }

        public int RecipientEmployeeId { get; set; }
        public int? ActorEmployeeId { get; set; }

        // Rendered at DECIDE time, both languages, and STORED. Re-rendering on read would show today's
        // template text for a notification sent last year, and would need the token values kept alive
        // forever to do it.
        public string TitleAr { get; set; } = "";
        public string TitleEn { get; set; } = "";
        public string BodyAr { get; set; } = "";
        public string BodyEn { get; set; } = "";
        public string? Url { get; set; }

        // TRUE idempotency, unique per company: (DedupKeyPrefix + recipient). Not the "unread-noise guard"
        // the legacy NotificationService.dedupKey provides — a retried transaction here produces zero extra
        // rows, whether or not the first one has been read.
        public string DedupKey { get; set; } = "";

        public DateTime? ReadAt { get; set; }
    }

    // One delivery attempt of one notification over one channel.
    //
    // Claimed exists so a worker can take a row ATOMICALLY; it is reclaimed after StaleClaimMinutes if the
    // worker died holding it. Identical semantics to BusinessEventDispatchStatus.Claimed, deliberately, so an
    // operator who can read one queue can read this one.
    public class CommNotificationDelivery : BaseEntity
    {
        public long Id { get; set; }
        public int CompanyID { get; set; }
        public long NotificationId { get; set; }

        public string Channel { get; set; } = "";         // CommChannel
        public string Status { get; set; } = "";          // CommDeliveryStatus

        public int Attempts { get; set; }
        public DateTime? ClaimedAt { get; set; }
        public DateTime? SentAt { get; set; }
        public string? Error { get; set; }

        // The adapter's own reference (provider message id), for tracing a delivery outside this system.
        public string? ExternalReference { get; set; }

        // Deterministic per (notification, channel). Handed to the downstream provider so a redelivered row
        // cannot produce a second email or push.
        public string DedupKey { get; set; } = "";
    }

    // Per-employee delivery preference (module 22).
    //
    // Keyed (CompanyID, EmployeeId, Category, Channel). ABSENCE OF A ROW IS NOT "OFF" — it is "use the
    // deployment default", which ICommPreferenceResolver applies. That distinction is why a new channel
    // switched on by an operator reaches people who never opened the preferences screen, instead of reaching
    // nobody and looking broken.
    public class CommNotificationPreference : BaseEntity
    {
        public long Id { get; set; }
        public int CompanyID { get; set; }
        public int EmployeeId { get; set; }

        public string Category { get; set; } = "";        // CommNotificationCategories
        public string Channel { get; set; } = "";         // CommChannel
        public string Mode { get; set; } = "";            // CommPreferenceMode
    }
}