namespace CrossBuy.ViewModel
{
	public class LeavePoliciesDto
	{
		public int ID { get; set; }
		public int LeavePolicyTypeID { get; set; } // اللوائح
		public int EntitlementDaysPerYear { get; set; } // عدد ايام الاجازه السنوية
		public int CarryOverLimit { get; set; } // اقصي عد ايام يمكن ترحيلها
		public int NoticePeriodDays { get; set; } // فترة الإخطار المطلوبة
		public bool? RequiresMedicalCertificate { get; set; }  // للاجازة المرضية هل مطلوب شهاده طبيه ؟
        public int? LeaveTypeID { get; set; }
        public LeaveTypesDto leaveTypes { get; set; }


    }
}
