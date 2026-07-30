namespace CrossBuy.Models.Context.Accounting
{
	// per-company accounting governance settings
	public class AccountingSettings
	{
		[System.ComponentModel.DataAnnotations.Key]
		public int CompanyID { get; set; }
		public decimal ApprovalThreshold { get; set; }   // journals/payments ≥ this need ChiefAccountant approval (0 = off)
		public DateTime? CreatedAt { get; set; }
	}
}
