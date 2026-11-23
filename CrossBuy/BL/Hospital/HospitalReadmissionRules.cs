namespace CrossBuy.BL.Hospital
{
	public sealed record HospitalRiskCase(int Score, bool Outcome);
	public sealed record HospitalRiskBand(string Band, int Cases, int Events)
	{
		public decimal? ObservedPct => Cases == 0 ? null : Math.Round(Events * 100m / Cases, 1, MidpointRounding.AwayFromZero);
	}

	// ==========================================================================================
	// HOSPITAL H253 — PREDICTIVE MODELS, VALIDATED ON THIS HOSPITAL'S OWN PATIENTS. The model is the published LACE index
	// for 30-day readmission or death (van Walraven 2010), scored from what the record holds, and before anyone relies on
	// it here it is MEASURED here:
	//   · L — length of stay in days: <1 → 0, 1 → 1, 2 → 2, 3 → 3, 4–6 → 4, 7–13 → 5, ≥14 → 7
	//   · A — acute (emergency) admission → 3
	//   · C — Charlson comorbidity score from the patient's ICD-10 diagnoses: 0 → 0, 1 → 1, 2 → 2, 3 → 3, ≥4 → 5
	//   · E — emergency visits in the six months before: 0–4 points, capped at 4
	//   LACE ≥ 10 is HIGH risk.
	//   · DISCRIMINATION is the AUC (the chance a readmitted patient scored above one who was not; ties count half) on
	//     the discharges whose 30 days have passed; CALIBRATION is the observed readmission rate in each band.
	//   · DRIFT is the population stability index of the score bands, the last 90 days against the year before:
	//     < 0.1 stable, 0.1–0.25 watch, > 0.25 the population has moved (re-validate before relying on the score).
	// The Charlson weights are the Quan ICD-10 groups, read by code prefix (a summary, not the full coding algorithm).
	// ==========================================================================================
	public static class HospitalReadmissionRules
	{
		public const int HighRisk = 10;

		public static int LosPoints(int days) => days < 1 ? 0 : days <= 3 ? days : days <= 6 ? 4 : days <= 13 ? 5 : 7;
		public static int CharlsonPoints(int charlson) => charlson >= 4 ? 5 : Math.Max(0, charlson);
		public static int Lace(int losDays, bool acute, int charlson, int edVisits6m) =>
			LosPoints(losDays) + (acute ? 3 : 0) + CharlsonPoints(charlson) + Math.Min(4, Math.Max(0, edVisits6m));

		private static readonly (string[] Prefixes, int Weight, string Group)[] Charlson =
		{
			(new[] { "I21", "I22", "I252" }, 1, "MI"), (new[] { "I50", "I110", "I130", "I132", "I255", "I42" }, 1, "CHF"),
			(new[] { "I70", "I71", "I731", "I738", "I739", "I771", "K551", "K558", "K559", "Z958", "Z959" }, 1, "PVD"),
			(new[] { "I60", "I61", "I62", "I63", "I64", "I65", "I66", "I67", "I68", "I69", "G45", "G46" }, 1, "CVD"),
			(new[] { "F00", "F01", "F02", "F03", "F051", "G30", "G311" }, 1, "Dementia"),
			(new[] { "J40", "J41", "J42", "J43", "J44", "J45", "J46", "J47", "J60", "J61", "J62", "J63", "J64", "J65", "J66", "J67" }, 1, "COPD"),
			(new[] { "M05", "M06", "M315", "M32", "M33", "M34", "M351", "M353", "M360" }, 1, "Rheumatic"),
			(new[] { "K25", "K26", "K27", "K28" }, 1, "PUD"), (new[] { "B18", "K700", "K701", "K702", "K703", "K709", "K713", "K714", "K715", "K717", "K73", "K74" }, 1, "MildLiver"),
			(new[] { "E100", "E101", "E106", "E108", "E109", "E110", "E111", "E116", "E118", "E119" }, 1, "Diabetes"),
			(new[] { "E102", "E103", "E104", "E105", "E107", "E112", "E113", "E114", "E115", "E117" }, 2, "DiabetesComplicated"),
			(new[] { "G81", "G82", "G041", "G114", "G801", "G802" }, 2, "Plegia"),
			(new[] { "N18", "N19", "N052", "N053", "N054", "N055", "N056", "N057", "N250", "Z490", "Z491", "Z492", "Z940", "Z992" }, 2, "Renal"),
			(new[] { "C0", "C1", "C2", "C30", "C31", "C32", "C33", "C34", "C37", "C38", "C39", "C40", "C41", "C43", "C45", "C46", "C47", "C48", "C49", "C50", "C51", "C52", "C53", "C54", "C55", "C56", "C57", "C58", "C60", "C61", "C62", "C63", "C64", "C65", "C66", "C67", "C68", "C69", "C70", "C71", "C72", "C73", "C74", "C75", "C76", "C81", "C82", "C83", "C84", "C85", "C88", "C90", "C91", "C92", "C93", "C94", "C95", "C96", "C97" }, 2, "Cancer"),
			(new[] { "I850", "I859", "I864", "I982", "K704", "K711", "K721", "K729", "K765", "K766", "K767" }, 3, "SevereLiver"),
			(new[] { "C77", "C78", "C79", "C80" }, 6, "Metastatic"),
			(new[] { "B20", "B21", "B22", "B24" }, 6, "HIV"),
		};

		/// The Charlson score of a set of ICD-10 codes: each group counts once; a complicated form replaces its mild one.
		public static int CharlsonScore(IEnumerable<string> codes)
		{
			var norm = codes.Select(c => (c ?? "").Replace(".", "").Trim().ToUpperInvariant()).Where(c => c.Length >= 3).Distinct().ToList();
			var groups = Charlson.Where(g => norm.Any(c => g.Prefixes.Any(p => c.StartsWith(p, StringComparison.Ordinal)))).Select(g => (g.Group, g.Weight)).ToDictionary(x => x.Group, x => x.Weight);
			if (groups.ContainsKey("DiabetesComplicated")) groups.Remove("Diabetes");
			if (groups.ContainsKey("SevereLiver")) groups.Remove("MildLiver");
			if (groups.ContainsKey("Metastatic")) groups.Remove("Cancer");
			return groups.Values.Sum();
		}

		/// Area under the ROC curve (Mann-Whitney), null without both outcomes.
		public static decimal? Auc(IReadOnlyList<HospitalRiskCase> cases)
		{
			var pos = cases.Where(c => c.Outcome).Select(c => c.Score).ToList();
			var neg = cases.Where(c => !c.Outcome).Select(c => c.Score).ToList();
			if (pos.Count == 0 || neg.Count == 0) return null;
			double wins = 0;
			foreach (var p in pos) foreach (var n in neg) wins += p > n ? 1 : p == n ? 0.5 : 0;
			return Math.Round((decimal)(wins / (pos.Count * (double)neg.Count)), 3, MidpointRounding.AwayFromZero);
		}

		public static string Band(int score) => score < 5 ? "0-4" : score < 10 ? "5-9" : score < 15 ? "10-14" : "15+";
		public static readonly string[] Bands = { "0-4", "5-9", "10-14", "15+" };

		public static IReadOnlyList<HospitalRiskBand> Calibration(IReadOnlyList<HospitalRiskCase> cases) =>
			Bands.Select(b => { var x = cases.Where(c => Band(c.Score) == b).ToList(); return new HospitalRiskBand(b, x.Count, x.Count(c => c.Outcome)); }).ToList();

		/// Population stability index of the score bands (recent against baseline), with 0.5 % floors so an empty band does not explode.
		public static decimal? Psi(IReadOnlyList<int> baseline, IReadOnlyList<int> recent)
		{
			if (baseline.Count == 0 || recent.Count == 0) return null;
			double psi = 0;
			foreach (var b in Bands)
			{
				double e = Math.Max(0.005, baseline.Count(s => Band(s) == b) / (double)baseline.Count);
				double a = Math.Max(0.005, recent.Count(s => Band(s) == b) / (double)recent.Count);
				psi += (a - e) * Math.Log(a / e);
			}
			return Math.Round((decimal)psi, 3, MidpointRounding.AwayFromZero);
		}

		public static string Drift(decimal? psi) => psi is not decimal p ? "Unknown" : p < 0.1m ? "Stable" : p <= 0.25m ? "Watch" : "Shifted";
	}
}
