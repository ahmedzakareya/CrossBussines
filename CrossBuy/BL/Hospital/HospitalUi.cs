namespace CrossBuy.BL.Hospital
{
	/// Small presentation helpers the hospital views share.
	public static class HospitalUi
	{
		/// Two initials from a name (the English one reads best on the brand tint), for a person with no photo.
		public static string Initials(string? name)
		{
			var p = (name ?? "").Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
			return ((p.Length > 0 ? p[0][..1] : "") + (p.Length > 1 ? p[1][..1] : "")).ToUpperInvariant();
		}

		/// A wall-clock minute of the day as "09:30".
		public static string Clock(int minute) => $"{minute / 60:00}:{minute % 60:00}";

		/// A lab value at its analyte's decimals, invariant (results are read left to right in both languages).
		public static string LabNum(decimal? v, int dec) => v?.ToString("F" + dec, System.Globalization.CultureInfo.InvariantCulture) ?? "";

		/// A reference range as "4.0 – 11.0", "≥ 40" or "≤ 200"; "—" when neither limit is set.
		public static string LabRange(decimal? lo, decimal? hi, int dec) =>
			lo != null && hi != null ? LabNum(lo, dec) + " – " + LabNum(hi, dec) : lo != null ? "≥ " + LabNum(lo, dec) : hi != null ? "≤ " + LabNum(hi, dec) : "—";
	}
}
