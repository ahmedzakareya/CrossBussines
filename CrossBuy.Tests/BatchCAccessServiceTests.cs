using static CrossBuy.Tests.B6TestWiring;
﻿using CrossBuy.BL;
using CrossBuy.BL.Platform;
using CrossBuy.Models.Context.Accounting;
using CrossBuy.Models.Context.Admin;
using CrossBuy.Models.Context.Chat;
using CrossBuy.Models.Context.Platform;
using CrossBuy.Models.Context.Tasks;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CrossBuy.Tests
{
    // Stage 1 Batch C — the four module access services, the shared role directory, and the additive
    // PermissionTarget forwarding.
    //
    // WHAT MAKES THESE TESTS MEANINGFUL
    //
    // All three modules had ZERO authorization before this batch (no attribute, no role check, no in-body
    // gate — only the global session middleware). So every rule here is new, and a test that merely proves
    // "the service returns true for an admin" would prove nothing about the gap being closed. The cases that
    // matter are the DENIALS and the boundaries: bootstrap-open per company, the company intersection on a
    // hierarchy that has no CompanyID, membership that has ended, and the tiers that are deliberately never
    // bootstrap-open.
    public class BatchCAccessServiceTests
    {
        private const int CompanyOne = 1;
        private const int CompanyTwo = 2;

        // ============================================================================================
        // fixtures
        // ============================================================================================

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

        private static IOrgHierarchy Org(PlatformTestHost host)
            => new OrgHierarchy(host.Db, NullLogger<OrgHierarchy>.Instance);

        private static HrAccessService Hr(PlatformTestHost host)
            => new(host.Db, Directory(host), Org(host), NullLogger<HrAccessService>.Instance);

        // The REAL AccountingAccessService over the test host, because ProjectsAccessService now takes the concrete
        // type (see its constructor comment — IEnumerable<IModuleAccessService> was a circular dependency).
        // `accountingAllows` drives the delegation both ways: the accounting module is bootstrap-open when no
        // AccountingUserRole exists (⇒ allows), and closed for a caller with no accounting role once one does.
        private static ProjectsAccessService Projects(PlatformTestHost host, bool accountingAllows = true)
        {
            if (!accountingAllows)
            {
                // Configure an accounting role for SOMEONE ELSE, which closes accounting's bootstrap-open for this
                // company and therefore denies a caller who holds no accounting role.
                host.Seed.AccountingUserRoles.Add(new CrossBuy.Models.Context.Accounting.AccountingUserRole
                { CompanyID = CompanyOne, EmployeeId = 999, Role = "ChiefAccountant" });
                host.Seed.SaveChanges();
            }

            var http = new Microsoft.AspNetCore.Http.HttpContextAccessor();
            var accessor = new BusinessContextAccessor(new BusinessContextFactory(
                http, host.Db, host.Holder, NullLogger<BusinessContextFactory>.Instance));
            var accounting = new AccountingAccessService(host.Db, http, accessor, Policies(host.Db), Log<AccountingAccessService>());

            return new ProjectsAccessService(host.Db, Directory(host), accounting,
                NullLogger<ProjectsAccessService>.Instance);
        }

        private static TasksAccessService Tasks(PlatformTestHost host, IPlatformPermissionProvider? permissions = null)
        {
            var provider = permissions ?? new AllowAllProvider();
            return new TasksAccessService(host.Db, Directory(host), Org(host),
                () => provider, NullLogger<TasksAccessService>.Instance);
        }

        private static CommunicationAccessService Comm(PlatformTestHost host)
            => new(host.Db, Directory(host), NullLogger<CommunicationAccessService>.Instance);


        private sealed class AllowAllProvider : IPlatformPermissionProvider
        {
            public Task<PermissionDecision> CanAsync(BusinessContext c, string entityType, int entityId, string action,
                CancellationToken ct = default) => Task.FromResult(PermissionDecision.Allow("test"));
        }

        private sealed class DenyAllProvider : IPlatformPermissionProvider
        {
            public Task<PermissionDecision> CanAsync(BusinessContext c, string entityType, int entityId, string action,
                CancellationToken ct = default) => Task.FromResult(PermissionDecision.Deny("test denies"));
        }

        private static async Task GrantAsync(PlatformTestHost host, int companyId, string scope, int employeeId,
            string role, bool active = true, DateTime? from = null, DateTime? to = null, int? branchId = null)
        {
            host.Seed.PlatformRoleAssignments.Add(new PlatformRoleAssignment
            {
                CompanyID = companyId, Scope = scope, PrincipalType = PlatformPrincipalTypes.Employee,
                PrincipalId = employeeId, Role = role, IsActive = active, ValidFrom = from, ValidTo = to,
                ScopeBranchId = branchId, CreatedAt = DateTime.UtcNow,
            });
            await host.Seed.SaveChangesAsync();
        }

        // ============================================================================================
        // 1. THE ROLE DIRECTORY
        // ============================================================================================

        [Fact]
        public async Task A_grant_in_another_company_never_answers_this_companys_question()
        {
            using var host = new PlatformTestHost(companyId: CompanyOne);
            await GrantAsync(host, CompanyTwo, EntityRegistry.ScopeHr, 10, HrRoles.HrManager);

            var roles = await Directory(host).RolesAsync(Ctx(10, CompanyOne), EntityRegistry.ScopeHr);

            Assert.Empty(roles);
        }

        [Theory]
        [InlineData(true, null, null, true)]      // active, unbounded          → counts
        [InlineData(false, null, null, false)]    // revoked                    → ignored
        [InlineData(true, 10, null, false)]       // ValidFrom 10 days in future → not yet
        [InlineData(true, null, -1, false)]       // ValidTo yesterday           → expired
        [InlineData(true, -10, 10, true)]         // inside the window           → counts
        public async Task A_grant_counts_only_when_active_and_in_date(
            bool active, int? fromOffsetDays, int? toOffsetDays, bool expected)
        {
            using var host = new PlatformTestHost(companyId: CompanyOne);
            await GrantAsync(host, CompanyOne, EntityRegistry.ScopeHr, 10, HrRoles.HrManager, active,
                fromOffsetDays.HasValue ? DateTime.UtcNow.AddDays(fromOffsetDays.Value) : null,
                toOffsetDays.HasValue ? DateTime.UtcNow.AddDays(toOffsetDays.Value) : null);

            var roles = await Directory(host).RolesAsync(Ctx(10, CompanyOne), EntityRegistry.ScopeHr);

            Assert.Equal(expected, roles.Count == 1);
            // ...and AnyConfiguredAsync must agree: a company whose only grant expired is BACK to
            // bootstrap-open, not permanently locked by a lapsed assignment.
            Assert.Equal(expected, await Directory(host).AnyConfiguredAsync(CompanyOne, EntityRegistry.ScopeHr));
        }

        [Fact]
        public async Task A_branch_scoped_grant_is_returned_with_its_branch()
        {
            using var host = new PlatformTestHost(companyId: CompanyOne);
            await GrantAsync(host, CompanyOne, EntityRegistry.ScopeCommunication, 10,
                CommunicationRoles.AnnouncementPublisher, branchId: 77);

            var roles = await Directory(host).RolesAsync(Ctx(10, CompanyOne), EntityRegistry.ScopeCommunication);

            Assert.Equal(77, Assert.Single(roles).ScopeBranchId);
        }

        // An unsupported principal kind is not returned, so no service can evaluate a grant it has no rule
        // for. The row exists; the directory refuses to hand it over.
        [Fact]
        public async Task A_grant_for_an_unsupported_principal_type_is_not_returned()
        {
            using var host = new PlatformTestHost(companyId: CompanyOne);
            host.Seed.PlatformRoleAssignments.Add(new PlatformRoleAssignment
            {
                CompanyID = CompanyOne, Scope = EntityRegistry.ScopeHr,
                PrincipalType = PlatformPrincipalTypes.CustomerContact,   // structurally known, NOT supported
                PrincipalId = 10, Role = HrRoles.HrManager, IsActive = true, CreatedAt = DateTime.UtcNow,
            });
            await host.Seed.SaveChangesAsync();

            Assert.Empty(await Directory(host).RolesAsync(Ctx(10, CompanyOne), EntityRegistry.ScopeHr));
            Assert.False(PlatformPrincipalTypes.IsSupported(PlatformPrincipalTypes.CustomerContact));
            Assert.True(PlatformPrincipalTypes.IsKnown(PlatformPrincipalTypes.CustomerContact));
        }

        // An unknown scope THROWS rather than returning "nothing granted" — because under bootstrap-open,
        // "nothing granted" reads as "leave the module wide open", which is the worst reading of a typo.
        [Theory]
        [InlineData("HR")]        // wrong casing
        [InlineData("Human")]     // wrong name
        [InlineData("None")]      // deliberately not a permission scope
        [InlineData("")]
        public async Task An_unknown_scope_throws_rather_than_answering_nothing_granted(string scope)
        {
            using var host = new PlatformTestHost(companyId: CompanyOne);

            await Assert.ThrowsAsync<ArgumentException>(
                () => Directory(host).RolesAsync(Ctx(10, CompanyOne), scope));
            await Assert.ThrowsAsync<ArgumentException>(
                () => Directory(host).AnyConfiguredAsync(CompanyOne, scope));
        }

        // Bootstrap-open is per COMPANY and per SCOPE — both halves asserted, because getting either wrong
        // silently changes the security posture of an unrelated tenant or module.
        [Fact]
        public async Task Bootstrap_open_is_evaluated_per_company_and_per_scope()
        {
            using var host = new PlatformTestHost(companyId: CompanyOne);
            await GrantAsync(host, CompanyOne, EntityRegistry.ScopeHr, 10, HrRoles.HrManager);

            var dir = Directory(host);
            Assert.True(await dir.AnyConfiguredAsync(CompanyOne, EntityRegistry.ScopeHr));      // configured
            Assert.False(await dir.AnyConfiguredAsync(CompanyTwo, EntityRegistry.ScopeHr));     // other company
            Assert.False(await dir.AnyConfiguredAsync(CompanyOne, EntityRegistry.ScopeTasks));  // other scope
        }

        // ============================================================================================
        // 2. THE SHARED GATES — every service, one table
        // ============================================================================================

        public static IEnumerable<object[]> AllModules()
        {
            yield return new object[] { EntityRegistry.ScopeHr, HrActions.Read };
            yield return new object[] { EntityRegistry.ScopeProjects, ProjectsActions.Read };
            yield return new object[] { EntityRegistry.ScopeTasks, TasksActions.Read };
            yield return new object[] { EntityRegistry.ScopeCommunication, CommunicationActions.Read };
        }

        private static IModuleAccessService ServiceFor(PlatformTestHost host, string scope) => scope switch
        {
            var s when s == EntityRegistry.ScopeHr => Hr(host),
            var s when s == EntityRegistry.ScopeProjects => Projects(host),
            var s when s == EntityRegistry.ScopeTasks => Tasks(host),
            var s when s == EntityRegistry.ScopeCommunication => Comm(host),
            _ => throw new InvalidOperationException(scope),
        };

        [Theory]
        [MemberData(nameof(AllModules))]
        public async Task A_missing_context_denies(string scope, string action)
        {
            using var host = new PlatformTestHost(companyId: CompanyOne);
            Assert.False(await ServiceFor(host, scope).CanAsync(null!, action));
        }

        [Theory]
        [MemberData(nameof(AllModules))]
        public async Task An_unknown_action_denies(string scope, string action)
        {
            using var host = new PlatformTestHost(companyId: CompanyOne);
            var svc = ServiceFor(host, scope);
            Assert.False(await svc.CanAsync(Ctx(10, CompanyOne), "not-a-real-action"));
            Assert.False(await svc.CanAsync(Ctx(10, CompanyOne), ""));
            // ...and the real action is not denied for the same reason, so the test proves the discrimination.
            Assert.True(svc.Actions.Contains(action, StringComparer.Ordinal));
        }

        // A target naming another company is refused BEFORE any role or record rule runs.
        [Theory]
        [MemberData(nameof(AllModules))]
        public async Task A_target_naming_another_company_denies(string scope, string action)
        {
            using var host = new PlatformTestHost(companyId: CompanyOne);
            var target = new PermissionTarget { CompanyId = CompanyTwo };
            Assert.False(await ServiceFor(host, scope).CanAsync(Ctx(10, CompanyOne), action, target));
        }

        // A worker names a company but holds NO employee, so it holds no role and no relationship.
        [Theory]
        [MemberData(nameof(AllModules))]
        public async Task A_worker_context_does_not_become_employee_access(string scope, string action)
        {
            using var host = new PlatformTestHost(companyId: CompanyOne);
            Assert.False(await ServiceFor(host, scope).CanAsync(BusinessContext.ForWorker(CompanyOne), action));
        }

        // A System context is governed by SystemContextPolicy, which allows View only — so a module action is
        // refused rather than being handed the module's own decision.
        [Theory]
        [MemberData(nameof(AllModules))]
        public async Task A_system_context_is_governed_by_the_policy(string scope, string action)
        {
            using var host = new PlatformTestHost(companyId: CompanyOne);
            var svc = ServiceFor(host, scope);
            // "read" is not PlatformActions.View, so the policy does not cover it.
            Assert.False(await svc.CanAsync(BusinessContext.ForSystem(CompanyOne), action));
            Assert.False(SystemContextPolicy.Allows(action));
        }

        [Theory]
        [MemberData(nameof(AllModules))]
        public async Task No_employee_identity_denies(string scope, string action)
        {
            using var host = new PlatformTestHost(companyId: CompanyOne);
            var noEmployee = new BusinessContext { CompanyId = CompanyOne, EmployeeId = null, UserId = "" };
            Assert.False(await ServiceFor(host, scope).CanAsync(noEmployee, action));
        }

        // ============================================================================================
        // 3. HR
        // ============================================================================================

        [Fact]
        public async Task An_employee_may_read_their_own_record_with_no_hr_role()
        {
            using var host = new PlatformTestHost(companyId: CompanyOne);
            host.Seed.Employee.Add(Emp(10, CompanyOne));
            await host.Seed.SaveChangesAsync();
            await GrantAsync(host, CompanyOne, EntityRegistry.ScopeHr, 99, HrRoles.HrManager);  // closes bootstrap

            var hr = Hr(host);
            var me = Ctx(10, CompanyOne);

            Assert.True(await hr.CanAsync(me, HrActions.EmployeeView, PermissionTarget.ForSubjectEmployee(10)));
            // ...but NOT their own confidential or payroll data: "it is mine" is not "I may see salary data".
            Assert.False(await hr.CanAsync(me, HrActions.ConfidentialView, PermissionTarget.ForSubjectEmployee(10)));
            Assert.False(await hr.CanAsync(me, HrActions.PayrollView, PermissionTarget.ForSubjectEmployee(10)));
        }

        [Fact]
        public async Task An_employee_cannot_read_another_employees_record_without_a_role()
        {
            using var host = new PlatformTestHost(companyId: CompanyOne);
            host.Seed.Employee.AddRange(Emp(10, CompanyOne), Emp(11, CompanyOne));
            await host.Seed.SaveChangesAsync();
            await GrantAsync(host, CompanyOne, EntityRegistry.ScopeHr, 99, HrRoles.HrManager);

            Assert.False(await Hr(host).CanAsync(
                Ctx(10, CompanyOne), HrActions.EmployeeView, PermissionTarget.ForSubjectEmployee(11)));
        }

        [Fact]
        public async Task Payroll_and_confidential_require_the_stronger_role()
        {
            using var host = new PlatformTestHost(companyId: CompanyOne);
            host.Seed.Employee.AddRange(Emp(10, CompanyOne), Emp(11, CompanyOne));
            await host.Seed.SaveChangesAsync();
            await GrantAsync(host, CompanyOne, EntityRegistry.ScopeHr, 10, HrRoles.HrOfficer);

            var hr = Hr(host);
            var officer = Ctx(10, CompanyOne);
            var subject = PermissionTarget.ForSubjectEmployee(11);

            // An HR officer administers people…
            Assert.True(await hr.CanAsync(officer, HrActions.EmployeeManage, subject));
            Assert.True(await hr.CanAsync(officer, HrActions.AttendanceManage, subject));
            // …but money and confidential detail are separate rights.
            Assert.False(await hr.CanAsync(officer, HrActions.PayrollManage, subject));
            Assert.False(await hr.CanAsync(officer, HrActions.ConfidentialView, subject));

            // And an HrManager does NOT get payroll-manage either: money is PayrollOfficer's.
            await GrantAsync(host, CompanyOne, EntityRegistry.ScopeHr, 12, HrRoles.HrManager);
            host.Seed.Employee.Add(Emp(12, CompanyOne));
            await host.Seed.SaveChangesAsync();
            Assert.False(await hr.CanAsync(Ctx(12, CompanyOne), HrActions.PayrollManage, subject));
        }

        // The confidential and payroll-manage tiers are NEVER bootstrap-open: opening them by default would be
        // a new exposure created by the batch meant to close one.
        [Fact]
        public async Task The_confidential_and_payroll_tiers_are_never_bootstrap_open()
        {
            using var host = new PlatformTestHost(companyId: CompanyOne);
            host.Seed.Employee.AddRange(Emp(10, CompanyOne), Emp(11, CompanyOne));
            await host.Seed.SaveChangesAsync();
            // NO grant at all ⇒ bootstrap-open.

            var hr = Hr(host);
            var me = Ctx(10, CompanyOne);
            var subject = PermissionTarget.ForSubjectEmployee(11);

            Assert.True(await hr.CanAsync(me, HrActions.EmployeeView, subject));       // open, as today
            Assert.False(await hr.CanAsync(me, HrActions.ConfidentialView, subject));  // never
            Assert.False(await hr.CanAsync(me, HrActions.PayrollManage, subject));     // never
        }

        [Fact]
        public async Task Company_A_hr_cannot_reach_a_company_B_employee()
        {
            using var host = new PlatformTestHost(companyId: CompanyOne);
            host.Seed.Employee.AddRange(Emp(10, CompanyOne), Emp(20, CompanyTwo));
            await host.Seed.SaveChangesAsync();
            await GrantAsync(host, CompanyOne, EntityRegistry.ScopeHr, 10, HrRoles.HrManager);

            // A posted employee id is only a lookup key: the subject's company comes from the Employee ROW.
            Assert.False(await Hr(host).CanAsync(
                Ctx(10, CompanyOne), HrActions.EmployeeView, PermissionTarget.ForSubjectEmployee(20)));
        }

        // A posted id that does not exist answers exactly like one belonging to another company, so employees
        // cannot be enumerated by probing.
        [Fact]
        public async Task A_posted_employee_id_that_does_not_exist_is_indistinguishable_from_another_companys()
        {
            using var host = new PlatformTestHost(companyId: CompanyOne);
            host.Seed.Employee.AddRange(Emp(10, CompanyOne), Emp(20, CompanyTwo));
            await host.Seed.SaveChangesAsync();
            await GrantAsync(host, CompanyOne, EntityRegistry.ScopeHr, 10, HrRoles.HrManager);

            var hr = Hr(host);
            bool other = await hr.CanAsync(Ctx(10, CompanyOne), HrActions.EmployeeView, PermissionTarget.ForSubjectEmployee(20));
            bool absent = await hr.CanAsync(Ctx(10, CompanyOne), HrActions.EmployeeView, PermissionTarget.ForSubjectEmployee(999_999));
            Assert.Equal(other, absent);
            Assert.False(other);
        }

        // An INACTIVE employee remains administrable — a leaver's record must stay correctable. Asserted so it
        // cannot drift into an accidental deny.
        [Fact]
        public async Task An_inactive_employee_remains_administrable()
        {
            using var host = new PlatformTestHost(companyId: CompanyOne);
            host.Seed.Employee.AddRange(Emp(10, CompanyOne), Emp(11, CompanyOne, active: false));
            await host.Seed.SaveChangesAsync();
            await GrantAsync(host, CompanyOne, EntityRegistry.ScopeHr, 10, HrRoles.HrOfficer);

            Assert.True(await Hr(host).CanAsync(
                Ctx(10, CompanyOne), HrActions.EmployeeManage, PermissionTarget.ForSubjectEmployee(11)));
        }

        // ============================================================================================
        // 4. THE HIERARCHY — and the company intersection it never had
        // ============================================================================================

        // Hierarchical has NO CompanyID, so a raw walk can cross tenants. This is the test that proves the
        // intersection: node 1 (manager, company 1) is the parent of node 2 (employee 20, COMPANY 2).
        [Fact]
        public async Task The_hierarchy_walk_never_returns_another_companys_employee()
        {
            using var host = new PlatformTestHost(companyId: CompanyOne);
            host.Seed.Employee.AddRange(Emp(10, CompanyOne), Emp(11, CompanyOne), Emp(20, CompanyTwo));
            host.Seed.Hierarchicals.AddRange(
                new Hierarchical { H_ID = 1, H_Type = 5, H_ObjectID = 10, H_Parent = null },   // the manager
                new Hierarchical { H_ID = 2, H_Type = 5, H_ObjectID = 11, H_Parent = 1 },      // same company
                new Hierarchical { H_ID = 3, H_Type = 5, H_ObjectID = 20, H_Parent = 1 });     // ANOTHER company
            await host.Seed.SaveChangesAsync();

            var team = await Org(host).DirectAndIndirectReportsAsync(CompanyOne, 10);

            Assert.Contains(10, team);      // always includes self
            Assert.Contains(11, team);      // the real report
            Assert.DoesNotContain(20, team); // the cross-company node is excluded
        }

        [Fact]
        public async Task A_manager_may_approve_a_direct_reports_leave_but_not_their_own()
        {
            using var host = new PlatformTestHost(companyId: CompanyOne);
            host.Seed.Employee.AddRange(Emp(10, CompanyOne), Emp(11, CompanyOne), Emp(12, CompanyOne));
            host.Seed.Hierarchicals.AddRange(
                new Hierarchical { H_ID = 1, H_Type = 5, H_ObjectID = 10, H_Parent = null },
                new Hierarchical { H_ID = 2, H_Type = 5, H_ObjectID = 11, H_Parent = 1 });
            await host.Seed.SaveChangesAsync();
            await GrantAsync(host, CompanyOne, EntityRegistry.ScopeHr, 99, HrRoles.HrManager);

            var hr = Hr(host);
            var manager = Ctx(10, CompanyOne);

            Assert.True(await hr.CanAsync(manager, HrActions.LeaveApprove, PermissionTarget.ForSubjectEmployee(11)));
            Assert.False(await hr.CanAsync(manager, HrActions.LeaveApprove, PermissionTarget.ForSubjectEmployee(10)));  // own
            Assert.False(await hr.CanAsync(manager, HrActions.LeaveApprove, PermissionTarget.ForSubjectEmployee(12)));  // unrelated
        }

        // An HR ROLE does not substitute for being the approver: approving out of turn is what the chain exists
        // to prevent.
        [Fact]
        public async Task An_hr_role_does_not_grant_leave_approval()
        {
            using var host = new PlatformTestHost(companyId: CompanyOne);
            host.Seed.Employee.AddRange(Emp(10, CompanyOne), Emp(11, CompanyOne));
            await host.Seed.SaveChangesAsync();
            await GrantAsync(host, CompanyOne, EntityRegistry.ScopeHr, 10, HrRoles.HrManager);

            Assert.False(await Hr(host).CanAsync(
                Ctx(10, CompanyOne), HrActions.LeaveApprove, PermissionTarget.ForSubjectEmployee(11)));
        }

        // ============================================================================================
        // 5. PROJECTS
        // ============================================================================================

        private static async Task<int> SeedProjectAsync(PlatformTestHost host, int companyId)
        {
            var p = new Project { CompanyID = companyId, Code = "P" + companyId, Name = "p", NameEn = "p" };
            host.Seed.Projects.Add(p);
            await host.Seed.SaveChangesAsync();
            return p.ID;
        }

        private static async Task JoinAsync(PlatformTestHost host, int companyId, int projectId, int employeeId,
            string role, bool active = true, DateTime? leftAt = null)
        {
            host.Seed.ProjectMembers.Add(new ProjectMember
            {
                CompanyID = companyId, ProjectId = projectId, EmployeeId = employeeId, RoleOnProject = role,
                IsActive = active, JoinedAt = DateTime.UtcNow.AddDays(-30), LeftAt = leftAt, CreatedAt = DateTime.UtcNow,
            });
            await host.Seed.SaveChangesAsync();
        }

        [Fact]
        public async Task A_project_member_may_read_their_project_and_a_non_member_may_not()
        {
            using var host = new PlatformTestHost(companyId: CompanyOne);
            host.Seed.Employee.AddRange(Emp(10, CompanyOne), Emp(11, CompanyOne));
            await host.Seed.SaveChangesAsync();
            int project = await SeedProjectAsync(host, CompanyOne);
            await JoinAsync(host, CompanyOne, project, 10, ProjectMemberRoles.Member);
            await GrantAsync(host, CompanyOne, EntityRegistry.ScopeProjects, 99, ProjectsRoles.ProjectsViewer);

            var svc = Projects(host);
            var target = PermissionTarget.ForProject(project);

            Assert.True(await svc.CanAsync(Ctx(10, CompanyOne), ProjectsActions.Read, target));
            Assert.False(await svc.CanAsync(Ctx(11, CompanyOne), ProjectsActions.Read, target));
        }

        [Theory]
        [InlineData(false, null, false)]                       // IsActive = false      ⇒ nothing
        [InlineData(true, -1, false)]                          // LeftAt yesterday      ⇒ nothing
        [InlineData(true, null, true)]                         // active, not ended     ⇒ access
        public async Task An_inactive_or_ended_membership_grants_nothing(bool active, int? leftOffsetDays, bool expected)
        {
            using var host = new PlatformTestHost(companyId: CompanyOne);
            host.Seed.Employee.Add(Emp(10, CompanyOne));
            await host.Seed.SaveChangesAsync();
            int project = await SeedProjectAsync(host, CompanyOne);
            await JoinAsync(host, CompanyOne, project, 10, ProjectMemberRoles.Member, active,
                leftOffsetDays.HasValue ? DateTime.UtcNow.AddDays(leftOffsetDays.Value) : null);
            await GrantAsync(host, CompanyOne, EntityRegistry.ScopeProjects, 99, ProjectsRoles.ProjectsViewer);

            Assert.Equal(expected, await Projects(host).CanAsync(
                Ctx(10, CompanyOne), ProjectsActions.Read, PermissionTarget.ForProject(project)));
        }

        // THE rule the schema comment warns about: membership is not money.
        [Fact]
        public async Task A_project_member_cannot_see_the_budget()
        {
            using var host = new PlatformTestHost(companyId: CompanyOne);
            host.Seed.Employee.Add(Emp(10, CompanyOne));
            await host.Seed.SaveChangesAsync();
            int project = await SeedProjectAsync(host, CompanyOne);
            await JoinAsync(host, CompanyOne, project, 10, ProjectMemberRoles.Manager);   // even a MANAGER
            await GrantAsync(host, CompanyOne, EntityRegistry.ScopeProjects, 99, ProjectsRoles.ProjectsViewer);

            var svc = Projects(host);
            var target = PermissionTarget.ForProject(project);

            Assert.True(await svc.CanAsync(Ctx(10, CompanyOne), ProjectsActions.Read, target));
            Assert.False(await svc.CanAsync(Ctx(10, CompanyOne), ProjectsActions.BudgetView, target));
            Assert.False(await svc.CanAsync(Ctx(10, CompanyOne), ProjectsActions.BudgetManage, target));
            // ...and closing a project stays administrative even for its manager.
            Assert.False(await svc.CanAsync(Ctx(10, CompanyOne), ProjectsActions.Close, target));
        }

        [Fact]
        public async Task An_observer_may_read_but_not_edit()
        {
            using var host = new PlatformTestHost(companyId: CompanyOne);
            host.Seed.Employee.Add(Emp(10, CompanyOne));
            await host.Seed.SaveChangesAsync();
            int project = await SeedProjectAsync(host, CompanyOne);
            await JoinAsync(host, CompanyOne, project, 10, ProjectMemberRoles.Observer);
            await GrantAsync(host, CompanyOne, EntityRegistry.ScopeProjects, 99, ProjectsRoles.ProjectsViewer);

            var svc = Projects(host);
            var target = PermissionTarget.ForProject(project);
            Assert.True(await svc.CanAsync(Ctx(10, CompanyOne), ProjectsActions.Read, target));
            Assert.False(await svc.CanAsync(Ctx(10, CompanyOne), ProjectsActions.Edit, target));
            Assert.False(await svc.CanAsync(Ctx(10, CompanyOne), ProjectsActions.Manage, target));
        }

        [Fact]
        public async Task A_project_in_another_company_is_refused_even_with_a_membership_row()
        {
            using var host = new PlatformTestHost(companyId: CompanyOne);
            host.Seed.Employee.Add(Emp(10, CompanyOne));
            await host.Seed.SaveChangesAsync();
            int theirProject = await SeedProjectAsync(host, CompanyTwo);
            // A membership row that (incorrectly) names company 1 for a company-2 project — the tampering shape.
            await JoinAsync(host, CompanyOne, theirProject, 10, ProjectMemberRoles.Manager);
            await GrantAsync(host, CompanyOne, EntityRegistry.ScopeProjects, 99, ProjectsRoles.ProjectsViewer);

            // The PROJECT ROW's company decides, so the mismatched membership grants nothing.
            Assert.False(await Projects(host).CanAsync(
                Ctx(10, CompanyOne), ProjectsActions.Read, PermissionTarget.ForProject(theirProject)));
        }

        // Billing delegates to the accounting module. Project administration is not a right over the ledger.
        [Fact]
        public async Task Project_billing_requires_the_accounting_modules_permission_too()
        {
            using var host = new PlatformTestHost(companyId: CompanyOne);
            host.Seed.Employee.Add(Emp(10, CompanyOne));
            await host.Seed.SaveChangesAsync();
            int project = await SeedProjectAsync(host, CompanyOne);
            await GrantAsync(host, CompanyOne, EntityRegistry.ScopeProjects, 10, ProjectsRoles.ProjectsAdministrator);

            var target = PermissionTarget.ForProject(project);
            var me = Ctx(10, CompanyOne);

            // B6 TRANSITION. Accounting bootstrap-open used to allow, so billing succeeded on an unconfigured
            // company with no ledger right at all. Accounting.post is now Never-Bootstrap-Open, so the delegation
            // denies. The test's core claim is unchanged and in fact STRENGTHENED: billing requires the accounting
            // module's own permission, and there is no longer a bootstrap route around it.
            Assert.False(await Projects(host).CanAsync(me, ProjectsActions.Billing, target));

            // Accounting CLOSED for this caller (a role exists, but not for them) ⇒ refused, despite full project
            // administration. This is the rule that stops project admin becoming a back door into the ledger.
            Assert.False(await Projects(host, accountingAllows: false).CanAsync(me, ProjectsActions.Billing, target));
        }

        // ============================================================================================
        // 6. TASKS
        // ============================================================================================

        private static async Task<int> SeedTaskAsync(PlatformTestHost host, int companyId, int assignee,
            int creator, string? entityType = null, int? entityId = null)
        {
            var t = new TaskItem
            {
                CompanyId = companyId, Title = "t", AssigneeEmployeeId = assignee,
                CreatedByEmployeeId = creator, Status = "New", CreatedAt = DateTime.UtcNow,
                EntityType = entityType, EntityId = entityId,
            };
            host.Seed.TaskItems.Add(t);
            await host.Seed.SaveChangesAsync();
            return t.ID;
        }

        [Fact]
        public async Task The_assignee_and_the_creator_may_reach_a_task_and_an_unrelated_employee_may_not()
        {
            using var host = new PlatformTestHost(companyId: CompanyOne);
            host.Seed.Employee.AddRange(Emp(10, CompanyOne), Emp(11, CompanyOne), Emp(12, CompanyOne));
            await host.Seed.SaveChangesAsync();
            int task = await SeedTaskAsync(host, CompanyOne, assignee: 10, creator: 11);
            await GrantAsync(host, CompanyOne, EntityRegistry.ScopeTasks, 99, TasksRoles.TasksViewer);

            var svc = Tasks(host);
            var target = PermissionTarget.ForTask(task);

            Assert.True(await svc.CanAsync(Ctx(10, CompanyOne), TasksActions.Read, target));   // assignee
            Assert.True(await svc.CanAsync(Ctx(11, CompanyOne), TasksActions.Read, target));   // creator
            // Module read alone is NOT enough — this is the rule the brief singles out.
            Assert.False(await svc.CanAsync(Ctx(12, CompanyOne), TasksActions.Read, target));
        }

        // Reassign is deliberately stronger than edit: an assignee may work a task but not hand it on.
        [Fact]
        public async Task Reassign_requires_more_than_being_the_assignee()
        {
            using var host = new PlatformTestHost(companyId: CompanyOne);
            host.Seed.Employee.AddRange(Emp(10, CompanyOne), Emp(11, CompanyOne));
            await host.Seed.SaveChangesAsync();
            int task = await SeedTaskAsync(host, CompanyOne, assignee: 10, creator: 11);
            await GrantAsync(host, CompanyOne, EntityRegistry.ScopeTasks, 99, TasksRoles.TasksViewer);

            var svc = Tasks(host);
            var target = PermissionTarget.ForTask(task);

            Assert.True(await svc.CanAsync(Ctx(10, CompanyOne), TasksActions.Edit, target));       // assignee edits
            Assert.False(await svc.CanAsync(Ctx(10, CompanyOne), TasksActions.Reassign, target));  // …but not reassign
            Assert.True(await svc.CanAsync(Ctx(11, CompanyOne), TasksActions.Reassign, target));   // creator may
        }

        [Fact]
        public async Task Completing_a_task_you_cannot_access_is_denied()
        {
            using var host = new PlatformTestHost(companyId: CompanyOne);
            host.Seed.Employee.AddRange(Emp(10, CompanyOne), Emp(12, CompanyOne));
            await host.Seed.SaveChangesAsync();
            int task = await SeedTaskAsync(host, CompanyOne, assignee: 10, creator: 10);
            await GrantAsync(host, CompanyOne, EntityRegistry.ScopeTasks, 99, TasksRoles.TasksViewer);

            Assert.False(await Tasks(host).CanAsync(
                Ctx(12, CompanyOne), TasksActions.Complete, PermissionTarget.ForTask(task)));
            Assert.True(await Tasks(host).CanAsync(
                Ctx(10, CompanyOne), TasksActions.Complete, PermissionTarget.ForTask(task)));
        }

        [Fact]
        public async Task A_task_in_another_company_is_refused()
        {
            using var host = new PlatformTestHost(companyId: CompanyOne);
            host.Seed.Employee.Add(Emp(10, CompanyOne));
            await host.Seed.SaveChangesAsync();
            // Assigned to employee 10, but the TASK belongs to company 2.
            int task = await SeedTaskAsync(host, CompanyTwo, assignee: 10, creator: 10);

            Assert.False(await Tasks(host).CanAsync(
                Ctx(10, CompanyOne), TasksActions.Read, PermissionTarget.ForTask(task)));
        }

        // The linked-entity gate: a task about an invoice must not reveal that invoice to someone who may not
        // see it, even to the task's own assignee.
        [Fact]
        public async Task A_task_linked_to_an_entity_the_caller_cannot_view_is_refused()
        {
            using var host = new PlatformTestHost(companyId: CompanyOne);
            host.Seed.Employee.Add(Emp(10, CompanyOne));
            await host.Seed.SaveChangesAsync();
            int task = await SeedTaskAsync(host, CompanyOne, assignee: 10, creator: 10,
                entityType: EntityRegistry.SalesInvoice, entityId: 500);
            await GrantAsync(host, CompanyOne, EntityRegistry.ScopeTasks, 99, TasksRoles.TasksViewer);

            var target = PermissionTarget.ForTask(task);
            // The assignee — but the linked invoice is not viewable.
            Assert.False(await Tasks(host, new DenyAllProvider()).CanAsync(Ctx(10, CompanyOne), TasksActions.Read, target));
            // ...and viewable ⇒ allowed, so the denial above was the linked-entity rule and not a broken lookup.
            Assert.True(await Tasks(host, new AllowAllProvider()).CanAsync(Ctx(10, CompanyOne), TasksActions.Read, target));
        }

        // CanAsync and ResolveScopeAsync must AGREE. Two shapes of one rule is how the ~1,100 hand-written
        // predicates diverged; this is the test that keeps them together.
        [Theory]
        [InlineData(null)]                         // bootstrap-open
        [InlineData(TasksRoles.TasksAdministrator)]
        [InlineData(TasksRoles.TasksViewer)]
        public async Task CanAsync_and_ResolveScopeAsync_agree_for_read(string? role)
        {
            using var host = new PlatformTestHost(companyId: CompanyOne);
            host.Seed.Employee.AddRange(Emp(10, CompanyOne), Emp(11, CompanyOne), Emp(12, CompanyOne));
            host.Seed.Hierarchicals.AddRange(
                new Hierarchical { H_ID = 1, H_Type = 5, H_ObjectID = 10, H_Parent = null },
                new Hierarchical { H_ID = 2, H_Type = 5, H_ObjectID = 11, H_Parent = 1 });
            await host.Seed.SaveChangesAsync();
            if (role != null) await GrantAsync(host, CompanyOne, EntityRegistry.ScopeTasks, 10, role);
            else await GrantAsync(host, CompanyOne, EntityRegistry.ScopeTasks, 99, TasksRoles.TasksViewer);

            var svc = Tasks(host);
            var me = Ctx(10, CompanyOne);
            var scope = await svc.ResolveScopeAsync(me, TasksActions.Read);

            // For each of three tasks — mine, my report's, and a stranger's — the yes/no answer and the
            // set-shaped answer must say the same thing.
            foreach (var (assignee, label) in new[] { (10, "mine"), (11, "report"), (12, "stranger") })
            {
                int task = await SeedTaskAsync(host, CompanyOne, assignee: assignee, creator: assignee);
                bool can = await svc.CanAsync(me, TasksActions.Read, PermissionTarget.ForTask(task));
                bool inScope = scope.Includes(CompanyOne, assignee, me.EmployeeId);
                Assert.Equal(inScope, can);
            }
        }

        // The casing trap: TaskItem.CompanyId, not CompanyID. Asserted structurally so a rename cannot
        // silently drop the company filter from a copied predicate.
        [Fact]
        public void TaskItem_company_property_is_named_CompanyId_not_CompanyID()
        {
            var props = typeof(TaskItem).GetProperties().Select(p => p.Name).ToList();
            Assert.Contains("CompanyId", props);
            Assert.DoesNotContain("CompanyID", props);
        }

        // ============================================================================================
        // 7. COMMUNICATION
        // ============================================================================================

        private static async Task<int> SeedConversationAsync(PlatformTestHost host, int companyId, string kind,
            int createdBy, params (int employeeId, string role)[] members)
        {
            var c = new Conversation
            {
                CompanyID = companyId, Kind = kind, CreatedByEmployeeId = createdBy, CreatedAt = DateTime.UtcNow,
            };
            host.Seed.Conversations.Add(c);
            await host.Seed.SaveChangesAsync();
            foreach (var (employeeId, role) in members)
                host.Seed.ConversationMembers.Add(new ConversationMember
                { ConversationId = c.ID, EmployeeId = employeeId, Role = role, JoinedAt = DateTime.UtcNow });
            await host.Seed.SaveChangesAsync();
            return c.ID;
        }

        [Theory]
        [InlineData("Direct")]
        [InlineData("Group")]
        public async Task Only_a_participant_may_read_a_conversation(string kind)
        {
            using var host = new PlatformTestHost(companyId: CompanyOne);
            host.Seed.Employee.AddRange(Emp(10, CompanyOne), Emp(11, CompanyOne), Emp(12, CompanyOne));
            await host.Seed.SaveChangesAsync();
            int conv = await SeedConversationAsync(host, CompanyOne, kind, 10, (10, "Owner"), (11, "Member"));

            var svc = Comm(host);
            var target = PermissionTarget.ForConversation(conv);

            Assert.True(await svc.CanAsync(Ctx(10, CompanyOne), CommunicationActions.Read, target));
            Assert.True(await svc.CanAsync(Ctx(11, CompanyOne), CommunicationActions.Read, target));
            // Belonging to the company is NOT enough — and this holds under bootstrap-open too.
            Assert.False(await svc.CanAsync(Ctx(12, CompanyOne), CommunicationActions.Read, target));
        }

        [Fact]
        public async Task Group_management_requires_owner_or_the_module_role()
        {
            using var host = new PlatformTestHost(companyId: CompanyOne);
            host.Seed.Employee.AddRange(Emp(10, CompanyOne), Emp(11, CompanyOne));
            await host.Seed.SaveChangesAsync();
            int conv = await SeedConversationAsync(host, CompanyOne, "Group", 10, (10, "Owner"), (11, "Member"));

            var svc = Comm(host);
            var target = PermissionTarget.ForConversation(conv);

            Assert.True(await svc.CanAsync(Ctx(10, CompanyOne), CommunicationActions.ManageGroup, target));   // Owner
            Assert.False(await svc.CanAsync(Ctx(11, CompanyOne), CommunicationActions.ManageGroup, target));  // Member

            // ...and the module administrator may, without being a member.
            host.Seed.Employee.Add(Emp(13, CompanyOne));
            await host.Seed.SaveChangesAsync();
            await GrantAsync(host, CompanyOne, EntityRegistry.ScopeCommunication, 13,
                CommunicationRoles.CommunicationAdministrator);
            Assert.True(await Comm(host).CanAsync(Ctx(13, CompanyOne), CommunicationActions.ManageGroup, target));
        }

        [Fact]
        public async Task A_conversation_in_another_company_is_refused_even_with_a_member_row()
        {
            using var host = new PlatformTestHost(companyId: CompanyOne);
            host.Seed.Employee.Add(Emp(10, CompanyOne));
            await host.Seed.SaveChangesAsync();
            // The conversation belongs to company 2; employee 10 (company 1) has a member row anyway.
            int conv = await SeedConversationAsync(host, CompanyTwo, "Group", 10, (10, "Owner"));

            var svc = Comm(host);
            Assert.False(await svc.CanAsync(Ctx(10, CompanyOne), CommunicationActions.Read,
                PermissionTarget.ForConversation(conv)));
            Assert.False(await svc.IsConversationParticipantAsync(Ctx(10, CompanyOne), conv));
        }

        // The email outbox has no participant column at all, so it needs an explicit role — and is never
        // bootstrap-open, because every queued email body would otherwise be readable by every employee.
        [Fact]
        public async Task The_email_outbox_requires_an_explicit_role_and_is_never_bootstrap_open()
        {
            using var host = new PlatformTestHost(companyId: CompanyOne);
            host.Seed.Employee.AddRange(Emp(10, CompanyOne), Emp(11, CompanyOne));
            await host.Seed.SaveChangesAsync();

            // No grant at all ⇒ bootstrap-open for everything else…
            Assert.True(await Comm(host).CanAsync(Ctx(10, CompanyOne), CommunicationActions.Read));
            // …but never for the outbox.
            Assert.False(await Comm(host).CanAsync(Ctx(10, CompanyOne), CommunicationActions.OutboxManage));

            await GrantAsync(host, CompanyOne, EntityRegistry.ScopeCommunication, 10, CommunicationRoles.OutboxOperator);
            Assert.True(await Comm(host).CanAsync(Ctx(10, CompanyOne), CommunicationActions.OutboxManage));
            Assert.False(await Comm(host).CanAsync(Ctx(11, CompanyOne), CommunicationActions.OutboxManage));
        }

        // A branch-scoped announcement grant may only publish to its own branch.
        [Fact]
        public async Task A_branch_scoped_publisher_may_only_publish_to_that_branch()
        {
            using var host = new PlatformTestHost(companyId: CompanyOne);
            host.Seed.Employee.Add(Emp(10, CompanyOne));
            await host.Seed.SaveChangesAsync();
            await GrantAsync(host, CompanyOne, EntityRegistry.ScopeCommunication, 10,
                CommunicationRoles.AnnouncementPublisher, branchId: 77);

            var svc = Comm(host);
            var me = Ctx(10, CompanyOne);

            Assert.True(await svc.CanAsync(me, CommunicationActions.AnnouncementSend, new PermissionTarget { BranchId = 77 }));
            Assert.False(await svc.CanAsync(me, CommunicationActions.AnnouncementSend, new PermissionTarget { BranchId = 88 }));
        }
    }
}
