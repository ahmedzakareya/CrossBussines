using CrossBuy.BL;
using CrossBuy.BL.Hospital;
using CrossBuy.Models;
using CrossBuy.Models.Platform;
using Microsoft.AspNetCore.Mvc;

namespace CrossBuy.Controllers
{
	// HOSPITAL H259 — report templates, applying one to a study, and the radiologists' peer-review worklist.
	public partial class HospitalController
	{
		private HospitalRadReportingService? _radRepSvc;
		private HospitalRadReportingService RadRepSvc => _radRepSvc ??= ActivatorUtilities.CreateInstance<HospitalRadReportingService>(HttpContext.RequestServices);
		public sealed class HospitalRadApplyInput { public int TemplateId { get; set; } }

		[HttpGet("Radiology/Templates"), HospitalPerm(HospitalActions.Manage)]
		public async Task<IActionResult> RadTemplates(string? q, int page = 1, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return RedirectToAction("Index", "Home");
			Chrome();
			const int size = 20;
			var d = await RadRepSvc.TemplatesAsync(ctx, q, page, size, ct);
			if (d == null) return Forbid();
			ViewBag.Q = q; ViewBag.Page = Math.Max(1, page); ViewBag.Pages = Math.Max(1, (d.Total + size - 1) / size);
			return View(d);
		}

		[HttpPost("Radiology/Templates"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Manage)]
		public async Task<IActionResult> RadTemplateSave([FromBody] HospitalRadTemplateInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null || input == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await RadRepSvc.SaveTemplateAsync(ctx, input, DateTime.UtcNow, ct));
		}

		[HttpPost("Radiology/Templates/SeedStandard"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Manage)]
		public async Task<IActionResult> RadTemplateSeed(CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await RadRepSvc.SeedStandardAsync(ctx, DateTime.UtcNow, ct));
		}

		[HttpPost("Radiology/{id:int}/Template"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.RadReport)]
		public async Task<IActionResult> RadTemplateApply(int id, [FromBody] HospitalRadApplyInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null || input == null) return NotFound(new { ok = false, code = "not_found" });
			var (r, sk) = await RadRepSvc.ApplyAsync(ctx, id, input.TemplateId, ctx.EmployeeId, DateTime.UtcNow, ct);
			if (!r.Ok) return Result(r);
			return Json(new { ok = true, id = sk!.TemplateId, arabic = sk.Arabic, english = sk.English });
		}

		[HttpGet("Radiology/PeerReview"), HospitalPerm(HospitalActions.RadReport)]
		public async Task<IActionResult> RadPeerReview(string? q, string? view, int page = 1, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return RedirectToAction("Index", "Home");
			Chrome();
			const int size = 20;
			var d = await RadRepSvc.PeerAsync(ctx, q, view, page, size, DateTime.UtcNow, ct);
			if (d == null) return Forbid();
			ViewBag.Q = q; ViewBag.View = view ?? ""; ViewBag.Page = Math.Max(1, page); ViewBag.Pages = Math.Max(1, (d.Total + size - 1) / size);
			return View(d);
		}

		[HttpPost("Radiology/{id:int}/PeerReview"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.RadReport)]
		public async Task<IActionResult> RadPeerReviewSave(int id, [FromBody] HospitalRadPeerInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null || input == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await RadRepSvc.ReviewAsync(ctx, id, input, DateTime.UtcNow, ct));
		}
	}
}
