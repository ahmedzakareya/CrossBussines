namespace CrossBuy.ViewModel
{
	// Row model for the Employees list screen (mirrors the Branches list pattern).
	public class EmployeeListItemDto
	{
		public int ID { get; set; }
		public string? FullName { get; set; }
		public string? FullNameEn { get; set; }
		public string? ProfileImage { get; set; }
		public string? JobTitleAr { get; set; }
		public string? JobTitleEn { get; set; }
		public string? CompanyAr { get; set; }
		public string? CompanyEn { get; set; }
		public string? BranchAr { get; set; }
		public string? BranchEn { get; set; }
		public string? CountryAr { get; set; }
		public string? CountryEn { get; set; }
		public string? Email { get; set; }
		public string? PhoneNumber { get; set; }
		public bool IsActive { get; set; }
	}
}
