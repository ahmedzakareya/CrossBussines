using CrossBuy.BL.Communication;
using CrossBuy.BL.Platform;
using CrossBuy.BL.Reporting;
using CrossBuy.BL.TasksCalendar;
using CrossBuy.Models.Communication;
using CrossBuy.Models.Platform;

namespace CrossBuy.BL.Workspace
{
    // ============================================================================================
    // CrossBusiness Workspace — THE READ MODEL
    //
    // NOTE WHAT IS ABSENT FROM THIS CLASS: there is no CrossDbContext. Every fact arrives through a contract —
    // ITaskService, IWorkspaceAgendaService, IWorkspaceNotificationSource, ICommMentionService,
    // IWorkspaceReportSource, IWorkspaceFavoritesSource, IWorkspaceActivitySource, IWorkspaceIdentityResolver.
    // That is what makes "the Workspace performs no direct database read" a structural fact rather than a
    // promise: the type it would need is not injected.
    //
    // EVERY PANEL IS INDEPENDENTLY GUARDED. A workspace is the first screen a user sees; one undeployed
    // service must produce one dark panel, never a 500 that hides five working ones. So each source is awaited
    // separately and a failure becomes a typed state with a reason.
    //
    // THE STATE A PANEL REPORTS IS PART OF THE CONTRACT, not a UI detail:
    //     Unavailable       — not registered/deployed. Permanent until a deployment changes.
    //     AccessDenied      — authenticated but not permitted. No deployment will change it.
    //     TemporaryFailure  — the source threw. May succeed next request.
    //     Empty             — answered, nothing to show.
    // Collapsing any of these into Empty is how a broken deployment looks like a quiet week.
    // ============================================================================================
    public class WorkspaceService : IWorkspaceService
    {
        // Five to ten rows is the brief. Eight keeps the card readable above the fold on a laptop
        // without becoming a second work list — if everything is urgent, nothing is.
        private const int AttentionShown = 8;
        private const int WorkItemsShown = 8;
        private const int NotificationsShown = 8;
        private const int MentionsShown = 6;
        private const int ActivityShown = 10;
        private const int FavoritesShown = 8;
        private const int AgendaShown = 8;
        private const int ReportsShown = 8;

        // ------------------------------------------------------------------------------------------------
        // THE AGENDA OVERDUE POLICY — the Workspace-owned half of the UAT defect.
        //
        // THE DEFECT. The agenda range opened at `from = today`, so a task whose due date had already passed
        // was never REQUESTED. The one screen a person opens to find late work was the one place late work
        // could not appear — plainly visible against the populated CrossBuyDev UAT dataset, which seeds a
        // month of overdue tasks on purpose.
        //
        // WHAT IS OWNED HERE, AND WHAT IS NOT. Gathering late work is TAB 5's contract
        // (WorkspaceAgendaQuery.IncludeOverdue / OverdueLookbackDays): it pulls overdue tasks in ABOVE the
        // window, keeps their real due dates, excludes anything already inside the window so nothing arrives
        // twice, and leaves Calendar alone because a meeting that has happened is history rather than late
        // work. None of that is re-implemented here. The Workspace decides only WHETHER to ask for late work
        // and HOW FAR BACK — the request policy — and then how to present three bands in one summary.
        //
        // THE HORIZON IS TAB 5'S, DELIBERATELY. The brief's rule is to use an overdue horizon the repository
        // already defines, and it now defines exactly one: WorkspaceAgendaService.DefaultOverdueLookbackDays.
        // It is referenced as a symbol rather than copied as a literal, because a second number maintained in
        // the Workspace would be a second horizon, and the two would disagree the first time either moved.
        // The Workspace's decision is to STATE it rather than inherit it silently, so that reading this call
        // tells you how much history the screen asks for without opening another tab's file.
        //
        // IT IS NOT SCALED BY `days`. /Workspace/Agenda?days=1 means "today", and late work must not vanish
        // because the reader narrowed the FORWARD range — that would reintroduce the same defect by a
        // different door. TAB 5 anchors the floor to the window START for the same reason.
        //
        // WHAT THIS IS NOT: no due date is rewritten, nothing is re-dated to today, no unbounded history is
        // requested, and the task service's own company/employee scope is the only scope in play.
        // ------------------------------------------------------------------------------------------------
        // PUBLIC because it is a stated product policy, not an implementation detail: the regression tests
        // assert the request carries THIS number, and a reader asking "how far back does my agenda reach?"
        // should be able to find the answer without reading another tab's internals. It is initialised FROM
        // TAB 5's constant rather than copied, so the two cannot drift apart.
        public const int AgendaOverdueLookbackDays = WorkspaceAgendaService.DefaultOverdueLookbackDays;

        // The contract's own maximum page. Asking for it means the widened window is not silently trimmed by
        // a second, tighter cap invented here; anything beyond it is still reported through the panel's Total
        // and through the service's own DegradedSources note.
        private const int AgendaPageSize = 200;

        // Slots the dashboard's 8-row agenda summary reserves for the past. Without a reserve, a head-of-list
        // cap on a chronological list fills every slot with the OLDEST late work and pushes today and
        // tomorrow off the panel entirely. Fewer than half keeps the summary forward-looking while making it
        // impossible for late work to be invisible; unused slots are handed back to either side, so neither
        // band can starve the other.
        private const int AgendaOverdueShown = 3;

        private readonly IBusinessContextAccessor _contexts;
        private readonly IServiceProvider _services;
        private readonly IWorkspaceNotificationSource _notifications;
        private readonly IWorkspaceIdentityResolver _identity;
        private readonly IEnumerable<IWorkspaceFavoritesSource> _favoriteSources;
        private readonly IEnumerable<IWorkspaceActivitySource> _activitySources;
        private readonly IEnumerable<IWorkspaceReportSource> _reportSources;
        private readonly ILogger<WorkspaceService> _log;

        public WorkspaceService(
            IBusinessContextAccessor contexts,
            IServiceProvider services,
            IWorkspaceNotificationSource notifications,
            IWorkspaceIdentityResolver identity,
            IEnumerable<IWorkspaceFavoritesSource> favoriteSources,
            IEnumerable<IWorkspaceActivitySource> activitySources,
            IEnumerable<IWorkspaceReportSource> reportSources,
            ILogger<WorkspaceService> log)
        {
            _contexts = contexts;
            _services = services;
            _notifications = notifications;
            _identity = identity;
            _favoriteSources = favoriteSources;
            _activitySources = activitySources;
            _reportSources = reportSources;
            _log = log;
        }

        // ------------------------------------------------------------------------------------------------
        public async Task<WorkspaceCapabilities> GetCapabilitiesAsync(CancellationToken cancellationToken = default)
        {
            var context = await _contexts.TryGetCurrentAsync(cancellationToken);
            if (context == null || context.CompanyId <= 0) return WorkspaceCapabilities.None;

            // A capability is "the contract resolves AND the caller can be identified". Tasks needs an employee
            // id because every task query is scoped to a person; a session without one cannot show My Work and
            // the nav entry is hidden rather than shown leading to an empty screen.
            return new WorkspaceCapabilities
            {
                Tasks = _services.GetService<ITaskService>() != null && context.EmployeeId is > 0,
                Calendar = _services.GetService<ICalendarService>() != null && context.EmployeeId is > 0,
                Reporting = _reportSources.Any(s => s.IsAvailable),
                Communication = _services.GetService<ICommMentionService>() != null,
                Notifications = _notifications.IsAvailable && context.EmployeeId is > 0,
            };
        }

        public async Task<WorkspaceDashboard> GetDashboardAsync(CancellationToken cancellationToken = default)
        {
            var context = await _contexts.TryGetCurrentAsync(cancellationToken);
            if (context == null || context.CompanyId <= 0)
                return new WorkspaceDashboard { EmployeeName = "", IsUnresolved = true };

            var capabilities = await GetCapabilitiesAsync(cancellationToken);
            var (employeeName, companyName) = await SafeIdentityAsync(context, cancellationToken);

            var work = await SafeAsync("My Work", () => GetMyWorkAsync(context, cancellationToken));

            // The agenda's own service already returns a typed panel with all five states, so it is passed
            // through rather than re-wrapped — re-wrapping would flatten PartiallyAvailable into Ready.
            //
            // TrimAgenda, not Trim: the range now opens BEFORE today, and a plain head-of-list cap on a
            // chronological list would fill all eight summary slots with the oldest overdue rows and push
            // today and tomorrow off the dashboard entirely.
            var agenda = TrimAgenda(await SafeAsync("Agenda", () => LoadAgendaAsync(context, 7, cancellationToken)),
                AgendaShown);

            var notifications = await SafeAsync("Notifications",
                () => LoadNotificationsAsync(context, false, NotificationsShown, cancellationToken));
            var mentions = await SafeAsync("Mentions", () => LoadMentionsAsync(context, MentionsShown, cancellationToken));
            var favorites = await SafeAsync("Favorites", () => LoadFavoritesAsync(context, cancellationToken));
            var reports = Trim(await SafeAsync("Reports", () => LoadReportsAsync(context, cancellationToken)), ReportsShown);
            var activity = await SafeAsync("Recent Activity", () => LoadActivityAsync(context, cancellationToken));
            var approvals = await SafeAsync("Approvals", () => LoadApprovalsAsync(context, cancellationToken));

            var unreadNotifications = await SafeCountAsync(() => _notifications.CountUnreadAsync(context, cancellationToken));
            var unreadMentions = await SafeCountAsync(() => CountUnreadMentionsAsync(context, cancellationToken));

            // Composed LAST, from panels that have already resolved. It issues no query of its own —
            // see ComposeAttention for why this is a composition rather than a sixth source.
            var attention = ComposeAttention(work, approvals, mentions, DateTime.Now);

            var metrics = await SafeMetricsAsync(context, unreadNotifications, unreadMentions, cancellationToken);

            // Only genuinely BLOCKED panels are reported in the diagnostics strip. A partially-available agenda
            // already explains itself on the panel and does not belong in a "these are dark" list.
            var dark = new (WorkspacePanelState State, string Name)[]
                {
                    (work.State, "My Work"), (agenda.State, "Agenda"),
                    (notifications.State, "Notifications"), (mentions.State, "Mentions"),
                    (favorites.State, "Favorites"), (reports.State, "Reports"),
                    (activity.State, "Recent Activity"),
                    (approvals.State, "Approvals"),
                }
                .Where(p => p.State == WorkspacePanelState.Unavailable)
                .Select(p => p.Name)
                .ToList();

            return new WorkspaceDashboard
            {
                EmployeeName = employeeName,
                EmployeeId = context.EmployeeId ?? 0,
                CompanyId = context.CompanyId,
                CompanyName = companyName,
                Capabilities = capabilities,
                Metrics = metrics,
                Attention = attention,
                MyWork = work,
                Agenda = agenda,
                Notifications = notifications,
                Mentions = mentions,
                Favorites = favorites,
                Reports = reports,
                Activity = activity,
                Approvals = approvals,
                QuickActions = WorkspaceNavigation.QuickActions(capabilities),
                UnreadNotifications = unreadNotifications,
                UnreadMentions = unreadMentions,
                PendingApprovals = approvals.Total ?? 0,
                UnavailablePanels = dark,
            };
        }

        public async Task<WorkspacePanel<WorkspaceNotification>> GetNotificationsAsync(
            bool unreadOnly = false, int take = 20, CancellationToken cancellationToken = default)
        {
            var context = await _contexts.TryGetCurrentAsync(cancellationToken);
            if (context == null || context.CompanyId <= 0)
                return WorkspacePanel<WorkspaceNotification>.AccessDenied(
                    "No company is resolved for this session.");

            return await SafeAsync("Notifications",
                () => LoadNotificationsAsync(context, unreadOnly, take, cancellationToken));
        }

        public async Task<WorkspacePanel<WorkspaceMention>> GetMentionsAsync(
            int take = 20, CancellationToken cancellationToken = default)
        {
            var context = await _contexts.TryGetCurrentAsync(cancellationToken);
            if (context == null || context.CompanyId <= 0)
                return WorkspacePanel<WorkspaceMention>.AccessDenied("No company is resolved for this session.");

            return await SafeAsync("Mentions", () => LoadMentionsAsync(context, take, cancellationToken));
        }

        public async Task<WorkspacePanel<WorkspaceAgendaRow>> GetAgendaAsync(
            int days = 7, CancellationToken cancellationToken = default)
        {
            var context = await _contexts.TryGetCurrentAsync(cancellationToken);
            if (context == null || context.CompanyId <= 0)
                return WorkspacePanel<WorkspaceAgendaRow>.AccessDenied("No company is resolved for this session.");

            return await SafeAsync("Agenda", () => LoadAgendaAsync(context, days, cancellationToken));
        }

        // ================================================================================================
        // AGENDA — Phase 3. CONSUMES TAB 4's IWorkspaceAgendaService and renders its result.
        //
        // The Workspace composes NOTHING here: it does not read tasks, does not read calendar events, does not
        // merge, does not sort and does not decide overdue. All of that is TAB 4's service, which already
        // unions the two sources over ITaskService + ICalendarService. This method builds a display projection
        // and nothing else.
        // ================================================================================================
        private async Task<WorkspacePanel<WorkspaceAgendaRow>> LoadAgendaAsync(
            BusinessContext context, int days, CancellationToken cancellationToken)
        {
            var agenda = _services.GetService<IWorkspaceAgendaService>();
            if (agenda == null)
                return WorkspacePanel<WorkspaceAgendaRow>.Unavailable(
                    "The Tasks & Calendar agenda service is not registered in this environment.");

            if (context.EmployeeId is not > 0)
                return WorkspacePanel<WorkspaceAgendaRow>.AccessDenied(
                    "An agenda is always somebody's agenda, and this session has no employee.");

            var from = DateOnly.FromDateTime(DateTime.Now.Date);
            var to = from.AddDays(Math.Clamp(days, 1, 60));

            WorkspaceAgendaResult result;
            try
            {
                result = await agenda.GetAgendaAsync(new WorkspaceAgendaQuery
                {
                    CompanyId = context.CompanyId,
                    EmployeeId = context.EmployeeId!.Value,
                    FromLocalDate = from,
                    ToLocalDate = to,

                    // The service REFUSES an unresolved zone rather than falling back to server-local — that is
                    // its owner's decision, and the Workspace honours it by supplying the zone explicitly.
                    TimeZoneId = TimeZoneInfo.Local.Id,

                    // The window still STARTS today — `from` is unchanged. Late work is pulled in above it by
                    // the agenda service, keeping its real due date, so overdue / today / upcoming stay three
                    // distinguishable things. The flag is opt-in on the contract precisely so a caller wanting
                    // a literal date range is not given extra rows; the Workspace wants them, because a
                    // dashboard that hides what is already late is the one thing it must not do.
                    IncludeOverdue = true,

                    // Stated, not inherited. See AgendaOverdueLookbackDays for why the number is TAB 5's.
                    OverdueLookbackDays = AgendaOverdueLookbackDays,

                    PageSize = AgendaPageSize,
                }, cancellationToken);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                // Includes the service's own refusals (unresolved company/employee/zone). Surfaced as a
                // temporary failure with its message rather than swallowed into an empty agenda.
                _log.LogWarning(ex, "Workspace agenda could not be composed.");
                return WorkspacePanel<WorkspaceAgendaRow>.TemporaryFailure(ex.Message);
            }

            var arabic = WorkspaceCulture.IsArabic();

            // The band is computed HERE, once, from the local date the service already resolved — not in the
            // view and not per panel. Three readers (the day grouping, the dashboard's cap, and any business
            // rule that follows) then agree by construction instead of each re-deriving a date comparison.
            var todayLocal = DateTime.Now.Date;

            var rows = result.Items.Select(i => new WorkspaceAgendaRow
            {
                // A redacted row keeps its SLOT but loses its title: "busy at 14:00" is the useful half and is
                // not private. That rule is the service's; the Workspace only renders it.
                Title = i.IsRedacted
                    ? (arabic ? "مشغول" : "Busy")
                    : i.Title,
                TypeLabelAr = i.ItemType == AgendaItemType.Task ? "مهمة" : "موعد",
                TypeLabelEn = i.ItemType == AgendaItemType.Task ? "Task" : "Event",
                LocalAt = LocalOf(i.Start),
                LocalEndsAt = i.End is null ? null : LocalOf(i.End),
                AllDay = i.IsAllDay,
                Overdue = i.IsOverdue,
                Completed = i.IsCompleted,
                Redacted = i.IsRedacted,
                Band = BandOf(LocalOf(i.Start), todayLocal),
                SourceModule = i.SourceModule,
                DeepLink = i.DeepLink,
                Tone = i.IsCompleted ? WorkspaceTone.Ok
                     : i.IsOverdue ? WorkspaceTone.Critical
                     : i.ItemType == AgendaItemType.Task ? WorkspaceTone.Info
                     : WorkspaceTone.Neutral,
            }).ToList();

            // DegradedSources is the service's own partial-availability signal. Mapped straight onto the
            // panel's PartiallyAvailable state rather than being flattened away — "showing the agenda without
            // Calendar" is a different promise from "here is your agenda".
            if (result.DegradedSources.Count > 0)
                return WorkspacePanel<WorkspaceAgendaRow>.Partial(rows, result.DegradedSources,
                    $"Showing the agenda without {string.Join(", ", result.DegradedSources)} — those entries " +
                    "are missing from this list.");

            return WorkspacePanel<WorkspaceAgendaRow>.From(rows, result.TotalMatched);
        }

        // OVERDUE / TODAY / UPCOMING, by the row's own LOCAL DATE.
        //
        // It reads the date the service already resolved and compares nothing else — in particular it does
        // NOT re-derive "is this late", which is the service's IsOverdue and a different question (a task due
        // 09:00 today, read at 14:00, is Band = Today and Overdue = true; both are correct and both are kept).
        private static WorkspaceAgendaBand BandOf(DateTime localAt, DateTime today) =>
            localAt.Date < today ? WorkspaceAgendaBand.Overdue
            : localAt.Date == today ? WorkspaceAgendaBand.Today
            : WorkspaceAgendaBand.Upcoming;

        // An AgendaInstant is EITHER a UTC instant carrying the viewer's offset, OR a bare all-day local date
        // that has no zone and must never be shifted. Reading the right one is the whole point of the type.
        private static DateTime LocalOf(AgendaInstant instant) =>
            instant.Kind == AgendaTimeKind.AllDayDate && instant.Date.HasValue
                ? instant.Date.Value.ToDateTime(TimeOnly.MinValue)
                : (instant.Local?.DateTime ?? instant.Utc ?? DateTime.MinValue);

        public async Task<WorkspacePanel<WorkspaceReportLink>> GetReportsAsync(
            CancellationToken cancellationToken = default)
        {
            var context = await _contexts.TryGetCurrentAsync(cancellationToken);
            if (context == null || context.CompanyId <= 0)
                return WorkspacePanel<WorkspaceReportLink>.AccessDenied("No company is resolved for this session.");

            return await SafeAsync("Reports", () => LoadReportsAsync(context, cancellationToken));
        }

        // ================================================================================================
        // MY WORK — consumes ITaskService.
        // ================================================================================================
        private async Task<WorkspacePanel<WorkspaceWorkItem>> GetMyWorkAsync(
            BusinessContext context, CancellationToken cancellationToken)
        {
            var tasks = _services.GetService<ITaskService>();
            if (tasks == null)
                return WorkspacePanel<WorkspaceWorkItem>.Unavailable(
                    "The Tasks module is not registered in this environment.");

            if (context.EmployeeId is not > 0)
                return WorkspacePanel<WorkspaceWorkItem>.AccessDenied(
                    "This session has no employee, so personal work cannot be resolved.");

            // OPEN WORK ONLY, AND THE FILTER IS THE TASKS MODULE'S OWN.
            //
            // This asked for `status: null`, so a DONE task competed for one of the eight rows. With
            // `sort: "due"` — which orders by `DueDate ?? MaxValue` ascending — long-finished work carrying an
            // early due date sorts to the TOP, so a panel whose whole job is "what is still on me" could be
            // filled with work that is already closed. Same defect class as the agenda window: the panel could
            // not show the thing it exists to show.
            //
            // `view: "inprogress"` is NOT a UI tab name being borrowed. It is TaskService's own open-work
            // predicate — `Status == "InProgress" || Status == "New"` — and New ∪ InProgress is exactly
            // "not Done", because Done is the only terminal status TaskItem has (TaskOverdueSweepService says
            // so explicitly, and pins the day a Cancelled status is added). Re-deriving "open" here with a
            // client-side `!= "Done"` would be a second copy of a rule the Tasks module already owns, and the
            // copy is what drifts the day Cancelled lands.
            const string OpenWork = "inprogress";

            var rows = await tasks.GetTasksAsync(
                context.CompanyId, scope: "mine", currentEmployeeId: context.EmployeeId!.Value,
                status: null, priority: null, q: null, page: 1, pageSize: WorkItemsShown,
                view: OpenWork, assignee: null, sort: "due");

            // The SAME filters the rows were fetched with, so the count and the list cannot disagree —
            // TaskService shares one WHERE builder between list and count for precisely this reason. Without
            // it the panel shows eight rows and silently implies that is all of them.
            var total = await SafeCountAsync(() => tasks.CountTasksAsync(
                context.CompanyId, scope: "mine", currentEmployeeId: context.EmployeeId!.Value,
                status: null, priority: null, q: null, view: OpenWork, assignee: null));

            var now = DateTime.Now;
            var items = rows.Select(r => new WorkspaceWorkItem
            {
                Id = r.Id,
                Title = r.Title,
                Status = r.Status,
                Priority = r.Priority,
                Due = r.DueDate,
                Assignee = r.AssigneeName,

                // THE TASKS MODULE OWNS ITS OWN DEEP LINK. This used to emit "/Tasks/Index?open={id}", a shape
                // invented here and used by nothing else in the product: TasksController.Index binds
                // status/priority/q/page/pageSize/view/assignee/sort and has never bound `open`. The module's
                // own producer is TaskNotificationService.DeepLink, which is what its notifications and TAB 5's
                // agenda already emit — so the same task now resolves to the same URL from every surface
                // instead of My Work pointing somewhere the module does not answer.
                Url = TaskNotificationService.DeepLink(r.Id),

                Tone = ToneForTask(r.Status, r.Priority, r.DueDate, now),
                DueHint = DueHint(r.DueDate, now),
            }).ToList();

            return WorkspacePanel<WorkspaceWorkItem>.From(items, total);
        }

        // WHERE A TASK TILE ACTUALLY GOES.
        //
        // Every task metric used to link to "/Workspace/Index#my-work" — the page the reader is already on.
        // Clicking "Overdue: 12" scrolled you down eight rows and answered nothing, because My Work is capped
        // and unfiltered. The Tasks module already implements these exact three questions as first-class list
        // views (TaskService.Filter: "overdue" -> due < now && not Done, "urgent" -> Urgent && not Done,
        // "inprogress" -> New | InProgress), so the tile now hands the reader to the module's own answer.
        //
        // The list route is spelled here rather than resolved because no navigation resolver owns module LIST
        // views — TaskLinkResolver resolves a task's linked ENTITY, and TaskNotificationService.DeepLink
        // resolves a single record. Both of those are used where they apply; this is the remaining gap, and it
        // is centralised in one method so a future resolver replaces one line.
        private static string TasksView(string view) => $"/Tasks/Index?view={view}";

        // The ONE place a task's state becomes a colour. Overdue beats priority: a late task needs attention
        // regardless of how it was originally ranked.
        private static WorkspaceTone ToneForTask(string? status, string? priority, DateTime? due, DateTime now)
        {
            if (string.Equals(status, "Done", StringComparison.OrdinalIgnoreCase)) return WorkspaceTone.Ok;
            if (due.HasValue && due.Value.Date < now.Date) return WorkspaceTone.Critical;
            if (string.Equals(priority, "Urgent", StringComparison.OrdinalIgnoreCase)) return WorkspaceTone.Warn;
            if (due.HasValue && due.Value.Date == now.Date) return WorkspaceTone.Warn;
            return WorkspaceTone.Neutral;
        }

        private static string? DueHint(DateTime? due, DateTime now)
        {
            if (!due.HasValue) return null;
            var days = (due.Value.Date - now.Date).Days;
            return days switch
            {
                < 0 => $"{-days}d late",
                0 => "today",
                1 => "tomorrow",
                <= 7 => $"in {days}d",
                _ => due.Value.ToString("yyyy-MM-dd"),
            };
        }

        // ================================================================================================
        // NOTIFICATIONS + MENTIONS — Phase 5. Consumption-only; neither activates anything.
        // ================================================================================================
        // How many approval rows the dashboard panel shows. The TOTAL is reported separately, so a
        // capped panel never implies the queue is this short.
        private const int ApprovalsShown = 5;

        private async Task<WorkspacePanel<WorkspaceApprovalItem>> LoadApprovalsAsync(
            BusinessContext context, CancellationToken cancellationToken)
        {
            var inbox = _services.GetService<CrossBuy.BL.Approvals.IApprovalInboxService>();
            if (inbox == null)
                return WorkspacePanel<WorkspaceApprovalItem>.Unavailable(
                    "The approvals read platform is not registered in this environment.");

            if (context.EmployeeId is not > 0)
                return WorkspacePanel<WorkspaceApprovalItem>.AccessDenied(
                    "This session has no employee, so pending approvals cannot be resolved.");

            // take is applied by the READ PLATFORM, not here, so the rows the panel shows are the newest
            // across all three silos rather than the newest of whichever silo happened to be listed first.
            var page = await inbox.GetPendingForCurrentApproverAsync(
                context, context.EmployeeId!.Value, ApprovalsShown, cancellationToken);

            var now = DateTime.UtcNow;
            var items = page.Rows.Select(r => new WorkspaceApprovalItem
            {
                Reference = r.Reference,
                Silo = r.Silo,
                ApprovalType = r.ApprovalType,
                // The module carries bilingual pairs; the panel picks by current culture the same way every
                // other Workspace panel does.
                Title = PickTitle(r),
                Requester = Pick(r.RequesterNameAr, r.RequesterNameEn),
                Submitted = r.SubmittedAt,
                AgeDays = r.SubmittedAt.HasValue
                    ? Math.Max(0, (int)(now.Date - r.SubmittedAt.Value.Date).TotalDays)
                    : null,
                // The MODULE supplied the pair; this only formats it. No navigation framework is
                // introduced, and Workspace never names a module route itself.
                Url = ApprovalUrl(r.Navigation),
            }).ToList();

            // Total is the whole inbox, not the page: From() maps an empty list to the Empty state and
            // keeps the real total, so a capped panel never implies the queue is this short.
            return WorkspacePanel<WorkspaceApprovalItem>.From(items, page.TotalPending);
        }

        // The module names its own route AND, where the document needs identifying, its own route values.
        // Workspace still invents nothing - it concatenates what the module supplied. Without this, a silo
        // whose screen is addressed by id would link to a LIST and the approver would have to hunt for the
        // document they were sent to act on.
        private static string ApprovalUrl(CrossBuy.BL.Approvals.ApprovalNavigationTarget nav)
        {
            var path = "/" + nav.Controller + "/" + nav.Action;
            if (nav.RouteValues == null || nav.RouteValues.Count == 0) return path;
        
            var query = string.Join("&", nav.RouteValues
                .Where(kv => !string.IsNullOrWhiteSpace(kv.Value))
                .Select(kv => Uri.EscapeDataString(kv.Key) + "=" + Uri.EscapeDataString(kv.Value!)));
            return string.IsNullOrEmpty(query) ? path : path + "?" + query;
        }

        private static string PickTitle(CrossBuy.BL.Approvals.PendingApprovalRow row)
        {
            var picked = Pick(row.TitleAr, row.TitleEn);
            // Employee requests carry their kind as the discriminator rather than a title, and inventory
            // carries a DocType. Falling back to it keeps every row labelled with something real instead of
            // inventing a localized string in a BL service.
            return string.IsNullOrWhiteSpace(picked) ? (row.ApprovalType ?? "") : picked;
        }

        private static string? Pick(string? ar, string? en)
        {
            bool isAr = System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ar";
            return isAr ? (ar ?? en) : (en ?? ar);
        }

        private async Task<WorkspacePanel<WorkspaceNotification>> LoadNotificationsAsync(
            BusinessContext context, bool unreadOnly, int take, CancellationToken cancellationToken)
        {
            if (!_notifications.IsAvailable)
                return WorkspacePanel<WorkspaceNotification>.Unavailable(
                    "The notification store is not available in this environment.");

            if (context.EmployeeId is not > 0)
                return WorkspacePanel<WorkspaceNotification>.AccessDenied(
                    "This session has no employee, so personal notifications cannot be resolved.");

            var rows = await _notifications.GetAsync(context, unreadOnly, take, cancellationToken);
            return WorkspacePanel<WorkspaceNotification>.From(rows);
        }

        // MENTIONS — the platform is resolved OPTIONALLY and is never activated as a side effect of rendering
        // a screen. Activation is the Communication tab's gated decision.
        private async Task<WorkspacePanel<WorkspaceMention>> LoadMentionsAsync(
            BusinessContext context, int take, CancellationToken cancellationToken)
        {
            var mentions = _services.GetService<ICommMentionService>();
            if (mentions == null)
                return WorkspacePanel<WorkspaceMention>.Unavailable(
                    "The Communication Platform is not activated in this environment. Mentions appear once " +
                    "AddCommunicationPlatform is registered and communication_platform_slice_001.sql is applied.");

            if (context.EmployeeId is not > 0)
                return WorkspacePanel<WorkspaceMention>.AccessDenied(
                    "This session has no employee, so mentions cannot be resolved.");

            var page = await mentions.GetHistoryAsync(context,
                new CommPageRequest { PageSize = Math.Clamp(take, 1, 50) }, cancellationToken);

            var items = page.Items.Select(m => new WorkspaceMention
            {
                MentionId = m.MentionId,
                EntityLabel = m.Entity.Key,
                Excerpt = m.Excerpt,
                MentionedBy = m.MentionedBy?.Display(WorkspaceCulture.IsArabic()),
                ViaKind = m.ViaKind,
                At = m.CreatedAt,
                Url = null,
            }).ToList();

            return WorkspacePanel<WorkspaceMention>.From(items);
        }

        private async Task<int> CountUnreadMentionsAsync(BusinessContext context, CancellationToken ct)
        {
            var mentions = _services.GetService<ICommMentionService>();
            if (mentions == null) return 0;
            return await mentions.GetUnreadCountAsync(context, ct);
        }

        // ================================================================================================
        // REPORTS — Phase 4. Extension sources only; no data source, renderer or engine is touched.
        // ================================================================================================
        private async Task<WorkspacePanel<WorkspaceReportLink>> LoadReportsAsync(
            BusinessContext context, CancellationToken cancellationToken)
        {
            var available = _reportSources.Where(s => s.IsAvailable).ToList();

            if (available.Count == 0)
                return WorkspacePanel<WorkspaceReportLink>.Unavailable(
                    "The Reporting platform is not registered in this environment, so report shortcuts cannot " +
                    "be listed.");

            var links = new List<WorkspaceReportLink>();
            var failed = new List<string>();

            foreach (var source in available)
            {
                try
                {
                    links.AddRange(await source.GetAsync(context, cancellationToken));
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    _log.LogWarning(ex, "Workspace report source {Source} failed and was skipped.", source.SourceName);
                    failed.Add(source.SourceName);
                }
            }

            if (failed.Count == available.Count)
                return WorkspacePanel<WorkspaceReportLink>.TemporaryFailure(
                    $"Report shortcuts could not be loaded ({string.Join(", ", failed)}).");

            // Favourites first, then shortcuts: a pinned report is a stronger signal of intent than a
            // catalogue entry the caller merely has permission to open.
            var ordered = links.OrderBy(l => (int)l.Kind).ThenBy(l => l.Label, StringComparer.CurrentCulture).ToList();

            return failed.Count > 0
                ? WorkspacePanel<WorkspaceReportLink>.Partial(ordered, failed,
                    $"Showing report shortcuts without {string.Join(", ", failed)}.")
                : WorkspacePanel<WorkspaceReportLink>.From(ordered);
        }

        // ================================================================================================
        // FAVOURITES + ACTIVITY
        // ================================================================================================
        private async Task<WorkspacePanel<WorkspaceFavorite>> LoadFavoritesAsync(
            BusinessContext context, CancellationToken cancellationToken)
        {
            var all = new List<WorkspaceFavorite>();
            var failed = new List<string>();

            foreach (var source in _favoriteSources)
            {
                try { all.AddRange(await source.GetAsync(context, cancellationToken)); }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    _log.LogWarning(ex, "Workspace favourites source {Source} failed.", source.SourceName);
                    failed.Add(source.SourceName);
                }
            }

            var items = all.Take(FavoritesShown).ToList();
            return failed.Count > 0 && items.Count > 0
                ? WorkspacePanel<WorkspaceFavorite>.Partial(items, failed,
                    $"Showing favourites without {string.Join(", ", failed)}.")
                : WorkspacePanel<WorkspaceFavorite>.From(items, all.Count);
        }

        private async Task<WorkspacePanel<WorkspaceActivityItem>> LoadActivityAsync(
            BusinessContext context, CancellationToken cancellationToken)
        {
            var all = new List<WorkspaceActivityItem>();
            var failed = new List<string>();

            foreach (var source in _activitySources)
            {
                try { all.AddRange(await source.GetAsync(context, ActivityShown, cancellationToken)); }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    _log.LogWarning(ex, "Workspace activity source {Source} failed.", source.SourceName);
                    failed.Add(source.SourceName);
                }
            }

            // Merged newest-first ACROSS sources — sorted here, because the merged stream is what the user reads.
            var items = all.OrderByDescending(a => a.At ?? DateTime.MinValue).Take(ActivityShown).ToList();

            // EVERY source failed. That is NOT an empty week, and it must not render as one: "nothing changed"
            // and "we could not find out what changed" are different sentences, and collapsing them is how a
            // broken deployment looks quiet. This became the ordinary failure path the moment activity had a
            // SINGLE authoritative producer — with one source, any platform fault landed on the old
            // `failed.Count > 0 && items.Count > 0` guard, fell through, and reported Empty.
            //
            // Same rule the Reports panel already applies when all of its sources fail.
            if (failed.Count > 0 && items.Count == 0)
                return WorkspacePanel<WorkspaceActivityItem>.TemporaryFailure(
                    $"Recent activity could not be loaded ({string.Join(", ", failed)}). " +
                    "This is usually temporary — try again.");

            return failed.Count > 0
                ? WorkspacePanel<WorkspaceActivityItem>.Partial(items, failed,
                    $"Showing activity without {string.Join(", ", failed)}.")
                : WorkspacePanel<WorkspaceActivityItem>.From(items);
        }

        // ================================================================================================
        // ATTENTION — "what requires my attention?"
        //
        // A COMPOSITION OVER PANELS THAT HAVE ALREADY LOADED. It takes no service, opens no connection and
        // reads no module table: every row here is a row the dashboard is already showing somewhere else.
        // What it adds is the one thing the separate panels cannot say — the ORDER.
        //
        // THE RANKING IS THE ENUM, and that is the whole rule:
        //
        //     overdue task  >  approval waiting  >  due today  >  urgent task  >  mention
        //
        // Reason first, ALWAYS. A three-day-old approval does not overtake an overdue commitment, however
        // long it has waited — seniority inside a reason is the second key, never the first. Age breaks
        // ties within a reason; a stable identity key breaks ties within an age, so the same data always
        // produces the same order and a test can assert a position rather than "somewhere near the top".
        //
        // ONE ROW PER THING. A task can be overdue AND urgent; it appears once, under the stronger reason,
        // because a person reading a list of eight does not need to be told twice.
        //
        // STATIC AND PURE so the ranking is testable without a container, a context or a clock.
        // PUBLIC because the ranking IS the product rule here, and a rule nobody can assert from
        // outside is a rule that drifts. It takes panels and a clock and returns a panel - there is
        // nothing to mock and nothing to arrange, so the ladder is testable as a plain function.
        public static WorkspacePanel<WorkspaceAttentionItem> ComposeAttention(
            WorkspacePanel<WorkspaceWorkItem> work,
            WorkspacePanel<WorkspaceApprovalItem> approvals,
            WorkspacePanel<WorkspaceMention> mentions,
            DateTime now)
        {
            var today = now.Date;
            var candidates = new List<(WorkspaceAttentionItem Item, int Age, string Key)>();

            // ---- tasks -------------------------------------------------------------------------
            //
            // From MY WORK and not from the Agenda, deliberately: both carry the same tasks, and taking
            // them from two panels is how one commitment becomes two rows. My Work is also already
            // filtered to OPEN work, so nothing finished can reach this list.
            foreach (var w in work.Items)
            {
                var due = w.Due?.Date;
                WorkspaceAttentionReason? reason =
                    due is { } d && d < today ? WorkspaceAttentionReason.OverdueTask
                    : due is { } t && t == today ? WorkspaceAttentionReason.DueToday
                    : string.Equals(w.Priority, "Urgent", StringComparison.OrdinalIgnoreCase)
                        ? WorkspaceAttentionReason.UrgentTask
                        : null;

                if (reason is null) continue;   // open, on time, not urgent — it is work, not attention

                var age = reason == WorkspaceAttentionReason.OverdueTask && due is { } late
                    ? (today - late).Days
                    : 0;

                candidates.Add((new WorkspaceAttentionItem
                {
                    Reason = reason.Value,
                    ReasonLabelAr = ReasonAr(reason.Value),
                    ReasonLabelEn = ReasonEn(reason.Value),
                    Title = w.Title,
                    SourceAr = "المهام", SourceEn = "Tasks",
                    Due = w.Due,
                    AgeDays = reason == WorkspaceAttentionReason.OverdueTask ? age : null,
                    Priority = w.Priority,
                    Url = w.Url,
                    Tone = reason == WorkspaceAttentionReason.OverdueTask ? WorkspaceTone.Critical
                         : reason == WorkspaceAttentionReason.DueToday ? WorkspaceTone.Warn
                         : WorkspaceTone.Warn,
                    Rank = 0,
                }, age, $"task:{w.Id}"));
            }

            // ---- approvals ---------------------------------------------------------------------
            //
            // Second by reason and not first: somebody else is blocked, which is urgent, but a
            // commitment this person has ALREADY missed is more urgent still.
            foreach (var a in approvals.Items)
            {
                var age = a.AgeDays ?? 0;
                candidates.Add((new WorkspaceAttentionItem
                {
                    Reason = WorkspaceAttentionReason.ApprovalWaiting,
                    ReasonLabelAr = ReasonAr(WorkspaceAttentionReason.ApprovalWaiting),
                    ReasonLabelEn = ReasonEn(WorkspaceAttentionReason.ApprovalWaiting),
                    Title = a.Title,
                    SourceAr = "الاعتمادات", SourceEn = "Approvals",
                    Due = a.Submitted,
                    AgeDays = a.AgeDays,
                    Priority = a.ApprovalType,
                    Url = a.Url,
                    Tone = WorkspaceTone.Warn,
                    Rank = 0,
                }, age, $"approval:{a.Reference}"));
            }

            // ---- mentions ----------------------------------------------------------------------
            //
            // Last by reason: being named is a request for attention, not yet an obligation. Present
            // only when Communication is activated — a dormant platform contributes nothing here rather
            // than an apology, because the Mentions panel already states that on its own.
            foreach (var m in mentions.Items)
            {
                var age = m.At is { } at ? Math.Max(0, (today - at.Date).Days) : 0;
                candidates.Add((new WorkspaceAttentionItem
                {
                    Reason = WorkspaceAttentionReason.Mention,
                    ReasonLabelAr = ReasonAr(WorkspaceAttentionReason.Mention),
                    ReasonLabelEn = ReasonEn(WorkspaceAttentionReason.Mention),
                    Title = m.Excerpt ?? m.EntityLabel,
                    SourceAr = "التواصل", SourceEn = "Communication",
                    Due = m.At,
                    AgeDays = age,
                    Priority = null,
                    Url = m.Url,
                    Tone = WorkspaceTone.Info,
                    Rank = 0,
                }, age, $"mention:{m.MentionId}"));
            }

            // Reason, then seniority inside the reason, then identity. The third key is what makes the
            // order total: without it two same-age rows could swap places between two renders of the
            // same data, and a screenshot baseline over this card could never be verified.
            var ordered = candidates
                .GroupBy(c => c.Key)
                .Select(g => g.OrderBy(c => (int)c.Item.Reason).First())   // one row per thing
                .OrderBy(c => (int)c.Item.Reason)
                .ThenByDescending(c => c.Age)
                .ThenBy(c => c.Key, StringComparer.Ordinal)
                .ToList();

            var items = ordered
                .Take(AttentionShown)
                .Select((c, i) => new WorkspaceAttentionItem
                {
                    Reason = c.Item.Reason,
                    ReasonLabelAr = c.Item.ReasonLabelAr, ReasonLabelEn = c.Item.ReasonLabelEn,
                    Title = c.Item.Title,
                    SourceAr = c.Item.SourceAr, SourceEn = c.Item.SourceEn,
                    Due = c.Item.Due, AgeDays = c.Item.AgeDays, Priority = c.Item.Priority,
                    Url = c.Item.Url, Tone = c.Item.Tone,
                    Rank = i + 1,
                })
                .ToList();

            if (items.Count > 0)
                return WorkspacePanel<WorkspaceAttentionItem>.From(items, ordered.Count);

            // NOTHING TO SHOW IS NOT AUTOMATICALLY "NOTHING NEEDS YOU". If a contributing panel could not
            // answer, this card inherits that — otherwise a failed Tasks module would render as a calm
            // morning, which is the one thing an attention panel must never do.
            var contributing = new[] { work.State, approvals.State, mentions.State };

            if (contributing.Any(st => st == WorkspacePanelState.TemporaryFailure))
                return WorkspacePanel<WorkspaceAttentionItem>.TemporaryFailure(
                    "Some of the sources behind this list could not be read, so it may be incomplete. " +
                    "This is usually temporary — try again.");

            if (contributing.All(st => st is WorkspacePanelState.Unavailable or WorkspacePanelState.AccessDenied))
                return WorkspacePanel<WorkspaceAttentionItem>.Unavailable(
                    "None of the sources behind this list is available in this environment.");

            return WorkspacePanel<WorkspaceAttentionItem>.Empty();
        }

        // Bilingual in code rather than through resx, matching the rest of this file: these two labels are
        // produced by the read model, not by a view, and the file already carries its Arabic this way.
        private static string ReasonAr(WorkspaceAttentionReason r) => r switch
        {
            WorkspaceAttentionReason.OverdueTask => "متأخرة",
            WorkspaceAttentionReason.ApprovalWaiting => "بانتظار اعتمادك",
            WorkspaceAttentionReason.DueToday => "مستحقة اليوم",
            WorkspaceAttentionReason.UrgentTask => "عاجلة",
            _ => "إشارة إليك",
        };

        private static string ReasonEn(WorkspaceAttentionReason r) => r switch
        {
            WorkspaceAttentionReason.OverdueTask => "Overdue",
            WorkspaceAttentionReason.ApprovalWaiting => "Waiting on you",
            WorkspaceAttentionReason.DueToday => "Due today",
            WorkspaceAttentionReason.UrgentTask => "Urgent",
            _ => "You were mentioned",
        };

        // ================================================================================================
        // METRICS
        // ================================================================================================
        private async Task<IReadOnlyList<WorkspaceMetric>> SafeMetricsAsync(
            BusinessContext context, int unreadNotifications, int unreadMentions, CancellationToken ct)
        {
            var metrics = new List<WorkspaceMetric>();
            var tasks = _services.GetService<ITaskService>();

            if (tasks != null && context.EmployeeId is > 0)
            {
                try
                {
                    var kpi = await tasks.GetKpisAsync(context.CompanyId, "mine", context.EmployeeId!.Value);

                    metrics.Add(new WorkspaceMetric
                    {
                        LabelAr = "مهامي المفتوحة", LabelEn = "My open work",
                        Value = (kpi.Total - kpi.Done).ToString("N0"),
                        HintAr = $"{kpi.InProgress:N0} قيد التنفيذ", HintEn = $"{kpi.InProgress:N0} in progress",
                        Tone = WorkspaceTone.Info, Url = TasksView("inprogress"),
                    });

                    metrics.Add(new WorkspaceMetric
                    {
                        LabelAr = "متأخرة", LabelEn = "Overdue",
                        Value = kpi.Overdue.ToString("N0"),
                        HintAr = "تحتاج انتباهك", HintEn = "needs attention",
                        // Zero overdue is a GOOD state, not a neutral one — the tile reads green, not grey.
                        Tone = kpi.Overdue > 0 ? WorkspaceTone.Critical : WorkspaceTone.Ok,
                        Url = TasksView("overdue"),
                    });

                    metrics.Add(new WorkspaceMetric
                    {
                        LabelAr = "عاجلة", LabelEn = "Urgent",
                        Value = kpi.Urgent.ToString("N0"),
                        Tone = kpi.Urgent > 0 ? WorkspaceTone.Warn : WorkspaceTone.Neutral,
                        Url = TasksView("urgent"),
                    });
                }
                catch (Exception ex)
                {
                    _log.LogWarning(ex, "Workspace task metrics unavailable.");
                }
            }

            metrics.Add(new WorkspaceMetric
            {
                LabelAr = "إشعارات غير مقروءة", LabelEn = "Unread notifications",
                Value = unreadNotifications.ToString("N0"),
                Tone = unreadNotifications > 0 ? WorkspaceTone.Warn : WorkspaceTone.Ok,
                Url = "/Workspace/Notifications",
            });

            if (unreadMentions > 0)
                metrics.Add(new WorkspaceMetric
                {
                    LabelAr = "إشارات إليّ", LabelEn = "Mentions",
                    Value = unreadMentions.ToString("N0"),
                    Tone = WorkspaceTone.Warn, Url = "/Workspace/Mentions",
                });

            return metrics;
        }

        // ================================================================================================
        // helpers
        // ================================================================================================

        // A source that throws produces ONE panel in TemporaryFailure with a reason — never a failed page, and
        // never an Empty panel that would misreport a fault as "nothing to show".
        private async Task<WorkspacePanel<T>> SafeAsync<T>(string panel, Func<Task<WorkspacePanel<T>>> load)
        {
            try
            {
                return await load();
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Workspace panel {Panel} could not be loaded.", panel);
                return WorkspacePanel<T>.TemporaryFailure(
                    $"{panel} could not be loaded. This is usually temporary — try again.");
            }
        }

        private async Task<int> SafeCountAsync(Func<Task<int>> count)
        {
            try { return await count(); }
            catch (Exception ex) { _log.LogWarning(ex, "Workspace count unavailable."); return 0; }
        }

        private async Task<(string, string?)> SafeIdentityAsync(BusinessContext context, CancellationToken ct)
        {
            try { return await _identity.ResolveAsync(context, ct); }
            catch (Exception ex) { _log.LogWarning(ex, "Workspace identity unavailable."); return ("", null); }
        }

        // Caps the dashboard's agenda summary so that BOTH SIDES OF TODAY survive the cap.
        //
        // Trim would be wrong here now. The agenda list is chronological and it starts in the past, so taking
        // its first eight rows means taking the eight OLDEST late items — and the dashboard would answer "what
        // is on today?" with a month-old backlog and nothing else. That is not a hypothetical: the dense UAT
        // employee has nine overdue tasks, which is more than the eight slots on offer.
        //
        // So the past gets a RESERVE rather than the whole panel: at most AgendaOverdueShown rows, and the
        // ones NEAREST TODAY rather than the oldest, because a task that slipped yesterday is the actionable
        // one and a task that slipped seven weeks ago is the least actionable thing that could occupy a
        // summary slot. Whatever the other band does not use is handed back, so a person with no upcoming
        // work still fills the panel with their backlog and vice versa.
        //
        // Both slices are taken from an already-ordered list and re-joined in the same order, so the view's
        // day-header grouping still walks a sorted sequence. Total keeps the true match count: the panel is
        // capped, and it does not pretend otherwise.
        private static WorkspacePanel<WorkspaceAgendaRow> TrimAgenda(
            WorkspacePanel<WorkspaceAgendaRow> panel, int take)
        {
            if (panel.Items.Count <= take) return panel;

            var overdue = panel.Items.Where(r => r.Band == WorkspaceAgendaBand.Overdue).ToList();
            var ahead = panel.Items.Where(r => r.Band != WorkspaceAgendaBand.Overdue).ToList();

            int overdueSlots = Math.Min(AgendaOverdueShown, overdue.Count);

            // Hand back what the other band cannot use — in both directions.
            overdueSlots = Math.Min(overdue.Count, Math.Max(overdueSlots, take - ahead.Count));

            var items = overdue.Skip(overdue.Count - overdueSlots)      // the tail = nearest to today
                .Concat(ahead.Take(take - overdueSlots))
                .ToList();

            return new WorkspacePanel<WorkspaceAgendaRow>
            {
                State = panel.State,
                Items = items,
                Reason = panel.Reason,
                Total = panel.Total ?? panel.Items.Count,
                MissingSources = panel.MissingSources,
            };
        }

        // Caps a panel for the dashboard while PRESERVING its state — a truncated PartiallyAvailable panel is
        // still partially available, and flattening it to Ready would silently upgrade the promise.
        private static WorkspacePanel<T> Trim<T>(WorkspacePanel<T> panel, int take) =>
            panel.Items.Count <= take
                ? panel
                : new WorkspacePanel<T>
                {
                    State = panel.State,
                    Items = panel.Items.Take(take).ToList(),
                    Reason = panel.Reason,
                    Total = panel.Total ?? panel.Items.Count,
                    MissingSources = panel.MissingSources,
                };
    }
}
