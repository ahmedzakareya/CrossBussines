using System.Globalization;

namespace CrossBuy.BL.Hospital
{
	/// <summary>
	/// The chronic conditions a file lists, in the screen's language (owner 2026-11-25: the consult's "At a glance"
	/// showed «سكري من النوع الثاني» on an English screen).
	///
	/// HospitalPatients.ChronicConditions is free text, one condition per line, usually typed in Arabic. This turns a
	/// line into English/French ONLY when the WHOLE line (after HospitalAllergyText.Fold) is a known condition, and with
	/// a plain translation of exactly what was written. It never borrows a diagnosis-catalogue name: the nearest ICD
	/// entry adds clinical detail the doctor did not write ("Type 2 diabetes" → "... without complications"). A line it
	/// does not know is shown as typed.
	/// </summary>
	public static class HospitalConditionText
	{
		private static readonly (string Ar, string En, string Fr)[] Known =
		{
			("سكري", "Diabetes", "Diabète"),
			("داء السكري", "Diabetes", "Diabète"),
			("سكري من النوع الأول", "Type 1 diabetes", "Diabète de type 1"),
			("سكري النوع الأول", "Type 1 diabetes", "Diabète de type 1"),
			("سكري من النوع الثاني", "Type 2 diabetes", "Diabète de type 2"),
			("سكري النوع الثاني", "Type 2 diabetes", "Diabète de type 2"),
			("ارتفاع ضغط الدم", "Hypertension", "Hypertension"),
			("ضغط الدم المرتفع", "Hypertension", "Hypertension"),
			("ربو", "Asthma", "Asthme"),
			("الربو الشعبي", "Asthma", "Asthme"),
			("مرض الانسداد الرئوي المزمن", "COPD", "BPCO"),
			("قصور القلب", "Heart failure", "Insuffisance cardiaque"),
			("فشل القلب", "Heart failure", "Insuffisance cardiaque"),
			("مرض الشريان التاجي", "Coronary artery disease", "Maladie coronarienne"),
			("الرجفان الأذيني", "Atrial fibrillation", "Fibrillation auriculaire"),
			("مرض الكلى المزمن", "Chronic kidney disease", "Maladie rénale chronique"),
			("قصور كلوي مزمن", "Chronic kidney failure", "Insuffisance rénale chronique"),
			("قصور الغدة الدرقية", "Hypothyroidism", "Hypothyroïdie"),
			("فرط نشاط الغدة الدرقية", "Hyperthyroidism", "Hyperthyroïdie"),
			("ارتفاع الكوليسترول", "High cholesterol", "Hypercholestérolémie"),
			("فقر الدم", "Anaemia", "Anémie"),
			("صرع", "Epilepsy", "Épilepsie"),
			("التهاب المفاصل الروماتويدي", "Rheumatoid arthritis", "Polyarthrite rhumatoïde"),
			("هشاشة العظام", "Osteoporosis", "Ostéoporose"),
			("نقرس", "Gout", "Goutte"),
			("تليف الكبد", "Liver cirrhosis", "Cirrhose du foie"),
			("اكتئاب", "Depression", "Dépression"),
			("السمنة", "Obesity", "Obésité"),
		};

		private static readonly Dictionary<string, (string En, string Fr)> ByFold =
			Known.GroupBy(k => HospitalAllergyText.Fold(k.Ar)).ToDictionary(g => g.Key, g => (g.First().En, g.First().Fr));

		/// One line in the current UI language; Arabic screens (and any line not known) get the line as written.
		public static string Display(string? line) => Display(line, CultureInfo.CurrentUICulture.TwoLetterISOLanguageName);

		public static string Display(string? line, string lang)
		{
			var s = (line ?? "").Trim();
			if (s.Length == 0 || lang == "ar") return s;
			return ByFold.TryGetValue(HospitalAllergyText.Fold(s), out var t) ? (lang == "fr" ? t.Fr : t.En) : s;
		}
	}
}
