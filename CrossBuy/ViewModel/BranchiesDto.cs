using CrossBuy.Models.Context.Admin;
using System.ComponentModel.DataAnnotations.Schema;

namespace CrossBuy.ViewModel
{
	public class BranchiesDto
	{
		public int ID { get; set; }

		public string Name { get; set; }
		public string NameAr { get; set; }

		public string Location { get; set; }


        public int CompanyID { get; set; }
        // Foreign Key
        public int CountryID { get; set; }

		[ForeignKey(nameof(CountryID))]
		public CountriesLookup? Country { get; set; } // Ensure the navigation property is singular and matches the foreign key.

		public string PhoneNumber { get; set; }
		public string Email { get; set; }

		public string? ImageUrl { get; set; }
		public IFormFile? Image { get; set; }
		public string Description { get; set; }
        public string? CompanyNameAr { get; set; }
        public string? CompanyNameEn { get; set; }

        public int? CompanyTypeID { get; set; }
		public string? CompanyTypeNameAr { get; set; }
		public string? CompanyTypeNameEn { get;set; }

        public List<CompanyDto>? companies { get; set; }

        public List<CountriesLookup>? countriesLookups { get; set; }
		public List<CrossBuy.Models.Context.Admin.Attachment>? AttachmentsFiles { get; set; }
		public List<IFormFile>? Attachments { get; set; }




	}
}
