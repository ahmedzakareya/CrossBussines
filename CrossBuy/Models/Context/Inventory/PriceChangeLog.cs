namespace CrossBuy.Models.Context.Inventory
{
	// HM-4: the durable, append-only audit of bulk price changes and their undos. Structure is deployed by the
	// idempotent deploy/sql/hm4_pricing.sql (migrations are disabled in this project).
	//
	// It references (PriceListId, ItemId, UoMId) — NOT a PriceListLine.ID — on purpose: PricingService.SaveAsync
	// full-replaces a list's lines and renumbers LineIds (HM-D47), so a LineId is not a stable identity to log against.
	//
	// Decimals carry no [Precision]: the model-wide convention HavePrecision(19,4) (CrossDbContext.ConfigureConventions)
	// gives them decimal(19,4), matching the SQL column and PriceListLine.UnitPrice — so the ef_precision guard stays 0.
	public class PriceChangeLog
	{
		public long ID { get; set; }
		public int CompanyID { get; set; }
		public Guid BatchId { get; set; }                 // one id per bulk operation (and per undo operation)
		public int PriceListId { get; set; }
		public int ItemId { get; set; }
		public int? UoMId { get; set; }                   // the priced unit (null = base/any line)
		public decimal? OldPrice { get; set; }
		public decimal? NewPrice { get; set; }
		public string AdjustType { get; set; } = "";      // Percent | Amount | Undo
		public decimal AdjustValue { get; set; }          // the % or amount applied (0 for Undo)
		public string PriceRounding { get; set; } = "None";   // None | Nearest5Fils | Nearest10Fils
		public string Reason { get; set; } = "";          // mandatory business reason
		public Guid? ReversalOfBatchId { get; set; }      // set on Undo rows → the batch they reverse
		public string? PerformedBy { get; set; }
		public DateTime PerformedAt { get; set; }
	}
}
