namespace CrossBuy.Models.Context.Tasks
{
	// TM-3: a line of time logged against a task. Operational only — NO GL/stock (value comes in TM-4/TM-5/TM-6 via
	// existing services). Two sources: a live server-side Timer (StartedAt set, EndedAt null = running) or Manual entry.
	// A task's ActualHours is maintained = Σ Hours of its entries (never entered by hand — like the derived "Overdue").
	public class TimesheetEntry
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public int TaskId { get; set; }
		public int EmployeeId { get; set; }
		public DateTime WorkDate { get; set; }        // the day the work counts for
		public decimal Hours { get; set; }            // 0 while a Timer entry is still running
		public string? Description { get; set; }
		public string Source { get; set; } = "Manual"; // Timer | Manual
		public DateTime? StartedAt { get; set; }       // Timer only
		public DateTime? EndedAt { get; set; }         // Timer only (null = currently running)
		public bool IsBillable { get; set; }           // TM-5 flag (default false; reserved)
		public int? InvoicedInvoiceId { get; set; }    // TM-5: set to the sales invoice that billed this entry (null = not billed → double-billing guard)
		public DateTime CreatedAt { get; set; }
	}
}
