using System.ComponentModel.DataAnnotations.Schema;

namespace CrossBuy.Models.Context.Admin
{
	public class Branch : BaseEntity
	{
		public int ID { get; set; }

		public string Name { get; set; }
		public string NameAr { get; set; }

		public string Location { get; set; }

		// Foreign Key
		public int CountryID { get; set; }

		[ForeignKey(nameof(CountryID))]
		public CountriesLookup Country { get; set; } // Ensure the navigation property is singular and matches the foreign key.
		


		[ForeignKey(nameof(CompanyID))]
		public int CompanyID { get; set; }
        public Companies? Company { get; set; }
        public string PhoneNumber { get; set; }
		public string Email { get; set; }

		public string? ImageUrl { get; set; }

		public string Description { get; set; }

		// Multi-Currency (1-1): the branch functional currency (books, statements, filing).
		// Stable: editable until the first transaction, then locked (CurrencyLockedAt set).
		public int? FunctionalCurrencyId { get; set; }
		public DateTime? CurrencyLockedAt { get; set; }

		// Brand foundation: the trade name (Brand) this location belongs to. Nullable = no brand (default);
		// existing branches stay NULL and behave exactly as before. Never made required.
		public int? BrandId { get; set; }

		// Operations platform: activity-type preset (Restaurant/Cafe/Hyper/Retail). Nullable = not an operations location.
		public string? ActivityPresetCode { get; set; }
	}

}
