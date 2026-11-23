using System.Globalization;
using System.Text;

namespace CrossBuy.BL.Hospital
{
	public static class HospitalPortalAssistantIntent
	{
		public const string Emergency = "Emergency", NextVisit = "NextVisit", Results = "Results", Bills = "Bills", Medicines = "Medicines", Book = "Book", Clinical = "Clinical", Unknown = "Unknown";
	}

	/// One line of an answer: a resource key and its arguments (localized by the controller).
	public sealed record HospitalAssistantLine(string Key, object[] Args);
	public sealed record HospitalAssistantAnswer(string Intent, string TitleKey, IReadOnlyList<HospitalAssistantLine> Lines, string? Link, string? LinkKey);

	// ==========================================================================================
	// HOSPITAL H266 — THE PATIENT'S ASSISTANT on the portal. It answers questions about the patient's OWN file from the
	// pages the portal already shows — the next appointment, released results, what is owed, current medicines — read
	// through the same services and so with the same proxy scope (H251). It never gives medical advice, never guesses:
	//   · words of an emergency are answered first, always, with the emergency number;
	//   · a clinical question ("should I…", "is it dangerous…") is sent to the clinic as a portal message;
	//   · anything it does not recognise is refused with the questions it can answer (a default answer is a wrong one).
	// Arabic is read after folding letter forms (أ/إ/آ→ا, ة→ه, ى→ي, diacritics off) and matched as substrings, so the
	// clitics glued to a word ("وموعدي", "بنتيجتي") do not hide it. No model and no data leave the hospital.
	// ==========================================================================================
	public static class HospitalPortalAssistantRules
	{
		public const string EmergencyNumber = "112";

		public static string Fold(string? s)
		{
			if (string.IsNullOrWhiteSpace(s)) return "";
			var sb = new StringBuilder(s.Length);
			foreach (var c in s.Normalize(NormalizationForm.FormC))
			{
				if (c is >= 'ً' and <= 'ْ' or 'ـ') continue;   // harakat, tatweel
				if ((char.IsPunctuation(c) && c != '\'') || char.IsSymbol(c)) { sb.Append(' '); continue; }   // "next?" and "موعدي؟" read as words
				sb.Append(c switch { 'أ' or 'إ' or 'آ' => 'ا', 'ة' => 'ه', 'ى' => 'ي', 'ؤ' => 'و', 'ئ' => 'ي', _ => char.ToLowerInvariant(c) });
			}
			return " " + sb.ToString() + " ";
		}

		private static readonly string[] EmergencyWords = { "الم في الصدر", "الم بالصدر", "ضيق تنفس", "ضيق في التنفس", "لا استطيع التنفس", "نزيف", "اغماء", "فقد الوعي", "جلطه", "سكته", "تسمم", "انتحار", "اذيه نفسي",
			"chest pain", "can't breathe", "cannot breathe", "short of breath", "bleeding", "unconscious", "fainted", "stroke", "overdose", "suicid", "emergency", "طوارئ" };
		private static readonly string[] ClinicalWords = { "هل يجب", "هل اوقف", "هل اتوقف", "هل اخذ", "خطير", "طبيعي ام", "ماذا افعل", "شو اسوي", "اعراض", "جرعه", "اعاني", "عندي الم",
			"should i", "is it dangerous", "is it normal", "what should", "side effect", "dose", "symptom", "i have pain" };
		private static readonly (string Intent, string[] Words)[] Map =
		{
			(HospitalPortalAssistantIntent.Book, new[] { "احجز", "حجز موعد", "موعد جديد", " book", "new appointment", "make an appointment" }),
			(HospitalPortalAssistantIntent.NextVisit, new[] { "موعد", "مواعيد", "متي زيارتي", "زيارتي", "appointment", " visit", "when do i see", " next " }),
			(HospitalPortalAssistantIntent.Results, new[] { "نتيجه", "نتائج", "نتيجتي", "تحليل", "تحاليل", "اشعه", "مختبر", "result", " lab", " test", " scan", "x ray" }),
			(HospitalPortalAssistantIntent.Bills, new[] { "فاتوره", "فواتير", "كم علي", "مبلغ", "ادفع", "دفع", "حساب", " bill", " owe", " pay", "invoice", "balance" }),
			(HospitalPortalAssistantIntent.Medicines, new[] { "دوا", "ادوي", "علاج", "وصفه", "صرف", "تجديد", "medicine", "medication", " drug", "prescription", "refill", " pills" }),
		};

		public static string Intent(string? question)
		{
			var q = Fold(question);
			if (q.Trim().Length < 2) return HospitalPortalAssistantIntent.Unknown;
			// the word lists are folded the same way as the question (a hamza or a ta marbuta in a list never misses)
			bool Has(string w) { var f = Fold(w); return q.Contains(f[1..^1], StringComparison.Ordinal); }
			if (EmergencyWords.Any(Has)) return HospitalPortalAssistantIntent.Emergency;
			if (ClinicalWords.Any(Has)) return HospitalPortalAssistantIntent.Clinical;
			foreach (var (intent, words) in Map) if (words.Any(Has)) return intent;
			return HospitalPortalAssistantIntent.Unknown;
		}

		private static HospitalAssistantLine L(string key, params object[] args) => new(key, args);
		private static string Day(DateTime d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

		/// The answer to one intent from what the portal shows the account (already limited to its people and scope).
		public static HospitalAssistantAnswer Answer(string intent, IReadOnlyList<HospitalPortalVisit> visits, IReadOnlyList<HospitalPortalResult> results,
			IReadOnlyList<HospitalPortalBill> bills, IReadOnlyList<HospitalPortalMedicine> meds, bool arabic, DateTime nowLocal)
		{
			string N(HospitalPortalPerson p) => arabic ? p.Name : (string.IsNullOrWhiteSpace(p.NameEn) ? p.Name : p.NameEn);
			string B(string ar, string en) => arabic || string.IsNullOrWhiteSpace(en) ? ar : en;
			switch (intent)
			{
				case HospitalPortalAssistantIntent.Emergency:
					return new(intent, "This may be an emergency", new[] { L("Call {0} now, or go to the nearest emergency department. Do not wait for a reply here.", EmergencyNumber) }, null, null);
				case HospitalPortalAssistantIntent.Clinical:
					return new(intent, "This is a question for your doctor", new[] { L("I cannot give medical advice. Send your question to the clinic and the care team will answer.") }, "Messages", "Message the clinic");
				case HospitalPortalAssistantIntent.Book:
					return new(intent, "Book an appointment", new[] { L("Choose the clinic, the doctor and a free time.") }, "Book", "Book an appointment");
				case HospitalPortalAssistantIntent.NextVisit:
				{
					var next = visits.Where(v => v.Status is "Booked" or "Arrived" && v.Date.Date.AddMinutes(v.StartMinute) >= nowLocal.AddMinutes(-30))
						.OrderBy(v => v.Date).ThenBy(v => v.StartMinute).Take(3).ToList();
					if (next.Count == 0) return new(intent, "Your appointments", new[] { L("You have no appointment booked.") }, "Book", "Book an appointment");
					return new(intent, "Your next appointments", next.Select(v => L("{0} — {1} at {2} with {3} ({4})", N(v.Person), Day(v.Date), HospitalUi.Clock(v.StartMinute), B(v.Doctor, v.DoctorEn), B(v.Clinic, v.ClinicEn))).ToList(), "Appointments", "My appointments");
				}
				case HospitalPortalAssistantIntent.Results:
				{
					var rel = results.Where(r => r.Released).OrderByDescending(r => r.At).Take(3).ToList();
					var waiting = results.Count(r => !r.Released);
					var lines = rel.Select(r => L(r.Abnormal ? "{0}: {1} of {2} — some values are outside the normal range; your doctor will explain them" : "{0}: {1} of {2} — within the normal range",
						N(r.Person), B(r.Name, r.NameEn), Day(r.At))).ToList();
					if (waiting > 0) lines.Add(L("{0} result(s) are with your doctor and will appear once released.", waiting));
					if (lines.Count == 0) lines.Add(L("No result has been released to you yet."));
					return new(intent, "Your results", lines, "Results", "My results");
				}
				case HospitalPortalAssistantIntent.Bills:
				{
					var due = bills.Where(b => b.Status != "Open" && b.Due > 0).ToList();
					if (due.Count == 0) return new(intent, "Your bills", new[] { L("Nothing is due on your bills.") }, "Bills", "My bills");
					var lines = due.GroupBy(b => b.Currency.Code).Select(g => L("Due: {0} {1} on {2} bill(s)", g.Sum(b => b.Due).ToString("F" + g.First().Currency.Decimals, CultureInfo.InvariantCulture), g.Key, g.Count())).ToList();
					return new(intent, "Your bills", lines, "Bills", "My bills");
				}
				case HospitalPortalAssistantIntent.Medicines:
				{
					var list = meds.OrderByDescending(m => m.PrescribedAt).Take(5).ToList();
					if (list.Count == 0) return new(intent, "Your medicines", new[] { L("No current medicine is on your file.") }, "Medicines", "My medicines");
					return new(intent, "Your medicines", list.Select(m => L("{0} — {1}", m.Drug, m.Instructions ?? "")).ToList(), "Medicines", "My medicines");
				}
				default:
					return new(HospitalPortalAssistantIntent.Unknown, "I did not understand", new[] { L("I can tell you your next appointment, your released results, what you owe and your medicines. For anything else, message the clinic.") }, "Messages", "Message the clinic");
			}
		}
	}
}
