namespace CrossBuy.Models.Context.Admin
{
	// An employee's employment contract (HR-5). EndDate null = open-ended/permanent.
	public class EmploymentContract
	{
		public int ID { get; set; }
		public int CompanyID { get; set; }
		public int EmployeeID { get; set; }
		public string ContractType { get; set; } = "Permanent";  // Permanent | FixedTerm | Probation | PartTime
		public DateTime StartDate { get; set; }
		public DateTime? EndDate { get; set; }
		public string Status { get; set; } = "Active";           // Active | Expired | Terminated
		public string? FilePath { get; set; }                     // optional scanned contract
		public string? Notes { get; set; }
		public DateTime? CreatedAt { get; set; }
	}
}
