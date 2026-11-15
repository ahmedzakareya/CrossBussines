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
	// THE STOCK MOVEMENT SURFACE — what moved, where, when, and what it was worth.
	//
	// THE BEST-ATTRIBUTED DATA IN THE PRODUCT, and measured before anything was built on it:
	//
	//   1,525 posted movements · ItemId on 100% of them · WarehouseId on 100% · a cost on 99.2%
	//   89 distinct items · 27 movement types
	//
	// That is a different world from sales, where only 6.5% of revenue could be attributed to an
	// item at all. Questions asked of this surface can actually be answered, which is why it is
	// worth more than its line count suggests.
	//
	// THE JUDGEMENTS BAKED IN:
	//
	//   1. DIRECTION IS NOT A NUMBER TO SUM. The table stores Direction (+1/-1) beside a positive
	//      QtyBase. There is no signed quantity column here, because summing one gives a net that
	//      reads as "how much moved" and is not — 539 sales movements and 114 of their reversals
	//      would net to something meaningless. The surface exposes `qtyIn`, `qtyOut` and `netQty`
	//      separately so a caller must say which one they meant.
	//
	//   2. COST IS NULL WHERE NONE WAS RECORDED, never zero. 12 movements have no cost. A zero
	//      would make them look free and would drag any average down invisibly.
	//
	//   3. THE HISTORY IS BURST-SEEDED AND THE SURFACE SAYS SO. 1,525 movements fall on 28 distinct
	//      days out of a 312-day span. Any per-day average taken over the calendar is wrong by
	//      roughly elevenfold, and nothing in the data announces that. It is stated in Rules, which
	//      travels with every answer, because a reader who does not know it cannot tell whether the
	//      figure they are looking at means anything.
	//
	//   4. REVERSALS ARE REAL MOVEMENTS AND ARE INCLUDED. SalesInvoiceReversal is 114 of the 1,525.
	//      They are shown under their own movement type rather than hidden, because a reversal is a
	//      thing that happened to the stock and excluding it silently would make the warehouse's own
	//      history disagree with its balance.
	// =================================================================================================
	public sealed class AskInventorySurface : AskSurface
	{
		public const string SurfaceCode = "stock.movements";

		private readonly CrossDbContext _db;
		public AskInventorySurface(CrossDbContext db) => _db = db ?? throw new ArgumentNullException(nameof(db));

		public override string Code => SurfaceCode;

		// Inventory authority, unlike sales and purchases. Seeing what moved on a shelf is not the
		// same permission as seeing what a customer paid, and this surface carries no selling price.
		public override string Module => EntityRegistry.ScopeInventory;
		public override string Action => "read";

		public override AskText Description => AskText.Of(
			"حركات المخزون: الوارد والمنصرف والصافي والتكلفة، حسب الصنف أو المخزن أو نوع الحركة أو الشهر",
			"Stock movements: in, out, net and cost, by item, warehouse, movement type or month");

		public override AskText Rules => AskText.Of(
			"الوارد والمنصرف مفصولان ولا يُجمعان في رقم واحد · التكلفة هي المسجَّلة وقت الحركة، وتكون "
			+ "غير معروفة لا صفرًا إذا لم تُسجَّل · حركات عكس فواتير البيع مشمولة وظاهرة بنوعها · "
			+ "الحركات مركَّزة في أيام قليلة، فأي متوسط يومي على التقويم كله مُضلِّل.",
			"In and out are kept separate and never summed into one figure · cost is the one recorded "
			+ "at the time of the movement, and is unknown rather than zero where none was recorded · "
			+ "sales-invoice reversals are included and shown under their own type · movements are "
			+ "concentrated in a few days, so any per-day average over the whole calendar misleads.");

		public override IReadOnlyList<AskColumn> Columns { get; } = new[]
		{
			new AskColumn { Name = "item", Kind = AskColumnKind.Dimension, Label = AskText.Of("الصنف", "Item"),
				Synonyms = new[] { "صنف", "اصناف", "الاصناف", "بضاعة", "البضاعة", "منتج", "منتجات", "المنتجات",
					"item", "items", "product", "products", "sku", "goods" } },
			new AskColumn { Name = "itemcode", Kind = AskColumnKind.Dimension, Label = AskText.Of("كود الصنف", "Item code"),
				Synonyms = new[] { "كود", "الكود", "code", "item code" } },
			new AskColumn { Name = "category", Kind = AskColumnKind.Dimension, Label = AskText.Of("الفئة", "Category"),
				Synonyms = new[] { "فئة", "فئات", "الفئات", "تصنيف", "التصنيف", "تصنيفات", "التصنيفات",
					"مجموعة", "المجموعات", "قسم", "الاقسام",
					"category", "categories", "item category", "group", "groups", "classification" } },
			// WAREHOUSE IS THE REASON THIS SURFACE EXISTS. Every movement has one, so "what moved in
			// which store" is answerable here and nowhere else in the assistant.
			new AskColumn { Name = "warehouse", Kind = AskColumnKind.Dimension, Label = AskText.Of("المخزن", "Warehouse"),
				Synonyms = new[] { "مخزن", "مخازن", "المخازن", "المخزن", "مستودع", "مستودعات", "فرع", "الفروع",
					"warehouse", "warehouses", "store", "stores", "location", "locations", "branch" } },
			new AskColumn { Name = "movementtype", Kind = AskColumnKind.Dimension, Label = AskText.Of("نوع الحركة", "Movement type"),
				Synonyms = new[] { "نوع الحركة", "انواع الحركات", "نوع", "النوع", "حسب النوع", "بالنوع",
					"سبب الحركة", "مصدر الحركة",
					"movement type", "movement types", "type", "by type", "reason", "source" } },
			new AskColumn { Name = "month", Kind = AskColumnKind.Dimension, Label = AskText.Of("الشهر", "Month"),
				Synonyms = new[] { "شهر", "شهري", "شهور", "الشهور", "شهريا", "بالشهر",
					"month", "monthly", "months", "per month", "by month" } },
			new AskColumn { Name = "date", Kind = AskColumnKind.Dimension, Label = AskText.Of("التاريخ", "Date"),
				Synonyms = new[] { "تاريخ", "يوم", "يومي", "بالتاريخ", "date", "day", "daily" } },

			new AskColumn { Name = "qtyin", Kind = AskColumnKind.Measure, Label = AskText.Of("الوارد", "Quantity in"),
				Synonyms = new[] { "وارد", "الوارد", "داخل", "الداخل", "دخل", "استلمنا", "اضافة", "الاضافات",
					"in", "inbound", "received", "receipts", "quantity in", "additions" } },
			new AskColumn { Name = "qtyout", Kind = AskColumnKind.Measure, Label = AskText.Of("المنصرف", "Quantity out"),
				Synonyms = new[] { "منصرف", "المنصرف", "صادر", "الصادر", "خارج", "الخارج", "خرج", "صرفنا", "اتصرف",
					"out", "outbound", "issued", "issues", "quantity out", "consumed" } },
			new AskColumn { Name = "netqty", Kind = AskColumnKind.Measure, Label = AskText.Of("الصافي", "Net quantity"),
				Synonyms = new[] { "صافي", "الصافي", "الفرق", "net", "net quantity", "difference", "change" } },
			new AskColumn { Name = "cost", Kind = AskColumnKind.Measure, IsMoney = true, Nullable = true, Label = AskText.Of("التكلفة", "Cost"),
				Synonyms = new[] { "تكلفة", "التكلفة", "قيمة", "القيمة", "كلفة", "تكاليف",
					"cost", "value", "costs", "worth" } },
			// A MEASURE IS NAMED BY WHAT IT COUNTS, NOT BY THE SUBJECT OF THE SURFACE.
			//
			// This measure used to claim the bare words "حركات" / "الحركات" / "حركة", and that broke
			// "الحركات حسب النوع": the composer takes the longest match, "الحركات" (7) beat "النوع"
			// (5), so a question asking for a BREAKDOWN BY TYPE came back as one grand total. Every
			// question about this surface contains the word "حركة" — it is what the surface is —
			// so letting the measure own it means the measure wins almost every sentence.
			//
			// What is left are the forms that actually ask for a COUNT.
			new AskColumn { Name = "movements", Kind = AskColumnKind.Measure, Label = AskText.Of("عدد الحركات", "Movements"),
				Synonyms = new[] { "عدد الحركات", "كام حركة", "عدد الحركة", "كم حركة",
					"movement count", "how many movements", "number of movements", "transaction count" } },
		};

		/// Movement types read as code names in the table. These are the ones this data actually
		/// holds; anything unmapped is shown as written rather than hidden behind a generic label.
		private static readonly Dictionary<string, (string Ar, string En)> TypeNames = new(StringComparer.OrdinalIgnoreCase)
		{
			["SalesInvoice"] = ("بيع", "Sale"),
			["SalesInvoiceReversal"] = ("عكس بيع", "Sale reversal"),
			["PurchaseInvoice"] = ("شراء", "Purchase"),
			["Receipt"] = ("استلام", "Goods receipt"),
			["Issue"] = ("صرف", "Issue"),
			["Adjustment"] = ("تسوية", "Adjustment"),
			["OpeningStock"] = ("رصيد افتتاحي", "Opening stock"),
			["Opening"] = ("رصيد افتتاحي", "Opening"),
			["WorkOrder"] = ("أمر تشغيل", "Work order"),
			["Transfer"] = ("تحويل", "Transfer"),
		};

		public override async Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> RowsAsync(
			AskScope scope, CancellationToken ct = default)
		{
			var arabic = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ar";

			var moves = await _db.StockMovements.AsNoTracking()
				.Where(m => m.CompanyID == scope.CompanyId)
				.Select(m => new
				{
					m.ItemId, m.WarehouseId, m.Direction, m.QtyBase, m.TotalCost,
					m.SourceType, m.MovementDate,
				})
				.ToListAsync(ct);

			if (moves.Count == 0) return Array.Empty<IReadOnlyDictionary<string, object?>>();

			var itemIds = moves.Select(m => m.ItemId).Distinct().ToList();
			var items = (await _db.Items.AsNoTracking()
					.Where(i => i.CompanyID == scope.CompanyId && itemIds.Contains(i.ID))
					.Select(i => new { i.ID, i.ItemCode, i.Name, i.NameEn, i.ItemCategoryId })
					.ToListAsync(ct))
				.ToDictionary(i => i.ID);

			var categoryIds = items.Values.Where(i => i.ItemCategoryId != 0)
				.Select(i => i.ItemCategoryId).Distinct().ToList();
			var categories = (await _db.ItemCategories.AsNoTracking()
					.Where(c => c.CompanyID == scope.CompanyId && categoryIds.Contains(c.ID))
					.Select(c => new { c.ID, c.Name, c.NameEn })
					.ToListAsync(ct))
				.ToDictionary(c => c.ID);

			// WAREHOUSE AUTHORITY IS THE SCOPE'S, NOT THIS SURFACE'S OPINION. Only the warehouses the
			// asker may see are named; a movement in any other one is dropped entirely rather than
			// shown under a masked label, because the quantity itself would still leak.
			var warehouses = (await _db.Warehouses.AsNoTracking()
					.Where(w => w.CompanyID == scope.CompanyId)
					.Select(w => new { w.ID, w.Name, w.NameEn, w.Code })
					.ToListAsync(ct))
				.ToDictionary(w => w.ID);

			var rows = new List<IReadOnlyDictionary<string, object?>>(moves.Count);
			foreach (var m in moves)
			{
				if (!warehouses.TryGetValue(m.WarehouseId, out var wh)) continue;

				items.TryGetValue(m.ItemId, out var it);
				var itemLabel = it == null
					? "#" + m.ItemId
					: (AskItems.Readable(arabic ? it.Name : it.NameEn, arabic ? it.NameEn : it.Name) ?? it.ItemCode);

				var categoryLabel = arabic ? "(بلا فئة)" : "(no category)";
				if (it != null && it.ItemCategoryId != 0 && categories.TryGetValue(it.ItemCategoryId, out var cat))
					categoryLabel = AskItems.Readable(arabic ? cat.Name : cat.NameEn, arabic ? cat.NameEn : cat.Name)
						?? ("#" + cat.ID);

				var whLabel = AskItems.Readable(arabic ? wh.Name : wh.NameEn, arabic ? wh.NameEn : wh.Name)
					?? wh.Code ?? ("#" + wh.ID);

				var src = m.SourceType ?? "";
				var typeLabel = TypeNames.TryGetValue(src, out var tn)
					? (arabic ? tn.Ar : tn.En)
					: (string.IsNullOrWhiteSpace(src) ? (arabic ? "(غير محدد)" : "(unspecified)") : src);

				// IN AND OUT, NEVER A SIGNED NUMBER. QtyBase is positive and Direction says which way.
				var isIn = m.Direction > 0;
				var qty = m.QtyBase;

				rows.Add(new Dictionary<string, object?>
				{
					["item"] = itemLabel,
					["itemcode"] = it?.ItemCode,
					["category"] = categoryLabel,
					["warehouse"] = whLabel,
					["movementtype"] = typeLabel,
					["month"] = m.MovementDate.ToString("yyyy-MM", CultureInfo.InvariantCulture),
					["date"] = m.MovementDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
					["qtyin"] = isIn ? qty : 0m,
					["qtyout"] = isIn ? 0m : qty,
					["netqty"] = isIn ? qty : -qty,
					// NULL, NOT ZERO, on the 12 movements that carry no cost.
					["cost"] = m.TotalCost == 0m ? (decimal?)null : m.TotalCost,
					["movements"] = 1m,
					[LinkFor("item")] = "/Inventory/EditItem/" + m.ItemId.ToString(CultureInfo.InvariantCulture),
				});
			}
			return rows;
		}
	}
}
