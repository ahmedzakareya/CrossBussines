namespace CrossBuy.Models.Context.Loyalty
{
	// HM-9 slice 2: the points LEDGER (earn now; redeem/expire in a later slice). The balance is DERIVED — the sum of the
	// signed Points column, NEVER a stored balance column. This mirrors HM-6 (batch on-hand = Σ movements, no balance
	// column) and deliberately avoids the stored-balance lost-update we fought in #5173 and HM-D7. No GL effect: points are
	// a memo until redemption. Every movement links to its source SALE invoice for trace + reversal. Reverse-never-delete:
	// a return posts a NEGATIVE movement, never a row deletion.
	public class PointsMovement
	{
		public int ID { get; set; }
		public int CompanyID { get; set; }
		public int CustomerId { get; set; }
		// Earn | ReturnReversal | CancelReversal | Redeem | RedeemReversal
		// HM-22 added the last two. The column is a free NVARCHAR(30) with no CHECK constraint, so they need
		// no migration — but note that Redeem is NEGATIVE and is NOT a reversal. Anything deciding "how much
		// of this earn has been given back" must filter on Kind, never on the sign, or a customer who spent
		// points on the sale that earned them will have that spending mistaken for a refund of it.
		public string Kind { get; set; } = "Earn";
		public int? SourceInvoiceId { get; set; }        // the sale that earned (always set — the trace + reversal anchor)
		public string? SourceDocType { get; set; }       // reversal provenance, e.g. "SalesReturn"
		public int? SourceDocId { get; set; }            // the return/cancel document id
		public long Points { get; set; }                 // SIGNED WHOLE points: +earn (Math.Floor), −reversal
		public DateTime CreatedAt { get; set; }
		public int? CreatedBy { get; set; }
	}
}
