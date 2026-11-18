using CrossBuy.Models.Context.Hospital;

namespace CrossBuy.BL.Hospital
{
	// ==========================================================================================
	// HOSPITAL H186 — implant traceability and peri-implant maintenance; the rules, without a database.
	//   · The classification (2017 World Workshop, Berglundh et al.; without a baseline radiograph):
	//       mobility                                                → failing (not integrated);
	//       bleeding or suppuration AND (bone loss ≥ 3 mm OR probing ≥ 6 mm) → peri-implantitis;
	//       bleeding or suppuration otherwise                       → mucositis;
	//       none of these                                           → health.
	//   · A restored implant is reviewed every 12 months from its restoration, then from its last review.
	//   · A graft recorded at placement carries its lot (a recall reaches it as an implant lot does).
	// ==========================================================================================
	public static class HospitalImplantReviewRules
	{
		public const int ReviewEveryMonths = 12;
		public const decimal BoneLossLimitMm = 3m;
		public const decimal ProbingLimitMm = 6m;

		public static string Classify(bool bleeding, bool suppuration, decimal maxProbingMm, decimal? boneLossMm, bool mobility)
		{
			if (mobility) return HospitalPeriImplantStatus.Failing;
			bool inflamed = bleeding || suppuration;
			if (inflamed && ((boneLossMm ?? 0) >= BoneLossLimitMm || maxProbingMm >= ProbingLimitMm)) return HospitalPeriImplantStatus.Periimplantitis;
			return inflamed ? HospitalPeriImplantStatus.Mucositis : HospitalPeriImplantStatus.Health;
		}

		public static string? Refusal(string stage, decimal maxProbingMm, decimal? boneLossMm, DateTime on, DateTime today, DateTime? restoredOn)
		{
			if (stage != HospitalImplantStage.Restored) return "A peri-implant review is of a restored implant";
			if (maxProbingMm is < 1 or > 15) return "The deepest probing is 1 to 15 mm";
			if (boneLossMm is < 0 or > 15) return "The bone loss is 0 to 15 mm";
			if (on.Date > today.Date) return "A date cannot be in the future";
			if (restoredOn is DateTime r && on.Date < r.Date) return "A review cannot be before the restoration was fitted";
			return null;
		}

		/// When the next review is due: 12 months after the last one, or after the restoration.
		public static DateTime? NextReview(DateTime? restoredOn, DateTime? lastReview) =>
			(lastReview ?? restoredOn)?.Date.AddMonths(ReviewEveryMonths);

		public static bool Overdue(string stage, DateTime? restoredOn, DateTime? lastReview, DateTime today) =>
			stage == HospitalImplantStage.Restored && NextReview(restoredOn, lastReview) is DateTime d && d < today.Date;

		public static string? GraftRefusal(string? material, string? lot) =>
			!string.IsNullOrWhiteSpace(material) && string.IsNullOrWhiteSpace(lot) ? "A graft is recorded with its lot" : null;

		/// The lot as searched: trimmed, case ignored.
		public static string? LotKey(string? lot) => string.IsNullOrWhiteSpace(lot) ? null : lot.Trim().ToUpperInvariant();
	}
}
