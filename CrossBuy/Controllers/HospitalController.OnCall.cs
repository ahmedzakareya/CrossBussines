using CrossBuy.BL;
using CrossBuy.BL.Hospital;
using CrossBuy.Models;
using Microsoft.AspNetCore.Mvc;

namespace CrossBuy.Controllers
{
	// ==========================================================================================
	// HOSPITAL — H68: on-call. The week board (Saturday to Friday) by department, who every department calls now, a
	// shift planned by the administrator, and a shift cancelled with why.
	// ==========================================================================================
	public partial class HospitalController
	{
		private HospitalOnCallService? _onCallSvc;
		private HospitalOnCallService OnCallSvc => _onCallSvc ??= ActivatorUtilities.CreateInstance<HospitalOnCallService>(HttpContext.RequestServices);

		public sealed class HospitalOnCallCancelInput { public string? Reason { get; set; } }

		[HttpGet("OnCall")]
		[HospitalPerm(HospitalActions.Read)]
		public async Task<IActionResult> OnCall(string? week, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return RedirectToAction("Index", "Home");
			Chrome();
			var wardToday = HospitalNursingService.UtcToWard(DateTime.UtcNow).Date;
			var day = DateTime.TryParseExact(week ?? "", "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var d) ? d : wardToday;
			ViewBag.Today = wardToday;
			ViewBag.CanManage = await _access.CanAsync(ctx, HospitalActions.Manage, null, ct);
			return View(await OnCallSvc.WeekAsync(ctx, day, DateTime.UtcNow, ct));
		}

		[HttpPost("OnCall"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Manage)]
		public async Task<IActionResult> PlanOnCall([FromBody] HospitalOnCallInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null || input == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await OnCallSvc.PlanAsync(ctx, input, ctx.EmployeeId, DateTime.UtcNow, ct));
		}

		[HttpPost("OnCall/{id:int}/Cancel"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Manage)]
		public async Task<IActionResult> CancelOnCall(int id, [FromBody] HospitalOnCallCancelInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null || input == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await OnCallSvc.CancelAsync(ctx, id, input.Reason, ctx.EmployeeId, DateTime.UtcNow, ct));
		}
	}
}
