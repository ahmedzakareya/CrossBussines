using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Hospital;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL.Hospital
{
	// ==========================================================================================
	// HOSPITAL H161 — clinic disruptions (see HospitalClinicDisruptions.cs).
	//   · DECLARE: the doctor, the day, the kind and the window (with the delay, the new room or the device). Every
	//     patient booked (or already arrived) with that doctor in the window is listed at once with a plan: a late
	//     doctor, a room change or a device down → keep and inform; an absent doctor → move, to a PROPOSED slot (another
	//     doctor of the same department that day, else the same doctor on a later working day; never one slot twice).
	//   · PREVIEW AND APPROVE: the desk changes any plan (keep / move / cancel) while the disruption is open; "apply"
	//     makes every move and cancellation (each one alone: a slot taken meanwhile leaves that row with the reason and
	//     the rest go on) and queues one message per patient saying what changed.
	//   · FOLLOW UP: a patient is "reached" only when a call confirms it; until then every one whose message was not
	//     sent (no mobile, opted out, no provider connected, failed) is on the list to call; each call is counted.
	//   · THE BOARD: an open late-doctor disruption turns the patients' lateness into the CLINIC's delay (+minutes).
	// ==========================================================================================

	public sealed class HospitalDisruptionInput
	{
		public int PractitionerId { get; set; }
		public DateTime? Day { get; set; }
		public string? Kind { get; set; }
		public int? FromMinute { get; set; }
		public int? ToMinute { get; set; }
		public int? DelayMinutes { get; set; }
		public string? Room { get; set; }
		public string? Equipment { get; set; }
		public string? Note { get; set; }
	}

	public static class HospitalDisruptionRules
	{
		public static string? Refuse(HospitalDisruptionInput i, DateTime todayLocal)
		{
			if (!HospitalDisruptionKind.All.Contains(i.Kind ?? "")) return "Choose what happened";
			if (i.Day is not DateTime d) return "Choose the day";
			if (d.Date < todayLocal.Date) return "A disruption cannot be declared for a past day";
			if (i.FromMinute is not int f || i.ToMinute is not int t || f < 0 || t > 24 * 60 || t <= f) return "Choose the hours it covers";
			if (i.Kind == HospitalDisruptionKind.Late && i.DelayMinutes is not (> 0 and <= 480)) return "Write the delay in minutes (up to 8 hours)";
			if (i.Kind == HospitalDisruptionKind.Room && string.IsNullOrWhiteSpace(i.Room)) return "Write the new room";
			if (i.Kind == HospitalDisruptionKind.Equipment && string.IsNullOrWhiteSpace(i.Equipment)) return "Write which device is down";
			return null;
		}

		public static string DefaultPlan(string kind) => kind == HospitalDisruptionKind.Absent ? HospitalDisruptionPlan.Move : HospitalDisruptionPlan.Keep;

		/// The patient's message in Modern Standard Arabic (the SMS language), from what changed for them.
		public static string Message(HospitalClinicDisruption d, HospitalDisruptionPatient r, string patient, string doctor, string? newDoctor, int startMinute, string? hospital)
		{
			string date = HospitalSmsRules.Date(d.Day), time = HospitalSmsRules.Time(startMinute), tail = string.IsNullOrWhiteSpace(hospital) ? "" : " " + hospital;
			if (r.Plan == HospitalDisruptionPlan.Cancel)
				return $"عزيزي {patient}، نعتذر عن إلغاء موعدك مع {doctor} يوم {date} الساعة {time}. يُرجى الاتصال بنا لحجز موعد جديد.{tail}";
			if (r.Plan == HospitalDisruptionPlan.Move && r.NewDay is DateTime nd && r.NewMinute is int nm)
				return $"عزيزي {patient}، نعتذر عن تعذّر موعدك مع {doctor} يوم {date}. نُقل موعدك إلى {newDoctor ?? doctor} يوم {HospitalSmsRules.Date(nd)} الساعة {HospitalSmsRules.Time(nm)}. للتعديل يُرجى الاتصال بنا.{tail}";
			return d.Kind switch
			{
				HospitalDisruptionKind.Late => $"عزيزي {patient}، نعتذر عن تأخر {doctor} اليوم نحو {d.DelayMinutes} دقيقة؛ موعدك المتوقع الساعة {HospitalSmsRules.Time(Math.Min(startMinute + (d.DelayMinutes ?? 0), 24 * 60 - 1))}.{tail}",
				HospitalDisruptionKind.Room => $"عزيزي {patient}، تغيّرت غرفة عيادة {doctor} يوم {date} إلى {d.Room}، وموعدك كما هو الساعة {time}.{tail}",
				HospitalDisruptionKind.Equipment => $"عزيزي {patient}، قد يتأخر جزء من خدمتك يوم {date} مع {doctor} بسبب عطل في {d.Equipment}، وموعدك كما هو الساعة {time}.{tail}",
				_ => $"عزيزي {patient}، طرأ تغيير على عيادة {doctor} يوم {date}، وسنتصل بك لترتيب موعدك.{tail}",
			};
		}
	}

	public sealed record HospitalDisruptionRow(HospitalDisruptionPatient R, string PatientName, string? PatientNameEn, string Mrn, int StartMinute, string? NewDoctor, string? NewDoctorEn, string? SmsStatus, string? SmsError)
	{
		public bool ToCall => R.AppliedAt != null && R.Reached != true && SmsStatus != HospitalSmsStatus.Sent;
	}
	public sealed record HospitalDisruptionListRow(HospitalClinicDisruption D, string Doctor, string? DoctorEn, int Patients, int ToCall);
	public sealed record HospitalDisruptionView(HospitalClinicDisruption D, string Doctor, string? DoctorEn, IReadOnlyList<HospitalDisruptionRow> Rows);

	public sealed class HospitalClinicDisruptionService
	{
		private readonly CrossDbContext _db;
		private readonly IHospitalAccessService _access;
		private readonly HospitalFrontDeskWorkService _work;
		private readonly HospitalOutpatientService _op;
		public HospitalClinicDisruptionService(CrossDbContext db, IHospitalAccessService access, HospitalFrontDeskWorkService work, HospitalOutpatientService op) { _db = db; _access = access; _work = work; _op = op; }

		private static bool Missing(Microsoft.Data.SqlClient.SqlException ex) => ex.Number == 208;
		private static string? Clip(string? s, int n) => string.IsNullOrWhiteSpace(s) ? null : s.Trim()[..Math.Min(n, s.Trim().Length)];
		private IQueryable<HospitalClinicDisruption> Ds(BusinessContext ctx) => _db.Set<HospitalClinicDisruption>().Where(d => d.CompanyId == ctx.CompanyId);
		private IQueryable<HospitalDisruptionPatient> Ps(BusinessContext ctx) => _db.Set<HospitalDisruptionPatient>().Where(p => p.CompanyId == ctx.CompanyId);

		public async Task<HospitalResult> DeclareAsync(BusinessContext ctx, HospitalDisruptionInput input, int? employeeId, DateTime nowLocal, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await _access.CanAsync(ctx, HospitalActions.Book, null, ct)) return HospitalResult.Fail("forbidden");
			var why = HospitalDisruptionRules.Refuse(input, nowLocal.Date);
			if (why != null) return HospitalResult.Fail(why);
			var doc = await _db.HospitalPractitioners.AsNoTracking().FirstOrDefaultAsync(x => x.ID == input.PractitionerId && x.CompanyId == ctx.CompanyId, ct);
			if (doc == null) return HospitalResult.Fail("not_found");
			var day = input.Day!.Value.Date;
			var appts = await _db.HospitalAppointments.AsNoTracking()
				.Where(a => a.CompanyId == ctx.CompanyId && a.PractitionerId == doc.ID && a.AppointmentDate == day && a.StartMinute >= input.FromMinute && a.StartMinute < input.ToMinute
					&& (a.Status == HospitalAppointmentStatus.Booked || a.Status == HospitalAppointmentStatus.Arrived))
				.OrderBy(a => a.StartMinute).ToListAsync(ct);
			var nos = await Ds(ctx).Select(x => x.DisruptionNo).ToListAsync(ct);
			var d = new HospitalClinicDisruption
			{
				CompanyId = ctx.CompanyId, DisruptionNo = HospitalDentalPracticeRules.NextNo(nos, "CD-", 6), PractitionerId = doc.ID, Day = day, Kind = input.Kind!,
				FromMinute = input.FromMinute!.Value, ToMinute = input.ToMinute!.Value, DelayMinutes = input.Kind == HospitalDisruptionKind.Late ? input.DelayMinutes : null,
				Room = input.Kind == HospitalDisruptionKind.Room ? Clip(input.Room, 60) : null, Equipment = input.Kind == HospitalDisruptionKind.Equipment ? Clip(input.Equipment, 120) : null,
				Note = Clip(input.Note, 300), Status = HospitalDisruptionStatus.Open, CreatedAt = nowUtc, CreatedByEmployeeId = employeeId,
			};
			await using var tx = await ScopedTx.BeginOrJoinAsync(_db);
			_db.Add(d);
			await _db.SaveChangesAsync(ct);
			var taken = new HashSet<(int, DateTime, int)>();
			foreach (var a in appts)
			{
				var row = new HospitalDisruptionPatient { CompanyId = ctx.CompanyId, DisruptionId = d.ID, AppointmentId = a.ID, PatientId = a.PatientId, Plan = HospitalDisruptionRules.DefaultPlan(d.Kind) };
				if (d.Kind is HospitalDisruptionKind.Absent or HospitalDisruptionKind.Equipment)
				{
					var p = await ProposeAsync(ctx, doc, d, a, taken, nowLocal, ct);
					if (p is var (pd, pday, pm)) { row.NewPractitionerId = pd; row.NewDay = pday; row.NewMinute = pm; taken.Add((pd, pday, pm)); }
					else if (row.Plan == HospitalDisruptionPlan.Move) row.Plan = HospitalDisruptionPlan.Keep;   // nothing free to propose: the desk decides
				}
				_db.Add(row);
			}
			await _db.SaveChangesAsync(ct);
			await tx.CommitAsync();
			return HospitalResult.Success(d.ID);
		}

		/// The first free slot for this patient: another doctor of the department that day (from the patient's own time),
		/// else the same doctor on a later working day (two weeks at most).
		private async Task<(int Doctor, DateTime Day, int Minute)?> ProposeAsync(BusinessContext ctx, HospitalPractitioner doc, HospitalClinicDisruption d, HospitalAppointment a,
			HashSet<(int, DateTime, int)> taken, DateTime nowLocal, CancellationToken ct)
		{
			if (doc.DepartmentId is int dep)
			{
				var peers = await _db.HospitalPractitioners.AsNoTracking().Where(x => x.CompanyId == ctx.CompanyId && x.IsActive && x.DepartmentId == dep && x.ID != doc.ID).OrderBy(x => x.ID).Select(x => x.ID).ToListAsync(ct);
				foreach (var peer in peers)
					foreach (var m in await _work.SlotsAsync(ctx, peer, d.Day, nowLocal, ct))
						if (m >= a.StartMinute && !taken.Contains((peer, d.Day, m))) return (peer, d.Day, m);
			}
			for (int i = 1; i <= 14; i++)
			{
				var day = d.Day.AddDays(i);
				foreach (var m in await _work.SlotsAsync(ctx, doc.ID, day, nowLocal, ct))
					if (!taken.Contains((doc.ID, day, m))) return (doc.ID, day, m);
			}
			return null;
		}

		public async Task<HospitalResult> SetPlanAsync(BusinessContext ctx, int rowId, string? plan, CancellationToken ct = default)
		{
			if (!await _access.CanAsync(ctx, HospitalActions.Book, null, ct)) return HospitalResult.Fail("forbidden");
			var r = await Ps(ctx).FirstOrDefaultAsync(x => x.ID == rowId, ct);
			if (r == null) return HospitalResult.Fail("not_found");
			var d = await Ds(ctx).AsNoTracking().FirstAsync(x => x.ID == r.DisruptionId, ct);
			if (d.Status != HospitalDisruptionStatus.Open || r.AppliedAt != null) return HospitalResult.Fail("The plans are applied already");
			if (!HospitalDisruptionPlan.All.Contains(plan ?? "")) return HospitalResult.Fail("Choose keep, move or cancel");
			if (plan == HospitalDisruptionPlan.Move && r.NewMinute == null) return HospitalResult.Fail("No free slot was found to move this patient to: keep or cancel");
			r.Plan = plan!;
			await _db.SaveChangesAsync(ct);
			return HospitalResult.Success(r.ID);
		}

		/// Makes every move and cancellation and queues each patient's message. Each row stands alone: one that fails
		/// keeps its reason and the others go on. A failed move rolls its own transaction back, which clears what this
		/// context tracks — so every row is read again and saved on its own, never carried across a step.
		public async Task<(HospitalResult Result, int Applied, int Failed)> ApplyAsync(BusinessContext ctx, int id, int? employeeId, DateTime nowLocal, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await _access.CanAsync(ctx, HospitalActions.Book, null, ct)) return (HospitalResult.Fail("forbidden"), 0, 0);
			var d = await Ds(ctx).AsNoTracking().FirstOrDefaultAsync(x => x.ID == id, ct);
			if (d == null) return (HospitalResult.Fail("not_found"), 0, 0);
			if (d.Status != HospitalDisruptionStatus.Open) return (HospitalResult.Fail("The plans are applied already"), 0, 0);
			var rowIds = await Ps(ctx).Where(r => r.DisruptionId == d.ID && r.AppliedAt == null).Select(r => r.ID).ToListAsync(ct);
			var doctors = await _db.HospitalPractitioners.AsNoTracking().Where(x => x.CompanyId == ctx.CompanyId).Select(x => new { x.ID, x.Name }).ToDictionaryAsync(x => x.ID, x => x.Name, ct);
			var co = await _db.Companies.AsNoTracking().Where(c => c.CompanyID == ctx.CompanyId).Select(c => new { c.CompanyName, c.ComoanyNameAr }).FirstOrDefaultAsync(ct);
			string? hospital = co?.ComoanyNameAr ?? co?.CompanyName;
			int ok = 0, failed = 0;
			foreach (var rid in rowIds)
			{
				var plan = await Ps(ctx).AsNoTracking().FirstAsync(x => x.ID == rid, ct);
				var a = await _db.HospitalAppointments.AsNoTracking().Where(x => x.ID == plan.AppointmentId && x.CompanyId == ctx.CompanyId).Select(x => new { x.StartMinute, x.Status }).FirstOrDefaultAsync(ct);
				string? error = null; int? newAppt = null;
				if (a == null || a.Status is not (HospitalAppointmentStatus.Booked or HospitalAppointmentStatus.Arrived)) error = "The appointment changed meanwhile";
				else
				{
					HospitalResult done = HospitalResult.Success(plan.AppointmentId);
					if (plan.Plan == HospitalDisruptionPlan.Move)
						done = await _work.RescheduleAsync(ctx, plan.AppointmentId, plan.NewPractitionerId!.Value, plan.NewDay!.Value, plan.NewMinute!.Value, employeeId, nowLocal, nowUtc, ct);
					else if (plan.Plan == HospitalDisruptionPlan.Cancel)
						done = await _op.MoveAsync(ctx, plan.AppointmentId, HospitalAppointmentStatus.Cancelled, employeeId, nowUtc, ct);
					if (!done.Ok) error = done.Error ?? "The change could not be saved";
					else if (plan.Plan == HospitalDisruptionPlan.Move) newAppt = done.Id;
				}
				var r = await Ps(ctx).FirstAsync(x => x.ID == rid, ct);   // tracked afresh, after whatever the step did to the tracker
				if (error != null) { r.ApplyError = Clip(error, 200); failed++; await _db.SaveChangesAsync(ct); continue; }
				r.NewAppointmentId = newAppt; r.AppliedAt = nowUtc; r.ApplyError = null; ok++;
				await _db.SaveChangesAsync(ct);
				var body = HospitalDisruptionRules.Message(d, r, await _db.HospitalPatients.AsNoTracking().Where(p => p.ID == r.PatientId && p.CompanyId == ctx.CompanyId).Select(p => p.Name).FirstAsync(ct),
					doctors.GetValueOrDefault(d.PractitionerId) ?? "", r.NewPractitionerId is int np ? doctors.GetValueOrDefault(np) : null, a!.StartMinute, hospital);
				var sms = await HospitalSmsService.QueueTextAsync(_db, ctx.CompanyId, r.PatientId, body, "ClinicDisruption", d.ID, $"cd:{d.ID}:{r.AppointmentId}", employeeId, nowUtc, ct);
				var again = await Ps(ctx).FirstAsync(x => x.ID == rid, ct);
				again.SmsMessageId = sms;
				await _db.SaveChangesAsync(ct);
			}
			if (failed == 0)
			{
				var dd = await Ds(ctx).FirstAsync(x => x.ID == id, ct);
				dd.Status = HospitalDisruptionStatus.Applied; dd.AppliedAt = nowUtc; dd.AppliedByEmployeeId = employeeId;
				await _db.SaveChangesAsync(ct);
			}
			return (HospitalResult.Success(d.ID), ok, failed);
		}

		/// A call to a patient: reached or not, with a word; counted.
		public async Task<HospitalResult> CallAsync(BusinessContext ctx, int rowId, bool reached, string? note, int? employeeId, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await _access.CanAsync(ctx, HospitalActions.Register, null, ct)) return HospitalResult.Fail("forbidden");
			var r = await Ps(ctx).FirstOrDefaultAsync(x => x.ID == rowId, ct);
			if (r == null) return HospitalResult.Fail("not_found");
			if (r.AppliedAt == null) return HospitalResult.Fail("Apply the plan before calling the patient");
			if (r.Reached == true) return HospitalResult.Fail("The patient is reached already");
			if (!reached && string.IsNullOrWhiteSpace(note)) return HospitalResult.Fail("Write what happened on the call");
			r.Reached = reached; r.Calls++; r.LastCallAt = nowUtc; r.LastCallByEmployeeId = employeeId; r.CallNote = Clip(note, 300);
			await _db.SaveChangesAsync(ct);
			return HospitalResult.Success(r.ID);
		}

		public async Task<HospitalResult> CloseAsync(BusinessContext ctx, int id, int? employeeId, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await _access.CanAsync(ctx, HospitalActions.Book, null, ct)) return HospitalResult.Fail("forbidden");
			var d = await Ds(ctx).FirstOrDefaultAsync(x => x.ID == id, ct);
			if (d == null) return HospitalResult.Fail("not_found");
			if (d.Status == HospitalDisruptionStatus.Closed) return HospitalResult.Fail("This disruption is closed already");
			if (d.Status == HospitalDisruptionStatus.Open && await Ps(ctx).AnyAsync(r => r.DisruptionId == d.ID && r.AppliedAt == null, ct)) return HospitalResult.Fail("Apply the plans before closing");
			d.Status = HospitalDisruptionStatus.Closed; d.ClosedAt = nowUtc; d.ClosedByEmployeeId = employeeId;
			await _db.SaveChangesAsync(ct);
			return HospitalResult.Success(d.ID);
		}

		public async Task<HospitalDisruptionView?> ViewAsync(BusinessContext ctx, int id, CancellationToken ct = default)
		{
			var d = await Ds(ctx).AsNoTracking().FirstOrDefaultAsync(x => x.ID == id, ct);
			if (d == null) return null;
			var rows = await Ps(ctx).AsNoTracking().Where(r => r.DisruptionId == d.ID).ToListAsync(ct);
			var doc = await _db.HospitalPractitioners.AsNoTracking().Where(x => x.ID == d.PractitionerId && x.CompanyId == ctx.CompanyId).Select(x => new { x.Name, x.NameEn }).FirstOrDefaultAsync(ct);
			return new HospitalDisruptionView(d, doc?.Name ?? "", doc?.NameEn, await RowsAsync(ctx, rows, ct));
		}

		private async Task<IReadOnlyList<HospitalDisruptionRow>> RowsAsync(BusinessContext ctx, List<HospitalDisruptionPatient> rows, CancellationToken ct)
		{
			if (rows.Count == 0) return Array.Empty<HospitalDisruptionRow>();
			var pids = rows.Select(r => r.PatientId).Distinct().ToList(); var aids = rows.Select(r => r.AppointmentId).ToList();
			var sids = rows.Where(r => r.SmsMessageId != null).Select(r => r.SmsMessageId!.Value).ToList();
			var pats = await _db.HospitalPatients.AsNoTracking().Where(p => p.CompanyId == ctx.CompanyId && pids.Contains(p.ID)).ToDictionaryAsync(p => p.ID, ct);
			var starts = await _db.HospitalAppointments.AsNoTracking().Where(a => a.CompanyId == ctx.CompanyId && aids.Contains(a.ID)).ToDictionaryAsync(a => a.ID, a => a.StartMinute, ct);
			var docs = await _db.HospitalPractitioners.AsNoTracking().Where(x => x.CompanyId == ctx.CompanyId).ToDictionaryAsync(x => x.ID, x => (x.Name, x.NameEn), ct);
			var sms = new Dictionary<int, (string Status, string? Error)>();
			try { sms = await _db.Set<HospitalSmsMessage>().AsNoTracking().Where(m => m.CompanyId == ctx.CompanyId && sids.Contains(m.ID)).ToDictionaryAsync(m => m.ID, m => (m.Status, m.Error), ct); }
			catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number is 207 or 208) { }
			return rows.OrderBy(r => starts.GetValueOrDefault(r.AppointmentId)).Select(r =>
			{
				var p = pats.GetValueOrDefault(r.PatientId); var nd = r.NewPractitionerId is int n ? docs.GetValueOrDefault(n) : default;
				var s = r.SmsMessageId is int sid && sms.TryGetValue(sid, out var x) ? x : (null, null);
				return new HospitalDisruptionRow(r, p?.Name ?? "", p?.NameEn, p?.Mrn ?? "", starts.GetValueOrDefault(r.AppointmentId), nd.Name, nd.NameEn, s.Item1, s.Item2);
			}).ToList();
		}

		/// The disruptions, newest first; "open" = not closed.
		public async Task<(IReadOnlyList<HospitalDisruptionListRow> Rows, int Total)> ListAsync(BusinessContext ctx, string? q, bool openOnly, int page, int size, CancellationToken ct = default)
		{
			IQueryable<HospitalClinicDisruption> ds;
			try { ds = Ds(ctx).AsNoTracking(); await ds.AnyAsync(ct); }
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex)) { return (Array.Empty<HospitalDisruptionListRow>(), 0); }
			if (openOnly) ds = ds.Where(d => d.Status != HospitalDisruptionStatus.Closed);
			var joined = from d in ds join p in _db.HospitalPractitioners.AsNoTracking() on d.PractitionerId equals p.ID select new { d, p.Name, p.NameEn };
			if (!string.IsNullOrWhiteSpace(q)) { var s = q.Trim(); joined = joined.Where(x => x.d.DisruptionNo.Contains(s) || x.Name.Contains(s) || x.NameEn.Contains(s)); }
			int total = await joined.CountAsync(ct);
			var page1 = await joined.OrderByDescending(x => x.d.ID).Skip((page - 1) * size).Take(size).ToListAsync(ct);
			var ids = page1.Select(x => x.d.ID).ToList();
			var rows = await Ps(ctx).AsNoTracking().Where(r => ids.Contains(r.DisruptionId)).ToListAsync(ct);
			var detail = (await RowsAsync(ctx, rows, ct)).ToLookup(r => r.R.DisruptionId);
			return (page1.Select(x => new HospitalDisruptionListRow(x.d, x.Name, x.NameEn, detail[x.d.ID].Count(), detail[x.d.ID].Count(r => r.ToCall))).ToList(), total);
		}

		/// Everyone still to call, across the disruptions not closed (the desk's tab).
		public async Task<IReadOnlyList<(HospitalClinicDisruption D, HospitalDisruptionRow Row)>> ToCallAsync(BusinessContext ctx, CancellationToken ct = default)
		{
			List<HospitalClinicDisruption> ds;
			try { ds = await Ds(ctx).AsNoTracking().Where(d => d.Status != HospitalDisruptionStatus.Closed).ToListAsync(ct); }
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex)) { return Array.Empty<(HospitalClinicDisruption, HospitalDisruptionRow)>(); }
			if (ds.Count == 0) return Array.Empty<(HospitalClinicDisruption, HospitalDisruptionRow)>();
			var ids = ds.Select(d => d.ID).ToList();
			var rows = await Ps(ctx).AsNoTracking().Where(r => ids.Contains(r.DisruptionId) && r.AppliedAt != null && r.Reached != true).ToListAsync(ct);
			var byId = ds.ToDictionary(d => d.ID);
			return (await RowsAsync(ctx, rows, ct)).Where(r => r.ToCall).Select(r => (byId[r.R.DisruptionId], r)).ToList();
		}

		/// For the board: today's open late-doctor disruptions, as appointment → the clinic's delay in minutes.
		public async Task<IReadOnlyDictionary<int, int>> ClinicDelaysAsync(BusinessContext ctx, DateTime todayLocal, CancellationToken ct = default)
		{
			try
			{
				var day = todayLocal.Date;
				var late = await Ds(ctx).AsNoTracking().Where(d => d.Day == day && d.Kind == HospitalDisruptionKind.Late && d.Status != HospitalDisruptionStatus.Closed).Select(d => new { d.ID, d.DelayMinutes }).ToListAsync(ct);
				if (late.Count == 0) return new Dictionary<int, int>();
				var ids = late.Select(l => l.ID).ToList(); var by = late.ToDictionary(l => l.ID, l => l.DelayMinutes ?? 0);
				var rows = await Ps(ctx).AsNoTracking().Where(r => ids.Contains(r.DisruptionId)).Select(r => new { r.AppointmentId, r.DisruptionId }).ToListAsync(ct);
				return rows.GroupBy(r => r.AppointmentId).ToDictionary(g => g.Key, g => g.Max(r => by[r.DisruptionId]));
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex)) { return new Dictionary<int, int>(); }
		}
	}
}
