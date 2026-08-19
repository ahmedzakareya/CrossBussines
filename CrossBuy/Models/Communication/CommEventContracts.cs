namespace CrossBuy.Models.Communication
{
    // =============================================================================================
    // Communication Platform (ADR-030 §7) — COMMUNICATION EVENTS (module 26) and the shape the platform
    // kernel bridge consumes (module 27).
    //
    // WHY A SECOND EVENT TYPE EXISTS AT ALL
    //
    // The obvious design is "just call IBusinessEventService". It was rejected, and the reason is recorded
    // here rather than rediscovered:
    //
    //   1. RecordAsync THROWS with no swallowing catch when there is no ambient transaction, and throws
    //      SQL-208 when BusinessEvents is not deployed. Making every comment depend on the kernel's schema
    //      would mean a missing platform slice breaks commenting on every screen in the product.
    //   2. Its event-name grammar is "<RegisteredEntityCode>.<Action>". A communication fact is not about a
    //      registered entity — it is about a comment, which is not (and should not be) a registry code.
    //      "Comment.Added" would fail BusinessEventTypes.Validate against the anchor entity.
    //   3. CLAUDE.md records a standing decision, taken in another track for the same reason, not to bind
    //      new work to the kernel's queue while it is uncommitted.
    //
    // So: this platform ALWAYS records its own event (durably, in the caller's transaction, to CommAuditEntry
    // via ICommAuditWriter) and OPTIONALLY forwards a translated copy to the kernel when a deployment turns
    // the bridge on. The translation is lossy in one direction only — narrowing — and is spelled out below.
    // =============================================================================================

    // ---------------------------------------------------------------------------------------------
    // Event names. Grammar: "<Aggregate>.<Action>", Aggregate PascalCase, Action PascalCase.
    //
    // These deliberately do NOT look like kernel event types (whose prefix must be a registered entity
    // code), so a name from one vocabulary can never be mistaken for the other.
    // ---------------------------------------------------------------------------------------------
    public static class CommEventTypes
    {
        public const string ThreadCreated = "Thread.Created";
        public const string ThreadLocked = "Thread.Locked";
        public const string ThreadUnlocked = "Thread.Unlocked";

        public const string CommentAdded = "Comment.Added";
        public const string CommentEdited = "Comment.Edited";
        public const string CommentDeleted = "Comment.Deleted";
        public const string CommentRestored = "Comment.Restored";

        public const string MentionCreated = "Mention.Created";

        public const string ReactionAdded = "Reaction.Added";
        public const string ReactionRemoved = "Reaction.Removed";

        public const string AttachmentAdded = "Attachment.Added";
        public const string AttachmentRemoved = "Attachment.Removed";

        public const string ParticipantAdded = "Participant.Added";
        public const string ParticipantRemoved = "Participant.Removed";

        public const string PermissionGranted = "Permission.Granted";
        public const string PermissionRevoked = "Permission.Revoked";

        public const string NotificationQueued = "Notification.Queued";

        private static readonly HashSet<string> All = new(StringComparer.Ordinal)
        {
            ThreadCreated, ThreadLocked, ThreadUnlocked,
            CommentAdded, CommentEdited, CommentDeleted, CommentRestored,
            MentionCreated, ReactionAdded, ReactionRemoved,
            AttachmentAdded, AttachmentRemoved,
            ParticipantAdded, ParticipantRemoved,
            PermissionGranted, PermissionRevoked,
            NotificationQueued,
        };

        public static bool IsValid(string? value) => value != null && All.Contains(value);
        public static IReadOnlyCollection<string> Values => All;

        // ---- the kernel translation table (module 27) ------------------------------------------------
        //
        // Maps a communication event to the ACTION half of a kernel event type. The kernel type is then
        // "<AnchorEntityCode>." + action, which satisfies BusinessEventTypes: PascalCase, single dot,
        // prefix equal to the entity the event is recorded against.
        //
        // A null means NOT BRIDGEABLE — deliberately, not by omission:
        //   Reaction.*      a reaction is a signal, not a business fact; bridging it would put thousands of
        //                   rows a day into the table BusinessEventService itself calls "destined to be the
        //                   largest table in the database".
        //   Participant.*   following a record is a personal preference, not a fact about the record.
        //   Permission.*    a thread grant is a security artefact. EntityRegistry's own PlatformRoleAssignment
        //                   comment sets the precedent: security artefacts are read through the audit path,
        //                   not rendered on a record timeline.
        //   Notification.*  a delivery is an outcome of an event, never itself an event — bridging it would
        //                   create a feedback loop through the notification projection consumer.
        public static string? KernelActionFor(string? commEventType) => commEventType switch
        {
            CommentAdded => "Commented",
            CommentEdited => "CommentEdited",
            CommentDeleted => "CommentDeleted",
            CommentRestored => "CommentRestored",
            MentionCreated => "Mentioned",
            AttachmentAdded => "AttachmentAdded",
            AttachmentRemoved => "AttachmentRemoved",
            ThreadCreated => "ThreadOpened",
            ThreadLocked => "ThreadLocked",
            ThreadUnlocked => "ThreadUnlocked",
            _ => null,
        };

        public static bool IsBridgeable(string? commEventType) => KernelActionFor(commEventType) != null;
    }

    // ---------------------------------------------------------------------------------------------
    // The fact itself. Immutable, serializable, and free of side effects — the same discipline ADR-001
    // imposes on BusinessEventRecord, for the same reason: it is created inside a business transaction.
    // ---------------------------------------------------------------------------------------------
    public sealed class CommEvent
    {
        public required string EventType { get; init; }            // CommEventTypes
        public required CommEntityRef Entity { get; init; }        // the ANCHOR business record

        public long? ThreadId { get; init; }
        public long? CommentId { get; init; }
        public long? MentionId { get; init; }

        public required int CompanyId { get; init; }
        public int? BranchId { get; init; }
        public int? ActorEmployeeId { get; init; }

        // The communication visibility of the subject (thread/comment). Translated to the kernel's tier by
        // CommVisibility.ToBusinessEventVisibility when bridged.
        public string Visibility { get; init; } = CommVisibility.Internal;

        // Small, JSON-serializable summary. NEVER the comment body: a body can be 32 KB of authored text
        // including a customer's personal data, and an event payload is a summary by contract
        // (PKS-001 payload rules, which the kernel enforces at 64 KB and this platform respects at source).
        public object? Payload { get; init; }

        public int PayloadVersion { get; init; } = 1;

        // Idempotency, scoped per company. Set by the producing service from the row identity, so a retried
        // operation records one event.
        public string? DedupKey { get; init; }

        public Guid? CorrelationId { get; init; }

        public DateTime OccurredAt { get; init; } = DateTime.UtcNow;
    }

    // ---------------------------------------------------------------------------------------------
    // Event payloads. One per family, versioned, and containing IDs and COUNTS rather than content.
    // ---------------------------------------------------------------------------------------------
    public sealed class CommCommentEventPayload
    {
        public long ThreadId { get; init; }
        public long CommentId { get; init; }
        public long? ParentCommentId { get; init; }
        public string? ThreadKind { get; init; }
        public string? Visibility { get; init; }
        public string? BodyFormat { get; init; }
        public int BodyBytes { get; init; }
        public int MentionCount { get; init; }
        public int AttachmentCount { get; init; }
        public int RevisionNo { get; init; }

        // Which FIELDS changed on an edit — never their values. "Body changed" is a fact; the old body is
        // content, and it already lives in CommCommentRevisions where the visibility rules apply to it.
        public string[]? ChangedFields { get; init; }
    }

    public sealed class CommMentionEventPayload
    {
        public long ThreadId { get; init; }
        public long CommentId { get; init; }
        public long MentionId { get; init; }
        public string? TargetKind { get; init; }
        public int? TargetId { get; init; }
        public string? TargetKey { get; init; }
        public int ResolvedRecipientCount { get; init; }
    }

    public sealed class CommThreadEventPayload
    {
        public long ThreadId { get; init; }
        public string? Kind { get; init; }
        public string? ThreadKey { get; init; }
        public string? Visibility { get; init; }
        public string? Reason { get; init; }
    }

    public sealed class CommAttachmentEventPayload
    {
        public long ThreadId { get; init; }
        public long CommentId { get; init; }
        public long AttachmentId { get; init; }
        public string? ContentType { get; init; }
        public long SizeBytes { get; init; }
        public string? PreviewKind { get; init; }

        // FileName is present because it is what an operator needs to recognise the row in an audit trail.
        // StorageKey is NOT: it is a capability-bearing handle into the file store, and an event log is read
        // by more people than the file store is.
        public string? FileName { get; init; }
    }

    public sealed class CommParticipantEventPayload
    {
        public long ThreadId { get; init; }
        public int EmployeeId { get; init; }
        public string? Role { get; init; }
        public string? Source { get; init; }
    }

    public sealed class CommNotificationEventPayload
    {
        public long NotificationId { get; init; }
        public long? ThreadId { get; init; }
        public long? CommentId { get; init; }
        public string? TemplateKey { get; init; }
        public int RecipientEmployeeId { get; init; }
        public string[]? Channels { get; init; }
    }
}