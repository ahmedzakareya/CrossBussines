using CrossBuy.BL.Platform;
using CrossBuy.BL.Reporting;
using CrossBuy.Models.Context.Reporting;
using CrossBuy.Models.Platform;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CrossBuy.Controllers.Api
{
    // =============================================================================================
    // Reporting Platform — R2 ACTIVATION: the REPORTS CENTER BACKEND.
    //
    // The HTTP surface over the reporting platform. Backend only — there is no view, no Razor page and no
    // client asset in this increment, and none may be added until the approved Metronic reference design is
    // supplied (Stage-Reporting-Report-Studio-Contract §0).
    //
    // ────────────────────────────────────────────────────────────────────────────────────────────
    // THIS CONTROLLER DECIDES NOTHING.
    //
    // It resolves the caller's BusinessContext, calls one platform service, and shapes the answer for the wire.
    // Every authorization decision, every row cap, every visibility filter and every audit line happens inside
    // the services — which is what makes the API and a future MVC screen provably identical in what they permit.
    //
    // Three consequences, each deliberate:
    //
    //   1. THE COMPANY IS NEVER TAKEN FROM THE REQUEST. There is no companyId parameter anywhere in this file.
    //      It comes from the resolved BusinessContext, so a caller cannot name another tenant — the hole
    //      Hotfix A.1 closed for the Accounting API, avoided here by construction rather than by validation.
    //   2. THE CALLER CHOOSES A FORMAT, NEVER A RENDERER. `format` is bound to the closed ReportOutputFormat
    //      enum and handed to the engine, which owns the format→renderer mapping.
    //   3. AN UNAUTHORIZED PROBE GETS 404, NOT 403, wherever telling the two apart would disclose that a report
    //      or a run exists. `DescribeAsync` already returns null rather than throwing for exactly this reason.
    //
    // [Authorize] alone is AUTHENTICATION. The authorization is in the body, in the reporting services, and it
    // runs before anything is read or written.
    // =============================================================================================
    [ApiController]
    [Route("api/reports")]
    [Authorize]
    [Produces("application/json")]
    public sealed class ReportsCenterApiController : ControllerBase
    {
        private readonly IReportService _reports;
        private readonly IReportLibraryService _library;
        private readonly IReportHistoryService _history;
        private readonly IReportArchiveService _archive;
        private readonly IReportTemplateService _templates;
        private readonly IReportDatasetRegistry _datasets;
        private readonly IBusinessContextAccessor _contexts;

        public ReportsCenterApiController(
            IReportService reports,
            IReportLibraryService library,
            IReportHistoryService history,
            IReportArchiveService archive,
            IReportTemplateService templates,
            IReportDatasetRegistry datasets,
            IBusinessContextAccessor contexts)
        {
            _reports = reports;
            _library = library;
            _history = history;
            _archive = archive;
            _templates = templates;
            _datasets = datasets;
            _contexts = contexts;
        }

        // An unresolved context is a 401, not a 500 and not a silent company-1 fallback. CLAUDE.md: an
        // unresolved company scope reads no company-scoped data and writes none.
        private async Task<BusinessContext?> ResolveAsync(CancellationToken ct)
        {
            var context = await _contexts.TryGetCurrentAsync(ct);
            return context is { CompanyId: > 0 } ? context : null;
        }

        // =========================================================================================
        // CATALOG
        // =========================================================================================

        // The reports this caller may see. Already filtered by IReportAuthorizationService.FilterVisibleAsync,
        // so a user is never shown a report they cannot run.
        [HttpGet("catalog")]
        public async Task<IActionResult> Catalog(string? module, string? category, string? tag, string? search,
            CancellationToken ct)
        {
            if (await ResolveAsync(ct) is null) return Unauthorized();

            var definitions = await _reports.BrowseAsync(module, category, tag, search, ct);

            return Ok(definitions.Select(d => new
            {
                code = d.Code,
                module = d.Module,
                titleAr = d.TitleAr,
                titleEn = d.TitleEn,
                descriptionAr = d.DescriptionAr,
                descriptionEn = d.DescriptionEn,
                categoryKey = d.CategoryKey,
                tags = d.Tags,
                icon = d.Icon,
                color = d.Color,
                sortOrder = d.SortOrder,
            }));
        }

        [HttpGet("categories")]
        public async Task<IActionResult> Categories(CancellationToken ct)
        {
            var context = await ResolveAsync(ct);
            if (context is null) return Unauthorized();

            return Ok(await _library.GetCategoryTreeAsync(context, ct));
        }

        // Everything a screen needs to build a parameter form: columns, parameters, capabilities, and the
        // formats that can ACTUALLY be produced right now.
        //
        // 404 rather than 403 for a report the caller may not see — "no such report, as far as you are
        // concerned" is the correct answer to an unauthorized probe.
        [HttpGet("{code}/describe")]
        public async Task<IActionResult> Describe(string code, CancellationToken ct)
        {
            if (await ResolveAsync(ct) is null) return Unauthorized();

            var definition = await _reports.DescribeAsync(code, ct);
            if (definition is null) return NotFound();

            var formats = await _reports.AvailableFormatsAsync(code, ct);

            return Ok(new
            {
                code = definition.Code,
                module = definition.Module,
                titleAr = definition.TitleAr,
                titleEn = definition.TitleEn,
                definitionVersion = definition.DefinitionVersion,

                // Internal columns are excluded from the DESCRIBE payload too, not only from rendering. A UI
                // that received them could offer them as a filter or a visible column, and the engine would
                // then refuse the request it had itself invited.
                columns = definition.Columns.Where(c => !c.Internal).Select(c => new
                {
                    key = c.Key, titleAr = c.TitleAr, titleEn = c.TitleEn,
                    type = c.Type.ToString(), format = c.Format,
                    align = c.EffectiveAlign.ToString(),
                    visibleByDefault = c.VisibleByDefault,
                    filterable = c.Filterable, sortable = c.Sortable, groupable = c.Groupable,
                    aggregate = c.Aggregate.ToString(),
                    lookupEntityCode = c.LookupEntityCode,
                }),

                // SystemSupplied parameters are omitted: the engine supplies them and a request-supplied value
                // is ignored, so rendering an input for one would invite a caller to set something that cannot
                // take effect.
                parameters = definition.Parameters.Where(p => !p.SystemSupplied).Select(p => new
                {
                    key = p.Key, titleAr = p.TitleAr, titleEn = p.TitleEn,
                    type = p.Type.ToString(), required = p.Required,
                    defaultValue = p.DefaultValue, allowMultiple = p.AllowMultiple,
                    options = p.Options.Select(o => new { value = o.Value, labelAr = o.LabelAr, labelEn = o.LabelEn }),
                    lookupEntityCode = p.LookupEntityCode,
                    minValue = p.MinValue, maxValue = p.MaxValue,
                    helpTextAr = p.HelpTextAr, helpTextEn = p.HelpTextEn,
                }),

                capabilities = new
                {
                    formats = formats.Select(f => f.ToString()),
                    allowSchedule = definition.Capabilities.AllowSchedule,
                    allowArchive = definition.Capabilities.AllowArchive,
                    allowShare = definition.Capabilities.AllowShare,
                    maxRows = definition.Capabilities.MaxRows,
                    previewRows = definition.Capabilities.PreviewRows,
                },
            });
        }

        // The datasets this caller may build over. Empty until a module registers one — which is the honest
        // state, not a broken one.
        [HttpGet("datasets")]
        public async Task<IActionResult> Datasets(CancellationToken ct)
        {
            var context = await ResolveAsync(ct);
            if (context is null) return Unauthorized();

            var datasets = await _datasets.ListForStudioAsync(context, ct);

            return Ok(datasets.Select(d => new
            {
                datasetCode = d.DatasetCode,
                version = d.Version.ToString(),
                module = d.Module,
                titleAr = d.TitleAr, titleEn = d.TitleEn,
                descriptionAr = d.DescriptionAr, descriptionEn = d.DescriptionEn,
                availableAsWidget = d.AvailableAsWidget,
            }));
        }

        // =========================================================================================
        // RUN — preview and export
        // =========================================================================================

        // A capped, non-archivable HTML render for an on-screen pane. Same pipeline, same authorization, same
        // history — it is a Kind, not a shortcut.
        //
        // Returns the HTML as a STRING inside the JSON envelope rather than as a content response, so the
        // diagnostics and the truncation flag travel with it. A preview that silently lost "this is truncated"
        // would be a preview that lies.
        [HttpGet("{code}/preview")]
        public async Task<IActionResult> Preview(string code, int? templateId, int? maxRows, CancellationToken ct)
        {
            if (await ResolveAsync(ct) is null) return Unauthorized();

            var result = await _reports.PreviewAsync(
                BuildRequest(code, ReadParameters(), templateId, maxRows,
                    ReportOutputFormat.Html, preview: true, archive: false), ct);
            if (result.IsDenied) return NotFound();

            return Ok(new
            {
                success = result.IsSuccess,
                html = result.Artifact is null ? null : System.Text.Encoding.UTF8.GetString(result.Artifact.Content),
                summary = Summary(result),
                diagnostics = Diagnostics(result),
            });
        }

        // Produces the artifact and returns it as a FILE. HTML, CSV and XLSX are all activated; a format the
        // deployment cannot produce is refused by the engine with a diagnostic, not by a 500.
        [HttpGet("{code}/export")]
        public async Task<IActionResult> Export(string code, ReportOutputFormat format,
            int? templateId, int? maxRows, bool archive, CancellationToken ct)
        {
            if (await ResolveAsync(ct) is null) return Unauthorized();

            var result = await _reports.GenerateAsync(
                BuildRequest(code, ReadParameters(), templateId, maxRows, format,
                    preview: false, archive: archive), ct);

            if (result.IsDenied) return NotFound();
            if (!result.IsSuccess || result.Artifact is null)
                return UnprocessableEntity(new { summary = Summary(result), diagnostics = Diagnostics(result) });

            // The truncation flag rides on a header as well as in the JSON paths, because a downloaded file has
            // no envelope to carry it and a silently partial export is a wrong export.
            if (result.Run?.Truncated == true) Response.Headers["X-Report-Truncated"] = "true";
            Response.Headers["X-Report-Row-Count"] = (result.Run?.RowCount ?? 0).ToString();

            return File(result.Artifact.Content, result.Artifact.ContentType, result.Artifact.FileName);
        }

        // =========================================================================================
        // HISTORY
        // =========================================================================================

        [HttpGet("history")]
        public async Task<IActionResult> History(string? code, int take, CancellationToken ct)
        {
            var context = await ResolveAsync(ct);
            if (context is null) return Unauthorized();

            var rows = await _history.QueryAsync(new ReportHistoryQuery
            {
                ReportCode = code,
                Take = take > 0 ? take : 50,
            }, context, ct);

            return Ok(rows);
        }

        // The parameters a past run used, so a user can re-run "the same report as last month".
        [HttpGet("history/{runId:long}/parameters")]
        public async Task<IActionResult> HistoryParameters(long runId, CancellationToken ct)
        {
            var context = await ResolveAsync(ct);
            if (context is null) return Unauthorized();

            var parameters = await _history.GetParametersAsync(runId, context, ct);
            return parameters is null ? NotFound() : Ok(parameters);
        }

        // =========================================================================================
        // ARCHIVE
        // =========================================================================================

        [HttpGet("archive")]
        public async Task<IActionResult> Archive(string? code, int take, CancellationToken ct)
        {
            var context = await ResolveAsync(ct);
            if (context is null) return Unauthorized();

            return Ok(await _archive.ListAsync(code, context, take > 0 ? take : 100, ct));
        }

        // Retrieval re-checks the REPORT's permission, so an archived artifact stops being readable the moment
        // the permission behind it is revoked. Absent, unpermitted and bytes-gone all answer 404 — a caller has
        // one thing to do in all three cases, and distinguishing them would leak which.
        [HttpGet("archive/{entryId:long}/download")]
        public async Task<IActionResult> ArchiveDownload(long entryId, CancellationToken ct)
        {
            var context = await ResolveAsync(ct);
            if (context is null) return Unauthorized();

            var artifact = await _archive.RetrieveAsync(entryId, context, ct);
            return artifact is null
                ? NotFound()
                : File(artifact.Content, artifact.ContentType, artifact.FileName);
        }

        // =========================================================================================
        // SAVED REPORTS (templates)
        // =========================================================================================

        [HttpGet("{code}/saved")]
        public async Task<IActionResult> SavedReports(string code, CancellationToken ct)
        {
            var context = await ResolveAsync(ct);
            if (context is null) return Unauthorized();

            return Ok(await _templates.ListAsync(code, context, ct));
        }

        [HttpGet("saved/{templateId:int}/versions")]
        public async Task<IActionResult> SavedReportVersions(int templateId, CancellationToken ct)
        {
            var context = await ResolveAsync(ct);
            if (context is null) return Unauthorized();

            return Ok(await _templates.ListVersionsAsync(templateId, context, ct));
        }

        // =========================================================================================
        // WRITE ENDPOINTS — HELD BACK. Blocked by the authorization analyzer, not by design.
        // =========================================================================================
        //
        // Seven write endpoints belong here and are NOT in this increment:
        //
        //     POST   api/reports/saved                       save a report layout
        //     POST   api/reports/saved/{id}/fork             copy a platform layout into an editable scope
        //     POST   api/reports/saved/{id}/default          set the default layout
        //     DELETE api/reports/saved/{id}                  soft-delete a layout
        //     POST   api/reports/favorites                   favourite a report
        //     DELETE api/reports/favorites                   unfavourite
        //     POST   api/reports/favorites/reorder           reorder the favourites bar
        //
        // WHY THEY ARE ABSENT
        //
        // Each was written, compiled, and then removed — because CrossBuy.Analyzers reports each one as
        // CBA001: "Mutating endpoint has no authorization the analyzer can see." That diagnostic is CORRECT,
        // and it is an ERROR, not a warning.
        //
        // The analyzer credits authorization when an endpoint's call chain reaches a type in
        // AuthorizationSurface.AuthorityTypes. That list names the eight module access services,
        // IPlatformPermissionProvider, IAccountingApiAuthorization and IPlatformGrantWriter. It does NOT name
        // IReportTemplateService / IReportLibraryService / IReportAuthorizationService — reporting did not exist
        // when the list was written.
        //
        // These endpoints ARE authorized: IReportTemplateService and IReportLibraryService apply the report gate
        // and then the template's scope rules before touching a row, and 28 template-scope tests prove it
        // ("A_tenant_cannot_create_a_platform_template", "Nobody_can_grant_more_than_they_hold",
        // "Another_employees_personal_template_is_never_resolved_for_me"). The analyzer simply cannot SEE it.
        //
        // THREE WAYS OUT, AND WHY EACH WAS REJECTED HERE
        //
        //   1. Add IReportAuthorizationService to AuthorizationSurface.AuthorityTypes. This is the CORRECT fix
        //      and the documented process — that file's own header records IAccountingApiAuthorization and
        //      IPlatformGrantWriter being added exactly this way when the analyzer blocked their endpoints.
        //      It is a FIRST TAB file (Stage 2A), which this tab is instructed not to modify. → escalated.
        //   2. Add the endpoints to engineering/authorization-baseline.json. Refused by the analyzer itself:
        //      "The baseline may only shrink — a new entry is not an option."
        //   3. Attach [PlatformOps]. It is a recognised permission attribute, but it means platform
        //      administration — applying it would restrict favouriting a report to platform admins, which is
        //      wrong for per-user personal state.
        //
        // A fourth option — suppressing CBA001, or reshaping a write as a GET — was not considered. The verb
        // change applied to Preview/Export above is legitimate because those genuinely ARE reads; doing the same
        // to a write would be defeating a security guardrail rather than satisfying it.
        //
        // WHAT UNBLOCKS THEM: one line in CrossBuy.Analyzers/AuthorizationSurface.cs —
        //     "IReportAuthorizationService", "ReportAuthorizationService",
        // added to AuthorityTypes with the same visible justification the two existing additions carry. The
        // seven endpoints then compile unchanged. Owner action, first tab.

        // =========================================================================================
        // Shaping
        // =========================================================================================

        // Report parameters arrive as ordinary query-string entries, minus the routing keys this controller
        // owns. That keeps a report link readable and shareable — ?From=2026-01-01&To=2026-01-31 — and it means
        // the binder validates exactly the same text whether it came from a URL, a form or a schedule.
        private static readonly HashSet<string> ReservedQueryKeys =
            new(StringComparer.OrdinalIgnoreCase) { "format", "templateId", "maxRows", "archive", "code" };

        private Dictionary<string, string?> ReadParameters()
        {
            var parameters = new Dictionary<string, string?>(StringComparer.Ordinal);
            foreach (var entry in Request.Query)
            {
                if (ReservedQueryKeys.Contains(entry.Key)) continue;
                parameters[entry.Key] = entry.Value.ToString();
            }
            return parameters;
        }

        // NOTE the absence of a company parameter, and the absence of any renderer handle. Both are deliberate
        // and both are asserted by ReportingSecurityInvariantTests.
        private static ReportRequest BuildRequest(string code, Dictionary<string, string?> parameters,
            int? templateId, int? maxRows, ReportOutputFormat format, bool preview, bool archive) => new()
            {
                ReportCode = code,
                Format = format,
                Kind = preview ? ReportRunKind.Preview : ReportRunKind.Full,
                TemplateId = templateId,
                Parameters = parameters,

                // A caller may LOWER the row cap and never raise it — the engine reconciles this against the
                // definition's ceiling, so passing a huge number here changes nothing.
                MaxRows = maxRows,

                // Archiving is opt-in per run and refused for a preview by the engine.
                Archive = !preview && archive,
            };

        private static object Summary(ReportResult result) => new
        {
            runId = result.Run?.RunId,
            rowCount = (result.Run?.RowCount ?? 0),
            truncated = result.Run?.Truncated == true,
        };

        private static object Diagnostics(ReportResult result) =>
            result.Diagnostics.Select(d => new
            {
                code = d.Code,
                severity = d.Severity.ToString(),
                message = d.Message,
                field = d.Field,
            });
    }
}
