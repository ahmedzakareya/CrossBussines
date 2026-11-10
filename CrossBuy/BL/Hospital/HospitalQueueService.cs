using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Hospital;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL.Hospital
{
	// ==========================================================================================
	// HOSPITAL H83 — THE WAITING-ROOM DISPLAY AND THE SELF CHECK-IN KIOSK (phase 3 of the 2026-11-09 gap research). Both
	// read the outpatient day that H4 already keeps — the queue number given on arrival, the call that puts a patient in
	// the room — so nothing new is stored.
	//   * The DISPLAY (a screen in the waiting room) shows, per clinic, the number now being seen and its room, and the
	//     numbers waiting. It never shows a name: a number is enough, and the waiting room is public.
	//   * The KIOSK lets a patient with an appointment today check himself in: his file number and his year of birth find
	//     the appointment, a second tap confirms, and he gets his queue number — exactly what the desk's "arrived" does.
	// ==========================================================================================
	public sealed record HospitalQueueClinic(int PractitionerId, string Doctor, string DoctorEn, string? Department, string? DepartmentEn, string? Room,
		int? NowServing, DateTime? CalledAt, IReadOnlyList<int> Waiting);

	/// The display's state; Stamp changes whenever a new number is called (the screen chimes on a change).
	public sealed record HospitalQueueBoard(IReadOnlyList<HospitalQueueClinic> Clinics, string Stamp, DateTime NowLocal);

	public sealed record HospitalKioskAppointment(int Id, string Doctor, string DoctorEn, string? Department, string? DepartmentEn, int StartMinute, string Status, int? QueueNumber, bool TooEarly);

	public static class HospitalKioskRules
	{
		/// A patient can check in from an hour before the slot.
		public const int EarlyMinutes = 60;

		public static string? FindRefusal(string? mrn, int? birthYear, int thisYear)
		{
			if (string.IsNullOrWhiteSpace(mrn)) return "Enter your file number";
			if (birthYear is null || birthYear < thisYear - 130 || birthYear > thisYear) return "Enter your year of birth";
			return null;
		}

		/// Identity: the file number exists and its birth year matches. A file with no birth date on record cannot check in
		/// at the kiosk — the desk does it. The answer for a wrong year is the same as for a wrong number (no file probing).
		public static string? IdentityRefusal(bool found, DateTime? birthDate, int birthYear)
		{
			if (!found || birthDate == null || birthDate.Value.Year != birthYear) return "We could not find your appointment: please go to the desk";
			return null;
		}

		public static bool TooEarly(int startMinute, int nowMinute) => nowMinute < startMinute - EarlyMinutes;

		/// A different number was called since the screen's last look: chime.
		public static string Stamp(IEnumerable<(int Practitioner, int? Number, DateTime? CalledAt)> serving) =>
			string.Join(";", serving.Where(s => s.Number != null).OrderBy(s => s.Practitioner).Select(s => s.Practitioner + ":" + s.Number + "@" + s.CalledAt?.Ticks));
	}

	public sealed class HospitalQueueService
	{
		private readonly CrossDbContext _db;
		private readonly IHospitalAccessService _access;
		public HospitalQueueService(CrossDbContext db, IHospitalAccessService access) { _db = db; _access = access; }

		/// The day's clinics that have patients (optionally one department), each with the number in the room and those waiting.
		public async Task<HospitalQueueBoard> BoardAsync(BusinessContext ctx, DateTime day, int? departmentId, DateTime nowUtc, CancellationToken ct = default)
		{
			var d = day.Date;
			var appts = await _db.HospitalAppointments.AsNoTracking()
				.Where(a => a.CompanyId == ctx.CompanyId && a.AppointmentDate == d && (a.Status == HospitalAppointmentStatus.Arrived || a.Status == HospitalAppointmentStatus.InRoom))
				.Select(a => new { a.PractitionerId, a.Status, a.QueueNumber, a.StartedAt }).ToListAsync(ct);
			var docIds = appts.Select(a => a.PractitionerId).Distinct().ToList();
			var docs = await (from p in _db.HospitalPractitioners.AsNoTracking()
							  join dep in _db.HospitalDepartments.AsNoTracking() on p.DepartmentId equals dep.ID into dj
							  from dep in dj.DefaultIfEmpty()
							  join r in _db.HospitalRooms.AsNoTracking() on p.ClinicRoomId equals r.ID into rj
							  from r in rj.DefaultIfEmpty()
							  where p.CompanyId == ctx.CompanyId && docIds.Contains(p.ID) && (departmentId == null || p.DepartmentId == departmentId)
							  select new { p.ID, p.Name, p.NameEn, Dep = dep == null ? null : dep.Name, DepEn = dep == null ? null : dep.NameEn, Room = r == null ? null : r.Number }).ToListAsync(ct);
			var clinics = docs.Select(p =>
			{
				var mine = appts.Where(a => a.PractitionerId == p.ID).ToList();
				var now = mine.FirstOrDefault(a => a.Status == HospitalAppointmentStatus.InRoom);
				var waiting = mine.Where(a => a.Status == HospitalAppointmentStatus.Arrived && a.QueueNumber != null).Select(a => a.QueueNumber!.Value).OrderBy(n => n).ToList();
				return new HospitalQueueClinic(p.ID, p.Name, p.NameEn, p.Dep, p.DepEn, p.Room, now?.QueueNumber, now?.StartedAt, waiting);
			}).OrderBy(c => c.DepartmentEn).ThenBy(c => c.DoctorEn).ToList();
			return new HospitalQueueBoard(clinics, HospitalKioskRules.Stamp(clinics.Select(c => (c.PractitionerId, c.NowServing, c.CalledAt))), HospitalNursingService.UtcToWard(nowUtc));
		}

		private async Task<(HospitalPatient? Patient, string? Why)> IdentifyAsync(BusinessContext ctx, string? mrn, int? birthYear, DateTime nowLocal, CancellationToken ct)
		{
			if (HospitalKioskRules.FindRefusal(mrn, birthYear, nowLocal.Year) is string why) return (null, why);
			var m = mrn!.Trim();
			var p = await _db.HospitalPatients.AsNoTracking().FirstOrDefaultAsync(x => x.CompanyId == ctx.CompanyId && x.Mrn == m, ct);
			if (HospitalKioskRules.IdentityRefusal(p != null, p?.BirthDate, birthYear!.Value) is string no) return (null, no);
			return (p, null);
		}

		/// The patient's appointments of today that the kiosk can act on (booked, or already arrived — his number again).
		public async Task<(IReadOnlyList<HospitalKioskAppointment> Rows, string? Name, string? Why)> FindAsync(BusinessContext ctx, string? mrn, int? birthYear, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await _access.CanAsync(ctx, HospitalActions.Book, null, ct)) return (Array.Empty<HospitalKioskAppointment>(), null, "forbidden");
			var nowLocal = HospitalNursingService.UtcToWard(nowUtc);
			var (p, why) = await IdentifyAsync(ctx, mrn, birthYear, nowLocal, ct);
			if (p == null) return (Array.Empty<HospitalKioskAppointment>(), null, why);
			int nowMinute = nowLocal.Hour * 60 + nowLocal.Minute;
			var day = nowLocal.Date;
			var rows = await (from a in _db.HospitalAppointments.AsNoTracking()
							  join d in _db.HospitalPractitioners.AsNoTracking() on a.PractitionerId equals d.ID
							  join dep in _db.HospitalDepartments.AsNoTracking() on d.DepartmentId equals dep.ID into dj
							  from dep in dj.DefaultIfEmpty()
							  where a.CompanyId == ctx.CompanyId && a.PatientId == p.ID && a.AppointmentDate == day
								&& (a.Status == HospitalAppointmentStatus.Booked || a.Status == HospitalAppointmentStatus.Arrived)
							  orderby a.StartMinute
							  select new { a.ID, d.Name, d.NameEn, Dep = dep == null ? null : dep.Name, DepEn = dep == null ? null : dep.NameEn, a.StartMinute, a.Status, a.QueueNumber }).ToListAsync(ct);
			if (rows.Count == 0) return (Array.Empty<HospitalKioskAppointment>(), null, "We could not find your appointment: please go to the desk");
			return (rows.Select(r => new HospitalKioskAppointment(r.ID, r.Name, r.NameEn, r.Dep, r.DepEn, r.StartMinute, r.Status, r.QueueNumber,
				r.Status == HospitalAppointmentStatus.Booked && HospitalKioskRules.TooEarly(r.StartMinute, nowMinute))).ToList(), DisplayName.Of(p.Name, p.NameEn), null);
		}

		/// The second tap: the identity is checked again against THIS appointment, then the desk's own "arrived" runs.
		public async Task<(HospitalResult Result, int? Queue)> CheckInAsync(BusinessContext ctx, HospitalOutpatientService outpatient, int appointmentId, string? mrn, int? birthYear, int? employeeId, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await _access.CanAsync(ctx, HospitalActions.Book, null, ct)) return (HospitalResult.Fail("forbidden"), null);
			var nowLocal = HospitalNursingService.UtcToWard(nowUtc);
			var (p, why) = await IdentifyAsync(ctx, mrn, birthYear, nowLocal, ct);
			if (p == null) return (HospitalResult.Fail(why!), null);
			var a = await _db.HospitalAppointments.AsNoTracking().FirstOrDefaultAsync(x => x.ID == appointmentId && x.CompanyId == ctx.CompanyId && x.PatientId == p.ID && x.AppointmentDate == nowLocal.Date, ct);
			if (a == null) return (HospitalResult.Fail("We could not find your appointment: please go to the desk"), null);
			if (a.Status == HospitalAppointmentStatus.Arrived) return (HospitalResult.Success(a.ID), a.QueueNumber);   // already in: his number again
			if (a.Status != HospitalAppointmentStatus.Booked) return (HospitalResult.Fail("This appointment cannot be checked in here: please go to the desk"), null);
			if (HospitalKioskRules.TooEarly(a.StartMinute, nowLocal.Hour * 60 + nowLocal.Minute)) return (HospitalResult.Fail("It is too early: check in from an hour before your appointment"), null);
			var r = await outpatient.MoveAsync(ctx, a.ID, HospitalAppointmentStatus.Arrived, employeeId, nowUtc, ct);
			if (!r.Ok) return (r, null);
			var q = await _db.HospitalAppointments.AsNoTracking().Where(x => x.ID == a.ID).Select(x => x.QueueNumber).FirstAsync(ct);
			return (r, q);
		}
	}
}
