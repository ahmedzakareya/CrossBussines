using CrossBuy.BL;
using CrossBuy.BL.Platform;
using CrossBuy.Models.Platform;
using CrossBuy.Models.Context.Platform;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CrossBuy.Tests.Hr
{
    // ============================================================================================
    // THE SELF-SERVICE CAPABILITY, AS THE FOUR ENDPOINTS ACTUALLY USE IT.
    //
    // Four HR mutations are employee self-service: CreateLeave, CreateRequest,
    // AcknowledgeAppraisal and LeaveApiController.Create. Each one now asks
    //
    //     CanAsync(ctx, HrActions.EmployeeRequest,
    //              PermissionTarget.ForSubjectEmployee(ctx.EmployeeId.Value, ctx.CompanyId))
    //
    // and — this is the part worth testing — takes the subject from the RESOLVED CONTEXT rather
    // than from the form. These tests drive the real HrAccessService against a real two-company
    // database and assert the answers the endpoints depend on. If any of them changed, every one of
    // the four endpoints would silently widen.
    //
    // WHY NOT ENDPOINT-LEVEL TESTS FOR THE THREE MVC ACTIONS. PeopleController takes six services
    // and reads its identity from session state; standing that up would test the harness more than
    // the rule. LeaveApiController's Create is covered end-to-end by the same authority call, and
    // the endpoints' own wiring is proved by the analyzer: it credits the gate only when a
    // recognised authority is genuinely in the call graph. That combination — authority behaviour
    // here, call-graph presence there — is what the coverage rests on, and saying so is better than
    // implying more.
    //
    // A REAL BUG THIS CLASS OF THINKING CAUGHT. LeaveApiController's constructor gained the two new
    // dependencies as parameters and fields but no assignments, so `_hrAccess` was null and the gate
    // would have thrown at the first request. The analyzer still credited it, because a call graph
    // does not know whether a field was assigned. Static credit is not runtime correctness.
    // ============================================================================================
    public class HrSelfServiceAuthorizationTests
    {
        private const int CompanyA = 41;
        private const int CompanyB = 77;

        private const int Alice = 501;      // company A, ordinary employee
        private const int Bob = 502;        // company A, Alice's colleague
        private const int Manager = 599;    // company A, holds HrManager
        private const int Mallory = 901;    // company B

        private static HrAccessService Build(PlatformTestHost host) =>
            new(host.Db,
                new PlatformRoleDirectory(host.Db, NullLogger<PlatformRoleDirectory>.Instance),
                new OrgHierarchy(host.Db, NullLogger<OrgHierarchy>.Instance),
                new BootstrapAccessPolicyReader(host.Db, NullLogger<BootstrapAccessPolicyReader>.Instance),
                NullLogger<HrAccessService>.Instance);

        private static BusinessContext Ctx(int companyId, int? employeeId) => new()
        {
            CompanyId = companyId,
            EmployeeId = employeeId,
            UserId = "u" + (employeeId?.ToString() ?? "none"),
            // Http, NOT Worker. ModuleAccessServiceBase gate 6 refuses a Worker context outright — it
            // holds no employee identity, so it holds no role. Building these fixtures as Workers made
            // every CanAsync return false, which meant the REFUSAL assertions below passed without
            // exercising anything. Http is also what these four endpoints actually run under.
            Source = BusinessContextSource.Http,
        };

        private static PlatformTestHost Seed()
        {
            var host = new PlatformTestHost();
            var db = host.Db;
            db.JobTitles.Add(new CrossBuy.Models.Context.Admin.JobTitle
            { ID = 1, Title = "Dev", TitleAr = "مطور", Description = "" });
            db.Employee.AddRange(
                Person(Alice, CompanyA), Person(Bob, CompanyA),
                Person(Manager, CompanyA), Person(Mallory, CompanyB));
            db.SaveChanges();
            return host;
        }

        private static CrossBuy.Models.Context.Admin.Employee Person(int id, int companyId) => new()
        {
            ID = id, EmpCompanyID = companyId, IsActive = true,
            FullName = "E" + id, FullNameEn = "E" + id, FirstName = "E", LastName = id.ToString(),
            Email = id + "@x.local", PhoneNumber = "09" + id, Address = "", Gender = "F",
            MaritalStatus = "Single", ProfileImage = "", UserId = "u" + id, JobTitleID = 1,
        };

        private static void GiveHrRole(PlatformTestHost host, int companyId, int employeeId, string role)
        {
            host.Db.Set<PlatformRoleAssignment>().Add(new()
            {
                CompanyID = companyId,
                Scope = EntityRegistry.ScopeHr,
                PrincipalType = PlatformPrincipalTypes.Employee,
                PrincipalId = employeeId,
                Role = role,
                IsActive = true,
            });
            host.Db.SaveChanges();
        }

        private static Task<bool> MaySelfServeAsync(HrAccessService hr, BusinessContext ctx, int subject) =>
            hr.CanAsync(ctx, HrActions.EmployeeRequest,
                PermissionTarget.ForSubjectEmployee(subject, ctx.CompanyId));

        // =========================================================================================

        [Fact] // §15.1 / §15.3 / §15.5 — the positive path the four endpoints take
        public async Task An_employee_may_act_for_themselves()
        {
            using var host = Seed();
            var hr = Build(host);

            Assert.True(await MaySelfServeAsync(hr, Ctx(CompanyA, Alice), Alice));
        }

        [Fact] // §15.2 / §15.4 / §15.6 — the refusal that matters
        public async Task An_employee_may_not_act_for_a_colleague()
        {
            using var host = Seed();
            var hr = Build(host);

            // Bob is in the same company, active, and real. The only thing wrong with the request is
            // that Bob is not Alice — which is the whole point of a self-service capability.
            Assert.False(await MaySelfServeAsync(hr, Ctx(CompanyA, Alice), Bob));
        }

        [Fact] // §15.8
        public async Task An_employee_may_not_act_for_someone_in_another_company()
        {
            using var host = Seed();
            var hr = Build(host);

            Assert.False(await MaySelfServeAsync(hr, Ctx(CompanyA, Alice), Mallory));
        }

        [Fact] // §15.9 — fail closed, no fallback
        public async Task A_context_with_no_employee_identity_is_refused()
        {
            using var host = Seed();
            var hr = Build(host);

            // The endpoints check `ctx.EmployeeId is not > 0` BEFORE building a target, precisely so
            // they never reach for `.Value` on a null. This asserts the authority agrees: a null
            // subject is a refusal, not an invitation to fall back to the company or to employee 1.
            Assert.False(await hr.CanAsync(Ctx(CompanyA, null), HrActions.EmployeeRequest, null));
            Assert.False(await hr.CanAsync(Ctx(CompanyA, Alice), HrActions.EmployeeRequest, null));
        }

        [Fact] // §15.10 — the one a role model usually gets wrong
        public async Task An_HR_manager_gains_no_employee_request_reach_over_a_colleague()
        {
            using var host = Seed();
            GiveHrRole(host, CompanyA, Manager, HrRoles.HrManager);
            var hr = Build(host);

            var managerCtx = Ctx(CompanyA, Manager);

            // The HR manager may administer employees company-wide...
            Assert.True(await hr.CanAsync(managerCtx, HrActions.EmployeeManage,
                PermissionTarget.ForSubjectEmployee(Alice, CompanyA)));

            // ...and still may not raise a self-service request AS Alice. `employee-request` is answered
            // above the role rules, so no HR role widens it. Without this, "HR manager" would quietly
            // become "may submit anybody's leave", which is impersonation wearing an administrative hat.
            Assert.False(await MaySelfServeAsync(hr, managerCtx, Alice));

            // The manager's own self-service still works — the capability is not disabled for them.
            Assert.True(await MaySelfServeAsync(hr, managerCtx, Manager));
        }

        [Fact] // §6 — the property holds before roles exist, not only after
        public async Task Self_only_holds_under_bootstrap_as_well_as_after_roles_are_configured()
        {
            using var host = Seed();
            var hr = Build(host);

            // No PlatformRoleAssignment rows at all: the bootstrap-open state, where most HR actions
            // are permissive. employee-request is answered ABOVE that branch, so it stays self-only.
            Assert.True(await MaySelfServeAsync(hr, Ctx(CompanyA, Alice), Alice));
            Assert.False(await MaySelfServeAsync(hr, Ctx(CompanyA, Alice), Bob));

            // Now configure a role and assert the answer did not move in either direction.
            GiveHrRole(host, CompanyA, Alice, HrRoles.HrOfficer);
            var after = Build(host);
            Assert.True(await MaySelfServeAsync(after, Ctx(CompanyA, Alice), Alice));
            Assert.False(await MaySelfServeAsync(after, Ctx(CompanyA, Alice), Bob));
        }

        [Fact] // §17 — the payroll tier the SaveMainPolices second gate depends on
        public async Task Leave_manage_does_not_imply_payroll_manage()
        {
            using var host = Seed();
            GiveHrRole(host, CompanyA, Alice, HrRoles.HrOfficer);   // grants leave-manage, not payroll
            var hr = Build(host);

            var ctx = Ctx(CompanyA, Alice);

            // This pair is exactly what SaveMainPolices relies on: the ordinary policy authority passes,
            // and the salary branch does not. If these ever became the same answer, the second gate on
            // that endpoint would stop being a gate and the payroll back door would reopen.
            Assert.True(await hr.CanAsync(ctx, HrActions.LeaveManage, null));
            Assert.False(await hr.CanAsync(ctx, HrActions.PayrollManage, null));
        }
    }
}
