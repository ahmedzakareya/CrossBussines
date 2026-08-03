namespace CrossBuy.Models.Context.Inventory
{
	// P3-5 pricing engine: a price list groups price/discount rules. Resolution prefers
	// segment-specific lists, higher Priority, then the most specific quantity break.
	public class PriceList
	{
		public int ID { get; set; }
		public int CompanyID { get; set; }
		public string Code { get; set; } = "";
		public string Name { get; set; } = "";
		public string? NameEn { get; set; }
		public int? CustomerId { get; set; }          // Pricing 2-1: customer-specific list; null = segment/general
		public string? Segment { get; set; }          // applies to this customer segment; null = all customers
		public int? CurrencyId { get; set; }          // Pricing 2-1: list currency; null = branch functional currency
		public int Priority { get; set; }             // higher wins on ties
		public bool IsDefault { get; set; }
		public bool IsActive { get; set; } = true;
		public DateTime? ValidFrom { get; set; }
		public DateTime? ValidTo { get; set; }
		public string? CreatedBy { get; set; }
		public DateTime? CreatedAt { get; set; }
		public List<PriceListLine> Lines { get; set; } = new();
	}

	public class PriceListLine
	{
		public int ID { get; set; }
		public int PriceListId { get; set; }
		public int ItemId { get; set; }
		public int? UoMId { get; set; }                 // HM-2: unit this price applies to (null = base / any unit — backward-compatible)
		public decimal MinQty { get; set; } = 1;       // quantity break: rule applies when ordered qty >= MinQty
		public decimal? UnitPrice { get; set; }        // override price; null = use Item.SalesPrice then apply discount
		public decimal DiscountPercent { get; set; }   // 0..100
		// Pricing 2C — cost-plus: when PricingMode = CostPlus the unit price is COMPUTED as
		// resolved cost × (1 + MarkupPercent/100) in the functional currency, then converted to the document
		// currency and rounded to its decimals. A moving/manufactured cost → the price moves with the cost.
		public string PricingMode { get; set; } = "Fixed";   // Fixed | CostPlus
		public decimal MarkupPercent { get; set; }
		public DateTime? ValidFrom { get; set; }
		public DateTime? ValidTo { get; set; }
	}

	// Pricing 2B — time-bound promotion. Layered on top of the resolved list/base price as an ADDITIONAL discount,
	// applied sequentially (net = price × (1−listDisc) × (1−promo)). Best single promotion wins (no stacking).
	// The final net still honors the margin floor (2A). Amount discounts are stored in CurrencyId and converted to
	// the document currency at resolution; percent discounts are currency-agnostic.
	public class Promotion
	{
		public int ID { get; set; }
		public int CompanyID { get; set; }
		public string Code { get; set; } = "";
		public string Name { get; set; } = "";
		public string? NameEn { get; set; }
		public string DiscountType { get; set; } = "Percent";   // Percent (0..100) | Amount (fixed per unit)
		public decimal Value { get; set; }                      // percent, or fixed amount per unit (in CurrencyId)
		public int? CurrencyId { get; set; }                    // Amount only; null = branch functional currency
		// targeting — a null target matches everything; a set target must match the line/customer
		public int? ItemId { get; set; }
		public int? ItemCategoryId { get; set; }
		public int? CustomerId { get; set; }
		public string? Segment { get; set; }
		public decimal MinQty { get; set; } = 1;                // qty break
		public int Priority { get; set; }                       // higher wins on ties
		public DateTime? ValidFrom { get; set; }
		public DateTime? ValidTo { get; set; }
		public bool IsActive { get; set; } = true;
		public string? CreatedBy { get; set; }
		public DateTime? CreatedAt { get; set; }
	}
}
