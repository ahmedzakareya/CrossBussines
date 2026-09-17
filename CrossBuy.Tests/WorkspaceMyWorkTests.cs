using CrossBuy.BL;
using CrossBuy.BL.Platform;
using CrossBuy.BL.Workspace;
using CrossBuy.Models.Context.Tasks;
using CrossBuy.Models.Platform;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CrossBuy.Tests
{
    // ============================================================================================
    // WORKSPACE — MY WORK  (TAB 6)
    //
    // THE THREE DEFECTS THESE TESTS HOLD CLOSED, all of them in the Workspace's own request policy:
    //
    //   1. FINISHED WORK OCCUPIED THE PANEL. My Work asked for `status: null` — every task in any state —
    //      then took the first eight ordered by `sort: "due"`. TaskService orders that by
    //      `DueDate ?? DateTime.MaxValue` ASCENDING, so a task closed months ago carrying an early due
    //      date sorts ABOVE live work. The panel that answers "what is still on me" could therefore be
    //      filled with work that is already finished.
    //
    //   2. THE DEEP LINK WAS INVENTED HERE. It emitted "/Tasks/Index?open={id}". TasksController.Index
    //      binds status/priority/q/page/pageSize/view/assignee/sort and has never bound "open", and no
    //      other producer in the product emits that shape. The Tasks module's own producer is
    //      TaskNotificationService.DeepLink, which its notifications and TAB 5's agenda already use.
    //
    //   3. EVERY TASK METRIC POINTED AT THE PAGE THE READER WAS ALREADY ON ("/Workspace/Index#my-work"),
    //      while TaskService.Filter already implements overdue / urgent / open as real list views.
    //
    // WHAT IS NOT TESTED HERE, DELIBERATELY: what "open" or "overdue" MEAN. Those predicates belong to
    // TaskService and are asserted in its own tests. These assert only that the Workspace ASKS the module
    // for them instead of re-deriving them — which is the whole of this tab's ownership.
    // ============================================================================================
    public class WorkspaceMyWorkTests
    {
        private const int Company = 1;
        private const int Employee = 5;

        // ----------------------------------------------------------------------------------------
        // 1 · OPEN WORK ONLY, and the filter is the module's rather than a copy of it.
        // ----------------------------------------------------------------------------------------

        [Fact]
        public async Task My_work_asks_the_tasks_module_for_its_own_open_work_filter()
        {
            var tasks = new RecordingTaskService();

            await Workspace(tasks).GetDashboardAsync();

            var q = Assert.Single(tasks.ListQueries);

            // "inprogress" is TaskService's own predicate for New | InProgress — that is, not Done.
            // Asking for it is what keeps the definition of "open" in ONE place.
            Assert.Equal("inprogress", q.View);

            // A status filter would narrow to a SINGLE status and silently drop the other open one.
            Assert.Null(q.Status);
        }

        [Fact]
        public async Task A_completed_task_never_occupies_a_my_work_slot()
        {
            // The fake applies the module's real predicate, so a Workspace that stopped asking for it
            // would let this Done row through — which is exactly the defect.
            var tasks = new RecordingTaskService()
                .With(1, "closed months ago", "Done", due: DateTime.Today.AddDays(-90))
                .With(2, "still open", "InProgress", due: DateTime.Today.AddDays(1));

            var dashboard = await Workspace(tasks).GetDashboardAsync();

            var item = Assert.Single(dashboard.MyWork.Items);
            Assert.Equal("still open", item.Title);
            Assert.DoesNotContain(dashboard.MyWork.Items, i => i.Status == "Done");
        }

        // The ordering trap that made defect 1 bite: an old Done task sorts ABOVE live work under
        // sort:"due", so "it is only eight rows" was never a defence.
        [Fact]
        public async Task Finished_work_cannot_outrank_live_work_even_with_an_earlier_due_date()
        {
            var tasks = new RecordingTaskService();
            for (int i = 1; i <= 8; i++)
                tasks.With(i, $"done {i}", "Done", due: DateTime.Today.AddDays(-100 + i));
            tasks.With(99, "the live one", "New", due: DateTime.Today.AddDays(3));

            var dashboard = await Workspace(tasks).GetDashboardAsync();

            Assert.Contains(dashboard.MyWork.Items, i => i.Title == "the live one");
            Assert.All(dashboard.MyWork.Items, i => Assert.NotEqual("Done", i.Status));
        }

        // ----------------------------------------------------------------------------------------
        // 2 · AN HONEST TOTAL, counted with the SAME filters the rows were fetched with.
        // ----------------------------------------------------------------------------------------

        [Fact]
        public async Task The_panel_reports_the_full_open_count_not_just_the_rows_it_shows()
        {
            var tasks = new RecordingTaskService();
            for (int i = 1; i <= 25; i++) tasks.With(i, $"open {i}", "New", due: DateTime.Today.AddDays(i));

            var dashboard = await Workspace(tasks).GetDashboardAsync();

            Assert.Equal(8, dashboard.MyWork.Items.Count);   // the panel's cap
            Assert.Equal(25, dashboard.MyWork.Total);        // ...and the truth about how many there are
        }

        // A count taken with different filters would contradict the list it labels.
        [Fact]
        public async Task The_count_uses_the_same_filters_as_the_list()
        {
            var tasks = new RecordingTaskService().With(1, "open", "New");

            await Workspace(tasks).GetDashboardAsync();

            var list = Assert.Single(tasks.ListQueries);
            var count = Assert.Single(tasks.CountQueries);

            Assert.Equal(list.View, count.View);
            Assert.Equal(list.Status, count.Status);
            Assert.Equal(list.Priority, count.Priority);
            Assert.Equal(list.Scope, count.Scope);
            Assert.Equal(list.CompanyId, count.CompanyId);
            Assert.Equal(list.EmployeeId, count.EmployeeId);
            Assert.Equal(list.Assignee, count.Assignee);
        }

        // A failing count must not take the panel down with it — the rows are still real.
        [Fact]
        public async Task A_failing_count_leaves_the_rows_intact()
        {
            var tasks = new RecordingTaskService().With(1, "open", "New");
            tasks.CountThrows = true;

            var dashboard = await Workspace(tasks).GetDashboardAsync();

            Assert.Equal(WorkspacePanelState.Ready, dashboard.MyWork.State);
            Assert.Single(dashboard.MyWork.Items);
        }

        // ----------------------------------------------------------------------------------------
        // 3 · THE MODULE OWNS ITS DEEP LINK.
        // ----------------------------------------------------------------------------------------

        [Fact]
        public async Task A_work_row_links_through_the_tasks_modules_own_deep_link()
        {
            var tasks = new RecordingTaskService().With(4242, "open", "New");

            var dashboard = await Workspace(tasks).GetDashboardAsync();

            var item = Assert.Single(dashboard.MyWork.Items);

            // The shape notifications and the agenda emit, so one task resolves to one URL everywhere.
            Assert.Equal("/Tasks/Index?taskId=4242", item.Url);

            // The invented shape must not come back: nothing else in the product answers it.
            Assert.DoesNotContain("open=", item.Url);
        }

        // ----------------------------------------------------------------------------------------
        // 4 · TILES LEAD SOMEWHERE. Each metric hands the reader to the module's own answer.
        // ----------------------------------------------------------------------------------------

        [Fact]
        public async Task Task_metrics_link_into_the_tasks_module_not_back_to_the_workspace()
        {
            var tasks = new RecordingTaskService().With(1, "open", "New");

            var dashboard = await Workspace(tasks).GetDashboardAsync();

            var taskTiles = dashboard.Metrics
                .Where(m => m.Url != null && m.Url.StartsWith("/Tasks/", StringComparison.Ordinal))
                .ToList();

            Assert.Equal(3, taskTiles.Count);
            Assert.Contains(taskTiles, m => m.Url == "/Tasks/Index?view=overdue");
            Assert.Contains(taskTiles, m => m.Url == "/Tasks/Index?view=urgent");
            Assert.Contains(taskTiles, m => m.Url == "/Tasks/Index?view=inprogress");

            // The old behaviour: every tile scrolled the reader down the page they were already reading.
            Assert.DoesNotContain(dashboard.Metrics, m => m.Url == "/Workspace/Index#my-work");
        }

        // The notification tile is NOT a task tile and keeps its own destination.
        [Fact]
        public async Task The_notification_tile_still_points_at_the_workspace_notifications_screen()
        {
            var dashboard = await Workspace(new RecordingTaskService()).GetDashboardAsync();

            Assert.Contains(dashboard.Metrics, m => m.Url == "/Workspace/Notifications");
        }

        // ----------------------------------------------------------------------------------------
        // 5 · ISOLATION. The Workspace is an aggregation surface, so this is where a leak would show.
        // ----------------------------------------------------------------------------------------

        [Fact]
        public async Task Every_task_query_carries_the_resolved_company_and_employee_and_widens_no_scope()
        {
            var tasks = new RecordingTaskService().With(1, "open", "New");

            await Workspace(tasks).GetDashboardAsync();

            Assert.NotEmpty(tasks.ListQueries);

            foreach (var q in tasks.ListQueries.Concat(tasks.CountQueries))
            {
                Assert.Equal(Company, q.CompanyId);
                Assert.Equal(Employee, q.EmployeeId);

                // "mine" is what keeps this a personal panel; "all" would make it everybody's work.
                Assert.Equal("mine", q.Scope);

                // An assignee would override the caller and read another person's list.
                Assert.Null(q.Assignee);
            }
        }

        [Fact]
        public async Task A_session_with_no_employee_asks_the_tasks_module_nothing()
        {
            var tasks = new RecordingTaskService().With(1, "open", "New");
            var workspace = Workspace(tasks,
                new FixedContext(new BusinessContext { CompanyId = Company, EmployeeId = null }));

            var dashboard = await workspace.GetDashboardAsync();

            Assert.Equal(WorkspacePanelState.AccessDenied, dashboard.MyWork.State);
            Assert.Empty(tasks.ListQueries);      // fail closed: nothing was even asked
            Assert.Empty(tasks.CountQueries);
        }

        [Fact]
        public async Task An_unresolved_company_renders_no_workspace_at_all()
        {
            var tasks = new RecordingTaskService().With(1, "open", "New");

            var dashboard = await Workspace(tasks, new FixedContext(null)).GetDashboardAsync();

            Assert.True(dashboard.IsUnresolved);
            Assert.Empty(tasks.ListQueries);
        }

        // ----------------------------------------------------------------------------------------
        // 6 · THE PANEL STATES SURVIVE.
        // ----------------------------------------------------------------------------------------

        [Fact]
        public async Task A_deployment_without_the_tasks_module_reports_unavailable_not_empty()
        {
            var workspace = new WorkspaceService(
                new FixedContext(Resolved()), new ServiceCollection().BuildServiceProvider(),
                new NoNotifications(), new NoIdentity(),
                Array.Empty<IWorkspaceFavoritesSource>(), Array.Empty<IWorkspaceActivitySource>(),
                Array.Empty<IWorkspaceReportSource>(), NullLogger<WorkspaceService>.Instance);

            var dashboard = await workspace.GetDashboardAsync();

            Assert.Equal(WorkspacePanelState.Unavailable, dashboard.MyWork.State);
            Assert.False(string.IsNullOrWhiteSpace(dashboard.MyWork.Reason));
        }

        [Fact]
        public async Task A_person_with_nothing_open_gets_an_empty_panel_not_a_failure()
        {
            var dashboard = await Workspace(new RecordingTaskService()).GetDashboardAsync();

            Assert.Equal(WorkspacePanelState.Empty, dashboard.MyWork.State);
            Assert.Empty(dashboard.MyWork.Items);
        }

        [Fact]
        public async Task A_throwing_tasks_module_is_a_temporary_failure_not_an_empty_panel()
        {
            var tasks = new RecordingTaskService { ListThrows = true };

            var dashboard = await Workspace(tasks).GetDashboardAsync();

            Assert.Equal(WorkspacePanelState.TemporaryFailure, dashboard.MyWork.State);
            Assert.Empty(dashboard.MyWork.Items);
        }

        // ========================================================================================
        // harness
        // ========================================================================================

        private static BusinessContext Resolved() => new() { CompanyId = Company, EmployeeId = Employee };

        private static WorkspaceService Workspace(ITaskService tasks, IBusinessContextAccessor? contexts = null)
        {
            var services = new ServiceCollection();
            services.AddSingleton(tasks);

            return new WorkspaceService(
                contexts ?? new FixedContext(Resolved()),
                services.BuildServiceProvider(),
                new NoNotifications(),
                new NoIdentity(),
                Array.Empty<IWorkspaceFavoritesSource>(),
                Array.Empty<IWorkspaceActivitySource>(),
                Array.Empty<IWorkspaceReportSource>(),
                NullLogger<WorkspaceService>.Instance);
        }

        private sealed record Query(int CompanyId, string Scope, int EmployeeId, string? Status,
                                    string? Priority, string? View, int? Assignee, string? Sort, int PageSize);

        // Records what the Workspace ASKED for, and answers by applying the Tasks module's real
        // predicates. A stand-in for the contract, not a copy of TaskService: it holds no authorization
        // rule, no company query of its own, and no paging arithmetic beyond take.
        private sealed class RecordingTaskService : ITaskService
        {
            private readonly List<TaskRowDto> _rows = new();

            public List<Query> ListQueries { get; } = new();
            public List<Query> CountQueries { get; } = new();
            public bool ListThrows { get; set; }
            public bool CountThrows { get; set; }

            public RecordingTaskService With(int id, string title, string status, DateTime? due = null,
                                             string priority = "Normal")
            {
                _rows.Add(new TaskRowDto
                {
                    Id = id,
                    Title = title,
                    Status = status,
                    Priority = priority,
                    DueDate = due,
                    AssigneeEmployeeId = Employee,
                    AssigneeName = "tester",
                });
                return this;
            }

            // TaskService.Filter's own `view` predicates, mirrored here so that a Workspace which stops
            // asking for one is visibly wrong rather than accidentally right.
            private IEnumerable<TaskRowDto> Apply(string? view) => view switch
            {
                "inprogress" => _rows.Where(r => r.Status is "InProgress" or "New"),
                "overdue" => _rows.Where(r => r.DueDate < DateTime.Now && r.Status != "Done"),
                "urgent" => _rows.Where(r => r.Priority == "Urgent" && r.Status != "Done"),
                "done" => _rows.Where(r => r.Status == "Done"),
                _ => _rows,
            };

            // sort:"due" is ORDER BY (DueDate ?? MaxValue) ASC — the ordering that let finished work win.
            private static IEnumerable<TaskRowDto> Sort(IEnumerable<TaskRowDto> rows, string? sort) =>
                sort == "due" ? rows.OrderBy(r => r.DueDate ?? DateTime.MaxValue) : rows;

            public Task<List<TaskRowDto>> GetTasksAsync(int companyId, string scope, int currentEmployeeId,
                string? status, string? priority, string? q, int page = 1, int pageSize = 25,
                string? view = null, int? assignee = null, string? sort = null)
            {
                ListQueries.Add(new Query(companyId, scope, currentEmployeeId, status, priority, view,
                                          assignee, sort, pageSize));
                if (ListThrows) throw new InvalidOperationException("the tasks module is unreachable");
                return Task.FromResult(Sort(Apply(view), sort).Take(pageSize).ToList());
            }

            public Task<int> CountTasksAsync(int companyId, string scope, int currentEmployeeId,
                string? status, string? priority, string? q, string? view = null, int? assignee = null)
            {
                CountQueries.Add(new Query(companyId, scope, currentEmployeeId, status, priority, view,
                                           assignee, null, 0));
                if (CountThrows) throw new InvalidOperationException("count unavailable");
                return Task.FromResult(Apply(view).Count());
            }

            public Task<TaskKpiDto> GetKpisAsync(int companyId, string scope, int currentEmployeeId) =>
                Task.FromResult(new TaskKpiDto
                {
                    Total = _rows.Count,
                    Done = _rows.Count(r => r.Status == "Done"),
                    InProgress = _rows.Count(r => r.Status is "InProgress" or "New"),
                    Overdue = _rows.Count(r => r.DueDate < DateTime.Now && r.Status != "Done"),
                    Urgent = _rows.Count(r => r.Priority == "Urgent" && r.Status != "Done"),
                });

            // Not part of this surface. The Workspace holds no write path and never reaches these.
            public Task<TaskItem?> GetAsync(int companyId, int id) => Task.FromResult<TaskItem?>(null);

            public Task<(bool ok, string? error, int id)> SaveAsync(
                int companyId, TaskSaveInput input, int currentEmployeeId) =>
                throw new NotSupportedException("the Workspace has no write path");

            public Task<(bool ok, string? error)> ChangeStatusAsync(
                int companyId, int id, string status, int currentEmployeeId) =>
                throw new NotSupportedException("the Workspace has no write path");

            public Task<(bool ok, string? error)> DeleteAsync(int companyId, int id) =>
                throw new NotSupportedException("the Workspace has no write path");

            public Task<List<(int Id, string Name)>> ActiveEmployeesAsync(int companyId) =>
                Task.FromResult(new List<(int, string)>());
        }

        private sealed class FixedContext : IBusinessContextAccessor
        {
            private readonly BusinessContext? _context;
            public FixedContext(BusinessContext? context) { _context = context; }

            public Task<BusinessContext> GetCurrentAsync(CancellationToken cancellationToken = default) =>
                _context is null
                    ? throw new BusinessContextUnresolvedException("no context in this test")
                    : Task.FromResult(_context);

            public Task<BusinessContext?> TryGetCurrentAsync(CancellationToken cancellationToken = default) =>
                Task.FromResult(_context);
        }

        private sealed class NoNotifications : IWorkspaceNotificationSource
        {
            public bool IsAvailable => false;

            public Task<IReadOnlyList<WorkspaceNotification>> GetAsync(BusinessContext context,
                bool unreadOnly, int take, CancellationToken cancellationToken = default) =>
                Task.FromResult<IReadOnlyList<WorkspaceNotification>>(Array.Empty<WorkspaceNotification>());

            public Task<int> CountUnreadAsync(BusinessContext context,
                CancellationToken cancellationToken = default) => Task.FromResult(0);            
            // Added when IWorkspaceNotificationSource grew paging. The double still means the same thing it
            // always did - this caller has NO notifications - so a page of them is empty and the total is
            // zero. Returning anything else would make a "no notifications" fixture assert against them.
            public Task<IReadOnlyList<WorkspaceNotification>> GetPageAsync(BusinessContext context,
                bool unreadOnly, int skip, int take, CancellationToken cancellationToken = default) =>
                Task.FromResult<IReadOnlyList<WorkspaceNotification>>(Array.Empty<WorkspaceNotification>());
            
            public Task<int> CountAsync(BusinessContext context, bool unreadOnly,
                CancellationToken cancellationToken = default) => Task.FromResult(0);
        }

        private sealed class NoIdentity : IWorkspaceIdentityResolver
        {
            public Task<(string EmployeeName, string? CompanyName)> ResolveAsync(
                BusinessContext context, CancellationToken cancellationToken = default) =>
                Task.FromResult((string.Empty, (string?)null));
        }
    }
}
