namespace CrossBuy.BL.Hospital
{
	// ==========================================================================================
	// HOSPITAL H142 — what the desk typed in the one search, carried into the registration window, so a patient who was not
	// found is not typed twice: a mobile (it starts with + or 0) or an ID number (digits, Arabic-Indic too) or a name (in
	// Arabic or in Latin letters, each into its own field).
	// ==========================================================================================
	public sealed record HospitalDeskRegisterPrefill(string? Name, string? NameEn, string? NationalId, string? Phone);

	public static class HospitalDeskRegisterRules
	{
		public static HospitalDeskRegisterPrefill Prefill(string? q)
		{
			q = q?.Trim();
			if (string.IsNullOrEmpty(q) || q.Length > 200) return new(null, null, null, null);
			var digits = new string(q.Where(char.IsDigit).Select(c => (char)('0' + (int)char.GetNumericValue(c))).ToArray());   // ٠١٢ → 012: the file keeps Latin digits
			bool numeric = digits.Length > 0 && q.All(c => char.IsDigit(c) || c is '+' or ' ' or '-');
			if (numeric)
			{
				bool phone = q.StartsWith('+') || digits.StartsWith('0');
				if (phone && digits.Length is >= 6 and <= 15) return new(null, null, null, (q.StartsWith('+') ? "+" : "") + digits);
				if (digits.Length is >= 5 and <= 20) return new(null, null, digits, null);
				return new(null, null, null, null);
			}
			bool arabic = q.Any(c => c is >= '؀' and <= 'ۿ');
			return arabic ? new(q, null, null, null) : new(null, q, null, null);
		}
	}
}
