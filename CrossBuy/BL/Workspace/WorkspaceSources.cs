using CrossBuy.BL.Platform;
using Microsoft.Extensions.DependencyInjection;
using CrossBuy.BL.Reporting;
using CrossBuy.Models.Context;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL.Workspace
{
    // ============================================================================================
    // CrossBusiness Workspace — SOURCE ADAPTERS
    //
    // This file is the ONLY place in the Workspace where a DbContext appears, and every read in it is against
    // the PLATFORM's own tables (Notifications, Employee, Companies) — never a module's.
    //
    // WHY THE ADAPTERS EXIST AT ALL. Phase 1 requires the Workspace screen to perform no direct database read.
    // Two facts have no read service to consume:
    //   * notifications — INotificationService is a WRITER (NotifyAsync) and exposes no query;
    //   * the signed-in employee's and company's display names.
    // The choice was to leave a DbContext in the read model, or to put these two reads behind contracts and
    // give the read model none. The second is what makes the rule literally true rather than nearly true, and
    // it also makes each read swappable — a future INotificationQueryService replaces the adapter and the
    // Workspace does not change.
    //
    // WHAT IS NOT HERE, DELIBERATELY: no Tasks table, no Calendar table, no Comm table, no Report table. Those
    // all have services, and the Workspace consumes the services.
    // ============================================================================================

    // ---- notifications -------------------------------------------------------------------------
    public sealed class PlatformNotificationWorkspaceSource : IWorkspaceNotificationSource
    {
        private readonly CrossDbContext _db;
        private readonly TimeProvider _clock;

        public PlatformNotificationWorkspaceSource(CrossDbContext db, TimeProvider? clock = null)
        { _db = db; _clock = clock ?? TimeProvider.System; }

        public bool IsAvailable => true;

        public async Task<IReadOnlyList<WorkspaceNotification>> GetAsync(
            BusinessContext context, bool unreadOnly, int take, CancellationToken cancellationToken = default)
        {
            if (context.EmployeeId is not > 0) return Array.Empty<WorkspaceNotification>();

            var now = _clock.GetUtcNow().UtcDateTime;
            var arabic = WorkspaceCulture.IsArabic();

            // Company AND recipient are both in the WHERE clause. Not filtered after loading — a row belonging
            // to another tenant or another person is never materialised in the first place.
            var q = _db.Notifications.AsNoTracking()
                .Where(n => n.CompanyID == context.CompanyId
                            && n.RecipientEmployeeID == context.EmployeeId!.Value
                            // Expiry evaluated at READ time, never by a sweeper: an expiry that depends on a
                            // background job keeps working when the job stops.
                            && (n.ExpiresAt == null || n.ExpiresAt > now));

            if (unreadOnly) q = q.Where(n => !n.IsRead);

            var rows = await q
                .OrderByDescending(n => n.ID)
                .Take(Math.Clamp(take, 1, 100))
                .ToListAsync(cancellationToken);

            var actors = await ResolveActorsAsync(rows, cancellationToken);
            return rows.Select(n => Project(n, actors)).ToList();
        }

        // THE PEOPLE BEHIND THE PAGE, in ONE query, after the page is already bounded.
        //
        // Not a join on the notification query: that would re-read Employee for every repeated actor
        // and tie the list’s shape to a navigation. Not a lookup per row either - that is the N+1
        // this adapter exists to avoid. Employee is one of the three platform tables this file is
        // allowed to read, so nothing about the "no module reads" rule changes.
        private async Task<Dictionary<int, (string? Name, string? Avatar)>> ResolveActorsAsync(
            IReadOnlyList<Models.Context.Admin.Notification> rows, CancellationToken cancellationToken)
        {
            var ids = rows.Where(r => r.ActorEmployeeID is > 0)
                          .Select(r => r.ActorEmployeeID!.Value)
                          .Distinct().ToList();
            if (ids.Count == 0) return new Dictionary<int, (string? Name, string? Avatar)>();

            var arabic = WorkspaceCulture.IsArabic();
            var people = await _db.Employee.AsNoTracking()
                .Where(e => ids.Contains(e.ID))
                .Select(e => new { e.ID, e.FullName, e.FullNameEn, e.ProfileImage })
                .ToListAsync(cancellationToken);

            return people.ToDictionary(
                e => e.ID,
                e => (WorkspaceCulture.Pick(e.FullName, e.FullNameEn, arabic), e.ProfileImage));
        }

        // ONE PROJECTION FOR BOTH READS. The take-based read and the paged one produce the same rows,
        // and a second copy of this mapping is a second place for a tone or a language pick to drift —
        // which on a notification list means two screens disagreeing about whether something is urgent.
        //
        // `arabic` is read from the ambient culture inside the projection rather than passed, so a
        // caller cannot hand it the wrong one.
        private static WorkspaceNotification Project(
            Models.Context.Admin.Notification n,
            IReadOnlyDictionary<int, (string? Name, string? Avatar)> actors)
        {
            var arabic = System.Globalization.CultureInfo.CurrentUICulture
                .TwoLetterISOLanguageName == "ar";

            return new WorkspaceNotification
            {
                Id = n.ID,
                Title = WorkspaceCulture.Pick(n.TitleAr, n.TitleEn, arabic) ?? "",
                Body = WorkspaceCulture.Pick(n.BodyAr, n.BodyEn, arabic),
                Category = n.Category,
                IsRead = n.IsRead,
                At = n.CreatedAt,
                Url = n.Url,
                ActorEmployeeId = n.ActorEmployeeID,
                ActorName = n.ActorEmployeeID is > 0 && actors.TryGetValue(n.ActorEmployeeID.Value, out var a)
                    ? a.Name : null,
                ActorAvatarUrl = n.ActorEmployeeID is > 0 && actors.TryGetValue(n.ActorEmployeeID.Value, out var b)
                    ? b.Avatar : null,
                Tone = n.Priority?.ToLowerInvariant() switch
                {
                    "critical" => WorkspaceTone.Critical,
                    "high" => WorkspaceTone.Warn,
                    _ => WorkspaceTone.Info,
                },
            };
        }

        // ONE QUERY SHAPE FOR BOTH, so the page and its count can never disagree about what they are
        // counting. A filter added to one and forgotten in the other is how a pager ends up offering a
        // page that renders empty.
        private IQueryable<Models.Context.Admin.Notification> Scope(
            BusinessContext context, bool unreadOnly, DateTime now)
        {
            var q = _db.Notifications.AsNoTracking()
                .Where(n => n.CompanyID == context.CompanyId
                            && n.RecipientEmployeeID == context.EmployeeId!.Value
                            // Expiry evaluated at READ time, never by a sweeper: an expiry that depends
                            // on a background job keeps working when the job stops. It has to be in the
                            // COUNT as well — my first version of this helper left it out, which would
                            // have had the pager offering pages of rows the list does not show.
                            && (n.ExpiresAt == null || n.ExpiresAt > now));

            return unreadOnly ? q.Where(n => !n.IsRead) : q;
        }

        public async Task<IReadOnlyList<WorkspaceNotification>> GetPageAsync(
            BusinessContext context, bool unreadOnly, int skip, int take,
            CancellationToken cancellationToken = default)
        {
            if (context.EmployeeId is not > 0) return Array.Empty<WorkspaceNotification>();

            var now = _clock.GetUtcNow().UtcDateTime;
            var rows = await Scope(context, unreadOnly, now)
                .OrderByDescending(n => n.ID)
                .Skip(Math.Max(0, skip))
                .Take(Math.Clamp(take, 1, 100))
                .ToListAsync(cancellationToken);

            var actors = await ResolveActorsAsync(rows, cancellationToken);
            return rows.Select(n => Project(n, actors)).ToList();
        }

        public async Task<int> CountAsync(BusinessContext context, bool unreadOnly,
            CancellationToken cancellationToken = default)
        {
            if (context.EmployeeId is not > 0) return 0;
            return await Scope(context, unreadOnly, _clock.GetUtcNow().UtcDateTime).CountAsync(cancellationToken);
        }

        public async Task<int> CountUnreadAsync(BusinessContext context, CancellationToken cancellationToken = default)
        {
            if (context.EmployeeId is not > 0) return 0;
            var now = _clock.GetUtcNow().UtcDateTime;

            return await _db.Notifications.AsNoTracking().CountAsync(
                n => n.CompanyID == context.CompanyId
                     && n.RecipientEmployeeID == context.EmployeeId!.Value
                     && !n.IsRead
                     && (n.ExpiresAt == null || n.ExpiresAt > now), cancellationToken);
        }
    }

    // ---- identity ------------------------------------------------------------------------------
    public sealed class EmployeeWorkspaceIdentityResolver : IWorkspaceIdentityResolver
    {
        private readonly CrossDbContext _db;

        public EmployeeWorkspaceIdentityResolver(CrossDbContext db) { _db = db; }

        public async Task<(string EmployeeName, string? CompanyName)> ResolveAsync(
            BusinessContext context, CancellationToken cancellationToken = default)
        {
            var arabic = WorkspaceCulture.IsArabic();
            var name = "";
            string? company = null;

            if (context.EmployeeId is > 0)
            {
                name = await _db.Employee.AsNoTracking()
                    .Where(e => e.ID == context.EmployeeId!.Value && e.EmpCompanyID == context.CompanyId)
                    .Select(e => arabic ? e.FullName : (e.FullNameEn ?? e.FullName))
                    .FirstOrDefaultAsync(cancellationToken) ?? "";
            }

            company = await _db.Companies.AsNoTracking()
                .Where(c => c.CompanyID == context.CompanyId)
                // ComoanyNameAr is the existing (misspelled) production column, referenced as-is: renaming it is
                // out of scope, and aliasing it here would hide which column is actually read.
                .Select(c => arabic ? (c.ComoanyNameAr ?? c.CompanyName) : c.CompanyName)
                .FirstOrDefaultAsync(cancellationToken);

            return (name, company);
        }
    }

    // ---- recent activity -----------------------------------------------------------------------
    //
    // "WHAT CHANGED?" IS A BUSINESS-EVENT QUESTION, AND IT IS ANSWERED BY THE PLATFORM TIMELINE.
    //
    // This panel used to be built from the caller's own NOTIFICATIONS. The reasoning recorded here was that
    // ITimelineProjectionService answers "what happened to THIS RECORD" and needs an entity to be about,
    // whereas a workspace feed answers "what happened around me" - so the notification store was the only
    // thing already filtered to the recipient.
    //
    // THAT REASONING IS NOW OBSOLETE. The platform exposes GetRecentForContextAsync: a cross-entity,
    // company-scoped, permission-filtered recent feed. It applies the SAME four filters the per-entity read
    // applies, with one deliberate difference the Workspace depends on - an entity the caller may not view is
    // SKIPPED rather than throwing, so one unreadable record cannot blank the panel or turn it into a probe
    // for what exists.
    //
    // WHY THE SWAP MATTERS RATHER THAN BEING COSMETIC. A notification is something somebody decided to TELL
    // you; a business event is something that HAPPENED. The old feed could only ever show the subset of
    // activity that had a notification producer wired, and it re-rendered the Notifications panel's own rows
    // beside it - two panels, one source. This one reports the governed history that actually exists.
    //
    // THE WORKSPACE STILL COMPOSES NOTHING. It does not read BusinessEvents, does not judge visibility, does
    // not resolve routes: the row's Url arrives already resolved through IEntityRegistry, which is why no
    // module route is spelled anywhere in this file.
    public sealed class TimelineActivityWorkspaceSource : IWorkspaceActivitySource
    {
        // The dashboard asks "what changed lately", not "everything the platform retains". Seven days keeps
        // the panel about the current working week; the platform clamps anything larger to its own
        // MaxRecentLookbackDays, so this can only ever be narrower than the governed ceiling, never wider.
        // PUBLIC because it is a stated product policy, not an implementation detail: the regression
        // tests assert the request carries THIS window, and a reader asking "how far back does my
        // activity panel reach?" should find the answer without opening another tab's file.
        public const int LookbackDays = 7;

        private readonly IServiceProvider _services;

        public TimelineActivityWorkspaceSource(IServiceProvider services) { _services = services; }

        public string SourceName => "Timeline";

        public async Task<IReadOnlyList<WorkspaceActivityItem>> GetAsync(
            BusinessContext context, int take, CancellationToken cancellationToken = default)
        {
            // RESOLVED OPTIONALLY, LIKE EVERY OTHER CROSS-MODULE CONTRACT THIS TAB CONSUMES. Taking
            // ITimelineProjectionService as a constructor parameter would make AddCrossBusinessWorkspace
            // un-validatable on its own — the platform registers that service in Program.cs, not here — and
            // the Workspace must not register another tab's implementation to close its own graph. That is
            // exactly the duplicate-producer defect the Reporting favourites source was withdrawn for.
            var timeline = _services.GetService<ITimelineProjectionService>();

            // ...but UNLIKE the favourites source, absence is NOT answered with an empty list. Favourites
            // degrade to fewer rows; an activity feed that cannot reach the platform would be claiming
            // "nothing changed", which is a different sentence from "we could not find out". Throwing puts
            // the panel into TemporaryFailure with a reason, which is the honest state.
            if (timeline == null)
                throw new InvalidOperationException(
                    "The platform timeline is not registered in this environment, so recent activity cannot " +
                    "be read. Register ITimelineProjectionService (see the Platform block in Program.cs).");

            // No local guard on company or take. The platform read fails closed on an unresolved company and
            // clamps take itself; re-deciding either here would be a second copy of a rule this tab does not
            // own, and the copy is what drifts.
            var rows = await timeline.GetRecentForContextAsync(
                context, DateTime.UtcNow.AddDays(-LookbackDays), take, cancellationToken);

            var arabic = WorkspaceCulture.IsArabic();

            // ORDER IS NOT RE-APPLIED. The feed is already CreatedAt DESC, EventId DESC - a total order with a
            // unique tiebreak. Re-sorting on the timestamp alone would lose that tiebreak and let two events
            // sharing an instant swap places between renders.
            return rows.Select(t => new WorkspaceActivityItem
            {
                Title = WorkspaceCulture.Pick(t.TitleAr, t.TitleEn, arabic) ?? t.EventType,
                Detail = WorkspaceCulture.Pick(t.DescriptionAr, t.DescriptionEn, arabic),
                Actor = t.ActorDisplayName,
                ActorAvatarUrl = t.ActorAvatarUrl,
                At = t.CreatedAt,

                // The presenter's own icon and colour, so a row looks the same here as on the record's own
                // timeline. Nothing is re-derived from the event type.
                Icon = t.Icon,
                Tone = ToneOf(t.Color),

                // Resolved by the platform through IEntityRegistry. The Workspace never builds this.
                Url = t.Url,
            }).ToList();
        }

        // The presenter speaks Metronic's colour vocabulary; the Workspace speaks tones. This is the only
        // translation, and it maps onto the tones the dashboard already renders.
        private static WorkspaceTone ToneOf(string? color) => color?.ToLowerInvariant() switch
        {
            "success" => WorkspaceTone.Ok,
            "warning" => WorkspaceTone.Warn,
            "danger" => WorkspaceTone.Critical,
            "info" or "primary" => WorkspaceTone.Info,
            _ => WorkspaceTone.Neutral,
        };
    }

    // ---- favourites ----------------------------------------------------------------------------
    //
    // RETIRED AND REMOVED: ReportFavoritesWorkspaceSource (an IWorkspaceFavoritesSource) used to live here.
    //
    // It called IReportLibraryService.GetFavoritesAsync on EVERY dashboard load and projected the rows onto
    // WorkspaceDashboard.Favorites - a panel NO VIEW RENDERS. One database round trip per load of the first
    // screen every user sees, for an audience of nobody. Its rows also carried Url = "/Workspace/Reports",
    // the generic-list destination removed from every other Workspace row, invisible only because the panel
    // was never drawn.
    //
    // The note below spared it when the report SOURCE was withdrawn, on the grounds that it was "a different
    // seam feeding a different panel". There is no such panel, so that reasoning does not hold and the same
    // duplicate-producer argument applies: Reporting already contributes pinned reports through
    // ReportingWorkspaceSource, with working /Reports/Viewer urls.
    //
    // The IWorkspaceFavoritesSource SEAM survives - a module may still contribute - but a contributor's rows
    // would not render either. A Favourites CARD has to come first. See WorkspaceRegistration for the
    // registration this removal pairs with, and WorkspaceFavouritesWithdrawnTests for the guard.

    // ---- reporting links -------------------------------------------------------------------------
    //
    // RETIRED AND REMOVED: ReportLibraryWorkspaceReportSource (an IWorkspaceReportSource) used to live here.
    //
    // It re-derived report favourites from IReportLibraryService.GetFavoritesAsync — the very same call
    // Reporting's own ReportingWorkspaceSource makes. Once both were registered, every pinned report reached
    // the Reports panel TWICE: once from Reporting with a working /Reports/Viewer url, and once from here with
    // Url = null. Reporting owns report favourites and report-library semantics, so the Workspace's copy was
    // the one to go. See the NOTE in WorkspaceRegistration and H-1 in
    // docs/platform/Stage-Reporting-UI-05-Workspace-Integration.md, which predicted this exactly.
    //
    // Its Favorite rows are now contributed by Reporting. Its Shortcut rows (the permission-filtered catalogue
    // via IReportService.BrowseAsync) are NOT contributed by anything today — recorded as an open item for the
    // Reporting owner rather than re-implemented here, because reviving a second producer of report-library
    // facts inside the Workspace is the defect this removal resolves.
    //
    // The Favourites panel was ALSO withdrawn, later and for a different reason - see the retirement note
    // above. This paragraph used to read "the Favourites PANEL is unaffected ... a different seam feeding a
    // different panel, and is not a duplicate of anything", which was true about the seam and false about
    // the panel: no view ever rendered it.

    // ---- shared culture helpers ------------------------------------------------------------------
    internal static class WorkspaceCulture
    {
        public static bool IsArabic() =>
            System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ar";

        public static string? Pick(string? ar, string? en, bool arabic) => arabic ? (ar ?? en) : (en ?? ar);
    }
}
