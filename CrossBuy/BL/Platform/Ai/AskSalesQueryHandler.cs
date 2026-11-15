using System.Collections.Generic;

namespace CrossBuy.BL.Platform.Ai
{
	// =================================================================================================
	// SALES, as one case of the surface query handler.
	//
	// Everything this used to do itself — composing a query from the words, validating it, running it,
	// shaping rows, speaking a sentence, reporting what a nullable measure could not see — now lives
	// in AskSurfaceQueryHandler and is shared. What remains here is only what is true of SALES and of
	// nothing else: the intent id, what it says it does, its example, its words, the query it falls
	// back to, and the tables it reads.
	//
	// That split is the point. The next surface is a file this size, not a second copy of 440 lines
	// that drifts away from this one the first time either is fixed.
	// =================================================================================================
	public sealed class AskSalesQueryHandler : AskSurfaceQueryHandler
	{
		public const string IntentId = "sales.query";

		public AskSalesQueryHandler(AskSalesSurface surface, IAskQueryComposer composer)
			: base(surface, composer) { }

		public override string Intent => IntentId;

		public override AskText Description => AskText.Of(
			"أي سؤال عن المبيعات: الإيراد أو التكلفة أو الهامش أو الكمية، مجمَّعًا حسب الصنف أو العميل أو الشهر",
			"Any sales question: revenue, cost, margin or quantity, grouped by item, customer or month");

		public override AskText Example => AskText.Of("إيه الإيراد لكل شهر؟", "What is the revenue by month?");

		public override IReadOnlyCollection<string> Keywords { get; } = new[]
		{
			"المبيعات", "مبيعات", "الايراد", "ايراد", "الايرادات", "ايرادات", "الدخل",
			"بعنا كام", "بيعنا كام", "مبيعاتنا", "ايه مبيعات", "تحليل المبيعات",
			"sales", "revenue", "turnover", "how much did we sell", "sales analysis",
			"income from sales", "sold",
		};

		protected override IReadOnlyList<string> Sources { get; } = new[]
		{
			"SalesInvoices", "SalesInvoiceLines", "StockMovements", "Items", "Customers",
		};

		protected override AskQuery DefaultQuery() => new()
		{
			Surface = AskSalesSurface.SurfaceCode,
			GroupBy = new[] { "item" },
			Measures = new[] { new AskMeasure { Column = "revenue", Agg = AskAgg.Sum } },
			OrderBy = "revenue",
			Descending = true,
			Limit = 10,
		};
	}
}
