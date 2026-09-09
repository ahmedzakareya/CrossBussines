using CrossBuy.BL.Platform;
using CrossBuy.Models.Context.Reporting;
using CrossBuy.Models.Platform;

namespace CrossBuy.BL.Reporting
{
    // =============================================================================================
    // Reporting Platform — R3: THE PRESENTATION LAYER for the Reports Center and the Report Viewer.
    //
    // WHY A PRESENTER EXISTS AT ALL, when there is already an API controller and a set of services.
    //
    // Because the UI safety rules in Phase 6 have to be PROVABLE, and a rule that lives in a Razor view is not.
    // "Internal columns never appear in UI metadata" is a claim about a projection; if that projection happens
    // in `.cshtml`, the only way to test it is to render HTML and grep it — which passes for the wrong reasons
    // (a column can be absent because the test data had no rows) and fails for the wrong reasons (whitespace).
    //
    // So every decision about WHAT a screen may see happens here, in one testable class, and the views are
    // dumb. The controller is dumber still: it resolves nothing, filters nothing, and owns no default.
    //
    //     Controller → Presenter → platform services → data
    //                  ▲
    //                  └── every Phase 6 invariant is asserted against THIS boundary
    //
    // ────────────────────────────────────────────────────────────────────────────────────────────
    // THE SIX RULES THIS CLASS ENFORCES, and where each one is:
    //
    //   1. Authorization BEFORE fetch          — BuildViewerAsync calls DescribeAsync first; a null answer ends
    //                                            the method before any parameter is bound or any row is read.
    //   2. Hidden fields never reach the UI    — Visible(columns) / Answerable(parameters), applied ONCE, here.
    //   3. Internal fields cannot be filtered  — the same projection: a column the UI never learns about cannot
    //                                            be offered as a filter, so this is structural, not a check.
    //   4. Template visibility ≠ data access   — templates are listed only AFTER the report gate passed.
    //   5. Truncation is always visible        — ReportPreviewModel.Truncated is required and non-nullable.
    //   6. Export == preview, shaped           — both go through BuildRequest; there is one request builder.
    //
    // WHAT IT DELIBERATELY DOES NOT DO: it does not authorize. It calls services that authorize, and it
    // propagates their refusals. A presenter that made its own access decision would be a second permission
    // engine, and the ordering rule in ReportAuthorizationService would then exist in two places.
    // =============================================================================================

    // ---- panel state ----------------------------------------------------------------------------
    //
    // FIVE states, not "list or nothing". Each is a materially different thing to tell a person, and collapsing
    // any pair turns a broken deployment into a quiet week — the same reasoning the Workspace records for its
    // own panels, arrived at independently here because the failure mode is identical.
    //
    // (These are Reporting's OWN types. They deliberately do not reference CrossBuy.BL.Workspace: coupling this
    //  product's view models to another tab's evolving contracts is precisely how a signature change in one
    //  product darkens another. The single place the two meet is the Phase 5 adapter, and that is one file.)
    public enum ReportPanelState
    {
        Ready = 0,

        // The source answered "nothing". Ordinary.
        Empty = 1,

        // Nothing is registered / not deployed. Permanent until a deployment changes it.
        Unavailable = 2,

        // Authenticated but not permitted. Nothing is broken and no deployment will change it.
        AccessDenied = 3,

        // The source threw. May succeed next time, so the UI invites a retry.
        Failed = 4,
    }

    public sealed class ReportPanel<T>
    {
        public ReportPanelState State { get; init; } = ReportPanelState.Ready;
        public IReadOnlyList<T> Items { get; init; } = Array.Empty<T>();

        // A STABLE MACHINE CODE, never a sentence. ReportContracts records the convention: a BL service does not
        // localise for a view it cannot see. The view maps this to SharedResources.
        public string? ReasonCode { get; init; }

        public bool HasItems => Items.Count > 0;

        // PAGING, set only by the panels that page. Null everywhere else, which is how the view tells
        // "one page of many" from "this is all of it" without inferring anything from Items.Count.
        public int Page { get; init; } = 1;
        public int PageSize { get; init; }
        public int Total { get; init; }

        // Whether this panel pages at all. Set by Paged() and by nothing else, so the view asks one question
        // instead of testing three nullables.
        public bool IsPaged { get; init; }

        public int Pages => PageSize <= 0 ? 1 : Math.Max(1, (int)Math.Ceiling(Total / (double)PageSize));

        public static ReportPanel<T> From(IReadOnlyList<T> items) => new()
        {
            State = items.Count == 0 ? ReportPanelState.Empty : ReportPanelState.Ready,
            Items = items,
        };

        // The paged twin of From(). Same states, plus the three numbers an ordinary pager needs.
        public static ReportPanel<T> Paged(IReadOnlyList<T> items, int page, int pageSize, int total) => new()
        {
            State = items.Count == 0 ? ReportPanelState.Empty : ReportPanelState.Ready,
            Items = items,
            Page = page,
            PageSize = pageSize,
            Total = total,
            IsPaged = true,
        };

        public static ReportPanel<T> Unavailable(string reasonCode) =>
            new() { State = ReportPanelState.Unavailable, ReasonCode = reasonCode };

        public static ReportPanel<T> AccessDenied(string reasonCode) =>
            new() { State = ReportPanelState.AccessDenied, ReasonCode = reasonCode };

        public static ReportPanel<T> Failed(string reasonCode) =>
            new() { State = ReportPanelState.Failed, ReasonCode = reasonCode };

        public static ReportPanel<T> Empty() => new() { State = ReportPanelState.Empty };
    }

    // Reason codes. Frozen strings so the view's resource lookup and the tests agree without either guessing.
    public static class ReportPanelReasons
    {
        public const string NoCompany = "reporting.panel.no_company";
        public const string NotPermitted = "reporting.panel.not_permitted";
        public const string NothingRegistered = "reporting.panel.nothing_registered";
        public const string SourceFailed = "reporting.panel.source_failed";
        public const string SchemaMissing = "reporting.panel.schema_missing";
        public const string WritesUnavailable = "reporting.panel.writes_unavailable";
    }

    // =============================================================================================
    // WRITE AFFORDANCES
    //
    // The Viewer shows Save / Fork / Set default / Favourite. Right now none of them can be honoured, because
    // the seven write endpoints are held back by CBA001 (see ReportsCenterApiController's held-back block).
    //
    // THE CHOICE MADE HERE, and why: the buttons are RENDERED AND DISABLED, carrying a reason — not hidden.
    // Hiding them would make the product look finished and quietly lose a feature; a disabled control with an
    // explanation is the honest state of a system waiting on an approval. It also means activation is a flag
    // flip rather than a UI rewrite.
    //
    // TO ACTIVATE: TAB 1 adds IReportAuthorizationService to AuthorizationSurface.AuthorityTypes, the seven
    // endpoints are restored from ReportsCenterWriteEndpoints.cs.pending, and IsActivated becomes true. One
    // line here, one line there.
    // =============================================================================================
    public static class ReportingWriteSurface
    {
        // ACTIVATED. TAB 1 declared IReportAuthorizationService an authority in
        // CrossBuy.Analyzers/AuthorizationSurface.cs, the seven endpoints were restored from their parked
        // copy, and the Viewer's Save / Fork / Favourite controls are live rather than disabled.
        public const bool IsActivated = true;

        public const string BlockedReasonCode = ReportPanelReasons.WritesUnavailable;
    }

    // ---- catalog view models ----------------------------------------------------------------------

    public sealed class ReportCardModel
    {
        public required string Code { get; init; }
        public required string Module { get; init; }
        public required string Title { get; init; }
        public string? Description { get; init; }
        public string? CategoryKey { get; init; }
        public IReadOnlyList<string> Tags { get; init; } = Array.Empty<string>();
        public string Icon { get; init; } = "ki-outline ki-chart-simple";
        public string Color { get; init; } = "primary";
        public int SortOrder { get; init; }
        public bool IsFavorite { get; init; }
    }

    public sealed class ReportCategoryGroup
    {
        public required string Key { get; init; }
        public required string Title { get; init; }
        public string? Icon { get; init; }
        public IReadOnlyList<ReportCardModel> Reports { get; init; } = Array.Empty<ReportCardModel>();
    }

    public sealed class ReportTagChip
    {
        public required int Id { get; init; }
        public required string Name { get; init; }
        public string ColorToken { get; init; } = "primary";
        public int UsageCount { get; init; }
    }

    public sealed class SavedReportModel
    {
        public required int Id { get; init; }
        public required string ReportCode { get; init; }
        public required string Name { get; init; }
        public required string ReportTitle { get; init; }
        public ReportTemplateScope Scope { get; init; }
        public bool IsDefault { get; init; }
        public int CurrentVersionNo { get; init; }
        public DateTime? UpdatedAt { get; init; }

        // What the CALLER may do with it. Drives whether Edit/Delete render at all — and it comes from the
        // authorization service's own answer, not from a guess about ownership in the view.
        public ReportAccessLevel AccessLevel { get; init; }
        public bool CanEdit => AccessLevel >= ReportAccessLevel.Edit;
    }

    public sealed class ReportRunModel
    {
        public required long Id { get; init; }
        public required string ReportCode { get; init; }
        public required string ReportTitle { get; init; }
        public required string Format { get; init; }
        public ReportRunKind Kind { get; init; }
        public ReportRunStatus Status { get; init; }
        public int RowCount { get; init; }
        public int DurationMs { get; init; }
        public string? EmployeeName { get; init; }

        // KEPT so the screen can show the person's picture beside the name. ReportRunHistoryRow has
        // always carried it; this model dropped it, so a view had a name and no way to resolve a face.
        public int? EmployeeId { get; init; }

        public DateTime StartedAt { get; init; }
        public long? ArchiveEntryId { get; init; }
        public string? ErrorCode { get; init; }

        // Same reasoning as ReportArchiveModel.Retrievable: the row stays (it is the caller's own history) but
        // the re-run affordance is withdrawn when the report behind it is no longer theirs to open.
        public bool ReportAvailable { get; init; } = true;

        public bool Failed => Status != ReportRunStatus.Succeeded;
        public bool CanRerun => ReportAvailable;
    }

    public sealed class ReportArchiveModel
    {
        public required long Id { get; init; }
        public required string ReportCode { get; init; }
        public required string ReportTitle { get; init; }
        public required string FileName { get; init; }
        public long Length { get; init; }
        public DateTime? CreatedAt { get; init; }
        public DateTime? RetainUntil { get; init; }

        // An archive row whose bytes are gone is SHOWN and flagged, never hidden. Hiding it makes a retention
        // sweep look like a deletion of the record, and the user cannot tell why their link stopped working.
        public bool BytesPresent { get; init; } = true;

        // Whether the caller may still RETRIEVE it — i.e. whether they still hold the report's permission.
        //
        // TWO REASONS THIS EXISTS AS A FLAG RATHER THAN A FILTER:
        //
        //   1. The listing is already self-scoped: IReportArchiveService.ListAsync returns the caller's OWN
        //      artifacts (or everything, for a reporting administrator). So a row here is one this person
        //      produced. Dropping it when their permission changes would make their own history rewrite
        //      itself, which is the one thing an audit surface must never do.
        //   2. The BYTES are separately protected — RetrieveAsync re-checks the report permission and answers
        //      null. That is the actual control, and it holds whatever the UI does.
        //
        // What the flag buys is honesty: without it the screen renders a Download button that 404s, and the
        // user cannot tell a revoked permission from a broken product.
        public bool Retrievable { get; init; } = true;

        public bool CanDownload => BytesPresent && Retrievable;
    }

    // "Is the data behind this actually there?" — the dataset availability strip.
    public sealed class ReportDatasetStatusModel
    {
        public required string DatasetCode { get; init; }
        public required string Module { get; init; }
        public required string Title { get; init; }
        public string? Description { get; init; }
        public required string Version { get; init; }
        public int FieldCount { get; init; }
        public int MaxRows { get; init; }

        // True when a definition provider publishes a report over this dataset, i.e. it is reachable from the
        // catalog rather than merely registered. A dataset with no report is a real state and worth showing.
        public bool HasReport { get; init; }
    }

    public sealed class ReportsCenterQuery
    {
        public string? Search { get; init; }
        public string? Module { get; init; }
        public string? CategoryKey { get; init; }
        public string? Tag { get; init; }
        public bool FavoritesOnly { get; init; }
        public bool Arabic { get; init; }

        // 1-based, one per paged table. Two numbers rather than one because the two tables are on the SAME
        // screen: a single `page` would move both at once and there would be no way to be on page 3 of the
        // saved reports while still looking at the first page of the run history.
        public int SavedPage { get; init; } = 1;
        public int RecentPage { get; init; } = 1;
    }

    public sealed class ReportsCenterModel
    {
        // False when no company resolved. The view renders a sign-in notice rather than nine empty panels.
        public bool IsResolved { get; init; }

        public ReportsCenterQuery Query { get; init; } = new();

        public ReportPanel<ReportCategoryGroup> Catalog { get; init; } = ReportPanel<ReportCategoryGroup>.Empty();
        public ReportPanel<ReportCategoryGroup> Categories { get; init; } = ReportPanel<ReportCategoryGroup>.Empty();
        public ReportPanel<ReportTagChip> Tags { get; init; } = ReportPanel<ReportTagChip>.Empty();
        public ReportPanel<ReportCardModel> Favorites { get; init; } = ReportPanel<ReportCardModel>.Empty();
        public ReportPanel<SavedReportModel> Saved { get; init; } = ReportPanel<SavedReportModel>.Empty();
        public ReportPanel<ReportRunModel> Recent { get; init; } = ReportPanel<ReportRunModel>.Empty();
        public ReportPanel<ReportArchiveModel> Archive { get; init; } = ReportPanel<ReportArchiveModel>.Empty();
        public ReportPanel<ReportDatasetStatusModel> Datasets { get; init; } = ReportPanel<ReportDatasetStatusModel>.Empty();

        public int TotalReports { get; init; }
        public bool WritesEnabled => ReportingWriteSurface.IsActivated;
    }

    // ---- viewer view models -------------------------------------------------------------------------

    public sealed class ReportParameterModel
    {
        public required string Key { get; init; }
        public required string Title { get; init; }
        public required string Type { get; init; }
        public bool Required { get; init; }
        public bool AllowMultiple { get; init; }
        public string? Value { get; init; }
        public string? HelpText { get; init; }
        public string? LookupEntityCode { get; init; }

        // STRINGS, matching ReportParameterDescriptor. The descriptor keeps bounds as text because a bound can
        // be an expression ("month-start"), not only a number — coercing to decimal here would silently drop
        // the expression form and hand the input a null bound.
        public string? MinValue { get; init; }
        public string? MaxValue { get; init; }
        public IReadOnlyList<(string Value, string Label)> Options { get; init; } =
            Array.Empty<(string, string)>();

        // The engine's own complaint about THIS field, shown against the input rather than in a banner.
        public string? ValidationMessage { get; init; }
        public bool HasError => ValidationMessage != null;
    }

    public sealed class ReportColumnModel
    {
        public required string Key { get; init; }
        public required string Title { get; init; }
        public required string Type { get; init; }
        public required string Align { get; init; }
        public bool VisibleByDefault { get; init; }
        public bool Filterable { get; init; }
        public bool Sortable { get; init; }
    }

    public sealed class ReportPreviewModel
    {
        public required bool Succeeded { get; init; }
        public string? Html { get; init; }
        public long? RunId { get; init; }
        public int RowCount { get; init; }

        // NON-NULLABLE AND REQUIRED. A preview that lost this flag is a preview that lies, and an optional bool
        // is one `?? false` away from lying quietly.
        public required bool Truncated { get; init; }

        public int DurationMs { get; init; }
        public long? ArchiveEntryId { get; init; }

        // WHICH TEMPLATE THE RUN ACTUALLY USED, which is not the same as the one the URL asked for.
        //
        // Template resolution walks Personal → Team → Company → Platform, so a report opened with no
        // templateId can still be rendered through a saved layout. Without this the screen could not tell
        // the difference between "the definition's defaults" and "your own saved design", and the Design
        // button therefore started a NEW template every time — quietly accumulating duplicates of the same
        // report instead of editing the one in use.
        public int? TemplateId { get; init; }
        public int? TemplateVersionNo { get; init; }

        public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
        public IReadOnlyList<string> Errors { get; init; } = Array.Empty<string>();

        // True when the run was refused. Distinct from Errors so the view can say "you may not run this" rather
        // than showing an engine diagnostic to somebody who cannot act on it.
        public bool Denied { get; init; }
    }

    public sealed class ReportViewerQuery
    {
        public required string Code { get; init; }
        public int? TemplateId { get; init; }
        public IReadOnlyDictionary<string, string?> Parameters { get; init; } =
            new Dictionary<string, string?>(StringComparer.Ordinal);

        // False on first open: the screen shows the parameter form and no result. Running is an act.
        public bool Run { get; init; }
        public bool Arabic { get; init; }
    }

    public sealed class ReportViewerModel
    {
        public required string Code { get; init; }
        public required string Module { get; init; }
        public required string Title { get; init; }
        public string? Description { get; init; }
        public string Icon { get; init; } = "ki-outline ki-chart-simple";
        public string Color { get; init; } = "primary";
        public int DefinitionVersion { get; init; }
        public int? TemplateId { get; init; }

        public IReadOnlyList<ReportParameterModel> Parameters { get; init; } = Array.Empty<ReportParameterModel>();
        public IReadOnlyList<ReportColumnModel> Columns { get; init; } = Array.Empty<ReportColumnModel>();

        // Formats that can ACTUALLY be produced in this deployment, so an unbound PDF engine means no PDF
        // button rather than a button that errors.
        public IReadOnlyList<ReportOutputFormat> Formats { get; init; } = Array.Empty<ReportOutputFormat>();

        public int MaxRows { get; init; }
        public int PreviewRows { get; init; }
        public bool AllowArchive { get; init; }
        public bool AllowShare { get; init; }

        public ReportPreviewModel? Preview { get; init; }
        public ReportPanel<SavedReportModel> Saved { get; init; } = ReportPanel<SavedReportModel>.Empty();
        public ReportPanel<ReportRunModel> History { get; init; } = ReportPanel<ReportRunModel>.Empty();
        public ReportPanel<ReportArchiveModel> Archive { get; init; } = ReportPanel<ReportArchiveModel>.Empty();

        public bool IsFavorite { get; init; }

        // Set when the reporting SCHEMA is not deployed in this database. Distinct from "not permitted"
        // (null model → 404) and from "no rows": nothing the user does will fix it, and the fix is a
        // deployment. Without this the Viewer returns a 500 stack trace, which tells the person looking
        // at it nothing they can act on.
        public string? UnavailableReasonCode { get; init; }
        public bool IsUnavailable => UnavailableReasonCode != null;

        public bool WritesEnabled => ReportingWriteSurface.IsActivated;
        public string WritesBlockedReasonCode => ReportingWriteSurface.BlockedReasonCode;

        public bool CanExport => Formats.Count > 0;
    }

    // =============================================================================================
    public interface IReportsCenterPresenter
    {
        Task<ReportsCenterModel> BuildCenterAsync(ReportsCenterQuery query, CancellationToken cancellationToken = default);

        // null = no such report, AS FAR AS THIS CALLER IS CONCERNED. Absent and unpermitted answer the same way,
        // and the controller turns both into 404 — an unauthorized probe must not be able to enumerate the
        // catalog by watching status codes.
        Task<ReportViewerModel?> BuildViewerAsync(ReportViewerQuery query, CancellationToken cancellationToken = default);
    }

    public sealed class ReportsCenterPresenter : IReportsCenterPresenter
    {
        // RecentRuns is now a PAGE SIZE, not a ceiling. It used to be the only thing standing between the
        // screen and 273 run rows: the table showed the newest 12 and said nothing about the rest, so the
        // other 261 were simply unreachable from this page.
        private const int RecentRuns = 12;

        // The saved-reports table had NO limit at all - the loop below appends every template of every
        // visible report, which was 61 rows in one card on a screen that already carries eight others.
        private const int SavedPageSize = 10;
        private const int ArchiveRows = 12;
        private const int ViewerHistoryRows = 8;

        private readonly IReportService _reports;
        private readonly IReportLibraryService _library;
        private readonly IReportHistoryService _history;
        private readonly IReportArchiveService _archive;
        private readonly IReportTemplateService _templates;
        private readonly IReportDatasetRegistry _datasets;
        private readonly IReportCatalog _catalog;
        private readonly IBusinessContextAccessor _contexts;

        public ReportsCenterPresenter(
            IReportService reports,
            IReportLibraryService library,
            IReportHistoryService history,
            IReportArchiveService archive,
            IReportTemplateService templates,
            IReportDatasetRegistry datasets,
            IReportCatalog catalog,
            IBusinessContextAccessor contexts)
        {
            _reports = reports;
            _library = library;
            _history = history;
            _archive = archive;
            _templates = templates;
            _datasets = datasets;
            _catalog = catalog;
            _contexts = contexts;
        }

        // =========================================================================================
        // THE REPORTS CENTER
        // =========================================================================================
        public async Task<ReportsCenterModel> BuildCenterAsync(
            ReportsCenterQuery query, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(query);

            var context = await _contexts.TryGetCurrentAsync(cancellationToken);
            if (context is not { CompanyId: > 0 })
                return new ReportsCenterModel { IsResolved = false, Query = query };

            // BrowseAsync is ALREADY filtered by FilterVisibleAsync — the caller never sees a report they cannot
            // run. That is why this method does no permission work of its own: doing it twice would create a
            // second place the rule could drift.
            var definitions = await _reports.BrowseAsync(
                query.Module, query.CategoryKey, query.Tag, query.Search, cancellationToken);

            // TWO SETS, and conflating them is a bug I wrote first.
            //
            //   `definitions` — visible AND matching the current search/category/tag. Drives the CATALOG.
            //   `visible`     — everything this caller may run, unfiltered. Drives every OTHER panel, and it is
            //                   the set that answers "may they still open this?" for a history or archive row.
            //
            // Using the filtered set for that question makes typing a search term mark every other report's
            // history as unavailable — a UI that revokes affordances because you searched for something.
            var visible = string.IsNullOrEmpty(query.Module) && string.IsNullOrEmpty(query.CategoryKey)
                          && string.IsNullOrEmpty(query.Tag) && string.IsNullOrEmpty(query.Search)
                ? definitions
                : await _reports.BrowseAsync(cancellationToken: cancellationToken);

            // Set by ANY panel read that fails because its table does not exist. One flag rather than one
            // per panel: if the slice is missing every panel is missing, and six identical banners would
            // be noise.
            bool schemaMissing = false;
            void SchemaMissing() => schemaMissing = true;

            var favorites = await Safe(() => _library.GetFavoritesAsync(context, cancellationToken),
                Array.Empty<ReportFavoriteInfo>(), SchemaMissing);

            var favoriteCodes = new HashSet<string>(
                favorites.Where(f => f.TemplateId == null).Select(f => f.ReportCode), StringComparer.Ordinal);

            var cards = definitions
                .Select(d => Card(d, query.Arabic, favoriteCodes.Contains(d.Code)))
                .ToList();

            if (query.FavoritesOnly) cards = cards.Where(c => c.IsFavorite).ToList();

            // ---- catalog, grouped ---------------------------------------------------------------------
            var categoryNodes = await Safe(() => _library.GetCategoryTreeAsync(context, cancellationToken),
                Array.Empty<ReportCategoryNode>(), SchemaMissing);

            var categoryTitles = Flatten(categoryNodes)
                .ToDictionary(n => n.Key, n => (Title: query.Arabic ? n.Name : (n.NameEn ?? n.Name), n.Icon),
                    StringComparer.Ordinal);

            var groups = cards
                .GroupBy(c => c.CategoryKey ?? "", StringComparer.Ordinal)
                .Select(g => new ReportCategoryGroup
                {
                    Key = g.Key,
                    // An unknown key falls back to the key itself rather than to "Uncategorised": a report whose
                    // category row was never synced is a deployment fact, and showing the key makes it findable.
                    Title = categoryTitles.TryGetValue(g.Key, out var meta) ? meta.Title
                          : g.Key.Length == 0 ? "" : g.Key,
                    Icon = categoryTitles.TryGetValue(g.Key, out var m2) ? m2.Icon : null,
                    Reports = g.OrderBy(r => r.SortOrder).ThenBy(r => r.Title, StringComparer.CurrentCulture).ToList(),
                })
                .OrderBy(g => g.Key.Length == 0 ? 1 : 0)       // uncategorised last
                .ThenBy(g => g.Title, StringComparer.CurrentCulture)
                .ToList();

            // ---- the side panels ----------------------------------------------------------------------
            var tags = (await Safe(() => _library.GetTagsAsync(context, cancellationToken),
                    Array.Empty<ReportTagInfo>(), SchemaMissing))
                .Select(t => new ReportTagChip
                {
                    Id = t.Id,
                    Name = query.Arabic ? t.Name : (t.NameEn ?? t.Name),
                    ColorToken = t.ColorToken,
                    UsageCount = t.UsageCount,
                })
                .ToList();

            var titles = visible.ToDictionary(d => d.Code, d => d.Title(query.Arabic), StringComparer.Ordinal);

            // RUN HISTORY, one page of it. It used to be Take = 12 with no way to ask for row 13, so 261 of
            // this company's 273 run rows were unreachable from this screen.
            int recentTotal = await Safe(() => _history.CountAsync(new ReportHistoryQuery(), context,
                cancellationToken), 0, SchemaMissing);
            int recentPages = Math.Max(1, (int)Math.Ceiling(recentTotal / (double)RecentRuns));
            int recentPage = Math.Clamp(query.RecentPage, 1, recentPages);

            var recent = (await Safe(
                    () => _history.QueryAsync(
                        new ReportHistoryQuery { Skip = (recentPage - 1) * RecentRuns, Take = RecentRuns },
                        context, cancellationToken),
                    Array.Empty<ReportHistoryRow>(), SchemaMissing))
                .Select(r => Run(r, query.Arabic, titles.ContainsKey(r.ReportCode)))
                .ToList();

            var archive = (await Safe(() => _archive.ListAsync(null, context, ArchiveRows, cancellationToken),
                    Array.Empty<ReportArchiveInfo>(), SchemaMissing))
                .Select(a => Archived(a, titles))
                .ToList();

            // SAVED REPORTS across the catalog. Listed per report the caller can already see, which is what
            // makes "template visibility never grants data permission" structural: a template of a report that
            // was filtered out of `definitions` is never asked for.
            var savedAll = new List<SavedReportModel>();
            foreach (var definition in visible)
            {
                var list = await Safe(() => _templates.ListAsync(definition.Code, context, cancellationToken),
                    Array.Empty<ReportTemplateSummary>(), SchemaMissing);
                savedAll.AddRange(list.Select(t => Saved(t, definition.Title(query.Arabic), query.Arabic)));
            }

            // ORDERED BEFORE IT IS PAGED, and every tie broken. The list is assembled by looping over
            // definitions, so its natural order is "grouped by report, in whatever order each report's
            // templates came back" - stable enough to look fine on one page and not stable enough to page:
            // two rows sharing an Updated instant could swap and appear on both page 1 and page 2.
            // Newest-updated first, which is the column the table already shows.
            savedAll = savedAll
                .OrderByDescending(x => x.UpdatedAt)
                .ThenBy(x => x.Name, StringComparer.CurrentCulture)
                .ThenBy(x => x.Id)
                .ToList();

            // CLAMPED, so ?savedPage=999 shows the last real page instead of an empty table.
            int savedPages = Math.Max(1, (int)Math.Ceiling(savedAll.Count / (double)SavedPageSize));
            int savedPage = Math.Clamp(query.SavedPage, 1, savedPages);

            var saved = savedAll.Skip((savedPage - 1) * SavedPageSize).Take(SavedPageSize).ToList();

            var datasets = (await Safe(() => _datasets.ListForStudioAsync(context, cancellationToken),
                    Array.Empty<ReportDatasetDefinition>(), SchemaMissing))
                .Select(d => new ReportDatasetStatusModel
                {
                    DatasetCode = d.DatasetCode,
                    Module = d.Module,
                    Title = query.Arabic ? d.TitleAr : DisplayName.Or(d.TitleEn, d.TitleAr),
                    Description = query.Arabic ? d.DescriptionAr : DisplayName.Or(d.DescriptionEn, d.DescriptionAr),
                    Version = d.Version.ToString(),

                    // Never-sensitivity fields are not counted. The strip reports what a person could build
                    // with, and a field nobody may ever see is not one of them.
                    FieldCount = d.Fields.Count(f => f.Sensitivity != ReportFieldSensitivity.Never),
                    MaxRows = d.MaxRows,

                    // The unfiltered catalog is safe HERE and only here: ListForStudioAsync has already dropped
                    // every dataset this caller may not see, so the question being answered is "does a report
                    // exist over a dataset you are already permitted to know about". It is a boolean about
                    // wiring, not a row of data.
                    HasReport = _catalog.GetDefinitions().Any(r =>
                        string.Equals(r.DataSourceKey, d.DataSourceKey, StringComparison.Ordinal)),
                })
                .ToList();

            return new ReportsCenterModel
            {
                IsResolved = true,
                Query = query,
                TotalReports = cards.Count,

                // SCHEMA FIRST. "Not installed" outranks "no rows": an empty catalog on an undeployed
                // database is not an empty catalog, it is an absent product, and saying "no reports match"
                // would send somebody hunting for a filter to clear.
                Catalog = schemaMissing
                    ? ReportPanel<ReportCategoryGroup>.Unavailable(ReportPanelReasons.SchemaMissing)
                    : groups.Count == 0
                        ? ReportPanel<ReportCategoryGroup>.Empty()
                        : ReportPanel<ReportCategoryGroup>.From(groups),
                Categories = ReportPanel<ReportCategoryGroup>.From(
                    Flatten(categoryNodes).Select(n => new ReportCategoryGroup
                    {
                        Key = n.Key,
                        Title = query.Arabic ? n.Name : (n.NameEn ?? n.Name),
                        Icon = n.Icon,
                    }).ToList()),
                Tags = ReportPanel<ReportTagChip>.From(tags),
                Favorites = ReportPanel<ReportCardModel>.From(cards.Where(c => c.IsFavorite).ToList()),
                Saved = ReportPanel<SavedReportModel>.Paged(saved, savedPage, SavedPageSize, savedAll.Count),
                Recent = ReportPanel<ReportRunModel>.Paged(recent, recentPage, RecentRuns, recentTotal),
                Archive = ReportPanel<ReportArchiveModel>.From(archive),
                Datasets = schemaMissing
                    ? ReportPanel<ReportDatasetStatusModel>.Unavailable(ReportPanelReasons.SchemaMissing)
                    : datasets.Count == 0
                        ? ReportPanel<ReportDatasetStatusModel>.Unavailable(ReportPanelReasons.NothingRegistered)
                        : ReportPanel<ReportDatasetStatusModel>.From(datasets),
            };
        }

        // =========================================================================================
        // THE REPORT VIEWER
        // =========================================================================================
        public async Task<ReportViewerModel?> BuildViewerAsync(
            ReportViewerQuery query, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(query);

            var context = await _contexts.TryGetCurrentAsync(cancellationToken);
            if (context is not { CompanyId: > 0 }) return null;

            // ---- AUTHORIZATION BEFORE FETCH -------------------------------------------------------------
            //
            // FIRST statement that touches the report, and it is a gate. DescribeAsync returns null when the
            // caller may not see the report, so an unpermitted code leaves this method before a parameter is
            // bound, before a template is resolved, and — the property Phase 6 asserts — before the data source
            // is constructed, let alone queried.
            //
            // The schema guard sits AROUND the gate, not inside it. DescribeAsync authorizes, and
            // authorizing reads ReportShares — so on a database where the reporting slice is not deployed
            // the gate itself throws before it can answer. That surfaced as a 500 on a real database while
            // every test passed, because ReportingTestHost builds its tables from the EF model with
            // EnsureCreated(). A missing deployment is an operator fact, not an exception for a user.
            ReportDefinition? definition;
            try
            {
                definition = await _reports.DescribeAsync(query.Code, cancellationToken);
            }
            catch (Exception ex) when (IsSchemaMissing(ex))
            {
                return Unavailable(query, ReportPanelReasons.SchemaMissing);
            }

            if (definition is null) return null;

            var formats = await _reports.AvailableFormatsAsync(query.Code, cancellationToken);

            var favorites = await Safe(() => _library.GetFavoritesAsync(context, cancellationToken),
                Array.Empty<ReportFavoriteInfo>());

            var saved = await Safe(() => _templates.ListAsync(query.Code, context, cancellationToken),
                Array.Empty<ReportTemplateSummary>());

            var history = (await Safe(
                    () => _history.QueryAsync(
                        new ReportHistoryQuery { ReportCode = query.Code, Take = ViewerHistoryRows },
                        context, cancellationToken),
                    Array.Empty<ReportHistoryRow>()))
                .Select(r => Run(r, query.Arabic))
                .ToList();

            var archive = (await Safe(
                    () => _archive.ListAsync(query.Code, context, ArchiveRows, cancellationToken),
                    Array.Empty<ReportArchiveInfo>()))
                .Select(a => Archived(a, new Dictionary<string, string>(StringComparer.Ordinal)
                    { [definition.Code] = definition.Title(query.Arabic) }))
                .ToList();

            // ---- run, only if asked ----------------------------------------------------------------------
            ReportPreviewModel? preview = null;
            var fieldErrors = new Dictionary<string, string>(StringComparer.Ordinal);

            if (query.Run)
            {
                var result = await _reports.PreviewAsync(
                    BuildRequest(query.Code, query.Parameters, query.TemplateId,
                                 ReportOutputFormat.Html, preview: true, archive: false),
                    cancellationToken);

                preview = Preview(result);

                // Field-level diagnostics are hoisted onto their parameter so the message lands next to the
                // input that caused it. A validation banner listing "From is required" above a form with eight
                // inputs makes the user hunt for which one.
                foreach (var diagnostic in result.Diagnostics)
                    if (diagnostic.Field is { Length: > 0 } field && !fieldErrors.ContainsKey(field))
                        fieldErrors[field] = diagnostic.Message;
            }

            return new ReportViewerModel
            {
                Code = definition.Code,
                Module = definition.Module,
                Title = definition.Title(query.Arabic),
                Description = query.Arabic ? definition.DescriptionAr : DisplayName.Or(definition.DescriptionEn, definition.DescriptionAr),
                Icon = definition.Icon,
                Color = definition.Color,
                DefinitionVersion = definition.DefinitionVersion,
                TemplateId = query.TemplateId,

                Parameters = Answerable(definition, query, fieldErrors),
                Columns = Visible(definition, query.Arabic),
                Formats = formats,

                MaxRows = definition.Capabilities.MaxRows,
                PreviewRows = definition.Capabilities.PreviewRows,
                AllowArchive = definition.Capabilities.AllowArchive,
                AllowShare = definition.Capabilities.AllowShare,

                Preview = preview,
                Saved = ReportPanel<SavedReportModel>.From(
                    saved.Select(t => Saved(t, definition.Title(query.Arabic), query.Arabic)).ToList()),
                History = ReportPanel<ReportRunModel>.From(history),
                Archive = ReportPanel<ReportArchiveModel>.From(archive),

                IsFavorite = favorites.Any(f =>
                    string.Equals(f.ReportCode, definition.Code, StringComparison.Ordinal) && f.TemplateId == null),
            };
        }

        // =========================================================================================
        // THE TWO PROJECTIONS THAT ARE THE SECURITY BOUNDARY
        // =========================================================================================

        // COLUMNS. `Internal` columns are dropped HERE, once. Everything downstream — the column chooser, the
        // filter list, the export dialog — reads this list, so there is no path by which an internal column can
        // reach a screen. Dropping it in the view instead would leave the model carrying it, one `@foreach`
        // away from being rendered by the next person who adds a panel.
        private static IReadOnlyList<ReportColumnModel> Visible(ReportDefinition definition, bool arabic) =>
            definition.Columns
                .Where(c => !c.Internal)
                .Select(c => new ReportColumnModel
                {
                    Key = c.Key,
                    Title = arabic ? c.TitleAr : DisplayName.Or(c.TitleEn, c.TitleAr),
                    Type = c.Type.ToString(),
                    Align = c.EffectiveAlign.ToString(),
                    VisibleByDefault = c.VisibleByDefault,

                    // A non-internal column that the definition marks unfilterable stays unfilterable. The UI
                    // never widens what the engine will accept — an input the engine would refuse is an
                    // invitation to a validation error the user cannot fix.
                    Filterable = c.Filterable,
                    Sortable = c.Sortable,
                })
                .ToList();

        // PARAMETERS. `SystemSupplied` parameters are dropped: the engine supplies them and IGNORES a
        // request-supplied value, so rendering an input for one invites the user to set something that cannot
        // take effect. CompanyId is the one that matters — offering it would look exactly like a tenant
        // selector, which is the hole this whole product avoids by construction.
        private static IReadOnlyList<ReportParameterModel> Answerable(
            ReportDefinition definition, ReportViewerQuery query, IReadOnlyDictionary<string, string> fieldErrors) =>
            definition.Parameters
                .Where(p => !p.SystemSupplied)
                .Select(p => new ReportParameterModel
                {
                    Key = p.Key,
                    Title = query.Arabic ? p.TitleAr : DisplayName.Or(p.TitleEn, p.TitleAr),
                    Type = p.Type.ToString(),
                    Required = p.Required,
                    AllowMultiple = p.AllowMultiple,

                    // The value the user actually submitted, echoed back so a failed run does not clear the form.
                    // Falls back to the declared default on first open.
                    Value = query.Parameters.TryGetValue(p.Key, out var supplied) ? supplied : p.DefaultValue,

                    HelpText = query.Arabic ? p.HelpTextAr : p.HelpTextEn,
                    LookupEntityCode = p.LookupEntityCode,
                    MinValue = p.MinValue,
                    MaxValue = p.MaxValue,
                    Options = p.Options
                        .Select(o => (o.Value, query.Arabic ? o.LabelAr : DisplayName.Or(o.LabelEn, o.LabelAr)))
                        .ToList(),
                    ValidationMessage = fieldErrors.GetValueOrDefault(p.Key),
                })
                .ToList();

        // =========================================================================================
        // Shaping
        // =========================================================================================

        // ONE request builder for preview and export, so "export respects the same shaped result as preview" is
        // true because there is nothing else it could be. Note again: no company, no renderer.
        public static ReportRequest BuildRequest(
            string code, IReadOnlyDictionary<string, string?> parameters, int? templateId,
            ReportOutputFormat format, bool preview, bool archive) => new()
            {
                ReportCode = code,
                Format = format,
                Kind = preview ? ReportRunKind.Preview : ReportRunKind.Full,
                TemplateId = templateId,
                Parameters = new Dictionary<string, string?>(parameters, StringComparer.Ordinal),
                Archive = !preview && archive,
            };

        private static ReportPreviewModel Preview(ReportResult result) => new()
        {
            Succeeded = result.IsSuccess,
            Denied = result.IsDenied,
            Html = result.Artifact is null ? null : result.Artifact.AsText(),
            RunId = result.Run?.RunId,
            RowCount = result.Run?.RowCount ?? 0,

            // `== true` rather than `?? false`: identical here, but it reads as a deliberate three-state
            // collapse rather than a defaulted null, which is what this flag must never be.
            Truncated = result.Run?.Truncated == true,

            DurationMs = result.Run?.DurationMs ?? 0,
            ArchiveEntryId = result.Run?.ArchiveEntryId,

            // The template the run RESOLVED, which the request need not have named — see the property's
            // own comment. This is what lets the screen offer to edit the layout in use.
            TemplateId = result.Run?.TemplateId,
            TemplateVersionNo = result.Run?.TemplateVersionNo,

            Warnings = result.Diagnostics
                .Where(d => d.Severity == ReportDiagnosticSeverity.Warning)
                .Select(d => d.Message).ToList(),

            // A DENIAL IS NOT AN ERROR LIST. The engine encodes refusal as an Error diagnostic; surfacing it in
            // the error strip would show an authorization reason code to somebody who cannot act on it, and
            // would tell an unauthorized prober exactly which gate stopped them.
            Errors = result.IsDenied
                ? Array.Empty<string>()
                : result.Errors.Select(d => d.Message).Where(m => m.Length > 0).ToList(),
        };

        private static ReportCardModel Card(ReportDefinition d, bool arabic, bool favorite) => new()
        {
            Code = d.Code,
            Module = d.Module,
            Title = d.Title(arabic),
            Description = arabic ? d.DescriptionAr : DisplayName.Or(d.DescriptionEn, d.DescriptionAr),
            CategoryKey = d.CategoryKey,
            Tags = d.Tags,
            Icon = d.Icon,
            Color = d.Color,
            SortOrder = d.SortOrder,
            IsFavorite = favorite,
        };

        private static SavedReportModel Saved(ReportTemplateSummary t, string reportTitle, bool arabic) => new()
        {
            Id = t.Id,
            ReportCode = t.ReportCode,
            Name = arabic ? t.Name : (t.NameEn ?? t.Name),
            ReportTitle = reportTitle,
            Scope = t.Scope,
            IsDefault = t.IsDefault,
            CurrentVersionNo = t.CurrentVersionNo,
            UpdatedAt = t.UpdatedAt,
            AccessLevel = t.AccessLevel,
        };

        private static ReportRunModel Run(ReportHistoryRow r, bool arabic, bool reportAvailable = true) => new()
        {
            ReportAvailable = reportAvailable,
            Id = r.Id,
            ReportCode = r.ReportCode,
            ReportTitle = (arabic ? r.ReportTitleAr : DisplayName.Or(r.ReportTitleEn, r.ReportTitleAr)) ?? r.ReportCode,
            Format = r.Format,
            Kind = r.Kind,
            Status = r.Status,
            RowCount = r.RowCount,
            DurationMs = r.DurationMs,
            EmployeeName = r.EmployeeName,
            EmployeeId = r.EmployeeId,
            StartedAt = r.StartedAt,
            ArchiveEntryId = r.ArchiveEntryId,
            ErrorCode = r.ErrorCode,
        };

        // `titles` doubles as the RETRIEVABILITY SET: it is built from the definitions the caller may see, so a
        // code missing from it is a code whose report they can no longer open. That makes the flag free — no
        // extra permission evaluation and no extra query — and keeps the two facts from drifting apart.
        private static ReportArchiveModel Archived(ReportArchiveInfo a, IReadOnlyDictionary<string, string> titles) => new()
        {
            Id = a.Id,
            ReportCode = a.ReportCode,
            ReportTitle = titles.GetValueOrDefault(a.ReportCode, a.ReportCode),
            FileName = a.FileName,
            Length = a.Length,
            CreatedAt = a.CreatedAt,
            RetainUntil = a.RetainUntil,
            BytesPresent = a.BytesPresent,
            Retrievable = titles.ContainsKey(a.ReportCode),
        };

        private static IEnumerable<ReportCategoryNode> Flatten(IEnumerable<ReportCategoryNode> nodes)
        {
            foreach (var node in nodes)
            {
                yield return node;
                foreach (var child in Flatten(node.Children)) yield return child;
            }
        }

        // A minimal viewer model carrying only an actionable reason. Deliberately has no columns, no
        // parameters and no preview: there is nothing to render and nothing true to say about a report
        // whose storage does not exist.
        private static ReportViewerModel Unavailable(ReportViewerQuery query, string reasonCode) => new()
        {
            Code = query.Code,
            Module = "",
            Title = query.Code,
            UnavailableReasonCode = reasonCode,
        };

        // "The reporting tables are not deployed here" — recognised by the provider's own message rather
        // than by catching a provider-specific exception type, so it holds for SQL Server ("Invalid object
        // name 'ReportShares'") and SQLite ("no such table: ReportShares") alike.
        //
        // NARROW ON PURPOSE. It matches a missing OBJECT and nothing else: a timeout, a deadlock or a
        // permission error must keep throwing, because those are transient or operational and hiding them
        // behind "not deployed" would send an operator to the wrong problem.
        public static bool IsSchemaMissing(Exception exception)
        {
            for (var current = exception; current != null; current = current.InnerException)
            {
                if (current is not System.Data.Common.DbException) continue;

                var message = current.Message;
                if (message.Contains("Invalid object name", StringComparison.OrdinalIgnoreCase)
                    || message.Contains("no such table", StringComparison.OrdinalIgnoreCase)
                    || message.Contains("doesn't exist", StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        // A PANEL THAT THROWS MUST NOT BLANK THE SCREEN. Reports Center is nine panels over six services; one
        // unavailable table (a slice not yet deployed) would otherwise take the whole product down, and the
        // person looking at it would have no way to tell which part is broken.
        //
        // It swallows to a fallback rather than rethrowing, and the caller renders the resulting panel as Empty.
        // The trade is deliberate and it is the same one IReportHistoryService.RecordAsync makes.
        //
        // It reports a MISSING SCHEMA separately rather than swallowing it with everything else. The two
        // look identical from inside the try — both end with an empty panel — but they are opposite
        // answers to the user: "there is nothing here" versus "this is not installed". Collapsing them
        // made the Reports Center render as a working-but-empty product against a database where the
        // reporting tables had never been deployed.
        private static async Task<T> Safe<T>(Func<Task<T>> read, T fallback, Action? onSchemaMissing = null)
        {
            try { return await read(); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                if (IsSchemaMissing(ex)) onSchemaMissing?.Invoke();
                return fallback;
            }
        }
    }
}
