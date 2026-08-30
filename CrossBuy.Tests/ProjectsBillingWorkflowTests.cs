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
    // PROJECT BILLING — BATCH 1: ATOMIC POSTING, ACTOR EVIDENCE, SEPARATION OF DUTIES.
    //
    // The three P0 defects these tests hold closed:
    //
    //   P0-1  PostAsync created a sales invoice and up to two settlement receipts through
    //         ReceivableService, each of which opened its OWN transaction. With no ambient transaction
    //         the three committed separately, so a failure after the invoice left the invoice IN THE
    //         LEDGER with the billing still 'Approved' - and the retry posted a SECOND invoice.
    //   P0-2  Every controller call site passed a null actor, so CreatedBy and PostedBy were always
    //         null and no billing carried any evidence of who did anything.
    //   P0-3  Save, Approve and Post all gated on one right and nothing compared the approver to the
    //         preparer, so one billing user completed the whole lifecycle alone.
    //
    // The AR collaborator here is a double that writes REAL ROWS through the same DbContext. That is
    // deliberate: the claim under test is whether ProgressBillingService's transaction rolls back what
    // its collaborators wrote, and only a collaborator that actually writes can demonstrate that. A
    // recording stub would prove the call was made and nothing about atomicity.
    // ================================================================================================
    public class ProjectsBillingWorkflowTests
    {
        private const int CompanyOne = 1;
        private const int CompanyTwo = 2;
        private const int Preparer = 10;
        private const int Approver = 11;

        // ---- an AR double that really writes, and can be told to fail at a chosen step ------------
        private sealed class WritingArDouble : IReceivableService
        {
            private readonly CrossBuy.Models.Context.CrossDbContext _db;
            public bool FailOnReceipt { get; set; }
            public int InvoiceCalls { get; private set; }
            public int ReceiptCalls { get; private set; }

            public WritingArDouble(CrossBuy.Models.Context.CrossDbContext db) { _db = db; }

            public async Task<(bool ok, string? error, SalesInvoice? inv)> CreateSalesInvoiceAsync(
                int companyId, int customerId, DateTime date, List<SalesLineInput> lines, string? notes,
                int? userId, int? currencyId = null, decimal? exchangeRate = null, int? projectId = null)
            {
                InvoiceCalls++;
                var inv = new SalesInvoice
                {
                    CompanyID = companyId, CustomerId = customerId, InvoiceDate = date,
                    Status = "Posted", ProjectId = projectId,
                };
                _db.SalesInvoices.Add(inv);
                await _db.SaveChangesAsync();          // a real commit-intent inside the caller's transaction
                return (true, null, inv);
            }

            public async Task<(bool ok, string? error)> CreateReceiptAsync(
                int companyId, int customerId, DateTime date, decimal amount, string method, int cashAccountId,
                string? notes, int? userId, int? currencyId = null, decimal? exchangeRate = null, int? projectId = null)
            {
                ReceiptCalls++;
                // Models the real failure this defect was found through: the settlement account is not
                // configured, so the receipt cannot be written AFTER the invoice already has been.
                if (FailOnReceipt) return (false, "حساب غير مُهيّأ");

                _db.Receipts.Add(new Receipt
                {
                    CompanyID = companyId, CustomerId = customerId, ReceiptDate = date,
                    Amount = amount,
                });
                await _db.SaveChangesAsync();
                return (true, null);
            }

            // Nothing else on the interface is reachable from PostAsync; loud rather than silent.
            private static Exception No([System.Runtime.CompilerServices.CallerMemberName] string m = "")
                => new NotImplementedException($"ProgressBillingService.PostAsync is not expected to call {m}.");
            public Task<List<Customer>> GetCustomersAsync(int companyId) => throw No();
            public Task<(List<Customer> rows, int total)> SearchCustomersAsync(int companyId, string? q, bool? active, int page, int pageSize) => throw No();
            public Task<List<(string value, string name)>> SuggestCustomersAsync(int companyId, string? term, int take) => throw No();
            public Task<Customer> CreateCustomerAsync(int companyId, string name, string? nameEn, string? taxNo, decimal? creditLimit) => throw No();
            public Task<(bool ok, string? error)> SaveCustomerAsync(int companyId, Customer dto) => throw No();
            public Task<(bool ok, string? error, SalesInvoice? inv)> EditSalesInvoiceAsync(int companyId, int invoiceId, int customerId, DateTime date, List<SalesLineInput> lines, string? notes, int? userId, int? currencyId, decimal? exchangeRate, int? projectId) => throw No();
            public Task<List<SalesInvoice>> GetInvoicesAsync(int companyId) => throw No();
            public Task<decimal> CustomerOutstandingAsync(int companyId, int customerId) => throw No();
            public Task<List<AgingRow>> AgingAsync(int companyId, DateTime asOf) => throw No();
            public Task<CustomerAnalytics> GetCustomerAnalyticsAsync(int companyId) => throw No();
            public Task<List<SalesReturn>> GetSalesReturnsAsync(int companyId) => throw No();
            public Task<SalesReturn?> GetSalesReturnAsync(int companyId, int id) => throw No();
            public Task<(bool ok, string? error, SalesReturn? ret)> CreateSalesReturnAsync(int companyId, int customerId, int? originalInvoiceId, DateTime date, List<SalesLineInput> lines, string? notes, int? userId, int? currencyId, decimal? exchangeRate) => throw No();
            public Task<(bool ok, string? error, SalesReturn? ret)> EditSalesReturnAsync(int companyId, int returnId, int customerId, int? originalInvoiceId, DateTime date, List<SalesLineInput> lines, string? notes, int? userId, int? currencyId, decimal? exchangeRate) => throw No();
        }

        // ---- fixtures ------------------------------------------------------------------------------

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

        private sealed record Fixture(
            PlatformTestHost Host, ProgressBillingService Billing, WritingArDouble Ar, int ProjectId, int ProgressId);

        // A project with a Confirmed measurement, a BOQ line and a customer - the minimum that makes a
        // billing postable. Retention is on, because the retention receipt is the step that fails.
        private static async Task<Fixture> SeedAsync(bool retention = true, int companyId = CompanyOne)
        {
            var host = new PlatformTestHost(companyId: companyId);
            var db = host.Seed;

            db.Employee.AddRange(Emp(Preparer, companyId), Emp(Approver, companyId));
            db.Customers.Add(new Customer { ID = 1, CompanyID = companyId, Name = "c", ControlAccountId = 1 });
            db.Accounts.AddRange(
                new Account { ID = 1, CompanyID = companyId, Code = "4102", Name = "rev", IsPostable = true, IsActive = true },
                new Account { ID = 2, CompanyID = companyId, Code = "1104", Name = "ret", IsPostable = true, IsActive = true },
                new Account { ID = 3, CompanyID = companyId, Code = "2104", Name = "adv", IsPostable = true, IsActive = true });
            db.Projects.Add(new Project
            {
                ID = 5, CompanyID = companyId, Code = "P5", Name = "p5", NameEn = "p5",
                CustomerId = 1, RetentionPercent = retention ? 10m : 0m, AdvancePercent = 0m,
            });
            db.BoqItems.Add(new BoqItem { ID = 1, CompanyID = companyId, ProjectId = 5, Code = "A", Description = "a", Unit = "m", Quantity = 100m, UnitPrice = 100m, SortOrder = 1 });
            db.ProjectProgresses.Add(new ProjectProgress { ID = 7, CompanyID = companyId, ProjectId = 5, MeasurementNo = 1, MeasurementDate = DateTime.Today, Status = "Confirmed" });
            db.ProjectProgressLines.Add(new ProjectProgressLine { ID = 1, ProgressId = 7, BoqItemId = 1, CumulativeQty = 50m });
            await db.SaveChangesAsync();

            var ar = new WritingArDouble(host.Db);
            var billing = new ProgressBillingService(host.Db, ar, Progress(host), Contract(host), Events(host), Access(host));
            return new Fixture(host, billing, ar, 5, 7);
        }

        // The REAL event service, not a spy: the claim under test includes that events land inside the
        // caller's transaction and disappear with a rollback, and only the real one enforces that.
        private static CrossBuy.BL.Platform.IBusinessEventService Events(PlatformTestHost host)
            => new CrossBuy.BL.Platform.BusinessEventService(
                host.Db, host.Registry(), new StubContextAccessor(Ctx(Preparer, CompanyOne)),
                NullLogger<CrossBuy.BL.Platform.BusinessEventService>.Instance);

        // The REAL access service too, so inbox entitlement is decided by the same policy the
        // controller asks - a permissive stub would make the authorization tests meaningless.
        private static IProjectsAccessService Access(PlatformTestHost host)
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

        private static IProgressService Progress(PlatformTestHost host)
            => new ProgressService(host.Db);

        private static IContractService Contract(PlatformTestHost host)
            => new ContractService(host.Db, new JournalEntryServiceStub());

        // JournalEntryService is only reached by ContractService's own posting paths, which billing does
        // not use; billing reads the advance BALANCE, which is a query.
        private sealed class JournalEntryServiceStub : IJournalEntryService
        {
            private static Exception No([System.Runtime.CompilerServices.CallerMemberName] string m = "")
                => new NotImplementedException($"Billing is not expected to reach IJournalEntryService.{m}.");
            public Task<(bool ok, string? error, JournalEntry? entry)> CreateDraftAsync(JournalEntryInput input, int? userId) => throw No();
            public Task<(bool ok, string? error)> PostAsync(int entryId, int? userId) => throw No();
            public Task<(bool ok, string? error, JournalEntry? entry)> CreateAndPostAsync(JournalEntryInput input, int? userId) => throw No();
            public Task<(bool ok, string? error, JournalEntry? entry)> CreateAndPostNoTxAsync(JournalEntryInput input, int? userId) => throw No();
            public Task<(bool ok, string? error, int? reversalId)> ReverseAsync(int entryId, int? userId, string? reason) => throw No();
        }

        private static async Task<int> DraftAsync(Fixture f, int actor = Preparer)
        {
            var (ok, err, id) = await f.Billing.SaveDraftAsync(
                f.Host.Db.Projects.First().CompanyID, f.ProjectId, 0, f.ProgressId, DateTime.Today, 0m, null, actor);
            Assert.True(ok, err);
            return id;
        }

        private static async Task<int> ApprovedAsync(Fixture f)
        {
            var id = await DraftAsync(f);
            Assert.True((await f.Billing.SubmitAsync(CompanyOne, id, Preparer)).ok);
            var (ok, err) = await f.Billing.ApproveAsync(CompanyOne, id, Approver);
            Assert.True(ok, err);
            return id;
        }

        // ============================================================================================
        // P0-1 / D-G. ATOMIC POSTING.
        // ============================================================================================

        [Fact]
        public async Task BeginOrJoin_joins_an_ambient_transaction_instead_of_opening_a_second_one()
        {
            using var host = new PlatformTestHost(companyId: CompanyOne);

            await using var outer = await ScopedTx.BeginOrJoinAsync(host.Db);
            Assert.True(outer.Owns);                    // nothing ambient yet, so this one owns it

            await using var inner = await ScopedTx.BeginOrJoinAsync(host.Db);
            Assert.False(inner.Owns);                   // JOINS - this is what makes the post one unit
        }

        [Fact]
        public async Task A_failure_after_the_invoice_rolls_the_invoice_back()
        {
            var f = await SeedAsync();
            using var host = f.Host;
            var id = await ApprovedAsync(f);

            f.Ar.FailOnReceipt = true;
            var (ok, err) = await f.Billing.PostAsync(CompanyOne, id, Approver);

            Assert.False(ok);
            Assert.NotNull(err);
            Assert.Equal(1, f.Ar.InvoiceCalls);                 // the invoice WAS attempted...
            Assert.Empty(await host.Db.SalesInvoices.ToListAsync());   // ...and did not survive
        }

        [Fact]
        public async Task A_failure_after_the_invoice_leaves_no_retention_receipt()
        {
            var f = await SeedAsync();
            using var host = f.Host;
            var id = await ApprovedAsync(f);

            f.Ar.FailOnReceipt = true;
            await f.Billing.PostAsync(CompanyOne, id, Approver);

            Assert.Empty(await host.Db.Receipts.ToListAsync());
        }

        [Fact]
        public async Task A_failure_after_the_invoice_leaves_the_billing_unposted()
        {
            var f = await SeedAsync();
            using var host = f.Host;
            var id = await ApprovedAsync(f);

            f.Ar.FailOnReceipt = true;
            await f.Billing.PostAsync(CompanyOne, id, Approver);

            var after = await host.Db.ProgressBillings.AsNoTracking().FirstAsync(b => b.ID == id);
            Assert.Equal(ProgressBillingStatuses.Approved, after.Status);
            Assert.Null(after.SalesInvoiceId);
            Assert.Null(after.PostedBy);
            Assert.Null(after.PostedAt);
        }

        [Fact]
        public async Task A_retry_after_a_failed_post_produces_exactly_one_invoice()
        {
            var f = await SeedAsync();
            using var host = f.Host;
            var id = await ApprovedAsync(f);

            // THE REGRESSION. Before this batch the first attempt left a committed invoice behind and
            // the retry wrote a second one for the same work.
            f.Ar.FailOnReceipt = true;
            Assert.False((await f.Billing.PostAsync(CompanyOne, id, Approver)).ok);

            f.Ar.FailOnReceipt = false;
            var (ok, err) = await f.Billing.PostAsync(CompanyOne, id, Approver);

            Assert.True(ok, err);
            Assert.Single(await host.Db.SalesInvoices.ToListAsync());
            Assert.Single(await host.Db.Receipts.ToListAsync());
        }

        [Fact]
        public async Task A_second_post_of_the_same_billing_creates_no_second_financial_effect()
        {
            var f = await SeedAsync();
            using var host = f.Host;
            var id = await ApprovedAsync(f);

            Assert.True((await f.Billing.PostAsync(CompanyOne, id, Approver)).ok);
            var (second, _) = await f.Billing.PostAsync(CompanyOne, id, Approver);

            Assert.False(second);
            Assert.Single(await host.Db.SalesInvoices.ToListAsync());
            Assert.Single(await host.Db.Receipts.ToListAsync());
        }

        [Fact]
        public async Task A_successful_post_records_the_invoice_and_the_poster()
        {
            var f = await SeedAsync();
            using var host = f.Host;
            var id = await ApprovedAsync(f);

            Assert.True((await f.Billing.PostAsync(CompanyOne, id, Approver)).ok);

            var after = await host.Db.ProgressBillings.AsNoTracking().FirstAsync(b => b.ID == id);
            Assert.Equal(ProgressBillingStatuses.Posted, after.Status);
            Assert.NotNull(after.SalesInvoiceId);
            Assert.Equal(Approver, after.PostedBy);
            Assert.NotNull(after.PostedAt);
        }

        // ============================================================================================
        // P0-2. ACTOR EVIDENCE.
        // ============================================================================================

        [Fact]
        public async Task Every_lifecycle_step_records_who_did_it()
        {
            var f = await SeedAsync();
            using var host = f.Host;

            var id = await DraftAsync(f);
            Assert.True((await f.Billing.SubmitAsync(CompanyOne, id, Preparer)).ok);
            Assert.True((await f.Billing.ApproveAsync(CompanyOne, id, Approver)).ok);
            Assert.True((await f.Billing.PostAsync(CompanyOne, id, Approver)).ok);

            var b = await host.Db.ProgressBillings.AsNoTracking().FirstAsync(x => x.ID == id);
            Assert.Equal(Preparer, b.CreatedBy);
            Assert.Equal(Preparer, b.SubmittedBy);
            Assert.Equal(Approver, b.ApprovedBy);
            Assert.Equal(Approver, b.PostedBy);
            Assert.NotNull(b.CreatedAt);
            Assert.NotNull(b.SubmittedAt);
            Assert.NotNull(b.ApprovedAt);
            Assert.NotNull(b.PostedAt);
        }

        [Fact]
        public async Task Editing_a_draft_preserves_the_original_preparer()
        {
            var f = await SeedAsync();
            using var host = f.Host;
            var id = await DraftAsync(f);

            // A DIFFERENT person edits the draft. If this overwrote CreatedBy, the preparer could make
            // themselves eligible to approve their own billing by getting somebody else to touch it -
            // or, worse, by touching it themselves under a second account.
            var (ok, err, _) = await f.Billing.SaveDraftAsync(CompanyOne, f.ProjectId, id, f.ProgressId, DateTime.Today, 0m, "edited", Approver);
            Assert.True(ok, err);

            var b = await host.Db.ProgressBillings.AsNoTracking().FirstAsync(x => x.ID == id);
            Assert.Equal(Preparer, b.CreatedBy);        // unchanged
            Assert.Equal(Approver, b.UpdatedBy);        // the editor is recorded separately
            Assert.NotNull(b.UpdatedAt);
        }

        // ============================================================================================
        // P0-3. SEPARATION OF DUTIES.
        // ============================================================================================

        [Fact]
        public async Task The_preparer_cannot_approve_their_own_billing()
        {
            var f = await SeedAsync();
            using var host = f.Host;
            var id = await DraftAsync(f);
            Assert.True((await f.Billing.SubmitAsync(CompanyOne, id, Preparer)).ok);

            var (ok, err) = await f.Billing.ApproveAsync(CompanyOne, id, Preparer);

            Assert.False(ok);
            Assert.Equal(ProgressBillingService.SelfApprovalRefused, err);

            var b = await host.Db.ProgressBillings.AsNoTracking().FirstAsync(x => x.ID == id);
            Assert.Equal(ProgressBillingStatuses.Submitted, b.Status);   // unchanged
            Assert.Null(b.ApprovedBy);
        }

        [Fact]
        public async Task A_different_authorized_actor_can_approve()
        {
            var f = await SeedAsync();
            using var host = f.Host;
            var id = await DraftAsync(f);
            Assert.True((await f.Billing.SubmitAsync(CompanyOne, id, Preparer)).ok);

            var (ok, err) = await f.Billing.ApproveAsync(CompanyOne, id, Approver);

            Assert.True(ok, err);
            var b = await host.Db.ProgressBillings.AsNoTracking().FirstAsync(x => x.ID == id);
            Assert.Equal(ProgressBillingStatuses.Approved, b.Status);
            Assert.Equal(Approver, b.ApprovedBy);
            Assert.NotEqual(b.CreatedBy, b.ApprovedBy);
        }

        [Fact]
        public async Task The_approver_may_also_post_because_the_required_split_is_prepare_versus_approve()
        {
            var f = await SeedAsync();
            using var host = f.Host;
            var id = await ApprovedAsync(f);

            // Owner policy, deliberately: PREPARE != APPROVE. Approver and poster MAY be the same person
            // when that actor independently holds the posting authority.
            var (ok, err) = await f.Billing.PostAsync(CompanyOne, id, Approver);

            Assert.True(ok, err);
            var b = await host.Db.ProgressBillings.AsNoTracking().FirstAsync(x => x.ID == id);
            Assert.Equal(b.ApprovedBy, b.PostedBy);
        }

        [Fact]
        public async Task A_billing_with_no_recorded_preparer_cannot_be_approved()
        {
            var f = await SeedAsync();
            using var host = f.Host;
            var id = await DraftAsync(f);
            Assert.True((await f.Billing.SubmitAsync(CompanyOne, id, Preparer)).ok);

            // A legacy row from before this batch has CreatedBy = null. An unknown preparer cannot be
            // shown to be someone other than this approver, so it is refused rather than waved through.
            var row = await host.Db.ProgressBillings.FirstAsync(x => x.ID == id);
            row.CreatedBy = null;
            await host.Db.SaveChangesAsync();

            Assert.False((await f.Billing.ApproveAsync(CompanyOne, id, Approver)).ok);
        }

        // ============================================================================================
        // K / L. THE STATE MACHINE, ENFORCED IN THE SERVICE.
        // ============================================================================================

        [Fact]
        public async Task The_happy_path_is_draft_submit_approve_post()
        {
            var f = await SeedAsync();
            using var host = f.Host;
            var id = await DraftAsync(f);

            Assert.Equal(ProgressBillingStatuses.Draft, (await host.Db.ProgressBillings.AsNoTracking().FirstAsync(x => x.ID == id)).Status);
            Assert.True((await f.Billing.SubmitAsync(CompanyOne, id, Preparer)).ok);
            Assert.Equal(ProgressBillingStatuses.Submitted, (await host.Db.ProgressBillings.AsNoTracking().FirstAsync(x => x.ID == id)).Status);
            Assert.True((await f.Billing.ApproveAsync(CompanyOne, id, Approver)).ok);
            Assert.Equal(ProgressBillingStatuses.Approved, (await host.Db.ProgressBillings.AsNoTracking().FirstAsync(x => x.ID == id)).Status);
            Assert.True((await f.Billing.PostAsync(CompanyOne, id, Approver)).ok);
            Assert.Equal(ProgressBillingStatuses.Posted, (await host.Db.ProgressBillings.AsNoTracking().FirstAsync(x => x.ID == id)).Status);
        }

        [Fact]
        public async Task A_submitted_billing_can_be_returned_and_then_resubmitted()
        {
            var f = await SeedAsync();
            using var host = f.Host;
            var id = await DraftAsync(f);
            Assert.True((await f.Billing.SubmitAsync(CompanyOne, id, Preparer)).ok);

            Assert.True((await f.Billing.ReturnAsync(CompanyOne, id, Approver)).ok);
            var returned = await host.Db.ProgressBillings.AsNoTracking().FirstAsync(x => x.ID == id);
            Assert.Equal(ProgressBillingStatuses.Returned, returned.Status);
            Assert.Equal(Approver, returned.UpdatedBy);

            // Returned is editable, and editing puts it back to Draft so the next move is Submit again.
            var (ok, err, _) = await f.Billing.SaveDraftAsync(CompanyOne, f.ProjectId, id, f.ProgressId, DateTime.Today, 0m, "fixed", Preparer);
            Assert.True(ok, err);
            Assert.Equal(ProgressBillingStatuses.Draft, (await host.Db.ProgressBillings.AsNoTracking().FirstAsync(x => x.ID == id)).Status);
            Assert.True((await f.Billing.SubmitAsync(CompanyOne, id, Preparer)).ok);
        }

        [Fact]
        public async Task A_draft_cannot_be_approved_or_posted()
        {
            var f = await SeedAsync();
            using var host = f.Host;
            var id = await DraftAsync(f);

            Assert.False((await f.Billing.ApproveAsync(CompanyOne, id, Approver)).ok);
            Assert.False((await f.Billing.PostAsync(CompanyOne, id, Approver)).ok);
            Assert.Empty(await host.Db.SalesInvoices.ToListAsync());
        }

        [Fact]
        public async Task A_submitted_billing_cannot_be_posted_without_approval()
        {
            var f = await SeedAsync();
            using var host = f.Host;
            var id = await DraftAsync(f);
            Assert.True((await f.Billing.SubmitAsync(CompanyOne, id, Preparer)).ok);

            Assert.False((await f.Billing.PostAsync(CompanyOne, id, Approver)).ok);
            Assert.Empty(await host.Db.SalesInvoices.ToListAsync());
        }

        [Fact]
        public async Task An_approved_billing_cannot_be_edited()
        {
            var f = await SeedAsync();
            using var host = f.Host;
            var id = await ApprovedAsync(f);

            var (ok, _, _) = await f.Billing.SaveDraftAsync(CompanyOne, f.ProjectId, id, f.ProgressId, DateTime.Today, 5m, "sneaky", Preparer);
            Assert.False(ok);
        }

        [Fact]
        public async Task A_posted_billing_cannot_be_edited_or_deleted()
        {
            var f = await SeedAsync();
            using var host = f.Host;
            var id = await ApprovedAsync(f);
            Assert.True((await f.Billing.PostAsync(CompanyOne, id, Approver)).ok);

            Assert.False((await f.Billing.SaveDraftAsync(CompanyOne, f.ProjectId, id, f.ProgressId, DateTime.Today, 5m, "sneaky", Preparer)).ok);
            Assert.False((await f.Billing.DeleteAsync(CompanyOne, id)).ok);
            Assert.False((await f.Billing.SubmitAsync(CompanyOne, id, Preparer)).ok);
            Assert.False((await f.Billing.ApproveAsync(CompanyOne, id, Approver)).ok);
        }

        [Fact]
        public void The_legal_transition_table_is_the_state_machine()
        {
            // Asserted directly so the rule is readable in one place rather than inferred from services.
            Assert.True(ProgressBillingStatuses.CanMove(ProgressBillingStatuses.Draft, ProgressBillingStatuses.Submitted));
            Assert.True(ProgressBillingStatuses.CanMove(ProgressBillingStatuses.Submitted, ProgressBillingStatuses.Approved));
            Assert.True(ProgressBillingStatuses.CanMove(ProgressBillingStatuses.Submitted, ProgressBillingStatuses.Returned));
            Assert.True(ProgressBillingStatuses.CanMove(ProgressBillingStatuses.Returned, ProgressBillingStatuses.Submitted));
            Assert.True(ProgressBillingStatuses.CanMove(ProgressBillingStatuses.Approved, ProgressBillingStatuses.Posted));

            Assert.False(ProgressBillingStatuses.CanMove(ProgressBillingStatuses.Draft, ProgressBillingStatuses.Approved));
            Assert.False(ProgressBillingStatuses.CanMove(ProgressBillingStatuses.Draft, ProgressBillingStatuses.Posted));
            Assert.False(ProgressBillingStatuses.CanMove(ProgressBillingStatuses.Submitted, ProgressBillingStatuses.Posted));
            Assert.False(ProgressBillingStatuses.CanMove(ProgressBillingStatuses.Posted, ProgressBillingStatuses.Approved));

            // Posted is terminal in this batch. Reversal is Product Batch 2, and the empty array is where
            // that transition will be added rather than a second mechanism beside it.
            Assert.Empty(ProgressBillingStatuses.LegalNext[ProgressBillingStatuses.Posted]);
        }

        // ============================================================================================
        // Q. COMPANY ISOLATION ACROSS THE NEW TRANSITIONS.
        // ============================================================================================

        [Theory]
        [InlineData("submit")]
        [InlineData("approve")]
        [InlineData("return")]
        [InlineData("post")]
        public async Task No_transition_can_be_driven_from_another_company(string step)
        {
            var f = await SeedAsync();
            using var host = f.Host;
            var id = await DraftAsync(f);
            await f.Billing.SubmitAsync(CompanyOne, id, Preparer);

            // Every service method takes the company from its caller's resolved context and filters on it,
            // so a company-2 caller naming a company-1 billing finds nothing at all.
            var result = step switch
            {
                "submit" => await f.Billing.SubmitAsync(CompanyTwo, id, Preparer),
                "approve" => await f.Billing.ApproveAsync(CompanyTwo, id, Approver),
                "return" => await f.Billing.ReturnAsync(CompanyTwo, id, Approver),
                _ => await f.Billing.PostAsync(CompanyTwo, id, Approver),
            };

            Assert.False(result.ok);
            var b = await host.Db.ProgressBillings.AsNoTracking().FirstAsync(x => x.ID == id);
            Assert.Equal(ProgressBillingStatuses.Submitted, b.Status);   // untouched
            Assert.Empty(await host.Db.SalesInvoices.ToListAsync());
        }
    }
}
