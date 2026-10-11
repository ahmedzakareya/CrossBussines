using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CrossBuy.BL;
using CrossBuy.BL.Platform;
using CrossBuy.BL.Platform.Orchestration;
using CrossBuy.BL.TasksCalendar;
using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Tasks;
using CrossBuy.Models.Platform;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CrossBuy.Tests
{
    // =============================================================================================
    // STAGE 4 - THE GENERIC TASK ORCHESTRATION ENGINE.
    //
    // WHAT LANDED AND WHAT DID NOT. Stage 4 converged the engine, its contracts, the consumer and the
    // work policy. It converged ZERO business rules: all nine in the programme folder belong to module
    // waves (CRM, HR, Inventory, Manufacturing, POS, Projects, Documents), and five of them listen for
    // events no producer on this line emits yet. An engine with no rules is not a broken engine - it
    // Ignores every event, which is a successful no-op and the correct state until the first wave lands.
    //
    // SO THE RULES HERE ARE TEST-LOCAL, AND THAT IS STRONGER. A fake rule exercises the CONTRACT rather
    // than one business path: it can be made to produce a foreign company, to throw, or to return a
    // prefix that nearly matches - none of which a real rule would do on demand.
    // =============================================================================================
    public class Stage4TaskOrchestrationEngineTests : IDisposable
    {
        private const int CompanyA = 1;
        private const int CompanyB = 2;

        private readonly SqliteConnection _conn;
        private readonly CompanyScopeHolder _holder = new();
        private readonly CrossDbContext _db;
        private readonly ITaskService _tasks;

        public Stage4TaskOrchestrationEngineTests()
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

        // -----------------------------------------------------------------------------------------
        // harness
        // -----------------------------------------------------------------------------------------

        private ITaskOrchestrationService Engine(params ITaskOrchestrationRule[] rules) =>
            new TaskOrchestrationService(_db, rules, _tasks,
                new TaskWorkPolicyResolver(_db), TimeProvider.System,
                NullLogger<TaskOrchestrationService>.Instance);

        private static BusinessEventEnvelope Envelope(
            string eventType, int companyId, string entityCode, int entityId) => new()
            {
                EventUid = Guid.NewGuid(),
                EventType = eventType,
                Entity = new BusinessEventEntity { Code = entityCode, Id = entityId },
                Actor = new BusinessEventActor { EmployeeId = 7 },
                Context = new BusinessEventContext { CompanyId = companyId },
                PayloadVersion = 1,
                Visibility = "Internal",
                OccurredAt = DateTime.UtcNow,
            };

        private const string Rule = "Stage4Probe";

        private static string KeyFor(int entityId) =>
            $"TaskProjection:{Rule}:{EntityRegistry.Project}:{entityId}:Created";

        private static string PrefixFor(int entityId) =>
            $"TaskProjection:{Rule}:{EntityRegistry.Project}:{entityId}:";

        /// Creates on "Project.Created", completes on "Project.Closed", ignores everything else.
        private sealed class ProbeRule : ITaskOrchestrationRule
        {
            public int CompanyOverride { get; set; }          // 0 = use the event company
            public bool Throw { get; set; }
            public string RuleType => Rule;
            public DueDatePolicy DefaultDueDatePolicy => DueDatePolicy.None;

            public TaskProjectionDecision Decide(BusinessEventEnvelope e)
            {
                if (Throw) throw new InvalidOperationException("probe rule failed");

                var action = BusinessEventTypes.ActionOf(e.EventType);
                if (string.Equals(action, "Created", StringComparison.Ordinal))
                    return TaskProjectionDecision.Create(new TaskIntent
                    {
                        CompanyId = CompanyOverride > 0 ? CompanyOverride : e.Context.CompanyId,
                        RuleType = Rule,
                        RuleKey = KeyFor(e.Entity.Id),
                        TitleAr = "عمل",
                        TitleEn = "Work",
                        AssigneeEmployeeId = 0,
                        EntityType = EntityRegistry.Project,
                        EntityId = e.Entity.Id,
                    });

                if (string.Equals(action, "Closed", StringComparison.Ordinal))
                    return TaskProjectionDecision.Complete(PrefixFor(e.Entity.Id));

                return TaskProjectionDecision.Ignore;
            }
        }

        // -----------------------------------------------------------------------------------------
        // 1-4. CREATE, REPLAY, CROSS-COMPANY INDEPENDENCE, IGNORE
        // -----------------------------------------------------------------------------------------

        [Fact]
        public async Task A_matching_event_creates_exactly_one_task()
        {
            var result = await Engine(new ProbeRule())
                .ProjectAsync(Envelope("Project.Created", CompanyA, EntityRegistry.Project, 5));

            Assert.Equal(TaskProjectionAction.Create, result.Action);
            var task = _db.TaskItems.IgnoreQueryFilters().Single();
            Assert.Equal(CompanyA, task.CompanyId);
            Assert.Equal(EntityRegistry.Project, task.EntityType);
            Assert.Equal(5, task.EntityId);

            // System authority, never an impersonated person. Both zeros are the contract.
            Assert.Equal(0, task.CreatedByEmployeeId);
            Assert.Equal(0, task.AssigneeEmployeeId);
            Assert.Equal(TaskStatuses.New, task.Status);

            var ledger = _db.TaskAutoLogs.IgnoreQueryFilters().Single();
            Assert.Equal(CompanyA, ledger.CompanyId);
            Assert.Equal(KeyFor(5), ledger.RuleKey);
            Assert.Equal(task.ID, ledger.TaskId);
        }

        [Fact]
        public async Task A_redelivered_event_creates_no_second_task()
        {
            var engine = Engine(new ProbeRule());
            var first = await engine.ProjectAsync(Envelope("Project.Created", CompanyA, EntityRegistry.Project, 5));

            // A NEW envelope with a NEW EventUid - a redelivery is not the same row, so identity has to
            // come from the RuleKey rather than from the event's own id.
            var second = await engine.ProjectAsync(Envelope("Project.Created", CompanyA, EntityRegistry.Project, 5));

            Assert.True(second.AlreadyProjected);
            Assert.Equal(first.TaskId, second.TaskId);
            Assert.Equal(1, _db.TaskItems.IgnoreQueryFilters().Count());
            Assert.Equal(1, _db.TaskAutoLogs.IgnoreQueryFilters().Count());
        }

        [Fact]
        public async Task The_same_rule_key_in_another_company_projects_independently()
        {
            var engine = Engine(new ProbeRule());
            await engine.ProjectAsync(Envelope("Project.Created", CompanyA, EntityRegistry.Project, 5));
            await engine.ProjectAsync(Envelope("Project.Created", CompanyB, EntityRegistry.Project, 5));

            // Identical logical RuleKey, two companies, two tasks. A ledger keyed on the rule alone would
            // read company A's row as B's replay and silently suppress B's work.
            Assert.Equal(2, _db.TaskItems.IgnoreQueryFilters().Count());
            Assert.Equal(new[] { CompanyA, CompanyB },
                _db.TaskAutoLogs.IgnoreQueryFilters().Select(l => l.CompanyId).OrderBy(c => c).ToArray());
        }

        [Fact]
        public async Task An_event_no_rule_wants_creates_nothing_and_is_a_success()
        {
            var result = await Engine(new ProbeRule())
                .ProjectAsync(Envelope("Project.Updated", CompanyA, EntityRegistry.Project, 5));

            Assert.Equal(TaskProjectionAction.Ignore, result.Action);
            Assert.Empty(_db.TaskItems.IgnoreQueryFilters());
            Assert.Empty(_db.TaskAutoLogs.IgnoreQueryFilters());
        }

        [Fact]
        public async Task An_engine_with_no_rules_ignores_everything()
        {
            // The state Stage 4 actually ships in: contracts and engine present, no rule registered.
            var result = await Engine().ProjectAsync(Envelope("Project.Created", CompanyA, EntityRegistry.Project, 5));

            Assert.Equal(TaskProjectionAction.Ignore, result.Action);
            Assert.Empty(_db.TaskItems.IgnoreQueryFilters());
        }

        [Fact]
        public async Task A_task_event_is_never_projected_into_another_task()
        {
            var result = await Engine(new ProbeRule())
                .ProjectAsync(Envelope("Task.Created", CompanyA, EntityRegistry.Task, 5));

            // Without this, one source event could produce an unbounded chain of tasks.
            Assert.Equal(TaskProjectionAction.Ignore, result.Action);
            Assert.Empty(_db.TaskItems.IgnoreQueryFilters());
        }

        // -----------------------------------------------------------------------------------------
        // 5-8. COMPLETION
        // -----------------------------------------------------------------------------------------

        [Fact]
        public async Task Completion_closes_the_task_the_prefix_owns()
        {
            var engine = Engine(new ProbeRule());
            await engine.ProjectAsync(Envelope("Project.Created", CompanyA, EntityRegistry.Project, 5));

            var result = await engine.ProjectAsync(Envelope("Project.Closed", CompanyA, EntityRegistry.Project, 5));

            Assert.Equal(TaskProjectionAction.Complete, result.Action);
            var task = _db.TaskItems.IgnoreQueryFilters().Single();
            Assert.Equal("Done", task.Status);
            Assert.Equal(100, task.ProgressPct);
            Assert.NotNull(task.CompletedAt);
        }

        [Fact]
        public async Task Completion_replay_is_safe()
        {
            var engine = Engine(new ProbeRule());
            await engine.ProjectAsync(Envelope("Project.Created", CompanyA, EntityRegistry.Project, 5));
            await engine.ProjectAsync(Envelope("Project.Closed", CompanyA, EntityRegistry.Project, 5));

            var again = await engine.ProjectAsync(Envelope("Project.Closed", CompanyA, EntityRegistry.Project, 5));

            // ChangeStatusAsync returns ok for an unchanged status, so a second close is a no-op rather
            // than an error - and nothing new is created.
            Assert.Equal(TaskProjectionAction.Complete, again.Action);
            Assert.Equal(1, _db.TaskItems.IgnoreQueryFilters().Count());
            Assert.Equal("Done", _db.TaskItems.IgnoreQueryFilters().Single().Status);
        }

        [Fact]
        public async Task Completing_when_nothing_was_projected_creates_no_state()
        {
            var result = await Engine(new ProbeRule())
                .ProjectAsync(Envelope("Project.Closed", CompanyA, EntityRegistry.Project, 5));

            Assert.Equal(TaskProjectionAction.Ignore, result.Action);
            Assert.Empty(_db.TaskItems.IgnoreQueryFilters());
            Assert.Empty(_db.TaskAutoLogs.IgnoreQueryFilters());
        }

        [Fact]
        public async Task Another_companys_task_is_never_completed_by_this_companys_event()
        {
            var engine = Engine(new ProbeRule());
            await engine.ProjectAsync(Envelope("Project.Created", CompanyB, EntityRegistry.Project, 5));

            // Same entity id, same logical prefix, different company.
            var result = await engine.ProjectAsync(Envelope("Project.Closed", CompanyA, EntityRegistry.Project, 5));

            Assert.Equal(TaskProjectionAction.Ignore, result.Action);
            Assert.NotEqual("Done", _db.TaskItems.IgnoreQueryFilters().Single().Status);
        }

        [Fact]
        public async Task A_prefix_cannot_reach_a_neighbour_that_merely_starts_the_same()
        {
            var engine = Engine(new ProbeRule());
            await engine.ProjectAsync(Envelope("Project.Created", CompanyA, EntityRegistry.Project, 77));

            // THE TRAILING COLON IS THE GUARD. Closing entity 7 must not reach entity 77, and the only
            // thing standing between them is that the prefix ends ":" rather than "7". This is exactly
            // the boundary TAB-6's milestone rule will depend on.
            var result = await engine.ProjectAsync(Envelope("Project.Closed", CompanyA, EntityRegistry.Project, 7));

            Assert.Equal(TaskProjectionAction.Ignore, result.Action);
            Assert.NotEqual("Done", _db.TaskItems.IgnoreQueryFilters().Single().Status);
        }

        // -----------------------------------------------------------------------------------------
        // 9-11. COMPANY, PAYLOAD AND FAILURE
        // -----------------------------------------------------------------------------------------

        [Theory]
        [InlineData(0)]
        [InlineData(-1)]
        public async Task An_event_with_no_resolved_company_fails_closed(int companyId)
        {
            var result = await Engine(new ProbeRule())
                .ProjectAsync(Envelope("Project.Created", companyId, EntityRegistry.Project, 5));

            Assert.Equal(TaskProjectionAction.Ignore, result.Action);
            Assert.Empty(_db.TaskItems.IgnoreQueryFilters());
        }

        [Fact]
        public async Task A_rule_that_names_a_foreign_company_is_refused()
        {
            // The company comes from the EVENT, never from the rule. A rule that returns an intent for
            // another tenant - however it was built - projects nothing.
            var result = await Engine(new ProbeRule { CompanyOverride = CompanyB })
                .ProjectAsync(Envelope("Project.Created", CompanyA, EntityRegistry.Project, 5));

            Assert.Equal(TaskProjectionAction.Ignore, result.Action);
            Assert.Empty(_db.TaskItems.IgnoreQueryFilters());
        }

        [Fact]
        public async Task A_rule_that_throws_leaves_the_dispatch_row_retryable()
        {
            var consumer = new TaskOrchestrationConsumer(
                Engine(new ProbeRule { Throw = true }),
                NullLogger<TaskOrchestrationConsumer>.Instance);

            // THE CONSUMER MUST THROW. Only its own dispatch row is then marked Failed and only it
            // retries; a consumer that swallowed this would turn a lost task into a silent success.
            await Assert.ThrowsAnyAsync<Exception>(() =>
                consumer.HandleAsync(Envelope("Project.Created", CompanyA, EntityRegistry.Project, 5)));

            Assert.Empty(_db.TaskItems.IgnoreQueryFilters());
        }

        [Fact]
        public async Task An_ignored_event_is_a_successful_no_op_for_the_consumer()
        {
            var consumer = new TaskOrchestrationConsumer(
                Engine(new ProbeRule()), NullLogger<TaskOrchestrationConsumer>.Instance);

            // No throw means the dispatch row is marked Done and never retried.
            await consumer.HandleAsync(Envelope("Project.Updated", CompanyA, EntityRegistry.Project, 5));
            Assert.Empty(_db.TaskItems.IgnoreQueryFilters());
        }

        [Fact]
        public async Task A_null_envelope_is_refused_rather_than_projected()
        {
            var consumer = new TaskOrchestrationConsumer(
                Engine(new ProbeRule()), NullLogger<TaskOrchestrationConsumer>.Instance);

            await Assert.ThrowsAsync<ArgumentNullException>(() => consumer.HandleAsync(null!));
        }

        // -----------------------------------------------------------------------------------------
        // 12-17. REGISTRATION AND STAGE BOUNDARY
        // -----------------------------------------------------------------------------------------

        [Fact]
        public void The_consumer_name_is_registered_exactly_once_and_has_an_implementation()
        {
            Assert.Contains(BusinessEventConsumers.TaskOrchestration, BusinessEventConsumers.Registered);
            Assert.Equal(1, BusinessEventConsumers.Registered
                .Count(c => string.Equals(c, BusinessEventConsumers.TaskOrchestration, StringComparison.Ordinal)));

            // The name and the class agree, which is the both-directions contract the file states.
            var consumer = new TaskOrchestrationConsumer(Engine(), NullLogger<TaskOrchestrationConsumer>.Instance);
            Assert.Equal(BusinessEventConsumers.TaskOrchestration, consumer.Consumer);
            Assert.IsAssignableFrom<IBusinessEventConsumer>(consumer);
        }

        [Fact]
        public void The_other_consumers_are_unaffected_by_this_registration()
        {
            Assert.Contains(BusinessEventConsumers.TimelineProjection, BusinessEventConsumers.Registered);
            Assert.Contains(BusinessEventConsumers.NotificationProjection, BusinessEventConsumers.Registered);
            Assert.Contains(BusinessEventConsumers.AiProjection, BusinessEventConsumers.Registered);
            Assert.Equal(4, BusinessEventConsumers.Registered.Length);
        }

        [Fact]
        public void No_business_rule_ships_in_stage_four()
        {
            // Each rule lands with its module's qualified producer. Document verification, HR,
            // Inventory and now ProjectBilling are converged. The unrelated donor rules remain absent
            // and are pinned below.
            var assembly = typeof(ITaskOrchestrationRule).Assembly;
            foreach (var rule in new[]
            {
                "CrmFollowUpRule", "DocumentResubmissionRule",
                "ManufWorkOrderExecutionRule", "PosShortageRule",
            })
                Assert.Null(assembly.GetType("CrossBuy.BL.Platform.Orchestration." + rule));

            // The one that did land, asserted positively so the removal above is accounted for rather
            // than merely absent from a list.
            Assert.NotNull(assembly.GetType("CrossBuy.BL.Platform.Orchestration.DocumentVerificationRule"));
            // HR and Inventory arrived with their transactional producers in the integration wave.
            foreach (var present in new[] { "EmployeeRequestApprovalRule", "InventoryApprovalRule", "LeaveApprovalRule" })
                Assert.NotNull(assembly.GetType("CrossBuy.BL.Platform.Orchestration." + present));

            // ProjectBilling moved out of the absent list in WORK-PREREQUISITES-03. It consumes a
            // contract ProgressBillingService already emitted inside its own transaction, so nothing in
            // the producing module changed to enable it - which is the same bar every rule above met.
            Assert.NotNull(assembly.GetType("CrossBuy.BL.Platform.Orchestration.ProjectBillingApprovalRule"));

            // STAGE 4'S OWN CLAIM IS UNDISTURBED: the engine still ships no rule of its own, and a rule
            // is a separate type from the engine that runs it.
            foreach (var engineType in new[] { "TaskOrchestrationService", "TaskWorkPolicy" })
                Assert.NotNull(assembly.GetType("CrossBuy.BL.Platform.Orchestration." + engineType));
        }

        [Fact]
        public void The_project_milestone_rule_is_not_registered_yet_and_needs_no_engine_change()
        {
            // MOVED WITH THE TAB-6 P0 WAVE. The rule has landed, and the claim this test really makes
            // is the one that still matters and is now PROVEN rather than predicted: it plugged in
            // with a rule class and a registration line, and NO generic engine change.
            var assembly = typeof(ITaskOrchestrationRule).Assembly;
            var rule = assembly.GetType("CrossBuy.BL.Platform.Orchestration.ProjectMilestoneOrchestrationRule");
            Assert.NotNull(rule);
            Assert.True(typeof(ITaskOrchestrationRule).IsAssignableFrom(rule));

            // Everything it plugs into is unchanged: the stable entity code from Stage 2, the rule
            // contract, and the DueDatePolicy.FromSource its design names.
            Assert.Equal("ProjectMilestone", EntityRegistry.ProjectMilestone);
            Assert.True(Enum.IsDefined(typeof(DueDatePolicy), DueDatePolicy.FromSource));
            Assert.NotNull(typeof(ITaskOrchestrationRule).GetMethod("Decide"));

            // The engine's own contract is still exactly four actions - the rule added none.
            Assert.Equal(
                new[] { "Ignore", "Create", "Complete", "SyncProgress" }.OrderBy(x => x, StringComparer.Ordinal),
                Enum.GetNames(typeof(TaskProjectionAction)).OrderBy(x => x, StringComparer.Ordinal));
        }

        [Fact]
        public void Stage_four_introduced_no_hold_or_escalation_behaviour()
        {
            // TaskEscalationTiming came in because TaskWorkPolicy REQUIRES it, and it carries constants
            // and configuration only. The escalation service, the sweep and the Hold columns did not.
            var assembly = typeof(ITaskOrchestrationRule).Assembly;
            Assert.NotNull(assembly.GetType("CrossBuy.BL.Platform.Orchestration.TaskEscalationTiming"));

            var taskColumns = _db.Model.FindEntityType(typeof(TaskItem))!
                .GetProperties().Select(p => p.Name).ToHashSet(StringComparer.Ordinal);

            // ---- NARROWED BY THE AUTHORIZED HOLD STAGE -------------------------------------------
            //
            // This assertion originally named five columns. It was never a product rejection of Hold -
            // it was a SEQUENCING BOUNDARY, saying that STAGE 4 introduced no hold or escalation
            // behaviour of its own, which remains true and is what the name still claims. Hold arrived
            // later, through its own authorized wave, with its model, its SQL artifact, its service,
            // its events and its UI together.
            //
            // THE TWO HALVES ARE NOT THE SAME CLAIM, and separating them is the point:
            //
            //   THE HOLD COLUMNS now EXIST, and the test says so POSITIVELY rather than falling silent
            //   - an assertion that merely stopped mentioning them could not tell "the Hold stage
            //     landed" from "somebody deleted the line to go green".
            //
            //   THE ESCALATION-PROJECTION COLUMNS are STILL ABSENT. EscalationLevel and EscalatedAtUtc
            //   belong to a multi-level escalation feature that no wave has authorized; escalation on
            //   this line is derived, with the notification as its only durable witness. So the
            //   original boundary survives for exactly the half that was never granted.
            foreach (var unauthorized in new[] { "EscalationLevel", "EscalatedAtUtc", "EscalatedForDueUtc" })
                Assert.DoesNotContain(unauthorized, taskColumns);

            foreach (var hold in new[]
            {
                "HoldRequestedAtUtc", "HoldRequestedByEmployeeId", "HoldReason",
                "HoldApprovedAtUtc", "HoldApprovedByEmployeeId",
            })
                Assert.Contains(hold, taskColumns);

            // And Stage 4's own claim is re-stated where it can still fail: the engine, its contracts,
            // the consumer and the work policy carry no hold concern at all.
            foreach (var stageFourType in new[]
            {
                "CrossBuy.BL.Platform.Orchestration.TaskOrchestrationService",
                "CrossBuy.BL.Platform.Orchestration.TaskWorkPolicy",
                "CrossBuy.BL.Platform.Orchestration.TaskOrchestrationConsumer",
            })
            {
                var type = assembly.GetType(stageFourType);
                Assert.NotNull(type);
                Assert.DoesNotContain(type!.GetMethods(), m => m.Name.Contains("Hold", StringComparison.Ordinal));
            }
        }
    }
}
