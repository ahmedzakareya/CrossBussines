using CrossBuy.Models.Context.Hospital;

namespace CrossBuy.BL.Hospital
{
	// ==========================================================================================
	// HOSPITAL H22 — the catalogue's membership rules, with no database: any inventory item, or a whole category,
	// can be put on a price list (the owner's ruling, 2026-11-04).
	// ==========================================================================================

	/// One inventory item chosen for a list, as a medicine or a consumable.
	public sealed class HospitalItemPick
	{
		public int ItemId { get; set; }
		public string Kind { get; set; } = HospitalPriceGroups.Medicine;
	}

	/// An inventory item found by the search: its category, its main price, the kind it has in the hospital
	/// catalogue (null = not in it yet) and whether this list already holds it.
	public sealed record HospitalItemHit(int ItemId, string Code, string Name, string NameEn, string Category, string CategoryEn, decimal MainPrice, string? Kind, bool OnList);

	/// An inventory category: its active items, how many are not on this list yet, and its hospital kind if mapped.
	public sealed record HospitalCategoryChoice(int Id, string Code, string Name, string NameEn, int Items, int NotOnList, string? MappedKind);

	public static class HospitalCatalogRules
	{
		/// The kind an item has in the hospital catalogue: its own (it joined on its own) wins over its category's;
		/// null = the item is not in the catalogue.
		public static string? KindOf(string? categoryKind, string? ownKind) => ownKind ?? categoryKind;

		public const int MaxPicks = 500;

		/// Why a pick cannot be added, or null.
		public static string? PickRefusal(IReadOnlyList<HospitalItemPick>? picks)
		{
			if (picks == null || picks.Count == 0) return "Choose the items to add";
			if (picks.Count > MaxPicks) return "Add at most 500 items at a time";
			if (picks.Any(p => !HospitalPriceGroups.ItemKinds.Contains(p.Kind))) return "A category is a medicine or a consumable";
			if (picks.Any(p => p.ItemId <= 0)) return "not_found";
			return null;
		}
	}
}
