namespace CrossBuy.Models.Context.Admin
{
	/// HR5 (2026-11-08) — one employee's overtime decision for one month (hr5_overtime_approvals.sql). When the rule pack
	/// requires approval, payroll pays these minutes (by day type) and nothing that was not approved.
	public class OvertimeApproval
	{
		public int ID { get; set; }
		public int CompanyID { get; set; }
		public int EmployeeID { get; set; }
		public int Year { get; set; }
		public int Month { get; set; }
		public string Status { get; set; } = "Approved";   // Approved | Rejected
		public int WorkingMinutes { get; set; }
		public int RestDayMinutes { get; set; }
		public int HolidayMinutes { get; set; }
		public int RequestedMinutes { get; set; }          // what attendance showed when decided (the yearly cap may approve less)
		public string? Note { get; set; }
		public int? DecidedByEmployeeId { get; set; }
		public DateTime DecidedAt { get; set; }
		public int TotalMinutes => WorkingMinutes + RestDayMinutes + HolidayMinutes;
	}
}
