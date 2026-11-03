using CrossBuy.BL;
using CrossBuy.BL.Hospital;
using CrossBuy.BL.Platform;
using CrossBuy.Models;
using CrossBuy.Models.Context.Hospital;
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
	}
}
