using CrossBuy.BL;
using CrossBuy.BL.Hospital;
using CrossBuy.Models;
using Microsoft.AspNetCore.Mvc;

namespace CrossBuy.Controllers
{
	// ==========================================================================================
	// HOSPITAL — H55: the discharge summary of a stay. Read by the hospital (`read`), written and signed by a doctor
	// (`consult`); printed only through Report Studio (the print menu on the page: Hospital.DischargeSummary).
	// ==========================================================================================
	public partial class HospitalController
	{
		private HospitalDischargeService? _dischargeSvc;
		private HospitalDischargeService DischargeSvc => _dischargeSvc ??= ActivatorUtilities.CreateInstance<HospitalDischargeService>(HttpContext.RequestServices);

		[HttpGet("Admissions/{id:int}/Summary")]
		[HospitalPerm(HospitalActions.Read)]
		public async Task<IActionResult> DischargeSummary(int id, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return RedirectToAction("Index", "Home");
			var (view, ready) = await DischargeSvc.GetAsync(ctx, id, DateTime.UtcNow, ct);
			if (view == null) return NotFound();
			Chrome();
			ViewBag.Ready = ready;
			ViewBag.CanWrite = ready && view.Summary?.Status != Models.Context.Hospital.HospitalDischargeSummaryStatus.Signed && await _access.CanAsync(ctx, HospitalActions.Consult, null, ct);
			return View(view);
		}

		[HttpPost("Admissions/{id:int}/Summary"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Consult)]
		public async Task<IActionResult> SaveDischargeSummary(int id, [FromBody] HospitalDischargeInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null || input == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await DischargeSvc.SaveAsync(ctx, id, input, ctx.EmployeeId, DateTime.UtcNow, ct));
		}

		[HttpPost("Admissions/{id:int}/Summary/Sign"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Consult)]
		public async Task<IActionResult> SignDischargeSummary(int id, [FromBody] HospitalDischargeInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null || input == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await DischargeSvc.SignAsync(ctx, id, input, ctx.EmployeeId, DateTime.UtcNow, ct));
		}
	}
}
