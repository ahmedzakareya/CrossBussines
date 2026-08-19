namespace CrossBuy.Models.Communication
{
    // =============================================================================================
    // Communication Platform (ADR-030 §11) — the failure vocabulary.
    //
    // Four distinct types, because a controller has to answer four different HTTP statuses and a single
    // InvalidOperationException forces it to guess:
    //   CommEntityNotSupportedException  -> 400  (the caller asked about an entity this platform does not carry)
    //   CommValidationException          -> 400  (the caller's content or arguments are wrong)
    //   CommAccessDeniedException        -> 403  (the caller may not do this, and WHY is recorded)
    //   CommNotFoundException            -> 404  (or 403-as-404 for a cross-company row — see below)
    //
    // This mirrors DocCommentEntityTypeException's own reasoning: "Distinct from a generic argument error so
    // the controller can answer 400 rather than 500."
    // =============================================================================================

    // The anchor entity is not part of the collaboration surface: unregistered code, registry says the
    // capability is off, or the deployment blocked it. Never a 500 — a screen asking about an entity that
    // was never onboarded is a wiring mistake, not a server fault.
    public sealed class CommEntityNotSupportedException : InvalidOperationException
    {
        public CommEntityNotSupportedException(string? entityCode, string capability, string reason)
            : base($"Entity '{entityCode ?? "(null)"}' does not support communication capability '{capability}': {reason} " +
                   "Onboard it by registering the code in IEntityRegistry and adding it to " +
                   "CommunicationPlatform:EnabledEntityCodes.")
        { EntityCode = entityCode; Capability = capability; Reason = reason; }

        public string? EntityCode { get; }
        public string Capability { get; }
        public string Reason { get; }
    }

    // Caller-supplied content or arguments are invalid. Carries a machine Code so a UI can localize the
    // message itself instead of displaying a server-composed English string — the same reason the parallel
    // team's feature modules return machine error codes like "title_required".
    public sealed class CommValidationException : ArgumentException
    {
        public CommValidationException(string code, string message) : base(message)
        { Code = code; }

        public string Code { get; }

        // Frozen machine codes. A UI maps these to its own resx keys.
        public static class Codes
        {
            public const string BodyRequired = "body_required";
            public const string BodyTooLarge = "body_too_large";
            public const string BodyFormatInvalid = "body_format_invalid";
            public const string VisibilityInvalid = "visibility_invalid";
            public const string ThreadKindInvalid = "thread_kind_invalid";
            public const string ThreadLocked = "thread_locked";
            public const string ReplyDepthExceeded = "reply_depth_exceeded";
            public const string ReplyParentMismatch = "reply_parent_mismatch";
            public const string TooManyMentions = "too_many_mentions";
            public const string MentionKindUnsupported = "mention_kind_unsupported";
            public const string MentionTargetUnresolved = "mention_target_unresolved";
            public const string GroupMentionTooLarge = "group_mention_too_large";
            public const string ReactionKeyInvalid = "reaction_key_invalid";
            public const string AttachmentTooLarge = "attachment_too_large";
            public const string TooManyAttachments = "too_many_attachments";
            public const string AttachmentStorageKeyRequired = "attachment_storage_key_required";
            public const string ParticipantRoleInvalid = "participant_role_invalid";
            public const string PermissionLevelInvalid = "permission_level_invalid";
            public const string PrincipalKindInvalid = "principal_kind_invalid";
            public const string ChannelInvalid = "channel_invalid";
            public const string PreferenceModeInvalid = "preference_mode_invalid";
            public const string TemplateUnknown = "template_unknown";
            public const string TemplateTokenMissing = "template_token_missing";
            public const string ActorRequired = "actor_required";
            public const string EntityRefInvalid = "entity_ref_invalid";
            public const string EditWindowClosed = "edit_window_closed";
        }
    }

    // The caller may not perform the action. Reason is carried for the AUDIT ROW, not for the user: a denial
    // that explains itself to the caller ("you lack Moderate on thread 42") is an information leak about a
    // thread they cannot read. Services write Reason to CommAuditEntry and return a generic message.
    public sealed class CommAccessDeniedException : UnauthorizedAccessException
    {
        public CommAccessDeniedException(string action, CommEntityRef? entity, string reason)
            : base($"Communication action '{action}' denied on {entity?.Key ?? "(no entity)"}.")
        { Action = action; Entity = entity; Reason = reason; }

        public string Action { get; }
        public CommEntityRef? Entity { get; }

        // Never surfaced to an end user. Audit only.
        public string Reason { get; }
    }

    // The row does not exist, is soft-deleted, or belongs to another company.
    //
    // ALL THREE ANSWER IDENTICALLY, on purpose. Distinguishing "does not exist" from "exists in another
    // company" turns this exception into a cross-tenant existence oracle: a caller could enumerate ids and
    // learn which ones another company owns. CommunicationAccessService already applies exactly this rule
    // for conversations ("Absent and other-company answer identically").
    public sealed class CommNotFoundException : InvalidOperationException
    {
        public CommNotFoundException(string what, long id)
            : base($"{what} {id} was not found in the caller's company.")
        { What = what; Id = id; }

        public string What { get; }
        public long Id { get; }
    }
}