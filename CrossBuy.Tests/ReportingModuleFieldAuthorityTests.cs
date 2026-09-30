using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CrossBuy.BL;
using CrossBuy.BL.Platform;
using CrossBuy.BL.Reporting;
using CrossBuy.Models.Context.Accounting;
using CrossBuy.Models.Platform;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CrossBuy.Tests
{
    // ==================================================================================================
    // ADR-037 — INVARIANT #12, PROVED AGAINST THE REAL EVALUATOR CHAIN.
    //
    // WHAT THE PREVIOUS PROOF COULD AND COULD NOT SAY. The C2B wave proved that the ARCHIVE layer adds no
    // confidentiality bypass of its own, using an evaluator double, and said plainly that under the
    // registered evaluator the question could not even be posed: RoleMapReportPermissionEvaluator
    // answered TRUE to every key for an administrator role, so reporting administration WAS every
    // module's authority. That double is replaced here, not deleted - these tests build the same chain
    // production builds (adapter over role map, with the real AccountingAccessService behind it) and ask
    // the question for real.
    //
    // THE ONE SENTENCE THESE TESTS EXIST FOR: holding reporting.administer must not hand anyone
    // accounting.reports.profitability, and the refusal must be caused by MISSING MODULE AUTHORITY
    // rather than by missing reporting access - otherwise the boundary is a coincidence.
    // ==================================================================================================
    public class ReportingModuleFieldAuthorityTests
    {
        private const int Company = 1;
        private const int Admin = 7;          // the reporting administrator
        private const int Other = 999;        // somebody else, used only to configure accounting roles

        // ---- the chain, exactly as Program.cs composes it ---------------------------------------------
        private static IReportPermissionEvaluator Chain(ReportingTestHost host, CrossDbContextAccessorPair p)
            => new ReportModulePermissionAdapter(
                new RoleMapReportPermissionEvaluator(host.PermissionOptions),
                new IReportModuleAuthority[] { new AccountingReportModuleAuthority(p.Accounting) });

        private sealed class CrossDbContextAccessorPair
        {
            public required AccountingAccessService Accounting { get; init; }
        }

        private static CrossDbContextAccessorPair Accounting(ReportingTestHost host)
        {
            var http = new HttpContextAccessor();
            var accessor = new BusinessContextAccessor(new BusinessContextFactory(
                http, host.Db, host.Holder, NullLogger<BusinessContextFactory>.Instance));

            return new CrossDbContextAccessorPair
            {
                Accounting = new AccountingAccessService(host.Db, http, accessor,
                    B6TestWiring.Policies(host.Db), NullLogger<AccountingAccessService>.Instance),
            };
        }

        /// A host whose caller holds the reporting-administrator role and nothing else, with the
        /// accounting module CONFIGURED (so its bootstrap-open arm is closed and roles actually decide).
        private static ReportingTestHost AdminHost(params string[] accountingRolesForCaller)
        {
            var host = new ReportingTestHost(companyId: Company, employeeId: Admin, roles: "Admin");
            host.SeedEmployee(Admin, Company);
            host.SeedEmployee(Other, Company);

            // Configuring a role for SOMEBODY ELSE is what closes accounting's bootstrap-open arm for this
            // company. Without it every accounting question falls to the bootstrap policy reader and the
            // role switch is never reached, which would make these assertions prove nothing.
            host.Db.AccountingUserRoles.Add(new AccountingUserRole
            { CompanyID = Company, EmployeeId = Other, Role = "ChiefAccountant" });

            foreach (var role in accountingRolesForCaller)
                host.Db.AccountingUserRoles.Add(new AccountingUserRole
                { CompanyID = Company, EmployeeId = Admin, Role = role });

            host.Db.SaveChanges();
            return host;
        }

        private static BusinessContext Ctx(int employeeId, int companyId, params string[] roles) => new()
        {
            CompanyId = companyId, EmployeeId = employeeId, UserId = "user-" + employeeId,
            Roles = roles, Source = BusinessContextSource.Test,
        };

        // ==============================================================================================
        // F1-F3: reporting administration still administers reporting.
        //
        // The short-circuit is NARROWED, not removed. If these fail the wave has broken the role it was
        // meant to bound, and "admin = false everywhere" is not the fix that was asked for.
        // ==============================================================================================
        [Theory]
        [InlineData(ReportPermissions.Administer)]
        [InlineData(ReportPermissions.AuthorDatasets)]
        [InlineData(ReportPermissions.Public)]
        public async Task F1_a_reporting_administrator_keeps_every_reporting_owned_key(string key)
        {
            using var host = AdminHost();
            var ctx = Ctx(Admin, Company, "Admin");

            Assert.True(await Chain(host, Accounting(host)).HasPermissionAsync(key, ctx));
        }

        // The business-event tiers are reporting-owned too, by the same prefix and with no special case.
        [Fact]
        public async Task F1b_the_namespace_rule_is_generic_not_a_list_of_three_keys()
        {
            using var host = AdminHost();
            var ctx = Ctx(Admin, Company, "Admin");
            var chain = Chain(host, Accounting(host));

            Assert.True(await chain.HasPermissionAsync(BusinessEventsReportPermissions.View, ctx));
            Assert.True(await chain.HasPermissionAsync(BusinessEventsReportPermissions.Confidential, ctx));

            // A key nobody has ever mapped, invented here, still resolves by NAMESPACE rather than by a
            // table of known names - which is what makes the boundary survive the next reporting feature.
            Assert.True(await chain.HasPermissionAsync("reporting.some.future.capability", ctx));
        }

        // ==============================================================================================
        // F4-F5 + INVARIANT #12: the same administrator gets NO accounting authority.
        // ==============================================================================================
        [Theory]
        [InlineData(AccountingReportPermissions.View)]
        [InlineData(AccountingReportPermissions.Profitability)]
        public async Task F4_reporting_administration_does_not_confer_module_authority(string key)
        {
            using var host = AdminHost();                       // no accounting role for the caller
            var ctx = Ctx(Admin, Company, "Admin");

            Assert.False(await Chain(host, Accounting(host)).HasPermissionAsync(key, ctx));
        }

        // THE REFUSAL IS CAUSED BY MISSING MODULE AUTHORITY, NOT BY MISSING REPORTING ACCESS, and that
        // distinction is the whole point: a test that only showed "denied" would pass equally for an
        // engine that had simply stopped granting anything.
        [Fact]
        public async Task F5_the_same_caller_is_allowed_the_moment_the_module_says_yes()
        {
            var ctx = Ctx(Admin, Company, "Admin");

            // Reporting side held constant across both halves: same administrator role, same role map.
            using (var denied = AdminHost())
                Assert.False(await Chain(denied, Accounting(denied))
                    .HasPermissionAsync(AccountingReportPermissions.Profitability, ctx));

            // ONLY the module's answer changes - ChiefAccountant is the accounting role that holds the
            // confidential tier - and the reporting key is granted.
            using var granted = AdminHost("ChiefAccountant");
            Assert.True(await Chain(granted, Accounting(granted))
                .HasPermissionAsync(AccountingReportPermissions.Profitability, ctx));
        }

        // ==============================================================================================
        // F6-F7: the two accounting tiers are independent, and the module decides both.
        // ==============================================================================================
        [Fact]
        public async Task F6_an_accountant_reads_the_registers()
        {
            using var host = AdminHost("Accountant");
            var ctx = Ctx(Admin, Company, "Admin");

            Assert.True(await Chain(host, Accounting(host))
                .HasPermissionAsync(AccountingReportPermissions.View, ctx));
        }

        // PROFITABILITY IS NOT IMPLIED BY REPORT-READ. An Accountant and an Auditor both hold the register
        // tier; neither holds the margin. Mapping one onto the other is the single most tempting
        // simplification here and it would disclose what the company earned to everyone who can read what
        // it billed.
        [Theory]
        [InlineData("Accountant")]
        [InlineData("Auditor")]
        public async Task F7_profitability_is_enforced_independently_of_report_read(string role)
        {
            using var host = AdminHost(role);
            var ctx = Ctx(Admin, Company, "Admin");
            var chain = Chain(host, Accounting(host));

            Assert.True(await chain.HasPermissionAsync(AccountingReportPermissions.View, ctx));
            Assert.False(await chain.HasPermissionAsync(AccountingReportPermissions.Profitability, ctx));
        }

        // ==============================================================================================
        // F8: an ordinary non-administrator is unaffected by any of this.
        // ==============================================================================================
        [Fact]
        public async Task F8_a_non_administrator_behaves_exactly_as_before()
        {
            using var host = AdminHost();
            host.PermissionOptions.RoleMap[InventoryReportPermissions.View] = new[] { "Storekeeper" };
            var ctx = Ctx(Other, Company, "Storekeeper");
            var chain = Chain(host, Accounting(host));

            // A module nobody bridges still answers from the role map, unchanged.
            Assert.True(await chain.HasPermissionAsync(InventoryReportPermissions.View, ctx));
            Assert.False(await chain.HasPermissionAsync(InventoryReportPermissions.Cost, ctx));

            // And a reporting key they were never granted is still refused - the adapter widens nothing.
            Assert.False(await chain.HasPermissionAsync(ReportPermissions.Administer, ctx));
        }

        // ==============================================================================================
        // F8b: THE NARROWING IS WHAT PROVES ITSELF HERE, not the adapter.
        //
        // WHY THIS TEST EXISTS, and it exists because a mutation said so. Restoring the old broad
        // "administrator holds every key" short-circuit SURVIVED the first campaign: for accounting.* the
        // adapter answers before the role map is ever consulted, so the short-circuit had nothing left to
        // do there. The two defences are layered, and the outer one was untested.
        //
        // These are the modules with NO adapter - inventory, org structure, crm - whose keys still fall
        // through to the role map. For them the narrowed short-circuit is the ONLY thing standing between
        // a reporting administrator and another module's confidential column, and an unmapped key must be
        // denied however much reporting authority the caller holds.
        [Theory]
        [InlineData(InventoryReportPermissions.Cost)]
        [InlineData(OrgStructureReportPermissions.Notes)]
        [InlineData(CrmReportPermissions.View)]
        public async Task F8b_an_unmapped_module_key_is_refused_to_a_reporting_administrator(string key)
        {
            using var host = AdminHost();
            var ctx = Ctx(Admin, Company, "Admin");

            // Nothing maps these to the administrator's role, and no module claims their namespace, so the
            // fail-closed role map decides - which it only reaches because the shortcut no longer fires.
            Assert.False(await Chain(host, Accounting(host)).HasPermissionAsync(key, ctx));

            // NON-VACUITY: the identical caller and chain DO get the key once it is mapped, so the refusal
            // above is the boundary and not a chain that refuses everything.
            host.PermissionOptions.RoleMap[key] = new[] { "Admin" };
            Assert.True(await Chain(host, Accounting(host)).HasPermissionAsync(key, ctx));
        }

        // ==============================================================================================
        // F9: an unrecognised key INSIDE a claimed namespace fails closed.
        //
        // Handing it back to the role map would let a typo - or a key invented later and never mapped -
        // be answered by configuration the module never agreed to.
        // ==============================================================================================
        [Fact]
        public async Task F9_an_unknown_module_key_is_refused_even_for_an_administrator()
        {
            using var host = AdminHost("ChiefAccountant");     // every accounting right there is
            var ctx = Ctx(Admin, Company, "Admin");

            // Mapped generously in the role map, and it STILL fails: the namespace is claimed, and the
            // module did not issue this key.
            host.PermissionOptions.RoleMap["accounting.reports.invented"] = new[] { "Admin", "Storekeeper" };

            Assert.False(await Chain(host, Accounting(host))
                .HasPermissionAsync("accounting.reports.invented", ctx));
        }

        // ==============================================================================================
        // F10-F11: the tenant comes from the BusinessContext and from nowhere else.
        // ==============================================================================================
        [Fact]
        public async Task F10_an_unresolved_company_fails_closed()
        {
            using var host = AdminHost("ChiefAccountant");
            var chain = Chain(host, Accounting(host));

            Assert.True(await chain.HasPermissionAsync(
                AccountingReportPermissions.Profitability, Ctx(Admin, Company, "Admin")));   // control

            Assert.False(await chain.HasPermissionAsync(
                AccountingReportPermissions.Profitability, Ctx(Admin, 0, "Admin")));
            Assert.False(await chain.HasPermissionAsync(
                ReportPermissions.Administer, Ctx(Admin, 0, "Admin")));
        }

        // A FOREIGN COMPANY IS NOT A LOOKUP KEY. The caller's accounting role is recorded against company
        // 1; asking as company 2 must not find it, and must not fall back to company 1 either.
        [Fact]
        public async Task F11_a_foreign_company_context_gains_nothing()
        {
            using var host = AdminHost("ChiefAccountant");
            var chain = Chain(host, Accounting(host));

            Assert.True(await chain.HasPermissionAsync(
                AccountingReportPermissions.Profitability, Ctx(Admin, Company, "Admin")));   // control

            Assert.False(await chain.HasPermissionAsync(
                AccountingReportPermissions.Profitability, Ctx(Admin, 2, "Admin")));
        }

        // ==============================================================================================
        // F12-F13: neither side can be used to walk around the other.
        // ==============================================================================================
        [Fact]
        public async Task F12_the_administrator_shortcut_cannot_reach_past_the_adapter()
        {
            using var host = AdminHost();
            var ctx = Ctx(Admin, Company, "Admin");

            // Mapped to the administrator's own role, which under the OLD short-circuit was doubly
            // sufficient. The module is asked anyway, and refuses.
            host.PermissionOptions.RoleMap[AccountingReportPermissions.Profitability] =
                new[] { "Admin", "SuperAdmin" };

            Assert.False(await Chain(host, Accounting(host))
                .HasPermissionAsync(AccountingReportPermissions.Profitability, ctx));
        }

        // AND THE MODULE CANNOT REACH PAST REPORTING EITHER. Holding every accounting role does not make
        // anyone a reporting administrator: this decides one question, and the platform's own keys are
        // not a module's to grant.
        [Fact]
        public async Task F13_module_authority_does_not_confer_reporting_platform_authority()
        {
            using var host = AdminHost("ChiefAccountant", "Accountant", "Auditor", "Cashier");
            var ctx = Ctx(Admin, Company);                     // NO reporting-administrator role
            var chain = Chain(host, Accounting(host));

            Assert.True(await chain.HasPermissionAsync(AccountingReportPermissions.Profitability, ctx));
            Assert.False(await chain.HasPermissionAsync(ReportPermissions.Administer, ctx));
            Assert.False(await chain.HasPermissionAsync(ReportPermissions.AuthorDatasets, ctx));
        }

        // ==============================================================================================
        // THE PREFIX IS THE BOUNDARY, and it is pinned so a later edit cannot quietly move it.
        // ==============================================================================================
        [Theory]
        [InlineData(ReportPermissions.Administer, true)]
        [InlineData(ReportPermissions.AuthorDatasets, true)]
        [InlineData(ReportPermissions.Public, true)]
        [InlineData(AccountingReportPermissions.View, false)]
        [InlineData(AccountingReportPermissions.Profitability, false)]
        [InlineData(InventoryReportPermissions.Cost, false)]
        [InlineData(CrmReportPermissions.View, false)]
        [InlineData(OrgStructureReportPermissions.Notes, false)]
        public void The_reporting_namespace_claims_exactly_the_platforms_own_keys(string key, bool owned)
        {
            Assert.Equal(owned, key.StartsWith(
                RoleMapReportPermissionEvaluator.ReportingKeyPrefix, StringComparison.Ordinal));
        }

        // ==============================================================================================
        // TAB-5 HANDOFF.
        //
        // What this asserts is a CAPABILITY, not a dataset: that a test can now stand a caller up on one
        // side of a real module-confidentiality boundary and observe the other. Before this wave every
        // administrator was on both sides of it at once, so an isolation or drill-down test could not
        // distinguish "the boundary held" from "the caller was an administrator". TAB-5 can.
        // ==============================================================================================
        [Fact]
        public async Task TAB5_HANDOFF_a_real_module_confidentiality_boundary_is_observable()
        {
            var ctx = Ctx(Admin, Company, "Admin");

            using var below = AdminHost("Accountant");          // may read registers, not margin
            using var above = AdminHost("ChiefAccountant");     // may read both

            var b = Chain(below, Accounting(below));
            var a = Chain(above, Accounting(above));

            // Same caller, same reporting rights, same key - two answers, decided by the MODULE.
            Assert.False(await b.HasPermissionAsync(AccountingReportPermissions.Profitability, ctx));
            Assert.True(await a.HasPermissionAsync(AccountingReportPermissions.Profitability, ctx));

            // And the tier below is held constant across the boundary, so a drill-down test can vary one
            // thing at a time.
            Assert.True(await b.HasPermissionAsync(AccountingReportPermissions.View, ctx));
            Assert.True(await a.HasPermissionAsync(AccountingReportPermissions.View, ctx));
        }
    }
}
