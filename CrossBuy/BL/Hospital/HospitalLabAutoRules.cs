using CrossBuy.Models.Context.Hospital;

namespace CrossBuy.BL.Hospital
{
	public sealed record HospitalLabLimits(decimal? RefLow, decimal? RefHigh, decimal? CriticalLow, decimal? CriticalHigh);

	// ==========================================================================================
	// HOSPITAL H233 — reference ranges by age and sex, the delta check and autoverification, free of the database.
	//   · RANGE: the most specific range that fits the patient wins — one for their sex before one for both, then the
	//     narrowest age band; none fits → the analyte's own limits.
	//   · DELTA: the change from the patient's previous VERIFIED value of the same analyte (within the window) beyond the
	//     analyte's percentage is flagged "D" — a possible wrong patient, a contaminated sample or a real change, for a
	//     person to look at.
	//   · AUTOVERIFICATION: released without a second person only when every analyte allows it and every value is within
	//     its reference range (no L/H, nothing critical), passes its delta check, and the test's control is in (checked
	//     before results are taken at all). Anything else waits for the specialist, as before.
	// ==========================================================================================
	public static class HospitalLabAutoRules
	{
		public const int DeltaWindowDays = 30;

		public static HospitalLabLimits Limits(HospitalLabAnalyte a, IEnumerable<HospitalLabRefRange> ranges, string? sex, int? ageDays)
		{
			var fit = ranges.Where(r => (r.Sex == null || string.Equals(r.Sex, sex, StringComparison.OrdinalIgnoreCase))
					&& (ageDays == null ? r.AgeFromDays == 0 && r.AgeToDays == null : ageDays >= r.AgeFromDays && (r.AgeToDays == null || ageDays < r.AgeToDays)))
				.OrderByDescending(r => r.Sex != null)
				.ThenBy(r => (r.AgeToDays ?? 54750) - r.AgeFromDays)
				.FirstOrDefault();
			return fit == null ? new HospitalLabLimits(a.RefLow, a.RefHigh, a.CriticalLow, a.CriticalHigh)
				: new HospitalLabLimits(fit.RefLow, fit.RefHigh, fit.CriticalLow ?? a.CriticalLow, fit.CriticalHigh ?? a.CriticalHigh);
		}

		public static int? AgeDays(DateTime? birthDate, DateTime atLocal) => birthDate is DateTime b ? Math.Max(0, (int)(atLocal.Date - b.Date).TotalDays) : null;

		/// Whether the change from the previous value is beyond the allowed percentage (null threshold or no previous: no check).
		public static bool DeltaFailed(decimal? value, decimal? previous, decimal? percent)
		{
			if (value is not decimal v || previous is not decimal p || percent is not decimal pct || pct <= 0) return false;
			if (p == 0) return v != 0;
			return Math.Abs(v - p) * 100m / Math.Abs(p) > pct;
		}

		public static bool CanAutoVerify(IReadOnlyList<(bool AutoVerify, string Flag, bool Delta, bool HasValue)> lines) =>
			lines.Count > 0 && lines.All(l => l.AutoVerify && l.HasValue && l.Flag == HospitalLabFlag.Normal && !l.Delta);

		public static string? RangeRefusal(string? sex, int fromDays, int? toDays, decimal? low, decimal? high)
		{
			if (sex is not (null or "" or "M" or "F")) return "The sex is M, F or both";
			if (fromDays < 0 || (toDays is int t && t <= fromDays)) return "The age band must end after it starts";
			if (low == null && high == null) return "Enter at least one limit";
			return low is decimal l && high is decimal h && l > h ? "The low limit is above the high limit" : null;
		}

		/// Standard bands (code → sex, from days, to days, low, high), in the units of the standard panels.
		public static readonly (string Code, string? Sex, int From, int? To, decimal? Low, decimal? High)[] Standard =
		{
			("HGB", null, 0, 31, 14.0m, 24.0m), ("HGB", null, 31, 365, 10.0m, 14.0m), ("HGB", null, 365, 4380, 11.0m, 13.5m),
			("HGB", "M", 4380, null, 13.5m, 17.5m), ("HGB", "F", 4380, null, 12.0m, 15.5m),
			("HCT", "M", 4380, null, 41m, 53m), ("HCT", "F", 4380, null, 36m, 46m),
			("CREA", null, 0, 4380, 0.3m, 0.7m), ("CREA", "M", 4380, null, 0.74m, 1.35m), ("CREA", "F", 4380, null, 0.59m, 1.04m),
			("WBC", null, 0, 31, 9.0m, 30.0m), ("WBC", null, 31, 4380, 5.0m, 15.0m),
			("PLT", null, 0, 31, 150m, 450m),
			("ALT", "M", 6570, null, 7m, 56m), ("ALT", "F", 6570, null, 7m, 45m),
		};
	}
}
