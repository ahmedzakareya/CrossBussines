namespace CrossBuy.Models.Context.Admin
{
	// A period-end snapshot of the earned-but-untaken leave liability (HR-2f).
	// Adjustment = signed amount posted to bring the provision liability (210206) to TotalAmount.
	public class LeaveProvisionRun
	{
		public int ID { get; set; }
		public int CompanyID { get; set; }
		public DateTime AsOfDate { get; set; }
		public decimal TotalDays { get; set; }
		public decimal TotalAmount { get; set; }
		public decimal Adjustment { get; set; }
		public int? JournalEntryId { get; set; }
		public DateTime? CreatedAt { get; set; }
	}
}
