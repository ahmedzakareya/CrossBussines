namespace CrossBuy.Models.Communication
{
    // =============================================================================================
    // Communication Platform (ADR-034) — NOTIFICATION contracts (modules 13, 16-22).
    //
    // THE SPLIT THIS FILE ENFORCES
    //
    // Three concerns that are usually one tangled method are kept apart, because the platform kernel already
    // proved the value of the split in ADR-006: its BusinessEventNotificationMapper is "deliberately a pure
    // function of the envelope: no database, no permissions, no idempotency, no delivery".
    //
    //   DECIDE   — which template, which audience, which tokens.        (CommNotificationRequest)
    //   RESOLVE  — who, may they, do they want it, on which channels.   (ICommPreferenceResolver + policy)
    //   RENDER   — the text, per culture.                              (ICommTemplateRenderer)
    //   DELIVER  — one row per (notification, channel), retryable.     (ICommNotificationDispatcher)
    //
    // A caller only ever supplies the first. Everything else is the platform's, which is why a new
    // notification never comes with new permission code or new retry code.
    // =============================================================================================

    // ---------------------------------------------------------------------------------------------
    // TEMPLATES (module 16)
    //
    // A template is CODE-DECLARED, not a database row. That is a decision, not laziness:
    //   * A row lets an operator delete a template a producer still references, turning a notification into
    //     a silent no-op at runtime. A frozen key cannot be deleted by accident.
    //   * The token list is a compile-time contract. A producer that forgets a token fails a test, not a
    //     customer's inbox.
    //   * The TEXT is still overridable per deployment/culture — see ICommTemplateTextProvider, which reads
    //     resource keys first and falls back to the built-in bilingual default.
    // ---------------------------------------------------------------------------------------------
    public sealed class CommTemplateDefinition
    {
        public required string Key { get; init; }

        // Notification category, reused as the PREFERENCE key (module 22): a recipient opts out of
        // "Mentions" rather than of one template. Keeping them the same string is what makes a new template
        // inherit an existing preference instead of arriving un-opted-out.
        public required string Category { get; init; }

        // NotificationTypes catalog key, so a bridged/in-app notification renders with the icon and priority
        // the rest of the product already uses for that kind of thing. Reused, not replaced.
        public required string LegacyNotificationType { get; init; }

        // Channels this template MAY use. Intersected with the deployment's EnabledChannels and with the
        // recipient's preference — never a union, so a template can only ever narrow.
        public required string[] DefaultChannels { get; init; }

        // Tokens the renderer requires. A request missing one is rejected at DECIDE time with
        // template_token_missing, rather than rendering "{author} mentioned you" literally.
        public required string[] RequiredTokens { get; init; }

        public required string DefaultTitleAr { get; init; }
        public required string DefaultTitleEn { get; init; }
        public required string DefaultBodyAr { get; init; }
        public required string DefaultBodyEn { get; init; }

        // Resource key PREFIX for a deployment that wants localized text. The provider looks for
        // "<prefix>.TitleAr" etc. and falls back to the defaults above when the key is absent — so no resx
        // edit is required to ship, and adding one later changes nothing but the words.
        public string? ResourceKeyPrefix { get; init; }

        public string Priority { get; init; } = "Normal";
    }

    // The frozen catalogue. Adding a template = adding a constant + a definition + a test.
    public static class CommTemplateKeys
    {
        public const string MentionedInComment = "comm.mentioned_in_comment";
        public const string CommentOnFollowedThread = "comm.comment_on_followed_thread";
        public const string ReplyToMyComment = "comm.reply_to_my_comment";
        public const string ReactionOnMyComment = "comm.reaction_on_my_comment";
        public const string AddedAsParticipant = "comm.added_as_participant";
        public const string ThreadLocked = "comm.thread_locked";
        public const string MyCommentDeleted = "comm.my_comment_deleted";

        private static readonly HashSet<string> All = new(StringComparer.Ordinal)
        {
            MentionedInComment, CommentOnFollowedThread, ReplyToMyComment,
            ReactionOnMyComment, AddedAsParticipant, ThreadLocked, MyCommentDeleted,
        };

        public static bool IsValid(string? value) => value != null && All.Contains(value);
        public static IReadOnlyCollection<string> Values => All;
    }

    // Preference categories (module 22). Deliberately COARSE: a recipient who has to make twenty decisions
    // makes none, and a per-template opt-out means every new template silently arrives switched on.
    public static class CommNotificationCategories
    {
        public const string Mentions = "Mentions";
        public const string Comments = "Comments";
        public const string Reactions = "Reactions";
        public const string Participation = "Participation";
        public const string Moderation = "Moderation";

        private static readonly HashSet<string> All =
            new(StringComparer.Ordinal) { Mentions, Comments, Reactions, Participation, Moderation };

        public static bool IsValid(string? value) => value != null && All.Contains(value);
        public static IReadOnlyCollection<string> Values => All;
    }

    // Token names a request may supply. Frozen so a template and a producer cannot disagree about spelling.
    public static class CommTemplateTokens
    {
        public const string ActorName = "actor";
        public const string EntityLabel = "entity";
        public const string EntityKind = "entityKind";
        public const string Excerpt = "excerpt";
        public const string ThreadSubject = "thread";
        public const string ReactionKey = "reaction";
        public const string MentionTargetLabel = "target";
        public const string Reason = "reason";
    }

    // ---------------------------------------------------------------------------------------------
    // DECIDE — what a producing service asks for.
    // ---------------------------------------------------------------------------------------------
    public sealed class CommNotificationRequest
    {
        public required string TemplateKey { get; init; }
        public required CommEntityRef Entity { get; init; }

        public long? ThreadId { get; init; }
        public long? CommentId { get; init; }
        public long? MentionId { get; init; }

        public required int CompanyId { get; init; }
        public int? BranchId { get; init; }
        public int? ActorEmployeeId { get; init; }

        // Explicit recipients. This platform has NO role-audience targeting, and that is deliberate: the
        // legacy NotifyRoleAsync only understands two scopes ("acc"/"inv"), and a collaboration notification
        // is always addressed to PEOPLE (a mention target, a follower, an author) — never to a job function.
        public required IReadOnlyList<int> RecipientEmployeeIds { get; init; }

        // The visibility of the SUBJECT. A recipient who may not read the comment is dropped before a
        // notification row exists — a notification is a read of the content by another name, and ADR-006's
        // consumer authorizes recipients for exactly this reason.
        public string SubjectVisibility { get; init; } = CommVisibility.Internal;

        public required IReadOnlyDictionary<string, string> Tokens { get; init; }

        // Idempotency root, per company. Derived by the producer from the row identity (comment id, mention
        // id), so a retried transaction produces one notification per recipient — the same construction
        // NotificationCommand.DedupKeyPrefix uses off EventUid.
        public required string DedupKeyPrefix { get; init; }

        // Overrides the template's channel set, still intersected with deployment + preference.
        public IReadOnlyList<string>? Channels { get; init; }
    }

    public sealed class CommNotificationResult
    {
        public required int RequestedRecipientCount { get; init; }
        public required int NotifiedRecipientCount { get; init; }
        public required int DeliveryRowCount { get; init; }
        public required IReadOnlyList<long> NotificationIds { get; init; }

        // Why recipients were dropped, counted by reason. Returned rather than logged-and-forgotten because
        // "the mention notified nobody" is a support question, and the answer is here.
        public required IReadOnlyDictionary<string, int> SkippedByReason { get; init; }

        public static class SkipReasons
        {
            public const string ActorSelf = "actor_self";
            public const string NotVisible = "not_visible";
            public const string PreferenceOff = "preference_off";
            public const string Muted = "muted";
            public const string AlreadyNotified = "already_notified";
            public const string NoChannel = "no_channel";
            public const string InactiveEmployee = "inactive_employee";
        }

        public static CommNotificationResult Empty(int requested, string reason) => new()
        {
            RequestedRecipientCount = requested,
            NotifiedRecipientCount = 0,
            DeliveryRowCount = 0,
            NotificationIds = Array.Empty<long>(),
            SkippedByReason = new Dictionary<string, int>(StringComparer.Ordinal) { [reason] = requested },
        };
    }

    // ---------------------------------------------------------------------------------------------
    // RENDER
    // ---------------------------------------------------------------------------------------------
    public sealed class CommRenderedNotification
    {
        public required string TitleAr { get; init; }
        public required string TitleEn { get; init; }
        public required string BodyAr { get; init; }
        public required string BodyEn { get; init; }
        public required string Category { get; init; }
        public required string Priority { get; init; }
        public required string LegacyNotificationType { get; init; }

        // Deep link to the anchor record, resolved through IEntityRegistry.BuildUrl. Null when the entity
        // has no screen — a notification with no destination is still worth sending, it just does not click.
        public string? Url { get; init; }
    }

    // ---------------------------------------------------------------------------------------------
    // DELIVER — the channel contract (modules 18, 19, 20, 21).
    // ---------------------------------------------------------------------------------------------
    public sealed class CommChannelMessage
    {
        public required long NotificationId { get; init; }
        public required long DeliveryId { get; init; }
        public required string Channel { get; init; }
        public required int RecipientEmployeeId { get; init; }
        public required int CompanyId { get; init; }
        public int? BranchId { get; init; }
        public int? ActorEmployeeId { get; init; }
        public required string TemplateKey { get; init; }
        public required CommEntityRef Entity { get; init; }
        public required CommRenderedNotification Rendered { get; init; }

        // Deterministic per (notification, channel). A channel adapter passes it to whatever downstream
        // system it talks to, so a redelivered row cannot produce a second email/push.
        public required string DedupKey { get; init; }
    }

    public sealed class CommChannelResult
    {
        public required bool Delivered { get; init; }

        // True => park the row as Skipped, never retry. Distinct from a failure: "the recipient has no
        // push token" and "the push service timed out" must not share a status, or a permanent condition
        // burns five retries every time.
        public required bool Skipped { get; init; }

        public string? Reason { get; init; }

        // Adapter's own handle (message id, provider reference) for operator traceability.
        public string? ExternalReference { get; init; }

        public static CommChannelResult Ok(string? externalReference = null) =>
            new() { Delivered = true, Skipped = false, ExternalReference = externalReference };

        public static CommChannelResult Skip(string reason) =>
            new() { Delivered = false, Skipped = true, Reason = reason };

        public static CommChannelResult Fail(string reason) =>
            new() { Delivered = false, Skipped = false, Reason = reason };
    }

    // ---------------------------------------------------------------------------------------------
    // PUSH (module 21) — CONTRACTS ONLY, as the requirement asks.
    //
    // No implementation, no token table, no registration endpoint. What is frozen here is the SHAPE a push
    // adapter will receive, so the adapter is the only thing a future slice has to write. ICommPushTokenStore
    // has no implementation registered anywhere; a push channel resolving no tokens returns Skip, which is
    // why an unwired push channel is harmless rather than a queue of Failed rows.
    // ---------------------------------------------------------------------------------------------
    public sealed class CommPushPayload
    {
        public required string Title { get; init; }
        public required string Body { get; init; }
        public string? Url { get; init; }
        public string? Icon { get; init; }

        // Collapse identity: a device shows one "3 new comments" rather than three notifications.
        public string? CollapseKey { get; init; }

        // Opaque routing data for the client app. Small by contract — a push payload has a hard size limit
        // on every platform, so this carries ids, never content.
        public IReadOnlyDictionary<string, string>? Data { get; init; }
    }

    public sealed class CommPushToken
    {
        public required int EmployeeId { get; init; }
        public required string Platform { get; init; }        // "web" | "android" | "ios"
        public required string Token { get; init; }
        public DateTime? RegisteredAt { get; init; }
        public DateTime? LastSeenAt { get; init; }
    }

    // ---------------------------------------------------------------------------------------------
    // Preferences (module 22)
    // ---------------------------------------------------------------------------------------------
    public sealed class CommPreferenceDto
    {
        public required int EmployeeId { get; init; }
        public required string Category { get; init; }
        public required string Channel { get; init; }
        public required string Mode { get; init; }

        // True when no stored row exists and this is the deployment default. Surfaced so a preferences
        // screen can show "inherited" rather than pretending the recipient chose it.
        public required bool IsDefault { get; init; }
    }

    public sealed class CommPreferenceUpdateRequest
    {
        public required int EmployeeId { get; init; }
        public required string Category { get; init; }
        public required string Channel { get; init; }
        public required string Mode { get; init; }
    }

    // The resolved answer for ONE (recipient, category) pair: which channels to create delivery rows for.
    public sealed class CommChannelPlan
    {
        public required int EmployeeId { get; init; }
        public required IReadOnlyList<string> Channels { get; init; }
        public required IReadOnlyDictionary<string, string> ExcludedChannels { get; init; }

        public bool HasAnyChannel => Channels.Count > 0;
    }

    // ---------------------------------------------------------------------------------------------
    // In-app inbox (module 19).
    //
    // This platform keeps its OWN inbox read model over CommNotifications rather than writing into the
    // product's Notifications table. Reason: that table is written by the kernel's NotificationProjection
    // consumer and by twelve legacy producers, and a second writer with a different dedup convention is
    // exactly the "second notification path bypasses the outbox" problem CLAUDE.md already records as an
    // open conflict (HM-D46). A bridge to the legacy table is an extension point, unregistered by default.
    // ---------------------------------------------------------------------------------------------
    public sealed class CommInboxItemDto
    {
        public required long NotificationId { get; init; }
        public required string TemplateKey { get; init; }
        public required string Category { get; init; }
        public required string Priority { get; init; }
        public required CommEntityRef Entity { get; init; }
        public long? ThreadId { get; init; }
        public long? CommentId { get; init; }
        public required string TitleAr { get; init; }
        public required string TitleEn { get; init; }
        public required string BodyAr { get; init; }
        public required string BodyEn { get; init; }
        public string? Url { get; init; }
        public CommActorDto? Actor { get; init; }
        public DateTime? CreatedAt { get; init; }
        public DateTime? ReadAt { get; init; }
        public bool IsRead => ReadAt.HasValue;
    }
}