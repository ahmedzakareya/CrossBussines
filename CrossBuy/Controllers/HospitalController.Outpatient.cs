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
			return View(p);
		}

		[HttpGet("Practitioners/New")]
		[HospitalPerm(HospitalActions.Manage)]
		public async Task<IActionResult> NewPractitioner(CancellationToken ct = default) => await PractitionerFormAsync(null, ct);

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

		[HttpPost("Practitioners/Save"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Manage)]
		public async Task<IActionResult> SavePractitioner([FromBody] HospitalPractitionerInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await _outpatient.SavePractitionerAsync(ctx, input ?? new(), ctx.EmployeeId, ct));
		}

		[HttpPost("Departments/SeedDefaults"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Manage)]
		public async Task<IActionResult> SeedDefaultDepartments(CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await _outpatient.SeedDefaultDepartmentsAsync(ctx, ctx.EmployeeId, ct));
		}

		// ---------------- appointments ----------------

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
			ViewBag.Appointments = await _outpatient.DayAsync(ctx, day, ct);
			var docs = (await _outpatient.PractitionersAsync(ctx, ct))
				.Where(p => p.Row.Days().Contains(day.DayOfWeek) && (department == null || p.Row.DepartmentId == department)).ToList();
			return View(docs);
		}

		[HttpPost("Appointments/Book"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Book)]
		public async Task<IActionResult> BookAppointment([FromBody] HospitalBookingInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await _outpatient.BookAsync(ctx, input ?? new(), ctx.EmployeeId, ClinicToday(), ct));
		}

		[HttpPost("Appointments/{id:int}/Move"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Book)]
		public async Task<IActionResult> MoveAppointment(int id, [FromBody] HospitalMoveInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await _outpatient.MoveAsync(ctx, id, input?.To ?? "", ctx.EmployeeId, DateTime.UtcNow, ct));
		}

		// ---------------- clinics board ----------------

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

		[HttpPost("Clinics/{practitionerId:int}/CallNext"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Book)]
		public async Task<IActionResult> CallNext(int practitionerId, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await _outpatient.CallNextAsync(ctx, practitionerId, ClinicToday(), ctx.EmployeeId, DateTime.UtcNow, ct));
		}
	}
}
