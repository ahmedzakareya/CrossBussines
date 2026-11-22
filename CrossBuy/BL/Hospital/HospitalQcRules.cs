using System.Globalization;
using CrossBuy.Models.Context.Hospital;

namespace CrossBuy.BL.Hospital
{
	// ==========================================================================================
	// HOSPITAL H232 — the Westgard multirules, free of the database. z = (value − mean) / SD.
	//   1-2s  one value beyond ±2 SD                         → warning (look at the other rules)
	//   1-3s  one value beyond ±3 SD                         → reject (random error)
	//   2-2s  this and the previous value beyond 2 SD, same side → reject (systematic)
	//   R-4s  this and the previous value 4 SD apart, opposite sides beyond 2 SD → reject (random)
	//   4-1s  this and the previous 3 beyond 1 SD, same side   → reject (systematic)
	//   10x   this and the previous 9 on the same side of the mean → reject (systematic)
	// "previous" is the same material's earlier runs, newest first.
	// ==========================================================================================
	public static class HospitalQcRules
	{
		public static decimal Z(decimal value, decimal mean, decimal sd) => sd <= 0 ? 0 : (value - mean) / sd;

		public static (string Status, IReadOnlyList<string> Violations) Judge(decimal z, IReadOnlyList<decimal> previousZNewestFirst)
		{
			var v = new List<string>();
			var seq = new List<decimal> { z }; seq.AddRange(previousZNewestFirst);
			if (Math.Abs(z) > 3) v.Add("1-3s");
			if (seq.Count >= 2 && ((seq[0] > 2 && seq[1] > 2) || (seq[0] < -2 && seq[1] < -2))) v.Add("2-2s");
			if (seq.Count >= 2 && Math.Abs(seq[0] - seq[1]) > 4 && Math.Sign(seq[0]) != Math.Sign(seq[1]) && Math.Abs(seq[0]) > 2 && Math.Abs(seq[1]) > 2) v.Add("R-4s");
			if (seq.Count >= 4 && (seq.Take(4).All(x => x > 1) || seq.Take(4).All(x => x < -1))) v.Add("4-1s");
			if (seq.Count >= 10 && (seq.Take(10).All(x => x > 0) || seq.Take(10).All(x => x < 0))) v.Add("10x");
			if (v.Count > 0) return (HospitalQcStatus.Rejected, v);
			return Math.Abs(z) > 2 ? (HospitalQcStatus.Warning, new[] { "1-2s" }) : (HospitalQcStatus.Accepted, Array.Empty<string>());
		}

		public static string? MaterialRefusal(string? lot, int level, decimal mean, decimal sd, DateTime? expiresOn, DateTime todayLocal)
		{
			if (string.IsNullOrWhiteSpace(lot) || lot.Trim().Length > 40) return "Write the control lot (40 characters at most)";
			if (level is < 1 or > 3) return "The level is 1, 2 or 3";
			if (sd <= 0) return "The standard deviation must be above zero";
			if (mean < 0) return "The mean cannot be negative";
			return expiresOn is DateTime e && e.Date < todayLocal.Date ? "This lot has expired" : null;
		}

		/// The analyte is out of control while any active material's LATEST run is rejected.
		public static bool OutOfControl(IEnumerable<string?> latestStatusPerMaterial) => latestStatusPerMaterial.Any(s => s == HospitalQcStatus.Rejected);

		public static string Zs(decimal z) => z.ToString("+0.00;-0.00;0.00", CultureInfo.InvariantCulture);
	}
}
