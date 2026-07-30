using System.ComponentModel.DataAnnotations.Schema;

namespace CrossBuy.Models.Context.Admin
{
	public class LeavePolicies:BaseEntity
	{
        public int ID { get; set; }
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
