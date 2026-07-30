namespace CrossBuy.Models.Context.Tasks
{
	// TM-1: the core Task entity. Operational only — NO GL/stock effect (accounting value comes later via existing
	// services in TM-4/TM-5). Named "TaskItem" (not "Task") to avoid clashing with System.Threading.Tasks.Task.
	// The polymorphic link (EntityType/EntityId), timesheet, and billing columns are added in their own phases (TM-2/TM-3/TM-5).
	public class TaskItem
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public string Title { get; set; } = "";
		public string? TitleEn { get; set; }               // English twin (shown when UI is not Arabic; falls back to Title)
		public string? Description { get; set; }
		public int AssigneeEmployeeId { get; set; }        // who does it
		public int CreatedByEmployeeId { get; set; }       // who created it
		public string Priority { get; set; } = "Normal";   // Low | Normal | High | Urgent
		public DateTime? DueDate { get; set; }
		public string Status { get; set; } = "New";         // New | InProgress | Done  ("Overdue" is DERIVED, never stored)
		public decimal? EstimatedHours { get; set; }
		public decimal ActualHours { get; set; }            // Σ timesheet lines (filled in TM-3); stays 0 in TM-1
		public int ProgressPct { get; set; }                // 0..100 completion percent (auto 100 when Done)
		public string? Category { get; set; }               // optional free tag/label shown as a chip on the task (e.g. "اختبار الأداء")
		// TM-2: OPTIONAL polymorphic link to any system record (same pattern as Crm.Activity.EntityType/EntityId,
		// JournalEntry.SourceType/SourceId). Both null = an unlinked task (still perfectly valid). No DB FK.
		public string? EntityType { get; set; }             // SalesInvoice | Customer | ManufWorkOrder | PosOrder | Employee | Project | Item
		public int? EntityId { get; set; }
		// TM-4: set once when this task's labor is posted to its linked work order (via ManufService.AddLaborAsync). Guards
		// against double-posting. Null = not yet posted. No GL here — this is just the posting marker.
		public DateTime? LaborPostedAt { get; set; }
		// TM-5: optional billing. When IsBillable, the task's uninvoiced hours can be billed to CustomerId at BillRate/hour
		// via the EXISTING ReceivableService (service line, no stock). Double-billing is prevented per-entry (InvoicedInvoiceId).
		public bool IsBillable { get; set; }
		public decimal? BillRate { get; set; }
		public int? CustomerId { get; set; }
		public DateTime CreatedAt { get; set; }
		public DateTime? CompletedAt { get; set; }
		// TM-9 (scheduled task): a task with a future due date + EXPECTED movement criteria but NO link yet (the movement
		// hasn't happened). When a matching movement is later created, TM-9-ب links it (EntityType/EntityId) retroactively.
		// All nullable/additive — a normal task leaves these untouched. Operational only; matching NEVER creates a movement.
		public bool IsScheduled { get; set; }                 // true = waiting for a matching movement
		public string? ExpectedEntityType { get; set; }       // PurchaseInvoice | SalesInvoice (TM-9-أ scope)
		public string? ExpectedPartyType { get; set; }        // Supplier | Customer (derived from the movement type)
		public int? ExpectedPartyId { get; set; }             // Vendor.ID or Customer.ID
		public DateTime? ExpectedFrom { get; set; }           // expected time window (optional, narrows matching)
		public DateTime? ExpectedTo { get; set; }
		public DateTime? MatchedAt { get; set; }              // set by TM-9-ب when auto-linked; null = still scheduled
	}
}
