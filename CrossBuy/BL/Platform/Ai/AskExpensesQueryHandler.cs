using System.Collections.Generic;

namespace CrossBuy.BL.Platform.Ai
{
	// =================================================================================================
	// WHAT WE SPEND, as the sixth surface — and the first one added because a WRONG ANSWER demanded
	// it rather than because a question was refused.
	//
	// "ايه المصروفات" was refused, which was honest. "المصروفات حسب النوع" was not: it landed on
	// stock movements and answered with movement types, because "حسب النوع" is vocabulary there and
	// "المصروفات" was vocabulary nowhere. The reader got a confident table of inventory adjustments
	// under a question about money going out. That is the failure this whole layer is built to
	// avoid, and the surface below is the fix — the refusal was never the problem.
	//
	// It was built only after counting what is behind it: 1,086 posted lines across 17 distinct
	// expense accounts. The companion question "ايه الاصناف اللي قربت تخلص" was counted the same way
	// and is NOT built, because the Items table records no reorder point and the stock history is
	// too sparse to infer one. A surface over a threshold I invented would answer that question
	// confidently and wrongly, which is exactly the trade this file exists to refuse.
	// =================================================================================================
	public sealed class AskExpensesQueryHandler : AskSurfaceQueryHandler
	{
		public const string IntentId = "accounting.expenses";

		public AskExpensesQueryHandler(AskExpensesSurface surface, IAskQueryComposer composer)
			: base(surface, composer) { }

		public override string Intent => IntentId;

		public override AskText Description => AskText.Of(
			"أي سؤال عن المصروفات: المبلغ مجمَّعًا حسب الحساب أو مركز التكلفة أو الشهر",
			"Any expense question: amount grouped by account, cost centre or month",
			"Questions sur les dépenses : montants regroupés par compte, centre de coûts ou mois");

		public override AskText Example => AskText.Of(
			"إيه المصروفات حسب الحساب؟", "What are the expenses by account?", "Quel est le montant des dépenses par compte ?");

		public override IReadOnlyCollection<string> Keywords { get; } = new[]
		{
			"المصروفات", "مصروفات", "المصاريف", "مصاريف", "ايه المصروفات", "المصروفات حسب الحساب",
			"المصروفات حسب النوع", "بنود المصروفات", "صرفنا كام", "احنا بنصرف على ايه",
			"اكبر بند مصروف", "اكتر حاجة بنصرف عليها", "المصروفات حسب مركز التكلفة",
			"المصروفات الشهرية", "مصروفات الشهر", "تحليل المصروفات",
			"expenses", "expense breakdown", "what do we spend on", "spending by account",
			"operating expenses", "costs by account", "expenses by cost centre",
			"biggest expense", "monthly expenses", "where the money goes",
		};

		protected override IReadOnlyList<string> Sources { get; } = new[]
		{
			"JournalEntries", "JournalEntryLines", "Accounts", "CostCenters",
		};

		// BY ACCOUNT, BIGGEST FIRST. "What do we spend on" names no grouping, and the account IS the
		// answer to it: a single total would be true and tell nobody anything they could act on.
		protected override AskQuery DefaultQuery() => new()
		{
			Surface = AskExpensesSurface.SurfaceCode,
			GroupBy = new[] { "account" },
			Measures = new[] { new AskMeasure { Column = "amount", Agg = AskAgg.Sum } },
			OrderBy = "amount",
			Descending = true,
			Limit = 10,
		};
	}
}
