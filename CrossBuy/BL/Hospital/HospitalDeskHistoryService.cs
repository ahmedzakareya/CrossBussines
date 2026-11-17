using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Hospital;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL.Hospital
{
	// ==========================================================================================
	// HOSPITAL H154 — the patient's history as the reception desk needs it: ADMINISTRATIVE, never clinical. Every clinic
	// visit (with whom, which clinic, came / did not come / cancelled), every stay (from when to when, which ward), every
	// emergency visit (when, how it ended), every operation (when, which, done or cancelled) — newest first, with the
	// counts. No diagnosis, result, note or medicine: those are the care team's, in the file. A confidential file's
	// history is not the desk's to give (as its whereabouts, H114).
	// ==========================================================================================

	public static class HospitalDeskEventKind
	{
		public const string Visit = "Visit", Admission = "Admission", Emergency = "Emergency", Operation = "Operation";
	}

	/// One event: what, when (until when, for a stay), where / with whom (both languages) and its state code.
	public sealed record HospitalDeskEvent(string Kind, DateTime At, DateTime? Until, string Title, string? TitleEn, string? Detail, string? DetailEn, string State, string? Photo = null);
	public sealed record HospitalDeskHistory(bool Restricted, int Visits, int NoShows, int Admissions, int EmergencyVisits, int Operations, DateTime? FirstSeen, IReadOnlyList<HospitalDeskEvent> Recent);

	public sealed class HospitalDeskHistoryService
	{
		private readonly CrossDbContext _db;
		public HospitalDeskHistoryService(CrossDbContext db) { _db = db; }

		public async Task<HospitalDeskHistory> HistoryAsync(BusinessContext ctx, int patientId, int take, DateTime todayLocal, CancellationToken ct = default)
		{
			int cid = ctx.CompanyId;
			if (await _db.Set<HospitalRestrictedFile>().AsNoTracking().AnyAsync(r => r.CompanyId == cid && r.PatientId == patientId && r.LiftedAt == null, ct))
				return new HospitalDeskHistory(true, 0, 0, 0, 0, 0, null, Array.Empty<HospitalDeskEvent>());
			var events = new List<HospitalDeskEvent>();

			// clinic visits: past days, and today's that ended (seen, no-show or cancelled)
			var appts = await (from a in _db.HospitalAppointments.AsNoTracking()
							   join d in _db.HospitalPractitioners.AsNoTracking() on a.PractitionerId equals d.ID
							   join dep0 in _db.HospitalDepartments.AsNoTracking() on d.DepartmentId equals dep0.ID into dj
							   from dep in dj.DefaultIfEmpty()
							   where a.CompanyId == cid && a.PatientId == patientId && (a.AppointmentDate < todayLocal
								   || a.Status == HospitalAppointmentStatus.Done || a.Status == HospitalAppointmentStatus.NoShow || a.Status == HospitalAppointmentStatus.Cancelled)
							   select new { a.AppointmentDate, a.StartMinute, a.Status, Doctor = d.Name, DoctorEn = d.NameEn, Dept = (string?)dep.Name, DeptEn = dep.NameEn, d.EmployeeId }).ToListAsync(ct);
			// the doctor is shown by face, like everywhere else on the desk
			var photos = await CrossBuy.BL.Platform.EmployeePhotos.ResolveAsync(_db, ctx, appts.Where(x => x.EmployeeId != null).Select(x => x.EmployeeId!.Value).Distinct(), ct);
			foreach (var a in appts)
				events.Add(new HospitalDeskEvent(HospitalDeskEventKind.Visit, a.AppointmentDate.AddMinutes(a.StartMinute), null, a.Doctor, a.DoctorEn, a.Dept, a.DeptEn,
					a.Status == HospitalAppointmentStatus.Booked ? HospitalAppointmentStatus.NoShow : a.Status,   // a past day still "booked" was never attended
					a.EmployeeId is int de ? photos.GetValueOrDefault(de) : null));   // a past day still "booked" was never attended

			// stays: from admission to discharge, with the ward of the first bed
			var stays = await _db.HospitalAdmissions.AsNoTracking().Where(a => a.CompanyId == cid && a.PatientId == patientId).ToListAsync(ct);
			var stayIds = stays.Select(s => s.ID).ToList();
			var wards = await (from s in _db.HospitalBedStays.AsNoTracking()
							   join r in _db.HospitalRooms.AsNoTracking() on s.RoomId equals r.ID
							   join w in _db.HospitalWards.AsNoTracking() on r.WardId equals w.ID
							   where s.CompanyId == cid && stayIds.Contains(s.AdmissionId)
							   orderby s.StartedAt
							   select new { s.AdmissionId, w.Name, w.NameEn }).ToListAsync(ct);
			foreach (var s in stays)
			{
				var w = wards.FirstOrDefault(x => x.AdmissionId == s.ID);
				events.Add(new HospitalDeskEvent(HospitalDeskEventKind.Admission, s.AdmittedAt, s.DischargedAt, w?.Name ?? "", w?.NameEn, null, null, s.Status));
			}

			int er = 0, ops = 0;
			try
			{
				var erVisits = await _db.Set<HospitalErVisit>().AsNoTracking().Where(v => v.CompanyId == cid && v.PatientId == patientId)
					.Select(v => new { v.VisitNo, v.ArrivedAt, v.ClosedAt, v.Stage, v.Outcome }).ToListAsync(ct);
				er = erVisits.Count;
				foreach (var v in erVisits)
					events.Add(new HospitalDeskEvent(HospitalDeskEventKind.Emergency, v.ArrivedAt, v.ClosedAt, v.VisitNo, v.VisitNo, null, null, v.Stage == HospitalErStage.Closed ? v.Outcome ?? HospitalErStage.Closed : v.Stage));
				var cases = await _db.Set<HospitalSurgery>().AsNoTracking().Where(s => s.CompanyId == cid && s.PatientId == patientId)
					.Select(s => new { s.CaseDate, s.StartMinute, s.Name, s.NameEn, s.Status }).ToListAsync(ct);
				ops = cases.Count(c => c.Status == HospitalSurgeryStatus.Done);
				foreach (var c in cases)
					events.Add(new HospitalDeskEvent(HospitalDeskEventKind.Operation, c.CaseDate.AddMinutes(c.StartMinute), null, c.Name, c.NameEn, null, null, c.Status));
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number == 208) { }   // a module not installed answers nothing

			var ordered = events.OrderByDescending(e => e.At).ToList();
			return new HospitalDeskHistory(false,
				appts.Count(a => a.Status == HospitalAppointmentStatus.Done), events.Count(e => e.Kind == HospitalDeskEventKind.Visit && e.State == HospitalAppointmentStatus.NoShow),
				stays.Count, er, ops, ordered.Count == 0 ? null : ordered[^1].At, ordered.Take(Math.Clamp(take, 1, 50)).ToList());
		}
	}
}
