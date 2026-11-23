using CrossBuy.Models.Context.Hospital;

namespace CrossBuy.BL.Hospital
{
	public sealed record HospitalAcuityFactor(string Code, string Ar, string En, int Points);

	// ==========================================================================================
	// HOSPITAL H246 — the patient classification, free of the database (a factor-type system in the manner of the
	// published patient-classification instruments; the points and hours are the facility's starting values).
	//   · Each factor carries points; the total gives the LEVEL: 0–3 → 1 (minimal), 4–7 → 2 (moderate),
	//     8–12 → 3 (high), 13+ → 4 (intensive). A patient on a ventilator or needing one-to-one observation is level 4.
	//   · HOURS OF CARE per patient-day by level: 3, 5, 8, 16 (an ICU patient-day at one nurse to one or two patients).
	//   · NURSES NEEDED for an 8-hour shift = Σ (hours per day ÷ 3) ÷ 8, rounded up.
	// ==========================================================================================
	public static class HospitalAcuityRules
	{
		public const int ShiftHours = 8;

		public static readonly IReadOnlyList<HospitalAcuityFactor> Factors = new HospitalAcuityFactor[]
		{
			new("VITALS", "علامات حيوية كل ساعة إلى ساعتين", "Vital signs every 1–2 h", 3),
			new("IV", "محاليل أو أدوية وريدية مستمرة", "Continuous IV fluids or drips", 2),
			new("MEDS", "أدوية متعددة أو عالية الخطورة", "Many or high-alert medicines", 2),
			new("MOBILITY", "يحتاج مساعدة كاملة في الحركة", "Full help to move", 3),
			new("HYGIENE", "يحتاج مساعدة كاملة في النظافة والتغذية", "Full help with hygiene and feeding", 2),
			new("WOUND", "عناية معقدة بالجروح أو الأنابيب", "Complex wound or drain care", 2),
			new("ISOLATION", "عزل", "Isolation precautions", 2),
			new("CONFUSION", "تشوش أو خطر سقوط مرتفع", "Confusion or high fall risk", 2),
			new("TEACHING", "تثقيف أو تخطيط خروج مكثف", "Intensive teaching or discharge planning", 1),
			new("VENT", "على جهاز تنفس صناعي", "On a ventilator", 13),
			new("ONE2ONE", "مراقبة فردية مستمرة", "One-to-one observation", 13),
		};

		public static (int Points, int Level) Classify(IEnumerable<string>? codes)
		{
			var set = new HashSet<string>(codes ?? Array.Empty<string>());
			int pts = Factors.Where(f => set.Contains(f.Code)).Sum(f => f.Points);
			int level = set.Contains("VENT") || set.Contains("ONE2ONE") || pts >= 13 ? 4 : pts >= 8 ? 3 : pts >= 4 ? 2 : 1;
			return (pts, level);
		}

		public static decimal HoursPerDay(int level) => level switch { 1 => 3m, 2 => 5m, 3 => 8m, 4 => 16m, _ => 3m };

		public static int NursesNeeded(IEnumerable<int> levels)
		{
			var shiftHours = levels.Sum(l => HoursPerDay(l) / 3m);
			return shiftHours <= 0 ? 0 : (int)Math.Ceiling(shiftHours / ShiftHours);
		}

		/// A patient not yet classified this shift is counted at level 2 (moderate) so the need is never understated to zero.
		public const int Unclassified = 2;

		public static string FactorText(IEnumerable<string>? codes) =>
			string.Join(",", Factors.Where(f => (codes ?? Array.Empty<string>()).Contains(f.Code)).Select(f => f.Code));

		public static string Gap(int needed, int? planned) => planned is not int p ? "Unplanned" : p < needed ? "Short" : p > needed + 1 ? "Over" : "Matched";
	}
}
