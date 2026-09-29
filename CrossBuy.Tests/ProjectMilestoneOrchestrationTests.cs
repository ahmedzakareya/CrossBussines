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
using CrossBuy.Models.Context.Accounting;
using CrossBuy.Models.Context.Platform;
using CrossBuy.Models.Context.Tasks;
using CrossBuy.Models.Platform;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CrossBuy.Tests
{
    // ================================================================================================
    // TAB-6 P0 — PROJECT MILESTONE -> TASK ORCHESTRATION.
    //
    // Four things this suite exists to hold:
    //
    //   1. ONE COMMITMENT IS ONE PIECE OF WORK. Created opens exactly one task; a redelivered event
    //      opens none; a non-terminal move opens none. The failure this guards against is task spam,
    //      which is invisible until somebody opens the list.
    //
    //   2. THE DEADLINE IS THE MILESTONE'S OWN. TargetDate reaches TaskItem.DueDate through the REAL
    //      producer, the REAL stored payload and the REAL envelope builder. A test that hand-built the
    //      payload would prove the rule and miss a producer that never populated the field.
    //
    //   3. SETTLEMENT IS THE MODEL'S VOCABULARY, AND IT FAILS SAFE. Completed and Cancelled settle;
    //      Planned and InProgress do not; and a status nobody defined must NOT retire live work.
    //
    //   4. THE PRODUCER IS A PRODUCER. ProjectMilestoneService writes business events and NOT tasks.
    //      Asserted behaviourally - drive the real service and look at the table - rather than by
    //      grepping the file for the word "Task", because a grep-shaped guard fails open.
    //
    // NON-VACUITY: every "nothing happened" assertion has a "something happened" twin driven through
    // the same path, so an orchestration that silently did nothing at all cannot pass.
    //
    // THE ENVELOPE IS NEVER FAKED WHERE A REAL ONE EXISTS. BusinessEventService.BuildEnvelope is the
    // production builder the dispatch worker uses, and it is what these tests drive. Hand-built
    // envelopes appear only for shapes the producer CANNOT emit - an unknown status, a foreign
    // company announcing another tenant's milestone - and each one says so at the call site.
    // ================================================================================================
    public class ProjectMilestoneOrchestrationTests : IDisposable
    {
        private const int CompanyA = 1;
        private const int CompanyB = 2;
        private const int ActorA = 10;
        private static readonly DateTime Target = new(2026, 11, 20, 0, 0, 0, DateTimeKind.Utc);

        private readonly SqliteConnection _conn;
        private readonly CompanyScopeHolder _holder = new();
        private readonly CrossDbContext _db;
        private readonly IProjectMilestoneService _milestones;
        private readonly BusinessEventService _events;
        private readonly TaskOrchestrationService _engine;
        private readonly TaskOrchestrationConsumer _consumer;

        public ProjectMilestoneOrchestrationTests()
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

            var accessor = new StubContextAccessor(Ctx(CompanyA));
            _events = new BusinessEventService(_db, new EntityRegistry(_db), accessor,
                NullLogger<BusinessEventService>.Instance);

            _milestones = new ProjectMilestoneService(
                _db, accessor, new AllowAllProjectsAccess(), _events);

            var tasks = new TaskService(_db, new TaskCalendarEventPublisher(_events),
                new TaskNotificationService(_db, new TestSupport.UatRecordingNotificationService(_db)));

            // THE REAL ENGINE with exactly ONE rule: this suite proves the milestone rule, and a second
            // rule in the set could mask or supply a projection it did not make.
            _engine = new TaskOrchestrationService(
                _db,
                new ITaskOrchestrationRule[] { new ProjectMilestoneOrchestrationRule() },
                tasks,
                new TaskWorkPolicyResolver(_db));

            _consumer = new TaskOrchestrationConsumer(_engine, NullLogger<TaskOrchestrationConsumer>.Instance);

            Seed(1, CompanyA);
            Seed(2, CompanyB);
        }

        public void Dispose() { _db.Dispose(); _conn.Dispose(); }

        private static BusinessContext Ctx(int companyId) => new()
        {
            CompanyId = companyId, EmployeeId = ActorA, UserId = "u10",
            Roles = Array.Empty<string>(),
        };

        private void Seed(int projectId, int companyId)
        {
            _db.Projects.Add(new Project
            { ID = projectId, CompanyID = companyId, Code = "P" + projectId, Name = "Project " + projectId });
            _db.SaveChanges();
        }

        private sealed class AllowAllProjectsAccess : IProjectsAccessService
        {
            public Task<bool> CanAsync(BusinessContext context, string action,
                PermissionTarget? target = null, CancellationToken cancellationToken = default)
                => Task.FromResult(true);
            public Task<AccessScope> ResolveProjectScopeAsync(BusinessContext context, string action,
                CancellationToken cancellationToken = default)
                => Task.FromResult(AccessScope.Company(context.CompanyId));
            public Task<IReadOnlySet<int>> MemberProjectIdsAsync(BusinessContext context,
                CancellationToken cancellationToken = default)
                => Task.FromResult<IReadOnlySet<int>>(new HashSet<int>());
            public Task<bool> CanApproveBillingAsync(BusinessContext context, int projectId,
                CancellationToken cancellationToken = default) => Task.FromResult(false);
            public Task<IReadOnlySet<int>> BillingApproverProjectIdsAsync(BusinessContext context,
                CancellationToken cancellationToken = default)
                => Task.FromResult<IReadOnlySet<int>>(new HashSet<int>());
        }

        // ---- helpers -------------------------------------------------------------------------------

        private List<BusinessEvent> Stored(string action) =>
            _db.BusinessEvents.AsNoTracking()
                .Where(e => e.EntityType == EntityRegistry.ProjectMilestone && e.EventType.EndsWith(action))
                .OrderBy(e => e.EventId).ToList();

        private List<TaskItem> TasksOf(int companyId) =>
            _db.TaskItems.AsNoTracking().Where(t => t.CompanyId == companyId)
                .OrderBy(t => t.ID).ToList();

        /// The PRODUCTION envelope builder, exactly as the dispatch worker uses it.
        private BusinessEventEnvelope Real(BusinessEvent stored) => _events.BuildEnvelope(stored);

        /// Hand-built, and ONLY for shapes the producer cannot emit. Each caller says why.
        private static BusinessEventEnvelope Synthetic(
            string entityCode, int entityId, string action, int companyId, object payload) => new()
            {
                EventUid = Guid.NewGuid(),
                EventType = $"{entityCode}.{action}",
                Entity = new BusinessEventEntity { Code = entityCode, Id = entityId },
                Actor = new BusinessEventActor { EmployeeId = null },
                Context = new BusinessEventContext { CompanyId = companyId },
                OccurredAt = new DateTime(2026, 9, 29, 0, 0, 0, DateTimeKind.Utc),
                PayloadVersion = ProjectMilestoneEventPayload.Version,
                Visibility = BusinessEventVisibility.Internal,
                Payload = System.Text.Json.Nodes.JsonNode.Parse(
                    System.Text.Json.JsonSerializer.Serialize(payload,
                        new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))),
            };

        /// Create a milestone through the REAL service and project its Created event through the REAL
        /// consumer. Returns the milestone id and the projected task.
        private async Task<(int MilestoneId, TaskItem Task)> OpenAsync()
        {
            var (ok, err, id) = await _milestones.SaveMilestoneAsync(
                1, 0, "معلم", "milestone", null, null, Target, 0);
            Assert.True(ok, err);

            var created = Assert.Single(Stored(ProjectMilestoneEvents.Created));
            await _consumer.HandleAsync(Real(created));

            return (id, Assert.Single(TasksOf(CompanyA)));
        }

        // ==========================================================================================
        // 1, 3, 4, 5, 7 + END-TO-END: producer -> event -> consumer -> rule -> task.
        // ==========================================================================================

        [Fact]
        public async Task P0_end_to_end_a_created_milestone_becomes_exactly_one_task()
        {
            var (milestoneId, task) = await OpenAsync();

            // 3 / 4 — the link the platform resolves "why does this task exist?" by.
            Assert.Equal(EntityRegistry.ProjectMilestone, task.EntityType);
            Assert.Equal(milestoneId, task.EntityId);

            // correct company
            Assert.Equal(CompanyA, task.CompanyId);

            // 5 — the committed date, carried all the way through the real chain.
            Assert.NotNull(task.DueDate);
            Assert.Equal(Target.Date, task.DueDate!.Value.Date);

            // 7 — nobody named. 0 = unassigned, for a manager to route.
            Assert.Equal(0, task.AssigneeEmployeeId);

            // system-created, not impersonating a person who pressed nothing
            Assert.Equal(0, task.CreatedByEmployeeId);
            Assert.Equal(TaskStatuses.New, task.Status);

            // ONE ledger row, company-scoped, keyed on the milestone and not on its status.
            var log = Assert.Single(_db.TaskAutoLogs.AsNoTracking().ToList());
            Assert.Equal(CompanyA, log.CompanyId);
            Assert.Equal(task.ID, log.TaskId);

            // THE LITERAL KEY, NOT KeyFor() COMPARED WITH ITSELF. Mutation proved the difference: a
            // rule whose suffix changed from "Created" to "Opened" passed every assertion in this file,
            // because KeyFor built the wrong key and the test asked KeyFor what to expect. Completion
            // still worked too, since it matches by PREFIX - so nothing observable broke and the
            // agreed correlation contract had silently changed.
            //
            // The key is a published contract: it is what the ledger stores, what a support engineer
            // greps for, and what any future reader of TaskAutoLog will match on. So it is spelled out.
            Assert.Equal($"TaskProjection:ProjectMilestone:{milestoneId}:Created", log.RuleKey);
            Assert.Equal(ProjectMilestoneOrchestrationRule.KeyFor(milestoneId), log.RuleKey);
        }

        [Fact]
        public async Task P0_the_rule_names_nobody_and_the_check_cannot_be_laundered()
        {
            var (_, task) = await OpenAsync();
            Assert.Equal(0, task.AssigneeEmployeeId);

            // THE TASK-LEVEL ASSERTION IS NOT ENOUGH ON ITS OWN, and mutation proved that rather than
            // theory: TaskOrchestrationService independently refuses an assignee that is not in the
            // company and downgrades it to 0, so a rule mutated to GUESS still produced an unassigned
            // task. The task check was reading the engine's cleanup, not the rule's restraint.
            //
            // So the rule's own decision is asked directly, where no guard can launder the answer.
            var decision = new ProjectMilestoneOrchestrationRule().Decide(Synthetic(
                EntityRegistry.ProjectMilestone, 3, ProjectMilestoneEvents.Created, CompanyA,
                new { companyId = CompanyA, projectId = 1, milestoneId = 3, targetDate = Target }));

            Assert.Equal(TaskProjectionAction.Create, decision.Action);
            Assert.Equal(0, decision.Intent!.AssigneeEmployeeId);
        }

        [Fact]
        public void P0_the_rule_declares_the_source_deadline_policy()
        {
            var rule = new ProjectMilestoneOrchestrationRule();
            Assert.Equal("ProjectMilestone", rule.RuleType);

            // 6 — FromSource is what lets TargetDate become the deadline. OffsetFromEvent would quietly
            // replace the committed date with now+N, and None would make the work unescalatable.
            Assert.Equal(DueDatePolicy.FromSource, rule.DefaultDueDatePolicy);
        }

        // ==========================================================================================
        // 2, 20, 21 — replay.
        // ==========================================================================================

        [Fact]
        public async Task P0_replaying_created_projects_no_duplicate()
        {
            var (_, first) = await OpenAsync();
            var created = Assert.Single(Stored(ProjectMilestoneEvents.Created));

            await _consumer.HandleAsync(Real(created));
            await _consumer.HandleAsync(Real(created));

            var again = Assert.Single(TasksOf(CompanyA));
            Assert.Equal(first.ID, again.ID);
            Assert.Single(_db.TaskAutoLogs.AsNoTracking().ToList());

            var replay = await _engine.ProjectAsync(Real(created));
            Assert.True(replay.AlreadyProjected);
            Assert.Equal(first.ID, replay.TaskId);
        }

        [Fact]
        public async Task P0_replaying_a_settlement_is_safe()
        {
            var (milestoneId, task) = await OpenAsync();
            Assert.True((await _milestones.SetMilestoneStatusAsync(
                1, milestoneId, ProjectMilestoneStatuses.Completed)).ok);

            var moved = Assert.Single(Stored(ProjectMilestoneEvents.StatusChanged));
            await _consumer.HandleAsync(Real(moved));
            await _consumer.HandleAsync(Real(moved));   // second delivery

            var after = Assert.Single(TasksOf(CompanyA));
            Assert.Equal(task.ID, after.ID);
            Assert.Equal(TaskStatuses.Done, after.Status);
            Assert.Single(_db.TaskAutoLogs.AsNoTracking().ToList());
        }

        // ==========================================================================================
        // 8, 9, 10, 11, 12 — settlement semantics.
        // ==========================================================================================

        [Theory]
        [InlineData(ProjectMilestoneStatuses.Completed)]
        [InlineData(ProjectMilestoneStatuses.Cancelled)]
        public async Task P0_a_settled_milestone_settles_its_task(string terminal)
        {
            var (milestoneId, task) = await OpenAsync();
            Assert.True((await _milestones.SetMilestoneStatusAsync(1, milestoneId, terminal)).ok);

            var moved = Assert.Single(Stored(ProjectMilestoneEvents.StatusChanged));
            var result = await _engine.ProjectAsync(Real(moved));

            Assert.Equal(TaskProjectionAction.Complete, result.Action);
            Assert.Equal(task.ID, result.TaskId);
            Assert.Equal(TaskStatuses.Done, Assert.Single(TasksOf(CompanyA)).Status);
        }

        [Fact]
        public async Task P0_a_non_terminal_move_opens_nothing_and_settles_nothing()
        {
            var (milestoneId, task) = await OpenAsync();

            // Planned -> InProgress is a real, legal move the producer emits.
            Assert.True((await _milestones.SetMilestoneStatusAsync(
                1, milestoneId, ProjectMilestoneStatuses.InProgress)).ok);

            var moved = Assert.Single(Stored(ProjectMilestoneEvents.StatusChanged));
            Assert.Equal(TaskProjectionAction.Ignore, (await _engine.ProjectAsync(Real(moved))).Action);

            var after = Assert.Single(TasksOf(CompanyA));
            Assert.Equal(task.ID, after.ID);
            Assert.NotEqual(TaskStatuses.Done, after.Status);

            // …and the Planned side of the same rule, driven directly because the producer refuses a
            // move to the status the row already holds.
            var rule = new ProjectMilestoneOrchestrationRule();
            foreach (var open in new[] { ProjectMilestoneStatuses.Planned, ProjectMilestoneStatuses.InProgress })
                Assert.Equal(TaskProjectionAction.Ignore, rule.Decide(Synthetic(
                    EntityRegistry.ProjectMilestone, milestoneId, ProjectMilestoneEvents.StatusChanged,
                    CompanyA, new { companyId = CompanyA, milestoneId, status = open })).Action);

            // THE ALLOW that makes those refusals mean something.
            Assert.Equal(TaskProjectionAction.Complete, rule.Decide(Synthetic(
                EntityRegistry.ProjectMilestone, milestoneId, ProjectMilestoneEvents.StatusChanged,
                CompanyA, new { companyId = CompanyA, milestoneId, status = ProjectMilestoneStatuses.Completed })).Action);
        }

        [Fact]
        public async Task P0_an_unknown_status_never_settles_live_work()
        {
            var (milestoneId, task) = await OpenAsync();

            // SYNTHETIC BY NECESSITY: SetMilestoneStatusAsync refuses a status outside the vocabulary,
            // so the producer CANNOT emit this. The event is built by hand precisely because the only
            // way this shape reaches a rule in production is corruption or a future vocabulary change.
            foreach (var junk in new[] { "Teleported", "completed", "COMPLETED", "Done", "" })
            {
                var e = Synthetic(EntityRegistry.ProjectMilestone, milestoneId,
                    ProjectMilestoneEvents.StatusChanged, CompanyA,
                    new { companyId = CompanyA, milestoneId, status = junk });

                Assert.Equal(TaskProjectionAction.Ignore, (await _engine.ProjectAsync(e)).Action);
            }

            // Still open: an undefined word must not retire a live commitment. Note "completed" in the
            // wrong case is included on purpose - the vocabulary is Ordinal, and a case-insensitive
            // reading here would settle work the model never marked settled.
            var after = Assert.Single(TasksOf(CompanyA));
            Assert.Equal(task.ID, after.ID);
            Assert.NotEqual(TaskStatuses.Done, after.Status);
        }

        // ==========================================================================================
        // 13, 14 — deletion.
        // ==========================================================================================

        [Fact]
        public async Task P0_deleting_a_milestone_settles_its_task_and_the_event_carries_the_captured_date()
        {
            var (milestoneId, task) = await OpenAsync();
            Assert.True((await _milestones.DeleteMilestoneAsync(1, milestoneId)).ok);

            var deleted = Assert.Single(Stored(ProjectMilestoneEvents.Deleted));

            // 14 — the date was read from the row BEFORE it was removed. The row is gone now, so a
            // producer that tried to reconstruct it afterwards could only have emitted null.
            var payload = System.Text.Json.Nodes.JsonNode.Parse(deleted.Payload!)!;
            Assert.NotNull(payload["targetDate"]);
            Assert.Equal(Target.Date, payload["targetDate"]!.GetValue<DateTime>().Date);
            Assert.False(_db.ProjectMilestones.AsNoTracking().Any(m => m.ID == milestoneId));

            var result = await _engine.ProjectAsync(Real(deleted));
            Assert.Equal(TaskProjectionAction.Complete, result.Action);
            Assert.Equal(task.ID, result.TaskId);
            Assert.Equal(TaskStatuses.Done, Assert.Single(TasksOf(CompanyA)).Status);
        }

        // ==========================================================================================
        // 15, 16 — payload compatibility.
        // ==========================================================================================

        [Fact]
        public void P0_old_payload_json_without_target_date_still_deserializes()
        {
            var old = System.Text.Json.JsonSerializer.Deserialize<ProjectMilestoneEventPayload>(
                """{"companyId":1,"projectId":1,"milestoneId":3,"status":"Planned","previousStatus":null,"actorEmployeeId":10}""",
                new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));

            Assert.NotNull(old);
            Assert.Null(old!.TargetDate);

            // Nothing existing was renamed or removed.
            Assert.Equal(1, old.CompanyId);
            Assert.Equal(1, old.ProjectId);
            Assert.Equal(3, old.MilestoneId);
            Assert.Equal(ProjectMilestoneStatuses.Planned, old.Status);
            Assert.Equal(10, old.ActorEmployeeId);

            // 16 — version unchanged; the field is additive.
            Assert.Equal(1, ProjectMilestoneEventPayload.Version);
        }

        [Fact]
        public void P0_an_event_without_a_target_date_gets_no_invented_deadline()
        {
            var decision = new ProjectMilestoneOrchestrationRule().Decide(Synthetic(
                EntityRegistry.ProjectMilestone, 3, ProjectMilestoneEvents.Created, CompanyA,
                new { companyId = CompanyA, projectId = 1, milestoneId = 3, status = "Planned" }));

            Assert.Equal(TaskProjectionAction.Create, decision.Action);

            // The rule supplies NOTHING rather than a fabricated date; the work policy answers instead.
            Assert.Null(decision.Intent!.SourceDueDate);
        }

        // ==========================================================================================
        // 17, 18, 19 — the tenant and the correlation boundary.
        // ==========================================================================================

        [Fact]
        public async Task P0_the_same_milestone_id_in_two_companies_projects_independently()
        {
            // SYNTHETIC BY NECESSITY: milestone ids are IDENTITY values, so one database cannot
            // naturally produce the same id in two companies. The collision is the whole point of the
            // test, so it is constructed - and the engine's company scoping is what must separate them.
            const int SharedId = 55;
            object P(int c) => new { companyId = c, projectId = c, milestoneId = SharedId, status = "Planned", targetDate = Target };

            await _engine.ProjectAsync(Synthetic(EntityRegistry.ProjectMilestone, SharedId,
                ProjectMilestoneEvents.Created, CompanyA, P(CompanyA)));
            await _engine.ProjectAsync(Synthetic(EntityRegistry.ProjectMilestone, SharedId,
                ProjectMilestoneEvents.Created, CompanyB, P(CompanyB)));

            var a = Assert.Single(TasksOf(CompanyA));
            var b = Assert.Single(TasksOf(CompanyB));
            Assert.NotEqual(a.ID, b.ID);

            // Two ledger rows, same RuleKey, DIFFERENT companies - which is what makes them independent.
            var logs = _db.TaskAutoLogs.AsNoTracking().OrderBy(l => l.ID).ToList();
            Assert.Equal(2, logs.Count);
            Assert.All(logs, l => Assert.Equal(ProjectMilestoneOrchestrationRule.KeyFor(SharedId), l.RuleKey));
            Assert.Equal(new[] { CompanyA, CompanyB }, logs.Select(l => l.CompanyId).OrderBy(c => c));

            // 18 — company A settling cannot reach company B's task.
            await _engine.ProjectAsync(Synthetic(EntityRegistry.ProjectMilestone, SharedId,
                ProjectMilestoneEvents.StatusChanged, CompanyA,
                new { companyId = CompanyA, milestoneId = SharedId, status = ProjectMilestoneStatuses.Completed }));

            Assert.Equal(TaskStatuses.Done, Assert.Single(TasksOf(CompanyA)).Status);
            Assert.NotEqual(TaskStatuses.Done, Assert.Single(TasksOf(CompanyB)).Status);
        }

        [Fact]
        public async Task P0_milestone_7_cannot_settle_milestone_77()
        {
            // The trailing colon is the whole defence: without it "…:7" prefixes "…:77".
            object P(int id) => new { companyId = CompanyA, projectId = 1, milestoneId = id, status = "Planned", targetDate = Target };

            await _engine.ProjectAsync(Synthetic(EntityRegistry.ProjectMilestone, 77,
                ProjectMilestoneEvents.Created, CompanyA, P(77)));
            await _engine.ProjectAsync(Synthetic(EntityRegistry.ProjectMilestone, 7,
                ProjectMilestoneEvents.Created, CompanyA, P(7)));

            var before = TasksOf(CompanyA);
            Assert.Equal(2, before.Count);
            var task77 = before.Single(t => t.EntityId == 77);
            var task7 = before.Single(t => t.EntityId == 7);

            // Settle milestone 7 ONLY.
            await _engine.ProjectAsync(Synthetic(EntityRegistry.ProjectMilestone, 7,
                ProjectMilestoneEvents.StatusChanged, CompanyA,
                new { companyId = CompanyA, milestoneId = 7, status = ProjectMilestoneStatuses.Completed }));

            var after = TasksOf(CompanyA);
            Assert.Equal(TaskStatuses.Done, after.Single(t => t.ID == task7.ID).Status);
            Assert.NotEqual(TaskStatuses.Done, after.Single(t => t.ID == task77.ID).Status);

            // The key shapes themselves, spelled out literally so a suffix or separator change is a
            // failure here rather than a silent contract drift.
            Assert.Equal("TaskProjection:ProjectMilestone:7:", ProjectMilestoneOrchestrationRule.KeyPrefixFor(7));
            Assert.Equal("TaskProjection:ProjectMilestone:7:Created", ProjectMilestoneOrchestrationRule.KeyFor(7));
            Assert.EndsWith(":", ProjectMilestoneOrchestrationRule.KeyPrefixFor(7));
            Assert.False(ProjectMilestoneOrchestrationRule.KeyFor(77)
                .StartsWith(ProjectMilestoneOrchestrationRule.KeyPrefixFor(7), StringComparison.Ordinal));
            Assert.StartsWith(ProjectMilestoneOrchestrationRule.KeyPrefixFor(7),
                ProjectMilestoneOrchestrationRule.KeyFor(7), StringComparison.Ordinal);
            foreach (var s in ProjectMilestoneStatuses.All)
                Assert.DoesNotContain(s, ProjectMilestoneOrchestrationRule.KeyPrefixFor(7), StringComparison.Ordinal);
        }

        [Fact]
        public async Task P0_an_unresolved_company_fails_closed()
        {
            var e = Synthetic(EntityRegistry.ProjectMilestone, 9, ProjectMilestoneEvents.Created, 0,
                new { companyId = 0, projectId = 1, milestoneId = 9, status = "Planned", targetDate = Target });

            Assert.Equal(TaskProjectionAction.Ignore, (await _engine.ProjectAsync(e)).Action);
            Assert.Empty(TasksOf(CompanyA));
            Assert.Empty(TasksOf(CompanyB));
        }

        [Fact]
        public void P0_the_rule_intent_company_is_the_event_company_and_never_a_constant()
        {
            var rule = new ProjectMilestoneOrchestrationRule();
            foreach (var company in new[] { CompanyA, CompanyB, 7, 4242 })
            {
                var d = rule.Decide(Synthetic(EntityRegistry.ProjectMilestone, 3,
                    ProjectMilestoneEvents.Created, company,
                    // The PAYLOAD deliberately names a DIFFERENT company: the envelope context is the
                    // authority, and a rule reading the payload's companyId would fail here.
                    new { companyId = 999, projectId = 1, milestoneId = 3, targetDate = Target }));

                Assert.Equal(company, d.Intent!.CompanyId);
                Assert.NotEqual(999, d.Intent.CompanyId);
            }
        }

        // ==========================================================================================
        // 24, 25 — the engine is untouched and the producer is a producer.
        // ==========================================================================================

        [Fact]
        public async Task P0_task_events_are_never_projected_into_tasks()
        {
            await OpenAsync();

            var taskEvent = Synthetic(EntityRegistry.Task, 1, ProjectMilestoneEvents.Created, CompanyA,
                new { companyId = CompanyA, milestoneId = 1, targetDate = Target });

            Assert.Equal(TaskProjectionAction.Ignore, (await _engine.ProjectAsync(taskEvent)).Action);
            Assert.Single(TasksOf(CompanyA));

            // The rule refuses it on its OWN entity check too, so removing either defence still leaves
            // the loop closed.
            Assert.Equal(TaskProjectionAction.Ignore,
                new ProjectMilestoneOrchestrationRule().Decide(taskEvent).Action);
        }

        [Fact]
        public void P0_the_rule_ignores_every_entity_that_is_not_a_milestone()
        {
            var rule = new ProjectMilestoneOrchestrationRule();
            foreach (var code in new[] { EntityRegistry.Project, EntityRegistry.Task, EntityRegistry.Employee })
                Assert.Equal(TaskProjectionAction.Ignore, rule.Decide(Synthetic(
                    code, 3, ProjectMilestoneEvents.Created, CompanyA,
                    new { companyId = CompanyA, milestoneId = 3, targetDate = Target })).Action);

            // THE ALLOW.
            Assert.Equal(TaskProjectionAction.Create, rule.Decide(Synthetic(
                EntityRegistry.ProjectMilestone, 3, ProjectMilestoneEvents.Created, CompanyA,
                new { companyId = CompanyA, milestoneId = 3, targetDate = Target })).Action);
        }

        [Fact]
        public async Task P0_the_milestone_service_writes_events_and_never_tasks()
        {
            // The whole lifecycle through the REAL service with NO orchestration anywhere near it.
            var (ok, err, id) = await _milestones.SaveMilestoneAsync(
                1, 0, "معلم", "milestone", null, null, Target, 0);
            Assert.True(ok, err);
            Assert.True((await _milestones.SetMilestoneStatusAsync(1, id, ProjectMilestoneStatuses.InProgress)).ok);
            Assert.True((await _milestones.SetMilestoneStatusAsync(1, id, ProjectMilestoneStatuses.Completed)).ok);

            // BEHAVIOURAL, NOT GREP-SHAPED. The tables are the evidence.
            Assert.Equal(3, _db.BusinessEvents.AsNoTracking()
                .Count(e => e.EntityType == EntityRegistry.ProjectMilestone));
            Assert.Empty(TasksOf(CompanyA));
            Assert.Empty(_db.TaskAutoLogs.AsNoTracking().ToList());
        }

        [Fact]
        public async Task P0_every_milestone_event_carries_the_target_date()
        {
            var (ok, err, id) = await _milestones.SaveMilestoneAsync(
                1, 0, "معلم", "milestone", null, null, Target, 0);
            Assert.True(ok, err);
            Assert.True((await _milestones.SetMilestoneStatusAsync(1, id, ProjectMilestoneStatuses.Completed)).ok);

            var stored = _db.BusinessEvents.AsNoTracking()
                .Where(e => e.EntityType == EntityRegistry.ProjectMilestone)
                .OrderBy(e => e.EventId).ToList();
            Assert.Equal(2, stored.Count);

            foreach (var e in stored)
            {
                // CAMEL CASE, because BusinessEventService serializes with JsonSerializerDefaults.Web.
                // A PascalCase reader would bind to nothing and this assertion would pass by finding no
                // value at all - so the date is COMPARED, not merely probed for presence.
                var payload = System.Text.Json.Nodes.JsonNode.Parse(e.Payload!)!;
                Assert.NotNull(payload["targetDate"]);
                Assert.Equal(Target.Date, payload["targetDate"]!.GetValue<DateTime>().Date);
            }
        }

        // ==========================================================================================
        // 22, 23 — registration.
        // ==========================================================================================

        [Fact]
        public void P0_the_rule_resolves_from_DI_as_a_task_orchestration_rule()
        {
            var services = new ServiceCollection();
            services.AddScoped<ITaskOrchestrationRule, ProjectMilestoneOrchestrationRule>();

            using var provider = services.BuildServiceProvider(new ServiceProviderOptions
            { ValidateOnBuild = true, ValidateScopes = true });
            using var scope = provider.CreateScope();

            var rules = scope.ServiceProvider.GetServices<ITaskOrchestrationRule>().ToList();
            Assert.Single(rules.OfType<ProjectMilestoneOrchestrationRule>());
            Assert.Equal("ProjectMilestone", Assert.Single(rules).RuleType);
        }

        [Fact]
        public void P0_exactly_one_projectmilestone_rule_registration_exists()
        {
            // A SOURCE ASSERTION, and the only honest one available: booting the whole composition
            // root here would drag in a database, a host environment and every other module. What it
            // guards is real - a duplicate AddScoped would run the rule twice per event, and the
            // engine runs EVERY matching rule, so the second pass would try to re-project the same
            // milestone.
            var program = System.IO.File.ReadAllText(
                System.IO.Path.Combine(RepoRoot(), "CrossBuy", "Program.cs"));

            var lines = program.Split('\n')
                .Where(l => !l.TrimStart().StartsWith("//"))
                .Where(l => l.Contains("ITaskOrchestrationRule") && l.Contains("ProjectMilestoneOrchestrationRule"))
                .ToList();

            Assert.Single(lines);
            Assert.Contains("AddScoped", lines[0]);
        }

        private static string RepoRoot()
        {
            var dir = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !System.IO.File.Exists(System.IO.Path.Combine(dir.FullName, "CrossBuy.sln")))
                dir = dir.Parent;
            Assert.NotNull(dir);
            return dir!.FullName;
        }
    }
}
