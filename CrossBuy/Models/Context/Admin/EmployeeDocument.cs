namespace CrossBuy.Models.Context.Admin
{
	// A classified employee document in the HR vault (HR-5), with optional expiry for alerts.
	public class EmployeeDocument
	{
		public int ID { get; set; }
		public int CompanyID { get; set; }
		public int EmployeeID { get; set; }
		public string DocType { get; set; } = "Other";  // NationalId | Passport | Qualification | Contract | Insurance | WorkPermit | Other
		public string? DocNumber { get; set; }
		public string? FilePath { get; set; }
		public DateTime? IssueDate { get; set; }
		public DateTime? ExpiryDate { get; set; }
		public string? Notes { get; set; }
		public DateTime? CreatedAt { get; set; }
	}
}
