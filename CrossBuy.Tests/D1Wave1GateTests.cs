using static CrossBuy.Tests.B6TestWiring;
using CrossBuy.BL;
using CrossBuy.BL.Platform;
using CrossBuy.Models;
using CrossBuy.Models.Context.Accounting;
using CrossBuy.Models.Context.Admin;
using CrossBuy.Models.Context.Platform;
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
    // Stage 1 Batch D1 Wave 1 — the three gates the wave introduced.
    //
    // Every allow/deny assertion here seeds REAL role rows and uses the REAL access services. That is not
    // ceremony: AccountingAccessService and ProjectsAccessService are bootstrap-open when their role tables are
    // empty for the company, so an unseeded "authorized" test passes whether or not anything is enforced. §0 pins
    // that trap in place for both modules so it cannot be quietly reintroduced.
    public class D1Wave1GateTests
    {
        private const int CompanyOne = 1;
        private const int CompanyTwo = 2;
        private const int ChiefId = 41;      // ChiefAccountant, company 1  → may post
        private const int CashierId = 43;    // Cashier, company 1          → may NOT post
        private const int FinanceId = 61;    // ProjectsFinance, company 1  → budgets and billing
        private const int PlainId = 62;      // no project role
        private const int OtherCoId = 70;    // company 2

        private static Employee Emp(int id, int companyId) => new()
        {
            ID = id, FirstName = "T", LastName = "T", FullName = "emp" + id, FullNameEn = "emp" + id,
            EmpCompanyID = companyId, IsActive = true, Address = "-", PhoneNumber = "-",
            Email = $"e{id}@example.com", ProfileImage = "-", Gender = "M", MaritalStatus = "S",
            UserId = "user-" + id,
        };

        private static BusinessContext Ctx(int employeeId, int companyId) => new()
        {
            CompanyId = companyId, EmployeeId = employeeId, UserId = "user-" + employeeId,
            Roles = Array.Empty<string>(), CorrelationId = Guid.NewGuid(),
        };

        private static AccountingAccessService Accounting(PlatformTestHost host, BusinessContext? ctx) =>
            new(host.Db, new HttpContextAccessor(),
                ctx == null ? StubContextAccessor.Unresolved() : new StubContextAccessor(ctx),
                Policies(host.Db), Log<AccountingAccessService>());

        private static ProjectsAccessService Projects(PlatformTestHost host) =>
            new(host.Db, new PlatformRoleDirectory(host.Db, NullLogger<PlatformRoleDirectory>.Instance),
                Accounting(host, null), NullLogger<ProjectsAccessService>.Instance);

        private static async Task<int> ProjectAsync(PlatformTestHost host, int companyId)
        {
            var p = new Project { CompanyID = companyId, Name = "P" + Guid.NewGuid().ToString("N")[..6], IsActive = true };
            host.Seed.Projects.Add(p);
            await host.Seed.SaveChangesAsync();
            return p.ID;
        }

        private static async Task SeedAsync(PlatformTestHost host)
        {
            host.Seed.Employee.AddRange(
                Emp(ChiefId, CompanyOne), Emp(CashierId, CompanyOne),
                Emp(FinanceId, CompanyOne), Emp(PlainId, CompanyOne), Emp(OtherCoId, CompanyTwo));

            // REAL accounting roles — this is what closes accounting's bootstrap-open for company 1.
            host.Seed.AccountingUserRoles.AddRange(
                new AccountingUserRole { CompanyID = CompanyOne, EmployeeId = ChiefId, Role = "ChiefAccountant" },
                new AccountingUserRole { CompanyID = CompanyOne, EmployeeId = CashierId, Role = "Cashier" });

            // REAL project role — closes the projects scope's bootstrap-open for company 1.
            host.Seed.PlatformRoleAssignments.Add(new PlatformRoleAssignment
            {
                CompanyID = CompanyOne, Scope = EntityRegistry.ScopeProjects,
                PrincipalType = PlatformPrincipalTypes.Employee, PrincipalId = FinanceId,
                Role = ProjectsRoles.ProjectsFinance, IsActive = true, CreatedAt = DateTime.UtcNow,
            });
            await host.Seed.SaveChangesAsync();
        }

        // ============================================================================================
        // 0. THE BOOTSTRAP-OPEN TRAP — pinned for BOTH modules this wave depends on
        // ============================================================================================

        [Fact]
        // B6 TRANSITION. This previously asserted the FINDING that an unconfigured company let a cashier post to
        // the ledger. B6 closed it: Accounting.post is Never-Bootstrap-Open, so no policy state opens it. The
        // assertion is inverted AND the test renamed, because a name saying "allows ... which is why seeding is
        // mandatory" would describe behaviour that no longer exists.
        public async Task Unseeded_accounting_DENIES_a_cashier_posting_because_post_is_never_bootstrap_open()
        {
            using var host = new PlatformTestHost(companyId: CompanyOne);
            host.Seed.Employee.Add(Emp(CashierId, CompanyOne));
            await host.Seed.SaveChangesAsync();

            var ctx = Ctx(CashierId, CompanyOne);
            Assert.True(NeverBootstrapOpen.Contains("Accounting", "post"));
            Assert.False(await Accounting(host, ctx).CanAsync(ctx, "post"));   // the finding, now CLOSED
        }

        [Fact]
        // B6 TRANSITION. Projects.billing delegates to the ACCOUNTING module's post decision. Now that
        // Accounting.post is Never-Bootstrap-Open, billing closes TRANSITIVELY on an unconfigured company — the
        // more valuable property, so it is asserted explicitly rather than left implied.
        public async Task Unseeded_projects_DENIES_billing_transitively_because_accounting_post_is_never()
        {
            using var host = new PlatformTestHost(companyId: CompanyOne);
            host.Seed.Employee.Add(Emp(PlainId, CompanyOne));
            await host.Seed.SaveChangesAsync();
            var pid = await ProjectAsync(host, CompanyOne);

            var ctx = Ctx(PlainId, CompanyOne);
            Assert.True(NeverBootstrapOpen.Contains("Accounting", "post"));
            Assert.False(await Projects(host).CanAsync(ctx, ProjectsActions.Billing, PermissionTarget.ForProject(pid)));
        }

        // Removing the role row flips the result — the diagnostic the brief asked for.
        [Fact]
        // B6 TRANSITION. Removing the role row used to flip the answer back to ALLOWED — that flip WAS the
        // exposure. Post is denied in both states now, so what this proves is that the answer is STABLE under
        // de-configuration. Renamed to say that.
        public async Task Removing_the_seeded_role_row_no_longer_reopens_posting()
        {
            using var host = new PlatformTestHost(companyId: CompanyOne);
            await SeedAsync(host);
            var ctx = Ctx(CashierId, CompanyOne);

            Assert.False(await Accounting(host, ctx).CanAsync(ctx, "post"));   // configured ⇒ denied

            host.Seed.AccountingUserRoles.RemoveRange(await host.Seed.AccountingUserRoles.ToListAsync());
            await host.Seed.SaveChangesAsync();

            Assert.False(await Accounting(host, ctx).CanAsync(ctx, "post"));   // unconfigured ⇒ STILL denied
        }

        // ============================================================================================
        // 1. THE PROJECT FINANCIAL GATE — project right, GL right, and the project ROW's company
        // ============================================================================================

        // CORRECTED EXPECTATION. My first version of this test asserted that ProjectsFinance ALONE may bill. It
        // fails, and the code is right: `ProjectsAccessService` already requires the ACCOUNTING module's own "post"
        // decision for `billing` — Batch C's rule that "stops project administration becoming a back door into
        // accounting". So billing needs BOTH rights, and the finance role by itself is not enough.
        [Fact]
        public async Task Project_billing_requires_the_accounting_post_right_as_well_as_a_project_role()
        {
            using var host = new PlatformTestHost(companyId: CompanyOne);
            await SeedAsync(host);
            var pid = await ProjectAsync(host, CompanyOne);
            var projects = Projects(host);

            // ProjectsFinance, but no accounting role ⇒ denied, because billing posts to the ledger.
            Assert.False(await projects.CanAsync(
                Ctx(FinanceId, CompanyOne), ProjectsActions.Billing, PermissionTarget.ForProject(pid)));

            // No project role at all ⇒ denied for an independent reason.
            Assert.False(await projects.CanAsync(
                Ctx(PlainId, CompanyOne), ProjectsActions.Billing, PermissionTarget.ForProject(pid)));

            // budget-manage does NOT delegate to accounting, so the finance role governs it on its own. This is
            // exactly why the controller gate passes `requireAccountingPost` for the budget-manage actions that
            // reach the GL: for those, the gate is the ONLY accounting check in the path.
            Assert.True(await projects.CanAsync(
                Ctx(FinanceId, CompanyOne), ProjectsActions.BudgetManage, PermissionTarget.ForProject(pid)));
            Assert.False(await projects.CanAsync(
                Ctx(PlainId, CompanyOne), ProjectsActions.BudgetManage, PermissionTarget.ForProject(pid)));
        }

        // The ownership authority is the PROJECT ROW's company — the case CORRECTION-005 exists for.
        [Fact]
        public async Task A_project_in_another_company_is_refused_even_to_the_finance_role()
        {
            using var host = new PlatformTestHost(companyId: CompanyOne);
            await SeedAsync(host);
            var foreign = await ProjectAsync(host, CompanyTwo);

            Assert.False(await Projects(host).CanAsync(
                Ctx(FinanceId, CompanyOne), ProjectsActions.Billing, PermissionTarget.ForProject(foreign)));
        }

        // Record-id tampering: a project id that does not exist answers exactly like a foreign one.
        [Fact]
        public async Task A_nonexistent_project_id_is_refused_identically_to_a_foreign_one()
        {
            using var host = new PlatformTestHost(companyId: CompanyOne);
            await SeedAsync(host);

            foreach (var bogus in new[] { 999999, int.MaxValue })
                Assert.False(await Projects(host).CanAsync(
                    Ctx(FinanceId, CompanyOne), ProjectsActions.Billing, PermissionTarget.ForProject(bogus)));
        }

        // THE SECOND HALF OF THE RULE. The gate requires accounting "post" for GL-reaching actions, so the project
        // finance role alone is NOT sufficient to post. This is what stops a project right becoming a posting right.
        [Fact]
        public async Task The_projects_finance_role_alone_cannot_satisfy_the_general_ledger_half_of_the_gate()
        {
            using var host = new PlatformTestHost(companyId: CompanyOne);
            await SeedAsync(host);
            var pid = await ProjectAsync(host, CompanyOne);

            var finance = Ctx(FinanceId, CompanyOne);

            // holds budget-manage on this project…
            Assert.True(await Projects(host).CanAsync(finance, ProjectsActions.BudgetManage, PermissionTarget.ForProject(pid)));
            // …but no accounting role, so the gate's GL half denies. Without that half, PostLabor /
            // PostMaterialIssue / PostEquipmentDepreciation would post journals on a project right alone.
            Assert.False(await Accounting(host, finance).CanAsync(finance, "post"));

            // and the chief, who may post, is separately governed on the project
            var chief = Ctx(ChiefId, CompanyOne);
            Assert.True(await Accounting(host, chief).CanAsync(chief, "post"));
        }

        [Fact]
        public async Task An_unresolved_context_fails_the_gate_before_any_permission_is_consulted()
        {
            var resolver = new RequestCompanyResolver(
                StubContextAccessor.Unresolved(), NullLogger<RequestCompanyResolver>.Instance);

            var r = await resolver.ResolveAsync();
            Assert.False(r.Ok);
            Assert.Equal(0, r.CompanyId);   // the gate's first step refuses, and never with company 1
        }

        // ============================================================================================
        // 2. THE API-SAFE GUARD — the one thing an MVC attribute could not do
        // ============================================================================================

        private static ActionExecutingContext ApiContext(PlatformTestHost host, BusinessContext? ctx, bool authenticated)
        {
            var services = new ServiceCollection();
            services.AddSingleton<IAccountingAccessService>(Accounting(host, ctx));
            services.AddSingleton<IInventoryAccessService>(new InventoryAccessService(
                host.Db, new HttpContextAccessor(),
                ctx == null ? StubContextAccessor.Unresolved() : new StubContextAccessor(ctx),
                Policies(host.Db), Log<InventoryAccessService>()));

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
        public async Task An_unauthenticated_json_caller_gets_401_and_not_a_redirect()
        {
            using var host = new PlatformTestHost(companyId: CompanyOne);
            await SeedAsync(host);

            var ctxObj = ApiContext(host, null, authenticated: false);
            await new ApiPermAttribute(ApiPermAttribute.Accounting, "post")
                .OnActionExecutionAsync(ctxObj, () => throw new Exception("the action must not run"));

            var json = Assert.IsType<JsonResult>(ctxObj.Result);
            Assert.Equal(401, json.StatusCode);
            // The defect this guard exists to prevent: a JSON client receiving an HTML login redirect.
            Assert.IsNotType<RedirectToActionResult>(ctxObj.Result);
        }

        [Fact]
        public async Task An_authenticated_but_unauthorized_json_caller_gets_403_in_the_projects_json_shape()
        {
            using var host = new PlatformTestHost(companyId: CompanyOne);
            await SeedAsync(host);

            var ctxObj = ApiContext(host, Ctx(CashierId, CompanyOne), authenticated: true);
            await new ApiPermAttribute(ApiPermAttribute.Accounting, "post")
                .OnActionExecutionAsync(ctxObj, () => throw new Exception("the action must not run"));

            var json = Assert.IsType<JsonResult>(ctxObj.Result);
            Assert.Equal(403, json.StatusCode);
            Assert.Contains("ok", json.Value!.ToString()!);   // the { ok = false, error = ... } shape
        }

        [Fact]
        public async Task An_authorized_json_caller_reaches_the_action()
        {
            using var host = new PlatformTestHost(companyId: CompanyOne);
            await SeedAsync(host);

            var ran = false;
            var ctxObj = ApiContext(host, Ctx(ChiefId, CompanyOne), authenticated: true);
            await new ApiPermAttribute(ApiPermAttribute.Accounting, "post")
                .OnActionExecutionAsync(ctxObj, () => { ran = true; return Task.FromResult<ActionExecutedContext>(null!); });

            Assert.True(ran);
            Assert.Null(ctxObj.Result);
        }

        // An unknown module denies rather than falling through — and a MISSING access service denies too, which is
        // where this guard deliberately differs from AccPerm/InvPerm (they call next() when the service is absent).
        [Fact]
        public async Task An_unknown_module_and_a_missing_service_both_deny()
        {
            using var host = new PlatformTestHost(companyId: CompanyOne);
            await SeedAsync(host);

            var unknown = ApiContext(host, Ctx(ChiefId, CompanyOne), authenticated: true);
            await new ApiPermAttribute("nope", "post")
                .OnActionExecutionAsync(unknown, () => throw new Exception("must not run"));
            Assert.Equal(403, Assert.IsType<JsonResult>(unknown.Result).StatusCode);

            var empty = new DefaultHttpContext { RequestServices = new ServiceCollection().BuildServiceProvider() };
            empty.User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, "u") }, "t"));
            var noService = new ActionExecutingContext(
                new ActionContext(empty, new RouteData(), new ActionDescriptor()),
                new List<IFilterMetadata>(), new Dictionary<string, object?>(), controller: null!);

            await new ApiPermAttribute(ApiPermAttribute.Accounting, "post")
                .OnActionExecutionAsync(noService, () => throw new Exception("must not run"));
            Assert.Equal(403, Assert.IsType<JsonResult>(noService.Result).StatusCode);
        }

        // ============================================================================================
        // 3. STRUCTURAL — the remediated paths no longer read a company literal
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

        public static IEnumerable<object[]> RemediatedProjectActions() => new[]
        {
            "SaveBilling", "ApproveBilling", "PostBilling", "DeleteBilling",
            "SaveSubBilling", "ApproveSubBilling", "PostSubBilling", "DeleteSubBilling",
            "ReceiveAdvance", "ReleaseRetention", "ReleaseSubRetention",
            "SaveEquipmentDepreciation", "PostEquipmentDepreciation", "DeleteEquipmentDepreciation",
            "PostLabor", "SaveMaterialIssue", "PostMaterialIssue", "DeleteMaterialIssue",
            "SaveVariationOrder", "ApproveVariationOrder", "DeleteVariationOrder",
            "SaveBoq", "SaveSubcontract",
        }.Select(a => new object[] { a });

        [Theory]
        [MemberData(nameof(RemediatedProjectActions))]
        public void Every_remediated_project_action_is_gated_and_uses_the_validated_company(string action)
        {
            var body = Body(File.ReadAllText(Path.Combine(ControllersRoot(), "ProjectController.cs")), action);

            Assert.Contains("GateAsync(", body);
            Assert.Contains("if (!gate.Ok) return gate.Denied!;", body);
            Assert.DoesNotContain("DefaultCompanyId", body);
        }

        // The GL-reaching subset must additionally demand accounting "post". Named individually, because "the
        // posting ones" is not something a future reader can verify.
        [Theory]
        [InlineData("PostBilling")]
        [InlineData("ApproveBilling")]
        [InlineData("DeleteBilling")]
        [InlineData("PostSubBilling")]
        [InlineData("ApproveSubBilling")]
        [InlineData("DeleteSubBilling")]
        [InlineData("ReceiveAdvance")]
        [InlineData("ReleaseRetention")]
        [InlineData("ReleaseSubRetention")]
        [InlineData("PostEquipmentDepreciation")]
        [InlineData("DeleteEquipmentDepreciation")]
        [InlineData("PostLabor")]
        [InlineData("PostMaterialIssue")]
        [InlineData("DeleteMaterialIssue")]
        public void Every_general_ledger_reaching_project_action_also_requires_the_accounting_post_right(string action)
        {
            var body = Body(File.ReadAllText(Path.Combine(ControllersRoot(), "ProjectController.cs")), action);
            Assert.Contains("requireAccountingPost: true", body);
        }

        [Theory]
        [InlineData("AccountingController.cs", "CustomerQuickAdd", "Accounting, \"post\"")]
        [InlineData("AccountingController.cs", "VendorQuickAdd", "Accounting, \"post\"")]
        [InlineData("InventoryController.cs", "WarehouseQuickAdd", "Inventory, \"manage\"")]
        public void Every_remediated_json_master_data_endpoint_uses_the_api_safe_guard(
            string file, string action, string expected)
        {
            var text = File.ReadAllText(Path.Combine(ControllersRoot(), file));
            var idx = text.IndexOf(" " + action + "(", StringComparison.Ordinal);
            Assert.True(idx > 0);

            var attrs = text[Math.Max(0, idx - 600)..idx];
            Assert.Contains("ApiPerm(", attrs);
            Assert.Contains(expected, attrs);

            var body = Body(text, action);
            Assert.Contains("scope.CompanyId", body);
            Assert.DoesNotContain("DefaultCompanyId", body);
        }

        [Theory]
        [InlineData("PostLaborToWO", false)]
        [InlineData("GenerateInvoice", true)]
        public void The_remediated_task_financial_actions_are_gated(string action, bool needsAccountingPost)
        {
            var body = Body(File.ReadAllText(Path.Combine(ControllersRoot(), "TasksController.cs")), action);

            Assert.Contains("TaskGateAsync(", body);
            Assert.Contains("gate.CompanyId", body);
            Assert.DoesNotContain("DefaultCompanyId", body);
            Assert.Equal(needsAccountingPost, body.Contains("requireAccountingPost: true"));
        }
    }
}
