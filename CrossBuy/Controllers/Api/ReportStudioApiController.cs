using CrossBuy.BL.Reporting;
using CrossBuy.Models.Context.Reporting;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CrossBuy.Controllers.Api
{
    // =================================================================================================
    // REPORT STUDIO — THE BUILDER'S ENDPOINTS.
    //
    // A SEPARATE CONTROLLER from the Studio screen, for the reason ReportsCenterWriteEndpoints already
    // records: the screen must not carry [AutoValidateAntiforgeryToken] (it would break bookmarkable
    // report links), and everything that mutates or accepts a caller-authored draft must.
    //
    // ANTI-FORGERY IS NEEDED EVEN ON PREVIEW, and that is not over-caution. Preview accepts a draft the
    // caller composed and returns rendered data from it; these endpoints authenticate by SESSION COOKIE,
    // which a browser attaches to a cross-site form post automatically. A cross-origin page must not be
    // able to run a report as the signed-in user and read the answer.
    //
    // ────────────────────────────────────────────────────────────────────────────────────────────
    // THIS CONTROLLER DECIDES NOTHING. Every action hands the draft to IReportStudioService, which is the
    // one place a Studio draft is validated:
    //
    //   · no companyId parameter exists in this file — the tenant is the resolved BusinessContext;
    //   · no field list, filter or sort is trusted — each is checked against the fields THIS caller may
    //     see on THAT dataset before it reaches the engine;
    //   · no SQL and no expression is accepted in any shape.
    //
    // A dataset the caller may not use answers the same way a missing one does, so this endpoint cannot be
    // used to enumerate the catalogue.
    // =================================================================================================
    [ApiController]
    [Route("api/reports/studio")]
    [Authorize]
    [AutoValidateAntiforgeryToken]
    [Produces("application/json")]
    public sealed class ReportStudioApiController : ControllerBase
    {
        private readonly IReportStudioService _studio;
        private readonly IReportAuthorizationService _authorization;
        private readonly CrossBuy.BL.Platform.IBusinessContextAccessor _contexts;
        private readonly IReportAssetService _assets;

        public ReportStudioApiController(IReportStudioService studio,
            IReportAuthorizationService authorization,
            CrossBuy.BL.Platform.IBusinessContextAccessor contexts,
            IReportAssetService assets)
        {
            _studio = studio;
            _authorization = authorization;
            _contexts = contexts;
            _assets = assets;
        }

        // THE AUTHORITY CALL, in one place, run by EVERY endpoint below before it does anything.
        //
        // CBA001 requires an authorization decision the analyzer can see inside a mutating endpoint's call
        // graph, and refuses to credit one buried in a service — correctly, because a rule found by descent
        // is a rule nobody declared. So the platform's own report gate runs HERE, at the boundary.
        //
        // This is a gate, not a replacement: IReportStudioService still validates the draft field by field,
        // and IReportService authorizes again when the run starts. Three checks, each with a different job —
        // may you use this report at all, may you use these fields, may you run it right now.
        private async Task<bool> AuthorizeAsync(string? datasetCode, CancellationToken ct)
        {
            var context = await _contexts.TryGetCurrentAsync(ct);
            if (context is not { CompanyId: > 0 }) return false;

            // Null covers BOTH "no such dataset" and "not yours" — the same answer, so this endpoint cannot
            // be used to enumerate the catalogue.
            var definition = await _studio.ResolveDefinitionAsync(datasetCode ?? "", ct);
            if (definition is null) return false;

            var decision = await _authorization.AuthorizeReportAsync(
                definition, ReportAccessLevel.Run, context, ct);

            return decision.Allowed;
        }

        // THE AUTHOR GATE — for the asset endpoints, which belong to no single dataset.
        //
        // A logo is not attached to a report; it is a company image that any of the caller's reports may place.
        // So there is no dataset code to authorize, and inventing a fake one would be worse than useless.
        //
        // What IS checkable, and is exactly the right question, is whether this caller may author reports here
        // at all: they must have at least one dataset they can build over, and the platform's own report gate
        // must clear the report behind it. Someone with no Studio dataset cannot upload a logo, and the company
        // isolation on the assets themselves is enforced independently by IReportAssetService.
        //
        // It also puts AuthorizeReportAsync in these endpoints' call graph, which is what CBA001 requires of a
        // mutating action — and requires for a good reason: an authorization decision reached by descending
        // into a service is one no reader of the endpoint can see.
        private async Task<bool> AuthorizeAuthorAsync(CancellationToken ct)
        {
            var context = await _contexts.TryGetCurrentAsync(ct);
            if (context is not { CompanyId: > 0 }) return false;

            var model = await _studio.BuildAsync(ct);
            if (model.HasNoDatasets) return false;

            foreach (var dataset in model.Datasets)
            {
                var definition = await _studio.ResolveDefinitionAsync(dataset.DatasetCode, ct);
                if (definition is null) continue;

                var decision = await _authorization.AuthorizeReportAsync(
                    definition, ReportAccessLevel.Run, context, ct);

                if (decision.Allowed) return true;
            }
            return false;
        }

        // The fields the caller may use on one dataset, with the operators each field legally accepts.
        //
        // A GET, because it is a read and the screen calls it on every dataset change. It carries no draft,
        // so there is nothing for a forged request to submit — and an empty list is the honest answer for a
        // dataset that is not the caller's.
        [HttpGet("fields")]
        [IgnoreAntiforgeryToken]
        public async Task<IActionResult> Fields(string datasetCode, CancellationToken ct)
        {
            var fields = await _studio.FieldsAsync(datasetCode ?? "", ct);
            return Ok(new { fields });
        }

        // Validate without running. The screen calls this as the user builds, so the errors it shows are the
        // SAME strings the run would produce — one validator, one vocabulary.
        [HttpPost("validate")]
        public async Task<IActionResult> Validate([FromBody] StudioDraft draft, CancellationToken ct)
        {
            if (!await AuthorizeAsync(draft?.DatasetCode, ct)) return NotFound();

            var validation = await _studio.ValidateAsync(draft, ct);
            return Ok(new { ok = validation.Ok, errors = validation.Errors });
        }

        // PREVIEW — capped, never archived, and it re-validates the draft first.
        [HttpPost("preview")]
        public async Task<IActionResult> Preview([FromBody] StudioDraft draft, CancellationToken ct)
        {
            if (!await AuthorizeAsync(draft?.DatasetCode, ct)) return NotFound();

            var result = await _studio.RunAsync(draft, ReportOutputFormat.Html, preview: true, ct);

            // A denial answers like an absent report, not like a refusal: the caller already learned what they
            // may build over from the screen, and a distinct status here would tell them more.
            if (result.IsDenied) return NotFound();

            if (!result.IsSuccess || result.Artifact is null)
                return BadRequest(new { errors = result.Errors.Select(d => d.Message).ToList() });

            return Ok(new
            {
                html = System.Text.Encoding.UTF8.GetString(result.Artifact.Content),
                rowCount = result.Run?.RowCount ?? 0,
                truncated = result.Run?.Truncated ?? false,
            });
        }

        [HttpPost("save")]
        public async Task<IActionResult> Save([FromBody] StudioDraft draft, CancellationToken ct)
        {
            if (!await AuthorizeAsync(draft?.DatasetCode, ct)) return NotFound();

            var result = await _studio.SaveAsync(draft, ct);

            // A refusal is a RESULT, not an exception: "that field is not yours" is an ordinary answer to an
            // ordinary mistake, and the caller needs the reason to fix it.
            return result.Success
                ? Ok(new { templateId = result.TemplateId, versionNo = result.VersionNo })
                : StatusCode(StatusCodes.Status403Forbidden,
                    new { errors = result.Diagnostics.Select(d => d.Message).ToList() });
        }

        // EXPORT — the same draft, the same validation, a different format.
        //
        // A POST returning a FILE, which is unusual and deliberate: the draft is too large and too structured
        // for a query string, and making it a GET would mean either a URL that breaks at length or a second
        // export path that could drift from what the preview showed. There is exactly one path.
        [HttpPost("export")]
        public async Task<IActionResult> Export([FromBody] StudioDraft draft, ReportOutputFormat format,
            CancellationToken ct)
        {
            if (!await AuthorizeAsync(draft?.DatasetCode, ct)) return NotFound();

            if (format != ReportOutputFormat.Csv && format != ReportOutputFormat.Xlsx)
                return BadRequest(new { errors = new[] { "Only CSV and XLSX are available in this release." } });

            var result = await _studio.RunAsync(draft, format, preview: false, ct);

            if (result.IsDenied) return NotFound();

            if (!result.IsSuccess || result.Artifact is null)
                return BadRequest(new { errors = result.Errors.Select(d => d.Message).ToList() });

            // Truncation rides on a header as well as on the screen: a downloaded file has no envelope to carry
            // "this is partial", and a silently partial export is a wrong export.
            if (result.Run?.Truncated == true) Response.Headers["X-Report-Truncated"] = "true";
            Response.Headers["X-Report-Row-Count"] = (result.Run?.RowCount ?? 0).ToString();

            return File(result.Artifact.Content, result.Artifact.ContentType, result.Artifact.FileName);
        }

        // =========================================================================================
        // REPORT STUDIO V2 — THE VISUAL DESIGNER'S ENDPOINTS
        // =========================================================================================

        // §9. The parameters the dataset declares, so the designer can offer "last quarter" instead of being
        // stuck on whatever the dataset defaults to. A GET for the same reason /fields is one.
        [HttpGet("parameters")]
        [IgnoreAntiforgeryToken]
        public async Task<IActionResult> Parameters(string datasetCode, CancellationToken ct)
        {
            var parameters = await _studio.ParametersAsync(datasetCode ?? "", ct);
            return Ok(new { parameters });
        }

        // The images this caller may place. Company-scoped inside the service; this endpoint has no company
        // parameter and could not be given one.
        [HttpGet("assets")]
        [IgnoreAntiforgeryToken]
        public async Task<IActionResult> Assets(CancellationToken ct)
        {
            var assets = await _studio.AssetsAsync(ct);
            return Ok(new { assets });
        }

        // THE BYTES, for the designer canvas and the picker.
        //
        // Note what the route takes: an ID, not a path and not a URL. There is no shape of request to this
        // endpoint that names a file, so there is nothing here to traverse and nothing to point at a remote
        // host. A foreign id 404s because the service's query carries the company predicate — the same answer
        // a deleted one gives.
        [HttpGet("assets/{id:int}")]
        [IgnoreAntiforgeryToken]
        public async Task<IActionResult> Asset(int id, CancellationToken ct)
        {
            var context = await _contexts.TryGetCurrentAsync(ct);
            if (context is not { CompanyId: > 0 }) return NotFound();

            var asset = await _assets.ReadAsync(id, context, ct);
            if (asset is null) return NotFound();

            return File(asset.Value.Bytes, asset.Value.ContentType);
        }

        // UPLOAD. The browser sends BYTES; it does not send a path, and there is no property on this action
        // that could carry one. What gets stored, where, and under what name is decided by the service.
        [HttpPost("assets")]
        [RequestSizeLimit(8 * 1024 * 1024)]
        public async Task<IActionResult> UploadAsset(IFormFile file, ReportImageRole role, string? title,
            CancellationToken ct)
        {
            if (!await AuthorizeAuthorAsync(ct)) return NotFound();

            var context = await _contexts.TryGetCurrentAsync(ct);
            if (context is not { CompanyId: > 0 }) return NotFound();

            if (file is null || file.Length == 0)
                return BadRequest(new { errors = new[] { "No file was supplied." } });

            await using var stream = file.OpenReadStream();
            var saved = await _assets.UploadAsync(stream, file.FileName, role, title, context, ct);

            // Null means the bytes were refused — not an image, too large, or a script-bearing SVG. One message
            // for all three: a precise reason here would be a free oracle for what the sniffer accepts.
            return saved is null
                ? BadRequest(new { errors = new[] { "That file was not accepted as an image." } })
                : Ok(new { asset = saved });
        }

        [HttpPost("assets/{id:int}/delete")]
        public async Task<IActionResult> DeleteAsset(int id, CancellationToken ct)
        {
            if (!await AuthorizeAuthorAsync(ct)) return NotFound();

            var context = await _contexts.TryGetCurrentAsync(ct);
            if (context is not { CompanyId: > 0 }) return NotFound();

            var removed = await _assets.DeleteAsync(id, context, ct);
            return removed ? Ok(new { ok = true }) : NotFound();
        }

        // PRINT PREVIEW — the standalone print document for the SAME draft the designer is showing.
        //
        // Returned as HTML rather than as a view because this controller has none, and because the markup is
        // produced by the renderer in BL: a Razor print view would be a second template of exactly the kind §12
        // forbids. The browser opens it and calls print(); the PDF endpoint below converts the identical bytes.
        [HttpPost("print")]
        [Produces("text/html")]
        public async Task<IActionResult> Print([FromBody] StudioDraft draft, CancellationToken ct)
        {
            if (!await AuthorizeAsync(draft?.DatasetCode, ct)) return NotFound();

            var result = await _studio.RunAsync(draft, ReportOutputFormat.PrintHtml, preview: false, ct);

            if (result.IsDenied) return NotFound();
            if (!result.IsSuccess || result.Artifact is null)
                return BadRequest(new { errors = result.Errors.Select(d => d.Message).ToList() });

            return Content(System.Text.Encoding.UTF8.GetString(result.Artifact.Content), "text/html");
        }

        // PDF — first-class, per §13, and now genuinely bound: PlaywrightHtmlToPdfConverter converts the print
        // document this same pipeline produces. If the browser is not installed the run comes back Failed with
        // the converter's own reason, which is actionable, rather than with a stack trace.
        [HttpPost("pdf")]
        public async Task<IActionResult> Pdf([FromBody] StudioDraft draft, CancellationToken ct)
        {
            if (!await AuthorizeAsync(draft?.DatasetCode, ct)) return NotFound();

            var result = await _studio.RunAsync(draft, ReportOutputFormat.Pdf, preview: false, ct);

            if (result.IsDenied) return NotFound();
            if (!result.IsSuccess || result.Artifact is null)
                return BadRequest(new { errors = result.Errors.Select(d => d.Message).ToList() });

            if (result.Run?.Truncated == true) Response.Headers["X-Report-Truncated"] = "true";

            return File(result.Artifact.Content, result.Artifact.ContentType, result.Artifact.FileName);
        }

        // REOPEN — the stored design, re-validated against what this caller may see today.
        [HttpGet("open/{templateId:int}")]
        [IgnoreAntiforgeryToken]
        public async Task<IActionResult> Open(int templateId, CancellationToken ct)
        {
            var draft = await _studio.OpenAsync(templateId, ct);
            return draft is null ? NotFound() : Ok(new { draft });
        }
    }
}
