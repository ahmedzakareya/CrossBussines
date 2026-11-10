using CrossBuy.BL;
using CrossBuy.BL.Hospital;
using CrossBuy.Models;
using Microsoft.AspNetCore.Mvc;

namespace CrossBuy.Controllers
{
	// HOSPITAL H21 — medication routing: from the prescription to the bedside, six stamped steps.
	public partial class HospitalController
	{
		public sealed class HospitalMedCancelInput { public string? Reason { get; set; } }

		private HospitalMedRoutingService? _medRoutingSvc;
		private HospitalMedRoutingService MedRoutingSvc => _medRoutingSvc ??= ActivatorUtilities.CreateInstance<HospitalMedRoutingService>(HttpContext.RequestServices);

		[HttpGet("MedRouting")]
		[HospitalPerm(HospitalActions.Read)]
		public async Task<IActionResult> MedRouting(CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return RedirectToAction("Index", "Home");
			Chrome();
			ViewBag.Staff = await NursingSvc.StaffAsync(ctx, ct);
			ViewBag.CanPharmacy = await _access.CanAsync(ctx, HospitalActions.Pharmacy, null, ct);
			ViewBag.CanAdmit = await _access.CanAsync(ctx, HospitalActions.Admit, null, ct);
			ViewBag.Me = ctx.EmployeeId;
			return View(await MedRoutingSvc.BoardAsync(ctx, DateTime.UtcNow, ct));
		}

		[HttpPost("MedRouting"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Pharmacy)]
		public async Task<IActionResult> MedRoutingCreate([FromBody] HospitalMedDeliveryInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null || input == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await MedRoutingSvc.CreateAsync(ctx, input, ctx.EmployeeId, DateTime.UtcNow, ct));
		}

		[HttpPost("MedRouting/{id:int}/Step"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Read)]
		public async Task<IActionResult> MedRoutingStep(int id, [FromBody] HospitalMedStepInput input, CancellationToken ct = default)
		{
			// the service decides: the pharmacy's steps need `pharmacy`, the ward's need `admit`
			var ctx = await ContextAsync(ct);
			if (ctx == null || input == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await MedRoutingSvc.StepAsync(ctx, id, input, ctx.EmployeeId, ctx.UserId, DateTime.UtcNow, ct));
		}

		[HttpPost("MedRouting/{id:int}/Cancel"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Pharmacy)]
		public async Task<IActionResult> MedRoutingCancel(int id, [FromBody] HospitalMedCancelInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await MedRoutingSvc.CancelAsync(ctx, id, input?.Reason, ctx.EmployeeId, DateTime.UtcNow, ct));
		}
	}
}
