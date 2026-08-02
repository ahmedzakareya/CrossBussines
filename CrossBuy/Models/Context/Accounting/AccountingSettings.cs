namespace CrossBuy.Models.Context.Accounting
{
	// per-company accounting governance settings
	public class AccountingSettings
	{
		[System.ComponentModel.DataAnnotations.Key]
		public int CompanyID { get; set; }
		public decimal ApprovalThreshold { get; set; }   // journals/payments ≥ this need ChiefAccountant approval (0 = off)
		public DateTime? CreatedAt { get; set; }
		// HM-D23: exchange-rate staleness policy (company level). 0 = no limit (default). Behavior when a looked-up rate is older
		// than MaxAgeDays vs the DOCUMENT date: "Warn" (proceed + on-screen notice + counted) or "Reject" (block the sale).
		public int RateMaxAgeDays { get; set; }
		public string RateStaleBehavior { get; set; } = "Warn";
	}
}
