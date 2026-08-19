namespace CrossBuy.Models.Context.Communication
{
    // =============================================================================================
    // Communication Platform (ADR-030) — thread aggregate.
    //
    // COMPANY ISOLATION NOTE, stated once for every entity in this namespace:
    //
    // None of these tables is in the platform kernel's CompanyQueryFilters pilot set, so NO global query
    // filter protects them. Every read in BL/Communication therefore filters CompanyID EXPLICITLY, and
    // CommunicationTestHost/company-isolation tests prove it. That is the honest position: claiming filter
    // coverage this platform does not have would be the "a query filter is not an authorization control"
    // mistake CLAUDE.md already records.
    //
    // Soft delete (DeletedAt) follows the parallel team's feature-module convention. It does NOT contradict
    // our "reverse, never delete" rule — that rule governs FINANCIAL history (a journal entry, a stock
    // movement), where a reversing entry is the correction primitive. A comment has no ledger; its
    // correction primitive is a revision row plus a soft-delete marker, and both are append-only.
    // =============================================================================================

    // One conversation anchored to one business record.
    //
    // Identity is (CompanyID, EntityType, EntityId, Kind, ThreadKey). ThreadKey is "" for the record's
    // default thread and a caller-chosen token for a named topic thread — that is how module 2 (Discussion
    // Threads) exists without a second table and without a nullable self-referencing parent.
    public class CommThread : BaseEntity
    {
        public long Id { get; set; }
        public int CompanyID { get; set; }

        // Branch is CAPTURED, never filtered on by default. The kernel's timeline learned this: filtering
        // unconditionally hides branch-stamped history from a head-office reader AND hides unstamped rows
        // from a branch reader. It is stored so a future report can group by it.
        public int? BranchID { get; set; }

        // Canonical IEntityRegistry code. Validated on WRITE by ICommEntitySurface; reads tolerate whatever
        // is stored so a code that was later renamed still loads its history.
        public string EntityType { get; set; } = "";
        public int EntityId { get; set; }

        public string Kind { get; set; } = "";            // CommThreadKind
        public string ThreadKey { get; set; } = "";       // "" = the default thread

        public string? SubjectAr { get; set; }
        public string? SubjectEn { get; set; }

        // The thread's visibility CEILING. A comment may be equally or more restricted, never more open —
        // enforced in CommCommentService, not by a database constraint, because the comparison is ordinal
        // over CommVisibility.Rank rather than a column relation.
        public string Visibility { get; set; } = "";      // CommVisibility

        public bool IsLocked { get; set; }
        public string? LockedReason { get; set; }
        public int? LockedBy { get; set; }
        public DateTime? LockedAt { get; set; }

        // Denormalised counters. Maintained inside the same transaction as the row they count, so they are
        // never a lagging cache — a thread list screen must not fan out N count queries, and this is the
        // cheapest correct alternative.
        public int CommentCount { get; set; }
        public int ParticipantCount { get; set; }
        public DateTime? LastActivityAt { get; set; }

        public DateTime? DeletedAt { get; set; }
        public int? DeletedBy { get; set; }
    }

    // A thread-level grant (modules 23, 24).
    //
    // THE INVARIANT THIS TABLE MUST NEVER BREAK: a grant here can only ADD access WITHIN what the anchor
    // entity already allows. Entity-level View remains a precondition evaluated by IPlatformPermissionProvider
    // before this table is consulted. A row here can therefore open a restricted thread to a colleague who
    // can already open the invoice — it can never open the invoice. Proven by CommAccessPolicyTests.
    public class CommThreadPermission : BaseEntity
    {
        public long Id { get; set; }
        public int CompanyID { get; set; }
        public long ThreadId { get; set; }

        public string PrincipalKind { get; set; } = "";   // CommPrincipalKind
        public int? PrincipalId { get; set; }             // employee / manager / org-node id
        public string? PrincipalKey { get; set; }         // role name, for PrincipalKind = Role

        public string Level { get; set; } = "";           // CommPermissionLevel

        public DateTime? RevokedAt { get; set; }
        public int? RevokedBy { get; set; }
    }
}