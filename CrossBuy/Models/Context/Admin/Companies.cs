using System.ComponentModel.DataAnnotations;
using System.Diagnostics.Metrics;
using static Microsoft.EntityFrameworkCore.DbLoggerCategory.Database;

namespace CrossBuy.Models.Context.Admin
{
	public class Companies:BaseEntity
	{
		[Key]
		public int CompanyID { get; set; } 
		public string? CompanyName { get; set; }
		public string? ComoanyNameAr { get; set; }
		public string Address { get; set; } 
		public string PhoneNumber { get; set; } 
		public string Email { get; set; }

		public string? PostalCode { get; set; }

		public string? Website { get; set; }
		public int CountryID { get; set; } 
		public virtual CountriesLookup Country { get; set; } 
		public string? CompanyImage { get; set; } 


		public int CompanyTypeId { get; set; }
		public CompanyType CompanyType { get; set; }

		public virtual ICollection<Employee> Employees { get; set; }

		public string? RegistrationNumber { get; set; }
		public string? TaxNumber { get; set; }

		// HM-9 slice 2: loyalty earn rate — points earned per 1 unit of the sale's DOCUMENT currency on the NET (post-discount)
		// eligible amount. Company-level DEFAULT; a branch may override via BranchPosSetting.LoyaltyPointsPerCurrencyUnit.
		// Nullable → no company default (a branch with no override earns nothing). Data, not code (the end-customer sets it).
		public decimal? LoyaltyPointsPerCurrencyUnit { get; set; }

		public string? Description { get; set; }

		public int? ParentCompany { get; set; }
		public virtual Companies Parent { get; set; }
		public virtual ICollection<Companies> ChildCompanies { get; set; }

		// Multi-Currency (1-1): default currency inherited by new branches.
		public int? DefaultCurrencyId { get; set; }

	}

}
