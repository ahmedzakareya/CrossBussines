using CrossBuy.BL.Platform;
using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Hospital;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL.Hospital
{
	// ==========================================================================================
	// HOSPITAL OUTPATIENT SERVICE — practitioners, the appointment book and the clinics board.
	//
	// A booking is refused, HERE (the grid only shows free slots, for speed), when:
	//   · the practitioner does not work that weekday, or the minute is outside their session or
	//     off their slot grid;
	//   · their professional licence has expired by that day (booking stops on expiry);
	//   · the slot already holds a live appointment (the database backs this with a filtered
	//     unique index, so a race loses cleanly);
	//   · the patient already has a live appointment overlapping that time;
	//   · the day is in the past.
	// Status moves follow HospitalAppointmentStatus.CanMove; arrival gives the day's next queue
	// number; a practitioner has at most one patient in the room.
	// Practitioner and department writes ask `manage`; booking and the board ask `book`.
	// ==========================================================================================

	public sealed class HospitalPractitionerInput
	{
		public int? Id { get; set; }
		public int? EmployeeId { get; set; }
		public string Name { get; set; } = "";
		public string NameEn { get; set; } = "";
		public string? Title { get; set; }
		public string? TitleEn { get; set; }
		public int? DepartmentId { get; set; }
		public string Engagement { get; set; } = HospitalEngagements.Permanent;
		public string PayChannel { get; set; } = HospitalPayChannels.Payroll;
		public string? LicenseNumber { get; set; }
		public DateTime? LicenseExpiry { get; set; }
		public List<string> Privileges { get; set; } = new();
		public List<int> WorkDays { get; set; } = new();
		public int SessionStartMinute { get; set; } = 9 * 60;
		public int SessionEndMinute { get; set; } = 14 * 60;
		public int SlotMinutes { get; set; } = 15;
		public int? ClinicRoomId { get; set; }
		/// H6: the catalogue service a visit to this doctor is billed as.
		public int? ConsultationServiceId { get; set; }
	}

	public sealed class HospitalBookingInput
	{
		public int PatientId { get; set; }
		public int PractitionerId { get; set; }
		public DateTime Date { get; set; }
		public int StartMinute { get; set; }
		public string VisitType { get; set; } = HospitalVisitTypes.New;
		public string? Note { get; set; }
	}

	/// A practitioner as every screen shows them: their photo comes from the employee they are.
	public sealed record HospitalPractitionerView(HospitalPractitioner Row, string? PhotoPath, HospitalDepartment? Department, string? ClinicRoomNumber);

	public sealed record HospitalAppointmentView(int Id, int PatientId, string PatientName, string PatientNameEn, string Mrn, string? PatientPhoto, string Sex,
		int PractitionerId, DateTime Date, int StartMinute, int DurationMinutes, string VisitType, string Status, int? QueueNumber, string? Note,
		DateTime? ArrivedAt, DateTime? StartedAt, DateTime? FinishedAt);

	public sealed record HospitalClinicCard(HospitalPractitionerView Practitioner, HospitalAppointmentView? Current, IReadOnlyList<HospitalAppointmentView> Waiting,
		int Seen, HospitalAppointmentView? Next, int DelayMinutes, bool WorksToday);

	public sealed class HospitalOutpatientService
	{
		private readonly CrossDbContext _db;
		private readonly IHospitalAccessService _access;

		public HospitalOutpatientService(CrossDbContext db, IHospitalAccessService access)
		{ _db = db; _access = access; }

		private Task<bool> Can(BusinessContext ctx, string action, CancellationToken ct) => _access.CanAsync(ctx, action, null, ct);

		// ====================================================================================
		// DEPARTMENTS
		// ====================================================================================

		public async Task<IReadOnlyList<HospitalDepartment>> DepartmentsAsync(BusinessContext ctx, CancellationToken ct = default)
			=> await _db.HospitalDepartments.AsNoTracking().Where(d => d.CompanyId == ctx.CompanyId && d.IsActive)
				.OrderBy(d => d.SortOrder).ThenBy(d => d.Name).ToListAsync(ct);

		private static readonly (string Code, string Ar, string En)[] StandardDepartments =
		{
			("IM", "الباطنة", "Internal medicine"), ("CARD", "القلب", "Cardiology"), ("PED", "الأطفال", "Paediatrics"),
			("OBG", "النساء والولادة", "Obstetrics & gynaecology"), ("ORTH", "العظام", "Orthopaedics"), ("SURG", "الجراحة العامة", "General surgery"),
			("ENT", "الأنف والأذن والحنجرة", "ENT"), ("DERM", "الجلدية", "Dermatology"), ("ER", "الطوارئ", "Emergency"),
			// H96/H97 — the specialty clinics of the approved prototype
			("OPH", "العيون", "Ophthalmology"), ("ALG", "الحساسية والمناعة", "Allergy & immunology"),
			// the dental pack's clinic (its chairs are booked on the chair schedule; the portal takes requests for it)
			("DENT", "الأسنان", "Dentistry"),
		};

		/// The standard departments, for a hospital that has none yet. Adds only what is missing.
		public async Task<HospitalResult> SeedDefaultDepartmentsAsync(BusinessContext ctx, int? employeeId, CancellationToken ct = default)
		{
			if (!await Can(ctx, HospitalActions.Manage, ct)) return HospitalResult.Fail("forbidden");
			var have = await _db.HospitalDepartments.Where(d => d.CompanyId == ctx.CompanyId).Select(d => d.Code).ToListAsync(ct);
			int order = 0;
			foreach (var d in StandardDepartments)
			{
				order += 10;
				if (have.Contains(d.Code)) continue;
				_db.HospitalDepartments.Add(new HospitalDepartment { CompanyId = ctx.CompanyId, Code = d.Code, Name = d.Ar, NameEn = d.En, SortOrder = order, CreatedAt = DateTime.UtcNow, CreatedByEmployeeId = employeeId });
			}
			await _db.SaveChangesAsync(ct);
			return HospitalResult.Success();
		}

		// ====================================================================================
		// PRACTITIONERS
		// ====================================================================================

		public async Task<IReadOnlyList<HospitalPractitionerView>> PractitionersAsync(BusinessContext ctx, CancellationToken ct = default)
		{
			var rows = await _db.HospitalPractitioners.AsNoTracking().Where(p => p.CompanyId == ctx.CompanyId && p.IsActive)
				.OrderBy(p => p.Name).ToListAsync(ct);
			return await ViewsAsync(ctx, rows, ct);
		}

		public async Task<HospitalPractitionerView?> PractitionerAsync(BusinessContext ctx, int id, CancellationToken ct = default)
		{
			var row = await _db.HospitalPractitioners.AsNoTracking().FirstOrDefaultAsync(p => p.ID == id && p.CompanyId == ctx.CompanyId && p.IsActive, ct);
			return row == null ? null : (await ViewsAsync(ctx, new List<HospitalPractitioner> { row }, ct))[0];
		}

		private async Task<List<HospitalPractitionerView>> ViewsAsync(BusinessContext ctx, List<HospitalPractitioner> rows, CancellationToken ct)
		{
			var photos = await EmployeePhotos.ResolveAsync(_db, ctx, rows.Where(r => r.EmployeeId is > 0).Select(r => r.EmployeeId!.Value), ct);
			var depts = (await DepartmentsAsync(ctx, ct)).ToDictionary(d => d.ID);
			var roomIds = rows.Where(r => r.ClinicRoomId != null).Select(r => r.ClinicRoomId!.Value).Distinct().ToList();
			var rooms = await _db.HospitalRooms.AsNoTracking().Where(r => r.CompanyId == ctx.CompanyId && roomIds.Contains(r.ID)).ToDictionaryAsync(r => r.ID, r => r.Number, ct);
			return rows.Select(r => new HospitalPractitionerView(r,
				r.EmployeeId is int e && photos.TryGetValue(e, out var ph) ? ph : null,
				r.DepartmentId is int d && depts.TryGetValue(d, out var dep) ? dep : null,
				r.ClinicRoomId is int c && rooms.TryGetValue(c, out var n) ? n : null)).ToList();
		}

		public async Task<HospitalResult> SavePractitionerAsync(BusinessContext ctx, HospitalPractitionerInput input, int? employeeId, CancellationToken ct = default)
		{
			if (!await Can(ctx, HospitalActions.Manage, ct)) return HospitalResult.Fail("forbidden");
			var name = (input.Name ?? "").Trim(); var nameEn = (input.NameEn ?? "").Trim();
			if (name.Length == 0) return HospitalResult.Fail("Enter the Arabic name");
			if (nameEn.Length == 0) return HospitalResult.Fail("Enter the English name");
			if (!HospitalEngagements.IsKnown(input.Engagement)) return HospitalResult.Fail("Unknown engagement");
			if (!HospitalPayChannels.IsKnown(input.PayChannel)) return HospitalResult.Fail("Unknown payment channel");
			if (input.Privileges.Any(p => !HospitalPrivileges.IsKnown(p))) return HospitalResult.Fail("Unknown privilege");
			if (input.WorkDays.Any(d => d is < 0 or > 6)) return HospitalResult.Fail("Unknown weekday");
			if (input.SlotMinutes is < 5 or > 120) return HospitalResult.Fail("A slot is 5 to 120 minutes");
			if (input.SessionStartMinute < 0 || input.SessionEndMinute > 1440 || input.SessionStartMinute + input.SlotMinutes > input.SessionEndMinute)
				return HospitalResult.Fail("The session must end after it starts and hold at least one slot");
			if (input.DepartmentId is int dep && !await _db.HospitalDepartments.AnyAsync(d => d.ID == dep && d.CompanyId == ctx.CompanyId, ct))
				return HospitalResult.Fail("not_found");
			if (input.ClinicRoomId is int room && !await _db.HospitalRooms.AnyAsync(r => r.ID == room && r.CompanyId == ctx.CompanyId && r.IsActive && r.Kind == HospitalRoomKinds.Clinic, ct))
				return HospitalResult.Fail("Choose a clinic room");
			if (input.EmployeeId is int emp && !await _db.Employee.AnyAsync(e => e.ID == emp && e.EmpCompanyID == ctx.CompanyId, ct))
				return HospitalResult.Fail("not_found");
			if (input.ConsultationServiceId is int fee && !await _db.HospitalServices.AnyAsync(s => s.ID == fee && s.CompanyId == ctx.CompanyId && s.Category == HospitalServiceCategories.Consultation, ct))
				return HospitalResult.Fail("Choose a consultation service");

			HospitalPractitioner? row;
			if (input.Id is int id)
			{
				row = await _db.HospitalPractitioners.FirstOrDefaultAsync(p => p.ID == id && p.CompanyId == ctx.CompanyId && p.IsActive, ct);
				if (row == null) return HospitalResult.Fail("not_found");
				row.UpdatedAt = DateTime.UtcNow; row.UpdatedByEmployeeId = employeeId;
			}
			else
			{
				row = new HospitalPractitioner { CompanyId = ctx.CompanyId, CreatedAt = DateTime.UtcNow, CreatedByEmployeeId = employeeId };
				_db.HospitalPractitioners.Add(row);
			}
			row.EmployeeId = input.EmployeeId; row.Name = name; row.NameEn = nameEn;
			row.Title = Clean(input.Title, 200); row.TitleEn = Clean(input.TitleEn, 200); row.DepartmentId = input.DepartmentId;
			row.Engagement = input.Engagement; row.PayChannel = input.PayChannel;
			row.LicenseNumber = Clean(input.LicenseNumber, 60); row.LicenseExpiry = input.LicenseExpiry?.Date;
			row.Privileges = input.Privileges.Count == 0 ? null : string.Join(",", input.Privileges.Distinct());
			row.WorkDays = input.WorkDays.Count == 0 ? null : string.Join(",", input.WorkDays.Distinct().OrderBy(d => d));
			row.SessionStartMinute = input.SessionStartMinute; row.SessionEndMinute = input.SessionEndMinute; row.SlotMinutes = input.SlotMinutes;
			row.ClinicRoomId = input.ClinicRoomId; row.ConsultationServiceId = input.ConsultationServiceId;
			await _db.SaveChangesAsync(ct);
			return HospitalResult.Success(row.ID);
		}

		/// Every slot a practitioner offers on a day: start minutes from the session start, a slot apart.
		public static IReadOnlyList<int> Slots(HospitalPractitioner p)
		{
			var list = new List<int>();
			for (int m = p.SessionStartMinute; m + p.SlotMinutes <= p.SessionEndMinute; m += p.SlotMinutes) list.Add(m);
			return list;
		}

		public static bool LicenseValidOn(HospitalPractitioner p, DateTime day) => p.LicenseExpiry is not DateTime x || x.Date >= day.Date;

		// ====================================================================================
		// APPOINTMENTS
		// ====================================================================================

		/// The appointments as screens show them. FILTER `source` BEFORE calling: a filter on the projected
		/// record cannot be translated to SQL.
		private IQueryable<HospitalAppointmentView> Views(BusinessContext ctx, IQueryable<HospitalAppointment> source) =>
			from a in source
			join p in _db.HospitalPatients.AsNoTracking() on a.PatientId equals p.ID
			where a.CompanyId == ctx.CompanyId && p.CompanyId == ctx.CompanyId
			select new HospitalAppointmentView(a.ID, p.ID, p.Name, p.NameEn, p.Mrn, p.PhotoPath, p.Sex, a.PractitionerId, a.AppointmentDate, a.StartMinute,
				a.DurationMinutes, a.VisitType, a.Status, a.QueueNumber, a.Note, a.ArrivedAt, a.StartedAt, a.FinishedAt);

		/// Every appointment of a day (cancelled ones left out: they free their slot).
		public async Task<IReadOnlyList<HospitalAppointmentView>> DayAsync(BusinessContext ctx, DateTime day, CancellationToken ct = default)
		{
			var d = day.Date;
			var source = _db.HospitalAppointments.AsNoTracking()
				.Where(a => a.CompanyId == ctx.CompanyId && a.AppointmentDate == d && a.Status != HospitalAppointmentStatus.Cancelled)
				.OrderBy(a => a.StartMinute);
			return (await Views(ctx, source).ToListAsync(ct)).OrderBy(a => a.StartMinute).ToList();
		}

		public async Task<IReadOnlyList<HospitalAppointmentView>> PatientAppointmentsAsync(BusinessContext ctx, int patientId, CancellationToken ct = default)
			=> (await Views(ctx, _db.HospitalAppointments.AsNoTracking()
					.Where(a => a.CompanyId == ctx.CompanyId && a.PatientId == patientId)
					.OrderByDescending(a => a.AppointmentDate).ThenByDescending(a => a.StartMinute).Take(100)).ToListAsync(ct))
				.OrderByDescending(a => a.Date).ThenByDescending(a => a.StartMinute).ToList();

		public async Task<HospitalResult> BookAsync(BusinessContext ctx, HospitalBookingInput input, int? employeeId, DateTime today, CancellationToken ct = default)
		{
			if (!await Can(ctx, HospitalActions.Book, ct)) return HospitalResult.Fail("forbidden");
			var day = input.Date.Date;
			if (!HospitalVisitTypes.IsKnown(input.VisitType)) return HospitalResult.Fail("Unknown visit type");
			if (day < today.Date) return HospitalResult.Fail("An appointment cannot be booked in the past");
			if (await _db.HospitalPatients.AnyAsync(p => p.ID == input.PatientId && p.CompanyId == ctx.CompanyId && p.DeceasedAt != null, ct))
				return HospitalResult.Fail("This patient is deceased");   // H59
			var doc = await _db.HospitalPractitioners.AsNoTracking().FirstOrDefaultAsync(p => p.ID == input.PractitionerId && p.CompanyId == ctx.CompanyId && p.IsActive, ct);
			var patient = await _db.HospitalPatients.AsNoTracking().FirstOrDefaultAsync(p => p.ID == input.PatientId && p.CompanyId == ctx.CompanyId && p.IsActive, ct);
			if (doc == null || patient == null) return HospitalResult.Fail("not_found");
			if (!LicenseValidOn(doc, day)) return HospitalResult.Fail("The doctor's licence has expired; booking is stopped until it is renewed");
			// H26: the doctor's weekly sessions (or the single window until they are started), less the leave
			var schedule = await HospitalScheduleLoader.OneAsync(_db, ctx.CompanyId, doc, day, day, ct);
			if (!schedule.WorksOn(day)) return HospitalResult.Fail(schedule.WholeDayLeave(day) ? "The doctor is on leave that day" : "The doctor does not see patients on that day");
			if (schedule.DurationAt(day, input.StartMinute) is not int duration) return HospitalResult.Fail("That time is not one of the doctor's slots");

			int end = input.StartMinute + duration;
			var live = _db.HospitalAppointments.Where(a => a.CompanyId == ctx.CompanyId && a.AppointmentDate == day && a.Status != HospitalAppointmentStatus.Cancelled);
			if (await live.AnyAsync(a => a.PractitionerId == doc.ID && a.StartMinute == input.StartMinute, ct))
				return HospitalResult.Fail("That slot is already booked");
			if (await live.AnyAsync(a => a.PatientId == patient.ID && a.StartMinute < end && input.StartMinute < a.StartMinute + a.DurationMinutes, ct))
				return HospitalResult.Fail("The patient already has an appointment at that time");

			var row = new HospitalAppointment
			{
				CompanyId = ctx.CompanyId, PatientId = patient.ID, PractitionerId = doc.ID, AppointmentDate = day, StartMinute = input.StartMinute,
				DurationMinutes = duration, VisitType = input.VisitType, Status = HospitalAppointmentStatus.Booked,
				Note = Clean(input.Note, 400), CreatedAt = DateTime.UtcNow, CreatedByEmployeeId = employeeId,
			};
			_db.HospitalAppointments.Add(row);
			try { await _db.SaveChangesAsync(ct); }
			catch (DbUpdateException) { _db.Entry(row).State = EntityState.Detached; return HospitalResult.Fail("That slot is already booked"); }
			await HospitalSmsService.QueueAppointmentAsync(_db, ctx.CompanyId, row.ID, HospitalSmsKind.Booked, employeeId, DateTime.UtcNow, ct);   // H70
			return HospitalResult.Success(row.ID);
		}

		/// Moves an appointment along its flow. Arrival takes the day's next queue number; a doctor
		/// has one patient in the room at a time.
		public async Task<HospitalResult> MoveAsync(BusinessContext ctx, int appointmentId, string to, int? employeeId, DateTime now, CancellationToken ct = default)
		{
			if (!await Can(ctx, HospitalActions.Book, ct)) return HospitalResult.Fail("forbidden");
			var a = await _db.HospitalAppointments.FirstOrDefaultAsync(x => x.ID == appointmentId && x.CompanyId == ctx.CompanyId, ct);
			if (a == null) return HospitalResult.Fail("not_found");
			if (!HospitalAppointmentStatus.CanMove(a.Status, to)) return HospitalResult.Fail("This appointment cannot make that move");
			if (to == HospitalAppointmentStatus.InRoom &&
				// H105: only the same day's room counts (a stale "in the room" from an earlier day blocked the doctor)
				await _db.HospitalAppointments.AnyAsync(x => x.CompanyId == ctx.CompanyId && x.PractitionerId == a.PractitionerId && x.Status == HospitalAppointmentStatus.InRoom && x.ID != a.ID && x.AppointmentDate == a.AppointmentDate, ct))
				return HospitalResult.Fail("The doctor already has a patient in the room");

			a.Status = to; a.UpdatedAt = DateTime.UtcNow; a.UpdatedByEmployeeId = employeeId;
			if (to == HospitalAppointmentStatus.InRoom) a.StartedAt = now;
			if (to == HospitalAppointmentStatus.Done) a.FinishedAt = now;
			if (to == HospitalAppointmentStatus.Arrived)
			{
				// THE QUEUE NUMBER IS ONE SEQUENCE FOR THE WHOLE HOSPITAL'S DAY (so a ticket never repeats in the waiting room),
				// and "the day's largest + 1" is a read-then-write: two desks — or a desk and the self check-in kiosk — marking
				// patients arrived in the same instant both read the same largest and handed out the SAME ticket. On SQL Server
				// the read and the save now run under an application lock per company and day, so arrivals take numbers one at
				// a time. (SQLite, used by the unit tests, has no such lock and runs single-writer anyway.)
				a.ArrivedAt = now;
				bool sql = _db.Database.IsSqlServer();
				var own = sql && _db.Database.CurrentTransaction == null ? await _db.Database.BeginTransactionAsync(ct) : null;
				try
				{
					if (sql)
						await _db.Database.ExecuteSqlInterpolatedAsync(
							$"EXEC sp_getapplock @Resource = {"hospital-queue-" + ctx.CompanyId + "-" + a.AppointmentDate.ToString("yyyyMMdd", System.Globalization.CultureInfo.InvariantCulture)}, @LockMode = 'Exclusive', @LockOwner = 'Transaction', @LockTimeout = 10000", ct);
					int last = await _db.HospitalAppointments.Where(x => x.CompanyId == ctx.CompanyId && x.AppointmentDate == a.AppointmentDate && x.QueueNumber != null)
						.Select(x => (int?)x.QueueNumber).MaxAsync(ct) ?? 0;
					a.QueueNumber = last + 1;
					await _db.SaveChangesAsync(ct);
					if (own != null) await own.CommitAsync(ct);
				}
				finally { if (own != null) await own.DisposeAsync(); }
			}
			else await _db.SaveChangesAsync(ct);
			if (to == HospitalAppointmentStatus.NoShow) await HospitalSmsService.QueueAppointmentAsync(_db, ctx.CompanyId, a.ID, HospitalSmsKind.NoShow, employeeId, DateTime.UtcNow, ct);   // H70
			return HospitalResult.Success(a.ID);
		}

		/// The clinic's "call next": the earliest arrived patient (by queue number) goes into the room.
		public async Task<HospitalResult> CallNextAsync(BusinessContext ctx, int practitionerId, DateTime day, int? employeeId, DateTime now, CancellationToken ct = default)
		{
			if (!await Can(ctx, HospitalActions.Book, ct)) return HospitalResult.Fail("forbidden");
			var d = day.Date;
			var next = await _db.HospitalAppointments.AsNoTracking()
				.Where(x => x.CompanyId == ctx.CompanyId && x.PractitionerId == practitionerId && x.AppointmentDate == d && x.Status == HospitalAppointmentStatus.Arrived)
				.OrderBy(x => x.QueueNumber).ThenBy(x => x.StartMinute).Select(x => (int?)x.ID).FirstOrDefaultAsync(ct);
			if (next == null) return HospitalResult.Fail("Nobody is waiting for this doctor");
			return await MoveAsync(ctx, next.Value, HospitalAppointmentStatus.InRoom, employeeId, now, ct);
		}

		/// One card per practitioner for the clinics board: who is in the room, who waits, how far behind.
		/// `nowMinute` is the clinic's wall-clock minute and `nowUtc` the same moment as an instant.
		public async Task<IReadOnlyList<HospitalClinicCard>> BoardAsync(BusinessContext ctx, DateTime day, int nowMinute, DateTime nowUtc, CancellationToken ct = default)
		{
			var docs = await PractitionersAsync(ctx, ct);
			var appts = await DayAsync(ctx, day, ct);
			var sch = await HospitalScheduleLoader.LoadAsync(_db, ctx.CompanyId, docs.Select(d => d.Row).ToList(), day, day, ct);   // H26
			return docs.Select(p =>
			{
				var mine = appts.Where(a => a.PractitionerId == p.Row.ID).ToList();
				var current = mine.FirstOrDefault(a => a.Status == HospitalAppointmentStatus.InRoom);
				var waiting = mine.Where(a => a.Status == HospitalAppointmentStatus.Arrived).OrderBy(a => a.QueueNumber).ToList();
				var next = mine.Where(a => a.Status is HospitalAppointmentStatus.Arrived or HospitalAppointmentStatus.Booked)
					.OrderBy(a => a.Status == HospitalAppointmentStatus.Arrived ? 0 : 1).ThenBy(a => a.QueueNumber ?? int.MaxValue).ThenBy(a => a.StartMinute).FirstOrDefault();
				// Behind schedule: how long a waiting patient has been kept PAST their slot - counted from the
				// later of the slot's start and their arrival, so a patient who came late does not make the
				// doctor look late.
				var late = waiting.Where(a => a.StartMinute < nowMinute)
					.Select(a => Math.Min(nowMinute - a.StartMinute, a.ArrivedAt is DateTime arr ? Math.Max(0, (int)(nowUtc - arr).TotalMinutes) : nowMinute - a.StartMinute))
					.DefaultIfEmpty(0).Max();
				return new HospitalClinicCard(p, current, waiting, mine.Count(a => a.Status == HospitalAppointmentStatus.Done), next, late, sch[p.Row.ID].WorksOn(day));
			}).Where(c => c.WorksToday || c.Current != null || c.Waiting.Count > 0).ToList();
		}

		private static string? Clean(string? s, int max)
		{
			var v = (s ?? "").Trim();
			return v.Length == 0 ? null : v.Length > max ? v[..max] : v;
		}
	}
}
