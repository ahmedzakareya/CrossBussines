using System.ComponentModel.DataAnnotations.Schema;

namespace CrossBuy.Models.Context.Admin
{
	/// خطوة اعتماد واحدة ضمن سلسلة اعتماد طلب الإجازة (متعددة المستويات).
	/// Level 1 = المدير المباشر , 2 = الأعلى منه , وهكذا.
	/// Status: 0 = معلّق , 1 = موافَق , 2 = مرفوض
	public class LeaveApprovalStep : BaseEntity
	{
		public int ID { get; set; }

		public int LeaveRequestID { get; set; }
		public int Level { get; set; }                 // ترتيب الخطوة في السلسلة (1 = الأدنى)
		public int ApproverEmployeeID { get; set; }    // المعتمِد لهذه الخطوة

		public int Status { get; set; } = 0;           // 0 معلّق / 1 موافَق / 2 مرفوض
		public DateTime? DecisionAt { get; set; }
		public string? DecisionNote { get; set; }

		[ForeignKey(nameof(LeaveRequestID))]
		public LeaveRequest? LeaveRequest { get; set; }

		[ForeignKey(nameof(ApproverEmployeeID))]
		public Employee? Approver { get; set; }
	}
}
