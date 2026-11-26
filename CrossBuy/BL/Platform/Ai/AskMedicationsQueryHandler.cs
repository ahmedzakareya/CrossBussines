using System;
using System.Collections.Generic;
using System.Linq;

namespace CrossBuy.BL.Platform.Ai
{
	// =================================================================================================
	// «طيب الادوية» — the owner's second hospital question, refused like the first.
	//
	// Note what is NOT in the keywords below: no wording that names a VALUE. "المخدرات" would route
	// a question here and simultaneously make the word look like this surface's own vocabulary, so
	// the named-value filter would never look for it and "أوامر المخدرات" would answer with the
	// count of ALL orders. That cost the doctors surface a wrong answer an hour ago; the model's
	// training data carries those phrasings instead, which is where routing belongs.
	// =================================================================================================
	public sealed class AskMedicationsQueryHandler : AskSurfaceQueryHandler
	{
		public const string IntentId = "hospital.medications";

		public AskMedicationsQueryHandler(AskMedicationsSurface surface, IAskQueryComposer composer)
			: base(surface, composer) { }

		public override string Intent => IntentId;

		public override AskText Description => AskText.Of(
			"أي سؤال عن أوامر الأدوية للمنوَّمين: العدد حسب الدواء أو طريقة الإعطاء أو الحالة",
			"Any question about inpatient medication orders: the count by drug, route or status",
			"Questions sur les prescriptions des patients hospitalisés : nombre par médicament, voie d'administration ou statut");

		public override AskText Example => AskText.Of(
			"إيه الأدوية الأكثر وصفًا؟", "Which medicines are prescribed most?", "Quels médicaments sont les plus prescrits ?");

		public override IReadOnlyCollection<string> Keywords { get; } = new[]
		{
			"الادوية", "ادوية", "قائمة الادوية", "الاوامر الدوائية", "اوامر الدواء",
			"الادوية الاكثر وصفا", "اكتر دواء بيتوصف", "كام امر دوائي", "الوصفات",
			"الادوية حسب طريقة الاعطاء", "العلاج الموصوف", "ايه الادوية",
			"medications", "medicines", "drugs prescribed", "medication orders",
			"most prescribed", "prescriptions", "what medicines",
		};

		protected override IReadOnlyList<string> Sources { get; } = new[]
		{
			"HospitalMedOrders",
		};

		/// A QUESTION ABOUT DISPENSING IS NOT ABOUT PRESCRIBING, AND THE DIFFERENCE IS SAID.
		///
		/// Three tables carry "الأدوية" and they hold three different numbers: 15 orders, 18
		/// dispensed lines, 3 administration records. Someone who asks «كام دواء اتصرف من الصيدلية»
		/// and is handed the ORDER count has been answered with a true figure for a question they
		/// did not ask — the failure this whole layer exists to stop. The surface cannot see the
		/// dispensing side, so it says that instead of standing in for it.
		private static readonly string[] NotOurs =
			{ "صرف", "اتصرف", "صُرف", "مصروف", "الصيدلية", "صيدلية", "تسليم", "استلم",
			  "اعطاء", "الاعطاء", "اتعطى", "اتاخد",
			  "dispensed", "dispensing", "pharmacy", "issued", "administered", "given" };

		public override AskText? Caveat(string? question)
		{
			if (string.IsNullOrWhiteSpace(question)) return null;
			var q = AskSearch.Normalise(question);
			return NotOurs.Any(w => q.Contains(AskSearch.Normalise(w), StringComparison.Ordinal))
				? AskText.Of(
					"ده عدد أوامر الوصف، مش الصرف من الصيدلية ولا تسجيل الإعطاء — تلات أرقام مختلفة، "
					+ "وأنا أشوف الأوامر بس:",
					"This counts prescribing ORDERS, not pharmacy dispensing and not administration "
					+ "records — three different numbers, and I can only see the orders:")
				: null;
		}

		// BY DRUG. "The medicines" names no grouping, and the drug is what the question is about —
		// a bare total of orders answers nothing anyone would act on.
		protected override AskQuery DefaultQuery() => new()
		{
			Surface = AskMedicationsSurface.SurfaceCode,
			GroupBy = new[] { "drug" },
			Measures = new[] { new AskMeasure { Column = "orders", Agg = AskAgg.Sum } },
			OrderBy = "orders",
			Descending = true,
			Limit = 20,
		};
	}
}
