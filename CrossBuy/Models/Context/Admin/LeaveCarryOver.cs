namespace CrossBuy.Models.Context.Admin
{
	// Leave days carried forward into a year from the prior year's unused balance (HR carry-over).
	// Year = the year these days are AVAILABLE IN (i.e. prior year + 1). Capped by LeavePolicies.CarryOverLimit.
	public class LeaveCarryOver
	{
		public int ID { get; set; }
		public int CompanyID { get; set; }
		public int EmployeeID { get; set; }
		public int LeaveTypeID { get; set; }
		public int Year { get; set; }
		public int Days { get; set; }
		public DateTime? CreatedAt { get; set; }
	}
}
