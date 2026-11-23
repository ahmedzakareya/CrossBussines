using CrossBuy.BL;
using CrossBuy.BL.Hospital;
using CrossBuy.Models;
using CrossBuy.Models.Context.Hospital;
using CrossBuy.Models.Platform;
using Microsoft.AspNetCore.Mvc;

namespace CrossBuy.Controllers
{
	// HOSPITAL H246 — acuity-based staffing: classify each inpatient per shift and plan each ward's nurses.
	public partial class HospitalController
	{
		private HospitalAcuityService? _acuitySvc;
		private HospitalAcuityService AcuitySvc => _acuitySvc ??= ActivatorUtilities.CreateInstance<HospitalAcuityService>(HttpContext.RequestServices);

		[HttpGet("Staffing"), HospitalPerm(HospitalActions.Read)]
		public async Task<IActionResult> Staffing(DateTime? day, string? shift, string? q, int page = 1, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return RedirectToAction("Index", "Home");
			Chrome();
			var now = HospitalNursingService.UtcToWard(DateTime.UtcNow);
			var sh = HospitalShift.All.Contains(shift ?? "") ? shift! : now.Hour is >= 7 and < 15 ? HospitalShift.Day : now.Hour is >= 15 and < 23 ? HospitalShift.Evening : HospitalShift.Night;
			const int size = 20;
			var d = await AcuitySvc.BoardAsync(ctx, (day ?? now).Date, sh, q, page, size, ct);
			ViewBag.Q = q; ViewBag.Page = Math.Max(1, page); ViewBag.Pages = Math.Max(1, (d.PatientTotal + size - 1) / size);
			ViewBag.CanAdmit = await _access.CanAsync(ctx, HospitalActions.Admit, null, ct);
			return View(d);
		}

		[HttpPost("Staffing/Classify/{admissionId:int}"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Admit)]
		public async Task<IActionResult> StaffingClassify(int admissionId, [FromBody] HospitalAcuityInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null || input == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await AcuitySvc.ClassifyAsync(ctx, admissionId, input, ctx.EmployeeId, DateTime.UtcNow, ct));
		}

		[HttpPost("Staffing/Plan/{wardId:int}"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Admit)]
		public async Task<IActionResult> StaffingPlan(int wardId, [FromBody] HospitalStaffPlanInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null || input == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await AcuitySvc.PlanAsync(ctx, wardId, input, ctx.EmployeeId, DateTime.UtcNow, ct));
		}
	}
}
