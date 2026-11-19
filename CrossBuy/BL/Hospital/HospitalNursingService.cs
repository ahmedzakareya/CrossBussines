using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Hospital;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL.Hospital
{
	// ==========================================================================================
	// HOSPITAL H14 — the nursing station of a ward.
	//
	//   · Who is on the ward: the patients whose open bed stay (H3) is in one of the ward's rooms.
	//   · Medication orders (`consult`, the doctor's): drug, dose, route, the ward-clock hours or "as needed",
	//     controlled or not (HospitalNursingRules.OrderRefusal). Stopping one ends its future doses.
	//   · The MAR (`admit`, the ward desk's — nurses work on it): one answer per scheduled dose, given / held /
	//     missed, the reason when not given, a witness for a controlled drug (HospitalNursingRules.RecordRefusal).
	//     The unique dose index lets only one answer land.
	//   · Nursing tasks and the shift handover (`admit`).
	//   · Nothing leaves the stock or reaches a bill here: dispensing is H10's, and charging inpatient doses is
	//     an integration of its own. No job runs. The entities reach the model through HospitalNursingModel.Configure.
	// ==========================================================================================

	public sealed class HospitalMedOrderInput
	{
		public int AdmissionId { get; set; }
		public string Drug { get; set; } = "";
		public int? ItemId { get; set; }
		public string Dose { get; set; } = "";
		public string Route { get; set; } = HospitalMedRoute.Oral;
		public string? Hours { get; set; }
		public bool AsNeeded { get; set; }
		public bool Controlled { get; set; }
		public string? Note { get; set; }
		/// H73: why the order is still right past a stopping alert (an interaction or a dose above its limit).
		public string? OverrideReason { get; set; }
	}

	public sealed class HospitalMarInput
	{
		public int OrderId { get; set; }
		/// The scheduled dose (UTC, as the board sent it); null for an as-needed dose.
		public DateTime? DueAt { get; set; }
		public string Status { get; set; } = HospitalMarStatus.Given;
		public string? Reason { get; set; }
		public int? WitnessEmployeeId { get; set; }
		/// H205: what the nurse scanned at the bedside — the patient's wristband and the medicine's pack — or, when that
		/// cannot be done, why (the dose is then an override that counts against the ward's scanning compliance).
		public string? Wristband { get; set; }
		public string? Pack { get; set; }
		public string? ScanOverride { get; set; }
	}

	public sealed record HospitalWardOption(int Id, string Code, string Name, string? NameEn);

	public sealed record HospitalMarDose(DateTime DueAt, int Hour, string State, HospitalMarEntry? Entry);

	public sealed record HospitalMarLine(HospitalMedOrder Order, IReadOnlyList<HospitalMarDose> Doses, IReadOnlyList<HospitalMarEntry> AsNeededGiven);

	public sealed record HospitalWardPatient(int AdmissionId, int PatientId, string Name, string NameEn, string Mrn, string? Photo, string Bed, string? Allergies,
		IReadOnlyList<HospitalMarLine> Lines, IReadOnlyList<HospitalNursingTask> Tasks);

	public sealed record HospitalNursingBoard(HospitalWardOption Ward, IReadOnlyList<HospitalWardPatient> Patients, IReadOnlyList<int> Hours,
		int DueNow, int Overdue, int TasksDue, string Shift, DateTime ShiftDate, HospitalShiftHandover? Handover, IReadOnlyDictionary<int, string> People);

	public sealed class HospitalNursingService
	{
		private readonly CrossDbContext _db;
		private readonly IHospitalAccessService _access;

		public HospitalNursingService(CrossDbContext db, IHospitalAccessService access) { _db = db; _access = access; }

		public const string ConcurrentChange = HospitalInsuranceService.ConcurrentChange;
		private Task<bool> Can(BusinessContext ctx, string action, CancellationToken ct) => _access.CanAsync(ctx, action, null, ct);
		private IQueryable<HospitalMedOrder> Orders(BusinessContext ctx) => _db.Set<HospitalMedOrder>().Where(o => o.CompanyId == ctx.CompanyId);
		private IQueryable<HospitalMarEntry> Mar(BusinessContext ctx) => _db.Set<HospitalMarEntry>().Where(m => m.CompanyId == ctx.CompanyId);
		private IQueryable<HospitalNursingTask> Tasks(BusinessContext ctx) => _db.Set<HospitalNursingTask>().Where(t => t.CompanyId == ctx.CompanyId);
		private static string? Clip(string? s, int max) => string.IsNullOrWhiteSpace(s) ? null : s.Trim()[..Math.Min(max, s.Trim().Length)];

		/// Ward clock → UTC. The ward runs on the server's zone, as every hospital screen's "today" does.
		public static DateTime WardToUtc(DateTime wardTime) => DateTime.SpecifyKind(wardTime, DateTimeKind.Local).ToUniversalTime();
		public static DateTime UtcToWard(DateTime utc) => DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToLocalTime();

		public async Task<IReadOnlyList<HospitalWardOption>> WardsAsync(BusinessContext ctx, CancellationToken ct = default)
			=> await _db.HospitalWards.AsNoTracking().Where(w => w.CompanyId == ctx.CompanyId && w.IsActive).OrderBy(w => w.Code)
				.Select(w => new HospitalWardOption(w.ID, w.Code, w.Name, w.NameEn)).ToListAsync(ct);

		/// The active admission of a ward patient, checked against the company (the doctor's and the nurse's writes start here).
		private Task<HospitalAdmission?> AdmissionAsync(BusinessContext ctx, int admissionId, CancellationToken ct)
			=> _db.HospitalAdmissions.FirstOrDefaultAsync(a => a.ID == admissionId && a.CompanyId == ctx.CompanyId && a.Status == HospitalAdmissionStatus.Active, ct)!;

		public async Task<HospitalNursingBoard?> BoardAsync(BusinessContext ctx, int wardId, DateTime nowUtc, CancellationToken ct = default)
		{
			var ward = await _db.HospitalWards.AsNoTracking().Where(w => w.ID == wardId && w.CompanyId == ctx.CompanyId).Select(w => new HospitalWardOption(w.ID, w.Code, w.Name, w.NameEn)).FirstOrDefaultAsync(ct);
			if (ward == null) return null;
			var wardNow = UtcToWard(nowUtc);
			var day = wardNow.Date;
			DateTime dayStartUtc = WardToUtc(day), dayEndUtc = WardToUtc(day.AddDays(1));
			var here = await (from s in _db.HospitalBedStays.AsNoTracking()
							  join b in _db.HospitalBeds.AsNoTracking() on s.BedId equals b.ID
							  join r in _db.HospitalRooms.AsNoTracking() on s.RoomId equals r.ID
							  join a in _db.HospitalAdmissions.AsNoTracking() on s.AdmissionId equals a.ID
							  join p in _db.HospitalPatients.AsNoTracking() on s.PatientId equals p.ID
							  where s.CompanyId == ctx.CompanyId && s.EndedAt == null && r.WardId == wardId && a.Status == HospitalAdmissionStatus.Active && p.CompanyId == ctx.CompanyId
							  orderby b.Code
							  select new { s.AdmissionId, p.ID, p.Name, p.NameEn, p.Mrn, p.PhotoPath, p.Allergies, Bed = b.Code }).ToListAsync(ct);
			var admissions = here.Select(h => h.AdmissionId).ToList();
			var orders = await Orders(ctx).AsNoTracking().Where(o => admissions.Contains(o.AdmissionId) && (o.Status == HospitalMedOrderStatus.Active || o.StoppedAt >= dayStartUtc))
				.OrderBy(o => o.CreatedAt).ToListAsync(ct);
			var orderIds = orders.Select(o => o.ID).ToList();
			var entries = await Mar(ctx).AsNoTracking().Where(m => orderIds.Contains(m.OrderId) && ((m.DueAt >= dayStartUtc && m.DueAt < dayEndUtc) || (m.DueAt == null && m.RecordedAt >= dayStartUtc))).ToListAsync(ct);
			var tasks = await Tasks(ctx).AsNoTracking().Where(t => admissions.Contains(t.AdmissionId) && (t.DoneAt == null || t.DoneAt >= dayStartUtc)).OrderBy(t => t.DueAt).ToListAsync(ct);

			int dueNow = 0, overdue = 0;
			var hoursUsed = new SortedSet<int>();
			var patients = here.Select(h =>
			{
				var lines = orders.Where(o => o.AdmissionId == h.AdmissionId).Select(o =>
				{
					var hours = HospitalNursingRules.ParseHours(o.Hours) ?? Array.Empty<int>();
					var doses = HospitalNursingRules.DosesOn(day, hours, o.StartAt, o.StoppedAt, WardToUtc).Select(t =>
					{
						var entry = entries.FirstOrDefault(m => m.OrderId == o.ID && m.DueAt == t);
						string state = entry?.Status ?? HospitalNursingRules.DoseState(t, nowUtc);
						if (entry == null && state == HospitalNursingRules.Due) dueNow++;
						if (entry == null && state == HospitalNursingRules.Overdue) overdue++;
						int hour = UtcToWard(t).Hour; hoursUsed.Add(hour);
						return new HospitalMarDose(t, hour, state, entry);
					}).ToList();
					return new HospitalMarLine(o, doses, entries.Where(m => m.OrderId == o.ID && m.DueAt == null).OrderBy(m => m.RecordedAt).ToList());
				}).ToList();
				return new HospitalWardPatient(h.AdmissionId, h.ID, h.Name, h.NameEn, h.Mrn, h.PhotoPath, h.Bed, h.Allergies, lines, tasks.Where(t => t.AdmissionId == h.AdmissionId).ToList());
			}).ToList();
			int tasksDue = tasks.Count(t => t.DoneAt == null && t.DueAt <= nowUtc.AddHours(1));
			string shift = HospitalNursingRules.ShiftOf(wardNow);
			var shiftDate = HospitalNursingRules.ShiftDate(wardNow);
			var handover = await _db.Set<HospitalShiftHandover>().AsNoTracking().FirstOrDefaultAsync(x => x.CompanyId == ctx.CompanyId && x.WardId == wardId && x.ShiftDate == shiftDate && x.Shift == shift, ct);
			var ids = entries.SelectMany(m => new[] { m.RecordedByEmployeeId, m.WitnessEmployeeId }).Concat(tasks.Select(t => t.DoneByEmployeeId)).Append(handover?.UpdatedByEmployeeId)
				.Where(x => x != null).Select(x => x!.Value).Distinct().ToList();
			var people = await _db.Employee.AsNoTracking().Where(e => ids.Contains(e.ID) && e.EmpCompanyID == ctx.CompanyId)
				.Select(e => new { e.ID, e.FullName, e.FullNameEn }).ToDictionaryAsync(e => e.ID, e => DisplayName.Of(e.FullName ?? "", e.FullNameEn), ct);
			return new HospitalNursingBoard(ward, patients, hoursUsed.ToList(), dueNow, overdue, tasksDue, shift, shiftDate, handover, people);
		}

		/// The colleagues who can witness a controlled drug: the company's active employees, the recorder excluded on save.
		public async Task<IReadOnlyList<(int Id, string Name, string? Photo, string? NameEn)>> StaffAsync(BusinessContext ctx, CancellationToken ct = default)
		{
			var rows = await _db.Employee.AsNoTracking().Where(e => e.EmpCompanyID == ctx.CompanyId && e.IsActive).OrderBy(e => e.FullName).Take(500)
				.Select(e => new { e.ID, e.FullName, e.FullNameEn }).ToListAsync(ct);
			// the witness is chosen by face (the owner's rule)
			var photos = await CrossBuy.BL.Platform.EmployeePhotos.ResolveAsync(_db, ctx, rows.Select(e => e.ID), ct);
			return rows.Select(e => (e.ID, DisplayName.Of(e.FullName ?? "", e.FullNameEn), photos.GetValueOrDefault(e.ID), e.FullNameEn ?? e.FullName)).ToList();
		}

		public async Task<HospitalResult> AddOrderAsync(BusinessContext ctx, HospitalMedOrderInput input, int? employeeId, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await Can(ctx, HospitalActions.Consult, ct)) return HospitalResult.Fail("forbidden");
			var refusal = HospitalNursingRules.OrderRefusal(input.Drug, input.Dose, input.Route, input.Hours, input.AsNeeded);
			if (refusal != null) return HospitalResult.Fail(refusal);
			var adm = await AdmissionAsync(ctx, input.AdmissionId, ct);
			if (adm == null) return HospitalResult.Fail("not_found");
			if (input.ItemId is int item && !await _db.Items.AnyAsync(i => i.ID == item && i.CompanyID == ctx.CompanyId, ct))
				return HospitalResult.Fail("not_found");
			var o = new HospitalMedOrder
			{
				CompanyId = ctx.CompanyId, AdmissionId = adm.ID, PatientId = adm.PatientId, Drug = Clip(input.Drug, 200)!, ItemId = input.ItemId, Dose = Clip(input.Dose, 60)!,
				Route = input.Route, Hours = input.AsNeeded ? null : HospitalNursingRules.FormatHours(HospitalNursingRules.ParseHours(input.Hours)!), AsNeeded = input.AsNeeded,
				Controlled = input.Controlled, Note = Clip(input.Note, 300), StartAt = nowUtc, OrderedByEmployeeId = employeeId, CreatedAt = nowUtc,
			};
			_db.Set<HospitalMedOrder>().Add(o);
			await _db.SaveChangesAsync(ct); return HospitalResult.Success(o.ID);
		}

		public async Task<HospitalResult> StopOrderAsync(BusinessContext ctx, int orderId, int? employeeId, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await Can(ctx, HospitalActions.Consult, ct)) return HospitalResult.Fail("forbidden");
			if (!await Orders(ctx).AnyAsync(o => o.ID == orderId, ct)) return HospitalResult.Fail("not_found");
			int n = await Orders(ctx).Where(o => o.ID == orderId && o.Status == HospitalMedOrderStatus.Active).ExecuteUpdateAsync(u => u
				.SetProperty(o => o.Status, HospitalMedOrderStatus.Stopped).SetProperty(o => o.StoppedAt, nowUtc).SetProperty(o => o.StoppedByEmployeeId, employeeId), ct);
			return n == 1 ? HospitalResult.Success(orderId) : HospitalResult.Fail("This order is already stopped");
		}

		/// Records what happened to one dose. A scheduled dose must be one of the order's own doses; only one answer lands.
		public async Task<HospitalResult> RecordAsync(BusinessContext ctx, HospitalMarInput input, int? employeeId, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await Can(ctx, HospitalActions.Admit, ct)) return HospitalResult.Fail("forbidden");
			var o = await Orders(ctx).AsNoTracking().FirstOrDefaultAsync(x => x.ID == input.OrderId, ct);
			if (o == null) return HospitalResult.Fail("not_found");
			if (await AdmissionAsync(ctx, o.AdmissionId, ct) == null) return HospitalResult.Fail("The patient is no longer admitted");
			DateTime? due = input.DueAt == null ? null : DateTime.SpecifyKind(input.DueAt.Value, DateTimeKind.Utc);
			if (o.AsNeeded != (due == null)) return HospitalResult.Fail(o.AsNeeded ? "An as-needed dose has no scheduled time" : "Choose the scheduled dose");
			if (due is DateTime d)
			{
				var hours = HospitalNursingRules.ParseHours(o.Hours) ?? Array.Empty<int>();
				var wardDay = UtcToWard(d).Date;
				if (!HospitalNursingRules.DosesOn(wardDay, hours, o.StartAt, o.StoppedAt, WardToUtc).Contains(d)) return HospitalResult.Fail("This is not one of the order's doses");
			}
			else if (o.Status != HospitalMedOrderStatus.Active) return HospitalResult.Fail("This order is stopped");
			var refusal = HospitalNursingRules.RecordRefusal(input.Status, input.Reason, o.Controlled, employeeId, input.WitnessEmployeeId, due, nowUtc);
			if (refusal != null) return HospitalResult.Fail(refusal);
			if (input.WitnessEmployeeId is int w && !await _db.Employee.AnyAsync(e => e.ID == w && e.EmpCompanyID == ctx.CompanyId && e.IsActive, ct)) return HospitalResult.Fail("not_found");
			// H205: a dose GIVEN is scanned at the bedside (wristband + pack), or given with the reason it could not be
			HospitalBedsideScan? scan = null;
			if (input.Status == HospitalMarStatus.Given)
			{
				if (HospitalBedsideScanRules.Scanned(input.Wristband, input.Pack))
				{
					var mrn = await _db.HospitalPatients.AsNoTracking().Where(p => p.ID == o.PatientId && p.CompanyId == ctx.CompanyId).Select(p => p.Mrn).FirstAsync(ct);
					var (why, args, code) = await HospitalBedsideScanCheck.MedicationAsync(_db, ctx, o, mrn, input.Wristband!, input.Pack!, nowUtc, ct);
					if (why != null && !(why == HospitalBedsideScanRules.NoItem && !string.IsNullOrWhiteSpace(input.ScanOverride)))
					{
						// the scan that stopped the dose is kept: it is the near-miss the safety committee reads
						_db.Add(HospitalBedsideScanCheck.Row(ctx, HospitalBedsideScanKind.Medication, o.PatientId, o.AdmissionId, o.ID, input.Wristband, input.Pack, code,
							HospitalBedsideScanOutcome.Refused, HospitalBedsideScanCheck.Pack(why, args), employeeId, nowUtc));
						try { await _db.SaveChangesAsync(ct); } catch (DbUpdateException) { }
						return HospitalResult.Fail(why, args);
					}
					scan = why == null
						? HospitalBedsideScanCheck.Row(ctx, HospitalBedsideScanKind.Medication, o.PatientId, o.AdmissionId, o.ID, input.Wristband, input.Pack, code, HospitalBedsideScanOutcome.Matched, null, employeeId, nowUtc)
						: HospitalBedsideScanCheck.Row(ctx, HospitalBedsideScanKind.Medication, o.PatientId, o.AdmissionId, o.ID, input.Wristband, input.Pack, code, HospitalBedsideScanOutcome.Override, input.ScanOverride, employeeId, nowUtc);
				}
				else if (string.IsNullOrWhiteSpace(input.ScanOverride)) return HospitalResult.Fail(HospitalBedsideScanRules.ScanOrReason);
				else scan = HospitalBedsideScanCheck.Row(ctx, HospitalBedsideScanKind.Medication, o.PatientId, o.AdmissionId, o.ID, input.Wristband, input.Pack, null,
					HospitalBedsideScanOutcome.Override, input.ScanOverride, employeeId, nowUtc);
			}
			var m = new HospitalMarEntry
			{
				CompanyId = ctx.CompanyId, OrderId = o.ID, PatientId = o.PatientId, DueAt = due, Status = input.Status, Reason = Clip(input.Reason, 300),
				RecordedAt = nowUtc, RecordedByEmployeeId = employeeId, WitnessEmployeeId = input.Status == HospitalMarStatus.Given ? input.WitnessEmployeeId : null,
			};
			_db.Set<HospitalMarEntry>().Add(m);
			try { await _db.SaveChangesAsync(ct); }
			catch (DbUpdateException) { _db.Entry(m).State = EntityState.Detached; return HospitalResult.Fail("This dose is already recorded"); }
			if (scan != null)
			{
				scan.DoneId = m.ID;
				_db.Add(scan);
				try { await _db.SaveChangesAsync(ct); }
				catch (Microsoft.Data.SqlClient.SqlException) { _db.Entry(scan).State = EntityState.Detached; }   // the scan log not installed: the dose stands
				catch (DbUpdateException) { _db.Entry(scan).State = EntityState.Detached; }
			}
			return HospitalResult.Success(m.ID);
		}

		public async Task<HospitalResult> AddTaskAsync(BusinessContext ctx, int admissionId, string? title, DateTime dueUtc, int? employeeId, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await Can(ctx, HospitalActions.Admit, ct)) return HospitalResult.Fail("forbidden");
			if (string.IsNullOrWhiteSpace(title)) return HospitalResult.Fail("Write the task");
			var adm = await AdmissionAsync(ctx, admissionId, ct);
			if (adm == null) return HospitalResult.Fail("not_found");
			var t = new HospitalNursingTask { CompanyId = ctx.CompanyId, AdmissionId = adm.ID, PatientId = adm.PatientId, Title = Clip(title, 200)!, DueAt = DateTime.SpecifyKind(dueUtc, DateTimeKind.Utc), CreatedAt = nowUtc, CreatedByEmployeeId = employeeId };
			_db.Set<HospitalNursingTask>().Add(t);
			await _db.SaveChangesAsync(ct); return HospitalResult.Success(t.ID);
		}

		public async Task<HospitalResult> DoneTaskAsync(BusinessContext ctx, int taskId, int? employeeId, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await Can(ctx, HospitalActions.Admit, ct)) return HospitalResult.Fail("forbidden");
			if (!await Tasks(ctx).AnyAsync(t => t.ID == taskId, ct)) return HospitalResult.Fail("not_found");
			int n = await Tasks(ctx).Where(t => t.ID == taskId && t.DoneAt == null).ExecuteUpdateAsync(u => u.SetProperty(t => t.DoneAt, nowUtc).SetProperty(t => t.DoneByEmployeeId, employeeId), ct);
			return n == 1 ? HospitalResult.Success(taskId) : HospitalResult.Fail("This task is already done");
		}

		/// The handover of the current shift: one note per ward, date and shift, rewritten until the shift ends.
		public async Task<HospitalResult> SaveHandoverAsync(BusinessContext ctx, int wardId, string? note, int? employeeId, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await Can(ctx, HospitalActions.Admit, ct)) return HospitalResult.Fail("forbidden");
			if (string.IsNullOrWhiteSpace(note)) return HospitalResult.Fail("Write the handover");
			if (!await _db.HospitalWards.AnyAsync(w => w.ID == wardId && w.CompanyId == ctx.CompanyId, ct)) return HospitalResult.Fail("not_found");
			var wardNow = UtcToWard(nowUtc);
			string shift = HospitalNursingRules.ShiftOf(wardNow); var date = HospitalNursingRules.ShiftDate(wardNow);
			var h = await _db.Set<HospitalShiftHandover>().FirstOrDefaultAsync(x => x.CompanyId == ctx.CompanyId && x.WardId == wardId && x.ShiftDate == date && x.Shift == shift, ct);
			if (h == null) { h = new HospitalShiftHandover { CompanyId = ctx.CompanyId, WardId = wardId, ShiftDate = date, Shift = shift }; _db.Set<HospitalShiftHandover>().Add(h); }
			h.Note = Clip(note, 2000)!; h.UpdatedAt = nowUtc; h.UpdatedByEmployeeId = employeeId;
			try { await _db.SaveChangesAsync(ct); }
			catch (DbUpdateException) { _db.ChangeTracker.Clear(); return HospitalResult.Fail(ConcurrentChange); }
			return HospitalResult.Success(h.ID);
		}
	}
}
