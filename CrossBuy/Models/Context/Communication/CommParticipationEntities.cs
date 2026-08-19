namespace CrossBuy.Models.Context.Communication
{
    // =============================================================================================
    // Communication Platform (ADR-032) — participation, mentions and read state.
    // Company-isolation and soft-delete notes: see CommThreadEntities.cs.
    // =============================================================================================

    // Followers, watchers, participants and owners of a thread (modules 4, 5).
    //
    // ONE table for all four roles, because they differ only in what notification they earn — see
    // CommParticipantRole.NotifiedPerActivity. Two tables (Followers, Watchers) would need every membership
    // operation written twice and would make "am I involved in this thread at all" a UNION.
    public class CommParticipant : BaseEntity
    {
        public long Id { get; set; }
        public int CompanyID { get; set; }
        public long ThreadId { get; set; }

        // Denormalised anchor, same rationale as CommComment: "which records am I following" must not join
        // CommThreads per row.
        public string EntityType { get; set; } = "";
        public int EntityId { get; set; }

        public int EmployeeId { get; set; }

        public string Role { get; set; } = "";            // CommParticipantRole
        public string Source { get; set; } = "";          // CommParticipationSource

        // Muting is per participant, not per thread: one member silencing a busy thread must not silence it
        // for everybody. A muted participant keeps their row (so they stay visible in the participant list
        // and can unmute) and is skipped at notification time with reason "muted".
        public DateTime? MutedAt { get; set; }

        public DateTime? RemovedAt { get; set; }
        public int? RemovedBy { get; set; }
    }

    // One @mention TOKEN as authored (modules 3, 15).
    //
    // This row is the mention itself — "@department:5 was mentioned in comment 91". Who that resolved to is
    // a separate table, because a group mention is one authoring act and N recipients, and collapsing them
    // would either lose the group's identity or duplicate it N times.
    public class CommMention : BaseEntity
    {
        public long Id { get; set; }
        public int CompanyID { get; set; }
        public long ThreadId { get; set; }
        public long CommentId { get; set; }

        public string EntityType { get; set; } = "";
        public int EntityId { get; set; }

        public string TargetKind { get; set; } = "";      // CommMentionTargetKind
        public int? TargetId { get; set; }
        public string? TargetKey { get; set; }            // role name, for TargetKind = Role

        // Label AS RESOLVED AT AUTHORING TIME. Stored, not looked up on read: a department that was renamed
        // next month was not the department that was mentioned, and re-resolving would rewrite history.
        public string? LabelAr { get; set; }
        public string? LabelEn { get; set; }

        // How many employees the target resolved to when authored. Same reasoning as the label: a department
        // that gained ten members did not receive this mention.
        public int ResolvedRecipientCount { get; set; }

        public int MentionedByEmployeeId { get; set; }
    }

    // ONE resolved recipient of ONE mention.
    //
    // This is what makes "where have I been mentioned" (module 15) a single indexed read for an employee,
    // including mentions they received through a team or a department they belong to. ViaKind records HOW —
    // directly, or via the group — because "I was named personally" and "my department was named" are
    // different facts to the person reading the list.
    public class CommMentionRecipient : BaseEntity
    {
        public long Id { get; set; }
        public int CompanyID { get; set; }
        public long MentionId { get; set; }
        public long CommentId { get; set; }
        public long ThreadId { get; set; }

        public int EmployeeId { get; set; }

        // CommMentionTargetKind of the mention that reached them: Employee = named directly,
        // Team/Department/Role = reached through the group.
        public string ViaKind { get; set; } = "";

        // Set when the employee has seen the mention. Distinct from CommReadReceipt (which tracks a whole
        // thread) because a mention badge must clear independently of whether the thread was scrolled.
        public DateTime? ReadAt { get; set; }
    }

    // Read status per (thread, employee) (module 14).
    //
    // LastReadCommentId is a WATERMARK, not a count. Storing a count would need every read to recount on
    // every new comment; a watermark makes "unread" a single comparison, and the unread number is computed
    // against what the caller may READ so a Confidential comment they cannot see never shows as an unread
    // they can never clear.
    public class CommReadReceipt : BaseEntity
    {
        public long Id { get; set; }
        public int CompanyID { get; set; }
        public long ThreadId { get; set; }
        public int EmployeeId { get; set; }

        public long? LastReadCommentId { get; set; }
        public DateTime? LastReadAt { get; set; }
    }
}