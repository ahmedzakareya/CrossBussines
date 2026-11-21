using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Hospital;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL.Hospital
{
	// ==========================================================================================
	// HOSPITAL H214 — video visits (telehealth). A booked appointment is made a VIDEO VISIT by the clinic; the doctor
	// opens it from the worklist and the patient from the portal, and the two see and hear each other peer to peer
	// (WebRTC: the media never passes through the server and nothing is recorded). The server only passes the call's
	// set-up messages between the two sides, by short polling — no socket hub to register.
	//   · The patient may join from 15 minutes before the appointment until 2 hours after it; the doctor any time that
	//     day. Neither joins an ended visit.
	//   · A side reads only the other side's messages; a payload is at most 20 000 characters; only the five kinds of
	//     set-up message pass.
	//   · The doctor ends the visit; the appointment's clinical record is the consultation as for any visit.
	// ==========================================================================================
	public sealed record HospitalTeleRoom(HospitalTeleVisit Visit, string Patient, string PatientEn, string Mrn, string Doctor, string DoctorEn, DateTime StartsAt, string AppointmentStatus, HospitalFace? DoctorFace = null, string? PatientPhoto = null);

	public static class HospitalTeleRules
	{
		public const int EarlyMinutes = 15, LateHours = 2, MaxPayload = 20000;

		public static string? PatientJoinRefusal(HospitalTeleVisit v, DateTime startsAtLocal, DateTime nowLocal)
		{
			if (v.Status == HospitalTeleStatus.Ended) return "This video visit has ended";
			if (nowLocal < startsAtLocal.AddMinutes(-EarlyMinutes)) return "The video visit opens 15 minutes before its time";
			return nowLocal > startsAtLocal.AddHours(LateHours) ? "This video visit's time has passed" : null;
		}

		public static string? DoctorJoinRefusal(HospitalTeleVisit v, DateTime startsAtLocal, DateTime nowLocal)
		{
			if (v.Status == HospitalTeleStatus.Ended) return "This video visit has ended";
			return startsAtLocal.Date != nowLocal.Date ? "A video visit is held on its own day" : null;
		}

		public static string? SignalRefusal(HospitalTeleVisit v, string? kind, string? payload)
		{
			if (v.Status == HospitalTeleStatus.Ended) return "This video visit has ended";
			if (string.IsNullOrEmpty(kind) || !HospitalTeleSignalKind.All.Contains(kind)) return "not_found";
			return (payload?.Length ?? 0) > MaxPayload ? "The message is too long" : null;
		}

		public static string Other(string side) => side == HospitalTeleSide.Doctor ? HospitalTeleSide.Patient : HospitalTeleSide.Doctor;
	}

	public sealed class HospitalTeleVisitService
	{
		private readonly CrossDbContext _db;
		public HospitalTeleVisitService(CrossDbContext db) { _db = db; }

		private IQueryable<HospitalTeleVisit> Visits(int companyId) => _db.Set<HospitalTeleVisit>().Where(x => x.CompanyId == companyId);

		/// Makes the appointment a video visit (once; the same visit comes back after). Booked or checked-in only.
		public async Task<HospitalResult> CreateAsync(BusinessContext ctx, int appointmentId, int? employeeId, DateTime nowUtc, CancellationToken ct = default)
		{
			var a = await _db.HospitalAppointments.AsNoTracking().FirstOrDefaultAsync(x => x.ID == appointmentId && x.CompanyId == ctx.CompanyId, ct);
			if (a == null) return HospitalResult.Fail("not_found");
			var have = await Visits(ctx.CompanyId).AsNoTracking().FirstOrDefaultAsync(x => x.AppointmentId == appointmentId, ct);
			if (have != null) return HospitalResult.Success(have.ID);
			if (a.Status is not (HospitalAppointmentStatus.Booked or HospitalAppointmentStatus.Arrived or HospitalAppointmentStatus.InRoom)) return HospitalResult.Fail("Only a booked appointment becomes a video visit");
			var v = new HospitalTeleVisit { CompanyId = ctx.CompanyId, AppointmentId = a.ID, PatientId = a.PatientId, PractitionerId = a.PractitionerId, Status = HospitalTeleStatus.Scheduled, CreatedAt = nowUtc, CreatedByEmployeeId = employeeId };
			_db.Add(v);
			await _db.SaveChangesAsync(ct);
			return HospitalResult.Success(v.ID);
		}

		public async Task<HospitalTeleRoom?> RoomAsync(int companyId, int visitId, CancellationToken ct = default)
		{
			var r = await (from v in Visits(companyId).AsNoTracking()
						   join a in _db.HospitalAppointments.AsNoTracking() on v.AppointmentId equals a.ID
						   join p in _db.HospitalPatients.AsNoTracking() on v.PatientId equals p.ID
						   join d in _db.HospitalPractitioners.AsNoTracking() on v.PractitionerId equals d.ID
						   where v.ID == visitId && a.CompanyId == companyId && p.CompanyId == companyId
						   select new { v, a.AppointmentDate, a.StartMinute, a.Status, p.Name, p.NameEn, p.Mrn, p.PhotoPath, Doc = d.Name, DocEn = d.NameEn }).FirstOrDefaultAsync(ct);
			if (r == null) return null;
			var face = (await HospitalFaces.OfPractitionersAsync(_db, companyId, new int?[] { r.v.PractitionerId }, ct)).GetValueOrDefault(r.v.PractitionerId);
			return new HospitalTeleRoom(r.v, r.Name, r.NameEn, r.Mrn, r.Doc, r.DocEn, r.AppointmentDate.Date.AddMinutes(r.StartMinute), r.Status, face, r.PhotoPath);
		}

		/// The video visits of these appointments (for the portal's list and the worklist).
		public async Task<Dictionary<int, HospitalTeleVisit>> ForAppointmentsAsync(int companyId, IReadOnlyCollection<int> appointmentIds, CancellationToken ct = default)
		{
			try { return await Visits(companyId).AsNoTracking().Where(v => appointmentIds.Contains(v.AppointmentId)).ToDictionaryAsync(v => v.AppointmentId, ct); }
			catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number is 207 or 208) { return new(); }
		}

		/// A side opens the room: refused outside its window; the first join is stamped and the visit goes live.
		public async Task<HospitalResult> JoinAsync(int companyId, int visitId, string side, DateTime nowUtc, DateTime nowLocal, CancellationToken ct = default)
		{
			var room = await RoomAsync(companyId, visitId, ct);
			if (room == null) return HospitalResult.Fail("not_found");
			var why = side == HospitalTeleSide.Patient ? HospitalTeleRules.PatientJoinRefusal(room.Visit, room.StartsAt, nowLocal) : HospitalTeleRules.DoctorJoinRefusal(room.Visit, room.StartsAt, nowLocal);
			if (why != null) return HospitalResult.Fail(why);
			var q = Visits(companyId).Where(v => v.ID == visitId);
			if (side == HospitalTeleSide.Doctor) await q.Where(v => v.DoctorJoinedAt == null).ExecuteUpdateAsync(u => u.SetProperty(v => v.DoctorJoinedAt, nowUtc), ct);
			else await q.Where(v => v.PatientJoinedAt == null).ExecuteUpdateAsync(u => u.SetProperty(v => v.PatientJoinedAt, nowUtc), ct);
			await q.Where(v => v.Status == HospitalTeleStatus.Scheduled).ExecuteUpdateAsync(u => u.SetProperty(v => v.Status, HospitalTeleStatus.Live), ct);
			return HospitalResult.Success(visitId);
		}

		public async Task<HospitalResult> SignalAsync(int companyId, int visitId, string side, string? kind, string? payload, DateTime nowUtc, CancellationToken ct = default)
		{
			var v = await Visits(companyId).AsNoTracking().FirstOrDefaultAsync(x => x.ID == visitId, ct);
			if (v == null) return HospitalResult.Fail("not_found");
			if (HospitalTeleRules.SignalRefusal(v, kind, payload) is string why) return HospitalResult.Fail(why);
			_db.Add(new HospitalTeleSignal { CompanyId = companyId, VisitId = visitId, FromSide = side, Kind = kind!, Payload = payload, At = nowUtc });
			await _db.SaveChangesAsync(ct);
			return HospitalResult.Success(visitId);
		}

		/// The other side's messages after the last one this side has read.
		public async Task<IReadOnlyList<HospitalTeleSignal>> PollAsync(int companyId, int visitId, string side, long after, CancellationToken ct = default)
		{
			string other = HospitalTeleRules.Other(side);
			return await _db.Set<HospitalTeleSignal>().AsNoTracking().Where(s => s.CompanyId == companyId && s.VisitId == visitId && s.FromSide == other && s.ID > after)
				.OrderBy(s => s.ID).Take(50).ToListAsync(ct);
		}

		public async Task<HospitalResult> EndAsync(int companyId, int visitId, int? employeeId, DateTime nowUtc, CancellationToken ct = default)
		{
			var v = await Visits(companyId).AsNoTracking().FirstOrDefaultAsync(x => x.ID == visitId, ct);
			if (v == null) return HospitalResult.Fail("not_found");
			if (v.Status == HospitalTeleStatus.Ended) return HospitalResult.Fail("This video visit has ended");
			_db.Add(new HospitalTeleSignal { CompanyId = companyId, VisitId = visitId, FromSide = HospitalTeleSide.Doctor, Kind = HospitalTeleSignalKind.Bye, At = nowUtc });
			await _db.SaveChangesAsync(ct);
			await Visits(companyId).Where(x => x.ID == visitId).ExecuteUpdateAsync(u => u.SetProperty(x => x.Status, HospitalTeleStatus.Ended).SetProperty(x => x.EndedAt, nowUtc).SetProperty(x => x.EndedByEmployeeId, employeeId), ct);
			return HospitalResult.Success(visitId);
		}
	}
}
