using CrossBuy.BL;
using CrossBuy.BL.Hospital;
using CrossBuy.Models;
using CrossBuy.Models.Platform;
using Microsoft.AspNetCore.Mvc;

namespace CrossBuy.Controllers
{
	// ==========================================================================================
	// HOSPITAL — H42: central sterile supply (the prototype's #/cssd). The instrument sets and where each is, the
	// autoclave cycles, issuing a sterile set to a theatre case and taking it back. Every rule is HospitalCssdService's;
	// the door is `read`, the service decides who may do what. Built from the request's services.
	// ==========================================================================================
	public partial class HospitalController
	{
		private HospitalCssdService? _cssdSvc;
		private HospitalCssdService CssdSvc => _cssdSvc ??= ActivatorUtilities.CreateInstance<HospitalCssdService>(HttpContext.RequestServices);

		public sealed class HospitalSetStepInput { public string Step { get; set; } = ""; public int? SurgeryId { get; set; } public bool PackIntact { get; set; } }
		public sealed class HospitalCycleStartInput { public List<int> SetIds { get; set; } = new(); public string? Autoclave { get; set; } public bool ImplantLoad { get; set; } }
		public sealed class HospitalCssdResultInput { public string Result { get; set; } = ""; }
		public sealed class HospitalCssdReasonInput { public string? Reason { get; set; } }

		[HttpGet("Cssd")]
		[HospitalPerm(HospitalActions.Read)]
		public async Task<IActionResult> Cssd(CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return RedirectToAction("Index", "Home");
			Chrome();
			var (board, ready) = await CssdSvc.BoardAsync(ctx, DateTime.UtcNow, ct);
			ViewBag.Ready = ready;
			ViewBag.CanWork = await _access.CanAsync(ctx, HospitalActions.Admit, null, ct);
			ViewBag.CanManage = await _access.CanAsync(ctx, HospitalActions.Manage, null, ct);
			return View(board);
		}

		[HttpPost("Cssd/Sets"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Manage)]
		public async Task<IActionResult> CssdSaveSet([FromBody] HospitalSetInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null || input == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await CssdSvc.SaveSetAsync(ctx, input, DateTime.UtcNow, ct));
		}

		[HttpPost("Cssd/Sets/{id:int}/Step"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Read)]
		public async Task<IActionResult> CssdStep(int id, [FromBody] HospitalSetStepInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null || input == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await CssdSvc.StepAsync(ctx, id, input.Step, input.SurgeryId, ctx.EmployeeId, DateTime.UtcNow, ct, input.PackIntact));
		}

		[HttpPost("Cssd/Cycles"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Read)]
		public async Task<IActionResult> CssdStartCycle([FromBody] HospitalCycleStartInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null || input == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await CssdSvc.StartCycleAsync(ctx, input.SetIds, input.Autoclave, ctx.EmployeeId, DateTime.UtcNow, ct, input.ImplantLoad));
		}

		[HttpPost("Cssd/Cycles/{id:int}/End"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Read)]
		public async Task<IActionResult> CssdEndCycle(int id, [FromBody] HospitalCycleEndRecord input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null || input == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await CssdSvc.EndCycleAsync(ctx, id, input, ctx.EmployeeId, DateTime.UtcNow, ct));
		}

		// H170: release, the biological result, a recall by hand, and the recall follow-up
		[HttpPost("Cssd/Cycles/{id:int}/Release"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Read)]
		public async Task<IActionResult> CssdRelease(int id, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await CssdSvc.ReleaseAsync(ctx, id, ctx.EmployeeId, DateTime.UtcNow, ct));
		}

		[HttpPost("Cssd/Cycles/{id:int}/Biological"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Read)]
		public async Task<IActionResult> CssdBiological(int id, [FromBody] HospitalCssdResultInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null || input == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await CssdSvc.BiologicalAsync(ctx, id, input.Result, ctx.EmployeeId, DateTime.UtcNow, ct));
		}

		[HttpPost("Cssd/Cycles/{id:int}/Recall"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Read)]
		public async Task<IActionResult> CssdRecall(int id, [FromBody] HospitalCssdReasonInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null || input == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await CssdSvc.RecallAsync(ctx, id, input.Reason, ctx.EmployeeId, DateTime.UtcNow, ct));
		}

		[HttpPost("Cssd/Recalls/{id:int}/Notified"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Read)]
		public async Task<IActionResult> CssdRecallNotified(int id, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await CssdSvc.RecallNotifyAsync(ctx, id, ctx.EmployeeId, DateTime.UtcNow, ct));
		}

		[HttpPost("Cssd/Recalls/{id:int}/Close"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Read)]
		public async Task<IActionResult> CssdRecallClose(int id, [FromBody] HospitalCssdReasonInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null || input == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await CssdSvc.RecallCloseAsync(ctx, id, input.Reason, ctx.EmployeeId, DateTime.UtcNow, ct));
		}
	}
}
