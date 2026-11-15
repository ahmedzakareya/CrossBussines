using System;
using System.Collections.Generic;
using System.Linq;

namespace CrossBuy.BL.Platform.Ai
{
	// =================================================================================================
	// «كام إيراد المستشفى» — the question twenty-six billing tables could not answer.
	//
	// Note what is NOT in the keywords below: no wording that names a VALUE. "فواتير الأشعة" would
	// route a question here and, because a routing keyword is ALSO read as this surface's own
	// vocabulary, would make "الأشعة" look like a word the surface knows — so the named-value filter
	// would never look for it and the answer would be the total for EVERY category under a question
	// about radiology. That has cost this layer three wrong answers; the model's training data
	// carries those phrasings instead, which is where routing belongs.
	// =================================================================================================
	public sealed class AskHospitalBillingQueryHandler : AskSurfaceQueryHandler
	{
		public const string IntentId = "hospital.billing";

		public AskHospitalBillingQueryHandler(AskHospitalBillingSurface surface, IAskQueryComposer composer)
			: base(surface, composer) { }

		public override string Intent => IntentId;

		public override AskText Description => AskText.Of(
			"أي سؤال عن فواتير المستشفى: المبلغ المفوتر وعدد البنود حسب التصنيف أو القسم أو الشهر",
			"Any question about hospital bills: the billed amount and line count by category, department or month");

		public override AskText Example => AskText.Of(
			"كام فوترت المستشفى الشهر ده؟", "How much did the hospital bill this month?");

		public override IReadOnlyCollection<string> Keywords { get; } = new[]
		{
			"فواتير المستشفى", "فوترة المستشفى", "ايراد المستشفى", "ايرادات المستشفى",
			"كام فوترنا", "كام فوترت المستشفى", "المبلغ المفوتر", "اجمالي الفواتير",
			"فواتير المرضى", "الفوترة", "ايراد العيادات", "فواتير حسب القسم",
			"hospital billing", "hospital revenue", "billed amount", "patient bills",
			"how much did we bill", "billing by department", "hospital charges",
		};

		protected override IReadOnlyList<string> Sources { get; } = new[]
		{
			"HospitalBillLines", "HospitalBills", "HospitalDepartments",
		};

		/// THREE NEIGHBOURING NUMBERS, AND THIS SURFACE IS ONLY ONE OF THEM.
		///
		/// Billed, collected and accepted-by-the-insurer are different figures over different rows:
		/// 4,253.91 billed across 104 lines, 592.28 received across 6 payments, and 19 claim lines of
		/// which most are still undecided. Someone who asks «كام حصّلنا» and is handed the BILLED
		/// total has been answered with a true number for a question they did not ask. The surface
		/// cannot see the other two, so it says so rather than standing in for them.
		private static readonly string[] Collected =
			{ "حصلنا", "حصّلنا", "تحصيل", "التحصيل", "محصل", "المحصل", "مدفوع", "المدفوع",
			  "دفعات", "الدفعات", "مقبوضات", "المقبوضات", "كاش", "خزينة",
			  "collected", "collection", "received", "payments", "paid", "cash received" };

		private static readonly string[] Insurer =
			{ "تامين", "التامين", "مطالبة", "مطالبات", "المطالبات", "مرفوض", "المرفوض", "مرفوضة",
			  "معتمد", "المعتمد", "مقبول", "شركة التامين", "جهة الدفع",
			  "insurer", "insurance", "claim", "claims", "rejected", "accepted", "payer" };

		public override AskText? Caveat(string? question)
		{
			if (string.IsNullOrWhiteSpace(question)) return null;
			var q = AskSearch.Normalise(question);
			bool Mentions(string[] words) =>
				words.Any(w => q.Contains(AskSearch.Normalise(w), StringComparison.Ordinal));

			if (Mentions(Insurer))
				return AskText.Of(
					"ده المبلغ اللي المستشفى فوترته، مش اللي التأمين وافق عليه — رقمين مختلفين على "
					+ "صفوف مختلفة. قرارات التأمين في سؤال المطالبات:",
					"This is what the hospital BILLED, not what an insurer accepted — two different "
					+ "figures over different rows. Insurer decisions are a claims question:");

			if (Mentions(Collected))
				return AskText.Of(
					"ده المبلغ المفوتر، مش المحصَّل — الفاتورة ممكن تكون مفتوحة ولسه ما اتدفعتش. "
					+ "أنا أشوف الفوترة بس:",
					"This is the BILLED amount, not what was collected — a bill can be open and "
					+ "unpaid. I can only see the billing side:");

			return null;
		}

		// BY CATEGORY. "The hospital's billing" names no grouping, and a bare grand total answers
		// nothing anyone would act on; the category is what the money is actually made of.
		protected override AskQuery DefaultQuery() => new()
		{
			Surface = AskHospitalBillingSurface.SurfaceCode,
			GroupBy = new[] { "category" },
			Measures = new[] { new AskMeasure { Column = "billed", Agg = AskAgg.Sum } },
			OrderBy = "billed",
			Descending = true,
			Limit = 20,
		};
	}
}
