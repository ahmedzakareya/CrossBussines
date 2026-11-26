using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CrossBuy.Models.Context;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL.Platform.Ai
{
	// =================================================================================================
	// "What categories do we have?"
	//
	// ADDED BECAUSE IT ANSWERED THE WRONG QUESTION, WHICH IS WORSE THAN REFUSING. The owner asked
	// "قول علي فئات الاصناف ال عندنا" and got a list of ITEMS by revenue — a confident, well-formed,
	// completely different answer. Then "الفئات" on its own was refused outright.
	//
	// A CATEGORY IS NOT AN ITEM, and nothing in this assistant knew the difference. "فئة" and "صنف"
	// are a single short word apart in a vocabulary built on character n-grams, so the matcher
	// resolved one into the other exactly as it resolved "المخزن" into "المخزون" earlier. The cure is
	// the same both times: the concept has to exist, with its own words, before any amount of
	// matching can route to it.
	//
	// AND THE DATA IS THE BEST IN THE SYSTEM. 49 categories, every one of the 237 active items
	// carries one, no corrupted names, and every category has an English name as well. Measured
	// before building, as everything here is: this is the only dimension so far at 100% coverage,
	// which makes it the one most worth being able to group by.
	// =================================================================================================
	public sealed class AskItemCategoriesHandler : IAskHandler
	{
		public const string IntentId = "inventory.item-categories";

		private readonly CrossDbContext _db;
		public AskItemCategoriesHandler(CrossDbContext db) => _db = db ?? throw new ArgumentNullException(nameof(db));

		public string Intent => IntentId;
		public string Module => EntityRegistry.ScopeInventory;
		public string Action => "read";
		public IReadOnlyCollection<string> Parameters { get; } = Array.Empty<string>();

		public AskText Description => AskText.Of(
			"فئات الأصناف المسجَّلة وعدد الأصناف في كل فئة",
			"The registered item categories and how many items are in each",
			"Les catégories d'articles enregistrées et le nombre d'articles dans chacune");

		public AskText Example => AskText.Of("إيه فئات الأصناف اللي عندنا؟", "What item categories do we have?", "Quelles catégories d'articles avons-nous ?");

		public IReadOnlyCollection<string> Keywords { get; } = new[]
		{
			"الفئات", "فئات", "فئة", "الفئة", "فئات الاصناف", "فئات الأصناف", "فئات المنتجات",
			"التصنيفات", "تصنيفات", "تصنيف الاصناف", "المجموعات", "مجموعات الاصناف",
			"انواع الاصناف", "اقسام الاصناف", "قول علي الفئات", "ايه الفئات",
			"categories", "item categories", "product categories", "category list",
			"what categories", "item groups", "product groups", "classifications",
		};

		private const int MaxRows = 25;

		public async Task<AskAnswer> AnswerAsync(AskRequest request, AskScope scope, CancellationToken ct = default)
		{
			var arabic = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ar";

			var categories = await _db.ItemCategories.AsNoTracking()
				.Where(c => c.CompanyID == scope.CompanyId)
				.Select(c => new { c.ID, c.Name, c.NameEn })
				.ToListAsync(ct);

			if (categories.Count == 0)
				return AskAnswer.Refuse(AskRefusal.NoData, AskText.Of(
					"لا توجد فئات أصناف مسجَّلة في هذه الشركة.",
					"No item categories are registered in this company."));

			// COUNTED FROM THE ITEMS, not stored on the category. A count that is written down goes
			// stale the first time somebody adds an item; a count that is derived cannot.
			var counts = (await _db.Items.AsNoTracking()
					.Where(i => i.CompanyID == scope.CompanyId && i.IsActive && i.ItemCategoryId != 0)
					.GroupBy(i => i.ItemCategoryId)
					.Select(g => new { CategoryId = g.Key, Items = g.Count() })
					.ToListAsync(ct))
				.ToDictionary(x => x.CategoryId, x => x.Items);

			var uncategorised = await _db.Items.AsNoTracking()
				.CountAsync(i => i.CompanyID == scope.CompanyId && i.IsActive && i.ItemCategoryId == 0, ct);

			var ranked = categories
				.Select(c => new
				{
					c.ID,
					Label = AskItems.Readable(arabic ? c.Name : c.NameEn, arabic ? c.NameEn : c.Name) ?? ("#" + c.ID),
					Items = counts.TryGetValue(c.ID, out var n) ? n : 0,
				})
				.OrderByDescending(x => x.Items)
				.ThenBy(x => x.Label, StringComparer.Ordinal)
				.ToList();

			var known = categories.Select(c => c.ID).ToHashSet();
			var dangling = counts.Where(kv => !known.Contains(kv.Key)).Sum(kv => kv.Value);

			var shown = ranked.Take(MaxRows).ToList();
			var empty = ranked.Count(x => x.Items == 0);

			var rows = shown.Select(c => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>
			{
				["name"] = c.Label,
				["code"] = null,
				["warehouse"] = arabic ? $"{c.Items} صنف" : $"{c.Items} item(s)",
				["onHand"] = c.Items,
				["onHandText"] = c.Items.ToString(CultureInfo.InvariantCulture),
			}).ToList();

			var spokenAr = $"عندك {ranked.Count} فئة. أكبرها {shown[0].Label} بـ{shown[0].Items} صنف."
				+ (ranked.Count > shown.Count ? $" ومعروض {shown.Count} منها." : "");
			var spokenEn = $"You have {ranked.Count} categories. The largest is {shown[0].Label} with {shown[0].Items} item(s)."
				+ (ranked.Count > shown.Count ? $" Showing {shown.Count} of them." : "");

			// AN EMPTY CATEGORY AND AN UNCATEGORISED ITEM ARE BOTH WORTH SAYING. The first is a
			// category nobody uses; the second is an item nobody filed. Neither shows up in a plain
			// list, and both are the kind of thing somebody asking this question wants to know.
			var notes = new List<(string Ar, string En)>();
			if (empty > 0)
				notes.Add(($"{empty} فئة بلا أصناف.", $"{empty} category(ies) hold no items."));
			if (uncategorised > 0)
				notes.Add(($"{uncategorised} صنف بلا فئة.", $"{uncategorised} item(s) have no category."));
			if (dangling > 0)
				notes.Add(($"{dangling} صنف مربوط بفئة غير موجودة.",
						   $"{dangling} item(s) reference a category that no longer exists."));

			return new AskAnswer
			{
				Answered = true,
				Rows = rows,
				Spoken = AskText.Of(spokenAr, spokenEn),
				Understood = notes.Count == 0
					? null
					: AskText.Of(string.Join(" ", notes.Select(n => n.Ar)),
								 string.Join(" ", notes.Select(n => n.En))),
				Provenance = new AskProvenance
				{
					CompanyId = scope.CompanyId,
					Sources = new[] { "ItemCategories", "Items" },
					Authority = EntityRegistry.ScopeInventory + ":read",
				},
			};
		}
	}
}
