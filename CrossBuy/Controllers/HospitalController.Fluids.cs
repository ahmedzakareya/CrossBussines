using CrossBuy.BL;
using CrossBuy.BL.Hospital;
using CrossBuy.Models;
using Microsoft.AspNetCore.Mvc;

namespace CrossBuy.Controllers
{
	// ==========================================================================================
	// HOSPITAL — H78: the fluid chart of one admission (a fluid day and the last five), opened from the nursing station;
	// an entry is added and a wrong one voided from it.
	// ==========================================================================================
	public partial class HospitalController
	{
		private HospitalFluidService? _fluidSvc;
		private HospitalFluidService FluidSvc => _fluidSvc ??= ActivatorUtilities.CreateInstance<HospitalFluidService>(HttpContext.RequestServices);

		public sealed class HospitalFluidVoidInput { public string? Reason { get; set; } }

		[HttpGet("Admissions/{id:int}/Fluids")]
		[HospitalPerm(HospitalActions.Read)]
		public async Task<IActionResult> FluidChart(int id, int back = 0, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return NotFound();
			var chart = await FluidSvc.ChartAsync(ctx, id, back, DateTime.UtcNow, ct);
			if (chart == null) return NotFound();
			ViewBag.CanWard = await _access.CanAsync(ctx, HospitalActions.Admit, null, ct) || await _access.CanAsync(ctx, HospitalActions.Consult, null, ct);
			ViewBag.Back = Math.Clamp(back, 0, 30);
			return PartialView("_FluidChart", chart);
		}

		[HttpPost("Admissions/Fluids"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Read)]
		public async Task<IActionResult> AddFluid([FromBody] HospitalFluidInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null || input == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await FluidSvc.AddAsync(ctx, input, ctx.EmployeeId, DateTime.UtcNow, ct));
		}

		[HttpPost("Admissions/Fluids/{id:int}/Void"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Read)]
		public async Task<IActionResult> VoidFluid(int id, [FromBody] HospitalFluidVoidInput? input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await FluidSvc.VoidAsync(ctx, id, input?.Reason, ctx.EmployeeId, DateTime.UtcNow, ct));
		}
	}
}
