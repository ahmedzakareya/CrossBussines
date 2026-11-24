using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CrossBuy.BL;
using CrossBuy.Models.Context;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL.Platform.Ai
{
	// =================================================================================================
	// "How many items do we have?"
	//
	// ADDED BECAUSE THE ASSISTANT ANSWERED A DIFFERENT QUESTION, CONFIDENTLY. Asked «كم عدد الأصناف؟»
	// it replied "You have 49 categories" - while the inventory dashboard directly behind the panel
	// read 248 Items and 49 Categories. The asker was told a true fact about the wrong noun.
	//
	// THE CAUSE WAS NOT A BAD KEYWORD, IT WAS A MISSING CONCEPT. Nothing in this assistant could
	// answer how many items exist. AskItemCategoriesHandler was the nearest neighbour in a vocabulary
	// built on character n-grams - it owns the phrases "فئات الأصناف" and "انواع الاصناف", which share
	// almost every n-gram with "عدد الأصناف" - so it won a contest it should never have entered. That
	// file was itself written for exactly this failure, and records the cure: the concept has to exist,
	// with its own words, before any amount of matching can route to it. This is that concept.
	//
	// IN ENGLISH IT WAS WORSE. "how many items do we have?" resolved to an item NAME lookup and came
	// back "I found no item by exactly that name. Did you mean: RC6 صنف TIP item" - a refusal to a
	// question the system can answer perfectly, which is the one outcome worse than a wrong figure.
	//
	// IT COUNTS WHAT THE DASHBOARD COUNTS. ItemCount on the inventory dashboard is
	// IItemService.GetItemsAsync(company).Count - every item of the company, active or not. Counting
	// anything else here would make the assistant disagree with the screen it is sitting on top of,
	// which is how a person learns not to trust it. The active/inactive split is then said out loud,
	// because "248" and "237 that you can actually sell" are both real answers to the same sentence
	// and the asker is the one who knows which they meant.
	// =================================================================================================
	public sealed class AskItemCountHandler : IAskHandler
	{
		public const string IntentId = "inventory.item-count";

		private readonly CrossDbContext _db;
		private readonly IItemService _items;

		public AskItemCountHandler(CrossDbContext db, IItemService items)
		{
			_db = db ?? throw new ArgumentNullException(nameof(db));
			_items = items ?? throw new ArgumentNullException(nameof(items));
		}

		public string Intent => IntentId;
		public string Module => EntityRegistry.ScopeInventory;
		public string Action => "read";
		public IReadOnlyCollection<string> Parameters { get; } = Array.Empty<string>();

		// TWO ARGUMENTS, NOT THREE. AskText.Of(ar, en, fr) and AskText.Fr exist only in a colleague's
		// uncommitted edit to AskContracts.cs; in HEAD the type is bilingual. Passing a third argument
		// compiled here and broke the build the moment the committed tree was built on its own.
		// French, for the day that overload lands:
		//   Description : "Le nombre d'articles enregistrés dans l'entreprise, et combien sont actifs"
		//   Example     : "Combien d'articles avons-nous ?"
		public AskText Description => AskText.Of(
			"عدد الأصناف المسجَّلة في الشركة، والنشط منها وغير النشط",
			"How many items are registered in the company, and how many are active");

		public AskText Example => AskText.Of("كم عدد الأصناف عندنا؟", "How many items do we have?");

		// THE COUNTING WORDS, NOT THE GROUPING WORDS. Every phrase here is about HOW MANY of a thing
		// there are. "فئات الأصناف" and "تصنيفات" are deliberately absent - they belong to the
		// categories handler, and the whole point of this file is that the two must stop colliding.
		public IReadOnlyCollection<string> Keywords { get; } = new[]
		{
			// ANCHORED ON THE NOUN, NEVER ON THE FRAME. The first version also carried
			// "كم صنف عندنا", "كام صنف عندنا" and "عندنا كام صنف". Those are four-fifths the generic
			// frame «كم … عندنا», which «كم مخزن عندنا؟» shares entirely - and it promptly came back
			// with the item count. A phrase only earns its place here if صنف/منتج is what makes it
			// match, not the counting words around it.
			"كم عدد الأصناف", "كم عدد الاصناف", "عدد الأصناف", "عدد الاصناف",
			"كم صنف", "كام صنف", "اجمالي الاصناف", "إجمالي الأصناف", "اجمالي عدد الاصناف",
			"عدد المنتجات", "كم منتج", "كام منتج", "الأصناف كام", "عدد الاصناف المسجلة",
			"how many items", "how many items do we have", "item count", "number of items",
			"total items", "count of items", "how many products", "number of products", "product count",
		};

		private const int MaxRows = 12;

		public async Task<AskAnswer> AnswerAsync(AskRequest request, AskScope scope, CancellationToken ct = default)
		{
			var arabic = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ar";

			// The dashboard's own source, so the two can never disagree.
			var all = await _items.GetItemsAsync(scope.CompanyId);
			if (all.Count == 0)
				return AskAnswer.Refuse(AskRefusal.NoData, AskText.Of(
					"لا توجد أصناف مسجَّلة في هذه الشركة.",
					"No items are registered in this company."));

			var active = all.Count(i => i.IsActive);
			var inactive = all.Count - active;
			var composite = all.Count(i => i.IsComposite);
			var uncategorised = all.Count(i => i.ItemCategoryId == 0);

			// A BREAKDOWN, NOT A BARE NUMBER. The figure is in the sentence; these rows are what the
			// figure is made of, so the next question - "which ones are inactive?" - is already half
			// answered on the screen.
			var breakdown = new List<(string Ar, string En, int N)>
			{
				("إجمالي الأصناف", "All items", all.Count),
				("أصناف نشطة", "Active", active),
			};
			if (inactive > 0) breakdown.Add(("أصناف غير نشطة", "Inactive", inactive));
			if (composite > 0) breakdown.Add(("أصناف مركَّبة", "Composite (BOM)", composite));
			if (uncategorised > 0) breakdown.Add(("أصناف بلا فئة", "Without a category", uncategorised));

			var rows = breakdown.Take(MaxRows).Select(b => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>
			{
				["name"] = arabic ? b.Ar : b.En,
				["code"] = null,
				["onHand"] = b.N,
				["onHandText"] = b.N.ToString(CultureInfo.InvariantCulture),
			}).ToList();

			var spokenAr = $"عندك {all.Count} صنف مسجَّل في هذه الشركة."
				+ (inactive > 0 ? $" منها {active} نشط و{inactive} غير نشط." : " وكلها نشطة.");
			var spokenEn = $"You have {all.Count} item(s) registered in this company."
				+ (inactive > 0 ? $" {active} are active and {inactive} are not." : " All of them are active.");

			// SAID BECAUSE IT IS THE QUESTION THAT GOT ANSWERED INSTEAD. Anybody who lands here after
			// meaning categories should be able to see the other number without asking twice.
			var categories = await _db.ItemCategories.AsNoTracking()
				.CountAsync(c => c.CompanyID == scope.CompanyId, ct);

			return new AskAnswer
			{
				Answered = true,
				Rows = rows,
				Spoken = AskText.Of(spokenAr, spokenEn),
				Notes = AskText.Of(
					$"هذه الأصناف موزَّعة على {categories} فئة."
						+ (uncategorised > 0 ? $" و{uncategorised} صنف بلا فئة." : ""),
					$"These items are spread across {categories} category(ies)."
						+ (uncategorised > 0 ? $" {uncategorised} have no category." : "")),
				Suggestions = new[]
				{
					new AskSuggestion
					{
						Label = arabic ? "فئات الأصناف" : "Item categories",
						Question = arabic ? "إيه فئات الأصناف اللي عندنا؟" : "What item categories do we have?",
					},
				},
				Provenance = new AskProvenance
				{
					CompanyId = scope.CompanyId,
					Sources = new[] { "Items", "ItemCategories" },
					Authority = EntityRegistry.ScopeInventory + ":read",
				},
			};
		}
	}
}
