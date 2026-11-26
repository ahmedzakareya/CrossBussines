using System;
using System.Collections.Generic;
using System.Linq;

namespace CrossBuy.BL.Platform.Ai
{
	// =================================================================================================
	// «المطالبات المرفوضة» — the other half of the hospital's money.
	//
	// Its twin handler answers what the hospital charged. This one answers what the insurer did with
	// it. They are separate intents because they are separate questions, and because a single surface
	// spanning both grains would have to pick one of two true numbers to call "the amount".
	//
	// Note what is NOT in the keywords: no wording that names a VALUE. "المرفوضة" here would route
	// the question AND make the word look like this surface's own vocabulary, so the named-value
	// filter would never look for it and «المطالبات المرفوضة» would answer with the total for every
	// status. Those phrasings live in the model's training data, which is where routing belongs.
	// =================================================================================================
	public sealed class AskHospitalClaimsQueryHandler : AskSurfaceQueryHandler
	{
		public const string IntentId = "hospital.claims";

		public AskHospitalClaimsQueryHandler(AskHospitalClaimsSurface surface, IAskQueryComposer composer)
			: base(surface, composer) { }

		public override string Intent => IntentId;

		public override AskText Description => AskText.Of(
			"أي سؤال عن مطالبات التأمين: المبلغ المطالَب به والمعتمد وعدد البنود حسب الحالة أو جهة الدفع",
			"Any question about insurance claims: the claimed and accepted amounts and line count by status or payer",
			"Questions sur les demandes de remboursement : montants réclamés et acceptés, et nombre de lignes par statut ou payeur");

		public override AskText Example => AskText.Of(
			"إيه حالة المطالبات؟", "What is the state of the claims?", "Quel est le statut des demandes de remboursement ?");

		public override IReadOnlyCollection<string> Keywords { get; } = new[]
		{
			"المطالبات", "مطالبات", "مطالبات التامين", "حالة المطالبات", "ملف المطالبات",
			"المبلغ المطالب به", "قيمة المطالبات", "المطالبات حسب جهة الدفع",
			"claims", "insurance claims", "claim status", "claimed amount",
			"claims by payer", "claim lines",
		};

		protected override IReadOnlyList<string> Sources { get; } = new[]
		{
			"HospitalClaimLines", "HospitalClaims", "HospitalClaimBatches", "HospitalPayers",
		};

		/// THE CLAIM IS NOT THE BILL, AND NEITHER IS THE PAYMENT.
		///
		/// Someone who asks «كام فوترنا» and is handed the CLAIMED total has been answered with a
		/// true number for a question they did not ask — only insured lines are ever claimed, so the
		/// claim total is structurally smaller than the billed one and reads like a revenue drop.
		private static readonly string[] Billing =
			{ "فوترنا", "فوترت", "فاتورة", "فواتير", "الفواتير", "مفوتر", "المفوتر", "الفوترة",
			  "ايراد", "الايراد", "ايرادات",
			  "billed", "billing", "bills", "revenue", "charged", "charges" };

		private static readonly string[] Collected =
			{ "حصلنا", "حصّلنا", "تحصيل", "التحصيل", "محصل", "مدفوع", "دفعات", "مقبوضات",
			  "collected", "collection", "received", "payments", "paid" };

		public override AskText? Caveat(string? question)
		{
			if (string.IsNullOrWhiteSpace(question)) return null;
			var q = AskSearch.Normalise(question);
			bool Mentions(string[] words) =>
				words.Any(w => q.Contains(AskSearch.Normalise(w), StringComparison.Ordinal));

			if (Mentions(Billing))
				return AskText.Of(
					"ده المبلغ المطالَب به من التأمين، مش اللي المستشفى فوترته — البنود غير المؤمَّنة "
					+ "مش داخلة هنا أصلًا، فالرقم ده أصغر بطبيعته. الفوترة سؤال تاني:",
					"This is what was CLAIMED from the insurer, not what the hospital billed — "
					+ "uninsured lines never enter here, so this figure is structurally smaller. "
					+ "Billing is a separate question:");

			if (Mentions(Collected))
				return AskText.Of(
					"ده قرار التأمين على الورق، مش فلوس اتحصّلت فعلًا — المعتمد ممكن يكون لسه "
					+ "ما اتدفعش. أنا أشوف المطالبات بس:",
					"This is the insurer's decision on paper, not money actually received — an "
					+ "accepted line can still be unpaid. I can only see the claims side:");

			return null;
		}

		// BY STATUS. "The claims" names no grouping, and the first thing anyone wants to know about
		// a claims file is how much of it is settled, pending or refused.
		protected override AskQuery DefaultQuery() => new()
		{
			Surface = AskHospitalClaimsSurface.SurfaceCode,
			GroupBy = new[] { "status" },
			Measures = new[] { new AskMeasure { Column = "claimed", Agg = AskAgg.Sum } },
			OrderBy = "claimed",
			Descending = true,
			Limit = 20,
		};
	}
}
