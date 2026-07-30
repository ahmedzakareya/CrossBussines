using System.ComponentModel.DataAnnotations.Schema;

namespace CrossBuy.Models.Context.Admin
{
	/// طلب إجازة يقدّمه الموظف ويعتمده مديره المباشر
	/// Status: 0 = معلّق (Pending) , 1 = موافَق عليه (Approved) , 2 = مرفوض (Rejected)
	public class LeaveRequest : BaseEntity
	{
		public int ID { get; set; }

		public int EmployeeID { get; set; }          // مقدّم الطلب
		public int LeaveTypeID { get; set; }         // نوع الإجازة

		public DateTime StartDate { get; set; }
		public DateTime EndDate { get; set; }
		public int Days { get; set; }                // عدد أيام الإجازة

		public string? Reason { get; set; }          // سبب الطلب

		public int Status { get; set; } = 0;         // 0 معلّق / 1 موافَق / 2 مرفوض

		public int? ApproverEmployeeID { get; set; } // المعتمِد النهائي (آخر من بتّ في الطلب)
		public DateTime? DecisionAt { get; set; }    // وقت القرار النهائي
		public string? DecisionNote { get; set; }    // ملاحظة آخر معتمِد

		// --- سلسلة الاعتماد متعددة المستويات ---
		public int CurrentLevel { get; set; }              // المستوى المعلّق حاليًا (0 = منتهٍ / لا سلسلة)
		public int? CurrentApproverEmployeeID { get; set; } // من دوره يعتمد الآن (null عند الانتهاء)

		[ForeignKey(nameof(EmployeeID))]
		public Employee? Employee { get; set; }

		[ForeignKey(nameof(LeaveTypeID))]
		public LeaveTypes? LeaveType { get; set; }

		public ICollection<LeaveApprovalStep> ApprovalSteps { get; set; } = new List<LeaveApprovalStep>();
	}
}
