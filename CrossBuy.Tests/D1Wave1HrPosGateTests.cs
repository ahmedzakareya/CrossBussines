using static CrossBuy.Tests.B6TestWiring;
using CrossBuy.BL;
using CrossBuy.BL.Platform;
using CrossBuy.Models;
using CrossBuy.Models.Context.Accounting;
using CrossBuy.Models.Context.Admin;
using CrossBuy.Models.Context.Platform;
using CrossBuy.Models.Context.Pos;
using CrossBuy.Models.Platform;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using System.Security.Claims;
using Xunit;

namespace CrossBuy.Tests
{
    // Stage 1 Batch D1 Wave 1 — the final ten endpoints: HR payroll (5) and POS admin (5).
    //
    // Every allow/deny assertion seeds REAL role rows and uses the REAL access services. HrAccessService and
    // PosAccessService are the two modules here, and neither is stubbed.
    public class D1Wave1HrPosGateTests
    {
        private const int CompanyOne = 1;
        private const int CompanyTwo = 2;
        private const int BranchOne = 11;
        private const int BranchTwo = 12;

        private const int PayrollOfficerId = 81;   // HR: payroll-manage
        private const int HrOfficerId = 82;        // HR: day-to-day, NOT payroll
        private const int ChiefId = 83;            // accounting post
        private const int PosManagerId = 84;       // POS manager at branch 11
        private const int PosCashierId = 85;       // POS cashier at branch 11 — not a manager
        private const int OtherBranchMgr = 86;     // POS manager at branch 12

        private static Employee Emp(int id, int companyId, int? branchId = null) => new()
        {
            ID = id, FirstName = "T", LastName = "T", FullName = "emp" + id, FullNameEn = "emp" + id,
            EmpCompanyID = companyId, BranchID = branchId, IsActive = true, Address = "-", PhoneNumber = "-",
            Email = $"e{id}@example.com", ProfileImage = "-", Gender = "M", MaritalStatus = "S",
            UserId = "user-" + id,
        };

        private static BusinessContext Ctx(int employeeId, int companyId, int? branchId = null) => new()
        {
            CompanyId = companyId, EmployeeId = employeeId, BranchId = branchId, UserId = "user-" + employeeId,
            Roles = Array.Empty<string>(), CorrelationId = Guid.NewGuid(),
        };

        private static HrAccessService Hr(PlatformTestHost host) =>
            new(host.Db, new PlatformRoleDirectory(host.Db, NullLogger<PlatformRoleDirectory>.Instance),
                new OrgHierarchy(host.Db, NullLogger<OrgHierarchy>.Instance),
                NullLogger<HrAccessService>.Instance);

        private static PosAccessService Pos(PlatformTestHost host) => new(host.Db);

        private static AccountingAccessService Accounting(PlatformTestHost host, BusinessContext? ctx) =>
            new(host.Db, new HttpContextAccessor(),
                ctx == null ? StubContextAccessor.Unresolved() : new StubContextAccessor(ctx),
                Policies(host.Db), Log<AccountingAccessService>());

        private static async Task SeedAsync(PlatformTestHost host)
        {
            host.Seed.Employee.AddRange(
                Emp(PayrollOfficerId, CompanyOne), Emp(HrOfficerId, CompanyOne), Emp(ChiefId, CompanyOne),
                Emp(PosManagerId, CompanyOne, BranchOne), Emp(PosCashierId, CompanyOne, BranchOne),
                Emp(OtherBranchMgr, CompanyOne, BranchTwo));

            // REAL HR grants — closes the HR scope's bootstrap-open for company 1.
            host.Seed.PlatformRoleAssignments.AddRange(
                new PlatformRoleAssignment
                {
                    CompanyID = CompanyOne, Scope = EntityRegistry.ScopeHr,
                    PrincipalType = PlatformPrincipalTypes.Employee, PrincipalId = PayrollOfficerId,
                    Role = HrRoles.PayrollOfficer, IsActive = true, CreatedAt = DateTime.UtcNow,
                },
                new PlatformRoleAssignment
                {
                    CompanyID = CompanyOne, Scope = EntityRegistry.ScopeHr,
                    PrincipalType = PlatformPrincipalTypes.Employee, PrincipalId = HrOfficerId,
                    Role = HrRoles.HrOfficer, IsActive = true, CreatedAt = DateTime.UtcNow,
                });

            // REAL accounting role — closes accounting's bootstrap-open.
            host.Seed.AccountingUserRoles.Add(new AccountingUserRole
            { CompanyID = CompanyOne, EmployeeId = ChiefId, Role = "ChiefAccountant" });

            // REAL POS role rows — BranchUserRoles is the documented POS exception, preserved.
            host.Seed.BranchUserRoles.AddRange(
                new BranchUserRole { BranchId = BranchOne, EmployeeId = PosManagerId, PosRole = "pos-manager", IsActive = true },
                new BranchUserRole { BranchId = BranchOne, EmployeeId = PosCashierId, PosRole = "pos-cashier", IsActive = true },
                new BranchUserRole { BranchId = BranchTwo, EmployeeId = OtherBranchMgr, PosRole = "pos-manager", IsActive = true });

            await host.Seed.SaveChangesAsync();
        }

        // ============================================================================================
        // 0. BOOTSTRAP-OPEN SAFETY for the two modules added in this increment
        // ============================================================================================

        // CORRECTED EXPECTATION, and the correction is good news. I assumed HR behaved like accounting — open on
        // an unconfigured install. It does not: `HrAccessService` explicitly refuses `payroll-manage` and
        // `confidential-view` even under bootstrap-open, because "those were unreachable-by-design data before
        // this batch, and opening them by default would be a new exposure created by the very batch meant to close
        // one". So the payroll tier is genuinely closed by default — STRONGER than accounting.
        [Fact]
        public async Task Payroll_manage_is_never_bootstrap_open_even_with_no_hr_roles_configured()
        {
            using var host = new PlatformTestHost(companyId: CompanyOne);
            host.Seed.Employee.Add(Emp(HrOfficerId, CompanyOne));
            await host.Seed.SaveChangesAsync();   // no HR grants at all

            var ctx = Ctx(HrOfficerId, CompanyOne);
            Assert.False(await Hr(host).CanAsync(ctx, HrActions.PayrollManage));
            Assert.False(await Hr(host).CanAsync(ctx, HrActions.ConfidentialView));

            // …whereas leave-manage IS bootstrap-open. This asymmetry is the honest limitation of the three
            // leave-based endpoints (Encash / PostLeaveProvision / RunLeaveCarryOver): on a company with NO HR
            // role rows they remain open, because that is the HR module's own compatibility policy, not the gate's.
            // PostFinalSettlement and SaveSalaryPolicy take payroll-manage and are therefore closed by default.
            Assert.True(await Hr(host).CanAsync(ctx, HrActions.LeaveManage));
        }

        [Fact]
        public async Task Once_hr_grants_exist_an_hr_officer_cannot_manage_payroll()
        {
            using var host = new PlatformTestHost(companyId: CompanyOne);
            await SeedAsync(host);

            // HrOfficer is day-to-day HR. Payroll money is a separate, stronger right.
            Assert.False(await Hr(host).CanAsync(Ctx(HrOfficerId, CompanyOne), HrActions.PayrollManage));
            Assert.True(await Hr(host).CanAsync(Ctx(PayrollOfficerId, CompanyOne), HrActions.PayrollManage));
        }

        // POS is NOT bootstrap-open — no POS role assigned means no access at all. Worth pinning, because it is
        // the opposite of the accounting/HR default and a reader may assume otherwise.
        [Fact]
        public async Task Pos_is_not_bootstrap_open_an_employee_with_no_branch_role_is_denied()
        {
            using var host = new PlatformTestHost(companyId: CompanyOne);
            host.Seed.Employee.Add(Emp(PosManagerId, CompanyOne, BranchOne));
            await host.Seed.SaveChangesAsync();   // no BranchUserRole row

            Assert.False(await Pos(host).CanAsync(
                Ctx(PosManagerId, CompanyOne, BranchOne), "manage",
                new PermissionTarget { BranchId = BranchOne }));
        }

        // ============================================================================================
        // 1. HR — the payroll rights the five endpoints require
        // ============================================================================================

        [Theory]
        [InlineData(HrActions.PayrollManage, PayrollOfficerId, true)]    // PostFinalSettlement, SaveSalaryPolicy
        [InlineData(HrActions.PayrollManage, HrOfficerId, false)]
        [InlineData(HrActions.LeaveManage, HrOfficerId, true)]           // Encash, provision, carry-over
        [InlineData(HrActions.LeaveManage, PayrollOfficerId, false)]   // PayrollOfficer is payroll ONLY — leave admin is HrManager/HrOfficer
        public async Task The_hr_rights_the_payroll_endpoints_require(string action, int employeeId, bool expected)
        {
            using var host = new PlatformTestHost(companyId: CompanyOne);
            await SeedAsync(host);

            Assert.Equal(expected, await Hr(host).CanAsync(Ctx(employeeId, CompanyOne), action));
        }

        // A payroll officer of company 1 has no rights in company 2 — the grant is per company.
        [Fact]
        public async Task A_payroll_officer_holds_no_payroll_right_in_another_company()
        {
            using var host = new PlatformTestHost(companyId: CompanyOne);
            await SeedAsync(host);

            Assert.False(await Hr(host).CanAsync(Ctx(PayrollOfficerId, CompanyTwo), HrActions.PayrollManage));
        }

        // The GL half. PostFinalSettlement / Encash / PostLeaveProvision post journals, so the gate additionally
        // requires accounting "post" — the payroll right alone must not be a posting right.
        [Fact]
        public async Task A_payroll_officer_without_an_accounting_role_fails_the_general_ledger_half()
        {
            using var host = new PlatformTestHost(companyId: CompanyOne);
            await SeedAsync(host);

            var payroll = Ctx(PayrollOfficerId, CompanyOne);
            Assert.True(await Hr(host).CanAsync(payroll, HrActions.PayrollManage));      // HR half passes
            Assert.False(await Accounting(host, payroll).CanAsync(payroll, "post"));     // GL half denies

            var chief = Ctx(ChiefId, CompanyOne);
            Assert.True(await Accounting(host, chief).CanAsync(chief, "post"));
        }

        // ============================================================================================
        // 2. POS — role, branch and company
        // ============================================================================================

        [Fact]
        public async Task A_pos_manager_may_run_admin_operations_at_their_own_branch()
        {
            using var host = new PlatformTestHost(companyId: CompanyOne);
            await SeedAsync(host);

            Assert.True(await Pos(host).CanAsync(
                Ctx(PosManagerId, CompanyOne, BranchOne), "manage",
                new PermissionTarget { BranchId = BranchOne }));
        }

        // A cashier sells; opening/closing shifts and running production are manager operations. This preserves
        // the waiter/kitchen/cashier/manager distinction rather than flattening it.
        [Fact]
        public async Task A_pos_cashier_may_sell_but_may_not_run_admin_operations()
        {
            using var host = new PlatformTestHost(companyId: CompanyOne);
            await SeedAsync(host);

            var cashier = Ctx(PosCashierId, CompanyOne, BranchOne);
            var atOwnBranch = new PermissionTarget { BranchId = BranchOne };

            Assert.True(await Pos(host).CanAsync(cashier, "sell", atOwnBranch));    // workflow preserved
            Assert.False(await Pos(host).CanAsync(cashier, "manage", atOwnBranch)); // admin denied
        }

        // BRANCH TAMPERING: branchId arrives on the request, and a manager of branch 12 naming branch 11 is
        // refused. This is the check that makes the posted branch id non-authoritative.
        [Fact]
        public async Task A_manager_of_another_branch_is_refused_when_naming_this_branch()
        {
            using var host = new PlatformTestHost(companyId: CompanyOne);
            await SeedAsync(host);

            Assert.False(await Pos(host).CanAsync(
                Ctx(OtherBranchMgr, CompanyOne, BranchTwo), "manage",
                new PermissionTarget { BranchId = BranchOne }));

            // and is allowed at their own branch, so the denial above is about the branch, not the person
            Assert.True(await Pos(host).CanAsync(
                Ctx(OtherBranchMgr, CompanyOne, BranchTwo), "manage",
                new PermissionTarget { BranchId = BranchTwo }));
        }

        [Fact]
        public async Task A_pos_caller_with_no_user_id_is_denied_because_pos_identity_is_the_user_id()
        {
            using var host = new PlatformTestHost(companyId: CompanyOne);
            await SeedAsync(host);

            var noUser = new BusinessContext
            {
                CompanyId = CompanyOne, EmployeeId = PosManagerId, BranchId = BranchOne, UserId = "",
                Roles = Array.Empty<string>(), CorrelationId = Guid.NewGuid(),
            };

            Assert.False(await Pos(host).CanAsync(noUser, "manage", new PermissionTarget { BranchId = BranchOne }));
        }

        [Fact]
        public async Task An_unknown_pos_action_denies_rather_than_falling_through()
        {
            using var host = new PlatformTestHost(companyId: CompanyOne);
            await SeedAsync(host);

            Assert.False(await Pos(host).CanAsync(
                Ctx(PosManagerId, CompanyOne, BranchOne), "not-an-action",
                new PermissionTarget { BranchId = BranchOne }));
        }

        // ============================================================================================
        // 3. THE HR API GUARD — SaveSalaryPolicy returns JSON
        // ============================================================================================

        private static ActionExecutingContext HrApiContext(
            PlatformTestHost host, BusinessContext? ctx, bool authenticated)
        {
            var services = new ServiceCollection();
            services.AddSingleton<IHrAccessService>(Hr(host));
            services.AddSingleton<IBusinessContextAccessor>(
                ctx == null ? StubContextAccessor.Unresolved() : new StubContextAccessor(ctx));

            var http = new DefaultHttpContext { RequestServices = services.BuildServiceProvider() };
            http.User = authenticated
                ? new ClaimsPrincipal(new ClaimsIdentity(
                    new[] { new Claim(ClaimTypes.NameIdentifier, "user-" + ctx?.EmployeeId) }, "test"))
                : new ClaimsPrincipal(new ClaimsIdentity());

            return new ActionExecutingContext(
                new ActionContext(http, new RouteData(), new ActionDescriptor()),
                new List<IFilterMetadata>(), new Dictionary<string, object?>(), controller: null!);
        }

        [Fact]
        public async Task The_salary_policy_guard_returns_401_unauthenticated_and_403_unauthorized_never_a_redirect()
        {
            using var host = new PlatformTestHost(companyId: CompanyOne);
            await SeedAsync(host);

            var anon = HrApiContext(host, null, authenticated: false);
            await new ApiPermAttribute(ApiPermAttribute.Hr, HrActions.PayrollManage)
                .OnActionExecutionAsync(anon, () => throw new Exception("must not run"));
            Assert.Equal(401, Assert.IsType<JsonResult>(anon.Result).StatusCode);
            Assert.IsNotType<RedirectToActionResult>(anon.Result);

            var officer = HrApiContext(host, Ctx(HrOfficerId, CompanyOne), authenticated: true);
            await new ApiPermAttribute(ApiPermAttribute.Hr, HrActions.PayrollManage)
                .OnActionExecutionAsync(officer, () => throw new Exception("must not run"));
            Assert.Equal(403, Assert.IsType<JsonResult>(officer.Result).StatusCode);
            Assert.IsNotType<RedirectToActionResult>(officer.Result);
        }

        [Fact]
        public async Task The_salary_policy_guard_admits_a_payroll_officer()
        {
            using var host = new PlatformTestHost(companyId: CompanyOne);
            await SeedAsync(host);

            var ran = false;
            var ctxObj = HrApiContext(host, Ctx(PayrollOfficerId, CompanyOne), authenticated: true);
            await new ApiPermAttribute(ApiPermAttribute.Hr, HrActions.PayrollManage)
                .OnActionExecutionAsync(ctxObj, () => { ran = true; return Task.FromResult<ActionExecutedContext>(null!); });

            Assert.True(ran);
            Assert.Null(ctxObj.Result);
        }

        // An unresolved BusinessContext denies — it never falls back to a company.
        [Fact]
        public async Task The_hr_api_guard_denies_when_no_business_context_resolves()
        {
            using var host = new PlatformTestHost(companyId: CompanyOne);
            await SeedAsync(host);

            var ctxObj = HrApiContext(host, null, authenticated: true);   // authenticated, but nothing resolves
            await new ApiPermAttribute(ApiPermAttribute.Hr, HrActions.PayrollManage)
                .OnActionExecutionAsync(ctxObj, () => throw new Exception("must not run"));

            Assert.Equal(403, Assert.IsType<JsonResult>(ctxObj.Result).StatusCode);
        }

        // ============================================================================================
        // 4. STRUCTURAL — all ten endpoints gated, none reading a company literal
        // ============================================================================================

        private static string ControllersRoot()
        {
            var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "CrossBuy.sln"))) dir = dir.Parent;
            return Path.Combine(dir!.FullName, "CrossBuy", "Controllers");
        }

        private static string Body(string text, string method)
        {
            var sig = text.IndexOf(" " + method + "(", StringComparison.Ordinal);
            Assert.True(sig > 0, method + " not found");
            var open = text.IndexOf('{', sig);
            int d = 0;
            for (int i = open; i < text.Length; i++)
            {
                if (text[i] == '{') d++;
                else if (text[i] == '}' && --d == 0) return text[open..(i + 1)];
            }
            Assert.Fail("unbalanced braces in " + method);
            return "";
        }

        [Theory]
        [InlineData("PostFinalSettlement", true)]
        [InlineData("Encash", true)]
        [InlineData("PostLeaveProvision", true)]
        [InlineData("RunLeaveCarryOver", false)]   // rewrites balances; no journal
        public void Every_remediated_hr_payroll_action_is_gated_and_free_of_the_company_literal(
            string action, bool needsAccountingPost)
        {
            var body = Body(File.ReadAllText(Path.Combine(ControllersRoot(), "AdminController.cs")), action);

            Assert.Contains("HrGateAsync(", body);
            Assert.Contains("if (!gate.Ok) return HrDenied(", body);
            Assert.Contains("gate.CompanyId", body);
            Assert.DoesNotContain("HrCompanyId", body);
            Assert.Equal(needsAccountingPost, body.Contains("requireAccountingPost: true"));
        }

        [Theory]
        [InlineData("OpenShift", false)]
        [InlineData("CloseShift", true)]        // posts a cash-variance journal
        [InlineData("PrepareFinished", true)]   // moves stock and posts cost
        [InlineData("PrepareSemi", true)]
        public void Every_remediated_pos_admin_action_is_gated_and_free_of_the_company_literal(
            string action, bool needsAccountingPost)
        {
            var body = Body(File.ReadAllText(Path.Combine(ControllersRoot(), "PosController.cs")), action);

            Assert.Contains("PosGateAsync(", body);
            Assert.Contains("if (!gate.Ok) return PosDenied(", body);
            Assert.DoesNotContain("DefaultCompanyId", body);
            Assert.Equal(needsAccountingPost, body.Contains("requireAccountingPost: true"));
        }

        [Fact]
        public void The_two_json_endpoints_in_this_increment_use_the_api_safe_guard()
        {
            var admin = File.ReadAllText(Path.Combine(ControllersRoot(), "AdminController.cs"));
            var sp = admin.IndexOf(" SaveSalaryPolicy(", StringComparison.Ordinal);
            Assert.True(sp > 0);
            Assert.Contains("ApiPerm(", admin[Math.Max(0, sp - 700)..sp]);
            Assert.Contains("ApiPermAttribute.Hr", admin[Math.Max(0, sp - 700)..sp]);

            var pos = File.ReadAllText(Path.Combine(ControllersRoot(), "PosController.cs"));
            var cq = pos.IndexOf(" CustomerQuickAdd(", StringComparison.Ordinal);
            Assert.True(cq > 0);
            Assert.Contains("ApiPerm(", pos[Math.Max(0, cq - 800)..cq]);

            var body = Body(pos, "CustomerQuickAdd");
            Assert.Contains("scope.CompanyId", body);
            Assert.DoesNotContain("DefaultCompanyId", body);
        }

        // The POS lane guard must still be present as a LANE guard — removing it would lose the lane binding —
        // while never being the authorization.
        [Fact]
        public void The_pos_gate_asks_the_pos_access_service_and_not_the_lane_guard()
        {
            var pos = File.ReadAllText(Path.Combine(ControllersRoot(), "PosController.cs"));
            var gate = Body(pos, "PosGateAsync");

            Assert.Contains("_posAccess.CanAsync", gate);
            Assert.DoesNotContain("PosLaneActivityGuard", gate);
        }
    }
}
