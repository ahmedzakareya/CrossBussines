using CrossBuy.BL;
using CrossBuy.BL.Hospital;
using CrossBuy.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.Controllers
{
	// ==========================================================================================
	// HOSPITAL H158: the desk's handoffs — the arrival window (why they came, what is waiting for them, where they go), the
	// desk's list of handoffs in progress (age, overdue, chase), and the receiving teams' "incoming from reception" list
	// (accept, arrived, cannot take). Every rule is HospitalDeskHandoffService's.
	// ==========================================================================================
	public partial class HospitalController
	{
		public sealed class HospitalHandoffFailInput { public string? Reason { get; set; } }

		private HospitalDeskHandoffService? _handoffs;
		private HospitalDeskHandoffService HandoffSvc => _handoffs ??= ActivatorUtilities.CreateInstance<HospitalDeskHandoffService>(HttpContext.RequestServices);

		[HospitalReceptionAllowed]   // H158: the desk's arrival window
		[HttpGet("FrontDesk/Arrival")]
		[HospitalPerm(HospitalActions.Register)]
		public async Task<IActionResult> FrontDeskArrival(int? patient, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return NotFound();
			var (today, _, _) = DeskClock(DateTime.UtcNow);
			if (patient is int pid)
			{
				var p = await _db.HospitalPatients.AsNoTracking().FirstOrDefaultAsync(x => x.ID == pid && x.CompanyId == ctx.CompanyId, ct);
				if (p == null) return NotFound();
				ViewBag.Patient = p;
				ViewBag.Options = await HandoffSvc.OptionsAsync(ctx, pid, today, ct);
			}
			return PartialView("_DeskArrival");
		}

		[HospitalReceptionAllowed]   // H158
		[HttpPost("FrontDesk/Handoffs"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Register)]
		public async Task<IActionResult> FrontDeskSendHandoff([FromBody] HospitalHandoffInput? input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null || input == null) return NotFound(new { ok = false, code = "not_found" });
			var (today, _, _) = DeskClock(DateTime.UtcNow);
			var (r, sent) = await HandoffSvc.SendAsync(ctx, input, ctx.EmployeeId, DateTime.UtcNow, today, ct);
			if (!r.Ok) return Result(r);
			bool en = UiEnglish;
			return Json(new
			{
				ok = true, id = r.Id, confidential = sent?.Confidential ?? false,
				where = sent?.Where?.Select(w => new { place = en ? w.PlaceEn : w.Place, detail = en ? w.DetailEn : w.Detail }),
			});
		}

		[HospitalReceptionAllowed]   // H158
		[HttpPost("FrontDesk/Handoffs/{id:int}/Remind"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Register)]
		public async Task<IActionResult> FrontDeskRemindHandoff(int id, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await HandoffSvc.RemindAsync(ctx, id, DateTime.UtcNow, ct));
		}

		[HospitalReceptionAllowed]   // H158: the desk's own list (its work tab)
		[HttpGet("FrontDesk/Handoffs")]
		[HospitalPerm(HospitalActions.Read)]
		public async Task<IActionResult> FrontDeskHandoffs(CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return NotFound();
			var (rows, total) = await HandoffSvc.ListAsync(ctx, null, null, false, 1, 12, DateTime.UtcNow, ct);
			ViewBag.Total = total;
			return PartialView("_DeskHandoffs", rows);
		}

		// ---- the receiving teams ----

		[HospitalReceptionAllowed]   // H158: the desk follows its handoffs here; accepting stays the receiving teams'
		[HttpGet("Handoffs")]
		[HospitalPerm(HospitalActions.Read)]
		public async Task<IActionResult> Handoffs(string? destination, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return RedirectToAction("Index", "Home");
			Chrome();
			ViewBag.Destination = destination;
			var (open, overdue) = await HandoffSvc.CountsAsync(ctx, DateTime.UtcNow, ct);
			ViewBag.Open = open; ViewBag.Overdue = overdue;
			ViewBag.CanReceive = await _access.CanAsync(ctx, HospitalActions.ClinicalRead, null, ct);
			return View();
		}

		[HospitalReceptionAllowed]   // H158: the desk follows its handoffs here; accepting stays the receiving teams'
		[HttpGet("Handoffs/Data")]
		[HospitalPerm(HospitalActions.Read)]
		public async Task<IActionResult> HandoffsData(string? q, string? destination, string? open, int page = 1, int pageSize = 25, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return NotFound();
			Paging(ref page, ref pageSize);
			var (rows, total) = await HandoffSvc.ListAsync(ctx, q, destination, open != "all", page, pageSize, DateTime.UtcNow, ct);
			PageHeaders(total, page, pageSize);
			ViewBag.CanReceive = await _access.CanAsync(ctx, HospitalActions.ClinicalRead, null, ct);
			return PartialView("_HandoffRows", rows);
		}

		[HttpPost("Handoffs/{id:int}/Accept"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Read)]
		public async Task<IActionResult> HandoffAccept(int id, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await HandoffSvc.AcceptAsync(ctx, id, ctx.EmployeeId, DateTime.UtcNow, ct));
		}

		[HttpPost("Handoffs/{id:int}/Arrived"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Read)]
		public async Task<IActionResult> HandoffArrived(int id, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await HandoffSvc.ArrivedAsync(ctx, id, ctx.EmployeeId, DateTime.UtcNow, ct));
		}

		[HttpPost("Handoffs/{id:int}/Fail"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Read)]
		public async Task<IActionResult> HandoffFail(int id, [FromBody] HospitalHandoffFailInput? input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await HandoffSvc.FailAsync(ctx, id, input?.Reason, ctx.EmployeeId, DateTime.UtcNow, ct));
		}
	}
}
