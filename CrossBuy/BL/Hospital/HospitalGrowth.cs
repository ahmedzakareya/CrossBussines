using System.Globalization;
using System.Reflection;

namespace CrossBuy.BL.Hospital
{
	// ==========================================================================================
	// HOSPITAL H95 — CHILD GROWTH by the WHO references (the approved prototype screen sc-peds): weight, length/height,
	// BMI and head circumference for age, as z-scores and percentiles by the LMS method.
	//   * 0–1826 days: the WHO Child Growth Standards 2006 (by day — the tables end at day 1826);
	//   * 60–228 months: the WHO Growth Reference 2007 (by month, interpolated; weight only to 120 months — WHO publishes
	//     no weight-for-age after ten years, so none is computed: "not known", never a guess);
	//   * weight and BMI beyond ±3 SD use WHO's restricted method (the distance between SD2 and SD3), as WHO's own
	//     software does; length/height and head circumference use the plain LMS formula.
	// The tables are WHO's own files (BL/Hospital/Growth, README), embedded and read once.
	// ==========================================================================================
	public static class HospitalGrowthIndicator
	{
		public const string Weight = "wfa";
		public const string Height = "hfa";
		public const string Bmi = "bfa";
		public const string Head = "hcfa";
		public static readonly IReadOnlyList<string> All = new[] { Weight, Height, Bmi, Head };
		/// The dictionary concept each indicator reads.
		public static readonly IReadOnlyDictionary<string, string> Concept = new Dictionary<string, string>
		{ [Weight] = "29463-7", [Height] = "8302-2", [Bmi] = "39156-5", [Head] = "9843-4" };
	}

	public sealed record HospitalLms(double L, double M, double S);
	public sealed record HospitalGrowthPoint(DateTime At, int AgeDays, decimal Value, double? Z, double? Percentile);
	public sealed record HospitalGrowthCurves(string Indicator, IReadOnlyList<int> AgeDays, IReadOnlyDictionary<string, IReadOnlyList<double>> Lines);

	public static class HospitalGrowth
	{
		private const double DaysPerMonth = 30.4375;
		private static readonly Lazy<Dictionary<string, Dictionary<(int Sex, int Age), HospitalLms>>> Tables = new(Load);

		private static Dictionary<string, Dictionary<(int, int), HospitalLms>> Load()
		{
			var asm = typeof(HospitalGrowth).Assembly; var all = new Dictionary<string, Dictionary<(int, int), HospitalLms>>();
			foreach (var file in new[] { "weianthro", "lenanthro", "bmianthro", "hcanthro", "wfawho2007", "hfawho2007", "bfawho2007" })
			{
				var name = asm.GetManifestResourceNames().First(n => n.EndsWith("." + file + ".txt", StringComparison.Ordinal));
				using var r = new StreamReader(asm.GetManifestResourceStream(name)!);
				var t = new Dictionary<(int, int), HospitalLms>();
				r.ReadLine();   // the header
				for (string? line; (line = r.ReadLine()) != null;)
				{
					var p = line.Split('\t'); if (p.Length < 5) continue;
					t[(int.Parse(p[0], CultureInfo.InvariantCulture), int.Parse(p[1], CultureInfo.InvariantCulture))] =
						new HospitalLms(double.Parse(p[2], CultureInfo.InvariantCulture), double.Parse(p[3], CultureInfo.InvariantCulture), double.Parse(p[4], CultureInfo.InvariantCulture));
				}
				all[file] = t;
			}
			return all;
		}

		private static (string Under5, string? Over5, int MaxMonths) Files(string indicator) => indicator switch
		{
			HospitalGrowthIndicator.Weight => ("weianthro", "wfawho2007", 120),
			HospitalGrowthIndicator.Height => ("lenanthro", "hfawho2007", 228),
			HospitalGrowthIndicator.Bmi => ("bmianthro", "bfawho2007", 228),
			HospitalGrowthIndicator.Head => ("hcanthro", null, 0),
			_ => throw new ArgumentOutOfRangeException(nameof(indicator)),
		};

		/// The reference at an age in days for a sex ("M"/"F"); null when WHO publishes none for that age.
		public static HospitalLms? Lms(string indicator, string sex, int ageDays)
		{
			if (ageDays < 0) return null;
			int s = sex == "F" ? 2 : 1; var (u5, o5, max) = Files(indicator); var t = Tables.Value;
			if (ageDays <= 1826) return t[u5].TryGetValue((s, ageDays), out var a) ? a : null;
			if (o5 == null) return null;
			double months = ageDays / DaysPerMonth;
			if (months > max) return null;
			int lo = (int)Math.Floor(months), hi = lo + 1; double f = months - lo;
			if (!t[o5].TryGetValue((s, lo), out var A)) return t[o5].TryGetValue((s, hi), out var B0) ? B0 : null;
			if (!t[o5].TryGetValue((s, hi), out var B)) return A;
			return new HospitalLms(A.L + (B.L - A.L) * f, A.M + (B.M - A.M) * f, A.S + (B.S - A.S) * f);
		}

		private static double At(HospitalLms r, double z) => Math.Abs(r.L) < 1e-9 ? r.M * Math.Exp(r.S * z) : r.M * Math.Pow(1 + r.L * r.S * z, 1 / r.L);

		/// The z-score of a measure; weight and BMI beyond ±3 by WHO's restricted method.
		public static double? Z(string indicator, string sex, int ageDays, double value)
		{
			if (value <= 0 || Lms(indicator, sex, ageDays) is not HospitalLms r) return null;
			double z = Math.Abs(r.L) < 1e-9 ? Math.Log(value / r.M) / r.S : (Math.Pow(value / r.M, r.L) - 1) / (r.L * r.S);
			if (indicator is HospitalGrowthIndicator.Weight or HospitalGrowthIndicator.Bmi)
			{
				if (z > 3) { double sd3 = At(r, 3), sd23 = sd3 - At(r, 2); z = 3 + (value - sd3) / sd23; }
				else if (z < -3) { double sd3 = At(r, -3), sd23 = At(r, -2) - sd3; z = -3 + (value - sd3) / sd23; }
			}
			return Math.Round(z, 2);
		}

		/// The value on a z line at an age (the curves of the chart).
		public static double? ValueAt(string indicator, string sex, int ageDays, double z) => Lms(indicator, sex, ageDays) is HospitalLms r ? At(r, z) : null;

		/// The percentile of a z-score (the standard normal), to one decimal.
		public static double Percentile(double z) => Math.Round(100 * 0.5 * (1 + Erf(z / Math.Sqrt(2))), 1);

		private static double Erf(double x)
		{
			// Abramowitz & Stegun 7.1.26 (|error| < 1.5e-7)
			double t = 1 / (1 + 0.3275911 * Math.Abs(x)), y = 1 - (((((1.061405429 * t - 1.453152027) * t) + 1.421413741) * t - 0.284496736) * t + 0.254829592) * t * Math.Exp(-x * x);
			return x >= 0 ? y : -y;
		}

		/// The chart's lines (P3, P15, P50, P85, P97) over an age span, in steps.
		public static readonly IReadOnlyDictionary<string, double> LineZ = new Dictionary<string, double> { ["P3"] = -1.881, ["P15"] = -1.036, ["P50"] = 0, ["P85"] = 1.036, ["P97"] = 1.881 };
		public static HospitalGrowthCurves Curves(string indicator, string sex, int fromDays, int toDays)
		{
			int step = Math.Max(7, (toDays - fromDays) / 60);
			var ages = new List<int>();
			for (int a = Math.Max(0, fromDays); a <= toDays; a += step) if (Lms(indicator, sex, a) != null) ages.Add(a);
			return new HospitalGrowthCurves(indicator, ages, LineZ.ToDictionary(k => k.Key, k => (IReadOnlyList<double>)ages.Select(a => Math.Round(ValueAt(indicator, sex, a, k.Value)!.Value, 2)).ToList()));
		}

		/// The band between the chart's lines a z falls in (0 = under P3 … 5 = over P97).
		public static int Band(double z) => LineZ.Values.Count(v => z >= v);

		/// Crossed two or more percentile lines between the first and the latest measure (either way).
		public static (bool Crossed, HospitalGrowthPoint? From, HospitalGrowthPoint? To) Crossing(IReadOnlyList<HospitalGrowthPoint> pts)
		{
			var zs = pts.Where(p => p.Z != null).OrderBy(p => p.AgeDays).ToList();
			if (zs.Count < 2) return (false, null, null);
			var a = zs[0]; var b = zs[^1];
			return (Math.Abs(Band(b.Z!.Value) - Band(a.Z!.Value)) >= 2, a, b);
		}

		public static int AgeDays(DateTime birth, DateTime at) => (int)Math.Floor((at.Date - birth.Date).TotalDays);
	}
}
