using CrossBuy.Models.Context.Hospital;

namespace CrossBuy.BL.Hospital
{
	/// What the record holds about one patient, gathered for the registries.
	public sealed record HospitalRegistryFacts(
		IReadOnlyCollection<string> Codes, string? ChronicText,
		decimal? LastHba1c, DateTime? LastHba1cAt, decimal? MaxHba1c,
		DateTime? LastCreatinineAt, decimal? LastEgfr,
		int? LastSystolic, int? LastDiastolic, DateTime? LastBpAt);

	public sealed record HospitalRegistryGap(string Code, string Label);

	// ==========================================================================================
	// HOSPITAL H262 — who belongs to each registry and which care gaps they have, free of the database.
	//   Diabetes:     ICD-10 E10–E14, a "diabetes" chronic note, or any HbA1c ≥ 6.5 %.
	//                 Gaps: HbA1c not in 6 months; last HbA1c ≥ 8 %; kidney function not checked in 12 months.
	//   Hypertension: ICD-10 I10–I15 or a "hypertension" chronic note.
	//                 Gaps: no blood pressure in 6 months; last reading ≥ 140/90.
	//   CKD:          ICD-10 N18, a "kidney disease" chronic note, or an eGFR below 60.
	//                 Gaps: kidney function not checked in 6 months; last eGFR below 30 (refer to nephrology).
	// The targets are the common guideline thresholds (ADA, ESC/ESH, KDIGO), to be confirmed by the clinical lead.
	// ==========================================================================================
	public static class HospitalRegistryRules
	{
		private static bool Code(IReadOnlyCollection<string> codes, params string[] prefixes) => codes.Any(c => prefixes.Any(p => c.Trim().StartsWith(p, StringComparison.OrdinalIgnoreCase)));
		private static bool Text(string? t, params string[] words) => t != null && words.Any(w => t.Contains(w, StringComparison.OrdinalIgnoreCase));

		public static bool Member(string registry, HospitalRegistryFacts f) => registry switch
		{
			HospitalRegistryKind.Diabetes => Code(f.Codes, "E10", "E11", "E12", "E13", "E14") || Text(f.ChronicText, "سكري", "السكر", "diabet") || f.MaxHba1c >= 6.5m,
			HospitalRegistryKind.Hypertension => Code(f.Codes, "I10", "I11", "I12", "I13", "I15") || Text(f.ChronicText, "ضغط", "hypertens"),
			HospitalRegistryKind.Ckd => Code(f.Codes, "N18") || Text(f.ChronicText, "الكلى", "كلوي", "kidney disease", "ckd") || f.LastEgfr < 60m,
			_ => false,
		};

		public static IReadOnlyList<HospitalRegistryGap> Gaps(string registry, HospitalRegistryFacts f, DateTime nowUtc)
		{
			var g = new List<HospitalRegistryGap>();
			bool Older(DateTime? at, int days) => at == null || (nowUtc - at.Value).TotalDays > days;
			switch (registry)
			{
				case HospitalRegistryKind.Diabetes:
					if (Older(f.LastHba1cAt, 182)) g.Add(new("hba1c-due", "HbA1c not done in 6 months"));
					if (f.LastHba1c >= 8m) g.Add(new("hba1c-high", "HbA1c above target (≥ 8 %)"));
					if (Older(f.LastCreatinineAt, 365)) g.Add(new("kidney-due", "Kidney function not checked in 12 months"));
					break;
				case HospitalRegistryKind.Hypertension:
					if (Older(f.LastBpAt, 182)) g.Add(new("bp-due", "No blood pressure in 6 months"));
					else if (f.LastSystolic >= 140 || f.LastDiastolic >= 90) g.Add(new("bp-high", "Blood pressure above target (≥ 140/90)"));
					break;
				case HospitalRegistryKind.Ckd:
					if (Older(f.LastCreatinineAt, 182)) g.Add(new("kidney-due", "Kidney function not checked in 6 months"));
					if (f.LastEgfr < 30m) g.Add(new("egfr-low", "eGFR below 30: nephrology review"));
					break;
			}
			return g;
		}

		/// CKD-EPI 2021 (race-free) eGFR from serum creatinine in mg/dL, age in years and sex ("M"/"F"); null when unknown.
		public static decimal? Egfr(decimal? creatinineMgDl, int? ageYears, string? sex)
		{
			if (creatinineMgDl is not decimal scr || scr <= 0 || ageYears is not int age || age < 18 || sex is not ("M" or "F")) return null;
			double k = sex == "F" ? 0.7 : 0.9, a = sex == "F" ? -0.241 : -0.302, s = (double)scr / k;
			double e = 142 * Math.Pow(Math.Min(s, 1), a) * Math.Pow(Math.Max(s, 1), -1.200) * Math.Pow(0.9938, age) * (sex == "F" ? 1.012 : 1);
			return Math.Round((decimal)e, 0, MidpointRounding.AwayFromZero);
		}

		public static string? OutreachRefusal(string? registry, string? note)
		{
			if (!HospitalRegistryKind.All.Contains(registry ?? "")) return "Choose the registry";
			return string.IsNullOrWhiteSpace(note) ? "Write what was agreed with the patient" : null;
		}
	}
}
