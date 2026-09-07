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
        [HttpGet]
        public async Task<IActionResult> Studio(int? open, CancellationToken cancellationToken)
        {
            var model = await _studio.BuildAsync(cancellationToken);

            // `open` reopens a saved report. It is resolved SERVER-SIDE and re-validated against today's
            // permissions — a stale bookmark to somebody else's template simply yields an empty canvas rather
            // than an error that would confirm the template exists.
            if (open is > 0)
            {
                ViewData["StudioOpenDraft"] = await _studio.OpenAsync(open.Value, cancellationToken);
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
            bool archive, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(id)) return NotFound();

            ReportResult result;
            try
            {
                // The gate, before the run. GenerateAsync authorizes too — this is not a second decision, it is
                // the 404-instead-of-403 shaping, taken before the engine produces a diagnostic that would say
                // more.
                if (await _reports.DescribeAsync(id, cancellationToken) is null) return NotFound();

                result = await _reports.GenerateAsync(
                    ReportsCenterPresenter.BuildRequest(id, ReadParameters(), templateId, format,
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

            return File(result.Artifact.Content, result.Artifact.ContentType, result.Artifact.FileName);
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
