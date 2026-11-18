namespace CrossBuy.BL.Hospital
{
	// ==========================================================================================
	// HOSPITAL H190 — two specialty cards on the visit, read from the stored observations (no database here):
	//   · ANTICOAGULATION: the time in therapeutic range (TTR) of the INR by Rosendaal's linear interpolation — between
	//     two readings the INR is taken to change evenly day by day; the days within the target count. A gap longer than
	//     56 days is not interpolated (the patient was not followed). TTR under 65% is poor control (ESC/EHRA): review
	//     adherence and interactions, consider a direct oral anticoagulant.
	//   · THYROID IN PREGNANCY: the latest TSH during an active pregnancy against the pregnancy range (ATA 2017, when
	//     the laboratory has no trimester range of its own: 0.1 to 4.0 mIU/L) — above it is hypothyroidism to act on
	//     (TPO antibodies, levothyroxine), below it is to be assessed.
	// ==========================================================================================
	public sealed record HospitalVisitTtr(decimal? Pct, int Days, int Readings, decimal LastInr, DateTime LastAt, decimal Low, decimal High, bool Poor);
	public sealed record HospitalVisitPregnancyTsh(decimal Tsh, DateTime At, int GaWeeks, int Trimester, string Verdict);

	public static class HospitalSpecialtyWidgetRules
	{
		public const int MaxGapDays = 56;
		public const decimal PoorTtr = 65m;
		public const decimal PregnancyTshLow = 0.1m;
		public const decimal PregnancyTshHigh = 4.0m;

		/// Rosendaal TTR over the readings (any order); null with fewer than two readings in a followed interval.
		public static (decimal? Pct, int Days) Ttr(IEnumerable<(DateTime At, decimal Inr)> readings, decimal low, decimal high)
		{
			var r = readings.OrderBy(x => x.At).GroupBy(x => x.At.Date).Select(g => (At: g.Key, Inr: g.Last().Inr)).ToList();
			int days = 0; decimal inRange = 0;
			for (int i = 1; i < r.Count; i++)
			{
				int gap = (r[i].At - r[i - 1].At).Days;
				if (gap <= 0 || gap > MaxGapDays) continue;
				for (int d = 0; d < gap; d++)
				{
					decimal inr = r[i - 1].Inr + (r[i].Inr - r[i - 1].Inr) * d / gap;
					if (inr >= low && inr <= high) inRange++;
				}
				days += gap;
			}
			return days == 0 ? (null, 0) : (Math.Round(inRange * 100m / days, 0), days);
		}

		public static HospitalVisitTtr? Card(IReadOnlyList<(DateTime At, decimal Inr)> readings, decimal? low, decimal? high)
		{
			if (readings.Count == 0) return null;
			decimal lo = low ?? 2.0m, hi = high ?? 3.0m;
			var (pct, days) = Ttr(readings, lo, hi);
			var last = readings.OrderBy(x => x.At).Last();
			return new HospitalVisitTtr(pct, days, readings.Count, last.Inr, last.At, lo, hi, pct is decimal p && p < PoorTtr);
		}

		/// Gestational age in completed weeks on a day, from the ultrasound due date or else the last period; null with neither.
		public static int? GaWeeks(DateTime? lmp, DateTime? edd, DateTime on)
		{
			int? days = edd is DateTime e ? 280 - (e.Date - on.Date).Days : lmp is DateTime l ? (on.Date - l.Date).Days : null;
			return days is int d && d >= 0 && d <= 45 * 7 ? d / 7 : null;
		}

		public static int Trimester(int gaWeeks) => gaWeeks < 14 ? 1 : gaWeeks < 28 ? 2 : 3;

		public static HospitalVisitPregnancyTsh? PregnancyTsh(decimal? tsh, DateTime? at, DateTime? lmp, DateTime? edd)
		{
			if (tsh is not decimal t || at is not DateTime a || GaWeeks(lmp, edd, a) is not int ga) return null;
			string verdict = t > PregnancyTshHigh ? "High" : t < PregnancyTshLow ? "Low" : "InRange";
			return new HospitalVisitPregnancyTsh(t, a, ga, Trimester(ga), verdict);
		}
	}
}
