using CrossBuy.Models.Platform;

namespace CrossBuy.BL.Workspace
{
    // ============================================================================================
    // CrossBusiness Workspace — CONTRACTS
    //
    // The Workspace is a READ MODEL over services that already exist. It owns no table, no business rule and
    // no writer.
    //
    //     The Workspace CONSUMES. It never re-implements, and it never queries a module's tables.
    //
    // Every fact reaches this layer through a contract: ITaskService, ICalendarService, ICommMentionService,
    // IReportLibraryService, or one of the Workspace's own extension-point interfaces below. If a fact is not
    // obtainable from a contract, the Workspace reports it unavailable — it does not go and compute it.
    // ============================================================================================

    // ---- panel state ----------------------------------------------------------------------------
    //
    // SIX states, not two. Each is a materially different thing to tell a user, and collapsing any pair is how
    // a broken deployment comes to look like a quiet week.
    public enum WorkspacePanelState
    {
        // The source answered and returned rows.
        Ready = 0,

        // The source answered, and the answer was "nothing". A normal, unremarkable outcome.
        Empty = 1,

        // The backing platform is not registered or its schema is not deployed. Permanent until a deployment
        // changes — carries an actionable Reason.
        Unavailable = 2,

        // SOME sources answered and at least one did not. The rows shown are real but incomplete, which is a
        // different promise from "this is everything". Agenda is the panel that needs this: tasks may load
        // while calendar does not.
        PartiallyAvailable = 3,

        // The caller is authenticated but not permitted. Distinct from Unavailable: nothing is broken, and no
        // deployment will change it — the answer is about this person.
        AccessDenied = 4,

        // The source threw. Distinct from Unavailable because it may succeed on the next request, so the UI
        // invites a retry rather than telling the user to call an administrator.
        TemporaryFailure = 5,
    }

    public enum WorkspaceTone
    {
        Neutral = 0,
        Info = 1,
        Ok = 2,
        Warn = 3,
        Critical = 4,
    }

    // One panel's envelope, so every panel reports its state identically and one shared partial can render the
    // five non-data states.
    public sealed class WorkspacePanel<T>
    {
        public WorkspacePanelState State { get; init; } = WorkspacePanelState.Ready;
        public IReadOnlyList<T> Items { get; init; } = Array.Empty<T>();

        // Operator/user-facing explanation for every non-Ready, non-Empty state. Required to be actionable:
        // "not deployed, apply slice X" is useful; "error" is not.
        public string? Reason { get; init; }

        public int? Total { get; init; }

        // For PartiallyAvailable: which contributing sources failed, so the UI can name what is missing rather
        // than saying "some data".
        public IReadOnlyList<string> MissingSources { get; init; } = Array.Empty<string>();

        public bool HasItems => Items.Count > 0;

        // True when the panel is showing something a user can read — including a partial answer.
        public bool IsShowable => State is WorkspacePanelState.Ready or WorkspacePanelState.PartiallyAvailable;

        // True when the panel exists but cannot serve THIS caller or THIS deployment. Drives whether a
        // navigation entry is hidden (see WorkspaceCapabilities).
        public bool IsBlocked => State is WorkspacePanelState.Unavailable or WorkspacePanelState.AccessDenied;

        public static WorkspacePanel<T> From(IReadOnlyList<T> items, int? total = null) => new()
        {
            State = items.Count == 0 ? WorkspacePanelState.Empty : WorkspacePanelState.Ready,
            Items = items,
            Total = total,
        };

        public static WorkspacePanel<T> Partial(IReadOnlyList<T> items, IReadOnlyList<string> missing,
            string reason) => new()
            {
                State = WorkspacePanelState.PartiallyAvailable,
                Items = items,
                MissingSources = missing,
                Reason = reason,
            };

        public static WorkspacePanel<T> Unavailable(string reason) =>
            new() { State = WorkspacePanelState.Unavailable, Reason = reason };

        public static WorkspacePanel<T> AccessDenied(string reason) =>
            new() { State = WorkspacePanelState.AccessDenied, Reason = reason };

        public static WorkspacePanel<T> TemporaryFailure(string reason) =>
            new() { State = WorkspacePanelState.TemporaryFailure, Reason = reason };

        public static WorkspacePanel<T> Empty() => new() { State = WorkspacePanelState.Empty };
    }

    // The model the shared _WorkspacePanelState partial renders, so the five non-data states live in ONE place
    // rather than being repeated per panel — where they would inevitably drift into looking the same.
    public sealed class WorkspacePanelStateModel
    {
        public required WorkspacePanelState State { get; init; }
        public string? Reason { get; init; }
        public IReadOnlyList<string> MissingSources { get; init; } = Array.Empty<string>();
        public string? EmptyText { get; init; }
        public string EmptyIcon { get; init; } = "ki-outline ki-information-5";

        public static WorkspacePanelStateModel For<T>(WorkspacePanel<T> panel,
            string? emptyText = null, string? emptyIcon = null) => new()
            {
                State = panel.State,
                Reason = panel.Reason,
                MissingSources = panel.MissingSources,
                EmptyText = emptyText,
                EmptyIcon = emptyIcon ?? "ki-outline ki-information-5",
            };
    }

    // ---- capabilities ---------------------------------------------------------------------------
    //
    // What this deployment + this caller can actually reach. Navigation is built FROM this, which is what makes
    // the rail permission-aware and capability-aware rather than a fixed list with dead links.
    public sealed class WorkspaceCapabilities
    {
        public bool Tasks { get; init; }            // ITaskService resolvable AND the caller has an employee id
        public bool Calendar { get; init; }         // ICalendarService resolvable
        public bool Agenda => Tasks || Calendar;    // agenda needs at least ONE contributing source
        public bool Reporting { get; init; }        // IReportLibraryService resolvable
        public bool Communication { get; init; }    // ICommMentionService resolvable (platform activated)
        public bool Notifications { get; init; }    // the platform notification source resolvable

        public static WorkspaceCapabilities None { get; } = new();
    }

    // ---- navigation -----------------------------------------------------------------------------

    public sealed class WorkspaceNavItem
    {
        public required string LabelAr { get; init; }
        public required string LabelEn { get; init; }
        public required string Icon { get; init; }
        public string Controller { get; init; } = "Workspace";
        public string Action { get; init; } = "Index";
        public string? Fragment { get; init; }
        public int? Badge { get; init; }
        public bool BadgeIsAttention { get; init; }

        // A destination that exists in the product but is not built for this release. Rendered DIMMED and
        // NON-CLICKABLE — never as an active link, per the brief.
        public bool Soon { get; init; }

        public string Label(bool arabic) => arabic ? LabelAr : LabelEn;
    }

    public sealed class WorkspaceNavSection
    {
        public required string LabelAr { get; init; }
        public required string LabelEn { get; init; }
        public IReadOnlyList<WorkspaceNavItem> Items { get; init; } = Array.Empty<WorkspaceNavItem>();
        public string Label(bool arabic) => arabic ? LabelAr : LabelEn;
    }

    // ---- item shapes ----------------------------------------------------------------------------

    public sealed class WorkspaceWorkItem
    {
        public required int Id { get; init; }
        public required string Title { get; init; }
        public string? Status { get; init; }
        public string? Priority { get; init; }
        public DateTime? Due { get; init; }
        public string? Assignee { get; init; }
        public WorkspaceTone Tone { get; init; }
        public string? Url { get; init; }
        public string? DueHint { get; init; }
    }

    // ---- AGENDA ---------------------------------------------------------------------------------
    //
    // THE WORKSPACE DEFINES NO AGENDA TYPES. It consumes TAB 4's contract:
    //
    //     CrossBuy.BL.TasksCalendar.IWorkspaceAgendaService
    //         .GetAgendaAsync(WorkspaceAgendaQuery) -> WorkspaceAgendaResult
    //
    // That service already unions task due dates with calendar events over ITaskService + ICalendarService,
    // and already carries everything this screen must display: item type, title, local time (as an
    // AgendaInstant that is either a UTC instant + offset or a bare all-day date), all-day, overdue,
    // completion, source module, a permission-safe DeepLink, an IsRedacted flag, and DegradedSources for the
    // partially-available case.
    //
    // An earlier draft of this increment defined a parallel IWorkspaceAgendaService here. That was exactly the
    // duplication the brief forbids, and it was deleted before it shipped. The Workspace's job is to RENDER
    // that result, not to compose a second one — so the only agenda type below is a display projection, and it
    // is built from their rows rather than from Task or Calendar data.
    // The three positions a row can hold RELATIVE TO TODAY, named once so the view, the dashboard's cap
    // and any future business rule read the same classification instead of each re-deriving a date
    // comparison — and drifting the first time one of them is edited.
    //
    // IT INTRODUCES NO NEW VISUAL LANGUAGE. The agenda already draws these three buckets: sticky day
    // headers name Today and Tomorrow, and a late row already carries the service's own IsOverdue chip in
    // the critical tone. This enum only gives that existing grouping a name a program can test.
    //
    // BANDED BY LOCAL DATE, deliberately, and it is NOT the same fact as WorkspaceAgendaRow.Overdue:
    //   Band    — which DAY the row sits on (yesterday / today / later). What the grouping is about.
    //   Overdue — the agenda service's instant-level truth (`due < now`, and never true for a calendar
    //             event). A task due 09:00 today, read at 14:00, is Band = Today AND Overdue = true.
    // Both are real, they answer different questions, and collapsing either into the other would lose one.
    public enum WorkspaceAgendaBand
    {
        Overdue = 0,     // local date is before today — late work carried forward
        Today = 1,
        Upcoming = 2,    // local date is after today
    }

    public sealed class WorkspaceAgendaRow
    {
        public required string Title { get; init; }
        public required string TypeLabelAr { get; init; }
        public required string TypeLabelEn { get; init; }

        // Local wall-clock for display. Taken from the AgendaInstant the service produced — the Workspace does
        // NOT convert time zones, because a second conversion here would silently disagree with the module
        // screen the user clicks through to.
        public required DateTime LocalAt { get; init; }
        public DateTime? LocalEndsAt { get; init; }

        public bool AllDay { get; init; }
        public bool Overdue { get; init; }
        public bool Completed { get; init; }
        public bool Redacted { get; init; }

        // OVERDUE / TODAY / UPCOMING for this row. See WorkspaceAgendaBand for why this is a separate fact
        // from Overdue above.
        public WorkspaceAgendaBand Band { get; init; }

        public required string SourceModule { get; init; }
        public string? DeepLink { get; init; }
        public WorkspaceTone Tone { get; init; }

        public string TypeLabel(bool arabic) => arabic ? TypeLabelAr : TypeLabelEn;
    }

    // ---- notifications / mentions ---------------------------------------------------------------

    public sealed class WorkspaceNotification
    {
        public required int Id { get; init; }
        public required string Title { get; init; }
        public string? Body { get; init; }
        public string? Category { get; init; }
        public bool IsRead { get; init; }
        public WorkspaceTone Tone { get; init; }
        public DateTime? At { get; init; }
        public string? Url { get; init; }
    }

    public sealed class WorkspaceMention
    {
        public required long MentionId { get; init; }
        public required string EntityLabel { get; init; }
        public string? Excerpt { get; init; }
        public string? MentionedBy { get; init; }
        public string? ViaKind { get; init; }
        public DateTime? At { get; init; }
        public string? Url { get; init; }
    }

    // The notification READ contract.
    //
    // It exists because INotificationService is a WRITER (NotifyAsync) and exposes no query. Rather than let
    // the Workspace read model reach for a DbContext, the read is behind this interface and its one
    // implementation is the clearly-labelled adapter. That is what keeps "the Workspace performs no direct
    // database read" literally true of the read model itself.
    public interface IWorkspaceNotificationSource
    {
        bool IsAvailable { get; }

        Task<IReadOnlyList<WorkspaceNotification>> GetAsync(
            BusinessContext context, bool unreadOnly, int take, CancellationToken cancellationToken = default);

        Task<int> CountUnreadAsync(BusinessContext context, CancellationToken cancellationToken = default);
    }

    // Resolves display identity (employee name, company name) for the shell header. Same reasoning as above:
    // one adapter owns the lookup, the read model owns none.
    public interface IWorkspaceIdentityResolver
    {
        Task<(string EmployeeName, string? CompanyName)> ResolveAsync(
            BusinessContext context, CancellationToken cancellationToken = default);
    }

    // ---- REPORTING ------------------------------------------------------------------------------
    //
    // The Workspace consumes Reporting through EXTENSION SOURCES only. It never calls a report data source, a
    // renderer, an exporter or the engine — those are Reporting internals and are out of this tab's scope.
    public enum WorkspaceReportKind
    {
        Favorite = 0,
        Recent = 1,
        Saved = 2,
        Shortcut = 3,
    }

    public sealed class WorkspaceReportLink
    {
        public required WorkspaceReportKind Kind { get; init; }
        public required string Label { get; init; }
        public string? Sub { get; init; }
        public string Icon { get; init; } = "ki-outline ki-chart-simple";
        public string? Url { get; init; }
        public DateTime? At { get; init; }

        // A pinned report whose definition no longer resolves. Shown and flagged rather than hidden, so the
        // user can unpin it.
        public bool IsStale { get; init; }
    }

    // Contributes report links of one or more kinds. A module (or the Reporting platform itself) registers one
    // of these; the Workspace learns nothing about report internals.
    public interface IWorkspaceReportSource
    {
        string SourceName { get; }
        bool IsAvailable { get; }

        Task<IReadOnlyList<WorkspaceReportLink>> GetAsync(
            BusinessContext context, CancellationToken cancellationToken = default);
    }

    // ---- favourites / activity / actions / metrics ------------------------------------------------

    public sealed class WorkspaceFavorite
    {
        public required string Label { get; init; }
        public string? Sub { get; init; }
        public string Icon { get; init; } = "ki-outline ki-star";
        public string? Url { get; init; }
        public string Source { get; init; } = "";
        public bool IsStale { get; init; }
    }

    public sealed class WorkspaceActivityItem
    {
        public required string Title { get; init; }
        public string? Detail { get; init; }
        public string? Actor { get; init; }
        public DateTime? At { get; init; }
        public string Icon { get; init; } = "ki-outline ki-abstract-26";
        public WorkspaceTone Tone { get; init; }
        public string? Url { get; init; }
    }

    public sealed class WorkspaceQuickAction
    {
        public required string LabelAr { get; init; }
        public required string LabelEn { get; init; }
        public required string Icon { get; init; }
        public required string Url { get; init; }
        public string Label(bool arabic) => arabic ? LabelAr : LabelEn;
    }

    public sealed class WorkspaceMetric
    {
        public required string LabelAr { get; init; }
        public required string LabelEn { get; init; }
        public required string Value { get; init; }
        public string? HintAr { get; init; }
        public string? HintEn { get; init; }
        public WorkspaceTone Tone { get; init; }
        public string? Url { get; init; }

        public string Label(bool arabic) => arabic ? LabelAr : LabelEn;
        public string? Hint(bool arabic) => arabic ? HintAr : HintEn;
    }

    public interface IWorkspaceFavoritesSource
    {
        string SourceName { get; }
        Task<IReadOnlyList<WorkspaceFavorite>> GetAsync(
            BusinessContext context, CancellationToken cancellationToken = default);
    }

    public interface IWorkspaceActivitySource
    {
        string SourceName { get; }
        Task<IReadOnlyList<WorkspaceActivityItem>> GetAsync(
            BusinessContext context, int take, CancellationToken cancellationToken = default);
    }

    // ---- the page model -------------------------------------------------------------------------

    public sealed class WorkspaceDashboard
    {
        public required string EmployeeName { get; init; }
        public int EmployeeId { get; init; }
        public int CompanyId { get; init; }
        public string? CompanyName { get; init; }

        public WorkspaceCapabilities Capabilities { get; init; } = WorkspaceCapabilities.None;

        public IReadOnlyList<WorkspaceMetric> Metrics { get; init; } = Array.Empty<WorkspaceMetric>();
        public WorkspacePanel<WorkspaceWorkItem> MyWork { get; init; } = WorkspacePanel<WorkspaceWorkItem>.Empty();
        public WorkspacePanel<WorkspaceAgendaRow> Agenda { get; init; } = WorkspacePanel<WorkspaceAgendaRow>.Empty();
        public WorkspacePanel<WorkspaceNotification> Notifications { get; init; } = WorkspacePanel<WorkspaceNotification>.Empty();
        public WorkspacePanel<WorkspaceMention> Mentions { get; init; } = WorkspacePanel<WorkspaceMention>.Empty();
        public WorkspacePanel<WorkspaceFavorite> Favorites { get; init; } = WorkspacePanel<WorkspaceFavorite>.Empty();
        public WorkspacePanel<WorkspaceReportLink> Reports { get; init; } = WorkspacePanel<WorkspaceReportLink>.Empty();
        public WorkspacePanel<WorkspaceActivityItem> Activity { get; init; } = WorkspacePanel<WorkspaceActivityItem>.Empty();
        public IReadOnlyList<WorkspaceQuickAction> QuickActions { get; init; } = Array.Empty<WorkspaceQuickAction>();

        public int UnreadNotifications { get; init; }
        public int UnreadMentions { get; init; }

        // Panels that could not be served, for the shell's diagnostics strip. Published rather than swallowed:
        // an operator should see that panels are dark rather than infer it from an empty screen.
        public IReadOnlyList<string> UnavailablePanels { get; init; } = Array.Empty<string>();

        // True when nothing could be resolved at all (no company). The view renders a sign-in prompt instead of
        // eight empty panels.
        public bool IsUnresolved { get; init; }
    }

    public interface IWorkspaceService
    {
        Task<WorkspaceDashboard> GetDashboardAsync(CancellationToken cancellationToken = default);

        Task<WorkspacePanel<WorkspaceNotification>> GetNotificationsAsync(
            bool unreadOnly = false, int take = 20, CancellationToken cancellationToken = default);

        Task<WorkspacePanel<WorkspaceMention>> GetMentionsAsync(
            int take = 20, CancellationToken cancellationToken = default);

        Task<WorkspacePanel<WorkspaceAgendaRow>> GetAgendaAsync(
            int days = 7, CancellationToken cancellationToken = default);

        Task<WorkspacePanel<WorkspaceReportLink>> GetReportsAsync(
            CancellationToken cancellationToken = default);

        Task<WorkspaceCapabilities> GetCapabilitiesAsync(CancellationToken cancellationToken = default);

        // NOTE: no write. The mark-as-read endpoint stays withdrawn until an approved authority exists —
        // see Stage-Workspace-Integration-05 and the handover in the delivery report.
    }
}
