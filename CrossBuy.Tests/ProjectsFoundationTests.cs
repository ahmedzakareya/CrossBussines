using System.Text.RegularExpressions;
using CrossBuy.BL;
using CrossBuy.BL.ModulePermissions;
using CrossBuy.BL.Platform;
using CrossBuy.Models.Context.Accounting;
using CrossBuy.Models.Context.Admin;
using CrossBuy.Models.Context.Platform;
using CrossBuy.Models.Platform;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CrossBuy.Tests
{
    // ================================================================================================
    // PROJECTS SECURITY + MEMBERSHIP FOUNDATION.
    //
    // WHAT WAS BROKEN, all of it found by audit and all of it real:
    //
    //   * 16 of 16 Project GET screens had NO authorization and read `DefaultCompanyId = 1`, a
    //     compile-time constant used 61 times. Any signed-in employee of any company could read
    //     company 1's budgets, profitability, subcontracts, BOQ and billing.
    //   * 7 mutating actions had no authorization at all - SaveProject, DeleteProject, SaveProgress,
    //     ConfirmProgress, DeleteProgress, SaveActivityType, DeleteActivityType. Progress feeds
    //     progress billing, so the gate on billing could be bypassed one step upstream.
    //   * `Project` was registered with PermissionScope = ScopeNone, so PlatformPermissionProvider
    //     selected DefaultPermissionAdapter - View allowed, everything else denied - and the
    //     registered ProjectsPermissionAdapter was unreachable.
    //   * dbo.ProjectMembers had two readers and ZERO writers, so the record-level access model could
    //     not be populated at all.
    //
    // WHY THE LAST TWO HAD TO SHIP TOGETHER. ProjectsAccessService grants company-wide access only
    // while a company is bootstrap-open. The moment its FIRST Projects role is configured, every
    // non-role-holder falls through to membership. With membership unwritable that is
    // AccessScope.None() for everybody - configuring a role would have locked a company out of its
    // own projects. The last two tests in this file are that transition, in both directions.
    // ================================================================================================
    public class ProjectsFoundationTests
    {
        private const int CompanyOne = 1;
        private const int CompanyTwo = 2;

        // ---- fixtures ------------------------------------------------------------------------------

        private static Employee Emp(int id, int companyId, bool active = true) => new()
        {
            ID = id, FirstName = "T", LastName = "T", FullName = "emp" + id, FullNameEn = "emp" + id,
            EmpCompanyID = companyId, IsActive = active, Address = "-", PhoneNumber = "-",
            Email = $"e{id}@example.com", ProfileImage = "-", Gender = "M", MaritalStatus = "S",
            UserId = "user-" + id,
        };

        private static BusinessContext Ctx(int employeeId, int companyId) => new()
        {
            CompanyId = companyId, EmployeeId = employeeId, UserId = "user-" + employeeId,
            Roles = Array.Empty<string>(), CorrelationId = Guid.NewGuid(),
        };

        private static IPlatformRoleDirectory Directory(PlatformTestHost host)
            => new PlatformRoleDirectory(host.Db, NullLogger<PlatformRoleDirectory>.Instance);

        private static IBootstrapAccessPolicyReader Policies(PlatformTestHost host)
            => new BootstrapAccessPolicyReader(host.Db, NullLogger<BootstrapAccessPolicyReader>.Instance);

        // The REAL ProjectsAccessService, over the real AccountingAccessService, exactly as the
        // container composes it. Nothing here is a stand-in for the policy under test.
        private static ProjectsAccessService Projects(PlatformTestHost host)
        {
            var http = new Microsoft.AspNetCore.Http.HttpContextAccessor();
            var accessor = new BusinessContextAccessor(new BusinessContextFactory(
                http, host.Db, host.Holder, NullLogger<BusinessContextFactory>.Instance));
            var accounting = new AccountingAccessService(
                host.Db, http, accessor, Policies(host), NullLogger<AccountingAccessService>.Instance);
            return new ProjectsAccessService(
                host.Db, Directory(host), accounting, NullLogger<ProjectsAccessService>.Instance);
        }

        private static ProjectMembershipService Members(PlatformTestHost host)
            => new(host.Db, Projects(host));

        private static async Task<Project> SeedProjectAsync(PlatformTestHost host, int id, int companyId)
        {
            var p = new Project { ID = id, CompanyID = companyId, Code = "P" + id, Name = "p" + id, NameEn = "p" + id };
            host.Seed.Projects.Add(p);
            await host.Seed.SaveChangesAsync();
            return p;
        }

        private static async Task SeedEmployeeAsync(PlatformTestHost host, int id, int companyId, bool active = true)
        {
            host.Seed.Employee.Add(Emp(id, companyId, active));
            await host.Seed.SaveChangesAsync();
        }

        private static async Task GrantAsync(PlatformTestHost host, int companyId, string scope, int employeeId, string role)
        {
            host.Seed.PlatformRoleAssignments.Add(new PlatformRoleAssignment
            {
                CompanyID = companyId, Scope = scope, PrincipalType = PlatformPrincipalTypes.Employee,
                PrincipalId = employeeId, Role = role, IsActive = true, CreatedAt = DateTime.UtcNow,
            });
            await host.Seed.SaveChangesAsync();
        }

        // ============================================================================================
        // 1. COMPANY ISOLATION AND AUTHORIZATION OF THE CONTROLLER — structural.
        //
        // These read the controller SOURCE on purpose. The defect was not one wrong decision inside an
        // action; it was 61 uses of a constant and 23 actions that never asked. A structural assertion
        // is what actually holds that closed, because it fails when a NEW action forgets - which a
        // behavioural test over today's actions would not.
        // ============================================================================================

        private static string ControllerSource()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "CrossBuy.sln"))) dir = dir.Parent;
            Assert.NotNull(dir);
            var path = Path.Combine(dir!.FullName, "CrossBuy", "Controllers", "ProjectController.cs");
            Assert.True(File.Exists(path), $"ProjectController.cs not found at {path}");
            return File.ReadAllText(path);
        }

        // Source minus comments, so prose describing the old defect cannot satisfy or break a check.
        private static string CodeOnly(string source)
        {
            var noBlock = Regex.Replace(source, @"/\*.*?\*/", "", RegexOptions.Singleline);
            return string.Join("\n", noBlock.Split('\n').Where(l => !l.TrimStart().StartsWith("//")));
        }

        [Fact]
        public void The_hardcoded_company_constant_is_gone_from_the_projects_controller()
        {
            var code = CodeOnly(ControllerSource());

            // The constant itself, and every one of its 61 uses.
            Assert.DoesNotContain("DefaultCompanyId", code);
        }

        [Fact]
        public void Every_project_action_resolves_its_company_from_the_gate()
        {
            var code = CodeOnly(ControllerSource());

            // The only company any action may pass downstream is the one the gate resolved. A literal
            // company id, or a company arriving from the request, would both show up here.
            Assert.DoesNotContain("CompanyID == 1", code);
            Assert.DoesNotContain("companyId: 1", code);
        }

        [Fact]
        public void Every_action_on_the_projects_controller_is_gated()
        {
            var lines = CodeOnly(ControllerSource()).Split('\n');
            var ungated = new List<string>();

            for (var i = 0; i < lines.Length; i++)
            {
                var m = Regex.Match(lines[i], @"public\s+async\s+Task<IActionResult>\s+(\w+)\s*\(");
                if (!m.Success) continue;

                // The gate must be the FIRST thing the action does, so nothing reads or writes before
                // the decision. Ten lines covers a wrapped signature plus the gate pair.
                var window = string.Join("\n", lines.Skip(i).Take(12));
                if (!Regex.IsMatch(window, @"(CompanyGateAsync|GateAsync)\(ProjectsActions\."))
                    ungated.Add(m.Groups[1].Value);
            }

            Assert.True(ungated.Count == 0, "Ungated action(s): " + string.Join(", ", ungated));
        }

        [Theory]
        // The seven that carried no authorization at all before this batch.
        [InlineData("SaveProject")]
        [InlineData("DeleteProject")]
        [InlineData("SaveProgress")]
        [InlineData("ConfirmProgress")]
        [InlineData("DeleteProgress")]
        [InlineData("SaveActivityType")]
        [InlineData("DeleteActivityType")]
        public void Each_previously_unprotected_mutation_now_authorizes_before_it_acts(string action)
        {
            var lines = CodeOnly(ControllerSource()).Split('\n');
            var idx = Array.FindIndex(lines, l =>
                Regex.IsMatch(l, $@"public\s+async\s+Task<IActionResult>\s+{action}\s*\("));
            Assert.True(idx >= 0, $"{action} not found");

            var window = string.Join("\n", lines.Skip(idx).Take(12));
            Assert.Matches(@"(CompanyGateAsync|GateAsync)\(ProjectsActions\.", window);
            Assert.Contains("if (!gate.Ok) return gate.Denied!;", window);
        }

        [Fact]
        public void The_controller_does_not_carry_a_permission_rule_of_its_own()
        {
            var code = CodeOnly(ControllerSource());

            // Every decision must come from ProjectsAccessService. A role name compared inside the
            // controller would be a second, drifting copy of the policy.
            Assert.DoesNotContain("ProjectsRoles.", code);
            Assert.DoesNotContain("ProjectMemberRoles.Manager ==", code);
        }

        // ============================================================================================
        // 2. THE REGISTRY CONTRACT — Project must route to the Projects adapter.
        //
        // Ruling 3 made the scope change conditional on these four proofs, so they assert the routing
        // itself rather than trusting the field.
        // ============================================================================================

        private static PlatformPermissionProvider Provider(PlatformTestHost host, params IModulePermissionAdapter[] adapters)
            => new(host.Registry(), adapters, NullLogger<PlatformPermissionProvider>.Instance);

        [Fact]
        public void Proof_1_the_registry_routes_Project_to_the_projects_scope()
        {
            using var host = new PlatformTestHost(companyId: CompanyOne);
            Assert.True(host.Registry().TryGetDefinition(EntityRegistry.Project, out var def));

            // The whole defect in one assertion: ScopeNone here sent Project to DefaultPermissionAdapter.
            Assert.Equal(EntityRegistry.ScopeProjects, def!.PermissionScope);
            Assert.Equal(EntityRegistry.ScopeProjects, new ProjectsPermissionAdapter(Array.Empty<IModuleAccessService>()).Scope);
        }

        [Fact]
        public async Task Proof_2_an_entitled_caller_is_allowed_through_the_projects_adapter()
        {
            using var host = new PlatformTestHost(companyId: CompanyOne);
            await SeedEmployeeAsync(host, 10, CompanyOne);
            await SeedProjectAsync(host, 5, CompanyOne);

            var projects = Projects(host);
            var provider = Provider(host, new ProjectsPermissionAdapter(new IModuleAccessService[] { projects }));

            // Bootstrap-open (no Projects role configured), so an authenticated same-company caller reads.
            var decision = await provider.CanAsync(Ctx(10, CompanyOne), EntityRegistry.Project, 5, PlatformActions.View);

            Assert.True(decision.Allowed);
            // The reason names the module that decided — proving it was the Projects adapter, not Default,
            // whose allow-reason is "authenticated, company-scoped".
            Assert.Contains("projects:read", decision.Reason);
        }

        [Fact]
        public async Task Proof_3_a_project_in_another_company_fails_closed()
        {
            using var host = new PlatformTestHost(companyId: CompanyOne);
            await SeedEmployeeAsync(host, 10, CompanyOne);
            await SeedProjectAsync(host, 77, CompanyTwo);

            var projects = Projects(host);
            var provider = Provider(host, new ProjectsPermissionAdapter(new IModuleAccessService[] { projects }));

            var decision = await provider.CanAsync(Ctx(10, CompanyOne), EntityRegistry.Project, 77, PlatformActions.View);

            Assert.False(decision.Allowed);
        }

        [Fact]
        public async Task Proof_4_there_is_no_permissive_fallback_when_the_projects_adapter_is_absent()
        {
            using var host = new PlatformTestHost(companyId: CompanyOne);
            await SeedEmployeeAsync(host, 10, CompanyOne);
            await SeedProjectAsync(host, 5, CompanyOne);

            // Only the Default adapter is registered. Now that Project is ScopeProjects, nothing matches —
            // and the provider must DENY rather than fall back to the weakest adapter present.
            var provider = Provider(host, new DefaultPermissionAdapter());

            var decision = await provider.CanAsync(Ctx(10, CompanyOne), EntityRegistry.Project, 5, PlatformActions.View);

            Assert.False(decision.Allowed);
            Assert.Contains("No permission adapter is registered", decision.Reason);
        }

        // ============================================================================================
        // 3. MEMBERSHIP — the writer that did not exist.
        // ============================================================================================

        [Fact]
        public async Task A_member_can_be_added_and_is_then_listed()
        {
            using var host = new PlatformTestHost(companyId: CompanyOne);
            await SeedEmployeeAsync(host, 10, CompanyOne);
            await SeedEmployeeAsync(host, 11, CompanyOne);
            await SeedProjectAsync(host, 5, CompanyOne);

            var members = Members(host);
            var (ok, err, id) = await members.AddAsync(Ctx(10, CompanyOne), 5, 11, ProjectMemberRoles.Member, 50m);

            Assert.True(ok, err);
            Assert.True(id > 0);

            var list = await members.ListAsync(Ctx(10, CompanyOne), 5);
            var row = Assert.Single(list);
            Assert.Equal(11, row.EmployeeId);
            Assert.Equal(ProjectMemberRoles.Member, row.RoleOnProject);
            Assert.Equal(50m, row.AllocationPct);
            Assert.True(row.IsActive);
            Assert.Null(row.LeftAt);
        }

        [Fact]
        public async Task An_employee_of_another_company_cannot_be_added_to_this_companys_project()
        {
            using var host = new PlatformTestHost(companyId: CompanyOne);
            await SeedEmployeeAsync(host, 10, CompanyOne);
            await SeedEmployeeAsync(host, 99, CompanyTwo);      // the foreigner
            await SeedProjectAsync(host, 5, CompanyOne);

            var members = Members(host);
            var (ok, _, _) = await members.AddAsync(Ctx(10, CompanyOne), 5, 99, ProjectMemberRoles.Member, null);

            Assert.False(ok);
            Assert.Empty(await members.ListAsync(Ctx(10, CompanyOne), 5));
        }

        [Fact]
        public async Task A_member_cannot_be_added_to_a_project_in_another_company()
        {
            using var host = new PlatformTestHost(companyId: CompanyOne);
            await SeedEmployeeAsync(host, 10, CompanyOne);
            await SeedEmployeeAsync(host, 11, CompanyOne);
            await SeedProjectAsync(host, 77, CompanyTwo);       // the foreign project

            var (ok, _, _) = await Members(host).AddAsync(Ctx(10, CompanyOne), 77, 11, ProjectMemberRoles.Member, null);

            Assert.False(ok);
        }

        [Fact]
        public async Task A_project_that_does_not_exist_answers_exactly_like_a_foreign_one()
        {
            using var host = new PlatformTestHost(companyId: CompanyOne);
            await SeedEmployeeAsync(host, 10, CompanyOne);
            await SeedEmployeeAsync(host, 11, CompanyOne);
            await SeedProjectAsync(host, 77, CompanyTwo);

            var members = Members(host);
            var foreign = await members.AddAsync(Ctx(10, CompanyOne), 77, 11, ProjectMemberRoles.Member, null);
            var missing = await members.AddAsync(Ctx(10, CompanyOne), 4242, 11, ProjectMemberRoles.Member, null);

            // Indistinguishable, or the screen becomes a probe for which project ids exist elsewhere.
            Assert.False(foreign.ok);
            Assert.False(missing.ok);
            Assert.Equal(foreign.error, missing.error);
        }

        [Fact]
        public async Task An_unknown_role_is_refused_rather_than_stored()
        {
            using var host = new PlatformTestHost(companyId: CompanyOne);
            await SeedEmployeeAsync(host, 10, CompanyOne);
            await SeedEmployeeAsync(host, 11, CompanyOne);
            await SeedProjectAsync(host, 5, CompanyOne);

            // Mirrors CK_ProjectMembers_RoleOnProject — a role no service knows how to evaluate never lands.
            var (ok, _, _) = await Members(host).AddAsync(Ctx(10, CompanyOne), 5, 11, "Overlord", null);

            Assert.False(ok);
        }

        [Fact]
        public async Task The_same_employee_cannot_hold_two_active_memberships_on_one_project()
        {
            using var host = new PlatformTestHost(companyId: CompanyOne);
            await SeedEmployeeAsync(host, 10, CompanyOne);
            await SeedEmployeeAsync(host, 11, CompanyOne);
            await SeedProjectAsync(host, 5, CompanyOne);

            var members = Members(host);
            Assert.True((await members.AddAsync(Ctx(10, CompanyOne), 5, 11, ProjectMemberRoles.Member, null)).ok);
            var second = await members.AddAsync(Ctx(10, CompanyOne), 5, 11, ProjectMemberRoles.Manager, null);

            Assert.False(second.ok);
            Assert.Single(await members.ListAsync(Ctx(10, CompanyOne), 5));
        }

        [Fact]
        public async Task Ending_a_membership_keeps_the_history_and_frees_the_person_to_rejoin()
        {
            using var host = new PlatformTestHost(companyId: CompanyOne);
            await SeedEmployeeAsync(host, 10, CompanyOne);
            await SeedEmployeeAsync(host, 11, CompanyOne);
            await SeedProjectAsync(host, 5, CompanyOne);

            var members = Members(host);
            var added = await members.AddAsync(Ctx(10, CompanyOne), 5, 11, ProjectMemberRoles.Member, null);
            Assert.True((await members.EndAsync(Ctx(10, CompanyOne), 5, added.id)).ok);

            var afterEnd = await members.ListAsync(Ctx(10, CompanyOne), 5);
            var ended = Assert.Single(afterEnd);
            Assert.False(ended.IsActive);
            Assert.NotNull(ended.LeftAt);              // reversed, never deleted

            // The filtered unique index is released, so rejoining is allowed and creates a NEW row.
            Assert.True((await members.AddAsync(Ctx(10, CompanyOne), 5, 11, ProjectMemberRoles.Member, null)).ok);
            Assert.Equal(2, (await members.ListAsync(Ctx(10, CompanyOne), 5)).Count);
        }

        [Fact]
        public async Task An_ended_membership_cannot_be_re_roled()
        {
            using var host = new PlatformTestHost(companyId: CompanyOne);
            await SeedEmployeeAsync(host, 10, CompanyOne);
            await SeedEmployeeAsync(host, 11, CompanyOne);
            await SeedProjectAsync(host, 5, CompanyOne);

            var members = Members(host);
            var added = await members.AddAsync(Ctx(10, CompanyOne), 5, 11, ProjectMemberRoles.Member, null);
            await members.EndAsync(Ctx(10, CompanyOne), 5, added.id);

            // Rewriting history would change the record of what somebody did on this project.
            var (ok, _) = await members.UpdateAsync(Ctx(10, CompanyOne), 5, added.id, ProjectMemberRoles.Manager, null);
            Assert.False(ok);
        }

        [Fact]
        public async Task A_membership_id_from_another_project_cannot_be_ended_through_a_project_you_can_manage()
        {
            using var host = new PlatformTestHost(companyId: CompanyOne);
            await SeedEmployeeAsync(host, 10, CompanyOne);
            await SeedEmployeeAsync(host, 11, CompanyOne);
            await SeedProjectAsync(host, 5, CompanyOne);
            await SeedProjectAsync(host, 6, CompanyOne);

            var members = Members(host);
            var onSix = await members.AddAsync(Ctx(10, CompanyOne), 6, 11, ProjectMemberRoles.Member, null);

            // Authorized on project 5, passing a membership that belongs to project 6.
            var (ok, _) = await members.EndAsync(Ctx(10, CompanyOne), 5, onSix.id);

            Assert.False(ok);
            Assert.Single(await members.ListAsync(Ctx(10, CompanyOne), 6));
        }

        // ============================================================================================
        // 4. WHAT MEMBERSHIP GRANTS, AND WHAT IT NEVER GRANTS.
        // ============================================================================================

        [Theory]
        [InlineData(ProjectMemberRoles.Observer, "read",  true)]
        [InlineData(ProjectMemberRoles.Observer, "edit",  false)]   // read-only by name and by rule
        [InlineData(ProjectMemberRoles.Observer, "manage", false)]
        [InlineData(ProjectMemberRoles.Member,   "read",  true)]
        [InlineData(ProjectMemberRoles.Member,   "edit",  true)]
        [InlineData(ProjectMemberRoles.Member,   "manage", false)]  // may not change the team
        [InlineData(ProjectMemberRoles.Manager,  "read",  true)]
        [InlineData(ProjectMemberRoles.Manager,  "edit",  true)]
        [InlineData(ProjectMemberRoles.Manager,  "manage", true)]   // this project's membership only
        public async Task Membership_role_grants_exactly_its_documented_actions(string role, string action, bool expected)
        {
            using var host = new PlatformTestHost(companyId: CompanyOne);
            await SeedEmployeeAsync(host, 10, CompanyOne);   // the role holder who closes bootstrap
            await SeedEmployeeAsync(host, 11, CompanyOne);   // the member under test
            await SeedProjectAsync(host, 5, CompanyOne);

            // Bootstrap must be CLOSED, or everyone is allowed and the test proves nothing.
            await GrantAsync(host, CompanyOne, EntityRegistry.ScopeProjects, 10, ProjectsRoles.ProjectsAdministrator);
            Assert.True((await Members(host).AddAsync(Ctx(10, CompanyOne), 5, 11, role, null)).ok);

            var allowed = await Projects(host).CanAsync(Ctx(11, CompanyOne), action, PermissionTarget.ForProject(5));

            Assert.Equal(expected, allowed);
        }

        [Theory]
        [InlineData(ProjectMemberRoles.Member)]
        [InlineData(ProjectMemberRoles.Manager)]
        public async Task Membership_never_confers_budget_access_whatever_the_role(string role)
        {
            using var host = new PlatformTestHost(companyId: CompanyOne);
            await SeedEmployeeAsync(host, 10, CompanyOne);
            await SeedEmployeeAsync(host, 11, CompanyOne);
            await SeedProjectAsync(host, 5, CompanyOne);
            await GrantAsync(host, CompanyOne, EntityRegistry.ScopeProjects, 10, ProjectsRoles.ProjectsAdministrator);
            Assert.True((await Members(host).AddAsync(Ctx(10, CompanyOne), 5, 11, role, null)).ok);

            // "A Member sees the project, not its money." This is why the financial GET screens are
            // gated on budget-view rather than read.
            Assert.False(await Projects(host).CanAsync(Ctx(11, CompanyOne), ProjectsActions.BudgetView, PermissionTarget.ForProject(5)));
            Assert.False(await Projects(host).CanAsync(Ctx(11, CompanyOne), ProjectsActions.BudgetManage, PermissionTarget.ForProject(5)));
        }

        [Fact]
        public async Task An_ended_membership_grants_nothing()
        {
            using var host = new PlatformTestHost(companyId: CompanyOne);
            await SeedEmployeeAsync(host, 10, CompanyOne);
            await SeedEmployeeAsync(host, 11, CompanyOne);
            await SeedProjectAsync(host, 5, CompanyOne);
            await GrantAsync(host, CompanyOne, EntityRegistry.ScopeProjects, 10, ProjectsRoles.ProjectsAdministrator);

            var members = Members(host);
            var added = await members.AddAsync(Ctx(10, CompanyOne), 5, 11, ProjectMemberRoles.Member, null);
            Assert.True(await Projects(host).CanAsync(Ctx(11, CompanyOne), ProjectsActions.Read, PermissionTarget.ForProject(5)));

            await members.EndAsync(Ctx(10, CompanyOne), 5, added.id);

            Assert.False(await Projects(host).CanAsync(Ctx(11, CompanyOne), ProjectsActions.Read, PermissionTarget.ForProject(5)));
        }

        [Fact]
        public async Task A_membership_row_claiming_this_company_cannot_reach_another_companys_project()
        {
            using var host = new PlatformTestHost(companyId: CompanyOne);
            await SeedEmployeeAsync(host, 11, CompanyOne);
            await SeedProjectAsync(host, 77, CompanyTwo);

            // Written directly, bypassing the service, precisely to model a tampered or corrupted row:
            // it says company 1 while the project it names belongs to company 2.
            host.Seed.ProjectMembers.Add(new ProjectMember
            {
                CompanyID = CompanyOne, ProjectId = 77, EmployeeId = 11,
                RoleOnProject = ProjectMemberRoles.Manager, IsActive = true,
                JoinedAt = DateTime.UtcNow, CreatedAt = DateTime.UtcNow,
            });
            await host.Seed.SaveChangesAsync();
            await GrantAsync(host, CompanyOne, EntityRegistry.ScopeProjects, 10, ProjectsRoles.ProjectsAdministrator);

            // The PROJECT's own company is checked before the membership row is trusted.
            Assert.False(await Projects(host).CanAsync(Ctx(11, CompanyOne), ProjectsActions.Read, PermissionTarget.ForProject(77)));
        }

        // ============================================================================================
        // 5. THE BOOTSTRAP -> CONFIGURED TRANSITION, AND THE LOCKOUT IT USED TO CAUSE.
        // ============================================================================================

        [Fact]
        public async Task Before_any_projects_role_is_configured_the_company_is_bootstrap_open()
        {
            using var host = new PlatformTestHost(companyId: CompanyOne);
            await SeedEmployeeAsync(host, 11, CompanyOne);
            await SeedProjectAsync(host, 5, CompanyOne);

            var scope = await Projects(host).ResolveProjectScopeAsync(Ctx(11, CompanyOne), ProjectsActions.Read);

            Assert.Equal(CompanyOne, scope.CompanyId);
            Assert.True(await Projects(host).CanAsync(Ctx(11, CompanyOne), ProjectsActions.Read, PermissionTarget.ForProject(5)));
        }

        [Fact]
        public async Task Configuring_the_first_projects_role_closes_the_module_for_non_members()
        {
            using var host = new PlatformTestHost(companyId: CompanyOne);
            await SeedEmployeeAsync(host, 10, CompanyOne);
            await SeedEmployeeAsync(host, 11, CompanyOne);
            await SeedProjectAsync(host, 5, CompanyOne);

            await GrantAsync(host, CompanyOne, EntityRegistry.ScopeProjects, 10, ProjectsRoles.ProjectsAdministrator);

            // The role holder keeps company-wide access...
            Assert.True(await Projects(host).CanAsync(Ctx(10, CompanyOne), ProjectsActions.Read, PermissionTarget.ForProject(5)));
            // ...and a stranger with neither role nor membership is now refused. This is correct.
            Assert.False(await Projects(host).CanAsync(Ctx(11, CompanyOne), ProjectsActions.Read, PermissionTarget.ForProject(5)));
        }

        [Fact]
        public async Task Configuring_the_first_projects_role_does_not_lock_out_the_people_on_the_project()
        {
            using var host = new PlatformTestHost(companyId: CompanyOne);
            await SeedEmployeeAsync(host, 10, CompanyOne);
            await SeedEmployeeAsync(host, 11, CompanyOne);
            await SeedProjectAsync(host, 5, CompanyOne);

            // THE REGRESSION THIS WHOLE BATCH EXISTS FOR. Before membership had a writer this was
            // impossible to satisfy: the administrator had no way to put anybody on a project, so
            // configuring the first role dropped every non-role-holder to AccessScope.None() forever.
            await GrantAsync(host, CompanyOne, EntityRegistry.ScopeProjects, 10, ProjectsRoles.ProjectsAdministrator);
            Assert.True((await Members(host).AddAsync(Ctx(10, CompanyOne), 5, 11, ProjectMemberRoles.Member, null)).ok);

            var projects = Projects(host);
            Assert.True(await projects.CanAsync(Ctx(11, CompanyOne), ProjectsActions.Read, PermissionTarget.ForProject(5)));

            var scope = await projects.ResolveProjectScopeAsync(Ctx(11, CompanyOne), ProjectsActions.Read);
            Assert.Equal(CompanyOne, scope.CompanyId);
            Assert.Contains(5, await projects.MemberProjectIdsAsync(Ctx(11, CompanyOne)));
        }

        [Fact]
        public async Task A_member_of_one_project_does_not_thereby_reach_another()
        {
            using var host = new PlatformTestHost(companyId: CompanyOne);
            await SeedEmployeeAsync(host, 10, CompanyOne);
            await SeedEmployeeAsync(host, 11, CompanyOne);
            await SeedProjectAsync(host, 5, CompanyOne);
            await SeedProjectAsync(host, 6, CompanyOne);
            await GrantAsync(host, CompanyOne, EntityRegistry.ScopeProjects, 10, ProjectsRoles.ProjectsAdministrator);
            Assert.True((await Members(host).AddAsync(Ctx(10, CompanyOne), 5, 11, ProjectMemberRoles.Manager, null)).ok);

            var projects = Projects(host);
            Assert.True(await projects.CanAsync(Ctx(11, CompanyOne), ProjectsActions.Read, PermissionTarget.ForProject(5)));
            Assert.False(await projects.CanAsync(Ctx(11, CompanyOne), ProjectsActions.Read, PermissionTarget.ForProject(6)));
        }
    }
}
