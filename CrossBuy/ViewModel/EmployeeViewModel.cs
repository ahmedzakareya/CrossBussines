namespace CrossBuy.ViewModel
{
	public class EmployeeViewModel
	{
		public int ID { get; set; } 

		public string FirstName { get; set; }
		public string LastName { get; set; }
		public string FullName { get; set; }
		public string FullNameEn { get; set; }

		public string Address { get; set; } 
		public string PhoneNumber { get; set; } 
		public string Email { get; set; }
		public string UserId { get; set; } 

		public string ProfileImage { get; set; }

		public string Gender { get; set; }

		public string MaritalStatus { get; set; }

		public DateTime? DateOfBirth { get; set; }

		public DateTime? DateOfJoining { get; set; }

		public int? JobTitleID { get; set; }

		public int? BranchID { get; set; }

		public int? EmpCompanyID { get; set; }

		public int? DepartmentID { get; set; }

		public int? AdministrativeStructureID { get; set; }

		public int? CountryID { get; set; }

		public string? EmploymentType { get; set; }

		public int? PolicyID { get; set; }

		public decimal? ManufHourlyRate { get; set; }   // سعر ساعة التصنيع (لتحميل عمالة الموظف على أوامر التشغيل)
	}
}
