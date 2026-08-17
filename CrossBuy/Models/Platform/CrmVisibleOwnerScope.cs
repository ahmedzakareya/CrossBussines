namespace CrossBuy.Models.Platform
{
    // =============================================================================================
    // CrossBusiness Platform — Stage 2A Batch B: the CRM visible-owner scope, typed.
    //
    // WHY THIS EXISTS. `ICrmAccessService.VisibleOwnerIdsAsync()` returns `Task<HashSet<int>?>`, and the
    // interface documents the meaning as: "Owner-id whitelist the current user may SEE; null =
    // unrestricted (viewer/marketing/unconfigured)".
    //
    // Three different answers share two representations, and one of them is a nullable reference:
    //
    //     null            -> see EVERY owner's records in the company        (widest possible)
    //     empty set       -> see NOBODY's records                            (narrowest possible)
    //     populated set   -> see exactly these owners' records
    //
    // The two extremes are one keystroke apart. `?? new HashSet<int>()` — the reflex when a compiler
    // warns about a nullable — silently converts "see everything" into "see nothing", and the reverse
    // mistake (`if (ids == null || !ids.Any()) return all;`) converts "see nothing" into "see
    // everything" on a CRM database. Neither produces an error; both produce a wrong result quietly.
    //
    // And note the third word in that comment: **unconfigured**. A company with no CRM role configured
    // gets `null` — unrestricted company-wide owner visibility — through the bootstrap path. That is the
    // exposure B6 has to preserve deliberately rather than inherit accidentally.
    //
    // ADDITIVE ONLY. `VisibleOwnerIdsAsync` keeps its signature and every production caller is untouched
    // in this increment. This type and its adapter exist to prove the mapping BEFORE B6 depends on it.
    // =============================================================================================

    public enum CrmVisibleOwnerScopeState
    {
        /// <summary>
        /// No owner filter — every owner's records inside the resolved company. Maps from legacy `null`.
        /// Explicit, because "unrestricted" should be a value you can see in a debugger and log, not the
        /// absence of one.
        /// </summary>
        UnrestrictedCompanyScope = 0,

        /// <summary>Exactly these owner ids. Requires at least one.</summary>
        RestrictedOwnerIds,

        /// <summary>No visible owners at all. Maps from legacy empty set. DENIES.</summary>
        NoAccess,

        /// <summary>
        /// The scope could not be determined — an unresolved company, a cross-company owner id, an invalid id.
        /// Fails CLOSED, and is distinct from NoAccess because an administrator has to fix this and no amount of
        /// role assignment will.
        /// </summary>
        ConfigurationError,
    }

    /// <summary>
    /// A CRM owner-visibility scope with its reason. Shaped to sit alongside <see cref="AuthorizationDecision"/>
    /// so B6 can carry a bootstrap decision's provenance straight through to the owner filter.
    /// </summary>
    public sealed class CrmVisibleOwnerScopeResult
    {
        public CrmVisibleOwnerScopeState State { get; init; }

        /// <summary>Explicit. There is no default company and no fallback to company 1.</summary>
        public int CompanyID { get; init; }

        /// <summary>Empty unless <see cref="State"/> is <see cref="CrmVisibleOwnerScopeState.RestrictedOwnerIds"/>.</summary>
        public IReadOnlyCollection<int> OwnerIds { get; init; } = Array.Empty<int>();

        /// <summary>One of <see cref="AuthorizationDecisionSources"/>.</summary>
        public string DecisionSource { get; init; } = AuthorizationDecisionSources.Denied;

        /// <summary>A machine code, safe to log. Never carries customer data — see the invariant test.</summary>
        public string ReasonCode { get; init; } = "";

        /// <summary>Set when a bootstrap policy produced this scope.</summary>
        public int? BootstrapPolicyId { get; init; }

        public bool IsBootstrap => AuthorizationDecisionSources.IsBootstrap(DecisionSource);

        /// <summary>
        /// Whether any record is visible at all. UnrestrictedCompanyScope and RestrictedOwnerIds allow;
        /// NoAccess and ConfigurationError deny. Both denials are denials — that is the fail-closed property.
        /// </summary>
        public bool IsAllowed =>
            State is CrmVisibleOwnerScopeState.UnrestrictedCompanyScope
                  or CrmVisibleOwnerScopeState.RestrictedOwnerIds;

        // -----------------------------------------------------------------------------------------
        // factories — the only way to build one, so an invalid combination cannot be constructed
        // -----------------------------------------------------------------------------------------

        public static CrmVisibleOwnerScopeResult Unrestricted(
            int companyId, string decisionSource, string reasonCode, int? policyId = null)
        {
            if (companyId <= 0) return Misconfigured(companyId, AuthorizationReasonCodes.CompanyUnresolved);

            return new CrmVisibleOwnerScopeResult
            {
                State = CrmVisibleOwnerScopeState.UnrestrictedCompanyScope,
                CompanyID = companyId,
                DecisionSource = decisionSource,
                ReasonCode = reasonCode,
                BootstrapPolicyId = policyId,
            };
        }

        /// <summary>
        /// Restricted to specific owners. Normalises duplicates and refuses an empty result: "restricted to
        /// nobody" is <see cref="CrmVisibleOwnerScopeState.NoAccess"/>, and letting it be an empty
        /// RestrictedOwnerIds would recreate the exact ambiguity this type removes.
        /// </summary>
        public static CrmVisibleOwnerScopeResult Restricted(
            int companyId, IEnumerable<int>? ownerIds, string decisionSource, string reasonCode,
            int? policyId = null)
        {
            if (companyId <= 0) return Misconfigured(companyId, AuthorizationReasonCodes.CompanyUnresolved);
            if (ownerIds == null) return Misconfigured(companyId, "owner_ids_null_for_restricted_scope");

            var normalised = ownerIds.Where(id => id > 0).Distinct().OrderBy(id => id).ToArray();

            // A non-positive id is not merely filtered out — it means the caller built the set from something
            // it did not validate, so the whole scope is suspect. Fail closed rather than silently narrowing.
            if (ownerIds.Any(id => id <= 0))
                return Misconfigured(companyId, "invalid_owner_id");

            if (normalised.Length == 0) return NoOwners(companyId, decisionSource, reasonCode);

            return new CrmVisibleOwnerScopeResult
            {
                State = CrmVisibleOwnerScopeState.RestrictedOwnerIds,
                CompanyID = companyId,
                OwnerIds = normalised,
                DecisionSource = decisionSource,
                ReasonCode = reasonCode,
                BootstrapPolicyId = policyId,
            };
        }

        public static CrmVisibleOwnerScopeResult NoOwners(
            int companyId, string decisionSource, string reasonCode) => new()
            {
                State = CrmVisibleOwnerScopeState.NoAccess,
                CompanyID = companyId,
                DecisionSource = decisionSource,
                ReasonCode = reasonCode,
            };

        public static CrmVisibleOwnerScopeResult Misconfigured(int companyId, string reasonCode) => new()
        {
            State = CrmVisibleOwnerScopeState.ConfigurationError,
            CompanyID = companyId,
            DecisionSource = AuthorizationDecisionSources.ConfigurationError,
            ReasonCode = reasonCode,
        };
    }

    /// <summary>
    /// Maps the legacy nullable set to the typed result, and back.
    ///
    /// NOT WIRED INTO CrmAccessService. `VisibleOwnerIdsAsync` keeps its signature and its callers in this
    /// increment; this adapter exists so the mapping is proven by tests before B6 relies on it. Having the
    /// round trip available is what makes the eventual conversion a mechanical change rather than a rewrite.
    /// </summary>
    public static class CrmVisibleOwnerScopeAdapter
    {
        /// <summary>
        /// legacy null → UnrestrictedCompanyScope · legacy empty → NoAccess · legacy populated → RestrictedOwnerIds.
        ///
        /// <paramref name="companyOwnerIds"/>, when supplied, is the set of owners that genuinely belong to the
        /// resolved company. A legacy id outside it is a cross-company leak, so the whole scope becomes a
        /// ConfigurationError rather than being quietly filtered — filtering would hide the defect that produced it.
        /// </summary>
        public static CrmVisibleOwnerScopeResult FromLegacy(
            HashSet<int>? legacy,
            int companyId,
            string decisionSource,
            string reasonCode,
            int? policyId = null,
            IReadOnlyCollection<int>? companyOwnerIds = null)
        {
            if (companyId <= 0)
                return CrmVisibleOwnerScopeResult.Misconfigured(companyId, AuthorizationReasonCodes.CompanyUnresolved);

            // null is the WIDEST answer, not a missing one. This branch is the whole reason the type exists.
            if (legacy == null)
                return CrmVisibleOwnerScopeResult.Unrestricted(companyId, decisionSource, reasonCode, policyId);

            if (legacy.Count == 0)
                return CrmVisibleOwnerScopeResult.NoOwners(companyId, decisionSource, reasonCode);

            if (companyOwnerIds != null && legacy.Any(id => !companyOwnerIds.Contains(id)))
                return CrmVisibleOwnerScopeResult.Misconfigured(companyId, "cross_company_owner_id");

            return CrmVisibleOwnerScopeResult.Restricted(companyId, legacy, decisionSource, reasonCode, policyId);
        }

        /// <summary>
        /// The reverse map, for the transition period when a converted caller must still hand a legacy set to
        /// unconverted code. ConfigurationError maps to an EMPTY set — fail closed — and never to null, which
        /// would turn a misconfiguration into unrestricted company-wide visibility.
        /// </summary>
        public static HashSet<int>? ToLegacy(CrmVisibleOwnerScopeResult scope) => scope.State switch
        {
            CrmVisibleOwnerScopeState.UnrestrictedCompanyScope => null,
            CrmVisibleOwnerScopeState.RestrictedOwnerIds => new HashSet<int>(scope.OwnerIds),
            _ => new HashSet<int>(),   // NoAccess and ConfigurationError both deny
        };

        /// <summary>
        /// Maps a bootstrap decision onto an owner scope, for B6.
        ///
        /// An allowed bootstrap decision reproduces today's behaviour — `null`, i.e. unrestricted — and carries
        /// the policy id and source with it, so a CRM list built under compatibility is identifiable as such
        /// instead of looking like role-authorized access. A denied decision is NoAccess.
        /// </summary>
        public static CrmVisibleOwnerScopeResult FromAuthorizationDecision(AuthorizationDecision decision)
        {
            if (decision == null)
                return CrmVisibleOwnerScopeResult.Misconfigured(0, AuthorizationReasonCodes.CompanyUnresolved);

            if (decision.DecisionSource == AuthorizationDecisionSources.ConfigurationError)
                return CrmVisibleOwnerScopeResult.Misconfigured(decision.CompanyID, decision.ReasonCode);

            return decision.IsAllowed
                ? CrmVisibleOwnerScopeResult.Unrestricted(
                    decision.CompanyID, decision.DecisionSource, decision.ReasonCode, decision.BootstrapPolicyId)
                : CrmVisibleOwnerScopeResult.NoOwners(
                    decision.CompanyID, decision.DecisionSource, decision.ReasonCode);
        }
    }
}
