namespace CrossBuy.Models.Context.Admin
{
	// A cash payout of unused leave days (HR-2f). Counts against the employee's remaining balance.
	public class LeaveEncashment
	{
		public int ID { get; set; }
		public int CompanyID { get; set; }
		public int EmployeeID { get; set; }
		public string? EmployeeName { get; set; }
		public int LeaveTypeID { get; set; }
		public int Year { get; set; }
		public int Days { get; set; }
		public decimal DailyRate { get; set; }
		public decimal Amount { get; set; }
		public DateTime EncashDate { get; set; }
		public int? JournalEntryId { get; set; }
		public DateTime? CreatedAt { get; set; }
	}
}
