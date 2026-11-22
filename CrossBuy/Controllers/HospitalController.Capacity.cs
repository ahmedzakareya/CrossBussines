using CrossBuy.BL;
using CrossBuy.BL.Hospital;
using CrossBuy.Models;
using CrossBuy.Models.Platform;
using Microsoft.AspNetCore.Mvc;

namespace CrossBuy.Controllers
{
	// HOSPITAL H239 — the capacity centre: beds now, the four-day forecast, wards, and every inpatient's expected
	// discharge with its barriers. Reading is `read`; a discharge plan is the ward's or the bed manager's (`admit`).
	public partial class HospitalController
	{
		private HospitalCapacityService? _capSvc;
		private HospitalCapacityService CapSvc => _capSvc ??= ActivatorUtilities.CreateInstance<HospitalCapacityService>(HttpContext.RequestServices);

		[HttpGet("Capacity"), HospitalPerm(HospitalActions.Read)]
		public async Task<IActionResult> Capacity(string? q, int page = 1, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return RedirectToAction("Index", "Home");
			Chrome();
			var c = await CapSvc.CentreAsync(ctx, DateTime.UtcNow, q, page, ct);
			ViewBag.Q = q; ViewBag.Page = Math.Max(1, page); ViewBag.Pages = Math.Max(1, (c.PatientTotal + HospitalCapacityService.PageSize - 1) / HospitalCapacityService.PageSize);
			ViewBag.CanAdmit = await _access.CanAsync(ctx, HospitalActions.Admit, null, ct);
			return View(c);
		}

		[HttpPost("Capacity/{admissionId:int}/Plan"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Admit)]
		public async Task<IActionResult> CapacityPlan(int admissionId, [FromBody] HospitalDischargePlanInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null || input == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await CapSvc.SavePlanAsync(ctx, admissionId, input, ctx.EmployeeId, DateTime.UtcNow, ct));
		}

		[HttpPost("Capacity/{admissionId:int}/Plan/Clear"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Admit)]
		public async Task<IActionResult> CapacityPlanClear(int admissionId, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await CapSvc.ClearPlanAsync(ctx, admissionId, ct));
		}
	}
}
