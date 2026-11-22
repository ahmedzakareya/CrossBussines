using System.Globalization;
using System.Text.RegularExpressions;
using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Hospital;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL.Hospital
{
	// ==========================================================================================
	// HOSPITAL H73 — drug interactions and dose limits. The pure rules match a medicine's name against the knowledge
	// base's words, read a written dose ("1 g", "500mg", "٥٠٠ مجم" is not read — the order screen writes Latin units),
	// and compare it to the limit in the limit's own unit. The service checks an order against the patient's medicines
	// still running, records the alerts a doctor went past, and lets the pharmacy keep the knowledge base.
	// ==========================================================================================

	/// An alert: its kind, how severe, whether it stops the order, and what to say — as a resource key with values
	/// (the screen says it in the reader's language); an interaction carries its effect in both languages.
	public sealed record HospitalDrugAlert(string Kind, string Severity, bool Blocks, string Key, IReadOnlyList<string> Args, string? EffectAr, string? EffectEn, string Detail);

	public sealed class HospitalInteractionInput
	{
		public int? Id { get; set; }
		public string? DrugA { get; set; }
		public string? DrugB { get; set; }
		public string? Severity { get; set; }
		public string? EffectAr { get; set; }
		public string? EffectEn { get; set; }
		public bool IsActive { get; set; } = true;
	}

	public sealed class HospitalDoseLimitInput
	{
		public int? Id { get; set; }
		public string? Drug { get; set; }
		public string? Route { get; set; }
		public string? Unit { get; set; }
		public decimal? MaxSingle { get; set; }
		public decimal? MaxDaily { get; set; }
		public string? Note { get; set; }
		public bool IsActive { get; set; } = true;
		public decimal? PerKgMg { get; set; }
		public int? EveryHours { get; set; }
		public decimal? StrengthMgPerMl { get; set; }
		public decimal? RenalReviewBelow { get; set; }
		public decimal? RenalStopBelow { get; set; }
	}

	public sealed record HospitalOverrideRow(HospitalAlertOverride Row, string Patient, string PatientEn, string Mrn, string? By);
	/// H236: what the alerts gone past say about the alerts — by kind, the most overridden, and who overrides most.
	public sealed record HospitalOverrideStats(int Days, int Total, IReadOnlyList<(string Kind, int Count)> ByKind, IReadOnlyList<(string Detail, int Count)> TopAlerts,
		IReadOnlyList<(HospitalFace Who, int Count)> TopPrescribers, IReadOnlyList<(string Month, int Count)> ByMonth);

	public static class HospitalDrugSafetyRules
	{
		/// A knowledge-base word: trimmed, lower-case, single spaces.
		public static string Key(string? s) => Regex.Replace((s ?? "").Trim().ToLowerInvariant(), @"\s+", " ");

		/// The pair in the one order it is stored in.
		public static (string A, string B) Pair(string a, string b)
		{
			var (x, y) = (Key(a), Key(b));
			return string.CompareOrdinal(x, y) <= 0 ? (x, y) : (y, x);
		}

		/// A medicine's name mentions a knowledge-base word — as a WHOLE WORD, not anywhere inside one.
		///
		/// This was a bare substring test with a three-letter floor, and the floor is not enough. A
		/// knowledge-base entry written as "met" matches Metoprolol, Metformin AND Methotrexate; one
		/// written as "ace" matches Paracetamol. A pharmacist adding a short generic name would have
		/// armed an alert on every medicine that happens to contain those letters, and alerts that
		/// fire on the wrong drug are how prescribers learn to dismiss them without reading.
		///
		/// Measured before changing it: on this database the two rules agree exactly — all 8 real
		/// matches are whole words and substring finds no extra. So this costs nothing today and
		/// closes the trap for whoever extends the table next.
		///
		/// It does NOT fix the opposite miss: a brand name still matches no generic entry, silently.
		/// That one cannot be fixed by matching — it needs the brand recorded — and it is why
		/// CheckOrderAsync now says out loud which medicines the table has never heard of.
		public static bool Mentions(string drugName, string word)
		{
			if (word == null || word.Length < 3) return false;
			var name = drugName ?? "";
			var from = 0;
			while (true)
			{
				var at = name.IndexOf(word, from, StringComparison.OrdinalIgnoreCase);
				if (at < 0) return false;
				// A letter or digit on either side means this is the middle of a longer name.
				var beforeOk = at == 0 || !char.IsLetterOrDigit(name[at - 1]);
				var afterAt = at + word.Length;
				var afterOk = afterAt >= name.Length || !char.IsLetterOrDigit(name[afterAt]);
				if (beforeOk && afterOk) return true;
				from = at + 1;
			}
		}

		/// WHETHER THE TABLE HAS HEARD OF THIS MEDICINE AT ALL — in either check.
		///
		/// The one question the service could not answer before, and the one a prescriber needs most:
		/// an empty alert list meant "nothing found" and "nothing could be looked for" at the same
		/// time, and those are opposite claims.
		public static bool KnownToPairs(string drug, IEnumerable<HospitalDrugInteraction> kb) =>
			kb.Any(k => k.IsActive && (Mentions(drug, k.DrugA) || Mentions(drug, k.DrugB)));

		public static bool KnownToLimits(string drug, string route, IEnumerable<HospitalDoseLimit> limits) =>
			limits.Any(l => l.IsActive && Mentions(drug, l.Drug) && (l.Route == null || l.Route == route));

		/// The interactions of a medicine with each of the others: (the other medicine, the interaction).
		public static IReadOnlyList<(string Other, HospitalDrugInteraction Hit)> Interactions(string drug, IEnumerable<string> others, IEnumerable<HospitalDrugInteraction> kb)
		{
			var list = new List<(string, HospitalDrugInteraction)>();
			var rules = kb.Where(k => k.IsActive).ToList();
			foreach (var o in others.Where(o => !string.IsNullOrWhiteSpace(o)).Distinct(StringComparer.OrdinalIgnoreCase))
				foreach (var r in rules)
					if ((Mentions(drug, r.DrugA) && Mentions(o, r.DrugB)) || (Mentions(drug, r.DrugB) && Mentions(o, r.DrugA)))
						list.Add((o, r));
			return list.OrderBy(x => Array.IndexOf(HospitalInteractionSeverity.All.ToArray(), x.Item2.Severity)).ToList();
		}

		private static readonly Regex DoseText = new(@"^\s*(\d+(?:[.,]\d+)?)\s*(mg|mcg|µg|ug|g|gm|gram|grams|units?|iu|ml)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

		/// A written dose as an amount and a unit ("1 g" → 1 g; "500mg" → 500 mg; "10 units"); null when it cannot be read.
		public static (decimal Amount, string Unit)? ParseDose(string? dose)
		{
			var m = DoseText.Match(dose ?? "");
			if (!m.Success) return null;
			var amount = decimal.Parse(m.Groups[1].Value.Replace(',', '.'), CultureInfo.InvariantCulture);
			var u = m.Groups[2].Value.ToLowerInvariant() switch
			{
				"mcg" or "µg" or "ug" => HospitalDoseUnit.Mcg,
				"g" or "gm" or "gram" or "grams" => HospitalDoseUnit.G,
				"unit" or "units" or "iu" => HospitalDoseUnit.Units,
				"ml" => HospitalDoseUnit.Ml,
				_ => HospitalDoseUnit.Mg,
			};
			return (amount, u);
		}

		/// An amount in another unit: grams, milligrams and micrograms convert among themselves; units and ml only to themselves.
		public static decimal? Convert(decimal amount, string from, string to)
		{
			if (from == to) return amount;
			static decimal? ToMg(decimal a, string u) => u switch { HospitalDoseUnit.G => a * 1000m, HospitalDoseUnit.Mg => a, HospitalDoseUnit.Mcg => a / 1000m, _ => null };
			var mg = ToMg(amount, from);
			if (mg == null) return null;
			return to switch { HospitalDoseUnit.G => mg / 1000m, HospitalDoseUnit.Mg => mg, HospitalDoseUnit.Mcg => mg * 1000m, _ => null };
		}

		/// The limits a dose goes over: its single dose, and its day (single × the doses a day; an as-needed order has no day).
		public static IReadOnlyList<(HospitalDoseLimit Limit, bool Daily, decimal Value)> DoseHits(string drug, string? dose, string route, int? dosesPerDay, IEnumerable<HospitalDoseLimit> limits)
		{
			var hits = new List<(HospitalDoseLimit, bool, decimal)>();
			if (ParseDose(dose) is not var (amount, unit)) return hits;
			foreach (var l in limits.Where(l => l.IsActive && Mentions(drug, l.Drug) && (l.Route == null || l.Route == route)))
			{
				if (Convert(amount, unit, l.Unit) is not decimal single) continue;
				if (l.MaxSingle is decimal ms && single > ms) hits.Add((l, false, single));
				if (l.MaxDaily is decimal md && dosesPerDay is int n && single * n > md) hits.Add((l, true, single * n));
			}
			return hits;
		}

		public static string? InteractionRefusal(HospitalInteractionInput i)
		{
			var (a, b) = (Key(i.DrugA), Key(i.DrugB));
			if (a.Length < 3 || b.Length < 3) return "Write both medicines (three letters at least)";
			if (a == b) return "Write two different medicines";
			if (a.Length > 80 || b.Length > 80) return "The medicine's name is too long";
			if (!HospitalInteractionSeverity.All.Contains(i.Severity ?? "")) return "Choose the severity";
			if (string.IsNullOrWhiteSpace(i.EffectAr) || string.IsNullOrWhiteSpace(i.EffectEn)) return "Write the effect in Arabic and in English";
			return null;
		}

		public static string? LimitRefusal(HospitalDoseLimitInput i)
		{
			if (Key(i.Drug).Length < 3) return "Write the medicine (three letters at least)";
			if (!HospitalDoseUnit.All.Contains(i.Unit ?? "")) return "Choose the unit";
			if (!string.IsNullOrEmpty(i.Route) && !HospitalMedRoute.All.Contains(i.Route)) return "Choose the route";
			// H104: a rule by the kidney alone (metformin) needs no dose limit
			if (i.RenalReviewBelow != null || i.RenalStopBelow != null)
			{
				if (i.RenalReviewBelow is not (null or (>= 5 and <= 120)) || i.RenalStopBelow is not (null or (>= 5 and <= 120))) return "An eGFR threshold is between 5 and 120";
				if (i.RenalReviewBelow is decimal rv && i.RenalStopBelow is decimal st && st >= rv) return "Stopping comes below the review threshold";
			}
			else
			if (i.MaxSingle is null && i.MaxDaily is null) return "Write a single or a daily limit";
			if (i.MaxSingle is <= 0 || i.MaxDaily is <= 0) return "A limit is above zero";
			if (i.MaxSingle is decimal s && i.MaxDaily is decimal d && d < s) return "The daily limit cannot be below the single one";
			// H95: a child's dose is per kg and every so many hours, in mg, and never above the adult single limit
			if (i.PerKgMg != null || i.EveryHours != null)
			{
				if (i.Unit != HospitalDoseUnit.Mg) return "A dose per kg is written in mg";
				if (i.PerKgMg is not (> 0 and <= 1000) || i.EveryHours is not (>= 1 and <= 72)) return "Write the mg per kg and every how many hours";
				if (i.MaxSingle is null) return "A dose per kg needs the adult single limit as its cap";
			}
			if (i.StrengthMgPerMl is <= 0) return "The strength is above zero";
			return null;
		}

		public static string Num(decimal d) => d.ToString("0.###", CultureInfo.InvariantCulture);
	}

	public sealed class HospitalDrugSafetyService
	{
		public const string NoTables = "Drug safety is not installed yet (hospital_h73_drug_safety.sql).";
		private readonly CrossDbContext _db;
		private readonly IHospitalAccessService _access;
		public HospitalDrugSafetyService(CrossDbContext db, IHospitalAccessService access) { _db = db; _access = access; }
		private static bool Missing(Microsoft.Data.SqlClient.SqlException ex) => ex.Number is 207 or 208;

		/// The active knowledge base — empty when not installed.
		public static async Task<(List<HospitalDrugInteraction> Pairs, List<HospitalDoseLimit> Limits)> KbAsync(CrossDbContext db, int companyId, CancellationToken ct)
		{
			try
			{
				return (await db.Set<HospitalDrugInteraction>().AsNoTracking().Where(k => k.CompanyId == companyId && k.IsActive).ToListAsync(ct),
						await db.Set<HospitalDoseLimit>().AsNoTracking().Where(k => k.CompanyId == companyId && k.IsActive).ToListAsync(ct));
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex)) { return (new(), new()); }
		}

		/// What a new hospital order meets: interactions with the admission's medicines still running, and its dose limits.
		/// `alsoRunning`: medicines about to start with this one (the earlier lines of an order set, H74).
		public async Task<IReadOnlyList<HospitalDrugAlert>> CheckOrderAsync(BusinessContext ctx, int admissionId, string drug, string? dose, string route, string? hours, bool asNeeded, CancellationToken ct = default, IEnumerable<string>? alsoRunning = null)
		{
			var (pairs, limits) = await KbAsync(_db, ctx.CompanyId, ct);
			var running = await _db.Set<HospitalMedOrder>().AsNoTracking().Where(o => o.CompanyId == ctx.CompanyId && o.AdmissionId == admissionId && o.Status == HospitalMedOrderStatus.Active)
				.Select(o => o.Drug).ToListAsync(ct);
			if (alsoRunning != null) running.AddRange(alsoRunning);
			var alerts = new List<HospitalDrugAlert>();
			foreach (var (other, hit) in HospitalDrugSafetyRules.Interactions(drug, running, pairs))
				alerts.Add(new(HospitalAlertKind.Interaction, hit.Severity, HospitalInteractionSeverity.Blocks(hit.Severity), "{0} with {1}", new[] { drug, other }, hit.EffectAr, hit.EffectEn,
					$"{hit.DrugA} + {hit.DrugB} — {hit.Severity}"));
			int? perDay = asNeeded ? null : HospitalNursingRules.ParseHours(hours)?.Count;
			foreach (var (limit, daily, value) in HospitalDrugSafetyRules.DoseHits(drug, dose, route, perDay, limits))
			{
				var max = daily ? limit.MaxDaily!.Value : limit.MaxSingle!.Value;
				alerts.Add(new(HospitalAlertKind.Dose, HospitalInteractionSeverity.Major, true, daily ? "{0}: {1} {2} a day is above the daily limit of {3} {2}" : "{0}: {1} {2} is above the single-dose limit of {3} {2}",
					new[] { drug, HospitalDrugSafetyRules.Num(value), limit.Unit, HospitalDrugSafetyRules.Num(max) }, null, null,
					$"{limit.Drug} {(daily ? "daily" : "single")} {HospitalDrugSafetyRules.Num(value)} > {HospitalDrugSafetyRules.Num(max)} {limit.Unit}"));
			}
			// H104: the kidney — the admission's patient's last stored eGFR against the medicine's thresholds
			if (HospitalRenalRules.Rules(drug, route, limits).Any())
			{
				var patientId = await _db.Set<HospitalAdmission>().AsNoTracking().Where(a => a.ID == admissionId && a.CompanyId == ctx.CompanyId).Select(a => a.PatientId).FirstOrDefaultAsync(ct);
				var egfr = await HospitalRenalRules.EgfrAsync(_db, ctx.CompanyId, patientId, DateTime.UtcNow, ct);
				if (egfr == null)
					alerts.Add(new(HospitalAlertKind.NotChecked, HospitalInteractionSeverity.Unknown, false,
						"No eGFR in the last year, so the kidney dose of {0} was not checked", new[] { drug }, null, null, "renal-no-egfr"));
				else foreach (var (limit, stop, approaching) in HospitalRenalRules.Hits(drug, route, egfr, limits))
					if (stop)
						alerts.Add(new(HospitalAlertKind.Dose, HospitalInteractionSeverity.Major, true, "{0} with eGFR {1}: stop it below {2}",
							new[] { drug, HospitalDrugSafetyRules.Num(egfr.Value), HospitalDrugSafetyRules.Num(limit.RenalStopBelow!.Value) }, null, null, $"{limit.Drug} renal stop {egfr.Value} < {limit.RenalStopBelow}"));
					else if (!approaching)
						alerts.Add(new(HospitalAlertKind.Dose, HospitalInteractionSeverity.Moderate, false, "{0} with eGFR {1}: review the dose below {2}",
							new[] { drug, HospitalDrugSafetyRules.Num(egfr.Value), HospitalDrugSafetyRules.Num(limit.RenalReviewBelow!.Value) }, null, null, $"{limit.Drug} renal review {egfr.Value} < {limit.RenalReviewBelow}"));
			}
			// ---- H234: duplicate therapy, pregnancy / breastfeeding, the dose for the patient's weight ----
			await MoreChecksAsync(ctx, admissionId, drug, dose, route, running, alerts, ct);

			// ---- WHAT THIS CHECK COULD NOT CHECK --------------------------------------------------
			//
			// SILENCE USED TO MEAN TWO OPPOSITE THINGS. An empty list came back both when the pair
			// was checked and found safe and when the table had never heard of either medicine, and
			// the prescribing screen rendered the two identically: the order simply went through.
			//
			// This is not a hypothetical. Counted on 2026-11-11 against this database: 12 distinct
			// medicines have been ordered, the table holds 8 interaction pairs and 4 dose limits,
			// and of the 66 pairs those medicines could form it can speak about TWO. Sixty-four
			// pairs — 97% — produced confident silence. Four of the twelve medicines (amoxicillin,
			// atorvastatin, ceftriaxone, insulin glargine) are invisible to both checks, and ten of
			// the twelve have no dose limit at all.
			//
			// The programme chose an internal table on purpose, because a licensed knowledge base
			// counts as an integration and those are deferred. That decision is sound; presenting
			// its coverage as completeness is not. So the gap is now stated rather than closed.
			//
			// NEVER BLOCKING, AND ONE LINE. A notice on every order that stopped the order, or that
			// listed all 64 unknown pairs, would be read once and ignored afterwards — and alert
			// fatigue would cost more than the silence it replaced.
			if (pairs.Count == 0 && limits.Count == 0)
			{
				alerts.Add(new(HospitalAlertKind.NotChecked, HospitalInteractionSeverity.Unknown, false,
					"No drug safety table is installed, so nothing about {0} was checked",
					new[] { drug }, null, null, "kb-empty"));
				return alerts;
			}

			var unknownWith = running
				.Where(o => !string.IsNullOrWhiteSpace(o))
				.Distinct(StringComparer.OrdinalIgnoreCase)
				.Where(o => !HospitalDrugSafetyRules.KnownToPairs(o, pairs))
				.ToList();

			if (!HospitalDrugSafetyRules.KnownToPairs(drug, pairs))
				alerts.Add(new(HospitalAlertKind.NotChecked, HospitalInteractionSeverity.Unknown, false,
					"{0} is not in the interaction table, so no interaction was checked for it",
					new[] { drug }, null, null, "pairs-unknown-drug"));
			else if (unknownWith.Count > 0)
				alerts.Add(new(HospitalAlertKind.NotChecked, HospitalInteractionSeverity.Unknown, false,
					"Not in the interaction table, so these were not checked against {0}: {1}",
					new[] { drug, string.Join(", ", unknownWith) }, null, null, "pairs-unknown-others"));

			// THE DOSE IS A SEPARATE SILENCE WITH TWO CAUSES, and the prescriber can act on each:
			// a dose nobody wrote in a readable form, and a medicine with no limit recorded.
			if (!string.IsNullOrWhiteSpace(dose) && HospitalDrugSafetyRules.ParseDose(dose) == null)
				alerts.Add(new(HospitalAlertKind.NotChecked, HospitalInteractionSeverity.Unknown, false,
					"The dose {0} could not be read as an amount, so no dose limit was checked",
					new[] { dose! }, null, null, "dose-unreadable"));
			else if (!HospitalDrugSafetyRules.KnownToLimits(drug, route, limits))
				alerts.Add(new(HospitalAlertKind.NotChecked, HospitalInteractionSeverity.Unknown, false,
					"No dose limit is recorded for {0} by {1}, so the dose was not checked",
					new[] { drug, route }, null, null, "limits-unknown-drug"));

			return alerts;
		}

		/// H234: the checks the drug master and the patient's record make possible. A drug master that is not installed
		/// yet simply adds nothing here (the unchecked notices below still speak).
		private async Task MoreChecksAsync(BusinessContext ctx, int admissionId, string drug, string? dose, string route, IReadOnlyList<string> running, List<HospitalDrugAlert> alerts, CancellationToken ct)
		{
			List<HospitalDrugFacts> facts;
			try
			{
				facts = (await _db.Set<HospitalDrug>().AsNoTracking().Where(d => d.CompanyId == ctx.CompanyId)
					.Select(d => new { d.GenericName, d.GenericNameEn, d.Atc, d.PregnancyRisk, d.LactationRisk, d.IsAntimicrobial, d.FormularyStatus }).ToListAsync(ct))
					.Select(d => new HospitalDrugFacts(d.GenericName, d.GenericNameEn, d.Atc, d.PregnancyRisk, d.LactationRisk, d.IsAntimicrobial, d.FormularyStatus)).ToList();
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number is 207 or 208) { return; }

			foreach (var other in HospitalMedSafetyRules.Duplicates(drug, running, facts))
				alerts.Add(new(HospitalAlertKind.Duplicate, HospitalInteractionSeverity.Major, true, "{0} duplicates {1}: the same therapeutic group", new[] { drug, other }, null, null, $"duplicate {drug} / {other}"));

			var me = HospitalMedSafetyRules.Find(drug, facts);
			var patientId = await _db.Set<HospitalAdmission>().AsNoTracking().Where(a => a.ID == admissionId && a.CompanyId == ctx.CompanyId).Select(a => a.PatientId).FirstOrDefaultAsync(ct);
			if (me != null && (me.PregnancyRisk != null || me.LactationRisk != null) && patientId > 0)
			{
				bool pregnant = false; DateTime? lastBirth = null;
				try { pregnant = await _db.Set<HospitalPregnancy>().AsNoTracking().AnyAsync(p => p.CompanyId == ctx.CompanyId && p.PatientId == patientId && p.Status == HospitalPregnancyStatus.Active, ct); }
				catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number is 207 or 208) { }
				try { lastBirth = await _db.Set<HospitalBirth>().AsNoTracking().Where(b => b.CompanyId == ctx.CompanyId && b.MotherPatientId == patientId).MaxAsync(b => (DateTime?)b.BornAt, ct); }
				catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number is 207 or 208) { }
				if (pregnant && me.PregnancyRisk != null)
					alerts.Add(new(HospitalAlertKind.Pregnancy, me.PregnancyRisk == HospitalDrugRisk.Avoid ? HospitalInteractionSeverity.Major : HospitalInteractionSeverity.Moderate, me.PregnancyRisk == HospitalDrugRisk.Avoid,
						me.PregnancyRisk == HospitalDrugRisk.Avoid ? "{0} is to be avoided in pregnancy, and the patient is pregnant" : "{0} needs caution in pregnancy, and the patient is pregnant", new[] { drug }, null, null, $"pregnancy {me.PregnancyRisk} {drug}"));
				else if (HospitalMedSafetyRules.Lactating(lastBirth, DateTime.UtcNow) && me.LactationRisk != null)
					alerts.Add(new(HospitalAlertKind.Pregnancy, me.LactationRisk == HospitalDrugRisk.Avoid ? HospitalInteractionSeverity.Major : HospitalInteractionSeverity.Moderate, me.LactationRisk == HospitalDrugRisk.Avoid,
						me.LactationRisk == HospitalDrugRisk.Avoid ? "{0} is to be avoided in breastfeeding, and the patient gave birth in the last six months" : "{0} needs caution in breastfeeding, and the patient gave birth in the last six months", new[] { drug }, null, null, $"lactation {me.LactationRisk} {drug}"));
			}

			// the dose for the weight, every age: a per-kg limit for this medicine and route
			var limits = (await KbAsync(_db, ctx.CompanyId, ct)).Limits.Where(l => l.PerKgMg != null && HospitalDrugSafetyRules.Mentions(drug, l.Drug) && (l.Route == null || string.Equals(l.Route, route, StringComparison.OrdinalIgnoreCase))).ToList();
			if (limits.Count == 0 || patientId <= 0) return;
			var parsed = HospitalDrugSafetyRules.ParseDose(dose);
			decimal? mg = parsed is { } p0 ? HospitalDrugSafetyRules.Convert(p0.Amount, p0.Unit, HospitalDoseUnit.Mg) : null;
			decimal? kg = null;
			try
			{
				var wc = await _db.Set<HospitalConcept>().AsNoTracking().Where(c => c.CompanyId == ctx.CompanyId && c.Code == HospitalGrowthIndicator.Concept[HospitalGrowthIndicator.Weight]).Select(c => (int?)c.ID).FirstOrDefaultAsync(ct);
				if (wc is int wid)
					kg = await _db.Set<HospitalClinicalObservation>().AsNoTracking().Where(o => o.CompanyId == ctx.CompanyId && o.PatientId == patientId && o.ConceptId == wid && o.Value != null && o.ObservedAt >= DateTime.UtcNow.AddDays(-HospitalMedSafetyRules.WeightDays))
						.OrderByDescending(o => o.ObservedAt).Select(o => o.Value).FirstOrDefaultAsync(ct);
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number is 207 or 208) { }
			if (kg is not > 0)
			{
				alerts.Add(new(HospitalAlertKind.NotChecked, HospitalInteractionSeverity.Unknown, false, "{0} is dosed by weight and no weight is recorded in the last 30 days, so the dose for the weight was not checked", new[] { drug }, null, null, "weight-unknown"));
				return;
			}
			foreach (var l in limits)
				if (HospitalMedSafetyRules.PerKg(mg, l.PerKgMg, kg) is { Over: true } hit)
					alerts.Add(new(HospitalAlertKind.Dose, HospitalInteractionSeverity.Major, true, "{0}: {1} mg is above {2} mg for {3} kg ({4} mg/kg)",
						new[] { drug, HospitalDrugSafetyRules.Num(mg!.Value), HospitalDrugSafetyRules.Num(hit.Max), HospitalDrugSafetyRules.Num(kg.Value), HospitalDrugSafetyRules.Num(l.PerKgMg!.Value) }, null, null, $"{l.Drug} per-kg {mg} > {hit.Max}"));
		}

		/// Keeps the stopping alerts a doctor went past with their reason.
		public async Task RecordOverridesAsync(BusinessContext ctx, int patientId, int? admissionId, string sourceType, int sourceId, IEnumerable<HospitalDrugAlert> alerts, string reason, int? employeeId, DateTime nowUtc, CancellationToken ct = default)
		{
			try
			{
				foreach (var a in alerts.Where(a => a.Blocks))
					_db.Add(new HospitalAlertOverride
					{
						CompanyId = ctx.CompanyId, PatientId = patientId, AdmissionId = admissionId, SourceType = sourceType, SourceId = sourceId, Kind = a.Kind,
						Detail = Clip(a.Detail, 300)!, Reason = Clip(reason, 500)!, At = nowUtc, ByEmployeeId = employeeId,
					});
				await _db.SaveChangesAsync(ct);
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex)) { }
		}

		// ---------------- the knowledge base (the pharmacy keeps it) ----------------

		private Task<bool> CanKeepAsync(BusinessContext ctx, CancellationToken ct) => _access.CanAsync(ctx, HospitalActions.Manage, null, ct);

		public async Task<HospitalResult> SaveInteractionAsync(BusinessContext ctx, HospitalInteractionInput input, int? employeeId, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await CanKeepAsync(ctx, ct)) return HospitalResult.Fail("forbidden");
			if (HospitalDrugSafetyRules.InteractionRefusal(input) is string why) return HospitalResult.Fail(why);
			var (a, b) = HospitalDrugSafetyRules.Pair(input.DrugA!, input.DrugB!);
			try
			{
				var set = _db.Set<HospitalDrugInteraction>();
				if (await set.AnyAsync(x => x.CompanyId == ctx.CompanyId && x.DrugA == a && x.DrugB == b && x.ID != (input.Id ?? 0), ct)) return HospitalResult.Fail("This pair is already in the list");
				HospitalDrugInteraction? row;
				if (input.Id is int id) { row = await set.FirstOrDefaultAsync(x => x.ID == id && x.CompanyId == ctx.CompanyId, ct); if (row == null) return HospitalResult.Fail("not_found"); }
				else { row = new HospitalDrugInteraction { CompanyId = ctx.CompanyId }; set.Add(row); }
				row.DrugA = a; row.DrugB = b; row.Severity = input.Severity!; row.EffectAr = Clip(input.EffectAr, 400)!; row.EffectEn = Clip(input.EffectEn, 400)!;
				row.IsActive = input.IsActive; row.UpdatedAt = nowUtc; row.UpdatedByEmployeeId = employeeId;
				await _db.SaveChangesAsync(ct);
				return HospitalResult.Success(row.ID);
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex)) { return HospitalResult.Fail(NoTables); }
		}

		public async Task<HospitalResult> SaveLimitAsync(BusinessContext ctx, HospitalDoseLimitInput input, int? employeeId, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await CanKeepAsync(ctx, ct)) return HospitalResult.Fail("forbidden");
			if (HospitalDrugSafetyRules.LimitRefusal(input) is string why) return HospitalResult.Fail(why);
			try
			{
				var set = _db.Set<HospitalDoseLimit>();
				HospitalDoseLimit? row;
				if (input.Id is int id) { row = await set.FirstOrDefaultAsync(x => x.ID == id && x.CompanyId == ctx.CompanyId, ct); if (row == null) return HospitalResult.Fail("not_found"); }
				else { row = new HospitalDoseLimit { CompanyId = ctx.CompanyId }; set.Add(row); }
				row.Drug = HospitalDrugSafetyRules.Key(input.Drug); row.Route = string.IsNullOrEmpty(input.Route) ? null : input.Route; row.Unit = input.Unit!;
				row.MaxSingle = input.MaxSingle; row.MaxDaily = input.MaxDaily; row.Note = Clip(input.Note, 300); row.IsActive = input.IsActive; row.UpdatedAt = nowUtc; row.UpdatedByEmployeeId = employeeId;
				row.PerKgMg = input.PerKgMg; row.EveryHours = input.PerKgMg == null ? null : input.EveryHours; row.StrengthMgPerMl = input.StrengthMgPerMl;
				row.RenalReviewBelow = input.RenalReviewBelow; row.RenalStopBelow = input.RenalStopBelow;
				await _db.SaveChangesAsync(ct);
				return HospitalResult.Success(row.ID);
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex)) { return HospitalResult.Fail(NoTables); }
		}

		public async Task<(List<HospitalDrugInteraction> Rows, int Total, bool Ready)> InteractionsAsync(BusinessContext ctx, string? q, string? severity, int page, int pageSize, CancellationToken ct = default)
		{
			try
			{
				var rows = _db.Set<HospitalDrugInteraction>().AsNoTracking().Where(x => x.CompanyId == ctx.CompanyId);
				if (!string.IsNullOrEmpty(severity)) rows = rows.Where(x => x.Severity == severity);
				foreach (var t in (q ?? "").Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries))
				{
					var term = t.Trim();
					rows = rows.Where(x => x.DrugA.Contains(term) || x.DrugB.Contains(term) || x.EffectAr.Contains(term) || x.EffectEn.Contains(term));
				}
				int total = await rows.CountAsync(ct);
				var list = await rows.OrderBy(x => x.DrugA).ThenBy(x => x.DrugB).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
				return (list, total, true);
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex)) { return (new(), 0, false); }
		}

		public async Task<(List<HospitalDoseLimit> Rows, int Total)> LimitsAsync(BusinessContext ctx, string? q, int page, int pageSize, CancellationToken ct = default)
		{
			try
			{
				var rows = _db.Set<HospitalDoseLimit>().AsNoTracking().Where(x => x.CompanyId == ctx.CompanyId);
				foreach (var t in (q ?? "").Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries)) { var term = t.Trim(); rows = rows.Where(x => x.Drug.Contains(term) || (x.Note != null && x.Note.Contains(term))); }
				int total = await rows.CountAsync(ct);
				return (await rows.OrderBy(x => x.Drug).ThenBy(x => x.Route).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct), total);
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex)) { return (new(), 0); }
		}

		/// H236: the governance view of overrides. An alert gone past again and again is either needed and ignored, or
		/// noise to tune — the committee reads it here; the reasons stay in the list below.
		public async Task<HospitalOverrideStats?> OverrideStatsAsync(BusinessContext ctx, int days, DateTime nowUtc, CancellationToken ct = default)
		{
			try
			{
				var from = nowUtc.AddDays(-days);
				var rows = await _db.Set<HospitalAlertOverride>().AsNoTracking().Where(o => o.CompanyId == ctx.CompanyId && o.At >= from)
					.Select(o => new { o.Kind, o.Detail, o.ByEmployeeId, o.At }).ToListAsync(ct);
				var byKind = rows.GroupBy(r => r.Kind).Select(g => (g.Key, g.Count())).OrderByDescending(x => x.Item2).ToList();
				var top = rows.GroupBy(r => r.Detail).Select(g => (g.Key, g.Count())).OrderByDescending(x => x.Item2).ThenBy(x => x.Key).Take(8).ToList();
				var who = rows.Where(r => r.ByEmployeeId != null).GroupBy(r => r.ByEmployeeId!.Value).Select(g => (g.Key, g.Count())).OrderByDescending(x => x.Item2).Take(5).ToList();
				var faces = await HospitalFaces.OfEmployeesAsync(_db, ctx.CompanyId, who.Select(w => (int?)w.Key), ct);
				var months = rows.GroupBy(r => r.At.ToLocalTime().ToString("yyyy-MM")).OrderBy(g => g.Key).Select(g => (g.Key, g.Count())).ToList();
				return new HospitalOverrideStats(days, rows.Count, byKind, top, who.Select(w => (faces.GetValueOrDefault(w.Key) ?? new HospitalFace(null, null, null), w.Item2)).ToList(), months);
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex)) { return null; }
		}

		public async Task<(List<HospitalOverrideRow> Rows, int Total)> OverridesAsync(BusinessContext ctx, string? q, int page, int pageSize, CancellationToken ct = default)
		{
			try
			{
				var rows = from o in _db.Set<HospitalAlertOverride>().AsNoTracking()
						   join p in _db.HospitalPatients.AsNoTracking() on o.PatientId equals p.ID
						   where o.CompanyId == ctx.CompanyId
						   select new { o, p.Name, p.NameEn, p.Mrn };
				foreach (var t in (q ?? "").Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries))
				{ var term = t.Trim(); rows = rows.Where(x => x.Name.Contains(term) || x.NameEn.Contains(term) || x.Mrn.Contains(term) || x.o.Detail.Contains(term) || x.o.Reason.Contains(term)); }
				int total = await rows.CountAsync(ct);
				var list = await rows.OrderByDescending(x => x.o.At).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
				var ids = list.Where(x => x.o.ByEmployeeId != null).Select(x => x.o.ByEmployeeId!.Value).Distinct().ToList();
				var people = await _db.Employee.AsNoTracking().Where(e => ids.Contains(e.ID) && e.EmpCompanyID == ctx.CompanyId)
					.Select(e => new { e.ID, e.FullName, e.FullNameEn }).ToDictionaryAsync(e => e.ID, e => DisplayName.Of(e.FullName ?? "", e.FullNameEn), ct);
				return (list.Select(x => new HospitalOverrideRow(x.o, x.Name, x.NameEn, x.Mrn, x.o.ByEmployeeId is int b ? people.GetValueOrDefault(b) : null)).ToList(), total);
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex)) { return (new(), 0); }
		}

		private static string? Clip(string? s, int max) => string.IsNullOrWhiteSpace(s) ? null : s.Trim()[..Math.Min(max, s.Trim().Length)];
	}
}
