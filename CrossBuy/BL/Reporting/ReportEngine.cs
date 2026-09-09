using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Reporting;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL.Reporting
{
    // ============================================================================================
    // Reporting Platform (ADR-037) — THE ENGINE.
    //
    // One method, one fixed pipeline, in this order and no other:
    //
    //    1. RESOLVE     the definition from the catalog          (unregistered code → exception; a wiring bug)
    //    2. AUTHORIZE   the report                               (denied → a Denied RESULT, and it is RECORDED)
    //    3. RESOLVE     the template + version                   (missing/invisible → degrade + warn)
    //    4. MERGE       request over template over definition     (precedence below)
    //    5. BIND        parameters                               (invalid → Failed result + diagnostics)
    //    6. FETCH       from the module's data source            (read-only)
    //    7. SHAPE       filter → sort → group → aggregate → cap
    //    8. PRODUCE     via the output pipeline                  (renderer or exporter — engine does not care)
    //    9. ARCHIVE     if asked and allowed
    //   10. RECORD      the run in history
    //
    // Steps 2 and 10 are why this is a pipeline and not a helper: authorization happens BEFORE any data is read,
    // and history is written on EVERY path including denial and failure. A module calling a renderer directly
    // would skip both, which is the whole reason IReportService is the only public door.
    //
    // MERGE PRECEDENCE — request wins over template, template wins over definition. With ONE exception:
    // FILTERS ARE ADDITIVE (template filters AND request filters both apply). Overriding would let a request drop
    // a filter the saved layout imposed, which WIDENS the result set — the same asymmetry the shaper enforces.
    // Narrowing is always safe; widening never is.
    // ============================================================================================

    public sealed class ReportEngineOptions
    {
        // Row ceiling when a definition declares none. 50k rows is roughly the point beyond which no human reads
        // the output and the honest answer is "export it or narrow the filter".
        public int DefaultMaxRows { get; set; } = 50_000;

        // Rows a preview renders when a definition declares none.
        public int DefaultPreviewRows { get; set; } = 100;

        // Included in the rendered footer/meta. Off by default — a report page belongs to the business, not to
        // the tooling that produced it.
        public bool ShowEngineNameInOutput { get; set; }
    }

    // Visual identity per company. A seam so a deployment can brand reports from anywhere (a theme table, a CDN)
    // without touching the engine.
    public interface IReportBrandingProvider
    {
        Task<ReportBranding> GetAsync(BusinessContext context, CancellationToken cancellationToken = default);
    }

    // Reads the existing Companies row. READ-ONLY, one table, no production service called.
    public class CompanyReportBrandingProvider : IReportBrandingProvider
    {
        private readonly CrossDbContext _db;

        public CompanyReportBrandingProvider(CrossDbContext db) { _db = db; }

        public async Task<ReportBranding> GetAsync(BusinessContext context,
            CancellationToken cancellationToken = default)
        {
            if (context.CompanyId <= 0) return ReportBranding.Default;

            var row = await _db.Companies.AsNoTracking()
                .Where(c => c.CompanyID == context.CompanyId)
                .Select(c => new { c.CompanyName, c.ComoanyNameAr, c.RegistrationNumber, c.TaxNumber })
                .FirstOrDefaultAsync(cancellationToken);

            if (row == null) return ReportBranding.Default;

            var footer = string.Join("  ·  ", new[]
            {
                string.IsNullOrWhiteSpace(row.RegistrationNumber) ? null : $"CR {row.RegistrationNumber}",
                string.IsNullOrWhiteSpace(row.TaxNumber) ? null : $"TRN {row.TaxNumber}",
            }.Where(s => s != null));

            return new ReportBranding
            {
                // ComoanyNameAr is the existing (misspelled) Arabic-name column. Referenced as-is: renaming a
                // production column is out of scope, and quietly mapping it to something tidier here would hide
                // which column the report is actually reading.
                CompanyName = row.ComoanyNameAr ?? row.CompanyName,
                CompanyNameEn = row.CompanyName,

                // The logo is NOT loaded here. A report needs a data: URI (a headless PDF browser may have no
                // route back to the app), and turning a stored path into embedded bytes is a file-system read per
                // render. That belongs in a caching branding provider, which is a later slice.
                LogoDataUri = null,
                FooterNote = footer.Length == 0 ? null : footer,
            };
        }
    }

    public interface IReportEngine
    {
        // The full pipeline, against an EXPLICIT context. Used directly by the scheduler, which has no request.
        Task<ReportResult> GenerateAsync(ReportRequest request, BusinessContext context,
            CancellationToken cancellationToken = default);
    }

    public class ReportEngine : IReportEngine
    {
        public const string CodeUnsupportedFormat = "format_not_supported";
        public const string CodeArchiveRefused = "archive_refused";
        public const string CodeDataSourceFailed = "data_source_failed";
        public const string CodeRenderFailed = "render_failed";

        private readonly IReportCatalog _catalog;
        private readonly IReportAuthorizationService _authorization;
        private readonly IReportTemplateService _templates;
        private readonly IReportParameterBinder _binder;
        private readonly IReportDataSourceRegistry _dataSources;
        private readonly IReportDataShaper _shaper;
        private readonly IReportOutputPipeline _output;
        private readonly IReportArchiveService _archive;
        private readonly IReportHistoryService _history;
        private readonly IReportBrandingProvider _branding;
        private readonly ReportEngineOptions _options;
        private readonly IReportClock _clock;
        private readonly ILogger<ReportEngine> _logger;

        public ReportEngine(IReportCatalog catalog, IReportAuthorizationService authorization,
            IReportTemplateService templates, IReportParameterBinder binder,
            IReportDataSourceRegistry dataSources, IReportDataShaper shaper, IReportOutputPipeline output,
            IReportArchiveService archive, IReportHistoryService history, IReportBrandingProvider branding,
            ReportEngineOptions options, IReportClock clock, ILogger<ReportEngine> logger,
            IReportAssetService? assets = null)
        {
            _assets = assets;
            _catalog = catalog;
            _authorization = authorization;
            _templates = templates;
            _binder = binder;
            _dataSources = dataSources;
            _shaper = shaper;
            _output = output;
            _archive = archive;
            _history = history;
            _branding = branding;
            _options = options;
            _clock = clock;
            _logger = logger;
        }

        // OPTIONAL on purpose. A deployment (or a test host) with no asset store still renders every report; it
        // simply has no images to inline. Requiring it would couple every existing report to a V2 table.
        private readonly IReportAssetService? _assets;

        // ----------------------------------------------------------------------------------------------
        // ASSET INLINING. The bytes are read through IReportAssetService, which applies the company predicate on
        // its own query — so an id belonging to another tenant resolves to nothing here exactly as it does
        // everywhere else, and a layout carrying a foreign id renders a gap rather than a leak.
        //
        // Nothing runs at all unless a visual layout actually references an image. That is what keeps every
        // existing report (and a test host with no ReportAssets table) on precisely the path it was on before.
        // ----------------------------------------------------------------------------------------------
        private async Task<IReadOnlyDictionary<int, string>> ResolveAssetsAsync(ReportVisualLayout? visual,
            BusinessContext context, CancellationToken cancellationToken)
        {
            var empty = (IReadOnlyDictionary<int, string>)new Dictionary<int, string>();
            if (visual is null || _assets is null) return empty;

            var ids = visual.Bands
                .SelectMany(b => b.Elements)
                .Where(e => e.Kind == ReportElementKind.Image && e.AssetId is > 0)
                .Select(e => e.AssetId!.Value)
                .Distinct()
                .ToList();

            if (ids.Count == 0) return empty;

            var map = new Dictionary<int, string>();
            foreach (var id in ids)
            {
                var asset = await _assets.ReadAsync(id, context, cancellationToken);
                if (asset is null) continue;   // not this company's, or deleted — renders as an empty box
                map[id] = "data:" + asset.Value.ContentType + ";base64,"
                        + Convert.ToBase64String(asset.Value.Bytes);
            }
            return map;
        }

        public async Task<ReportResult> GenerateAsync(ReportRequest request, BusinessContext context,
            CancellationToken cancellationToken = default)
        {
            var startedAt = _clock.LocalNow;
            var stopwatch = Stopwatch.StartNew();
            var diagnostics = new List<ReportDiagnostic>();
            var culture = ResolveCulture(request.Culture);

            // ---- 1. definition ----------------------------------------------------------------------------
            // Throws for an unknown code. Deliberate: see ReportNotRegisteredException — a report code that does
            // not exist is a wiring bug, and returning an empty report would let a broken deployment look normal.
            var definition = _catalog.GetDefinition(request.ReportCode);

            // ---- 2. authorization, BEFORE any data is read ------------------------------------------------
            var decision = await _authorization.AuthorizeReportAsync(definition, ReportAccessLevel.Run, context,
                cancellationToken);

            if (!decision.Allowed)
            {
                // A denial is a recorded run. See ReportHistoryService: a history containing only successes would
                // be a success log wearing an audit log's name.
                await _history.RecordAsync(new ReportRunRecord
                {
                    ReportCode = definition.Code,
                    Kind = request.Kind,
                    Status = ReportRunStatus.Denied,
                    Format = request.Format,
                    ErrorCode = decision.ReasonCode,
                    ErrorMessage = decision.Reason,
                    StartedAt = startedAt,
                    DurationMs = (int)stopwatch.ElapsedMilliseconds,
                    CorrelationId = request.CorrelationId,
                }, context, cancellationToken);

                return ReportResult.Denied(definition.Code, request.Format,
                    decision.ReasonCode ?? ReportAuthorizationService.CodeNoModulePermission,
                    decision.Reason ?? "You may not run this report.");
            }

            // ---- format capability ------------------------------------------------------------------------
            if (!definition.Capabilities.SupportsFormat(request.Format))
                return await FailAsync(definition, request, context, startedAt, stopwatch, diagnostics,
                    ReportDiagnostic.Error(CodeUnsupportedFormat,
                        $"Report '{definition.Code}' cannot be produced as {request.Format}."),
                    cancellationToken);

            // ---- 3. template ------------------------------------------------------------------------------
            var resolution = await _templates.ResolveAsync(definition, request.TemplateId,
                request.TemplateVersionNo, context, cancellationToken);
            diagnostics.AddRange(resolution.Diagnostics);

            // ---- 4. merge ---------------------------------------------------------------------------------
            var layout = resolution.Layout;

            var parameters = MergeParameters(layout.Parameters, request.Parameters);

            // Additive, not overriding — see the precedence note in the header comment.
            var filters = layout.Filters.Concat(request.Filters).ToList();

            var sorts = request.Sorts.Count > 0
                ? request.Sorts
                : layout.Sorts.Count > 0 ? layout.Sorts : definition.DefaultSorts;

            var groupings = request.Groupings.Count > 0
                ? request.Groupings
                : layout.Groupings.Count > 0 ? layout.Groupings : definition.DefaultGroupings;

            var visibleColumns = request.VisibleColumns.Count > 0
                ? request.VisibleColumns
                : layout.VisibleColumns;

            var pageSetup = request.PageSetup ?? layout.PageSetup;

            // Same precedence as PageSetup directly above: an explicit request wins, else the stored template's.
            var visual = request.Visual ?? layout.Visual;

            // A DESIGNED report carries its own paper, and it is the authority for it: the designer positioned
            // every element against that page's printable box in millimetres, so honouring a different PageSetup
            // here would print the document at a size it was never laid out for.
            if (visual is not null) pageSetup = visual.Page;

            var maxRows = ResolveMaxRows(definition, request);

            // ---- 5. parameters ----------------------------------------------------------------------------
            var binding = _binder.Bind(definition, parameters, context, culture);
            diagnostics.AddRange(binding.Diagnostics);

            if (!binding.IsValid)
                return await FailAsync(definition, request, context, startedAt, stopwatch, diagnostics, null,
                    cancellationToken, resolution, binding.Parameters);

            var parameterSet = binding.Parameters!;

            // ---- 6. data ----------------------------------------------------------------------------------
            var dataSource = _dataSources.Resolve(definition.DataSourceKey, definition.Code);

            var requestedColumns = ResolveRequestedColumns(definition, visibleColumns, groupings);

            ReportDataSet data;
            try
            {
                data = await dataSource.FetchAsync(new ReportDataQuery
                {
                    Definition = definition,
                    Context = context,
                    Parameters = parameterSet,
                    Filters = filters,
                    Sorts = sorts,
                    Groupings = groupings,
                    RequestedColumns = requestedColumns,
                    MaxRows = maxRows,
                    Kind = request.Kind,
                    Culture = culture,
                }, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                // Cancellation is the caller leaving, not a failure of the report. Rethrown so it does not become
                // a Failed run in the history.
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Report data source '{Key}' failed for {ReportCode}.",
                    definition.DataSourceKey, definition.Code);

                return await FailAsync(definition, request, context, startedAt, stopwatch, diagnostics,
                    ReportDiagnostic.Error(CodeDataSourceFailed,
                        $"The data source for report '{definition.Code}' failed: {ex.Message}"),
                    cancellationToken, resolution, parameterSet);
            }

            // ---- 7. shape ---------------------------------------------------------------------------------
            var shaped = _shaper.Shape(definition, data, new ReportShapeRequest
            {
                Filters = filters,
                Sorts = sorts,
                Groupings = groupings,
                VisibleColumns = visibleColumns,
                MaxRows = maxRows,
                ShowGrandTotals = layout.ShowGrandTotals,
            }, culture);

            diagnostics.AddRange(shaped.Diagnostics);

            if (!shaped.IsValid)
                return await FailAsync(definition, request, context, startedAt, stopwatch, diagnostics, null,
                    cancellationToken, resolution, parameterSet);

            var view = shaped.View!;

            // ---- 8. produce -------------------------------------------------------------------------------
            var branding = await _branding.GetAsync(context, cancellationToken);

            // The CompanyScoped switch that stood here is gone with the thing it switched: no renderer prints
            // a company name any more, so there was nothing left for a per-report flag to turn off. Keeping a
            // flag nobody reads is how a codebase acquires settings that do nothing.

            var renderContext = new ReportRenderContext
            {
                View = view,
                Format = request.Format,
                PageSetup = pageSetup,
                Culture = culture,
                Branding = branding,
                Title = ResolveTitle(definition, layout, culture),
                Subtitle = culture.TwoLetterISOLanguageName == "ar"
                    ? definition.DescriptionAr
                    : definition.DescriptionEn,
                Parameters = BuildParameterDisplay(definition, parameterSet, culture),
                GeneratedAt = startedAt,
                GeneratedBy = context.EmployeeId?.ToString(),
                IsPreview = request.Kind == ReportRunKind.Preview,

                // V2. Null for every column-list report, which is all of them until somebody designs one.
                Visual = visual,
                Assets = await ResolveAssetsAsync(visual, context, cancellationToken),
            };

            ReportArtifact artifact;
            try
            {
                artifact = await _output.ProduceAsync(renderContext, cancellationToken);
            }
            catch (ReportRendererUnavailableException ex)
            {
                // An unbound engine (today: PDF) is a configuration state, not a crash. It comes back as a Failed
                // result with the operator-facing reason the converter supplied.
                return await FailAsync(definition, request, context, startedAt, stopwatch, diagnostics,
                    ReportDiagnostic.Error(CodeRenderFailed, ex.Message), cancellationToken, resolution,
                    parameterSet);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Report output failed for {ReportCode} as {Format}.",
                    definition.Code, request.Format);

                return await FailAsync(definition, request, context, startedAt, stopwatch, diagnostics,
                    ReportDiagnostic.Error(CodeRenderFailed,
                        $"Producing report '{definition.Code}' as {request.Format} failed: {ex.Message}"),
                    cancellationToken, resolution, parameterSet);
            }

            // ---- 9. archive -------------------------------------------------------------------------------
            long? archiveEntryId = null;
            if (request.Archive)
            {
                if (request.Kind == ReportRunKind.Preview)
                    // A preview is capped and marked provisional; archiving one would put a document in the
                    // archive that is not the report.
                    diagnostics.Add(ReportDiagnostic.Warning(CodeArchiveRefused,
                        "A preview is never archived."));
                else if (!definition.Capabilities.AllowArchive)
                    diagnostics.Add(ReportDiagnostic.Warning(CodeArchiveRefused,
                        $"Report '{definition.Code}' does not permit archiving."));
                else
                    archiveEntryId = await _archive.ArchiveAsync(artifact, definition, resolution.Template?.Id,
                        resolution.VersionNo, runId: null, context, cancellationToken: cancellationToken);
            }

            // ---- 10. history ------------------------------------------------------------------------------
            stopwatch.Stop();

            var parametersJson = SerializeParameters(parameterSet);
            var parametersHash = ReportRequest.HashParameters(parameterSet.RawText);

            var runId = await _history.RecordAsync(new ReportRunRecord
            {
                ReportCode = definition.Code,
                TemplateId = resolution.Template?.Id,
                TemplateVersionNo = resolution.VersionNo,
                Kind = request.Kind,
                Status = ReportRunStatus.Succeeded,
                Format = request.Format,
                ParametersJson = parametersJson,
                ParametersHash = parametersHash,
                RowCount = view.RowCount,
                DurationMs = (int)stopwatch.ElapsedMilliseconds,
                ArchiveEntryId = archiveEntryId,
                StartedAt = startedAt,
                CorrelationId = request.CorrelationId,
            }, context, cancellationToken);

            return ReportResult.Success(definition.Code, request.Format, artifact, new ReportRunSummary
            {
                RunId = runId,
                ReportCode = definition.Code,
                TemplateId = resolution.Template?.Id,
                TemplateVersionNo = resolution.VersionNo,
                DefinitionVersion = definition.DefinitionVersion,
                Kind = request.Kind,
                RowCount = view.RowCount,
                Truncated = view.Truncated,
                DurationMs = (int)stopwatch.ElapsedMilliseconds,
                ArchiveEntryId = archiveEntryId,
                StartedAt = startedAt,
                ParametersHash = parametersHash,
            }, diagnostics);
        }

        // ------------------------------------------------------------------------------------------------
        // One failure path, so every failure is recorded identically. A second ad-hoc `return Failed(...)`
        // somewhere in the pipeline is how a failure mode ends up missing from history.
        // ------------------------------------------------------------------------------------------------
        private async Task<ReportResult> FailAsync(ReportDefinition definition, ReportRequest request,
            BusinessContext context, DateTime startedAt, Stopwatch stopwatch, List<ReportDiagnostic> diagnostics,
            ReportDiagnostic? extra, CancellationToken cancellationToken,
            ReportTemplateResolution? resolution = null, ReportParameterSet? parameters = null)
        {
            if (extra != null) diagnostics.Add(extra);
            stopwatch.Stop();

            var first = diagnostics.FirstOrDefault(d => d.Severity == ReportDiagnosticSeverity.Error);

            var runId = await _history.RecordAsync(new ReportRunRecord
            {
                ReportCode = definition.Code,
                TemplateId = resolution?.Template?.Id,
                TemplateVersionNo = resolution?.VersionNo,
                Kind = request.Kind,
                Status = ReportRunStatus.Failed,
                Format = request.Format,
                ParametersJson = parameters == null ? null : SerializeParameters(parameters),
                ParametersHash = parameters == null ? null : ReportRequest.HashParameters(parameters.RawText),
                DurationMs = (int)stopwatch.ElapsedMilliseconds,
                ErrorCode = first?.Code,
                ErrorMessage = first?.Message,
                StartedAt = startedAt,
                CorrelationId = request.CorrelationId,
            }, context, cancellationToken);

            return ReportResult.Failed(definition.Code, request.Format, diagnostics, new ReportRunSummary
            {
                RunId = runId,
                ReportCode = definition.Code,
                TemplateId = resolution?.Template?.Id,
                TemplateVersionNo = resolution?.VersionNo,
                DefinitionVersion = definition.DefinitionVersion,
                Kind = request.Kind,
                DurationMs = (int)stopwatch.ElapsedMilliseconds,
                StartedAt = startedAt,
            });
        }

        // ------------------------------------------------------------------------------------------------
        private static Dictionary<string, string?> MergeParameters(
            IReadOnlyDictionary<string, string?> template, IReadOnlyDictionary<string, string?> request)
        {
            var merged = new Dictionary<string, string?>(StringComparer.Ordinal);
            foreach (var kv in template) merged[kv.Key] = kv.Value;

            // Request last — it wins. An empty string from a request still wins, because "clear this filter" is a
            // real intent and treating blank as absent would make a baked template value un-clearable.
            foreach (var kv in request) merged[kv.Key] = kv.Value;
            return merged;
        }

        // The cap is the MINIMUM of every applicable limit, and a caller can only ever lower it. A request that
        // could raise the definition's ceiling would make the ceiling advisory.
        private int ResolveMaxRows(ReportDefinition definition, ReportRequest request)
        {
            if (request.Kind == ReportRunKind.Preview)
            {
                var preview = definition.Capabilities.PreviewRows > 0
                    ? definition.Capabilities.PreviewRows
                    : _options.DefaultPreviewRows;
                return request.MaxRows is > 0 ? Math.Min(preview, request.MaxRows.Value) : preview;
            }

            var ceiling = definition.Capabilities.MaxRows > 0
                ? definition.Capabilities.MaxRows
                : _options.DefaultMaxRows;

            return request.MaxRows is > 0 ? Math.Min(ceiling, request.MaxRows.Value) : ceiling;
        }

        // The columns the data source is asked for: the visible set PLUS every grouping key. Grouping on a column
        // that was not fetched would silently band everything into one "(blank)" group.
        private static IReadOnlyList<ReportColumn> ResolveRequestedColumns(ReportDefinition definition,
            IReadOnlyList<string> visibleColumns, IReadOnlyList<ReportGrouping> groupings)
        {
            var keys = new List<string>(visibleColumns.Count > 0
                ? visibleColumns
                : definition.DefaultVisibleColumns.Select(c => c.Key));

            foreach (var grouping in groupings)
                if (!keys.Contains(grouping.Field, StringComparer.Ordinal)) keys.Add(grouping.Field);

            var columns = keys.Select(definition.FindColumn).Where(c => c != null).Select(c => c!).ToList();

            // A layout naming only removed columns must not turn into an empty fetch.
            return columns.Count > 0 ? columns : definition.Columns;
        }

        private static string ResolveTitle(ReportDefinition definition, ReportLayout layout, CultureInfo culture)
        {
            var arabic = culture.TwoLetterISOLanguageName == "ar";
            var overridden = arabic ? layout.TitleOverride : (layout.TitleOverrideEn ?? layout.TitleOverride);
            return string.IsNullOrWhiteSpace(overridden) ? definition.Title(arabic) : overridden!;
        }

        // The parameter strip printed in the report header. System-supplied parameters are EXCLUDED: "CompanyId: 1"
        // on a printed report is noise, and the company is already named in the header.
        private static IReadOnlyList<ReportParameterDisplay> BuildParameterDisplay(ReportDefinition definition,
            ReportParameterSet parameters, CultureInfo culture)
        {
            var arabic = culture.TwoLetterISOLanguageName == "ar";
            var result = new List<ReportParameterDisplay>();

            foreach (var descriptor in definition.Parameters.Where(p => !p.SystemSupplied))
            {
                var value = parameters.Values.FirstOrDefault(v =>
                    string.Equals(v.Key, descriptor.Key, StringComparison.Ordinal));
                if (value == null || !value.HasValue) continue;

                // Formatted through a synthetic column so a parameter and the column it filters print identically.
                var asColumn = new ReportColumn
                {
                    Key = descriptor.Key,
                    TitleAr = descriptor.TitleAr,
                    TitleEn = descriptor.TitleEn,
                    Type = descriptor.Type,
                };

                var text = value.IsMultiple
                    ? string.Join(", ", value.Values.Select(v => ReportValues.Format(v, asColumn, culture)))
                    : ReportValues.Format(value.Value, asColumn, culture);

                // Closed-option parameters print the LABEL, not the stored code: "Posted", not "P".
                var option = descriptor.Options.FirstOrDefault(o =>
                    string.Equals(o.Value, value.RawText, StringComparison.Ordinal));
                if (option != null) text = arabic ? option.LabelAr : DisplayName.Or(option.LabelEn, option.LabelAr);

                result.Add(new ReportParameterDisplay
                {
                    Label = arabic ? descriptor.TitleAr : DisplayName.Or(descriptor.TitleEn, descriptor.TitleAr),
                    Value = text,
                });
            }

            return result;
        }

        // Only the caller-supplied text is stored, never the resolved values, and never the system-supplied keys.
        // Storing CompanyId in a history row would make the row look like a caller-chosen company.
        private static string SerializeParameters(ReportParameterSet parameters) =>
            JsonSerializer.Serialize(parameters.RawText);

        private static CultureInfo ResolveCulture(string? name)
        {
            if (string.IsNullOrWhiteSpace(name)) return CultureInfo.CurrentUICulture;
            try
            {
                return CultureInfo.GetCultureInfo(name);
            }
            catch (CultureNotFoundException)
            {
                // An unknown culture falls back to the request's own — never an exception. A bad `culture=xx` in a
                // URL must not be able to break a report.
                return CultureInfo.CurrentUICulture;
            }
        }
    }
}