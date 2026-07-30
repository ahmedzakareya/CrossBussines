namespace CrossBuy.Models.Context.Tasks
{
	// TM-9-ب: when a scheduled task has MORE THAN ONE matching movement, the matcher records the candidates here instead of
	// auto-linking — a manager confirms the right one later (TM-9-ج). Operational only; this row is just a pointer to an
	// EXISTING movement (EntityType/EntityId, same as TM-2) — no GL, no movement is ever created. Unique per (task, movement)
	// so a re-scan never duplicates a suggestion (the double-suggestion guard, like TaskAutoLog).
	public class TaskMatchSuggestion
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public int TaskId { get; set; }             // the scheduled task
		public string EntityType { get; set; } = ""; // candidate movement type (PurchaseInvoice | SalesInvoice)
		public int EntityId { get; set; }            // candidate movement id
		public string? Label { get; set; }          // movement label (invoice no) for the review screen
		public DateTime CreatedAt { get; set; }
		public DateTime? ResolvedAt { get; set; }    // TM-9-ج: set when the manager links this one or dismisses it
	}
}
