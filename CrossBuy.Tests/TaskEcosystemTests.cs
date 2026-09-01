using CrossBuy.BL;
using CrossBuy.BL.Platform;
using CrossBuy.BL.TasksCalendar;
using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Admin;
using CrossBuy.Models.Context.Tasks;
using CrossBuy.Models.Platform;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CrossBuy.Tests
{
    // ==========================================================================================
    // TASK ECOSYSTEM — checklist, dependencies, templates
    //
    // The dependency tests carry the weight. A dependency graph with a cycle is not slightly wrong:
    // every task in the loop blocks every other, so nothing in it can start and no screen can explain
    // why. The cycle check is the feature; these tests are what make it a guard.
    // ==========================================================================================
    internal sealed class EcosystemFixture : IDisposable
    {
        private readonly SqliteConnection _conn;
        private readonly CompanyScopeHolder _holder = new();

        public CrossDbContext Db { get; }
        public ITaskService Tasks { get; }
        public ITaskChecklistService Checklist { get; }
        public ITaskDependencyService Dependencies { get; }
        public ITaskTemplateService Templates { get; }

        public EcosystemFixture(int companyId = 1)
        {
            _conn = new SqliteConnection("DataSource=:memory:");
            _conn.Open();
            _holder.Set(companyId, null);

            Db = NewContext();
            Db.Database.EnsureCreated();
            using (var cmd = _conn.CreateCommand())
            {
                cmd.CommandText = "PRAGMA foreign_keys = OFF;";
                cmd.ExecuteNonQuery();
            }

            var notifier = new RecordingNotificationService(Db);
            var notify = new TaskNotificationService(Db, notifier);
            var events = new BusinessEventService(Db, new EntityRegistry(Db),
                new StubContextAccessor(BusinessContext.ForSystem(companyId)),
                NullLogger<BusinessEventService>.Instance);

            Tasks = new TaskService(Db, new TaskCalendarEventPublisher(events), notify);
            Checklist = new TaskChecklistService(Db);
            Dependencies = new TaskDependencyService(Db);
            Templates = new TaskTemplateService(Db, Tasks, Checklist, Dependencies);
        }

        public CrossDbContext NewContext()
        {
            var o = new DbContextOptionsBuilder<CrossDbContext>()
                .UseSqlite(_conn)
                .AddInterceptors(new CompanyWriteGuardInterceptor(NullLogger<CompanyWriteGuardInterceptor>.Instance))
                .Options;
            return new CrossDbContext(o, _holder);
        }

        public async Task SeedEmployeeAsync(int id, int companyId = 1)
        {
            Db.Employee.Add(new Employee
            {
                ID = id, EmpCompanyID = companyId, IsActive = true,
                FirstName = $"Emp{id}", LastName = "T", FullName = $"Emp {id}",
                Address = "-", PhoneNumber = "-", Email = $"e{id}@t.local",
                ProfileImage = "-", Gender = "-", MaritalStatus = "-", UserId = $"u{id}"
            });
            await Db.SaveChangesAsync();
        }

        /// A task created directly, bypassing ITaskService — these tests are about the ecosystem
        /// services, and driving every fixture task through the full create path would make them slow
        /// and would couple them to transition behaviour they are not testing.
        public async Task<int> SeedTaskAsync(string title, int companyId = 1, string status = "New")
        {
            var t = new TaskItem
            {
                CompanyId = companyId, Title = title, AssigneeEmployeeId = 10,
                CreatedByEmployeeId = 20, Status = status, Priority = "Normal",
                CreatedAt = DateTime.UtcNow
            };
            Db.TaskItems.Add(t);
            await Db.SaveChangesAsync();
            return t.ID;
        }

        public void Dispose() { Db.Dispose(); _conn.Dispose(); }
    }

    // ==========================================================================================
    public class TaskDependencyTests
    {
        [Fact]
        public async Task A_dependency_can_be_added_and_blocks_its_successor_until_the_predecessor_is_done()
        {
            using var f = new EcosystemFixture();
            int a = await f.SeedTaskAsync("Pour foundation");
            int b = await f.SeedTaskAsync("Erect columns");

            var (ok, err, _) = await f.Dependencies.AddAsync(1, a, b, TaskDependencyKinds.FinishToStart, 0, 20);
            Assert.True(ok, err);

            var blocked = await f.Dependencies.BlockingStateAsync(1, b);
            Assert.True(blocked.IsBlocked);
            Assert.Equal(new[] { a }, blocked.BlockedByTaskIds);
            Assert.Contains("Pour foundation", blocked.BlockedByTitles);

            // The predecessor itself is not blocked — the edge only runs one way.
            Assert.False((await f.Dependencies.BlockingStateAsync(1, a)).IsBlocked);
        }

        [Fact]
        public async Task Completing_the_predecessor_unblocks_the_successor()
        {
            using var f = new EcosystemFixture();
            int a = await f.SeedTaskAsync("A");
            int b = await f.SeedTaskAsync("B");
            await f.Dependencies.AddAsync(1, a, b, TaskDependencyKinds.FinishToStart, 0, 20);
            Assert.True((await f.Dependencies.BlockingStateAsync(1, b)).IsBlocked);

            var task = await f.Db.TaskItems.FirstAsync(t => t.ID == a);
            task.Status = "Done";
            await f.Db.SaveChangesAsync();

            Assert.False((await f.Dependencies.BlockingStateAsync(1, b)).IsBlocked);
        }

        [Fact]
        public async Task A_task_cannot_depend_on_itself()
        {
            using var f = new EcosystemFixture();
            int a = await f.SeedTaskAsync("A");

            var (ok, err, _) = await f.Dependencies.AddAsync(1, a, a, TaskDependencyKinds.FinishToStart, 0, 20);

            Assert.False(ok);
            Assert.Contains("itself", err!, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(0, await f.Db.TaskDependencies.CountAsync());
        }

        [Fact]
        public async Task A_two_node_cycle_is_refused()
        {
            using var f = new EcosystemFixture();
            int a = await f.SeedTaskAsync("A");
            int b = await f.SeedTaskAsync("B");
            await f.Dependencies.AddAsync(1, a, b, TaskDependencyKinds.FinishToStart, 0, 20);   // a -> b

            var (ok, err, _) = await f.Dependencies.AddAsync(1, b, a, TaskDependencyKinds.FinishToStart, 0, 20); // b -> a

            Assert.False(ok);
            Assert.Contains("cycle", err!, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(1, await f.Db.TaskDependencies.CountAsync());   // the first edge survives
        }

        [Fact]
        public async Task A_long_cycle_is_refused_and_the_offending_chain_is_named()
        {
            using var f = new EcosystemFixture();
            int a = await f.SeedTaskAsync("A");
            int b = await f.SeedTaskAsync("B");
            int c = await f.SeedTaskAsync("C");
            int d = await f.SeedTaskAsync("D");

            await f.Dependencies.AddAsync(1, a, b, TaskDependencyKinds.FinishToStart, 0, 20);
            await f.Dependencies.AddAsync(1, b, c, TaskDependencyKinds.FinishToStart, 0, 20);
            await f.Dependencies.AddAsync(1, c, d, TaskDependencyKinds.FinishToStart, 0, 20);

            // d -> a would close a -> b -> c -> d -> a
            var (wouldCycle, path) = await f.Dependencies.WouldCreateCycleAsync(1, d, a);
            Assert.True(wouldCycle);
            // The user needs to see WHICH chain closes, not merely that one does.
            Assert.Contains(a, path);
            Assert.Contains(d, path);

            var (ok, err, _) = await f.Dependencies.AddAsync(1, d, a, TaskDependencyKinds.FinishToStart, 0, 20);
            Assert.False(ok);
            Assert.Contains("cycle", err!, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(3, await f.Db.TaskDependencies.CountAsync());
        }

        [Fact]
        public async Task A_diamond_is_allowed_because_it_is_not_a_cycle()
        {
            using var f = new EcosystemFixture();
            int a = await f.SeedTaskAsync("A");
            int b = await f.SeedTaskAsync("B");
            int c = await f.SeedTaskAsync("C");
            int d = await f.SeedTaskAsync("D");

            // a -> b, a -> c, b -> d, c -> d. Two paths converge; nothing loops.
            Assert.True((await f.Dependencies.AddAsync(1, a, b, TaskDependencyKinds.FinishToStart, 0, 20)).ok);
            Assert.True((await f.Dependencies.AddAsync(1, a, c, TaskDependencyKinds.FinishToStart, 0, 20)).ok);
            Assert.True((await f.Dependencies.AddAsync(1, b, d, TaskDependencyKinds.FinishToStart, 0, 20)).ok);
            var (ok, err, _) = await f.Dependencies.AddAsync(1, c, d, TaskDependencyKinds.FinishToStart, 0, 20);

            Assert.True(ok, err);
            Assert.Equal(4, await f.Db.TaskDependencies.CountAsync());

            // d waits for BOTH of its predecessors.
            var blocked = await f.Dependencies.BlockingStateAsync(1, d);
            Assert.Equal(2, blocked.BlockedByTaskIds.Count);
        }

        [Fact]
        public async Task The_same_edge_cannot_be_added_twice()
        {
            using var f = new EcosystemFixture();
            int a = await f.SeedTaskAsync("A");
            int b = await f.SeedTaskAsync("B");
            await f.Dependencies.AddAsync(1, a, b, TaskDependencyKinds.FinishToStart, 0, 20);

            var (ok, err, _) = await f.Dependencies.AddAsync(1, a, b, TaskDependencyKinds.FinishToStart, 0, 20);

            Assert.False(ok);
            Assert.Contains("already exists", err!, StringComparison.OrdinalIgnoreCase);
            // A duplicate edge is not a second dependency — it would double-count in the blocked list.
            Assert.Equal(1, await f.Db.TaskDependencies.CountAsync());
        }

        [Fact]
        public async Task An_edge_cannot_cross_a_company_boundary()
        {
            using var f = new EcosystemFixture();
            int mine = await f.SeedTaskAsync("Mine", companyId: 1);
            int theirs = await f.SeedTaskAsync("Theirs", companyId: 2);

            var (ok, err, _) = await f.Dependencies.AddAsync(1, mine, theirs, TaskDependencyKinds.FinishToStart, 0, 20);

            Assert.False(ok);
            Assert.Contains("not found", err!, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(0, await f.Db.TaskDependencies.CountAsync());
        }

        [Fact]
        public async Task An_unresolved_company_writes_nothing()
        {
            using var f = new EcosystemFixture();
            int a = await f.SeedTaskAsync("A");
            int b = await f.SeedTaskAsync("B");

            var (ok, _, _) = await f.Dependencies.AddAsync(0, a, b, TaskDependencyKinds.FinishToStart, 0, 20);

            Assert.False(ok);
            Assert.Equal(0, await f.Db.TaskDependencies.CountAsync());
        }

        [Fact]
        public async Task Removing_the_edge_unblocks_the_successor()
        {
            using var f = new EcosystemFixture();
            int a = await f.SeedTaskAsync("A");
            int b = await f.SeedTaskAsync("B");
            var (_, _, id) = await f.Dependencies.AddAsync(1, a, b, TaskDependencyKinds.FinishToStart, 0, 20);
            Assert.True((await f.Dependencies.BlockingStateAsync(1, b)).IsBlocked);

            var (ok, err) = await f.Dependencies.RemoveAsync(1, id);

            Assert.True(ok, err);
            Assert.False((await f.Dependencies.BlockingStateAsync(1, b)).IsBlocked);
        }

        [Fact]
        public async Task Blocking_state_for_a_board_is_resolved_in_one_pass()
        {
            using var f = new EcosystemFixture();
            int a = await f.SeedTaskAsync("A");
            int b = await f.SeedTaskAsync("B");
            int c = await f.SeedTaskAsync("C");
            await f.Dependencies.AddAsync(1, a, b, TaskDependencyKinds.FinishToStart, 0, 20);

            var many = await f.Dependencies.BlockingStateManyAsync(1, new[] { a, b, c });

            Assert.Equal(3, many.Count);
            Assert.False(many[a].IsBlocked);
            Assert.True(many[b].IsBlocked);
            Assert.False(many[c].IsBlocked);   // a task with no edges is never blocked
        }

        [Fact]
        public async Task Only_finish_to_start_gates_today_and_the_others_are_recorded_without_blocking()
        {
            using var f = new EcosystemFixture();
            int a = await f.SeedTaskAsync("A");
            int b = await f.SeedTaskAsync("B");

            var (ok, err, _) = await f.Dependencies.AddAsync(1, a, b, TaskDependencyKinds.StartToStart, 0, 20);
            Assert.True(ok, err);

            // The edge exists and is visible…
            var edges = await f.Dependencies.ForTaskAsync(1, b);
            Assert.Single(edges);
            Assert.Equal(TaskDependencyKinds.StartToStart, edges[0].Kind);
            // …but it does not gate, and the view says so rather than leaving it to be assumed.
            Assert.False(edges[0].IsBlocking);
            Assert.False((await f.Dependencies.BlockingStateAsync(1, b)).IsBlocked);
        }
    }

    // ==========================================================================================
    public class TaskChecklistTests
    {
        [Fact]
        public async Task Lines_are_added_in_order_and_progress_is_derived()
        {
            using var f = new EcosystemFixture();
            int t = await f.SeedTaskAsync("Inspect");

            await f.Checklist.AddAsync(1, t, "Check rebar", 20);
            var (_, _, second) = await f.Checklist.AddAsync(1, t, "Check formwork", 20);
            await f.Checklist.AddAsync(1, t, "Photograph", 20);

            await f.Checklist.SetDoneAsync(1, second, true, 20);

            var progress = (await f.Checklist.ProgressManyAsync(1, new[] { t }))[t];
            Assert.Equal(3, progress.Total);
            Assert.Equal(1, progress.Done);
            Assert.Equal(33, progress.Percent);
        }

        [Fact]
        public async Task Checklist_completion_does_not_touch_the_tasks_own_progress_column()
        {
            using var f = new EcosystemFixture();
            int t = await f.SeedTaskAsync("Inspect");
            var (_, _, line) = await f.Checklist.AddAsync(1, t, "Only line", 20);

            await f.Checklist.SetDoneAsync(1, line, true, 20);

            // ProgressPct has always been a human's judgement. Recomputing it here would silently change
            // what an existing column means.
            await using var verify = f.NewContext();
            Assert.Equal(0, (await verify.TaskItems.FirstAsync(x => x.ID == t)).ProgressPct);
        }

        [Fact]
        public async Task Marking_done_twice_writes_nothing_the_second_time()
        {
            using var f = new EcosystemFixture();
            int t = await f.SeedTaskAsync("T");
            var (_, _, line) = await f.Checklist.AddAsync(1, t, "L", 20);

            await f.Checklist.SetDoneAsync(1, line, true, 20);
            var first = (await f.Checklist.ForTaskAsync(1, t)).Single().DoneAt;
            await f.Checklist.SetDoneAsync(1, line, true, 99);   // a different actor, same state

            var after = (await f.Checklist.ForTaskAsync(1, t)).Single();
            Assert.Equal(first, after.DoneAt);          // untouched
            Assert.Equal(20, after.DoneByEmployeeId);    // the original completer, not the retrier
        }

        [Fact]
        public async Task Reordering_keeps_line_identity_and_its_done_state()
        {
            using var f = new EcosystemFixture();
            int t = await f.SeedTaskAsync("T");
            var (_, _, a) = await f.Checklist.AddAsync(1, t, "A", 20);
            var (_, _, b) = await f.Checklist.AddAsync(1, t, "B", 20);
            var (_, _, c) = await f.Checklist.AddAsync(1, t, "C", 20);
            await f.Checklist.SetDoneAsync(1, b, true, 20);

            var (ok, err) = await f.Checklist.ReorderAsync(1, t, new[] { c, b, a });

            Assert.True(ok, err);
            var lines = await f.Checklist.ForTaskAsync(1, t);
            Assert.Equal(new[] { c, b, a }, lines.Select(l => l.ID).ToArray());
            // Reorder is a DIFFERENCE, not delete-and-reinsert: B is still done, by the same person.
            Assert.True(lines.Single(l => l.ID == b).IsDone);
            Assert.Equal(20, lines.Single(l => l.ID == b).DoneByEmployeeId);
        }

        [Fact]
        public async Task A_line_cannot_be_added_to_another_companys_task()
        {
            using var f = new EcosystemFixture();
            int theirs = await f.SeedTaskAsync("Theirs", companyId: 2);

            var (ok, _, _) = await f.Checklist.AddAsync(1, theirs, "Sneak", 20);

            Assert.False(ok);
            Assert.Equal(0, await f.Db.TaskChecklistItems.CountAsync());
        }

        [Fact]
        public async Task A_reorder_naming_a_foreign_line_is_refused()
        {
            using var f = new EcosystemFixture();
            int t1 = await f.SeedTaskAsync("T1");
            int t2 = await f.SeedTaskAsync("T2");
            var (_, _, mine) = await f.Checklist.AddAsync(1, t1, "Mine", 20);
            var (_, _, other) = await f.Checklist.AddAsync(1, t2, "Other", 20);

            var (ok, err) = await f.Checklist.ReorderAsync(1, t1, new[] { mine, other });

            Assert.False(ok);
            Assert.Contains("not on this task", err!, StringComparison.OrdinalIgnoreCase);
        }
    }

    // ==========================================================================================
    public class TaskTemplateTests
    {
        private static TaskTemplate Sample() => new()
        {
            Name = "Site handover",
            IsActive = true,
            Items =
            {
                new TaskTemplateItem { Title = "Snag walk",       SortOrder = 1, DueOffsetDays = 0, Priority = "High",   ChecklistLines = "Rooms\nExternals" },
                new TaskTemplateItem { Title = "Fix snags",       SortOrder = 2, DueOffsetDays = 3, Priority = "Normal", PredecessorSortOrder = 1 },
                new TaskTemplateItem { Title = "Client sign-off", SortOrder = 3, DueOffsetDays = 7, Priority = "Urgent", PredecessorSortOrder = 2 },
            }
        };

        [Fact]
        public async Task Applying_a_template_creates_tasks_checklists_and_dependencies()
        {
            using var f = new EcosystemFixture();
            await f.SeedEmployeeAsync(10);
            await f.SeedEmployeeAsync(20);
            var (saved, err, id) = await f.Templates.SaveAsync(1, Sample(), 20);
            Assert.True(saved, err);

            var anchor = new DateTime(2026, 9, 1);
            var (ok, aerr, result) = await f.Templates.ApplyAsync(1, id, anchor, assigneeOverride: 10, currentEmployeeId: 20);

            Assert.True(ok, aerr);
            Assert.Equal(3, result!.CreatedTaskIds.Count);
            Assert.Equal(2, result.ChecklistLinesCreated);
            Assert.Equal(2, result.DependenciesCreated);

            await using var verify = f.NewContext();
            var created = await verify.TaskItems.Where(t => result.CreatedTaskIds.Contains(t.ID))
                .OrderBy(t => t.ID).ToListAsync();

            // Relative offsets became real dates from the anchor.
            Assert.Equal(new DateTime(2026, 9, 1), created[0].DueDate!.Value.Date);
            Assert.Equal(new DateTime(2026, 9, 4), created[1].DueDate!.Value.Date);
            Assert.Equal(new DateTime(2026, 9, 8), created[2].DueDate!.Value.Date);
            Assert.All(created, t => Assert.Equal(10, t.AssigneeEmployeeId));

            // The second task waits for the first.
            var blocked = await f.Dependencies.BlockingStateAsync(1, created[1].ID);
            Assert.True(blocked.IsBlocked);
        }

        [Fact]
        public async Task A_templated_task_is_created_through_the_normal_task_path()
        {
            using var f = new EcosystemFixture();
            await f.SeedEmployeeAsync(10);
            await f.SeedEmployeeAsync(20);
            var (_, _, id) = await f.Templates.SaveAsync(1, Sample(), 20);

            var (ok, _, result) = await f.Templates.ApplyAsync(1, id, new DateTime(2026, 9, 1), 10, 20);
            Assert.True(ok);

            // Because the applier goes through ITaskService, every templated task raises the same events
            // a hand-made task does. There is no second task-creation path to keep in step.
            await using var verify = f.NewContext();
            foreach (var taskId in result!.CreatedTaskIds)
                Assert.True(await verify.BusinessEvents.AnyAsync(e => e.EntityType == "Task" && e.EntityId == taskId
                                                                      && e.EventType == "Task.Created"));
        }

        [Fact]
        public async Task A_template_item_cannot_depend_on_itself()
        {
            using var f = new EcosystemFixture();
            var t = Sample();
            t.Items[0].PredecessorSortOrder = 1;   // item 1 points at item 1

            var (ok, err, _) = await f.Templates.SaveAsync(1, t, 20);

            Assert.False(ok);
            Assert.Contains("itself", err!, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public async Task A_template_item_cannot_name_a_predecessor_that_is_not_in_the_template()
        {
            using var f = new EcosystemFixture();
            var t = Sample();
            t.Items[1].PredecessorSortOrder = 99;

            var (ok, err, _) = await f.Templates.SaveAsync(1, t, 20);

            // Caught at SAVE time. Leaving it would surface as a failed edge at apply time, when the
            // user is trying to do something else entirely.
            Assert.False(ok);
            Assert.Contains("predecessor", err!, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public async Task An_empty_or_unnamed_template_is_refused()
        {
            using var f = new EcosystemFixture();

            var noName = Sample(); noName.Name = "  ";
            Assert.False((await f.Templates.SaveAsync(1, noName, 20)).ok);

            var noItems = new TaskTemplate { Name = "Empty", IsActive = true };
            Assert.False((await f.Templates.SaveAsync(1, noItems, 20)).ok);
        }

        [Fact]
        public async Task An_inactive_template_cannot_be_applied()
        {
            using var f = new EcosystemFixture();
            await f.SeedEmployeeAsync(10);
            await f.SeedEmployeeAsync(20);
            var (_, _, id) = await f.Templates.SaveAsync(1, Sample(), 20);
            await f.Templates.SetActiveAsync(1, id, false);

            var (ok, err, _) = await f.Templates.ApplyAsync(1, id, DateTime.Today, 10, 20);

            Assert.False(ok);
            Assert.Contains("not active", err!, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public async Task A_template_from_another_company_is_invisible()
        {
            using var f = new EcosystemFixture();
            var (_, _, id) = await f.Templates.SaveAsync(1, Sample(), 20);

            Assert.Null(await f.Templates.GetAsync(2, id));
            Assert.False((await f.Templates.ApplyAsync(2, id, DateTime.Today, 10, 20)).ok);
        }
    }
}
