namespace CrossBuy.Models.Platform
{
    // =============================================================================================
    // Stage 2A Batch B — bootstrap governance contracts.
    //
    // Three things live here, and they live TOGETHER on purpose:
    //   1. the policy state vocabulary,
    //   2. the NEVER-BOOTSTRAP-OPEN classification,
    //   3. the authorization decision result and its sources.
    //
    // WHY TOGETHER: the Never classification is the one rule that overrides every policy state. Putting
    // the states in one file and the overrides in another is how a state eventually gets added that nobody
    // checks the classification against. CORRECTION-004 was a credit nobody could see; this is the same
    // hazard in the other direction.
    //
    // NOTHING HERE CHANGES BEHAVIOUR. These are declarations. The access services do not consult them yet
    // (B6), and no storage reads them yet (B2). They exist so the classification confirmed by the owner is
    // recorded in code rather than in a document.
    // =============================================================================================

    /// <summary>
    /// What a bootstrap policy permits. Unknown values are rejected — a policy nobody can evaluate must not
    /// exist, because "cannot be evaluated" reads as "no policy" and no policy reads as bootstrap-open.
    /// </summary>
    public static class BootstrapPolicyStates
    {
        /// <summary>Records behaviour that is implicitly open TODAY. The seed's default; never a destination.</summary>
        public const string LegacyCompatibility = "LegacyCompatibility";

        /// <summary>A brand-new company that has no roles yet. Expected to end.</summary>
        public const string Installation = "Installation";

        /// <summary>A time-boxed exception. REQUIRES an expiry — see <see cref="RequiresExpiry"/>.</summary>
        public const string Temporary = "Temporary";

        /// <summary>A deliberate, reviewed decision that this action stays open on an unconfigured company.</summary>
        public const string ExplicitlyAllowed = "ExplicitlyAllowed";

        /// <summary>Bootstrap is off for this company, scope and action. Roles decide, or nothing does.</summary>
        public const string Disabled = "Disabled";

        /// <summary>Still permits, but flags that a human must look — set when the first real grant appears.</summary>
        public const string ReviewRequired = "ReviewRequired";

        public static readonly IReadOnlyList<string> All = new[]
        {
            LegacyCompatibility, Installation, Temporary, ExplicitlyAllowed, Disabled, ReviewRequired,
        };

        /// <summary>States that permit an action in the absence of a role. `Disabled` is not one of them.</summary>
        public static readonly IReadOnlyList<string> Permitting = new[]
        {
            LegacyCompatibility, Installation, Temporary, ExplicitlyAllowed, ReviewRequired,
        };

        public static bool IsKnown(string? state) => state != null && All.Contains(state, StringComparer.Ordinal);

        public static bool Permits(string? state) => state != null && Permitting.Contains(state, StringComparer.Ordinal);

        /// <summary>A time-boxed exception with no end date is a permanent one wearing a different name.</summary>
        public static bool RequiresExpiry(string? state) =>
            string.Equals(state, Temporary, StringComparison.Ordinal);
    }

    /// <summary>
    /// The actions that may NEVER be reached through bootstrap compatibility, whatever policy says.
    ///
    /// DERIVED FROM LIVE SOURCE and confirmed by the owner — see
    /// docs/platform/Stage-002A-Never-Bootstrap-Open-Derivation.md. The re-derivation produced **15**, not the
    /// 14 the frozen matrix recorded, and the difference was resolved by decision rather than by forcing a
    /// total:
    ///
    ///   + Inventory.purchase  — it receives stock (see the vocabulary defect note below)
    ///   + Inventory.manage    — it assigns Inventory roles INCLUDING branch scope, exactly as
    ///                           Accounting.manage and CRM.manage assign theirs
    ///   - migration administration and restricted security diagnostics are NOT separate live actions; both
    ///     collapse into PlatformOps today and are retained as PLANNED classifications only
    ///     (see <see cref="PlannedFutureClassifications"/>), because inventing permission codes to satisfy a
    ///     matrix would put unenforceable entries inside a security control.
    ///
    /// THE OVERRIDE IS ABSOLUTE. No state in <see cref="BootstrapPolicyStates.Permitting"/> may allow one of
    /// these, and no policy writer may enable one. That is what makes the classification a control rather than
    /// a default.
    /// </summary>
    public static class NeverBootstrapOpen
    {
        /// <summary>One entry per (scope, action) that bootstrap may never satisfy.</summary>
        public sealed record Entry(string Scope, string Action, string Reason);

        public static readonly IReadOnlyList<Entry> All = new[]
        {
            // ---- Accounting: 4 ----
            new Entry("Accounting", "post",
                "Posts to the general ledger. The two-writers rule makes JournalEntryService the only writer; " +
                "bootstrap must not decide who may invoke it."),
            new Entry("Accounting", "pay",
                "Receipts, payments, bank and cash transfers — money leaving the company."),
            new Entry("Accounting", "manage",
                "Gates AssignAccRole/RemoveAccRole (AccountingController:1586), so it grants SECURITY, and it is " +
                "PlatformOpsAttribute's fallback authority, so it exposes platform operations. Both halves of the " +
                "matrix's 'where it grants security or exposes PlatformOps'."),
            new Entry("Accounting", "currency-override",
                "Issues a document in a currency other than the branch's, which moves the recorded amount."),

            // ---- Inventory: 4 ----
            new Entry("Inventory", "doc",
                "Stock movement, transfer, count, assembly, landed cost and sales documents. ONE live action " +
                "covering the matrix's separate receipt, issue and transfer bullets."),
            new Entry("Inventory", "purchase",
                "VOCABULARY DEFECT, REPORTED AND CLOSED WHOLE. One action code gates a pre-stock draft " +
                "(CreatePurchaseOrder, GeneratePO), a STOCK RECEIPT (CreateGoodsReceipt) and an ACCOUNTING effect " +
                "(ConvertPoToInvoice). Owner decision: keep the entire action closed for Batch B rather than leave " +
                "stock receipt open, and do NOT invent a split permission code in this increment."),
            new Entry("Inventory", "manage",
                "Gates AssignRole(employeeId, role, scopeBranchId) (InventoryController:2352) — role assignment " +
                "INCLUDING the branch scope it applies to. Bootstrap here would let any authenticated user elevate " +
                "their own Inventory privileges and choose the warehouse scope."),
            new Entry("Inventory", "warehouse-access",
                "Compatibility here would widen an exact branch/warehouse restriction to company scope, which is " +
                "the one thing the warehouse path exists to prevent."),

            // ---- CRM: 1 ----
            new Entry("Crm", "manage",
                "Gates AssignCrmRole (CrmController:705) — CRM role assignment and revocation."),

            // ---- Platform: 1 enforceable today ----
            new Entry("Platform", "PlatformOps",
                "Platform operations. Must be independently closed so it cannot inherit an Accounting bootstrap " +
                "allow through PlatformOpsAttribute's accounting fallback — that inheritance is RISK-042."),

            // ---- Projects: 1 ----
            new Entry("Projects", "billing",
                "Delegates to the Accounting decision (ProjectsAccessService:100-101: 'Being an administrator of " +
                "projects is not a right over the ledger'). Closes transitively once Accounting.post closes, and is " +
                "listed explicitly so the transitive closure is asserted rather than assumed."),

            // ---- Already excluded by Mechanism B — PRESERVE, do not widen: 4 ----
            new Entry("Hr", "payroll-manage",
                "Already excluded by HrAccessService under bootstrap-open. Listed so a refactor cannot widen it."),
            new Entry("Hr", "confidential-view",
                "Already excluded by HrAccessService. Salary, disciplinary and performance detail."),
            new Entry("Tasks", "manage",
                "Already excluded by TasksAccessService."),
            new Entry("Communication", "outbox-manage",
                "Already excluded by CommunicationAccessService. CommMessage has no participant rule, so this is " +
                "its only gate."),
        };

        /// <summary>
        /// Classifications the matrix names but which have NO enforceable live action yet. Retained so they are
        /// not forgotten when a real action appears — owner decision, rather than creating a permission code with
        /// nothing behind it.
        /// </summary>
        public static readonly IReadOnlyList<string> PlannedFutureClassifications = new[]
        {
            "Migration administration — no distinct production action exists (Batch A left MigrationBatchId dormant). " +
            "Classify as Never the moment a migration administration action ships.",
            "Restricted security diagnostics — currently inherits PlatformOps protection because no separate live " +
            "endpoint exists. Classify independently when one does.",
        };

        public static int Count => All.Count;

        /// <summary>
        /// Is this (scope, action) beyond the reach of every bootstrap policy?
        ///
        /// Ordinal comparison on both halves: a scope or action differing only in case is a DIFFERENT string
        /// everywhere else in this platform (EntityRegistry.IsKnownScope, the access services' action switches),
        /// and a case-insensitive match here would be the one place the vocabulary is lenient.
        /// </summary>
        public static bool Contains(string? scope, string? action) =>
            scope != null && action != null &&
            All.Any(e => string.Equals(e.Scope, scope, StringComparison.Ordinal)
                      && string.Equals(e.Action, action, StringComparison.Ordinal));

        public static Entry? Find(string? scope, string? action) =>
            scope == null || action == null
                ? null
                : All.FirstOrDefault(e => string.Equals(e.Scope, scope, StringComparison.Ordinal)
                                       && string.Equals(e.Action, action, StringComparison.Ordinal));
    }

    /// <summary>
    /// WHY an authorization decision came out the way it did.
    ///
    /// The point of naming these is RISK-041: a bootstrap allow currently looks identical to a role allow in
    /// every log and every caller. A caller must never have to reconstruct the reason from tables.
    /// </summary>
    public static class AuthorizationDecisionSources
    {
        /// <summary>A row in PlatformRoleAssignments — the Batch A grant store.</summary>
        public const string DirectGrant = "DirectGrant";

        /// <summary>A legacy module role table (AccountingUserRoles / InventoryUserRoles / CrmUserRoles).</summary>
        public const string LegacyRole = "LegacyRole";

        public const string BootstrapLegacyCompatibility = "BootstrapLegacyCompatibility";
        public const string BootstrapInstallation = "BootstrapInstallation";
        public const string BootstrapTemporary = "BootstrapTemporary";
        public const string BootstrapExplicitlyAllowed = "BootstrapExplicitlyAllowed";

        /// <summary>ProjectMembers / ConversationMember — business membership, never a grant.</summary>
        public const string BusinessMembership = "BusinessMembership";

        /// <summary>The record is the caller's own (CRM owner, HR self-service).</summary>
        public const string Ownership = "Ownership";

        /// <summary>The caller manages the subject (leave approval, CRM team).</summary>
        public const string Hierarchy = "Hierarchy";

        /// <summary>SystemContextPolicy allowed a system context a narrow action.</summary>
        public const string SystemPolicy = "SystemPolicy";

        /// <summary>BranchUserRoles — the POS exception, which has no bootstrap behaviour at all.</summary>
        public const string PosBranchRole = "PosBranchRole";

        public const string Denied = "Denied";

        /// <summary>
        /// Something is wrong with the configuration — an unknown state, a Temporary policy with no expiry, a
        /// policy naming an unknown scope. Distinct from Denied because a denial is a correct answer and this is
        /// not: it needs an administrator, not a permission.
        /// </summary>
        public const string ConfigurationError = "ConfigurationError";

        public static readonly IReadOnlyList<string> All = new[]
        {
            DirectGrant, LegacyRole,
            BootstrapLegacyCompatibility, BootstrapInstallation, BootstrapTemporary, BootstrapExplicitlyAllowed,
            BusinessMembership, Ownership, Hierarchy, SystemPolicy, PosBranchRole, Denied, ConfigurationError,
        };

        public static readonly IReadOnlyList<string> BootstrapSources = new[]
        {
            BootstrapLegacyCompatibility, BootstrapInstallation, BootstrapTemporary, BootstrapExplicitlyAllowed,
        };

        public static bool IsBootstrap(string? source) =>
            source != null && BootstrapSources.Contains(source, StringComparer.Ordinal);

        /// <summary>Maps a policy state to the decision source it produces.</summary>
        public static string ForState(string? state) => state switch
        {
            BootstrapPolicyStates.LegacyCompatibility => BootstrapLegacyCompatibility,
            BootstrapPolicyStates.Installation => BootstrapInstallation,
            BootstrapPolicyStates.Temporary => BootstrapTemporary,
            BootstrapPolicyStates.ExplicitlyAllowed => BootstrapExplicitlyAllowed,
            // ReviewRequired still permits, and it is compatibility that someone has been asked to look at — so it
            // reports as compatibility rather than inventing a fifth bootstrap source the console would have to learn.
            BootstrapPolicyStates.ReviewRequired => BootstrapLegacyCompatibility,
            BootstrapPolicyStates.Disabled => Denied,
            _ => ConfigurationError,
        };
    }

    /// <summary>
    /// One authorization decision, with its reason.
    ///
    /// ADDITIVE. `CanAsync` keeps its `bool` signature and delegates here, returning <see cref="IsAllowed"/>, so
    /// no existing caller changes. What is new is that the reason is now available to the caller that wants it —
    /// which is what makes RISK-041 closable and what the future Security Console reads.
    ///
    /// Deliberately carries NO confidential payload: a decision explains itself in terms of scope, action, policy
    /// and role, never in terms of the data being protected.
    /// </summary>
    public sealed class AuthorizationDecision
    {
        public bool IsAllowed { get; init; }

        /// <summary>One of <see cref="AuthorizationDecisionSources"/>.</summary>
        public string DecisionSource { get; init; } = AuthorizationDecisionSources.Denied;

        public int CompanyID { get; init; }
        public string Scope { get; init; } = "";
        public string Action { get; init; } = "";

        /// <summary>The role(s) that decided it, when a role did. Never the record's contents.</summary>
        public IReadOnlyList<string> RoleEvidence { get; init; } = Array.Empty<string>();

        /// <summary>The policy that permitted it, when a policy did.</summary>
        public int? BootstrapPolicyId { get; init; }

        /// <summary>A short machine code for the reason, safe to log and to show an administrator.</summary>
        public string ReasonCode { get; init; } = "";

        /// <summary>The branch or warehouse the decision was scoped to, when it was.</summary>
        public int? ScopeBranchId { get; init; }

        public bool IsBootstrap => AuthorizationDecisionSources.IsBootstrap(DecisionSource);
        public bool IsMembership => DecisionSource == AuthorizationDecisionSources.BusinessMembership;
        public bool IsOwnership => DecisionSource == AuthorizationDecisionSources.Ownership;
        public bool IsHierarchy => DecisionSource == AuthorizationDecisionSources.Hierarchy;
        public bool IsSystemPolicy => DecisionSource == AuthorizationDecisionSources.SystemPolicy;
        public bool IsDirectGrant => DecisionSource == AuthorizationDecisionSources.DirectGrant;

        public static AuthorizationDecision Allow(
            string source, int companyId, string scope, string action, string reasonCode,
            IReadOnlyList<string>? roles = null, int? policyId = null, int? branchId = null) => new()
            {
                IsAllowed = true,
                DecisionSource = source,
                CompanyID = companyId,
                Scope = scope,
                Action = action,
                ReasonCode = reasonCode,
                RoleEvidence = roles ?? Array.Empty<string>(),
                BootstrapPolicyId = policyId,
                ScopeBranchId = branchId,
            };

        public static AuthorizationDecision Deny(
            int companyId, string scope, string action, string reasonCode, int? branchId = null) => new()
            {
                IsAllowed = false,
                DecisionSource = AuthorizationDecisionSources.Denied,
                CompanyID = companyId,
                Scope = scope,
                Action = action,
                ReasonCode = reasonCode,
                ScopeBranchId = branchId,
            };

        /// <summary>
        /// A misconfiguration, not a permission outcome. Denies — but says so differently, because an
        /// administrator has to fix this and no amount of role assignment will.
        /// </summary>
        public static AuthorizationDecision Misconfigured(
            int companyId, string scope, string action, string reasonCode, int? policyId = null) => new()
            {
                IsAllowed = false,
                DecisionSource = AuthorizationDecisionSources.ConfigurationError,
                CompanyID = companyId,
                Scope = scope,
                Action = action,
                ReasonCode = reasonCode,
                BootstrapPolicyId = policyId,
            };
    }

    /// <summary>Short, stable reason codes. Strings a console can group on and a log can be searched for.</summary>
    public static class AuthorizationReasonCodes
    {
        public const string NeverBootstrapOpen = "never_bootstrap_open";
        public const string NoPolicyConfigured = "no_bootstrap_policy";
        public const string PolicyDisabled = "bootstrap_policy_disabled";
        public const string PolicyExpired = "bootstrap_policy_expired";
        public const string PolicyInactive = "bootstrap_policy_inactive";
        public const string PolicyPermits = "bootstrap_policy_permits";
        public const string TemporaryWithoutExpiry = "temporary_policy_without_expiry";
        public const string UnknownPolicyState = "unknown_bootstrap_policy_state";
        public const string UnknownScope = "unknown_scope";
        public const string UnknownAction = "unknown_action";
        public const string PosHasNoBootstrap = "pos_has_no_bootstrap";
        public const string CompanyUnresolved = "company_unresolved";
        public const string RoleHeld = "role_held";
        public const string RoleNotHeld = "role_not_held";
    }
}
