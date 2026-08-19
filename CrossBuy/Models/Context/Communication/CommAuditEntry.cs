namespace CrossBuy.Models.Context.Communication
{
    // =============================================================================================
    // Communication Platform (ADR-035) — the AUDIT TRAIL (module 31) and the durable home of every
    // communication event (module 26).
    //
    // WHY THIS TABLE EXISTS RATHER THAN "JUST USE BusinessEvents"
    //
    // 1. AVAILABILITY. BusinessEvents is deployed by a platform-kernel SQL slice and RecordAsync throws with
    //    no swallowing catch when the table is missing or when there is no ambient transaction. If audit lived
    //    there, an undeployed slice would break commenting on every screen. This table is deployed with this
    //    platform, so its audit is always available.
    //
    // 2. COMPLETENESS. The kernel's event log is filtered on read by visibility and module permission, which
    //    is correct for a record TIMELINE and wrong for an AUDIT TRAIL: an auditor asking "who deleted the
    //    note" must get an answer even when the note was Restricted. So the two have different read rules and
    //    cannot be the same table.
    //
    // 3. GRANULARITY. Reactions, participation changes and permission grants are deliberately NOT bridged to
    //    the kernel (see CommEventTypes.KernelActionFor) — but they ARE audited. Audit is the superset.
    //
    // APPEND-ONLY. Nothing updates or deletes a row here. No service exposes a mutation; the SQL grants no
    // path to one. This is the one table in the platform with no DeletedAt, on purpose.
    // =============================================================================================
    public class CommAuditEntry
    {
        public long Id { get; set; }
        public int CompanyID { get; set; }
        public int? BranchID { get; set; }

        public string Action { get; set; } = "";           // CommAuditActions

        // The communication event type this row records, when the action corresponds to one
        // (CommEventTypes). Null for actions that are audit-only, e.g. AccessDenied.
        public string? EventType { get; set; }

        public string EntityType { get; set; } = "";
        public int EntityId { get; set; }

        public long? ThreadId { get; set; }
        public long? CommentId { get; set; }
        public long? MentionId { get; set; }
        public long? NotificationId { get; set; }

        public int? ActorEmployeeId { get; set; }

        // The employee the action was ABOUT, when different from the actor (a moderator deleting someone
        // else's comment, a participant being added). Present so "what was done to me" is queryable.
        public int? SubjectEmployeeId { get; set; }

        public string? Visibility { get; set; }

        // Small JSON summary. NEVER the comment body, and never a StorageKey: an audit trail is read by more
        // people than the content is, and a capability-bearing file handle in an audit row is a quiet
        // privilege escalation. Same payload discipline PKS-001 imposes on business events, applied at source.
        public string? DetailJson { get; set; }

        // Correlates every row written by one operation — the comment, its mentions, its notifications. Taken
        // from BusinessContext.CorrelationId so a comm audit row and a kernel event row from the same request
        // share an id and can be joined during an investigation.
        public Guid? CorrelationId { get; set; }

        // Idempotency for the audit row itself, unique per company where present. A retried operation inside
        // a rolled-back transaction leaves nothing; a retried operation that partially committed cannot
        // double-log.
        public string? DedupKey { get; set; }

        public DateTime CreatedAt { get; set; }
    }
}