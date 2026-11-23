namespace CrossBuy.BL.Hospital
{
	// ==========================================================================================
	// HOSPITAL H261 — LOINC on laboratory analytes. A LOINC code is digits, a hyphen and a mod-10 check digit; a code
	// whose check digit is wrong is a typing error and is refused. The STARTER map covers the standard panels' analytes
	// (by their local code and unit as the lab seed creates them); the laboratory director confirms or changes each.
	// The code travels next to the local one: HL7 ORU OBX-3 ("code^name^LN^local^name^L") and FHIR Observation.code.
	// ==========================================================================================
	public static class HospitalLoinc
	{
		/// The LOINC mod-10 check digit of the number part ("2345" → 7).
		public static int CheckDigit(string number)
		{
			var d = number.Select(c => c - '0').ToList();
			string odd = "", even = "";
			for (int i = d.Count - 1, pos = 1; i >= 0; i--, pos++) { if (pos % 2 == 1) odd = d[i] + odd; else even = d[i] + even; }
			var doubled = (long.Parse(odd == "" ? "0" : odd) * 2).ToString(System.Globalization.CultureInfo.InvariantCulture);
			int sum = (even + doubled).Sum(c => c - '0');
			return (10 - sum % 10) % 10;
		}

		public static bool Valid(string? code)
		{
			if (string.IsNullOrWhiteSpace(code)) return false;
			var p = code.Trim().Split('-');
			if (p.Length != 2 || p[0].Length is < 1 or > 7 || p[1].Length != 1 || !p[0].All(char.IsAsciiDigit) || !char.IsAsciiDigit(p[1][0])) return false;
			return CheckDigit(p[0]) == p[1][0] - '0';
		}

		/// Null when the entry is empty (no LOINC) or valid; otherwise why it is refused.
		public static string? Refusal(string? code) => string.IsNullOrWhiteSpace(code) || Valid(code) ? null : "This is not a LOINC code (digits, a hyphen and its check digit)";

		/// Local analyte code → LOINC, for the analytes the standard lab seed creates (in those units).
		public static readonly IReadOnlyDictionary<string, string> Starter = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
		{
			["WBC"] = "6690-2", ["RBC"] = "789-8", ["HGB"] = "718-7", ["HCT"] = "4544-3", ["PLT"] = "777-3", ["NEUT"] = "770-8",
			["HBA1C"] = "4548-4", ["GLU"] = "1558-6", ["CHOL"] = "2093-3", ["TG"] = "2571-8", ["HDL"] = "2085-9", ["LDL"] = "13457-7",
			["UREA"] = "3091-6", ["CREA"] = "2160-0", ["K"] = "2823-3", ["NA"] = "2951-2", ["ALT"] = "1742-6", ["AST"] = "1920-8",
			["TBIL"] = "1975-2", ["ALB"] = "1751-7", ["TSH"] = "3016-3", ["PH"] = "5803-2", ["PRO"] = "5804-0", ["GLUU"] = "5792-7",
			["CRP"] = "1988-5", ["TROP"] = "10839-9", ["VITD"] = "62292-8",
		};

		/// HL7 v2 OBX-3: LOINC first with the local code as the alternate, or the local code alone.
		public static string Obx3(string local, string name, string? loinc, Func<string?, string> esc) =>
			string.IsNullOrWhiteSpace(loinc) ? esc(local) + "^" + esc(name) : esc(loinc) + "^" + esc(name) + "^LN^" + esc(local) + "^" + esc(name) + "^L";
	}
}
