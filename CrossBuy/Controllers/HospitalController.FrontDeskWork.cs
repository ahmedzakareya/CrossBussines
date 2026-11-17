using CrossBuy.BL;
using CrossBuy.BL.Hospital;
using CrossBuy.Models;
using CrossBuy.Models.Context.Hospital;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.Controllers
{
	// ==========================================================================================
	// HOSPITAL H141: the desk's work under the board, one tab at a time (loaded when opened): confirm tomorrow, portal
	// requests, the waitlist, self-intake, pre-authorisations, unidentified patients, duplicates. And the reschedule
	// window (any booked appointment, from the board, the counter or the confirm list).
	// ==========================================================================================
	public partial class HospitalController
	{
		public sealed class HospitalDeskConfirmInput { public string? Status { get; set; } public string? Note { get; set; } }
		public sealed class HospitalDeskRescheduleInput { public int PractitionerId { get; set; } public DateTime Date { get; set; } public int StartMinute { get; set; } }

		private HospitalFrontDeskWorkService? _deskWork;
		private HospitalFrontDeskWorkService DeskWorkSvc => _deskWork ??= ActivatorUtilities.CreateInstance<HospitalFrontDeskWorkService>(HttpContext.RequestServices,
			_outpatient, ActivatorUtilities.CreateInstance<HospitalPortalBookingService>(HttpContext.RequestServices));

		[HospitalReceptionAllowed]   // H156: the reception desk works here (administrative)
		[HttpGet("FrontDesk/Work")]
		[HospitalPerm(HospitalActions.Read)]
		public async Task<IActionResult> FrontDeskWork(string? kind, string? whose, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return NotFound();
			var now = DateTime.UtcNow; var (today, _, _) = DeskClock(now);
			ViewBag.Kind = kind;
			ViewBag.CanBook = await _access.CanAsync(ctx, HospitalActions.Book, null, ct);
			if (kind == "confirm")
			{
				ViewBag.Day = today.AddDays(1);
				return PartialView("_DeskConfirm", await DeskWorkSvc.ConfirmListAsync(ctx, today.AddDays(1), ct));
			}
			if (!HospitalFrontDeskWorkRules.Kinds.Contains(kind ?? "")) return NotFound();
			await MoneyFormatAsync(ctx, ct);
			// H164: every item with its owner and lateness; the tab shows the slice asked for (mine / unassigned / all)
			var all = await DeskWorkSvc.WorkAsync(ctx, kind!, now, 300, ct);
			var (items, counts) = await ClaimSvc.QueueAsync(ctx, all, whose, ctx.EmployeeId, now, 8, ct);
			ViewBag.Counts = counts; ViewBag.Whose = whose is "mine" or "unassigned" ? whose : "all"; ViewBag.Me = ctx.EmployeeId;
			return PartialView("_DeskWork", items);
		}

		private HospitalDeskClaimService? _claims;
		private HospitalDeskClaimService ClaimSvc => _claims ??= ActivatorUtilities.CreateInstance<HospitalDeskClaimService>(HttpContext.RequestServices);

		[HospitalReceptionAllowed]   // H164: take an item of a queue (it becomes mine)
		[HttpPost("FrontDesk/Work/{kind}/Take"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Register)]
		public async Task<IActionResult> FrontDeskWorkTake(string kind, string? key, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null || ctx.EmployeeId is not int me) return NotFound(new { ok = false, code = "not_found" });
			return Result(await ClaimSvc.TakeAsync(ctx, kind, key, me, DateTime.UtcNow, ct));
		}

		[HospitalReceptionAllowed]   // H164: give it back to nobody
		[HttpPost("FrontDesk/Work/{kind}/Release"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Register)]
		public async Task<IActionResult> FrontDeskWorkRelease(string kind, string? key, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null || ctx.EmployeeId is not int me) return NotFound(new { ok = false, code = "not_found" });
			return Result(await ClaimSvc.ReleaseAsync(ctx, kind, key, me, DateTime.UtcNow, ct));
		}

		[HospitalReceptionAllowed]   // H156: the reception desk works here (administrative)
		[HttpPost("FrontDesk/Appointments/{id:int}/Confirm"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Book)]
		public async Task<IActionResult> FrontDeskConfirm(int id, [FromBody] HospitalDeskConfirmInput? input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await DeskWorkSvc.ConfirmAsync(ctx, id, input?.Status, input?.Note, ctx.EmployeeId, DateTime.UtcNow, ct));
		}

		[HospitalReceptionAllowed]   // H156: the reception desk works here (administrative)
		[HttpGet("FrontDesk/Appointments/{id:int}/Reschedule")]
		[HospitalPerm(HospitalActions.Book)]
		public async Task<IActionResult> FrontDeskRescheduleWindow(int id, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return NotFound();
			var a = await _db.HospitalAppointments.AsNoTracking().FirstOrDefaultAsync(x => x.ID == id && x.CompanyId == ctx.CompanyId, ct);
			if (a == null) return NotFound();
			var p = await _db.HospitalPatients.AsNoTracking().FirstOrDefaultAsync(x => x.ID == a.PatientId && x.CompanyId == ctx.CompanyId, ct);
			if (p == null) return NotFound();
			var now = DateTime.UtcNow; var (today, _, local) = DeskClock(now);
			ViewBag.Patient = p;
			ViewBag.Doctors = (await _outpatient.PractitionersAsync(ctx, ct)).Where(d => d.Row.IsActive || d.Row.ID == a.PractitionerId).ToList();
			ViewBag.Today = today;
			ViewBag.Slots = await DeskWorkSvc.SlotsAsync(ctx, a.PractitionerId, a.AppointmentDate < today ? today : a.AppointmentDate, local, ct);
			return PartialView("_DeskResched", a);
		}

		[HospitalReceptionAllowed]   // H156: the reception desk works here (administrative)
		[HttpGet("FrontDesk/Slots")]
		[HospitalPerm(HospitalActions.Book)]
		public async Task<IActionResult> FrontDeskSlots(int doctor, DateTime date, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return NotFound(new { ok = false, code = "not_found" });
			var slots = await DeskWorkSvc.SlotsAsync(ctx, doctor, date, DeskClock(DateTime.UtcNow).NowLocal, ct);
			return Json(new { ok = true, slots = slots.Select(m => new { m, t = (m / 60).ToString("00") + ":" + (m % 60).ToString("00") }) });
		}

		[HospitalReceptionAllowed]   // H156: the reception desk works here (administrative)
		[HttpPost("FrontDesk/Appointments/{id:int}/Reschedule"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Book)]
		public async Task<IActionResult> FrontDeskReschedule(int id, [FromBody] HospitalDeskRescheduleInput? input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null || input == null) return NotFound(new { ok = false, code = "not_found" });
			var now = DateTime.UtcNow;
			return Result(await DeskWorkSvc.RescheduleAsync(ctx, id, input.PractitionerId, input.Date, input.StartMinute, ctx.EmployeeId, DeskClock(now).NowLocal, now, ct));
		}
	}
}
