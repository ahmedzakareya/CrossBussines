using CrossBuy.BL;
using CrossBuy.BL.Hospital;
using CrossBuy.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.Controllers
{
	// ==========================================================================================
	// HOSPITAL H159: check-in gaps — the follow-up window from a card step (reason, owner, due date), the desk's tab
	// (mine and unassigned), the full list (whose, step, state; overdue first), and close / waive / take. Every rule is
	// HospitalCheckinGapService's.
	// ==========================================================================================
	public partial class HospitalController
	{
		public sealed class HospitalGapCloseInput { public string? Note { get; set; } }

		private HospitalCheckinGapService? _gaps;
		private HospitalCheckinGapService GapSvc => _gaps ??= ActivatorUtilities.CreateInstance<HospitalCheckinGapService>(HttpContext.RequestServices);

		[HospitalReceptionAllowed]   // H159
		[HttpGet("FrontDesk/Gap")]
		[HospitalPerm(HospitalActions.Register)]
		public async Task<IActionResult> FrontDeskGap(int patient, string? item, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return NotFound();
			var p = await _db.HospitalPatients.AsNoTracking().FirstOrDefaultAsync(x => x.ID == patient && x.CompanyId == ctx.CompanyId, ct);
			if (p == null) return NotFound();
			var (today, _, _) = DeskClock(DateTime.UtcNow);
			ViewBag.Patient = p; ViewBag.Item = item; ViewBag.Today = today; ViewBag.Me = ctx.EmployeeId;
			ViewBag.People = await GapSvc.PeopleAsync(ctx, ct);
			return PartialView("_DeskGap");
		}

		[HospitalReceptionAllowed]   // H159
		[HttpPost("FrontDesk/Gaps"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Register)]
		public async Task<IActionResult> FrontDeskOpenGap([FromBody] HospitalCheckinGapInput? input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null || input == null) return NotFound(new { ok = false, code = "not_found" });
			var (today, _, _) = DeskClock(DateTime.UtcNow);
			return Result(await GapSvc.OpenAsync(ctx, input, ctx.EmployeeId, DateTime.UtcNow, today, ct));
		}

		[HospitalReceptionAllowed]   // H159: the desk's tab — mine and nobody's, overdue first
		[HttpGet("FrontDesk/Gaps")]
		[HospitalPerm(HospitalActions.Read)]
		public async Task<IActionResult> FrontDeskGaps(CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return NotFound();
			var (today, _, _) = DeskClock(DateTime.UtcNow);
			await GapSvc.SettleAsync(ctx, null, DateTime.UtcNow, ct);
			var (rows, total) = await GapSvc.ListAsync(ctx, null, "desk", null, "open", ctx.EmployeeId, 1, 12, today, ct);
			ViewBag.Total = total; ViewBag.Me = ctx.EmployeeId;
			return PartialView("_DeskGaps", rows);
		}

		[HospitalReceptionAllowed]   // H159
		[HttpGet("CheckinGaps")]
		[HospitalPerm(HospitalActions.Read)]
		public async Task<IActionResult> CheckinGaps(string? whose, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return RedirectToAction("Index", "Home");
			Chrome();
			var (today, _, _) = DeskClock(DateTime.UtcNow);
			await GapSvc.SettleAsync(ctx, null, DateTime.UtcNow, ct);
			ViewBag.Counts = await GapSvc.CountsAsync(ctx, ctx.EmployeeId, today, ct);
			ViewBag.Whose = whose;
			return View();
		}

		[HospitalReceptionAllowed]   // H159
		[HttpGet("CheckinGaps/Data")]
		[HospitalPerm(HospitalActions.Read)]
		public async Task<IActionResult> CheckinGapsData(string? q, string? whose, string? item, string? state, int page = 1, int pageSize = 25, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return NotFound();
			Paging(ref page, ref pageSize);
			var (today, _, _) = DeskClock(DateTime.UtcNow);
			var (rows, total) = await GapSvc.ListAsync(ctx, q, whose, item, state, ctx.EmployeeId, page, pageSize, today, ct);
			PageHeaders(total, page, pageSize);
			ViewBag.Me = ctx.EmployeeId;
			return PartialView("_CheckinGapRows", rows);
		}

		[HospitalReceptionAllowed]   // H159
		[HttpPost("CheckinGaps/{id:int}/Done"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Register)]
		public async Task<IActionResult> CheckinGapDone(int id, [FromBody] HospitalGapCloseInput? input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await GapSvc.DoneAsync(ctx, id, input?.Note, ctx.EmployeeId, DateTime.UtcNow, ct));
		}

		[HospitalReceptionAllowed]   // H159
		[HttpPost("CheckinGaps/{id:int}/Waive"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Register)]
		public async Task<IActionResult> CheckinGapWaive(int id, [FromBody] HospitalGapCloseInput? input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await GapSvc.WaiveAsync(ctx, id, input?.Note, ctx.EmployeeId, DateTime.UtcNow, ct));
		}

		[HospitalReceptionAllowed]   // H159
		[HttpPost("CheckinGaps/{id:int}/Take"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Register)]
		public async Task<IActionResult> CheckinGapTake(int id, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null || ctx.EmployeeId is not int me) return NotFound(new { ok = false, code = "not_found" });
			return Result(await GapSvc.TakeAsync(ctx, id, me, ct));
		}
	}
}
