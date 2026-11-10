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

		public sealed class HospitalSetStepInput { public string Step { get; set; } = ""; public int? SurgeryId { get; set; } }
		public sealed class HospitalCycleStartInput { public List<int> SetIds { get; set; } = new(); public string? Autoclave { get; set; } }
		public sealed class HospitalCycleEndInput { public bool Passed { get; set; } public string? Note { get; set; } }

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
			return Result(await CssdSvc.StepAsync(ctx, id, input.Step, input.SurgeryId, ctx.EmployeeId, DateTime.UtcNow, ct));
		}

		[HttpPost("Cssd/Cycles"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Read)]
		public async Task<IActionResult> CssdStartCycle([FromBody] HospitalCycleStartInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null || input == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await CssdSvc.StartCycleAsync(ctx, input.SetIds, input.Autoclave, ctx.EmployeeId, DateTime.UtcNow, ct));
		}

		[HttpPost("Cssd/Cycles/{id:int}/End"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Read)]
		public async Task<IActionResult> CssdEndCycle(int id, [FromBody] HospitalCycleEndInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null || input == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await CssdSvc.EndCycleAsync(ctx, id, input.Passed, input.Note, ctx.EmployeeId, DateTime.UtcNow, ct));
		}
	}
}
