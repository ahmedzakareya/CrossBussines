using CrossBuy.BL;
using CrossBuy.BL.Hospital;
using CrossBuy.Models;
using Microsoft.AspNetCore.Mvc;

namespace CrossBuy.Controllers
{
	// ==========================================================================================
	// HOSPITAL — H89: physiotherapy. The episodes (open by default; searched, filtered, paged), one episode; referring,
	// assessing, recording a session, adding and closing a goal, discharging or cancelling.
	// ==========================================================================================
	public partial class HospitalController
	{
		private HospitalRehabService? _rehabSvc;
		private HospitalRehabService RehabSvc => _rehabSvc ??= ActivatorUtilities.CreateInstance<HospitalRehabService>(HttpContext.RequestServices);

		public sealed class HospitalRehabGoalMarkInput { public string? Status { get; set; } }

		[HttpGet("Rehab")]
		[HospitalPerm(HospitalActions.Read)]
		public async Task<IActionResult> Rehab(CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return RedirectToAction("Index", "Home");
			Chrome();
			var (_, _, ready) = await RehabSvc.ListAsync(ctx, null, null, 1, 1, DateTime.UtcNow, ct);
			ViewBag.Ready = ready;
			ViewBag.CanRefer = await RehabSvc.CanReferAsync(ctx, ct);
			ViewBag.UnitSettings = await UnitSettingsSvc.ViewAsync(ctx, HospitalSessionKinds.Physio, ct);   // H123
			return View();
		}

		[HttpGet("Rehab/Data")]
		[HospitalPerm(HospitalActions.Read)]
		public async Task<IActionResult> RehabData(string? q, string? filter, int page = 1, int pageSize = 25, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return NotFound();
			Paging(ref page, ref pageSize);
			var (rows, total, _) = await RehabSvc.ListAsync(ctx, q, filter, page, pageSize, DateTime.UtcNow, ct);
			PageHeaders(total, page, pageSize);
			return PartialView("_RehabRows", rows);
		}

		[HttpGet("Rehab/{id:int}")]
		[HospitalPerm(HospitalActions.Read)]
		public async Task<IActionResult> RehabEpisode(int id, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return RedirectToAction("Index", "Home");
			var page = await RehabSvc.PageAsync(ctx, id, DateTime.UtcNow, ct);
			if (page == null) return NotFound();
			Chrome();
			ViewBag.CanTreat = await RehabSvc.CanTreatAsync(ctx, ct);
			ViewBag.Today = HospitalNursingService.UtcToWard(DateTime.UtcNow).Date;
			// H130: what each attended session draws from the physiotherapy store
			ViewBag.KitCard = new HospitalSessionKitCard(HospitalSessionKinds.Physio, await SessionKitSvc.KitAsync(ctx, HospitalSessionKinds.Physio, null, ct), null, false,
				await _access.CanAsync(ctx, HospitalActions.Manage, null, ct));
			return View(page);
		}

		[HttpPost("Rehab"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Consult)]
		public async Task<IActionResult> ReferRehab([FromBody] HospitalRehabReferInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null || input == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await RehabSvc.ReferAsync(ctx, input, ctx.EmployeeId, DateTime.UtcNow, ct));
		}

		[HttpPost("Rehab/{id:int}/Assess"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Read)]
		public async Task<IActionResult> AssessRehab(int id, [FromBody] HospitalRehabAssessInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null || input == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await RehabSvc.AssessAsync(ctx, id, input, ctx.EmployeeId, DateTime.UtcNow, ct));
		}

		[HttpPost("Rehab/Session"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Read)]
		public async Task<IActionResult> RehabSession([FromBody] HospitalRehabSessionInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null || input == null) return NotFound(new { ok = false, code = "not_found" });
			// H130: an attended session draws the physiotherapy kit (a no-show or a cancellation draws nothing)
			return await CompletedWithKitAsync(ctx, HospitalSessionKinds.Physio, await RehabSvc.AddSessionAsync(ctx, input, ctx.EmployeeId, DateTime.UtcNow, ct), ct);
		}

		[HttpPost("Rehab/Goal"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Read)]
		public async Task<IActionResult> RehabGoal([FromBody] HospitalRehabGoalInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null || input == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await RehabSvc.AddGoalAsync(ctx, input, DateTime.UtcNow, ct));
		}

		[HttpPost("Rehab/Goal/{id:int}"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Read)]
		public async Task<IActionResult> MarkRehabGoal(int id, [FromBody] HospitalRehabGoalMarkInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null || input == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await RehabSvc.MarkGoalAsync(ctx, id, input.Status, DateTime.UtcNow, ct));
		}

		[HttpPost("Rehab/{id:int}/End"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Read)]
		public async Task<IActionResult> EndRehab(int id, [FromBody] HospitalRehabEndInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null || input == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await RehabSvc.EndAsync(ctx, id, input, DateTime.UtcNow, ct));
		}
	}
}
