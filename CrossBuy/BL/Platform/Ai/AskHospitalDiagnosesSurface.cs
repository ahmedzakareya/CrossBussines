using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CrossBuy.BL.Hospital;
using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Hospital;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL.Platform.Ai
{
	// =================================================================================================
	// WHAT THE HOSPITAL IS ACTUALLY TREATING — the diagnoses doctors wrote on their notes.
	//
	// THE GRAIN IS THE DIAGNOSIS, NOT THE VISIT, AND THE DIFFERENCE IS NOT COSMETIC. 69 consultations
	// carry 85 diagnoses: one visit routinely records a principal condition and a comorbidity beside
	// it. A single count measure cannot honestly mean both, and the layer has no distinct-count
	// aggregation to paper over it, so this surface counts DIAGNOSES and says so in every answer. The
	// visit count lives where visits live — the appointments surface, by status.
	//
	// WHY THAT MATTERS FOR A REAL QUESTION. Hypertension is recorded 18 times but is the PRINCIPAL
	// diagnosis only 5 of them. A surface built at the visit grain, carrying "the" diagnosis, would
	// answer «كام حالة ضغط» with 5 and look entirely plausible doing it. Counting every recorded
	// diagnosis is the only reading that matches what the doctors wrote.
	//
	// A VISIT WITH NO DIAGNOSIS IS NOT HERE. One consultation has none, and the Rules say so rather
	// than letting a reader infer that every visit reached a conclusion.
	//
	// THE COLUMN ORDER IS LOAD-BEARING. The named-value filter walks the dimensions in declaration
	// order and takes the first that matches, so `rank` is declared BEFORE `diagnosis` on purpose:
	// "Essential (primary) hypertension" contains the word "primary", and with the diagnosis column
	// first, an English question about PRINCIPAL diagnoses would have been silently filtered to
	// hypertension — a confident, specific, wrong answer.
	//
	// CLINICAL CONTENT READS UNDER `consult`, NOT `read`. Every other hospital surface aggregates
	// money or scheduling, which the cashier and the front desk are entitled to. What a patient was
	// diagnosed with is the doctor's record; the aggregate carries no name, but the authority that
	// governs the note governs the count of the notes. Today no hospital role is assigned anywhere,
	// so the module is bootstrap-open and this changes nothing — which is exactly when it is cheap
	// to get right.
	//
	// NO PATIENT DIMENSION. Same rule as the appointments surface: who was diagnosed with what is a
	// patient record, and this is a clinic's caseload.
	// =================================================================================================
	public sealed class AskHospitalDiagnosesSurface : AskSurface
	{
		public const string SurfaceCode = "hospital.diagnoses";

		private readonly CrossDbContext _db;
		public AskHospitalDiagnosesSurface(CrossDbContext db) => _db = db ?? throw new ArgumentNullException(nameof(db));

		public override string Code => SurfaceCode;
		public override string Module => EntityRegistry.ScopeHospital;
		public override string Action => HospitalActions.Consult;

		public override AskText Description => AskText.Of(
			"التشخيصات المسجَّلة على الكشوف: إيه أكتر الأمراض، وتوزيعها على الأقسام والأطباء والشهور",
			"The diagnoses recorded on consultations: the commonest conditions and how they fall across departments, practitioners and months");

		public override AskText Rules => AskText.Of(
			"كل سطر هنا تشخيص واحد اتكتب على كشف · الزيارة الواحدة ممكن تحمل أكتر من تشخيص، "
			+ "فالعدد ده عدد التشخيصات مش عدد الزيارات ولا عدد المرضى · الزيارة اللي الطبيب "
			+ "ماكتبش فيها تشخيص مش موجودة هنا أصلاً · التشخيص متسجَّل بنصّه وقت الكشف، وتغيير "
			+ "كتالوج الأكواد بعد كده مابيعدّلش اللي الطبيب كتبه · الكشوف اللي لسه مسوّدة داخلة "
			+ "في العد ومتميّزة بالتوقيع · التاريخ هو تاريخ بداية الكشف.",
			"Each row here is ONE diagnosis written on a consultation · one visit can carry several, "
			+ "so this counts DIAGNOSES, not visits and not patients · a visit the doctor recorded no "
			+ "diagnosis on is not here at all · the diagnosis is stored with the wording it had when "
			+ "the note was written, and a later change to the code catalogue does not rewrite it · "
			+ "notes still in draft are counted and are distinguished by their sign-off · the date is "
			+ "the consultation's start.");

		public override IReadOnlyList<AskColumn> Columns { get; } = new[]
		{
			// DECLARED FIRST SO "primary" RESOLVES HERE. See the header: a diagnosis NAME contains
			// that word, and the value filter stops at the first dimension that matches.
			//
			// The values themselves — «رئيسي»/«ثانوي», Principal/Additional — appear in NO synonym
			// list, here or anywhere on this surface. A value word promoted to vocabulary stops the
			// named-value filter from ever looking for it, and the question about one of them gets
			// answered with the total of all of them. That shape has cost this layer five wrong
			// answers; it is not repeated here.
			new AskColumn { Name = "rank", Kind = AskColumnKind.Dimension, Label = AskText.Of("ترتيب التشخيص", "Diagnosis rank"),
				Synonyms = new[] { "ترتيب", "الترتيب", "ترتيب التشخيص", "rank", "ranking", "diagnosis rank" } },
			// Labelled «التوقيع» and not «توقيع الكشف» on purpose: the longer label would have made
			// "كشف" a word this surface claims, and «كام كشف» would then have been read as naming
			// THIS column instead of asking the question the caveat exists to answer.
			new AskColumn { Name = "signoff", Kind = AskColumnKind.Dimension, Label = AskText.Of("التوقيع", "Sign-off"),
				Synonyms = new[] { "توقيع", "التوقيع", "sign off", "signoff", "signature" } },
			new AskColumn { Name = "visit", Kind = AskColumnKind.Dimension, Label = AskText.Of("نوع الزيارة", "Visit type"),
				Synonyms = new[] { "نوع الزيارة", "نوع الكشف", "visit type", "kind of visit" } },
			new AskColumn { Name = "department", Kind = AskColumnKind.Dimension, Label = AskText.Of("القسم", "Department"),
				Synonyms = new[] { "قسم", "القسم", "اقسام", "الاقسام", "حسب القسم",
					"department", "departments", "by department" } },
			new AskColumn { Name = "practitioner", Kind = AskColumnKind.Dimension, Label = AskText.Of("الطبيب", "Practitioner"),
				Synonyms = new[] { "طبيب", "الطبيب", "دكتور", "الدكتور", "حسب الطبيب",
					"doctor", "practitioner", "by doctor", "per doctor" } },
			new AskColumn { Name = "diagnosis", Kind = AskColumnKind.Dimension, Label = AskText.Of("التشخيص", "Diagnosis"),
				Synonyms = new[] { "تشخيص", "التشخيص", "تشخيصات", "التشخيصات", "حسب التشخيص",
					"مرض", "المرض", "امراض", "الامراض", "تشخيصا",
					"diagnosis", "diagnoses", "condition", "conditions", "disease", "diseases",
					"by diagnosis", "icd", "icd10" } },
			new AskColumn { Name = "month", Kind = AskColumnKind.Dimension, Label = AskText.Of("الشهر", "Month"),
				Synonyms = new[] { "شهر", "شهري", "شهور", "الشهور", "بالشهر", "month", "monthly", "by month" } },
			new AskColumn { Name = "date", Kind = AskColumnKind.Dimension, Label = AskText.Of("التاريخ", "Date"),
				Synonyms = new[] { "تاريخ", "يوم", "يومي", "بالتاريخ", "date", "day", "daily" } },

			// THE ONE MEASURE, AND IT CARRIES NO DIAGNOSIS WORD. "تشخيصات" belongs to the DIMENSION
			// above, so «عدد التشخيصات» reads as "count, grouped by diagnosis" — which is the table
			// the asker wanted — instead of collapsing to a single number with no breakdown.
			new AskColumn { Name = "diagnoses", Kind = AskColumnKind.Measure, Label = AskText.Of("عدد التشخيصات", "Diagnoses"),
				Synonyms = new[] { "عدد", "العدد", "كام", "حالة", "حالات", "الحالات",
					"count", "how many", "cases", "case" } },
		};

		public override async Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> RowsAsync(
			AskScope scope, CancellationToken ct = default)
		{
			var arabic = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ar";

			var lines = await _db.Set<HospitalEncounterDiagnosis>().AsNoTracking()
				.Where(d => d.CompanyId == scope.CompanyId)
				.Select(d => new { d.EncounterId, d.Code, d.Name, d.NameEn, d.IsPrimary })
				.ToListAsync(ct);

			if (lines.Count == 0) return Array.Empty<IReadOnlyDictionary<string, object?>>();

			var encIds = lines.Select(l => l.EncounterId).Distinct().ToList();
			var encs = (await _db.Set<HospitalEncounter>().AsNoTracking()
					.Where(e => e.CompanyId == scope.CompanyId && encIds.Contains(e.ID))
					.Select(e => new { e.ID, e.AppointmentId, e.PractitionerId, e.Status, e.StartedAt })
					.ToListAsync(ct))
				.ToDictionary(e => e.ID);

			var apptIds = encs.Values.Select(e => e.AppointmentId).Distinct().ToList();
			var appts = (await _db.Set<HospitalAppointment>().AsNoTracking()
					.Where(a => a.CompanyId == scope.CompanyId && apptIds.Contains(a.ID))
					.Select(a => new { a.ID, a.VisitType })
					.ToListAsync(ct))
				.ToDictionary(a => a.ID, a => a.VisitType);

			var docIds = encs.Values.Select(e => e.PractitionerId).Distinct().ToList();
			var docs = (await _db.HospitalPractitioners.AsNoTracking()
					.Where(p => p.CompanyId == scope.CompanyId && docIds.Contains(p.ID))
					.Select(p => new { p.ID, p.Name, p.NameEn, p.DepartmentId })
					.ToListAsync(ct))
				.ToDictionary(p => p.ID);

			var deptIds = docs.Values.Where(p => p.DepartmentId != null).Select(p => p.DepartmentId!.Value).Distinct().ToList();
			var depts = (await _db.HospitalDepartments.AsNoTracking()
					.Where(d => d.CompanyId == scope.CompanyId && deptIds.Contains(d.ID))
					.Select(d => new { d.ID, d.Name, d.NameEn })
					.ToListAsync(ct))
				.ToDictionary(d => d.ID);

			var rows = new List<IReadOnlyDictionary<string, object?>>(lines.Count);
			foreach (var l in lines)
			{
				var doc = arabic ? "(بلا طبيب)" : "(no practitioner)";
				var docSearch = "";
				var dept = arabic ? "(بلا قسم)" : "(no department)";
				var deptSearch = "";
				var signoff = arabic ? "(غير معروف)" : "(unknown)";
				var signoffSearch = "";
				var visit = arabic ? "(غير محدد)" : "(not recorded)";
				var visitSearch = "";
				DateTime when = DateTime.MinValue;

				if (encs.TryGetValue(l.EncounterId, out var e))
				{
					when = e.StartedAt;
					signoff = SignOff(e.Status, arabic);
					signoffSearch = (e.Status ?? "") + " " + SignOff(e.Status, true) + " " + SignOffSaid(e.Status);

					if (appts.TryGetValue(e.AppointmentId, out var vt))
					{
						visit = Visit(vt, arabic);
						visitSearch = (vt ?? "") + " " + Visit(vt, true);
					}

					if (docs.TryGetValue(e.PractitionerId, out var p))
					{
						doc = (arabic ? (p.Name ?? p.NameEn) : (p.NameEn ?? p.Name)) ?? doc;
						docSearch = ((p.Name ?? "") + " " + (p.NameEn ?? "")).Trim();
						if (p.DepartmentId != null && depts.TryGetValue(p.DepartmentId.Value, out var d))
						{
							dept = (arabic ? (d.Name ?? d.NameEn) : (d.NameEn ?? d.Name)) ?? dept;
							deptSearch = ((d.Name ?? "") + " " + (d.NameEn ?? "")).Trim();
						}
					}
				}

				// THE DIAGNOSIS SHOWS ITS NAME AND IS SEARCHED BY EVERYTHING IT HAS. The answer card
				// has to be readable, so "E11.65" does not sit in front of the reader; but someone
				// who types the code, or the other language's wording, still finds the rows — that
				// is what the search key is for.
				var name = (arabic ? (l.Name ?? l.NameEn) : (l.NameEn ?? l.Name)) ?? "";
				if (name.Trim().Length == 0) name = l.Code ?? (arabic ? "(بلا تشخيص)" : "(no diagnosis)");

				rows.Add(new Dictionary<string, object?>
				{
					["rank"] = Rank(l.IsPrimary, arabic),
					[SearchKeyFor("rank")] = Rank(l.IsPrimary, true) + " " + RankSaid(l.IsPrimary),
					["signoff"] = signoff,
					[SearchKeyFor("signoff")] = signoffSearch.Trim(),
					["visit"] = visit,
					[SearchKeyFor("visit")] = visitSearch.Trim(),
					["department"] = dept.Trim(),
					[SearchKeyFor("department")] = deptSearch,
					["practitioner"] = doc.Trim(),
					[SearchKeyFor("practitioner")] = docSearch,
					["diagnosis"] = name.Trim(),
					[SearchKeyFor("diagnosis")] = ((l.Code ?? "") + " " + (l.Name ?? "") + " " + (l.NameEn ?? "")).Trim(),
					["month"] = when == DateTime.MinValue ? "" : when.ToString("yyyy-MM", CultureInfo.InvariantCulture),
					["date"] = when == DateTime.MinValue ? "" : when.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
					["diagnoses"] = 1m,
					[LinkFor("rank")] = null,
					[LinkFor("signoff")] = null,
					[LinkFor("visit")] = null,
					[LinkFor("department")] = null,
					[LinkFor("practitioner")] = null,
					[LinkFor("diagnosis")] = null,
					[LinkFor("month")] = null,
					[LinkFor("date")] = null,
				});
			}
			return rows;
		}

		/// PRINCIPAL, NOT "PRIMARY". The English word "primary" is inside a diagnosis name this
		/// hospital actually records — "Essential (primary) hypertension" — so using it as the
		/// displayed value would have put the same word on two dimensions at once.
		private static string Rank(bool primary, bool arabic) =>
			primary ? (arabic ? "رئيسي" : "Principal") : (arabic ? "ثانوي" : "Additional");

		/// The spoken forms, for matching only. «الرئيسية», "main", "secondary" are how the question
		/// is really typed; the row has to carry them or the filter never finds the value.
		private static string RankSaid(bool primary) => primary
			? "رئيسي رئيسية الرئيسي الرئيسية اساسي اساسية الاساسي اولي primary main principal chief"
			: "ثانوي ثانوية الثانوي الثانوية اضافي اضافية مصاحب secondary additional comorbidity other";

		private static string SignOff(string? stored, bool arabic)
		{
			var v = (stored ?? "").Trim();
			if (v.Length == 0) return arabic ? "(غير معروف)" : "(unknown)";
			if (!arabic) return v;
			return v.ToLowerInvariant() switch
			{
				"signed" => "موقَّع",
				"draft" => "مسوّدة",
				_ => v,
			};
		}

		private static string SignOffSaid(string? stored) => (stored ?? "").Trim().ToLowerInvariant() switch
		{
			"signed" => "موقع موقعة موقعه اتوقعت اللي اتقفلت مقفولة",
			"draft" => "مسوده مسودة لسه مفتوحة متقفلتش",
			_ => "",
		};

		private static string Visit(string? stored, bool arabic)
		{
			var v = (stored ?? "").Trim();
			if (v.Length == 0) return arabic ? "(غير محدد)" : "(not recorded)";
			if (!arabic) return v;
			return v.ToLowerInvariant() switch
			{
				"new" => "كشف جديد",
				"followup" => "متابعة",
				"procedure" => "إجراء",
				"video" => "كشف بالفيديو",
				_ => v,
			};
		}
	}
}
