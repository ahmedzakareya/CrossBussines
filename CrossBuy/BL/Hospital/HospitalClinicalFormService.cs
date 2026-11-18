using System.Globalization;
using System.Text.Json;
using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Hospital;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL.Hospital
{
	// ==========================================================================================
	// HOSPITAL H93 — the template engine at the visit. A published template version is filled on a visit (a form);
	// at SIGN-OFF every value becomes one observation row, flagged against the dictionary's limits:
	//   * a field shown only when another field is above a value is neither required nor recorded while hidden;
	//   * a field with a side is answered per side (R / L) and makes one row per side;
	//   * calculated concepts are computed at sign-off from the form's values (and the patient's age and sex):
	//     BMI, eGFR CKD-EPI 2021 (race-free), the KDIGO 2024 category, a score's sum;
	//   * a "last lab" field is pre-filled from the last VERIFIED lab result within its look-back days.
	// Trends, alerts and care gaps read ONLY the observation rows.
	// ==========================================================================================
	public sealed record HospitalObsPoint(DateTime At, decimal? Value, decimal? Value2, string? Code, string? Side, string Flag, string? Unit);
	public sealed record HospitalCareItem(HospitalConcept Concept, string Kind, string Text, string TextEn, DateTime? LastAt, int? DaysOverdue);
	public sealed record HospitalPrefill(int FieldId, string Value, DateTime At);
	public sealed record HospitalFormValue(HospitalClinicalTemplateField Field, HospitalConcept Concept, string? Side, decimal? Value, decimal? Value2, string? Code);

	public static class HospitalClinicalCalc
	{
		public static decimal? Bmi(decimal? kg, decimal? cm) => kg is > 0 && cm is > 0 ? Math.Round(kg.Value / (cm.Value / 100m * (cm.Value / 100m)), 1) : null;

		/// CKD-EPI 2021, race-free: 142 × min(Scr/κ,1)^α × max(Scr/κ,1)^−1.200 × 0.9938^age × 1.012 [female]; Scr in mg/dL.
		public static decimal? EgfrCkdEpi2021(decimal? scrMgDl, int? age, bool female)
		{
			if (scrMgDl is not > 0 || age is not >= 18) return null;
			double k = female ? 0.7 : 0.9, a = female ? -0.241 : -0.302, r = (double)scrMgDl.Value / k;
			double e = 142 * Math.Pow(Math.Min(r, 1), a) * Math.Pow(Math.Max(r, 1), -1.200) * Math.Pow(0.9938, age.Value) * (female ? 1.012 : 1);
			return Math.Round((decimal)e, 0);
		}

		/// H97: the ImmunoCAP class of a specific IgE (kU/L): 0 under 0.35, 1 to 0.7, 2 to 3.5, 3 to 17.5, 4 to 50, 5 to 100, 6 above.
		public static int? IgeClass(decimal? ku) => ku == null || ku < 0 ? null : ku < 0.35m ? 0 : ku < 0.7m ? 1 : ku < 3.5m ? 2 : ku < 17.5m ? 3 : ku < 50m ? 4 : ku < 100m ? 5 : 6;

		/// H188: the corrected QT in ms; RR = 60 / heart rate (s). Bazett divides by √RR, Fridericia by ∛RR.
		public static decimal? QtcBazett(decimal? qtMs, decimal? hr) => qtMs is > 0 and < 1000 && hr is >= 20 and <= 300 ? Math.Round((decimal)((double)qtMs.Value / Math.Sqrt(60.0 / (double)hr.Value)), 0) : null;
		public static decimal? QtcFridericia(decimal? qtMs, decimal? hr) => qtMs is > 0 and < 1000 && hr is >= 20 and <= 300 ? Math.Round((decimal)((double)qtMs.Value / Math.Cbrt(60.0 / (double)hr.Value)), 0) : null;

		/// H188: ACR TI-RADS 2017 — 0–1 points TR1, 2 TR2, 3 TR3, 4–6 TR4, 7 or more TR5.
		public static int? TiRadsLevel(decimal? points) => points == null || points < 0 ? null : points <= 1 ? 1 : points == 2 ? 2 : points == 3 ? 3 : points <= 6 ? 4 : 5;

		/// H188: what the level and the largest diameter (cm) call for — FNA at or above the level's FNA size, follow-up at or
		/// above its follow-up size, otherwise none (TR1–TR2: none). TR3 2.5/1.5, TR4 1.5/1.0, TR5 1.0/0.5 cm.
		public static string? TiRadsAdvice(int? level, decimal? sizeCm)
		{
			if (level == null) return null;
			if (level <= 2 || sizeCm is not > 0) return "None";
			var (fna, follow) = level switch { 3 => (2.5m, 1.5m), 4 => (1.5m, 1.0m), _ => (1.0m, 0.5m) };
			return sizeCm >= fna ? "FNA" : sizeCm >= follow ? "FollowUp" : "None";
		}

		/// H188: PASI = Σ region weight × area (0–6) × (erythema + induration + scaling, each 0–4); the regions in the order
		/// head (0.1), upper limbs (0.2), trunk (0.3), lower limbs (0.4), each as area, erythema, induration, scaling.
		public static decimal? Pasi(IReadOnlyList<decimal?> v)
		{
			if (v.Count != 16 || v.Any(x => x == null)) return null;
			decimal[] w = { 0.1m, 0.2m, 0.3m, 0.4m };
			decimal total = 0;
			for (int r = 0; r < 4; r++)
			{
				decimal area = v[r * 4]!.Value, e = v[r * 4 + 1]!.Value, i = v[r * 4 + 2]!.Value, s = v[r * 4 + 3]!.Value;
				if (area is < 0 or > 6 || e is < 0 or > 4 || i is < 0 or > 4 || s is < 0 or > 4) return null;
				total += w[r] * area * (e + i + s);
			}
			return Math.Round(total, 1);
		}

		/// H188: WHO — a T-score of −1.0 or more is normal, between −1.0 and −2.5 osteopenia, −2.5 or less osteoporosis.
		public static string? TScoreClass(decimal? t) => t == null || t < -10 || t > 10 ? null : t >= -1.0m ? "Normal" : t > -2.5m ? "Osteopenia" : "Osteoporosis";

		public static string? GCategory(decimal? egfr) => egfr == null ? null : egfr >= 90 ? "G1" : egfr >= 60 ? "G2" : egfr >= 45 ? "G3a" : egfr >= 30 ? "G3b" : egfr >= 15 ? "G4" : "G5";
		public static string? ACategory(decimal? acrMgG) => acrMgG == null ? null : acrMgG < 30 ? "A1" : acrMgG <= 300 ? "A2" : "A3";

		/// The KDIGO 2024 heat-map risk of a G × A cell: low, moderate, high, very high.
		public static string? KdigoRisk(string? g, string? a)
		{
			if (g == null || a == null) return null;
			int gi = Array.IndexOf(new[] { "G1", "G2", "G3a", "G3b", "G4", "G5" }, g), ai = Array.IndexOf(new[] { "A1", "A2", "A3" }, a);
			if (gi < 0 || ai < 0) return null;
			int s = (gi <= 1 ? 0 : gi - 1) + ai;   // G1/G2 alike; each step down a G row or across an A column adds a level
			return s <= 0 ? "Low" : s == 1 ? "Moderate" : s == 2 ? "High" : "VeryHigh";
		}

		/// H173 (register rule "kdigo", 2024.1): a cell is a kidney DISEASE marker when the eGFR is under 60 (G3a–G5) or
		/// the ACR is 30 or more (A2–A3); G1–G2 with A1 is not CKD at all.
		public static bool KdigoAbnormal(string g, string a) => g is "G3a" or "G3b" or "G4" or "G5" || a is "A2" or "A3";

		/// KDIGO defines CKD by an abnormality that lasts MORE THAN THREE MONTHS: the stage is confirmed when an abnormal
		/// stage was also recorded at least 90 days before this one; otherwise it is provisional (repeat to confirm).
		public static bool KdigoConfirmed(DateTime at, IEnumerable<(string G, string A, DateTime At)> earlier) =>
			earlier.Any(e => e.At <= at.AddDays(-90) && KdigoAbnormal(e.G, e.A));

		public static int? AgeOn(DateTime? birth, DateTime day)
		{
			if (birth is not DateTime b) return null;
			int y = day.Year - b.Year; if (day.Date < b.Date.AddYears(y)) y--; return y;
		}
	}

	/// H96: visual acuity between Snellen and logMAR. CF and HM take the values of Schulze-Bonsel et al. (2006): 1.9 and 2.3.
	public static class HospitalVision
	{
		private static readonly System.Text.RegularExpressions.Regex Snellen = new(@"^\s*(\d+(?:\.\d+)?)\s*/\s*(\d+(?:\.\d+)?)\s*$");

		public static decimal? LogMar(string? raw)
		{
			var t = (raw ?? "").Trim().ToUpperInvariant();
			if (t == "CF") return 1.9m;
			if (t == "HM") return 2.3m;
			var m = Snellen.Match(t);
			if (!m.Success) return null;
			double num = double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture), den = double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
			if (num <= 0 || den <= 0) return null;
			return Math.Round((decimal)Math.Log10(den / num), 2);
		}

		/// The 6-metre Snellen line of a logMAR ("6/18"), snapped to a standard chart; CF / HM for their conventional values.
		public static string Snellen6(decimal logMar)
		{
			if (logMar >= 2.3m) return "HM";
			if (logMar >= 1.9m) return "CF";
			// the nearest line of a standard 6-metre chart (what the patient actually read)
			var lines = new[] { 3, 4, 5, 6, 7.5, 9, 12, 18, 24, 36, 60 };
			double best = lines.OrderBy(x => Math.Abs(Math.Log10(x / 6) - (double)logMar)).First();
			return "6/" + best.ToString("0.#", CultureInfo.InvariantCulture);
		}
	}

	public static class HospitalFormRules
	{
		public static string Key(int fieldId, string? side) => side == null ? fieldId.ToString(CultureInfo.InvariantCulture) : fieldId + "." + side;
		public static IEnumerable<string?> Sides(HospitalConcept c) => c.HasLaterality ? new string?[] { "R", "L" } : new string?[] { null };
		private static decimal? Dec(string? s) => decimal.TryParse((s ?? "").Trim(), NumberStyles.Number, CultureInfo.InvariantCulture, out var d) ? d : null;

		/// Shown when it has no condition, or the field it depends on (itself shown) holds a number above the threshold.
		public static bool Visible(HospitalClinicalTemplateField f, IReadOnlyDictionary<int, HospitalClinicalTemplateField> all, IReadOnlyDictionary<string, string> answers, int depth = 0)
		{
			if (f.ShowWhenFieldId is not int w) return true;
			if (depth > 20 || !all.TryGetValue(w, out var dep) || !Visible(dep, all, answers, depth + 1)) return false;
			var v = answers.Keys.Where(k => k == Key(w, null) || k.StartsWith(w + ".", StringComparison.Ordinal)).Select(k => Dec(answers[k])).Where(x => x != null).ToList();
			return v.Any(x => x > (f.ShowWhenAbove ?? 0));
		}

		/// One written answer read by its concept. Error = the refusal shown to the doctor.
		public static (decimal? V, decimal? V2, string? Code, string? Error) Parse(HospitalConcept c, string raw)
		{
			raw = (raw ?? "").Trim();
			if (raw.Length == 0) return (null, null, null, null);
			switch (c.ValueType)
			{
				case HospitalConceptType.Choice:
					var ch = HospitalConceptRules.Choices(c.Choices).FirstOrDefault(x => x.Code == raw);
					return ch.Code == null ? (null, null, null, "Choose one of the answers") : (null, null, ch.Code, null);
				case HospitalConceptType.Panel:
					var p = raw.Split('/', StringSplitOptions.TrimEntries);
					if (p.Length != 2 || Dec(p[0]) is not decimal a || Dec(p[1]) is not decimal b) return (null, null, null, "Write it as two numbers: 120/80");
					return (a, b, null, null);
				default:
					// H96: vision is written as Snellen ("6/18", "20/60", "CF", "HM") and kept as logMAR (the eye clinic's maths and trends)
					if (c.Unit == "logMAR" && !Dec(raw).HasValue)
						return HospitalVision.LogMar(raw) is decimal lm ? (lm, null, null, null) : (null, null, null, "Write the vision as Snellen (6/12, 20/40) or CF / HM");
					return Dec(raw) is decimal d ? (Math.Round(d, Math.Clamp(c.Decimals, 0, 4)), null, null, null) : (null, null, null, "Write a number");
			}
		}

		/// The values a signature records — refused with the first field missing or unreadable.
		public static (List<HospitalFormValue> Values, string? Error, object[]? Args) Read(IReadOnlyList<HospitalTemplateFieldView> fields, IReadOnlyDictionary<string, string> answers, bool en)
		{
			var all = fields.ToDictionary(f => f.Field.ID, f => f.Field);
			var list = new List<HospitalFormValue>();
			foreach (var fv in fields)
			{
				if (fv.Concept.ValueType is HospitalConceptType.Calculated || fv.Concept.ValueType == HospitalConceptType.Score && fv.Concept.Calculator == HospitalConceptCalculator.Sum) continue;   // computed, never typed
				if (!Visible(fv.Field, all, answers)) continue;
				string label = en ? fv.Field.LabelEn ?? fv.Concept.NameEn : fv.Field.Label ?? fv.Concept.Name;
				foreach (var side in Sides(fv.Concept))
				{
					answers.TryGetValue(Key(fv.Field.ID, side), out var raw);
					var (v, v2, code, err) = Parse(fv.Concept, raw ?? "");
					if (err != null) return (list, "{0}: " + err, new object[] { label + (side == null ? "" : " " + side) });
					if (v == null && code == null) { if (fv.Field.Required) return (list, "Fill the required field: {0}", new object[] { label + (side == null ? "" : " " + side) }); continue; }
					list.Add(new HospitalFormValue(fv.Field, fv.Concept, side, v, v2, code));
				}
			}
			return (list, null, null);
		}

		/// The calculated concepts of the form, from its values (inputs found by concept code) and the patient.
		/// The calculated concepts of the form, from its values (inputs found by concept code) and the patient. Computed in
		/// DEPENDENCY order: a calculated input of this form (eGFR for KDIGO) is computed in this signature first and used —
		/// never the patient's previous value, whatever order the designer put the fields in.
		public static List<HospitalFormValue> Compute(IReadOnlyList<HospitalTemplateFieldView> fields, List<HospitalFormValue> values, int? age, bool female, IReadOnlyDictionary<string, decimal> outside)
		{
			var calcs = fields.Where(f => f.Concept.Calculator != null && (f.Concept.ValueType == HospitalConceptType.Calculated || f.Concept.ValueType == HospitalConceptType.Score))
				.GroupBy(f => f.Concept.ID).Select(g => g.First()).ToList();
			var calcCodes = calcs.Select(c => c.Concept.Code).ToHashSet(StringComparer.Ordinal);
			var made = new List<HospitalFormValue>();
			var done = new HashSet<int>();
			decimal? Get(string code) => values.FirstOrDefault(v => v.Concept.Code == code && v.Side == null)?.Value
				?? made.FirstOrDefault(m => m.Concept.Code == code)?.Value
				?? (calcCodes.Contains(code) ? null : outside.TryGetValue(code, out var o) ? o : null);   // a calculated input of this form is never taken from before
			for (int pass = 0; pass <= calcs.Count && done.Count < calcs.Count; pass++)
			{
				foreach (var fv in calcs.Where(c => !done.Contains(c.Concept.ID)))
				{
					var ins = HospitalConceptRules.Inputs(fv.Concept.CalcInputs);
					// wait for any calculated input of this form not computed yet
					if (ins.Any(i => calcCodes.Contains(i) && i != fv.Concept.Code && !made.Any(m => m.Concept.Code == i) && calcs.Any(c => c.Concept.Code == i && !done.Contains(c.Concept.ID)))) continue;
					decimal? v = null; string? code = null;
					switch (fv.Concept.Calculator)
					{
						case HospitalConceptCalculator.Bmi when ins.Count >= 2: v = HospitalClinicalCalc.Bmi(Get(ins[0]), Get(ins[1])); break;
						case HospitalConceptCalculator.EgfrCkdEpi2021 when ins.Count >= 1: v = HospitalClinicalCalc.EgfrCkdEpi2021(Get(ins[0]), age, female); break;
						case HospitalConceptCalculator.Kdigo when ins.Count >= 2:
							var g = HospitalClinicalCalc.GCategory(Get(ins[0])); var a = HospitalClinicalCalc.ACategory(Get(ins[1]));
							if (g != null && a != null) code = g + " " + a; break;
						case HospitalConceptCalculator.IgeClass when ins.Count >= 1:
							if (HospitalClinicalCalc.IgeClass(Get(ins[0])) is int cls) { v = cls; code = "Class " + cls; } break;
						case HospitalConceptCalculator.QtcBazett when ins.Count >= 2: v = HospitalClinicalCalc.QtcBazett(Get(ins[0]), Get(ins[1])); break;   // H188
						case HospitalConceptCalculator.QtcFridericia when ins.Count >= 2: v = HospitalClinicalCalc.QtcFridericia(Get(ins[0]), Get(ins[1])); break;
						case HospitalConceptCalculator.TiRads when ins.Count >= 2:
							if (HospitalClinicalCalc.TiRadsLevel(Get(ins[0])) is int tr) { v = tr; code = "TR" + tr + "-" + HospitalClinicalCalc.TiRadsAdvice(tr, Get(ins[1])); } break;   // "TR4-FNA": the concept's choices name it in each language
						case HospitalConceptCalculator.Pasi when ins.Count >= 16: v = HospitalClinicalCalc.Pasi(ins.Take(16).Select(Get).ToList()); break;
						case HospitalConceptCalculator.TScoreClass when ins.Count >= 1:
							if (HospitalClinicalCalc.TScoreClass(Get(ins[0])) is string tc) { v = Get(ins[0]); code = tc; } break;
						case HospitalConceptCalculator.Sum:
							var items = ins.Select(Get).ToList();
							if (items.Count > 0 && items.All(x => x != null)) v = items.Sum(x => x!.Value); break;
					}
					done.Add(fv.Concept.ID);
					if (v != null || code != null) made.Add(new HospitalFormValue(fv.Field, fv.Concept, null, v, null, code));
				}
			}
			return made;
		}

		/// H118: the values typed so far — a draft, so a field still empty (even a required one) or unreadable is simply
		/// not there yet, never a refusal.
		public static List<HospitalFormValue> Typed(IReadOnlyList<HospitalTemplateFieldView> fields, IReadOnlyDictionary<string, string> answers)
		{
			var all = fields.ToDictionary(f => f.Field.ID, f => f.Field);
			var list = new List<HospitalFormValue>();
			foreach (var fv in fields)
			{
				if (fv.Concept.ValueType is HospitalConceptType.Calculated || fv.Concept.ValueType == HospitalConceptType.Score && fv.Concept.Calculator == HospitalConceptCalculator.Sum) continue;
				if (!Visible(fv.Field, all, answers)) continue;
				foreach (var side in Sides(fv.Concept))
				{
					if (!answers.TryGetValue(Key(fv.Field.ID, side), out var raw) || string.IsNullOrWhiteSpace(raw)) continue;
					var (v, v2, code, err) = Parse(fv.Concept, raw);
					if (err == null && (v != null || code != null)) list.Add(new HospitalFormValue(fv.Field, fv.Concept, side, v, v2, code));
				}
			}
			return list;
		}

		/// H118: the answers with the form's SUM scores added as they stand now (each under its own field's key), so an
		/// alert on a score is judged on save like any typed value. Only a score whose every item is answered is added.
		public static Dictionary<string, string> WithScores(IReadOnlyList<HospitalTemplateFieldView> fields, IReadOnlyDictionary<string, string> answers)
		{
			var outp = new Dictionary<string, string>(answers);
			var sums = fields.Where(f => f.Concept.ValueType == HospitalConceptType.Score && f.Concept.Calculator == HospitalConceptCalculator.Sum).ToList();
			if (sums.Count == 0) return outp;
			foreach (var m in Compute(sums, Typed(fields, answers), null, false, new Dictionary<string, decimal>()))
				if (m.Value is decimal v) outp[Key(m.Field.ID, null)] = v.ToString(CultureInfo.InvariantCulture);
			return outp;
		}

		public static Dictionary<string, string> Answers(string? json)
		{
			try { return JsonSerializer.Deserialize<Dictionary<string, string>>(string.IsNullOrWhiteSpace(json) ? "{}" : json) ?? new(); }
			catch (JsonException) { return new(); }
		}
	}

	public sealed class HospitalClinicalFormService
	{
		private readonly CrossDbContext _db;
		private readonly IHospitalAccessService _access;
		public HospitalClinicalFormService(CrossDbContext db, IHospitalAccessService access) { _db = db; _access = access; }
		private static bool Missing(Microsoft.Data.SqlClient.SqlException ex) => ex.Number is 207 or 208;
		private IQueryable<T> Q<T>(BusinessContext ctx) where T : class => _db.Set<T>().Where(x => EF.Property<int>(x, "CompanyId") == ctx.CompanyId);
		private HospitalClinicalContentService Content => new(_db, _access);

		/// Saves the answers of a template on a visit (a draft form; one per template on a visit). Returns the form id.
		public async Task<HospitalResult> SaveAsync(BusinessContext ctx, int encounterId, int versionId, IReadOnlyDictionary<string, string> answers, int? employeeId, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await _access.CanAsync(ctx, HospitalActions.Consult, null, ct)) return HospitalResult.Fail("forbidden");
			try
			{
				var enc = await Q<HospitalEncounter>(ctx).FirstOrDefaultAsync(e => e.ID == encounterId, ct);
				var ver = await Q<HospitalClinicalTemplateVersion>(ctx).FirstOrDefaultAsync(v => v.ID == versionId, ct);
				if (enc == null || ver == null) return HospitalResult.Fail("not_found");
				if (enc.Status == HospitalEncounterStatus.Signed) return HospitalResult.Fail("This visit is signed: its forms cannot change");
				var sameTemplate = await Q<HospitalClinicalForm>(ctx).Where(f => f.EncounterId == encounterId).Join(Q<HospitalClinicalTemplateVersion>(ctx), f => f.TemplateVersionId, v => v.ID, (f, v) => new { f, v.TemplateId })
					.Where(x => x.TemplateId == ver.TemplateId).Select(x => x.f).FirstOrDefaultAsync(ct);
				if (sameTemplate == null && ver.Status != HospitalTemplateStatus.Published) return HospitalResult.Fail("Only a published template can be filled");
				if (sameTemplate != null && sameTemplate.TemplateVersionId != ver.ID) return HospitalResult.Fail("This visit already has this template on another version");
				if (sameTemplate?.Status == HospitalFormStatus.Signed) return HospitalResult.Fail("This form is signed: it cannot change");
				var form = sameTemplate ?? new HospitalClinicalForm { CompanyId = ctx.CompanyId, PatientId = enc.PatientId, EncounterId = enc.ID, TemplateVersionId = ver.ID, CreatedAt = nowUtc, CreatedByEmployeeId = employeeId };
				if (sameTemplate == null) _db.Add(form);
				form.AnswersJson = JsonSerializer.Serialize(answers.Where(a => !string.IsNullOrWhiteSpace(a.Value)).ToDictionary(a => a.Key, a => a.Value.Trim()[..Math.Min(200, a.Value.Trim().Length)]));
				form.UpdatedAt = nowUtc;
				await _db.SaveChangesAsync(ct);
				return HospitalResult.Success(form.ID);
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex)) { return HospitalResult.Fail(HospitalClinicalContentService.NoTables); }
		}

		/// Signs a form: every visible value (and every calculated one) becomes one flagged observation row.
		public async Task<HospitalResult> SignAsync(BusinessContext ctx, int formId, bool en, int? employeeId, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await _access.CanAsync(ctx, HospitalActions.Consult, null, ct)) return HospitalResult.Fail("forbidden");
			try
			{
				var form = await Q<HospitalClinicalForm>(ctx).FirstOrDefaultAsync(f => f.ID == formId, ct);
				if (form == null) return HospitalResult.Fail("not_found");
				if (form.Status == HospitalFormStatus.Signed) return HospitalResult.Fail("This form is already signed");
				var fields = await Content.FieldViewsAsync(ctx, form.TemplateVersionId, null, ct);
				// H115: each value is flagged against the range for this patient on this day (sex, age, pregnancy)
				var who = await _db.Set<HospitalPatient>().AsNoTracking().FirstAsync(p => p.ID == form.PatientId, ct);
				var eff = (await new HospitalConceptRangeService(_db, _access).ForPatientAsync(ctx, fields.Select(f => f.Concept).DistinctBy(c => c.ID).ToList(), who, nowUtc, ct)).ToDictionary(c => c.ID);
				fields = fields.Select(f => f with { Concept = eff[f.Concept.ID] }).ToList();
				var answers = HospitalFormRules.Answers(form.AnswersJson);
				var (values, err, args) = HospitalFormRules.Read(fields, answers, en);
				if (err != null) return HospitalResult.Fail(err, args!);
				var pat = await _db.Set<HospitalPatient>().FirstAsync(p => p.ID == form.PatientId, ct);
				// a calculator may read a concept the form does not ask (the creatinine) — from the patient's latest values
				var codes = fields.Where(f => f.Concept.Calculator != null).SelectMany(f => HospitalConceptRules.Inputs(f.Concept.CalcInputs)).Distinct().ToList();
				var outside = await LatestByCodeAsync(ctx, form.PatientId, codes, nowUtc, ct);
				values.AddRange(HospitalFormRules.Compute(fields, values, HospitalClinicalCalc.AgeOn(pat.BirthDate, nowUtc), pat.Sex == HospitalPatientSex.Female, outside));
				// a concept two templates of this visit both ask is recorded once (the form signed first)
				var already = await Q<HospitalClinicalObservation>(ctx).Where(o => o.EncounterId == form.EncounterId).Select(o => new { o.ConceptId, o.Side }).ToListAsync(ct);
				values.RemoveAll(v => already.Any(x => x.ConceptId == v.Concept.ID && x.Side == v.Side));
				foreach (var v in values)
					_db.Add(new HospitalClinicalObservation
					{
						CompanyId = ctx.CompanyId, PatientId = form.PatientId, ConceptId = v.Concept.ID, EncounterId = form.EncounterId, FormId = form.ID, Value = v.Value, Value2 = v.Value2, ValueCode = v.Code,
						Side = v.Side, Unit = v.Concept.Unit, Flag = HospitalConceptRules.Flag(v.Concept, v.Value, v.Value2), ObservedAt = nowUtc, Source = HospitalObsSource.Form, RecordedAt = nowUtc, RecordedByEmployeeId = employeeId,
					});
				form.Status = HospitalFormStatus.Signed; form.SignedAt = nowUtc; form.SignedByEmployeeId = employeeId;
				await _db.SaveChangesAsync(ct);
				return new HospitalResult(true, null, form.ID, new object[] { values.Count });
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex)) { return HospitalResult.Fail(HospitalClinicalContentService.NoTables); }
		}

		/// The newest value of each code: an observation, else a verified lab result of the concept's analyte (one year).
		private async Task<Dictionary<string, decimal>> LatestByCodeAsync(BusinessContext ctx, int patientId, IReadOnlyList<string> codes, DateTime nowUtc, CancellationToken ct)
		{
			var res = new Dictionary<string, decimal>();
			if (codes.Count == 0) return res;
			var cs = await Q<HospitalConcept>(ctx).Where(c => codes.Contains(c.Code)).ToListAsync(ct);
			foreach (var c in cs)
			{
				var o = await Q<HospitalClinicalObservation>(ctx).Where(x => x.PatientId == patientId && x.ConceptId == c.ID && x.Value != null && x.Side == null).OrderByDescending(x => x.ObservedAt).Select(x => new { x.Value, x.ObservedAt }).FirstOrDefaultAsync(ct);
				var lab = c.LabAnalyteCode == null ? null : await LastLabAsync(ctx, patientId, c.LabAnalyteCode, nowUtc.AddDays(-365), ct);
				var pick = o != null && (lab == null || o.ObservedAt >= lab.Value.At) ? o.Value : lab?.Value;
				if (pick is decimal p) res[c.Code] = p;
			}
			return res;
		}

		private async Task<(decimal Value, DateTime At)?> LastLabAsync(BusinessContext ctx, int patientId, string analyteCode, DateTime since, CancellationToken ct)
		{
			var r = await (from res in Q<HospitalLabResult>(ctx)
						   join o in Q<HospitalLabOrder>(ctx) on res.LabOrderId equals o.ID
						   join a in Q<HospitalLabAnalyte>(ctx) on res.AnalyteId equals a.ID
						   where o.PatientId == patientId && a.Code == analyteCode && o.Status == HospitalLabStatus.Verified && o.VerifiedAt >= since && res.Value != null
						   orderby o.VerifiedAt descending
						   select new { res.Value, o.VerifiedAt }).FirstOrDefaultAsync(ct);
			return r == null ? null : (r.Value!.Value, r.VerifiedAt!.Value);
		}

		/// The "last lab" fields of a version, filled from the patient's verified results within each field's look-back.
		public async Task<IReadOnlyList<HospitalPrefill>> PrefillAsync(BusinessContext ctx, int patientId, int versionId, DateTime nowUtc, CancellationToken ct = default)
		{
			var list = new List<HospitalPrefill>();
			try
			{
				foreach (var fv in (await Content.FieldViewsAsync(ctx, versionId, null, ct)).Where(f => f.Field.ValueSource == HospitalValueSource.LastLab && f.Concept.LabAnalyteCode != null))
				{
					var lab = await LastLabAsync(ctx, patientId, fv.Concept.LabAnalyteCode!, nowUtc.AddDays(-(fv.Field.LookbackDays ?? 90)), ct);
					if (lab != null) list.Add(new HospitalPrefill(fv.Field.ID, HospitalConceptRules.Num(lab.Value.Value, fv.Concept.Decimals), lab.Value.At));
				}
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex)) { }
			return list;
		}

		/// A concept's values for a patient, oldest first (each side its own point).
		public async Task<IReadOnlyList<HospitalObsPoint>> TrendAsync(BusinessContext ctx, int patientId, int conceptId, CancellationToken ct = default)
		{
			try
			{
				return await Q<HospitalClinicalObservation>(ctx).Where(o => o.PatientId == patientId && o.ConceptId == conceptId).OrderBy(o => o.ObservedAt).ThenBy(o => o.ID)
					.Select(o => new HospitalObsPoint(o.ObservedAt, o.Value, o.Value2, o.ValueCode, o.Side, o.Flag, o.Unit)).ToListAsync(ct);
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex)) { return Array.Empty<HospitalObsPoint>(); }
		}

		/// Alerts (the latest value crossing a field's alert) and care gaps (no value within a field's days), from the
		/// published versions of the templates this patient has been seen on.
		/// extraTemplates: the templates on the screen — counted even before the patient has a signed form on them, so a
		/// patient nobody has assessed shows "never recorded" gaps instead of looking like a patient with none.
		public async Task<IReadOnlyList<HospitalCareItem>> CareAsync(BusinessContext ctx, int patientId, DateTime nowUtc, CancellationToken ct = default, IReadOnlyCollection<int>? extraTemplates = null)
		{
			var items = new List<HospitalCareItem>();
			try
			{
				var templateIds = await Q<HospitalClinicalForm>(ctx).Where(f => f.PatientId == patientId && f.Status == HospitalFormStatus.Signed)
					.Join(Q<HospitalClinicalTemplateVersion>(ctx), f => f.TemplateVersionId, v => v.ID, (f, v) => v.TemplateId).Distinct().ToListAsync(ct);
				if (extraTemplates != null) templateIds = templateIds.Union(extraTemplates).ToList();
				var versionIds = await Q<HospitalClinicalTemplateVersion>(ctx).Where(v => templateIds.Contains(v.TemplateId) && v.Status == HospitalTemplateStatus.Published).Select(v => v.ID).ToListAsync(ct);
				var fields = await Q<HospitalClinicalTemplateField>(ctx).Where(f => versionIds.Contains(f.VersionId) && (f.CareGapDays != null || f.AlertAbove != null || f.AlertBelow != null || f.AlertRange != null)).ToListAsync(ct);
				HospitalPatientFacts? facts = null;
				if (fields.Any(f => f.AlertRange == HospitalAlertRange.Pregnant))
					facts = await new HospitalConceptRangeService(_db, _access).FactsAsync(ctx, await _db.Set<HospitalPatient>().AsNoTracking().FirstAsync(p => p.ID == patientId, ct), nowUtc, ct);
				var cids = fields.Select(f => f.ConceptId).Distinct().ToList();
				var concepts = await Q<HospitalConcept>(ctx).Where(c => cids.Contains(c.ID)).ToDictionaryAsync(c => c.ID, ct);
				var latest = await Q<HospitalClinicalObservation>(ctx).Where(o => o.PatientId == patientId && cids.Contains(o.ConceptId))
					.GroupBy(o => o.ConceptId).Select(g => g.OrderByDescending(o => o.ObservedAt).ThenByDescending(o => o.ID).First()).ToListAsync(ct);
				foreach (var g in fields.GroupBy(f => f.ConceptId))
				{
					if (!concepts.TryGetValue(g.Key, out var c)) continue;   // a concept no longer there is a missing row, never a crash
					var last = latest.FirstOrDefault(o => o.ConceptId == g.Key);
					int? gap = g.Where(f => f.CareGapDays != null).Select(f => f.CareGapDays).Min();
					if (gap is int days && (last == null || last.ObservedAt < nowUtc.AddDays(-days)))
						items.Add(new HospitalCareItem(c, "Gap", c.Name, c.NameEn, last?.ObservedAt, last == null ? null : Math.Max(1, (int)Math.Ceiling((nowUtc - last.ObservedAt.AddDays(days)).TotalDays))));
					if (last?.Value is decimal v)
						foreach (var f in g.Where(f => f.AlertAbove <= v || f.AlertBelow >= v || HospitalFieldAlertRules.OutsideRange(f, last.Flag, facts)))
							items.Add(new HospitalCareItem(c, "Alert", f.AlertText ?? c.Name, f.AlertTextEn ?? c.NameEn, last.ObservedAt, null));
				}
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex)) { }
			return items.OrderBy(i => i.Kind == "Alert" ? 0 : 1).ThenByDescending(i => i.DaysOverdue ?? int.MaxValue).ToList();
		}
	}
}
