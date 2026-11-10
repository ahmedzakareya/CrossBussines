using CrossBuy.BL;
using CrossBuy.BL.Hospital;
using CrossBuy.Models;
using CrossBuy.Models.Platform;
using Microsoft.AspNetCore.Mvc;

namespace CrossBuy.Controllers
{
	// ==========================================================================================
	// HOSPITAL — H27, the staff side of what the patient portal shows: a doctor releases a verified lab result or a
	// signed imaging report to the portal, and the clinic answers refill requests. Every rule is
	// HospitalPortalRecordsService's; built from the request's services like H11–H26, so Program.cs is untouched.
	// ==========================================================================================
	public partial class HospitalController
	{
		private HospitalPortalRecordsService? _portalRecordsSvc;
		private HospitalPortalRecordsService PortalRecordsSvc => _portalRecordsSvc ??= ActivatorUtilities.CreateInstance<HospitalPortalRecordsService>(HttpContext.RequestServices, _billing);

		[HttpPost("PortalRelease"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Consult)]
		public async Task<IActionResult> PortalRelease([FromBody] HospitalPortalReleaseInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null || input == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await PortalRecordsSvc.ReleaseAsync(ctx, input, ctx.EmployeeId, DateTime.UtcNow, ct));
		}

		[HttpGet("PortalRefills")]
		[HospitalPerm(HospitalActions.Read)]
		public async Task<IActionResult> PortalRefills(CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return RedirectToAction("Index", "Home");
			Chrome();
			ViewBag.CanConsult = await _access.CanAsync(ctx, HospitalActions.Consult, null, ct);
			return View(await PortalRecordsSvc.RefillsAsync(ctx, ct));
		}

		[HttpPost("PortalRefills/{id:int}/Decide"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Consult)]
		public async Task<IActionResult> PortalRefillDecide(int id, [FromBody] HospitalPortalDecisionInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null || input == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await PortalRecordsSvc.DecideRefillAsync(ctx, id, input, ctx.EmployeeId, DateTime.UtcNow, ct));
		}
	}
}
