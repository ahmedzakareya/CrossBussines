using CrossBuy.BL.Reporting;
using CrossBuy.Models.Context.Reporting;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CrossBuy.Controllers.Api
{
	// ==========================================================================================
	// DEVELOPMENT ONLY — author the hospital's print layouts (the bill as an A4 invoice and a clinic receipt, the
	// prescription, the dental claim form) through Report Studio's OWN service, from a dev session.
	//
	// The Studio API sits behind a cookie sign-in; the dev session (/api/dev/culture-check) is a session-only
	// employee, so it cannot reach it. This takes the same StudioDraft the Studio screen posts and hands it to
	// IReportStudioService.SaveAsync / IReportAssetService.UploadAsync — the same validation, the same versioned
	// template rows, no SQL. The report gate runs here first, exactly as ReportStudioApiController's does.
	// [DevOnly] answers 404 in every other environment.
	// ==========================================================================================
	[ApiController]
	[Route("api/dev/hospital-print")]
	[AllowAnonymous]
	[CrossBuy.Models.DevOnly]
	[IgnoreAntiforgeryToken]
	public sealed class DevHospitalPrintController : ControllerBase
	{
		private readonly IReportStudioService _studio;
		private readonly IReportAuthorizationService _authorization;
		private readonly CrossBuy.BL.Platform.IBusinessContextAccessor _contexts;
		private readonly IReportAssetService _assets;
		private readonly IReportTemplateService _templates;

		public DevHospitalPrintController(IReportStudioService studio, IReportAuthorizationService authorization,
			CrossBuy.BL.Platform.IBusinessContextAccessor contexts, IReportAssetService assets, IReportTemplateService templates)
		{ _studio = studio; _authorization = authorization; _contexts = contexts; _assets = assets; _templates = templates; }

		private async Task<bool> AuthorizeAsync(string datasetCode, CancellationToken ct)
		{
			var context = await _contexts.TryGetCurrentAsync(ct);
			if (context is not { CompanyId: > 0 }) return false;
			var definition = await _studio.ResolveDefinitionAsync(datasetCode, ct);
			if (definition is null) return false;
			return (await _authorization.AuthorizeReportAsync(definition, ReportAccessLevel.Run, context, ct)).Allowed;
		}

		public sealed class SvgAsset { public string FileName { get; set; } = ""; public string Title { get; set; } = ""; public string Svg { get; set; } = ""; public string? Base64 { get; set; } }

		// POST /api/dev/hospital-print/asset?key=seed123 — a static drawing (the tooth chart) into the company's report assets.
		[HttpPost("asset")]
		public async Task<IActionResult> Asset(string key, [FromBody] SvgAsset input, CancellationToken ct)
		{
			if (key != "seed123") return Unauthorized();
			if (!await AuthorizeAsync(HospitalDocumentDatasetCodes.DentalClaim, ct)) return NotFound();
			var context = (await _contexts.TryGetCurrentAsync(ct))!;
			await using var stream = new MemoryStream(input.Base64 is { Length: > 0 } b64 ? Convert.FromBase64String(b64) : System.Text.Encoding.UTF8.GetBytes(input.Svg));
			var saved = await _assets.UploadAsync(stream, input.FileName, ReportImageRole.Custom, input.Title, context, ct);
			return saved is null ? BadRequest(new { error = "not accepted as an image" }) : Ok(new { id = saved.Id });
		}

		// POST /api/dev/hospital-print/delete?key=seed123&dataset=Hospital.X&id=N — retire a hospital layout. The template
		// service decides (Manage access on that template); only a layout of a hospital report is ever named here.
		[HttpPost("delete")]
		public async Task<IActionResult> Delete(string key, string dataset, int id, CancellationToken ct)
		{
			if (key != "seed123") return Unauthorized();
			if (!dataset.StartsWith("Hospital.", StringComparison.Ordinal) || !await AuthorizeAsync(dataset, ct)) return NotFound();
			var context = (await _contexts.TryGetCurrentAsync(ct))!;
			var layouts = await _templates.ListAsync(dataset, context, ct);
			if (!layouts.Any(t => t.Id == id)) return NotFound();
			return Ok(new { ok = await _templates.DeleteAsync(id, context, ct) });
		}

		// POST /api/dev/hospital-print/validate|save?key=seed123 — one Studio draft.
		[HttpPost("{mode:regex(^(validate|save)$)}")]
		public async Task<IActionResult> Draft(string key, string mode, [FromBody] StudioDraft draft, CancellationToken ct)
		{
			if (key != "seed123") return Unauthorized();
			if (draft == null || !draft.DatasetCode.StartsWith("Hospital.", StringComparison.Ordinal)) return BadRequest();
			if (!await AuthorizeAsync(draft.DatasetCode, ct)) return NotFound();
			if (mode == "validate")
			{
				var v = await _studio.ValidateAsync(draft, ct);
				return Ok(new { ok = v.Ok, errors = v.Errors });
			}
			var r = await _studio.SaveAsync(draft, ct);
			return Ok(new { ok = r.Success, templateId = r.TemplateId, versionNo = r.VersionNo, errors = r.Diagnostics.Select(d => d.Message) });
		}
	}
}
