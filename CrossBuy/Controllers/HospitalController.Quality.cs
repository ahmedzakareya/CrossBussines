using CrossBuy.BL;
using CrossBuy.BL.Hospital;
using CrossBuy.Models;
using Microsoft.AspNetCore.Mvc;

namespace CrossBuy.Controllers
{
	// ==========================================================================================
	// HOSPITAL — H81: the quality indicators of a month, with the five months before it, against the hospital's own
	// targets (a setting the administrator changes here).
	// ==========================================================================================
	public partial class HospitalController
	{
		private HospitalQualityService? _qualitySvc;
		private HospitalQualityService QualitySvc => _qualitySvc ??= ActivatorUtilities.CreateInstance<HospitalQualityService>(HttpContext.RequestServices);

		public sealed class HospitalQualityTargetInput { public string? Indicator { get; set; } public decimal? Target { get; set; } }

		[HttpGet("Quality")]
		[HospitalPerm(HospitalActions.Read)]
		public async Task<IActionResult> Quality(int? year, int? month, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return RedirectToAction("Index", "Home");
			Chrome();
			var now = DateTime.UtcNow;
			var local = HospitalNursingService.UtcToWard(now);
			int y = year is >= 2000 and <= 2100 ? year.Value : local.Year;
			int m = month is >= 1 and <= 12 ? month.Value : local.Month;
			if (new DateTime(y, m, 1) > local.Date) { y = local.Year; m = local.Month; }
			ViewBag.CanManage = await _access.CanAsync(ctx, HospitalActions.Manage, null, ct);
			return View(await QualitySvc.PageAsync(ctx, y, m, now, ct));
		}

		[HttpPost("Quality/Target"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Manage)]
		public async Task<IActionResult> SetQualityTarget([FromBody] HospitalQualityTargetInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null || input == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await QualitySvc.SetTargetAsync(ctx, input.Indicator, input.Target, ctx.EmployeeId, DateTime.UtcNow, ct));
		}
	}
}
