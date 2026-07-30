namespace CrossBuy.Models.Context.Admin
{
	// قسيمة راتب موظف لشهر — لقطة من تشغيل الرواتب (تشمل تعديلات الحضور)
	public class Payslip
	{
		public int ID { get; set; }
		public int CompanyID { get; set; }
		public int Year { get; set; }
		public int Month { get; set; }
		public int EmployeeID { get; set; }
		public string EmployeeName { get; set; } = "";
		public int? CostCenterId { get; set; }
		public decimal BaseSalary { get; set; }
		public decimal Allowances { get; set; }
		public decimal OvertimePay { get; set; }
		public decimal GrossEarnings { get; set; }       // الأساسي + البدلات + الإضافي
		public int LateMinutes { get; set; }
		public decimal LatePenalty { get; set; }
		public int AbsentDays { get; set; }
		public decimal AbsencePenalty { get; set; }
		public decimal SiEmployee { get; set; }
		public decimal SiCompany { get; set; }
		public decimal Tax { get; set; }
		public decimal Net { get; set; }
		public int? JournalEntryId { get; set; }
		public DateTime? CreatedAt { get; set; }
	}
}
