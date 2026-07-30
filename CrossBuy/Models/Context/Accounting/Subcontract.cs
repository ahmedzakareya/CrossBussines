namespace CrossBuy.Models.Context.Accounting
{
	// Projects & Contracting — P6-ج: subcontractor / equipment-rental (subcontract + progress billing).
	// The subcontractor is an existing Vendor. Billing posts via PayableService ONLY (purchase invoice + retention
	// settlement payment into 2105) → ap_sub intact; cost Dr 510104 tagged ProjectId. No new accounting writer.
	public class Subcontract
	{
		public int ID { get; set; }
		public int CompanyID { get; set; }
		public int ProjectId { get; set; }              // → Project (P0)
		public int VendorId { get; set; }               // → Vendor (the subcontractor / equipment supplier)
		public string? Description { get; set; }
		public decimal? ContractValue { get; set; }
		public decimal? RetentionPercent { get; set; }  // retention withheld from the sub's bills (optional)
		public string Status { get; set; } = "Active";
		public DateTime? CreatedAt { get; set; }
		public int? CreatedBy { get; set; }
	}

	public class SubcontractBilling
	{
		public int ID { get; set; }
		public int CompanyID { get; set; }
		public int SubcontractId { get; set; }          // → Subcontract
		public int ProjectId { get; set; }
		public int VendorId { get; set; }
		public int BillingNo { get; set; }              // sequence per subcontract
		public DateTime BillingDate { get; set; }
		public string Status { get; set; } = "Draft";   // Draft / Approved / Posted
		public decimal CumulativeWork { get; set; }     // cumulative work value entered to date
		public decimal GrossWork { get; set; }          // W = cumulative − previously billed
		public decimal TaxRate { get; set; }
		public decimal TaxAmount { get; set; }          // T
		public decimal RetentionPercent { get; set; }
		public decimal RetentionAmount { get; set; }    // R
		public decimal NetPayable { get; set; }         // W + T − R
		public int? PurchaseInvoiceId { get; set; }
		public int? RetentionPaymentId { get; set; }
		public string? Note { get; set; }
		public DateTime? CreatedAt { get; set; }
		public int? CreatedBy { get; set; }
		public DateTime? PostedAt { get; set; }
		public int? PostedBy { get; set; }
	}
}
