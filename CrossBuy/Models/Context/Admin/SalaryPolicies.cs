using System.ComponentModel.DataAnnotations.Schema;

namespace CrossBuy.Models.Context.Admin
{
	public class SalaryPolicies : BaseEntity
	{
		public int ID { get; set; }
        public int LeavePolicyTypeID { get; set; }

        
		public string? Description { get; set; } // وصف اللائحة

		public decimal BaseSalary { get; set; } // الراتب الأساسي
		public decimal HousingAllowance { get; set; } // بدل السكن
		public decimal TransportationAllowance { get; set; } // بدل المواصلات
		public decimal OtherAllowances { get; set; } // بدلات أخرى

		public decimal OvertimeRate { get; set; } // نسبة حساب الساعات الإضافية
		public decimal LatePenaltyPerMinute { get; set; } // خصم عن كل دقيقة تأخير
		public decimal AbsencePenaltyPerDay { get; set; } // خصم عن كل يوم غياب بدون إذن

		public decimal SocialInsuranceEmployeeShare { get; set; } // نسبة التأمينات الاجتماعية على الموظف
		public decimal SocialInsuranceCompanyShare { get; set; } // نسبة التأمينات على الشركة
		public decimal TaxRate { get; set; } // نسبة ضريبة الدخل (إن وجدت)

		public bool IsTaxApplicable { get; set; } // هل تطبق عليه ضريبة دخل؟

		public int PaymentDay { get; set; } // اليوم الشهري لصرف الراتب (مثلاً يوم 25 أو 30)
		public string PaymentMethod { get; set; } // طريقة الدفع (تحويل بنكي - كاش)
		[ForeignKey(nameof(LeavePolicyTypeID))]
		public Policies Policies { get; set; }

		public ICollection<Employee> Employees { get; set; } // الموظفين المرتبطين بهذه اللائحة
	}

}
