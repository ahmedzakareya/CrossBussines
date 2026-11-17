using CrossBuy.BL.Platform;
using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Hospital;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL.Hospital
{
	// ==========================================================================================
	// HOSPITAL H114 — WHERE IS THE PATIENT NOW: the front desk's oldest question ("where is my father?"), answered from
	// the same live sources the hospital map reads — an operation in progress, an open emergency visit, an imaging room
	// they arrived at or are being scanned in, the clinic room they are in or the clinic they wait for today, and the
	// ward bed of an open admission. The most immediate place comes first (in theatre before the bed they will return
	// to). Nothing here is stored: it is read the moment it is asked.
	// ==========================================================================================
	public static class HospitalWhereKind
	{
		public const string Theatre = "Theatre";
		public const string Emergency = "Emergency";
		public const string Imaging = "Imaging";
		public const string ClinicInRoom = "ClinicInRoom";
		public const string ClinicWaiting = "ClinicWaiting";
		public const string Ward = "Ward";
		/// Most immediate first: where the patient physically is right now, before where they belong.
		public static readonly IReadOnlyList<string> Order = new[] { Theatre, Emergency, Imaging, ClinicInRoom, ClinicWaiting, Ward };
	}

	/// One place a patient is now: the kind, where (in both languages), what is happening there, whether it is happening
	/// this minute (a case in progress, a scan running, the doctor seeing them), and the map floor to open.
	public sealed record HospitalWhere(string Kind, string Place, string PlaceEn, string? Detail, string? DetailEn, bool Live, int? FloorId, DateTime? Since);

	/// Restricted = a confidential file (H64): the desk is never told where that patient is.
	public sealed record HospitalWhereRow(int PatientId, string Mrn, string Name, string NameEn, string? PhotoPath, IReadOnlyList<HospitalWhere> Now, bool Restricted = false);

	/// The reception desk's day at a glance.
	public sealed record HospitalDeskCounts(int Admitted, int InClinic, int WaitingClinic, int InEmergency, int InTheatre, int InImaging, int ArrivalsToday);

	public static class HospitalWhereRules
	{
		/// The places in the order the desk reads them out.
		public static IReadOnlyList<HospitalWhere> Ordered(IEnumerable<HospitalWhere> all) =>
			all.OrderBy(w => { var i = IndexOf(w.Kind); return i < 0 ? 99 : i; }).ThenByDescending(w => w.Live).ToList();
		private static int IndexOf(string k) { for (int i = 0; i < HospitalWhereKind.Order.Count; i++) if (HospitalWhereKind.Order[i] == k) return i; return -1; }

		/// The fewest characters a search needs: a desk search must name someone, never list the hospital.
		public const int MinQuery = 2;
	}

	public sealed class HospitalWhereaboutsService
	{
		private readonly CrossDbContext _db;
		private readonly IHospitalAccessService _access;
		public HospitalWhereaboutsService(CrossDbContext db, IHospitalAccessService access) { _db = db; _access = access; }
		private static bool Missing(Microsoft.Data.SqlClient.SqlException ex) => ex.Number is 207 or 208;
		private static string Ar(string? ar, string? en) => string.IsNullOrWhiteSpace(ar) ? en ?? "" : ar!;
		private static string En(string? ar, string? en) => string.IsNullOrWhiteSpace(en) ? ar ?? "" : en!;
		private static string Iso(string? s) => "⁨" + s + "⁩";

		/// Patients whose name, MRN, national ID or mobile holds the words, each with where they are now.
		public async Task<IReadOnlyList<HospitalWhereRow>> SearchAsync(BusinessContext ctx, string? query, int take, DateTime nowLocal, CancellationToken ct = default)
		{
			if (!await _access.CanAsync(ctx, HospitalActions.Read, null, ct)) return Array.Empty<HospitalWhereRow>();
			var term = (query ?? "").Trim();
			if (term.Length < HospitalWhereRules.MinQuery) return Array.Empty<HospitalWhereRow>();
			var patients = await _db.HospitalPatients.AsNoTracking()
				.Where(p => p.CompanyId == ctx.CompanyId && p.IsActive && p.MergedIntoPatientId == null
					&& (p.Mrn.Contains(term) || p.Name.Contains(term) || p.NameEn.Contains(term) || (p.NationalId != null && p.NationalId == term) || (p.Phone != null && p.Phone.Contains(term))))
				.OrderByDescending(p => p.ID).Take(Math.Clamp(take, 1, 20))
				.Select(p => new { p.ID, p.Mrn, p.Name, p.NameEn, p.PhotoPath }).ToListAsync(ct);
			var ids = patients.Select(p => p.ID).ToList();
			// a confidential file's whereabouts are not the desk's to give: its place is never even read
			var restricted = (await _db.Set<HospitalRestrictedFile>().AsNoTracking().Where(r => r.CompanyId == ctx.CompanyId && r.LiftedAt == null && ids.Contains(r.PatientId))
				.Select(r => r.PatientId).ToListAsync(ct)).ToHashSet();
			var where = await WhereAsync(ctx, ids.Where(i => !restricted.Contains(i)).ToList(), nowLocal, ct);
			return patients.Select(p => restricted.Contains(p.ID)
					? new HospitalWhereRow(p.ID, p.Mrn, p.Name, p.NameEn, null, Array.Empty<HospitalWhere>(), true)
					: new HospitalWhereRow(p.ID, p.Mrn, p.Name, p.NameEn, p.PhotoPath, where.GetValueOrDefault(p.ID) ?? Array.Empty<HospitalWhere>()))
				// someone in the building first: the desk is asked about people who are here
				.OrderByDescending(r => r.Now.Count > 0).ToList();
		}

		/// H154: where these patients are, as the reception desk may be told — a confidential file is in `Restricted` and its
		/// place is never read.
		public async Task<(Dictionary<int, IReadOnlyList<HospitalWhere>> Where, HashSet<int> Restricted)> ForDeskAsync(BusinessContext ctx, IReadOnlyCollection<int> ids, DateTime nowLocal, CancellationToken ct = default)
		{
			var restricted = ids.Count == 0 ? new HashSet<int>() : (await _db.Set<HospitalRestrictedFile>().AsNoTracking()
				.Where(r => r.CompanyId == ctx.CompanyId && r.LiftedAt == null && ids.Contains(r.PatientId)).Select(r => r.PatientId).ToListAsync(ct)).ToHashSet();
			return (await WhereAsync(ctx, ids.Where(i => !restricted.Contains(i)).ToList(), nowLocal, ct), restricted);
		}

		/// Where each of these patients is now (an empty list = not in the hospital as far as the system knows).
		public async Task<Dictionary<int, IReadOnlyList<HospitalWhere>>> WhereAsync(BusinessContext ctx, IReadOnlyCollection<int> ids, DateTime nowLocal, CancellationToken ct = default)
		{
			var found = ids.Distinct().ToDictionary(i => i, _ => new List<HospitalWhere>());
			if (found.Count == 0) return new();
			var today = nowLocal.Date;
			int cid = ctx.CompanyId;
			var rooms = await (from r in _db.HospitalRooms.AsNoTracking()
							   join f in _db.HospitalFloors.AsNoTracking() on r.FloorId equals f.ID
							   join w0 in _db.HospitalWards.AsNoTracking() on r.WardId equals w0.ID into wj
							   from w in wj.DefaultIfEmpty()
							   where r.CompanyId == cid
							   select new { r.ID, r.Number, r.Name, r.NameEn, r.FloorId, Floor = f.Name, FloorEn = f.NameEn, Ward = (string?)w.Name, WardEn = w.NameEn }).ToDictionaryAsync(r => r.ID, ct);
			string RoomAr(int id) => rooms.TryGetValue(id, out var r) ? (Ar(r.Name, r.NameEn) is { Length: > 0 } n ? n + " · " : "") + "غرفة " + Iso(r.Number) + " · " + r.Floor : "";
			string RoomEn(int id) => rooms.TryGetValue(id, out var r) ? (En(r.Name, r.NameEn) is { Length: > 0 } n ? n + " · " : "") + "Room " + r.Number + " · " + En(r.Floor, r.FloorEn) : "";
			int? FloorOf(int? id) => id is int i && rooms.TryGetValue(i, out var r) ? r.FloorId : null;
			void Add(int pid, HospitalWhere w) { if (found.TryGetValue(pid, out var l)) l.Add(w); }
			var pids = found.Keys.ToList();

			try
			{
				// the ward bed of an open admission
				var beds = await (from a in _db.HospitalAdmissions.AsNoTracking()
								  join s in _db.HospitalBedStays.AsNoTracking() on a.ID equals s.AdmissionId
								  join b in _db.HospitalBeds.AsNoTracking() on s.BedId equals b.ID
								  where a.CompanyId == cid && pids.Contains(a.PatientId) && a.Status == HospitalAdmissionStatus.Active && s.EndedAt == null
								  select new { a.PatientId, s.RoomId, b.Code, s.StartedAt }).ToListAsync(ct);
				foreach (var x in beds)
				{
					var r = rooms.GetValueOrDefault(x.RoomId);
					Add(x.PatientId, new HospitalWhere(HospitalWhereKind.Ward,
						(r?.Ward is { } w ? "جناح " + w + " · " : "") + "غرفة " + Iso(r?.Number ?? "?") + " · سرير " + Iso(x.Code),
						(r?.Ward is { } w2 ? "Ward " + En(w2, r.WardEn) + " · " : "") + "Room " + (r?.Number ?? "?") + " · Bed " + x.Code,
						r == null ? null : r.Floor, r == null ? null : En(r.Floor, r.FloorEn), false, r?.FloorId, x.StartedAt));
				}

				// today's clinics: with the doctor, or waiting for them
				var appts = await (from a in _db.HospitalAppointments.AsNoTracking()
								   join d in _db.HospitalPractitioners.AsNoTracking() on a.PractitionerId equals d.ID
								   where a.CompanyId == cid && pids.Contains(a.PatientId) && a.AppointmentDate == today
										 && (a.Status == HospitalAppointmentStatus.InRoom || a.Status == HospitalAppointmentStatus.Arrived)
								   select new { a.PatientId, a.Status, a.ArrivedAt, a.StartedAt, d.Name, d.NameEn, d.ClinicRoomId }).ToListAsync(ct);
				foreach (var x in appts)
				{
					bool inRoom = x.Status == HospitalAppointmentStatus.InRoom;
					string room = x.ClinicRoomId is int cr ? RoomAr(cr) : "", roomEn = x.ClinicRoomId is int cr2 ? RoomEn(cr2) : "";
					Add(x.PatientId, new HospitalWhere(inRoom ? HospitalWhereKind.ClinicInRoom : HospitalWhereKind.ClinicWaiting,
						(inRoom ? "مع " : "ينتظر ") + x.Name, (inRoom ? "With " : "Waiting for ") + En(x.Name, x.NameEn),
						room.Length > 0 ? room : null, roomEn.Length > 0 ? roomEn : null, inRoom, FloorOf(x.ClinicRoomId), inRoom ? x.StartedAt : x.ArrivedAt));
				}

				// an operation in progress, or one still planned today
				var cases = await _db.Set<HospitalSurgery>().AsNoTracking()
					.Where(s => s.CompanyId == cid && pids.Contains(s.PatientId) && s.CaseDate == today && (s.Status == HospitalSurgeryStatus.InProgress || s.Status == HospitalSurgeryStatus.Planned))
					.Select(s => new { s.PatientId, s.Status, s.Name, s.NameEn, s.RoomId, s.StartedAt, s.StartMinute }).ToListAsync(ct);
				foreach (var x in cases.Where(c => c.Status == HospitalSurgeryStatus.InProgress))
					Add(x.PatientId, new HospitalWhere(HospitalWhereKind.Theatre, RoomAr(x.RoomId), RoomEn(x.RoomId), "عملية جارية: " + x.Name, "Operation in progress: " + En(x.Name, x.NameEn), true, FloorOf(x.RoomId), x.StartedAt));

				// imaging: arrived at the room or being scanned
				var scans = await (from o in _db.Set<HospitalRadOrder>().AsNoTracking()
								   join r in _db.Set<HospitalRadRoom>().AsNoTracking() on o.RadRoomId equals r.ID
								   where o.CompanyId == cid && pids.Contains(o.PatientId) && (o.Status == HospitalRadStatus.Arrived || o.Status == HospitalRadStatus.Scanning)
								   select new { o.PatientId, o.Status, o.Name, o.NameEn, Room = r.Name, RoomEn = r.NameEn, r.Code, r.FacilityRoomId }).ToListAsync(ct);
				foreach (var x in scans)
				{
					bool live = x.Status == HospitalRadStatus.Scanning;
					Add(x.PatientId, new HospitalWhere(HospitalWhereKind.Imaging, "الأشعة · " + Ar(x.Room, x.RoomEn) + " " + Iso("(" + x.Code + ")"), "Imaging · " + En(x.Room, x.RoomEn) + " (" + x.Code + ")",
						(live ? "جاري التصوير: " : "وصل وينتظر: ") + x.Name, (live ? "Being scanned: " : "Arrived, waiting: ") + En(x.Name, x.NameEn), live, FloorOf(x.FacilityRoomId), null));
				}

				// an open emergency visit, in its bay
				var er = await (from v in _db.Set<HospitalErVisit>().AsNoTracking()
								join b0 in _db.Set<HospitalErBay>().AsNoTracking() on v.BayId equals b0.ID into bj
								from b in bj.DefaultIfEmpty()
								where v.CompanyId == cid && v.PatientId != null && pids.Contains(v.PatientId.Value) && v.Stage != HospitalErStage.Closed
								select new { PatientId = v.PatientId!.Value, v.Stage, v.Esi, v.ArrivedAt, Bay = (string?)b.Name, BayEn = b.NameEn, BayCode = (string?)b.Code }).ToListAsync(ct);
				foreach (var x in er)
					Add(x.PatientId, new HospitalWhere(HospitalWhereKind.Emergency,
						"الطوارئ" + (x.BayCode != null ? " · " + Ar(x.Bay, x.BayEn) + " " + Iso("(" + x.BayCode + ")") : " · بدون سرير بعد"),
						"Emergency" + (x.BayCode != null ? " · " + En(x.Bay, x.BayEn) + " (" + x.BayCode + ")" : " · no bay yet"),
						(x.Esi is int e ? Iso("ESI " + e) + " · " : "") + StageAr(x.Stage), (x.Esi is int e2 ? "ESI " + e2 + " · " : "") + x.Stage, true, null, x.ArrivedAt));
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex)) { }   // a module not installed yet answers nothing
			return found.ToDictionary(k => k.Key, k => HospitalWhereRules.Ordered(k.Value));
		}

		private static string StageAr(string s) => s switch
		{
			HospitalErStage.Triage => "الفرز", HospitalErStage.Waiting => "ينتظر", HospitalErStage.Treatment => "تحت العلاج",
			HospitalErStage.Observation => "تحت الملاحظة", HospitalErStage.Disposition => "قرار الخروج أو التنويم", _ => s,
		};

		/// The desk's counters for today.
		public async Task<HospitalDeskCounts> CountsAsync(BusinessContext ctx, DateTime nowLocal, CancellationToken ct = default)
		{
			int cid = ctx.CompanyId; var today = nowLocal.Date;
			try
			{
				int admitted = await _db.HospitalAdmissions.CountAsync(a => a.CompanyId == cid && a.Status == HospitalAdmissionStatus.Active, ct);
				var appts = _db.HospitalAppointments.Where(a => a.CompanyId == cid && a.AppointmentDate == today);
				int inClinic = await appts.CountAsync(a => a.Status == HospitalAppointmentStatus.InRoom, ct);
				int waiting = await appts.CountAsync(a => a.Status == HospitalAppointmentStatus.Arrived, ct);
				int arrivals = await appts.CountAsync(a => a.ArrivedAt != null, ct);
				int er = await _db.Set<HospitalErVisit>().CountAsync(v => v.CompanyId == cid && v.Stage != HospitalErStage.Closed, ct);
				int theatre = await _db.Set<HospitalSurgery>().CountAsync(s => s.CompanyId == cid && s.Status == HospitalSurgeryStatus.InProgress, ct);
				int imaging = await _db.Set<HospitalRadOrder>().CountAsync(o => o.CompanyId == cid && (o.Status == HospitalRadStatus.Arrived || o.Status == HospitalRadStatus.Scanning), ct);
				return new HospitalDeskCounts(admitted, inClinic, waiting, er, theatre, imaging, arrivals);
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex)) { return new HospitalDeskCounts(0, 0, 0, 0, 0, 0, 0); }
		}
	}
}
