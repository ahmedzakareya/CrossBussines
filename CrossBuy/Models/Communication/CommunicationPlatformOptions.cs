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

        // D-05: missing entity policy permits staff-only authoring, never public disclosure.
        public Dictionary<string, CommEntityPrivacyPolicy> EntityPrivacyPolicies { get; set; }
            = new(StringComparer.Ordinal)
            {
                // Existing Wave 3B-C external authority explicitly supports Quotation only.
                ["Quotation"] = new()
                {
                    MaximumVisibility = CommVisibility.Public,
                    ExternalPolicyReference = "CommExternalAccessPolicy.Quotation"
                }
            };

        // Null durations retain history until an explicit policy is approved; no erasure runs here.
        public Dictionary<string, CommEntityGovernancePolicy> EntityGovernancePolicies { get; set; }
            = new(StringComparer.Ordinal);

        public CommEntityGovernancePolicy GovernanceFor(string entityCode)
        {
            EntityGovernancePolicies.TryGetValue(entityCode, out var policy);
            policy ??= new CommEntityGovernancePolicy();
            policy.Validate();
            return policy;
        }

        // Backend registrations, not configuration: a string in appsettings cannot invent an external authority.
        private readonly Dictionary<string, string> _externalPolicyReferences = new(StringComparer.Ordinal)
        { ["Quotation"] = "CommExternalAccessPolicy.Quotation" };

        public CommunicationPlatformOptions RegisterExternalPolicy(string entityCode, string reference)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(entityCode);
            ArgumentException.ThrowIfNullOrWhiteSpace(reference);
            _externalPolicyReferences[entityCode] = reference;
            return this;
        }

        public bool MayAuthorVisibility(string entityCode, string visibility)
        {
            if (!CommVisibility.IsValid(visibility)) return false;
            EntityPrivacyPolicies.TryGetValue(entityCode, out var policy);
            string ceiling = policy?.MaximumVisibility ?? CommVisibility.Internal;
            if (!CommVisibility.IsValid(ceiling)) return false;
            if (ceiling == CommVisibility.Public)
            {
                // These records remain internal even when a deployment declares an external policy.
                if (entityCode is "Employee" or "JournalEntry") return visibility != CommVisibility.Public;
                if (string.IsNullOrWhiteSpace(policy?.ExternalPolicyReference) ||
                    !_externalPolicyReferences.TryGetValue(entityCode, out var registeredReference) ||
                    !string.Equals(registeredReference, policy.ExternalPolicyReference, StringComparison.Ordinal)) return false;
            }
            return CommVisibility.Rank(visibility) >= CommVisibility.Rank(ceiling);
        }

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

        public bool RetireLegacyDocumentCommentWrites { get; set; } = false;

        public bool NotificationWorkerEnabled { get; set; } = false;
        public List<int> NotificationWorkerCompanyIds { get; set; } = new();
        public int NotificationPollSeconds { get; set; } = 30;

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

        // What a bridge failure does to the user's operation. See CommBridgeFailureMode.
        //
        // Isolate is the DEFAULT, deliberately. The bridge itself is off by default, so no existing
        // deployment changes behaviour — but the first deployment to switch it on gets the safe shape
        // instead of the HM-D44 one, which is the opposite of how that coupling reached production.
        public string BridgeFailureMode { get; set; } = CommBridgeFailureMode.Isolate;

        // Re-offer bridgeable events the inline bridge could not deliver. OFF by default and, like the
        // notification worker, refuses to start without an explicit positive company allow-list.
        public bool BridgeReplayEnabled { get; set; } = false;
        public List<int> BridgeReplayCompanyIds { get; set; } = new();

        // How far back one replay pass looks. Bounded because this is a WINDOW SCAN, not a queue: the
        // precise form is a nullable BridgedAt column on CommAuditEntries, which is a migration and is
        // therefore recorded for the owner rather than taken here.
        public int BridgeReplayLookbackHours { get; set; } = 24;
        public int BridgeReplayPollSeconds { get; set; } = 60;

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