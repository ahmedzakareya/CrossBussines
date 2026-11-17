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
	// HOSPITAL — the hospital map (the prototype's #/map): every floor as the admin drew it, each space a
	// tile coloured by its kind and showing who is inside NOW — the patients in a ward room's beds, and in a
	// clinic its doctor, the patient in the consultation and how many wait. Read-only; one screen, floors
	// switch in place.
	// ==========================================================================================
	public partial class HospitalController
	{
		/// A person on the map (a named record: the view never reads an anonymous type through dynamic).
		public sealed record HospitalMapPerson(int Id, string Name, string NameEn, string? Photo, string? Sub, string? Url);
		/// A clinic's doctor today: who is with them and how many wait.
		public sealed record HospitalMapClinic(HospitalMapPerson Doctor, HospitalMapPerson? InRoom, int Waiting);
		/// A patient in a theatre, an imaging room or the emergency department now: what is being done, where, and a badge (ESI).
		public sealed record HospitalMapCase(HospitalMapPerson Patient, string What, string? WhatEn, string? Where, string? Badge, bool Live);
		/// A theatre, imaging room or emergency department as it is NOW: its cases, the staff with them, the next case today.
		public sealed record HospitalMapLive(List<HospitalMapCase> Cases, List<HospitalMapPerson> Staff, string? Next, int Today, bool Maintenance);

		[HospitalReceptionAllowed]   // H156: the reception desk works here (administrative)
		[HttpGet("Map")]
		[HospitalPerm(HospitalActions.Read)]
		public async Task<IActionResult> Map(int? floor, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return RedirectToAction("Index", "Home");
			Chrome();
			var floors = await _facility.BuildingAsync(ctx, ct);
			var occupants = await _admissions.OccupantsAsync(ctx, ct);

			// The clinics: each practitioner with a clinic room, today's patient inside and the queue.
			var today = DateTime.Now.Date;
			var docs = await _db.HospitalPractitioners.AsNoTracking().Where(p => p.CompanyId == ctx.CompanyId && p.IsActive && p.ClinicRoomId != null).ToListAsync(ct);
			var docIds = docs.Select(d => d.ID).ToList();
			var appts = await (from a in _db.HospitalAppointments.AsNoTracking()
							   join p in _db.HospitalPatients.AsNoTracking() on a.PatientId equals p.ID
							   where a.CompanyId == ctx.CompanyId && p.CompanyId == ctx.CompanyId && a.AppointmentDate == today && docIds.Contains(a.PractitionerId)
									 && (a.Status == HospitalAppointmentStatus.InRoom || a.Status == HospitalAppointmentStatus.Arrived)
							   select new { a.PractitionerId, a.Status, p.ID, p.Name, p.NameEn, p.Mrn, p.PhotoPath }).ToListAsync(ct);
			var photos = await EmployeePhotos.ResolveAsync(_db, ctx, docs.Where(d => d.EmployeeId != null).Select(d => d.EmployeeId!.Value), ct);
			var clinics = new Dictionary<int, List<HospitalMapClinic>>();
			foreach (var d in docs)
			{
				var inRoom = appts.FirstOrDefault(a => a.PractitionerId == d.ID && a.Status == HospitalAppointmentStatus.InRoom);
				var doctor = new HospitalMapPerson(d.ID, d.Name, d.NameEn, d.EmployeeId is int e && photos.TryGetValue(e, out var ph) ? ph : null,
					DisplayName.Of(d.Title ?? "", d.TitleEn), Url.Action("Practitioner", new { id = d.ID }));
				var patient = inRoom == null ? null : new HospitalMapPerson(inRoom.ID, inRoom.Name, inRoom.NameEn, inRoom.PhotoPath, inRoom.Mrn, Url.Action("Patient", new { id = inRoom.ID }));
				if (!clinics.TryGetValue(d.ClinicRoomId!.Value, out var list)) clinics[d.ClinicRoomId.Value] = list = new();
				list.Add(new HospitalMapClinic(doctor, patient, appts.Count(a => a.PractitionerId == d.ID && a.Status == HospitalAppointmentStatus.Arrived)));
			}

			ViewBag.Live = await LiveRoomsAsync(ctx, floors, today, ct);
			ViewBag.Occupants = occupants;
			ViewBag.Clinics = clinics;
			ViewBag.Floor = floor;
			ViewBag.Waiting = appts.Count(a => a.Status == HospitalAppointmentStatus.Arrived);
			ViewBag.InClinics = appts.Count(a => a.Status == HospitalAppointmentStatus.InRoom);
			ViewBag.DoctorsToday = await _db.HospitalAppointments.AsNoTracking()
				.Where(a => a.CompanyId == ctx.CompanyId && a.AppointmentDate == today && a.Status != HospitalAppointmentStatus.Cancelled)
				.Select(a => a.PractitionerId).Distinct().CountAsync(ct);
			return View(floors);
		}

		/// H69 — the rooms the map showed empty though someone was in them: a theatre's case in progress (with its surgeon
		/// and anaesthetist) or its next case today; an imaging room's patient arrived or being scanned (an imaging room the
		/// admin has not placed on the plan shows in the floor plan's first imaging room); the emergency department's open
		/// visits. Keyed by facility room.
		private async Task<Dictionary<int, HospitalMapLive>> LiveRoomsAsync(BusinessContext ctx, IReadOnlyList<HospitalFloorView> floors, DateTime today, CancellationToken ct)
		{
			var live = new Dictionary<int, HospitalMapLive>();
			var rooms = floors.SelectMany(f => f.Rooms).ToList();
			HospitalMapLive At(int roomId) => live.TryGetValue(roomId, out var l) ? l : live[roomId] = new(new(), new(), null, 0, false);
			HospitalMapPerson Patient(int id, string name, string nameEn, string? photo, string? mrn) => new(id, name, nameEn, photo, mrn, Url.Action("Patient", new { id }));
			var nowMinute = DateTime.Now.Hour * 60 + DateTime.Now.Minute;

			// Theatres: today's cases in each theatre room.
			var theatreIds = rooms.Where(r => r.Kind == HospitalRoomKinds.Theatre).Select(r => r.Id).ToList();
			if (theatreIds.Count > 0)
			{
				var cases = await (from s in _db.Set<HospitalSurgery>().AsNoTracking()
								   join p in _db.HospitalPatients.AsNoTracking() on s.PatientId equals p.ID
								   where s.CompanyId == ctx.CompanyId && s.CaseDate == today && theatreIds.Contains(s.RoomId) && s.Status != HospitalSurgeryStatus.Cancelled
								   orderby s.StartMinute
								   select new { s, p.ID, p.Name, p.NameEn, p.PhotoPath, p.Mrn }).ToListAsync(ct);
				var docIds = cases.Where(c => c.s.Status == HospitalSurgeryStatus.InProgress).SelectMany(c => new[] { c.s.SurgeonId, c.s.AnaesthetistId ?? 0 }).Where(x => x > 0).Distinct().ToList();
				var docs = await _db.HospitalPractitioners.AsNoTracking().Where(d => d.CompanyId == ctx.CompanyId && docIds.Contains(d.ID)).ToDictionaryAsync(d => d.ID, ct);
				var docPhotos = await EmployeePhotos.ResolveAsync(_db, ctx, docs.Values.Where(d => d.EmployeeId != null).Select(d => d.EmployeeId!.Value), ct);
				foreach (var g in cases.GroupBy(c => c.s.RoomId))
				{
					var l = At(g.Key);
					foreach (var c in g.Where(c => c.s.Status == HospitalSurgeryStatus.InProgress))
					{
						l.Cases.Add(new(Patient(c.ID, c.Name, c.NameEn, c.PhotoPath, c.Mrn), c.s.Name, c.s.NameEn, c.s.CaseNo, null, true));
						foreach (var id in new[] { c.s.SurgeonId, c.s.AnaesthetistId ?? 0 })
							if (docs.TryGetValue(id, out var d) && !l.Staff.Any(x => x.Id == d.ID))
								l.Staff.Add(new(d.ID, d.Name, d.NameEn, d.EmployeeId is int e && docPhotos.TryGetValue(e, out var ph) ? ph : null, DisplayName.Of(d.Title ?? "", d.TitleEn), Url.Action("Practitioner", new { id = d.ID })));
					}
					var next = g.FirstOrDefault(c => c.s.Status == HospitalSurgeryStatus.Planned && c.s.StartMinute + c.s.DurationMinutes > nowMinute);
					live[g.Key] = l with
					{
						Today = g.Count(),
						Next = next == null ? null : $"{next.s.StartMinute / 60:00}:{next.s.StartMinute % 60:00} · {DisplayName.Of(next.Name, next.NameEn)} · {DisplayName.Of(next.s.Name, next.s.NameEn)}",
					};
				}
			}

			// Imaging: who arrived or is being scanned, in the facility room the imaging room sits in.
			var imaging = rooms.Where(r => r.Kind == HospitalRoomKinds.Imaging).Select(r => r.Id).OrderBy(x => x).ToList();
			if (imaging.Count > 0)
			{
				var radRooms = await _db.Set<HospitalRadRoom>().AsNoTracking().Where(r => r.CompanyId == ctx.CompanyId && r.IsActive).ToListAsync(ct);
				int Home(HospitalRadRoom r) => r.FacilityRoomId is int f && imaging.Contains(f) ? f : imaging[0];
				var radIds = radRooms.Select(r => r.ID).ToList();
				var orders = await (from o in _db.Set<HospitalRadOrder>().AsNoTracking()
									join p in _db.HospitalPatients.AsNoTracking() on o.PatientId equals p.ID
									where o.CompanyId == ctx.CompanyId && o.RadRoomId != null && radIds.Contains(o.RadRoomId.Value)
										  && (o.Status == HospitalRadStatus.Arrived || o.Status == HospitalRadStatus.Scanning)
									select new { o, p.ID, p.Name, p.NameEn, p.PhotoPath, p.Mrn }).ToListAsync(ct);
				foreach (var r in radRooms)
				{
					var l = At(Home(r));
					if (r.InMaintenance) l = live[Home(r)] = l with { Maintenance = true };
					foreach (var o in orders.Where(x => x.o.RadRoomId == r.ID).OrderByDescending(x => x.o.Status == HospitalRadStatus.Scanning))
						l.Cases.Add(new(Patient(o.ID, o.Name, o.NameEn, o.PhotoPath, o.Mrn), o.o.Name, o.o.NameEn, r.Code, null, o.o.Status == HospitalRadStatus.Scanning));
				}
			}

			// Emergency: every open visit, sickest first (a lower ESI is more urgent), in each emergency room on the plan.
			var er = rooms.Where(r => r.Kind == HospitalRoomKinds.Emergency).Select(r => r.Id).ToList();
			if (er.Count > 0)
			{
				var visits = await (from v in _db.Set<HospitalErVisit>().AsNoTracking()
									join p0 in _db.HospitalPatients.AsNoTracking() on v.PatientId equals p0.ID into pj
									from p in pj.DefaultIfEmpty()
									join b0 in _db.Set<HospitalErBay>().AsNoTracking() on v.BayId equals b0.ID into bj
									from b in bj.DefaultIfEmpty()
									where v.CompanyId == ctx.CompanyId && v.Stage != HospitalErStage.Closed
									orderby v.Esi ?? 9, v.ArrivedAt
									select new { v, PId = (int?)p.ID, p.Name, p.NameEn, p.PhotoPath, p.Mrn, Bay = b.Code }).ToListAsync(ct);
				foreach (var id in er)
				{
					var l = At(id);
					foreach (var x in visits)
					{
						var who = x.PId is int pid ? Patient(pid, x.Name!, x.NameEn!, x.PhotoPath, x.Mrn) : new HospitalMapPerson(0, x.v.UnknownLabel ?? x.v.VisitNo, x.v.UnknownLabel ?? x.v.VisitNo, null, x.v.VisitNo, null);
						l.Cases.Add(new(who, x.v.Complaint, x.v.Complaint, x.Bay, x.v.Esi is int esi ? "ESI " + esi : null, x.v.Stage != HospitalErStage.Waiting && x.v.Stage != HospitalErStage.Triage));
					}
				}
			}
			return live;
		}
	}
}
