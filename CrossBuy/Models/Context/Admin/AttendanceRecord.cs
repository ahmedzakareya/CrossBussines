namespace CrossBuy.Models.Context.Admin
{
	// سجل حضور يومي لموظف — مصدره ويب/موبايل/جهاز/يدوي. القيم المحسوبة (تأخير/إضافي/غياب)
	// تُحسب من سياسة الحضور (AttendancePolicies) مع استبعاد العطلات الرسمية وأيام الراحة والإجازات.
	public class AttendanceRecord
	{
		public int ID { get; set; }
		public int CompanyID { get; set; }
		public int EmployeeID { get; set; }
		public DateTime WorkDate { get; set; }          // اليوم (التاريخ فقط)
		public DateTime? CheckIn { get; set; }
		public DateTime? CheckOut { get; set; }
		public string Source { get; set; } = "Web";     // Web | Mobile | Device | Manual
		public int LateMinutes { get; set; }
		public int EarlyLeaveMinutes { get; set; }
		public int OvertimeMinutes { get; set; }
		public decimal WorkedHours { get; set; }
		public string Status { get; set; } = "Present";  // Present | Late | Absent | Holiday | RestDay | Leave
		public string? Notes { get; set; }
		public string? CreatedBy { get; set; }
		public DateTime? CreatedAt { get; set; }
	}
}
