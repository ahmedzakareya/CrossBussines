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

        // An INTERACTIVE employee, which is what every caller of these endpoints is.
        //
        // Not a Worker context: ModuleAccessServiceBase gate 6 refuses a worker outright, because a worker
        // names a company but holds no employee and therefore no role. A CanAsync test built on a worker
        // context is answered by that gate before HR's own rules run, so it proves nothing about HR - and a
        // DENIAL test written that way passes while asserting nothing at all.
        private static BusinessContext Employee(int companyId, int employeeId) =>
            new() { CompanyId = companyId, EmployeeId = employeeId, Source = BusinessContextSource.Http };

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

            var scope = await svc.ResolveEmployeeScopeAsync(Employee(CompanyA, 5), action);

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
            Assert.False(await svc.CanAsync(Employee(CompanyA, 5), action,
                PermissionTarget.ForSubjectEmployee(9)));

            Assert.Equal(AccessBreadth.None,
                (await svc.ResolveEmployeeScopeAsync(Employee(CompanyA, 5), action)).Breadth);
        }

        [Fact]
        public async Task Both_shapes_of_the_answer_agree_on_every_action()
        {
            using var host = new PlatformTestHost();
            var svc = Build(host, Real(host));
            var context = Employee(CompanyA, 5);

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
        public async Task The_real_table_actually_classifies_four_HR_actions_and_not_all_of_them()
        {
            using var host = new PlatformTestHost();
            var reader = Real(host);

            // Without this the theory above could pass vacuously against an empty table - every action
            // "not never-open" and nothing proved. Four, and exactly the sensitive four.
            //
            // employee-manage and attendance-manage joined the original pair in the bootstrap management
            // hardening. The count is asserted as an exact SET rather than a number so that adding a
            // fifth is a deliberate edit here, and so that this test keeps refusing the two readings
            // that would defeat it: an empty table, and a table that closed everything.
            var closed = new List<string>();
            foreach (var action in HrActions.All)
                if (await reader.IsNeverBootstrapOpenAsync(EntityRegistry.ScopeHr, action)) closed.Add(action);

            Assert.Equal(
                new[]
                {
                    HrActions.PayrollManage, HrActions.ConfidentialView,
                    HrActions.EmployeeManage, HrActions.AttendanceManage,
                }.OrderBy(x => x),
                closed.OrderBy(x => x));

            // ...and NOT all of them, which is the other half of the name. Self-service and the read
            // tier stay open or bootstrap stops being a bootstrap.
            Assert.False(await reader.IsNeverBootstrapOpenAsync(EntityRegistry.ScopeHr, HrActions.EmployeeRequest));
            Assert.False(await reader.IsNeverBootstrapOpenAsync(EntityRegistry.ScopeHr, HrActions.Read));
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
                Source = BusinessContextSource.Http,
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
                Source = BusinessContextSource.Http,
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
                Employee(CompanyA, 5), HrActions.EmployeeView, PermissionTarget.ForSubjectEmployee(900)));

            Assert.False(await svc.CanAsync(
                Employee(CompanyA, 5), HrActions.EmployeeView, PermissionTarget.ForSubjectEmployee(4242)));
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

        // ============================================================================================
        // employee-request - the self-service MUTATION capability.
        //
        // Four live self-service mutations (CreateLeave, CreateRequest, AcknowledgeAppraisal and
        // LeaveApiController.Create) derive the actor from authenticated identity and constrain the subject
        // correctly, but had no creditable mutation authority to check. Crediting them with leave-manage or
        // employee-manage would have handed a self-service POST authority over the whole tenant's HR
        // records, so the vocabulary gained one reusable capability for the class instead.
        //
        // The property every test below defends is AUTHORITY IS NOT REACH: holding employee-request never
        // means "may raise a request for anyone". It is answered self-only above both the bootstrap branch
        // and the role rules, so neither an unconfigured company nor an HR role can widen it.
        // ============================================================================================

        [Fact]
        public void Employee_request_is_part_of_the_hr_vocabulary()
        {
            Assert.Contains(HrActions.EmployeeRequest, HrActions.All);
            Assert.Equal("employee-request", HrActions.EmployeeRequest);

            // A mutation capability in its own right - not an alias for an administrative one.
            Assert.NotEqual(HrActions.EmployeeManage, HrActions.EmployeeRequest);
            Assert.NotEqual(HrActions.LeaveManage, HrActions.EmployeeRequest);
            Assert.NotEqual(HrActions.LeaveApprove, HrActions.EmployeeRequest);
            Assert.NotEqual(HrActions.Read, HrActions.EmployeeRequest);
            Assert.NotEqual(HrActions.EmployeeView, HrActions.EmployeeRequest);
        }

        [Fact]
        public async Task An_employee_may_raise_their_own_request_with_no_hr_role_at_all()
        {
            using var host = new PlatformTestHost();
            var svc = Build(host, Real(host));

            // The point of the capability: this is what makes the four self-service endpoints authorizable.
            Assert.True(await svc.CanAsync(
                Employee(CompanyA, 5), HrActions.EmployeeRequest, PermissionTarget.ForSubjectEmployee(5)));
        }

        [Fact]
        public async Task Employee_request_never_reaches_another_employee()
        {
            using var host = new PlatformTestHost();
            Seed(host, id: 6, companyId: CompanyA);
            Seed(host, id: 900, companyId: CompanyB);

            var svc = Build(host, Real(host));
            var me = Employee(CompanyA, 5);

            // A colleague in my own company: refused. THIS is the reading the capability must never carry -
            // "may submit a request for any employee in the company".
            Assert.False(await svc.CanAsync(me, HrActions.EmployeeRequest, PermissionTarget.ForSubjectEmployee(6)));

            // Another company's employee: refused, and refused identically.
            Assert.False(await svc.CanAsync(me, HrActions.EmployeeRequest, PermissionTarget.ForSubjectEmployee(900)));

            // No subject named at all: refused. A self-service mutation that does not say whose record it is
            // has not established self-service, so the endpoint must pass its own id explicitly.
            Assert.False(await svc.CanAsync(me, HrActions.EmployeeRequest, null));
        }

        [Fact]
        public async Task Bootstrap_does_not_widen_employee_request_to_the_callers_team()
        {
            using var host = new PlatformTestHost();
            var svc = Build(host, Real(host));

            // MUTATION PROOF FOR THE PLACEMENT. With no role configured the company is bootstrap-open, and
            // every other non-sensitive action falls through to SubjectIsInScopeAsync(allowTeam: true).
            // Moving this action below the bootstrap branch would let a manager raise requests for a report,
            // which is bootstrap compatibility WIDER than the configured answer.
            Seed(host, id: 5, companyId: CompanyA);
            Seed(host, id: 6, companyId: CompanyA);

            Assert.False(await svc.CanAsync(
                Employee(CompanyA, 5), HrActions.EmployeeRequest, PermissionTarget.ForSubjectEmployee(6)));

            // ...while the same caller, on the same bootstrap-open company, still reaches their own record.
            Assert.True(await svc.CanAsync(
                Employee(CompanyA, 5), HrActions.EmployeeRequest, PermissionTarget.ForSubjectEmployee(5)));
        }

        [Theory]
        [InlineData(HrActions.EmployeeManage)]
        [InlineData(HrActions.PayrollManage)]
        [InlineData(HrActions.ConfidentialView)]
        [InlineData(HrActions.LeaveManage)]
        public async Task Employee_request_implies_no_administrative_capability(string administrative)
        {
            using var host = new PlatformTestHost();
            Seed(host, id: 5, companyId: CompanyA);
            Seed(host, id: 6, companyId: CompanyA);

            // THE COMPANY MUST BE CONFIGURED for this question to mean anything. On a bootstrap-open company
            // employee-manage and leave-manage are already granted to everyone, so asserting they are refused
            // would be testing bootstrap, not the implication. Someone ELSE holds the HR role here, which is
            // what makes the company configured while leaving this caller with no role at all.
            Configure(host, companyId: CompanyA, employeeId: 99, role: HrRoles.HrManager);

            var svc = Build(host, Real(host));
            var me = Employee(CompanyA, 5);

            // The capability is genuinely held - it survives configuration, because no role grants it and
            // none is needed ...
            Assert.True(await svc.CanAsync(me, HrActions.EmployeeRequest, PermissionTarget.ForSubjectEmployee(5)));

            // ... and it carries no administrative authority whatsoever, about anyone. Not about a colleague:
            Assert.False(await svc.CanAsync(me, administrative, PermissionTarget.ForSubjectEmployee(6)));

            // ... and not about the caller's own record either, which is the sharper half: "it is my own"
            // is not a claim to administer it.
            Assert.False(await svc.CanAsync(me, administrative, PermissionTarget.ForSubjectEmployee(5)));
        }

        [Fact]
        public async Task Configuring_an_hr_role_does_not_take_employee_request_away()
        {
            using var host = new PlatformTestHost();
            Seed(host, id: 5, companyId: CompanyA);
            Configure(host, companyId: CompanyA, employeeId: 99, role: HrRoles.HrManager);

            var svc = Build(host, Real(host));

            // Guards the other direction. If employee-request were ever moved below the role rules it would
            // evaluate to false here - no role grants it - and self-service would break on the day a company
            // assigned its first HR role, which is the worst possible moment to discover it.
            Assert.True(await svc.CanAsync(
                Employee(CompanyA, 5), HrActions.EmployeeRequest, PermissionTarget.ForSubjectEmployee(5)));

            // Still self-only once configured: a role held by someone else cannot widen it either.
            Seed(host, id: 6, companyId: CompanyA);
            Assert.False(await svc.CanAsync(
                Employee(CompanyA, 5), HrActions.EmployeeRequest, PermissionTarget.ForSubjectEmployee(6)));
        }

        [Fact]
        public async Task The_set_shaped_answer_for_employee_request_is_own_and_never_company()
        {
            using var host = new PlatformTestHost();
            var svc = Build(host, Real(host));

            var scope = await svc.ResolveEmployeeScopeAsync(Employee(CompanyA, 5), HrActions.EmployeeRequest);

            // Both shapes must agree, which is the defect 8003212 closed. Company breadth here would mean
            // "every employee in the tenant" for a capability CanAsync answers self-only.
            Assert.Equal(AccessBreadth.Own, scope.Breadth);
        }

        [Fact]
        public async Task Employee_request_is_not_bootstrap_classified_and_does_not_disturb_the_pair()
        {
            using var host = new PlatformTestHost();
            var reader = Real(host);

            // Ordinary self-service: it is NOT never-bootstrap-open, because listing it there would refuse
            // an employee their own request form rather than protect anything.
            Assert.False(await reader.IsNeverBootstrapOpenAsync(EntityRegistry.ScopeHr, HrActions.EmployeeRequest));

            // And adding it moved neither existing classification.
            Assert.True(await reader.IsNeverBootstrapOpenAsync(EntityRegistry.ScopeHr, HrActions.PayrollManage));
            Assert.True(await reader.IsNeverBootstrapOpenAsync(EntityRegistry.ScopeHr, HrActions.ConfidentialView));
        }

        [Theory]
        [InlineData(0)]
        [InlineData(-1)]
        public async Task Employee_request_fails_closed_on_an_unresolved_company(int companyId)
        {
            using var host = new PlatformTestHost();
            var svc = Build(host, Real(host));
            var context = new BusinessContext
            {
                CompanyId = companyId,
                EmployeeId = 5,
                Source = BusinessContextSource.Http,
            };

            // Self-service is not an exemption from the company boundary: with no company there is no
            // employee to be, so the answer is no - never a default company.
            Assert.False(await svc.CanAsync(context, HrActions.EmployeeRequest, PermissionTarget.ForSubjectEmployee(5)));
            Assert.Equal(AccessBreadth.None,
                (await svc.ResolveEmployeeScopeAsync(context, HrActions.EmployeeRequest)).Breadth);
        }

        private static void Seed(PlatformTestHost host, int id, int companyId)
        {
            host.Db.Employee.Add(new CrossBuy.Models.Context.Admin.Employee
            {
                ID = id, EmpCompanyID = companyId, IsActive = true,
                FirstName = "E" + id, LastName = "T", FullName = "E" + id,
                Address = "-", PhoneNumber = "-", Email = "-", ProfileImage = "-",
                Gender = "-", MaritalStatus = "-",
                UserId = "u" + id,   // UNIQUE in the schema, so it cannot be a shared placeholder
            });
            host.Db.SaveChanges();
        }

        // Makes the company CONFIGURED for the Hr scope, so AnyConfiguredAsync stops reporting
        // bootstrap-open. The grant deliberately belongs to a DIFFERENT employee: the caller must end up
        // with a configured company and no role of their own.
        private static void Configure(PlatformTestHost host, int companyId, int employeeId, string role)
        {
            host.Db.Set<CrossBuy.Models.Context.Platform.PlatformRoleAssignment>().Add(new()
            {
                CompanyID = companyId,
                Scope = EntityRegistry.ScopeHr,
                PrincipalType = CrossBuy.Models.Context.Platform.PlatformPrincipalTypes.Employee,
                PrincipalId = employeeId,
                Role = role,
                IsActive = true,
            });
            host.Db.SaveChanges();
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
