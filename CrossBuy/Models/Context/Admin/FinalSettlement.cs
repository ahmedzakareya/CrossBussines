namespace CrossBuy.Models.Context.Admin
{
	// End-of-service settlement for a terminated employee (HR-7).
	public class FinalSettlement
	{
		public int ID { get; set; }
		public int CompanyID { get; set; }
		public int EmployeeID { get; set; }
		public string? EmployeeName { get; set; }
		public DateTime TerminationDate { get; set; }
		public string? Reason { get; set; }
		public decimal ServiceYears { get; set; }
		public int LeaveDays { get; set; }
		public decimal LeaveValue { get; set; }
		public decimal Gratuity { get; set; }
		public decimal OtherEarnings { get; set; }
		public decimal Deductions { get; set; }
		public decimal NetSettlement { get; set; }   // gross − deductions
		public int? JournalEntryId { get; set; }
		public DateTime? CreatedAt { get; set; }
	}
}
