using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Hospital;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL.Hospital
{
	// ==========================================================================================
	// HOSPITAL H95 — the visit's hand-built widgets (the prototype's "eight widgets a form cannot draw"), shown by what the
	// visit holds, never by which clinic it is:
	//   * GROWTH — a child (under 19) whose templates ask weight, height, BMI or head circumference: the WHO chart of
	//     every stored value, z and percentile, and the alert when weight has crossed two percentile lines;
	//   * DOSING — a child: the dose by weight from the drug-safety limits that carry a per-kg dose, capped by the adult
	//     single limit; with no weight recorded it says so (a dose cannot be computed), it never shows nothing;
	//   * DEVELOPMENT — the fields of a template section named "Development", as ticks;
	//   * SCORES — every score concept on the visit (c-ACT, ACT, PHQ-9): the value against its threshold;
	//   * VACCINES — a child's schedule status from the immunization register (H84).
	// ==========================================================================================
	public sealed record HospitalGrowthSeries(string Indicator, HospitalConcept Concept, IReadOnlyList<HospitalGrowthPoint> Points, HospitalGrowthCurves Curves);
	public sealed record HospitalVisitDosing(IReadOnlyList<HospitalDoseLimit> Drugs, decimal? WeightKg, DateTime? WeighedAt);
	public sealed record HospitalVisitMilestone(string Label, string LabelEn, string? Answer, string Key, int FormId);
	/// H97: one item of a summed score (an ACT question) and its answer on the visit.
	public sealed record HospitalScoreItem(string Label, string LabelEn, string? Answer, string? Unit);
	public sealed record HospitalVisitScore(HospitalConcept Concept, HospitalObsPoint? Last, IReadOnlyList<HospitalScoreItem>? Items = null);
	/// H97: the skin prick test — each allergen's wheal / flare (mm), positive when the wheal is 3 mm or more above the
	/// negative (saline) control; the test is valid when saline is 3 mm or less and histamine 3 mm or more above it.
	public sealed record HospitalSptRow(string Label, string LabelEn, decimal? Wheal, decimal? Flare, bool Control, bool? Positive);
	public sealed record HospitalSpt(IReadOnlyList<HospitalSptRow> Rows, bool? Valid);
	public sealed record HospitalVisitGlaucoma(decimal? IopR, decimal? IopL, decimal? CdR, decimal? CdL, bool Suspect, string Side, IReadOnlyList<string> Reasons);
	public sealed record HospitalVisitWidgets(bool Child, double? AgeYears, IReadOnlyList<HospitalGrowthSeries> Growth, (bool Crossed, HospitalGrowthPoint? From, HospitalGrowthPoint? To) Crossing,
		HospitalVisitDosing? Dosing, IReadOnlyList<HospitalVisitMilestone> Development, IReadOnlyList<HospitalVisitScore> Scores, int? VaccinesOverdue, int? VaccinesDue, bool VaccinesReady, HospitalVisitGlaucoma? Glaucoma = null,
		HospitalSpt? Spt = null
		, IReadOnlyList<HospitalImmunoCourseView>? Immuno = null, bool ImmunoReady = false
		, IReadOnlyList<HospitalRenalAlert>? Renal = null
		, HospitalAdrenaline? Adrenaline = null
		, HospitalChadsVasc? Chads = null
		, HospitalEyeOutputs? EyeOutputs = null
		);

	/// H108: the eye visit's outputs — the OCT and visual-field services of the price list (codes OCT, VF), null when missing.
	public sealed record HospitalEyeOutputs(HospitalService? Oct, HospitalService? Vf)
	{
		public static readonly IReadOnlyList<string> Codes = new[] { "OCT", "VF" };
	}

	/// H107: an allergy patient who may need an adrenaline auto-injector — the history that says so, and whether one is prescribed.
	public sealed record HospitalAdrenaline(bool Anaphylaxis, bool Needed, bool Prescribed, DateTime? PrescribedAt);

	public static class HospitalAdrenalineRules
	{
		/// The words that name a previous anaphylaxis in the patient's allergy record (Arabic and English).
		public static readonly IReadOnlyList<string> AnaphylaxisWords = new[] { "صدمة تحسسية", "تأق", "صدمه تحسسيه", "anaphyla" };
		/// The words that name an adrenaline auto-injector in a prescription line.
		public static readonly IReadOnlyList<string> AdrenalineWords = new[] { "adrenaline", "epinephrine", "epipen", "jext", "anapen", "أدرينالين", "ادرينالين", "إبينفرين", "ابينفرين" };

		public static bool Mentions(string? text, IEnumerable<string> words) =>
			!string.IsNullOrWhiteSpace(text) && words.Any(w => text.Contains(w, StringComparison.OrdinalIgnoreCase));

		/// Needed when the record names anaphylaxis, or the visit is an allergy visit (immunotherapy or a skin test).
		public static HospitalAdrenaline? Read(string? allergies, bool allergyVisit, DateTime? prescribedAt)
		{
			bool ana = Mentions(allergies, AnaphylaxisWords), needed = ana || allergyVisit;
			if (!needed && prescribedAt == null) return null;
			return new HospitalAdrenaline(ana, needed, prescribedAt != null, prescribedAt);
		}
	}

	public static class HospitalVisitWidgetRules
	{
		/// A child's dose in mg from the weight: per kg, never above the adult single limit. Capped = the limit decided it.
		public static (decimal? Mg, bool Capped) ChildDose(HospitalDoseLimit d, decimal? kg)
		{
			if (kg is not > 0 || d.PerKgMg is not decimal per) return (null, false);
			var mg = Math.Round(per * kg.Value, 1);
			return d.MaxSingle is decimal max && mg > max ? (max, true) : (mg, false);
		}

		/// How many doses a day the interval allows, within the adult daily limit.
		public static int? DosesPerDay(HospitalDoseLimit d, decimal? mg)
		{
			if (d.EveryHours is not > 0 || mg is not > 0) return null;
			int byClock = 24 / d.EveryHours.Value;
			return d.MaxDaily is decimal day ? Math.Max(0, Math.Min(byClock, (int)Math.Floor(day / mg.Value))) : byClock;
		}

		/// H96: glaucoma suspected — pressure above 21 in either eye, the eyes 4 mmHg apart or more, a cup-to-disc ratio of 0.6 or
		/// more, or cups 0.2 apart or more (the usual referral criteria). The side of concern is the higher pressure, else the
		/// larger cup. Reasons: the keys of what was found.
		public static HospitalVisitGlaucoma Glaucoma(decimal? iopR, decimal? iopL, decimal? cdR, decimal? cdL)
		{
			var why = new List<string>();
			if (iopR > 21 || iopL > 21) why.Add("iop");
			if (iopR != null && iopL != null && Math.Abs(iopR.Value - iopL.Value) >= 4) why.Add("iop-asym");
			if (cdR >= 0.6m || cdL >= 0.6m) why.Add("cup");
			if (cdR != null && cdL != null && Math.Abs(cdR.Value - cdL.Value) >= 0.2m) why.Add("cup-asym");
			string side = iopR != null && iopL != null && iopR != iopL ? (iopR > iopL ? "R" : "L") : (cdR ?? 0) >= (cdL ?? 0) ? "R" : "L";
			return new HospitalVisitGlaucoma(iopR, iopL, cdR, cdL, why.Count > 0, side, why);
		}

		public const string SptHistamine = "SPT-HIST", SptSaline = "SPT-SAL";
		public static bool IsSkinTest(HospitalClinicalTemplateField f) => string.Equals(f.SectionEn?.Trim(), "Skin prick test", StringComparison.OrdinalIgnoreCase);

		/// The skin test read from the answers (code → "wheal/flare").
		public static HospitalSpt SkinTest(IReadOnlyList<(string Code, string Label, string LabelEn, string? Answer)> rows)
		{
			(decimal? W, decimal? F) Read(string? a)
			{
				var p = (a ?? "").Split('/', StringSplitOptions.TrimEntries);
				decimal? D(string x) => decimal.TryParse(x, System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : null;
				return (p.Length > 0 ? D(p[0]) : null, p.Length > 1 ? D(p[1]) : null);
			}
			var sal = rows.FirstOrDefault(r => r.Code == SptSaline); var his = rows.FirstOrDefault(r => r.Code == SptHistamine);
			decimal? sw = sal.Code == null ? null : Read(sal.Answer).W, hw = his.Code == null ? null : Read(his.Answer).W;
			var list = rows.Select(r =>
			{
				var (w, f) = Read(r.Answer); bool ctl = r.Code is SptSaline or SptHistamine;
				bool? pos = ctl || w == null || sw == null ? null : w - sw >= 3;
				return new HospitalSptRow(r.Label, r.LabelEn, w, f, ctl, pos);
			}).OrderByDescending(r => r.Control).ToList();
			bool? valid = sw == null || hw == null ? null : sw <= 3 && hw - sw >= 3;
			return new HospitalSpt(list, valid);
		}

		public static bool IsDevelopment(HospitalClinicalTemplateField f) => string.Equals(f.SectionEn?.Trim(), "Development", StringComparison.OrdinalIgnoreCase);
	}

	public sealed class HospitalVisitWidgetsService
	{
		private readonly CrossDbContext _db;
		private readonly IHospitalAccessService _access;
		public HospitalVisitWidgetsService(CrossDbContext db, IHospitalAccessService access) { _db = db; _access = access; }
		private IQueryable<T> Q<T>(BusinessContext ctx) where T : class => _db.Set<T>().Where(x => EF.Property<int>(x, "CompanyId") == ctx.CompanyId);

		public async Task<HospitalVisitWidgets> BuildAsync(BusinessContext ctx, HospitalVisitModel m, DateTime nowUtc, CancellationToken ct = default)
		{
			var p = m.Patient;
			double? years = p.BirthDate is DateTime b ? HospitalGrowth.AgeDays(b, nowUtc) / 365.25 : null;
			bool child = years is < 19;
			var fields = m.Forms.SelectMany(f => f.Fields.Select(x => (Form: f, View: x))).ToList();

			// growth: the indicators the visit's templates ask, from every stored value of the child
			var growth = new List<HospitalGrowthSeries>();
			if (child && p.BirthDate is DateTime birth && HospitalPatientSex.IsKnown(p.Sex))
			{
				int now = HospitalGrowth.AgeDays(birth, nowUtc);
				foreach (var ind in HospitalGrowthIndicator.All)
				{
					var concept = fields.Select(x => x.View.Concept).FirstOrDefault(c => c.Code == HospitalGrowthIndicator.Concept[ind]);
					if (concept == null) continue;
					// WHO publishes no reference for this measure at this age (head circumference after five, weight after ten): no chart
					if (HospitalGrowth.Lms(ind, p.Sex, now) == null) continue;
					var obs = await Q<HospitalClinicalObservation>(ctx).Where(o => o.PatientId == p.ID && o.ConceptId == concept.ID && o.Side == null && o.Value != null).OrderBy(o => o.ObservedAt).ToListAsync(ct);
					var pts = obs.Select(o => { int a = HospitalGrowth.AgeDays(birth, o.ObservedAt); var z = HospitalGrowth.Z(ind, p.Sex, a, (double)o.Value!.Value);
						return new HospitalGrowthPoint(o.ObservedAt, a, o.Value!.Value, z, z is double zz ? HospitalGrowth.Percentile(zz) : null); }).ToList();
					int from = pts.Count == 0 ? Math.Max(0, now - 730) : Math.Max(0, Math.Min(pts.Min(x => x.AgeDays), now) - 180);
					int to = Math.Max(now, pts.Count == 0 ? now : pts.Max(x => x.AgeDays)) + 365;
					growth.Add(new HospitalGrowthSeries(ind, concept, pts, HospitalGrowth.Curves(ind, p.Sex, from, to)));
				}
			}
			var wfa = growth.FirstOrDefault(g => g.Indicator == HospitalGrowthIndicator.Weight);
			var crossing = wfa == null ? (false, (HospitalGrowthPoint?)null, (HospitalGrowthPoint?)null) : HospitalGrowth.Crossing(wfa.Points);

			// dosing: the per-kg limits, and the newest weight
			HospitalVisitDosing? dosing = null;
			if (child)
			{
				List<HospitalDoseLimit> drugs;
				try { drugs = await Q<HospitalDoseLimit>(ctx).AsNoTracking().Where(d => d.IsActive && d.PerKgMg != null).OrderBy(d => d.Drug).ToListAsync(ct); }
				catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number is 207 or 208) { drugs = new(); }
				var wc = await Q<HospitalConcept>(ctx).Where(c => c.Code == HospitalGrowthIndicator.Concept[HospitalGrowthIndicator.Weight]).Select(c => (int?)c.ID).FirstOrDefaultAsync(ct);
				var w = wc is int wid ? await Q<HospitalClinicalObservation>(ctx).Where(o => o.PatientId == p.ID && o.ConceptId == wid && o.Value != null).OrderByDescending(o => o.ObservedAt).FirstOrDefaultAsync(ct) : null;
				dosing = new HospitalVisitDosing(drugs, w?.Value, w?.ObservedAt);
			}

			// development ticks and score cards
			var dev = fields.Where(x => HospitalVisitWidgetRules.IsDevelopment(x.View.Field)).Select(x =>
			{
				var key = HospitalFormRules.Key(x.View.Field.ID, null);
				x.Form.Answers.TryGetValue(key, out var a);
				return new HospitalVisitMilestone(x.View.Field.Label ?? x.View.Concept.Name, x.View.Field.LabelEn ?? x.View.Concept.NameEn, a, key, x.Form.Form.ID);
			}).ToList();
			// the answers of the visit by concept code (the first form that asks it)
			var byCode = new Dictionary<string, (string Label, string LabelEn, string? Answer, string? Unit)>();
			foreach (var x in fields)
			{
				if (byCode.ContainsKey(x.View.Concept.Code)) continue;
				x.Form.Answers.TryGetValue(HospitalFormRules.Key(x.View.Field.ID, null), out var ans);
				byCode[x.View.Concept.Code] = (x.View.Field.Label ?? x.View.Concept.Name, x.View.Field.LabelEn ?? x.View.Concept.NameEn, ans, x.View.Concept.Unit);
			}
			var scores = fields.Select(x => x.View.Concept).Where(c => c.ValueType == HospitalConceptType.Score).GroupBy(c => c.ID).Select(g => g.First())
				.Select(c => new HospitalVisitScore(c, m.LastByConcept.TryGetValue(c.ID, out var l) ? l : null,
					c.Calculator == HospitalConceptCalculator.Sum ? HospitalConceptRules.Inputs(c.CalcInputs).Where(byCode.ContainsKey).Select(code => new HospitalScoreItem(byCode[code].Label, byCode[code].LabelEn, byCode[code].Answer, byCode[code].Unit)).ToList() : null)).ToList();
			// the skin prick test, read from its section
			var sptRows = fields.Where(x => HospitalVisitWidgetRules.IsSkinTest(x.View.Field)).Select(x => (x.View.Concept.Code, byCode[x.View.Concept.Code].Label, byCode[x.View.Concept.Code].LabelEn, byCode[x.View.Concept.Code].Answer)).ToList();
			var spt = sptRows.Count == 0 ? null : HospitalVisitWidgetRules.SkinTest(sptRows);

			// the child's vaccines (H84)
			int? overdue = null, due = null; bool vReady = false;
			if (child)
			{
				var card = await new HospitalImmunizationService(_db, _access).CardAsync(ctx, p.ID, nowUtc, ct);
				if (card is { Ready: true, HasSchedule: true })
				{ vReady = true; overdue = card.Doses.Count(d => d.Status == "Overdue"); due = card.Doses.Count(d => d.Status == "Due"); }
			}
			// the eyes: the latest pressure and cup of each side, when the visit asks for them
			HospitalVisitGlaucoma? glaucoma = null;
			var iopC = fields.Select(x => x.View.Concept).FirstOrDefault(c => c.Code == "79892-6");
			if (iopC != null)
			{
				var cdC = fields.Select(x => x.View.Concept).FirstOrDefault(c => c.Code == "CDR");
				var ids = new[] { iopC.ID, cdC?.ID ?? -1 };
				var latest = (await Q<HospitalClinicalObservation>(ctx).Where(o => o.PatientId == p.ID && ids.Contains(o.ConceptId) && o.Side != null && o.Value != null).ToListAsync(ct))
					.GroupBy(o => (o.ConceptId, o.Side)).ToDictionary(g => g.Key, g => g.OrderByDescending(o => o.ObservedAt).ThenByDescending(o => o.ID).First().Value);
				decimal? V(int c, string side) => latest.TryGetValue((c, side), out var v) ? v : null;
				if (latest.Count > 0) glaucoma = HospitalVisitWidgetRules.Glaucoma(V(iopC.ID, "R"), V(iopC.ID, "L"), cdC == null ? null : V(cdC.ID, "R"), cdC == null ? null : V(cdC.ID, "L"));
			}
			// immunotherapy: the patient's courses, when the visit holds a skin test or the patient already has a course
			var (immuno, iReady) = await new HospitalImmunotherapyService(_db, _access).PatientAsync(ctx, p.ID, ct);
			bool showImmuno = spt != null || immuno.Count > 0;
			// H104: the kidney — the medicines of the last 180 days against the last stored eGFR
			var renal = await HospitalRenalRules.VisitAlertsAsync(_db, ctx.CompanyId, p.ID, nowUtc, ct);
			// H107: the adrenaline auto-injector — prescribed to this patient in the last year (by either name of the medicine)
			var since = nowUtc.AddDays(-365);
			var rx = await (from l in _db.Set<HospitalPrescriptionLine>().AsNoTracking()
							join e in Q<HospitalEncounter>(ctx) on l.EncounterId equals e.ID
							where e.PatientId == p.ID && e.StartedAt >= since
							select new { l.Drug, l.DrugEn, e.StartedAt }).ToListAsync(ct);
			var epi = rx.Where(x => HospitalAdrenalineRules.Mentions(x.Drug + " " + x.DrugEn, HospitalAdrenalineRules.AdrenalineWords)).OrderByDescending(x => x.StartedAt).FirstOrDefault();
			var adrenaline = HospitalAdrenalineRules.Read(p.Allergies, spt != null || showImmuno, epi?.StartedAt);
			// H108: CHA2DS2-VASc — read from age, sex and the coded conditions; shown on the internal-medicine visit of an
			// adult, and on any visit of a patient with atrial fibrillation
			var chads = child ? null : await HospitalChadsRules.ForPatientAsync(_db, ctx.CompanyId, p, nowUtc, ct);
			if (chads != null && !chads.Af && m.Clinic?.Code != "IM") chads = null;
			// H108: the eye visit's outputs (the price list's OCT and VF services)
			HospitalEyeOutputs? eye = null;
			if (m.Clinic?.Code == "OPH")
			{
				// H129: the services the eye clinic chose in its settings; a service listed under the old code still counts
				async Task<HospitalService?> Chosen(string unit, string code)
				{
					if ((await HospitalUnitSettingsService.RowAsync(_db, ctx.CompanyId, unit, ct))?.SessionServiceId is int sid)
						return await Q<HospitalService>(ctx).AsNoTracking().FirstOrDefaultAsync(s => s.ID == sid && s.IsActive, ct);
					return await Q<HospitalService>(ctx).AsNoTracking().FirstOrDefaultAsync(s => s.IsActive && s.Code == code, ct);
				}
				eye = new HospitalEyeOutputs(await Chosen(HospitalKitUnits.EyeOct, "OCT"), await Chosen(HospitalKitUnits.EyeVf, "VF"));
			}
			return new HospitalVisitWidgets(child, years, growth, crossing, dosing, dev, scores, overdue, due, vReady, glaucoma, spt, showImmuno ? immuno : null, iReady, renal, adrenaline, chads, eye);
		}
	}
}
