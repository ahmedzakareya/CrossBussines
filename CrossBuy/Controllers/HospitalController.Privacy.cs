using CrossBuy.BL;
using CrossBuy.BL.Hospital;
using CrossBuy.Models;
using CrossBuy.Models.Platform;
using Microsoft.AspNetCore.Mvc;

namespace CrossBuy.Controllers
{
	// HOSPITAL H255 — the privacy officer's alerts read from the record-access log, and their review.
	public partial class HospitalController
	{
		private HospitalPrivacyService? _privSvc;
		private HospitalPrivacyService PrivSvc => _privSvc ??= ActivatorUtilities.CreateInstance<HospitalPrivacyService>(HttpContext.RequestServices);

		[HttpGet("Privacy"), HospitalPerm(HospitalActions.Manage)]
		public async Task<IActionResult> Privacy(string? q, string? view, string? kind, int page = 1, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return RedirectToAction("Index", "Home");
			Chrome();
			const int size = 25;
			var d = await PrivSvc.PageAsync(ctx, q, view, kind, page, size, DateTime.UtcNow, ct);
			if (d == null) return Forbid();
			ViewBag.Q = q; ViewBag.View = view ?? ""; ViewBag.Kind = kind ?? ""; ViewBag.Page = Math.Max(1, page); ViewBag.Pages = Math.Max(1, (d.Total + size - 1) / size);
			return View(d);
		}

		[HttpPost("Privacy/{id:int}"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Manage)]
		public async Task<IActionResult> PrivacyReview(int id, [FromBody] HospitalPrivacyReviewInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null || input == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await PrivSvc.ReviewAsync(ctx, id, input, DateTime.UtcNow, ct));
		}
	}
}
