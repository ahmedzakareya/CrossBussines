using CrossBuy.BL;
using CrossBuy.BL.Hospital;
using CrossBuy.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.Controllers
{
	// ==========================================================================================
	// HOSPITAL — H67: the support desk (housekeeping, porters, laundry). The figures, a job asked for in a minute, and
	// the jobs searched and paged on the server — take, close (a bed's cleaning frees the bed), cancel.
	// ==========================================================================================
	public partial class HospitalController
	{
		private HospitalSupportDeskService? _deskSvc;
		private HospitalSupportDeskService DeskSvc => _deskSvc ??= ActivatorUtilities.CreateInstance<HospitalSupportDeskService>(HttpContext.RequestServices);

		public sealed class HospitalSupportMoveInput { public string? To { get; set; } public string? Note { get; set; } }

		[HttpGet("SupportDesk")]
		[HospitalPerm(HospitalActions.Read)]
		public async Task<IActionResult> SupportDesk(string? kind, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return RedirectToAction("Index", "Home");
			Chrome();
			var f = await DeskSvc.FiguresAsync(ctx, DateTime.UtcNow, ct);
			ViewBag.Ready = f.Ready; ViewBag.OpenByKind = f.OpenByKind; ViewBag.BedsToClean = f.BedsToClean; ViewBag.AvgResponse = f.AvgResponseToday; ViewBag.DoneToday = f.DoneToday;
			ViewBag.Kind = Models.Context.Hospital.HospitalSupportKind.All.Contains(kind ?? "") ? kind : null;
			ViewBag.Rooms = await _db.HospitalRooms.AsNoTracking().Where(r => r.CompanyId == ctx.CompanyId).OrderBy(r => r.Number).Select(r => new HospitalChoice(r.ID, r.Number, r.Number, null)).ToListAsync(ct);
			ViewBag.Beds = await (from b in _db.HospitalBeds.AsNoTracking() join r in _db.HospitalRooms.AsNoTracking() on b.RoomId equals r.ID
								  where b.CompanyId == ctx.CompanyId && b.IsActive orderby r.Number, b.Code select new HospitalChoice(b.ID, b.Code, b.Code, r.Number)).ToListAsync(ct);
			return View();
		}

		[HttpGet("SupportDesk/Data")]
		[HospitalPerm(HospitalActions.Read)]
		public async Task<IActionResult> SupportDeskData(string? q, string? kind, string? state, int page = 1, int pageSize = 25, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return NotFound();
			if (pageSize is < 1 or > 200) pageSize = 25;
			if (page < 1) page = 1;
			var (rows, total) = await DeskSvc.ListAsync(ctx, string.IsNullOrEmpty(kind) ? null : kind, string.IsNullOrEmpty(state) ? null : state, q, page, pageSize, ct);
			Response.Headers["X-Total"] = total.ToString();
			Response.Headers["X-Page"] = page.ToString();
			Response.Headers["X-Pages"] = Math.Max(1, (int)Math.Ceiling(total / (double)pageSize)).ToString();
			ViewBag.Me = ctx.EmployeeId;
			ViewBag.CanManage = await _access.CanAsync(ctx, HospitalActions.Manage, null, ct);
			return PartialView("_SupportJobRows", rows);
		}

		[HttpPost("SupportDesk"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Read)]
		public async Task<IActionResult> AskSupportJob([FromBody] HospitalSupportJobInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null || input == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await DeskSvc.AskAsync(ctx, input, ctx.EmployeeId, DateTime.UtcNow, ct));
		}

		[HttpPost("SupportDesk/{id:int}/Move"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Read)]
		public async Task<IActionResult> MoveSupportJob(int id, [FromBody] HospitalSupportMoveInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null || input == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await DeskSvc.MoveAsync(ctx, id, input.To ?? "", input.Note, ctx.EmployeeId, DateTime.UtcNow, ct));
		}
	}
}
