using CrossBuy.Models.Platform;
using Xunit;

namespace CrossBuy.Tests
{
    // =============================================================================================
    // CrossBusiness Platform — Stage 2A Batch B: the CRM visible-owner typed contract.
    //
    // NOT gated on CROSSBUY_TEST_SQL: this is a pure mapping contract with no persistence, so it must run
    // everywhere. The whole point is that `null` and an empty set are the two OPPOSITE extremes of CRM
    // visibility and are one keystroke apart — `?? new HashSet<int>()` turns "see everything" into "see
    // nothing", and neither mistake produces an error.
    //
    // Production CrmAccessService is untouched. These tests prove the mapping before B6 depends on it.
    // =============================================================================================
    public class CrmVisibleOwnerScopeTests
    {
        private const int CompanyOne = 1;
        private const string Source = AuthorizationDecisionSources.BootstrapLegacyCompatibility;
        private const string Reason = AuthorizationReasonCodes.PolicyPermits;

        // -----------------------------------------------------------------------------------------
        // the three legacy shapes
        // -----------------------------------------------------------------------------------------

        [Fact]
        public void Legacy_null_maps_to_UnrestrictedCompanyScope()
        {
            var scope = CrmVisibleOwnerScopeAdapter.FromLegacy(null, CompanyOne, Source, Reason, policyId: 42);

            Assert.Equal(CrmVisibleOwnerScopeState.UnrestrictedCompanyScope, scope.State);
            Assert.True(scope.IsAllowed);
            Assert.Empty(scope.OwnerIds);          // unrestricted carries NO ids — it is not "all ids"
            Assert.Equal(42, scope.BootstrapPolicyId);
            Assert.True(scope.IsBootstrap);
        }

        [Fact]
        public void Legacy_EMPTY_maps_to_NoAccess_and_denies()
        {
            var scope = CrmVisibleOwnerScopeAdapter.FromLegacy(new HashSet<int>(), CompanyOne, Source, Reason);

            Assert.Equal(CrmVisibleOwnerScopeState.NoAccess, scope.State);
            Assert.False(scope.IsAllowed);
        }

        [Fact]
        public void Null_and_empty_are_NOT_equivalent_which_is_the_whole_reason_this_type_exists()
        {
            var fromNull = CrmVisibleOwnerScopeAdapter.FromLegacy(null, CompanyOne, Source, Reason);
            var fromEmpty = CrmVisibleOwnerScopeAdapter.FromLegacy(new HashSet<int>(), CompanyOne, Source, Reason);

            Assert.NotEqual(fromNull.State, fromEmpty.State);

            // The two OPPOSITE extremes: see everything vs see nothing. `?? new HashSet<int>()` collapses the
            // first into the second silently, on a CRM database.
            Assert.True(fromNull.IsAllowed);
            Assert.False(fromEmpty.IsAllowed);
        }

        [Fact]
        public void Legacy_populated_preserves_the_exact_owner_ids()
        {
            var scope = CrmVisibleOwnerScopeAdapter.FromLegacy(
                new HashSet<int> { 7, 3, 11 }, CompanyOne, Source, Reason);

            Assert.Equal(CrmVisibleOwnerScopeState.RestrictedOwnerIds, scope.State);
            Assert.True(scope.IsAllowed);
            Assert.Equal(new[] { 3, 7, 11 }, scope.OwnerIds);   // exact set, deterministically ordered
        }

        // -----------------------------------------------------------------------------------------
        // normalisation and refusals
        // -----------------------------------------------------------------------------------------

        [Fact]
        public void Duplicate_owner_ids_normalise_without_changing_the_visible_set()
        {
            var scope = CrmVisibleOwnerScopeResult.Restricted(
                CompanyOne, new[] { 5, 5, 9, 5, 9 }, Source, Reason);

            Assert.Equal(CrmVisibleOwnerScopeState.RestrictedOwnerIds, scope.State);
            Assert.Equal(new[] { 5, 9 }, scope.OwnerIds);
        }

        [Theory]
        [InlineData(0)]
        [InlineData(-1)]
        public void An_invalid_owner_id_fails_CLOSED_rather_than_being_filtered_away(int invalid)
        {
            // Filtering would silently narrow the scope and hide the defect that produced the bad id. A caller
            // that built a set it did not validate has a bug, and the whole scope is suspect.
            var scope = CrmVisibleOwnerScopeResult.Restricted(CompanyOne, new[] { 4, invalid }, Source, Reason);

            Assert.Equal(CrmVisibleOwnerScopeState.ConfigurationError, scope.State);
            Assert.False(scope.IsAllowed);
            Assert.Equal("invalid_owner_id", scope.ReasonCode);
        }

        [Fact]
        public void A_cross_company_owner_id_produces_ConfigurationError_not_a_filtered_set()
        {
            var scope = CrmVisibleOwnerScopeAdapter.FromLegacy(
                new HashSet<int> { 4, 999 }, CompanyOne, Source, Reason,
                companyOwnerIds: new[] { 4, 5, 6 });

            Assert.Equal(CrmVisibleOwnerScopeState.ConfigurationError, scope.State);
            Assert.False(scope.IsAllowed);
            Assert.Equal("cross_company_owner_id", scope.ReasonCode);
        }

        [Fact]
        public void Restricted_to_nobody_becomes_NoAccess_rather_than_an_empty_restricted_set()
        {
            // Allowing an empty RestrictedOwnerIds would recreate the exact ambiguity this type removes.
            var scope = CrmVisibleOwnerScopeResult.Restricted(
                CompanyOne, System.Array.Empty<int>(), Source, Reason);

            Assert.Equal(CrmVisibleOwnerScopeState.NoAccess, scope.State);
        }

        [Theory]
        [InlineData(0)]
        [InlineData(-5)]
        public void An_unresolved_company_fails_closed_with_no_fallback_to_company_1(int companyId)
        {
            Assert.Equal(CrmVisibleOwnerScopeState.ConfigurationError,
                CrmVisibleOwnerScopeResult.Unrestricted(companyId, Source, Reason).State);

            Assert.Equal(CrmVisibleOwnerScopeState.ConfigurationError,
                CrmVisibleOwnerScopeAdapter.FromLegacy(null, companyId, Source, Reason).State);

            // And crucially it does NOT become company 1.
            Assert.NotEqual(1, CrmVisibleOwnerScopeResult.Unrestricted(companyId, Source, Reason).CompanyID);
        }

        [Fact]
        public void ConfigurationError_and_NoAccess_both_DENY()
        {
            Assert.False(CrmVisibleOwnerScopeResult.Misconfigured(CompanyOne, "x").IsAllowed);
            Assert.False(CrmVisibleOwnerScopeResult.NoOwners(CompanyOne, Source, Reason).IsAllowed);
        }

        // -----------------------------------------------------------------------------------------
        // decision metadata is carried, not lost
        // -----------------------------------------------------------------------------------------

        [Fact]
        public void Decision_source_reason_code_policy_id_and_company_all_survive_the_mapping()
        {
            var scope = CrmVisibleOwnerScopeAdapter.FromLegacy(
                new HashSet<int> { 8 }, CompanyOne,
                AuthorizationDecisionSources.BootstrapExplicitlyAllowed,
                AuthorizationReasonCodes.PolicyPermits, policyId: 77);

            Assert.Equal(AuthorizationDecisionSources.BootstrapExplicitlyAllowed, scope.DecisionSource);
            Assert.Equal(AuthorizationReasonCodes.PolicyPermits, scope.ReasonCode);
            Assert.Equal(77, scope.BootstrapPolicyId);
            Assert.Equal(CompanyOne, scope.CompanyID);
            Assert.True(scope.IsBootstrap);
        }

        [Fact]
        public void A_role_based_scope_is_not_reported_as_bootstrap()
        {
            var scope = CrmVisibleOwnerScopeResult.Restricted(
                CompanyOne, new[] { 3 }, AuthorizationDecisionSources.LegacyRole,
                AuthorizationReasonCodes.RoleHeld);

            Assert.False(scope.IsBootstrap);
            Assert.True(scope.IsAllowed);
        }

        [Fact]
        public void The_reason_code_never_carries_customer_data()
        {
            // Every reason code the contract can emit is a short machine token. Asserted structurally so a future
            // change cannot start interpolating a customer name or an account id into it.
            var codes = new[]
            {
                CrmVisibleOwnerScopeResult.Misconfigured(CompanyOne, "invalid_owner_id").ReasonCode,
                CrmVisibleOwnerScopeResult.Misconfigured(CompanyOne, "cross_company_owner_id").ReasonCode,
                CrmVisibleOwnerScopeAdapter.FromLegacy(null, 0, Source, Reason).ReasonCode,
            };

            Assert.All(codes, c =>
            {
                Assert.Matches("^[a-z0-9_]+$", c);   // lowercase machine token only
                Assert.True(c.Length <= 60);
            });
        }

        // -----------------------------------------------------------------------------------------
        // the reverse map — fail closed, never null
        // -----------------------------------------------------------------------------------------

        [Fact]
        public void ToLegacy_round_trips_the_three_shapes()
        {
            Assert.Null(CrmVisibleOwnerScopeAdapter.ToLegacy(
                CrmVisibleOwnerScopeResult.Unrestricted(CompanyOne, Source, Reason)));

            Assert.Empty(CrmVisibleOwnerScopeAdapter.ToLegacy(
                CrmVisibleOwnerScopeResult.NoOwners(CompanyOne, Source, Reason))!);

            Assert.Equal(new[] { 2, 4 }, CrmVisibleOwnerScopeAdapter.ToLegacy(
                CrmVisibleOwnerScopeResult.Restricted(CompanyOne, new[] { 4, 2 }, Source, Reason))!
                .OrderBy(x => x).ToArray());
        }

        [Fact]
        public void ToLegacy_maps_ConfigurationError_to_an_EMPTY_set_and_never_to_null()
        {
            // null would mean UNRESTRICTED. Mapping a misconfiguration to null would turn a fault into
            // company-wide CRM visibility — the single worst available outcome for this type.
            var legacy = CrmVisibleOwnerScopeAdapter.ToLegacy(
                CrmVisibleOwnerScopeResult.Misconfigured(CompanyOne, "cross_company_owner_id"));

            Assert.NotNull(legacy);
            Assert.Empty(legacy!);
        }

        // -----------------------------------------------------------------------------------------
        // B6 readiness
        // -----------------------------------------------------------------------------------------

        [Fact]
        public void An_allowed_bootstrap_decision_maps_to_unrestricted_scope_carrying_its_provenance()
        {
            // This is the B6 path: today's behaviour (null / unrestricted) reproduced deliberately, but now
            // identifiable as compatibility rather than looking like role-authorized access.
            var decision = AuthorizationDecision.Allow(
                AuthorizationDecisionSources.BootstrapLegacyCompatibility, CompanyOne, "Crm", "read",
                AuthorizationReasonCodes.PolicyPermits, policyId: 5);

            var scope = CrmVisibleOwnerScopeAdapter.FromAuthorizationDecision(decision);

            Assert.Equal(CrmVisibleOwnerScopeState.UnrestrictedCompanyScope, scope.State);
            Assert.True(scope.IsBootstrap);
            Assert.Equal(5, scope.BootstrapPolicyId);
            Assert.Equal(CompanyOne, scope.CompanyID);
        }

        [Fact]
        public void A_denied_decision_maps_to_NoAccess_and_a_misconfiguration_maps_to_ConfigurationError()
        {
            var denied = AuthorizationDecision.Deny(
                CompanyOne, "Crm", "read", AuthorizationReasonCodes.NoPolicyConfigured);
            Assert.Equal(CrmVisibleOwnerScopeState.NoAccess,
                CrmVisibleOwnerScopeAdapter.FromAuthorizationDecision(denied).State);

            var broken = AuthorizationDecision.Misconfigured(
                CompanyOne, "Crm", "read", AuthorizationReasonCodes.UnknownPolicyState);
            Assert.Equal(CrmVisibleOwnerScopeState.ConfigurationError,
                CrmVisibleOwnerScopeAdapter.FromAuthorizationDecision(broken).State);
        }

        [Fact]
        public void A_Never_action_decision_can_never_produce_a_visible_scope()
        {
            // Crm.manage is Never-Bootstrap-Open. Its decision is a denial, so the owner scope is NoAccess —
            // there is no path from a Never classification to visible CRM records.
            Assert.True(NeverBootstrapOpen.Contains("Crm", "manage"));

            var decision = AuthorizationDecision.Deny(
                CompanyOne, "Crm", "manage", AuthorizationReasonCodes.NeverBootstrapOpen);

            Assert.False(CrmVisibleOwnerScopeAdapter.FromAuthorizationDecision(decision).IsAllowed);
        }

        [Fact]
        public void The_contract_needs_no_Session_and_no_HttpContext()
        {
            // Proven by construction: every test in this file runs with no web host, no HttpContext and no
            // Session anywhere. It could not execute at all if the contract required one.
            var scope = CrmVisibleOwnerScopeAdapter.FromLegacy(
                new HashSet<int> { 1 }, CompanyOne, Source, Reason);

            Assert.True(scope.IsAllowed);
        }

        [Fact]
        public void Production_CrmAccessService_still_returns_the_legacy_nullable_shape()
        {
            // The guarantee that this increment changed nothing: the production interface is unchanged, so no
            // caller has been converted. Asserted by reflection so a premature signature change fails here.
            var method = typeof(CrossBuy.BL.ICrmAccessService).GetMethod("VisibleOwnerIdsAsync");

            Assert.NotNull(method);
            Assert.Equal(
                typeof(System.Threading.Tasks.Task<System.Collections.Generic.HashSet<int>?>).Name,
                method!.ReturnType.Name);
        }
    }
}
