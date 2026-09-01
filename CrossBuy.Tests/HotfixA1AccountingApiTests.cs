using static CrossBuy.Tests.B6TestWiring;
using System.Security.Claims;
using CrossBuy.BL;
using CrossBuy.BL.Platform;
using CrossBuy.Controllers.Api;
using CrossBuy.Models.Context.Accounting;
using CrossBuy.Models.Context.Admin;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CrossBuy.Tests
{
    // Stage 1 Hotfix A.1 — AccountingApiController authorization and company isolation.
    //
    // Two layers, deliberately:
    //
    //   * the GUARD is tested against the REAL AccountingAccessService over real AccountingUserRoles rows and a
    //     real BusinessContext resolved from JWT-shaped claims. Every security decision lives there, so this is
    //     where the twenty-one required cases are proven — against real role data, not a stub that agrees with me.
    //   * the CONTROLLER is tested action by action for ALL TEN mutating actions, with recording services. That is
    //     the only way to prove the two claims that matter per endpoint: a refused request calls NOTHING (no
    //     journal, no partial data), and an allowed request passes the VALIDATED company and the resolved actor —
    //     never the value that arrived on the request.
    public class HotfixA1AccountingApiTests
    {
        private const int CompanyOne = 1;
        private const int CompanyTwo = 2;
        private const int ChiefId = 41;        // ChiefAccountant in company 1
        private const int AccountantId = 42;   // Accountant in company 1
        private const int CashierId = 43;      // Cashier in company 1
        private const int AuditorId = 44;      // Auditor in company 1 — read-only
        private const int OtherCoChief = 45;   // ChiefAccountant in company 2

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

        // Seeds the four accounting roles that make the permission matrix meaningful. The service is
        // bootstrap-open until at least one role exists in the company, so a role row is REQUIRED for any of these
        // tests to prove anything — without one, everything would be allowed and every assertion would pass
        // vacuously.
        private static async Task SeedAsync(PlatformTestHost host)
        {
            host.Seed.Employee.AddRange(
                Emp(ChiefId, CompanyOne), Emp(AccountantId, CompanyOne), Emp(CashierId, CompanyOne),
                Emp(AuditorId, CompanyOne), Emp(OtherCoChief, CompanyTwo));
            host.Seed.AccountingUserRoles.AddRange(
                new AccountingUserRole { CompanyID = CompanyOne, EmployeeId = ChiefId, Role = "ChiefAccountant" },
                new AccountingUserRole { CompanyID = CompanyOne, EmployeeId = AccountantId, Role = "Accountant" },
                new AccountingUserRole { CompanyID = CompanyOne, EmployeeId = CashierId, Role = "Cashier" },
                new AccountingUserRole { CompanyID = CompanyOne, EmployeeId = AuditorId, Role = "Auditor" },
                new AccountingUserRole { CompanyID = CompanyTwo, EmployeeId = OtherCoChief, Role = "ChiefAccountant" });
            await host.Seed.SaveChangesAsync();
        }

        // An HttpContext carrying ONLY what TokenService puts in the JWT: NameIdentifier + employeeId, and NO role
        // claims. That absence is the point — the accounting decision must come from AccountingUserRoles, and no
        // API caller can ever hold a platform-admin role.
        private static IHttpContextAccessor BearerFor(int employeeId)
        {
            var identity = new ClaimsIdentity(new[]
            {
                new Claim(ClaimTypes.NameIdentifier, "user-" + employeeId),
                new Claim("employeeId", employeeId.ToString()),
                new Claim(ClaimTypes.Name, "user-" + employeeId),
            }, "Bearer");
            var http = new DefaultHttpContext { User = new ClaimsPrincipal(identity) };
            return new HttpContextAccessor { HttpContext = http };
        }

        // No identity at all — a token that resolves to no Employee row.
        private static IHttpContextAccessor Anonymous() => new HttpContextAccessor { HttpContext = new DefaultHttpContext() };

        // The REAL guard over the REAL access service. Nothing here is stubbed.
        private static IAccountingApiAuthorization Guard(PlatformTestHost host, IHttpContextAccessor http)
        {
            var factory = new BusinessContextFactory(
                http, host.Db, host.Holder, NullLogger<BusinessContextFactory>.Instance);
            var accessor = new BusinessContextAccessor(factory);
            var accounting = new AccountingAccessService(host.Db, http, accessor, Policies(host.Db), Log<AccountingAccessService>());

            return new AccountingApiAuthorization(
                accessor,
                new IModuleAccessService[] { accounting },
                NullLogger<AccountingApiAuthorization>.Instance);
        }

        private static int StatusOf(IActionResult? result) => result switch
        {
            ObjectResult o => o.StatusCode ?? 0,
            StatusCodeResult s => s.StatusCode,
            _ => 0,
        };

        // ============================================================================================
        // 1-3, 10-11: authentication is not authorization; missing context denies; no company-1 fallback
        // ============================================================================================

        // A1/6 + A5/1: a request with no resolvable identity is refused. Authentication is handled by
        // [Authorize] before this point; what this proves is that a token which cannot be tied to an employee is
        // NOT treated as authorized.
        [Fact]
        public async Task A_request_with_no_resolvable_identity_is_denied()
        {
            using var host = new PlatformTestHost(companyId: null);
            await SeedAsync(host);

            var decision = await Guard(host, Anonymous()).AuthorizeAsync("post", null);

            Assert.False(decision.Ok);
            Assert.Equal(StatusCodes.Status403Forbidden, StatusOf(decision.Error));
        }

        // A5/10: an EXISTING employee whose company cannot be established (inactive, or no EmpCompanyID) is denied
        // — the case that used to become company 1.
        [Theory]
        [InlineData(false, 0)]    // no company assigned
        [InlineData(true, 0)]     // no company assigned, active
        public async Task An_employee_without_a_resolvable_company_is_denied(bool active, int companyId)
        {
            using var host = new PlatformTestHost(companyId: null);
            host.Seed.Employee.Add(new Employee
            {
                ID = 77, FirstName = "T", LastName = "T", FullName = "no-company", FullNameEn = "no-company",
                EmpCompanyID = companyId, IsActive = active, Address = "-", PhoneNumber = "-",
                Email = "e77@example.com", ProfileImage = "-", Gender = "M", MaritalStatus = "S", UserId = "user-77",
            });
            await host.Seed.SaveChangesAsync();

            var decision = await Guard(host, BearerFor(77)).AuthorizeAsync("post", null);

            Assert.False(decision.Ok);
            // And critically: it did not become company 1.
            Assert.NotEqual(CompanyOne, decision.CompanyId);
            Assert.Equal(0, decision.CompanyId);
        }

        // A5/11, stated positively: the company always comes from the Employee row. A company-2 accountant gets
        // company 2 — never 1 — even though 1 was the old default for every parameter on this controller.
        [Fact]
        public async Task The_resolved_company_comes_from_the_employee_row_not_from_a_default()
        {
            using var host = new PlatformTestHost(companyId: null);
            await SeedAsync(host);

            var decision = await Guard(host, BearerFor(OtherCoChief)).AuthorizeAsync("post", null);

            Assert.True(decision.Ok);
            Assert.Equal(CompanyTwo, decision.CompanyId);
            Assert.Equal(OtherCoChief, decision.EmployeeId);
        }

        // ============================================================================================
        // 2-3: the permission matrix, against REAL role rows
        // ============================================================================================

        // The full matrix. It is derived from AccountingAccessService's own rules — post = Chief|Accountant,
        // pay = Chief|Accountant|Cashier, manage = Chief only, read = anyone in the company — and it is the
        // evidence that the API grants exactly what the MVC screens grant, no more.
        [Theory]
        // read
        [InlineData(ChiefId, "read", true)]
        [InlineData(AccountantId, "read", true)]
        [InlineData(CashierId, "read", true)]
        [InlineData(AuditorId, "read", true)]
        // post — journals, invoices, customers, vendors
        [InlineData(ChiefId, "post", true)]
        [InlineData(AccountantId, "post", true)]
        [InlineData(CashierId, "post", false)]
        [InlineData(AuditorId, "post", false)]
        // pay — receipts and payments
        [InlineData(ChiefId, "pay", true)]
        [InlineData(AccountantId, "pay", true)]
        [InlineData(CashierId, "pay", true)]
        [InlineData(AuditorId, "pay", false)]
        // manage — the payroll run. Chief ONLY.
        [InlineData(ChiefId, "manage", true)]
        [InlineData(AccountantId, "manage", false)]
        [InlineData(CashierId, "manage", false)]
        [InlineData(AuditorId, "manage", false)]
        public async Task The_permission_matrix_matches_the_accounting_access_service(int employeeId, string action, bool expected)
        {
            using var host = new PlatformTestHost(companyId: null);
            await SeedAsync(host);

            var decision = await Guard(host, BearerFor(employeeId)).AuthorizeAsync(action, null);

            Assert.Equal(expected, decision.Ok);
            if (!expected) Assert.Equal(StatusCodes.Status403Forbidden, StatusOf(decision.Error));
        }

        // A1/4: an action the module does not define DENIES. It must not fall through to `read`, and it must not be
        // read as "no rule configured, therefore allow".
        [Theory]
        [InlineData("payroll")]       // plausible, and NOT in the vocabulary
        [InlineData("reverse")]       // plausible, and NOT in the vocabulary
        [InlineData("create")]
        [InlineData("confidential")]
        [InlineData("")]
        public async Task An_action_outside_the_accounting_vocabulary_is_denied(string action)
        {
            using var host = new PlatformTestHost(companyId: null);
            await SeedAsync(host);

            // The CHIEF asks — the most privileged accounting actor there is. It is still refused.
            var decision = await Guard(host, BearerFor(ChiefId)).AuthorizeAsync(action, null);

            Assert.False(decision.Ok);
            Assert.Equal(StatusCodes.Status403Forbidden, StatusOf(decision.Error));
        }

        // ============================================================================================
        // 6-8: company tampering
        // ============================================================================================

        // A5/6 + A5/7: a supplied company id that is not the caller's is REJECTED — not coerced, not honoured.
        // The same guard call covers query-string and body tampering because both arrive as this one argument;
        // the controller tests below prove each endpoint actually routes its own parameter into it.
        [Fact]
        public async Task A_supplied_company_that_is_not_the_callers_is_rejected()
        {
            using var host = new PlatformTestHost(companyId: null);
            await SeedAsync(host);

            var decision = await Guard(host, BearerFor(ChiefId)).AuthorizeAsync("post", CompanyTwo);

            Assert.False(decision.Ok);
            Assert.Equal(StatusCodes.Status403Forbidden, StatusOf(decision.Error));
        }

        // ...and the reverse direction, so the rule is symmetric rather than "company 1 is special".
        [Fact]
        public async Task A_company_two_caller_cannot_name_company_one()
        {
            using var host = new PlatformTestHost(companyId: null);
            await SeedAsync(host);

            var decision = await Guard(host, BearerFor(OtherCoChief)).AuthorizeAsync("post", CompanyOne);

            Assert.False(decision.Ok);
        }

        // Supplying your OWN company is fine — the parameter is preserved for compatibility and an honest client
        // that sends the right value must keep working.
        [Fact]
        public async Task Supplying_the_callers_own_company_is_accepted()
        {
            using var host = new PlatformTestHost(companyId: null);
            await SeedAsync(host);

            var decision = await Guard(host, BearerFor(ChiefId)).AuthorizeAsync("post", CompanyOne);

            Assert.True(decision.Ok);
            Assert.Equal(CompanyOne, decision.CompanyId);
        }

        // An omitted or non-positive value means "not supplied" and can never widen anything: the resolved company
        // is used. This is what makes the default-0 change safe for clients that never sent the parameter.
        [Theory]
        [InlineData(0)]
        [InlineData(-1)]
        [InlineData(null)]
        public async Task An_omitted_company_falls_back_to_the_resolved_company_and_not_to_one(int? supplied)
        {
            using var host = new PlatformTestHost(companyId: null);
            await SeedAsync(host);

            var decision = await Guard(host, BearerFor(OtherCoChief)).AuthorizeAsync("post", supplied);

            Assert.True(decision.Ok);
            Assert.Equal(CompanyTwo, decision.CompanyId);   // the EMPLOYEE's company, not 1 and not 0
        }

        // A5/9: cross-company access is NOT available on this surface. The JWT carries no role claim, so an API
        // caller cannot satisfy CompanyBypassPolicy — asserted here so the decision cannot rot into an assumption.
        [Fact]
        public async Task An_api_caller_cannot_obtain_a_cross_company_bypass()
        {
            using var host = new PlatformTestHost(companyId: null);
            await SeedAsync(host);

            var http = BearerFor(ChiefId);
            var factory = new BusinessContextFactory(http, host.Db, host.Holder, NullLogger<BusinessContextFactory>.Instance);
            var context = await factory.ForHttpAsync();

            Assert.Empty(context.Roles);   // no role claims are issued by TokenService

            var bypass = host.Bypass(host.Holder);
            Assert.Throws<CrossBuy.Models.Platform.CompanyBypassDeniedException>(() => bypass.Begin(
                CrossBuy.Models.Platform.CompanyBypassKind.CrossCompanyAdministration, context, "api attempt"));
        }

        // A5/9 second half: the guard does not consult a bypass even when one IS somehow in force. The company
        // still comes from the context, so an ambient bypass cannot turn a tampered id into an authorized one.
        [Fact]
        public async Task An_ambient_bypass_does_not_make_a_tampered_company_acceptable()
        {
            using var host = new PlatformTestHost(companyId: null);
            await SeedAsync(host);

            using var _ = host.HostBypass().BeginPlatformDispatch("simulating an ambient platform bypass");

            var decision = await Guard(host, BearerFor(ChiefId)).AuthorizeAsync("post", CompanyTwo);

            Assert.False(decision.Ok);
        }

        // A1/9: the refusal says nothing useful to an attacker — no role, no company, no employee, no exception
        // text — and it keeps the project's existing { success, message } shape.
        [Fact]
        public async Task A_refusal_reveals_no_permission_internals_and_keeps_the_response_shape()
        {
            using var host = new PlatformTestHost(companyId: null);
            await SeedAsync(host);

            var decision = await Guard(host, BearerFor(AuditorId)).AuthorizeAsync("post", null);
            var body = Assert.IsType<ObjectResult>(decision.Error).Value!;
            var json = System.Text.Json.JsonSerializer.Serialize(body);

            Assert.Contains("\"success\":false", json);
            Assert.Contains("message", json);
            foreach (var leak in new[] { "Auditor", "ChiefAccountant", "Accountant", "Cashier", "post",
                                         "AccountingUserRole", "Exception", "CompanyID" })
                Assert.DoesNotContain(leak, json, StringComparison.OrdinalIgnoreCase);
        }

        // ============================================================================================
        // Endpoint-specific: ALL TEN mutating actions
        // ============================================================================================

        // Every mutating action, its required permission, and an actor who does NOT hold it. Each case asserts
        // three things: 403, and that the service layer was NEVER reached (no journal, no invoice, no partial
        // data), and that no company leaked into a service call.
        [Theory]
        [InlineData("CreateJournal", AuditorId)]
        [InlineData("Post", AuditorId)]
        [InlineData("Reverse", AuditorId)]
        [InlineData("PayrollPost", AccountantId)]     // manage ⇒ even an Accountant is refused
        [InlineData("CreateCustomer", CashierId)]     // post ⇒ a Cashier is refused
        [InlineData("CreateSalesInvoice", CashierId)]
        [InlineData("CreateReceipt", AuditorId)]      // pay ⇒ an Auditor is refused
        [InlineData("CreateVendor", CashierId)]
        [InlineData("CreatePurchaseInvoice", CashierId)]
        [InlineData("CreatePayment", AuditorId)]
        public async Task An_unauthorized_caller_is_refused_and_no_service_is_called(string action, int employeeId)
        {
            using var host = new PlatformTestHost(companyId: null);
            await SeedAsync(host);
            var (controller, recorder) = Controller(host, BearerFor(employeeId));

            var result = await Invoke(controller, action, companyId: CompanyOne);

            Assert.Equal(StatusCodes.Status403Forbidden, StatusOf(result));
            Assert.Empty(recorder.Calls);          // nothing was attempted: no journal, no invoice, no payroll
        }

        // The same ten, authorized — each must reach its service with the VALIDATED company and, where the service
        // signature has somewhere to put it, the resolved actor. This is the assertion that proves
        // `dto.CompanyID` / `?companyId=` is no longer the source of truth.
        //
        // `carriesCompany` / `carriesActor` are per-signature FACTS, not conveniences:
        //   * PostAsync(entryId, userId) and ReverseAsync(entryId, userId, reason) take NO company — the entry
        //     carries it, which is why the controller checks ownership before calling them;
        //   * CreateCustomerAsync / CreateVendorAsync take NO userId, so no actor can be recorded on master-data
        //     creation. That is a pre-existing limitation of those two service signatures, reported rather than
        //     papered over by widening a service contract inside a security hotfix.
        [Theory]
        [InlineData("CreateJournal", ChiefId, true, true)]
        [InlineData("Post", ChiefId, false, true)]
        [InlineData("Reverse", ChiefId, false, true)]
        [InlineData("PayrollPost", ChiefId, true, true)]
        [InlineData("CreateCustomer", ChiefId, true, false)]
        [InlineData("CreateSalesInvoice", ChiefId, true, true)]
        [InlineData("CreateReceipt", CashierId, true, true)]
        [InlineData("CreateVendor", AccountantId, true, false)]
        [InlineData("CreatePurchaseInvoice", AccountantId, true, true)]
        [InlineData("CreatePayment", CashierId, true, true)]
        public async Task An_authorized_caller_reaches_the_service_with_the_validated_company_and_actor(
            string action, int employeeId, bool carriesCompany, bool carriesActor)
        {
            using var host = new PlatformTestHost(companyId: CompanyOne);
            await SeedAsync(host);

            // Post/Reverse address an existing entry, so one has to exist for them to get past the ownership check.
            // It belongs to company 1 — the caller's own company — because this theory is about the ALLOWED path.
            var mine = new JournalEntry
            {
                CompanyID = CompanyOne, EntryNo = "JV-MINE", EntryDate = new DateTime(2026, 1, 1),
                FiscalPeriodId = 1, CurrencyId = 1, Status = "Draft",
            };
            host.Seed.JournalEntries.Add(mine);
            await host.Seed.SaveChangesAsync();

            var (controller, recorder) = Controller(host, BearerFor(employeeId));

            var result = await Invoke(controller, action, companyId: CompanyOne, entryId: mine.ID);

            Assert.NotEqual(StatusCodes.Status403Forbidden, StatusOf(result));
            var call = Assert.Single(recorder.Calls);

            if (carriesCompany) Assert.Equal(CompanyOne, call.CompanyId);
            if (carriesActor) Assert.Equal(employeeId, call.UserId);   // the actor is recorded — it used to be null
        }

        // THE tampering test, per endpoint: a company-1 actor who supplies company 2 is refused, and the service is
        // never called — so company 2 receives no journal, no invoice, no customer and no vendor.
        [Theory]
        [InlineData("CreateJournal", ChiefId)]
        [InlineData("PayrollPost", ChiefId)]
        [InlineData("CreateCustomer", ChiefId)]
        [InlineData("CreateSalesInvoice", ChiefId)]
        [InlineData("CreateReceipt", ChiefId)]
        [InlineData("CreateVendor", ChiefId)]
        [InlineData("CreatePurchaseInvoice", ChiefId)]
        [InlineData("CreatePayment", ChiefId)]
        public async Task Company_tampering_is_refused_per_endpoint_and_nothing_is_written(string action, int employeeId)
        {
            using var host = new PlatformTestHost(companyId: null);
            await SeedAsync(host);
            var (controller, recorder) = Controller(host, BearerFor(employeeId));

            var result = await Invoke(controller, action, companyId: CompanyTwo);

            Assert.Equal(StatusCodes.Status403Forbidden, StatusOf(result));
            Assert.Empty(recorder.Calls);
        }

        // `Post` and `Reverse` carry NO company parameter — they address a journal by id. An id belonging to another
        // company must answer exactly as a missing id does, and must not reach the service.
        [Theory]
        [InlineData("Post")]
        [InlineData("Reverse")]
        public async Task Acting_on_another_companys_journal_by_id_is_not_found_and_never_posted(string action)
        {
            using var host = new PlatformTestHost(companyId: CompanyOne);
            await SeedAsync(host);

            // A company-2 journal, arranged through the authorized cross-company context.
            var theirs = new JournalEntry
            {
                CompanyID = CompanyTwo, EntryNo = "JV-OTHER", EntryDate = new DateTime(2026, 1, 1),
                FiscalPeriodId = 1, CurrencyId = 1, Status = "Draft",
            };
            host.Seed.JournalEntries.Add(theirs);
            await host.Seed.SaveChangesAsync();

            var (controller, recorder) = Controller(host, BearerFor(ChiefId));
            var result = await Invoke(controller, action, companyId: 0, entryId: theirs.ID);

            Assert.Equal(StatusCodes.Status404NotFound, StatusOf(result));
            Assert.Empty(recorder.Calls);

            // ...and a genuinely absent id gives the SAME answer, so existence cannot be probed.
            var absent = await Invoke(controller, action, companyId: 0, entryId: 999_999);
            Assert.Equal(StatusCodes.Status404NotFound, StatusOf(absent));
        }

        // ...and the caller's OWN journal is reachable, so the 404 above was the isolation rule and not a broken
        // lookup — the same discipline the event monitor's tests use.
        [Theory]
        [InlineData("Post")]
        [InlineData("Reverse")]
        public async Task Acting_on_your_own_journal_by_id_reaches_the_service(string action)
        {
            using var host = new PlatformTestHost(companyId: CompanyOne);
            await SeedAsync(host);

            var mine = new JournalEntry
            {
                CompanyID = CompanyOne, EntryNo = "JV-MINE", EntryDate = new DateTime(2026, 1, 1),
                FiscalPeriodId = 1, CurrencyId = 1, Status = "Draft",
            };
            host.Seed.JournalEntries.Add(mine);
            await host.Seed.SaveChangesAsync();

            var (controller, recorder) = Controller(host, BearerFor(ChiefId));
            var result = await Invoke(controller, action, companyId: 0, entryId: mine.ID);

            Assert.NotEqual(StatusCodes.Status404NotFound, StatusOf(result));
            var call = Assert.Single(recorder.Calls);
            // PostAsync/ReverseAsync take no company, so the actor is what there is to assert — and it is now
            // populated, where the API previously passed null on both.
            Assert.Equal(ChiefId, call.UserId);
        }

        // The reads are guarded too: a company-1 auditor asking for company 2's trial balance, ledger, customers,
        // vendors, aging, chart of accounts or dashboard is REFUSED — not handed an empty result that reads as
        // "company 2 has no data".
        [Theory]
        [InlineData("Summary")]
        [InlineData("Accounts")]
        [InlineData("Journals")]
        [InlineData("TrialBalance")]
        [InlineData("Ledger")]
        [InlineData("PayrollPreview")]
        [InlineData("Customers")]
        [InlineData("ArAging")]
        [InlineData("Vendors")]
        [InlineData("ApAging")]
        public async Task A_read_naming_another_company_is_refused(string action)
        {
            using var host = new PlatformTestHost(companyId: null);
            await SeedAsync(host);
            var (controller, recorder) = Controller(host, BearerFor(AuditorId));

            var result = await Invoke(controller, action, companyId: CompanyTwo);

            Assert.Equal(StatusCodes.Status403Forbidden, StatusOf(result));
            Assert.Empty(recorder.Calls);
        }

        // ============================================================================================
        // A4: the security posture is what the analysis says it is
        // ============================================================================================

        // Bearer-only, asserted from the attribute. If someone later adds a cookie scheme, the CSRF reasoning in
        // ADR-025 stops being true — and this test fails, which is the intent.
        [Fact]
        public void The_controller_is_bearer_only_which_is_why_antiforgery_is_not_required()
        {
            var authorize = typeof(AccountingApiController)
                .GetCustomAttributes(typeof(Microsoft.AspNetCore.Authorization.AuthorizeAttribute), false)
                .Cast<Microsoft.AspNetCore.Authorization.AuthorizeAttribute>()
                .Single();

            Assert.Equal(Microsoft.AspNetCore.Authentication.JwtBearer.JwtBearerDefaults.AuthenticationScheme,
                         authorize.AuthenticationSchemes);

            // No antiforgery anywhere on the controller or its actions — deliberate, for a bearer-only surface.
            Assert.Empty(typeof(AccountingApiController)
                .GetCustomAttributes(typeof(Microsoft.AspNetCore.Mvc.ValidateAntiForgeryTokenAttribute), true));
            Assert.All(typeof(AccountingApiController).GetMethods().Where(m => m.IsPublic && m.DeclaringType == typeof(AccountingApiController)),
                m => Assert.Empty(m.GetCustomAttributes(typeof(Microsoft.AspNetCore.Mvc.ValidateAntiForgeryTokenAttribute), true)));
        }

        // Every mutating action must be authorized. Asserted structurally so an ELEVENTH action cannot be added
        // later without a permission — the scanner counts attributes and would see nothing here, because this
        // controller authorizes in-body through the guard.
        [Fact]
        public void Every_mutating_action_calls_the_guard()
        {
            var source = File.ReadAllText(Path.Combine(RepoRoot(), "CrossBuy", "Controllers", "Api", "AccountingApiController.cs"));
            var lines = source.Split('\n');

            var unguarded = new List<string>();
            for (int i = 0; i < lines.Length; i++)
            {
                if (!lines[i].Contains("[HttpPost", StringComparison.Ordinal)) continue;
                // The guard must appear within the first few statements of the action that follows.
                var window = string.Join("\n", lines.Skip(i).Take(14));
                if (!window.Contains("_guard.AuthorizeAsync", StringComparison.Ordinal))
                    unguarded.Add(lines[i].Trim());
            }

            Assert.True(unguarded.Count == 0,
                "These mutating actions do not call the authorization guard:\n  " + string.Join("\n  ", unguarded));
        }

        // ...and no action may pass a request-supplied company into a service. Asserted on the source because that
        // is where the mistake would be made: the guard cannot prevent an action ignoring its answer.
        [Fact]
        public void No_action_passes_a_request_supplied_company_to_a_service()
        {
            var source = File.ReadAllText(Path.Combine(RepoRoot(), "CrossBuy", "Controllers", "Api", "AccountingApiController.cs"));

            // `dto.CompanyID` may be READ only on the line that validates it. Any other line mentioning it is a line
            // that could pass it onward — which is the mistake this test exists to catch, and the one the original
            // code made on five endpoints.
            var offenders = source.Split('\n')
                .Select((line, i) => (Line: line.Trim(), Number: i + 1))
                .Where(x => x.Line.Contains("dto.CompanyID", StringComparison.Ordinal)
                         && !x.Line.Contains("AuthorizeAsync", StringComparison.Ordinal)
                         && !x.Line.StartsWith("//", StringComparison.Ordinal)
                         && !x.Line.Contains("public int CompanyID", StringComparison.Ordinal))
                .ToList();

            Assert.True(offenders.Count == 0,
                "These lines read dto.CompanyID somewhere other than its validation:\n  " +
                string.Join("\n  ", offenders.Select(o => $"{o.Number}: {o.Line}")));

            // ...and each of the five body-carrying endpoints does validate it.
            Assert.Equal(5, System.Text.RegularExpressions.Regex.Matches(source, @"AuthorizeAsync\(Perm\w+, dto\.CompanyID").Count);
        }

        private static string RepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "CrossBuy.sln"))) dir = dir.Parent;
            Assert.NotNull(dir);
            return dir!.FullName;
        }

        // ============================================================================================
        // controller construction + invocation
        // ============================================================================================

        private static (AccountingApiController controller, ServiceRecorder recorder) Controller(
            PlatformTestHost host, IHttpContextAccessor http)
        {
            var recorder = new ServiceRecorder();
            var controller = new AccountingApiController(
                recorder, host.Db, recorder, recorder, recorder, recorder, recorder, recorder,
                Guard(host, http));
            controller.ControllerContext = new ControllerContext { HttpContext = http.HttpContext! };
            return (controller, recorder);
        }

        // One dispatcher instead of ten near-identical call sites, so the Theories above stay readable and every
        // action is exercised through its REAL signature.
        private static async Task<IActionResult?> Invoke(
            AccountingApiController c, string action, int companyId, int entryId = 1) => action switch
        {
            // --- mutating ---
            "CreateJournal" => await c.CreateJournal(new AccountingApiController.CreateJournalDto
            {
                CompanyID = companyId, EntryDate = new DateTime(2026, 1, 1), PostNow = true,
            }),
            "Post" => await c.Post(entryId),
            "Reverse" => await c.Reverse(entryId, null),
            "PayrollPost" => await c.PayrollPost(companyId, 2026, 1),
            "CreateCustomer" => await c.CreateCustomer(new AccountingApiController.CustomerDto { Name = "c" }, companyId),
            "CreateSalesInvoice" => await c.CreateSalesInvoice(new AccountingApiController.SalesInvoiceDto
            {
                CompanyID = companyId, CustomerId = 1, InvoiceDate = new DateTime(2026, 1, 1),
            }),
            "CreateReceipt" => await c.CreateReceipt(new AccountingApiController.ReceiptDto
            {
                CompanyID = companyId, CustomerId = 1, ReceiptDate = new DateTime(2026, 1, 1), Amount = 10, CashAccountId = 1,
            }),
            "CreateVendor" => await c.CreateVendor(new AccountingApiController.VendorDto { Name = "v" }, companyId),
            "CreatePurchaseInvoice" => await c.CreatePurchaseInvoice(new AccountingApiController.PurchaseInvoiceDto
            {
                CompanyID = companyId, VendorId = 1, InvoiceDate = new DateTime(2026, 1, 1),
            }),
            "CreatePayment" => await c.CreatePayment(new AccountingApiController.PaymentDto
            {
                CompanyID = companyId, VendorId = 1, PaymentDate = new DateTime(2026, 1, 1), Amount = 10, CashAccountId = 1,
            }),
            // --- reads ---
            "Summary" => await c.Summary(companyId),
            "Accounts" => await c.Accounts(companyId),
            "Journals" => await c.Journals(companyId),
            "TrialBalance" => await c.TrialBalance(companyId),
            "Ledger" => await c.Ledger(1, companyId),
            "PayrollPreview" => await c.PayrollPreview(companyId, 2026, 1),
            "Customers" => await c.Customers(companyId),
            "ArAging" => await c.ArAging(companyId),
            "Vendors" => await c.Vendors(companyId),
            "ApAging" => await c.ApAging(companyId),
            _ => throw new InvalidOperationException("Unhandled action " + action),
        };
    }
}
