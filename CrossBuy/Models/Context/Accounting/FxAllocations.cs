namespace CrossBuy.Models.Context.Accounting
{
	// Multi-Currency 1-5: links a receipt to a sales invoice it settled, capturing the realized FX.
	public class ReceiptAllocation
	{
		public int ID { get; set; }
		public int CompanyID { get; set; }
		public int ReceiptId { get; set; }
		public int SalesInvoiceId { get; set; }
		public decimal ForeignAmount { get; set; }   // foreign settled against this invoice
		public decimal InvoiceRate { get; set; }      // invoice's rate (foreign→functional)
		public decimal ReceiptRate { get; set; }      // receipt's rate
		public decimal ArBase { get; set; }           // AR cleared at the invoice rate (ForeignAmount × InvoiceRate)
		public decimal FxDiff { get; set; }           // realized FX gain(+)/loss(-) on this allocation
		public DateTime? CreatedAt { get; set; }
	}

	// Multi-Currency 1-6: a period-end revaluation run (AR/AP open foreign balances → 4903/5903, auto-reversed next period).
	public class FxRevaluationRun
	{
		public int ID { get; set; }
		public int CompanyID { get; set; }
		public DateTime AsOfDate { get; set; }
		public string RateType { get; set; } = "Central";
		public string Status { get; set; } = "Posted";
		public decimal TotalArDiff { get; set; }
		public decimal TotalApDiff { get; set; }
		public decimal TotalBankDiff { get; set; }
		public int? JournalEntryId { get; set; }
		public int? ReversalEntryId { get; set; }
		public string? CreatedBy { get; set; }
		public DateTime? CreatedAt { get; set; }
	}

	// Multi-Currency 1-5: links a payment to a purchase invoice it settled, capturing the realized FX.
	public class PaymentAllocation
	{
		public int ID { get; set; }
		public int CompanyID { get; set; }
		public int PaymentId { get; set; }
		public int PurchaseInvoiceId { get; set; }
		public decimal ForeignAmount { get; set; }
		public decimal InvoiceRate { get; set; }
		public decimal PaymentRate { get; set; }
		public decimal ApBase { get; set; }
		public decimal FxDiff { get; set; }
		public DateTime? CreatedAt { get; set; }
	}
}
