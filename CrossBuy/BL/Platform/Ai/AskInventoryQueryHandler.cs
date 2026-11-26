using System.Collections.Generic;

namespace CrossBuy.BL.Platform.Ai
{
	// =================================================================================================
	// STOCK MOVEMENTS, as the third case of the surface query handler — and the cheapest yet.
	//
	// The whole cost of adding warehouse-level stock analysis to the assistant is this file plus the
	// surface beside it. Nothing about composing, validating, running, shaping, speaking or coverage
	// appears here, because none of it is specific to inventory. That was the point of generalising
	// the handler when purchases arrived: the second surface proved it, and the third is where it
	// starts paying.
	//
	// WHY IT IS WORTH MORE THAN ITS SIZE. Until now the assistant could answer "what is the balance
	// of item X" and "what moved on item X", one item at a time. It could not answer a single
	// question about a WAREHOUSE — which store received the most, what moved this month, what type
	// of movement dominates — because no surface carried the warehouse dimension. Every movement in
	// this database has one.
	//
	// DISTINCT FROM inventory.item-movements ON PURPOSE. That handler answers "the last movements of
	// ONE named item" and returns records. This answers aggregates across items, warehouses, types
	// and months. A question that names an item belongs to the first; a question that groups belongs
	// here, and the training data is written to make that the deciding feature rather than the
	// vocabulary, which the two necessarily share.
	// =================================================================================================
	public sealed class AskInventoryQueryHandler : AskSurfaceQueryHandler
	{
		public const string IntentId = "inventory.stock-query";

		public AskInventoryQueryHandler(AskInventorySurface surface, IAskQueryComposer composer)
			: base(surface, composer) { }

		public override string Intent => IntentId;

		public override AskText Description => AskText.Of(
			"أي سؤال عن حركة المخزون: الوارد أو المنصرف أو الصافي أو التكلفة، مجمَّعًا حسب المخزن أو الصنف أو نوع الحركة أو الشهر",
			"Any stock movement question: in, out, net or cost, grouped by warehouse, item, movement type or month",
			"Questions sur les mouvements de stock : entrées, sorties, solde net ou coût, par entrepôt, article, type de mouvement ou mois");

		public override AskText Example => AskText.Of(
			"إيه الوارد لكل مخزن؟", "What came in by warehouse?", "Quelles sont les quantités entrées par entrepôt ?");

		public override IReadOnlyCollection<string> Keywords { get; } = new[]
		{
			"حركة المخزون", "حركات المخزون", "الوارد والمنصرف", "الوارد", "المنصرف",
			"الوارد لكل مخزن", "المخزون حسب المخزن", "حركة المخازن", "ايه اتحرك",
			"اكتر صنف اتحرك", "انواع الحركات", "حركات حسب النوع", "صرفنا كام من المخزن",
			"stock movements", "inventory movements", "goods in", "goods out",
			"stock by warehouse", "movements by warehouse", "what moved", "movement types",
			"inbound", "outbound", "stock activity",
		};

		protected override IReadOnlyList<string> Sources { get; } = new[]
		{
			"StockMovements", "Items", "ItemCategories", "Warehouses",
		};

		// IN, NOT NET, AS THE DEFAULT. "What moved" with no measure named is most often asked about
		// what arrived; and a net figure as the default would silently answer a different question
		// with a number that looks like an answer either way.
		protected override AskQuery DefaultQuery() => new()
		{
			Surface = AskInventorySurface.SurfaceCode,
			GroupBy = new[] { "warehouse" },
			Measures = new[] { new AskMeasure { Column = "qtyin", Agg = AskAgg.Sum } },
			OrderBy = "qtyin",
			Descending = true,
			Limit = 10,
		};
	}
}
