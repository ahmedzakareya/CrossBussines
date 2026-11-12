using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Hospital;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL.Hospital
{
	// ==========================================================================================
	// HOSPITAL H16 — the dental practice: the chair day, lab cases, recall and sterilisation.
	//
	//   · A chair booking (`book`) is an H4 appointment (HospitalOutpatientService.BookAsync keeps every H4 rule:
	//     the dentist's days and slots, the licence, the patient's own clash) plus its dental visit. Here: the
	//     chair is free for the whole chair time of the procedure, and so is the dentist.
	//   · The flow (`book`) is H4's (MoveAsync), except "in the chair": a dentist works two chairs, so the
	//     limit is one patient per CHAIR. A finished preventive visit moves the patient's recall on.
	//   · Lab cases (`consult` prescribes; `consult` or `book` moves them): the pipeline of HospitalDentalPracticeRules.
	//   · Recall (`book`): a contact attempt is recorded; the booking itself is a chair booking.
	//   · Sterilisation (`consult`): cycles, their indicators, and the kits used on patients from them.
	//   · Nothing is billed or posted here, and no job runs. Wired through HospitalDentalOpsModel.Configure.
	// ==========================================================================================

	public sealed class HospitalChairInput
	{
		public int? Id { get; set; }
		public string Code { get; set; } = "";
		public string Name { get; set; } = "";
		public string NameEn { get; set; } = "";
		public int? PractitionerId { get; set; }
		public int SortOrder { get; set; }
		public bool IsActive { get; set; } = true;
	}

	public sealed class HospitalChairBookingInput
	{
		public int PatientId { get; set; }
		public int ChairId { get; set; }
		public int PractitionerId { get; set; }
		public DateTime Date { get; set; }
		public int StartMinute { get; set; }
		public int? ServiceId { get; set; }
		public int? Tooth { get; set; }
		public int? PlanLineId { get; set; }
		public int? LabCaseId { get; set; }
		public string? Note { get; set; }
		/// H28: a portal request keeps its visit type (a follow-up stays one); the chair schedule books a new visit.
		public string? VisitType { get; set; }
	}

	public sealed class HospitalDentalLabInput
	{
		public int? Id { get; set; }
		public string Name { get; set; } = "";
		public string NameEn { get; set; } = "";
		public string? Phone { get; set; }
		public bool IsActive { get; set; } = true;
	}

	public sealed class HospitalLabCaseInput
	{
		public int PatientId { get; set; }
		public int LabId { get; set; }
		public string? Teeth { get; set; }
		public string CaseType { get; set; } = "Crown";
		public string Material { get; set; } = "";
		public string? Shade { get; set; }
		public DateTime DueDate { get; set; }
		public int? FitAppointmentId { get; set; }
		public int? PlanLineId { get; set; }
		public decimal Cost { get; set; }
	}

	public sealed class HospitalSterilCycleInput
	{
		public string Unit { get; set; } = "";
		public string Load { get; set; } = "";
		public DateTime? RunAt { get; set; }
	}

	public sealed record HospitalChairSlot(HospitalAppointment Appointment, HospitalDentalVisit Visit, string PatientName, string PatientNameEn, string Mrn, string? PatientPhoto,
		string? Procedure, string? ProcedureEn, string? LabCaseNo, bool LabAtRisk);

	public sealed record HospitalChairColumn(HospitalDentalChair Chair, HospitalDentist? Person, IReadOnlyList<HospitalChairSlot> Slots);

	public sealed record HospitalChairDay(DateTime Day, IReadOnlyList<HospitalChairColumn> Chairs, int Appointments, int? Occupancy, IReadOnlyList<HospitalLabCaseRow> LabAtRisk);

	public sealed record HospitalLabCaseRow(HospitalDentalLabCase Case, string PatientName, string PatientNameEn, string Mrn, string? PatientPhoto, string Lab, string LabEn, DateTime? FitAt, bool AtRisk);

	public sealed record HospitalDentalLabStats(int Open, decimal? OnTimeRate, decimal? RemakeRate, decimal? RemakeLabShare, decimal CostThisMonth);

	public sealed record HospitalRecallRow(HospitalDentalRecall Recall, string PatientName, string PatientNameEn, string Mrn, string? Phone, string? Photo, DateTime? NextVisit, string State);

	public sealed record HospitalSterilRow(HospitalSterilCycle Cycle, int Used, string? Operator, string? OperatorEn, string? OperatorPhoto, IReadOnlyList<string> UsedOn);

	public sealed record HospitalRecallStats(int? PreBookedRate, int DueThisMonth, int Overdue, int FailedCycles);

	public sealed class HospitalDentalPracticeService
	{
		private readonly CrossDbContext _db;
		private readonly IHospitalAccessService _access;
		private readonly HospitalOutpatientService _outpatient;
		private readonly HospitalDentalService _dental;

		public HospitalDentalPracticeService(CrossDbContext db, IHospitalAccessService access, HospitalOutpatientService outpatient, HospitalDentalService dental)
		{ _db = db; _access = access; _outpatient = outpatient; _dental = dental; }

		public const string ConcurrentChange = HospitalInsuranceService.ConcurrentChange;
		private Task<bool> Can(BusinessContext ctx, string action, CancellationToken ct) => _access.CanAsync(ctx, action, null, ct);
		private static string? Clip(string? s, int max) => string.IsNullOrWhiteSpace(s) ? null : s.Trim()[..Math.Min(max, s.Trim().Length)];
		private IQueryable<HospitalDentalChair> Chairs(BusinessContext ctx) => _db.Set<HospitalDentalChair>().Where(x => x.CompanyId == ctx.CompanyId);
		private IQueryable<HospitalDentalVisit> Visits(BusinessContext ctx) => _db.Set<HospitalDentalVisit>().Where(x => x.CompanyId == ctx.CompanyId);
		private IQueryable<HospitalDentalLabCase> Cases(BusinessContext ctx) => _db.Set<HospitalDentalLabCase>().Where(x => x.CompanyId == ctx.CompanyId);
		private IQueryable<HospitalDentalRecall> Recalls(BusinessContext ctx) => _db.Set<HospitalDentalRecall>().Where(x => x.CompanyId == ctx.CompanyId);
		private IQueryable<HospitalSterilCycle> Cycles(BusinessContext ctx) => _db.Set<HospitalSterilCycle>().Where(x => x.CompanyId == ctx.CompanyId);
		private IQueryable<HospitalAppointment> Appts(BusinessContext ctx) => _db.HospitalAppointments.Where(a => a.CompanyId == ctx.CompanyId);

		private async Task<HospitalResult> SaveAsync(CancellationToken ct, int? id = null)
		{
			try { await _db.SaveChangesAsync(ct); }
			catch (DbUpdateException) { _db.ChangeTracker.Clear(); return HospitalResult.Fail(ConcurrentChange); }
			return HospitalResult.Success(id);
		}

		// ====================================================================================
		// CHAIRS
		// ====================================================================================

		public async Task<IReadOnlyList<HospitalDentalChair>> ChairsAsync(BusinessContext ctx, bool activeOnly, CancellationToken ct = default) =>
			await Chairs(ctx).AsNoTracking().Where(c => !activeOnly || c.IsActive).OrderBy(c => c.SortOrder).ThenBy(c => c.Code).ToListAsync(ct);

		public async Task<HospitalResult> SaveChairAsync(BusinessContext ctx, HospitalChairInput input, CancellationToken ct = default)
		{
			if (!await Can(ctx, HospitalActions.Manage, ct)) return HospitalResult.Fail("forbidden");
			var code = (input.Code ?? "").Trim().ToUpperInvariant();
			if (code.Length is 0 or > 20) return HospitalResult.Fail("Enter a code of up to 20 characters");
			if (string.IsNullOrWhiteSpace(input.Name) || string.IsNullOrWhiteSpace(input.NameEn)) return HospitalResult.Fail("Enter the name in both languages");
			if (input.PractitionerId is int pid && !await _db.HospitalPractitioners.AnyAsync(d => d.ID == pid && d.CompanyId == ctx.CompanyId, ct)) return HospitalResult.Fail("not_found");
			int self = input.Id ?? 0;
			if (await Chairs(ctx).AnyAsync(c => c.Code == code && c.ID != self, ct)) return HospitalResult.Fail("Another chair uses this code");
			HospitalDentalChair? c;
			if (input.Id is int id) { c = await Chairs(ctx).FirstOrDefaultAsync(x => x.ID == id, ct); if (c == null) return HospitalResult.Fail("not_found"); }
			else { c = new HospitalDentalChair { CompanyId = ctx.CompanyId }; _db.Add(c); }
			c.Code = code; c.Name = input.Name.Trim(); c.NameEn = input.NameEn.Trim(); c.PractitionerId = input.PractitionerId; c.SortOrder = input.SortOrder; c.IsActive = input.IsActive;
			{ var saved = await SaveAsync(ct); return saved.Ok ? HospitalResult.Success(c.ID) : saved; }   // the id exists only after the save
		}

		public async Task<HospitalChairDay> DayAsync(BusinessContext ctx, DateTime day, DateTime nowLocal, CancellationToken ct = default)
		{
			day = day.Date;
			var chairs = await ChairsAsync(ctx, true, ct);
			var people = await _dental.DentistsAsync(ctx, ct);
			var q = from v in Visits(ctx).AsNoTracking()
					join a in Appts(ctx).AsNoTracking() on v.AppointmentId equals a.ID
					join p in _db.HospitalPatients.AsNoTracking() on a.PatientId equals p.ID
					join s0 in _db.HospitalServices.AsNoTracking() on v.ServiceId equals s0.ID into ss
					from s in ss.DefaultIfEmpty()
					join l0 in Cases(ctx).AsNoTracking() on v.LabCaseId equals l0.ID into ls
					from l in ls.DefaultIfEmpty()
					where a.AppointmentDate == day && a.Status != HospitalAppointmentStatus.Cancelled
					select new { v, a, p.Name, p.NameEn, p.Mrn, p.PhotoPath, SName = s == null ? null : s.Name, SNameEn = s == null ? null : s.NameEn, LabNo = l == null ? null : l.CaseNo, LabStage = l == null ? null : l.Stage };
			var rows = await q.ToListAsync(ct);
			var cols = chairs.Select(c => new HospitalChairColumn(c, people.FirstOrDefault(x => x.Id == c.PractitionerId),
				rows.Where(r => r.v.ChairId == c.ID).OrderBy(r => r.a.StartMinute).Select(r => new HospitalChairSlot(r.a, r.v, r.Name, r.NameEn, r.Mrn, r.PhotoPath, r.SName, r.SNameEn, r.LabNo,
					r.LabStage != null && HospitalDentalPracticeRules.AtRisk(r.LabStage, r.a.AppointmentDate.AddMinutes(r.a.StartMinute), nowLocal))).ToList())).ToList();
			int booked = rows.Where(r => chairs.Any(c => c.ID == r.v.ChairId)).Sum(r => r.a.DurationMinutes);
			var risky = (await CasesAsync(ctx, nowLocal, ct)).Where(c => c.AtRisk).ToList();
			return new HospitalChairDay(day, cols, rows.Count, HospitalDentalPracticeRules.Occupancy(booked, chairs.Count), risky);
		}

		/// A chair booking: the H4 appointment (every H4 rule), then its chair time and its dental visit.
		public async Task<HospitalResult> BookAsync(BusinessContext ctx, HospitalChairBookingInput input, int? employeeId, DateTime today, CancellationToken ct = default)
		{
			if (!await Can(ctx, HospitalActions.Book, ct)) return HospitalResult.Fail("forbidden");
			var chair = await Chairs(ctx).AsNoTracking().FirstOrDefaultAsync(c => c.ID == input.ChairId && c.IsActive, ct);
			if (chair == null) return HospitalResult.Fail("not_found");
			int minutes = HospitalDentalPracticeRules.DefaultMinutes;
			if (input.ServiceId is int sid)
			{
				var proc = await _dental.ProcedureOfAsync(ctx, sid, ct);
				if (proc == null) return HospitalResult.Fail("not_found");
				minutes = proc.Minutes;
			}
			if (input.Tooth is int t && !HospitalDentalRules.IsTooth(t)) return HospitalResult.Fail("Choose the tooth");
			if (input.LabCaseId is int lc && !await Cases(ctx).AnyAsync(c => c.ID == lc && c.PatientId == input.PatientId, ct)) return HospitalResult.Fail("not_found");
			var day = input.Date.Date;
			// the chair and the dentist are free for the whole chair time
			var same = await (from v in Visits(ctx) join a in Appts(ctx) on v.AppointmentId equals a.ID
							  where a.AppointmentDate == day && a.Status != HospitalAppointmentStatus.Cancelled && a.Status != HospitalAppointmentStatus.NoShow
							  select new { v.ChairId, a.PractitionerId, a.StartMinute, a.DurationMinutes }).ToListAsync(ct);
			if (same.Any(x => x.ChairId == chair.ID && HospitalDentalPracticeRules.Overlaps(input.StartMinute, minutes, x.StartMinute, x.DurationMinutes)))
				return HospitalResult.Fail("The chair is taken at that time");
			if (same.Any(x => x.PractitionerId == input.PractitionerId && HospitalDentalPracticeRules.Overlaps(input.StartMinute, minutes, x.StartMinute, x.DurationMinutes)))
				return HospitalResult.Fail("The dentist is with another patient at that time");

			// own-or-join: a portal request booked on a chair (H28) runs this inside the request's transaction
			await using var tx = _db.Database.IsRelational() ? await ScopedTx.BeginOrJoinAsync(_db) : null;
			var r = await _outpatient.BookAsync(ctx, new HospitalBookingInput { PatientId = input.PatientId, PractitionerId = input.PractitionerId, Date = day, StartMinute = input.StartMinute, VisitType = input.VisitType ?? HospitalVisitTypes.New, Note = Clip(input.Note, 400) }, employeeId, today, ct);
			if (!r.Ok || r.Id is not int apptId) return r;
			await Appts(ctx).Where(a => a.ID == apptId).ExecuteUpdateAsync(u => u.SetProperty(a => a.DurationMinutes, minutes), ct);
			_db.Add(new HospitalDentalVisit { CompanyId = ctx.CompanyId, AppointmentId = apptId, ChairId = chair.ID, ServiceId = input.ServiceId, Tooth = input.Tooth, PlanLineId = input.PlanLineId, LabCaseId = input.LabCaseId });
			r = await SaveAsync(ct, apptId);
			if (r.Ok && tx != null) await tx.CommitAsync();
			return r;
		}

		/// The flow of a chair appointment. "In the chair" is one patient per chair (a dentist works two); every
		/// other move is H4's. A finished preventive visit moves the patient's recall on.
		public async Task<HospitalResult> MoveAsync(BusinessContext ctx, int appointmentId, string to, int? employeeId, DateTime nowLocal, CancellationToken ct = default)
		{
			if (!await Can(ctx, HospitalActions.Book, ct)) return HospitalResult.Fail("forbidden");
			var v = await Visits(ctx).AsNoTracking().FirstOrDefaultAsync(x => x.AppointmentId == appointmentId, ct);
			if (v == null) return HospitalResult.Fail("not_found");
			if (to == HospitalAppointmentStatus.InRoom)
			{
				var a = await Appts(ctx).AsNoTracking().FirstAsync(x => x.ID == appointmentId, ct);
				if (!HospitalAppointmentStatus.CanMove(a.Status, to)) return HospitalResult.Fail("This appointment cannot make that move");
				bool busy = await (from x in Visits(ctx) join b in Appts(ctx) on x.AppointmentId equals b.ID
								   where x.ChairId == v.ChairId && b.Status == HospitalAppointmentStatus.InRoom && b.ID != appointmentId && b.AppointmentDate == a.AppointmentDate select b.ID).AnyAsync(ct);   // H105: the same day only
				if (busy) return HospitalResult.Fail("Another patient is in this chair");
				int n = await Appts(ctx).Where(x => x.ID == appointmentId && x.Status == a.Status).ExecuteUpdateAsync(u => u
					.SetProperty(x => x.Status, to).SetProperty(x => x.StartedAt, nowLocal).SetProperty(x => x.UpdatedAt, DateTime.UtcNow).SetProperty(x => x.UpdatedByEmployeeId, employeeId), ct);
				return n == 1 ? HospitalResult.Success(appointmentId) : HospitalResult.Fail(ConcurrentChange);
			}
			var r = await _outpatient.MoveAsync(ctx, appointmentId, to, employeeId, nowLocal, ct);
			if (r.Ok && to == HospitalAppointmentStatus.Done && v.ServiceId is int sid && (await _dental.ProcedureOfAsync(ctx, sid, ct))?.Category == HospitalDentalCategory.Preventive)
			{
				var patientId = await Appts(ctx).Where(x => x.ID == appointmentId).Select(x => x.PatientId).FirstAsync(ct);
				var rc = await Recalls(ctx).FirstOrDefaultAsync(x => x.PatientId == patientId, ct);
				if (rc == null) { rc = new HospitalDentalRecall { CompanyId = ctx.CompanyId, PatientId = patientId }; _db.Add(rc); }
				rc.LastVisit = nowLocal.Date; rc.DueDate = HospitalDentalPracticeRules.NextRecall(nowLocal.Date, rc.IntervalMonths); rc.Attempts = 0; rc.UpdatedAt = DateTime.UtcNow;
				await SaveAsync(ct);
			}
			return r;
		}

		// ====================================================================================
		// LAB CASES
		// ====================================================================================

		public async Task<IReadOnlyList<HospitalDentalLab>> LabsAsync(BusinessContext ctx, CancellationToken ct = default) =>
			await _db.Set<HospitalDentalLab>().AsNoTracking().Where(l => l.CompanyId == ctx.CompanyId).OrderBy(l => l.Name).ToListAsync(ct);

		public async Task<HospitalResult> SaveLabAsync(BusinessContext ctx, HospitalDentalLabInput input, CancellationToken ct = default)
		{
			if (!await Can(ctx, HospitalActions.Manage, ct)) return HospitalResult.Fail("forbidden");
			if (string.IsNullOrWhiteSpace(input.Name) || string.IsNullOrWhiteSpace(input.NameEn)) return HospitalResult.Fail("Enter the name in both languages");
			HospitalDentalLab? l;
			if (input.Id is int id) { l = await _db.Set<HospitalDentalLab>().FirstOrDefaultAsync(x => x.ID == id && x.CompanyId == ctx.CompanyId, ct); if (l == null) return HospitalResult.Fail("not_found"); }
			else { l = new HospitalDentalLab { CompanyId = ctx.CompanyId }; _db.Add(l); }
			l.Name = input.Name.Trim(); l.NameEn = input.NameEn.Trim(); l.Phone = Clip(input.Phone, 30); l.IsActive = input.IsActive;
			{ var saved = await SaveAsync(ct); return saved.Ok ? HospitalResult.Success(l.ID) : saved; }   // the id exists only after the save
		}

		public async Task<IReadOnlyList<HospitalLabCaseRow>> CasesAsync(BusinessContext ctx, DateTime nowLocal, CancellationToken ct = default)
		{
			var q = from c in Cases(ctx).AsNoTracking()
					join p in _db.HospitalPatients.AsNoTracking() on c.PatientId equals p.ID
					join l in _db.Set<HospitalDentalLab>().AsNoTracking() on c.LabId equals l.ID
					join a0 in Appts(ctx).AsNoTracking() on c.FitAppointmentId equals a0.ID into aa
					from a in aa.DefaultIfEmpty()
					select new { c, p.Name, p.NameEn, p.Mrn, p.PhotoPath, Lab = l.Name, LabEn = l.NameEn, FitDay = a == null ? (DateTime?)null : a.AppointmentDate, FitMin = a == null ? 0 : a.StartMinute };
			var rows = await q.OrderByDescending(x => x.c.CreatedAt).Take(300).ToListAsync(ct);
			return rows.Select(x =>
			{
				DateTime? fit = x.FitDay?.AddMinutes(x.FitMin);
				return new HospitalLabCaseRow(x.c, x.Name, x.NameEn, x.Mrn, x.PhotoPath, x.Lab, x.LabEn, fit, HospitalDentalPracticeRules.AtRisk(x.c.Stage, fit, nowLocal));
			}).ToList();
		}

		public async Task<HospitalDentalLabStats> LabStatsAsync(BusinessContext ctx, DateTime today, CancellationToken ct = default)
		{
			var all = await Cases(ctx).AsNoTracking().Select(c => new { c.Stage, c.DueDate, c.ReceivedAt, c.Remakes, c.RemakeFault, c.Cost, c.CreatedAt }).ToListAsync(ct);
			var month = new DateTime(today.Year, today.Month, 1);
			int remakes = all.Sum(c => (int)c.Remakes), labFault = all.Count(c => c.RemakeFault == "Lab");
			return new HospitalDentalLabStats(all.Count(c => c.Stage != HospitalLabCaseStage.Fitted),
				HospitalDentalPracticeRules.OnTimeRate(all.Select(c => (c.DueDate, c.ReceivedAt)), today),
				HospitalDentalPracticeRules.RemakeRate(all.Count, remakes),
				remakes == 0 ? null : Math.Round(labFault * 100m / all.Count(c => c.RemakeFault != null), 0),
				all.Where(c => c.CreatedAt >= month).Sum(c => c.Cost));
		}

		/// A prescription never leaves incomplete: the lab, the type, the material, the date it is due.
		public async Task<HospitalResult> CreateCaseAsync(BusinessContext ctx, HospitalLabCaseInput input, int? employeeId, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await Can(ctx, HospitalActions.Consult, ct)) return HospitalResult.Fail("forbidden");
			if (!await _db.HospitalPatients.AnyAsync(p => p.ID == input.PatientId && p.CompanyId == ctx.CompanyId, ct)) return HospitalResult.Fail("not_found");
			if (!await _db.Set<HospitalDentalLab>().AnyAsync(l => l.ID == input.LabId && l.CompanyId == ctx.CompanyId && l.IsActive, ct)) return HospitalResult.Fail("Choose the lab");
			if (!HospitalLabCaseType.All.Contains(input.CaseType)) return HospitalResult.Fail("Choose the type of work");
			if (string.IsNullOrWhiteSpace(input.Material)) return HospitalResult.Fail("Enter the material");
			if (input.DueDate.Date < nowUtc.Date) return HospitalResult.Fail("The due date cannot be in the past");
			if (input.Cost < 0) return HospitalResult.Fail("A cost cannot be negative");
			if (input.FitAppointmentId is int fa && !await Appts(ctx).AnyAsync(a => a.ID == fa && a.PatientId == input.PatientId && a.Status != HospitalAppointmentStatus.Cancelled, ct)) return HospitalResult.Fail("not_found");
			var nos = await Cases(ctx).Where(c => c.CaseNo.StartsWith("LC-")).Select(c => c.CaseNo).ToListAsync(ct);
			var c = new HospitalDentalLabCase
			{
				CompanyId = ctx.CompanyId, CaseNo = HospitalDentalPracticeRules.NextNo(nos, "LC-", 4), PatientId = input.PatientId, PlanLineId = input.PlanLineId, Teeth = Clip(input.Teeth, 20),
				CaseType = input.CaseType, Material = input.Material.Trim()[..Math.Min(60, input.Material.Trim().Length)], Shade = Clip(input.Shade, 10), LabId = input.LabId, DueDate = input.DueDate.Date,
				FitAppointmentId = input.FitAppointmentId, Cost = input.Cost, CreatedAt = nowUtc, CreatedByEmployeeId = employeeId,
			};
			_db.Add(c);
			var r = await SaveAsync(ct, null);
			if (!r.Ok) return r;
			// the fit appointment waits for this case
			if (input.FitAppointmentId is int fit) await Visits(ctx).Where(v => v.AppointmentId == fit).ExecuteUpdateAsync(u => u.SetProperty(v => v.LabCaseId, c.ID), ct);
			return HospitalResult.Success(c.ID);
		}

		public async Task<HospitalResult> MoveCaseAsync(BusinessContext ctx, int id, string to, string? fault, string? reason, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await Can(ctx, HospitalActions.Consult, ct) && !await Can(ctx, HospitalActions.Book, ct)) return HospitalResult.Fail("forbidden");
			var c = await Cases(ctx).AsNoTracking().FirstOrDefaultAsync(x => x.ID == id, ct);
			if (c == null) return HospitalResult.Fail("not_found");
			if (!HospitalDentalPracticeRules.CanMove(c.Stage, to)) return HospitalResult.Fail("This case cannot make that move");
			var why = Clip(reason, 300);
			if (to == HospitalLabCaseStage.Remake && (fault is not ("Lab" or "Clinic") || why == null)) return HospitalResult.Fail("Say whose fault the remake is, and why");
			int n = await Cases(ctx).Where(x => x.ID == id && x.Stage == c.Stage).ExecuteUpdateAsync(u => u
				.SetProperty(x => x.Stage, to)
				.SetProperty(x => x.SentAt, x => to == HospitalLabCaseStage.Sent ? nowUtc : x.SentAt)
				.SetProperty(x => x.ReceivedAt, x => to == HospitalLabCaseStage.Received ? nowUtc : x.ReceivedAt)
				.SetProperty(x => x.FittedAt, x => to == HospitalLabCaseStage.Fitted ? nowUtc : x.FittedAt)
				.SetProperty(x => x.RemakeFault, x => to == HospitalLabCaseStage.Remake ? fault : x.RemakeFault)
				.SetProperty(x => x.RemakeReason, x => to == HospitalLabCaseStage.Remake ? why : x.RemakeReason)
				.SetProperty(x => x.Remakes, x => to == HospitalLabCaseStage.Remake ? (byte)(x.Remakes + 1) : x.Remakes), ct);
			return n == 1 ? HospitalResult.Success(id) : HospitalResult.Fail(ConcurrentChange);
		}

		// ====================================================================================
		// RECALL AND STERILISATION
		// ====================================================================================

		public async Task<(IReadOnlyList<HospitalRecallRow> Rows, HospitalRecallStats Stats)> RecallsAsync(BusinessContext ctx, DateTime today, CancellationToken ct = default)
		{
			var q = from r in Recalls(ctx).AsNoTracking() join p in _db.HospitalPatients.AsNoTracking() on r.PatientId equals p.ID
					select new { r, p.Name, p.NameEn, p.Mrn, p.Phone, p.PhotoPath };
			var rows = await q.OrderBy(x => x.r.DueDate).Take(500).ToListAsync(ct);
			var ids = rows.Select(x => x.r.PatientId).ToList();
			// the next chair visit of each patient (booked, today or later)
			var next = await (from v in Visits(ctx) join a in Appts(ctx) on v.AppointmentId equals a.ID
							  where ids.Contains(a.PatientId) && a.AppointmentDate >= today.Date && a.Status == HospitalAppointmentStatus.Booked
							  select new { a.PatientId, At = a.AppointmentDate.AddMinutes(a.StartMinute) }).ToListAsync(ct);
			var list = rows.Select(x =>
			{
				DateTime? n = next.Where(y => y.PatientId == x.r.PatientId).Select(y => (DateTime?)y.At).Min();
				return new HospitalRecallRow(x.r, x.Name, x.NameEn, x.Mrn, x.Phone, x.PhotoPath, n, HospitalDentalPracticeRules.RecallState(x.r.DueDate, n, today));
			}).ToList();
			var dueSoon = list.Where(x => x.Recall.DueDate <= today.AddMonths(1)).ToList();
			int failed = await Cycles(ctx).CountAsync(c => c.Biological == HospitalIndicator.Fail || c.Chemical == HospitalIndicator.Fail, ct);
			return (list, new HospitalRecallStats(dueSoon.Count == 0 ? null : (int)Math.Round(dueSoon.Count(x => x.State == "Booked") * 100m / dueSoon.Count),
				list.Count(x => x.Recall.DueDate.Year == today.Year && x.Recall.DueDate.Month == today.Month), list.Count(x => x.State == "Overdue"), failed));
		}

		public async Task<HospitalResult> RecallContactAsync(BusinessContext ctx, int id, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await Can(ctx, HospitalActions.Book, ct)) return HospitalResult.Fail("forbidden");
			int n = await Recalls(ctx).Where(r => r.ID == id).ExecuteUpdateAsync(u => u.SetProperty(r => r.Attempts, r => (byte)(r.Attempts + 1)).SetProperty(r => r.LastContactAt, nowUtc).SetProperty(r => r.UpdatedAt, nowUtc), ct);
			return n == 1 ? HospitalResult.Success(id) : HospitalResult.Fail("not_found");
		}

		public async Task<IReadOnlyList<HospitalSterilRow>> CyclesAsync(BusinessContext ctx, CancellationToken ct = default)
		{
			var cycles = await Cycles(ctx).AsNoTracking().OrderByDescending(c => c.RunAt).Take(60).ToListAsync(ct);
			var ids = cycles.Select(c => c.ID).ToList();
			var uses = await (from u in _db.Set<HospitalSterilUse>().AsNoTracking() join p in _db.HospitalPatients.AsNoTracking() on u.PatientId equals p.ID
							  where ids.Contains(u.CycleId) select new { u.CycleId, p.Name, p.NameEn, p.Mrn }).ToListAsync(ct);
			var emps = cycles.Where(c => c.OperatorEmployeeId != null).Select(c => c.OperatorEmployeeId!.Value).Distinct().ToList();
			var names = await _db.Employee.AsNoTracking().Where(e => emps.Contains(e.ID)).Select(e => new { e.ID, Name = e.FullName, NameEn = e.FullNameEn }).ToListAsync(ct);
			var photos = await CrossBuy.BL.Platform.EmployeePhotos.ResolveAsync(_db, ctx, emps, ct);
			return cycles.Select(c => new HospitalSterilRow(c, uses.Count(u => u.CycleId == c.ID), names.FirstOrDefault(n => n.ID == c.OperatorEmployeeId) is { } op ? DisplayName.Of(op.Name, op.NameEn ?? op.Name) : null, names.FirstOrDefault(n => n.ID == c.OperatorEmployeeId)?.NameEn,
				c.OperatorEmployeeId is int e ? photos.GetValueOrDefault(e) : null,
				uses.Where(u => u.CycleId == c.ID).Select(u => DisplayName.Of(u.Name, u.NameEn) + " — " + u.Mrn).ToList())).ToList();
		}

		public async Task<HospitalResult> CreateCycleAsync(BusinessContext ctx, HospitalSterilCycleInput input, int? employeeId, DateTime nowUtc, DateTime nowLocal, CancellationToken ct = default)
		{
			if (!await Can(ctx, HospitalActions.Consult, ct)) return HospitalResult.Fail("forbidden");
			var unit = Clip(input.Unit, 60); var load = Clip(input.Load, 300);
			if (unit == null || load == null) return HospitalResult.Fail("Enter the unit and what the load holds");
			var run = input.RunAt ?? nowLocal;
			if (run > nowLocal.AddMinutes(5)) return HospitalResult.Fail("A cycle cannot be recorded before it runs");
			string prefix = "AC-" + run.ToString("yyyy-MMdd") + "-";
			var nos = await Cycles(ctx).Where(c => c.CycleNo.StartsWith(prefix)).Select(c => c.CycleNo).ToListAsync(ct);
			var c = new HospitalSterilCycle { CompanyId = ctx.CompanyId, CycleNo = HospitalDentalPracticeRules.NextNo(nos, prefix, 2), Unit = unit, Load = load, RunAt = run, OperatorEmployeeId = employeeId, CreatedAt = nowUtc };
			_db.Add(c);
			return await SaveAsync(ct, null) is { Ok: true } ? HospitalResult.Success(c.ID) : HospitalResult.Fail(ConcurrentChange);
		}

		/// An indicator's result. Only a pending one is recorded: a result is never rewritten.
		public async Task<HospitalResult> RecordIndicatorAsync(BusinessContext ctx, int id, string kind, string result, CancellationToken ct = default)
		{
			if (!await Can(ctx, HospitalActions.Consult, ct)) return HospitalResult.Fail("forbidden");
			if (result is not (HospitalIndicator.Pass or HospitalIndicator.Fail)) return HospitalResult.Fail("Record pass or fail");
			int n = kind switch
			{
				"Chemical" => await Cycles(ctx).Where(c => c.ID == id && c.Chemical == HospitalIndicator.Pending).ExecuteUpdateAsync(u => u.SetProperty(c => c.Chemical, result), ct),
				"Biological" => await Cycles(ctx).Where(c => c.ID == id && c.Biological == HospitalIndicator.Pending).ExecuteUpdateAsync(u => u.SetProperty(c => c.Biological, result), ct),
				_ => -1,
			};
			return n == 1 ? HospitalResult.Success(id) : n == -1 ? HospitalResult.Fail("not_found") : HospitalResult.Fail("This indicator is already recorded");
		}

		/// A kit from a cycle, used on a patient (refused from a failed cycle).
		public async Task<HospitalResult> UseKitAsync(BusinessContext ctx, int cycleId, int patientId, int? appointmentId, int? employeeId, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await Can(ctx, HospitalActions.Consult, ct)) return HospitalResult.Fail("forbidden");
			var c = await Cycles(ctx).AsNoTracking().FirstOrDefaultAsync(x => x.ID == cycleId, ct);
			if (c == null || !await _db.HospitalPatients.AnyAsync(p => p.ID == patientId && p.CompanyId == ctx.CompanyId, ct)) return HospitalResult.Fail("not_found");
			var why = HospitalDentalPracticeRules.UseRefusal(c);
			if (why != null) return HospitalResult.Fail(why);
			_db.Add(new HospitalSterilUse { CompanyId = ctx.CompanyId, CycleId = cycleId, PatientId = patientId, AppointmentId = appointmentId, UsedAt = nowUtc, UsedByEmployeeId = employeeId });
			return await SaveAsync(ct, cycleId);
		}
	}
}
