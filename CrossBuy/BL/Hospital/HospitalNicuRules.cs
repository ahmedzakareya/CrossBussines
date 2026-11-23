using CrossBuy.Models.Context.Hospital;

namespace CrossBuy.BL.Hospital
{
	// ==========================================================================================
	// HOSPITAL H252 — neonatal arithmetic, free of the database.
	//   · DAY OF LIFE: the day of birth is day 0.
	//   · POSTMENSTRUAL AGE = gestation at birth + days since birth (weeks + days). A baby is PRETERM when born before 37
	//     weeks; the CORRECTED age (preterm only) is the chronological age less the weeks short of 40.
	//   · WEIGHT against birth weight, in percent (the usual nadir is about 7–10 % below; birth weight regained in about
	//     two weeks).
	//   · GROWTH VELOCITY over a span = 1000 × ln(W2 / W1) / days, in g/kg/day (the exponential method); 15–20 g/kg/day
	//     is the usual target for a preterm baby.
	//   · FEEDS in mL/kg/day against the day's weight (the last known when none that day).
	//   · Size for gestation at birth (Fenton-style cut-offs are not bundled): under 2500 g is LOW birth weight, under
	//     1500 g VERY LOW, under 1000 g EXTREMELY LOW.
	// ==========================================================================================
	public static class HospitalNicuRules
	{
		public static int DayOfLife(DateTime bornLocalDay, DateTime todayLocal) => Math.Max(0, (todayLocal.Date - bornLocalDay.Date).Days);

		public static (int Weeks, int Days)? Pma(int? gestationWeeks, int dayOfLife)
		{
			if (gestationWeeks is not int g) return null;
			int total = g * 7 + dayOfLife;
			return (total / 7, total % 7);
		}

		public static bool Preterm(int? gestationWeeks) => gestationWeeks is < 37;

		/// Corrected age in days (null when term or unknown; negative while still before term-equivalent).
		public static int? CorrectedDays(int? gestationWeeks, int dayOfLife) => Preterm(gestationWeeks) ? dayOfLife - (40 - gestationWeeks!.Value) * 7 : null;

		public static decimal? PercentOfBirth(int? weight, int birthWeight) =>
			weight is int w && birthWeight > 0 ? Math.Round(w * 100m / birthWeight, 1, MidpointRounding.AwayFromZero) : null;

		public static decimal? Velocity(int w1, DateTime d1, int w2, DateTime d2)
		{
			int days = (d2.Date - d1.Date).Days;
			if (days <= 0 || w1 <= 0 || w2 <= 0) return null;
			return Math.Round((decimal)(1000 * Math.Log((double)w2 / w1) / days), 1, MidpointRounding.AwayFromZero);
		}

		public static decimal? FeedsPerKg(int? feedsMl, int? weightGrams) =>
			feedsMl is int f && weightGrams is int w && w > 0 ? Math.Round(f * 1000m / w, 0, MidpointRounding.AwayFromZero) : null;

		public static string? BirthWeightClass(int grams) => grams < 1000 ? "ELBW" : grams < 1500 ? "VLBW" : grams < 2500 ? "LBW" : null;

		public static string? DayRefusal(int? weight, decimal? length, decimal? head, int? feeds, string? feedType, int? bili)
		{
			if (weight is < 300 or > 7000) return "The weight is 300 to 7000 g";
			if (length is < 20 or > 70 || head is < 15 or > 50) return "A value is outside what can be measured";
			if (feeds is < 0 or > 2000) return "The day's feeds are 0 to 2000 mL";
			if (feedType != null && !HospitalNicuFeed.All.Contains(feedType)) return "not_found";
			if (bili is < 0 or > 900) return "The bilirubin is 0 to 900 µmol/L";
			return weight == null && length == null && head == null && feeds == null && bili == null ? "Enter at least one value" : null;
		}
	}
}
