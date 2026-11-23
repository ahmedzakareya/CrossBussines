namespace CrossBuy.BL.Hospital
{
	public sealed record HospitalInstrumentItem(string Ar, string En);
	public sealed record HospitalInstrument(string Code, string Ar, string En, string StemAr, string StemEn, IReadOnlyList<HospitalInstrumentItem> Items,
		IReadOnlyList<(string Ar, string En)> Options, IReadOnlyList<(int Min, string Severity)> Bands);

	// ==========================================================================================
	// HOSPITAL H250 — the instruments and their scoring, free of the database (the published items, in English and in
	// Arabic; scoring as published):
	//   · PHQ-9 (depression, Kroenke 2001): 9 items 0–3, total 0–27; 0–4 minimal, 5–9 mild, 10–14 moderate,
	//     15–19 moderately severe, 20–27 severe. Item 9 (thoughts of self-harm) above 0 is URGENT whatever the total.
	//   · GAD-7 (anxiety, Spitzer 2006): 7 items 0–3, total 0–21; 0–4 minimal, 5–9 mild, 10–14 moderate, 15–21 severe.
	//   · AUDIT-C (alcohol, Bush 1998): 3 items 0–4, total 0–12; positive at 4 or more (3 or more for women).
	// ==========================================================================================
	public static class HospitalQuestionnaireRules
	{
		private static readonly (string, string)[] Freq =
		{
			("أبدًا", "Not at all"), ("عدة أيام", "Several days"), ("أكثر من نصف الأيام", "More than half the days"), ("تقريبًا كل يوم", "Nearly every day"),
		};

		public static readonly IReadOnlyList<HospitalInstrument> All = new[]
		{
			new HospitalInstrument("PHQ9", "استبيان صحة المريض (PHQ-9)", "Patient Health Questionnaire (PHQ-9)",
				"خلال الأسبوعين الماضيين، كم مرة انزعجت من أي من المشكلات التالية؟", "Over the last 2 weeks, how often have you been bothered by any of the following problems?",
				new HospitalInstrumentItem[]
				{
					new("قلة الاهتمام أو المتعة في القيام بالأشياء", "Little interest or pleasure in doing things"),
					new("الشعور بالإحباط أو الاكتئاب أو اليأس", "Feeling down, depressed, or hopeless"),
					new("صعوبة في النوم أو الاستمرار فيه أو النوم أكثر من اللازم", "Trouble falling or staying asleep, or sleeping too much"),
					new("الشعور بالتعب أو قلة الطاقة", "Feeling tired or having little energy"),
					new("ضعف الشهية أو الإفراط في الأكل", "Poor appetite or overeating"),
					new("الشعور بالسوء تجاه نفسك أو أنك فاشل أو خذلت نفسك أو أسرتك", "Feeling bad about yourself — or that you are a failure or have let yourself or your family down"),
					new("صعوبة التركيز في أشياء مثل قراءة الجريدة أو مشاهدة التلفاز", "Trouble concentrating on things, such as reading the newspaper or watching television"),
					new("البطء في الحركة أو الكلام بشكل يلاحظه الآخرون، أو العكس: التململ وكثرة الحركة", "Moving or speaking so slowly that other people could have noticed, or the opposite — being so fidgety or restless that you have been moving around a lot more than usual"),
					new("أفكار بأنك ستكون أفضل حالًا لو مت، أو أفكار بإيذاء نفسك", "Thoughts that you would be better off dead, or of hurting yourself in some way"),
				}, Freq, new[] { (0, "Minimal"), (5, "Mild"), (10, "Moderate"), (15, "ModeratelySevere"), (20, "Severe") }),
			new HospitalInstrument("GAD7", "مقياس القلق العام (GAD-7)", "Generalized Anxiety Disorder scale (GAD-7)",
				"خلال الأسبوعين الماضيين، كم مرة انزعجت من المشكلات التالية؟", "Over the last 2 weeks, how often have you been bothered by the following problems?",
				new HospitalInstrumentItem[]
				{
					new("الشعور بالتوتر أو القلق أو العصبية", "Feeling nervous, anxious, or on edge"),
					new("عدم القدرة على إيقاف القلق أو التحكم فيه", "Not being able to stop or control worrying"),
					new("القلق الزائد بشأن أمور مختلفة", "Worrying too much about different things"),
					new("صعوبة الاسترخاء", "Trouble relaxing"),
					new("التململ لدرجة صعوبة البقاء ساكنًا", "Being so restless that it is hard to sit still"),
					new("سرعة الانزعاج أو الغضب", "Becoming easily annoyed or irritable"),
					new("الشعور بالخوف وكأن شيئًا فظيعًا قد يحدث", "Feeling afraid, as if something awful might happen"),
				}, Freq, new[] { (0, "Minimal"), (5, "Mild"), (10, "Moderate"), (15, "Severe") }),
			new HospitalInstrument("AUDITC", "اختبار تحديد اضطرابات تعاطي الكحول (AUDIT-C)", "Alcohol Use Disorders Identification Test (AUDIT-C)",
				"عن استهلاكك خلال السنة الماضية", "About your use during the past year",
				new HospitalInstrumentItem[]
				{
					new("كم مرة تتناول مشروبًا يحتوي على الكحول؟ (أبدًا · شهريًا أو أقل · 2–4 مرات شهريًا · 2–3 مرات أسبوعيًا · 4 مرات أو أكثر أسبوعيًا)", "How often do you have a drink containing alcohol? (Never · Monthly or less · 2–4 times a month · 2–3 times a week · 4 or more times a week)"),
					new("كم مشروبًا تتناول في اليوم المعتاد؟ (1–2 · 3–4 · 5–6 · 7–9 · 10 أو أكثر)", "How many drinks do you have on a typical day when drinking? (1–2 · 3–4 · 5–6 · 7–9 · 10 or more)"),
					new("كم مرة تتناول ستة مشروبات أو أكثر في مناسبة واحدة؟ (أبدًا · أقل من شهريًا · شهريًا · أسبوعيًا · يوميًا تقريبًا)", "How often do you have six or more drinks on one occasion? (Never · Less than monthly · Monthly · Weekly · Daily or almost daily)"),
				}, new[] { ("0", "0"), ("1", "1"), ("2", "2"), ("3", "3"), ("4", "4") }, new[] { (0, "Negative"), (4, "Positive") }),
		};

		public static HospitalInstrument? Find(string? code) => All.FirstOrDefault(i => i.Code == code);

		public static string? AnswerRefusal(HospitalInstrument ins, IReadOnlyList<int>? answers)
		{
			if (answers == null || answers.Count != ins.Items.Count) return "Answer every question";
			return answers.Any(a => a < 0 || a >= ins.Options.Count) ? "Answer every question" : null;
		}

		/// The total, its severity band, and whether the clinic must look at it now.
		public static (int Score, string Severity, bool Urgent) Score(HospitalInstrument ins, IReadOnlyList<int> answers, bool female = false)
		{
			int total = answers.Sum();
			var bands = ins.Code == "AUDITC" && female ? new[] { (0, "Negative"), (3, "Positive") } : ins.Bands.ToArray();
			string sev = bands.Where(b => total >= b.Item1).Last().Item2;
			bool urgent = ins.Code == "PHQ9" && (answers[8] > 0 || total >= 20) || ins.Code == "GAD7" && total >= 15;
			return (total, sev, urgent);
		}
	}
}
