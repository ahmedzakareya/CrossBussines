using CrossBuy.BL.Workspace;
using CrossBuy.Models.Context.Reporting;
using CrossBuy.Models.Platform;

namespace CrossBuy.BL.Reporting
{
    // =============================================================================================
    // Reporting Platform — R3 PHASE 5: THE WORKSPACE CONTRIBUTION.
    //
    // THIS IS THE ONLY FILE IN THE REPORTING PLATFORM THAT REFERENCES CrossBuy.BL.Workspace, and that is a
    // deliberate containment rather than an accident of layering.
    //
    // The Workspace is another tab's product, developed concurrently in the same working tree. Its contracts
    // move: during this increment WorkspaceNavigation.Build gained a parameter and broke the tree for everyone
    // who had a call site. Reporting has exactly ONE call site into that product, so a contract change there
    // costs one file here — not a screen, not a view model, not the presenter.
    //
    //     Reporting view models are Reporting's own (ReportPanel<T>, ReportCardModel, …). They deliberately do
    //     not reuse WorkspacePanel<T>, even though the two are shaped alike. Sharing them would put a second
    //     product's release cycle inside this one's UI.
    //
    // ────────────────────────────────────────────────────────────────────────────────────────────
    // DIRECTION OF DEPENDENCY: Reporting contributes; the Workspace consumes. The Workspace publishes
    // IWorkspaceReportSource precisely so a module can hand it links without it learning anything about
    // reports. It gets links. It never sees a definition, a dataset, a renderer or a permission key.
    //
    // WHAT IS CONTRIBUTED, and the one judgement call:
    //
    //   Favourite     — the reports this person pinned.
    //   Recent        — the reports this person actually ran, most recent first, de-duplicated by code.
    //   Saved         — saved layouts, as shortcuts straight to the configured view.
    //   Shortcut      — the permission-filtered catalogue: a starting point for somebody who has pinned
    //                   nothing. Its previous producer lived in the Workspace and was withdrawn as a duplicate
    //                   library-fact producer; Reporting owns catalogue facts, so the kind moved here rather
    //                   than being revived there. See the SHORTCUT block for why it does not use BrowseAsync.
    //   FAILED RUNS   — a run that ERRORED, contributed as a Recent link flagged IsStale.
    //
    // The brief says "failed operational reports where appropriate". The judgement: a failed run is worth
    // surfacing to THE PERSON WHO RAN IT and to nobody else. A failed report is not an operational alert — it
    // is usually a bad parameter — and broadcasting other people's failures onto a shared dashboard would turn
    // the Workspace into a noticeboard of colleagues' mistakes. So failures are included only from the
    // caller's OWN history, which is all IReportHistoryService returns for a non-administrator anyway.
    //
    // ────────────────────────────────────────────────────────────────────────────────────────────
    // AUTHORIZATION. This adapter performs none, and must not: it reads through IReportLibraryService,
    // IReportHistoryService and IReportTemplateService, each of which applies the report gate itself. A link
    // therefore cannot appear for a report the caller may not run.
    //
    // The links point at /Reports/Viewer, which authorizes again on arrival. So even a STALE link — a pinned
    // report whose permission was revoked yesterday — leads to a 404, not to data. That is why a link whose
    // definition no longer resolves is shown and flagged rather than hidden: the user can unpin it, and it
    // cannot leak anything.
    // =============================================================================================
    public sealed class ReportingWorkspaceSource : IWorkspaceReportSource
    {
        // Small deliberately. This is a dashboard panel, not the Reports Center; the Workspace trims further.
        private const int RecentRuns = 20;
        private const int MaxPerKind = 6;

        private readonly IReportLibraryService _library;
        private readonly IReportHistoryService _history;
        private readonly IReportTemplateService _templates;
        private readonly IReportCatalog _catalog;
        private readonly IReportAuthorizationService _authorization;

        public ReportingWorkspaceSource(
            IReportLibraryService library,
            IReportHistoryService history,
            IReportTemplateService templates,
            IReportCatalog catalog,
            IReportAuthorizationService authorization)
        {
            _library = library;
            _history = history;
            _templates = templates;
            _catalog = catalog;
            _authorization = authorization;
        }

        public string SourceName => "Reporting";

        // Constructor-injected rather than resolved optionally, so this is always true once the adapter itself
        // is registered. The registration IS the availability signal: AddCrossBusinessReporting adds this line,
        // a deployment without Reporting never runs it, and the Workspace sees no contributor at all.
        public bool IsAvailable => true;

        public async Task<IReadOnlyList<WorkspaceReportLink>> GetAsync(
            BusinessContext context, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);

            // Fail closed and QUIETLY: an unresolved company contributes nothing. Throwing would push the whole
            // Workspace Reports panel into TemporaryFailure and invite a retry that cannot succeed.
            if (context.CompanyId <= 0) return Array.Empty<WorkspaceReportLink>();

            bool arabic = System.Globalization.CultureInfo.CurrentUICulture
                .TwoLetterISOLanguageName == "ar";

            var links = new List<WorkspaceReportLink>();

            // ---- FAVOURITES --------------------------------------------------------------------------
            foreach (var favorite in Take(await Safe(() => _library.GetFavoritesAsync(context, cancellationToken))))
            {
                var definition = Resolve(favorite.ReportCode);
                links.Add(new WorkspaceReportLink
                {
                    Kind = WorkspaceReportKind.Favorite,
                    Label = definition?.Title(arabic) ?? favorite.ReportCode,
                    Sub = definition?.Module,
                    Icon = definition?.Icon ?? "ki-outline ki-star",
                    Url = ViewerUrl(favorite.ReportCode, favorite.TemplateId),

                    // A pinned report whose definition is gone — a module withdrawn, a code renamed. Shown so it
                    // can be unpinned; harmless because the destination authorizes on arrival.
                    IsStale = definition is null,
                });
            }

            // ---- RECENT (including this person's own failures) ---------------------------------------
            //
            // De-duplicated BY REPORT CODE. Running the same report eleven times while tuning a date range is
            // one entry in a "recent" list, not eleven — otherwise the panel shows one afternoon's work and
            // nothing else the person did all week.
            var runs = await Safe(() => _history.QueryAsync(
                new ReportHistoryQuery { Take = RecentRuns }, context, cancellationToken));

            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var run in runs)
            {
                if (!seen.Add(run.ReportCode)) continue;
                if (seen.Count > MaxPerKind) break;

                bool failed = run.Status != ReportRunStatus.Succeeded;
                var definition = Resolve(run.ReportCode);

                links.Add(new WorkspaceReportLink
                {
                    Kind = WorkspaceReportKind.Recent,
                    Label = (arabic ? run.ReportTitleAr : run.ReportTitleEn)
                            ?? definition?.Title(arabic) ?? run.ReportCode,

                    // The failure reason CODE, never the engine's message: a Workspace tile is read by people
                    // who did not ask for a diagnostic, and the message can quote a parameter value.
                    Sub = failed ? (run.ErrorCode ?? "failed") : definition?.Module,

                    Icon = failed ? "ki-outline ki-information-5"
                                  : definition?.Icon ?? "ki-outline ki-chart-simple",
                    Url = ViewerUrl(run.ReportCode, run.TemplateId),
                    At = run.StartedAt,

                    // A failed run reuses the stale flag rather than adding a Kind. The Workspace renders
                    // IsStale as "needs attention", which is the right treatment for both, and adding a member
                    // to another tab's enum would be a change to their contract.
                    IsStale = failed || definition is null,
                });
            }

            // ---- SAVED LAYOUTS as shortcuts ----------------------------------------------------------
            //
            // Walks the catalog rather than the template table, because ListAsync applies the report gate per
            // code. Iterating templates directly would need a query this adapter has no business writing.
            int savedAdded = 0;
            foreach (var definition in _catalog.GetDefinitions())
            {
                if (savedAdded >= MaxPerKind) break;

                foreach (var template in await Safe(() => _templates.ListAsync(definition.Code, context, cancellationToken)))
                {
                    // Personal and Team layouts only. A Company or Platform layout is the default everybody
                    // already gets by opening the report, so pinning it as a "shortcut" adds a row and no
                    // information.
                    if (template.Scope is not (ReportTemplateScope.Personal or ReportTemplateScope.Team)) continue;

                    links.Add(new WorkspaceReportLink
                    {
                        Kind = WorkspaceReportKind.Saved,
                        Label = arabic ? template.Name : (template.NameEn ?? template.Name),
                        Sub = definition.Title(arabic),
                        Icon = "ki-outline ki-questionnaire-tablet",
                        Url = ViewerUrl(definition.Code, template.Id),
                        At = template.UpdatedAt,
                    });

                    if (++savedAdded >= MaxPerKind) break;
                }
            }

            // ---- SHORTCUT — the permission-filtered catalogue -----------------------------------------
            //
            // The kind exists to give A STARTING POINT TO SOMEBODY WHO HAS PINNED NOTHING. Its only previous
            // producer was the Workspace's own ReportLibraryWorkspaceReportSource, withdrawn by TAB 3 because it
            // was a SECOND producer of report-library facts and double-emitted every favourite with a null URL.
            // Reporting owns catalogue/library facts, so the kind's home is here. There is still exactly ONE
            // IWorkspaceReportSource; this adds a kind to it, not a producer beside it.
            //
            // AUTHORIZATION comes from IReportAuthorizationService.FilterVisibleAsync — the SAME authority the
            // Reports Center browses through, given the caller's OWN context. This adapter still decides nothing:
            // the catalog is a pure unauthorised registry, so unlike the library/history/template reads above
            // (which gate internally) the gate has to be ASKED for explicitly here. Asking the authority is not
            // deciding; the company guard inside it is also what keeps a cross-company caller out.
            //
            // DELIBERATELY NOT IReportService.BrowseAsync, which is what the withdrawn source used. Two reasons:
            // it resolves the context from the HTTP accessor and would ignore the context this contract is
            // handed (wrong for any non-request caller), and per the removal note it caused a report run to be
            // RECORDED on every dashboard render, which then fed back into this panel's own Recent group.
            // FilterVisibleAsync writes nothing.
            //
            // Codes already contributed above are skipped. That is a SELECTION rule, not the dedup workaround
            // the brief forbids: "discover a report" is meaningless for one the person has already pinned or
            // just run, and the forbidden dedup was the cross-producer kind that removing the duplicate source
            // fixed. Within Shortcut every code still appears at most once.
            var alreadyLinked = new HashSet<string>(
                links.Where(l => l.Kind is WorkspaceReportKind.Favorite or WorkspaceReportKind.Recent)
                     .Select(CodeOf)
                     .Where(c => c.Length > 0),
                StringComparer.Ordinal);

            var visible = await Safe(() => _authorization.FilterVisibleAsync(
                _catalog.GetDefinitions(), context, cancellationToken));

            int shortcutsAdded = 0;
            foreach (var definition in visible.OrderBy(d => d.SortOrder).ThenBy(d => d.Code, StringComparer.Ordinal))
            {
                if (shortcutsAdded >= MaxPerKind) break;
                if (!alreadyLinked.Add(definition.Code)) continue;

                links.Add(new WorkspaceReportLink
                {
                    Kind = WorkspaceReportKind.Shortcut,
                    Label = definition.Title(arabic),
                    Sub = definition.Module,
                    Icon = definition.Icon ?? "ki-outline ki-rocket",

                    // No templateId: a shortcut opens the report itself, at its default layout.
                    Url = ViewerUrl(definition.Code, templateId: null),

                    // Never stale by construction — the definition came FROM the catalogue, so it resolves, and
                    // it passed the gate a moment ago.
                    IsStale = false,
                });
                shortcutsAdded++;
            }

            return links;
        }

        // The report code carried by a link, recovered from the URL this class built. Cheaper and safer than
        // threading a parallel list: ViewerUrl is the single place the shape is defined, so the two cannot drift.
        private static string CodeOf(WorkspaceReportLink link)
        {
            const string prefix = "/Reports/Viewer/";
            var url = link.Url;
            if (string.IsNullOrEmpty(url) || !url.StartsWith(prefix, StringComparison.Ordinal)) return "";

            var rest = url.Substring(prefix.Length);
            var query = rest.IndexOf('?');
            if (query >= 0) rest = rest.Substring(0, query);
            return Uri.UnescapeDataString(rest);
        }

        // The ONE place the Workspace's links are built, so a route change is a single edit. `run=true` is
        // deliberately absent: a dashboard tile takes the user to the report, it does not execute it. A panel
        // whose tiles each run a query on click is how a dashboard becomes a load test.
        private static string ViewerUrl(string reportCode, int? templateId) =>
            templateId is > 0
                ? $"/Reports/Viewer/{Uri.EscapeDataString(reportCode)}?templateId={templateId.Value}"
                : $"/Reports/Viewer/{Uri.EscapeDataString(reportCode)}";

        private ReportDefinition? Resolve(string code) =>
            _catalog.TryGetDefinition(code, out var definition) ? definition : null;

        private static IEnumerable<T> Take<T>(IReadOnlyList<T> items) => items.Take(MaxPerKind);

        // A contributor that throws must not take the Workspace's Reports panel down. The Workspace already
        // guards each source, but returning empty is a better answer than an exception it has to classify:
        // "Reporting had nothing to say" is true and harmless, whereas a TemporaryFailure invites a retry.
        private static async Task<IReadOnlyList<T>> Safe<T>(Func<Task<IReadOnlyList<T>>> read)
        {
            try { return await read(); }
            catch (OperationCanceledException) { throw; }
            catch { return Array.Empty<T>(); }
        }
    }
}
