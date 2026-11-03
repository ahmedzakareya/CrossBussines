using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Hospital;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL.Hospital
{
	// ==========================================================================================
	// HOSPITAL H9 — radiology.
	//
	//   · Imaging orders are born from signed visits (one per radiology order line) and walk
	//     Scheduled → Arrived → Scanning → Reporting → Signed.
	//   · Arriving gives the patient a room of the exam's modality — the one asked for, else the working
	//     room with the shortest queue. A room in maintenance takes nobody; a room scans one patient at a time.
	//   · A contrast exam on a patient whose allergies mention contrast or iodine is flagged everywhere.
	//   · The radiographer (`rad`) works the scan; the radiologist (`rad-report`) writes and signs the report.
	//     Signed, it is locked and reaches the doctor and the file. Signed Critical, the order is flagged and
	//     told through the critical-value alerts (HospitalLabAlertService, source Radiology).
	// ==========================================================================================

	public sealed class HospitalRadExamInput
	{
		public string Modality { get; set; } = HospitalRadModality.XRay;
		public bool Contrast { get; set; }
		public string? Template { get; set; }
		public string? TemplateEn { get; set; }
	}

	public sealed class HospitalRadRoomInput
	{
		public int? Id { get; set; }
		public string Code { get; set; } = "";
		public string Name { get; set; } = "";
		public string NameEn { get; set; } = "";
		public string Modality { get; set; } = HospitalRadModality.XRay;
		public int? FacilityRoomId { get; set; }
		public bool IsActive { get; set; } = true;
	}

	public sealed class HospitalRadReportInput
	{
		public string? Findings { get; set; }
		public string? Impression { get; set; }
		public string? Category { get; set; }
	}

	public sealed record HospitalRadRow(int Id, string AccessionNo, int PatientId, string PatientName, string PatientNameEn, string Mrn, string? PatientPhoto,
		string Code, string Name, string NameEn, string Modality, string? RoomCode, string Priority, string Status, DateTime OrderedAt,
		string? From, string? FromEn, string? FromPhoto, bool ContrastAlert, bool CriticalOpen);

	public sealed record HospitalRadRoomCard(HospitalRadRoom Room, string State, HospitalRadRow? Current, int Queue);

	public sealed record HospitalRadStats(IReadOnlyDictionary<string, int> TodayByModality, IReadOnlyDictionary<string, int> Pipeline, int AwaitingReport, int OpenCriticals);

	public sealed record HospitalRadOrderView(HospitalRadOrder Order, HospitalPatient Patient, HospitalRadExam? Exam, HospitalRadRoom? Room,
		IReadOnlyList<HospitalRadRoom> Rooms, IReadOnlyDictionary<int, string> People, bool ContrastAlert);

	public sealed record HospitalRadSetupRow(HospitalService Service, HospitalRadExam? Exam);

	/// A patient's imaging reports for the file and the consultation: the signed ones (only these reach a doctor)
	/// and how many are still in radiology.
	public sealed record HospitalRadHistory(IReadOnlyList<HospitalRadOrder> Signed, int Pending, bool Compact);

	public sealed class HospitalRadiologyService
	{
		public const string RoomFree = "Free", RoomBusy = "Busy", RoomReady = "Ready", RoomMaintenance = "Maintenance";

		private readonly CrossDbContext _db;
		private readonly IHospitalAccessService _access;

		public HospitalRadiologyService(CrossDbContext db, IHospitalAccessService access) { _db = db; _access = access; }

		private Task<bool> Can(BusinessContext ctx, string action, CancellationToken ct) => _access.CanAsync(ctx, action, null, ct);

		// ====================================================================================
		// THE RULES (pure)
		// ====================================================================================

		private static readonly string[] ContrastWords = { "contrast", "iodine", "iodinated", "gadolinium", "صبغة", "صبغه", "يود", "اليود", "جادولينيوم" };

		/// A contrast exam on a patient whose recorded allergies mention contrast or iodine.
		public static bool ContrastAlert(bool contrast, string? allergies)
			=> contrast && !string.IsNullOrWhiteSpace(allergies) && ContrastWords.Any(w => allergies.Contains(w, StringComparison.OrdinalIgnoreCase));

		/// The modality a radiology service is taken for when nobody set it up: by the starter codes.
		public static string GuessModality(string code)
		{
			var c = code.ToUpperInvariant();
			return c.Contains("CT") ? HospitalRadModality.CT : c.Contains("MRI") || c.Contains("-MR") ? HospitalRadModality.MRI
				: c.Contains("US") || c.Contains("ECHO") ? HospitalRadModality.Ultrasound : HospitalRadModality.XRay;
		}

		/// A room's state on the board: maintenance first, then a scan in progress, then a patient waiting.
		public static string RoomState(bool inMaintenance, bool scanning, int queue)
			=> inMaintenance ? RoomMaintenance : scanning ? RoomBusy : queue > 0 ? RoomReady : RoomFree;

		// ====================================================================================
		// ORDERS FROM VISITS
		// ====================================================================================

		public async Task<int> SyncOrdersAsync(BusinessContext ctx, CancellationToken ct = default)
		{
			var pending = await (from o in _db.HospitalEncounterOrders.AsNoTracking()
								 join e in _db.HospitalEncounters.AsNoTracking() on o.EncounterId equals e.ID
								 where o.CompanyId == ctx.CompanyId && e.CompanyId == ctx.CompanyId && o.Category == HospitalServiceCategories.Radiology && e.Status == HospitalEncounterStatus.Signed
									   && !_db.HospitalRadOrders.Any(r => r.CompanyId == ctx.CompanyId && r.EncounterOrderId == o.ID)
								 orderby o.ID
								 select new { o, e.PatientId, e.SignedAt, e.SignedByEmployeeId, e.ID }).ToListAsync(ct);
			if (pending.Count == 0) return 0;
			var exams = await _db.HospitalRadExams.AsNoTracking().Where(x => x.CompanyId == ctx.CompanyId).ToDictionaryAsync(x => x.ServiceId, ct);
			var nos = await _db.HospitalRadOrders.Where(r => r.CompanyId == ctx.CompanyId && r.AccessionNo.StartsWith("RAD-")).Select(r => r.AccessionNo).ToListAsync(ct);
			int n = 0;
			foreach (var no in nos) if (int.TryParse(no.AsSpan(4), out var v) && v > n) n = v;
			foreach (var p in pending)
			{
				exams.TryGetValue(p.o.ServiceId, out var ex);
				_db.HospitalRadOrders.Add(new HospitalRadOrder
				{
					CompanyId = ctx.CompanyId, AccessionNo = "RAD-" + (++n).ToString("000000"), PatientId = p.PatientId, EncounterId = p.ID, EncounterOrderId = p.o.ID,
					ServiceId = p.o.ServiceId, Code = p.o.Code, Name = p.o.Name, NameEn = p.o.NameEn, Modality = ex?.Modality ?? GuessModality(p.o.Code), Contrast = ex?.Contrast ?? false,
					OrderedByEmployeeId = p.SignedByEmployeeId, OrderedAt = p.SignedAt ?? DateTime.UtcNow,
				});
			}
			try { await _db.SaveChangesAsync(ct); }
			catch (DbUpdateException) { _db.ChangeTracker.Clear(); return 0; }   // another request created them first
			return pending.Count;
		}

		// ====================================================================================
		// READS
		// ====================================================================================

		public async Task<IReadOnlyList<HospitalRadRow>> WorklistAsync(BusinessContext ctx, string? status, int? patientId = null, CancellationToken ct = default)
		{
			var q = from r in _db.HospitalRadOrders.AsNoTracking()
					join p in _db.HospitalPatients.AsNoTracking() on r.PatientId equals p.ID
					join e0 in _db.HospitalEncounters.AsNoTracking() on r.EncounterId equals (int?)e0.ID into es
					from e in es.DefaultIfEmpty()
					join d0 in _db.HospitalPractitioners.AsNoTracking() on e.PractitionerId equals d0.ID into ds
					from d in ds.DefaultIfEmpty()
					join m0 in _db.HospitalRadRooms.AsNoTracking() on r.RadRoomId equals (int?)m0.ID into ms
					from m in ms.DefaultIfEmpty()
					where r.CompanyId == ctx.CompanyId && p.CompanyId == ctx.CompanyId
					select new { r, p, d, m };
			if (status != null) q = q.Where(x => x.r.Status == status);
			else q = q.Where(x => x.r.Status != HospitalRadStatus.Signed || x.r.SignedAt >= DateTime.UtcNow.AddDays(-2));
			if (patientId != null) q = q.Where(x => x.r.PatientId == patientId);
			var rows = await q.OrderByDescending(x => x.r.Priority == HospitalLabPriority.Stat).ThenByDescending(x => x.r.Priority == HospitalLabPriority.Urgent).ThenByDescending(x => x.r.OrderedAt).Take(300).ToListAsync(ct);
			var docEmps = rows.Where(x => x.d?.EmployeeId != null).Select(x => x.d!.EmployeeId!.Value).Distinct().ToList();
			var photos = await CrossBuy.BL.Platform.EmployeePhotos.ResolveAsync(_db, ctx, docEmps, ct);
			return rows.Select(x => new HospitalRadRow(x.r.ID, x.r.AccessionNo, x.p.ID, x.p.Name, x.p.NameEn, x.p.Mrn, x.p.PhotoPath, x.r.Code, x.r.Name, x.r.NameEn, x.r.Modality, x.m?.Code,
				x.r.Priority, x.r.Status, x.r.OrderedAt, x.d?.Name, x.d?.NameEn, x.d?.EmployeeId is int de ? photos.GetValueOrDefault(de) : null,
				ContrastAlert(x.r.Contrast, x.p.Allergies), x.r.IsCritical && x.r.CriticalAcknowledgedAt == null)).ToList();
		}

		public async Task<HospitalRadStats> StatsAsync(BusinessContext ctx, DateTime dayStartUtc, CancellationToken ct = default)
		{
			var all = _db.HospitalRadOrders.AsNoTracking().Where(r => r.CompanyId == ctx.CompanyId);
			var today = await all.Where(r => r.OrderedAt >= dayStartUtc || r.Status != HospitalRadStatus.Signed).GroupBy(r => r.Modality).Select(g => new { g.Key, N = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.N, ct);
			var pipe = await all.Where(r => r.Status != HospitalRadStatus.Signed || r.SignedAt >= dayStartUtc).GroupBy(r => r.Status).Select(g => new { g.Key, N = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.N, ct);
			int critical = await all.CountAsync(r => r.IsCritical && r.CriticalAcknowledgedAt == null, ct);
			return new HospitalRadStats(HospitalRadModality.All.ToDictionary(m => m, m => today.GetValueOrDefault(m)), HospitalRadStatus.All.ToDictionary(s => s, s => pipe.GetValueOrDefault(s)),
				pipe.GetValueOrDefault(HospitalRadStatus.Reporting), critical);
		}

		/// The machines and what each is doing now.
		public async Task<IReadOnlyList<HospitalRadRoomCard>> RoomBoardAsync(BusinessContext ctx, IReadOnlyList<HospitalRadRow> open, CancellationToken ct = default)
		{
			var rooms = await _db.HospitalRadRooms.AsNoTracking().Where(m => m.CompanyId == ctx.CompanyId && m.IsActive).OrderBy(m => m.SortOrder).ThenBy(m => m.Code).ToListAsync(ct);
			return rooms.Select(m =>
			{
				var here = open.Where(r => r.RoomCode == m.Code).ToList();
				var scanning = here.FirstOrDefault(r => r.Status == HospitalRadStatus.Scanning);
				var waiting = here.Where(r => r.Status == HospitalRadStatus.Arrived).OrderBy(r => r.OrderedAt).ToList();
				return new HospitalRadRoomCard(m, RoomState(m.InMaintenance, scanning != null, waiting.Count), scanning ?? waiting.FirstOrDefault(), waiting.Count);
			}).ToList();
		}

		public async Task<HospitalRadOrderView?> OrderAsync(BusinessContext ctx, int id, CancellationToken ct = default)
		{
			var o = await _db.HospitalRadOrders.AsNoTracking().FirstOrDefaultAsync(r => r.ID == id && r.CompanyId == ctx.CompanyId, ct);
			if (o == null) return null;
			var p = await _db.HospitalPatients.AsNoTracking().FirstAsync(x => x.ID == o.PatientId && x.CompanyId == ctx.CompanyId, ct);
			var exam = await _db.HospitalRadExams.AsNoTracking().FirstOrDefaultAsync(x => x.CompanyId == ctx.CompanyId && x.ServiceId == o.ServiceId, ct);
			var rooms = await _db.HospitalRadRooms.AsNoTracking().Where(m => m.CompanyId == ctx.CompanyId && m.IsActive && m.Modality == o.Modality).OrderBy(m => m.SortOrder).ThenBy(m => m.Code).ToListAsync(ct);
			var room = o.RadRoomId == null ? null : await _db.HospitalRadRooms.AsNoTracking().FirstOrDefaultAsync(m => m.ID == o.RadRoomId && m.CompanyId == ctx.CompanyId, ct);
			var empIds = new[] { o.OrderedByEmployeeId, o.ArrivedByEmployeeId, o.ScannedByEmployeeId, o.ReportedByEmployeeId, o.SignedByEmployeeId, o.CriticalAcknowledgedByEmployeeId }
				.Where(i => i != null).Select(i => i!.Value).Distinct().ToList();
			var people = await _db.Employee.AsNoTracking().Where(e => empIds.Contains(e.ID) && e.EmpCompanyID == ctx.CompanyId)
				.ToDictionaryAsync(e => e.ID, e => DisplayName.Of(e.FullName ?? "", e.FullNameEn), ct);
			return new HospitalRadOrderView(o, p, exam, room, rooms, people, ContrastAlert(o.Contrast, p.Allergies));
		}

		/// A patient's signed imaging reports, newest first (the patient file and the consultation).
		public async Task<IReadOnlyList<HospitalRadOrder>> SignedForPatientAsync(BusinessContext ctx, int patientId, int take = 50, CancellationToken ct = default)
			=> await _db.HospitalRadOrders.AsNoTracking().Where(r => r.CompanyId == ctx.CompanyId && r.PatientId == patientId && r.Status == HospitalRadStatus.Signed)
				.OrderByDescending(r => r.SignedAt).Take(take).ToListAsync(ct);

		public async Task<HospitalRadHistory> HistoryAsync(BusinessContext ctx, int patientId, bool compact, CancellationToken ct = default)
			=> new(await SignedForPatientAsync(ctx, patientId, compact ? 5 : 50, ct),
				await _db.HospitalRadOrders.CountAsync(r => r.CompanyId == ctx.CompanyId && r.PatientId == patientId && r.Status != HospitalRadStatus.Signed, ct), compact);

		// ====================================================================================
		// THE SCAN AND THE REPORT
		// ====================================================================================

		private async Task<(HospitalRadOrder? Order, HospitalResult? Fail)> LoadAsync(BusinessContext ctx, int id, string action, CancellationToken ct)
		{
			if (!await Can(ctx, action, ct)) return (null, HospitalResult.Fail("forbidden"));
			var o = await _db.HospitalRadOrders.FirstOrDefaultAsync(r => r.ID == id && r.CompanyId == ctx.CompanyId, ct);
			return o == null ? (null, HospitalResult.Fail("not_found")) : (o, null);
		}

		/// The patient is at radiology: give him a room of the exam's modality.
		public async Task<HospitalResult> ArriveAsync(BusinessContext ctx, int id, int? roomId, int? employeeId, DateTime nowUtc, CancellationToken ct = default)
		{
			var (o, fail) = await LoadAsync(ctx, id, HospitalActions.Rad, ct); if (fail != null) return fail;
			if (o!.Status != HospitalRadStatus.Scheduled) return HospitalResult.Fail("The patient has already arrived");
			var rooms = await _db.HospitalRadRooms.AsNoTracking().Where(m => m.CompanyId == ctx.CompanyId && m.IsActive && m.Modality == o.Modality && !m.InMaintenance).ToListAsync(ct);
			HospitalRadRoom? room;
			if (roomId != null)
			{
				room = rooms.FirstOrDefault(m => m.ID == roomId);
				if (room == null) return HospitalResult.Fail("Choose a working room of this exam's modality");
			}
			else
			{
				if (rooms.Count == 0) return HospitalResult.Fail("No working room does this modality. Add one in radiology setup, or end its maintenance");
				var ids = rooms.Select(m => m.ID).ToList();
				var queues = await _db.HospitalRadOrders.Where(r => r.CompanyId == ctx.CompanyId && r.RadRoomId != null && ids.Contains(r.RadRoomId.Value) && (r.Status == HospitalRadStatus.Arrived || r.Status == HospitalRadStatus.Scanning))
					.GroupBy(r => r.RadRoomId).Select(g => new { g.Key, N = g.Count() }).ToDictionaryAsync(x => x.Key!.Value, x => x.N, ct);
				room = rooms.OrderBy(m => queues.GetValueOrDefault(m.ID)).ThenBy(m => m.SortOrder).ThenBy(m => m.Code).First();
			}
			o.Status = HospitalRadStatus.Arrived; o.ArrivedAt = nowUtc; o.ArrivedByEmployeeId = employeeId; o.RadRoomId = room.ID;
			await _db.SaveChangesAsync(ct); return HospitalResult.Success(o.ID);
		}

		public async Task<HospitalResult> StartScanAsync(BusinessContext ctx, int id, int? employeeId, DateTime nowUtc, CancellationToken ct = default)
		{
			var (o, fail) = await LoadAsync(ctx, id, HospitalActions.Rad, ct); if (fail != null) return fail;
			if (o!.Status != HospitalRadStatus.Arrived) return HospitalResult.Fail("Mark the patient arrived before scanning");
			var room = await _db.HospitalRadRooms.AsNoTracking().FirstOrDefaultAsync(m => m.ID == o.RadRoomId && m.CompanyId == ctx.CompanyId, ct);
			if (room == null || room.InMaintenance || !room.IsActive) return HospitalResult.Fail("This room is in maintenance");
			if (await _db.HospitalRadOrders.AnyAsync(r => r.CompanyId == ctx.CompanyId && r.RadRoomId == room.ID && r.Status == HospitalRadStatus.Scanning && r.ID != o.ID, ct))
				return HospitalResult.Fail("The room is scanning another patient");
			o.Status = HospitalRadStatus.Scanning; o.ScanStartedAt = nowUtc; o.ScannedByEmployeeId = employeeId;
			await _db.SaveChangesAsync(ct); return HospitalResult.Success(o.ID);
		}

		public async Task<HospitalResult> EndScanAsync(BusinessContext ctx, int id, int? employeeId, DateTime nowUtc, CancellationToken ct = default)
		{
			var (o, fail) = await LoadAsync(ctx, id, HospitalActions.Rad, ct); if (fail != null) return fail;
			if (o!.Status != HospitalRadStatus.Scanning) return HospitalResult.Fail("The scan has not started");
			o.Status = HospitalRadStatus.Reporting; o.ScanEndedAt = nowUtc;
			if (o.Findings == null)   // the report starts from the exam's template, in both languages kept as written
			{
				var ex = await _db.HospitalRadExams.AsNoTracking().FirstOrDefaultAsync(x => x.CompanyId == ctx.CompanyId && x.ServiceId == o.ServiceId, ct);
				o.Findings = DisplayName.IsArabic ? ex?.Template ?? ex?.TemplateEn : ex?.TemplateEn ?? ex?.Template;
			}
			await _db.SaveChangesAsync(ct); return HospitalResult.Success(o.ID);
		}

		public async Task<HospitalResult> SetPriorityAsync(BusinessContext ctx, int id, string priority, CancellationToken ct = default)
		{
			var (o, fail) = await LoadAsync(ctx, id, HospitalActions.Rad, ct); if (fail != null) return fail;
			if (!HospitalLabPriority.All.Contains(priority)) return HospitalResult.Fail("Unknown priority");
			if (o!.Status == HospitalRadStatus.Signed) return HospitalResult.Fail("This report is signed and locked");
			o.Priority = priority; await _db.SaveChangesAsync(ct); return HospitalResult.Success(o.ID);
		}

		private static string? Clip(string? s, int max) => string.IsNullOrWhiteSpace(s) ? null : s.Trim()[..Math.Min(max, s.Trim().Length)];

		public async Task<HospitalResult> SaveReportAsync(BusinessContext ctx, int id, HospitalRadReportInput input, int? employeeId, DateTime nowUtc, CancellationToken ct = default)
		{
			var (o, fail) = await LoadAsync(ctx, id, HospitalActions.RadReport, ct); if (fail != null) return fail;
			if (o!.Status == HospitalRadStatus.Signed) return HospitalResult.Fail("This report is signed and locked");
			if (o.Status != HospitalRadStatus.Reporting) return HospitalResult.Fail("End the scan before writing the report");
			if (input.Category != null && !HospitalRadImpression.All.Contains(input.Category)) return HospitalResult.Fail("Unknown impression category");
			o.Findings = Clip(input.Findings, 4000); o.Impression = Clip(input.Impression, 1000); o.ImpressionCategory = input.Category;
			o.ReportedAt = nowUtc; o.ReportedByEmployeeId = employeeId;
			await _db.SaveChangesAsync(ct); return HospitalResult.Success(o.ID);
		}

		/// The radiologist signs: the report is locked and reaches the doctor; Critical raises the order's flag.
		public async Task<HospitalResult> SignAsync(BusinessContext ctx, int id, HospitalRadReportInput input, int? employeeId, DateTime nowUtc, CancellationToken ct = default)
		{
			var saved = await SaveReportAsync(ctx, id, input, employeeId, nowUtc, ct);
			if (!saved.Ok) return saved;
			var o = await _db.HospitalRadOrders.FirstAsync(r => r.ID == id && r.CompanyId == ctx.CompanyId, ct);
			if (o.Findings == null || o.Impression == null) return HospitalResult.Fail("Write the findings and the impression before signing");
			if (o.ImpressionCategory == null) return HospitalResult.Fail("Choose whether the result is normal, abnormal or critical");
			o.Status = HospitalRadStatus.Signed; o.SignedAt = nowUtc; o.SignedByEmployeeId = employeeId;
			o.IsCritical = o.ImpressionCategory == HospitalRadImpression.Critical;
			await _db.SaveChangesAsync(ct); return HospitalResult.Success(o.ID);
		}

		// ====================================================================================
		// SETUP: exams and rooms
		// ====================================================================================

		public async Task<IReadOnlyList<HospitalRadSetupRow>> ExamsAsync(BusinessContext ctx, CancellationToken ct = default)
		{
			var services = await _db.HospitalServices.AsNoTracking().Where(s => s.CompanyId == ctx.CompanyId && s.Category == HospitalServiceCategories.Radiology && s.IsActive).OrderBy(s => s.SortOrder).ThenBy(s => s.Code).ToListAsync(ct);
			var exams = await _db.HospitalRadExams.AsNoTracking().Where(x => x.CompanyId == ctx.CompanyId).ToListAsync(ct);
			return services.Select(s => new HospitalRadSetupRow(s, exams.FirstOrDefault(x => x.ServiceId == s.ID))).ToList();
		}

		public async Task<IReadOnlyList<HospitalRadRoom>> RoomsAsync(BusinessContext ctx, CancellationToken ct = default)
			=> await _db.HospitalRadRooms.AsNoTracking().Where(m => m.CompanyId == ctx.CompanyId).OrderBy(m => m.SortOrder).ThenBy(m => m.Code).ToListAsync(ct);

		public async Task<HospitalResult> SaveExamAsync(BusinessContext ctx, int serviceId, HospitalRadExamInput input, CancellationToken ct = default)
		{
			if (!await Can(ctx, HospitalActions.Manage, ct)) return HospitalResult.Fail("forbidden");
			if (!HospitalRadModality.All.Contains(input.Modality)) return HospitalResult.Fail("Unknown modality");
			if (!await _db.HospitalServices.AnyAsync(s => s.ID == serviceId && s.CompanyId == ctx.CompanyId && s.Category == HospitalServiceCategories.Radiology, ct)) return HospitalResult.Fail("not_found");
			var x = await _db.HospitalRadExams.FirstOrDefaultAsync(e => e.CompanyId == ctx.CompanyId && e.ServiceId == serviceId, ct);
			if (x == null) { x = new HospitalRadExam { CompanyId = ctx.CompanyId, ServiceId = serviceId }; _db.HospitalRadExams.Add(x); }
			x.Modality = input.Modality; x.Contrast = input.Contrast; x.Template = Clip(input.Template, 2000); x.TemplateEn = Clip(input.TemplateEn, 2000);
			await _db.SaveChangesAsync(ct); return HospitalResult.Success(x.ID);
		}

		public async Task<HospitalResult> SaveRoomAsync(BusinessContext ctx, HospitalRadRoomInput input, CancellationToken ct = default)
		{
			if (!await Can(ctx, HospitalActions.Manage, ct)) return HospitalResult.Fail("forbidden");
			var code = (input.Code ?? "").Trim().ToUpperInvariant();
			if (code.Length is 0 or > 20) return HospitalResult.Fail("Enter a code of up to 20 characters");
			if (string.IsNullOrWhiteSpace(input.Name) || string.IsNullOrWhiteSpace(input.NameEn)) return HospitalResult.Fail("Enter the name in both languages");
			if (!HospitalRadModality.All.Contains(input.Modality)) return HospitalResult.Fail("Unknown modality");
			int self = input.Id ?? 0;
			if (await _db.HospitalRadRooms.AnyAsync(m => m.CompanyId == ctx.CompanyId && m.Code == code && m.ID != self, ct)) return HospitalResult.Fail("Another room uses this code");
			if (input.FacilityRoomId is int fr && !await _db.HospitalRooms.AnyAsync(r => r.ID == fr && r.CompanyId == ctx.CompanyId, ct)) return HospitalResult.Fail("not_found");
			HospitalRadRoom? m;
			if (input.Id is int id)
			{
				m = await _db.HospitalRadRooms.FirstOrDefaultAsync(x => x.ID == id && x.CompanyId == ctx.CompanyId, ct);
				if (m == null) return HospitalResult.Fail("not_found");
				if (m.Modality != input.Modality && await _db.HospitalRadOrders.AnyAsync(r => r.CompanyId == ctx.CompanyId && r.RadRoomId == m.ID && (r.Status == HospitalRadStatus.Arrived || r.Status == HospitalRadStatus.Scanning), ct))
					return HospitalResult.Fail("Patients are waiting in this room; it keeps its modality until they are done");
			}
			else
			{
				int sort = (await _db.HospitalRadRooms.Where(x => x.CompanyId == ctx.CompanyId).MaxAsync(x => (int?)x.SortOrder, ct) ?? 0) + 10;
				m = new HospitalRadRoom { CompanyId = ctx.CompanyId, SortOrder = sort };
				_db.HospitalRadRooms.Add(m);
			}
			m.Code = code; m.Name = input.Name.Trim(); m.NameEn = input.NameEn.Trim(); m.Modality = input.Modality; m.FacilityRoomId = input.FacilityRoomId; m.IsActive = input.IsActive;
			await _db.SaveChangesAsync(ct); return HospitalResult.Success(m.ID);
		}

		/// A room goes into (or out of) maintenance. A patient scanning there blocks it; waiting ones stay queued.
		public async Task<HospitalResult> SetMaintenanceAsync(BusinessContext ctx, int roomId, bool on, string? note, CancellationToken ct = default)
		{
			if (!await Can(ctx, HospitalActions.Rad, ct)) return HospitalResult.Fail("forbidden");
			var m = await _db.HospitalRadRooms.FirstOrDefaultAsync(x => x.ID == roomId && x.CompanyId == ctx.CompanyId, ct);
			if (m == null) return HospitalResult.Fail("not_found");
			if (on && await _db.HospitalRadOrders.AnyAsync(r => r.CompanyId == ctx.CompanyId && r.RadRoomId == m.ID && r.Status == HospitalRadStatus.Scanning, ct))
				return HospitalResult.Fail("A patient is being scanned in this room");
			m.InMaintenance = on; m.MaintenanceNote = on ? Clip(note, 200) : null;
			await _db.SaveChangesAsync(ct); return HospitalResult.Success(m.ID);
		}

		/// The starter rooms and the exam setup of the starter radiology services (codes from the billing seed).
		public async Task<HospitalResult> SeedStandardAsync(BusinessContext ctx, CancellationToken ct = default)
		{
			if (!await Can(ctx, HospitalActions.Manage, ct)) return HospitalResult.Fail("forbidden");
			int added = 0;
			var haveRooms = (await _db.HospitalRadRooms.Where(m => m.CompanyId == ctx.CompanyId).Select(m => m.Code).ToListAsync(ct)).ToHashSet();
			int sort = 0;
			foreach (var (code, ar, en, mod) in StandardRooms)
			{
				sort += 10;
				if (haveRooms.Contains(code)) continue;
				_db.HospitalRadRooms.Add(new HospitalRadRoom { CompanyId = ctx.CompanyId, Code = code, Name = ar, NameEn = en, Modality = mod, SortOrder = sort });
				added++;
			}
			var services = await _db.HospitalServices.AsNoTracking().Where(s => s.CompanyId == ctx.CompanyId && s.Category == HospitalServiceCategories.Radiology).ToListAsync(ct);
			var haveExams = (await _db.HospitalRadExams.Where(x => x.CompanyId == ctx.CompanyId).Select(x => x.ServiceId).ToListAsync(ct)).ToHashSet();
			foreach (var s in services.Where(s => !haveExams.Contains(s.ID)))
			{
				var t = StandardTemplates.FirstOrDefault(x => s.Code.StartsWith(x.Prefix, StringComparison.OrdinalIgnoreCase));
				_db.HospitalRadExams.Add(new HospitalRadExam { CompanyId = ctx.CompanyId, ServiceId = s.ID, Modality = GuessModality(s.Code), Contrast = t.Contrast, Template = Clip(t.Ar, 2000), TemplateEn = Clip(t.En, 2000) });
				added++;
			}
			await _db.SaveChangesAsync(ct);
			return HospitalResult.Success(added);
		}

		private static readonly (string Code, string Ar, string En, string Modality)[] StandardRooms =
		{
			("XR-1", "أشعة سينية ١", "X-ray 1", HospitalRadModality.XRay),
			("CT-1", "مقطعية ١٢٨ شريحة", "CT 128-slice", HospitalRadModality.CT),
			("MR-1", "رنين ١٫٥ تسلا", "MRI 1.5T", HospitalRadModality.MRI),
			("US-1", "موجات صوتية ١", "Ultrasound 1", HospitalRadModality.Ultrasound),
			("US-2", "موجات صوتية ٢", "Ultrasound 2", HospitalRadModality.Ultrasound),
		};

		private static readonly (string Prefix, bool Contrast, string Ar, string En)[] StandardTemplates =
		{
			("RAD-CXR", false, "الفحص: أشعة صدر.\nالنتائج: الرئتان صافيتان، لا ارتشاح ولا انصباب. حجم القلب طبيعي.", "Exam: chest X-ray.\nFindings: lungs clear, no consolidation or effusion. Heart size normal."),
			("RAD-USAB", false, "الفحص: موجات صوتية على البطن.\nالنتائج: الكبد والمرارة والكليتان والطحال بالحجم والشكل الطبيعيين.", "Exam: abdominal ultrasound.\nFindings: liver, gallbladder, kidneys and spleen of normal size and echotexture."),
			("RAD-USPEL", false, "الفحص: موجات صوتية على الحوض.\nالنتائج: الرحم والمبيضان بالحجم والشكل الطبيعيين، لا سوائل حرة.", "Exam: pelvic ultrasound.\nFindings: uterus and ovaries of normal size and appearance, no free fluid."),
			("RAD-CT", true, "الفحص: أشعة مقطعية.\nالنتائج: ", "Exam: CT scan.\nFindings: "),
			("RAD-MRI", false, "الفحص: رنين مغناطيسي.\nالنتائج: ", "Exam: MRI.\nFindings: "),
			("", false, "", ""),
		};
	}
}
