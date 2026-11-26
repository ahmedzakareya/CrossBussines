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
	// "What is this item worth, and what does it cost us?"
	//
	// ADDED BECAUSE THE OWNER ASKED FOR IT TWICE and got "I did not understand" both times —
	// "اعرض التكلفة ليهم" and "كم رصيد لاب توب + التكلفة". A catalogue is only as good as the
	// questions people actually put to it, and the refusals are the list of what is missing.
	//
	// Cost is money, so it is scoped to warehouses the asker may use exactly as quantity is, and a
	// partial answer says so. The average cost shown is the one StockBalances carries — the same
	// figure the stock screens display — rather than a second calculation that could drift from it.
	// =================================================================================================
	public sealed class AskItemValueHandler : IAskHandler
	{
		public const string IntentId = "inventory.item-value";

		private readonly CrossDbContext _db;
		private readonly IInventoryAccessService _inventory;

		public AskItemValueHandler(CrossDbContext db, IInventoryAccessService inventory)
		{
			_db = db ?? throw new ArgumentNullException(nameof(db));
			_inventory = inventory ?? throw new ArgumentNullException(nameof(inventory));
		}

		public string Intent => IntentId;
		public string Module => EntityRegistry.ScopeInventory;
		public string Action => "read";
		public IReadOnlyCollection<string> Parameters { get; } = new[] { "item" };

		public AskText Description => AskText.Of(
			"تكلفة صنف وقيمة رصيده في المخازن التي تملك صلاحية عليها",
			"The cost of an item and the value of its stock, in the warehouses you may see",
			"Le coût d'un article et la valeur de son stock dans les entrepôts auxquels vous avez accès");

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
		public AskText Example => AskText.Of("كام تكلفة صنف معيّن؟", "What does an item cost us?", "Quel est le coût de cet article ?");

		public IReadOnlyCollection<string> Keywords { get; } = new[]
		{
			// Multi-word phrases earn their length in the score, which is how "قيمة مخزون" beats the
			// stock question's "مخزون" — "stock value" is a different question from "stock", and the
			// longer phrase is the more specific claim about what was meant.
			"قيمة مخزون", "قيمة المخزون", "قيمه مخزون", "stock value", "inventory value",
			"تكلفة", "التكلفة", "تكلفه", "التكلفه", "متوسط التكلفة", "قيمة", "القيمة", "قيمه", "سعر التكلفة",
			"cost", "costs", "value", "worth", "average cost",
		};

		private const int MaxMatches = 5;

		public async Task<AskAnswer> AnswerAsync(AskRequest request, AskScope scope, CancellationToken ct = default)
		{
			var isArabic = System.Globalization.CultureInfo.CurrentUICulture
				.TwoLetterISOLanguageName == "ar";

			request.Parameters.TryGetValue("item", out var query);
			query = (query ?? string.Empty).Trim();
			if (query.Length < 2)
				return AskAnswer.Refuse(AskRefusal.NoData, AskText.Of(
					"اذكر حرفين على الأقل من اسم الصنف أو كوده.",
					"Give at least two characters of the item name or code."));

			// Same folded, ranked search the stock question uses, so the two agree about what the
			// words mean. Two lookups that disagree would be worse than one that is merely narrow.
			var catalogue = await _db.Items.AsNoTracking()
				.Where(i => i.CompanyID == scope.CompanyId && i.IsActive)
				.Select(i => new { i.ID, i.ItemCode, i.Name, i.NameEn })
				.ToListAsync(ct);

			var ranked = AskItems.Rank(catalogue, query,
				i => new AskItems.Hit(i.ID, i.ItemCode, i.Name, i.NameEn), request.Question);

			var (items, corrected, offer) = AskSearch.Resolve(ranked, MaxMatches);

			if (items.Count == 0)
			{
				var suggestions = AskItems.Offer(offer, isArabic, AskItems.ValueQuestion);
				if (suggestions.Count == 0)
					return AskAnswer.Refuse(AskRefusal.NoData, AskText.Of(
						"لا يوجد صنف بهذا الاسم أو الكود ضمن ما يمكنك الاطلاع عليه.",
						"There is no item with that name or code within what you can see."));
				return AskAnswer.Refuse(AskRefusal.NoData, AskText.Of(
					"لم أجد صنفًا بهذا الاسم بالضبط. هل تقصد:",
					"I found no item by exactly that name. Did you mean:"), suggestions);
			}

			var understood = corrected ? AskItems.Corrected(items[0]) : null;
			var subject = AskItems.SubjectOf(items[0], isArabic);

			var ids = items.Select(i => i.ID).ToList();
			var balances = await _db.StockBalances.AsNoTracking()
				.Where(b => b.CompanyID == scope.CompanyId && ids.Contains(b.ItemId))
				.Select(b => new { b.ItemId, b.WarehouseId, b.QtyOnHand, b.TotalValue, b.AvgCost })
				.ToListAsync(ct);

			var whIds = balances.Select(b => b.WarehouseId).Distinct().ToList();
			var visible = new Dictionary<int, bool>();
			foreach (var id in whIds) visible[id] = await _inventory.CanUseWarehouseAsync(id);

			var rows = new List<IReadOnlyDictionary<string, object?>>();
			var withheld = 0;
			foreach (var it in items)
			{
				var mine = balances.Where(b => b.ItemId == it.ID && visible.TryGetValue(b.WarehouseId, out var ok) && ok).ToList();
				withheld += balances.Count(b => b.ItemId == it.ID) - mine.Count;
				if (mine.Count == 0) continue;

				var qty = mine.Sum(b => b.QtyOnHand);
				var value = mine.Sum(b => b.TotalValue);
				// VALUE DIVIDED BY QUANTITY, not the average of the averages. Averaging AvgCost
				// across warehouses weights a warehouse holding three units the same as one holding
				// three thousand, which is a different number and a wrong one.
				var avg = qty > 0 ? value / qty : 0m;

				rows.Add(new Dictionary<string, object?>
				{
					["name"] = isArabic ? it.Name : (it.NameEn ?? it.Name),
					["code"] = it.ItemCode,
					["warehouse"] = (isArabic ? "متوسط التكلفة " : "avg cost ") + avg.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)
						+ (isArabic ? $" · الرصيد {decimal.Round(qty, 2)}" : $" · qty {decimal.Round(qty, 2)}"),
					// MONEY IS ALWAYS TWO PLACES. "4" beside "8.25" in one column reads as a different kind
					// of number, and a money column whose width changes per row cannot be scanned.
					["onHand"] = decimal.Round(value, 2),
					["onHandText"] = value.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture),
				});
			}

			if (rows.Count == 0 && withheld > 0)
				return new AskAnswer
				{
					Answered = false,
					Refusal = AskRefusal.NotPermitted,
					Message = AskText.Of(
						"هذا الصنف له قيمة مخزون، لكن في مخازن ليست ضمن صلاحيتك.",
						"This item holds stock value, but only in warehouses outside your permission."),
					Provenance = Prov(scope, withheld),
					Understood = understood,
					Subject = subject,
				};

			if (rows.Count == 0)
			{
				// NEVER COSTED IS NOT WORTH NOTHING. An item that was never received has no cost
				// because no purchase ever set one, which is a gap in the data; an item that was
				// received and issued down to zero has a cost history and a current value of zero,
				// which is a fact about the warehouse. The old sentence covered both and told the
				// reader neither.
				var everMoved = await _db.StockMovements.AsNoTracking()
					.AnyAsync(m => m.CompanyID == scope.CompanyId && m.ItemId == items[0].ID, ct);

				return new AskAnswer
				{
					Answered = false,
					Refusal = AskRefusal.NoData,
					Message = everMoved
						? AskText.Of(
							"قيمة مخزون هذا الصنف صفر فيما تملك صلاحية عليه، وله حركات سابقة.",
							"This item's stock value is zero within what you may see, and it has past movements.")
						: AskText.Of(
							"لم يُشترَ هذا الصنف ولم يُستلم قط، فلا توجد له تكلفة مسجَّلة — "
							+ "التكلفة غير معروفة، وليست صفرًا.",
							"This item has never been purchased or received, so no cost was ever "
							+ "recorded — the cost is unknown, not zero."),
					Understood = understood,
					Subject = subject,
				};
			}

			return new AskAnswer
			{
				Answered = true,
				Rows = rows,
				Provenance = Prov(scope, withheld),
				Understood = understood,
				Subject = subject,
			};
		}

		private static AskProvenance Prov(AskScope scope, int withheld) => new()
		{
			CompanyId = scope.CompanyId,
			Sources = new[] { "Items", "StockBalances" },
			Authority = EntityRegistry.ScopeInventory + ":read",
			RowsWithheld = withheld,
			WithheldNote = withheld == 0 ? null : AskText.Of(
				$"استُبعد {withheld} سجل في مخازن خارج صلاحيتك، فالقيمة أعلاه جزئية.",
				$"{withheld} record(s) in warehouses outside your permission were excluded, so the"
				+ " value above is partial."),
		};
	}
}
