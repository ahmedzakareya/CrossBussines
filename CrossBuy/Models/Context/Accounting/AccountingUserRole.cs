namespace CrossBuy.Models.Context.Accounting
{
	// Accounting RBAC role assignment. Role: Accountant | ChiefAccountant | Auditor | Cashier
	public class AccountingUserRole
	{
		public int ID { get; set; }
		public int CompanyID { get; set; }
		public int EmployeeId { get; set; }
		public string Role { get; set; } = "";
		public DateTime? CreatedAt { get; set; }
	}
}
