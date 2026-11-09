namespace CrossBuy.Models.Context.Admin
{
	/// HR9 (2026-11-09) — an approver away for a period hands their approvals to a colleague (hr9_approval_delegations.sql).
	/// Applied when an approval chain is BUILT (LeaveWorkflowService.ApproverChainAsync, shared by leave and employee
	/// requests): requests raised in the window go to the delegate. Requests already waiting are not moved.
	public class ApprovalDelegation
	{
		public int ID { get; set; }
		public int CompanyID { get; set; }
		public int FromEmployeeID { get; set; }
		public int ToEmployeeID { get; set; }
		public DateTime StartDate { get; set; }
		public DateTime EndDate { get; set; }
		public string? Reason { get; set; }
		public bool IsActive { get; set; } = true;
		public DateTime CreatedAt { get; set; }
	}
}
