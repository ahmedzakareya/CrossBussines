using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CrossBuy.BL;
using CrossBuy.BL.Platform;
using CrossBuy.BL.Reporting;
using CrossBuy.Models.Context.Accounting;
using CrossBuy.Models.Context.Admin;
using CrossBuy.Models.Context.Platform;
using CrossBuy.Models.Platform;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using static CrossBuy.Tests.B6TestWiring;

namespace CrossBuy.Tests
{
    // ==================================================================================================
    // WORK AND PROJECTS REPORTING AUTHORITY — ADR-037, proved against the REAL chain.
    //
    // WHAT WOULD MAKE THESE TESTS WORTHLESS, and is therefore avoided everywhere below:
    //
    //   * ASSERTING A GRANT. "A TasksAdministrator may open a work report" is true of almost any wiring,
    //     including a wiring that grants everyone. Every positive here is paired with a negative that
    //     differs in ONE fact, so the assertion encodes WHICH fact decided.
    //   * A DOUBLE FOR THE MODULE. The whole claim of this wave is that the MODULE decides, so a fake
    //     IModuleAccessService would prove only that the adapter calls something. The real
    //     TasksAccessService and ProjectsAccessService are behind the chain in every test.
    //   * A SYMMETRIC COMPANY FIXTURE. Company-isolation assertions are asymmetric on purpose: the
    //     grant exists in exactly one company, so inverting the directory's predicate flips a result.
    // ==================================================================================================
    public class WorkProjectsReportAuthorityTests
    {
        private const int CompanyOne = 1;
        private const int CompanyTwo = 2;
        private const int Caller = 10;
        private const int Somebody = 999;   // configures a scope so its bootstrap-open arm is CLOSED

        // ---- fixtures ---------------------------------------------------------------------------------

        private static Employee Emp(int id, int companyId) => new()
        {
            ID = id, FirstName = "T", LastName = "T", FullName = "emp" + id, FullNameEn = "emp" + id,
            EmpCompanyID = companyId, IsActive = true, Address = "-", PhoneNumber = "-",
            Email = $"e{id}@example.com", ProfileImage = "-", Gender = "M", MaritalStatus = "S",
            UserId = "user-" + id,
        };

        private static BusinessContext Ctx(int employeeId, int companyId, params string[] roles) => new()
        {
            CompanyId = companyId, EmployeeId = employeeId, UserId = "user-" + employeeId,
            Roles = roles, Source = BusinessContextSource.Test, CorrelationId = Guid.NewGuid(),
        };

        private static IPlatformRoleDirectory Directory(PlatformTestHost host)
            => new PlatformRoleDirectory(host.Db, NullLogger<PlatformRoleDirectory>.Instance);

        private static IOrgHierarchy Org(PlatformTestHost host)
            => new OrgHierarchy(host.Db, NullLogger<OrgHierarchy>.Instance);

        private sealed class AllowAllProvider : IPlatformPermissionProvider
        {
            public Task<PermissionDecision> CanAsync(BusinessContext c, string entityType, int entityId,
                string action, CancellationToken ct = default)
                => Task.FromResult(PermissionDecision.Allow("test"));
        }

        private static TasksAccessService Tasks(PlatformTestHost host)
        {
            var provider = new AllowAllProvider();
            return new TasksAccessService(host.Db, Directory(host), Org(host),
                () => provider, NullLogger<TasksAccessService>.Instance);
        }

        private static AccountingAccessService Accounting(PlatformTestHost host)
        {
            var http = new Microsoft.AspNetCore.Http.HttpContextAccessor();
            var accessor = new BusinessContextAccessor(new BusinessContextFactory(
                http, host.Db, host.Holder, NullLogger<BusinessContextFactory>.Instance));
            return new AccountingAccessService(host.Db, http, accessor, Policies(host.Db),
                NullLogger<AccountingAccessService>.Instance);
        }

        private static ProjectsAccessService Projects(PlatformTestHost host)
            => new(host.Db, Directory(host), Accounting(host), NullLogger<ProjectsAccessService>.Instance);

        private static CalendarAccessService Calendar(PlatformTestHost host)
            => new(host.Db, Directory(host), Org(host), NullLogger<CalendarAccessService>.Instance);

        // The role map carries the modules that have NO authority of their own, so the regression tests
        // below assert they still answer exactly as they did before this wave.
        private static ReportPermissionOptions Options() => new()
        {
            AdministratorRoles = new[] { "Admin", "SuperAdmin" },
            RoleMap = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
            {
                ["crm.reports.view"] = new[] { "CrmUser" },
                ["inventory.reports.view"] = new[] { "StoreKeeper" },
                ["roster.reports.view"] = new[] { "HrPlanner" },

                // DELIBERATELY POISONED ENTRIES, inside namespaces the modules claim. Configuration must
                // not be able to grant a module's key - see the test that names them. Without these the
                // "unmapped key" tests pass for the wrong reason: an unmapped key is also denied by the
                // role map itself (AllowUnmappedPermissions is false), so a fall-through bug would be
                // invisible. These make the two layers disagree, which is the only way to tell them apart.
                ["work.reports.legacy"] = new[] { "Admin" },
                ["projects.reports.legacy"] = new[] { "Admin" },
            },
        };

        /// The chain EXACTLY as Program.cs composes it: the role-map evaluator underneath, and the three
        /// module authorities over it. Accounting is included in every test so its presence, and the
        /// prefix resolution between three claimants, is under test rather than assumed.
        private static IReportPermissionEvaluator Chain(PlatformTestHost host) =>
            new ReportModulePermissionAdapter(
                new RoleMapReportPermissionEvaluator(Options()),
                new IReportModuleAuthority[]
                {
                    new AccountingReportModuleAuthority(Accounting(host)),
                    new WorkReportModuleAuthority(Tasks(host)),
                    new ProjectsReportModuleAuthority(Projects(host)),
                });

        private static async Task GrantAsync(PlatformTestHost host, int companyId, string scope,
            int employeeId, string role)
        {
            host.Seed.PlatformRoleAssignments.Add(new PlatformRoleAssignment
            {
                CompanyID = companyId, Scope = scope, PrincipalType = PlatformPrincipalTypes.Employee,
                PrincipalId = employeeId, Role = role, IsActive = true, CreatedAt = DateTime.UtcNow,
            });
            await host.Seed.SaveChangesAsync();
        }

        private static PlatformTestHost Host()
        {
            var host = new PlatformTestHost(companyId: CompanyOne);
            host.Seed.Employee.Add(Emp(Caller, CompanyOne));
            host.Seed.Employee.Add(Emp(Somebody, CompanyOne));
            host.Seed.SaveChanges();
            return host;
        }

        // ==============================================================================================
        // 1-4. THE MODULE DECIDES — the positive and its one-fact-different negative.
        // ==============================================================================================

        [Fact] // 1
        public async Task Work_reporting_is_denied_when_the_tasks_module_refuses()
        {
            using var host = Host();
            // A role for SOMEBODY ELSE closes the Tasks bootstrap-open arm, so the role switch really
            // decides. Without this the caller would be allowed by compatibility and prove nothing.
            await GrantAsync(host, CompanyOne, EntityRegistry.ScopeTasks, Somebody, TasksRoles.TasksViewer);

            Assert.False(await Chain(host).HasPermissionAsync(
                WorkReportPermissions.View, Ctx(Caller, CompanyOne)));
        }

        [Fact] // 2
        public async Task Work_reporting_is_allowed_only_once_the_tasks_module_grants_it()
        {
            using var host = Host();
            await GrantAsync(host, CompanyOne, EntityRegistry.ScopeTasks, Somebody, TasksRoles.TasksViewer);
            var ctx = Ctx(Caller, CompanyOne);

            Assert.False(await Chain(host).HasPermissionAsync(WorkReportPermissions.View, ctx));

            // THE ONE FACT THAT CHANGES. Nothing about reporting is touched.
            await GrantAsync(host, CompanyOne, EntityRegistry.ScopeTasks, Caller, TasksRoles.TasksViewer);

            Assert.True(await Chain(host).HasPermissionAsync(WorkReportPermissions.View, ctx));
        }

        [Fact] // 3
        public async Task Projects_reporting_is_denied_when_the_projects_module_refuses()
        {
            using var host = Host();
            await GrantAsync(host, CompanyOne, EntityRegistry.ScopeProjects, Somebody, ProjectsRoles.ProjectsViewer);

            Assert.False(await Chain(host).HasPermissionAsync(
                ProjectsReportPermissions.View, Ctx(Caller, CompanyOne)));
        }

        [Fact] // 4
        public async Task Projects_reporting_is_allowed_only_once_the_projects_module_grants_it()
        {
            using var host = Host();
            await GrantAsync(host, CompanyOne, EntityRegistry.ScopeProjects, Somebody, ProjectsRoles.ProjectsViewer);
            var ctx = Ctx(Caller, CompanyOne);

            Assert.False(await Chain(host).HasPermissionAsync(ProjectsReportPermissions.View, ctx));

            await GrantAsync(host, CompanyOne, EntityRegistry.ScopeProjects, Caller, ProjectsRoles.ProjectsViewer);

            Assert.True(await Chain(host).HasPermissionAsync(ProjectsReportPermissions.View, ctx));
        }

        // ==============================================================================================
        // 5-6. ADMIN BYPASS. The sentence this whole wave exists for.
        // ==============================================================================================

        [Fact] // 5
        public async Task Reporting_administration_alone_does_not_confer_work_authority()
        {
            using var host = Host();
            await GrantAsync(host, CompanyOne, EntityRegistry.ScopeTasks, Somebody, TasksRoles.TasksViewer);

            // Every reporting right there is, and no Tasks role.
            var admin = Ctx(Caller, CompanyOne, "Admin", "SuperAdmin");
            var chain = Chain(host);

            Assert.False(await chain.HasPermissionAsync(WorkReportPermissions.View, admin));

            // AND THE REFUSAL IS CAUSED BY MISSING MODULE AUTHORITY, not by missing reporting access —
            // otherwise the boundary would be a coincidence. The same caller keeps reporting's own keys.
            Assert.True(await chain.HasPermissionAsync(ReportPermissions.Administer, admin));
        }

        [Fact] // 6
        public async Task Reporting_administration_alone_does_not_confer_projects_authority()
        {
            using var host = Host();
            await GrantAsync(host, CompanyOne, EntityRegistry.ScopeProjects, Somebody, ProjectsRoles.ProjectsViewer);

            var admin = Ctx(Caller, CompanyOne, "Admin", "SuperAdmin");
            var chain = Chain(host);

            Assert.False(await chain.HasPermissionAsync(ProjectsReportPermissions.View, admin));
            Assert.True(await chain.HasPermissionAsync(ReportPermissions.Administer, admin));
        }

        // ==============================================================================================
        // 7-8. UNRESOLVED COMPANY. No fallback to company 1, for a caller who otherwise WOULD pass.
        // ==============================================================================================

        [Fact] // 7
        public async Task An_unresolved_company_denies_work_reporting_even_for_a_role_holder()
        {
            using var host = Host();
            await GrantAsync(host, CompanyOne, EntityRegistry.ScopeTasks, Caller, TasksRoles.TasksAdministrator);
            var chain = Chain(host);

            // Resolved: allowed. Unresolved: refused. Same employee, same grant, same key.
            Assert.True(await chain.HasPermissionAsync(WorkReportPermissions.View, Ctx(Caller, CompanyOne)));
            Assert.False(await chain.HasPermissionAsync(WorkReportPermissions.View, Ctx(Caller, 0)));
            Assert.False(await chain.HasPermissionAsync(WorkReportPermissions.View, Ctx(Caller, -1)));
        }

        [Fact] // 8
        public async Task An_unresolved_company_denies_projects_reporting_even_for_a_role_holder()
        {
            using var host = Host();
            await GrantAsync(host, CompanyOne, EntityRegistry.ScopeProjects, Caller, ProjectsRoles.ProjectsAdministrator);
            var chain = Chain(host);

            Assert.True(await chain.HasPermissionAsync(ProjectsReportPermissions.View, Ctx(Caller, CompanyOne)));
            Assert.False(await chain.HasPermissionAsync(ProjectsReportPermissions.View, Ctx(Caller, 0)));
            Assert.False(await chain.HasPermissionAsync(ProjectsReportPermissions.View, Ctx(Caller, -1)));
        }

        // ==============================================================================================
        // 9. CROSS-COMPANY. Asymmetric by construction: the grant exists in company TWO only.
        // ==============================================================================================

        [Fact] // 9
        public async Task A_grant_in_another_company_never_answers_this_companys_reporting_question()
        {
            using var host = Host();
            await GrantAsync(host, CompanyTwo, EntityRegistry.ScopeTasks, Caller, TasksRoles.TasksAdministrator);
            await GrantAsync(host, CompanyTwo, EntityRegistry.ScopeProjects, Caller, ProjectsRoles.ProjectsAdministrator);
            var chain = Chain(host);

            // Company TWO is where the roles are, so company TWO answers yes...
            Assert.True(await chain.HasPermissionAsync(WorkReportPermissions.View, Ctx(Caller, CompanyTwo)));
            Assert.True(await chain.HasPermissionAsync(ProjectsReportPermissions.View, Ctx(Caller, CompanyTwo)));

            // ...and company ONE does not, for the same employee holding the same identity. Company ONE
            // has a Tasks/Projects role configured (for Somebody, below) so it is not bootstrap-open.
            await GrantAsync(host, CompanyOne, EntityRegistry.ScopeTasks, Somebody, TasksRoles.TasksViewer);
            await GrantAsync(host, CompanyOne, EntityRegistry.ScopeProjects, Somebody, ProjectsRoles.ProjectsViewer);

            Assert.False(await Chain(host).HasPermissionAsync(WorkReportPermissions.View, Ctx(Caller, CompanyOne)));
            Assert.False(await Chain(host).HasPermissionAsync(ProjectsReportPermissions.View, Ctx(Caller, CompanyOne)));
        }

        // ==============================================================================================
        // 10-11. UNKNOWN KEY INSIDE A CLAIMED NAMESPACE FAILS CLOSED.
        //
        // The caller here holds EVERY relevant right — module administration AND reporting administration
        // — so a pass would be caused by the key being unrecognised, not by a weak caller.
        // ==============================================================================================

        [Theory] // 10
        [InlineData("work.reports.invented")]
        [InlineData("work.reports.calendar")]   // the carve-out: real, unmapped, and therefore REFUSED
        [InlineData("work.reports.")]
        [InlineData("work.anything")]
        [InlineData("work.")]
        public async Task An_unmapped_key_in_the_work_namespace_is_refused_not_delegated(string key)
        {
            using var host = Host();
            await GrantAsync(host, CompanyOne, EntityRegistry.ScopeTasks, Caller, TasksRoles.TasksAdministrator);
            var chain = Chain(host);
            var ctx = Ctx(Caller, CompanyOne, "Admin", "SuperAdmin");

            // The mapped key passes for this caller, so the refusal below is about the KEY.
            Assert.True(await chain.HasPermissionAsync(WorkReportPermissions.View, ctx));
            Assert.False(await chain.HasPermissionAsync(key, ctx));
        }

        [Theory] // 11
        [InlineData("projects.reports.invented")]
        [InlineData("projects.reports.budget")]  // budget-view is a SEPARATE module right, never mapped here
        [InlineData("projects.reports.")]
        [InlineData("projects.anything")]
        [InlineData("projects.")]
        public async Task An_unmapped_key_in_the_projects_namespace_is_refused_not_delegated(string key)
        {
            using var host = Host();
            await GrantAsync(host, CompanyOne, EntityRegistry.ScopeProjects, Caller, ProjectsRoles.ProjectsAdministrator);
            var chain = Chain(host);
            var ctx = Ctx(Caller, CompanyOne, "Admin", "SuperAdmin");

            Assert.True(await chain.HasPermissionAsync(ProjectsReportPermissions.View, ctx));
            Assert.False(await chain.HasPermissionAsync(key, ctx));
        }

        [Theory] // 10 + 11, the half that is not redundant with the role map
        [InlineData("work.reports.legacy")]
        [InlineData("projects.reports.legacy")]
        public async Task A_role_map_entry_cannot_grant_a_key_inside_a_claimed_namespace(string key)
        {
            using var host = Host();
            // Both modules are CONFIGURED for somebody else, so neither grants this caller anything.
            await GrantAsync(host, CompanyOne, EntityRegistry.ScopeTasks, Somebody, TasksRoles.TasksViewer);
            await GrantAsync(host, CompanyOne, EntityRegistry.ScopeProjects, Somebody, ProjectsRoles.ProjectsViewer);

            // The key IS in the role map and the caller DOES hold the role it maps to...
            Assert.Contains(key, Options().RoleMap.Keys);

            // ...and it is STILL refused, because the module owns the namespace and never issued this key.
            // This is the assertion that distinguishes "the module refused" from "the role map happened to
            // have nothing mapped": a fall-through to the map would answer true here.
            Assert.False(await Chain(host).HasPermissionAsync(key, Ctx(Caller, CompanyOne, "Admin")));
        }

        // ==============================================================================================
        // 12-13. ACCOUNTING IS UNCHANGED BY THE TWO NEW CLAIMANTS.
        // ==============================================================================================

        [Fact] // 12 + 13
        public async Task Accounting_keeps_its_tiers_with_three_authorities_registered()
        {
            using var host = Host();
            // Close accounting's bootstrap-open arm for this company.
            host.Seed.AccountingUserRoles.Add(new AccountingUserRole
            { CompanyID = CompanyOne, EmployeeId = Somebody, Role = "ChiefAccountant" });
            await host.Seed.SaveChangesAsync();

            var chain = Chain(host);
            var admin = Ctx(Caller, CompanyOne, "Admin", "SuperAdmin");

            // A reporting administrator with no accounting role holds NEITHER accounting tier — the ADR-037
            // defect, still closed with Work and Projects now claiming namespaces beside Accounting.
            Assert.False(await chain.HasPermissionAsync(AccountingReportPermissions.View, admin));
            Assert.False(await chain.HasPermissionAsync(AccountingReportPermissions.Profitability, admin));

            // An accountant holds the ordinary tier and NOT the profitability tier — the two stay separate.
            host.Seed.AccountingUserRoles.Add(new AccountingUserRole
            { CompanyID = CompanyOne, EmployeeId = Caller, Role = "Accountant" });
            await host.Seed.SaveChangesAsync();

            var fresh = Chain(host);
            var accountant = Ctx(Caller, CompanyOne);
            Assert.True(await fresh.HasPermissionAsync(AccountingReportPermissions.View, accountant));
            Assert.False(await fresh.HasPermissionAsync(AccountingReportPermissions.Profitability, accountant));
        }

        // ==============================================================================================
        // 14-16. THE ROLE-MAP MODULES STILL ANSWER FROM THE ROLE MAP.
        //
        // crm., inventory. and roster. are claimed by NOBODY, so the adapter must still hand them to the
        // role-map evaluator. A prefix bug in either new authority would capture one of them and turn a
        // configured grant into a silent refusal.
        // ==============================================================================================

        [Theory] // 14, 15, 16
        [InlineData("crm.reports.view", "CrmUser")]
        [InlineData("inventory.reports.view", "StoreKeeper")]
        [InlineData("roster.reports.view", "HrPlanner")]
        public async Task An_unclaimed_module_namespace_still_answers_from_the_role_map(string key, string role)
        {
            using var host = Host();
            var chain = Chain(host);

            Assert.True(await chain.HasPermissionAsync(key, Ctx(Caller, CompanyOne, role)));
            Assert.False(await chain.HasPermissionAsync(key, Ctx(Caller, CompanyOne, "SomeOtherRole")));
        }

        // ==============================================================================================
        // 17-18. AN EXTERNAL PORTAL IDENTITY IS NOT AN EMPLOYEE.
        //
        // A Client Portal login has no employee at all, so it holds no module role and cannot hold a
        // reporting key that projects one. Asserted against a company that IS bootstrap-open, which is the
        // most permissive state the modules ever reach — if the refusal survives there it survives anywhere.
        // ==============================================================================================

        [Fact] // 17 + 18
        public async Task An_external_portal_context_cannot_satisfy_internal_work_or_projects_authority()
        {
            using var host = Host();
            var chain = Chain(host);

            var portal = new BusinessContext
            {
                CompanyId = CompanyOne, EmployeeId = null, UserId = "portal-user",
                Roles = new[] { "Admin", "SuperAdmin" }, Source = BusinessContextSource.ExternalPortal,
            };

            Assert.False(await chain.HasPermissionAsync(WorkReportPermissions.View, portal));
            Assert.False(await chain.HasPermissionAsync(ProjectsReportPermissions.View, portal));

            // The company IS bootstrap-open, proved by an EMPLOYEE in the same company passing.
            Assert.True(await chain.HasPermissionAsync(WorkReportPermissions.View, Ctx(Caller, CompanyOne)));
            Assert.True(await chain.HasPermissionAsync(ProjectsReportPermissions.View, Ctx(Caller, CompanyOne)));
        }

        [Fact] // 17 + 18, the worker arm
        public async Task A_worker_context_cannot_satisfy_internal_work_or_projects_authority()
        {
            using var host = Host();
            var chain = Chain(host);

            var worker = new BusinessContext
            {
                CompanyId = CompanyOne, EmployeeId = null, UserId = null,
                Roles = Array.Empty<string>(), Source = BusinessContextSource.Worker,
            };

            Assert.False(await chain.HasPermissionAsync(WorkReportPermissions.View, worker));
            Assert.False(await chain.HasPermissionAsync(ProjectsReportPermissions.View, worker));
        }

        // ==============================================================================================
        // 19-20. THE ROW-SCOPE CONTRACT. The permission answers WHETHER and never WHICH.
        //
        // This is the invariant RECONCILIATION-02 depends on: granting the key must NOT widen the breadth
        // a data source applies per row. Both assertions below would fail if the authority were ever
        // "fixed" to confer reach.
        // ==============================================================================================

        [Fact] // 19
        public async Task Holding_the_work_key_does_not_widen_the_tasks_breadth()
        {
            using var host = Host();
            var chain = Chain(host);
            var ctx = Ctx(Caller, CompanyOne);

            // Bootstrap-open: the caller holds the reporting key...
            Assert.True(await chain.HasPermissionAsync(WorkReportPermissions.View, ctx));

            // ...and the module still answers Own, NOT Company. The key opened the subject; it granted
            // no rows. A data source that trusted the key instead of the breadth would show the company.
            var scope = await Tasks(host).ResolveScopeAsync(ctx, TasksActions.Read);
            Assert.Equal(AccessBreadth.Own, scope.Breadth);
            Assert.NotEqual(AccessBreadth.Company, scope.Breadth);
        }

        [Fact] // 20
        public async Task Holding_the_projects_key_does_not_widen_the_project_scope()
        {
            using var host = Host();
            // A configured Projects role for somebody else closes bootstrap-open, then the caller is
            // granted the role so they legitimately hold the key.
            await GrantAsync(host, CompanyOne, EntityRegistry.ScopeProjects, Somebody, ProjectsRoles.ProjectsViewer);
            await GrantAsync(host, CompanyOne, EntityRegistry.ScopeProjects, Caller, ProjectsRoles.ProjectsViewer);
            var ctx = Ctx(Caller, CompanyOne);

            Assert.True(await Chain(host).HasPermissionAsync(ProjectsReportPermissions.View, ctx));

            // A role holder resolves to Company — which is the module's answer, not the key's. The point of
            // the pairing is the NEXT test: the same key held by a non-role-holder resolves to nothing.
            var role = await Projects(host).ResolveProjectScopeAsync(ctx, ProjectsActions.Read);
            Assert.Equal(AccessBreadth.Company, role.Breadth);
        }

        [Fact] // 20, the asymmetric half
        public async Task A_projects_member_with_no_module_role_is_refused_the_key_and_holds_no_company_scope()
        {
            using var host = Host();
            await GrantAsync(host, CompanyOne, EntityRegistry.ScopeProjects, Somebody, ProjectsRoles.ProjectsViewer);
            var ctx = Ctx(Caller, CompanyOne);

            // THE MODULE-LEVEL QUESTION IS ASKED WITH NO RECORD, so an ordinary member is refused the
            // subject entirely. Recorded because the donor pack's own comment claims the opposite — its
            // adapter also passes no target, so this is the behaviour that pack would actually have had.
            Assert.False(await Chain(host).HasPermissionAsync(ProjectsReportPermissions.View, ctx));

            var scope = await Projects(host).ResolveProjectScopeAsync(ctx, ProjectsActions.Read);
            Assert.NotEqual(AccessBreadth.Company, scope.Breadth);
        }

        // ==============================================================================================
        // 21. THE CALENDAR CARVE-OUT, PROVED RATHER THAN ASSERTED.
        //
        // This is the evidence for excluding work.calendar from work.reports.view. A TasksViewer holds
        // company-wide TASK read and NO calendar right, so a calendar subject gated on Tasks read would
        // show that person events CalendarAccessService refuses them.
        // ==============================================================================================

        [Fact] // 21
        public async Task Tasks_read_is_broader_than_calendar_read_so_calendar_cannot_ride_on_the_work_key()
        {
            using var host = Host();
            // Both modules are CONFIGURED, so neither is answering from bootstrap-open compatibility.
            await GrantAsync(host, CompanyOne, EntityRegistry.ScopeTasks, Caller, TasksRoles.TasksViewer);
            await GrantAsync(host, CompanyOne, EntityRegistry.ScopeCalendar, Somebody, CalendarRoles.CalendarAdministrator);
            var ctx = Ctx(Caller, CompanyOne);

            // The caller holds the Work reporting key...
            Assert.True(await Chain(host).HasPermissionAsync(WorkReportPermissions.View, ctx));
            // ...and company-wide TASK read...
            Assert.True(await Tasks(host).CanAsync(ctx, TasksActions.Read));
            // ...and NO company-wide calendar read at all. Gating CalendarEvent rows on the Work key would
            // therefore be a grant the Calendar module never made.
            Assert.False(await Calendar(host).CanAsync(ctx, CalendarActions.Read));
        }

        // ==============================================================================================
        // 22-23. MIS-WIRING DENIES. Both guards exist so a bad registration fails closed and loudly,
        // rather than answering from the wrong module and being believed.
        // ==============================================================================================

        [Fact] // 22
        public async Task An_authority_wired_to_the_wrong_module_refuses_instead_of_answering()
        {
            using var host = Host();
            // A caller who is administrator of BOTH modules, so only the mis-wire can cause a refusal.
            await GrantAsync(host, CompanyOne, EntityRegistry.ScopeTasks, Caller, TasksRoles.TasksAdministrator);
            await GrantAsync(host, CompanyOne, EntityRegistry.ScopeProjects, Caller, ProjectsRoles.ProjectsAdministrator);
            var ctx = Ctx(Caller, CompanyOne);

            // Correctly wired: allowed.
            Assert.True(await new WorkReportModuleAuthority(Tasks(host))
                .EvaluateAsync(WorkReportPermissions.View, ctx) == true);

            // Wired to Projects by mistake: REFUSED, not answered by Projects.
            Assert.False(await new WorkReportModuleAuthority(Projects(host))
                .EvaluateAsync(WorkReportPermissions.View, ctx) == true);

            Assert.False(await new ProjectsReportModuleAuthority(Tasks(host))
                .EvaluateAsync(ProjectsReportPermissions.View, ctx) == true);
        }

        [Fact] // 23
        public async Task An_action_the_module_does_not_publish_refuses()
        {
            using var host = Host();
            await GrantAsync(host, CompanyOne, EntityRegistry.ScopeTasks, Caller, TasksRoles.TasksAdministrator);
            var ctx = Ctx(Caller, CompanyOne);

            // The published action decides; an absent one denies rather than throwing or passing.
            Assert.Equal(true, await WorkReportModuleAuthority.Decide(
                Tasks(host), EntityRegistry.ScopeTasks, TasksActions.Read, ctx, default));
            Assert.Equal(false, await WorkReportModuleAuthority.Decide(
                Tasks(host), EntityRegistry.ScopeTasks, "not-an-action", ctx, default));
        }

        // ==============================================================================================
        // 24. THE VOCABULARY ITSELF — the keys are the canonical shape, and the prefixes are disjoint.
        // ==============================================================================================

        [Fact] // 24
        public void The_keys_and_prefixes_are_canonical_and_disjoint()
        {
            Assert.Equal("work.reports.view", WorkReportPermissions.View);
            Assert.Equal("projects.reports.view", ProjectsReportPermissions.View);

            Assert.StartsWith(WorkReportPermissions.Prefix, WorkReportPermissions.View, StringComparison.Ordinal);
            Assert.StartsWith(ProjectsReportPermissions.Prefix, ProjectsReportPermissions.View, StringComparison.Ordinal);

            // No claimant may swallow another's namespace, and neither may swallow reporting's own.
            Assert.False(WorkReportPermissions.Prefix.StartsWith(ProjectsReportPermissions.Prefix, StringComparison.Ordinal));
            Assert.False(ProjectsReportPermissions.Prefix.StartsWith(WorkReportPermissions.Prefix, StringComparison.Ordinal));
            foreach (var key in new[] { ReportPermissions.Administer, ReportPermissions.Public })
            {
                Assert.False(key.StartsWith(WorkReportPermissions.Prefix, StringComparison.Ordinal));
                Assert.False(key.StartsWith(ProjectsReportPermissions.Prefix, StringComparison.Ordinal));
            }
        }
    }
}
