using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Hospital;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL.Hospital
{
	public sealed record HospitalPackConcept(string Code, string System, string Type, string Ar, string En, string? Unit = null, int Decimals = 0,
		decimal? NormalLow = null, decimal? NormalHigh = null, string? Choices = null, string? Calculator = null, string? Inputs = null, string? Lab = null);
	public sealed record HospitalPackField(string Concept, string SectionAr, string SectionEn, bool Required = false, string? Source = null, int? Lookback = null,
		decimal? AlertAbove = null, decimal? AlertBelow = null, string? AlertAr = null, string? AlertEn = null);
	public sealed record HospitalPackTemplate(string Code, string Dept, string Ar, string En, IReadOnlyList<HospitalPackField> Fields);

	// ==========================================================================================
	// HOSPITAL H268 — THE SPECIALTY PACK: the clinics that had no structured form get one, built on the template engine
	// (H85/H98) exactly as an administrator would in the designer — concepts, a template with its fields and alerts, then
	// published — so every form is editable and versioned like the others. Validated instruments only:
	//   Neurology       modified Rankin scale, MIDAS headache disability, seizures per month, pain
	//   Urology         IPSS (7 items) with its quality-of-life item, post-void residual, PSA
	//   Gastroenterology Bristol stool form, reflux days per week, weight, alarm features
	//   Pulmonology     CAT (8 items), mMRC dyspnoea, SpO2, FEV1 % predicted, exacerbations a year
	//   Rheumatology    CDAI (28 tender + 28 swollen joints, patient and evaluator global)
	//   Nephrology      creatinine (last lab), eGFR (CKD-EPI 2021), urine ACR, potassium, blood pressure
	//   General surgery post-operative wound review: wound, pain, temperature, drain output, Clavien-Dindo grade
	// A concept already in the dictionary (by its code) is reused, never copied; a template whose code exists is left as it
	// is. Thresholds are the instruments' published bands, to be confirmed by the clinical lead.
	// ==========================================================================================
	public static class HospitalSpecialtyPack
	{
		private static HospitalPackConcept Item(string code, string ar, string en, decimal max) => new(code, HospitalCodeSystem.Local, HospitalConceptType.Number, ar, en, "/" + max.ToString(System.Globalization.CultureInfo.InvariantCulture), 0, 0, max);

		public static readonly IReadOnlyList<(string Code, string Ar, string En)> Departments = new[]
		{
			("NEURO", "المخ والأعصاب", "Neurology"), ("URO", "المسالك البولية", "Urology"), ("GI", "الجهاز الهضمي", "Gastroenterology"),
			("PULM", "الصدرية", "Pulmonology"), ("RHEUM", "الروماتيزم", "Rheumatology"), ("NEPH", "الكلى", "Nephrology"),
		};

		public static readonly IReadOnlyList<HospitalPackConcept> Concepts = new HospitalPackConcept[]
		{
			// shared measurements (reused when present)
			new("72514-3", HospitalCodeSystem.Loinc, HospitalConceptType.Number, "شدة الألم", "Pain severity", "/10", 0, 0, 3),
			new("29463-7", HospitalCodeSystem.Loinc, HospitalConceptType.Number, "الوزن", "Body weight", "kg", 1),
			new("85354-9", HospitalCodeSystem.Loinc, HospitalConceptType.Panel, "ضغط الدم", "Blood pressure", "mmHg"),
			new("2160-0", HospitalCodeSystem.Loinc, HospitalConceptType.Number, "الكرياتينين", "Creatinine", "mg/dL", 2, 0.6m, 1.3m, Lab: "CREA"),
			new("62238-1", HospitalCodeSystem.Loinc, HospitalConceptType.Calculated, "معدل الترشيح الكبيبي", "eGFR (CKD-EPI 2021)", "mL/min/1.73m²", 0, Calculator: HospitalConceptCalculator.EgfrCkdEpi2021, Inputs: "2160-0"),
			new("9318-7", HospitalCodeSystem.Loinc, HospitalConceptType.Number, "نسبة الألبومين إلى الكرياتينين في البول", "Urine albumin/creatinine ratio", "mg/g", 0, 0, 30),
			new("2823-3", HospitalCodeSystem.Loinc, HospitalConceptType.Number, "البوتاسيوم", "Potassium", "mmol/L", 1, 3.5m, 5.1m, Lab: "K"),
			new("8310-5", HospitalCodeSystem.Loinc, HospitalConceptType.Number, "درجة الحرارة", "Body temperature", "°C", 1, 36.0m, 37.9m),
			new("59408-5", HospitalCodeSystem.Loinc, HospitalConceptType.Number, "تشبع الأكسجين", "Oxygen saturation", "%", 0, 94, 100),
			new("2857-1", HospitalCodeSystem.Loinc, HospitalConceptType.Number, "مستضد البروستاتا النوعي", "Prostate-specific antigen", "ng/mL", 2, 0, 4),
			// neurology
			new("LOCAL-MRS", HospitalCodeSystem.Local, HospitalConceptType.Choice, "مقياس رانكن المعدّل", "Modified Rankin scale",
				Choices: "0|لا أعراض|No symptoms\n1|لا إعاقة ملحوظة|No significant disability\n2|إعاقة خفيفة|Slight disability\n3|إعاقة متوسطة، يمشي دون مساعدة|Moderate disability, walks unassisted\n4|إعاقة متوسطة إلى شديدة|Moderately severe disability\n5|إعاقة شديدة، طريح الفراش|Severe disability, bedridden\n6|متوفى|Dead"),
			new("LOCAL-SEIZ-M", HospitalCodeSystem.Local, HospitalConceptType.Number, "عدد النوبات في الشهر", "Seizures per month", "/month", 0, 0, 0),
			Item("MIDAS-Q1", "أيام التغيب عن العمل أو الدراسة بسبب الصداع", "Days of work or school missed", 92),
			Item("MIDAS-Q2", "أيام انخفضت فيها الإنتاجية إلى النصف", "Days productivity halved", 92),
			Item("MIDAS-Q3", "أيام لم يُنجز فيها العمل المنزلي", "Days household work not done", 92),
			Item("MIDAS-Q4", "أيام انخفض فيها إنجاز العمل المنزلي إلى النصف", "Days household productivity halved", 92),
			Item("MIDAS-Q5", "أيام فاتته فيها أنشطة عائلية أو اجتماعية", "Days family or social activities missed", 92),
			new("LOCAL-MIDAS", HospitalCodeSystem.Local, HospitalConceptType.Score, "مقياس MIDAS لإعاقة الصداع", "MIDAS headache disability", "/460", 0,
				Choices: "0|إعاقة قليلة أو معدومة|Little or no disability\n6|إعاقة خفيفة|Mild disability\n11|إعاقة متوسطة|Moderate disability\n21|إعاقة شديدة|Severe disability",
				Calculator: HospitalConceptCalculator.Sum, Inputs: "MIDAS-Q1, MIDAS-Q2, MIDAS-Q3, MIDAS-Q4, MIDAS-Q5"),
			// urology
			Item("IPSS-Q1", "الإفراغ غير الكامل", "Incomplete emptying", 5), Item("IPSS-Q2", "التبول المتكرر", "Frequency", 5),
			Item("IPSS-Q3", "التقطع", "Intermittency", 5), Item("IPSS-Q4", "الإلحاح", "Urgency", 5), Item("IPSS-Q5", "ضعف التدفق", "Weak stream", 5),
			Item("IPSS-Q6", "الإجهاد عند التبول", "Straining", 5), Item("IPSS-Q7", "التبول الليلي", "Nocturia", 5),
			new("LOCAL-IPSS", HospitalCodeSystem.Local, HospitalConceptType.Score, "مقياس أعراض البروستاتا الدولي IPSS", "International Prostate Symptom Score", "/35", 0,
				Choices: "0|أعراض خفيفة|Mild symptoms\n8|أعراض متوسطة|Moderate symptoms\n20|أعراض شديدة|Severe symptoms",
				Calculator: HospitalConceptCalculator.Sum, Inputs: "IPSS-Q1, IPSS-Q2, IPSS-Q3, IPSS-Q4, IPSS-Q5, IPSS-Q6, IPSS-Q7"),
			Item("IPSS-QOL", "جودة الحياة بسبب الأعراض البولية", "Quality of life due to urinary symptoms", 6),
			new("LOCAL-PVR", HospitalCodeSystem.Local, HospitalConceptType.Number, "البول المتبقي بعد الإفراغ", "Post-void residual", "mL", 0, 0, 100),
			// gastroenterology
			new("LOCAL-BRISTOL", HospitalCodeSystem.Local, HospitalConceptType.Choice, "مقياس بريستول للبراز", "Bristol stool form",
				Choices: "1|كتل صلبة منفصلة|Separate hard lumps\n2|متكتل على شكل نقانق|Lumpy, sausage-shaped\n3|نقانق بشقوق|Sausage with cracks\n4|ناعم أملس|Smooth and soft\n5|كتل طرية|Soft blobs\n6|قطع هشة طرية|Fluffy, mushy pieces\n7|مائي|Watery"),
			new("LOCAL-GERD-DAYS", HospitalCodeSystem.Local, HospitalConceptType.Number, "أيام الحرقة في الأسبوع", "Heartburn days a week", "/7", 0, 0, 1),
			new("LOCAL-GI-ALARM", HospitalCodeSystem.Local, HospitalConceptType.Choice, "علامات الإنذار", "Alarm features",
				Choices: "N|لا يوجد|None\nW|نقص وزن غير مبرر|Unexplained weight loss\nB|نزيف أو براز أسود|Bleeding or black stool\nD|صعوبة البلع|Difficulty swallowing\nA|فقر دم|Anaemia"),
			// pulmonology
			Item("CAT-Q1", "السعال", "Cough", 5), Item("CAT-Q2", "البلغم", "Phlegm", 5), Item("CAT-Q3", "ضيق الصدر", "Chest tightness", 5),
			Item("CAT-Q4", "ضيق النفس عند صعود الدرج", "Breathless on stairs", 5), Item("CAT-Q5", "تقييد الأنشطة المنزلية", "Activity limitation at home", 5),
			Item("CAT-Q6", "الثقة بالخروج من المنزل", "Confidence leaving home", 5), Item("CAT-Q7", "النوم", "Sleep", 5), Item("CAT-Q8", "الطاقة", "Energy", 5),
			new("LOCAL-CAT", HospitalCodeSystem.Local, HospitalConceptType.Score, "اختبار تقييم الانسداد الرئوي CAT", "COPD Assessment Test (CAT)", "/40", 0,
				Choices: "0|تأثير منخفض|Low impact\n10|تأثير متوسط|Medium impact\n21|تأثير مرتفع|High impact\n31|تأثير مرتفع جدًا|Very high impact",
				Calculator: HospitalConceptCalculator.Sum, Inputs: "CAT-Q1, CAT-Q2, CAT-Q3, CAT-Q4, CAT-Q5, CAT-Q6, CAT-Q7, CAT-Q8"),
			new("LOCAL-MMRC", HospitalCodeSystem.Local, HospitalConceptType.Choice, "مقياس ضيق النفس mMRC", "mMRC dyspnoea scale",
				Choices: "0|ضيق نفس عند الجهد الشديد فقط|Only with strenuous exercise\n1|عند الإسراع أو صعود منحدر|Hurrying or walking uphill\n2|يمشي أبطأ من أقرانه|Walks slower than peers\n3|يتوقف بعد 100 متر|Stops after 100 m\n4|لا يستطيع مغادرة المنزل|Too breathless to leave the house"),
			new("LOCAL-FEV1PCT", HospitalCodeSystem.Local, HospitalConceptType.Number, "حجم الزفير القسري في الثانية الأولى من المتوقع", "FEV1 % predicted", "%", 0, 80, 120),
			new("LOCAL-COPD-EXAC", HospitalCodeSystem.Local, HospitalConceptType.Number, "عدد النوبات الحادة خلال السنة", "Exacerbations in the past year", "/year", 0, 0, 1),
			// rheumatology
			new("LOCAL-TJC28", HospitalCodeSystem.Local, HospitalConceptType.Number, "عدد المفاصل المؤلمة (من 28)", "Tender joint count (28)", "/28", 0, 0, 28),
			new("LOCAL-SJC28", HospitalCodeSystem.Local, HospitalConceptType.Number, "عدد المفاصل المتورمة (من 28)", "Swollen joint count (28)", "/28", 0, 0, 28),
			new("LOCAL-PTGA", HospitalCodeSystem.Local, HospitalConceptType.Number, "تقييم المريض العام", "Patient global assessment", "/10", 1, 0, 10),
			new("LOCAL-EGA", HospitalCodeSystem.Local, HospitalConceptType.Number, "تقييم الطبيب العام", "Evaluator global assessment", "/10", 1, 0, 10),
			new("LOCAL-CDAI", HospitalCodeSystem.Local, HospitalConceptType.Score, "مؤشر نشاط المرض السريري CDAI", "Clinical Disease Activity Index (CDAI)", "/76", 1,
				Choices: "0|هدأة|Remission\n2.9|نشاط منخفض|Low activity\n10.1|نشاط متوسط|Moderate activity\n22.1|نشاط مرتفع|High activity",
				Calculator: HospitalConceptCalculator.Sum, Inputs: "LOCAL-TJC28, LOCAL-SJC28, LOCAL-PTGA, LOCAL-EGA"),
			// general surgery
			new("LOCAL-WOUND", HospitalCodeSystem.Local, HospitalConceptType.Choice, "حالة الجرح", "Wound",
				Choices: "C|نظيف ملتئم|Clean, healing\nE|احمرار حول الجرح|Erythema\nD|إفرازات|Discharge\nX|انفتاح الجرح|Dehiscence"),
			new("LOCAL-DRAIN", HospitalCodeSystem.Local, HospitalConceptType.Number, "ناتج المصرف خلال 24 ساعة", "Drain output in 24 h", "mL", 0, 0, 50),
			new("LOCAL-CLAVIEN", HospitalCodeSystem.Local, HospitalConceptType.Choice, "تصنيف كلافيان-ديندو للمضاعفات", "Clavien-Dindo complication grade",
				Choices: "0|لا مضاعفات|No complication\nI|انحراف دون علاج خاص|Deviation without specific treatment\nII|احتاج إلى أدوية|Needed drugs\nIIIa|تدخل دون تخدير عام|Intervention without general anaesthesia\nIIIb|تدخل تحت تخدير عام|Intervention under general anaesthesia\nIVa|قصور عضو واحد|Single-organ dysfunction\nIVb|قصور أعضاء متعددة|Multi-organ dysfunction\nV|وفاة|Death"),
		};

		private static HospitalPackField F(string c, string sar, string sen, bool req = false) => new(c, sar, sen, req);

		public static readonly IReadOnlyList<HospitalPackTemplate> Templates = new[]
		{
			new HospitalPackTemplate("NEURO-FU", "NEURO", "متابعة المخ والأعصاب", "Neurology follow-up", new[]
			{
				F("LOCAL-MRS", "الوظيفة", "Function", true),
				new HospitalPackField("LOCAL-SEIZ-M", "النوبات", "Seizures", AlertAbove: 3, AlertAr: "نوبات متكررة: راجع العلاج المضاد للصرع", AlertEn: "Frequent seizures: review the antiseizure treatment"),
				F("72514-3", "الصداع", "Headache"),
				F("MIDAS-Q1", "الصداع", "Headache"), F("MIDAS-Q2", "الصداع", "Headache"), F("MIDAS-Q3", "الصداع", "Headache"), F("MIDAS-Q4", "الصداع", "Headache"), F("MIDAS-Q5", "الصداع", "Headache"),
				new HospitalPackField("LOCAL-MIDAS", "الصداع", "Headache", AlertAbove: 20.9m, AlertAr: "إعاقة شديدة بسبب الصداع: فكّر في العلاج الوقائي", AlertEn: "Severe headache disability: consider preventive treatment"),
			}),
			new HospitalPackTemplate("URO-LUTS", "URO", "أعراض المسالك البولية السفلية", "Lower urinary tract symptoms", new[]
			{
				F("IPSS-Q1", "IPSS", "IPSS"), F("IPSS-Q2", "IPSS", "IPSS"), F("IPSS-Q3", "IPSS", "IPSS"), F("IPSS-Q4", "IPSS", "IPSS"), F("IPSS-Q5", "IPSS", "IPSS"), F("IPSS-Q6", "IPSS", "IPSS"), F("IPSS-Q7", "IPSS", "IPSS"),
				F("LOCAL-IPSS", "IPSS", "IPSS", true), F("IPSS-QOL", "IPSS", "IPSS"),
				new HospitalPackField("LOCAL-PVR", "الفحص", "Examination", AlertAbove: 200, AlertAr: "بول متبقٍّ كثير: احتمال احتباس", AlertEn: "Large residual: possible retention"),
				new HospitalPackField("2857-1", "الفحص", "Examination", AlertAbove: 4, AlertAr: "مستضد البروستاتا مرتفع: يحتاج متابعة", AlertEn: "Raised PSA: needs follow-up"),
			}),
			new HospitalPackTemplate("GI-FU", "GI", "متابعة الجهاز الهضمي", "Gastroenterology follow-up", new[]
			{
				F("LOCAL-BRISTOL", "الإخراج", "Bowel habit"), F("LOCAL-GERD-DAYS", "الارتجاع", "Reflux"),
				F("29463-7", "العلامات", "Signs"), F("LOCAL-GI-ALARM", "علامات الإنذار", "Alarm features", true),
			}),
			new HospitalPackTemplate("PULM-COPD", "PULM", "متابعة الانسداد الرئوي المزمن", "COPD follow-up", new[]
			{
				F("CAT-Q1", "CAT", "CAT"), F("CAT-Q2", "CAT", "CAT"), F("CAT-Q3", "CAT", "CAT"), F("CAT-Q4", "CAT", "CAT"),
				F("CAT-Q5", "CAT", "CAT"), F("CAT-Q6", "CAT", "CAT"), F("CAT-Q7", "CAT", "CAT"), F("CAT-Q8", "CAT", "CAT"),
				F("LOCAL-CAT", "CAT", "CAT", true), F("LOCAL-MMRC", "ضيق النفس", "Dyspnoea", true),
				new HospitalPackField("59408-5", "القياسات", "Measurements", AlertBelow: 90, AlertAr: "تشبع الأكسجين أقل من 90%: قيّم الحاجة إلى الأكسجين", AlertEn: "SpO2 below 90%: assess for oxygen"),
				F("LOCAL-FEV1PCT", "القياسات", "Measurements"),
				new HospitalPackField("LOCAL-COPD-EXAC", "النوبات", "Exacerbations", AlertAbove: 1, AlertAr: "نوبتان أو أكثر في السنة: فئة GOLD E", AlertEn: "Two or more a year: GOLD group E"),
			}),
			new HospitalPackTemplate("RHEUM-RA", "RHEUM", "متابعة التهاب المفاصل الروماتويدي", "Rheumatoid arthritis follow-up", new[]
			{
				F("LOCAL-TJC28", "المفاصل", "Joints", true), F("LOCAL-SJC28", "المفاصل", "Joints", true),
				F("LOCAL-PTGA", "التقييم العام", "Global assessment", true), F("LOCAL-EGA", "التقييم العام", "Global assessment", true),
				new HospitalPackField("LOCAL-CDAI", "نشاط المرض", "Disease activity", true, AlertAbove: 22, AlertAr: "نشاط مرتفع للمرض: راجع العلاج (هدف العلاج)", AlertEn: "High disease activity: review treatment (treat to target)"),
			}),
			new HospitalPackTemplate("NEPH-CKD", "NEPH", "متابعة مرض الكلى المزمن", "Chronic kidney disease follow-up", new[]
			{
				new HospitalPackField("2160-0", "وظائف الكلى", "Kidney function", true, HospitalValueSource.LastLab, 90),
				F("62238-1", "وظائف الكلى", "Kidney function"),
				new HospitalPackField("9318-7", "وظائف الكلى", "Kidney function", AlertAbove: 300, AlertAr: "بيلة ألبومينية شديدة (A3)", AlertEn: "Severely increased albuminuria (A3)"),
				new HospitalPackField("2823-3", "الأملاح", "Electrolytes", false, HospitalValueSource.LastLab, 30, AlertAbove: 5.5m, AlertAr: "البوتاسيوم مرتفع: راجع الأدوية والغذاء", AlertEn: "High potassium: review medicines and diet"),
				F("85354-9", "العلامات", "Signs", true),
			}),
			new HospitalPackTemplate("SURG-PO", "SURG", "مراجعة ما بعد العملية", "Post-operative review", new[]
			{
				F("LOCAL-WOUND", "الجرح", "Wound", true), F("72514-3", "الألم", "Pain"),
				new HospitalPackField("8310-5", "العلامات", "Signs", AlertAbove: 38, AlertAr: "حرارة بعد العملية: ابحث عن عدوى", AlertEn: "Post-operative fever: look for infection"),
				F("LOCAL-DRAIN", "المصرف", "Drain"), F("LOCAL-CLAVIEN", "المضاعفات", "Complications", true),
			}),
		};
	}

	public sealed record HospitalPackResult(int Departments, int Concepts, int Templates, int Skipped);

	/// Lays the pack in through the content service (the designer's own path) — idempotent: what exists is kept.
	public sealed class HospitalSpecialtyPackService
	{
		private readonly CrossDbContext _db;
		private readonly HospitalClinicalContentService _content;
		public HospitalSpecialtyPackService(CrossDbContext db, HospitalClinicalContentService content) { _db = db; _content = content; }

		public async Task<(HospitalResult Result, HospitalPackResult? Done)> SeedAsync(BusinessContext ctx, int? employeeId, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await _content.CanManageAsync(ctx, ct)) return (HospitalResult.Fail("forbidden"), null);
			int deps = 0, concepts = 0, templates = 0, skipped = 0;
			var have = await _db.HospitalDepartments.Where(d => d.CompanyId == ctx.CompanyId).Select(d => d.Code).ToListAsync(ct);
			int order = await _db.HospitalDepartments.Where(d => d.CompanyId == ctx.CompanyId).MaxAsync(d => (int?)d.SortOrder, ct) ?? 0;
			foreach (var (code, ar, en) in HospitalSpecialtyPack.Departments.Where(d => !have.Contains(d.Code)))
			{
				order += 10; deps++;
				_db.HospitalDepartments.Add(new HospitalDepartment { CompanyId = ctx.CompanyId, Code = code, Name = ar, NameEn = en, SortOrder = order, CreatedAt = nowUtc, CreatedByEmployeeId = employeeId });
			}
			await _db.SaveChangesAsync(ct);

			async Task<int?> ConceptIdAsync(string code) =>
				await _db.Set<HospitalConcept>().AsNoTracking().Where(c => c.CompanyId == ctx.CompanyId && c.Code == code).Select(c => (int?)c.ID).FirstOrDefaultAsync(ct);
			foreach (var c in HospitalSpecialtyPack.Concepts)
			{
				if (await ConceptIdAsync(c.Code) != null) continue;
				var r = await _content.SaveConceptAsync(ctx, new HospitalConceptInput
				{
					Code = c.Code, CodeSystem = c.System, Name = c.Ar, NameEn = c.En, ValueType = c.Type, Unit = c.Unit, Decimals = c.Decimals,
					NormalLow = c.NormalLow, NormalHigh = c.NormalHigh, Choices = c.Choices, Calculator = c.Calculator, CalcInputs = c.Inputs, LabAnalyteCode = c.Lab,
				}, employeeId, nowUtc, ct);
				if (!r.Ok) return (r, null);
				concepts++;
			}

			var depIds = await _db.HospitalDepartments.AsNoTracking().Where(d => d.CompanyId == ctx.CompanyId).ToDictionaryAsync(d => d.Code, d => d.ID, ct);
			foreach (var t in HospitalSpecialtyPack.Templates)
			{
				if (await _db.Set<HospitalClinicalTemplate>().AnyAsync(x => x.CompanyId == ctx.CompanyId && x.Code == t.Code, ct)) { skipped++; continue; }
				var made = await _content.CreateTemplateAsync(ctx, new HospitalTemplateInput { Code = t.Code, Name = t.Ar, NameEn = t.En, DepartmentId = depIds.TryGetValue(t.Dept, out var d) ? d : null }, employeeId, nowUtc, ct);
				if (!made.Ok) return (made, null);
				int versionId = await _db.Set<HospitalClinicalTemplateVersion>().AsNoTracking().Where(v => v.TemplateId == made.Id && v.Status == HospitalTemplateStatus.Draft).Select(v => v.ID).FirstAsync(ct);
				foreach (var f in t.Fields)
				{
					var cid = await ConceptIdAsync(f.Concept);
					var r = await _content.AddFieldAsync(ctx, versionId, new HospitalTemplateFieldInput
					{
						ConceptId = cid, Section = f.SectionAr, SectionEn = f.SectionEn, Required = f.Required, ValueSource = f.Source ?? HospitalValueSource.Manual, LookbackDays = f.Lookback,
						AlertAbove = f.AlertAbove, AlertBelow = f.AlertBelow, AlertText = f.AlertAr, AlertTextEn = f.AlertEn, ShowTrend = true,
					}, ct);
					if (!r.Ok) return (r, null);
				}
				var pub = await _content.PublishAsync(ctx, versionId, "الحزمة القياسية للتخصصات", "Standard specialty pack", employeeId, nowUtc, ct);
				if (!pub.Ok) return (pub, null);
				templates++;
			}
			return (HospitalResult.Success(templates), new HospitalPackResult(deps, concepts, templates, skipped));
		}
	}
}
