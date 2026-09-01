using CrossBuy.BL;
using CrossBuy.BL.TasksCalendar;
using CrossBuy.Models.Context.Tasks;
using Xunit;

namespace CrossBuy.Tests
{
    // ==========================================================================================
    // WORKSPACE AGENDA — UAT DEFECT CLOSURE (paging + overdue)
    //
    // TWO DEFECTS FOUND BY THE POPULATED UAT DATASET, both silent, both invisible on a small database.
    //
    // DEFECT 1 — the agenda dropped tasks. It asked ITaskService for `MaxPageSize * 4` = 800 rows, and
    // GetTasksAsync contains `if (pageSize < 1 || pageSize > 200) pageSize = 25`. 800 > 200, so every
    // request was rewritten to 25 — with no exception and no signal. Worse, the service's default sort is
    // `CreatedAt DESC`, not due date, so the 25 rows that came back were the most recently CREATED tasks
    // and had no relationship to the agenda window at all. UAT employee 5 had 18 tasks due inside seven
    // days; the agenda showed 4.
    //
    // THE FAKE IN THIS FILE REPRODUCES THAT CLAMP ON PURPOSE (see PagingTaskService). A fake that honoured
    // pageSize = 800 would make these tests pass against the OLD code too, and prove nothing. The clamp is
    // the defect; a test that does not model it is not a test of the fix.
    //
    // DEFECT 2 — overdue work could never appear. The window opened at `today`, so anything already late
    // was outside the requested range. The one screen a person opens to find late work was the one place
    // it could not be.
    //
    // Both fixes are BOUNDED: paging stops at MaxTaskPages, overdue reaches back a clamped number of days
    // and is capped by count, and every truncation is reported through DegradedSources rather than
    // silently swallowed — an agenda missing rows without saying so is the failure mode being closed.
    // ==========================================================================================
    public class WorkspaceAgendaPagingAndOverdueTests
    {
        private const string Zone = "UTC";
        private const int Company = 1;
        private const int Me = 10;

        // The real service's honoured maximum. Anything above it is silently rewritten to 25.
        private const int RealTaskServiceMaxPageSize = 200;

        // ----------------------------------------------------------------------------------------
        // DEFECT 1 — aggregation across pages
        // ----------------------------------------------------------------------------------------

        // The headline defect, at UAT scale: far more than 25 candidates, all of them in window.
        [Fact]
        public async Task More_than_twenty_five_candidate_tasks_are_all_aggregated()
        {
            var tasks = new PagingTaskService();
            for (int i = 1; i <= 120; i++)
                tasks.All.Add(TaskRow(i, $"T{i}", Day(4).AddHours(9)));

            var svc = new WorkspaceAgendaService(tasks, new FakeCalendarService());

            var result = await svc.GetAgendaAsync(Query(Day(4), Day(4), pageSize: 500));

            Assert.Equal(120, result.TotalMatched);
            Assert.Equal(120, result.Items.Count);

            // The proof it is not the old behaviour: 25 is what the silent clamp used to yield.
            Assert.NotEqual(25, result.Items.Count);
        }

        // More than one underlying page must actually be fetched, at a size the task service honours.
        [Fact]
        public async Task More_than_one_underlying_page_is_retrieved_at_an_honoured_page_size()
        {
            var tasks = new PagingTaskService();
            for (int i = 1; i <= 450; i++)
                tasks.All.Add(TaskRow(i, $"T{i}", Day(4).AddHours(9)));

            var svc = new WorkspaceAgendaService(tasks, new FakeCalendarService());

            var result = await svc.GetAgendaAsync(Query(Day(4), Day(4), pageSize: 1000));

            // 450 rows over 200-row pages = 3 reads (200 + 200 + 50).
            Assert.Equal(3, tasks.Calls);
            Assert.Equal(450, result.TotalMatched);

            // Every requested page size must be one the service does NOT rewrite. This is the assertion
            // that fails if somebody restores an over-large request.
            Assert.All(tasks.PageSizesRequested,
                size => Assert.InRange(size, 1, RealTaskServiceMaxPageSize));
        }

        [Fact]
        public async Task No_task_appears_twice_when_the_aggregation_spans_pages()
        {
            var tasks = new PagingTaskService();
            for (int i = 1; i <= 450; i++)
                tasks.All.Add(TaskRow(i, $"T{i}", Day(4).AddHours(9)));

            var svc = new WorkspaceAgendaService(tasks, new FakeCalendarService());

            var result = await svc.GetAgendaAsync(Query(Day(4), Day(4), pageSize: 1000));

            var taskIds = result.Items.Where(i => i.ItemType == AgendaItemType.Task).Select(i => i.SourceId).ToList();
            Assert.Equal(taskIds.Count, taskIds.Distinct().Count());
        }

        // A source that keeps handing back a FULL page of the same rows — the shape a shifting or broken
        // implementation produces — must not spin. The id set makes no progress, so the loop must stop.
        [Fact]
        public async Task Bounded_paging_cannot_loop_forever_on_a_source_that_never_advances()
        {
            var tasks = new StuckTaskService(pageSize: RealTaskServiceMaxPageSize, dueUtc: Day(4).AddHours(9));
            var svc = new WorkspaceAgendaService(tasks, new FakeCalendarService());

            var result = await svc.GetAgendaAsync(Query(Day(4), Day(4), pageSize: 1000));

            // Terminates, and cheaply: the second page contributes no new id, so it stops there.
            Assert.Equal(2, tasks.Calls);
            Assert.Equal(RealTaskServiceMaxPageSize, result.TotalMatched);
        }

        // The other bound: a source with more rows than MaxTaskPages * TaskFetchPageSize is truncated, and
        // the truncation is STATED. A short agenda that does not admit it is the defect being closed.
        [Fact]
        public async Task Reaching_the_candidate_bound_is_reported_and_not_silent()
        {
            var tasks = new PagingTaskService();
            for (int i = 1; i <= 5_400; i++)          // > 25 pages × 200
                tasks.All.Add(TaskRow(i, $"T{i}", Day(4).AddHours(9)));

            var svc = new WorkspaceAgendaService(tasks, new FakeCalendarService());

            var result = await svc.GetAgendaAsync(Query(Day(4), Day(4), pageSize: 10_000));

            Assert.Equal(5_000, result.TotalMatched);                     // 25 × 200, the stated bound
            Assert.Contains(result.DegradedSources, d => d.Contains("Tasks"));
        }

        // ----------------------------------------------------------------------------------------
        // DEFECT 2 — overdue
        // ----------------------------------------------------------------------------------------

        [Fact]
        public async Task Overdue_tasks_are_included_and_keep_their_real_due_date()
        {
            using var clock = PinClock(Day(10).AddHours(12));

            var tasks = new PagingTaskService();
            tasks.All.Add(TaskRow(1, "Late", Day(3).AddHours(11)));       // 7 days before "today"
            var svc = new WorkspaceAgendaService(tasks, new FakeCalendarService());

            var result = await svc.GetAgendaAsync(Query(Day(10), Day(17), includeOverdue: true));

            var item = Assert.Single(result.Items);
            Assert.Equal("Late", item.Title);
            Assert.True(item.IsOverdue);

            // NOT restamped to today — the real date survives, which is what lets the UI band it as overdue.
            Assert.Equal(Day(3).AddHours(11), item.Start.Utc);
            Assert.Equal(Day(3).Date, item.SortKeyUtc.Date);
        }

        [Fact]
        public async Task Overdue_is_opt_in_so_a_literal_date_range_still_excludes_what_precedes_it()
        {
            using var clock = PinClock(Day(10).AddHours(12));

            var tasks = new PagingTaskService();
            tasks.All.Add(TaskRow(1, "Late", Day(3).AddHours(11)));
            var svc = new WorkspaceAgendaService(tasks, new FakeCalendarService());

            // Default (no IncludeOverdue) — the existing contract, unchanged.
            Assert.Empty((await svc.GetAgendaAsync(Query(Day(10), Day(17)))).Items);
        }

        [Fact]
        public async Task Upcoming_and_today_still_arrive_alongside_overdue()
        {
            using var clock = PinClock(Day(10).AddHours(12));

            var tasks = new PagingTaskService();
            tasks.All.Add(TaskRow(1, "Late", Day(4).AddHours(11)));
            tasks.All.Add(TaskRow(2, "DueToday", Day(10).AddHours(16)));   // later today = still ahead
            tasks.All.Add(TaskRow(3, "Ahead", Day(14).AddHours(9)));
            var svc = new WorkspaceAgendaService(tasks, new FakeCalendarService());

            var result = await svc.GetAgendaAsync(Query(Day(10), Day(17), includeOverdue: true));

            Assert.Equal(3, result.Items.Count);
            Assert.Equal(new[] { "Late", "DueToday", "Ahead" }, result.Items.Select(i => i.Title).ToArray());
            Assert.True(result.Items[0].IsOverdue);
            Assert.False(result.Items[1].IsOverdue);
            Assert.False(result.Items[2].IsOverdue);
        }

        // THE DOUBLE-COUNT TRAP. A task due EARLIER TODAY is inside the window AND already late. It must be
        // emitted once, and still say it is late.
        [Fact]
        public async Task A_task_due_earlier_today_is_late_and_appears_exactly_once()
        {
            using var clock = PinClock(Day(10).AddHours(14));

            var tasks = new PagingTaskService();
            tasks.All.Add(TaskRow(1, "SlippedThisMorning", Day(10).AddHours(9)));
            var svc = new WorkspaceAgendaService(tasks, new FakeCalendarService());

            var result = await svc.GetAgendaAsync(Query(Day(10), Day(17), includeOverdue: true));

            var item = Assert.Single(result.Items);
            Assert.True(item.IsOverdue);
            Assert.Equal(1, result.TotalMatched);
        }

        // Bounded, not unlimited history: beyond the lookback the row is not gathered.
        [Fact]
        public async Task Overdue_lookback_is_bounded_and_older_work_is_not_scanned_in()
        {
            using var clock = PinClock(Day(100).AddHours(12));

            var tasks = new PagingTaskService();
            tasks.All.Add(TaskRow(1, "JustInside", Day(95).AddHours(11)));
            tasks.All.Add(TaskRow(2, "TooOld", Day(60).AddHours(11)));
            var svc = new WorkspaceAgendaService(tasks, new FakeCalendarService());

            var result = await svc.GetAgendaAsync(
                Query(Day(100), Day(107), includeOverdue: true, overdueLookbackDays: 10));

            Assert.Equal("JustInside", Assert.Single(result.Items).Title);
        }

        [Fact]
        public async Task A_zero_lookback_means_no_overdue_even_when_requested()
        {
            using var clock = PinClock(Day(10).AddHours(12));

            var tasks = new PagingTaskService();
            tasks.All.Add(TaskRow(1, "Late", Day(4).AddHours(11)));
            var svc = new WorkspaceAgendaService(tasks, new FakeCalendarService());

            var result = await svc.GetAgendaAsync(
                Query(Day(10), Day(17), includeOverdue: true, overdueLookbackDays: 0));

            Assert.Empty(result.Items);
            Assert.Null(result.OverdueFromLocalDate);
        }

        // A completed task is never late — the same rule TaskOverdueSweepService applies.
        [Fact]
        public async Task A_completed_task_is_not_pulled_in_as_overdue()
        {
            using var clock = PinClock(Day(10).AddHours(12));

            var tasks = new PagingTaskService();
            tasks.All.Add(TaskRow(1, "Finished", Day(4).AddHours(11), status: "Done"));
            var svc = new WorkspaceAgendaService(tasks, new FakeCalendarService());

            Assert.Empty((await svc.GetAgendaAsync(Query(Day(10), Day(17), includeOverdue: true))).Items);
        }

        // ----------------------------------------------------------------------------------------
        // ISOLATION + DEGRADATION
        // ----------------------------------------------------------------------------------------

        // The agenda passes the RESOLVED company down on every page and never widens scope. Company
        // filtering itself is TaskService's rule; what is asserted here is that the agenda does not
        // undermine it — including on page 2, which is new surface.
        [Fact]
        public async Task Company_and_scope_are_passed_unchanged_on_every_page()
        {
            var tasks = new PagingTaskService();
            for (int i = 1; i <= 450; i++)
                tasks.All.Add(TaskRow(i, $"T{i}", Day(4).AddHours(9)));

            var svc = new WorkspaceAgendaService(tasks, new FakeCalendarService());

            await svc.GetAgendaAsync(Query(Day(4), Day(4), pageSize: 1000));

            Assert.Equal(3, tasks.Calls);
            Assert.All(tasks.CompaniesRequested, c => Assert.Equal(Company, c));
            Assert.All(tasks.EmployeesRequested, e => Assert.Equal(Me, e));
            Assert.All(tasks.ScopesRequested, s => Assert.Equal("mine", s));   // never widened to "all"
        }

        // A foreign-company row cannot reach the agenda through the paging loop: the fake honours the
        // companyId it is given, exactly as TaskService does.
        [Fact]
        public async Task A_row_from_another_company_never_reaches_the_agenda()
        {
            var tasks = new PagingTaskService();
            tasks.All.Add(TaskRow(1, "Mine", Day(4).AddHours(9), companyId: Company));
            tasks.All.Add(TaskRow(2, "OtherCompany", Day(4).AddHours(9), companyId: 99));

            var svc = new WorkspaceAgendaService(tasks, new FakeCalendarService());

            var result = await svc.GetAgendaAsync(Query(Day(4), Day(4)));

            Assert.Equal("Mine", Assert.Single(result.Items).Title);
        }

        // The panel must degrade per source. A dead Tasks source cannot cost the Calendar its rows.
        [Fact]
        public async Task A_failing_task_source_does_not_take_down_calendar_agenda_data()
        {
            var cal = new FakeCalendarService();
            cal.Events.Add(Event(7, "Standup", Day(4).AddHours(10).ToString("s")));

            var svc = new WorkspaceAgendaService(new ThrowingTaskService(), cal);

            var result = await svc.GetAgendaAsync(Query(Day(4), Day(4), includeOverdue: true));

            var item = Assert.Single(result.Items);
            Assert.Equal(AgendaItemType.CalendarEvent, item.ItemType);
            Assert.Equal("Standup", item.Title);

            // Reported, not swallowed.
            Assert.Contains(result.DegradedSources, d => d.StartsWith("Tasks:"));
        }

        // The lookback must not drag the CALENDAR window backwards: a past meeting is history, not late work.
        [Fact]
        public async Task The_overdue_lookback_does_not_widen_the_calendar_window()
        {
            using var clock = PinClock(Day(10).AddHours(12));

            var cal = new FakeCalendarService();
            var svc = new WorkspaceAgendaService(new PagingTaskService(), cal);

            await svc.GetAgendaAsync(Query(Day(10), Day(17), includeOverdue: true, overdueLookbackDays: 30));

            Assert.Equal(Day(10), cal.LastFrom);      // still today, not today-30
        }

        // ----------------------------------------------------------------------------------------
        // helpers
        // ----------------------------------------------------------------------------------------

        // A fixed 2026 calendar, so no test depends on the day it runs. Counted as an OFFSET from an anchor
        // rather than as a day-of-month, because a day-of-month helper silently breaks the moment a test wants
        // Day(100) — which is exactly how the first version of this file failed.
        private static readonly DateTime Anchor = new(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc);
        private static DateTime Day(int day) => Anchor.AddDays(day - 1);

        private static WorkspaceAgendaQuery Query(
            DateTime from, DateTime to, int pageSize = 500,
            bool includeOverdue = false, int? overdueLookbackDays = null) => new()
            {
                CompanyId = Company,
                EmployeeId = Me,
                FromLocalDate = DateOnly.FromDateTime(from),
                ToLocalDate = DateOnly.FromDateTime(to),
                TimeZoneId = Zone,
                PageSize = pageSize,
                IncludeOverdue = includeOverdue,
                OverdueLookbackDays = overdueLookbackDays ?? 90,
            };

        private static TaskRowDto TaskRow(int id, string title, DateTime dueUtc,
            string status = "New", int companyId = Company) => new()
            {
                Id = id, Title = title, DueDate = dueUtc, Status = status, Priority = "Normal",
                AssigneeEmployeeId = Me, AssigneeName = "Emp 10",
                // Not part of TaskRowDto's identity; carried here only so the fake can filter by company
                // the way the real Filter() does.
                Category = companyId.ToString(),
            };

        private static CalEventDto Event(int id, string title, string start) =>
            new(id, title, start, null, false, "cbev", "Company", null, null, "Owner",
                new List<string>(), new List<int>(), true);

        /// TaskCalendarTime.UtcNow is a settable static. Pin it for the test and always put it back —
        /// leaking a frozen clock into the rest of the suite would be far worse than the test it serves.
        private static ClockPin PinClock(DateTime utcNow) => new(utcNow);

        private sealed class ClockPin : IDisposable
        {
            private readonly Func<DateTime> _previous;
            public ClockPin(DateTime utcNow)
            {
                _previous = TaskCalendarTime.UtcNow;
                TaskCalendarTime.UtcNow = () => utcNow;
            }
            public void Dispose() => TaskCalendarTime.UtcNow = _previous;
        }

        // ----------------------------------------------------------------------------------------
        // doubles
        // ----------------------------------------------------------------------------------------

        /// Honours page/pageSize AND REPRODUCES THE REAL CLAMP — `pageSize > 200 => 25`. That clamp is the
        /// defect; a fake without it would let these tests pass against the unfixed service.
        private sealed class PagingTaskService : ITaskService
        {
            public List<TaskRowDto> All { get; } = new();
            public int Calls { get; private set; }
            public List<int> PageSizesRequested { get; } = new();
            public List<int> CompaniesRequested { get; } = new();
            public List<int> EmployeesRequested { get; } = new();
            public List<string> ScopesRequested { get; } = new();

            public Task<List<TaskRowDto>> GetTasksAsync(int companyId, string scope, int currentEmployeeId,
                string? status, string? priority, string? q, int page = 1, int pageSize = 25,
                string? view = null, int? assignee = null, string? sort = null)
            {
                Calls++;
                PageSizesRequested.Add(pageSize);
                CompaniesRequested.Add(companyId);
                EmployeesRequested.Add(currentEmployeeId);
                ScopesRequested.Add(scope);

                if (page < 1) page = 1;
                if (pageSize < 1 || pageSize > 200) pageSize = 25;      // the real clamp, verbatim

                // Company isolation, as TaskService.Filter applies it.
                var rows = All.Where(t => t.Category == companyId.ToString()).ToList();

                return Task.FromResult(rows.Skip((page - 1) * pageSize).Take(pageSize).ToList());
            }

            public Task<int> CountTasksAsync(int companyId, string scope, int currentEmployeeId, string? status,
                string? priority, string? q, string? view = null, int? assignee = null) => Task.FromResult(All.Count);
            public Task<TaskKpiDto> GetKpisAsync(int companyId, string scope, int currentEmployeeId) =>
                Task.FromResult(new TaskKpiDto());
            public Task<TaskItem?> GetAsync(int companyId, int id) => Task.FromResult<TaskItem?>(null);
            public Task<(bool ok, string? error, int id)> SaveAsync(int companyId, TaskSaveInput input, int currentEmployeeId) =>
                Task.FromResult((true, (string?)null, 0));
            public Task<(bool ok, string? error)> ChangeStatusAsync(int companyId, int id, string status, int currentEmployeeId) =>
                Task.FromResult((true, (string?)null));
            public Task<(bool ok, string? error)> DeleteAsync(int companyId, int id) => Task.FromResult((true, (string?)null));
            public Task<List<(int Id, string Name)>> ActiveEmployeesAsync(int companyId) =>
                Task.FromResult(new List<(int, string)>());
        }

        /// Always returns a FULL page of the SAME rows — a source that never advances. The loop must notice
        /// it gained no new id and stop rather than spin.
        private sealed class StuckTaskService : ITaskService
        {
            private readonly List<TaskRowDto> _page;
            public int Calls { get; private set; }

            public StuckTaskService(int pageSize, DateTime dueUtc)
            {
                _page = Enumerable.Range(1, pageSize)
                    .Select(i => TaskRow(i, $"T{i}", dueUtc))
                    .ToList();
            }

            public Task<List<TaskRowDto>> GetTasksAsync(int companyId, string scope, int currentEmployeeId,
                string? status, string? priority, string? q, int page = 1, int pageSize = 25,
                string? view = null, int? assignee = null, string? sort = null)
            {
                Calls++;
                return Task.FromResult(_page.ToList());
            }

            public Task<int> CountTasksAsync(int companyId, string scope, int currentEmployeeId, string? status,
                string? priority, string? q, string? view = null, int? assignee = null) => Task.FromResult(_page.Count);
            public Task<TaskKpiDto> GetKpisAsync(int companyId, string scope, int currentEmployeeId) =>
                Task.FromResult(new TaskKpiDto());
            public Task<TaskItem?> GetAsync(int companyId, int id) => Task.FromResult<TaskItem?>(null);
            public Task<(bool ok, string? error, int id)> SaveAsync(int companyId, TaskSaveInput input, int currentEmployeeId) =>
                Task.FromResult((true, (string?)null, 0));
            public Task<(bool ok, string? error)> ChangeStatusAsync(int companyId, int id, string status, int currentEmployeeId) =>
                Task.FromResult((true, (string?)null));
            public Task<(bool ok, string? error)> DeleteAsync(int companyId, int id) => Task.FromResult((true, (string?)null));
            public Task<List<(int Id, string Name)>> ActiveEmployeesAsync(int companyId) =>
                Task.FromResult(new List<(int, string)>());
        }

        private sealed class ThrowingTaskService : ITaskService
        {
            public Task<List<TaskRowDto>> GetTasksAsync(int companyId, string scope, int currentEmployeeId,
                string? status, string? priority, string? q, int page = 1, int pageSize = 25,
                string? view = null, int? assignee = null, string? sort = null) =>
                throw new InvalidOperationException("task source is down");

            public Task<int> CountTasksAsync(int companyId, string scope, int currentEmployeeId, string? status,
                string? priority, string? q, string? view = null, int? assignee = null) => Task.FromResult(0);
            public Task<TaskKpiDto> GetKpisAsync(int companyId, string scope, int currentEmployeeId) =>
                Task.FromResult(new TaskKpiDto());
            public Task<TaskItem?> GetAsync(int companyId, int id) => Task.FromResult<TaskItem?>(null);
            public Task<(bool ok, string? error, int id)> SaveAsync(int companyId, TaskSaveInput input, int currentEmployeeId) =>
                Task.FromResult((true, (string?)null, 0));
            public Task<(bool ok, string? error)> ChangeStatusAsync(int companyId, int id, string status, int currentEmployeeId) =>
                Task.FromResult((true, (string?)null));
            public Task<(bool ok, string? error)> DeleteAsync(int companyId, int id) => Task.FromResult((true, (string?)null));
            public Task<List<(int Id, string Name)>> ActiveEmployeesAsync(int companyId) =>
                Task.FromResult(new List<(int, string)>());
        }
    }
}
