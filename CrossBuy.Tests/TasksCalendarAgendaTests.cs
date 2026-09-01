using CrossBuy.BL;
using CrossBuy.BL.TasksCalendar;
using CrossBuy.Models.Context.Tasks;
using Xunit;

namespace CrossBuy.Tests
{
    // ==========================================================================================
    // PHASE 8 / 10 — WORKSPACE AGENDA
    //
    // The agenda is a COMPOSER: it asks ITaskService and ICalendarService and unions the answers. So
    // the tests substitute those two interfaces. That is not mocking away the thing under test — the
    // thing under test is the composition, the time handling and the ordering, and using fakes is what
    // makes "no task rule is re-implemented here" provable: if the service queried the database itself,
    // these fakes would return nothing and every test would fail.
    // ==========================================================================================

    internal sealed class FakeTaskService : ITaskService
    {
        public List<TaskRowDto> Rows { get; } = new();
        public int Calls { get; private set; }
        public string? LastScope { get; private set; }
        public int? LastAssignee { get; private set; }

        public Task<List<TaskRowDto>> GetTasksAsync(int companyId, string scope, int currentEmployeeId,
            string? status, string? priority, string? q, int page = 1, int pageSize = 25,
            string? view = null, int? assignee = null, string? sort = null)
        {
            Calls++; LastScope = scope; LastAssignee = assignee;
            return Task.FromResult(Rows.ToList());
        }

        public Task<int> CountTasksAsync(int companyId, string scope, int currentEmployeeId, string? status,
            string? priority, string? q, string? view = null, int? assignee = null) => Task.FromResult(Rows.Count);
        public Task<TaskKpiDto> GetKpisAsync(int companyId, string scope, int currentEmployeeId) =>
            Task.FromResult(new TaskKpiDto());
        public Task<TaskItem?> GetAsync(int companyId, int id) => Task.FromResult<TaskItem?>(null);
        public Task<(bool ok, string? error, int id)> SaveAsync(int companyId, TaskSaveInput input, int currentEmployeeId) =>
            Task.FromResult((true, (string?)null, 0));
        public Task<(bool ok, string? error)> ChangeStatusAsync(int companyId, int id, string status, int currentEmployeeId) =>
            Task.FromResult((true, (string?)null));
        public Task<(bool ok, string? error)> DeleteAsync(int companyId, int id) => Task.FromResult((true, (string?)null));
        // Follows ITaskService, which gained a companyId parameter. The agenda never calls this member; it
        // exists only so the double still satisfies the interface.
        public Task<List<(int Id, string Name)>> ActiveEmployeesAsync(int companyId) => Task.FromResult(new List<(int, string)>());
    }

    internal sealed class FakeCalendarService : ICalendarService
    {
        public List<CalEventDto> Events { get; } = new();
        public int Calls { get; private set; }
        public DateTime? LastFrom { get; private set; }
        public DateTime? LastTo { get; private set; }

        public Task<List<CalEventDto>> ListAsync(int companyId, int empId, DateTime? from, DateTime? to)
        {
            Calls++; LastFrom = from; LastTo = to;
            return Task.FromResult(Events.ToList());
        }

        public Task<CalEventDto?> GetAsync(int companyId, int empId, int id) => Task.FromResult<CalEventDto?>(null);
        public Task<int> SaveAsync(int companyId, int empId, CalEventInput input) => Task.FromResult(0);
        public Task<bool> DeleteAsync(int companyId, int empId, int id) => Task.FromResult(false);
    }

    public class TasksCalendarAgendaTests
    {
        private const string Zone = "UTC";

        private static (WorkspaceAgendaService svc, FakeTaskService tasks, FakeCalendarService cal) Build()
        {
            var t = new FakeTaskService();
            var c = new FakeCalendarService();
            return (new WorkspaceAgendaService(t, c), t, c);
        }

        private static WorkspaceAgendaQuery Query(DateOnly from, DateOnly to, string? tz = Zone) => new()
        {
            CompanyId = 1,
            EmployeeId = 10,
            FromLocalDate = from,
            ToLocalDate = to,
            TimeZoneId = tz
        };

        private static TaskRowDto TaskRow(int id, string title, DateTime dueUtc, string status = "New") => new()
        {
            Id = id, Title = title, DueDate = dueUtc, Status = status, Priority = "Normal",
            AssigneeEmployeeId = 10, AssigneeName = "Emp 10"
        };

        private static CalEventDto Event(int id, string title, string start, string? end,
            bool allDay = false, string scope = "Company", bool canEdit = true) =>
            new(id, title, start, end, allDay, "cbev", scope, null, null, "Owner",
                new List<string>(), new List<int>(), canEdit);

        // ------------------------------------------------------------------------------------------
        [Fact]
        public async Task The_agenda_combines_tasks_and_calendar_events_in_one_ordered_list()
        {
            var (svc, tasks, cal) = Build();
            tasks.Rows.Add(TaskRow(1, "Task at 14:00", new DateTime(2026, 5, 4, 14, 0, 0, DateTimeKind.Utc)));
            cal.Events.Add(Event(7, "Meeting at 09:00", "2026-05-04T09:00:00", "2026-05-04T10:00:00"));

            var result = await svc.GetAgendaAsync(Query(new DateOnly(2026, 5, 4), new DateOnly(2026, 5, 4)));

            Assert.Equal(2, result.Items.Count);
            Assert.Equal(AgendaItemType.CalendarEvent, result.Items[0].ItemType);   // 09:00 first
            Assert.Equal(AgendaItemType.Task, result.Items[1].ItemType);            // 14:00 second
            Assert.Equal(1, tasks.Calls);
            Assert.Equal(1, cal.Calls);
        }

        [Fact]
        public async Task A_task_appears_as_a_task_and_is_never_turned_into_a_calendar_event()
        {
            var (svc, tasks, cal) = Build();
            tasks.Rows.Add(TaskRow(1, "Submit certificate", new DateTime(2026, 5, 4, 12, 0, 0, DateTimeKind.Utc)));

            var result = await svc.GetAgendaAsync(Query(new DateOnly(2026, 5, 4), new DateOnly(2026, 5, 4)));

            var item = Assert.Single(result.Items);
            Assert.Equal(AgendaItemType.Task, item.ItemType);
            Assert.Equal(TaskCalendarEntityCodes.Task, item.EntityCode);
            Assert.Equal("Tasks", item.SourceModule);
            Assert.Equal(1, item.SourceId);                     // the TASK's id, not a synthesised event id
            // The calendar was still consulted, and it produced nothing — so nothing was materialised into it.
            Assert.Equal(1, cal.Calls);
            Assert.DoesNotContain(result.Items, i => i.ItemType == AgendaItemType.CalendarEvent);
        }

        [Fact]
        public async Task The_same_work_never_appears_twice()
        {
            var (svc, tasks, cal) = Build();
            var due = new DateTime(2026, 5, 4, 12, 0, 0, DateTimeKind.Utc);
            tasks.Rows.Add(TaskRow(1, "Submit certificate", due));

            var result = await svc.GetAgendaAsync(Query(new DateOnly(2026, 5, 4), new DateOnly(2026, 5, 4)));

            Assert.Single(result.Items);
            Assert.Single(result.Items.Select(i => (i.ItemType, i.SourceId)).Distinct());
        }

        [Fact]
        public async Task A_date_range_excludes_what_falls_outside_it()
        {
            var (svc, tasks, cal) = Build();
            tasks.Rows.Add(TaskRow(1, "Inside", new DateTime(2026, 5, 4, 12, 0, 0, DateTimeKind.Utc)));
            tasks.Rows.Add(TaskRow(2, "Before", new DateTime(2026, 5, 1, 12, 0, 0, DateTimeKind.Utc)));
            tasks.Rows.Add(TaskRow(3, "After", new DateTime(2026, 5, 9, 12, 0, 0, DateTimeKind.Utc)));

            var result = await svc.GetAgendaAsync(Query(new DateOnly(2026, 5, 4), new DateOnly(2026, 5, 5)));

            Assert.Equal("Inside", Assert.Single(result.Items).Title);
        }

        [Fact]
        public async Task An_item_late_on_the_last_day_of_the_range_is_included()
        {
            var (svc, tasks, _) = Build();
            tasks.Rows.Add(TaskRow(1, "Late", new DateTime(2026, 5, 5, 23, 30, 0, DateTimeKind.Utc)));

            var result = await svc.GetAgendaAsync(Query(new DateOnly(2026, 5, 4), new DateOnly(2026, 5, 5)));

            Assert.Single(result.Items);
        }

        [Fact]
        public async Task An_all_day_event_keeps_its_date_and_carries_no_offset()
        {
            var (svc, _, cal) = Build();
            cal.Events.Add(Event(7, "National holiday", "2026-05-04T00:00:00", null, allDay: true));

            var result = await svc.GetAgendaAsync(Query(new DateOnly(2026, 5, 4), new DateOnly(2026, 5, 4)));

            var item = Assert.Single(result.Items);
            Assert.True(item.IsAllDay);
            Assert.Equal(AgendaTimeKind.AllDayDate, item.Start.Kind);
            Assert.Equal(new DateOnly(2026, 5, 4), item.Start.Date);
            Assert.Equal("2026-05-04", item.Start.ToApiString());
            Assert.Null(item.TimeZoneId);           // an all-day value has no zone, by design
        }

        [Fact]
        public async Task A_timed_item_carries_an_explicit_offset()
        {
            var (svc, _, cal) = Build();
            cal.Events.Add(Event(7, "Site meeting", "2026-05-04T09:00:00", "2026-05-04T10:00:00"));

            var result = await svc.GetAgendaAsync(Query(new DateOnly(2026, 5, 4), new DateOnly(2026, 5, 4)));

            var item = Assert.Single(result.Items);
            Assert.Equal(AgendaTimeKind.Instant, item.Start.Kind);
            Assert.NotNull(item.Start.Local);
            Assert.Contains("+00:00", item.Start.ToApiString());
        }

        [Fact]
        public async Task Somebody_elses_personal_event_is_shown_as_busy_with_its_detail_withheld()
        {
            var (svc, _, cal) = Build();
            cal.Events.Add(Event(7, "Doctor — oncology follow-up", "2026-05-04T09:00:00", "2026-05-04T10:00:00",
                scope: "Personal", canEdit: false));

            var result = await svc.GetAgendaAsync(Query(new DateOnly(2026, 5, 4), new DateOnly(2026, 5, 4)));

            var item = Assert.Single(result.Items);
            Assert.True(item.IsRedacted);
            Assert.DoesNotContain("oncology", item.Title);
            Assert.Null(item.OwnerName);
            // The SLOT is still shown: "busy at 09:00" is the useful half and is not private.
            Assert.Equal(AgendaTimeKind.Instant, item.Start.Kind);
        }

        [Fact]
        public async Task My_own_personal_event_is_not_redacted()
        {
            var (svc, _, cal) = Build();
            cal.Events.Add(Event(7, "Dentist", "2026-05-04T09:00:00", null, scope: "Personal", canEdit: true));

            var result = await svc.GetAgendaAsync(Query(new DateOnly(2026, 5, 4), new DateOnly(2026, 5, 4)));

            var item = Assert.Single(result.Items);
            Assert.False(item.IsRedacted);
            Assert.Equal("Dentist", item.Title);
        }

        [Fact]
        public async Task Task_scope_is_never_widened_by_the_agenda()
        {
            var (svc, tasks, _) = Build();

            await svc.GetAgendaAsync(Query(new DateOnly(2026, 5, 4), new DateOnly(2026, 5, 4)));

            // No assignee filter was requested, so the agenda asks for MINE — it does not ask for everyone's
            // and then filter, which would leak counts through timing and paging.
            Assert.Equal("mine", tasks.LastScope);
            Assert.Null(tasks.LastAssignee);
        }

        [Fact]
        public async Task Ordering_is_deterministic_when_two_items_share_an_instant()
        {
            var (svc, tasks, cal) = Build();
            var at = new DateTime(2026, 5, 4, 9, 0, 0, DateTimeKind.Utc);
            tasks.Rows.Add(TaskRow(2, "Task B", at));
            tasks.Rows.Add(TaskRow(1, "Task A", at));
            cal.Events.Add(Event(9, "Event", "2026-05-04T09:00:00", null));

            var first = await svc.GetAgendaAsync(Query(new DateOnly(2026, 5, 4), new DateOnly(2026, 5, 4)));
            var second = await svc.GetAgendaAsync(Query(new DateOnly(2026, 5, 4), new DateOnly(2026, 5, 4)));

            var keyA = first.Items.Select(i => (i.ItemType, i.SourceId)).ToArray();
            var keyB = second.Items.Select(i => (i.ItemType, i.SourceId)).ToArray();
            Assert.Equal(keyA, keyB);
            // Task sorts before CalendarEvent at an equal instant, then by id — every tie is broken.
            Assert.Equal((AgendaItemType.Task, 1), keyA[0]);
            Assert.Equal((AgendaItemType.Task, 2), keyA[1]);
            Assert.Equal((AgendaItemType.CalendarEvent, 9), keyA[2]);
        }

        [Fact]
        public async Task Paging_is_stable_and_does_not_repeat_a_row_across_pages()
        {
            var (svc, tasks, _) = Build();
            for (int i = 1; i <= 5; i++)
                tasks.Rows.Add(TaskRow(i, $"T{i}", new DateTime(2026, 5, 4, 9, 0, 0, DateTimeKind.Utc).AddMinutes(i)));

            var q1 = Query(new DateOnly(2026, 5, 4), new DateOnly(2026, 5, 4));
            var page1 = await svc.GetAgendaAsync(new WorkspaceAgendaQuery
            {
                CompanyId = q1.CompanyId, EmployeeId = q1.EmployeeId, FromLocalDate = q1.FromLocalDate,
                ToLocalDate = q1.ToLocalDate, TimeZoneId = Zone, Page = 1, PageSize = 2
            });
            var page2 = await svc.GetAgendaAsync(new WorkspaceAgendaQuery
            {
                CompanyId = q1.CompanyId, EmployeeId = q1.EmployeeId, FromLocalDate = q1.FromLocalDate,
                ToLocalDate = q1.ToLocalDate, TimeZoneId = Zone, Page = 2, PageSize = 2
            });

            Assert.Equal(5, page1.TotalMatched);
            Assert.Equal(2, page1.Items.Count);
            Assert.Empty(page1.Items.Select(i => i.SourceId).Intersect(page2.Items.Select(i => i.SourceId)));
        }

        [Fact]
        public async Task A_completed_task_is_excluded_unless_it_is_asked_for()
        {
            var (svc, tasks, _) = Build();
            tasks.Rows.Add(TaskRow(1, "Done already", new DateTime(2026, 5, 4, 9, 0, 0, DateTimeKind.Utc), status: "Done"));

            var without = await svc.GetAgendaAsync(Query(new DateOnly(2026, 5, 4), new DateOnly(2026, 5, 4)));
            Assert.Empty(without.Items);

            var with = await svc.GetAgendaAsync(new WorkspaceAgendaQuery
            {
                CompanyId = 1, EmployeeId = 10, FromLocalDate = new DateOnly(2026, 5, 4),
                ToLocalDate = new DateOnly(2026, 5, 4), TimeZoneId = Zone, IncludeCompletedTasks = true
            });
            var item = Assert.Single(with.Items);
            Assert.True(item.IsCompleted);
            Assert.False(item.IsOverdue);      // a completed task is never overdue
        }

        [Fact]
        public async Task An_unresolved_company_or_employee_is_refused_rather_than_defaulted()
        {
            var (svc, _, _) = Build();

            await Assert.ThrowsAsync<InvalidOperationException>(() => svc.GetAgendaAsync(new WorkspaceAgendaQuery
            {
                CompanyId = 0, EmployeeId = 10, FromLocalDate = new DateOnly(2026, 5, 4),
                ToLocalDate = new DateOnly(2026, 5, 4), TimeZoneId = Zone
            }));

            await Assert.ThrowsAsync<InvalidOperationException>(() => svc.GetAgendaAsync(new WorkspaceAgendaQuery
            {
                CompanyId = 1, EmployeeId = 0, FromLocalDate = new DateOnly(2026, 5, 4),
                ToLocalDate = new DateOnly(2026, 5, 4), TimeZoneId = Zone
            }));
        }

        [Fact]
        public async Task A_missing_timezone_is_refused_rather_than_guessed()
        {
            var (svc, _, _) = Build();

            await Assert.ThrowsAsync<TimeZoneUnresolvedException>(
                () => svc.GetAgendaAsync(Query(new DateOnly(2026, 5, 4), new DateOnly(2026, 5, 4), tz: null)));
        }

        [Fact]
        public async Task A_failing_source_degrades_the_agenda_visibly_instead_of_silently()
        {
            var tasks = new ThrowingTaskService();
            var cal = new FakeCalendarService();
            cal.Events.Add(Event(7, "Meeting", "2026-05-04T09:00:00", null));
            var svc = new WorkspaceAgendaService(tasks, cal);

            var result = await svc.GetAgendaAsync(Query(new DateOnly(2026, 5, 4), new DateOnly(2026, 5, 4)));

            // The calendar half still renders, and the missing half is REPORTED — an agenda quietly
            // missing its tasks is worse than one that says so.
            Assert.Single(result.Items);
            Assert.Contains(result.DegradedSources, d => d.StartsWith("Tasks:"));
        }

        /// Its own implementation rather than a subclass: FakeTaskService is sealed, and a source that
        /// throws is a different fake, not a variation of the working one.
        private sealed class ThrowingTaskService : ITaskService
        {
            public Task<List<TaskRowDto>> GetTasksAsync(int companyId, string scope, int currentEmployeeId,
                string? status, string? priority, string? q, int page = 1, int pageSize = 25,
                string? view = null, int? assignee = null, string? sort = null) =>
                throw new InvalidOperationException("task source is unavailable");

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
            // Follows ITaskService, which gained a companyId parameter. The agenda never calls this member; it
        // exists only so the double still satisfies the interface.
        public Task<List<(int Id, string Name)>> ActiveEmployeesAsync(int companyId) => Task.FromResult(new List<(int, string)>());
        }
    }
}
