namespace CrossBuy.BL.Hospital
{
	/// Small presentation helpers the hospital views share.
	public static class HospitalUi
	{
		/// Two initials from a name (the English one reads best on the brand tint), for a person with no photo.
		public static string Initials(string? name)
		{
			// The first LETTER of the first two words that have one: "Test — death cert" was "T—" before, and "د. أحمد" was "دأ".
			var p = (name ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries)
				.Where(w => w.Any(char.IsLetter) && !w.EndsWith('.'))
				.Select(w => w.First(char.IsLetter).ToString()).Take(2);
			return string.Concat(p).ToUpperInvariant();
		}

		/// A queue number as the patient's ticket reads it, "A-004" — the one spelling the desk, the clinics board, the
		/// kiosk and the waiting-room display share (it used to be formatted inline in each view, and the display showed a bare "4").
		public static string Ticket(int? n) => n is int v ? "A-" + v.ToString("000", System.Globalization.CultureInfo.InvariantCulture) : "—";

		/// A wall-clock minute of the day as "09:30".
		public static string Clock(int minute) => $"{minute / 60:00}:{minute % 60:00}";

		/// A lab value at its analyte's decimals, invariant (results are read left to right in both languages).
		public static string LabNum(decimal? v, int dec) => v?.ToString("F" + dec, System.Globalization.CultureInfo.InvariantCulture) ?? "";

		/// A reference range as "4.0 – 11.0", "≥ 40" or "≤ 200"; "—" when neither limit is set.
		public static string LabRange(decimal? lo, decimal? hi, int dec) =>
			lo != null && hi != null ? LabNum(lo, dec) + " – " + LabNum(hi, dec) : lo != null ? "≥ " + LabNum(lo, dec) : hi != null ? "≤ " + LabNum(hi, dec) : "—";
	}
}
