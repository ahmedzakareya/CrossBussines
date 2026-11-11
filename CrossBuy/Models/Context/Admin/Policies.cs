namespace CrossBuy.Models.Context.Admin
{
	public class Policies:BaseEntity
	{
        public int ID { get; set; }
        // HR0 (2026-11-11): the owning company. Filtered by CompanyQueryFilters and guarded on write; null = no company
        // (an orphan no company sees). Set by deploy/sql/hr0_company_isolation.sql for existing rows.
        public int? CompanyID { get; set; }
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
