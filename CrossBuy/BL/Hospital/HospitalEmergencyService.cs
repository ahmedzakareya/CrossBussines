using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Hospital;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL.Hospital
{
	// ==========================================================================================
	// HOSPITAL H13 — the emergency department.
	//
	//   · Arrival (`admit`, the desk's work): a known patient, or an unknown one by a label, with how they came
	//     and the complaint. One open visit per known patient.
	//   · Triage (`admit`): the ESI level with its vital signs (HospitalEmergencyRules.TriageRefusal); the danger-zone
	//     vitals are flagged, the nurse decides. Re-triage is allowed while the visit is open.
	//   · The board (`admit`): the bay (one of the department's own bays — never a facility bed, those are admission's; one open visit per bay), the doctor (H4), and
	//     the moves along the lanes (HospitalEmergencyRules.CanMove). Taking a patient into treatment with a doctor
	//     stamps SeenAt, the end of door-to-doctor.
	//   · Closing (`consult` — the doctor's decision; `admit` for a patient who left without being seen):
	//     the outcome and its note. Admission itself stays H3's (the accommodation window).
	//   · Nothing is billed or posted here, and no job runs. The entity reaches the model through
	//     HospitalEmergencyModel.Configure (wired in CrossDbContext).
	// ==========================================================================================

	public sealed class HospitalErArrivalInput
	{
		public int? PatientId { get; set; }
		public string? UnknownLabel { get; set; }
		public string ArrivalMode { get; set; } = HospitalErArrival.WalkIn;
		public string Complaint { get; set; } = "";
		public int? Esi { get; set; }
	}

	public sealed class HospitalErTriageInput
	{
		public int Esi { get; set; }
		public int? BpSystolic { get; set; }
		public int? BpDiastolic { get; set; }
		public int? HeartRate { get; set; }
		public int? RespRate { get; set; }
		public decimal? TemperatureC { get; set; }
		public int? SpO2 { get; set; }
		public int? PainScore { get; set; }
		public string? Note { get; set; }
	}

	public sealed record HospitalErRow(HospitalErVisit Visit, string? PatientName, string? PatientNameEn, string? Mrn, string? Photo, string? Sex, DateTime? BirthDate, string? Allergies,
		string? Bay, string? Doctor, string? DoctorEn, string? DoctorPhoto, int WaitMinutes, bool DangerZone);

	public sealed record HospitalErBayRow(HospitalErBay Bay, int? VisitId);

	public sealed class HospitalErBayInput
	{
		public int? Id { get; set; }
		public string Code { get; set; } = "";
		public string Name { get; set; } = "";
		public string NameEn { get; set; } = "";
		public string Zone { get; set; } = HospitalErZone.Acute;
		public int SortOrder { get; set; }
		public bool IsActive { get; set; } = true;
	}

	public sealed record HospitalErStats(int InDepartment, int Capacity, int? DoorToDoctorToday, int? LongestWaitMinutes, int? LongestWaitEsi, decimal? LeftWithoutBeingSeenRate30d);

	public sealed class HospitalEmergencyService
	{
		private readonly CrossDbContext _db;
		private readonly IHospitalAccessService _access;

		public HospitalEmergencyService(CrossDbContext db, IHospitalAccessService access) { _db = db; _access = access; }

		public const string ConcurrentChange = HospitalInsuranceService.ConcurrentChange;
		private Task<bool> Can(BusinessContext ctx, string action, CancellationToken ct) => _access.CanAsync(ctx, action, null, ct);
		private IQueryable<HospitalErVisit> Visits(BusinessContext ctx) => _db.Set<HospitalErVisit>().Where(v => v.CompanyId == ctx.CompanyId);
		private static string? Clip(string? s, int max) => string.IsNullOrWhiteSpace(s) ? null : s.Trim()[..Math.Min(max, s.Trim().Length)];

		private IQueryable<HospitalErBay> Bays(BusinessContext ctx) => _db.Set<HospitalErBay>().Where(b => b.CompanyId == ctx.CompanyId);

		/// The department's bays (active only, or all for the setup), with the open visit in each.
		public async Task<IReadOnlyList<HospitalErBayRow>> BaysAsync(BusinessContext ctx, bool activeOnly = true, CancellationToken ct = default)
		{
			var bays = await Bays(ctx).AsNoTracking().Where(b => !activeOnly || b.IsActive).OrderBy(b => b.SortOrder).ThenBy(b => b.Code).ToListAsync(ct);
			var used = await Visits(ctx).AsNoTracking().Where(v => v.Stage != HospitalErStage.Closed && v.BayId != null).Select(v => new { v.BayId, v.ID }).ToListAsync(ct);
			return bays.Select(b => new HospitalErBayRow(b, used.FirstOrDefault(u => u.BayId == b.ID)?.ID)).ToList();
		}

		public async Task<HospitalResult> SaveBayAsync(BusinessContext ctx, HospitalErBayInput input, CancellationToken ct = default)
		{
			if (!await Can(ctx, HospitalActions.Manage, ct)) return HospitalResult.Fail("forbidden");
			var code = (input.Code ?? "").Trim().ToUpperInvariant();
			if (code.Length is 0 or > 20) return HospitalResult.Fail("Enter a code of up to 20 characters");
			if (string.IsNullOrWhiteSpace(input.Name) || string.IsNullOrWhiteSpace(input.NameEn)) return HospitalResult.Fail("Enter the name in both languages");
			if (!HospitalErZone.All.Contains(input.Zone)) return HospitalResult.Fail("Choose the zone");
			int self = input.Id ?? 0;
			if (await Bays(ctx).AnyAsync(b => b.Code == code && b.ID != self, ct)) return HospitalResult.Fail("Another bay uses this code");
			HospitalErBay? bay;
			if (input.Id is int id)
			{
				bay = await Bays(ctx).FirstOrDefaultAsync(b => b.ID == id, ct);
				if (bay == null) return HospitalResult.Fail("not_found");
				if (!input.IsActive && bay.IsActive && await Visits(ctx).AnyAsync(v => v.BayId == id && v.Stage != HospitalErStage.Closed, ct))
					return HospitalResult.Fail("A patient is in this bay; move them before closing it");
			}
			else { bay = new HospitalErBay { CompanyId = ctx.CompanyId }; _db.Set<HospitalErBay>().Add(bay); }
			bay.Code = code; bay.Name = input.Name.Trim(); bay.NameEn = input.NameEn.Trim(); bay.Zone = input.Zone; bay.SortOrder = input.SortOrder; bay.IsActive = input.IsActive;
			try { await _db.SaveChangesAsync(ct); }
			catch (DbUpdateException) { _db.ChangeTracker.Clear(); return HospitalResult.Fail(ConcurrentChange); }
			return HospitalResult.Success(bay.ID);
		}

		public async Task<IReadOnlyList<HospitalErRow>> BoardAsync(BusinessContext ctx, DateTime nowUtc, int? onlyId = null, CancellationToken ct = default)
		{
			var q = from v in Visits(ctx).AsNoTracking()
					join p0 in _db.HospitalPatients.AsNoTracking().Where(p => p.CompanyId == ctx.CompanyId) on v.PatientId equals p0.ID into ps
					from p in ps.DefaultIfEmpty()
					join b0 in Bays(ctx).AsNoTracking() on v.BayId equals b0.ID into bs
					from b in bs.DefaultIfEmpty()
					join d0 in _db.HospitalPractitioners.AsNoTracking().Where(d => d.CompanyId == ctx.CompanyId) on v.PractitionerId equals d0.ID into ds
					from d in ds.DefaultIfEmpty()
					where onlyId != null ? v.ID == onlyId : v.Stage != HospitalErStage.Closed
					select new { v, p, Bay = b == null ? null : b.Code, d };
			var rows = await q.Take(300).ToListAsync(ct);
			var emps = rows.Where(x => x.d?.EmployeeId != null).Select(x => x.d!.EmployeeId!.Value).Distinct().ToList();
			var photos = await CrossBuy.BL.Platform.EmployeePhotos.ResolveAsync(_db, ctx, emps, ct);
			var list = rows.Select(x => new HospitalErRow(x.v, x.p?.Name, x.p?.NameEn, x.p?.Mrn, x.p?.PhotoPath, x.p?.Sex, x.p?.BirthDate, x.p?.Allergies, x.Bay,
				x.d?.Name, x.d?.NameEn, x.d?.EmployeeId is int e ? photos.GetValueOrDefault(e) : null,
				HospitalEmergencyRules.WaitMinutes(x.v.ArrivedAt, x.v.SeenAt, x.v.ClosedAt ?? nowUtc),
				HospitalEmergencyRules.DangerZone(new HospitalErVitals(x.v.BpSystolic, x.v.HeartRate, x.v.RespRate, x.v.SpO2, x.v.TemperatureC))));
			return HospitalEmergencyRules.BoardOrder(list, r => r.Visit.Esi, r => r.Visit.ArrivedAt).ToList();
		}

		public async Task<HospitalErStats> StatsAsync(BusinessContext ctx, DateTime dayStartUtc, DateTime nowUtc, CancellationToken ct = default)
		{
			var open = await Visits(ctx).AsNoTracking().Where(v => v.Stage != HospitalErStage.Closed).Select(v => new { v.ArrivedAt, v.SeenAt, v.Esi, v.Stage }).ToListAsync(ct);
			int capacity = (await BaysAsync(ctx, true, ct)).Count;
			var today = await Visits(ctx).AsNoTracking().Where(v => v.ArrivedAt >= dayStartUtc).Select(v => new HospitalErTimes(v.ArrivedAt, v.SeenAt, v.Stage, v.Outcome)).ToListAsync(ct);
			var month = await Visits(ctx).AsNoTracking().Where(v => v.ArrivedAt >= nowUtc.AddDays(-30)).Select(v => new HospitalErTimes(v.ArrivedAt, v.SeenAt, v.Stage, v.Outcome)).ToListAsync(ct);
			var waiting = open.Where(v => v.SeenAt == null).OrderBy(v => v.ArrivedAt).FirstOrDefault();
			return new HospitalErStats(open.Count, capacity, HospitalEmergencyRules.DoorToDoctor(today),
				waiting == null ? null : HospitalEmergencyRules.WaitMinutes(waiting.ArrivedAt, null, nowUtc), waiting?.Esi, HospitalEmergencyRules.LeftWithoutBeingSeenRate(month));
		}

		private async Task<string> NextNoAsync(BusinessContext ctx, CancellationToken ct)
		{
			var nos = await Visits(ctx).Where(v => v.VisitNo.StartsWith("ER-")).Select(v => v.VisitNo).ToListAsync(ct);
			int n = 0;
			foreach (var no in nos) if (int.TryParse(no.AsSpan(3), out var x) && x > n) n = x;
			return "ER-" + (n + 1).ToString("000000");
		}

		public async Task<HospitalResult> ArriveAsync(BusinessContext ctx, HospitalErArrivalInput input, int? employeeId, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await Can(ctx, HospitalActions.Admit, ct)) return HospitalResult.Fail("forbidden");
			if (!HospitalErArrival.All.Contains(input.ArrivalMode)) return HospitalResult.Fail("Choose how the patient arrived");
			if (string.IsNullOrWhiteSpace(input.Complaint)) return HospitalResult.Fail("Enter the presenting complaint");
			if (input.Esi is < 1 or > 5) return HospitalResult.Fail("The triage level is 1 to 5");
			if (input.PatientId is int pid)
			{
				if (!await _db.HospitalPatients.AnyAsync(p => p.ID == pid && p.CompanyId == ctx.CompanyId, ct)) return HospitalResult.Fail("not_found");
				if (await Visits(ctx).AnyAsync(v => v.PatientId == pid && v.Stage != HospitalErStage.Closed, ct)) return HospitalResult.Fail("This patient is already in the department");
			}
			else if (string.IsNullOrWhiteSpace(input.UnknownLabel)) return HospitalResult.Fail("Choose the patient, or describe the unknown patient");
			var v = new HospitalErVisit
			{
				CompanyId = ctx.CompanyId, VisitNo = await NextNoAsync(ctx, ct), PatientId = input.PatientId, UnknownLabel = input.PatientId == null ? Clip(input.UnknownLabel, 100) : null,
				ArrivalMode = input.ArrivalMode, Complaint = Clip(input.Complaint, 300)!, ArrivedAt = nowUtc, CreatedAt = nowUtc, CreatedByEmployeeId = employeeId,
			};
			// ESI 1 is triaged on sight at the door: the arrival is the triage
			if (input.Esi == 1) { v.Esi = 1; v.TriagedAt = nowUtc; v.TriagedByEmployeeId = employeeId; v.Stage = HospitalErStage.Waiting; }
			_db.Set<HospitalErVisit>().Add(v);
			try { await _db.SaveChangesAsync(ct); }
			catch (DbUpdateException) { _db.Entry(v).State = EntityState.Detached; return HospitalResult.Fail(ConcurrentChange); }
			return HospitalResult.Success(v.ID);
		}

		public async Task<HospitalResult> TriageAsync(BusinessContext ctx, int id, HospitalErTriageInput input, int? employeeId, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await Can(ctx, HospitalActions.Admit, ct)) return HospitalResult.Fail("forbidden");
			var v = await Visits(ctx).FirstOrDefaultAsync(x => x.ID == id, ct);
			if (v == null) return HospitalResult.Fail("not_found");
			if (v.Stage == HospitalErStage.Closed) return HospitalResult.Fail("This visit is already closed");
			var vitals = new HospitalErVitals(input.BpSystolic, input.HeartRate, input.RespRate, input.SpO2, input.TemperatureC);
			var refusal = HospitalEmergencyRules.TriageRefusal(input.Esi, vitals);
			if (refusal != null) return HospitalResult.Fail(refusal);
			if (input.PainScore is < 0 or > 10) return HospitalResult.Fail("The pain score is 0 to 10");
			v.Esi = input.Esi; v.BpSystolic = input.BpSystolic; v.BpDiastolic = input.BpDiastolic; v.HeartRate = input.HeartRate; v.RespRate = input.RespRate;
			v.TemperatureC = input.TemperatureC; v.SpO2 = input.SpO2; v.PainScore = input.PainScore; v.TriageNote = Clip(input.Note, 500);
			v.TriagedAt = nowUtc; v.TriagedByEmployeeId = employeeId;
			if (v.Stage == HospitalErStage.Triage) v.Stage = HospitalErStage.Waiting;   // triaged: now waiting for the doctor
			await _db.SaveChangesAsync(ct); return HospitalResult.Success(v.ID);
		}

		/// Puts the patient in one of the department's bays, or takes them out of it (null).
		public async Task<HospitalResult> BayAsync(BusinessContext ctx, int id, int? bayId, CancellationToken ct = default)
		{
			if (!await Can(ctx, HospitalActions.Admit, ct)) return HospitalResult.Fail("forbidden");
			var v = await Visits(ctx).FirstOrDefaultAsync(x => x.ID == id, ct);
			if (v == null) return HospitalResult.Fail("not_found");
			if (v.Stage == HospitalErStage.Closed) return HospitalResult.Fail("This visit is already closed");
			if (bayId is int bid)
			{
				if (!await Bays(ctx).AnyAsync(b => b.ID == bid && b.IsActive, ct)) return HospitalResult.Fail("not_found");
				if (await Visits(ctx).AnyAsync(x => x.BayId == bid && x.ID != id && x.Stage != HospitalErStage.Closed, ct)) return HospitalResult.Fail("Another patient is in this bay");
			}
			v.BayId = bayId;
			try { await _db.SaveChangesAsync(ct); }
			catch (DbUpdateException) { _db.ChangeTracker.Clear(); return HospitalResult.Fail("Another patient is in this bay"); }
			return HospitalResult.Success(v.ID);
		}

		/// Moves the visit along the board. Into treatment it needs its doctor; the first time a doctor takes
		/// the patient, SeenAt is stamped (door-to-doctor ends).
		public async Task<HospitalResult> MoveAsync(BusinessContext ctx, int id, string stage, int? practitionerId, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await Can(ctx, HospitalActions.Admit, ct)) return HospitalResult.Fail("forbidden");
			var v = await Visits(ctx).FirstOrDefaultAsync(x => x.ID == id, ct);
			if (v == null) return HospitalResult.Fail("not_found");
			if (!HospitalErStage.Board.Contains(stage)) return HospitalResult.Fail("Choose where the patient goes");
			if (v.Stage == HospitalErStage.Triage && v.Esi == null) return HospitalResult.Fail("Triage the patient first");
			if (practitionerId is int did)
			{
				if (!await _db.HospitalPractitioners.AnyAsync(d => d.ID == did && d.CompanyId == ctx.CompanyId && d.IsActive, ct)) return HospitalResult.Fail("not_found");
				v.PractitionerId = did;
			}
			if (stage == HospitalErStage.Treatment && v.PractitionerId == null) return HospitalResult.Fail("Choose the doctor who takes the patient");
			if (!HospitalEmergencyRules.CanMove(v.Stage, stage, v.SeenAt != null || stage == HospitalErStage.Treatment)) return HospitalResult.Fail("The patient cannot go there from this lane");
			if (stage == HospitalErStage.Treatment && v.SeenAt == null) v.SeenAt = nowUtc;
			string from = v.Stage;
			v.Stage = stage;
			// guarded on the lane it was read in: two people moving one patient at once cannot both win
			int n = await Visits(ctx).Where(x => x.ID == id && x.Stage == from).ExecuteUpdateAsync(u => u
				.SetProperty(x => x.Stage, v.Stage).SetProperty(x => x.PractitionerId, v.PractitionerId).SetProperty(x => x.SeenAt, v.SeenAt), ct);
			_db.Entry(v).State = EntityState.Detached;
			return n == 1 ? HospitalResult.Success(id) : HospitalResult.Fail(ConcurrentChange);
		}

		/// Closes the visit with its outcome: the doctor's decision, or — before any doctor — a patient who left.
		public async Task<HospitalResult> CloseAsync(BusinessContext ctx, int id, string outcome, string? note, int? employeeId, DateTime nowUtc, CancellationToken ct = default)
		{
			var v = await Visits(ctx).AsNoTracking().FirstOrDefaultAsync(x => x.ID == id, ct);
			if (v == null) return HospitalResult.Fail("not_found");
			string action = outcome == HospitalErOutcome.LeftWithoutBeingSeen ? HospitalActions.Admit : HospitalActions.Consult;
			if (!await Can(ctx, action, ct)) return HospitalResult.Fail("forbidden");
			var refusal = HospitalEmergencyRules.CloseRefusal(v.Stage, v.SeenAt != null, outcome);
			if (refusal != null) return HospitalResult.Fail(refusal);
			string? why = Clip(note, 500);
			int n = await Visits(ctx).Where(x => x.ID == id && x.Stage == v.Stage).ExecuteUpdateAsync(u => u
				.SetProperty(x => x.Stage, HospitalErStage.Closed).SetProperty(x => x.Outcome, outcome).SetProperty(x => x.OutcomeNote, why)
				.SetProperty(x => x.ClosedAt, nowUtc).SetProperty(x => x.ClosedByEmployeeId, employeeId).SetProperty(x => x.BayId, (int?)null), ct);
			return n == 1 ? HospitalResult.Success(id) : HospitalResult.Fail(ConcurrentChange);
		}

		/// Links an unknown patient's visit to the patient file once they are identified (H2 registers them).
		public async Task<HospitalResult> IdentifyAsync(BusinessContext ctx, int id, int patientId, CancellationToken ct = default)
		{
			if (!await Can(ctx, HospitalActions.Register, ct)) return HospitalResult.Fail("forbidden");
			var v = await Visits(ctx).FirstOrDefaultAsync(x => x.ID == id, ct);
			if (v == null || !await _db.HospitalPatients.AnyAsync(p => p.ID == patientId && p.CompanyId == ctx.CompanyId, ct)) return HospitalResult.Fail("not_found");
			if (v.PatientId != null) return HospitalResult.Fail("This visit already has its patient");
			if (v.Stage != HospitalErStage.Closed && await Visits(ctx).AnyAsync(x => x.PatientId == patientId && x.ID != id && x.Stage != HospitalErStage.Closed, ct))
				return HospitalResult.Fail("This patient is already in the department");
			v.PatientId = patientId;
			try { await _db.SaveChangesAsync(ct); }
			catch (DbUpdateException) { _db.ChangeTracker.Clear(); return HospitalResult.Fail(ConcurrentChange); }
			return HospitalResult.Success(v.ID);
		}
	}
}
