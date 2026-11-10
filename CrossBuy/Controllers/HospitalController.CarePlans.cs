using CrossBuy.BL;
using CrossBuy.BL.Hospital;
using CrossBuy.Models;
using Microsoft.AspNetCore.Mvc;

namespace CrossBuy.Controllers
{
	// ==========================================================================================
	// HOSPITAL — H79: the patient's problem list and the stay's nursing care plan, as one panel opened from the nursing
	// station; problems are added and resolved by doctors, plans written and closed by the ward.
	// ==========================================================================================
	public partial class HospitalController
	{
		private HospitalCarePlanService? _carePlanSvc;
		private HospitalCarePlanService CarePlanSvc => _carePlanSvc ??= ActivatorUtilities.CreateInstance<HospitalCarePlanService>(HttpContext.RequestServices);

		[HttpGet("Admissions/{id:int}/CarePlan")]
		[HospitalPerm(HospitalActions.Read)]
		public async Task<IActionResult> CarePlanPanel(int id, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return NotFound();
			var panel = await CarePlanSvc.PanelAsync(ctx, id, DateTime.UtcNow, ct);
			if (panel == null) return NotFound();
			ViewBag.CanConsult = await _access.CanAsync(ctx, HospitalActions.Consult, null, ct);
			ViewBag.CanWard = ViewBag.CanConsult == true || await _access.CanAsync(ctx, HospitalActions.Admit, null, ct);
			return PartialView("_CarePlanPanel", panel);
		}

		[HttpPost("Admissions/Problems"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Consult)]
		public async Task<IActionResult> AddProblem([FromBody] HospitalProblemInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null || input == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await CarePlanSvc.AddProblemAsync(ctx, input, ctx.EmployeeId, DateTime.UtcNow, ct));
		}

		[HttpPost("Admissions/Problems/{id:int}/Resolve"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Consult)]
		public async Task<IActionResult> ResolveProblem(int id, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await CarePlanSvc.ResolveProblemAsync(ctx, id, ctx.EmployeeId, DateTime.UtcNow, ct));
		}

		[HttpPost("Admissions/CarePlan"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Read)]
		public async Task<IActionResult> AddCarePlan([FromBody] HospitalCarePlanInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null || input == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await CarePlanSvc.AddPlanAsync(ctx, input, ctx.EmployeeId, DateTime.UtcNow, ct));
		}

		[HttpPost("Admissions/CarePlan/{id:int}/Close"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Read)]
		public async Task<IActionResult> CloseCarePlan(int id, [FromBody] HospitalCarePlanCloseInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null || input == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await CarePlanSvc.ClosePlanAsync(ctx, id, input, ctx.EmployeeId, DateTime.UtcNow, ct));
		}
	}
}
