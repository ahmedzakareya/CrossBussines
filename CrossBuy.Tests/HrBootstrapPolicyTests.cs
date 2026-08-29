using CrossBuy.BL;
using CrossBuy.BL.Platform;
using CrossBuy.Models.Platform;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CrossBuy.Tests
{
    // ============================================================================================
    // HR BOOTSTRAP — the classification is the platform's, not this module's.
    //
    // HrAccessService used to name its own never-bootstrap-open actions inline:
    //
    //     if (action == HrActions.ConfidentialView || action == HrActions.PayrollManage) return false;
    //
    // while NeverBootstrapOpen already carried ("Hr","payroll-manage") and ("Hr","confidential-view")
    // with the note "listed so a refactor cannot widen it". Two copies of one security rule, in two
    // files, free to drift. HR now asks IBootstrapAccessPolicyReader, so the table is the only copy.
    //
    // WHAT THIS IS NOT, STATED PLAINLY. HR is still on ModuleAccessServiceBase's action-aware bootstrap
    // (grants.Count == 0 && no role configured => open for non-sensitive actions). It does NOT gate on an
    // explicit BootstrapAccessPolicy row the way Accounting, Inventory and CRM do. That is deliberate and
    // documented: BootstrapAccessPolicySeeder says HR, Projects, Tasks and Communication "already run
    // through ModuleAccessServiceBase, which is action-aware; B6 does not touch them and they never consult
    // this reader", and seeding them "would create policy rows that no production decision path reads".
    // There is no HR seed anywhere in the repository, so switching HR to ResolveDecisionAsync today would
    // close HR for every company that has not configured an HR role. These tests assert what the code now
    // does, not what a fuller migration would do.
    //
    // The strongest assertion here is the SECOND one: it substitutes a classification that closes
    // everything, and proves the answer follows it. That is what makes this a policy test rather than a
    // restatement of an if-statement.
    // ============================================================================================
    public class HrBootstrapPolicyTests
    {
        private const int CompanyA = 41;
        private const int CompanyB = 77;

        private static BusinessContext Worker(int companyId, int employeeId) =>
            new() { CompanyId = companyId, EmployeeId = employeeId, Source = BusinessContextSource.Worker };

        private static HrAccessService Build(PlatformTestHost host, IBootstrapAccessPolicyReader policies)
        {
            var db = host.Db;
            return new HrAccessService(
                db,
                new PlatformRoleDirectory(db, NullLogger<PlatformRoleDirectory>.Instance),
                new OrgHierarchy(db, NullLogger<OrgHierarchy>.Instance),
                policies,
                NullLogger<HrAccessService>.Instance);
        }

        private static IBootstrapAccessPolicyReader Real(PlatformTestHost host) =>
            new BootstrapAccessPolicyReader(host.Db, NullLogger<BootstrapAccessPolicyReader>.Instance);

        // ---- the module is on the platform contract ------------------------------------------------

        [Fact]
        public void Hr_is_a_platform_module_access_service_and_not_a_second_engine()
        {
            // TAB-2's correction, pinned: this contract already exists and must not be rebuilt. If someone
            // introduces a parallel HR permission service, the shape asserted here is what they would break.
            Assert.True(typeof(IModuleAccessService).IsAssignableFrom(typeof(HrAccessService)));
            Assert.True(typeof(IHrAccessService).IsAssignableFrom(typeof(HrAccessService)));
            Assert.True(typeof(ModuleAccessServiceBase).IsAssignableFrom(typeof(HrAccessService)));
        }

        [Fact]
        public void Hr_takes_the_canonical_policy_reader_rather_than_carrying_its_own_list()
        {
            // The constructor is the proof that the classification is injected: a hardcoded list needs no
            // collaborator, so this parameter cannot exist unless the rule comes from the platform.
            var parameters = typeof(HrAccessService).GetConstructors().Single().GetParameters();
            Assert.Contains(parameters, p => p.ParameterType == typeof(IBootstrapAccessPolicyReader));
        }

        // ---- the classification decides, proved by substituting it ---------------------------------

        [Theory]
        [InlineData(HrActions.Read)]
        [InlineData(HrActions.EmployeeView)]
        [InlineData(HrActions.PayrollView)]
        [InlineData(HrActions.LeaveManage)]
        public async Task A_classification_that_closes_everything_closes_the_bootstrap_answer_too(string action)
        {
            using var host = new PlatformTestHost();

            // MUTATION PROOF. These four are open under bootstrap today. With a classification that calls
            // every action never-open, the scope answer must become None. If HR still consulted its own
            // hardcoded pair, read and leave-manage would come back Company and this would fail.
            var svc = Build(host, new FixedClassification(neverOpen: true));

            var scope = await svc.ResolveEmployeeScopeAsync(Worker(CompanyA, 5), action);

            Assert.Equal(AccessBreadth.None, scope.Breadth);
        }

        [Theory]
        [InlineData(HrActions.PayrollManage)]
        [InlineData(HrActions.ConfidentialView)]
        public async Task The_sensitive_pair_stays_closed_under_bootstrap_with_the_real_table(string action)
        {
            using var host = new PlatformTestHost();
            var svc = Build(host, Real(host));

            // No role rows at all: the company is bootstrap-open. Salary, disciplinary and payroll
            // administration must still refuse - this is the behaviour the migration had to preserve.
            Assert.False(await svc.CanAsync(Worker(CompanyA, 5), action,
                PermissionTarget.ForSubjectEmployee(9)));

            Assert.Equal(AccessBreadth.None,
                (await svc.ResolveEmployeeScopeAsync(Worker(CompanyA, 5), action)).Breadth);
        }

        [Fact]
        public async Task Both_shapes_of_the_answer_agree_on_every_action()
        {
            using var host = new PlatformTestHost();
            var svc = Build(host, Real(host));
            var context = Worker(CompanyA, 5);

            // THE DEFECT THIS CLOSES. ResolveEmployeeScopeAsync used to return Company scope under bootstrap
            // for EVERY action, including the two CanAsync refuses. One API said "not for you" and the other
            // said "everyone in this company", for the same caller and the same action - so a caller that
            // asked for the scope first read salary and disciplinary data company-wide on any tenant that had
            // simply never configured an HR role.
            foreach (var action in HrActions.All)
            {
                bool never = await Real(host).IsNeverBootstrapOpenAsync(EntityRegistry.ScopeHr, action);
                var breadth = (await svc.ResolveEmployeeScopeAsync(context, action)).Breadth;

                if (never)
                    Assert.Equal(AccessBreadth.None, breadth);
                else
                    Assert.NotEqual(AccessBreadth.None, breadth);
            }
        }

        [Fact]
        public async Task The_real_table_actually_classifies_two_HR_actions_and_not_all_of_them()
        {
            using var host = new PlatformTestHost();
            var reader = Real(host);

            // Without this the theory above could pass vacuously against an empty table - every action
            // "not never-open" and nothing proved. Two, and exactly the sensitive two.
            var closed = new List<string>();
            foreach (var action in HrActions.All)
                if (await reader.IsNeverBootstrapOpenAsync(EntityRegistry.ScopeHr, action)) closed.Add(action);

            Assert.Equal(new[] { HrActions.PayrollManage, HrActions.ConfidentialView }.OrderBy(x => x),
                closed.OrderBy(x => x));
        }

        // ---- fail-closed ---------------------------------------------------------------------------

        [Theory]
        [InlineData(0)]
        [InlineData(-1)]
        public async Task An_unresolved_company_refuses_and_never_becomes_company_one(int companyId)
        {
            using var host = new PlatformTestHost();
            var svc = Build(host, Real(host));
            var context = new BusinessContext
            {
                CompanyId = companyId,
                EmployeeId = 5,
                Source = BusinessContextSource.Worker,
            };

            Assert.False(await svc.CanAsync(context, HrActions.Read, PermissionTarget.ForSubjectEmployee(5)));
            Assert.Equal(AccessBreadth.None,
                (await svc.ResolveEmployeeScopeAsync(context, HrActions.Read)).Breadth);
        }

        [Fact]
        public async Task A_caller_with_no_resolved_employee_refuses()
        {
            using var host = new PlatformTestHost();
            var svc = Build(host, Real(host));
            var context = new BusinessContext
            {
                CompanyId = CompanyA,
                EmployeeId = null,
                Source = BusinessContextSource.Worker,
            };

            Assert.False(await svc.CanAsync(context, HrActions.Read, PermissionTarget.ForSubjectEmployee(5)));
        }

        [Fact]
        public async Task A_subject_in_another_company_is_refused_even_while_bootstrap_is_open()
        {
            using var host = new PlatformTestHost();
            host.Db.Employee.Add(new CrossBuy.Models.Context.Admin.Employee
            {
                ID = 900, EmpCompanyID = CompanyB, IsActive = true,
                // The fixture's non-null columns. None of them participate in the decision under test -
                // only EmpCompanyID does - but SQLite enforces them, unlike the EF InMemory provider.
                FirstName = "Foreign", LastName = "Subject", FullName = "Foreign Subject",
                Address = "-", PhoneNumber = "-", Email = "-", ProfileImage = "-",
                Gender = "-", MaritalStatus = "-", UserId = "-",
            });
            host.Db.SaveChanges();

            var svc = Build(host, Real(host));

            // Bootstrap-open widens WHICH ACTIONS are permitted, never WHICH COMPANY. A caller in company A
            // reaching employee 900 in company B is refused, and refused the same way a missing employee is,
            // so the panel cannot be used to enumerate ids across a tenant boundary.
            Assert.False(await svc.CanAsync(
                Worker(CompanyA, 5), HrActions.EmployeeView, PermissionTarget.ForSubjectEmployee(900)));

            Assert.False(await svc.CanAsync(
                Worker(CompanyA, 5), HrActions.EmployeeView, PermissionTarget.ForSubjectEmployee(4242)));
        }

        [Fact]
        public void No_hardcoded_company_one_and_no_default_company_in_the_hr_service()
        {
            var source = File.ReadAllText(Path.Combine(RepoRoot(), "CrossBuy", "BL", "HrAccessService.cs"));
            var code = string.Join("\n", source.Split('\n')
                .Where(l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal)));

            Assert.DoesNotContain("DefaultCompanyId", code, StringComparison.Ordinal);
            Assert.DoesNotContain("CompanyId = 1", code, StringComparison.Ordinal);
            Assert.DoesNotContain("CompanyID == 1", code, StringComparison.Ordinal);
        }

        [Fact]
        public void The_never_open_rule_is_stated_once_and_not_copied_back_into_the_module()
        {
            var source = File.ReadAllText(Path.Combine(RepoRoot(), "CrossBuy", "BL", "HrAccessService.cs"));
            var code = string.Join("\n", source.Split('\n')
                .Where(l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal)));

            // The classification must be asked for, never restated. A future edit that reintroduces the
            // literal pair inside the bootstrap branch is exactly the drift this batch removed.
            Assert.Contains("IsNeverBootstrapOpenAsync", code, StringComparison.Ordinal);

            int branch = code.IndexOf("if (bootstrapOpen)", StringComparison.Ordinal);
            Assert.True(branch > 0);
            var afterBranch = code[branch..];
            Assert.DoesNotContain("action == HrActions.ConfidentialView", afterBranch, StringComparison.Ordinal);
            Assert.DoesNotContain("action == HrActions.PayrollManage", afterBranch, StringComparison.Ordinal);
        }

        private static string RepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "CrossBuy.sln"))) dir = dir.Parent;
            Assert.NotNull(dir);
            return dir!.FullName;
        }

        // A classification that answers the same way for everything, so a test can prove the ANSWER follows
        // the CLASSIFICATION rather than a list the module keeps for itself.
        private sealed class FixedClassification : IBootstrapAccessPolicyReader
        {
            private readonly bool _neverOpen;
            public FixedClassification(bool neverOpen) { _neverOpen = neverOpen; }

            public Task<bool> IsNeverBootstrapOpenAsync(
                string scope, string actionCode, CancellationToken cancellationToken = default)
                => Task.FromResult(_neverOpen);

            public Task<AuthorizationDecision> ResolveDecisionAsync(
                BusinessContext context, string scope, string actionCode, CancellationToken cancellationToken = default)
                => throw new NotSupportedException(
                    "HR consults the classification only; a call here would mean the module started gating on " +
                    "policy rows, which needs an HR seed that does not exist yet.");

            public Task<BootstrapDecisionMetadata> GetDecisionMetadataAsync(
                BusinessContext context, string scope, string actionCode, CancellationToken cancellationToken = default)
                => throw new NotSupportedException();

            public Task<CrossBuy.Models.Context.Platform.BootstrapAccessPolicy?> GetEffectivePolicyAsync(
                BusinessContext context, string scope, string actionCode, CancellationToken cancellationToken = default)
                => Task.FromResult<CrossBuy.Models.Context.Platform.BootstrapAccessPolicy?>(null);

            public Task<IReadOnlyList<CrossBuy.Models.Context.Platform.BootstrapAccessPolicy>> ListPoliciesAsync(
                BusinessContext context, string? scope = null, bool includeInactive = false,
                CancellationToken cancellationToken = default)
                => Task.FromResult<IReadOnlyList<CrossBuy.Models.Context.Platform.BootstrapAccessPolicy>>(
                    Array.Empty<CrossBuy.Models.Context.Platform.BootstrapAccessPolicy>());
        }
    }
}
