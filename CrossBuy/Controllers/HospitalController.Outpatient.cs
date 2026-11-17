using CrossBuy.BL;
using CrossBuy.BL.Hospital;
using CrossBuy.BL.Platform;
using CrossBuy.Models;
using CrossBuy.Models.Context.Hospital;
using CrossBuy.Models.Platform;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.Controllers
{
	// ==========================================================================================
	// HOSPITAL — H4: outpatient. Practitioners (registry, profile, form), the appointment book
	// (the doctor schedule grid) and the clinics board. Every rule is HospitalOutpatientService's.
	//
	// THE CLINIC DAY is the server's local date and minute (DateTime.Now): appointments are wall-clock
	// clinic time, and the server runs where the clinic is. A per-company time zone belongs to the
	// country settings unit.
	// ==========================================================================================
	public partial class HospitalController
	{
		private static DateTime ClinicToday() => DateTime.Now.Date;
		private static int ClinicMinute() => DateTime.Now.Hour * 60 + DateTime.Now.Minute;

		public sealed class HospitalMoveInput { public string To { get; set; } = ""; }

		/// A choice in a form list. A NAMED type on purpose: an anonymous type read through `dynamic` in a
		/// view fails at render time ("'object' does not contain a definition for 'ID'").
		public sealed record HospitalChoice(int Id, string Name, string? NameEn, string? Label = null);

		// ---------------- practitioners ----------------

		[HospitalReceptionAllowed]   // H156: the reception desk works here (administrative)
		[HttpGet("Practitioners")]
		[HospitalPerm(HospitalActions.Read)]
		public async Task<IActionResult> Practitioners(CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return RedirectToAction("Index", "Home");
			Chrome();
			ViewBag.CanManage = await _access.CanAsync(ctx, HospitalActions.Manage, null, ct);
			ViewBag.Departments = await _outpatient.DepartmentsAsync(ctx, ct);
			ViewBag.Today = ClinicToday();
			return View(await _outpatient.PractitionersAsync(ctx, ct));
		}

		[HospitalReceptionAllowed]   // H156: the reception desk works here (administrative)
		[HttpGet("Practitioner/{id:int}")]
		[HospitalPerm(HospitalActions.Read)]
		public async Task<IActionResult> Practitioner(int id, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return RedirectToAction("Index", "Home");
			var p = await _outpatient.PractitionerAsync(ctx, id, ct);
			if (p == null) return NotFound();
			Chrome();
			ViewBag.CanManage = await _access.CanAsync(ctx, HospitalActions.Manage, null, ct);
			ViewBag.Today = ClinicToday();
			ViewBag.Day = (await _outpatient.DayAsync(ctx, ClinicToday(), ct)).Where(a => a.PractitionerId == id).ToList();
			// H19: his fee rules, and the services a rule may name
			ViewBag.FeeRules = await FeeSvc.RulesAsync(ctx, id, ct);
			ViewBag.RuleServices = (await _billing.ServicesAsync(ctx, null, true, ct)).Select(s => new HospitalChoice(s.ID, s.Name, s.NameEn, s.Code + "|" + s.Category)).ToList();
			// H26: his weekly schedule and leave
			ViewBag.Schedule = await ScheduleSvc.ScheduleAsync(ctx, id, ClinicToday(), ct);
			return View(p);
		}

		[HospitalReceptionAllowed]   // H156: the reception desk works here (administrative)
		[HttpGet("Practitioners/New")]
		[HospitalPerm(HospitalActions.Manage)]
		public async Task<IActionResult> NewPractitioner(CancellationToken ct = default) => await PractitionerFormAsync(null, ct);

		[HospitalReceptionAllowed]   // H156: the reception desk works here (administrative)
		[HttpGet("Practitioner/{id:int}/Edit")]
		[HospitalPerm(HospitalActions.Manage)]
		public async Task<IActionResult> EditPractitioner(int id, CancellationToken ct = default) => await PractitionerFormAsync(id, ct);

		private async Task<IActionResult> PractitionerFormAsync(int? id, CancellationToken ct)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return RedirectToAction("Index", "Home");
			HospitalPractitionerView? p = null;
			if (id is int i) { p = await _outpatient.PractitionerAsync(ctx, i, ct); if (p == null) return NotFound(); }
			Chrome();
			ViewBag.Departments = await _outpatient.DepartmentsAsync(ctx, ct);
			ViewBag.ClinicRooms = await _db.HospitalRooms.AsNoTracking()
				.Where(r => r.CompanyId == ctx.CompanyId && r.IsActive && r.Kind == HospitalRoomKinds.Clinic).OrderBy(r => r.Number)
				.Select(r => new HospitalChoice(r.ID, r.Name ?? "", r.NameEn, r.Number)).ToListAsync(ct);
			ViewBag.Employees = await _db.Employee.AsNoTracking().Where(e => e.EmpCompanyID == ctx.CompanyId)
				.OrderBy(e => e.FullName).Select(e => new HospitalChoice(e.ID, e.FullName, e.FullNameEn, null)).Take(1000).ToListAsync(ct);
			ViewBag.FeeServices = (await _billing.ServicesAsync(ctx, HospitalServiceCategories.Consultation, true, ct))
				.Select(s => new HospitalChoice(s.ID, s.Name, s.NameEn, s.Code)).ToList();
			return View("PractitionerEdit", p);
		}

		[HospitalReceptionAllowed]   // H156: the reception desk works here (administrative)
		[HttpPost("Practitioners/Save"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Manage)]
		public async Task<IActionResult> SavePractitioner([FromBody] HospitalPractitionerInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await _outpatient.SavePractitionerAsync(ctx, input ?? new(), ctx.EmployeeId, ct));
		}

		[HospitalReceptionAllowed]   // H156: the reception desk works here (administrative)
		[HttpPost("Departments/SeedDefaults"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Manage)]
		public async Task<IActionResult> SeedDefaultDepartments(CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await _outpatient.SeedDefaultDepartmentsAsync(ctx, ctx.EmployeeId, ct));
		}

		// ---------------- appointments ----------------

		[HospitalReceptionAllowed]   // H156: the reception desk works here (administrative)
		[HttpGet("Appointments")]
		[HospitalPerm(HospitalActions.Read)]
		public async Task<IActionResult> Appointments(DateTime? date, int? department, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return RedirectToAction("Index", "Home");
			Chrome();
			var day = (date ?? ClinicToday()).Date;
			ViewBag.Date = day;
			ViewBag.Today = ClinicToday();
			ViewBag.Department = department;
			ViewBag.Departments = await _outpatient.DepartmentsAsync(ctx, ct);
			ViewBag.CanBook = await _access.CanAsync(ctx, HospitalActions.Book, null, ct);
			var appts = await _outpatient.DayAsync(ctx, day, ct);
			ViewBag.Appointments = appts;
			// H26: who works that day is the schedule's answer (weekly sessions, leave); a doctor who has appointments
			// that day still shows, so nothing booked disappears from the grid
			var all = (await _outpatient.PractitionersAsync(ctx, ct)).Where(p => department == null || p.Row.DepartmentId == department).ToList();
			var schedules = await HospitalScheduleLoader.LoadAsync(_db, ctx.CompanyId, all.Select(p => p.Row).ToList(), day, day, ct);
			ViewBag.Schedules = schedules;
			var docs = all.Where(p => schedules[p.Row.ID].WorksOn(day) || appts.Any(a => a.PractitionerId == p.Row.ID)).ToList();
			return View(docs);
		}

		/// The booking modal's calendar: for one doctor, the days ahead on which he has a FREE slot and those slots —
		/// his schedule (weekly sessions less leave, H26) minus what is already booked (anything not cancelled), and on
		/// today minus the times already past. The calendar enables only these days; the time list shows only these slots.
		/// BookAsync still refuses a taken slot, so this is a guide for the picker, never the authority.
		[HospitalReceptionAllowed]   // H156: the reception desk works here (administrative)
		[HttpGet("Appointments/Availability")]
		[HospitalPerm(HospitalActions.Book)]
		public async Task<IActionResult> AppointmentAvailability(int practitionerId, int days = 60, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return NotFound(new { ok = false, code = "not_found" });
			var doc = await _db.HospitalPractitioners.AsNoTracking().FirstOrDefaultAsync(p => p.ID == practitionerId && p.CompanyId == ctx.CompanyId, ct);
			if (doc == null) return NotFound(new { ok = false, code = "not_found" });
			var from = ClinicToday(); var to = from.AddDays(Math.Clamp(days, 1, 120));
			var sched = (await HospitalScheduleLoader.LoadAsync(_db, ctx.CompanyId, new[] { doc }, from, to, ct))[doc.ID];
			var taken = (await _db.HospitalAppointments.AsNoTracking()
					.Where(a => a.CompanyId == ctx.CompanyId && a.PractitionerId == doc.ID && a.AppointmentDate >= from && a.AppointmentDate <= to && a.Status != HospitalAppointmentStatus.Cancelled)
					.Select(a => new { a.AppointmentDate, a.StartMinute }).ToListAsync(ct))
				.Select(a => (a.AppointmentDate.Date, a.StartMinute)).ToHashSet();
			int nowMinute = ClinicMinute();
			var result = new Dictionary<string, List<int>>();
			for (var d = from; d <= to; d = d.AddDays(1))
			{
				var free = sched.SlotsOn(d).Select(s => s.Minute).Where(m => !taken.Contains((d, m)) && (d > from || m > nowMinute)).ToList();
				if (free.Count > 0) result[d.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture)] = free;
			}
			return Json(new { ok = true, days = result });
		}

		[HospitalReceptionAllowed]   // H156: the reception desk works here (administrative)
		[HttpPost("Appointments/Book"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Book)]
		public async Task<IActionResult> BookAppointment([FromBody] HospitalBookingInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await _outpatient.BookAsync(ctx, input ?? new(), ctx.EmployeeId, ClinicToday(), ct));
		}

		[HospitalReceptionAllowed]   // H156: the reception desk works here (administrative)
		[HttpPost("Appointments/{id:int}/Move"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Book)]
		public async Task<IActionResult> MoveAppointment(int id, [FromBody] HospitalMoveInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return NotFound(new { ok = false, code = "not_found" });
			var r = await _outpatient.MoveAsync(ctx, id, input?.To ?? "", ctx.EmployeeId, DateTime.UtcNow, ct);
			// H48: a cancelled time goes down the earlier-slot waitlist at once
			if (r.Ok && input?.To == Models.Context.Hospital.HospitalAppointmentStatus.Cancelled) await WaitlistSvc.RunAsync(ctx.CompanyId, WaitlistMail, DateTime.Now, DateTime.UtcNow, null, ct);
			return Result(r);
		}

		// ---------------- clinics board ----------------

		[HospitalReceptionAllowed]   // H156: the reception desk works here (administrative)
		[HttpGet("Clinics")]
		[HospitalPerm(HospitalActions.Read)]
		public async Task<IActionResult> Clinics(CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return RedirectToAction("Index", "Home");
			Chrome();
			ViewBag.CanBook = await _access.CanAsync(ctx, HospitalActions.Book, null, ct);
			ViewBag.Today = ClinicToday();
			return View(await _outpatient.BoardAsync(ctx, ClinicToday(), ClinicMinute(), DateTime.UtcNow, ct));
		}

		[HospitalReceptionAllowed]   // H156: the reception desk works here (administrative)
		[HttpPost("Clinics/{practitionerId:int}/CallNext"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Book)]
		public async Task<IActionResult> CallNext(int practitionerId, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await _outpatient.CallNextAsync(ctx, practitionerId, ClinicToday(), ctx.EmployeeId, DateTime.UtcNow, ct));
		}
	}
}
