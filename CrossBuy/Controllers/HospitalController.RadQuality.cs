using CrossBuy.BL;
using CrossBuy.BL.Hospital;
using CrossBuy.Models;
using CrossBuy.Models.Platform;
using Microsoft.AspNetCore.Mvc;

namespace CrossBuy.Controllers
{
	// HOSPITAL H243 — a study's structured category and follow-up, its radiation dose, and the follow-up worklist.
	public partial class HospitalController
	{
		private HospitalRadQualityService? _radQSvc;
		private HospitalRadQualityService RadQSvc => _radQSvc ??= ActivatorUtilities.CreateInstance<HospitalRadQualityService>(HttpContext.RequestServices);

		[HttpGet("Radiology/FollowUps"), HospitalPerm(HospitalActions.Read)]
		public async Task<IActionResult> RadFollowUps(string? q, string? view, int page = 1, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return RedirectToAction("Index", "Home");
			Chrome();
			const int size = 20;
			var d = await RadQSvc.FollowUpsAsync(ctx, q, view, page, size, DateTime.UtcNow, ct);
			ViewBag.Q = q; ViewBag.View = view ?? ""; ViewBag.Page = Math.Max(1, page); ViewBag.Pages = Math.Max(1, (d.Total + size - 1) / size);
			ViewBag.CanClose = await _access.CanAsync(ctx, HospitalActions.RadReport, null, ct) || await _access.CanAsync(ctx, HospitalActions.Rad, null, ct);
			return View(d);
		}

		[HttpPost("Radiology/{id:int}/Category"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.RadReport)]
		public async Task<IActionResult> RadCategory(int id, [FromBody] HospitalRadCategoryInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null || input == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await RadQSvc.CategorizeAsync(ctx, id, input, ctx.EmployeeId, DateTime.UtcNow, ct));
		}

		[HttpPost("Radiology/{id:int}/Dose"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Rad)]
		public async Task<IActionResult> RadDose(int id, [FromBody] HospitalRadDoseInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null || input == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await RadQSvc.RecordDoseAsync(ctx, id, input, ctx.EmployeeId, DateTime.UtcNow, ct));
		}

		[HttpPost("Radiology/FollowUps/{id:int}/Close"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Read)]
		public async Task<IActionResult> RadFollowUpClose(int id, [FromBody] HospitalRadFollowUpCloseInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null || input == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await RadQSvc.CloseFollowUpAsync(ctx, id, input, DateTime.UtcNow, ct));
		}
	}
}
