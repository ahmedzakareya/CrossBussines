namespace CrossBuy.Models.Context.Admin
{
	// HR-8 — ESS self-service request (one entity for two kinds). Approval chain mirrors LeaveRequest
	// (hierarchical, ascending to the nearest unit head). No GL impact.
	//  • Letter     → an HR letter (salary certificate / employment / experience / to-whom-it-may-concern),
	//                 printable AR/EN once approved; salary figures are pulled from the latest payslip.
	//  • Permission → an hourly permission (إذن) on a date; once approved it waives late minutes in that window.
	public class EmployeeRequest
	{
		public int ID { get; set; }
		public int CompanyID { get; set; }
		public int EmployeeID { get; set; }              // requester
		public string RequestType { get; set; } = "Letter";   // Letter | Permission

		// ---- Letter ----
		public string? LetterType { get; set; }          // Salary | Employment | Experience | ToWhom
		public string? Addressee { get; set; }           // "لمن يهمه الأمر" or a named entity/bank

		// ---- Permission (hourly) ----
		public DateTime? PermissionDate { get; set; }
		public TimeSpan? FromTime { get; set; }
		public TimeSpan? ToTime { get; set; }

		public string? Reason { get; set; }

		// ---- workflow (same shape as LeaveRequest) ----
		public int Status { get; set; } = 0;             // 0 pending / 1 approved / 2 rejected
		public int CurrentLevel { get; set; }            // pending level (0 = finished / no chain)
		public int? CurrentApproverEmployeeID { get; set; }
		public int? ApproverEmployeeID { get; set; }     // final decider
		public DateTime? DecisionAt { get; set; }
		public string? DecisionNote { get; set; }

		public DateTime? CreatedAt { get; set; }
		public int? CreatedBy { get; set; }
		public DateTime? UpdatedAt { get; set; }
		public int? updatedBy { get; set; }

		public List<EmployeeRequestStep> ApprovalSteps { get; set; } = new();
	}

	public class EmployeeRequestStep
	{
		public int ID { get; set; }
		public int EmployeeRequestID { get; set; }
		public int Level { get; set; }                   // 1 = first (direct manager)
		public int ApproverEmployeeID { get; set; }
		public int Status { get; set; } = 0;             // 0 pending / 1 approved / 2 rejected
		public DateTime? DecisionAt { get; set; }
		public string? DecisionNote { get; set; }
		public DateTime? CreatedAt { get; set; }
		public int? CreatedBy { get; set; }
		public DateTime? UpdatedAt { get; set; }
		public int? updatedBy { get; set; }

		public EmployeeRequest? Request { get; set; }
	}
}
