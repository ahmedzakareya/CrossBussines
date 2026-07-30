using System.ComponentModel.DataAnnotations.Schema;

namespace CrossBuy.Models.Context.Accounting
{
	// Projects & Contracting — P4 progress billing (المستخلص). Built from a Confirmed ProjectProgress.
	// CUMULATIVE: period work = cumulative executed − previously billed (no double-count). Posted ONLY via
	// ReceivableService (invoice + two settlement receipts into 1104/2104) + JournalEntryService — no new writer.
	// Gross W + Tax T → invoice; Retention R → Dr 1104; Advance recovery A → Dr 2104; net due = W+T−R−A. ProjectId tagged.
	public class ProgressBilling
	{
		public int ID { get; set; }
		public int CompanyID { get; set; }
		public int ProjectId { get; set; }                 // → Project (P0)
		public int ProgressId { get; set; }                // → the Confirmed ProjectProgress being billed
		public int? CustomerId { get; set; }               // from the project (invoice/receipts party)
		public int BillingNo { get; set; }                 // sequence per project
		public DateTime BillingDate { get; set; }
		public string Status { get; set; } = "Draft";      // Draft (edit) / Approved (locked) / Posted (GL created)
		public decimal GrossWork { get; set; }             // W — period work value
		public decimal TaxRate { get; set; }
		public decimal TaxAmount { get; set; }             // T = W × TaxRate
		public decimal RetentionPercent { get; set; }
		public decimal RetentionAmount { get; set; }       // R = W × RetentionPercent (Dr 1104)
		public decimal AdvanceRecoveryAmount { get; set; } // A = min(W × AdvancePercent, remaining 2104) (Dr 2104)
		public decimal NetDue { get; set; }                // W + T − R − A
		public int? SalesInvoiceId { get; set; }
		public int? RetentionReceiptId { get; set; }
		public int? AdvanceReceiptId { get; set; }
		public string? Note { get; set; }
		public DateTime? CreatedAt { get; set; }
		public int? CreatedBy { get; set; }
		public DateTime? PostedAt { get; set; }
		public int? PostedBy { get; set; }

		public List<ProgressBillingLine> Lines { get; set; } = new();
	}

	public class ProgressBillingLine
	{
		public int ID { get; set; }
		public int BillingId { get; set; }                 // → ProgressBilling
		public int? BoqItemId { get; set; }                // → BoqItem (null = whole-project no-BOQ line)
		public decimal CumulativeExecutedValue { get; set; }   // from the measurement
		public decimal PreviouslyBilledValue { get; set; }     // Σ prior posted billings for this item
		public decimal PeriodValue { get; set; }               // cumulative − previously billed (this billing's work)

		[ForeignKey(nameof(BillingId))]
		public ProgressBilling? Billing { get; set; }
	}
}
