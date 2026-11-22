using CrossBuy.BL;
using CrossBuy.BL.Hospital;
using CrossBuy.Models;
using CrossBuy.Models.Platform;
using Microsoft.AspNetCore.Mvc;

namespace CrossBuy.Controllers
{
	// ==========================================================================================
	// HOSPITAL — H40: intensive care (the prototype's #/icu). The chosen ICU patient's banner, the unit's figures and
	// the hourly flowsheet; the nurse enters an hour, the day's assessment holds SOFA / ventilator / vasopressors.
	// Every rule is HospitalIcuService's; built from the request's services, so Program.cs is untouched.
	// ==========================================================================================
	public partial class HospitalController
	{
		private HospitalIcuService? _icuSvc;
		private HospitalIcuService IcuSvc => _icuSvc ??= ActivatorUtilities.CreateInstance<HospitalIcuService>(HttpContext.RequestServices);

		[HttpGet("Icu")]
		[HospitalPerm(HospitalActions.Read)]
		public async Task<IActionResult> Icu(int? admission, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return RedirectToAction("Index", "Home");
			Chrome();
			var (board, ready) = await IcuSvc.BoardAsync(ctx, admission, DateTime.UtcNow, ct);
			ViewBag.Ready = ready;
			ViewBag.CanAdmit = await _access.CanAsync(ctx, HospitalActions.Admit, null, ct);
			ViewBag.CanAssess = ViewBag.CanAdmit == true || await _access.CanAsync(ctx, HospitalActions.Consult, null, ct);
			if (board.Selected != null) ViewBag.Care = await IcuCareSvc.CareAsync(ctx, board.Selected.AdmissionId, DateTime.UtcNow, ct);   // H238
			return View(board);
		}

		[HttpPost("Icu/Hour"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Admit)]
		public async Task<IActionResult> IcuHour([FromBody] HospitalIcuHourInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null || input == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await IcuSvc.RecordHourAsync(ctx, input, ctx.EmployeeId, DateTime.UtcNow, ct));
		}

		[HttpPost("Icu/Assess"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Read)]
		public async Task<IActionResult> IcuAssess([FromBody] HospitalIcuAssessInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null || input == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await IcuSvc.AssessAsync(ctx, input, ctx.EmployeeId, DateTime.UtcNow, ct));
		}
	}
}
