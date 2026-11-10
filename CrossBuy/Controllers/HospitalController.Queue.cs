using CrossBuy.BL;
using CrossBuy.BL.Hospital;
using CrossBuy.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.Controllers
{
	// ==========================================================================================
	// HOSPITAL — H83: the waiting-room display (a full screen that refreshes itself and chimes on a new call) and the
	// self check-in kiosk (a full screen a patient uses: file number + year of birth, then a confirming tap). Both run on
	// a signed-in device: the display needs `read`, the kiosk `book` (the desk's own right to mark a patient arrived).
	// ==========================================================================================
	public partial class HospitalController
	{
		private HospitalQueueService? _queueSvc;
		private HospitalQueueService QueueSvc => _queueSvc ??= ActivatorUtilities.CreateInstance<HospitalQueueService>(HttpContext.RequestServices);

		public sealed class HospitalKioskFindInput { public string? Mrn { get; set; } public int? BirthYear { get; set; } }
		public sealed class HospitalKioskCheckInInput { public int AppointmentId { get; set; } public string? Mrn { get; set; } public int? BirthYear { get; set; } }

		[HttpGet("Queue/Display")]
		[HospitalPerm(HospitalActions.Read)]
		public async Task<IActionResult> QueueDisplay(int? department, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return RedirectToAction("Index", "Home");
			ViewBag.Department = department;
			ViewBag.Departments = await _db.HospitalDepartments.AsNoTracking().Where(d => d.CompanyId == ctx.CompanyId && d.IsActive).OrderBy(d => d.SortOrder).ToListAsync(ct);
			return View(await QueueSvc.BoardAsync(ctx, ClinicToday(), department, DateTime.UtcNow, ct));
		}

		[HttpGet("Queue/Display/Data")]
		[HospitalPerm(HospitalActions.Read)]
		public async Task<IActionResult> QueueDisplayData(int? department, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return NotFound();
			var b = await QueueSvc.BoardAsync(ctx, ClinicToday(), department, DateTime.UtcNow, ct);
			return PartialView("_QueueClinics", b);
		}

		[HttpGet("Queue/Kiosk")]
		[HospitalPerm(HospitalActions.Book)]
		public async Task<IActionResult> QueueKiosk(CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return RedirectToAction("Index", "Home");
			return View();
		}

		[HttpPost("Queue/Kiosk/Find"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Book)]
		public async Task<IActionResult> KioskFind([FromBody] HospitalKioskFindInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null || input == null) return NotFound(new { ok = false, code = "not_found" });
			var (rows, name, why) = await QueueSvc.FindAsync(ctx, input.Mrn, input.BirthYear, DateTime.UtcNow, ct);
			if (why != null) return Result(HospitalResult.Fail(why));
			return Json(new
			{
				ok = true, name,
				appointments = rows.Select(r => new
				{
					id = r.Id, doctor = DisplayName.Of(r.Doctor, r.DoctorEn), department = r.Department == null ? null : DisplayName.Of(r.Department, r.DepartmentEn ?? r.Department),
					time = (r.StartMinute / 60).ToString("00") + ":" + (r.StartMinute % 60).ToString("00"), arrived = r.Status == Models.Context.Hospital.HospitalAppointmentStatus.Arrived,
					queue = r.QueueNumber, tooEarly = r.TooEarly,
				}),
			});
		}

		[HttpPost("Queue/Kiosk/CheckIn"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Book)]
		public async Task<IActionResult> KioskCheckIn([FromBody] HospitalKioskCheckInInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null || input == null) return NotFound(new { ok = false, code = "not_found" });
			var (r, q) = await QueueSvc.CheckInAsync(ctx, _outpatient, input.AppointmentId, input.Mrn, input.BirthYear, ctx.EmployeeId, DateTime.UtcNow, ct);
			if (!r.Ok) return Result(r);
			return Json(new { ok = true, queue = q });
		}
	}
}
