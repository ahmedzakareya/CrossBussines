using CrossBuy.BL;
using CrossBuy.BL.Platform;
using CrossBuy.Models.Context.Accounting;
using CrossBuy.Models.Context.Admin;
using CrossBuy.Models.Context.Platform;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CrossBuy.Tests
{
    // ================================================================================================
    // PROJECT BILLING — BATCH 2: BUSINESS EVENTS AND APPROVAL ROUTING.
    //
    // Two claims, and they are the whole batch:
    //
    //   1. Every lifecycle transition becomes a canonical Business Event, written INSIDE the same
    //      transaction as the fact it reports. A rolled-back post tells nobody it posted.
    //   2. A Submitted billing becomes work in the EXISTING approval inbox — and therefore in the
    //      existing Workspace Attention card — for the people entitled to act on it, and for nobody
    //      else. No second inbox, no second attention system, no duplicated approval rule.
    //
    // Reversal is NOT here. It is blocked on a financial compensation gap: there is no receipt
    // reversal anywhere in the repository, and the retention and advance-recovery settlements are
    // receipts. See the batch report.
    // ================================================================================================
    public class ProjectsBillingEventsAndInboxTests
    {
        private const int CompanyOne = 1;
        private const int CompanyTwo = 2;
        private const int Preparer = 10;
        private const int Approver = 11;
        private const int Outsider = 12;

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

        // ---- an AR double that writes real rows, so a rollback is observable -----------------------
        private sealed class Ar : IReceivableService
        {
            private readonly CrossBuy.Models.Context.CrossDbContext _db;
            public bool FailOnReceipt { get; set; }
            public Ar(CrossBuy.Models.Context.CrossDbContext db) { _db = db; }

            public async Task<(bool ok, string? error, SalesInvoice? inv)> CreateSalesInvoiceAsync(
                int companyId, int customerId, DateTime date, List<SalesLineInput> lines, string? notes,
                int? userId, int? currencyId = null, decimal? exchangeRate = null, int? projectId = null)
            {
                var inv = new SalesInvoice { CompanyID = companyId, CustomerId = customerId, InvoiceDate = date, Status = "Posted", ProjectId = projectId };
                _db.SalesInvoices.Add(inv);
                await _db.SaveChangesAsync();
                return (true, null, inv);
            }

            public async Task<(bool ok, string? error)> CreateReceiptAsync(
                int companyId, int customerId, DateTime date, decimal amount, string method, int cashAccountId,
                string? notes, int? userId, int? currencyId = null, decimal? exchangeRate = null, int? projectId = null)
            {
                if (FailOnReceipt) return (false, "حساب غير مُهيّأ");
                _db.Receipts.Add(new Receipt { CompanyID = companyId, CustomerId = customerId, ReceiptDate = date, Amount = amount });
                await _db.SaveChangesAsync();
                return (true, null);
            }

            private static Exception No([System.Runtime.CompilerServices.CallerMemberName] string m = "")
                => new NotImplementedException($"Billing is not expected to call IReceivableService.{m}.");
            public Task<List<Customer>> GetCustomersAsync(int a) => throw No();
            public Task<(List<Customer> rows, int total)> SearchCustomersAsync(int a, string? b, bool? c, int d, int e) => throw No();
            public Task<List<(string value, string name)>> SuggestCustomersAsync(int a, string? b, int c) => throw No();
            public Task<Customer> CreateCustomerAsync(int a, string b, string? c, string? d, decimal? e) => throw No();
            public Task<(bool ok, string? error)> SaveCustomerAsync(int a, Customer b) => throw No();
            public Task<(bool ok, string? error, SalesInvoice? inv)> EditSalesInvoiceAsync(int a, int b, int c, DateTime d, List<SalesLineInput> e, string? f, int? g, int? h, decimal? i, int? j) => throw No();
            public Task<List<SalesInvoice>> GetInvoicesAsync(int a) => throw No();
            public Task<decimal> CustomerOutstandingAsync(int a, int b) => throw No();
            public Task<List<AgingRow>> AgingAsync(int a, DateTime b) => throw No();
            public Task<CustomerAnalytics> GetCustomerAnalyticsAsync(int a) => throw No();
            public Task<List<SalesReturn>> GetSalesReturnsAsync(int a) => throw No();
            public Task<SalesReturn?> GetSalesReturnAsync(int a, int b) => throw No();
            public Task<(bool ok, string? error, SalesReturn? ret)> CreateSalesReturnAsync(int a, int b, int? c, DateTime d, List<SalesLineInput> e, string? f, int? g, int? h, decimal? i) => throw No();
            public Task<(bool ok, string? error, SalesReturn? ret)> EditSalesReturnAsync(int a, int b, int c, int? d, DateTime e, List<SalesLineInput> f, string? g, int? h, int? i, decimal? j) => throw No();
        }

        private sealed record Fx(PlatformTestHost Host, ProgressBillingService Billing, Ar Ar, IProjectsAccessService Access);

        private static IProjectsAccessService AccessOf(PlatformTestHost host)
        {
            var http = new Microsoft.AspNetCore.Http.HttpContextAccessor();
            var accessor = new BusinessContextAccessor(new BusinessContextFactory(
                http, host.Db, host.Holder, NullLogger<BusinessContextFactory>.Instance));
            var accounting = new AccountingAccessService(
                host.Db, http, accessor,
                new BootstrapAccessPolicyReader(host.Db, NullLogger<BootstrapAccessPolicyReader>.Instance),
                NullLogger<AccountingAccessService>.Instance);
            return new ProjectsAccessService(
                host.Db, new PlatformRoleDirectory(host.Db, NullLogger<PlatformRoleDirectory>.Instance),
                accounting, NullLogger<ProjectsAccessService>.Instance);
        }

        private static async Task<Fx> SeedAsync(int actorForEvents = Preparer, int companyId = CompanyOne)
        {
            var host = new PlatformTestHost(companyId: companyId);
            var db = host.Seed;
            db.Employee.AddRange(Emp(Preparer, companyId), Emp(Approver, companyId), Emp(Outsider, companyId));
            db.Customers.Add(new Customer { ID = 1, CompanyID = companyId, Name = "c", ControlAccountId = 1 });
            db.Accounts.AddRange(
                new Account { ID = 1, CompanyID = companyId, Code = "4102", Name = "rev", IsPostable = true, IsActive = true },
                new Account { ID = 2, CompanyID = companyId, Code = "1104", Name = "ret", IsPostable = true, IsActive = true },
                new Account { ID = 3, CompanyID = companyId, Code = "2104", Name = "adv", IsPostable = true, IsActive = true });
            db.Projects.Add(new Project { ID = 5, CompanyID = companyId, Code = "P5", Name = "p5", NameEn = "p5", CustomerId = 1, RetentionPercent = 10m, AdvancePercent = 0m });
            db.BoqItems.Add(new BoqItem { ID = 1, CompanyID = companyId, ProjectId = 5, Code = "A", Description = "a", Unit = "m", Quantity = 100m, UnitPrice = 100m, SortOrder = 1 });
            db.ProjectProgresses.Add(new ProjectProgress { ID = 7, CompanyID = companyId, ProjectId = 5, MeasurementNo = 1, MeasurementDate = DateTime.Today, Status = "Confirmed" });
            db.ProjectProgressLines.Add(new ProjectProgressLine { ID = 1, ProgressId = 7, BoqItemId = 1, CumulativeQty = 50m });
            await db.SaveChangesAsync();

            var ar = new Ar(host.Db);
            var access = AccessOf(host);
            var events = new BusinessEventService(
                host.Db, host.Registry(), new StubContextAccessor(Ctx(actorForEvents, companyId)),
                NullLogger<BusinessEventService>.Instance);
            var billing = new ProgressBillingService(host.Db, ar, new ProgressService(host.Db), ContractOf(host), events, access);
            return new Fx(host, billing, ar, access);
        }

        private static IContractService ContractOf(PlatformTestHost host) => new ContractService(host.Db, new JeStub());

        private sealed class JeStub : IJournalEntryService
        {
            private static Exception No([System.Runtime.CompilerServices.CallerMemberName] string m = "")
                => new NotImplementedException($"Billing is not expected to reach IJournalEntryService.{m}.");
            public Task<(bool ok, string? error, JournalEntry? entry)> CreateDraftAsync(JournalEntryInput a, int? b) => throw No();
            public Task<(bool ok, string? error, JournalEntry? entry)> CreateAndPostAsync(JournalEntryInput a, int? b) => throw No();
            public Task<(bool ok, string? error, JournalEntry? entry)> CreateAndPostNoTxAsync(JournalEntryInput a, int? b) => throw No();
            public Task<(bool ok, string? error)> PostAsync(int a, int? b) => throw No();
            public Task<(bool ok, string? error, int? reversalId)> ReverseAsync(int a, int? b, string? c) => throw No();
        }

        private static async Task<int> DraftAsync(Fx f) =>
            (await f.Billing.SaveDraftAsync(CompanyOne, 5, 0, 7, DateTime.Today, 0m, null, Preparer)).id;

        private static Task<List<BusinessEvent>> EventsOf(Fx f) =>
            f.Host.Db.BusinessEvents.AsNoTracking().OrderBy(e => e.EventId).ToListAsync();

        private static async Task GrantAsync(PlatformTestHost host, int employeeId, string role, int companyId = CompanyOne)
        {
            host.Seed.PlatformRoleAssignments.Add(new PlatformRoleAssignment
            {
                CompanyID = companyId, Scope = EntityRegistry.ScopeProjects, PrincipalType = PlatformPrincipalTypes.Employee,
                PrincipalId = employeeId, Role = role, IsActive = true, CreatedAt = DateTime.UtcNow,
            });
            await host.Seed.SaveChangesAsync();
        }

        // ============================================================================================
        // L / M. EVENTS, AND THEIR TRANSACTIONAL HONESTY.
        // ============================================================================================

        [Fact]
        public async Task Each_lifecycle_transition_emits_exactly_one_event()
        {
            var f = await SeedAsync();
            using var host = f.Host;
            var id = await DraftAsync(f);

            Assert.True((await f.Billing.SubmitAsync(CompanyOne, id, Preparer)).ok);
            Assert.True((await f.Billing.ApproveAsync(CompanyOne, id, Approver)).ok);
            Assert.True((await f.Billing.PostAsync(CompanyOne, id, Approver)).ok);

            var types = (await EventsOf(f)).Select(e => e.EventType).ToList();
            Assert.Equal(
                new[] { "ProjectBilling.Submitted", "ProjectBilling.Approved", "ProjectBilling.Posted" },
                types);
        }

        [Fact]
        public async Task Returning_emits_its_own_event()
        {
            var f = await SeedAsync();
            using var host = f.Host;
            var id = await DraftAsync(f);
            await f.Billing.SubmitAsync(CompanyOne, id, Preparer);

            Assert.True((await f.Billing.ReturnAsync(CompanyOne, id, Approver)).ok);

            Assert.Contains("ProjectBilling.Returned", (await EventsOf(f)).Select(e => e.EventType));
        }

        [Fact]
        public async Task Saving_or_editing_a_draft_emits_nothing()
        {
            var f = await SeedAsync();
            using var host = f.Host;
            var id = await DraftAsync(f);
            await f.Billing.SaveDraftAsync(CompanyOne, 5, id, 7, DateTime.Today, 0m, "edited", Preparer);

            // An event is a business fact with a consumer. A draft being typed into is neither.
            Assert.Empty(await EventsOf(f));
        }

        [Fact]
        public async Task A_refused_transition_emits_no_event()
        {
            var f = await SeedAsync();
            using var host = f.Host;
            var id = await DraftAsync(f);

            // Draft cannot be approved, and the preparer cannot approve at all.
            Assert.False((await f.Billing.ApproveAsync(CompanyOne, id, Approver)).ok);
            await f.Billing.SubmitAsync(CompanyOne, id, Preparer);
            Assert.False((await f.Billing.ApproveAsync(CompanyOne, id, Preparer)).ok);

            var types = (await EventsOf(f)).Select(e => e.EventType).ToList();
            Assert.Equal(new[] { "ProjectBilling.Submitted" }, types);
            Assert.DoesNotContain("ProjectBilling.Approved", types);
        }

        [Fact]
        public async Task A_failed_post_emits_no_posted_event_because_the_event_rolls_back_with_it()
        {
            var f = await SeedAsync();
            using var host = f.Host;
            var id = await DraftAsync(f);
            await f.Billing.SubmitAsync(CompanyOne, id, Preparer);
            await f.Billing.ApproveAsync(CompanyOne, id, Approver);

            f.Ar.FailOnReceipt = true;
            Assert.False((await f.Billing.PostAsync(CompanyOne, id, Approver)).ok);

            // THE POINT OF ADR-001. The event is written inside the financial transaction, so the same
            // rollback that removed the invoice removed the claim that this billing posted.
            var types = (await EventsOf(f)).Select(e => e.EventType).ToList();
            Assert.DoesNotContain("ProjectBilling.Posted", types);
            Assert.Empty(await host.Db.SalesInvoices.ToListAsync());
        }

        [Fact]
        public async Task A_retry_after_a_failed_post_still_produces_exactly_one_posted_event()
        {
            var f = await SeedAsync();
            using var host = f.Host;
            var id = await DraftAsync(f);
            await f.Billing.SubmitAsync(CompanyOne, id, Preparer);
            await f.Billing.ApproveAsync(CompanyOne, id, Approver);

            f.Ar.FailOnReceipt = true;
            Assert.False((await f.Billing.PostAsync(CompanyOne, id, Approver)).ok);
            f.Ar.FailOnReceipt = false;
            Assert.True((await f.Billing.PostAsync(CompanyOne, id, Approver)).ok);

            Assert.Single((await EventsOf(f)).Where(e => e.EventType == "ProjectBilling.Posted"));
            Assert.Single(await host.Db.SalesInvoices.ToListAsync());
        }

        [Fact]
        public async Task A_repeated_transition_does_not_write_a_second_event()
        {
            var f = await SeedAsync();
            using var host = f.Host;
            var id = await DraftAsync(f);
            await f.Billing.SubmitAsync(CompanyOne, id, Preparer);
            Assert.True((await f.Billing.ApproveAsync(CompanyOne, id, Approver)).ok);

            // The state machine refuses the second approve, and the dedup key would refuse the second
            // event even if it did not. Belt and braces, on purpose: the permanent log is not a place
            // to discover that two mechanisms disagreed.
            Assert.False((await f.Billing.ApproveAsync(CompanyOne, id, Approver)).ok);

            Assert.Single((await EventsOf(f)).Where(e => e.EventType == "ProjectBilling.Approved"));
        }

        [Fact]
        public async Task The_event_payload_carries_identifiers_and_no_documents()
        {
            var f = await SeedAsync();
            using var host = f.Host;
            var id = await DraftAsync(f);
            await f.Billing.SubmitAsync(CompanyOne, id, Preparer);

            var ev = Assert.Single(await EventsOf(f));
            Assert.Equal(EntityRegistry.ProjectBilling, ev.EntityType);
            Assert.Equal(id, ev.EntityId);
            Assert.Equal(CompanyOne, ev.CompanyID);
            Assert.Equal(ProgressBillingEventPayload.Version, ev.PayloadVersion);

            var payload = ev.Payload ?? "";
            Assert.Contains("\"billingId\"", payload);
            Assert.Contains("\"projectId\"", payload);
            // Nothing resembling a file, a session or a serialized entity graph.
            // No entity graph, no line collection, no path-looking value.
            Assert.DoesNotContain("lines", payload);
            Assert.DoesNotContain(":\\\\", payload);
        }

        // ============================================================================================
        // O / P / Q. APPROVAL INBOX AND ATTENTION ENTITLEMENT.
        // ============================================================================================

        [Fact]
        public async Task A_submitted_billing_reaches_an_eligible_approver()
        {
            var f = await SeedAsync();
            using var host = f.Host;
            await GrantAsync(host, Approver, ProjectsRoles.ProjectsAdministrator);
            var id = await DraftAsync(f);
            await f.Billing.SubmitAsync(CompanyOne, id, Preparer);

            var rows = await f.Billing.PendingApprovalsForAsync(Ctx(Approver, CompanyOne));

            var row = Assert.Single(rows);
            Assert.Equal(id, row.BillingId);
            Assert.Equal(ProgressBillingStatuses.Submitted, row.Status);
            Assert.Equal(Preparer, row.PreparedBy);
        }

        [Fact]
        public async Task The_preparer_never_sees_their_own_billing_as_work_to_approve()
        {
            var f = await SeedAsync();
            using var host = f.Host;
            await GrantAsync(host, Preparer, ProjectsRoles.ProjectsAdministrator);
            var id = await DraftAsync(f);
            await f.Billing.SubmitAsync(CompanyOne, id, Preparer);

            // Not "shown and then refused" - it is not their work to do, so it is not in their inbox.
            Assert.Empty(await f.Billing.PendingApprovalsForAsync(Ctx(Preparer, CompanyOne)));
        }

        [Fact]
        public async Task A_user_without_approval_authority_sees_nothing()
        {
            var f = await SeedAsync();
            using var host = f.Host;
            // Someone ELSE holds a Projects role, which closes bootstrap for this company; the outsider
            // holds none, so the access service refuses them.
            await GrantAsync(host, Approver, ProjectsRoles.ProjectsAdministrator);
            var id = await DraftAsync(f);
            await f.Billing.SubmitAsync(CompanyOne, id, Preparer);

            Assert.Empty(await f.Billing.PendingApprovalsForAsync(Ctx(Outsider, CompanyOne)));
        }

        [Fact]
        public async Task An_approved_billing_becomes_posting_work_for_a_poster()
        {
            var f = await SeedAsync();
            using var host = f.Host;
            await GrantAsync(host, Approver, ProjectsRoles.ProjectsAdministrator);
            // A REAL accounting role is required, not merely an unconfigured module: accounting "post" is
            // on the NeverBootstrapOpen list, so bootstrap never confers it. My first version of this test
            // assumed bootstrap would be enough and correctly failed.
            host.Seed.AccountingUserRoles.Add(new AccountingUserRole { CompanyID = CompanyOne, EmployeeId = Approver, Role = "Accountant" });
            await host.Seed.SaveChangesAsync();

            var id = await DraftAsync(f);
            await f.Billing.SubmitAsync(CompanyOne, id, Preparer);
            await f.Billing.ApproveAsync(CompanyOne, id, Approver);

            var rows = await f.Billing.PendingApprovalsForAsync(Ctx(Approver, CompanyOne));
            var row = Assert.Single(rows);
            Assert.Equal(ProgressBillingStatuses.Approved, row.Status);
        }

        [Fact]
        public async Task Posting_work_is_withheld_from_someone_who_lacks_the_accounting_right()
        {
            var f = await SeedAsync();
            using var host = f.Host;
            await GrantAsync(host, Approver, ProjectsRoles.ProjectsAdministrator);
            // Close accounting's bootstrap for SOMEBODY ELSE, so the approver holds no accounting role.
            host.Seed.AccountingUserRoles.Add(new AccountingUserRole { CompanyID = CompanyOne, EmployeeId = 999, Role = "ChiefAccountant" });
            await host.Seed.SaveChangesAsync();

            var id = await DraftAsync(f);
            await f.Billing.SubmitAsync(CompanyOne, id, Preparer);
            await f.Billing.ApproveAsync(CompanyOne, id, Approver);

            // They could approve it; they may not post it, so it is not posting work for them.
            Assert.Empty(await f.Billing.PendingApprovalsForAsync(Ctx(Approver, CompanyOne)));
        }

        [Fact]
        public async Task A_posted_billing_is_nobodys_work()
        {
            var f = await SeedAsync();
            using var host = f.Host;
            await GrantAsync(host, Approver, ProjectsRoles.ProjectsAdministrator);
            var id = await DraftAsync(f);
            await f.Billing.SubmitAsync(CompanyOne, id, Preparer);
            await f.Billing.ApproveAsync(CompanyOne, id, Approver);
            await f.Billing.PostAsync(CompanyOne, id, Approver);

            Assert.Empty(await f.Billing.PendingApprovalsForAsync(Ctx(Approver, CompanyOne)));
        }

        [Fact]
        public async Task A_billing_never_reaches_an_approver_in_another_company()
        {
            var f = await SeedAsync();
            using var host = f.Host;
            await GrantAsync(host, Approver, ProjectsRoles.ProjectsAdministrator);
            var id = await DraftAsync(f);
            await f.Billing.SubmitAsync(CompanyOne, id, Preparer);

            // Same employee id, same billing id, different company on the context.
            Assert.Empty(await f.Billing.PendingApprovalsForAsync(Ctx(Approver, CompanyTwo)));
        }

        [Fact]
        public async Task An_unresolved_identity_sees_nothing()
        {
            var f = await SeedAsync();
            using var host = f.Host;
            var id = await DraftAsync(f);
            await f.Billing.SubmitAsync(CompanyOne, id, Preparer);

            var noEmployee = new BusinessContext
            { CompanyId = CompanyOne, EmployeeId = null, UserId = "u", Roles = Array.Empty<string>(), CorrelationId = Guid.NewGuid() };

            Assert.Empty(await f.Billing.PendingApprovalsForAsync(noEmployee));
        }

        [Fact]
        public void The_inbox_holds_no_billing_rule_of_its_own()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "CrossBuy.sln"))) dir = dir.Parent;
            var raw = File.ReadAllText(Path.Combine(dir!.FullName, "CrossBuy", "BL", "Approvals", "ApprovalInboxService.cs"));
            // Comments stripped: this file EXPLAINS in prose that it holds no context and no rule, and
            // the assertion is about the code, not about the explanation.
            var inbox = string.Join("\n", raw
                .Replace("\r\n", "\n")
                .Split('\n')
                .Where(l => !l.TrimStart().StartsWith("//")));

            // It maps what the module returned. It must not re-derive entitlement, or the two copies
            // will drift and the screen will offer an action the service then refuses.
            Assert.Contains("PendingApprovalsForAsync", inbox);
            Assert.DoesNotContain("CreatedBy", inbox);
            Assert.DoesNotContain("ProjectsActions.", inbox);
            Assert.DoesNotContain("CrossDbContext", inbox);
        }
    }
}
