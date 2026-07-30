namespace CrossBuy.Models.Context.Inventory
{
	// Sales quotation (P3-2). Converts to a SalesOrder (reusing the selling pipeline) when accepted.
	public class Quotation
	{
		public int ID { get; set; }
		public int CompanyID { get; set; }
		public string? QuoteNo { get; set; }
		public DateTime QuoteDate { get; set; }
		public DateTime? ValidUntil { get; set; }
		public int CustomerId { get; set; }
		public int? WarehouseId { get; set; }
		public int? CurrencyId { get; set; }
		public decimal SubTotal { get; set; }
		public decimal TaxTotal { get; set; }
		public decimal GrandTotal { get; set; }
		public decimal? ExchangeRate { get; set; }       // Multi-Currency (1-1): rate at quote date
		public string Status { get; set; } = "Draft";   // Draft | Sent | Accepted | Rejected | Converted | Expired
		public string? Notes { get; set; }
		public int? SalesOrderId { get; set; }            // set when converted
		public string? CreatedBy { get; set; }
		public DateTime? CreatedAt { get; set; }
		public List<QuotationLine> Lines { get; set; } = new();
	}

	public class QuotationLine
	{
		public int ID { get; set; }
		public int QuotationId { get; set; }
		public int LineNo { get; set; }
		public int? ItemId { get; set; }
		public string? ItemDescription { get; set; }
		public decimal Qty { get; set; } = 1;
		public int? UoMId { get; set; }
		public decimal UnitPrice { get; set; }
		public decimal DiscountAmount { get; set; }
		public decimal TaxRate { get; set; }
		public decimal LineTotal { get; set; }
	}
}
