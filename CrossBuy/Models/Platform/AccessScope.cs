namespace CrossBuy.Models.Platform
{
    // Stage 1 Batch C — the SET-shaped companion to CanAsync.
    //
    // WHY A SECOND SHAPE IS NEEDED AT ALL
    //
    // `CanAsync(context, action, target)` answers one record. A Unified Work Inbox over 50 000 tasks cannot
    // ask 50 000 questions — and if it writes its own WHERE clause instead, the rules diverge. That is not a
    // hypothetical: this codebase already carries ~1,100 hand-written `CompanyID ==` predicates that exist
    // precisely because there was no set-shaped way to ask.
    //
    // So a consumer asks ONCE, receives an AccessScope, and translates it into ONE predicate:
    //
    //     var scope = await tasks.ResolveScopeAsync(context, "read");
    //     var query = scope.Breadth switch
    //     {
    //         AccessBreadth.None    => db.Tasks.Where(_ => false),
    //         AccessBreadth.Own     => db.Tasks.Where(t => t.AssigneeEmployeeId == me || t.CreatedByEmployeeId == me),
    //         AccessBreadth.Team    => db.Tasks.Where(t => scope.PrincipalIds!.Contains(t.AssigneeEmployeeId)),
    //         AccessBreadth.Company => db.Tasks.Where(t => t.CompanyId == context.CompanyId),
    //         …
    //     };
    //
    // Batch C ships the contract plus the real Tasks implementation. It does NOT build the Inbox, Search,
    // Reports or AI Context — but designing this now is what keeps them from re-deriving authorization later.
    public enum AccessBreadth
    {
        // Nothing. The caller may not see any record of this type for this action.
        None = 0,

        // Only records the caller is personally attached to — assignee, creator, owner, participant.
        Own,

        // Own plus the records of the principals in PrincipalIds. Those ids are ALWAYS company-intersected
        // by the producer, because the org tree (`Hierarchical`) carries no CompanyID and a raw walk can
        // cross companies.
        Team,

        // The caller's branch, when BranchId is set. Used by branch-scoped role grants.
        Branch,

        // Every record in the caller's company.
        Company,

        // Every record in every company. NEVER granted by an ordinary module role — it exists so a consumer
        // can represent what an authorized, audited ICompanyIsolationBypass holder sees, and so that state is
        // expressible rather than smuggled in as "Company with a different company id".
        CrossCompany,
    }

    // What a caller may see, expressed as a set rather than a verdict.
    public sealed class AccessScope
    {
        public required AccessBreadth Breadth { get; init; }

        // For Team: the principals whose records are included, ALWAYS company-intersected and always
        // including the caller. Null for every other breadth — a non-null value on Company would imply a
        // narrowing that Company does not mean.
        public IReadOnlyCollection<int>? PrincipalIds { get; init; }

        // For Branch: which branch. Null otherwise.
        public int? BranchId { get; init; }

        // The company every breadth except CrossCompany is confined to. Carried explicitly so a consumer
        // building a predicate never has to reach back into the context for it — the commonest way a
        // company predicate gets forgotten is having to remember to add it.
        public int? CompanyId { get; init; }

        public static AccessScope None() => new() { Breadth = AccessBreadth.None };

        public static AccessScope Own(int companyId) =>
            new() { Breadth = AccessBreadth.Own, CompanyId = companyId };

        public static AccessScope Team(int companyId, IReadOnlyCollection<int> principalIds) =>
            new() { Breadth = AccessBreadth.Team, CompanyId = companyId, PrincipalIds = principalIds };

        public static AccessScope Branch(int companyId, int branchId) =>
            new() { Breadth = AccessBreadth.Branch, CompanyId = companyId, BranchId = branchId };

        public static AccessScope Company(int companyId) =>
            new() { Breadth = AccessBreadth.Company, CompanyId = companyId };

        // Only a holder of an authorized cross-company bypass may legitimately be described this way.
        public static AccessScope CrossCompany() => new() { Breadth = AccessBreadth.CrossCompany };

        // True when this scope can see nothing at all — the check a consumer makes before building a query.
        public bool IsEmpty => Breadth == AccessBreadth.None;

        // Does this scope include a record attached to `principalId` in `companyId`?
        //
        // This is the bridge that keeps CanAsync and ResolveScopeAsync from drifting: an access service
        // implements the yes/no answer for a set-shaped action BY resolving the scope and asking this, so
        // there is one rule with two presentations rather than two rules.
        public bool Includes(int companyId, int principalId, int? callerEmployeeId)
        {
            if (Breadth == AccessBreadth.CrossCompany) return true;
            if (CompanyId.HasValue && CompanyId.Value != companyId) return false;

            return Breadth switch
            {
                AccessBreadth.None => false,
                AccessBreadth.Own => callerEmployeeId.HasValue && principalId == callerEmployeeId.Value,
                AccessBreadth.Team => PrincipalIds != null && PrincipalIds.Contains(principalId),
                // Branch and Company are not principal-scoped: within the company (and branch) every record
                // qualifies, so the principal is irrelevant rather than unchecked.
                AccessBreadth.Branch => true,
                AccessBreadth.Company => true,
                _ => false,
            };
        }
    }
}
