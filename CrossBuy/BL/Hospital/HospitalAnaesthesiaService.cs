using System.Globalization;
using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Hospital;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL.Hospital
{
	// ==========================================================================================
	// HOSPITAL H202 — the anaesthesia record of an operation.
	//   · BEFORE: the pre-anaesthetic assessment — ASA physical status (and E for an emergency), weight, the airway
	//     (Mallampati, mouth opening, thyromental distance → a predicted difficult airway the team prepares for), when the
	//     patient last ate, had breast milk and clear fluids, and the planned technique. Induction — the operation's
	//     start — is REFUSED without it, and while fasting is short of the ASA 2017 times (solids 6 h, breast milk 4 h,
	//     clear fluids 2 h); only an emergency, with its reason written, starts before. The assessment locks at induction.
	//   · DURING: the milestones (induction, airway, incision, end of surgery, emergence — each once), the drugs and
	//     fluids given, and the readings (pulse, pressure, SpO2, EtCO2), each at its time.
	//   · AFTER: the recovery room — arrival once the patient has left the theatre, the Aldrete score (activity,
	//     respiration, circulation, consciousness, SpO2; 0–2 each), and leaving only at 9 or more.
	// Times are the theatre's (local), as the operation's StartedAt/EndedAt are.
	// ==========================================================================================
	public sealed class HospitalAnaesthesiaInput
	{
		public int Asa { get; set; }
		public bool Emergency { get; set; }
		public decimal? WeightKg { get; set; }
		public int? Mallampati { get; set; }
		public decimal? MouthOpeningCm { get; set; }
		public decimal? ThyromentalCm { get; set; }
		public DateTime? LastSolidsAt { get; set; }
		public DateTime? LastMilkAt { get; set; }
		public DateTime? LastClearAt { get; set; }
		public string? FastingOverride { get; set; }
		public string? Technique { get; set; }
		public string? Plan { get; set; }
	}

	public sealed class HospitalAnaesthesiaEntryInput
	{
		public string? Kind { get; set; }
		public DateTime? At { get; set; }
		public string? Code { get; set; }
		public decimal? Dose { get; set; }
		public string? Unit { get; set; }
		public string? Route { get; set; }
		public int? HeartRate { get; set; }
		public int? SystolicBp { get; set; }
		public int? DiastolicBp { get; set; }
		public int? SpO2 { get; set; }
		public int? EtCo2 { get; set; }
		public List<int> Items { get; set; } = new();
		public string? Note { get; set; }
	}

	public sealed record HospitalAnaesthesiaView(HospitalSurgery Case, string PatientName, string PatientNameEn, string Mrn, string? Allergies,
		HospitalAnaesthesia? Record, IReadOnlyList<HospitalAnaesthesiaEntry> Entries, IReadOnlyDictionary<int, string> People, int? LastAldrete);

	public static class HospitalAnaesthesiaRules
	{
		public const double SolidsHours = 6, MilkHours = 4, ClearHours = 2;
		public const int AldreteToLeave = 9;
		public const string NoAssessment = "Write the pre-anaesthetic assessment before induction";

		public static bool DifficultAirway(int? mallampati, decimal? mouthCm, decimal? thyromentalCm) =>
			mallampati >= 3 || mouthCm < 3m || thyromentalCm < 6m;

		/// The first fasting time short of its rule: (the refusal, hours since), or null when fasting is complete.
		public static (string Refusal, double Hours)? FastingShortfall(DateTime? solids, DateTime? milk, DateTime? clear, DateTime now)
		{
			double H(DateTime t) => Math.Round((now - t).TotalHours, 1);
			if (solids is DateTime s && H(s) < SolidsHours) return ("Fasting is not complete: solids {0} h ago, 6 h needed", H(s));
			if (milk is DateTime m && H(m) < MilkHours) return ("Fasting is not complete: breast milk {0} h ago, 4 h needed", H(m));
			if (clear is DateTime c && H(c) < ClearHours) return ("Fasting is not complete: clear fluids {0} h ago, 2 h needed", H(c));
			return null;
		}

		public static string? AssessmentRefusal(HospitalAnaesthesiaInput i, DateTime now)
		{
			if (i.Asa is < 1 or > 6) return "Choose the ASA class (1 to 6)";
			if (string.IsNullOrEmpty(i.Technique) || !HospitalAnaesthesiaTechnique.All.Contains(i.Technique)) return "Choose the planned technique";
			if (i.Mallampati is < 1 or > 4) return "Mallampati is 1 to 4";
			if (i.MouthOpeningCm is < 0 or > 10 || i.ThyromentalCm is < 0 or > 15) return "The airway measurements are out of range";
			if (i.WeightKg is < 0.3m or > 400m) return "The weight must be 0.3 to 400 kg";
			if (new[] { i.LastSolidsAt, i.LastMilkAt, i.LastClearAt }.Any(t => t > now.AddMinutes(5))) return "A fasting time cannot be in the future";
			if (!i.Emergency && (i.LastSolidsAt == null || i.LastClearAt == null)) return "Record when the patient last ate and drank";
			if (!i.Emergency && !string.IsNullOrWhiteSpace(i.FastingOverride)) return "Only an emergency may start before fasting is complete";
			return null;
		}

		/// Why induction cannot start now, with its argument, or null.
		public static (string Refusal, object[] Args)? StartRefusal(HospitalAnaesthesia? a, DateTime now)
		{
			if (a == null) return (NoAssessment, Array.Empty<object>());
			var shortfall = FastingShortfall(a.LastSolidsAt, a.LastMilkAt, a.LastClearAt, now);
			if (shortfall is var (why, hours) && !(a.Emergency && !string.IsNullOrWhiteSpace(a.FastingOverride)))
				return (why, new object[] { hours.ToString("0.0", CultureInfo.InvariantCulture) });
			if (a.Emergency && a.LastSolidsAt == null && string.IsNullOrWhiteSpace(a.FastingOverride)) return ("An emergency with unknown fasting needs its reason written", Array.Empty<object>());
			return null;
		}

		public static (int Score, string? Refusal) Aldrete(IReadOnlyList<int> items)
		{
			if (items.Count != 5 || items.Any(x => x is < 0 or > 2)) return (0, "Score the five Aldrete items, 0 to 2 each");
			return (items.Sum(), null);
		}

		public static string? EntryRefusal(HospitalAnaesthesiaEntryInput i, string surgeryStatus, HospitalAnaesthesia a, IReadOnlyCollection<string> eventsDone, DateTime at, DateTime now)
		{
			if (string.IsNullOrEmpty(i.Kind) || !HospitalAnaesthesiaEntryKind.All.Contains(i.Kind)) return "not_found";
			if (a.PacuOutAt != null) return "The record closed when the patient left recovery";
			if (at > now.AddMinutes(5)) return "The time cannot be in the future";
			if (at < a.AssessedAt.AddDays(-1)) return "The time is before the assessment";
			bool started = surgeryStatus is HospitalSurgeryStatus.InProgress or HospitalSurgeryStatus.Done;
			switch (i.Kind)
			{
				case HospitalAnaesthesiaEntryKind.Event:
					if (string.IsNullOrEmpty(i.Code) || !HospitalAnaesthesiaEvent.All.Contains(i.Code)) return "Choose the milestone";
					if (!started) return "Milestones are recorded once the operation has started";
					return eventsDone.Contains(i.Code) ? "This milestone is already recorded" : null;
				case HospitalAnaesthesiaEntryKind.Drug:
				case HospitalAnaesthesiaEntryKind.Fluid:
					if (string.IsNullOrWhiteSpace(i.Code)) return i.Kind == HospitalAnaesthesiaEntryKind.Drug ? "Name the drug" : "Name the fluid";
					if (i.Dose is not > 0) return "Enter the dose";
					return string.IsNullOrWhiteSpace(i.Unit) ? "Enter the unit" : null;
				case HospitalAnaesthesiaEntryKind.Vitals:
					if (i.HeartRate == null && i.SystolicBp == null && i.SpO2 == null && i.EtCo2 == null) return "Enter at least one reading";
					if (i.EtCo2 is < 0 or > 100) return "EtCO₂ must be 0 to 100 mmHg";
					if ((i.SystolicBp == null) != (i.DiastolicBp == null)) return "Enter both pressures";
					return HospitalNews2Rules.Refusal(new HospitalObservationInput { HeartRate = i.HeartRate, SystolicBp = i.SystolicBp, DiastolicBp = i.DiastolicBp, SpO2 = i.SpO2, PainScore = i.SpO2 == null && i.HeartRate == null && i.SystolicBp == null ? 0 : null });
				default:   // Aldrete
					if (a.PacuInAt == null) return "Record the arrival in recovery first";
					return Aldrete(i.Items).Refusal;
			}
		}

		public static string? PacuOutRefusal(HospitalAnaesthesia a, int? lastAldrete)
		{
			if (a.PacuInAt == null) return "The patient has not arrived in recovery";
			if (a.PacuOutAt != null) return "The patient already left recovery";
			if (lastAldrete is not int s) return "Score Aldrete before the patient leaves recovery";
			return s < AldreteToLeave ? "Aldrete {0}: 9 or more is needed to leave recovery" : null;
		}
	}

	public sealed class HospitalAnaesthesiaService
	{
		private readonly CrossDbContext _db;
		private readonly IHospitalAccessService _access;
		public HospitalAnaesthesiaService(CrossDbContext db, IHospitalAccessService access) { _db = db; _access = access; }

		private IQueryable<HospitalAnaesthesia> Records(BusinessContext ctx) => _db.Set<HospitalAnaesthesia>().Where(x => x.CompanyId == ctx.CompanyId);
		private IQueryable<HospitalAnaesthesiaEntry> Entries(BusinessContext ctx) => _db.Set<HospitalAnaesthesiaEntry>().Where(x => x.CompanyId == ctx.CompanyId);
		private IQueryable<HospitalSurgery> Cases(BusinessContext ctx) => _db.Set<HospitalSurgery>().Where(x => x.CompanyId == ctx.CompanyId);
		private static string? Clip(string? s, int n) => string.IsNullOrWhiteSpace(s) ? null : s.Trim().Length <= n ? s.Trim() : s.Trim()[..n];

		/// The theatre's start asks this: a refusal (translated by the caller) or null. Fails closed when not installed.
		public static async Task<HospitalResult?> StartRefusalAsync(CrossDbContext db, BusinessContext ctx, int surgeryId, DateTime nowLocal, CancellationToken ct)
		{
			HospitalAnaesthesia? a;
			try { a = await db.Set<HospitalAnaesthesia>().AsNoTracking().FirstOrDefaultAsync(x => x.CompanyId == ctx.CompanyId && x.SurgeryId == surgeryId, ct); }
			catch (Microsoft.Data.SqlClient.SqlException) { return HospitalResult.Fail("The anaesthesia record is not installed yet (hospital_h202_anaesthesia.sql)"); }
			return HospitalAnaesthesiaRules.StartRefusal(a, nowLocal) is var (why, args) ? HospitalResult.Fail(why, args) : null;
		}

		public async Task<HospitalAnaesthesiaView?> ViewAsync(BusinessContext ctx, int surgeryId, CancellationToken ct = default)
		{
			if (!await _access.CanAsync(ctx, HospitalActions.ClinicalRead, null, ct)) return null;
			var c = await Cases(ctx).AsNoTracking().FirstOrDefaultAsync(x => x.ID == surgeryId, ct);
			if (c == null) return null;
			var p = await _db.HospitalPatients.AsNoTracking().FirstAsync(x => x.ID == c.PatientId && x.CompanyId == ctx.CompanyId, ct);
			var a = await Records(ctx).AsNoTracking().FirstOrDefaultAsync(x => x.SurgeryId == surgeryId, ct);
			var entries = a == null ? new List<HospitalAnaesthesiaEntry>() : await Entries(ctx).AsNoTracking().Where(x => x.AnaesthesiaId == a.ID).OrderBy(x => x.At).ThenBy(x => x.ID).ToListAsync(ct);
			var ids = entries.Select(e => e.RecordedByEmployeeId).Append(a?.AssessedByEmployeeId).Append(a?.PacuOutByEmployeeId).Where(x => x != null).Select(x => x!.Value).Distinct().ToList();
			var people = (await _db.Employee.AsNoTracking().Where(e => ids.Contains(e.ID)).ToListAsync(ct)).ToDictionary(e => e.ID, e => EmployeeNames.Of(e));
			int? last = entries.Where(e => e.Kind == HospitalAnaesthesiaEntryKind.Aldrete).Select(e => e.Score).LastOrDefault();
			return new HospitalAnaesthesiaView(c, p.Name, p.NameEn, p.Mrn, p.Allergies, a, entries, people, last);
		}

		/// The pre-anaesthetic assessment, saved until induction; then it is the record's and does not change.
		public async Task<HospitalResult> AssessAsync(BusinessContext ctx, int surgeryId, HospitalAnaesthesiaInput input, int? employeeId, DateTime nowLocal, CancellationToken ct = default)
		{
			if (!await _access.CanAsync(ctx, HospitalActions.Consult, null, ct)) return HospitalResult.Fail("forbidden");
			var c = await Cases(ctx).AsNoTracking().FirstOrDefaultAsync(x => x.ID == surgeryId, ct);
			if (c == null) return HospitalResult.Fail("not_found");
			if (c.Status != HospitalSurgeryStatus.Planned) return HospitalResult.Fail("The assessment locks at induction");
			if (HospitalAnaesthesiaRules.AssessmentRefusal(input, nowLocal) is string why) return HospitalResult.Fail(why);
			var a = await Records(ctx).FirstOrDefaultAsync(x => x.SurgeryId == surgeryId, ct);
			if (a == null) { a = new HospitalAnaesthesia { CompanyId = ctx.CompanyId, SurgeryId = surgeryId, PatientId = c.PatientId }; _db.Add(a); }
			a.Asa = input.Asa; a.Emergency = input.Emergency; a.WeightKg = input.WeightKg;
			a.Mallampati = input.Mallampati; a.MouthOpeningCm = input.MouthOpeningCm; a.ThyromentalCm = input.ThyromentalCm;
			a.DifficultAirway = HospitalAnaesthesiaRules.DifficultAirway(input.Mallampati, input.MouthOpeningCm, input.ThyromentalCm);
			a.LastSolidsAt = input.LastSolidsAt; a.LastMilkAt = input.LastMilkAt; a.LastClearAt = input.LastClearAt;
			a.FastingOverride = Clip(input.FastingOverride, 300); a.Technique = input.Technique!; a.PlanText = Clip(input.Plan, 1000);
			a.AssessedAt = nowLocal; a.AssessedByEmployeeId = employeeId;
			await _db.SaveChangesAsync(ct);
			return HospitalResult.Success(a.ID);
		}

		public async Task<HospitalResult> AddEntryAsync(BusinessContext ctx, int surgeryId, HospitalAnaesthesiaEntryInput input, int? employeeId, DateTime nowLocal, CancellationToken ct = default)
		{
			if (!await _access.CanAsync(ctx, HospitalActions.Consult, null, ct)) return HospitalResult.Fail("forbidden");
			var c = await Cases(ctx).AsNoTracking().FirstOrDefaultAsync(x => x.ID == surgeryId, ct);
			var a = c == null ? null : await Records(ctx).AsNoTracking().FirstOrDefaultAsync(x => x.SurgeryId == surgeryId, ct);
			if (c == null) return HospitalResult.Fail("not_found");
			if (a == null) return HospitalResult.Fail(HospitalAnaesthesiaRules.NoAssessment);
			var done = await Entries(ctx).Where(x => x.AnaesthesiaId == a.ID && x.Kind == HospitalAnaesthesiaEntryKind.Event).Select(x => x.Code!).ToListAsync(ct);
			var at = input.At ?? nowLocal;
			if (HospitalAnaesthesiaRules.EntryRefusal(input, c.Status, a, done, at, nowLocal) is string why) return HospitalResult.Fail(why);
			var e = new HospitalAnaesthesiaEntry { CompanyId = ctx.CompanyId, AnaesthesiaId = a.ID, At = at, Kind = input.Kind!, Note = Clip(input.Note, 300), RecordedAt = nowLocal, RecordedByEmployeeId = employeeId };
			switch (input.Kind)
			{
				case HospitalAnaesthesiaEntryKind.Event: e.Code = input.Code; break;
				case HospitalAnaesthesiaEntryKind.Drug:
				case HospitalAnaesthesiaEntryKind.Fluid: e.Code = Clip(input.Code, 100); e.Dose = input.Dose; e.Unit = Clip(input.Unit, 20); e.Route = Clip(input.Route, 20); break;
				case HospitalAnaesthesiaEntryKind.Vitals: e.HeartRate = input.HeartRate; e.SystolicBp = input.SystolicBp; e.DiastolicBp = input.DiastolicBp; e.SpO2 = input.SpO2; e.EtCo2 = input.EtCo2; break;
				default: e.Code = string.Join(",", input.Items); e.Score = HospitalAnaesthesiaRules.Aldrete(input.Items).Score; break;
			}
			_db.Add(e);
			await _db.SaveChangesAsync(ct);
			return HospitalResult.Success(e.ID);
		}

		/// The patient arrives in recovery once out of the theatre (the operation closed).
		public async Task<HospitalResult> PacuInAsync(BusinessContext ctx, int surgeryId, DateTime nowLocal, CancellationToken ct = default)
		{
			if (!await _access.CanAsync(ctx, HospitalActions.Consult, null, ct)) return HospitalResult.Fail("forbidden");
			var c = await Cases(ctx).AsNoTracking().FirstOrDefaultAsync(x => x.ID == surgeryId, ct);
			if (c == null) return HospitalResult.Fail("not_found");
			if (c.Status != HospitalSurgeryStatus.Done) return HospitalResult.Fail("Close the operation before the patient goes to recovery");
			int n = await Records(ctx).Where(x => x.SurgeryId == surgeryId && x.PacuInAt == null).ExecuteUpdateAsync(u => u.SetProperty(x => x.PacuInAt, nowLocal), ct);
			return n == 1 ? HospitalResult.Success(surgeryId)
				: await Records(ctx).AnyAsync(x => x.SurgeryId == surgeryId, ct) ? HospitalResult.Fail("The arrival in recovery is already recorded") : HospitalResult.Fail(HospitalAnaesthesiaRules.NoAssessment);
		}

		/// The patient leaves recovery — only with a last Aldrete of 9 or more.
		public async Task<HospitalResult> PacuOutAsync(BusinessContext ctx, int surgeryId, int? employeeId, DateTime nowLocal, CancellationToken ct = default)
		{
			if (!await _access.CanAsync(ctx, HospitalActions.Consult, null, ct)) return HospitalResult.Fail("forbidden");
			var a = await Records(ctx).AsNoTracking().FirstOrDefaultAsync(x => x.SurgeryId == surgeryId, ct);
			if (a == null) return HospitalResult.Fail("not_found");
			int? last = await Entries(ctx).Where(x => x.AnaesthesiaId == a.ID && x.Kind == HospitalAnaesthesiaEntryKind.Aldrete).OrderByDescending(x => x.At).ThenByDescending(x => x.ID).Select(x => x.Score).FirstOrDefaultAsync(ct);
			if (HospitalAnaesthesiaRules.PacuOutRefusal(a, last) is string why) return HospitalResult.Fail(why, last ?? 0);
			int n = await Records(ctx).Where(x => x.ID == a.ID && x.PacuOutAt == null).ExecuteUpdateAsync(u => u.SetProperty(x => x.PacuOutAt, nowLocal).SetProperty(x => x.PacuOutByEmployeeId, employeeId), ct);
			return n == 1 ? HospitalResult.Success(surgeryId) : HospitalResult.Fail("The patient already left recovery");
		}
	}
}
