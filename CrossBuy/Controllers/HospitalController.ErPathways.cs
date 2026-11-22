using CrossBuy.BL;
using CrossBuy.BL.Hospital;
using CrossBuy.Models;
using CrossBuy.Models.Platform;
using Microsoft.AspNetCore.Mvc;

namespace CrossBuy.Controllers
{
	// HOSPITAL H237 — the emergency department's time-critical pathways and ambulance pre-arrival (`admit`).
	public partial class HospitalController
	{
		private HospitalErPathwayService? _erPathSvc;
		private HospitalErPathwayService ErPathSvc => _erPathSvc ??= ActivatorUtilities.CreateInstance<HospitalErPathwayService>(HttpContext.RequestServices);
		public sealed class HospitalErPathwayActivateInput { public int VisitId { get; set; } public string? Kind { get; set; } }
		public sealed class HospitalErStepInput { public string? Code { get; set; } public bool NotIndicated { get; set; } public string? Note { get; set; } }
		public sealed class HospitalErReceiveInput { public int? PatientId { get; set; } }
		public sealed class HospitalErPathwayCloseInput { public string? Note { get; set; } }

		[HttpPost("Emergency/Pathways"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Admit)]
		public async Task<IActionResult> ErPathwayActivate([FromBody] HospitalErPathwayActivateInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null || input == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await ErPathSvc.ActivateAsync(ctx, input.VisitId, input.Kind, ctx.EmployeeId, DateTime.UtcNow, ct));
		}

		[HttpPost("Emergency/Pathways/{id:int}/Step"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Admit)]
		public async Task<IActionResult> ErPathwayStep(int id, [FromBody] HospitalErStepInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null || input == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await ErPathSvc.StepAsync(ctx, id, input.Code, input.NotIndicated, input.Note, ctx.EmployeeId, DateTime.UtcNow, ct));
		}

		[HttpPost("Emergency/Pathways/{id:int}/Close"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Admit)]
		public async Task<IActionResult> ErPathwayClose(int id, [FromBody] HospitalErPathwayCloseInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await ErPathSvc.CloseAsync(ctx, id, input?.Note, DateTime.UtcNow, ct));
		}

		[HttpPost("Emergency/PreArrivals"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Admit)]
		public async Task<IActionResult> ErPreAlert([FromBody] HospitalPreArrivalInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null || input == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await ErPathSvc.PreAlertAsync(ctx, input, ctx.EmployeeId, DateTime.UtcNow, ct));
		}

		[HttpPost("Emergency/PreArrivals/{id:int}/Receive"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Admit)]
		public async Task<IActionResult> ErPreReceive(int id, [FromBody] HospitalErReceiveInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await ErPathSvc.ReceiveAsync(ctx, ErSvc, id, input?.PatientId, ctx.EmployeeId, DateTime.UtcNow, ct));
		}

		[HttpPost("Emergency/PreArrivals/{id:int}/Cancel"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Admit)]
		public async Task<IActionResult> ErPreCancel(int id, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await ErPathSvc.CancelPreAlertAsync(ctx, id, ct));
		}
	}
}
