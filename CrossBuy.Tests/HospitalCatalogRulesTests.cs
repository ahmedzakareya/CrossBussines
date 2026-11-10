using CrossBuy.BL.Hospital;
using CrossBuy.Models.Context.Hospital;
using Xunit;

namespace CrossBuy.Tests
{
	// ==========================================================================================
	// HOSPITAL H22 — any inventory item, or a whole category, onto a price list, DATABASE-FREE: the kind an item
	// has in the catalogue, the pick refusals, and the model against its slice.
	//
	// What must never happen again (the owner, 2026-11-04): an item already in the inventory that cannot be put on
	// a price list without creating a new item card.
	// ==========================================================================================
	public class HospitalCatalogRulesTests
	{
		[Fact]
		public void An_item_in_no_mapped_category_and_not_joined_is_not_in_the_catalogue() => Assert.Null(HospitalCatalogRules.KindOf(null, null));

		[Fact]
		public void An_item_of_a_mapped_category_takes_the_categorys_kind() =>
			Assert.Equal(HospitalPriceGroups.Medicine, HospitalCatalogRules.KindOf(HospitalPriceGroups.Medicine, null));

		[Fact]
		public void An_item_that_joined_on_its_own_is_in_the_catalogue_whatever_its_category() =>
			Assert.Equal(HospitalPriceGroups.Consumable, HospitalCatalogRules.KindOf(null, HospitalPriceGroups.Consumable));

		[Fact]
		public void An_items_own_kind_wins_over_its_categorys() =>
			Assert.Equal(HospitalPriceGroups.Consumable, HospitalCatalogRules.KindOf(HospitalPriceGroups.Medicine, HospitalPriceGroups.Consumable));

		[Fact]
		public void Nothing_picked_is_refused()
		{
			Assert.NotNull(HospitalCatalogRules.PickRefusal(null));
			Assert.NotNull(HospitalCatalogRules.PickRefusal(Array.Empty<HospitalItemPick>()));
		}

		[Fact]
		public void A_kind_other_than_medicine_or_consumable_is_refused() =>
			Assert.NotNull(HospitalCatalogRules.PickRefusal(new[] { new HospitalItemPick { ItemId = 1, Kind = HospitalPriceGroups.Service } }));

		[Fact]
		public void More_than_five_hundred_at_a_time_is_refused() =>
			Assert.NotNull(HospitalCatalogRules.PickRefusal(Enumerable.Range(1, 501).Select(i => new HospitalItemPick { ItemId = i, Kind = HospitalPriceGroups.Medicine }).ToList()));

		[Fact]
		public void Medicines_and_consumables_together_are_accepted() =>
			Assert.Null(HospitalCatalogRules.PickRefusal(new[] { new HospitalItemPick { ItemId = 1, Kind = HospitalPriceGroups.Medicine }, new HospitalItemPick { ItemId = 2, Kind = HospitalPriceGroups.Consumable } }));

		// ---------------- the model against its slice ----------------

		private static string Slice() => HospitalSliceParity.Slice("hospital_h22_catalog_items.sql");
		private static Microsoft.EntityFrameworkCore.Metadata.IModel Model() => HospitalSliceParity.Model(HospitalCatalogItemsModel.Configure);

		[Fact]
		public void The_model_names_the_table_its_slice_creates() => HospitalSliceParity.NamesItsTable(Model(), Slice(), typeof(HospitalCatalogItem), "HospitalCatalogItems");

		[Fact]
		public void Every_column_is_read_in_the_type_its_slice_creates() => HospitalSliceParity.ColumnsMatch(Model(), Slice(), typeof(HospitalCatalogItem), "HospitalCatalogItems");

		[Fact]
		public void An_item_joins_the_catalogue_once_in_model_and_slice()
		{
			Assert.True(Model().FindEntityType(typeof(HospitalCatalogItem))!.GetIndexes().Single().IsUnique);
			Assert.Contains("CREATE UNIQUE INDEX IX_HospitalCatalogItems_CompanyId_ItemId ON dbo.HospitalCatalogItems (CompanyId, ItemId);", Slice().Split('\n').Select(l => l.Trim()));
		}

		[Fact]
		public void Every_table_the_slice_references_but_does_not_create_is_a_checked_prerequisite() =>
			Assert.Equal(1, HospitalSliceParity.CheckedPrerequisites(Slice()).Count);   // the inventory's items

		[Fact]
		public void The_slice_alters_no_existing_table() => Assert.DoesNotContain("ALTER TABLE", Slice(), StringComparison.OrdinalIgnoreCase);
	}
}
