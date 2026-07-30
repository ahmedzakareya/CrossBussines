namespace CrossBuy.Models.Context.Admin
{
	public class Policies:BaseEntity
	{
        public int ID { get; set; }
        public string? NameAr { get; set; }
        public string? NameEn { get; set; }
        public string? Notes { get; set; }

        /// عدد مستويات اعتماد الإجازة المطلوبة صعودًا في السلسلة الإدارية (افتراضي 2).
        public int? ApprovalLevels { get; set; }

		public ICollection<LeavePolicies> LeavePolicies { get; set; }
        public ICollection<AttendancePolicies> AttendancePolicies { get; set; }
        public ICollection<SalaryPolicies> salaryPolicies { get; set; }



    }
}
