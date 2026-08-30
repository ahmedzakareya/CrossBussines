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
		public string Status { get; set; } = ProgressBillingStatuses.Draft;
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
		// ---- lifecycle actor evidence ----
		//
		// CreatedBy is the PREPARER and is written once. Editing a draft updates UpdatedBy/UpdatedAt and
		// leaves CreatedBy alone, because the separation-of-duties rule is ApprovedBy != CreatedBy: if an
		// edit could rewrite CreatedBy, the preparer could make themselves eligible to approve their own
		// billing by touching it once. That is the whole reason these are two distinct pairs.
		public DateTime? CreatedAt { get; set; }
		public int? CreatedBy { get; set; }
		public DateTime? UpdatedAt { get; set; }
		public int? UpdatedBy { get; set; }
		public DateTime? SubmittedAt { get; set; }
		public int? SubmittedBy { get; set; }
		public DateTime? ApprovedAt { get; set; }
		public int? ApprovedBy { get; set; }
		public DateTime? PostedAt { get; set; }
		public int? PostedBy { get; set; }

		public List<ProgressBillingLine> Lines { get; set; } = new();
	}

	// The billing lifecycle. STRINGS, not an enum, because the column is nvarchar(20) and rows written
	// before this batch already carry these literals - an enum would need a mapping layer to say the same
	// thing. Draft, Approved and Posted keep their exact historical spelling so existing rows stay legal.
	public static class ProgressBillingStatuses
	{
		public const string Draft = "Draft";          // being prepared; editable
		public const string Submitted = "Submitted";  // handed to an approver; no longer an ordinary draft
		public const string Returned = "Returned";    // sent back by an approver; editable again
		public const string Approved = "Approved";    // authorized to post; locked
		public const string Posted = "Posted";        // GL effects exist; immutable

		public static readonly IReadOnlyList<string> All =
			new[] { Draft, Submitted, Returned, Approved, Posted };

		// The ONLY legal moves. Read it as a table rather than scattered if-statements, so the state machine
		// can be asserted directly by a test instead of inferred from the services that use it.
		public static readonly IReadOnlyDictionary<string, string[]> LegalNext =
			new Dictionary<string, string[]>(StringComparer.Ordinal)
			{
				[Draft]     = new[] { Submitted },
				[Submitted] = new[] { Approved, Returned },
				[Returned]  = new[] { Submitted },
				[Approved]  = new[] { Posted },
				[Posted]    = Array.Empty<string>(),   // terminal in this batch; reversal is Product Batch 2
			};

		public static bool CanMove(string? from, string to) =>
			from != null && LegalNext.TryGetValue(from, out var next)
			&& Array.IndexOf(next, to) >= 0;

		// Draft and Returned are the two editable states - Returned exists precisely so an approver can put
		// a billing back into the preparer's hands without deleting it.
		public static bool IsEditable(string? status) =>
			status == Draft || status == Returned;
	}

	// The lifecycle transitions worth telling the rest of the platform about.
	//
	// Save and Edit are deliberately NOT here. An event is a business fact with a consumer; a draft
	// being edited has neither, and emitting one would bury the four that matter in noise.
	public static class ProgressBillingEvents
	{
		public const string Submitted = "Submitted";
		public const string Returned = "Returned";
		public const string Approved = "Approved";
		public const string Posted = "Posted";
	}

	// The wire payload. VERSIONED, and deliberately identifiers-and-amounts only: no file paths, no
	// session data, no EF entities. A consumer that needs the document fetches it through its own
	// authorization rather than reading it out of an event it was handed.
	public sealed class ProgressBillingEventPayload
	{
		public const int Version = 1;

		public int BillingId { get; init; }
		public int BillingNo { get; init; }
		public int ProjectId { get; init; }
		public int ProgressId { get; init; }
		public int? CustomerId { get; init; }
		public string Status { get; init; } = "";
		public decimal GrossWork { get; init; }
		public decimal NetDue { get; init; }
		public int ActorEmployeeId { get; init; }

		// Present only once posting has created them; null on the earlier transitions.
		public int? SalesInvoiceId { get; init; }
		public int? RetentionReceiptId { get; init; }
		public int? AdvanceReceiptId { get; init; }
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
