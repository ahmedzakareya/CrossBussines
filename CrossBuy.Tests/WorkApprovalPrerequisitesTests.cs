using System;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using CrossBuy.BL;
using CrossBuy.BL.Platform;
using CrossBuy.BL.Platform.Orchestration;
using CrossBuy.BL.TasksCalendar;
using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Accounting;
using CrossBuy.Models.Context.Tasks;
using CrossBuy.Models.Platform;
using CrossBuy.Tests.TestSupport;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CrossBuy.Tests
{
    // =============================================================================================
    // WORK APPROVAL PREREQUISITES — the two pieces Work Reporting is owed, proved on THIS line.
    //
    // WHY THESE ARE NEW RATHER THAN THE DONOR'S OWN SUITES, and the reason is a boundary rather than
    // convenience. The programme line's TasksCalendarApprovalVocabularyTests and
    // ProjectsBillingApprovalOrchestrationTests were read, imported and then WITHDRAWN: between them
    // they require TaskItem.EscalationLevel / EscalatedAtUtc / EscalatedForDueUtc — the three columns
    // Stage4TaskOrchestrationEngineTests on this line asserts must be ABSENT, because multi-level
    // escalation is a feature no wave has authorized — plus ProjectBillingApprovers,
    // ProgressBilling.ApprovalCycle, ProgressBillingService.DedupKeyFor, DocumentResubmissionRule,
    // CrmFollowUpRule and PosShortageRule. Importing them would have meant either adding a forbidden
    // feature or gutting the suites until they proved very little. These prove the same contracts
    // against what this line actually has.
    //
    // THEY DRIVE THE REAL ENGINE, in the house style: TaskOrchestrationService, TaskWorkPolicyResolver,
    // TaskService and the real CrossDbContext over in-memory SQLite with the company write guard
    // armed. A suite asserting against a mocked engine would prove the rule returns an object, not
    // that a task appears and a ledger row is written.
    // =============================================================================================
    public class WorkApprovalPrerequisitesTests : IDisposable
    {
        private const int CompanyA = 1;
        private const int CompanyB = 65;

        private readonly SqliteConnection _conn;
        private readonly CompanyScopeHolder _holder = new();
        private readonly CrossDbContext _db;
        private readonly ITaskService _tasks;

        public WorkApprovalPrerequisitesTests()
        {
            _conn = new SqliteConnection("DataSource=:memory:");
            _conn.Open();
            _holder.Set(CompanyA, null);

            var options = new DbContextOptionsBuilder<CrossDbContext>()
                .UseSqlite(_conn)
                .AddInterceptors(new CompanyWriteGuardInterceptor(
                    NullLogger<CompanyWriteGuardInterceptor>.Instance))
                .Options;

            _db = new CrossDbContext(options, _holder);
            _db.Database.EnsureCreated();
            using (var cmd = _conn.CreateCommand())
            {
                cmd.CommandText = "PRAGMA foreign_keys = OFF;";
                cmd.ExecuteNonQuery();
            }

            var events = new BusinessEventService(_db, new EntityRegistry(_db),
                new StubContextAccessor(BusinessContext.ForSystem(CompanyA)),
                NullLogger<BusinessEventService>.Instance);

            _tasks = new TaskService(_db, new TaskCalendarEventPublisher(events),
                new TaskNotificationService(_db, new RecordingNotificationService(_db)));
        }

        public void Dispose() { _db.Dispose(); _conn.Dispose(); }

        // ---- harness ------------------------------------------------------------------------------

        private ITaskOrchestrationService Engine() =>
            new TaskOrchestrationService(_db,
                new ITaskOrchestrationRule[] { new ProjectBillingApprovalRule() },
                _tasks, new TaskWorkPolicyResolver(_db), TimeProvider.System,
                NullLogger<TaskOrchestrationService>.Instance);

        /// Built the way ProgressBilling's producer builds it: the same entity code, the same
        /// EventType shape, and a payload carrying the keys the rule reads. `approvalCycle` is passed
        /// explicitly so a test can state which partition it expects rather than inferring one.
        private static BusinessEventEnvelope Billing(
            string action, int companyId = CompanyA, int billingId = 12,
            int? approvalCycle = null, Guid? uid = null, bool thinPayload = false)
        {
            var payload = new JsonObject();
            if (!thinPayload)
            {
                payload["billingId"] = billingId;
                payload["billingNo"] = 7;
                payload["netDue"] = 1500.50m;
            }
            if (approvalCycle is int c) payload["approvalCycle"] = c;

            return new BusinessEventEnvelope
            {
                EventUid = uid ?? Guid.NewGuid(),
                EventType = BusinessEventTypes.Build(EntityRegistry.ProjectBilling, action),
                Entity = new BusinessEventEntity { Code = EntityRegistry.ProjectBilling, Id = billingId },
                Actor = new BusinessEventActor { EmployeeId = 7 },
                Context = new BusinessEventContext { CompanyId = companyId },
                PayloadVersion = ProgressBillingEventPayload.Version,
                Visibility = BusinessEventVisibility.Internal,
                OccurredAt = DateTime.UtcNow,
                Payload = payload,
            };
        }

        private TaskItem[] Tasks(int companyId = CompanyA) =>
            _db.TaskItems.AsNoTracking().Where(t => t.CompanyId == companyId).ToArray();

        private TaskAutoLog[] Ledger(int companyId = CompanyA) =>
            _db.TaskAutoLogs.AsNoTracking().Where(l => l.CompanyId == companyId).ToArray();

        /// Write a ledger row directly, for projection tests that need a known rule key without
        /// standing up the producing rule. The task is real; only the provenance is shortcut.
        private TaskItem SeedTask(string ruleKey, int companyId = CompanyA,
            string status = TaskStatuses.New, int ledgerCompanyId = 0)
        {
            var task = new TaskItem
            {
                CompanyId = companyId, Title = "t", TitleEn = "t",
                Status = status, Priority = "Normal", CreatedAt = DateTime.UtcNow,
            };
            _db.TaskItems.Add(task);
            _db.SaveChanges();

            _db.TaskAutoLogs.Add(new TaskAutoLog
            {
                CompanyId = ledgerCompanyId != 0 ? ledgerCompanyId : companyId,
                TaskId = task.ID,
                RuleKey = ruleKey,
                CreatedAt = DateTime.UtcNow,
            });
            _db.SaveChanges();
            return task;
        }

        private int WaitingCount(int companyId = CompanyA) =>
            TaskApprovalProjection
                .WaitingApproval(_db.TaskItems.AsNoTracking().Where(t => t.CompanyId == companyId),
                                 _db, companyId)
                .Count();

        // =========================================================================================
        // PART 1 — THE RULE. Two of four events are work, and the asymmetry is the rule.
        // =========================================================================================

        [Fact]
        public async Task R1_a_submitted_billing_becomes_one_unassigned_undated_task()
        {
            var result = await Engine().ProjectAsync(Billing(ProgressBillingEvents.Submitted));

            Assert.Equal(TaskProjectionAction.Create, result.Action);
            var task = Assert.Single(Tasks());

            // THE ASSIGNEE IS NOBODY, and that is the domain's answer rather than a placeholder: no
            // field on a ProgressBilling header names an approver in advance.
            Assert.Equal(0, task.AssigneeEmployeeId);

            // THERE IS NO DEADLINE, and DueDatePolicy.None means the work policy does not substitute
            // one either. This work can never be overdue and can never escalate.
            Assert.Null(task.DueDate);
            Assert.Equal(DueDatePolicy.None, new ProjectBillingApprovalRule().DefaultDueDatePolicy);

            Assert.Equal(EntityRegistry.ProjectBilling, task.EntityType);
            Assert.Equal(12, task.EntityId);
            Assert.Equal(TaskStatuses.New, task.Status);

            var log = Assert.Single(Ledger());
            Assert.Equal(task.ID, log.TaskId);
        }

        [Theory]
        [InlineData(ProgressBillingEvents.Approved)]
        [InlineData(ProgressBillingEvents.Returned)]
        public async Task R2_a_decision_ends_the_review_the_submission_asked_for(string decision)
        {
            var engine = Engine();
            await engine.ProjectAsync(Billing(ProgressBillingEvents.Submitted));
            Assert.Equal(TaskStatuses.New, Assert.Single(Tasks()).Status);

            await engine.ProjectAsync(Billing(decision));

            // RETURNED IS NOT AN APPROVAL AND Done DOES NOT SAY IT IS. The task records that the
            // requested review happened; the billing records what was decided.
            Assert.Equal(TaskStatuses.Done, Assert.Single(Tasks()).Status);
        }

        // POSTING IS NOT THE SAME WORK. It has its own actor, its own authorization and its own GL
        // consequences; a person who approves is PERMITTED to post but not REQUIRED to. Projecting it
        // would invent an obligation no evidence in the module supports.
        [Fact]
        public async Task R3_posting_touches_no_approval_work()
        {
            var engine = Engine();
            await engine.ProjectAsync(Billing(ProgressBillingEvents.Submitted));

            await engine.ProjectAsync(Billing(ProgressBillingEvents.Posted));

            Assert.Equal(TaskStatuses.New, Assert.Single(Tasks()).Status);   // still open
            Assert.Single(Ledger());                                          // and nothing new opened
        }

        // EXACT ENTITY CODE, ORDINAL. Not a prefix and not a wildcard: an entity that merely reads
        // like a billing must fall straight through, and one that grows its own "Approved" later must
        // not be answered by this rule.
        [Fact]
        public void R4_nothing_that_merely_reads_like_a_billing_is_answered()
        {
            var rule = new ProjectBillingApprovalRule();

            foreach (var code in new[] { "ProjectBillingX", "Progress", "Project", EntityRegistry.Project })
            {
                var envelope = Billing(ProgressBillingEvents.Submitted);
                var near = new BusinessEventEnvelope
                {
                    EventUid = envelope.EventUid,
                    EventType = BusinessEventTypes.Build(code, ProgressBillingEvents.Submitted),
                    Entity = new BusinessEventEntity { Code = code, Id = 12 },
                    Actor = envelope.Actor,
                    Context = envelope.Context,
                    PayloadVersion = 1,
                    Visibility = BusinessEventVisibility.Internal,
                    OccurredAt = envelope.OccurredAt,
                    Payload = envelope.Payload,
                };

                Assert.Equal(TaskProjectionAction.Ignore, rule.Decide(near).Action);
            }

            // control: the exact code IS answered, so the loop above is not passing vacuously.
            Assert.NotEqual(TaskProjectionAction.Ignore,
                rule.Decide(Billing(ProgressBillingEvents.Submitted)).Action);
        }

        // A THINNER PAYLOAD PRODUCES A THINNER TITLE AND NEVER AN EXCEPTION. The rule reads its
        // payload defensively because a producer may legitimately omit an optional field.
        [Fact]
        public void R5_a_payload_missing_every_optional_field_is_still_answered()
        {
            var decision = new ProjectBillingApprovalRule()
                .Decide(Billing(ProgressBillingEvents.Submitted, thinPayload: true));

            Assert.Equal(TaskProjectionAction.Create, decision.Action);
            Assert.False(string.IsNullOrWhiteSpace(decision.Intent!.TitleEn));
        }

        // ---- THE CYCLE PARTITION, ASSERTED AS THIS LINE ACTUALLY BEHAVES -------------------------
        //
        // The rule addresses work by billing AND round, so a late cycle-1 decision cannot close
        // round 2's task. On THIS line the producer's ProgressBillingEventPayload carries no
        // `approvalCycle` (verified: the payload has BillingId, BillingNo, ProjectId, ProgressId,
        // CustomerId, Status, GrossWork, NetDue, ActorEmployeeId and three receipt ids, and no round),
        // so every event lands in the LEGACY partition. That is the rule's own documented fallback and
        // it is safe here for the reason the rule states: under this producer a billing can have at
        // most one Submitted fact, so the legacy partition holds at most one task.
        //
        // These assert the ACTUAL state rather than the donor's. When the producer grows the field,
        // the first of these flips to KeyPrefixFor(12, 1) and the second stops being a no-op — which
        // is exactly the signal that the dependency closed.
        [Fact]
        public async Task R6_without_a_producer_cycle_the_work_lands_in_the_legacy_partition()
        {
            await Engine().ProjectAsync(Billing(ProgressBillingEvents.Submitted));

            var log = Assert.Single(Ledger());
            Assert.StartsWith(ProjectBillingApprovalRule.LegacyKeyPrefixFor(12), log.RuleKey);
            Assert.DoesNotContain(":cycle:", log.RuleKey, StringComparison.Ordinal);
        }

        // AND THE PARTITIONS CANNOT REACH EACH OTHER. A cycle-scoped decision must not close legacy
        // work, which is what makes the fallback safe in both directions rather than merely quiet.
        [Fact]
        public async Task R7_a_cycle_scoped_decision_cannot_close_legacy_work()
        {
            var engine = Engine();
            await engine.ProjectAsync(Billing(ProgressBillingEvents.Submitted));     // legacy partition

            await engine.ProjectAsync(Billing(ProgressBillingEvents.Approved, approvalCycle: 1));

            Assert.Equal(TaskStatuses.New, Assert.Single(Tasks()).Status);           // untouched

            // control: the legacy decision DOES close it, so the assertion above is about the
            // partition and not about decisions failing generally.
            await engine.ProjectAsync(Billing(ProgressBillingEvents.Approved));
            Assert.Equal(TaskStatuses.Done, Assert.Single(Tasks()).Status);
        }

        // THE TRAILING COLON IS LOAD-BEARING: without it "cycle:1:" would prefix-match "cycle:11:",
        // and round 11's task would answer to round 1's decision.
        [Fact]
        public void R8_round_one_is_not_a_prefix_of_round_eleven()
        {
            var one = ProjectBillingApprovalRule.KeyPrefixFor(12, 1);
            var eleven = ProjectBillingApprovalRule.KeyPrefixFor(12, 11);

            Assert.False(eleven.StartsWith(one, StringComparison.Ordinal));
            Assert.EndsWith(":", one, StringComparison.Ordinal);
        }

        // =========================================================================================
        // PART 2 — THE PROJECTION. Membership comes from the ORCHESTRATION LEDGER, and from nothing
        // else. This is the contract Work Reporting is owed: a report and a screen must not be able
        // to disagree about the word "approval".
        // =========================================================================================

        [Theory]
        [InlineData("LeaveApproval")]
        [InlineData("EmployeeRequestApproval")]
        [InlineData("InventoryApproval")]
        [InlineData("ProjectBillingApproval")]
        [InlineData("DocumentVerification")]
        public void P1_each_of_the_five_flows_is_waiting_when_its_ledger_row_says_so(string rule)
        {
            SeedTask($"{TaskApprovalProjection.ProjectionPrefix}{rule}:x:1");

            Assert.Equal(1, WaitingCount());
        }

        // NOT TITLES, NOT ENTITY LINKS. A task whose TITLE and ENTITY say "approval" but whose ledger
        // row is some other rule is NOT waiting approval. This is the assertion that makes the
        // projection ledger-derived rather than text-derived.
        [Fact]
        public void P2_a_title_and_an_entity_link_do_not_make_a_task_an_approval()
        {
            var task = new TaskItem
            {
                CompanyId = CompanyA,
                Title = "اعتماد مستخلص", TitleEn = "Approve project billing",
                EntityType = EntityRegistry.ProjectBilling, EntityId = 12,
                Status = TaskStatuses.New, Priority = "Normal", CreatedAt = DateTime.UtcNow,
            };
            _db.TaskItems.Add(task);
            _db.SaveChanges();
            _db.TaskAutoLogs.Add(new TaskAutoLog
            {
                CompanyId = CompanyA, TaskId = task.ID,
                RuleKey = "TaskProjection:SomethingElse:12",
                CreatedAt = DateTime.UtcNow,
            });
            _db.SaveChanges();

            Assert.Equal(0, WaitingCount());
        }

        // PREFIX-ANCHORED, not "contains". A key that merely MENTIONS an approval rule name - another
        // rule's key naming a document, or a look-alike rule name - is not that flow.
        [Theory]
        [InlineData("TaskProjection:NotLeaveApproval:1")]
        [InlineData("SomeOtherPrefix:TaskProjection:LeaveApproval:1")]
        [InlineData("TaskProjection:LeaveApprovalLookalike")]
        public void P3_a_key_that_merely_contains_an_approval_name_does_not_qualify(string ruleKey)
        {
            SeedTask(ruleKey);

            Assert.Equal(0, WaitingCount());
            Assert.Null(TaskApprovalProjection.FlowForRuleKey(ruleKey));
        }

        // COMPLETION AFTER A DECISION. The projection narrows to work still AWAITING a decision, so a
        // task the engine marked Done drops out - which is what stops a report counting finished
        // approvals as backlog.
        [Fact]
        public void P4_a_decided_task_is_no_longer_waiting()
        {
            SeedTask($"{TaskApprovalProjection.ProjectionPrefix}LeaveApproval:x:1",
                     status: TaskStatuses.Done);

            Assert.Equal(0, WaitingCount());
        }

        // ---- COMPANY ISOLATION, BOTH DIRECTIONS --------------------------------------------------

        [Fact]
        public void P5_another_companys_task_does_not_qualify_for_this_one()
        {
            SeedTask($"{TaskApprovalProjection.ProjectionPrefix}LeaveApproval:x:1", companyId: CompanyB);

            Assert.Equal(0, WaitingCount(CompanyA));
            Assert.Equal(1, WaitingCount(CompanyB));
        }

        // THE LEDGER'S OWN CompanyId IS IN THE PREDICATE beside the task's. A ledger row from another
        // tenant must not be able to admit this company's task into the list.
        [Fact]
        public void P6_a_foreign_ledger_row_cannot_admit_a_local_task()
        {
            SeedTask($"{TaskApprovalProjection.ProjectionPrefix}LeaveApproval:x:1",
                     companyId: CompanyA, ledgerCompanyId: CompanyB);

            Assert.Equal(0, WaitingCount(CompanyA));
        }

        // FAIL CLOSED ON AN UNRESOLVED TENANT. Not "all rows" and not a fallback company.
        //
        // A LEDGER ROW STAMPED company 0 IS SEEDED DELIBERATELY, and a mutation is why. Asserting only
        // that company 0 returns nothing passed for an implementation whose guard had been weakened to
        // `companyId < 0`: with no company-0 row in the fixture the ledger predicate found nothing
        // anyway, so the removed guard was invisible. Seeding the row makes the guard the ONLY thing
        // standing between an unresolved caller and a matching row.
        [Fact]
        public void P7_an_unresolved_company_matches_nothing()
        {
            SeedTask($"{TaskApprovalProjection.ProjectionPrefix}LeaveApproval:x:1");
            SeedTask($"{TaskApprovalProjection.ProjectionPrefix}LeaveApproval:x:2", ledgerCompanyId: -1);

            // A row whose ledger stamp is the unresolved value itself.
            var orphan = new TaskItem
            {
                CompanyId = CompanyA, Title = "t", TitleEn = "t",
                Status = TaskStatuses.New, Priority = "Normal", CreatedAt = DateTime.UtcNow,
            };
            _db.TaskItems.Add(orphan);
            _db.SaveChanges();
            _db.Database.ExecuteSqlRaw(
                "INSERT INTO TaskAutoLogs (CompanyId, TaskId, RuleKey, CreatedAt) VALUES (0, {0}, {1}, {2})",
                orphan.ID, $"{TaskApprovalProjection.ProjectionPrefix}LeaveApproval:x:3",
                DateTime.UtcNow);

            Assert.Empty(TaskApprovalProjection
                .WaitingApproval(_db.TaskItems.AsNoTracking(), _db, 0)
                .ToList());

            // control: the same query for a REAL company does return work, so the emptiness above is
            // the guard and not an empty fixture.
            Assert.NotEmpty(TaskApprovalProjection
                .WaitingApproval(_db.TaskItems.AsNoTracking(), _db, CompanyA)
                .ToList());
        }

        // IT NARROWS AND NEVER WIDENS. Composed onto a query the caller already scoped, it can only
        // remove rows from a set they were already entitled to.
        [Fact]
        public void P8_the_projection_only_ever_removes_rows_from_the_callers_set()
        {
            SeedTask($"{TaskApprovalProjection.ProjectionPrefix}LeaveApproval:x:1");
            SeedTask($"{TaskApprovalProjection.ProjectionPrefix}InventoryApproval:x:2");

            var alreadyScoped = _db.TaskItems.AsNoTracking()
                .Where(t => t.CompanyId == CompanyA && t.ID < 0);        // deliberately empty

            Assert.Empty(TaskApprovalProjection.WaitingApproval(alreadyScoped, _db, CompanyA).ToList());
        }

        // ---- THE FLOW VOCABULARY -----------------------------------------------------------------

        [Fact]
        public async Task P9_the_flow_is_answered_from_the_same_prefixes_that_decide_membership()
        {
            await Engine().ProjectAsync(Billing(ProgressBillingEvents.Submitted));
            var task = Assert.Single(Tasks());

            var flows = await TaskApprovalProjection.FlowsForTasksAsync(_db, CompanyA, new[] { task.ID });

            Assert.Equal(TaskApprovalProjection.ApprovalFlows.ProjectBilling, flows[task.ID]);
            Assert.Equal(5, TaskApprovalProjection.ApprovalFlows.All.Count);
        }

        [Fact]
        public async Task P10_a_foreign_company_cannot_label_this_companys_work()
        {
            await Engine().ProjectAsync(Billing(ProgressBillingEvents.Submitted));
            var task = Assert.Single(Tasks());

            var flows = await TaskApprovalProjection.FlowsForTasksAsync(_db, CompanyB, new[] { task.ID });

            Assert.Empty(flows);
        }

        // =========================================================================================
        // PART 3 — THE HOLD AND ESCALATION FIXES THIS LINE ALREADY CARRIES MUST SURVIVE THE IMPORT.
        //
        // Stated here because this package touches the same task substrate: if converging approvals
        // had reintroduced the donor's escalation-projection columns, this would fail.
        // =========================================================================================

        [Fact]
        public void X1_the_hold_columns_are_present_and_the_escalation_projection_columns_are_not()
        {
            var columns = typeof(TaskItem).GetProperties().Select(p => p.Name).ToArray();

            foreach (var hold in new[]
            {
                "HoldRequestedAtUtc", "HoldRequestedByEmployeeId", "HoldReason",
                "HoldApprovedAtUtc", "HoldApprovedByEmployeeId",
            })
                Assert.Contains(hold, columns);

            foreach (var unauthorized in new[] { "EscalationLevel", "EscalatedAtUtc", "EscalatedForDueUtc" })
                Assert.DoesNotContain(unauthorized, columns);
        }

        // A BILLING APPROVAL CAN NEVER ESCALATE, and that follows from the rule's own DueDatePolicy
        // rather than from escalation being switched off: no due date, no sweep eligibility.
        [Fact]
        public async Task X2_billing_approval_work_is_never_sweep_eligible()
        {
            await Engine().ProjectAsync(Billing(ProgressBillingEvents.Submitted));
            var task = Assert.Single(Tasks());

            Assert.Null(task.DueDate);
            Assert.False(TaskStatuses.IsSweepEligible(task, DateTime.UtcNow.AddYears(1)));
        }
    }
}
