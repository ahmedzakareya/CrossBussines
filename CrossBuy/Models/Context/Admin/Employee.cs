using static Microsoft.EntityFrameworkCore.DbLoggerCategory.Database;
using System.Diagnostics.Metrics;
using Microsoft.AspNetCore.Identity;
using System.ComponentModel.DataAnnotations;

namespace CrossBuy.Models.Context.Admin
{
	public class Employee : BaseEntity
	{
		[Key]
		public int ID { get; set; } // معرّف الموظف

		public string FirstName { get; set; } // الاسم الأول
		public string LastName { get; set; } // الاسم الأخير
		public string FullName { get; set; } // الاسم الكامل (عربي)
		public string? FullNameEn { get; set; } // الاسم الكامل (إنجليزي)

		public string Address { get; set; } // العنوان
		public string PhoneNumber { get; set; } // رقم الهاتف
		public string Email { get; set; } // البريد الإلكتروني

		public int? CountryID { get; set; } // معرّف الدولة
		public CountriesLookup Country { get; set; } // الربط مع جدول الدول

		public int JobTitleID { get; set; } // معرّف المسمي الوظيفي
		public JobTitle JobTitle { get; set; } // الربط مع جدول المسميات الوظيفية

		public int? BranchID { get; set; } // معرّف الفرع
		public Branch Branch { get; set; } // الربط مع جدول الفروع

		public int EmpCompanyID { get; set; } // معرّف الشركة
		public Companies Company { get; set; } // الربط مع جدول الشركات

		public string ProfileImage { get; set; } // صورة الموظف

		public DateTime DateOfBirth { get; set; } // تاريخ الميلاد
		public string Gender { get; set; } // الجنس
		public string MaritalStatus { get; set; } // الحالة الاجتماعية
		public DateTime DateOfJoining { get; set; } // تاريخ الانضمام
		public bool IsActive { get; set; } // حالة النشاط

		public int? DepartmentID { get; set; }      // → Hierarchical (عقدة الإدارة)
		public string? EmploymentType { get; set; } // نوع التوظيف (دوام كامل/جزئي...)

		public decimal? ManufHourlyRate { get; set; } // سعر ساعة التصنيع الصريح (يتجاوز اشتقاق الراتب)

		public string UserId { get; set; }
		public Users User { get; set; }

		public ICollection<PolicyAssignments> policyAssignments { get; set; }

	}


}
