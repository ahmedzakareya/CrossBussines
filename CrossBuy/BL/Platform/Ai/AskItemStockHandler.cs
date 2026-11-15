using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CrossBuy.Models.Context;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL.Platform.Ai
{
	// =================================================================================================
	// "How much of this item do we hold?"
	//
	// The simplest useful question, and the one that exercises every part of the contract: it crosses
	// warehouses, and warehouse visibility is per-person. A WarehouseKeeper sees their own branch; an
	// InventoryManager sees all of them. The SAME question must therefore produce different totals for
	// different people, and must never let the smaller answer be mistaken for the whole.
	//
	// WHY THE TOTAL IS COMPUTED AFTER FILTERING, NOT BEFORE. Totalling first and then hiding rows
	// would leak the hidden quantity through arithmetic — the reader could subtract. Each row is
	// authorised, then what survives is summed, and the count of what did not survive is reported
	// alongside. The count is the most that may be said: naming the warehouse, or its quantity, is
	// exactly the thing the permission exists to withhold.
	//
	// WHY "NOT FOUND" AND "NOT YOURS" ARE THE SAME ANSWER. An item in another company, and an item
	// that does not exist, both return NoData with identical wording. Distinguishing them turns this
	// into an oracle for enumerating other companies' catalogues one code at a time.
	// =================================================================================================
	public sealed class AskItemStockHandler : IAskHandler
	{
		public const string IntentId = "inventory.item-stock";

		private readonly CrossDbContext _db;
		private readonly IInventoryAccessService _inventory;

		public AskItemStockHandler(CrossDbContext db, IInventoryAccessService inventory)
		{
			_db = db ?? throw new ArgumentNullException(nameof(db));
			_inventory = inventory ?? throw new ArgumentNullException(nameof(inventory));
		}

		public string Intent => IntentId;
		public string Module => EntityRegistry.ScopeInventory;
		public string Action => "read";
		public IReadOnlyCollection<string> Parameters { get; } = new[] { "item" };

		public AskText Description => AskText.Of(
			"رصيد صنف معيّن موزّعًا على المخازن التي تملك صلاحية عليها",
			"The on-hand quantity of one item, across the warehouses you are allowed to see");

		// A SUGGESTION SHOWS THE SHAPE OF A QUESTION, NOT ONE ROW'S DATA.
		//
		// This read «كام رصيد مياه معدنية؟» and the owner's objection was the right one: the chip
		// list is the assistant's menu of what it can do, and naming one product there makes it
		// look like the one product it knows. It is also brittle — another company's catalogue has
		// no mineral water, and the chip would offer a question with no answer behind it.
		//
		// Naming no item is not a loss: a question that names none makes the handler ASK which one,
		// which is behaviour this already has. The chip teaches the shape and the assistant asks
		// for the noun.
		public AskText Example => AskText.Of("كام رصيد صنف معيّن؟", "How much of an item do we hold?");

		// ONLY WORDS THAT IDENTIFY THIS QUESTION. The first version of this list held "كام",
		// "عندنا" and "كمية", and the screen proved why that was wrong within three questions:
		// "مين أحسن مورد عندنا؟" — who is our best supplier — matched on "عندنا" and came back as an
		// answer about item stock. A matcher that accepts loose phrasing answers the WRONG question
		// confidently, and a figure that looks like the one you asked for is worse than no figure.
		//
		// So a keyword here must be a word that could not plausibly belong to another question.
		// Quantity words ("كام", "how much") are deliberately absent: they ask HOW MUCH of anything.
		public IReadOnlyCollection<string> Keywords { get; } = new[]
		{
			// Arabic, as people actually write it — definite article attached or not, and the
			// several words Egyptian and Gulf users reach for to mean the same thing.
			"رصيد", "الرصيد", "ارصدة", "الارصدة", "مخزون", "المخزون", "مخزن", "المخزن",
			"متبقي", "المتبقي", "متوفر", "المتوفر", "باقي",
			// English
			"stock", "in stock", "on hand", "onhand", "inventory", "balance", "balances",
			"available", "remaining", "left",
		};

		/// Bounded so a one-character query cannot sweep the catalogue into one answer.
		private const int MaxMatches = 5;
		private const int MinQueryLength = 2;

		private static readonly AskText NoData = AskText.Of(
			"لا يوجد صنف بهذا الاسم أو الكود ضمن ما يمكنك الاطلاع عليه.",
			"There is no item with that name or code within what you can see.");

		private static readonly AskText TooShort = AskText.Of(
			"اذكر حرفين على الأقل من اسم الصنف أو كوده.",
			"Give at least two characters of the item name or code.");

		public async Task<AskAnswer> AnswerAsync(AskRequest request, AskScope scope, CancellationToken ct = default)
		{
			var isArabic = System.Globalization.CultureInfo.CurrentUICulture
				.TwoLetterISOLanguageName == "ar";

			request.Parameters.TryGetValue("item", out var query);
			query = (query ?? string.Empty).Trim();
			if (query.Length < MinQueryLength)
				// ASKED, NOT REFUSED. "TooShort" was a form validation message in a chat bubble; a
				// person asks "رصيد أنهي صنف؟" and waits. NeedsParameter makes the gate remember the
				// question, so the next message is read as the answer instead of as a new question
				// about nothing.
				return new AskAnswer
				{
					Answered = false,
					Refusal = AskRefusal.NoData,
					NeedsParameter = "item",
					Spoken = AskText.Of("رصيد أنهي صنف؟", "Which item's stock?"),
				};

			// BOTH SIDES FOLDED, THEN RANKED. See AskSearch for why one-sided folding is worse than
			// none: it made "مياه معدنية" search a catalogue that still reads "معدنية" and match
			// nothing, ever. The whole company catalogue is loaded and compared in memory — 237 rows
			// here, cheaper than the chain of LIKEs it replaces, and it allows an ordering SQL could
			// not express: an exact code beats a prefix beats every-word beats some-words.
			//
			// COMPANY FIRST, in the predicate itself, so an item outside the resolved company is
			// never materialised at all.
			var catalogue = await _db.Items.AsNoTracking()
				.Where(i => i.CompanyID == scope.CompanyId && i.IsActive)
				.Select(i => new { i.ID, i.ItemCode, i.Name, i.NameEn })
				.ToListAsync(ct);

			var ranked = AskItems.Rank(catalogue, query,
				i => new AskItems.Hit(i.ID, i.ItemCode, i.Name, i.NameEn), request.Question);

			var (items, corrected, offer) = AskSearch.Resolve(ranked, MaxMatches);

			if (items.Count == 0)
			{
				// A PARTIAL MATCH IS A QUESTION, NOT AN ANSWER. Returning the nearest rows as though
				// they were the result is how an assistant answers confidently about the wrong item.
				// They are offered back as "did you mean" — now as BUTTONS that ask the question,
				// because a list of names you still have to retype is a dead end with extra steps.
				var suggestions = AskItems.Offer(offer, isArabic, AskItems.StockQuestion);

				if (suggestions.Count == 0)
					return AskAnswer.Refuse(AskRefusal.NoData, NoData);

				return AskAnswer.Refuse(AskRefusal.NoData, AskText.Of(
					"لم أجد صنفًا بهذا الاسم بالضبط. هل تقصد:",
					"I found no item by exactly that name. Did you mean:"), suggestions);
			}

			// WHEN THE SPELLING WAS CORRECTED, SAY SO. Answering "برجر داج" with برجر دجاج's figures
			// without a word is a wrong answer that looks right; the person cannot correct an
			// assumption they were never shown.
			var understood = corrected ? AskItems.Corrected(items[0]) : null;
			// What a follow-up may build on. Set even when the answer below is a refusal, because
			// the ITEM was resolved — "والتكلفة؟" after "no stock recorded" is a fair question.
			var subject = AskItems.SubjectOf(items[0], isArabic);

			var itemIds = items.Select(i => i.ID).ToList();
			var balances = await _db.StockBalances.AsNoTracking()
				.Where(b => b.CompanyID == scope.CompanyId && itemIds.Contains(b.ItemId))
				.Select(b => new { b.ItemId, b.WarehouseId, b.QtyOnHand, b.LastMovementAt })
				.ToListAsync(ct);

			var warehouseIds = balances.Select(b => b.WarehouseId).Distinct().ToList();
			var warehouseNames = (await _db.Warehouses.AsNoTracking()
					.Where(w => warehouseIds.Contains(w.ID))
					.Select(w => new { w.ID, w.Name })
					.ToListAsync(ct))
				.ToDictionary(w => w.ID, w => w.Name);

			// ROW-LEVEL AUTHORITY, asked once per warehouse rather than once per row.
			var visible = new Dictionary<int, bool>();
			foreach (var id in warehouseIds)
				visible[id] = await _inventory.CanUseWarehouseAsync(id);

			var rows = new List<IReadOnlyDictionary<string, object?>>();
			var withheld = 0;
			foreach (var it in items)
			{
				foreach (var b in balances.Where(x => x.ItemId == it.ID).OrderBy(x => x.WarehouseId))
				{
					if (!visible.TryGetValue(b.WarehouseId, out var ok) || !ok) { withheld++; continue; }
					rows.Add(new Dictionary<string, object?>
					{
						["itemId"] = it.ID,
						["code"] = it.ItemCode,
						["name"] = it.Name,
						["nameEn"] = it.NameEn,
						["warehouse"] = warehouseNames.TryGetValue(b.WarehouseId, out var wn) ? wn : null,
						["onHand"] = decimal.Round(b.QtyOnHand, 3),
						["lastMovementAt"] = b.LastMovementAt?.ToString("yyyy-MM-dd"),
					});
				}
			}

			// Every warehouse holding this item is one the asker may not see. That is NOT the same as
			// the item having no stock, and must not read as it.
			if (rows.Count == 0 && withheld > 0)
				return new AskAnswer
				{
					Answered = false,
					Refusal = AskRefusal.NotPermitted,
					Message = AskText.Of(
						"هذا الصنف له رصيد، لكن في مخازن ليست ضمن صلاحيتك.",
						"This item holds stock, but only in warehouses outside your permission."),
					Provenance = Provenance(scope, withheld),
					Understood = understood,
					Subject = subject,
				};

			if (rows.Count == 0)
			{
				// "NEVER STOCKED" AND "CURRENTLY ZERO" ARE DIFFERENT FACTS, and the old wording read
				// as the second while usually meaning the first. 140 of the 231 stockable items in
				// this catalogue have never had a single movement — they are rows somebody created
				// and nothing else — so the sentence a person met most often was the one that told
				// them least. One says the data was never entered; the other says the shelf is
				// empty. Only the second is about stock.
				var everMoved = await _db.StockMovements.AsNoTracking()
					.AnyAsync(m => m.CompanyID == scope.CompanyId && m.ItemId == items[0].ID, ct);

				return new AskAnswer
				{
					Answered = false,
					Refusal = AskRefusal.NoData,
					Message = everMoved
						? AskText.Of(
							"رصيد هذا الصنف صفر في كل مخزن تملك صلاحية عليه. له حركات سابقة.",
							"This item's balance is zero in every warehouse you may see. It does have past movements.")
						: AskText.Of(
							"هذا الصنف موجود في الكتالوج ولم تُسجَّل عليه أي حركة مخزون قط — "
							+ "فلا رصيد له لأنه لم يُستلم ولم يُصرف، لا لأن رصيده صفر.",
							"This item exists in the catalogue and has never had a single stock movement — "
							+ "so it has no balance because nothing was ever received or issued, "
							+ "not because the balance is zero."),
					Understood = understood,
					Subject = subject,
					// The item resolved, so the obvious next questions are worth offering rather
					// than leaving the person to guess which other ones exist.
					Suggestions = new[]
					{
						new AskSuggestion
						{
							Label = isArabic ? "التكلفة" : "cost",
							Question = AskItems.ValueQuestion(items[0].Display(isArabic), isArabic),
						},
						new AskSuggestion
						{
							Label = isArabic ? "آخر الحركات" : "last movements",
							Question = AskItems.MovementsQuestion(items[0].Display(isArabic), isArabic),
						},
					},
				};
			}

			// THE ANSWER IN WORDS, from the rows above. One warehouse reads as a sentence; several read
			// as a total and a count, because listing five warehouses in prose is worse than the grid
			// underneath it.
			var totalQty = rows.Sum(r => r["onHand"] as decimal? ?? 0m);
			var itemName = items[0].Display(isArabic);
			var spoken = rows.Count == 1
				? AskText.Of(
					itemName + ": " + decimal.Round(totalQty, 2) + " في " + rows[0]["warehouse"] + ".",
					itemName + ": " + decimal.Round(totalQty, 2) + " in " + rows[0]["warehouse"] + ".")
				: AskText.Of(
					itemName + ": " + decimal.Round(totalQty, 2) + " في المجموع، على " + rows.Count + " مخازن.",
					itemName + ": " + decimal.Round(totalQty, 2) + " in total, across " + rows.Count + " warehouses.");

			return new AskAnswer
			{
				Answered = true,
				Rows = rows,
				Provenance = Provenance(scope, withheld),
				Understood = understood,
				Subject = subject,
				Spoken = spoken,
			};
		}

		private static AskProvenance Provenance(AskScope scope, int withheld) => new()
		{
			CompanyId = scope.CompanyId,
			Sources = new[] { "Items", "StockBalances", "Warehouses" },
			Authority = EntityRegistry.ScopeInventory + ":read",
			RowsWithheld = withheld,
			// A COUNT, never a name and never a quantity. Saying nothing at all would let a partial
			// answer read as a complete one; saying more would hand over what the permission withholds.
			WithheldNote = withheld == 0 ? null : AskText.Of(
				$"استُبعد {withheld} سجل مخزون لأنك لا تملك صلاحية على مخزنه. الإجمالي أعلاه جزئي.",
				$"{withheld} stock record(s) were excluded because you have no permission for their"
				+ " warehouse. The figures above are partial."),
		};
	}
}
