using CrossBuy.BL;
using CrossBuy.BL.Hospital;
using CrossBuy.Models;
using CrossBuy.Models.Platform;
using Microsoft.AspNetCore.Mvc;

namespace CrossBuy.Controllers
{
	// HOSPITAL H240 — the denial work-queue: every rejected claim line as a work item with its root cause, owner and
	// appeal deadline, and the denials by cause and by payer. The biller's (`bill`). Resubmission stays the claim
	// batch's own action (Insurance/Line/{id}/Resubmit).
	public partial class HospitalController
	{
		private HospitalDenialService? _denialSvc;
		private HospitalDenialService DenialSvc => _denialSvc ??= ActivatorUtilities.CreateInstance<HospitalDenialService>(HttpContext.RequestServices);

		[HttpGet("Insurance/Denials"), HospitalPerm(HospitalActions.Bill)]
		public async Task<IActionResult> Denials(string? q, string? view, string? cause, int page = 1, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return RedirectToAction("Index", "Home");
			Chrome();
			const int size = 20;
			var d = await DenialSvc.QueueAsync(ctx, q, view, cause, page, size, DateTime.UtcNow, ct);
			if (d == null) return Forbid();
			ViewBag.Q = q; ViewBag.View = view ?? ""; ViewBag.Cause = cause ?? ""; ViewBag.Page = Math.Max(1, page); ViewBag.Pages = Math.Max(1, (d.Total + size - 1) / size);
			return View(d);
		}

		[HttpPost("Insurance/Denials/{id:int}"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Bill)]
		public async Task<IActionResult> DenialUpdate(int id, [FromBody] HospitalDenialInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null || input == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await DenialSvc.UpdateAsync(ctx, id, input, ctx.EmployeeId, DateTime.UtcNow, ct));
		}
	}
}
