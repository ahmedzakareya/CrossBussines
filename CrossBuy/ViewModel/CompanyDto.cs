using CrossBuy.Models.Context.Admin;

namespace CrossBuy.ViewModel
{
	public class CompanyDto
	{
		public int CompanyID { get; set; }
		public string? CompanyName { get; set; }
		public string? CompanyNameAr { get; set; }
		public string? Address { get; set; }
		public string? PhoneNumber { get; set; }
		public string? Email { get; set; }
		public string? PostalCode { get; set; }
		public string? Website { get; set; }
		public string? CountryName { get; set; }
		public string? CountryNameAr { get; set; }
		public string? CompanyTypeName { get; set; }
		public string? CompanyTypeNameEn { get; set; }

		public IFormFile? CompanyImage { get; set; }
		public string? CompanyImageUrl { get; set; }

		public string? RegistrationNumber { get; set; }
		public string? TaxNumber { get; set; }
		public string? Description { get; set; }
		public int? ParentCompany { get; set; }
		public string? ParentCompanyName { get; set; }
		public string ? ParentCompanyNameEn { get; set; }

		public int CountryID { get; set; }
		public int CompanyTypeId { get; set; }
		public int FormID { get; set; }
		public List<IFormFile>? Attachments { get; set; } 
		public List<CountriesLookup>?	 countriesLookups { get; set; }
		public List<Companies>? ChildCompanies { get; set; }
		public List<CompanyType>? companyTypes { get; set; }

		public List<CrossBuy.Models.Context.Admin.Attachment >? AttachmentsFiles { get; set; }

		// ---- first-administrator provisioning (new companies only) ----
		// A company is created with nobody able to administer it: the Employee row is the authority for
		// company (BusinessContextFactory) and there is no company switcher, so no existing user can act
		// inside a company they have no row in. These carry the administrator the company is born with.
		public string? AdministratorEmail { get; set; }
		public string? AdministratorFullName { get; set; }
		public string? AdministratorFullNameEn { get; set; }

		// ---- provisioning outcome (server-filled, never bound from the form) ----
		public bool AdministratorProvisioned { get; set; }
		public string? AdministratorTempPassword { get; set; }
		public string? AdministratorMessage { get; set; }
	}

}
