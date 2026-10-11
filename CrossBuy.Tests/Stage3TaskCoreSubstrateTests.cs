using System;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using CrossBuy.BL;
using CrossBuy.BL.Platform;
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
    // STAGE 3 - THE TASK CORE SUBSTRATE THAT STAGE 4 ORCHESTRATION WILL STAND ON.
    //
    // WHY THIS FILE EXISTS AND NOT AN IMPORT. Stage 3 was scoped to converge Task Core. Measuring it
    // first showed there was nothing to converge: the orchestration engine's entire dependency on Task
    // Core is ITaskService.ChangeStatusAsync, CrossDbContext.TaskItems and CrossDbContext.TaskAutoLogs,
    // and all three are already present here - ChangeStatusAsync with a byte-identical signature and
    // TaskAutoLog as a byte-identical entity. The programme line's Task delta is Hold lifecycle, tag
    // filtering, escalation columns and read authorization, none of which the engine touches.
    //
    // SO THE DELIVERABLE IS A CONTRACT, NOT CODE. These tests pin the exact substrate Stage 4 is being
    // promised. If a later wave changes one of these shapes, Stage 4 breaks - and it should break HERE,
    // in a file that says why, rather than inside an orchestration rule that looks unrelated.
    //
    // THEY ARE NOT ORCHESTRATION TESTS. No rule, no dispatcher and no consumer is exercised; none of
    // that exists on this branch yet. Every assertion below is about Task Core alone.
    // =============================================================================================
    public class Stage3TaskCoreSubstrateTests : IDisposable
    {
        private const int CompanyA = 1;
        private const int CompanyB = 2;
        private const int ActorA = 10;
        private const int ActorB = 20;

        private readonly SqliteConnection _conn;
        private readonly CompanyScopeHolder _holder = new();
        private readonly CrossDbContext _db;
        private readonly ITaskService _tasks;

        // A SECOND SCOPE, AND IT IS NOT TEST SCAFFOLDING. CompanyWriteGuardInterceptor refuses a write
        // whose company differs from the ambient scope, so company B rows cannot be created from
        // company A context at all - passing B id is not enough, which is the guard doing its job.
        // Proving isolation therefore needs a genuinely separate scope, not a different argument.
        private readonly CompanyScopeHolder _holderB = new();
        private readonly CrossDbContext _dbB;
        private readonly ITaskService _tasksB;

        public Stage3TaskCoreSubstrateTests()
        {
            _conn = new SqliteConnection("DataSource=:memory:");
            _conn.Open();

            // The scope is left UNRESOLVED on purpose for most of this file. Task Core takes its company
            // as an explicit argument, so these tests prove the argument is honoured rather than leaning
            // on an ambient filter to do the isolation for them.
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
            var notify = new TaskNotificationService(_db, new RecordingNotificationService(_db));
            _tasks = new TaskService(_db, new TaskCalendarEventPublisher(events), notify);

            _holderB.Set(CompanyB, null);
            var optionsB = new DbContextOptionsBuilder<CrossDbContext>()
                .UseSqlite(_conn)
                .AddInterceptors(new CompanyWriteGuardInterceptor(
                    NullLogger<CompanyWriteGuardInterceptor>.Instance))
                .Options;
            _dbB = new CrossDbContext(optionsB, _holderB);
            var eventsB = new BusinessEventService(_dbB, new EntityRegistry(_dbB),
                new StubContextAccessor(BusinessContext.ForSystem(CompanyB)),
                NullLogger<BusinessEventService>.Instance);
            _tasksB = new TaskService(_dbB,
                new TaskCalendarEventPublisher(eventsB),
                new TaskNotificationService(_dbB, new RecordingNotificationService(_dbB)));

            Seed(ActorA, CompanyA);
            Seed(ActorB, CompanyB);
        }

        public void Dispose() { _dbB.Dispose(); _db.Dispose(); _conn.Dispose(); }

        private void Seed(int employeeId, int companyId)
        {
            _db.Employee.Add(new CrossBuy.Models.Context.Admin.Employee
            {
                ID = employeeId, EmpCompanyID = companyId, IsActive = true,
                FirstName = "Emp" + employeeId, LastName = "T", FullName = "Emp " + employeeId,
                Address = "-", PhoneNumber = "-", Email = "e" + employeeId + "@t.local",
                ProfileImage = "-", Gender = "-", MaritalStatus = "-", UserId = "u" + employeeId,
            });
            _db.SaveChanges();
        }

        // SaveAsync REQUIRES an assignee, so the helper supplies one. The unassigned case is its own
        // test below, because it is a contract in its own right rather than a variation of this one.
        private static TaskSaveInput New(string title, int assignee = ActorA, DateTime? due = null) => new()
        {
            Title = title,
            AssigneeEmployeeId = assignee,
            Priority = "Normal",
            DueDate = due,
        };

        // -----------------------------------------------------------------------------------------
        // THE ENGINE'S ACTUAL DEPENDENCY SURFACE
        // -----------------------------------------------------------------------------------------

        [Fact]
        public void The_substrate_stage_four_was_promised_is_present_with_the_exact_shape()
        {
            // ChangeStatusAsync is the ONLY Task Core method the orchestration engine calls. Its exact
            // signature is asserted rather than its mere existence: the engine passes positionally, so a
            // reordered or widened parameter list is a silent breakage, not a compile error, for anyone
            // binding by reflection or by DI against an older assembly.
            var method = typeof(ITaskService).GetMethod("ChangeStatusAsync");
            Assert.NotNull(method);

            var parameters = method!.GetParameters();
            Assert.Equal(4, parameters.Length);
            Assert.Equal(new[] { typeof(int), typeof(int), typeof(string), typeof(int) },
                         parameters.Select(p => p.ParameterType).ToArray());
            Assert.Equal(new[] { "companyId", "id", "status", "currentEmployeeId" },
                         parameters.Select(p => p.Name).ToArray());
        }

        [Fact]
        public void The_replay_ledger_carries_the_four_columns_the_engine_writes()
        {
            // TaskAutoLog is the idempotency ledger: the engine reads it before any write and adds a row
            // after. Company and RuleKey together are the key it looks up by, so both must exist and the
            // company one must be an int - a nullable or a string here would change the lookup silently.
            var entity = _db.Model.FindEntityType(typeof(TaskAutoLog));
            Assert.NotNull(entity);

            var properties = entity!.GetProperties().ToDictionary(p => p.Name, p => p.ClrType);
            Assert.Equal(typeof(int), properties["CompanyId"]);
            Assert.Equal(typeof(string), properties["RuleKey"]);
            Assert.Equal(typeof(int), properties["TaskId"]);
            Assert.Equal(typeof(DateTime), properties["CreatedAt"]);
        }

        [Fact]
        public void The_task_fields_the_engine_reads_and_writes_are_present()
        {
            var properties = _db.Model.FindEntityType(typeof(TaskItem))!
                .GetProperties().Select(p => p.Name).ToHashSet(StringComparer.Ordinal);

            foreach (var required in new[] { "ID", "CompanyId", "Status", "ProgressPct" })
                Assert.Contains(required, properties);
        }

        // -----------------------------------------------------------------------------------------
        // CREATION AND LOOKUP
        // -----------------------------------------------------------------------------------------

        [Fact]
        public async Task A_task_is_created_into_the_company_that_was_passed_not_the_ambient_one()
        {
            var (ok, error, id) = await _tasksB.SaveAsync(CompanyB, New("in B", ActorB), ActorB);

            Assert.True(ok, error);

            // Written through B own scope, and the row carries B - the company argument is honoured
            // rather than being overwritten by anything ambient.
            var row = _db.TaskItems.IgnoreQueryFilters().Single(t => t.ID == id);
            Assert.Equal(CompanyB, row.CompanyId);
        }

        [Fact]
        public async Task A_company_reads_only_its_own_tasks()
        {
            // ASYMMETRIC: two in A, one in B. A symmetric fixture passes just as happily with the
            // company predicate inverted, so the counts have to differ to carry any identity at all.
            await _tasks.SaveAsync(CompanyA, New("a-one"), ActorA);
            await _tasks.SaveAsync(CompanyA, New("a-two"), ActorA);
            var (_, _, bId) = await _tasksB.SaveAsync(CompanyB, New("b-only", ActorB), ActorB);

            Assert.Null(await _tasks.GetAsync(CompanyA, bId));
            Assert.NotNull(await _tasksB.GetAsync(CompanyB, bId));

            Assert.Equal(2, _db.TaskItems.IgnoreQueryFilters().Count(t => t.CompanyId == CompanyA));
            Assert.Equal(1, _db.TaskItems.IgnoreQueryFilters().Count(t => t.CompanyId == CompanyB));
        }

        [Fact]
        public async Task A_foreign_task_id_answers_exactly_as_a_missing_one()
        {
            var (_, _, bId) = await _tasksB.SaveAsync(CompanyB, New("b-only", ActorB), ActorB);

            // No oracle: "someone else's task" and "no such task" are the same answer, so an id cannot be
            // probed to discover that another tenant holds it.
            Assert.Null(await _tasks.GetAsync(CompanyA, bId));
            Assert.Null(await _tasks.GetAsync(CompanyA, 999_999));

            var foreign = await _tasks.ChangeStatusAsync(CompanyA, bId, "InProgress", ActorA);
            var missing = await _tasks.ChangeStatusAsync(CompanyA, 999_999, "InProgress", ActorA);
            Assert.False(foreign.ok);
            Assert.False(missing.ok);
            Assert.Equal(missing.error, foreign.error);
        }

        // -----------------------------------------------------------------------------------------
        // ASSIGNMENT, DUE DATE, COMPLETION
        // -----------------------------------------------------------------------------------------

        [Fact]
        public async Task Unassigned_work_is_refused_by_the_service_and_is_exactly_why_the_engine_writes_directly()
        {
            // THE FINDING THIS TEST RECORDS. The interactive path refuses an unassigned task - a person
            // filling in a form must say who owns the work. But the orchestration design creates work
            // with AssigneeEmployeeId = 0, because when a milestone changes status there is no authority
            // that knows whose job it is, and inventing one would be a fabricated fact about ownership.
            //
            // Both are correct, and they are reconciled by the engine constructing TaskItem DIRECTLY
            // rather than calling SaveAsync. Stage 4 depends on that being possible, so it is pinned
            // here: if a future guard moved this refusal onto the entity or into SaveChanges, every
            // projected task would start failing and the cause would look unrelated.
            var refused = await _tasks.SaveAsync(CompanyA, New("no owner", assignee: 0), ActorA);
            Assert.False(refused.ok);
            Assert.Equal(0, refused.id);

            // The engine's path: a direct add, unassigned, with no interactive actor impersonated.
            _db.TaskItems.Add(new TaskItem
            {
                CompanyId = CompanyA,
                Title = "projected",
                AssigneeEmployeeId = 0,
                CreatedByEmployeeId = 0,
                Priority = "Normal",
                Status = TaskStatuses.New,
                ActualHours = 0m,
                CreatedAt = DateTime.UtcNow,
            });
            await _db.SaveChangesAsync();

            var projected = _db.TaskItems.IgnoreQueryFilters().Single(t => t.Title == "projected");
            Assert.Equal(0, projected.AssigneeEmployeeId);
            Assert.Equal(CompanyA, projected.CompanyId);
        }

        [Fact]
        public void The_status_vocabulary_stage_four_calls_is_present_and_hold_free()
        {
            // Stage 3 imported TaskStatuses so Stage 4 can name a status instead of spelling a literal.
            // These six members are exactly what the orchestration engine references.
            Assert.Equal("New", TaskStatuses.New);
            Assert.Equal("InProgress", TaskStatuses.InProgress);
            Assert.Equal("Done", TaskStatuses.Done);
            Assert.True(TaskStatuses.IsActionable(TaskStatuses.New));
            Assert.False(TaskStatuses.IsActionable(TaskStatuses.Done));
            Assert.True(TaskStatuses.IsOverdue(DateTime.UtcNow.AddDays(-1), TaskStatuses.New, DateTime.UtcNow));
            Assert.NotNull(TaskStatuses.OverdueAt(DateTime.UtcNow));

            // ---- THE SEAM THIS ASSERTION GUARDED IS NOW CLOSED ------------------------------------
            //
            // Stage 3 imported TaskStatuses verbatim EXCEPT three TaskItem-typed overloads, because each
            // reads hold columns that did not exist yet, and asserted their absence so they would be
            // recognised as OWED rather than missing. That was correct for Stage 3 and stayed correct
            // for Stage 4, which introduced no Hold behaviour of its own.
            //
            // The authorized Hold stage has since landed the five columns, so all three are back. The
            // assertion is INVERTED rather than deleted: what Stage 3 proved was that the parameter-
            // taking twin was always present and the file was never rewritten to fit, and that is still
            // what matters - the rule has ONE definition and these overloads only change its shape.
            var members = typeof(TaskStatuses).GetMethods().Select(m => m.Name).ToHashSet(StringComparer.Ordinal);
            Assert.Contains("IsOnHold", members);          // the parameter-taking twin, present since Stage 3
            Assert.Contains("SweepEligibleAt", members);   // restored by the Hold stage, with its columns

            // The twins agree, which is the property the seam existed to protect: one rule, two shapes.
            var overdue = new TaskItem
            {
                CompanyId = 1, Title = "t", Status = TaskStatuses.New,
                DueDate = DateTime.UtcNow.AddDays(-1), Priority = "Normal",
            };
            Assert.True(TaskStatuses.IsSweepEligible(overdue, DateTime.UtcNow));
            overdue.HoldApprovedAtUtc = DateTime.UtcNow;
            Assert.False(TaskStatuses.IsSweepEligible(overdue, DateTime.UtcNow));
        }

        [Fact]
        public async Task A_due_date_supplied_at_creation_is_the_one_stored()
        {
            var due = new DateTime(2026, 11, 30, 0, 0, 0, DateTimeKind.Utc);

            var (ok, error, id) = await _tasks.SaveAsync(CompanyA, New("dated", ActorA, due), ActorA);

            Assert.True(ok, error);
            var stored = _db.TaskItems.IgnoreQueryFilters().Single(t => t.ID == id).DueDate;
            Assert.NotNull(stored);
            Assert.Equal(due.Date, stored!.Value.Date);
        }

        [Fact]
        public async Task Completion_moves_the_task_to_done_and_fills_progress()
        {
            var (_, _, id) = await _tasks.SaveAsync(CompanyA, New("finish me"), ActorA);

            var (ok, error) = await _tasks.ChangeStatusAsync(CompanyA, id, "Done", ActorA);
            Assert.True(ok, error);

            var row = _db.TaskItems.IgnoreQueryFilters().Single(t => t.ID == id);
            Assert.Equal("Done", row.Status);
            Assert.Equal(100, row.ProgressPct);
            Assert.NotNull(row.CompletedAt);
        }

        [Fact]
        public async Task Completing_another_companys_task_changes_nothing()
        {
            var (_, _, bId) = await _tasksB.SaveAsync(CompanyB, New("b-only", ActorB), ActorB);

            var (ok, _) = await _tasks.ChangeStatusAsync(CompanyA, bId, "Done", ActorA);

            Assert.False(ok);
            var row = _db.TaskItems.IgnoreQueryFilters().Single(t => t.ID == bId);
            Assert.NotEqual("Done", row.Status);
            Assert.Null(row.CompletedAt);
        }

        [Fact]
        public async Task An_unknown_status_is_refused_before_anything_is_written()
        {
            var (_, _, id) = await _tasks.SaveAsync(CompanyA, New("guarded"), ActorA);
            var before = _db.TaskItems.IgnoreQueryFilters().Single(t => t.ID == id).Status;

            var (ok, _) = await _tasks.ChangeStatusAsync(CompanyA, id, "Teleported", ActorA);

            Assert.False(ok);
            Assert.Equal(before, _db.TaskItems.IgnoreQueryFilters().Single(t => t.ID == id).Status);
        }

        // -----------------------------------------------------------------------------------------
        // REPLAY / SOURCE CORRELATION - the ledger Stage 4 depends on
        // -----------------------------------------------------------------------------------------

        [Fact]
        public async Task The_replay_ledger_is_looked_up_by_company_and_rule_key_together()
        {
            var (_, _, aId) = await _tasks.SaveAsync(CompanyA, New("a"), ActorA);
            var (_, _, bId) = await _tasksB.SaveAsync(CompanyB, New("b", ActorB), ActorB);

            // The SAME rule key in two companies. This is the shape that matters: a ledger keyed on the
            // rule alone would treat company B's first delivery as company A's replay and silently skip
            // creating B's task - a cross-tenant loss with no error anywhere.
            const string key = "TaskProjection:ProjectMilestone:77:Created";
            _db.TaskAutoLogs.Add(new TaskAutoLog
            { CompanyId = CompanyA, RuleKey = key, TaskId = aId, CreatedAt = DateTime.UtcNow });
            _db.TaskAutoLogs.Add(new TaskAutoLog
            { CompanyId = CompanyB, RuleKey = key, TaskId = bId, CreatedAt = DateTime.UtcNow });
            await _db.SaveChangesAsync();

            var a = _db.TaskAutoLogs.IgnoreQueryFilters()
                .Single(l => l.CompanyId == CompanyA && l.RuleKey == key);
            var b = _db.TaskAutoLogs.IgnoreQueryFilters()
                .Single(l => l.CompanyId == CompanyB && l.RuleKey == key);

            Assert.NotEqual(a.TaskId, b.TaskId);
            Assert.Equal(aId, a.TaskId);
            Assert.Equal(bId, b.TaskId);
        }

        [Fact]
        public async Task A_rule_key_that_was_never_recorded_finds_nothing_rather_than_the_nearest_row()
        {
            var (_, _, id) = await _tasks.SaveAsync(CompanyA, New("a"), ActorA);
            _db.TaskAutoLogs.Add(new TaskAutoLog
            {
                CompanyId = CompanyA,
                RuleKey = "TaskProjection:ProjectMilestone:77:Created",
                TaskId = id,
                CreatedAt = DateTime.UtcNow,
            });
            await _db.SaveChangesAsync();

            // Ordinal, exact, whole-string. The trailing colon in the prefix form and the numeric segment
            // both matter: a prefix match would make milestone 7 collide with milestone 77.
            foreach (var probe in new[]
            {
                "TaskProjection:ProjectMilestone:7:Created",
                "TaskProjection:ProjectMilestone:77:",
                "taskprojection:projectmilestone:77:created",
                "TaskProjection:ProjectMilestone:770:Created",
            })
                Assert.Null(_db.TaskAutoLogs.IgnoreQueryFilters()
                    .FirstOrDefault(l => l.CompanyId == CompanyA && l.RuleKey == probe));
        }

        [Fact]
        public async Task The_interactive_save_keeps_only_whitelisted_links_which_is_why_the_engine_writes_directly()
        {
            // A SECOND FINDING, RECORDED RATHER THAN REPAIRED. SaveAsync normalises the optional link
            // against a fixed whitelist and SILENTLY DROPS anything else. That list is identical on both
            // lines and contains no ProjectMilestone, so a milestone-linked task saved through the
            // interactive path loses its link without an error.
            //
            // It is not a Stage 4 blocker - the engine sets EntityType/EntityId on the entity directly -
            // but it is the second reason the engine does not go through SaveAsync, and a later stage
            // that decides orchestrated work should be author-editable has to widen the list first.
            var whitelisted = New("linked");
            whitelisted.EntityType = EntityRegistry.Project;
            whitelisted.EntityId = 77;
            var (ok, error, id) = await _tasks.SaveAsync(CompanyA, whitelisted, ActorA);
            Assert.True(ok, error);
            var kept = _db.TaskItems.IgnoreQueryFilters().Single(t => t.ID == id);
            Assert.Equal(EntityRegistry.Project, kept.EntityType);
            Assert.Equal(77, kept.EntityId);

            var milestone = New("milestone-linked");
            milestone.EntityType = EntityRegistry.ProjectMilestone;
            milestone.EntityId = 77;
            var (ok2, error2, id2) = await _tasks.SaveAsync(CompanyA, milestone, ActorA);
            Assert.True(ok2, error2);
            var dropped = _db.TaskItems.IgnoreQueryFilters().Single(t => t.ID == id2);
            Assert.True(string.IsNullOrEmpty(dropped.EntityType));   // silently dropped, not refused
            Assert.Null(dropped.EntityId);

            // The engine path keeps it, because it never passes through the whitelist.
            _db.TaskItems.Add(new TaskItem
            {
                CompanyId = CompanyA, Title = "projected-link", AssigneeEmployeeId = 0,
                CreatedByEmployeeId = 0, Priority = "Normal", Status = TaskStatuses.New,
                ActualHours = 0m, CreatedAt = DateTime.UtcNow,
                EntityType = EntityRegistry.ProjectMilestone, EntityId = 77,
            });
            await _db.SaveChangesAsync();
            var direct = _db.TaskItems.IgnoreQueryFilters().Single(t => t.Title == "projected-link");
            Assert.Equal(EntityRegistry.ProjectMilestone, direct.EntityType);

            // And an unlinked task is still valid - orchestration is not the only way a task is born.
            var (ok3, error3, id3) = await _tasks.SaveAsync(CompanyA, New("unlinked"), ActorA);
            Assert.True(ok3, error3);
            var plain = _db.TaskItems.IgnoreQueryFilters().Single(t => t.ID == id3);
            Assert.True(string.IsNullOrEmpty(plain.EntityType));
        }

        // -----------------------------------------------------------------------------------------
        // FALLBACK AND SCOPE
        // -----------------------------------------------------------------------------------------

        [Theory]
        [InlineData(0)]
        [InlineData(-1)]
        public async Task An_unresolved_company_does_not_fall_back_to_a_real_one(int unresolved)
        {
            await _tasks.SaveAsync(CompanyA, New("a-one"), ActorA);

            // Whatever Task Core does with a non-company, it must not answer with company 1's rows. The
            // assertion is on what comes back, not on the mechanism, so it holds whether the service
            // refuses, returns empty, or writes an orphan.
            Assert.Null(await _tasks.GetAsync(unresolved, 1));

            var leaked = _db.TaskItems.IgnoreQueryFilters().Count(t => t.CompanyId == unresolved);
            Assert.Equal(0, leaked);
        }

        [Fact]
        public void Task_core_names_no_company_literal()
        {
            // The platform-wide prohibition, asserted against the substrate Stage 4 will call into
            // rather than against the whole tree.
            var source = System.IO.File.ReadAllText(
                System.IO.Path.Combine(RepoRoot(), "CrossBuy", "BL", "TaskService.cs"));

            Assert.DoesNotContain("CompanyId = 1", source, StringComparison.Ordinal);
            Assert.DoesNotContain("CompanyId ?? 1", source, StringComparison.Ordinal);
            Assert.DoesNotContain("DefaultCompanyId", source, StringComparison.Ordinal);
        }

        // -----------------------------------------------------------------------------------------
        // THE STAGE BOUNDARY ITSELF
        // -----------------------------------------------------------------------------------------

        [Fact]
        public void The_orchestration_layer_arrived_as_one_unit_in_stage_four()
        {
            // SUPERSEDED BY CONVERGENCE STAGE 4, AND REWRITTEN RATHER THAN DELETED. This test used to
            // assert the orchestration layer was ABSENT, which was the correct Stage 3 boundary: the
            // engine, its consumer and the dispatcher registration had to arrive together or not at
            // all. Stage 4 landed them together, so the assertion now checks what it always meant -
            // that the unit is whole. A partial landing still fails here.
            var assembly = typeof(ITaskService).Assembly;

            Assert.NotNull(assembly.GetType("CrossBuy.BL.Platform.Orchestration.ITaskOrchestrationRule"));
            Assert.NotNull(assembly.GetType("CrossBuy.BL.Platform.Orchestration.ITaskOrchestrationService"));
            Assert.NotNull(assembly.GetType("CrossBuy.BL.Platform.Orchestration.TaskOrchestrationConsumer"));

            // The name and its implementation are registered together - a name without one accumulates
            // dispatch rows nothing drains, which is what the old assertion was protecting against.
            Assert.Contains("TaskOrchestration", BusinessEventConsumers.Registered);
        }

        private static string RepoRoot()
        {
            var dir = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !System.IO.Directory.Exists(System.IO.Path.Combine(dir.FullName, "CrossBuy", "BL")))
                dir = dir.Parent;
            Assert.NotNull(dir);
            return dir!.FullName;
        }
    }
}
