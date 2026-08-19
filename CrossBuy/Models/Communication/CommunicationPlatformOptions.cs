namespace CrossBuy.Models.Communication
{
    // =============================================================================================
    // Communication Platform (ADR-030 §10) — the platform's configuration surface.
    //
    // Bound from configuration section "CommunicationPlatform". Every default below is the CLOSED one:
    // no entity is onboarded, no channel sends, no event is bridged to the platform kernel. A deployment
    // that adds the section gets a working collaboration layer; a deployment that does not is byte-for-byte
    // unchanged. That is what makes this phase safe to land next to two other active work streams.
    // =============================================================================================
    public sealed class CommunicationPlatformOptions
    {
        public const string SectionName = "CommunicationPlatform";

        // ---- the collaboration surface (ADR-031) ----------------------------------------------------

        // Registry codes this platform accepts comments/threads on, IN ADDITION to any code whose
        // IEntityRegistry definition already says SupportsComments.
        //
        // Why an additive option instead of editing the registry: EntityRegistry is platform-kernel code
        // owned by another work stream, and its capability flags describe "what is WIRED today". Flipping
        // SupportsComments on eight entities from here would be an uncoordinated edit to a file CLAUDE.md
        // names an architectural invariant. Configuration lets a deployment onboard an entity now and the
        // registry catch up when its owner is ready — with ICommEntitySurface reporting the difference.
        public List<string> EnabledEntityCodes { get; set; } = new();

        // Codes explicitly refused even if the registry says SupportsComments. Deny always wins.
        // Exists so a deployment can pull one entity out of the collaboration surface without a code change
        // (e.g. an entity whose screen was found to leak a confidential note in a printed document).
        public List<string> BlockedEntityCodes { get; set; } = new();

        // ---- authoring limits (ADR-030 §9) ---------------------------------------------------------

        // Bytes of UTF-8, not characters — the storage limit is bytes and an Arabic body is ~2 bytes per
        // character, so a character cap would silently halve the real limit for Arabic authors.
        public int MaxBodyBytes { get; set; } = 32 * 1024;

        public int MaxMentionsPerComment { get; set; } = 25;
        public int MaxAttachmentsPerComment { get; set; } = 10;
        public long MaxAttachmentBytes { get; set; } = 25L * 1024 * 1024;

        // How deep a reply chain may nest. 1 = flat, 2 = a reply to a comment, 3 = a reply to a reply.
        // Bounded because an unbounded chain makes the thread read query unbounded too.
        public int MaxReplyDepth { get; set; } = 3;

        // Minutes after posting during which the author may still edit without a moderator right.
        // 0 disables author self-edit entirely; null means always allowed.
        public int? AuthorEditWindowMinutes { get; set; } = 60 * 24;

        // ---- mentions (ADR-032) --------------------------------------------------------------------

        // Team and Department mentions fan out to whole org subtrees, so they are opt-in per deployment.
        public bool EnableTeamMentions { get; set; } = true;
        public bool EnableDepartmentMentions { get; set; } = true;

        // Declared-not-wired. Setting it true does NOT make role mentions work — no target provider exists
        // (ADR-032 §5). It is here so the switch has a name before the provider lands.
        public bool EnableRoleMentions { get; set; } = false;

        // A group mention that would resolve to more than this many recipients is refused rather than
        // silently truncated. Truncation is the worse failure: the author believes the department was
        // notified and half of it was not.
        public int MaxGroupMentionRecipients { get; set; } = 200;

        // ---- notifications (ADR-034) ---------------------------------------------------------------

        // Channels a delivery row may be created for. A channel NOT listed here produces no row at all;
        // a channel listed here with no registered adapter produces a row parked as Skipped.
        public List<string> EnabledChannels { get; set; } = new() { CommChannel.InApp };

        // Default mode when a recipient has expressed no preference for a category.
        public string DefaultPreferenceMode { get; set; } = CommPreferenceMode.Immediate;

        // Delivery attempts before a Failed row stops being retried. Mirrors BusinessEventDispatchOptions.
        public int MaxDeliveryAttempts { get; set; } = 5;

        // A Claimed row whose claim is older than this is considered abandoned by a dead worker and may be
        // reclaimed. Same reasoning as the kernel's StaleClaimMinutes.
        public int StaleClaimMinutes { get; set; } = 10;

        public int DispatchBatchSize { get; set; } = 50;

        // Never notify the actor about their own action. Overridable because a "your comment was posted"
        // confirmation is a legitimate product choice on a slow connection.
        public bool ExcludeActorFromOwnNotifications { get; set; } = true;

        // ---- platform kernel bridge (ADR-030 §7) ---------------------------------------------------

        // OFF BY DEFAULT, and that default is a decision with a paper trail.
        //
        // Forwarding a communication event to IBusinessEventService binds this platform to the kernel's
        // outbox, dispatcher and consumers — and to a schema (BusinessEvents / BusinessEventDispatch) whose
        // absence makes RecordAsync throw with no swallowing catch, failing the whole caller transaction.
        // CLAUDE.md records the same reasoning for the hypermarket track's standing decision not to raise
        // platform events yet. Turning this on is a deployment decision that must be taken WITH the kernel
        // owner, after confirming slice-1 and slice-2 SQL are applied.
        public bool BridgeToBusinessEvents { get; set; } = false;

        // Only these communication event types are bridged when the bridge is on. Empty = all bridgeable
        // types. Exists because "a comment was added" is worth a timeline entry while "a reaction was
        // removed" is almost certainly not, and the kernel event log is destined to be the largest table
        // in the database (BusinessEventService's own payload comment).
        public List<string> BridgedEventTypes { get; set; } = new();

        // ---- read paths -----------------------------------------------------------------------------

        public int DefaultPageSize { get; set; } = 50;
        public int MaxPageSize { get; set; } = 200;

        // Clamp helper so every read path applies the same bound instead of each one inventing its own.
        public int ClampPageSize(int? requested)
        {
            int size = requested is > 0 ? requested.Value : DefaultPageSize;
            return Math.Clamp(size, 1, MaxPageSize > 0 ? MaxPageSize : 200);
        }
    }
}