using System;
using System.Linq;
using System.Threading.Tasks;
using CrossBuy.BL;
using CrossBuy.BL.Platform;
using CrossBuy.BL.Platform.Orchestration;
using CrossBuy.BL.TasksCalendar;
using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Accounting;
using CrossBuy.Models.Context.Admin;
using CrossBuy.Models.Context.Platform;
using CrossBuy.Models.Context.Tasks;
using CrossBuy.Models.Platform;
using CrossBuy.Tests.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CrossBuy.Tests
{
    // =============================================================================================
    // THE APPROVAL ROUND, END TO END — producer, ledger, rule, task.
    //
    // WHAT WAS BROKEN, MEASURED RATHER THAN ASSUMED. The dedup key was billing + action for every
    // transition. Submitted and Returned are the two a billing can reach repeatedly, because
    // Submitted -> Returned -> Submitted is a legal cycle, so a RESUBMISSION deduplicated against the
    // first submission: RecordAsync returned the original event id, no second fact was written, and a
    // consumer that opens approval work created nothing for the second request. The approver was
    // never asked again, and nothing anywhere said so.
    //
    // THESE DRIVE THE REAL CHAIN. The real ProgressBillingService inside its own ScopedTx, the real
    // BusinessEventService (so the dedup index and the in-transaction recording are the real ones),
    // the real envelope the dispatcher would build from the stored row, the real
    // TaskOrchestrationService and the real TaskService. The round therefore has to survive JSON
    // serialization into the Payload column and come back out, which a rule-level test could not show.
    //
    // THE ROUND IS DERIVED, NEVER INVENTED. It is the count of Submitted facts the ledger already
    // holds for this billing - not a timestamp, not the title, not a task id, and not a new column.
    // =============================================================================================
    public class ProjectBillingApprovalCycleTests : IAsyncLifetime
    {
        private const int CompanyOne = 1;
        private const int CompanyTwo = 65;
        private const int Preparer = 11;
        private const int Approver = 22;
        private const int ProjectId = 5;
        private const int ProgressId = 7;

        private PlatformTestHost _host = null!;
        private ProgressBillingService _billing = null!;
        private IBusinessEventService _events = null!;

        public async Task InitializeAsync()
        {
            _host = new PlatformTestHost(companyId: CompanyOne);
            await SeedAsync(_host, CompanyOne);

            _events = new BusinessEventService(_host.Db, _host.Registry(),
                new StubContextAccessor(Ctx(Preparer, CompanyOne)),
                NullLogger<BusinessEventService>.Instance);

            _billing = new ProgressBillingService(_host.Db, new InertAr(), new ProgressService(_host.Db),
                new ContractService(_host.Db, new InertJournal()), _events, Access(_host));
        }

        public Task DisposeAsync() { _host.Dispose(); return Task.CompletedTask; }

        // ---- fixture ------------------------------------------------------------------------------

        private static BusinessContext Ctx(int employeeId, int companyId) => new()
        {
            CompanyId = companyId, EmployeeId = employeeId, UserId = "user-" + employeeId,
            Roles = Array.Empty<string>(), CorrelationId = Guid.NewGuid(),
        };

        private static Employee Emp(int id, int companyId) => new()
        {
            ID = id, FirstName = "T", LastName = "T", FullName = "emp" + id, FullNameEn = "emp" + id,
            EmpCompanyID = companyId, IsActive = true, Address = "-", PhoneNumber = "-",
            Email = $"e{id}@example.com", ProfileImage = "-", Gender = "M", MaritalStatus = "S",
            UserId = "user-" + id,
        };

        private static async Task SeedAsync(PlatformTestHost host, int companyId)
        {
            var db = host.Seed;
            db.Employee.AddRange(Emp(Preparer, companyId), Emp(Approver, companyId));
            db.Customers.Add(new Customer { ID = 1, CompanyID = companyId, Name = "c", ControlAccountId = 1 });
            db.Projects.Add(new Project
            {
                ID = ProjectId, CompanyID = companyId, Code = "P5", Name = "p5", NameEn = "p5",
                CustomerId = 1, RetentionPercent = 0m, AdvancePercent = 0m,
            });
            db.BoqItems.Add(new BoqItem
            {
                ID = 1, CompanyID = companyId, ProjectId = ProjectId, Code = "A", Description = "a",
                Unit = "m", Quantity = 100m, UnitPrice = 100m, SortOrder = 1,
            });
            db.ProjectProgresses.Add(new ProjectProgress
            {
                ID = ProgressId, CompanyID = companyId, ProjectId = ProjectId,
                MeasurementNo = 1, MeasurementDate = DateTime.Today, Status = "Confirmed",
            });
            db.ProjectProgressLines.Add(new ProjectProgressLine
            { ID = 1, ProgressId = ProgressId, BoqItemId = 1, CumulativeQty = 50m });
            await db.SaveChangesAsync();
        }

        private static IProjectsAccessService Access(PlatformTestHost host)
        {
            var http = new Microsoft.AspNetCore.Http.HttpContextAccessor();
            var accessor = new BusinessContextAccessor(new BusinessContextFactory(
                http, host.Db, host.Holder, NullLogger<BusinessContextFactory>.Instance));
            var accounting = new AccountingAccessService(host.Db, http, accessor,
                new BootstrapAccessPolicyReader(host.Db, NullLogger<BootstrapAccessPolicyReader>.Instance),
                NullLogger<AccountingAccessService>.Instance);
            return new ProjectsAccessService(host.Db,
                new PlatformRoleDirectory(host.Db, NullLogger<PlatformRoleDirectory>.Instance),
                accounting, NullLogger<ProjectsAccessService>.Instance);
        }

        /// Accounting and stock writers stay OWNED by their modules: nothing in this wave posts, so
        /// reaching either is a defect and fails loudly rather than returning something benign.
        /// Accounting and stock writers stay OWNED by their modules: nothing in this wave posts, so
        /// reaching either is a defect and fails loudly rather than returning something benign.
        private sealed class InertAr : IReceivableService
        {
            private static Exception No([System.Runtime.CompilerServices.CallerMemberName] string m = "")
                => new NotImplementedException($"No test here posts; IReceivableService.{m} must not be reached.");
            public Task<List<Customer>> GetCustomersAsync(int a) => throw No();
            public Task<(List<Customer> rows, int total)> SearchCustomersAsync(int a, string? b, bool? c, int d, int e) => throw No();
            public Task<List<(string value, string name)>> SuggestCustomersAsync(int a, string? b, int c) => throw No();
            public Task<Customer> CreateCustomerAsync(int a, string b, string? c, string? d, decimal? e) => throw No();
            public Task<(bool ok, string? error)> SaveCustomerAsync(int a, Customer b) => throw No();
            public Task<(bool ok, string? error, SalesInvoice? inv)> CreateSalesInvoiceAsync(int a, int b, DateTime c, List<SalesLineInput> d, string? e, int? f, int? g, decimal? h, int? i) => throw No();
            public Task<(bool ok, string? error, SalesInvoice? inv)> EditSalesInvoiceAsync(int a, int b, int c, DateTime d, List<SalesLineInput> e, string? f, int? g, int? h, decimal? i, int? j) => throw No();
            public Task<(bool ok, string? error)> CreateReceiptAsync(int a, int b, DateTime c, decimal d, string e, int f, string? g, int? h, int? i, decimal? j, int? k) => throw No();
            public Task<List<SalesInvoice>> GetInvoicesAsync(int a) => throw No();
            public Task<decimal> CustomerOutstandingAsync(int a, int b) => throw No();
            public Task<List<AgingRow>> AgingAsync(int a, DateTime b) => throw No();
            public Task<CustomerAnalytics> GetCustomerAnalyticsAsync(int a) => throw No();
            public Task<List<SalesReturn>> GetSalesReturnsAsync(int a) => throw No();
            public Task<SalesReturn?> GetSalesReturnAsync(int a, int b) => throw No();
            public Task<(bool ok, string? error, SalesReturn? ret)> CreateSalesReturnAsync(int a, int b, int? c, DateTime d, List<SalesLineInput> e, string? f, int? g, int? h, decimal? i) => throw No();
            public Task<(bool ok, string? error, SalesReturn? ret)> EditSalesReturnAsync(int a, int b, int c, int? d, DateTime e, List<SalesLineInput> f, string? g, int? h, int? i, decimal? j) => throw No();
        }

        private sealed class InertJournal : IJournalEntryService
        {
            private static Exception No([System.Runtime.CompilerServices.CallerMemberName] string m = "")
                => new NotImplementedException($"Billing must not reach IJournalEntryService.{m}.");
            public Task<(bool ok, string? error, JournalEntry? entry)> CreateDraftAsync(JournalEntryInput a, int? b) => throw No();
            public Task<(bool ok, string? error, JournalEntry? entry)> CreateAndPostAsync(JournalEntryInput a, int? b) => throw No();
            public Task<(bool ok, string? error, JournalEntry? entry)> CreateAndPostNoTxAsync(JournalEntryInput a, int? b) => throw No();
            public Task<(bool ok, string? error)> PostAsync(int a, int? b) => throw No();
            public Task<(bool ok, string? error, int? reversalId)> ReverseAsync(int a, int? b, string? c) => throw No();
            public Task<(bool ok, string? error)> UpdateDraftAsync(int a, int b, JournalEntryInput c, int? d) => throw No();
            public Task<(bool ok, string? error)> DeleteDraftAsync(int a, int b, int? c) => throw No();
        }

        // ---- harness ------------------------------------------------------------------------------

        private async Task<int> DraftAsync()
        {
            var (ok, error, id) = await _billing.SaveDraftAsync(
                CompanyOne, ProjectId, 0, ProgressId, DateTime.Today, 0m, null, Preparer);
            Assert.True(ok, error);
            return id;
        }

        private BusinessEvent[] Ledger(int billingId, string? action = null) =>
            _host.Seed.BusinessEvents.AsNoTracking()
                .Where(e => e.EntityType == EntityRegistry.ProjectBilling && e.EntityId == billingId
                            && (action == null
                                || e.EventType == $"{EntityRegistry.ProjectBilling}.{action}"))
                .OrderBy(e => e.EventId)
                .ToArray();

        private ITaskOrchestrationService Engine() =>
            new TaskOrchestrationService(_host.Db,
                new ITaskOrchestrationRule[] { new ProjectBillingApprovalRule() },
                new TaskService(_host.Db, new TaskCalendarEventPublisher(_events),
                    new TaskNotificationService(_host.Db, new RecordingNotificationService(_host.Db))),
                new TaskWorkPolicyResolver(_host.Db), TimeProvider.System,
                NullLogger<TaskOrchestrationService>.Instance);

        /// Replay exactly what the dispatcher would hand the consumer: the envelope built from the
        /// STORED row, so the round has to have survived the Payload column to be seen here.
        private async Task<TaskProjectionResult> DeliverAsync(BusinessEvent stored) =>
            await Engine().ProjectAsync(_events.BuildEnvelope(stored));

        private TaskItem[] Tasks() => _host.Seed.TaskItems.AsNoTracking()
            .Where(t => t.CompanyId == CompanyOne).OrderBy(t => t.ID).ToArray();

        private static int? CycleOf(BusinessEvent e) =>
            System.Text.Json.Nodes.JsonNode.Parse(e.Payload!)?["approvalCycle"]?.GetValue<int>();

        // =========================================================================================
        // 1 — ONE SUBMISSION, ONE EVENT, ONE TASK.
        // =========================================================================================
        [Fact]
        public async Task C1_a_first_submission_records_one_event_and_opens_one_task()
        {
            int id = await DraftAsync();
            Assert.True((await _billing.SubmitAsync(CompanyOne, id, Preparer)).ok);

            var submitted = Assert.Single(Ledger(id, ProgressBillingEvents.Submitted));
            Assert.Equal(1, CycleOf(submitted));
            Assert.Equal($"projectbilling:{id}:Submitted:cycle:1", submitted.DedupKey);

            Assert.Equal(TaskProjectionAction.Create, (await DeliverAsync(submitted)).Action);
            var task = Assert.Single(Tasks());
            Assert.Equal(EntityRegistry.ProjectBilling, task.EntityType);
            Assert.Equal(id, task.EntityId);

            var log = Assert.Single(_host.Seed.TaskAutoLogs.AsNoTracking().ToArray());
            Assert.StartsWith(ProjectBillingApprovalRule.KeyPrefixFor(id, 1), log.RuleKey);
        }

        // =========================================================================================
        // 2 — THE SAME FACT DELIVERED TWICE IS ONE PIECE OF WORK.
        //
        // Both halves matter. The PRODUCER must not write a second event for a retried transition,
        // and the CONSUMER must not open a second task for a redelivered one - a dispatcher retry,
        // an at-least-once queue, an operator replay.
        // =========================================================================================
        [Fact]
        public async Task C2_a_duplicate_delivery_of_one_submission_opens_no_second_task()
        {
            int id = await DraftAsync();
            await _billing.SubmitAsync(CompanyOne, id, Preparer);
            var submitted = Assert.Single(Ledger(id, ProgressBillingEvents.Submitted));

            await DeliverAsync(submitted);
            var again = await DeliverAsync(submitted);

            Assert.True(again.AlreadyProjected);
            Assert.Single(Tasks());
            Assert.Single(_host.Seed.TaskAutoLogs.AsNoTracking().ToArray());

            // And the producer side: submitting again from Submitted is not a legal move, so there is
            // no second fact either.
            var (ok, _) = await _billing.SubmitAsync(CompanyOne, id, Preparer);
            Assert.False(ok);
            Assert.Single(Ledger(id, ProgressBillingEvents.Submitted));
        }

        // =========================================================================================
        // 3 — A RETURN CLOSES THE ROUND IT ANSWERS.
        // =========================================================================================
        [Fact]
        public async Task C3_a_return_closes_the_matching_round()
        {
            int id = await DraftAsync();
            await _billing.SubmitAsync(CompanyOne, id, Preparer);
            await DeliverAsync(Ledger(id, ProgressBillingEvents.Submitted).Single());

            Assert.True((await _billing.ReturnAsync(CompanyOne, id, Approver)).ok);
            var returned = Assert.Single(Ledger(id, ProgressBillingEvents.Returned));
            Assert.Equal(1, CycleOf(returned));

            await DeliverAsync(returned);

            Assert.Equal(TaskStatuses.Done, Assert.Single(Tasks()).Status);
        }

        // =========================================================================================
        // 4 — A RESUBMISSION IS A SECOND REQUEST: ITS OWN ROUND, ITS OWN EVENT, ITS OWN TASK.
        //
        // THIS IS THE DEFECT THE WAVE EXISTS TO CLOSE. Before the change the second Submitted
        // deduplicated against the first, no event was written, and no task was created.
        // =========================================================================================
        [Fact]
        public async Task C4_a_resubmission_opens_a_distinct_round_event_and_task()
        {
            int id = await DraftAsync();
            await _billing.SubmitAsync(CompanyOne, id, Preparer);
            await DeliverAsync(Ledger(id, ProgressBillingEvents.Submitted).Single());
            await _billing.ReturnAsync(CompanyOne, id, Approver);
            await DeliverAsync(Ledger(id, ProgressBillingEvents.Returned).Single());

            Assert.True((await _billing.SubmitAsync(CompanyOne, id, Preparer)).ok);

            var submissions = Ledger(id, ProgressBillingEvents.Submitted);
            Assert.Equal(2, submissions.Length);                       // a SECOND fact, not a dedup hit
            Assert.Equal(new int?[] { 1, 2 }, submissions.Select(CycleOf).ToArray());
            Assert.Equal($"projectbilling:{id}:Submitted:cycle:2", submissions[1].DedupKey);
            Assert.NotEqual(submissions[0].EventUid, submissions[1].EventUid);

            Assert.Equal(TaskProjectionAction.Create, (await DeliverAsync(submissions[1])).Action);

            var tasks = Tasks();
            Assert.Equal(2, tasks.Length);
            Assert.Equal(TaskStatuses.Done, tasks[0].Status);          // round 1, decided
            Assert.Equal(TaskStatuses.New, tasks[1].Status);           // round 2, open
        }

        // =========================================================================================
        // 5 — A DELAYED ROUND-1 DECISION CANNOT CLOSE ROUND 2.
        //
        // A redelivered dispatch row, a retry, an out-of-order delivery. Without the round in the
        // address this would find round 2's task and close work nobody did.
        // =========================================================================================
        [Fact]
        public async Task C5_a_late_round_one_decision_cannot_close_round_two()
        {
            int id = await DraftAsync();
            await _billing.SubmitAsync(CompanyOne, id, Preparer);
            await DeliverAsync(Ledger(id, ProgressBillingEvents.Submitted).Single());
            await _billing.ReturnAsync(CompanyOne, id, Approver);
            var roundOneDecision = Ledger(id, ProgressBillingEvents.Returned).Single();
            await DeliverAsync(roundOneDecision);
            await _billing.SubmitAsync(CompanyOne, id, Preparer);
            await DeliverAsync(Ledger(id, ProgressBillingEvents.Submitted)[1]);

            var before = Tasks();
            Assert.Equal(TaskStatuses.New, before[1].Status);

            // Round 1's decision arrives again, late.
            await DeliverAsync(roundOneDecision);

            var after = Tasks();
            Assert.Equal(TaskStatuses.Done, after[0].Status);          // its own round, already closed
            Assert.Equal(TaskStatuses.New, after[1].Status);           // round 2 untouched
        }

        // =========================================================================================
        // 6 — A DECISION CLOSES ONLY ITS MATCHING ROUND, including the approval that ends the flow.
        // =========================================================================================
        [Fact]
        public async Task C6_an_approval_closes_only_the_round_it_answers()
        {
            int id = await DraftAsync();
            await _billing.SubmitAsync(CompanyOne, id, Preparer);
            await DeliverAsync(Ledger(id, ProgressBillingEvents.Submitted).Single());
            await _billing.ReturnAsync(CompanyOne, id, Approver);
            await DeliverAsync(Ledger(id, ProgressBillingEvents.Returned).Single());
            await _billing.SubmitAsync(CompanyOne, id, Preparer);
            await DeliverAsync(Ledger(id, ProgressBillingEvents.Submitted)[1]);

            Assert.True((await _billing.ApproveAsync(CompanyOne, id, Approver)).ok);
            var approved = Assert.Single(Ledger(id, ProgressBillingEvents.Approved));

            // The approval answers ROUND 2, because that is the round the open submission carries.
            Assert.Equal(2, CycleOf(approved));
            await DeliverAsync(approved);

            Assert.All(Tasks(), t => Assert.Equal(TaskStatuses.Done, t.Status));
            Assert.Equal(2, Tasks().Length);
        }

        // =========================================================================================
        // 7 — CROSS-COMPANY AND UNRESOLVED COMPANY FAIL CLOSED.
        //
        // The round is counted per tenant: a ledger row belongs to one company, and a count that
        // spanned companies would hand one tenant's round number to another's billing.
        // =========================================================================================
        [Fact]
        public async Task C7_another_company_cannot_reach_or_renumber_this_billing()
        {
            int id = await DraftAsync();
            await _billing.SubmitAsync(CompanyOne, id, Preparer);

            foreach (var foreign in new[] { CompanyTwo, 0, -1 })
            {
                Assert.False((await _billing.SubmitAsync(foreign, id, Preparer)).ok);
                Assert.False((await _billing.ApproveAsync(foreign, id, Approver)).ok);
                Assert.False((await _billing.ReturnAsync(foreign, id, Approver)).ok);
            }

            // One submission, still round 1 - no foreign call advanced or consumed a round.
            var submissions = Ledger(id, ProgressBillingEvents.Submitted);
            Assert.Single(submissions);
            Assert.Equal(1, CycleOf(submissions[0]));
        }

        // =========================================================================================
        // 8 — A ROLLED-BACK TRANSITION LEAVES NO EVENT, NO ROUND AND NO TASK.
        //
        // The event is recorded INSIDE the caller's ScopedTx and BEFORE its commit, so a refused
        // transition cannot leave a fact behind - and therefore cannot consume a round number either,
        // which is what keeps the count honest.
        // =========================================================================================
        [Fact]
        public async Task C8_a_refused_transition_records_nothing_and_consumes_no_round()
        {
            int id = await DraftAsync();

            // Approving a Draft is not a legal move.
            Assert.False((await _billing.ApproveAsync(CompanyOne, id, Approver)).ok);
            Assert.Empty(Ledger(id));
            Assert.Empty(Tasks());

            // Self-approval is refused on the record, after the state check passes.
            await _billing.SubmitAsync(CompanyOne, id, Preparer);
            var (ok, error) = await _billing.ApproveAsync(CompanyOne, id, Preparer);
            Assert.False(ok);
            Assert.Equal(ProgressBillingService.SelfApprovalRefused, error);
            Assert.Empty(Ledger(id, ProgressBillingEvents.Approved));

            // The submission that DID happen still holds round 1: a refusal consumed nothing.
            Assert.Equal(1, CycleOf(Ledger(id, ProgressBillingEvents.Submitted).Single()));
        }

        // =========================================================================================
        // 9 — POSTING COMPLETES NO APPROVAL WORK.
        //
        // Posting is separate work with its own actor, its own authorization and its own GL and
        // receipt consequences. The round rides on the Posted fact for FIDELITY - a report can say
        // which round was approved - and its dedup key is deliberately unchanged, because the state
        // machine admits Posted at most once per billing.
        //
        // SCOPE OF THIS TEST, STATED. It drives the rule with a Posted envelope rather than calling
        // PostAsync, because posting writes a sales invoice through IArService and this wave owns
        // neither the accounting nor the stock writer. What is proved is the consumer contract: a
        // Posted fact for a billing with OPEN approval work leaves that work open.
        // =========================================================================================
        [Fact]
        public async Task C9_a_posted_fact_leaves_open_approval_work_open()
        {
            int id = await DraftAsync();
            await _billing.SubmitAsync(CompanyOne, id, Preparer);
            var submitted = Ledger(id, ProgressBillingEvents.Submitted).Single();
            await DeliverAsync(submitted);
            Assert.Equal(TaskStatuses.New, Assert.Single(Tasks()).Status);

            var posted = _events.BuildEnvelope(submitted);
            var postedFact = new BusinessEventEnvelope
            {
                EventUid = Guid.NewGuid(),
                EventType = $"{EntityRegistry.ProjectBilling}.{ProgressBillingEvents.Posted}",
                Entity = posted.Entity,
                Actor = posted.Actor,
                Context = posted.Context,
                PayloadVersion = posted.PayloadVersion,
                Visibility = posted.Visibility,
                OccurredAt = DateTime.UtcNow,
                Payload = posted.Payload,          // same round the approval carried
            };

            Assert.Equal(TaskProjectionAction.Ignore,
                new ProjectBillingApprovalRule().Decide(postedFact).Action);

            await Engine().ProjectAsync(postedFact);

            Assert.Equal(TaskStatuses.New, Assert.Single(Tasks()).Status);
        }

        // =========================================================================================
        // THE MIGRATION EDGE, PROVED RATHER THAN ASSUMED.
        //
        // A billing submitted BEFORE rounds existed carries no cycle, so its task sits in the rule's
        // legacy partition. Its decision must land there too, or the approver's task never closes.
        // That is why a decision READS BACK the open submission's own round instead of recomputing a
        // count: recomputing would stamp "1" onto a decision whose submission had none.
        // =========================================================================================
        [Fact]
        public async Task C10_a_legacy_submission_is_closed_by_a_legacy_decision()
        {
            int id = await DraftAsync();

            // A pre-change Submitted fact: the old key, and a payload with no round at all.
            // Through the SERVICE'S OWN context: it already tracks this billing from the draft, so a
            // change made on a second context would be invisible to it and the transition below
            // would be refused for the wrong reason.
            _host.Db.ProgressBillings.Single(b => b.ID == id).Status = ProgressBillingStatuses.Submitted;
            await _host.Db.SaveChangesAsync();
            // INSIDE A TRANSACTION, because the kernel refuses an event recorded without one - the
            // same contract the producer meets. Seeding the legacy fact any other way would be
            // writing a row the product could never have written.
            await using (var tx = await ScopedTx.BeginOrJoinAsync(_host.Db))
            {
                await _events.RecordAsync(new BusinessEventRecord
                {
                    EntityCode = EntityRegistry.ProjectBilling,
                    EntityId = id,
                    EventType = $"{EntityRegistry.ProjectBilling}.{ProgressBillingEvents.Submitted}",
                    PayloadVersion = ProgressBillingEventPayload.Version,
                    Visibility = BusinessEventVisibility.Internal,
                    DedupKey = $"projectbilling:{id}:Submitted",
                    Payload = new { billingId = id, billingNo = 1, netDue = 10m },
                });
                await tx.CommitAsync();
            }

            var legacy = Ledger(id, ProgressBillingEvents.Submitted).Single();
            Assert.Null(CycleOf(legacy));
            await DeliverAsync(legacy);

            var log = Assert.Single(_host.Seed.TaskAutoLogs.AsNoTracking().ToArray());
            Assert.StartsWith(ProjectBillingApprovalRule.LegacyKeyPrefixFor(id), log.RuleKey);

            // The decision reads the open submission's round - there is none - so it lands legacy too.
            Assert.True((await _billing.ReturnAsync(CompanyOne, id, Approver)).ok);
            var returned = Ledger(id, ProgressBillingEvents.Returned).Single();
            Assert.Null(CycleOf(returned));

            await DeliverAsync(returned);
            Assert.Equal(TaskStatuses.Done, Assert.Single(Tasks()).Status);
        }
    }
}
