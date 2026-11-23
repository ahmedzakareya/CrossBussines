namespace CrossBuy.BL.Hospital
{
	// ==========================================================================================
	// HOSPITAL H255 — the privacy patterns, free of the database.
	//   · FAMILY NAME: the last word of the employee's name equals the last word of the patient's (Arabic names compared
	//     without the article "ال" and the hamza forms; English case-insensitively); names of one word are not compared.
	//   · VOLUME: distinct files opened in a day above both 30 and three times the employee's median day of the previous
	//     30 days.
	//   · AFTER HOURS: 22:00–06:00 on the ward clock.
	// ==========================================================================================
	public static class HospitalPrivacyRules
	{
		public const int VolumeFloor = 30, VolumeFactor = 3;

		public static string NormalizeName(string? word)
		{
			var w = (word ?? "").Trim().ToLowerInvariant();
			if (w.StartsWith("ال") && w.Length > 3) w = w[2..];
			return w.Replace('أ', 'ا').Replace('إ', 'ا').Replace('آ', 'ا').Replace('ة', 'ه').Replace('ى', 'ي');
		}

		public static string? FamilyName(string? fullName)
		{
			var parts = (fullName ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
			return parts.Length < 2 ? null : NormalizeName(parts[^1]);
		}

		public static bool SameFamily(string? employeeAr, string? employeeEn, string? patientAr, string? patientEn)
		{
			bool Eq(string? a, string? b) { var x = FamilyName(a); var y = FamilyName(b); return x != null && y != null && x.Length >= 3 && x == y; }
			return Eq(employeeAr, patientAr) || Eq(employeeEn, patientEn);
		}

		public static bool VolumeAnomaly(int today, IReadOnlyList<int> previousDays)
		{
			if (today <= VolumeFloor) return false;
			var s = previousDays.Where(x => x > 0).OrderBy(x => x).ToList();
			double median = s.Count == 0 ? 0 : s.Count % 2 == 1 ? s[s.Count / 2] : (s[s.Count / 2 - 1] + s[s.Count / 2]) / 2.0;
			return today > VolumeFactor * median;
		}

		public static bool AfterHours(DateTime wardTime) => wardTime.Hour >= 22 || wardTime.Hour < 6;

		public static string? ReviewRefusal(string current, string? status, string? note)
		{
			if (current != Models.Context.Hospital.HospitalPrivacyStatus.Open) return "This alert is already reviewed";
			if (status is not (Models.Context.Hospital.HospitalPrivacyStatus.Cleared or Models.Context.Hospital.HospitalPrivacyStatus.Confirmed)) return "not_found";
			return string.IsNullOrWhiteSpace(note) ? "Write what was found" : null;
		}
	}
}
