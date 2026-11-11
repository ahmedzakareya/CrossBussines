using System.ComponentModel.DataAnnotations.Schema;

namespace CrossBuy.Models.Context.Admin
{
	public class LeavePolicies:BaseEntity
	{
        public int ID { get; set; }
        // HR0 (2026-11-11): the owning company. Filtered by CompanyQueryFilters and guarded on write; null = no company
        // (an orphan no company sees). Set by deploy/sql/hr0_company_isolation.sql for existing rows.
        public int? CompanyID { get; set; }
        public int LeavePolicyTypeID { get; set; } // اللوائح

        public int LeaveTypeID { get; set; }
        public int EntitlementDaysPerYear { get; set; } // عدد ايام الاجازه السنوية
        public int CarryOverLimit { get; set; } // اقصي عد ايام يمكن ترحيلها
        public int NoticePeriodDays { get; set; } // فترة الإخطار المطلوبة
        public bool? RequiresMedicalCertificate { get; set; }  // للاجازة المرضية هل مطلوب شهاده طبيه ؟

		[ForeignKey(nameof(LeavePolicyTypeID))]
		public Policies? Policies { get; set; }


		[ForeignKey(nameof(LeaveTypeID))]

		public LeaveTypes? leaveTypes { get; set; }



    }
}
