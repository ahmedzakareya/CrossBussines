using CrossBuy.BL;
using CrossBuy.BL.Hospital;
using CrossBuy.Models;
using CrossBuy.Models.Context.Hospital;
using CrossBuy.Models.Platform;
using Microsoft.AspNetCore.Mvc;

namespace CrossBuy.Controllers
{
	// HOSPITAL H217 — endoscopy: the procedures and the scopes (read with `clinical-read`), a procedure started and
	// completed (`consult`), a scope added (`manage`), a reprocessing cycle recorded (`admit`, the unit's nurses).
	public partial class HospitalController
	{
		public sealed class HospitalScopeInput { public string? Serial { get; set; } public string? Kind { get; set; } public string? Model { get; set; } }

		private HospitalEndoscopyService? _endoSvc;
		private HospitalEndoscopyService EndoSvc => _endoSvc ??= ActivatorUtilities.CreateInstance<HospitalEndoscopyService>(HttpContext.RequestServices);

		[HttpGet("Endoscopy")]
		[HospitalPerm(HospitalActions.ClinicalRead)]
		public async Task<IActionResult> Endoscopy(string? q, string? status, int page = 1, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return RedirectToAction("Index", "Home");
			Chrome();
			const int size = 20;
			if (!string.IsNullOrEmpty(status) && !HospitalEndoStatus.All.Contains(status)) status = null;
			var (rows, total, ready) = await EndoSvc.ListAsync(ctx, q, status, page, size, ct);
			ViewBag.Q = q; ViewBag.Status = status; ViewBag.Page = Math.Max(1, page); ViewBag.Pages = Math.Max(1, (total + size - 1) / size); ViewBag.Total = total; ViewBag.Ready = ready;
			ViewBag.Scopes = await EndoSvc.ScopesAsync(ctx, DateTime.UtcNow, ct);
			ViewBag.CanConsult = await _access.CanAsync(ctx, HospitalActions.Consult, null, ct);
			ViewBag.CanManage = await _access.CanAsync(ctx, HospitalActions.Manage, null, ct);
			ViewBag.CanNurse = await _access.CanAsync(ctx, HospitalActions.Admit, null, ct);
			return View(rows);
		}

		[HttpPost("Endoscopy"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Consult)]
		public async Task<IActionResult> EndoscopyStart([FromBody] HospitalEndoStartInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null || input == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await EndoSvc.StartAsync(ctx, input, ctx.EmployeeId, DateTime.UtcNow, ct));
		}

		[HttpPost("Endoscopy/{id:int}/Complete"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Consult)]
		public async Task<IActionResult> EndoscopyComplete(int id, [FromBody] HospitalEndoCompleteInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null || input == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await EndoSvc.CompleteAsync(ctx, PathSvc, id, input, ctx.EmployeeId, DateTime.UtcNow, ct));
		}

		[HttpPost("Endoscopy/Scopes"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Manage)]
		public async Task<IActionResult> EndoscopyScope([FromBody] HospitalScopeInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null || input == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await EndoSvc.AddScopeAsync(ctx, input.Serial, input.Kind, input.Model, DateTime.UtcNow, ct));
		}

		[HttpPost("Endoscopy/Scopes/{id:int}/Cycle"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Admit)]
		public async Task<IActionResult> EndoscopyCycle(int id, [FromBody] HospitalScopeCycleInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null || input == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await EndoSvc.CycleAsync(ctx, id, input, ctx.EmployeeId, DateTime.UtcNow, ct));
		}
	}
}
