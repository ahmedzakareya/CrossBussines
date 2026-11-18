using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Hospital;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL.Hospital
{
	// ==========================================================================================
	// HOSPITAL H86 — labour monitoring. H175: the WHO LABOUR CARE GUIDE (2020) replaces the partograph (register rule
	// "labour", version 2). What changed:
	//   · active labour starts at 5 cm (not 4) — or when the midwife writes that she diagnosed it;
	//   · no alert and action lines: the guide's time limit at each centimetre — an alert when the cervix has stayed at
	//     5 cm for 6 h, 6 cm 5 h, 7 cm 3 h, 8 cm 2.5 h, 9 cm 2 h — and the second stage (10 cm) at 3 h for a first birth,
	//     2 h otherwise (unknown parity: 2 h);
	//   · each item of the guide's sections has its own alert threshold (supportive care, the baby, the woman, labour
	//     progress); the medication and the shared decision (assessment, plan) are recorded with the observation.
	// Every alert is computed from what was recorded; nothing is ticked. Rows recorded under the partograph keep their
	// readings and are read by the guide's rules.
	// ==========================================================================================
	public sealed class HospitalLabourObservationInput
	{
		public int LabourId { get; set; }
		/// Ward-local; empty means now.
		public DateTime? At { get; set; }
		public decimal? CervixCm { get; set; }
		public int? DescentFifths { get; set; }
		public int? ContractionsPer10 { get; set; }
		public int? ContractionSeconds { get; set; }
		public int? FetalHeartRate { get; set; }
		public string? Liquor { get; set; }
		public int? BpSystolic { get; set; }
		public int? BpDiastolic { get; set; }
		public int? Pulse { get; set; }
		public decimal? TemperatureC { get; set; }
		public string? Note { get; set; }
		// H175
		public bool? Companion { get; set; }
		public bool? PainRelief { get; set; }
		public bool? OralFluid { get; set; }
		public string? Posture { get; set; }
		public string? Deceleration { get; set; }
		public string? FetalPosition { get; set; }
		public int? Caput { get; set; }
		public int? Moulding { get; set; }
		public int? UrineProtein { get; set; }
		public int? UrineAcetone { get; set; }
		public string? Oxytocin { get; set; }
		public string? Medicine { get; set; }
		public bool? IvFluids { get; set; }
		public string? Assessment { get; set; }
		public string? Plan { get; set; }
	}

	public sealed class HospitalLabourCloseInput
	{
		public string? Outcome { get; set; }
		public string? Note { get; set; }
	}

	/// H175: the guide's first section.
	public sealed class HospitalLabourInfoInput
	{
		public int? Parity { get; set; }
		public string? Onset { get; set; }
		public DateTime? MembranesRupturedAt { get; set; }
		public string? RiskFactors { get; set; }
		public DateTime? ActiveLabourAt { get; set; }
	}

	public static class HospitalLabourWarning
	{
		// labour progress
		public const string CervixDelay = "CervixDelay";
		public const string SecondStageDelay = "SecondStageDelay";
		public const string FewContractions = "FewContractions";
		public const string Tachysystole = "Tachysystole";
		public const string ContractionLength = "ContractionLength";
		// the baby
		public const string FetalHeart = "FetalHeart";
		public const string LateDecelerations = "LateDecelerations";
		public const string Meconium = "Meconium";
		public const string Bleeding = "Bleeding";
		public const string Malposition = "Malposition";
		public const string Caput = "Caput";
		public const string Moulding = "Moulding";
		// the woman
		public const string Pulse = "Pulse";
		public const string Hypotension = "Hypotension";
		public const string Hypertension = "Hypertension";
		public const string Temperature = "Temperature";
		public const string Proteinuria = "Proteinuria";
		public const string Ketonuria = "Ketonuria";
		// supportive care
		public const string NoCompanion = "NoCompanion";
		public const string NoPainRelief = "NoPainRelief";
		public const string NoOralFluid = "NoOralFluid";
		public const string Supine = "Supine";

		public static readonly IReadOnlyList<string> All = new[] { CervixDelay, SecondStageDelay, FewContractions, Tachysystole, ContractionLength, FetalHeart, LateDecelerations,
			Meconium, Bleeding, Malposition, Caput, Moulding, Pulse, Hypotension, Hypertension, Temperature, Proteinuria, Ketonuria, NoCompanion, NoPainRelief, NoOralFluid, Supine };
	}

	public sealed record HospitalLabourPoint(HospitalLabourObservation Obs, double Hours, IReadOnlyList<string> Warnings);
	public sealed record HospitalLabourPage(HospitalLabour Labour, string Name, string NameEn, string Mrn, string? Photo, DateTime? ActiveStart, decimal? ActiveCervix,
		IReadOnlyList<HospitalLabourPoint> Points, IReadOnlyList<string> Current, IReadOnlyDictionary<int, string> People)
	{
		/// H175: when the second stage began (the first 10 cm), and the hours left at the current centimetre before its limit.
		public DateTime? SecondStageAt { get; init; }
		public double? HoursLeft { get; init; }
	}
	public sealed record HospitalLabourRow(HospitalLabour Labour, int PatientId, string Name, string NameEn, string Mrn, string? Bed, HospitalLabourObservation? Last, IReadOnlyList<string> Warnings, int MinutesSinceLast);

	public static class HospitalLabourRules
	{
		/// H175: the guide's active labour.
		public const decimal ActivePhaseCm = 5m;

		/// The guide's time limit (hours) at each centimetre of the active first stage.
		public static double? CmLimitHours(decimal cm) => Math.Floor(cm) switch { 5 => 6, 6 => 5, 7 => 3, 8 => 2.5, 9 => 2, _ => null };

		/// The second stage's limit: 3 h for a first birth (parity 0), 2 h otherwise or when the parity is not written.
		public static double SecondStageLimitHours(int? parity) => parity == 0 ? 3 : 2;

		/// Active labour: the time the midwife wrote she diagnosed it, else the first observation at 5 cm or more.
		public static (DateTime At, decimal Cm)? ActiveStart(IEnumerable<HospitalLabourObservation> obs, DateTime? diagnosedAt = null)
		{
			var list = obs.Where(o => o.CervixCm != null).OrderBy(o => o.At).ToList();
			if (diagnosedAt is DateTime d)
			{
				var near = list.LastOrDefault(o => o.At <= d) ?? list.FirstOrDefault();
				return (d, near?.CervixCm ?? ActivePhaseCm);
			}
			var f = list.FirstOrDefault(o => o.CervixCm >= ActivePhaseCm);
			return f == null ? null : (f.At, f.CervixCm!.Value);
		}

		/// The first time the cervix was recorded at this whole centimetre (or more), within the active stage.
		private static DateTime? FirstAt(IEnumerable<HospitalLabourObservation> obs, decimal cm) =>
			obs.Where(o => o.CervixCm != null && Math.Floor(o.CervixCm.Value) >= Math.Floor(cm)).OrderBy(o => o.At).Select(o => (DateTime?)o.At).FirstOrDefault();

		/// Labour progress at a moment: the time spent at the current centimetre against its limit (active stage only), or
		/// the second stage against its limit.
		public static IReadOnlyList<string> ProgressWarnings(IReadOnlyList<HospitalLabourObservation> obs, (DateTime At, decimal Cm)? start, int? parity, DateTime at)
		{
			var w = new List<string>();
			var last = obs.Where(o => o.CervixCm != null && o.At <= at).OrderBy(o => o.At).LastOrDefault();
			if (start == null || last == null || last.At < start.Value.At) return w;
			var cm = last.CervixCm!.Value;
			if (cm >= 10)
			{
				var second = FirstAt(obs, 10);
				if (second != null && (at - second.Value).TotalHours >= SecondStageLimitHours(parity)) w.Add(HospitalLabourWarning.SecondStageDelay);
			}
			else if (CmLimitHours(cm) is double lim && FirstAt(obs.Where(o => o.At <= at), cm) is DateTime first && (at - first).TotalHours >= lim)
				w.Add(HospitalLabourWarning.CervixDelay);
			return w;
		}

		/// Hours left at the current centimetre (or in the second stage) before the guide's alert; null outside the active stage.
		public static double? HoursLeft(IReadOnlyList<HospitalLabourObservation> obs, (DateTime At, decimal Cm)? start, int? parity, DateTime now)
		{
			var last = obs.Where(o => o.CervixCm != null).OrderBy(o => o.At).LastOrDefault();
			if (start == null || last == null) return null;
			var cm = last.CervixCm!.Value;
			double? lim = cm >= 10 ? SecondStageLimitHours(parity) : CmLimitHours(cm);
			var first = FirstAt(obs, cm >= 10 ? 10 : cm);
			return lim is double l && first is DateTime f ? Math.Round(l - (now - f).TotalHours, 1) : null;
		}

		/// One observation's own alerts, by the guide's thresholds.
		public static IReadOnlyList<string> ItemWarnings(HospitalLabourObservation o)
		{
			var w = new List<string>();
			if (o.Companion == false) w.Add(HospitalLabourWarning.NoCompanion);
			if (o.PainRelief == false) w.Add(HospitalLabourWarning.NoPainRelief);
			if (o.OralFluid == false) w.Add(HospitalLabourWarning.NoOralFluid);
			if (o.Posture == "SP") w.Add(HospitalLabourWarning.Supine);
			if (o.FetalHeartRate is int f && (f < 110 || f >= 160)) w.Add(HospitalLabourWarning.FetalHeart);
			if (o.Deceleration == "L") w.Add(HospitalLabourWarning.LateDecelerations);
			if (o.Liquor == HospitalLiquor.ThickMeconium) w.Add(HospitalLabourWarning.Meconium);
			if (o.Liquor == HospitalLiquor.Blood) w.Add(HospitalLabourWarning.Bleeding);
			if (o.FetalPosition is "OP" or "OT") w.Add(HospitalLabourWarning.Malposition);
			if (o.Caput >= 3) w.Add(HospitalLabourWarning.Caput);
			if (o.Moulding >= 3) w.Add(HospitalLabourWarning.Moulding);
			if (o.Pulse is int p && (p < 60 || p >= 120)) w.Add(HospitalLabourWarning.Pulse);
			if (o.BpSystolic < 80) w.Add(HospitalLabourWarning.Hypotension);
			if (o.BpSystolic >= 140 || o.BpDiastolic >= 90) w.Add(HospitalLabourWarning.Hypertension);
			if (o.TemperatureC is decimal t && (t < 35m || t >= 37.5m)) w.Add(HospitalLabourWarning.Temperature);
			if (o.UrineProtein >= 2) w.Add(HospitalLabourWarning.Proteinuria);
			if (o.UrineAcetone >= 2) w.Add(HospitalLabourWarning.Ketonuria);
			if (o.ContractionsPer10 is int c && (c <= 2 || c > 5)) w.Add(c > 5 ? HospitalLabourWarning.Tachysystole : HospitalLabourWarning.FewContractions);
			if (o.ContractionSeconds is int s && (s < 20 || s > 60)) w.Add(HospitalLabourWarning.ContractionLength);
			return w;
		}

		/// The alerts of an observation: its items, and labour progress at its time.
		public static IReadOnlyList<string> Warnings(IReadOnlyList<HospitalLabourObservation> obs, (DateTime At, decimal Cm)? start, int? parity, HospitalLabourObservation o) =>
			ProgressWarnings(obs, start, parity, o.At).Concat(ItemWarnings(o)).ToList();

		public static string? ObservationRefusal(HospitalLabourObservationInput i, DateTime atUtc, DateTime startedAtUtc, DateTime nowUtc, decimal? lastCervix)
		{
			if (atUtc > nowUtc.AddMinutes(5)) return "The time is in the future";
			if (atUtc < startedAtUtc.AddMinutes(-5)) return "The time is before the labour started";
			if (i.CervixCm is < 0 or > 10) return "The cervix is between 0 and 10 cm";
			if (i.CervixCm != null && lastCervix != null && i.CervixCm < lastCervix - 1) return "The cervix cannot close by more than a centimetre: check the reading";
			if (i.DescentFifths is < 0 or > 5) return "The descent is between 0 and 5 fifths";
			if (i.ContractionsPer10 is < 0 or > 10) return "The contractions are between 0 and 10 in ten minutes";
			if (i.ContractionSeconds is < 0 or > 180) return "A contraction lasts up to 180 seconds";
			if (i.FetalHeartRate is < 50 or > 240) return "The fetal heart rate is not plausible";
			if (!string.IsNullOrEmpty(i.Liquor) && !HospitalLiquor.All.Contains(i.Liquor)) return "Choose the liquor";
			if ((i.BpSystolic == null) != (i.BpDiastolic == null)) return "Write both blood pressure numbers";
			if (i.TemperatureC is < 33 or > 43) return "The temperature is not plausible";
			if (!string.IsNullOrEmpty(i.Posture) && !HospitalLcg.Postures.Contains(i.Posture)) return "Choose the posture";
			if (!string.IsNullOrEmpty(i.Deceleration) && !HospitalLcg.Decelerations.Contains(i.Deceleration)) return "Choose the deceleration";
			if (!string.IsNullOrEmpty(i.FetalPosition) && !HospitalLcg.Positions.Contains(i.FetalPosition)) return "Choose the fetal position";
			if (i.Caput is < 0 or > 3 || i.Moulding is < 0 or > 3) return "Caput and moulding are 0 to +++";
			if (i.UrineProtein is < 0 or > 4 || i.UrineAcetone is < 0 or > 4) return "Urine protein and acetone are 0 to ++++";
			bool any = i.CervixCm != null || i.DescentFifths != null || i.ContractionsPer10 != null || i.FetalHeartRate != null || !string.IsNullOrEmpty(i.Liquor)
				|| i.BpSystolic != null || i.Pulse != null || i.TemperatureC != null || !string.IsNullOrWhiteSpace(i.Note)
				|| i.Companion != null || i.PainRelief != null || i.OralFluid != null || !string.IsNullOrEmpty(i.Posture) || !string.IsNullOrEmpty(i.Deceleration)
				|| !string.IsNullOrEmpty(i.FetalPosition) || i.Caput != null || i.Moulding != null || i.UrineProtein != null || i.UrineAcetone != null
				|| !string.IsNullOrWhiteSpace(i.Oxytocin) || !string.IsNullOrWhiteSpace(i.Medicine) || i.IvFluids != null || !string.IsNullOrWhiteSpace(i.Assessment) || !string.IsNullOrWhiteSpace(i.Plan);
			return any ? null : "Write at least one measure or a note";
		}

		/// H175: the labour's characteristics.
		public static string? InfoRefusal(HospitalLabourInfoInput i, DateTime? rupturedUtc, DateTime? activeUtc, DateTime startedAtUtc, DateTime nowUtc)
		{
			if (i.Parity is < 0 or > 20) return "The parity is between 0 and 20";
			if (!string.IsNullOrEmpty(i.Onset) && !HospitalLcg.Onsets.Contains(i.Onset)) return "Choose how labour began";
			if (rupturedUtc > nowUtc.AddMinutes(5) || activeUtc > nowUtc.AddMinutes(5)) return "The time is in the future";
			if (rupturedUtc < nowUtc.AddDays(-7)) return "The membranes' rupture can be written back a week at most";
			if (activeUtc < startedAtUtc.AddMinutes(-5)) return "Active labour cannot begin before the labour started";
			return null;
		}

		public static string? StartRefusal(DateTime startUtc, DateTime admittedAtUtc, DateTime nowUtc)
		{
			if (startUtc > nowUtc.AddMinutes(5)) return "The time is in the future";
			if (startUtc < nowUtc.AddHours(-24)) return "The labour's start can be written back a day at most";
			if (startUtc < admittedAtUtc.AddMinutes(-5)) return "The time is before the admission";
			return null;
		}

		public static string? CloseRefusal(HospitalLabour l, string? outcome)
		{
			if (l.Status != HospitalLabourStatus.Active) return "This labour is already closed";
			if (!HospitalLabourOutcome.All.Contains(outcome ?? "")) return "Choose how the labour ended";
			return null;
		}
	}

	public sealed class HospitalLabourService
	{
		public const string NoTables = "The labour care guide is not installed yet (hospital_h86_labour.sql, hospital_h175_labour_care_guide.sql).";
		private readonly CrossDbContext _db;
		private readonly IHospitalAccessService _access;
		public HospitalLabourService(CrossDbContext db, IHospitalAccessService access) { _db = db; _access = access; }
		private static bool Missing(Microsoft.Data.SqlClient.SqlException ex) => ex.Number is 207 or 208;
		private static string? Clip(string? s, int max) => string.IsNullOrWhiteSpace(s) ? null : s.Trim()[..Math.Min(max, s.Trim().Length)];
		private IQueryable<HospitalLabour> Labours(BusinessContext ctx) => _db.Set<HospitalLabour>().Where(x => x.CompanyId == ctx.CompanyId);
		private IQueryable<HospitalLabourObservation> Obs(BusinessContext ctx) => _db.Set<HospitalLabourObservation>().Where(x => x.CompanyId == ctx.CompanyId);

		private async Task<bool> WardAsync(BusinessContext ctx, CancellationToken ct) =>
			await _access.CanAsync(ctx, HospitalActions.Admit, null, ct) || await _access.CanAsync(ctx, HospitalActions.Consult, null, ct);

		/// The labour is opened when the midwife opens the guide, often hours after it began: the start may be written
		/// back up to a day, never before the admission and never in the future.
		public async Task<HospitalResult> StartAsync(BusinessContext ctx, int admissionId, DateTime? startedAtLocal, int? employeeId, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await WardAsync(ctx, ct)) return HospitalResult.Fail("forbidden");
			var a = await (from x in _db.HospitalAdmissions.AsNoTracking() join p in _db.HospitalPatients.AsNoTracking() on x.PatientId equals p.ID
						   where x.ID == admissionId && x.CompanyId == ctx.CompanyId select new { x.ID, x.PatientId, x.Status, x.AdmittedAt, p.Sex }).FirstOrDefaultAsync(ct);
			if (a == null) return HospitalResult.Fail("not_found");
			if (a.Status != HospitalAdmissionStatus.Active) return HospitalResult.Fail("This admission is not active");
			if (a.Sex != HospitalPatientSex.Female) return HospitalResult.Fail("A labour is opened on a female patient's stay");
			var startUtc = startedAtLocal == null ? nowUtc : HospitalNursingService.WardToUtc(startedAtLocal.Value);
			if (HospitalLabourRules.StartRefusal(startUtc, a.AdmittedAt, nowUtc) is string bad) return HospitalResult.Fail(bad);
			try
			{
				var open = await Labours(ctx).Where(x => x.AdmissionId == a.ID && x.Status == HospitalLabourStatus.Active).Select(x => (int?)x.ID).FirstOrDefaultAsync(ct);
				if (open != null) return HospitalResult.Success(open);   // already open: go to it
				var row = new HospitalLabour { CompanyId = ctx.CompanyId, AdmissionId = a.ID, PatientId = a.PatientId, Status = HospitalLabourStatus.Active, StartedAt = startUtc, StartedByEmployeeId = employeeId };
				_db.Add(row);
				await _db.SaveChangesAsync(ct);
				return HospitalResult.Success(row.ID);
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex)) { return HospitalResult.Fail(NoTables); }
		}

		/// H175: the labour's characteristics (the guide's first section), while the labour is open.
		public async Task<HospitalResult> SaveInfoAsync(BusinessContext ctx, int id, HospitalLabourInfoInput input, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await WardAsync(ctx, ct)) return HospitalResult.Fail("forbidden");
			try
			{
				var l = await Labours(ctx).FirstOrDefaultAsync(x => x.ID == id, ct);
				if (l == null) return HospitalResult.Fail("not_found");
				if (l.Status != HospitalLabourStatus.Active) return HospitalResult.Fail("This labour is already closed");
				DateTime? rup = input.MembranesRupturedAt == null ? null : HospitalNursingService.WardToUtc(input.MembranesRupturedAt.Value);
				DateTime? act = input.ActiveLabourAt == null ? null : HospitalNursingService.WardToUtc(input.ActiveLabourAt.Value);
				if (HospitalLabourRules.InfoRefusal(input, rup, act, l.StartedAt, nowUtc) is string why) return HospitalResult.Fail(why);
				l.Parity = input.Parity; l.Onset = string.IsNullOrEmpty(input.Onset) ? null : input.Onset; l.MembranesRupturedAt = rup; l.RiskFactors = Clip(input.RiskFactors, 300); l.ActiveLabourAt = act;
				await _db.SaveChangesAsync(ct);
				return HospitalResult.Success(l.ID);
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex)) { return HospitalResult.Fail(NoTables); }
		}

		public async Task<HospitalResult> ObserveAsync(BusinessContext ctx, HospitalLabourObservationInput input, int? employeeId, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await WardAsync(ctx, ct)) return HospitalResult.Fail("forbidden");
			try
			{
				var l = await Labours(ctx).AsNoTracking().FirstOrDefaultAsync(x => x.ID == input.LabourId, ct);
				if (l == null) return HospitalResult.Fail("not_found");
				if (l.Status != HospitalLabourStatus.Active) return HospitalResult.Fail("This labour is already closed");
				var at = input.At == null ? nowUtc : HospitalNursingService.WardToUtc(input.At.Value);
				var lastCervix = await Obs(ctx).Where(o => o.LabourId == l.ID && o.CervixCm != null && o.At <= at).OrderByDescending(o => o.At).Select(o => o.CervixCm).FirstOrDefaultAsync(ct);
				if (HospitalLabourRules.ObservationRefusal(input, at, l.StartedAt, nowUtc, lastCervix) is string why) return HospitalResult.Fail(why);
				var row = new HospitalLabourObservation
				{
					CompanyId = ctx.CompanyId, LabourId = l.ID, PatientId = l.PatientId, At = at, CervixCm = input.CervixCm, DescentFifths = input.DescentFifths,
					ContractionsPer10 = input.ContractionsPer10, ContractionSeconds = input.ContractionSeconds, FetalHeartRate = input.FetalHeartRate,
					Liquor = string.IsNullOrEmpty(input.Liquor) ? null : input.Liquor, BpSystolic = input.BpSystolic, BpDiastolic = input.BpDiastolic, Pulse = input.Pulse,
					TemperatureC = input.TemperatureC, Note = Clip(input.Note, 300), CreatedAt = nowUtc, CreatedByEmployeeId = employeeId,
					Companion = input.Companion, PainRelief = input.PainRelief, OralFluid = input.OralFluid, Posture = string.IsNullOrEmpty(input.Posture) ? null : input.Posture,
					Deceleration = string.IsNullOrEmpty(input.Deceleration) ? null : input.Deceleration, FetalPosition = string.IsNullOrEmpty(input.FetalPosition) ? null : input.FetalPosition,
					Caput = input.Caput, Moulding = input.Moulding, UrineProtein = input.UrineProtein, UrineAcetone = input.UrineAcetone,
					Oxytocin = Clip(input.Oxytocin, 40), Medicine = Clip(input.Medicine, 200), IvFluids = input.IvFluids, Assessment = Clip(input.Assessment, 300), Plan = Clip(input.Plan, 300),
				};
				_db.Add(row);
				await _db.SaveChangesAsync(ct);
				return HospitalResult.Success(row.ID);
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex)) { return HospitalResult.Fail(NoTables); }
		}

		public async Task<HospitalResult> CloseAsync(BusinessContext ctx, int id, HospitalLabourCloseInput input, int? employeeId, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await WardAsync(ctx, ct)) return HospitalResult.Fail("forbidden");
			try
			{
				var l = await Labours(ctx).FirstOrDefaultAsync(x => x.ID == id, ct);
				if (l == null) return HospitalResult.Fail("not_found");
				if (HospitalLabourRules.CloseRefusal(l, input.Outcome) is string why) return HospitalResult.Fail(why);
				l.Status = HospitalLabourStatus.Closed; l.Outcome = input.Outcome; l.ClosedAt = nowUtc; l.ClosedByEmployeeId = employeeId; l.Note = Clip(input.Note, 500);
				await _db.SaveChangesAsync(ct);
				return HospitalResult.Success(l.ID);
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex)) { return HospitalResult.Fail(NoTables); }
		}

		/// The current alerts: the latest observation's items, and labour progress NOW (time keeps running at a centimetre).
		private static IReadOnlyList<string> Current(IReadOnlyList<HospitalLabourObservation> obs, HospitalLabour l, DateTime nowUtc)
		{
			var last = obs.OrderBy(o => o.At).LastOrDefault();
			if (last == null) return Array.Empty<string>();
			var start = HospitalLabourRules.ActiveStart(obs, l.ActiveLabourAt);
			var at = l.Status == HospitalLabourStatus.Active ? nowUtc : last.At;
			return HospitalLabourRules.ProgressWarnings(obs, start, l.Parity, at).Concat(HospitalLabourRules.ItemWarnings(last)).ToList();
		}

		public async Task<HospitalLabourPage?> PageAsync(BusinessContext ctx, int id, DateTime nowUtc, CancellationToken ct = default)
		{
			try
			{
				var l = await Labours(ctx).AsNoTracking().FirstOrDefaultAsync(x => x.ID == id, ct);
				if (l == null) return null;
				var p = await _db.HospitalPatients.AsNoTracking().Where(x => x.ID == l.PatientId).Select(x => new { x.Name, x.NameEn, x.Mrn, x.PhotoPath }).FirstAsync(ct);
				var obs = await Obs(ctx).AsNoTracking().Where(o => o.LabourId == id).OrderBy(o => o.At).ThenBy(o => o.ID).ToListAsync(ct);
				var start = HospitalLabourRules.ActiveStart(obs, l.ActiveLabourAt);
				var points = obs.Select(o => new HospitalLabourPoint(o, (o.At - l.StartedAt).TotalHours, HospitalLabourRules.Warnings(obs, start, l.Parity, o))).ToList();
				var eids = obs.Where(o => o.CreatedByEmployeeId != null).Select(o => o.CreatedByEmployeeId!.Value).Distinct().ToList();
				var people = await _db.Employee.AsNoTracking().Where(e => eids.Contains(e.ID) && e.EmpCompanyID == ctx.CompanyId)
					.Select(e => new { e.ID, e.FullName, e.FullNameEn }).ToDictionaryAsync(e => e.ID, e => DisplayName.Of(e.FullName ?? "", e.FullNameEn), ct);
				var second = obs.Where(o => o.CervixCm >= 10).OrderBy(o => o.At).Select(o => (DateTime?)o.At).FirstOrDefault();
				return new HospitalLabourPage(l, p.Name, p.NameEn ?? "", p.Mrn, p.PhotoPath, start?.At, start?.Cm, points, Current(obs, l, nowUtc), people)
				{
					SecondStageAt = second,
					HoursLeft = l.Status == HospitalLabourStatus.Active ? HospitalLabourRules.HoursLeft(obs, start, l.Parity, nowUtc) : null,
				};
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex)) { return null; }
		}

		/// The labours in progress, the most worrying first: the current alerts, and how long since anyone looked.
		public async Task<(IReadOnlyList<HospitalLabourRow> Rows, bool Ready)> ActiveAsync(BusinessContext ctx, DateTime nowUtc, CancellationToken ct = default)
		{
			try
			{
				var ls = await (from l in Labours(ctx).AsNoTracking()
								join p in _db.HospitalPatients.AsNoTracking() on l.PatientId equals p.ID
								where l.Status == HospitalLabourStatus.Active
								select new { l, p.Name, p.NameEn, p.Mrn }).ToListAsync(ct);
				var ids = ls.Select(x => x.l.ID).ToList();
				var obs = await Obs(ctx).AsNoTracking().Where(o => ids.Contains(o.LabourId)).ToListAsync(ct);
				var adm = ls.Select(x => x.l.AdmissionId).ToList();
				var beds = await (from s in _db.HospitalBedStays.AsNoTracking() join b in _db.HospitalBeds.AsNoTracking() on s.BedId equals b.ID
								  where s.CompanyId == ctx.CompanyId && s.EndedAt == null && adm.Contains(s.AdmissionId) select new { s.AdmissionId, b.Code }).ToListAsync(ct);
				var rows = ls.Select(x =>
				{
					var mine = obs.Where(o => o.LabourId == x.l.ID).OrderBy(o => o.At).ToList();
					var last = mine.LastOrDefault();
					var w = Current(mine, x.l, nowUtc);
					int since = (int)Math.Round((nowUtc - (last?.At ?? x.l.StartedAt)).TotalMinutes);
					return new HospitalLabourRow(x.l, x.l.PatientId, x.Name, x.NameEn ?? "", x.Mrn, beds.FirstOrDefault(b => b.AdmissionId == x.l.AdmissionId)?.Code, last, w, since);
				}).OrderByDescending(r => r.Warnings.Count).ThenByDescending(r => r.MinutesSinceLast).ToList();
				return (rows, true);
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex)) { return (Array.Empty<HospitalLabourRow>(), false); }
		}
	}
}
