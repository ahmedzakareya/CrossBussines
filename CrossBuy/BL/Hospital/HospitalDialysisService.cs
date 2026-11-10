using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Hospital;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL.Hospital
{
	// ==========================================================================================
	// HOSPITAL H87 — haemodialysis. The unit's day (who is due in which shift, who is on the machine, who did not come),
	// the patient's standing plan and history, and one session from the weight before to the urea after.
	// The targets are KDOQI's (2015 adequacy update): spKt/V at least 1.2 delivered, URR at least 65 %, an ultrafiltration
	// rate kept under 13 ml/kg/h; a gain between sessions above 4 % of the dry weight, a systolic fall of 20 mmHg or a
	// nadir under 90 are flagged as intradialytic risks.
	// ==========================================================================================
	public sealed class HospitalDialysisPlanInput
	{
		public int Id { get; set; }
		public int PatientId { get; set; }
		public decimal DryWeightKg { get; set; }
		public string? Access { get; set; }
		public int DurationMin { get; set; }
		public int BloodFlow { get; set; }
		public int? DialysateFlow { get; set; }
		public string? Dialyzer { get; set; }
		public string? Anticoagulation { get; set; }
		public int DaysMask { get; set; }
		public int Shift { get; set; }
		public string? Note { get; set; }
	}

	public sealed class HospitalDialysisStartInput
	{
		public int PlanId { get; set; }
		/// Ward-local; empty means now. The session's day is this time's day.
		public DateTime? At { get; set; }
		public string? Station { get; set; }
		public decimal? PreWeightKg { get; set; }
		public int? PreSbp { get; set; }
		public int? PreDbp { get; set; }
		public int? PrePulse { get; set; }
		/// Empty means the weight above the dry weight.
		public int? UfGoalMl { get; set; }
	}

	public sealed class HospitalDialysisObservationInput
	{
		public int SessionId { get; set; }
		public DateTime? At { get; set; }
		public int? Sbp { get; set; }
		public int? Dbp { get; set; }
		public int? Pulse { get; set; }
		public int? BloodFlow { get; set; }
		public int? VenousPressure { get; set; }
		public int? UfMl { get; set; }
		public string? Note { get; set; }
	}

	public sealed class HospitalDialysisFinishInput
	{
		public DateTime? At { get; set; }
		public decimal? PostWeightKg { get; set; }
		public int? PostSbp { get; set; }
		public int? PostDbp { get; set; }
		/// Empty means the weight lost.
		public int? UfMl { get; set; }
		public decimal? PreUrea { get; set; }
		public decimal? PostUrea { get; set; }
		public List<string>? Complications { get; set; }
		public string? Note { get; set; }
	}

	public sealed class HospitalDialysisMissInput
	{
		public int PlanId { get; set; }
		/// Ward-local day; empty means today.
		public DateTime? Date { get; set; }
		public string? Reason { get; set; }
	}

	public static class HospitalDialysisWarning
	{
		public const string HighGain = "HighGain";
		public const string HighUfRate = "HighUfRate";
		public const string LowUrr = "LowUrr";
		public const string LowKtv = "LowKtv";
		public const string Hypotension = "Hypotension";
		public const string Short = "Short";
		public const string AboveDry = "AboveDry";
	}

	/// What the board shows for a scheduled patient on the day.
	public static class HospitalDialysisDayState
	{
		public const string Due = "Due";
		public const string Running = "Running";
		public const string Done = "Done";
		public const string Missed = "Missed";
	}

	public sealed record HospitalDialysisOutcome(decimal? GainKg, decimal? GainPct, int? UfMl, decimal? UfRate, int? Minutes, decimal? Urr, decimal? KtV, int? NadirSbp, IReadOnlyList<string> Warnings);
	public sealed record HospitalDialysisBoardRow(HospitalDialysisPlan Plan, HospitalDialysisSession? Session, string Name, string NameEn, string Mrn, string? Photo, string State,
		HospitalDialysisObservation? Last, HospitalDialysisOutcome? Outcome, int? MinutesOn);
	public sealed record HospitalDialysisBoard(DateTime Day, IReadOnlyList<HospitalDialysisBoardRow> Rows, int Total, int Due, int Running, int Done, int Missed, int Warned);
	public sealed record HospitalDialysisPatientRow(HospitalDialysisPlan Plan, string Name, string NameEn, string Mrn, DateTime? LastSession, decimal? LastKtV, int Missed30, IReadOnlyList<string> LastWarnings);
	public sealed record HospitalDialysisHistoryRow(HospitalDialysisSession Session, HospitalDialysisOutcome Outcome);
	public sealed record HospitalDialysisPatientPage(int PatientId, string Name, string NameEn, string Mrn, string? Photo, HospitalDialysisPlan? Plan, IReadOnlyList<HospitalDialysisHistoryRow> History,
		decimal? AvgKtV, int Done30, int Missed30);
	public sealed record HospitalDialysisSessionPage(HospitalDialysisSession Session, HospitalDialysisPlan Plan, string Name, string NameEn, string Mrn, string? Photo, decimal? PrevPostKg,
		IReadOnlyList<HospitalDialysisObservation> Observations, HospitalDialysisOutcome Outcome, IReadOnlyDictionary<int, string> People);

	public static class HospitalDialysisRules
	{
		public const decimal MaxGainPct = 4m;
		public const decimal MaxUfRate = 13m;
		public const decimal MinUrr = 65m;
		public const decimal MinKtV = 1.2m;
		public const int HypotensionNadir = 90;
		public const int HypotensionFall = 20;
		public const int ShortByMin = 15;
		public const decimal AboveDryKg = 0.5m;

		/// The complications that are symptoms of a falling pressure.
		public static bool Symptomatic(string? complications) =>
			!string.IsNullOrEmpty(complications) && complications.Split(',').Any(c => c is HospitalDialysisComplication.Cramps or HospitalDialysisComplication.Nausea or HospitalDialysisComplication.Headache or HospitalDialysisComplication.ChestPain);

		public static int DayBit(DateTime day) => 1 << (int)day.DayOfWeek;
		public static bool Scheduled(int mask, DateTime day) => (mask & DayBit(day)) != 0;
		public static int DaysPerWeek(int mask) => Enumerable.Range(0, 7).Count(i => (mask & (1 << i)) != 0);

		/// The volume to remove: the weight above the dry weight, in millilitres (never negative).
		public static int UfGoal(decimal preKg, decimal dryKg) => (int)Math.Max(0, Math.Round((preKg - dryKg) * 1000m));

		public static decimal? Urr(decimal? pre, decimal? post) =>
			pre is decimal a && post is decimal b && a > 0 && b >= 0 && b < a ? Math.Round((a - b) / a * 100m, 1) : null;

		/// Single-pool Kt/V by Daugirdas' second-generation formula: −ln(R − 0.008·t) + (4 − 3.5·R) · UF / W,
		/// with R = post/pre urea, t the hours on the machine, UF the litres removed and W the weight after.
		public static decimal? KtV(decimal? preUrea, decimal? postUrea, double hours, int? ufMl, decimal? postKg)
		{
			if (preUrea is not decimal a || postUrea is not decimal b || a <= 0 || b < 0 || b >= a || hours <= 0 || postKg is not decimal w || w <= 0) return null;
			double r = (double)(b / a), inner = r - 0.008 * hours;
			if (inner <= 0) return null;
			double k = -Math.Log(inner) + (4 - 3.5 * r) * ((ufMl ?? 0) / 1000.0) / (double)w;
			return Math.Round((decimal)k, 2);
		}

		public static HospitalDialysisOutcome Outcome(HospitalDialysisSession s, decimal? prevPostKg, IEnumerable<HospitalDialysisObservation> obs)
		{
			decimal? gain = s.PreWeightKg is decimal pre && prevPostKg is decimal pp ? pre - pp : null;
			decimal? gainPct = gain is decimal g && s.DryWeightKg > 0 ? Math.Round(g / s.DryWeightKg * 100m, 1) : null;
			int? minutes = s.StartedAt != null && s.EndedAt != null ? (int)Math.Round((s.EndedAt.Value - s.StartedAt.Value).TotalMinutes) : null;
			int? uf = s.UfMl ?? (s.PreWeightKg is decimal a && s.PostWeightKg is decimal b ? (int)Math.Max(0, Math.Round((a - b) * 1000m)) : null);
			decimal? weight = s.PostWeightKg ?? (s.DryWeightKg > 0 ? s.DryWeightKg : null);
			decimal? rate = uf != null && minutes > 0 && weight > 0 ? Math.Round(uf.Value / weight.Value / (minutes.Value / 60m), 1) : null;
			var urr = Urr(s.PreUrea, s.PostUrea);
			var ktv = minutes > 0 ? KtV(s.PreUrea, s.PostUrea, minutes.Value / 60.0, uf, s.PostWeightKg) : null;
			var sbps = obs.Where(o => o.Sbp != null).Select(o => o.Sbp!.Value).ToList();
			if (s.PostSbp != null) sbps.Add(s.PostSbp.Value);
			int? nadir = sbps.Count == 0 ? null : sbps.Min();

			var w = new List<string>();
			if (gainPct > MaxGainPct) w.Add(HospitalDialysisWarning.HighGain);
			if (rate > MaxUfRate) w.Add(HospitalDialysisWarning.HighUfRate);
			if (urr < MinUrr) w.Add(HospitalDialysisWarning.LowUrr);
			if (ktv < MinKtV) w.Add(HospitalDialysisWarning.LowKtv);
			// KDOQI: a fall of 20 counts with symptoms; a nadir under 90 counts on its own (Flythe 2015)
			if (nadir < HypotensionNadir || (s.PreSbp != null && nadir != null && s.PreSbp - nadir >= HypotensionFall && Symptomatic(s.Complications))) w.Add(HospitalDialysisWarning.Hypotension);
			if (s.Status == HospitalDialysisSessionStatus.Done && minutes != null && minutes < s.PlannedMin - ShortByMin) w.Add(HospitalDialysisWarning.Short);
			if (s.PostWeightKg is decimal post && post > s.DryWeightKg + AboveDryKg) w.Add(HospitalDialysisWarning.AboveDry);
			return new HospitalDialysisOutcome(gain, gainPct, uf, rate, minutes, urr, ktv, nadir, w);
		}

		public static string? PlanRefusal(HospitalDialysisPlanInput i)
		{
			if (i.DryWeightKg is < 20 or > 250) return "The dry weight is between 20 and 250 kg";
			if (!HospitalDialysisAccess.All.Contains(i.Access ?? "")) return "Choose the vascular access";
			if (i.DurationMin is < 60 or > 480) return "A session lasts between 1 and 8 hours";
			if (i.BloodFlow is < 100 or > 600) return "The blood flow is between 100 and 600 ml/min";
			if (i.DialysateFlow is < 300 or > 1000) return "The dialysate flow is between 300 and 1000 ml/min";
			if (i.DaysMask is < 1 or > 127) return "Choose the dialysis days";
			if (i.Shift is < 1 or > 3) return "Choose the shift";
			return null;
		}

		private static string? BpRefusal(int? s, int? d)
		{
			if ((s == null) != (d == null)) return "Write both blood pressure numbers";
			if (s is < 50 or > 260 || d is < 20 or > 160 || (s != null && d >= s)) return "The blood pressure is not plausible";
			return null;
		}

		public static string? StartRefusal(HospitalDialysisStartInput i, DateTime atUtc, DateTime nowUtc)
		{
			if (atUtc > nowUtc.AddMinutes(5)) return "The time is in the future";
			if (atUtc < nowUtc.AddHours(-12)) return "A session's start can be written back 12 hours at most";
			if (i.PreWeightKg == null) return "Weigh the patient before the session";
			if (i.PreWeightKg is < 20 or > 260) return "The weight is not plausible";
			if (BpRefusal(i.PreSbp, i.PreDbp) is string bp) return bp;
			if (i.PrePulse is < 20 or > 250) return "The pulse is not plausible";
			if (i.UfGoalMl is < 0 or > 6000) return "The volume to remove is between 0 and 6000 ml";
			return null;
		}

		public static string? ObservationRefusal(HospitalDialysisObservationInput i, DateTime atUtc, DateTime startedUtc, DateTime nowUtc)
		{
			if (atUtc > nowUtc.AddMinutes(5)) return "The time is in the future";
			if (atUtc < startedUtc.AddMinutes(-5)) return "The time is before the session started";
			if (BpRefusal(i.Sbp, i.Dbp) is string bp) return bp;
			if (i.Pulse is < 20 or > 250) return "The pulse is not plausible";
			if (i.BloodFlow is < 0 or > 600) return "The blood flow is between 0 and 600 ml/min";
			if (i.VenousPressure is < -100 or > 400) return "The venous pressure is not plausible";
			if (i.UfMl is < 0 or > 6000) return "The volume removed is between 0 and 6000 ml";
			bool any = i.Sbp != null || i.Pulse != null || i.BloodFlow != null || i.VenousPressure != null || i.UfMl != null || !string.IsNullOrWhiteSpace(i.Note);
			return any ? null : "Write at least one measure or a note";
		}

		public static string? FinishRefusal(HospitalDialysisSession s, HospitalDialysisFinishInput i, DateTime atUtc, DateTime nowUtc)
		{
			if (s.Status != HospitalDialysisSessionStatus.Running) return "This session is not running";
			if (atUtc > nowUtc.AddMinutes(5)) return "The time is in the future";
			if (s.StartedAt != null && atUtc <= s.StartedAt.Value) return "The end is before the start";
			if (i.PostWeightKg == null) return "Weigh the patient after the session";
			if (i.PostWeightKg is < 20 or > 260) return "The weight is not plausible";
			if (s.PreWeightKg != null && i.PostWeightKg > s.PreWeightKg + 1) return "The weight after is more than a kilo above the weight before: check it";
			if (BpRefusal(i.PostSbp, i.PostDbp) is string bp) return bp;
			if (i.UfMl is < 0 or > 6000) return "The volume removed is between 0 and 6000 ml";
			if ((i.PreUrea == null) != (i.PostUrea == null)) return "Write both urea values, before and after";
			if (i.PreUrea != null && (i.PreUrea <= 0 || i.PostUrea < 0 || i.PostUrea >= i.PreUrea)) return "The urea after must be below the urea before";
			if (i.Complications != null && i.Complications.Any(c => !HospitalDialysisComplication.All.Contains(c))) return "Choose the complications from the list";
			return null;
		}

		public static string? MissRefusal(HospitalDialysisPlan p, DateTime day, DateTime today, string? reason)
		{
			if (p.Status != HospitalDialysisPlanStatus.Active) return "This dialysis plan is stopped";
			if (day > today) return "A day that has not come cannot be missed";
			if (day < today.AddDays(-7)) return "A missed session can be written back a week at most";
			if (!Scheduled(p.DaysMask, day)) return "The patient is not scheduled on that day";
			if (string.IsNullOrWhiteSpace(reason)) return "Write why the session was missed";
			return null;
		}
	}

	public sealed class HospitalDialysisService
	{
		public const string NoTables = "Dialysis is not installed yet (hospital_h87_dialysis.sql).";
		private readonly CrossDbContext _db;
		private readonly IHospitalAccessService _access;
		public HospitalDialysisService(CrossDbContext db, IHospitalAccessService access) { _db = db; _access = access; }
		private static bool Missing(Microsoft.Data.SqlClient.SqlException ex) => ex.Number is 207 or 208;
		private static string? Clip(string? s, int max) => string.IsNullOrWhiteSpace(s) ? null : s.Trim()[..Math.Min(max, s.Trim().Length)];
		private static DateTime Today(DateTime nowUtc) => HospitalNursingService.UtcToWard(nowUtc).Date;
		private IQueryable<HospitalDialysisPlan> Plans(BusinessContext ctx) => _db.Set<HospitalDialysisPlan>().Where(x => x.CompanyId == ctx.CompanyId);
		private IQueryable<HospitalDialysisSession> Sessions(BusinessContext ctx) => _db.Set<HospitalDialysisSession>().Where(x => x.CompanyId == ctx.CompanyId);
		private IQueryable<HospitalDialysisObservation> Obs(BusinessContext ctx) => _db.Set<HospitalDialysisObservation>().Where(x => x.CompanyId == ctx.CompanyId);

		public async Task<bool> CanTreatAsync(BusinessContext ctx, CancellationToken ct) =>
			await _access.CanAsync(ctx, HospitalActions.Admit, null, ct) || await _access.CanAsync(ctx, HospitalActions.Consult, null, ct);

		/// The weight after the patient's previous completed session, the baseline for the gain between sessions.
		private async Task<decimal?> PrevPostAsync(BusinessContext ctx, int patientId, DateTime day, CancellationToken ct) =>
			await Sessions(ctx).AsNoTracking().Where(s => s.PatientId == patientId && s.SessionDate < day && s.Status == HospitalDialysisSessionStatus.Done)
				.OrderByDescending(s => s.SessionDate).Select(s => s.PostWeightKg).FirstOrDefaultAsync(ct);

		public async Task<HospitalResult> SavePlanAsync(BusinessContext ctx, HospitalDialysisPlanInput input, int? employeeId, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await _access.CanAsync(ctx, HospitalActions.Consult, null, ct)) return HospitalResult.Fail("forbidden");
			if (HospitalDialysisRules.PlanRefusal(input) is string why) return HospitalResult.Fail(why);
			try
			{
				HospitalDialysisPlan row;
				if (input.Id == 0)
				{
					if (!await _db.HospitalPatients.AsNoTracking().AnyAsync(p => p.ID == input.PatientId && p.CompanyId == ctx.CompanyId, ct)) return HospitalResult.Fail("not_found");
					if (await Plans(ctx).AnyAsync(p => p.PatientId == input.PatientId && p.Status == HospitalDialysisPlanStatus.Active, ct)) return HospitalResult.Fail("This patient already has an active dialysis plan");
					row = new HospitalDialysisPlan { CompanyId = ctx.CompanyId, PatientId = input.PatientId, Status = HospitalDialysisPlanStatus.Active, CreatedAt = nowUtc, CreatedByEmployeeId = employeeId };
					_db.Add(row);
				}
				else
				{
					var found = await Plans(ctx).FirstOrDefaultAsync(p => p.ID == input.Id, ct);
					if (found == null) return HospitalResult.Fail("not_found");
					if (found.Status != HospitalDialysisPlanStatus.Active) return HospitalResult.Fail("This dialysis plan is stopped");
					row = found; row.UpdatedAt = nowUtc; row.UpdatedByEmployeeId = employeeId;
				}
				row.DryWeightKg = input.DryWeightKg; row.Access = input.Access!; row.DurationMin = input.DurationMin; row.BloodFlow = input.BloodFlow; row.DialysateFlow = input.DialysateFlow;
				row.Dialyzer = Clip(input.Dialyzer, 60); row.Anticoagulation = Clip(input.Anticoagulation, 100); row.DaysMask = input.DaysMask; row.Shift = input.Shift; row.Note = Clip(input.Note, 500);
				await _db.SaveChangesAsync(ct);
				return HospitalResult.Success(row.ID);
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex)) { return HospitalResult.Fail(NoTables); }
		}

		public async Task<HospitalResult> StopPlanAsync(BusinessContext ctx, int id, int? employeeId, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await _access.CanAsync(ctx, HospitalActions.Consult, null, ct)) return HospitalResult.Fail("forbidden");
			try
			{
				var p = await Plans(ctx).FirstOrDefaultAsync(x => x.ID == id, ct);
				if (p == null) return HospitalResult.Fail("not_found");
				if (p.Status != HospitalDialysisPlanStatus.Active) return HospitalResult.Fail("This dialysis plan is stopped");
				if (await Sessions(ctx).AnyAsync(s => s.PlanId == id && s.Status == HospitalDialysisSessionStatus.Running, ct)) return HospitalResult.Fail("Finish the running session first");
				p.Status = HospitalDialysisPlanStatus.Stopped; p.StoppedAt = nowUtc; p.UpdatedAt = nowUtc; p.UpdatedByEmployeeId = employeeId;
				await _db.SaveChangesAsync(ct);
				return HospitalResult.Success(p.ID);
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex)) { return HospitalResult.Fail(NoTables); }
		}

		public async Task<HospitalResult> StartAsync(BusinessContext ctx, HospitalDialysisStartInput input, int? employeeId, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await CanTreatAsync(ctx, ct)) return HospitalResult.Fail("forbidden");
			try
			{
				var p = await Plans(ctx).AsNoTracking().FirstOrDefaultAsync(x => x.ID == input.PlanId, ct);
				if (p == null) return HospitalResult.Fail("not_found");
				if (p.Status != HospitalDialysisPlanStatus.Active) return HospitalResult.Fail("This dialysis plan is stopped");
				var at = input.At == null ? nowUtc : HospitalNursingService.WardToUtc(input.At.Value);
				if (HospitalDialysisRules.StartRefusal(input, at, nowUtc) is string why) return HospitalResult.Fail(why);
				var day = HospitalNursingService.UtcToWard(at).Date;
				var existing = await Sessions(ctx).AsNoTracking().Where(s => s.PatientId == p.PatientId && s.SessionDate == day).Select(s => new { s.ID, s.Status }).FirstOrDefaultAsync(ct);
				if (existing != null)
					return existing.Status == HospitalDialysisSessionStatus.Running ? HospitalResult.Success(existing.ID)   // already on: go to it
						: HospitalResult.Fail(existing.Status == HospitalDialysisSessionStatus.Missed ? "This day is marked missed" : "The patient already had a session that day");
				var row = new HospitalDialysisSession
				{
					CompanyId = ctx.CompanyId, PlanId = p.ID, PatientId = p.PatientId, SessionDate = day, Shift = p.Shift, Station = Clip(input.Station, 20), Status = HospitalDialysisSessionStatus.Running,
					DryWeightKg = p.DryWeightKg, PlannedMin = p.DurationMin, StartedAt = at, PreWeightKg = input.PreWeightKg, PreSbp = input.PreSbp, PreDbp = input.PreDbp, PrePulse = input.PrePulse,
					UfGoalMl = input.UfGoalMl ?? HospitalDialysisRules.UfGoal(input.PreWeightKg!.Value, p.DryWeightKg), CreatedAt = nowUtc, CreatedByEmployeeId = employeeId,
				};
				_db.Add(row);
				await _db.SaveChangesAsync(ct);
				return HospitalResult.Success(row.ID);
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex)) { return HospitalResult.Fail(NoTables); }
		}

		public async Task<HospitalResult> ObserveAsync(BusinessContext ctx, HospitalDialysisObservationInput input, int? employeeId, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await CanTreatAsync(ctx, ct)) return HospitalResult.Fail("forbidden");
			try
			{
				var s = await Sessions(ctx).AsNoTracking().FirstOrDefaultAsync(x => x.ID == input.SessionId, ct);
				if (s == null) return HospitalResult.Fail("not_found");
				if (s.Status != HospitalDialysisSessionStatus.Running) return HospitalResult.Fail("This session is not running");
				var at = input.At == null ? nowUtc : HospitalNursingService.WardToUtc(input.At.Value);
				if (HospitalDialysisRules.ObservationRefusal(input, at, s.StartedAt ?? at, nowUtc) is string why) return HospitalResult.Fail(why);
				var row = new HospitalDialysisObservation
				{
					CompanyId = ctx.CompanyId, SessionId = s.ID, PatientId = s.PatientId, At = at, Sbp = input.Sbp, Dbp = input.Dbp, Pulse = input.Pulse, BloodFlow = input.BloodFlow,
					VenousPressure = input.VenousPressure, UfMl = input.UfMl, Note = Clip(input.Note, 300), CreatedAt = nowUtc, CreatedByEmployeeId = employeeId,
				};
				_db.Add(row);
				await _db.SaveChangesAsync(ct);
				return HospitalResult.Success(row.ID);
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex)) { return HospitalResult.Fail(NoTables); }
		}

		public async Task<HospitalResult> FinishAsync(BusinessContext ctx, int id, HospitalDialysisFinishInput input, int? employeeId, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await CanTreatAsync(ctx, ct)) return HospitalResult.Fail("forbidden");
			try
			{
				var s = await Sessions(ctx).FirstOrDefaultAsync(x => x.ID == id, ct);
				if (s == null) return HospitalResult.Fail("not_found");
				var at = input.At == null ? nowUtc : HospitalNursingService.WardToUtc(input.At.Value);
				if (HospitalDialysisRules.FinishRefusal(s, input, at, nowUtc) is string why) return HospitalResult.Fail(why);
				s.Status = HospitalDialysisSessionStatus.Done; s.EndedAt = at; s.PostWeightKg = input.PostWeightKg; s.PostSbp = input.PostSbp; s.PostDbp = input.PostDbp;
				s.UfMl = input.UfMl ?? (int)Math.Max(0, Math.Round((s.PreWeightKg!.Value - input.PostWeightKg!.Value) * 1000m));
				s.PreUrea = input.PreUrea; s.PostUrea = input.PostUrea;
				s.Complications = input.Complications == null || input.Complications.Count == 0 ? null : string.Join(",", input.Complications.Distinct());
				s.Note = Clip(input.Note, 500); s.ClosedByEmployeeId = employeeId;
				await _db.SaveChangesAsync(ct);
				return HospitalResult.Success(s.ID);
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex)) { return HospitalResult.Fail(NoTables); }
		}

		public async Task<HospitalResult> MissAsync(BusinessContext ctx, HospitalDialysisMissInput input, int? employeeId, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await CanTreatAsync(ctx, ct)) return HospitalResult.Fail("forbidden");
			try
			{
				var p = await Plans(ctx).AsNoTracking().FirstOrDefaultAsync(x => x.ID == input.PlanId, ct);
				if (p == null) return HospitalResult.Fail("not_found");
				var today = Today(nowUtc);
				var day = (input.Date ?? today).Date;
				if (HospitalDialysisRules.MissRefusal(p, day, today, input.Reason) is string why) return HospitalResult.Fail(why);
				if (await Sessions(ctx).AnyAsync(s => s.PatientId == p.PatientId && s.SessionDate == day, ct)) return HospitalResult.Fail("The patient already had a session that day");
				var row = new HospitalDialysisSession
				{
					CompanyId = ctx.CompanyId, PlanId = p.ID, PatientId = p.PatientId, SessionDate = day, Shift = p.Shift, Status = HospitalDialysisSessionStatus.Missed,
					DryWeightKg = p.DryWeightKg, PlannedMin = p.DurationMin, MissedReason = Clip(input.Reason, 200), CreatedAt = nowUtc, CreatedByEmployeeId = employeeId,
				};
				_db.Add(row);
				await _db.SaveChangesAsync(ct);
				return HospitalResult.Success(row.ID);
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex)) { return HospitalResult.Fail(NoTables); }
		}

		/// The unit's day: every active plan scheduled on it, plus any session held on it (an extra day, a stopped plan).
		/// The headline counts are over the whole day; the rows are searched, filtered and paged.
		public async Task<(HospitalDialysisBoard Board, bool Ready)> BoardAsync(BusinessContext ctx, DateTime? day, string? q, int? shift, string? state, int page, int pageSize, DateTime nowUtc, CancellationToken ct = default)
		{
			var d = (day ?? Today(nowUtc)).Date;
			try
			{
				var bit = HospitalDialysisRules.DayBit(d);
				var sessions = await Sessions(ctx).AsNoTracking().Where(s => s.SessionDate == d).ToListAsync(ct);
				var sPlanIds = sessions.Select(s => s.PlanId).ToList();
				var plans = await (from p in Plans(ctx).AsNoTracking()
								   join pt in _db.HospitalPatients.AsNoTracking() on p.PatientId equals pt.ID
								   where (p.Status == HospitalDialysisPlanStatus.Active && (p.DaysMask & bit) != 0) || sPlanIds.Contains(p.ID)
								   select new { p, pt.Name, pt.NameEn, pt.Mrn, pt.Phone, pt.PhotoPath }).ToListAsync(ct);
				var sIds = sessions.Select(s => s.ID).ToList();
				var obs = await Obs(ctx).AsNoTracking().Where(o => sIds.Contains(o.SessionId)).ToListAsync(ct);
				var pIds = plans.Select(x => x.p.PatientId).ToList();
				var prev = (await Sessions(ctx).AsNoTracking().Where(s => pIds.Contains(s.PatientId) && s.SessionDate < d && s.Status == HospitalDialysisSessionStatus.Done)
						.Select(s => new { s.PatientId, s.SessionDate, s.PostWeightKg }).ToListAsync(ct))
					.GroupBy(x => x.PatientId).ToDictionary(g => g.Key, g => g.OrderByDescending(x => x.SessionDate).First().PostWeightKg);
				var all = plans.Select(x =>
				{
					var s = sessions.FirstOrDefault(y => y.PatientId == x.p.PatientId);
					var mine = s == null ? new List<HospitalDialysisObservation>() : obs.Where(o => o.SessionId == s.ID).OrderBy(o => o.At).ToList();
					var outcome = s == null || s.Status == HospitalDialysisSessionStatus.Missed ? null : HospitalDialysisRules.Outcome(s, prev.GetValueOrDefault(x.p.PatientId), mine);
					var st = s == null ? HospitalDialysisDayState.Due : s.Status;
					int? on = s?.Status == HospitalDialysisSessionStatus.Running && s.StartedAt != null ? (int)Math.Round((nowUtc - s.StartedAt.Value).TotalMinutes) : null;
					return (Row: new HospitalDialysisBoardRow(x.p, s, x.Name, x.NameEn ?? "", x.Mrn, x.PhotoPath, st, mine.LastOrDefault(), outcome, on), Phone: (string?)x.Phone);
				}).ToList();
				int warned = all.Count(r => r.Row.Outcome?.Warnings.Count > 0);
				int Count(string s) => all.Count(r => r.Row.State == s);
				IEnumerable<(HospitalDialysisBoardRow Row, string? Phone)> f = all;
				if (!string.IsNullOrWhiteSpace(q)) { var s = q.Trim(); f = f.Where(r => r.Row.Name.Contains(s) || r.Row.NameEn.Contains(s, StringComparison.OrdinalIgnoreCase) || r.Row.Mrn.Contains(s) || (r.Row.Session?.Station ?? "").Contains(s) || (r.Phone ?? "").Contains(s)); }
				if (shift is >= 1 and <= 3) f = f.Where(r => (r.Row.Session?.Shift ?? r.Row.Plan.Shift) == shift);
				if (state == "warning") f = f.Where(r => r.Row.Outcome?.Warnings.Count > 0);
				else if (!string.IsNullOrEmpty(state)) f = f.Where(r => r.Row.State == state);
				var ordered = f.Select(r => r.Row)
					.OrderBy(r => r.State == HospitalDialysisDayState.Running ? 0 : r.State == HospitalDialysisDayState.Due ? 1 : r.State == HospitalDialysisDayState.Done ? 2 : 3)
					.ThenByDescending(r => r.Outcome?.Warnings.Count ?? 0).ThenBy(r => r.Session?.Shift ?? r.Plan.Shift).ThenBy(r => r.Name).ToList();
				var rows = ordered.Skip((page - 1) * pageSize).Take(pageSize).ToList();
				return (new HospitalDialysisBoard(d, rows, ordered.Count, Count(HospitalDialysisDayState.Due), Count(HospitalDialysisDayState.Running), Count(HospitalDialysisDayState.Done), Count(HospitalDialysisDayState.Missed), warned), true);
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex)) { return (new HospitalDialysisBoard(d, Array.Empty<HospitalDialysisBoardRow>(), 0, 0, 0, 0, 0, 0), false); }
		}

		/// Every patient on an active plan, searched and paged: their last session, last Kt/V and missed days in 30.
		public async Task<(IReadOnlyList<HospitalDialysisPatientRow> Rows, int Total, bool Ready)> PatientsAsync(BusinessContext ctx, string? q, int page, int pageSize, DateTime nowUtc, CancellationToken ct = default)
		{
			try
			{
				var query = from p in Plans(ctx).AsNoTracking()
							join pt in _db.HospitalPatients.AsNoTracking() on p.PatientId equals pt.ID
							where p.Status == HospitalDialysisPlanStatus.Active
							select new { p, pt.Name, pt.NameEn, pt.Mrn, pt.Phone };
				if (!string.IsNullOrWhiteSpace(q)) { var s = q.Trim(); query = query.Where(r => r.Name.Contains(s) || (r.NameEn != null && r.NameEn.Contains(s)) || r.Mrn.Contains(s) || (r.Phone != null && r.Phone.Contains(s))); }
				int total = await query.CountAsync(ct);
				var list = await query.OrderBy(r => r.p.Shift).ThenBy(r => r.Name).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
				var pIds = list.Select(r => r.p.PatientId).ToList();
				var since = Today(nowUtc).AddDays(-30);
				var recent = await Sessions(ctx).AsNoTracking().Where(s => pIds.Contains(s.PatientId) && s.SessionDate >= since.AddDays(-14)).ToListAsync(ct);
				var rows = list.Select(r =>
				{
					var mine = recent.Where(s => s.PatientId == r.p.PatientId).OrderBy(s => s.SessionDate).ToList();
					var done = mine.Where(s => s.Status == HospitalDialysisSessionStatus.Done).ToList();
					var last = done.LastOrDefault();
					var before = last == null ? null : done.Where(s => s.SessionDate < last.SessionDate).LastOrDefault()?.PostWeightKg;
					var o = last == null ? null : HospitalDialysisRules.Outcome(last, before, Array.Empty<HospitalDialysisObservation>());
					var ktv = done.Where(s => s.PreUrea != null).Select(s => HospitalDialysisRules.Outcome(s, null, Array.Empty<HospitalDialysisObservation>()).KtV).LastOrDefault(k => k != null);
					return new HospitalDialysisPatientRow(r.p, r.Name, r.NameEn ?? "", r.Mrn, last?.SessionDate, ktv,
						mine.Count(s => s.Status == HospitalDialysisSessionStatus.Missed && s.SessionDate >= since), o?.Warnings ?? Array.Empty<string>());
				}).ToList();
				return (rows, total, true);
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex)) { return (Array.Empty<HospitalDialysisPatientRow>(), 0, false); }
		}

		public async Task<HospitalDialysisPatientPage?> PatientPageAsync(BusinessContext ctx, int patientId, DateTime nowUtc, CancellationToken ct = default)
		{
			var pt = await _db.HospitalPatients.AsNoTracking().Where(x => x.ID == patientId && x.CompanyId == ctx.CompanyId).Select(x => new { x.ID, x.Name, x.NameEn, x.Mrn, x.PhotoPath }).FirstOrDefaultAsync(ct);
			if (pt == null) return null;
			try
			{
				var plan = await Plans(ctx).AsNoTracking().Where(p => p.PatientId == patientId).OrderByDescending(p => p.Status == HospitalDialysisPlanStatus.Active).ThenByDescending(p => p.ID).FirstOrDefaultAsync(ct);
				var sessions = await Sessions(ctx).AsNoTracking().Where(s => s.PatientId == patientId).OrderByDescending(s => s.SessionDate).Take(61).ToListAsync(ct);
				var ids = sessions.Select(s => s.ID).ToList();
				var obs = await Obs(ctx).AsNoTracking().Where(o => ids.Contains(o.SessionId)).ToListAsync(ct);
				var asc = sessions.OrderBy(s => s.SessionDate).ToList();
				var history = new List<HospitalDialysisHistoryRow>();
				decimal? prevPost = null;
				foreach (var s in asc)
				{
					history.Add(new HospitalDialysisHistoryRow(s, s.Status == HospitalDialysisSessionStatus.Missed
						? new HospitalDialysisOutcome(null, null, null, null, null, null, null, null, Array.Empty<string>())
						: HospitalDialysisRules.Outcome(s, prevPost, obs.Where(o => o.SessionId == s.ID))));
					if (s.Status == HospitalDialysisSessionStatus.Done) prevPost = s.PostWeightKg;
				}
				history.Reverse();
				if (history.Count > 60) history.RemoveAt(history.Count - 1);   // the 61st only served as the first one's baseline
				var since = Today(nowUtc).AddDays(-30);
				var month = history.Where(h => h.Session.SessionDate >= since).ToList();
				var ktvs = history.Where(h => h.Outcome.KtV != null).Take(3).Select(h => h.Outcome.KtV!.Value).ToList();
				return new HospitalDialysisPatientPage(pt.ID, pt.Name, pt.NameEn ?? "", pt.Mrn, pt.PhotoPath, plan, history, ktvs.Count == 0 ? null : Math.Round(ktvs.Average(), 2),
					month.Count(h => h.Session.Status == HospitalDialysisSessionStatus.Done), month.Count(h => h.Session.Status == HospitalDialysisSessionStatus.Missed));
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex)) { return new HospitalDialysisPatientPage(pt.ID, pt.Name, pt.NameEn ?? "", pt.Mrn, pt.PhotoPath, null, Array.Empty<HospitalDialysisHistoryRow>(), null, 0, 0); }
		}

		public async Task<HospitalDialysisSessionPage?> SessionPageAsync(BusinessContext ctx, int id, CancellationToken ct = default)
		{
			try
			{
				var s = await Sessions(ctx).AsNoTracking().FirstOrDefaultAsync(x => x.ID == id, ct);
				if (s == null || s.Status == HospitalDialysisSessionStatus.Missed) return null;
				var plan = await Plans(ctx).AsNoTracking().FirstAsync(p => p.ID == s.PlanId, ct);
				var pt = await _db.HospitalPatients.AsNoTracking().Where(x => x.ID == s.PatientId).Select(x => new { x.Name, x.NameEn, x.Mrn, x.PhotoPath }).FirstAsync(ct);
				var obs = await Obs(ctx).AsNoTracking().Where(o => o.SessionId == id).OrderBy(o => o.At).ThenBy(o => o.ID).ToListAsync(ct);
				var prev = await PrevPostAsync(ctx, s.PatientId, s.SessionDate, ct);
				var eids = obs.Where(o => o.CreatedByEmployeeId != null).Select(o => o.CreatedByEmployeeId!.Value)
					.Concat(new[] { s.CreatedByEmployeeId, s.ClosedByEmployeeId }.Where(x => x != null).Select(x => x!.Value)).Distinct().ToList();
				var people = await _db.Employee.AsNoTracking().Where(e => eids.Contains(e.ID) && e.EmpCompanyID == ctx.CompanyId)
					.Select(e => new { e.ID, e.FullName, e.FullNameEn }).ToDictionaryAsync(e => e.ID, e => DisplayName.Of(e.FullName ?? "", e.FullNameEn), ct);
				return new HospitalDialysisSessionPage(s, plan, pt.Name, pt.NameEn ?? "", pt.Mrn, pt.PhotoPath, prev, obs, HospitalDialysisRules.Outcome(s, prev, obs), people);
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex)) { return null; }
		}
	}
}
