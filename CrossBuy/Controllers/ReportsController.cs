using CrossBuy.BL.Reporting;
using CrossBuy.Models;
using Microsoft.AspNetCore.Mvc;

namespace CrossBuy.Controllers
{
    // =============================================================================================
    // CrossBusiness Reporting Platform — R3: THE REPORTS CENTER and THE REPORT VIEWER.
    //
    // The first user-facing Reporting product. Two screens, plus the two file responses they link to.
    //
    // ────────────────────────────────────────────────────────────────────────────────────────────
    // EVERY ACTION IS A GET, AND EVERY ACTION IS EXPLICITLY [HttpGet].
    //
    // Not because writes were avoided — the seven writes belong on the API controller and are held back by
    // CBA001 (see ReportsCenterWriteEndpoints.cs.pending) — but because every action HERE genuinely is a read:
    // browsing a catalog, running a report, downloading an artifact. The verb is honest, and it buys the two
    // things a report screen actually needs: a bookmarkable URL and a shareable link.
    //
    // The [HttpGet] is explicit rather than implied. An MVC action with no verb attribute is reachable by POST
    // as well, and the authorization analyzer is right to treat that as a mutating surface. Declaring the verb
    // is how the screen stays a read in the analyzer's eyes as well as the author's.
    //
    // ────────────────────────────────────────────────────────────────────────────────────────────
    // THIS CONTROLLER DECIDES NOTHING — the same rule as ReportsCenterApiController, held for the same reason:
    // the API and the screen must permit EXACTLY the same things. Both call the same services; the screen adds
    // IReportsCenterPresenter, which shapes but never authorizes.
    //
    //   · No companyId parameter exists in this file. The tenant comes from the resolved BusinessContext.
    //   · No renderer handle is accepted. The caller names a FORMAT, from a closed enum.
    //   · A report the caller may not see answers 404, never 403 — an unauthorized probe must not be able to
    //     enumerate the catalog by watching status codes.
    //
    // [SessionValidation] is AUTHENTICATION, matching every other backend screen in the product. The
    // authorization is inside the reporting services and runs before a row is read.
    // =============================================================================================
    [SessionValidation]
    public class ReportsController : Controller
    {
        private readonly IReportsCenterPresenter _presenter;
        private readonly IReportService _reports;
        private readonly IReportArchiveService _archive;
        private readonly IReportHistoryService _history;
        private readonly CrossBuy.BL.Platform.IBusinessContextAccessor _contexts;
        private readonly IReportStudioService _studio;

        public ReportsController(
            IReportsCenterPresenter presenter,
            IReportService reports,
            IReportArchiveService archive,
            IReportHistoryService history,
            CrossBuy.BL.Platform.IBusinessContextAccessor contexts,
            IReportStudioService studio)
        {
            _studio = studio;
            _presenter = presenter;
            _reports = reports;
            _archive = archive;
            _history = history;
            _contexts = contexts;
        }

        private static bool Arabic =>
            System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ar";

        // =========================================================================================
        // THE REPORTS CENTER
        // =========================================================================================
        [HttpGet]
        public async Task<IActionResult> Index(
            string? search, string? module, string? category, string? tag, bool favorites,
            int savedPage = 1, int recentPage = 1,
            CancellationToken cancellationToken = default)
        {
            var model = await _presenter.BuildCenterAsync(new ReportsCenterQuery
            {
                Search = search,
                Module = module,
                CategoryKey = category,
                Tag = tag,
                FavoritesOnly = favorites,
                Arabic = Arabic,

                // Two page numbers, one per paged table. Clamped in the presenter, which also pulls a page
                // past the end back onto the last real page rather than serving an empty table.
                SavedPage = savedPage,
                RecentPage = recentPage,
            }, cancellationToken);

            return View(model);
        }

        // =========================================================================================
        // THE REPORT STUDIO
        //
        // A GET like every other action here, and for the same reason: opening the builder is a read. The
        // builder's own writes (preview, save) are POSTs on ReportStudioApiController, which carries the
        // antiforgery filter this controller deliberately does not — the same read/write split the Reports
        // Center already uses.
        //
        // The screen is handed only what the CALLER may build over. Nothing about the shape of a dataset the
        // caller cannot use reaches the browser, so there is no client-side filtering to get wrong.
        // =========================================================================================
        // =========================================================================================
        // DATASETS - authoring a derived dataset.
        //
        // THE SCREEN IS A SHELL AND HOLDS NO DECISION. Everything it shows comes from endpoints that
        // already gate themselves: the parent list from the permission-filtered catalogue, the fields
        // from the Studio's own field endpoint, the saved derivations from an endpoint that checks the
        // author key. So there is nothing here to authorize, and inventing a check would be inventing a
        // SECOND answer to questions already answered.
        //
        // A caller without the author key reaches the page and finds it empty, because every endpoint
        // behind it returns nothing to them. That is the platform's ordinary shape - the screen is not
        // the gate, and a screen that looks like one invites someone to trust it.
        // =========================================================================================
        [HttpGet]
        public IActionResult Datasets() => View();

        [HttpGet]
        public async Task<IActionResult> Studio(int? open, string? code, CancellationToken cancellationToken)
        {
            var model = await _studio.BuildAsync(cancellationToken);

            // `open` reopens a saved report. It is resolved SERVER-SIDE and re-validated against today's
            // permissions — a stale bookmark to somebody else's template simply yields an empty canvas rather
            // than an error that would confirm the template exists.
            if (open is > 0)
            {
                ViewData["StudioOpenDraft"] = await _studio.OpenAsync(open.Value, cancellationToken);
            }
            else if (!string.IsNullOrWhiteSpace(code))
            {
                // `code` STARTS FROM AN EXISTING REPORT rather than from nothing.
                //
                // Studio opened on an empty canvas whatever route you arrived by, so "change how this
                // report looks" meant re-picking the dataset and rebuilding its column list by hand — and
                // the thing you were looking at a second ago was no help at all. Seeding the draft from the
                // definition means the designer opens on the CURRENT shape, which is what editing means.
                //
                // Authorization is the same gate the rest of this screen uses and is not weakened here:
                // ResolveDefinitionAsync returns null for a dataset this caller may not use — the identical
                // "no such thing, as far as you are concerned" answer FieldsAsync gives — so an unpermitted
                // code seeds nothing and lands on the ordinary empty canvas.
                var definition = await _studio.ResolveDefinitionAsync(code, cancellationToken);
                if (definition is not null)
                {
                    ViewData["StudioOpenDraft"] = new StudioDraft
                    {
                        TemplateId = 0,                       // a NEW report over an existing definition
                        DatasetCode = code,
                        Name = definition.TitleAr ?? definition.TitleEn ?? code,
                        NameEn = definition.TitleEn ?? definition.TitleAr ?? code,

                        // The columns the report shows TODAY, in its own order. Hidden-by-default fields are
                        // left out for the same reason the viewer leaves them out: the starting point is what
                        // the reader sees, and the designer can add the rest.
                        Columns = definition.Columns
                            .Where(c => c.VisibleByDefault && !c.Internal)
                            .Select(c => c.Key).ToList(),

                        Sorts = definition.DefaultSorts
                            .Select(s => new StudioSortDraft { Field = s.Field, Descending = s.Descending })
                            .ToList(),

                        // Whatever the viewer was running with, so the design previews the same rows the
                        // user just looked at instead of an unfiltered set —
                        //
                        // BUT ONLY KEYS THIS DATASET DECLARES. ReadParameters returns every query-string
                        // key that is not reserved, which on this route includes `culture`. Handing that to
                        // the studio draft made the designer's first Preview fail with HTTP 400,
                        // "Parameter 'culture' is not declared on this data set" — the validator was right
                        // and the seed was wrong. Filtering here rather than widening ReservedQueryKeys:
                        // any stray key would do the same, and a draft has no business carrying a parameter
                        // its dataset does not have.
                        Parameters = ReadParameters()
                            .Where(kv => definition.Parameters.Any(pd =>
                                !pd.SystemSupplied
                                && string.Equals(pd.Key, kv.Key, StringComparison.Ordinal)))
                            .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal),

                        // AND THE CANVAS ITSELF. Without this the designer opened on seven empty bands:
                        // the column list was in state but Studio V2 draws the Visual layout, so "edit the
                        // current look" showed a blank page. StarterFor lays the report's title, date and
                        // table onto it, which is the look being edited.
                        Visual = ReportVisualLayout.StarterFor(
                            definition.TitleAr ?? definition.TitleEn ?? code, definition.Columns),
                    };
                }
            }

            return View(model);
        }

        // =========================================================================================
        // THE REPORT VIEWER
        //
        // `run` is what separates opening the screen from executing the report. Landing on a report with an
        // expensive default range and having it run unasked is how a reporting product becomes the thing that
        // slows the database down every morning.
        // =========================================================================================
        [HttpGet]
        public async Task<IActionResult> Viewer(string id, int? templateId, bool run,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(id)) return NotFound();

            var model = await _presenter.BuildViewerAsync(new ReportViewerQuery
            {
                Code = id,
                TemplateId = templateId,
                Parameters = ReadParameters(),
                Run = run,
                Arabic = Arabic,
            }, cancellationToken);

            // null covers BOTH "no such report" and "not yours". Distinguishing them here would hand an
            // unauthorized caller a catalog enumeration oracle.
            return model is null ? NotFound() : View(model);
        }

        // =========================================================================================
        // EXPORT
        //
        // Goes through the SAME request builder the preview uses (ReportsCenterPresenter.BuildRequest), so the
        // downloaded file is the shaped result the user just looked at — not a second pipeline that happens to
        // agree today. Phase 6 asserts that property against the builder rather than against two call sites.
        // =========================================================================================
        [HttpGet]
        public async Task<IActionResult> Export(string id, ReportOutputFormat format, int? templateId,
            bool archive, bool inline, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(id)) return NotFound();

            ReportResult result;
            try
            {
                // The gate, before the run. GenerateAsync authorizes too — this is not a second decision, it is
                // the 404-instead-of-403 shaping, taken before the engine produces a diagnostic that would say
                // more.
                if (await _reports.DescribeAsync(id, cancellationToken) is null) return NotFound();

                // A TABULAR PREVIEW RUNS CSV, whatever format the button named.
                //
                // The grid is built by PARSING the exporter's own output, so the preview cannot drift from
                // the file — see ReportTabularPreview. That means an XLSX preview needs CSV bytes, and
                // running BOTH would fetch the whole report twice for one click. So the preview runs the
                // cheap one and the Download button inside the modal, which is a separate request without
                // `inline`, runs the real format. One run per action, and the modal still shows the data
                // the workbook will contain.
                var runFormat = inline && IsTabular(format) ? ReportOutputFormat.Csv : format;

                result = await _reports.GenerateAsync(
                    ReportsCenterPresenter.BuildRequest(id, ReadParameters(), templateId, runFormat,
                        preview: false, archive: archive),
                    cancellationToken);
            }
            catch (Exception ex) when (ReportsCenterPresenter.IsSchemaMissing(ex))
            {
                // The reporting tables are not deployed here. Hand the caller to the Viewer, which states
                // that in words — a download endpoint has no way to explain itself, and a 500 on a file
                // request tells the person nothing they can act on.
                return RedirectToAction(nameof(Viewer), new { id, templateId });
            }

            if (result.IsDenied) return NotFound();

            if (!result.IsSuccess || result.Artifact is null)
            {
                // A failed export returns the user to the Viewer with the run performed, so they see the engine's
                // own field-level messages against their inputs instead of a bare error page.
                TempData["ReportExportFailed"] = string.Join(" · ",
                    result.Errors.Select(d => d.Message).DefaultIfEmpty("export_failed"));
                return RedirectToAction(nameof(Viewer), new { id, templateId, run = true });
            }

            // TRUNCATION RIDES ON A HEADER as well as on the screen. A downloaded file has no envelope to carry
            // "this is partial", and a silently partial export is a wrong export.
            if (result.Run?.Truncated == true) Response.Headers["X-Report-Truncated"] = "true";
            Response.Headers["X-Report-Row-Count"] = (result.Run?.RowCount ?? 0).ToString();

            // ---- INLINE vs ATTACHMENT ------------------------------------------------------------------
            //
            // File(content, type, fileName) sets `Content-Disposition: attachment`, so EVERY format
            // downloaded — including HTML and PDF, which a browser renders perfectly well. Asking to look
            // at a report and getting a file in the downloads tray is the wrong answer: the reader wanted
            // to read it, and now has to find it, open it, and remember to delete it.
            //
            // `inline` serves the same bytes with a disposition the browser displays. It is honoured only
            // for the formats a browser can actually render — a CSV served inline is a wall of text and an
            // XLSX is binary noise, so those stay attachments whatever the caller asks for.
            // XLSX IS VIEWABLE NOW, and the comment that used to justify excluding it — "it is a zip and
            // there is nothing a browser can do with it but save it" — was answering the wrong question.
            // The reader does not want to look at the ZIP; they want to see the data they are about to
            // download. Excluding it meant the Excel button opened a modal with a download and no preview,
            // which honours the rule that no click downloads only in form.
            bool viewable = format is ReportOutputFormat.Html
                                   or ReportOutputFormat.PrintHtml
                                   or ReportOutputFormat.Pdf
                                   or ReportOutputFormat.Csv
                                   or ReportOutputFormat.Xlsx;
            if (!inline || !viewable)
                return File(result.Artifact.Content, result.Artifact.ContentType, result.Artifact.FileName);

            // A DATA EXPORT IS PREVIEWED AS A GRID, not as text.
            //
            // Serving the CSV as `text/plain` did show the exact bytes, and that was not enough: a CSV line
            // is Arabic labels joined by ';', the semicolon is bidi-NEUTRAL, so the algorithm absorbs the
            // separators and lays adjacent Arabic fields out as one right-to-left run — field 3 printed to
            // the left of field 1 and the numbers clumped at the end. Correct bytes, wrong columns, and no
            // way for the reader to tell. A grid gives every value its own bidi context, and it is
            // drawable whether the download is text or a workbook.
            if (IsTabular(format))
            {
                var grid = ReportTabularPreview.Html(
                    result.Artifact.Content,
                    System.Globalization.CultureInfo.CurrentUICulture,
                    arabic: string.Equals(
                        System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName,
                        "ar", StringComparison.OrdinalIgnoreCase),
                    title: result.Artifact.FileName,
                    downloadFormat: format);

                SecurePreviewHeaders(result.Artifact.FileName);
                return Content(grid, "text/html; charset=utf-8");
            }

            SecurePreviewHeaders(result.Artifact.FileName);
            return File(result.Artifact.Content, result.Artifact.ContentType);
        }

        // A DATA export rather than a document: the reader wants the values, so the preview is a grid and
        // both of these formats take the same one.
        private static bool IsTabular(ReportOutputFormat format) =>
            format is ReportOutputFormat.Csv or ReportOutputFormat.Xlsx;

        // ONE PLACE FOR THE PREVIEW'S HEADERS, because there are now two responses that serve markup into
        // the same iframe and a policy applied to one of them is not a policy.
        //
        // Anything served inline runs in THIS origin, and although the renderers encode every cell there is
        // no reason for a report document to run script, load a frame or post anywhere. Locked down per
        // response rather than trusted: the renderers' own rule is that a report carries no script, so a
        // policy forbidding script forbids nothing they need.
        private void SecurePreviewHeaders(string fileName)
        {
            // The filename still rides along, so the browser's own "Save page as" keeps it.
            Response.Headers["Content-Disposition"] =
                new System.Net.Mime.ContentDisposition { Inline = true, FileName = fileName }.ToString();

            Response.Headers["X-Content-Type-Options"] = "nosniff";
            Response.Headers["Content-Security-Policy"] =
                "default-src 'none'; img-src data: blob:; style-src 'unsafe-inline'; font-src data:; " +
                "object-src 'none'; frame-ancestors 'self'; base-uri 'none'; form-action 'none'";
        }

        // =========================================================================================
        // ARCHIVE DOWNLOAD
        //
        // RetrieveAsync re-checks the REPORT's permission, so an archived artifact stops being readable the
        // moment the permission behind it is revoked — an archive is not a permanent grant. Absent, unpermitted
        // and bytes-swept all answer 404: the caller does the same thing in all three cases, and telling them
        // apart would disclose which.
        // =========================================================================================
        [HttpGet]
        public async Task<IActionResult> Download(long id, CancellationToken cancellationToken)
        {
            var context = await _contexts.TryGetCurrentAsync(cancellationToken);
            if (context is not { CompanyId: > 0 }) return NotFound();

            var artifact = await _archive.RetrieveAsync(id, context, cancellationToken);
            return artifact is null
                ? NotFound()
                : File(artifact.Content, artifact.ContentType, artifact.FileName);
        }

        // =========================================================================================
        // RE-RUN A PAST RUN
        //
        // Rebuilds the Viewer URL from the parameters a previous run recorded, so "the same report as last
        // month" is one click. It REDIRECTS rather than rendering: the resulting URL is the shareable one, and
        // the user can see and edit what they are about to re-run before it executes.
        // =========================================================================================
        [HttpGet]
        public async Task<IActionResult> Rerun(long id, CancellationToken cancellationToken)
        {
            var context = await _contexts.TryGetCurrentAsync(cancellationToken);
            if (context is not { CompanyId: > 0 }) return NotFound();

            var row = await _history.GetAsync(id, context, cancellationToken);
            if (row is null) return NotFound();

            var parameters = await _history.GetParametersAsync(id, context, cancellationToken);

            var route = new RouteValueDictionary
            {
                ["id"] = row.ReportCode,
                ["templateId"] = row.TemplateId,
                ["run"] = true,
            };

            // System-supplied parameters are NOT replayed. CompanyId in particular: replaying a recorded company
            // would be a caller-supplied tenant arriving through the back door of a history link.
            if (parameters != null)
                foreach (var entry in parameters)
                    if (!ReportSystemParameters.IsSystemKey(entry.Key) && !ReservedQueryKeys.Contains(entry.Key))
                        route[entry.Key] = entry.Value;

            return RedirectToAction(nameof(Viewer), route);
        }

        // =========================================================================================
        // Shaping
        // =========================================================================================

        // Report parameters ride the query string as ordinary entries, minus the keys this controller owns.
        // Same contract as the API surface, so a link works against either.
        private static readonly HashSet<string> ReservedQueryKeys = new(StringComparer.OrdinalIgnoreCase)
        {
            "id", "code", "format", "templateId", "archive", "run",
            "search", "module", "category", "tag", "favorites",
        };

        private Dictionary<string, string?> ReadParameters()
        {
            var parameters = new Dictionary<string, string?>(StringComparer.Ordinal);
            foreach (var entry in Request.Query)
            {
                if (ReservedQueryKeys.Contains(entry.Key)) continue;

                // A SYSTEM-SUPPLIED KEY ARRIVING FROM THE WIRE IS DROPPED, not passed through for the engine to
                // ignore. The engine does ignore it — but relying on that leaves ?CompanyId=2 sitting in a URL
                // looking like it works, and the next person to touch the binder inherits a live hole.
                if (ReportSystemParameters.IsSystemKey(entry.Key)) continue;

                parameters[entry.Key] = entry.Value.ToString();
            }
            return parameters;
        }
    }
}
