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

        public ReportStudioApiController(IReportStudioService studio,
            IReportAuthorizationService authorization,
            CrossBuy.BL.Platform.IBusinessContextAccessor contexts)
        {
            _studio = studio;
            _authorization = authorization;
            _contexts = contexts;
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
    }
}
