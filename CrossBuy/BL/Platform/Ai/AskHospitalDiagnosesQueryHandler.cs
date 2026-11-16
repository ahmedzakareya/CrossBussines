using System;
using System.Collections.Generic;
using System.Linq;

namespace CrossBuy.BL.Platform.Ai
{
	// =================================================================================================
	// «ايه اكتر الامراض عندنا» — the question a hospital owner asks before any other clinical one,
	// and the one thing 85 recorded diagnoses could not be asked.
	//
	// ROUTING WORDS LIVE HERE, NOT ON THE SURFACE. "الكشوف", "الزيارات", "سكري" would each bring a
	// question to this handler, and none of them may be surface vocabulary: a value word promoted to
	// vocabulary is a value the filter stops looking for. The split is deliberate and is the same one
	// the appointments handler documents.
	// =================================================================================================
	public sealed class AskHospitalDiagnosesQueryHandler : AskSurfaceQueryHandler
	{
		public const string IntentId = "hospital.diagnoses";

		public AskHospitalDiagnosesQueryHandler(AskHospitalDiagnosesSurface surface, IAskQueryComposer composer)
			: base(surface, composer) { }

		public override string Intent => IntentId;

		public override AskText Description => AskText.Of(
			"أي سؤال عن التشخيصات المسجَّلة على الكشوف: أكتر الأمراض، وتوزيعها على الأقسام والأطباء والشهور",
			"Any question about the diagnoses recorded on consultations: the commonest conditions and how they fall across departments, practitioners and months");

		public override AskText Example => AskText.Of(
			"ايه اكتر التشخيصات عندنا", "What are our commonest diagnoses");

		public override IReadOnlyCollection<string> Keywords { get; } = new[]
		{
			// "الكشوف" and "الكشف" are deliberately NOT here. They read as a count of VISITS far
			// more often than as a question about what was diagnosed, and this surface would
			// answer such a question with 85 for 68 visits. They belong to the appointments
			// surface, which counts the visits themselves.
			"التشخيصات", "تشخيصات", "التشخيص", "اكتر التشخيصات", "اكتر الامراض", "الامراض",
			"امراض المرضى", "التشخيصات حسب القسم", "التشخيصات حسب الطبيب",
			"الحالات المرضية", "تشخيصات الشهر", "اكواد التشخيص",
			"diagnoses", "diagnosis", "commonest diagnoses", "top diagnoses", "conditions",
			"diseases", "case mix", "casemix", "icd", "icd10", "diagnoses by department",
			"diagnoses by doctor", "what are we treating",
		};

		protected override IReadOnlyList<string> Sources { get; } = new[]
		{
			"HospitalEncounterDiagnoses", "HospitalEncounters", "HospitalAppointments",
			"HospitalPractitioners", "HospitalDepartments",
		};

		/// A VISIT IS NOT A DIAGNOSIS, AND THE NUMBERS ARE CLOSE ENOUGH TO BE BELIEVED.
		///
		/// 85 diagnoses sit on 68 consultations. Someone who asks how many VISITS there were and is
		/// handed 85 has no way to see the difference from the figure alone — the two are within a
		/// quarter of each other — so the answer has to say which one it is, and where the other
		/// one lives.
		private static readonly string[] Visits =
			{ "زيارة", "زيارات", "الزيارات", "كشف", "كشوف", "الكشوف", "كام كشف", "كام زيارة",
			  "visit", "visits", "consultation", "consultations", "encounters", "how many visits" };

		/// A PATIENT IS NOT A DIAGNOSIS EITHER, and here the gap is wider: one patient can attend
		/// several times and collect a diagnosis on each.
		private static readonly string[] Patients =
			{ "مريض", "مرضى", "المرضى", "كام مريض", "patients", "how many patients" };

		public override AskText? Caveat(string? question)
		{
			if (string.IsNullOrWhiteSpace(question)) return null;
			var q = AskSearch.Normalise(question);
			bool Mentions(string[] words) =>
				words.Any(w => q.Contains(AskSearch.Normalise(w), StringComparison.Ordinal));

			if (Mentions(Patients))
				return AskText.Of(
					"ده عدد التشخيصات مش عدد المرضى — المريض الواحد ممكن يتكشف أكتر من مرة وياخد "
					+ "تشخيص في كل مرة:",
					"This counts DIAGNOSES, not patients — one patient can be seen more than once "
					+ "and take a diagnosis each time:");

			if (Mentions(Visits))
				return AskText.Of(
					"ده عدد التشخيصات مش عدد الزيارات — الزيارة الواحدة ممكن تحمل أكتر من تشخيص. "
					+ "لو عايز عدد الزيارات نفسها، اسأل عن المواعيد وحالتها:",
					"This counts DIAGNOSES, not visits — one visit can carry several. For the number "
					+ "of visits themselves, ask about appointments and their status:");

			return null;
		}

		// BY DIAGNOSIS. "The diagnoses" names no grouping, and the first thing anyone running a
		// hospital wants from this table is which conditions keep walking through the door.
		protected override AskQuery DefaultQuery() => new()
		{
			Surface = AskHospitalDiagnosesSurface.SurfaceCode,
			GroupBy = new[] { "diagnosis" },
			Measures = new[] { new AskMeasure { Column = "diagnoses", Agg = AskAgg.Sum } },
			OrderBy = "diagnoses",
			Descending = true,
			Limit = 15,
		};
	}
}
