using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Hospital;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL.Hospital
{
	// ==========================================================================================
	// HOSPITAL H71 — ward observations as NEWS2, and the Morse / Braden risk assessments. The rules are pure and follow
	// the published scales band for band; the service records, works out what is due, and — when NEWS2 calls for a
	// response — opens an urgent nursing task on the ward board ("tell the doctor now"), once per set of observations.
	// ==========================================================================================
	public sealed class HospitalObservationInput
	{
		public int AdmissionId { get; set; }
		public int? RespRate { get; set; }
		public int? SpO2 { get; set; }
		public bool SpO2Scale2 { get; set; }
		public bool OnOxygen { get; set; }
		public int? SystolicBp { get; set; }
		public int? DiastolicBp { get; set; }
		public int? HeartRate { get; set; }
		public string? Consciousness { get; set; }
		public decimal? TempC { get; set; }
		public int? PainScore { get; set; }
		public string? Note { get; set; }
	}

	public sealed class HospitalRiskInput
	{
		public int AdmissionId { get; set; }
		public string? Kind { get; set; }
		public List<int> Items { get; set; } = new();
		public string? Note { get; set; }
	}

	/// One patient's line on the ward's observation panel.
	public sealed record HospitalObsPatient(int AdmissionId, int PatientId, string Name, string NameEn, string Mrn, string? Photo, string Bed,
		HospitalObservation? Last, DateTime? NextDueAt, bool ObsOverdue, HospitalRiskAssessment? Morse, bool MorseDue, HospitalRiskAssessment? Braden, bool BradenDue,
		// MedRecReady false = the reconciliation module is not installed, so neither the green nor
		// the red badge is a claim this board is entitled to make about this patient.
		DateTime? PainReassessAt, bool MedRecSigned, bool MedRecOverdue, bool MedRecReady = true);

	public sealed record HospitalObsChart(int AdmissionId, string Name, string NameEn, string Mrn, IReadOnlyList<HospitalObservation> Observations,
		IReadOnlyList<HospitalRiskAssessment> Assessments, IReadOnlyDictionary<int, string> People);

	public static class HospitalNews2Rules
	{
		// ---- each parameter's score, as the RCP chart bands it ----
		public static int Resp(int rr) => rr <= 8 ? 3 : rr <= 11 ? 1 : rr <= 20 ? 0 : rr <= 24 ? 2 : 3;
		public static int SpO2Scale1(int s) => s <= 91 ? 3 : s <= 93 ? 2 : s <= 95 ? 1 : 0;
		/// Scale 2: 88–92 scores nothing; above 92 scores only on oxygen (93–94 → 1, 95–96 → 2, ≥97 → 3).
		public static int SpO2Scale2(int s, bool onOxygen) => s <= 83 ? 3 : s <= 85 ? 2 : s <= 87 ? 1 : s <= 92 ? 0 : !onOxygen ? 0 : s <= 94 ? 1 : s <= 96 ? 2 : 3;
		public static int Air(bool onOxygen) => onOxygen ? 2 : 0;
		public static int Systolic(int sbp) => sbp <= 90 ? 3 : sbp <= 100 ? 2 : sbp <= 110 ? 1 : sbp <= 219 ? 0 : 3;
		public static int Pulse(int hr) => hr <= 40 ? 3 : hr <= 50 ? 1 : hr <= 90 ? 0 : hr <= 110 ? 1 : hr <= 130 ? 2 : 3;
		public static int Conscious(string c) => c == HospitalConsciousness.Alert ? 0 : 3;
		public static int Temp(decimal t) => t <= 35.0m ? 3 : t <= 36.0m ? 1 : t <= 38.0m ? 0 : t <= 39.0m ? 1 : 2;

		/// The total and whether any one parameter scored 3 — null when a parameter is missing (an incomplete set is not a NEWS2).
		public static (int Total, bool AnyThree)? Score(HospitalObservationInput i)
		{
			if (i.RespRate is not int rr || i.SpO2 is not int sp || i.SystolicBp is not int sbp || i.HeartRate is not int hr || i.TempC is not decimal t
				|| !HospitalConsciousness.All.Contains(i.Consciousness ?? "")) return null;
			var parts = new[] { Resp(rr), i.SpO2Scale2 ? SpO2Scale2(sp, i.OnOxygen) : SpO2Scale1(sp), Air(i.OnOxygen), Systolic(sbp), Pulse(hr), Conscious(i.Consciousness!), Temp(t) };
			return (parts.Sum(), parts.Any(p => p == 3));
		}

		/// The clinical risk: 7+ high, 5–6 medium, a single 3 low-medium, otherwise low.
		public static string Risk(int total, bool anyThree) => total >= 7 ? HospitalNews2Risk.High : total >= 5 ? HospitalNews2Risk.Medium : anyThree ? HospitalNews2Risk.LowSingle : HospitalNews2Risk.Low;

		/// The longest the next set may wait: 0 → 12 h; 1–4 → 4 h; a single 3 or 5–6 → 1 h; 7+ → continuous (read as 1 h).
		public static int HoursToNext(int total, string risk) => risk is HospitalNews2Risk.High or HospitalNews2Risk.Medium or HospitalNews2Risk.LowSingle ? 1 : total == 0 ? 12 : 4;

		/// A risk that calls for a response from the doctor.
		public static bool Escalates(string risk) => risk is HospitalNews2Risk.LowSingle or HospitalNews2Risk.Medium or HospitalNews2Risk.High;

		/// Values no human body gives back are refused, so a typo is caught at the bedside.
		public static string? Refusal(HospitalObservationInput i)
		{
			if (i.RespRate is null && i.SpO2 is null && i.SystolicBp is null && i.HeartRate is null && i.TempC is null && i.PainScore is null && string.IsNullOrEmpty(i.Consciousness))
				return "Enter at least one reading";
			if (i.RespRate is < 0 or > 80) return "Respiration must be 0 to 80 a minute";
			if (i.SpO2 is < 50 or > 100) return "SpO₂ must be 50 to 100%";
			if (i.SystolicBp is < 40 or > 300 || i.DiastolicBp is < 20 or > 200) return "Blood pressure is out of range";
			if (i.SystolicBp is int s && i.DiastolicBp is int d && d >= s) return "The diastolic pressure must be below the systolic";
			if (i.HeartRate is < 20 or > 250) return "Pulse must be 20 to 250 a minute";
			if (i.TempC is < 30m or > 45m) return "Temperature must be 30 to 45 °C";
			if (i.PainScore is < 0 or > 10) return "Pain is scored 0 to 10";
			if (!string.IsNullOrEmpty(i.Consciousness) && !HospitalConsciousness.All.Contains(i.Consciousness)) return "Choose the level of consciousness";
			return null;
		}

		/// WHEN THE NEXT SET IS DUE — and an incomplete one is due AT ONCE.
		///
		/// Score() returns null for a set missing any parameter, which is right: an incomplete set
		/// is not a NEWS2. What the ward board then does with that null is the question, and the
		/// code and its own comment disagreed. The comment said "no set yet, or an incomplete one:
		/// the next is due now"; the code gave an incomplete set FOUR HOURS — the interval for a
		/// patient scored 1–4, which is a claim about a patient who was never scored at all.
		///
		/// The comment is the specification and the safer side of the argument. A set with the
		/// pulse and the temperature and nothing else means nobody knows this patient's risk, and
		/// the answer to that is to finish the set, not to wait on it. Measured first: 1 of the 4
		/// sets recorded here is incomplete, so this is not a theoretical branch.
		public static DateTime? NextDueAt(HospitalObservation? last)
		{
			if (last == null) return null;                                     // nothing yet: overdue
			if (last.News2 is int n && last.News2Risk != null)
				return last.TakenAt.AddHours(HoursToNext(n, last.News2Risk));
			return last.TakenAt;                                               // incomplete: due when it was taken
		}

		/// A pain of 4 or more is reassessed within the hour.
		public static DateTime? PainReassessAt(HospitalObservation? last) => last?.PainScore is >= 4 ? last.TakenAt.AddHours(1) : null;
	}

	public static class HospitalNews2Text
	{
		/// A nursing task's title as the reader sees it: an escalation code becomes its sentence (key "news2task:{risk}"
		/// with the score), anything else is shown as typed.
		public static string TaskText(string title, Func<string, string> localize)
		{
			var p = (title ?? "").Split('|');
			if (p.Length == 3 && p[0] == "NEWS2" && HospitalNews2Risk.All.Contains(p[1]) && int.TryParse(p[2], out var n))
				return string.Format(localize("news2task:" + p[1]), n);
			// H74: a task an order set put on the board carries both its languages ("OS|عربي|English")
			if (p.Length == 3 && p[0] == "OS")
			{
				bool ar = System.Globalization.CultureInfo.CurrentUICulture.Name.StartsWith("ar");
				var (mine, other) = ar ? (p[1], p[2]) : (p[2], p[1]);
				return string.IsNullOrWhiteSpace(mine) ? other : mine;
			}
			return title ?? "";
		}
	}

	public static class HospitalRiskRules
	{
		/// The allowed scores of each Morse item: history of falling, secondary diagnosis, ambulatory aid, IV or heparin
		/// lock, gait, mental status.
		public static readonly IReadOnlyList<int[]> MorseItems = new[] { new[] { 0, 25 }, new[] { 0, 15 }, new[] { 0, 15, 30 }, new[] { 0, 20 }, new[] { 0, 10, 20 }, new[] { 0, 15 } };
		/// Braden: sensory perception, moisture, activity, mobility, nutrition (1–4 each), friction and shear (1–3).
		public static readonly IReadOnlyList<int[]> BradenItems = new[] { new[] { 1, 2, 3, 4 }, new[] { 1, 2, 3, 4 }, new[] { 1, 2, 3, 4 }, new[] { 1, 2, 3, 4 }, new[] { 1, 2, 3, 4 }, new[] { 1, 2, 3 } };

		public static IReadOnlyList<int[]> ItemsOf(string kind) => kind == HospitalRiskKind.Braden ? BradenItems : MorseItems;

		/// Morse: under 25 low, 25–44 moderate, 45 and over high.
		public static string MorseLevel(int score) => score >= 45 ? "High" : score >= 25 ? "Moderate" : "Low";
		/// Braden (lower is worse): 19+ none, 15–18 mild, 13–14 moderate, 10–12 high, 9 or less very high.
		public static string BradenLevel(int score) => score <= 9 ? "VeryHigh" : score <= 12 ? "High" : score <= 14 ? "Moderate" : score <= 18 ? "Mild" : "None";

		public static string? Refusal(HospitalRiskInput i)
		{
			if (!HospitalRiskKind.All.Contains(i.Kind ?? "")) return "Choose the assessment";
			var allowed = ItemsOf(i.Kind!);
			if (i.Items.Count != allowed.Count) return "Answer every item";
			for (int k = 0; k < allowed.Count; k++) if (!allowed[k].Contains(i.Items[k])) return "Answer every item";
			return null;
		}

		public static (int Score, string Level) Score(HospitalRiskInput i)
		{
			int s = i.Items.Sum();
			return (s, i.Kind == HospitalRiskKind.Braden ? BradenLevel(s) : MorseLevel(s));
		}

		/// A high falls risk (Morse 45+) or a high pressure-injury risk (Braden 12 or less) puts precautions on the board.
		public static bool Flags(HospitalRiskAssessment? a) => a != null && (a.Kind == HospitalRiskKind.Morse ? a.Score >= 45 : a.Score <= 12);

		/// Each scale is reassessed every 24 hours (on admission first).
		public static bool Due(HospitalRiskAssessment? last, DateTime nowUtc) => last == null || last.AssessedAt <= nowUtc.AddHours(-24);
	}

	public sealed class HospitalObservationService
	{
		public const string NoTables = "Ward observations are not installed yet (hospital_h71_observations.sql).";
		private readonly CrossDbContext _db;
		private readonly IHospitalAccessService _access;
		public HospitalObservationService(CrossDbContext db, IHospitalAccessService access) { _db = db; _access = access; }
		private static bool Missing(Microsoft.Data.SqlClient.SqlException ex) => ex.Number is 207 or 208;

		/// The patient of an active admission, or null.
		private async Task<int?> ActivePatientAsync(BusinessContext ctx, int admissionId, CancellationToken ct) =>
			await _db.HospitalAdmissions.AsNoTracking().Where(a => a.ID == admissionId && a.CompanyId == ctx.CompanyId && a.Status == HospitalAdmissionStatus.Active)
				.Select(a => (int?)a.PatientId).FirstOrDefaultAsync(ct);

		/// Records a set of observations; when NEWS2 calls for a response, an urgent task goes on the ward board. The
		/// result's id is the observation; the score and risk travel back for the nurse to see at once.
		public async Task<(HospitalResult Result, int? News2, string? Risk, bool Escalated)> RecordAsync(BusinessContext ctx, HospitalObservationInput input, int? employeeId, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await _access.CanAsync(ctx, HospitalActions.Admit, null, ct)) return (HospitalResult.Fail("forbidden"), null, null, false);
			if (HospitalNews2Rules.Refusal(input) is string why) return (HospitalResult.Fail(why), null, null, false);
			var stay = await ActivePatientAsync(ctx, input.AdmissionId, ct);
			if (stay == null) return (HospitalResult.Fail("This admission is not active"), null, null, false);
			var score = HospitalNews2Rules.Score(input);
			string? risk = score is var (t, three) ? HospitalNews2Rules.Risk(t, three) : null;
			try
			{
				var row = new HospitalObservation
				{
					CompanyId = ctx.CompanyId, AdmissionId = input.AdmissionId, PatientId = stay.Value, TakenAt = nowUtc,
					RespRate = input.RespRate, SpO2 = input.SpO2, SpO2Scale2 = input.SpO2Scale2, OnOxygen = input.OnOxygen, SystolicBp = input.SystolicBp, DiastolicBp = input.DiastolicBp,
					HeartRate = input.HeartRate, Consciousness = string.IsNullOrEmpty(input.Consciousness) ? null : input.Consciousness, TempC = input.TempC, PainScore = input.PainScore,
					News2 = score?.Total, News2Risk = risk, Note = Clip(input.Note, 300), RecordedByEmployeeId = employeeId, RecordedAt = nowUtc,
				};
				_db.Add(row);
				bool escalated = false;
				if (risk != null && HospitalNews2Rules.Escalates(risk))
				{
					// the board's task list is the nurse's own: an urgent line that stays until someone ticks it done
					_db.Add(new HospitalNursingTask
					{
						CompanyId = ctx.CompanyId, AdmissionId = input.AdmissionId, PatientId = stay.Value,
						Title = Clip(EscalationTitle(score!.Value.Total, risk), 200)!, DueAt = nowUtc, CreatedAt = nowUtc, CreatedByEmployeeId = employeeId,
					});
					escalated = true;
				}
				await _db.SaveChangesAsync(ct);
				return (HospitalResult.Success(row.ID), score?.Total, risk, escalated);
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex)) { return (HospitalResult.Fail(NoTables), null, null, false); }
		}

		/// The task stores a code, not words ("NEWS2|High|7"), so the board shows it in the reader's language (TaskText).
		public static string EscalationTitle(int total, string risk) => $"NEWS2|{risk}|{total}";

		public async Task<HospitalResult> AssessAsync(BusinessContext ctx, HospitalRiskInput input, int? employeeId, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await _access.CanAsync(ctx, HospitalActions.Admit, null, ct)) return HospitalResult.Fail("forbidden");
			if (HospitalRiskRules.Refusal(input) is string why) return HospitalResult.Fail(why);
			var stay = await ActivePatientAsync(ctx, input.AdmissionId, ct);
			if (stay == null) return HospitalResult.Fail("This admission is not active");
			var (score, level) = HospitalRiskRules.Score(input);
			try
			{
				var row = new HospitalRiskAssessment
				{
					CompanyId = ctx.CompanyId, AdmissionId = input.AdmissionId, PatientId = stay.Value, Kind = input.Kind!, Items = string.Join(",", input.Items),
					Score = score, Level = level, Note = Clip(input.Note, 300), AssessedAt = nowUtc, AssessedByEmployeeId = employeeId,
				};
				_db.Add(row);
				await _db.SaveChangesAsync(ct);
				return HospitalResult.Success(row.ID);
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex)) { return HospitalResult.Fail(NoTables); }
		}

		/// The ward's panel: every patient in a bed with their last set, when the next is due, and the two risk scales.
		public async Task<(IReadOnlyList<HospitalObsPatient> Patients, bool Ready)> WardAsync(BusinessContext ctx, int wardId, DateTime nowUtc, CancellationToken ct = default)
		{
			var here = await (from s in _db.HospitalBedStays.AsNoTracking()
							  join b in _db.HospitalBeds.AsNoTracking() on s.BedId equals b.ID
							  join r in _db.HospitalRooms.AsNoTracking() on s.RoomId equals r.ID
							  join a in _db.HospitalAdmissions.AsNoTracking() on s.AdmissionId equals a.ID
							  join p in _db.HospitalPatients.AsNoTracking() on s.PatientId equals p.ID
							  where s.CompanyId == ctx.CompanyId && s.EndedAt == null && r.WardId == wardId && a.Status == HospitalAdmissionStatus.Active
							  orderby b.Code
							  select new { s.AdmissionId, p.ID, p.Name, p.NameEn, p.Mrn, p.PhotoPath, Bed = b.Code, a.AdmittedAt }).ToListAsync(ct);
			var ids = here.Select(h => h.AdmissionId).ToList();
			// H72: whose home medicines are reconciled (the panel flags the rest, red after a day)
			var (reconciled, medRecReady) = await HospitalMedReconService.AdmissionSignedAsync(_db, ctx.CompanyId, ids, ct);
			List<HospitalObservation> obs; List<HospitalRiskAssessment> risks;
			try
			{
				// the latest set and the latest of each scale per admission
				obs = await _db.Set<HospitalObservation>().AsNoTracking().Where(o => o.CompanyId == ctx.CompanyId && ids.Contains(o.AdmissionId))
					.GroupBy(o => o.AdmissionId).Select(g => g.OrderByDescending(o => o.TakenAt).ThenByDescending(o => o.ID).First()).ToListAsync(ct);
				risks = await _db.Set<HospitalRiskAssessment>().AsNoTracking().Where(o => o.CompanyId == ctx.CompanyId && ids.Contains(o.AdmissionId))
					.GroupBy(o => new { o.AdmissionId, o.Kind }).Select(g => g.OrderByDescending(o => o.AssessedAt).ThenByDescending(o => o.ID).First()).ToListAsync(ct);
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex)) { return (Array.Empty<HospitalObsPatient>(), false); }
			var list = here.Select(h =>
			{
				var last = obs.FirstOrDefault(o => o.AdmissionId == h.AdmissionId);
				var morse = risks.FirstOrDefault(r => r.AdmissionId == h.AdmissionId && r.Kind == HospitalRiskKind.Morse);
				var braden = risks.FirstOrDefault(r => r.AdmissionId == h.AdmissionId && r.Kind == HospitalRiskKind.Braden);
				DateTime? next = HospitalNews2Rules.NextDueAt(last);
				bool overdue = last == null || next <= nowUtc;
				var pain = HospitalNews2Rules.PainReassessAt(last);
				return new HospitalObsPatient(h.AdmissionId, h.ID, h.Name, h.NameEn, h.Mrn, h.PhotoPath, h.Bed, last, next, overdue,
					morse, HospitalRiskRules.Due(morse, nowUtc), braden, HospitalRiskRules.Due(braden, nowUtc), pain,
					HospitalMedReconRules.Badge(medRecReady, reconciled.Contains(h.AdmissionId), h.AdmittedAt, nowUtc).Signed,
					HospitalMedReconRules.Badge(medRecReady, reconciled.Contains(h.AdmissionId), h.AdmittedAt, nowUtc).Overdue,
					medRecReady);
			}).ToList();
			return (list, true);
		}

		/// One admission's chart: the last 30 sets and every assessment, newest first.
		public async Task<HospitalObsChart?> ChartAsync(BusinessContext ctx, int admissionId, CancellationToken ct = default)
		{
			var a = await (from x in _db.HospitalAdmissions.AsNoTracking()
						   join p in _db.HospitalPatients.AsNoTracking() on x.PatientId equals p.ID
						   where x.ID == admissionId && x.CompanyId == ctx.CompanyId
						   select new { p.Name, p.NameEn, p.Mrn }).FirstOrDefaultAsync(ct);
			if (a == null) return null;
			try
			{
				var obs = await _db.Set<HospitalObservation>().AsNoTracking().Where(o => o.CompanyId == ctx.CompanyId && o.AdmissionId == admissionId).OrderByDescending(o => o.TakenAt).Take(30).ToListAsync(ct);
				var ass = await _db.Set<HospitalRiskAssessment>().AsNoTracking().Where(o => o.CompanyId == ctx.CompanyId && o.AdmissionId == admissionId).OrderByDescending(o => o.AssessedAt).Take(30).ToListAsync(ct);
				var eids = obs.Select(o => o.RecordedByEmployeeId).Concat(ass.Select(x => x.AssessedByEmployeeId)).Where(x => x != null).Select(x => x!.Value).Distinct().ToList();
				var people = await _db.Employee.AsNoTracking().Where(e => eids.Contains(e.ID) && e.EmpCompanyID == ctx.CompanyId)
					.Select(e => new { e.ID, e.FullName, e.FullNameEn }).ToDictionaryAsync(e => e.ID, e => DisplayName.Of(e.FullName ?? "", e.FullNameEn), ct);
				return new HospitalObsChart(admissionId, a.Name, a.NameEn, a.Mrn, obs, ass, people);
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex)) { return new HospitalObsChart(admissionId, a.Name, a.NameEn, a.Mrn, Array.Empty<HospitalObservation>(), Array.Empty<HospitalRiskAssessment>(), new Dictionary<int, string>()); }
		}

		private static string? Clip(string? s, int max) => string.IsNullOrWhiteSpace(s) ? null : s.Trim()[..Math.Min(max, s.Trim().Length)];
	}
}
