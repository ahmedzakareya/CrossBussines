using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace CrossBuy.BL.Hospital
{
	public sealed record HospitalScribeDraft(string? ChiefComplaint, string History, string Examination, string Plan,
		int? BpSystolic, int? BpDiastolic, int? HeartRate, decimal? TemperatureC, int? SpO2, decimal? WeightKg);

	// ==========================================================================================
	// HOSPITAL H254 — AMBIENT DOCUMENTATION, the drafting half (free of the database and of any model): the transcript of
	// the visit, in Arabic or English or both, is cut into sentences and each sentence goes to the SOAP section its cue
	// words point at — complaint ("يشكو", "complains of"), examination ("بالفحص", "on examination"), plan ("الخطة",
	// "سنطلب", "prescribe", "follow up") — the rest is history. Vital signs said aloud ("الضغط 130 على 85", "pulse 92",
	// "الحرارة 38.5", "saturation 96") are read into their fields. Arabic-Indic digits are read as digits.
	// The draft only FILLS THE FORM: the doctor reads, corrects and saves the note as always. Nothing is saved from here.
	// ==========================================================================================
	public static class HospitalScribeRules
	{
		private static readonly string[] Complaint = { "يشكو", "تشكو", "يعاني", "تعاني", "شكوى", "الشكوى", "جاء بسبب", "جاءت بسبب", "complain", "presents with", "presenting", "came in with", "chief complaint" };
		private static readonly string[] Exam = { "بالفحص", "الفحص", "فحص", "بالسماعة", "سماع", "البطن لين", "الصدر", "on examination", "examination", "exam", "auscultation", "tender", "chest is", "abdomen is" };
		private static readonly string[] Plan = { "الخطة", "خطة", "سنطلب", "نطلب", "نبدأ", "وصفت", "سأصف", "نصف", "تحليل", "أشعة", "متابعة", "مراجعة بعد", "يراجع", "plan", "prescribe", "start", "order", "follow up", "follow-up", "review in", "refer" };

		private static readonly Regex Reading = new(@"(?:ضغط|نبض|حرارة|تشبع|وزن|blood pressure|\bbp\b|pulse|heart rate|temperature|\btemp\b|saturation|\bsats\b|spo2|weight)\D{0,15}\d", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

		public static string LatinDigits(string s)
		{
			var b = new StringBuilder(s.Length);
			foreach (var c in s) b.Append(c is >= '٠' and <= '٩' ? (char)('0' + (c - '٠')) : c is >= '۰' and <= '۹' ? (char)('0' + (c - '۰')) : c == '٫' ? '.' : c);
			return b.ToString();
		}

		private static string Section(string sentence)
		{
			var s = sentence.ToLowerInvariant();
			if (Plan.Any(w => s.Contains(w))) return "P";
			if (Exam.Any(w => s.Contains(w))) return "O";
			if (Complaint.Any(w => s.Contains(w))) return "C";
			// a sentence of readings said aloud is examination, not history (live test with a microphone, 2026-11-23)
			if (Reading.IsMatch(s)) return "O";
			return "S";
		}

		private static int? Int(Match m, int g) => m.Success && int.TryParse(m.Groups[g].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null;
		private static decimal? Dec(Match m, int g) => m.Success && decimal.TryParse(m.Groups[g].Value.Replace(',', '.'), NumberStyles.Number, CultureInfo.InvariantCulture, out var v) ? v : null;

		public static HospitalScribeDraft Draft(string? transcript)
		{
			var text = LatinDigits(transcript ?? "").Trim();
			var sentences = Regex.Split(text, @"(?<=[\.!\?؟،؛\n])\s+|\n+").Select(x => x.Trim(' ', '.', '،', '؛')).Where(x => x.Length > 1).ToList();
			var c = new List<string>(); var sub = new List<string>(); var obj = new List<string>(); var plan = new List<string>();
			foreach (var s in sentences)
				switch (Section(s)) { case "C": c.Add(s); break; case "O": obj.Add(s); break; case "P": plan.Add(s); break; default: sub.Add(s); break; }
			string? chief = c.FirstOrDefault() ?? sub.FirstOrDefault();
			// the complaint sentences other than the one heading the note are history
			var history = c.Skip(1).Concat(chief != null && c.Count == 0 ? sub.Skip(1) : sub).ToList();

			const RegexOptions O = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
			var bp = Regex.Match(text, @"(?:ضغط|الضغط|blood pressure|\bbp\b)\D{0,15}?(\d{2,3})\s*(?:/|على|over)\s*(\d{2,3})", O);
			var hr = Regex.Match(text, @"(?:نبض|النبض|pulse|heart rate)\D{0,12}?(\d{2,3})", O);
			var tp = Regex.Match(text, @"(?:حرارة|الحرارة|temperature|\btemp\b)\D{0,12}?(\d{2}(?:[\.,]\d)?)", O);
			var sp = Regex.Match(text, @"(?:تشبع|الأكسجين|الاكسجين|saturation|\bsats\b|spo2)\D{0,15}?(\d{2,3})", O);
			var wt = Regex.Match(text, @"(?:وزن|الوزن|weight)\D{0,12}?(\d{1,3}(?:[\.,]\d)?)", O);

			int? Within(int? v, int lo, int hi) => v is int x && x >= lo && x <= hi ? x : null;
			decimal? WithinD(decimal? v, decimal lo, decimal hi) => v is decimal x && x >= lo && x <= hi ? x : null;
			return new HospitalScribeDraft(chief, string.Join(". ", history), string.Join(". ", obj), string.Join(". ", plan),
				Within(Int(bp, 1), 50, 260), Within(Int(bp, 2), 30, 160), Within(Int(hr, 1), 20, 250), WithinD(Dec(tp, 1), 30m, 45m), Within(Int(sp, 1), 50, 100), WithinD(Dec(wt, 1), 0.5m, 400m));
		}
	}
}
