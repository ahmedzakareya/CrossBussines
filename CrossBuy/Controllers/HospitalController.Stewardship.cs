using CrossBuy.BL;
using CrossBuy.BL.Hospital;
using CrossBuy.Models;
using CrossBuy.Models.Platform;
using Microsoft.AspNetCore.Mvc;

namespace CrossBuy.Controllers
{
	// HOSPITAL H235 — antimicrobial stewardship: the team's worklist (approval of restricted agents, the 48–72 h time-out)
	// and the last 30 days' days of therapy. The team is the infection doctor (`consult`) or the pharmacist (`pharmacy`).
	public partial class HospitalController
	{
		private HospitalStewardshipService? _stewSvc;
		private HospitalStewardshipService StewSvc => _stewSvc ??= ActivatorUtilities.CreateInstance<HospitalStewardshipService>(HttpContext.RequestServices);
		public sealed class HospitalAbxReviewInput { public string? Kind { get; set; } public string? Decision { get; set; } public string? Note { get; set; } }

		[HttpGet("Stewardship"), HospitalPerm(HospitalActions.ClinicalRead)]
		public async Task<IActionResult> Stewardship(string? q, string? state, int page = 1, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return RedirectToAction("Index", "Home");
			Chrome();
			const int size = 20;
			var p = await StewSvc.PageAsync(ctx, q, state, page, size, DateTime.UtcNow, ct);
			if (p == null) return Forbid();
			ViewBag.Q = q; ViewBag.State = state; ViewBag.Page = Math.Max(1, page); ViewBag.Pages = Math.Max(1, (p.Total + size - 1) / size);
			return View(p);
		}

		[HttpPost("Stewardship/{orderId:int}/Review"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.ClinicalRead)]
		public async Task<IActionResult> StewardshipReview(int orderId, [FromBody] HospitalAbxReviewInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null || input == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await StewSvc.ReviewAsync(ctx, orderId, input.Kind, input.Decision, input.Note, ctx.EmployeeId, DateTime.UtcNow, ct));
		}
	}
}
