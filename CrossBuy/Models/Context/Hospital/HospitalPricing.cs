using Microsoft.EntityFrameworkCore;

namespace CrossBuy.Models.Context.Hospital
{
	// ==========================================================================================
	// HOSPITAL PRICE LISTS (unit H7).
	//
	// The MAIN list is not stored: it IS the selling price on each card — HospitalService.Price for a
	// service, Inventory Item.SalesPrice for a medicine or consumable (an inventory item belongs to the
	// hospital catalogue when its category is mapped in HospitalItemCategories). A new card is therefore
	// on the main list the moment it exists, and a changed price is the main price at once.
	//
	// Every other list is a HospitalPriceList: ONE currency (null = the facility's), a validity, a
	// version and a discount rule (services / medicines / consumables %). Its lines name a service or an
	// item. A line without a fixed price follows the rule from the CURRENT main price; a line is "not
	// updated" while it awaits review, or once the main price moved away from the price it was confirmed
	// against. A list cannot be approved while any line is not updated, and only an approved list prices
	// a bill. deploy/sql/hospital_h7_pricelists.sql builds the tables.
	// ==========================================================================================

	public static class HospitalPriceListKinds
	{
		public const string Cash = "Cash";
		public const string Insurer = "Insurer";
		public const string Corporate = "Corporate";
		public const string Government = "Government";
		public static readonly IReadOnlyList<string> All = new[] { Cash, Insurer, Corporate, Government };
		public static bool IsKnown(string? s) => s != null && All.Contains(s, StringComparer.Ordinal);
	}

	public static class HospitalPriceListStatus
	{
		public const string Draft = "Draft";
		public const string Active = "Active";
		public const string Archived = "Archived";
	}

	/// Which percentage of the discount rule a catalogue entry takes.
	public static class HospitalPriceGroups
	{
		public const string Service = "Service";
		public const string Medicine = "Medicine";
		public const string Consumable = "Consumable";
		public static readonly IReadOnlyList<string> All = new[] { Service, Medicine, Consumable };
		/// What an inventory category can be mapped as.
		public static readonly IReadOnlyList<string> ItemKinds = new[] { Medicine, Consumable };
	}

	public class HospitalPriceList
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public string Code { get; set; } = "";
		public string Name { get; set; } = "";
		public string NameEn { get; set; } = "";
		public string Kind { get; set; } = HospitalPriceListKinds.Insurer;
		/// Null = the facility's currency.
		public int? CurrencyId { get; set; }
		/// List-currency units per one facility unit, frozen when the list (version) was built, so its
		/// prices do not move with the daily rate. 1 when the list is in the facility's currency.
		public decimal FrozenRate { get; set; } = 1;
		public decimal ServicesAdjPct { get; set; }
		public decimal MedicinesAdjPct { get; set; }
		public decimal ConsumablesAdjPct { get; set; }
		/// A service price snaps to this step (e.g. 0.250, 5); null = the currency's decimals.
		public decimal? RoundingStep { get; set; }
		public DateTime ValidFrom { get; set; }
		public DateTime? ValidTo { get; set; }
		public int Version { get; set; } = 1;
		public int? PreviousVersionId { get; set; }
		public string Status { get; set; } = HospitalPriceListStatus.Draft;
		public DateTime CreatedAt { get; set; }
		public int? CreatedByEmployeeId { get; set; }
		public DateTime? ApprovedAt { get; set; }
		public int? ApprovedByEmployeeId { get; set; }
	}

	public class HospitalPriceListLine
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public int PriceListId { get; set; }
		public int? ServiceId { get; set; }
		public int? ItemId { get; set; }
		/// A price of its own in the list's currency (an override, or a payer's tariff); null = the rule.
		public decimal? FixedPrice { get; set; }
		/// Not covered by this list: billed from the main list, to the patient.
		public bool IsExcluded { get; set; }
		/// Copied or added and not yet reviewed.
		public bool NeedsReview { get; set; } = true;
		/// The main price (facility currency) the line was last confirmed against.
		public decimal? ConfirmedMainPrice { get; set; }
		public DateTime AddedAt { get; set; }
		public DateTime? ReviewedAt { get; set; }
		public int? ReviewedByEmployeeId { get; set; }
	}

	/// An inventory category whose items are hospital medicines or consumables.
	public class HospitalItemCategory
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public int ItemCategoryId { get; set; }
		public string Kind { get; set; } = HospitalPriceGroups.Medicine;
	}

	/// The regulator's approved public price of a medicine (facility currency): no list may charge more.
	public class HospitalItemPublicPrice
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public int ItemId { get; set; }
		public decimal PublicPrice { get; set; }
		public DateTime UpdatedAt { get; set; }
		public int? UpdatedByEmployeeId { get; set; }
	}

	public static class HospitalPricingModel
	{
		public static void Configure(ModelBuilder b)
		{
			b.Entity<HospitalPriceList>(e =>
			{
				e.Property(x => x.FrozenRate).HasPrecision(18, 8);
				e.Property(x => x.ServicesAdjPct).HasPrecision(7, 2);
				e.Property(x => x.MedicinesAdjPct).HasPrecision(7, 2);
				e.Property(x => x.ConsumablesAdjPct).HasPrecision(7, 2);
				e.Property(x => x.RoundingStep).HasPrecision(18, 4);
				e.HasIndex(x => new { x.CompanyId, x.Code, x.Version }).IsUnique();
			});
			b.Entity<HospitalPriceListLine>(e =>
			{
				e.Property(x => x.FixedPrice).HasPrecision(18, 4);
				e.Property(x => x.ConfirmedMainPrice).HasPrecision(18, 4);
				e.HasOne<HospitalPriceList>().WithMany().HasForeignKey(x => x.PriceListId).OnDelete(DeleteBehavior.NoAction);
				e.HasIndex(x => new { x.PriceListId, x.ServiceId }).IsUnique().HasFilter("[ServiceId] IS NOT NULL");
				e.HasIndex(x => new { x.PriceListId, x.ItemId }).IsUnique().HasFilter("[ItemId] IS NOT NULL");
			});
			b.Entity<HospitalItemCategory>(e => e.HasIndex(x => new { x.CompanyId, x.ItemCategoryId }).IsUnique());
			b.Entity<HospitalItemPublicPrice>(e => { e.Property(x => x.PublicPrice).HasPrecision(18, 4); e.HasIndex(x => new { x.CompanyId, x.ItemId }).IsUnique(); });
		}
	}
}
