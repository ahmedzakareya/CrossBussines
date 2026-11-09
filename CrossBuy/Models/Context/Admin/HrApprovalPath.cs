namespace CrossBuy.Models.Context.Admin
{
	/// An approval path a company designs for one request kind (hr10_approval_paths.sql). It SHAPES the manager chain the
	/// org tree produces (HrApprovalPathService.Shape); it never replaces the safeguards the chain builder enforces.
	public class HrApprovalPath
	{
		public int ID { get; set; }
		public int CompanyID { get; set; }
		public string Kind { get; set; } = "Leave";       // Leave | Letter | Permission | Advance | ProfileChange
		public int? LeaveTypeID { get; set; }             // Leave only; null = every leave type
		public int ManagerLevels { get; set; }            // 0 = up to the unit head
		public int DirectOnlyUpToDays { get; set; }       // Leave: a request of this many days or fewer needs the direct manager only; 0 = off
		public int? FinalApproverEmployeeID { get; set; } // an HR officer appended as the last approver
		public bool IsActive { get; set; } = true;
		public DateTime UpdatedAt { get; set; }
		public int? UpdatedByEmployeeID { get; set; }
	}
}
