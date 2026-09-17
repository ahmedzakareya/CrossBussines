using CrossBuy.BL.Platform;
using CrossBuy.BL.TasksCalendar;
using CrossBuy.BL.Workspace;
using CrossBuy.Models.Platform;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CrossBuy.Tests
{
    // ============================================================================================
    // WORKSPACE AGENDA — THE OVERDUE WINDOW POLICY  (TAB 6, UAT defect closure)
    //
    // THE DEFECT THESE TESTS HOLD CLOSED. LoadAgendaAsync opened its range at `from = today` and asked
    // for nothing before it. A task whose due date had already passed was therefore never REQUESTED —
    // so the one screen a person opens to find late work was the only place late work could not appear.
    // On the populated CrossBuyDev UAT dataset the dense employee had nine overdue tasks and the agenda
    // showed zero of them.
    //
    // WHAT IS UNDER TEST HERE, AND WHAT IS DELIBERATELY NOT.
    //
    //   Under test — the WORKSPACE's request policy and its presentation of the answer:
    //     * that it ASKS for late work at all, with a bounded, stated lookback;
    //     * that the bound does not move when the caller narrows the forward range;
    //     * that it passes the RESOLVED company/employee and never anything else;
    //     * that it bands rows OVERDUE / TODAY / UPCOMING without inventing a second visual language;
    //     * that its dashboard cap cannot let one band swallow the panel.
    //
    //   NOT under test — how late work is gathered. That is TAB 5's WorkspaceAgendaService: its paging,
    //     its overdue floor, its exclusive `!inWindow` guard, its per-source degradation. Re-testing it
    //     here would be a second copy of somebody else's contract, and the copy is what drifts.
    //
    // SO THE SEAM IS THE CONTRACT. Every test drives WorkspaceService through a fake
    // IWorkspaceAgendaService. Two fakes are used, for two different jobs:
    //
    //   RecordingAgenda — captures the WorkspaceAgendaQuery and returns whatever it is handed. This is
    //                     how the REQUEST POLICY is asserted, which is the half TAB 6 owns.
    //   WindowedAgenda  — a small honest stand-in that applies the window and the lookback the query
    //                     asked for, exactly as the contract documents them. This is how the OUTCOME is
    //                     asserted end-to-end without reaching into TAB 5's implementation.
    //
    // These tests deliberately do NOT construct ITaskService or ICalendarService. Nothing below depends
    // on the shape of either, so a change in the Tasks module cannot make this file fail for a reason
    // that has nothing to do with the Workspace.
    // ============================================================================================
    public class WorkspaceAgendaOverdueTests
    {
        private const int Company = 1;
        private const int Employee = 5;      // the dense UAT employee, by number rather than by accident

        private static readonly DateTime Today = DateTime.Now.Date;

        // ----------------------------------------------------------------------------------------
        // 1 · OVERDUE IS INCLUDED — the defect itself.
        // ----------------------------------------------------------------------------------------

        [Fact]
        public async Task An_overdue_task_reaches_the_agenda()
        {
            var agenda = new WindowedAgenda()
                .WithTask(1, "late by three days", Today.AddDays(-3).AddHours(11));

            var panel = await Workspace(agenda).GetAgendaAsync(days: 7);

            Assert.Equal(WorkspacePanelState.Ready, panel.State);
            var row = Assert.Single(panel.Items);
            Assert.Equal("late by three days", row.Title);
            Assert.Equal(WorkspaceAgendaBand.Overdue, row.Band);
        }

        // The regression in its most literal form: the OLD policy asked for [today, today+days] and this
        // is what that asked for. If the request ever narrows back to today, this fails.
        [Fact]
        public async Task The_request_opens_before_today_rather_than_at_it()
        {
            var recorder = new RecordingAgenda();

            await Workspace(recorder).GetAgendaAsync(days: 7);

            var q = Assert.Single(recorder.Queries);

            Assert.True(q.IncludeOverdue,
                "The Workspace must ASK for late work. With IncludeOverdue false the contract returns a " +
                "literal date range, which is exactly the defect: overdue tasks are never requested.");

            Assert.True(q.OverdueLookbackDays > 0,
                "A zero lookback means 'no overdue' on this contract — the flag would be on and the answer " +
                "would still be empty.");
        }

        // ----------------------------------------------------------------------------------------
        // 2 · TODAY  ·  3 · UPCOMING — the two bands that must survive the widening.
        // ----------------------------------------------------------------------------------------

        [Fact]
        public async Task A_task_due_today_reaches_the_agenda()
        {
            var agenda = new WindowedAgenda().WithTask(2, "due today", Today.AddHours(9));

            var panel = await Workspace(agenda).GetAgendaAsync(days: 7);

            var row = Assert.Single(panel.Items);
            Assert.Equal(WorkspaceAgendaBand.Today, row.Band);
        }

        [Fact]
        public async Task An_upcoming_task_reaches_the_agenda()
        {
            var agenda = new WindowedAgenda().WithTask(3, "due in three days", Today.AddDays(3).AddHours(15));

            var panel = await Workspace(agenda).GetAgendaAsync(days: 7);

            var row = Assert.Single(panel.Items);
            Assert.Equal(WorkspaceAgendaBand.Upcoming, row.Band);
        }

        // All three at once, because each of the tests above would also pass on a screen that could show
        // only its own band. The product expectation is that the agenda REPRESENTS all three.
        [Fact]
        public async Task Overdue_today_and_upcoming_appear_together_in_one_agenda()
        {
            var agenda = new WindowedAgenda()
                .WithTask(1, "late", Today.AddDays(-4).AddHours(11))
                .WithTask(2, "today", Today.AddHours(9))
                .WithTask(3, "soon", Today.AddDays(2).AddHours(13));

            var panel = await Workspace(agenda).GetAgendaAsync(days: 7);

            Assert.Equal(
                new[] { WorkspaceAgendaBand.Overdue, WorkspaceAgendaBand.Today, WorkspaceAgendaBand.Upcoming },
                panel.Items.Select(r => r.Band).ToArray());
        }

        // ----------------------------------------------------------------------------------------
        // 4 · THE BOUND IS REAL — "include overdue" must not mean "include everything ever late".
        // ----------------------------------------------------------------------------------------

        [Fact]
        public async Task A_task_older_than_the_bounded_policy_is_excluded()
        {
            var agenda = new WindowedAgenda()
                .WithTask(1, "just inside the horizon", Today.AddDays(-(Lookback() - 1)).AddHours(11))
                .WithTask(2, "far outside the horizon", Today.AddDays(-(Lookback() + 30)).AddHours(11));

            var panel = await Workspace(agenda).GetAgendaAsync(days: 7);

            var row = Assert.Single(panel.Items);
            Assert.Equal("just inside the horizon", row.Title);
        }

        // The bound must be a BOUND, not merely large. A request that asked for everything would satisfy
        // every other test in this file and reintroduce an unbounded historical scan.
        [Fact]
        public async Task The_requested_lookback_is_bounded_and_is_the_horizon_the_repository_defines()
        {
            var recorder = new RecordingAgenda();

            await Workspace(recorder).GetAgendaAsync(days: 7);

            var q = Assert.Single(recorder.Queries);

            // The request carries the Workspace's STATED policy, which is initialised from the one horizon
            // the repository defines (TAB 5's DefaultOverdueLookbackDays) rather than copied as a literal.
            Assert.Equal(WorkspaceService.AgendaOverdueLookbackDays, q.OverdueLookbackDays);

            // ...and that policy is genuinely a BOUND, asserted on its own terms rather than against the
            // number it happens to equal today:
            //   * at least the product's own overdue band — the UAT dataset seeds late work across
            //     today-1 .. today-30, so anything shorter would hide seeded overdue rows by construction;
            //   * and no more than a year, because "include overdue" must never mean "scan all history".
            Assert.InRange(WorkspaceService.AgendaOverdueLookbackDays, 30, 365);
        }

        // Late work must not vanish because the reader narrowed the FORWARD range. `days=1` is "today",
        // not "forget everything that is late" — that would be the same defect through a different door.
        [Theory]
        [InlineData(1)]
        [InlineData(7)]
        [InlineData(30)]
        public async Task The_overdue_horizon_does_not_shrink_when_the_forward_range_narrows(int days)
        {
            var recorder = new RecordingAgenda();

            await Workspace(recorder).GetAgendaAsync(days);

            var q = Assert.Single(recorder.Queries);
            Assert.Equal(WorkspaceService.AgendaOverdueLookbackDays, q.OverdueLookbackDays);
            Assert.True(q.IncludeOverdue);
        }

        [Fact]
        public async Task An_overdue_task_is_visible_on_every_published_range_of_the_screen()
        {
            // /Workspace/Agenda offers exactly these three ranges as links.
            foreach (var days in new[] { 1, 7, 30 })
            {
                var agenda = new WindowedAgenda().WithTask(1, "late", Today.AddDays(-5).AddHours(11));

                var panel = await Workspace(agenda).GetAgendaAsync(days);

                Assert.True(panel.Items.Any(r => r.Band == WorkspaceAgendaBand.Overdue),
                    $"days={days} lost the overdue row.");
            }
        }

        // ----------------------------------------------------------------------------------------
        // 5 · THE REAL DUE DATE SURVIVES. The forbidden "fix" for this defect was to re-stamp a late
        //     task to today so it falls inside the window. That would make every UAT count right and
        //     every date wrong, and the row would silently stop being late.
        // ----------------------------------------------------------------------------------------

        [Fact]
        public async Task An_overdue_row_keeps_its_own_due_date_and_is_never_restamped_to_today()
        {
            var due = Today.AddDays(-9).AddHours(11);
            var agenda = new WindowedAgenda().WithTask(1, "late", due);

            var row = Assert.Single((await Workspace(agenda).GetAgendaAsync(days: 7)).Items);

            Assert.Equal(due, row.LocalAt);
            Assert.NotEqual(Today, row.LocalAt.Date);
            Assert.Equal(WorkspaceAgendaBand.Overdue, row.Band);

            // ...and it still READS as late: the flag, and the tone the flag drives, both survive.
            Assert.True(row.Overdue);
            Assert.Equal(WorkspaceTone.Critical, row.Tone);
        }

        // Band and Overdue are two different facts and the row carries both. A task due at 09:00 today,
        // read later the same day, sits in the TODAY band and is ALREADY LATE. Collapsing either into the
        // other loses a fact the screen needs.
        [Fact]
        public async Task A_task_due_earlier_today_is_banded_today_and_still_flagged_overdue()
        {
            var agenda = new WindowedAgenda().WithTask(2, "due 00:01 today", Today.AddMinutes(1), late: true);

            var row = Assert.Single((await Workspace(agenda).GetAgendaAsync(days: 7)).Items);

            Assert.Equal(WorkspaceAgendaBand.Today, row.Band);
            Assert.True(row.Overdue);
        }

        // ----------------------------------------------------------------------------------------
        // 6 · NO DUPLICATES across the overdue and window ranges.
        //
        // The Workspace makes ONE call and does not merge two result sets, so a duplicate could only
        // arrive from the contract. This asserts BOTH halves of that: the shape of the request (one call,
        // no second overdue query of our own) and the absence of repeats in what is rendered.
        // ----------------------------------------------------------------------------------------

        [Fact]
        public async Task The_agenda_is_one_request_not_an_overdue_query_stitched_onto_a_window_query()
        {
            var recorder = new RecordingAgenda();

            await Workspace(recorder).GetAgendaAsync(days: 7);

            Assert.Single(recorder.Queries);
        }

        [Fact]
        public async Task No_task_appears_twice_across_the_overdue_and_upcoming_ranges()
        {
            var agenda = new WindowedAgenda()
                .WithTask(1, "late", Today.AddDays(-2).AddHours(11))
                .WithTask(2, "due 00:01 today", Today.AddMinutes(1), late: true)   // late AND in-window
                .WithTask(3, "today", Today.AddHours(9))
                .WithTask(4, "soon", Today.AddDays(1).AddHours(10));

            var panel = await Workspace(agenda).GetAgendaAsync(days: 7);

            var repeated = panel.Items
                .GroupBy(r => (r.Title, r.LocalAt))
                .Where(g => g.Count() > 1)
                .Select(g => g.Key.Title)
                .ToList();

            Assert.True(repeated.Count == 0, "Rows rendered more than once: " + string.Join(", ", repeated));
            Assert.Equal(4, panel.Items.Count);
        }

        // ----------------------------------------------------------------------------------------
        // 7 · COMPANY ISOLATION. The Workspace's half of this is that the query carries the RESOLVED
        //     context and nothing else — it takes no company or employee from the request, and widening
        //     the window did not become an excuse to widen the scope.
        // ----------------------------------------------------------------------------------------

        [Fact]
        public async Task The_query_carries_the_resolved_company_and_employee_and_widens_no_scope()
        {
            var recorder = new RecordingAgenda();

            await Workspace(recorder).GetAgendaAsync(days: 30);

            var q = Assert.Single(recorder.Queries);

            Assert.Equal(Company, q.CompanyId);
            Assert.Equal(Employee, q.EmployeeId);

            // Null keeps the contract on its own "mine" scope. An assignee here would flip it to "all",
            // which is how a personal agenda quietly becomes everybody's.
            Assert.Null(q.AssigneeEmployeeId);

            // Late work is asked for; COMPLETED work is not. A done task is not outstanding, and turning
            // this on would inflate every overdue count on the screen.
            Assert.False(q.IncludeCompletedTasks);
        }

        [Fact]
        public async Task A_session_with_no_company_is_refused_before_any_agenda_request_is_made()
        {
            var recorder = new RecordingAgenda();
            var workspace = Workspace(recorder, new FixedContext(null));

            var panel = await workspace.GetAgendaAsync(days: 7);

            Assert.Equal(WorkspacePanelState.AccessDenied, panel.State);
            Assert.Empty(recorder.Queries);      // fail closed: nothing was even asked
        }

        [Fact]
        public async Task A_session_with_no_employee_is_refused_before_any_agenda_request_is_made()
        {
            var recorder = new RecordingAgenda();
            var workspace = Workspace(recorder,
                new FixedContext(new BusinessContext { CompanyId = Company, EmployeeId = null }));

            var panel = await workspace.GetAgendaAsync(days: 7);

            Assert.Equal(WorkspacePanelState.AccessDenied, panel.State);
            Assert.Empty(recorder.Queries);
        }

        // ----------------------------------------------------------------------------------------
        // 8 · THE CALENDAR CONTRIBUTION SURVIVES. Widening the range for late TASKS must not cost the
        //     other half of the agenda — the screen is "tasks and appointments in one list".
        // ----------------------------------------------------------------------------------------

        [Fact]
        public async Task Calendar_entries_still_appear_alongside_overdue_and_upcoming_tasks()
        {
            var agenda = new WindowedAgenda()
                .WithTask(1, "late", Today.AddDays(-3).AddHours(11))
                .WithEvent(9, "team stand-up", Today.AddHours(10))
                .WithTask(2, "soon", Today.AddDays(2).AddHours(13));

            var panel = await Workspace(agenda).GetAgendaAsync(days: 7);

            Assert.Contains(panel.Items, r => r.SourceModule == "Calendar" && r.Title == "team stand-up");
            Assert.Contains(panel.Items, r => r.SourceModule == "Tasks" && r.Band == WorkspaceAgendaBand.Overdue);
            Assert.Contains(panel.Items, r => r.SourceModule == "Tasks" && r.Band == WorkspaceAgendaBand.Upcoming);
        }

        // A calendar entry is never "late" — a meeting that has happened is history, not outstanding work,
        // and the contract sets IsOverdue = false on every event. The Workspace must not invent otherwise.
        [Fact]
        public async Task A_calendar_entry_is_never_rendered_as_late_work()
        {
            var agenda = new WindowedAgenda().WithEvent(9, "stand-up", Today.AddHours(10));

            var row = Assert.Single((await Workspace(agenda).GetAgendaAsync(days: 7)).Items);

            Assert.False(row.Overdue);
            Assert.NotEqual(WorkspaceTone.Critical, row.Tone);
        }

        // ----------------------------------------------------------------------------------------
        // 9 · THE PANEL STATES STILL BEHAVE. Six states exist because each is a different thing to tell
        //     a person, and the whole point of the Workspace's guarding is that none of them collapses
        //     into "empty" — a broken deployment must not read as a quiet week.
        // ----------------------------------------------------------------------------------------

        [Fact]
        public async Task An_unregistered_agenda_service_is_unavailable_not_empty()
        {
            var workspace = new WorkspaceService(
                new FixedContext(Resolved()), new ServiceCollection().BuildServiceProvider(),
                new NoNotifications(), new NoIdentity(),
                Array.Empty<IWorkspaceFavoritesSource>(), Array.Empty<IWorkspaceActivitySource>(),
                Array.Empty<IWorkspaceReportSource>(), NullLogger<WorkspaceService>.Instance);

            var panel = await workspace.GetAgendaAsync(days: 7);

            Assert.Equal(WorkspacePanelState.Unavailable, panel.State);
            Assert.False(string.IsNullOrWhiteSpace(panel.Reason));
        }

        [Fact]
        public async Task A_throwing_agenda_service_is_a_temporary_failure_carrying_its_own_message()
        {
            var panel = await Workspace(new ThrowingAgenda()).GetAgendaAsync(days: 7);

            Assert.Equal(WorkspacePanelState.TemporaryFailure, panel.State);
            Assert.Contains("the calendar is not deployed here", panel.Reason);
        }

        // One source down is PARTIAL, and the rows that DID load are still shown — including the late
        // ones. A banner instead of the rows would hide real work; the rows without the banner would
        // overstate completeness. The reader needs both.
        [Fact]
        public async Task A_degraded_source_stays_partially_available_with_its_overdue_rows_intact()
        {
            var agenda = new WindowedAgenda()
                .WithTask(1, "late", Today.AddDays(-3).AddHours(11))
                .Degraded("Calendar: InvalidOperationException");

            var panel = await Workspace(agenda).GetAgendaAsync(days: 7);

            Assert.Equal(WorkspacePanelState.PartiallyAvailable, panel.State);
            Assert.Contains("Calendar: InvalidOperationException", panel.MissingSources);
            Assert.Contains(panel.Items, r => r.Band == WorkspaceAgendaBand.Overdue);
        }

        [Fact]
        public async Task A_genuinely_empty_agenda_is_still_empty_and_not_a_failure()
        {
            var panel = await Workspace(new WindowedAgenda()).GetAgendaAsync(days: 7);

            Assert.Equal(WorkspacePanelState.Empty, panel.State);
            Assert.Empty(panel.Items);
        }

        // ----------------------------------------------------------------------------------------
        // THE DASHBOARD CAP. The dashboard shows eight agenda rows. The list is chronological and now
        // starts in the past, so a head-of-list cap would answer "what is on today?" with a month-old
        // backlog and nothing else — and the dense UAT employee has more overdue tasks than there are
        // slots, so this is the real case rather than a contrived one.
        // ----------------------------------------------------------------------------------------

        [Fact]
        public async Task The_dashboard_summary_shows_both_sides_of_today_when_late_work_would_fill_it()
        {
            var agenda = new WindowedAgenda();
            for (int i = 1; i <= 12; i++) agenda.WithTask(i, $"late {i}", Today.AddDays(-i).AddHours(11));
            for (int i = 1; i <= 6; i++) agenda.WithTask(100 + i, $"soon {i}", Today.AddDays(i).AddHours(11));

            var dashboard = await Workspace(agenda).GetDashboardAsync();
            var shown = dashboard.Agenda.Items;

            Assert.Equal(8, shown.Count);
            Assert.Contains(shown, r => r.Band == WorkspaceAgendaBand.Overdue);
            Assert.Contains(shown, r => r.Band == WorkspaceAgendaBand.Upcoming);

            // The late rows kept are the ones NEAREST today. A task that slipped yesterday is actionable;
            // one that slipped seven weeks ago is the least useful thing that could hold a summary slot.
            Assert.Contains(shown, r => r.Title == "late 1");
            Assert.DoesNotContain(shown, r => r.Title == "late 12");

            // Capped, and honest about it.
            Assert.Equal(18, dashboard.Agenda.Total);
        }

        // The reserve is a floor for the past, not a quota against it: someone whose whole agenda is late
        // work still gets a full panel rather than three rows and five blanks.
        [Fact]
        public async Task An_all_overdue_agenda_still_fills_the_dashboard_panel()
        {
            var agenda = new WindowedAgenda();
            for (int i = 1; i <= 12; i++) agenda.WithTask(i, $"late {i}", Today.AddDays(-i).AddHours(11));

            var dashboard = await Workspace(agenda).GetDashboardAsync();

            Assert.Equal(8, dashboard.Agenda.Items.Count);
            Assert.All(dashboard.Agenda.Items, r => Assert.Equal(WorkspaceAgendaBand.Overdue, r.Band));
        }

        // THE RESERVE IS ONLY LOAD-BEARING WHEN THERE IS ENOUGH UPCOMING WORK TO FILL THE PANEL ON ITS
        // OWN. The case above supplies six ahead rows, so `take - ahead.Count` already forces two overdue
        // slots and the reserve is never consulted — mutation testing proved that: setting
        // AgendaOverdueShown to 0 left the whole suite green. This is the case that fails when the reserve
        // is removed, and it is the real one: a person with a full week ahead is exactly the person whose
        // late work would otherwise fall off the dashboard entirely.
        [Fact]
        public async Task Late_work_keeps_its_reserved_slots_even_when_upcoming_work_could_fill_the_panel()
        {
            var agenda = new WindowedAgenda();
            for (int i = 1; i <= 12; i++) agenda.WithTask(i, $"late {i}", Today.AddDays(-i).AddHours(11));
            // ten upcoming rows inside the dashboard's 7-day range - more than the eight slots on offer
            for (int i = 1; i <= 10; i++)
                agenda.WithTask(100 + i, $"soon {i}", Today.AddDays(1 + (i - 1) / 2).AddHours(9 + i % 2));

            var shown = (await Workspace(agenda).GetDashboardAsync()).Agenda.Items;

            Assert.Equal(8, shown.Count);

            // Without the reserve this is ZERO: upcoming work alone would take every slot.
            Assert.Equal(3, shown.Count(r => r.Band == WorkspaceAgendaBand.Overdue));
            Assert.Equal(5, shown.Count(r => r.Band != WorkspaceAgendaBand.Overdue));

            // ...and the late rows kept are still the ones nearest today.
            Assert.Contains(shown, r => r.Title == "late 1");
            Assert.DoesNotContain(shown, r => r.Title == "late 12");
        }

        // ...and the mirror: no late work must not cost upcoming rows their slots.
        //
        // Twelve rows across the SIX days after today, two per day — the dashboard's range is 7 days, so
        // spreading one per day over twelve days would put half of them outside the window and this test
        // would be measuring the window rather than the cap.
        [Fact]
        public async Task An_agenda_with_no_late_work_fills_the_dashboard_panel_with_upcoming_rows()
        {
            var agenda = new WindowedAgenda();
            for (int i = 1; i <= 12; i++)
                agenda.WithTask(i, $"soon {i}", Today.AddDays(1 + (i - 1) / 2).AddHours(9 + i % 2));

            var dashboard = await Workspace(agenda).GetDashboardAsync();

            Assert.Equal(8, dashboard.Agenda.Items.Count);
            Assert.All(dashboard.Agenda.Items, r => Assert.Equal(WorkspaceAgendaBand.Upcoming, r.Band));
        }

        // The cap must not upgrade the promise: a truncated partial panel is still partial.
        [Fact]
        public async Task The_dashboard_cap_preserves_a_partially_available_state()
        {
            var agenda = new WindowedAgenda().Degraded("Calendar: InvalidOperationException");
            for (int i = 1; i <= 12; i++) agenda.WithTask(i, $"late {i}", Today.AddDays(-i).AddHours(11));

            var dashboard = await Workspace(agenda).GetDashboardAsync();

            Assert.Equal(WorkspacePanelState.PartiallyAvailable, dashboard.Agenda.State);
            Assert.Equal(8, dashboard.Agenda.Items.Count);
        }

        // The dashboard's agenda is the 7-day range, and it asks for late work on the same terms.
        [Fact]
        public async Task The_dashboard_asks_for_late_work_too()
        {
            var recorder = new RecordingAgenda();

            await Workspace(recorder).GetDashboardAsync();

            var q = Assert.Single(recorder.Queries);
            Assert.True(q.IncludeOverdue);
            Assert.Equal(WorkspaceService.AgendaOverdueLookbackDays, q.OverdueLookbackDays);
        }

        // ========================================================================================
        // harness
        // ========================================================================================

        private static int Lookback() => WorkspaceService.AgendaOverdueLookbackDays;

        private static BusinessContext Resolved() =>
            new() { CompanyId = Company, EmployeeId = Employee };

        private static WorkspaceService Workspace(
            IWorkspaceAgendaService agenda, IBusinessContextAccessor? contexts = null)
        {
            var services = new ServiceCollection();
            services.AddSingleton(agenda);

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

        /// A local wall-clock moment expressed the way the contract expresses one: a UTC instant carrying
        /// the viewer's offset. Built through TaskCalendarTime so these tests read the same type the
        /// Workspace reads, and so `LocalAt` comes back as exactly the wall clock that went in — which is
        /// what makes the "the real due date survives" assertions meaningful rather than tautological.
        private static AgendaInstant Instant(DateTime localWallClock) =>
            TaskCalendarTime.Timed(
                TimeZoneInfo.ConvertTimeToUtc(
                    DateTime.SpecifyKind(localWallClock, DateTimeKind.Unspecified), TimeZoneInfo.Local),
                TimeZoneInfo.Local);

        // ---- the two fakes ---------------------------------------------------------------------

        /// Captures the query and returns nothing. This is the fake that tests the REQUEST — the half of
        /// the defect TAB 6 actually owns.
        private sealed class RecordingAgenda : IWorkspaceAgendaService
        {
            public List<WorkspaceAgendaQuery> Queries { get; } = new();

            public Task<WorkspaceAgendaResult> GetAgendaAsync(WorkspaceAgendaQuery query, CancellationToken ct = default)
            {
                Queries.Add(query);
                return Task.FromResult(Result(Array.Empty<WorkspaceAgendaItem>(), query, Array.Empty<string>()));
            }
        }

        /// Applies the window and the lookback THE QUERY ASKED FOR, following the contract's documented
        /// rules — including the exclusive `!inWindow` guard that is what makes an item due earlier today
        /// belong to the window bucket once rather than to both. It is a stand-in for the contract, not a
        /// copy of TAB 5's implementation: it holds no paging, no scope rule and no time-zone policy.
        private sealed class WindowedAgenda : IWorkspaceAgendaService
        {
            private readonly List<(WorkspaceAgendaItem Item, DateTime LocalAt, bool Late)> _all = new();
            private readonly List<string> _degraded = new();

            public WindowedAgenda WithTask(int id, string title, DateTime localDue, bool? late = null)
            {
                _all.Add((Build(id, title, localDue, AgendaItemType.Task, "Tasks",
                        late ?? localDue < DateTime.Now),
                    localDue, late ?? localDue < DateTime.Now));
                return this;
            }

            public WindowedAgenda WithEvent(int id, string title, DateTime localStart)
            {
                // IsOverdue is false for every calendar row — the contract's rule, mirrored here.
                _all.Add((Build(id, title, localStart, AgendaItemType.CalendarEvent, "Calendar", false),
                    localStart, false));
                return this;
            }

            public WindowedAgenda Degraded(string source) { _degraded.Add(source); return this; }

            public Task<WorkspaceAgendaResult> GetAgendaAsync(WorkspaceAgendaQuery q, CancellationToken ct = default)
            {
                var from = q.FromLocalDate;
                var to = q.ToLocalDate;                                   // inclusive, per the contract
                var floor = q.IncludeOverdue && q.OverdueLookbackDays > 0
                    ? from.AddDays(-Math.Clamp(q.OverdueLookbackDays, 0, 365))
                    : (DateOnly?)null;

                var kept = new List<WorkspaceAgendaItem>();
                foreach (var (item, localAt, late) in _all)
                {
                    var on = DateOnly.FromDateTime(localAt);
                    bool inWindow = on >= from && on <= to;
                    bool lateOutside = floor is DateOnly f && late && !inWindow && on >= f;

                    if (inWindow || lateOutside) kept.Add(item);
                }

                var ordered = kept
                    .OrderBy(i => i.SortKeyUtc).ThenBy(i => i.ItemType).ThenBy(i => i.SourceId)
                    .ToList();

                return Task.FromResult(Result(ordered, q, _degraded));
            }

            private static WorkspaceAgendaItem Build(
                int id, string title, DateTime localAt, AgendaItemType type, string module, bool late)
            {
                var start = Instant(localAt);
                return new WorkspaceAgendaItem
                {
                    ItemType = type,
                    SourceId = id,
                    Title = title,
                    Start = start,
                    End = null,
                    IsAllDay = false,
                    SourceModule = module,
                    EntityCode = module,
                    DeepLink = $"/{module}/Index?id={id}",
                    IsOverdue = late,
                    IsCompleted = false,
                    IsRedacted = false,
                    TimeZoneId = TimeZoneInfo.Local.Id,
                    SortKeyUtc = start.SortKeyUtc(TimeZoneInfo.Local),
                };
            }
        }

        private sealed class ThrowingAgenda : IWorkspaceAgendaService
        {
            public Task<WorkspaceAgendaResult> GetAgendaAsync(WorkspaceAgendaQuery query, CancellationToken ct = default) =>
                throw new InvalidOperationException("the calendar is not deployed here");
        }

        private static WorkspaceAgendaResult Result(
            IReadOnlyList<WorkspaceAgendaItem> items, WorkspaceAgendaQuery q, IReadOnlyList<string> degraded) =>
            new()
            {
                Items = items,
                TotalMatched = items.Count,
                Page = 1,
                PageSize = q.PageSize,
                FromLocalDate = q.FromLocalDate,
                ToLocalDate = q.ToLocalDate,
                TimeZoneId = TimeZoneInfo.Local.Id,
                DegradedSources = degraded,
                OverdueFromLocalDate = q.IncludeOverdue && q.OverdueLookbackDays > 0
                    ? q.FromLocalDate.AddDays(-q.OverdueLookbackDays)
                    : null,
            };

        // ---- the pieces the Workspace needs but this file is not about --------------------------

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

            // Added when IWorkspaceNotificationSource grew paging. The double still means the same
            // thing it always did - this caller has NO notifications - so a page of them is empty and
            // the total is zero. Returning anything else would make a "no notifications" fixture
            // assert against notifications.
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
