using Microsoft.EntityFrameworkCore;

namespace CrossBuy.Models.Context.Hospital
{
	// ==========================================================================================
	// HOSPITAL — the catalogue's single items (unit H22, the owner's ruling on price lists, 2026-11-04).
	//
	// The hospital catalogue was services + the items of the inventory categories mapped to the hospital
	// (HospitalItemCategory): an item already in the inventory under any other category could not be put on a
	// price list without creating a new item card. Now ANY active inventory item can join the catalogue on its
	// own, as a medicine or a consumable (which list adjustment it takes); a whole category still joins through
	// the category map, and then its new items show as "new — not added" on every list by themselves.
	// CrossBuy/deploy/sql/hospital_h22_catalog_items.sql builds the table.
	// ==========================================================================================
	public class HospitalCatalogItem
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public int ItemId { get; set; }
		/// HospitalPriceGroups.Medicine | Consumable.
		public string Kind { get; set; } = HospitalPriceGroups.Medicine;
		public DateTime AddedAt { get; set; }
		public int? AddedByEmployeeId { get; set; }
	}

	public static class HospitalCatalogItemsModel
	{
		public static void Configure(ModelBuilder b)
		{
			b.Entity<HospitalCatalogItem>(e =>
			{
				e.ToTable("HospitalCatalogItems");   // no DbSet: it names its table
				e.Property(x => x.Kind).HasMaxLength(20);
				e.HasIndex(x => new { x.CompanyId, x.ItemId }).IsUnique();
			});
		}
	}
}
