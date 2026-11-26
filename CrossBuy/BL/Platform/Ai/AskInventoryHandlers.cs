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
	// Two more inventory questions. Both filter rows through IInventoryAccessService, because warehouse
	// visibility is per-person and the gate in AskService cannot know it.
	// =================================================================================================

	/// "What has moved on this item lately?"
	public sealed class AskItemMovementsHandler : IAskHandler
	{
		public const string IntentId = "inventory.item-movements";

		private readonly CrossDbContext _db;
		private readonly IInventoryAccessService _inventory;

		public AskItemMovementsHandler(CrossDbContext db, IInventoryAccessService inventory)
		{
			_db = db ?? throw new ArgumentNullException(nameof(db));
			_inventory = inventory ?? throw new ArgumentNullException(nameof(inventory));
		}

		public string Intent => IntentId;
		public string Module => EntityRegistry.ScopeInventory;
		public string Action => "read";
		public IReadOnlyCollection<string> Parameters { get; } = new[] { "item" };

		public AskText Description => AskText.Of(
			"آخر حركات صنف معيّن في المخازن التي تملك صلاحية عليها",
			"The latest movements of one item, in the warehouses you are allowed to see",
			"Les derniers mouvements d'un article dans les entrepôts auxquels vous avez accès");

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
		public AskText Example => AskText.Of("آخر حركات صنف معيّن", "Latest movements for an item", "Quels sont les derniers mouvements de cet article ?");

		public IReadOnlyCollection<string> Keywords { get; } = new[]
		{
			"حركات", "الحركات", "حركة", "اخر حركات", "آخر حركات", "صرف", "الصرف", "وارد", "منصرف",
			"movements", "movement", "transactions", "issued", "issues", "received", "latest movements",
		};

		private const int MaxRows = 12;

		public async Task<AskAnswer> AnswerAsync(AskRequest request, AskScope scope, CancellationToken ct = default)
		{
			request.Parameters.TryGetValue("item", out var query);
			query = (query ?? string.Empty).Trim();
			if (query.Length < 2)
				return AskAnswer.Refuse(AskRefusal.NoData, AskText.Of(
					"اذكر حرفين على الأقل من اسم الصنف أو كوده.",
					"Give at least two characters of the item name or code."));

			var isAr = System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ar";

			// THE SAME LOOKUP THE OTHER ITEM QUESTIONS USE.
			//
			// This handler used to run its own LIKE-per-word query against the database. Two
			// matchers for the same noun is two definitions of what a word means: this one could
			// not fold Arabic spellings, could not rank, had no notion of an exact code beating a
			// partial name, and tolerated no typo — so "برجر داج" found nothing here while the
			// stock question offered a correction. One lookup, one answer to "which item is that".
			var catalogue = await _db.Items.AsNoTracking()
				.Where(i => i.CompanyID == scope.CompanyId && i.IsActive)
				.Select(i => new { i.ID, i.ItemCode, i.Name, i.NameEn })
				.ToListAsync(ct);

			var ranked = AskItems.Rank(catalogue, query,
				i => new AskItems.Hit(i.ID, i.ItemCode, i.Name, i.NameEn), request.Question);

			var (matches, corrected, offer) = AskSearch.Resolve(ranked, 1);

			// "Not found" and "not yours" read identically: distinguishing them lets a caller
			// enumerate another company's catalogue one code at a time.
			if (matches.Count == 0)
			{
				var suggestions = AskItems.Offer(offer, isAr, AskItems.MovementsQuestion);
				if (suggestions.Count == 0)
					return AskAnswer.Refuse(AskRefusal.NoData, AskText.Of(
						"لا يوجد صنف بهذا الاسم أو الكود ضمن ما يمكنك الاطلاع عليه.",
						"There is no item with that name or code within what you can see."));
				return AskAnswer.Refuse(AskRefusal.NoData, AskText.Of(
					"لم أجد صنفًا بهذا الاسم بالضبط. هل تقصد:",
					"I found no item by exactly that name. Did you mean:"), suggestions);
			}

			var item = matches[0];
			var understood = corrected ? AskItems.Corrected(item) : null;
			var subject = AskItems.SubjectOf(item, isAr);

			// Over-fetched on purpose: rows are dropped AFTER the authority check, so taking exactly
			// MaxRows first could return fewer than MaxRows visible ones and look like the end of
			// the history when it is not.
			var raw = await _db.StockMovements.AsNoTracking()
				.Where(m => m.CompanyID == scope.CompanyId && m.ItemId == item.ID)
				.OrderByDescending(m => m.MovementDate).ThenByDescending(m => m.ID)
				.Take(MaxRows * 4)
				.Select(m => new { m.ID, m.MovementDate, m.WarehouseId, m.Direction, m.QtyBase, m.SourceType })
				.ToListAsync(ct);

			if (raw.Count == 0)
				return new AskAnswer
				{
					Answered = false,
					Refusal = AskRefusal.NoData,
					Message = AskText.Of(
						"لا توجد حركات مسجَّلة على هذا الصنف.", "This item has no recorded movements."),
					Understood = understood,
					Subject = subject,
				};

			var warehouseIds = raw.Select(r => r.WarehouseId).Distinct().ToList();
			var names = (await _db.Warehouses.AsNoTracking()
					.Where(w => warehouseIds.Contains(w.ID))
					.Select(w => new { w.ID, w.Name }).ToListAsync(ct))
				.ToDictionary(w => w.ID, w => w.Name);

			var visible = new Dictionary<int, bool>();
			foreach (var id in warehouseIds) visible[id] = await _inventory.CanUseWarehouseAsync(id);

			var rows = new List<IReadOnlyDictionary<string, object?>>();
			var withheld = 0;
			foreach (var m in raw)
			{
				if (!visible.TryGetValue(m.WarehouseId, out var ok) || !ok) { withheld++; continue; }
				if (rows.Count >= MaxRows) continue;
				rows.Add(new Dictionary<string, object?>
				{
					["name"] = m.MovementDate.ToString("yyyy-MM-dd") + " · "
						+ (names.TryGetValue(m.WarehouseId, out var wn) ? wn : "")
						+ " · " + (m.SourceType ?? ""),
					["code"] = m.Direction < 0 ? (isAr ? "منصرف" : "out") : (isAr ? "وارد" : "in"),
					["onHand"] = decimal.Round(m.QtyBase, 3),
				});
			}

			if (rows.Count == 0 && withheld > 0)
				return new AskAnswer
				{
					Answered = false,
					Refusal = AskRefusal.NotPermitted,
					Message = AskText.Of(
						"لهذا الصنف حركات، لكنها في مخازن ليست ضمن صلاحيتك.",
						"This item has movements, but only in warehouses outside your permission."),
					Provenance = Prov(scope, withheld),
					Understood = understood,
					Subject = subject,
				};

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
			Sources = new[] { "Items", "StockMovements", "Warehouses" },
			Authority = EntityRegistry.ScopeInventory + ":read",
			RowsWithheld = withheld,
			WithheldNote = withheld == 0 ? null : AskText.Of(
				$"استُبعدت {withheld} حركة في مخازن خارج صلاحيتك، فالقائمة أعلاه جزئية.",
				$"{withheld} movement(s) in warehouses outside your permission were excluded, so the"
				+ " list above is partial."),
		};
	}

	/// "Which warehouses can I see?" — small, and the most direct way for a person to find out
	/// where their own boundary is without discovering it through a refusal.
	public sealed class AskMyWarehousesHandler : IAskHandler
	{
		public const string IntentId = "inventory.my-warehouses";

		private readonly CrossDbContext _db;
		private readonly IInventoryAccessService _inventory;

		public AskMyWarehousesHandler(CrossDbContext db, IInventoryAccessService inventory)
		{
			_db = db ?? throw new ArgumentNullException(nameof(db));
			_inventory = inventory ?? throw new ArgumentNullException(nameof(inventory));
		}

		public string Intent => IntentId;
		public string Module => EntityRegistry.ScopeInventory;
		public string Action => "read";
		public IReadOnlyCollection<string> Parameters { get; } = Array.Empty<string>();

		public AskText Description => AskText.Of(
			"المخازن التي تملك صلاحية الاطلاع عليها",
			"The warehouses you are allowed to see",
			"Les entrepôts auxquels vous êtes autorisé à accéder");

		public AskText Example => AskText.Of("إيه المخازن اللي أقدر أشوفها؟", "Which warehouses can I see?", "À quels entrepôts puis-je accéder ?");

		public IReadOnlyCollection<string> Keywords { get; } = new[]
		{
			// IT HAD NO COUNTING WORDS AT ALL. This handler knew only "المخازن" - plural and
			// definite - so «كم مخزن عندنا؟» (singular, indefinite) was never really its question,
			// and a neighbouring intent took it without a fight. Now it owns the way people
			// actually ask how many there are.
			"المخازن", "مخازني", "صلاحيتي", "صلاحياتي", "اقدر اشوف", "أقدر أشوف",
			"كم مخزن", "كام مخزن", "عدد المخازن", "كم عدد المخازن", "اجمالي المخازن", "المخازن كام",
			"my warehouses", "warehouses", "which warehouses", "my permissions", "my access",
			"how many warehouses", "number of warehouses", "warehouse count", "total warehouses",
		};

		public async Task<AskAnswer> AnswerAsync(AskRequest request, AskScope scope, CancellationToken ct = default)
		{
			var arabic = System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ar";

			var all = await _db.Warehouses.AsNoTracking()
				.Where(w => w.CompanyID == scope.CompanyId)
				.OrderBy(w => w.ID)
				.Select(w => new { w.ID, w.Name, w.NameEn, w.Code, w.IsActive })
				.ToListAsync(ct);

			var rows = new List<IReadOnlyDictionary<string, object?>>();
			var withheld = 0;
			var inactive = 0;
			foreach (var w in all)
			{
				if (!await _inventory.CanUseWarehouseAsync(w.ID)) { withheld++; continue; }
				if (!w.IsActive) inactive++;
				rows.Add(new Dictionary<string, object?>
				{
					// THE WAREHOUSE'S OWN CODE, NOT ITS PRIMARY KEY. This row used to carry w.ID in
					// the quantity column, so the panel printed "المخزن الرئيسي 1 ... 3017 3018" -
					// database identifiers, shown to a person who asked how many warehouses exist.
					// An id is not a fact about a warehouse; the code is the one it is known by.
					["name"] = AskItems.Readable(arabic ? w.Name : w.NameEn, arabic ? w.NameEn : w.Name)
							   ?? ("#" + w.ID.ToString(System.Globalization.CultureInfo.InvariantCulture)),
					["code"] = w.Code,
					["onHand"] = null,
				});
			}

			if (rows.Count == 0)
				return AskAnswer.Refuse(AskRefusal.NotPermitted, AskText.Of(
					"لا تملك صلاحية على أي مخزن في هذه الشركة.",
					"You have no permission for any warehouse in this company."));

			// SAY THE NUMBER. This handler answered with rows and no sentence at all - Spoken was
			// never set - so «كم مخزن عندنا؟» came back as a bare list and the asker had to count it
			// themselves. The count is the answer; the list is the evidence under it.
			var spokenAr = $"عندك {rows.Count} مخزن ضمن صلاحيتك."
				+ (inactive > 0 ? $" منها {inactive} غير نشط." : "");
			var spokenEn = $"You have {rows.Count} warehouse(s) within your permission."
				+ (inactive > 0 ? $" {inactive} of them {(inactive == 1 ? "is" : "are")} inactive." : "");

			return new AskAnswer
			{
				Answered = true,
				Rows = rows,
				Spoken = AskText.Of(spokenAr, spokenEn),
				Provenance = new AskProvenance
				{
					CompanyId = scope.CompanyId,
					Sources = new[] { "Warehouses" },
					Authority = EntityRegistry.ScopeInventory + ":read",
					// Here the withheld count is the ANSWER, not a caveat: knowing that warehouses
					// exist beyond your reach is the point of asking.
					RowsWithheld = withheld,
					WithheldNote = withheld == 0 ? null : AskText.Of(
						$"يوجد {withheld} مخزن آخر في هذه الشركة خارج صلاحيتك.",
						$"There are {withheld} further warehouse(s) in this company outside your permission."),
				},
			};
		}
	}
}
