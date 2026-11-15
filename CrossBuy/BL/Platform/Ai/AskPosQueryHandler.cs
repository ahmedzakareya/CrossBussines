using System.Collections.Generic;

namespace CrossBuy.BL.Platform.Ai
{
	// =================================================================================================
	// THE TILL, as the fourth case of the surface query handler.
	//
	// The first surface that can answer WHEN. Revenue by month was already reachable; revenue by
	// HOUR was not, and for a counter business the hour is the question — which hour is busy, when
	// to put a second person on, when the kitchen is idle. 15 distinct hours are present in the
	// data and nothing in the product could group by them.
	//
	// It is also the first that can answer WHERE: 5 branches, and no existing surface carried a
	// branch dimension at all.
	//
	// DEFAULT: AMOUNT BY ITEM. The question anyone asks of a till first is what sells.
	// =================================================================================================
	public sealed class AskPosQueryHandler : AskSurfaceQueryHandler
	{
		public const string IntentId = "pos.query";

		public AskPosQueryHandler(AskPosSurface surface, IAskQueryComposer composer)
			: base(surface, composer) { }

		public override string Intent => IntentId;

		public override AskText Description => AskText.Of(
			"أي سؤال عن مبيعات نقطة البيع: المبلغ أو الكمية أو عدد الطلبات، حسب الصنف أو الفرع أو الساعة أو نوع الطلب",
			"Any point-of-sale question: amount, quantity or order count, by item, branch, hour or order type");

		public override AskText Example => AskText.Of(
			"أي ساعة أكتر زحمة؟", "Which hour is busiest?");

		public override IReadOnlyCollection<string> Keywords { get; } = new[]
		{
			"نقطة البيع", "الكاشير", "الكاشيرات", "المحل", "الفرع", "الفروع",
			"مبيعات الفروع", "مبيعات الفرع", "اي فرع بيبيع اكتر", "اي ساعة اكتر زحمة",
			"وقت الذروة", "ساعات الذروة", "الزحمة امتى", "تيك اواي", "دليفري", "صالة",
			"كام طلب", "عدد الطلبات", "متوسط الطلب", "مبيعات الكاشير",
			"point of sale", "pos", "till", "counter", "cashier sales", "branch sales",
			"busiest hour", "peak hour", "rush hour", "takeaway", "dine in", "delivery",
			"order count", "how many orders", "sales by branch", "sales by hour",
		};

		protected override IReadOnlyList<string> Sources { get; } = new[]
		{
			"PosOrders", "PosOrderLines", "Items", "ItemCategories", "Branches", "Employee",
		};

		protected override AskQuery DefaultQuery() => new()
		{
			Surface = AskPosSurface.SurfaceCode,
			GroupBy = new[] { "item" },
			Measures = new[] { new AskMeasure { Column = "amount", Agg = AskAgg.Sum } },
			OrderBy = "amount",
			Descending = true,
			Limit = 10,
		};
	}
}
