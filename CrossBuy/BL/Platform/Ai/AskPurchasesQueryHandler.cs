using System.Collections.Generic;

namespace CrossBuy.BL.Platform.Ai
{
	// =================================================================================================
	// PURCHASES, as the second case of the surface query handler.
	//
	// This file is the whole cost of adding a new area of the business to the assistant, and that is
	// the point of having generalised the handler first: no composing, validating, running, shaping,
	// speaking or coverage code appears here, because none of it is specific to purchases.
	//
	// WHY IT EXISTS. "طيب هات الاصناف المشتراه" was refused outright — there was no purchases
	// capability at all. The refusal was honest and the gap was real.
	//
	// WHAT IT CANNOT DO, deliberately: there is no margin measure, because a purchase has no revenue
	// to take one against. See AskPurchasesSurface for the rest of the judgements.
	// =================================================================================================
	public sealed class AskPurchasesQueryHandler : AskSurfaceQueryHandler
	{
		public const string IntentId = "purchases.query";

		public AskPurchasesQueryHandler(AskPurchasesSurface surface, IAskQueryComposer composer)
			: base(surface, composer) { }

		public override string Intent => IntentId;

		public override AskText Description => AskText.Of(
			"أي سؤال عن المشتريات: المبلغ أو الكمية، مجمَّعًا حسب الصنف أو المورد أو الشهر",
			"Any purchasing question: amount or quantity, grouped by item, vendor or month");

		public override AskText Example => AskText.Of(
			"إيه الأصناف اللي اشتريناها؟", "What items have we purchased?");

		// THE WORDS PEOPLE ACTUALLY USED. "المشتراه" is here because it is the exact spelling in the
		// question that was refused, and "مشتريات" because it is the word anyone else would reach
		// for. Both, not one — a vocabulary that only knows the formal word fails the real question.
		public override IReadOnlyCollection<string> Keywords { get; } = new[]
		{
			"المشتريات", "مشتريات", "الشراء", "شراء", "اشترينا", "اشترينه", "المشتراه", "المشتراة",
			"الاصناف المشتراه", "الاصناف المشتراة", "اشترينا كام", "صرفنا كام", "موردينا",
			"فواتير الشراء", "فاتورة شراء", "تحليل المشتريات", "من مين اشترينا",
			"purchases", "purchasing", "bought", "purchased", "procurement", "spend",
			"what did we buy", "purchase invoices", "supplier spend", "vendor spend",
		};

		protected override IReadOnlyList<string> Sources { get; } = new[]
		{
			"PurchaseInvoices", "PurchaseInvoiceLines", "Items", "Vendors",
		};

		// The question the owner asked, as the fallback: what did we buy, biggest first.
		protected override AskQuery DefaultQuery() => new()
		{
			Surface = AskPurchasesSurface.SurfaceCode,
			GroupBy = new[] { "item" },
			Measures = new[] { new AskMeasure { Column = "amount", Agg = AskAgg.Sum } },
			OrderBy = "amount",
			Descending = true,
			Limit = 10,
		};
	}
}
