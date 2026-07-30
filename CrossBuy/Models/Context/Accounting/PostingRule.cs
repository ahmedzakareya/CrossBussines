namespace CrossBuy.Models.Context.Accounting
{
	/// قاعدة ترحيل — maps a source document component (e.g. a payroll component) to GL accounts,
	/// so the posting bridge can build journal entries without hard-coding account ids.
	public class PostingRule
	{
		public int ID { get; set; }
		public int CompanyID { get; set; }
		public string SourceType { get; set; } = "";      // Payroll / ...
		public string ComponentCode { get; set; } = "";    // BasicSalary/Allowance/SocialInsCompany/IncomeTax/SocialInsPayable/NetPay
		public int? DebitAccountId { get; set; }
		public int? CreditAccountId { get; set; }
		public string? CostCenterSource { get; set; }       // EmployeeDepartment / Fixed
		public DateTime? CreatedAt { get; set; }
	}
}
