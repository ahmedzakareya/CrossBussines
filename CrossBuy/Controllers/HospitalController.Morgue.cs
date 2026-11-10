using CrossBuy.BL;
using CrossBuy.BL.Hospital;
using CrossBuy.Models;
using Microsoft.AspNetCore.Mvc;

namespace CrossBuy.Controllers
{
	// ==========================================================================================
	// HOSPITAL — H90: the mortuary. The compartments board and the register (searched, filtered, paged), one case;
	// adding compartments, receiving a body, the police clearance, the autopsy, moving, a belonging, the release.
	// ==========================================================================================
	public partial class HospitalController
	{
		private HospitalMorgueService? _morgueSvc;
		private HospitalMorgueService MorgueSvc => _morgueSvc ??= ActivatorUtilities.CreateInstance<HospitalMorgueService>(HttpContext.RequestServices);

		public sealed class HospitalMorgueCompartmentsInput { public string? Codes { get; set; } public string? Kind { get; set; } }

		[HttpGet("Morgue")]
		[HospitalPerm(HospitalActions.Read)]
		public async Task<IActionResult> Morgue(CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return RedirectToAction("Index", "Home");
			Chrome();
			var (board, ready) = await MorgueSvc.BoardAsync(ctx, DateTime.UtcNow, ct);
			ViewBag.Ready = ready;
			ViewBag.CanWork = await MorgueSvc.CanWorkAsync(ctx, ct);
			ViewBag.CanManage = await MorgueSvc.CanManageAsync(ctx, ct);
			ViewBag.Deaths = await MorgueSvc.PendingDeathsAsync(ctx, ct);
			return View(board);
		}

		[HttpGet("Morgue/Data")]
		[HospitalPerm(HospitalActions.Read)]
		public async Task<IActionResult> MorgueData(string? q, string? filter, int page = 1, int pageSize = 25, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return NotFound();
			Paging(ref page, ref pageSize);
			var (rows, total) = await MorgueSvc.ListAsync(ctx, q, filter, page, pageSize, DateTime.UtcNow, ct);
			PageHeaders(total, page, pageSize);
			return PartialView("_MorgueRows", rows);
		}

		[HttpGet("Morgue/{id:int}")]
		[HospitalPerm(HospitalActions.Read)]
		public async Task<IActionResult> MorgueCase(int id, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return RedirectToAction("Index", "Home");
			var page = await MorgueSvc.CasePageAsync(ctx, id, DateTime.UtcNow, ct);
			if (page == null) return NotFound();
			Chrome();
			ViewBag.CanWork = await MorgueSvc.CanWorkAsync(ctx, ct);
			return View(page);
		}

		[HttpPost("Morgue/Compartments"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Manage)]
		public async Task<IActionResult> AddMorgueCompartments([FromBody] HospitalMorgueCompartmentsInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null || input == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await MorgueSvc.AddCompartmentsAsync(ctx, input.Codes, input.Kind, ct));
		}

		[HttpPost("Morgue/Receive"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Read)]
		public async Task<IActionResult> ReceiveMorgue([FromBody] HospitalMorgueReceiveInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null || input == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await MorgueSvc.ReceiveAsync(ctx, input, ctx.EmployeeId, DateTime.UtcNow, ct));
		}

		[HttpPost("Morgue/{id:int}/{step}"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Read)]
		public async Task<IActionResult> MorgueStep(int id, string step, [FromBody] HospitalMorgueStepInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null || input == null) return NotFound(new { ok = false, code = "not_found" });
			var now = DateTime.UtcNow;
			return Result(step switch
			{
				"Police" => await MorgueSvc.ClearPoliceAsync(ctx, id, input, ctx.EmployeeId, now, ct),
				"AutopsyRequest" => await MorgueSvc.AutopsyAsync(ctx, id, false, input, now, ct),
				"AutopsyDone" => await MorgueSvc.AutopsyAsync(ctx, id, true, input, now, ct),
				"Move" => await MorgueSvc.MoveAsync(ctx, id, input, ct),
				_ => HospitalResult.Fail("not_found"),
			});
		}

		[HttpPost("Morgue/{id:int}/Belonging"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Read)]
		public async Task<IActionResult> MorgueBelonging(int id, [FromBody] HospitalMorgueItemInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null || input == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await MorgueSvc.AddBelongingAsync(ctx, id, input, ctx.EmployeeId, DateTime.UtcNow, ct));
		}

		[HttpPost("Morgue/{id:int}/Release"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Read)]
		public async Task<IActionResult> ReleaseMorgue(int id, [FromBody] HospitalMorgueReleaseInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null || input == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await MorgueSvc.ReleaseAsync(ctx, id, input, ctx.EmployeeId, DateTime.UtcNow, ct));
		}
	}
}
