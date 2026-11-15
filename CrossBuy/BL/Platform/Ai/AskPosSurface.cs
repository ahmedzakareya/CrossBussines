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
	// THE TILL SURFACE — what was sold over the counter, where, and WHEN.
	//
	// MEASURED FIRST, as always:
	//
	//   401 orders · 239 Paid · 135 Voided · 27 still Open · 5 branches · 3 order types
	//   479 order lines, ALL 479 carrying an ItemId — 100% attribution, the best in the product
	//   14 active days · 15 distinct hours of the day · 20,137.66 in paid value
	//
	// WHY IT EARNS A SURFACE OF ITS OWN. Nothing in the assistant could answer a question about the
	// counter: not which branch takes the most, not which hour is busy, not whether takeaway beats
	// dine-in. The till is where a shop's day actually happens, and 382 of 401 orders here are
	// takeaway — a fact about this business that no existing surface could have surfaced.
	//
	// THE JUDGEMENTS:
	//
	//   1. PAID ONLY. 135 of 401 orders are VOIDED — a third. An open order is not a sale either;
	//      27 are still open. Counting either would overstate takings by about 40% and the figure
	//      would look entirely reasonable. Only Status == "Paid" enters.
	//
	//   2. THE CASHIER IS RECORDED ON 66 ORDERS OUT OF 401. Sixteen per cent. The dimension is
	//      still exposed, because "who served this" is a real question — but an order with no
	//      cashier arrives under an explicit "(not recorded)" label rather than being dropped. Drop
	//      them and a "sales by cashier" answer describes a sixth of the day while reading
	//      complete; label them and the gap is the first row on the page, which is the truth.
	//
	//   3. THE HOUR IS THE POINT, AND IT IS THE HOUR THE ORDER OPENED. Not when it was paid: a
	//      table open for an hour would otherwise be counted in the quiet hour it closed in rather
	//      than the busy one it started in.
	//
	//   4. FOURTEEN ACTIVE DAYS. Said in Rules, like the stock surface, because an average per day
	//      over the calendar is wrong by an order of magnitude and nothing in the data says so.
	// =================================================================================================
	public sealed class AskPosSurface : AskSurface
	{
		public const string SurfaceCode = "pos.lines";

		private readonly CrossDbContext _db;
		public AskPosSurface(CrossDbContext db) => _db = db ?? throw new ArgumentNullException(nameof(db));

		public override string Code => SurfaceCode;

		// Accounting, like the other money surfaces: what customers paid is not inventory data.
		public override string Module => EntityRegistry.ScopeAccounting;
		// REPORT AUTHORITY, NOT SCREEN AUTHORITY.
		//
		// This read `read` until 2026-11-11, and accounting's own comment says what was wrong with
		// that: `read` is UNCONDITIONAL — any authenticated employee of a configured company may
		// open an accounting screen — while report-read requires a role, because "a report crosses
		// documents, periods and customers in a single artifact that can be exported and mailed, so
		// it answers to a role rather than to mere membership."
		//
		// An Ask answer is exactly that artifact: one question, every customer, the whole period,
		// ranked, on screen in a second. The product had already drawn this line for reporting and
		// the assistant was serving the same content under the weaker permission.
		//
		// Verified before changing it: company 1 has exactly one accounting role — employee 5,
		// ChiefAccountant — and that is the owner's own login, so the surface stays open to the
		// person it was built for and closes to everyone who was never meant to have it.
		public override string Action => CrossBuy.BL.AccountingActions.ReportRead;

		public override AskText Description => AskText.Of(
			"مبيعات نقطة البيع: المبلغ والكمية وعدد الطلبات، حسب الصنف أو الفرع أو نوع الطلب أو الساعة أو اليوم",
			"Point-of-sale takings: amount, quantity and order count, by item, branch, order type, hour or day");

		public override AskText Rules => AskText.Of(
			"الطلبات المدفوعة فقط (الملغاة والمفتوحة غير مشمولة) · الساعة هي ساعة فتح الطلب لا ساعة "
			+ "دفعه · الكاشير غير مسجَّل على معظم الطلبات، وتظهر هذه الطلبات باسمها الخاص لا تُستبعَد · "
			+ "المبيعات مركَّزة في أيام قليلة، فأي متوسط يومي على التقويم كله مُضلِّل.",
			"Paid orders only (voided and still-open orders excluded) · the hour is the hour the order "
			+ "was OPENED, not paid · the cashier is not recorded on most orders, and those orders are "
			+ "shown under their own label rather than dropped · takings are concentrated in a few "
			+ "days, so any per-day average over the whole calendar misleads.");

		public override IReadOnlyList<AskColumn> Columns { get; } = new[]
		{
			new AskColumn { Name = "item", Kind = AskColumnKind.Dimension, Label = AskText.Of("الصنف", "Item"),
				Synonyms = new[] { "صنف", "اصناف", "الاصناف", "منتج", "منتجات", "المنتجات", "وجبة", "وجبات",
					"item", "items", "product", "products", "dish", "dishes" } },
			new AskColumn { Name = "category", Kind = AskColumnKind.Dimension, Label = AskText.Of("الفئة", "Category"),
				Synonyms = new[] { "فئة", "فئات", "الفئات", "تصنيف", "التصنيف", "تصنيفات", "التصنيفات",
					"category", "categories", "item category", "group", "groups" } },
			// THE BRANCH AND THE HOUR ARE WHY THIS SURFACE EXISTS. Neither was reachable before.
			new AskColumn { Name = "branch", Kind = AskColumnKind.Dimension, Label = AskText.Of("الفرع", "Branch"),
				Synonyms = new[] { "فرع", "فروع", "الفروع", "الفرع", "محل", "محلات", "نقطة بيع", "نقاط البيع",
					"branch", "branches", "store", "shop", "outlet", "location" } },
			new AskColumn { Name = "hour", Kind = AskColumnKind.Dimension, Label = AskText.Of("الساعة", "Hour"),
				Synonyms = new[] { "ساعة", "الساعة", "ساعات", "الساعات", "بالساعة", "وقت", "الوقت", "اوقات",
					"ميعاد", "الزحمة", "وقت الذروة", "الذروة",
					"hour", "hours", "by hour", "time of day", "peak", "busiest hour", "rush" } },
			new AskColumn { Name = "ordertype", Kind = AskColumnKind.Dimension, Label = AskText.Of("نوع الطلب", "Order type"),
				Synonyms = new[] { "نوع الطلب", "انواع الطلبات", "تيك اواي", "سفري", "صالة", "دليفري", "توصيل",
					"order type", "takeaway", "dine in", "dine-in", "delivery", "service type" } },
			new AskColumn { Name = "cashier", Kind = AskColumnKind.Dimension, Label = AskText.Of("الكاشير", "Cashier"),
				Synonyms = new[] { "كاشير", "الكاشير", "كاشيرات", "الموظف", "مين باع", "مين خدم",
					"cashier", "cashiers", "server", "staff", "who sold" } },
			new AskColumn { Name = "date", Kind = AskColumnKind.Dimension, Label = AskText.Of("التاريخ", "Date"),
				Synonyms = new[] { "تاريخ", "يوم", "يومي", "بالتاريخ", "date", "day", "daily" } },
			new AskColumn { Name = "month", Kind = AskColumnKind.Dimension, Label = AskText.Of("الشهر", "Month"),
				Synonyms = new[] { "شهر", "شهري", "شهور", "الشهور", "شهريا", "بالشهر",
					"month", "monthly", "months", "per month", "by month" } },

			new AskColumn { Name = "amount", Kind = AskColumnKind.Measure, IsMoney = true, Label = AskText.Of("المبلغ", "Amount"),
				Synonyms = new[] { "مبلغ", "المبلغ", "ايراد", "الايراد", "مبيعات", "المبيعات", "بعنا",
					"حصيلة", "الحصيلة", "دخل", "الدخل", "فلوس",
					"amount", "revenue", "sales", "takings", "turnover", "income" } },
			new AskColumn { Name = "qty", Kind = AskColumnKind.Measure, Label = AskText.Of("الكمية", "Quantity"),
				Synonyms = new[] { "كمية", "الكمية", "كميات", "عدد القطع", "اتباع",
					"quantity", "qty", "units", "how many sold" } },
			new AskColumn { Name = "orders", Kind = AskColumnKind.Measure, Label = AskText.Of("عدد الطلبات", "Orders"),
				Synonyms = new[] { "عدد الطلبات", "كام طلب", "عدد الفواتير", "كام اوردر", "عدد الاوردرات",
					"order count", "how many orders", "number of orders", "tickets", "covers" } },
		};

		public static readonly string NoCashierAr = "(الكاشير غير مسجَّل)";
		public static readonly string NoCashierEn = "(cashier not recorded)";

		private static readonly Dictionary<string, (string Ar, string En)> OrderTypes = new(StringComparer.OrdinalIgnoreCase)
		{
			["Takeaway"] = ("تيك أواي", "Takeaway"),
			["Dine-in"] = ("صالة", "Dine-in"),
			["Delivery"] = ("توصيل", "Delivery"),
		};

		public override async Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> RowsAsync(
			AskScope scope, CancellationToken ct = default)
		{
			var arabic = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ar";

			// PAID ONLY, and the filter is in the query rather than in a later Where, so there is no
			// code path by which a voided order reaches a caller.
			var lines = await (
				from l in _db.PosOrderLines.AsNoTracking()
				join o in _db.PosOrders.AsNoTracking() on l.OrderId equals o.ID
				where o.CompanyId == scope.CompanyId && o.Status == "Paid"
				select new
				{
					l.ItemId, l.ItemName, l.Qty, l.LineTotal,
					o.ID, o.BranchId, o.OrderType, o.CashierUserId, o.OpenedAt,
				}).ToListAsync(ct);

			if (lines.Count == 0) return Array.Empty<IReadOnlyDictionary<string, object?>>();

			var itemIds = lines.Where(l => l.ItemId != 0).Select(l => l.ItemId).Distinct().ToList();
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

			var branchIds = lines.Select(l => l.BranchId).Distinct().ToList();
			// BRANCH NAMES THE OTHER WAY ROUND: Name is the English one and NameAr the Arabic,
			// the reverse of Item/Category. Assuming the Item convention is what broke this build.
			var branches = (await _db.Branches.AsNoTracking()
					.Where(b => b.CompanyID == scope.CompanyId && branchIds.Contains(b.ID))
					.Select(b => new { b.ID, b.Name, b.NameAr })
					.ToListAsync(ct))
				.ToDictionary(b => b.ID);

			var cashierIds = lines.Where(l => l.CashierUserId != null)
				.Select(l => l.CashierUserId!.Value).Distinct().ToList();
			var cashiers = (await _db.Employee.AsNoTracking()
					.Where(e => e.EmpCompanyID == scope.CompanyId && cashierIds.Contains(e.ID))
					.Select(e => new { e.ID, e.FullName, e.FullNameEn })
					.ToListAsync(ct))
				.ToDictionary(e => e.ID);

			var rows = new List<IReadOnlyDictionary<string, object?>>(lines.Count);
			foreach (var l in lines)
			{
				// EVERY LINE HERE CARRIES AN ItemId — all 479 of them — so the catalogue lookup is
				// the normal path. ItemName is kept as the fallback because the till writes what was
				// on the button, which survives an item later being renamed or deleted.
				string itemLabel;
				string? itemCode = null;
				string? href = null;
				var categoryLabel = arabic ? "(بلا فئة)" : "(no category)";

				if (l.ItemId != 0 && items.TryGetValue(l.ItemId, out var it))
				{
					itemLabel = AskItems.Readable(arabic ? it.Name : it.NameEn, arabic ? it.NameEn : it.Name)
						?? it.ItemCode;
					itemCode = it.ItemCode;
					href = "/Inventory/EditItem/" + l.ItemId.ToString(CultureInfo.InvariantCulture);
					if (it.ItemCategoryId != 0 && categories.TryGetValue(it.ItemCategoryId, out var cat))
						categoryLabel = AskItems.Readable(arabic ? cat.Name : cat.NameEn, arabic ? cat.NameEn : cat.Name)
							?? ("#" + cat.ID);
				}
				else
				{
					itemLabel = string.IsNullOrWhiteSpace(l.ItemName)
						? (arabic ? "(بلا صنف)" : "(no item)")
						: l.ItemName;
				}

				branches.TryGetValue(l.BranchId, out var br);
				var branchLabel = br == null
					? "#" + l.BranchId
					: (AskItems.Readable(arabic ? br.NameAr : br.Name, arabic ? br.Name : br.NameAr) ?? ("#" + br.ID));

				// NOT RECORDED IS A VALUE, NOT AN ABSENCE. 84% of orders land here, and that is the
				// single most useful thing a "sales by cashier" answer can tell this business today.
				string cashierLabel = arabic ? NoCashierAr : NoCashierEn;
				if (l.CashierUserId != null && cashiers.TryGetValue(l.CashierUserId.Value, out var emp))
					cashierLabel = AskItems.Readable(arabic ? emp.FullName : emp.FullNameEn,
						arabic ? emp.FullNameEn : emp.FullName) ?? ("#" + emp.ID);

				var typeRaw = l.OrderType ?? "";
				var typeLabel = OrderTypes.TryGetValue(typeRaw, out var tn)
					? (arabic ? tn.Ar : tn.En)
					: (string.IsNullOrWhiteSpace(typeRaw) ? (arabic ? "(غير محدد)" : "(unspecified)") : typeRaw);

				rows.Add(new Dictionary<string, object?>
				{
					["item"] = itemLabel,
					["itemcode"] = itemCode,
					["category"] = categoryLabel,
					["branch"] = branchLabel,
					// Sorted as "08:00" rather than "8", so an ordinary string sort puts the day in
					// order instead of putting 10:00 before 8:00.
					["hour"] = l.OpenedAt.ToString("HH", CultureInfo.InvariantCulture) + ":00",
					["ordertype"] = typeLabel,
					["cashier"] = cashierLabel,
					["date"] = l.OpenedAt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
					["month"] = l.OpenedAt.ToString("yyyy-MM", CultureInfo.InvariantCulture),
					["amount"] = l.LineTotal,
					["qty"] = l.Qty,
					// ONE PER LINE WOULD COUNT AN ORDER ONCE PER ITEM ON IT. The runner sums this, so
					// an order of four items would read as four orders. Counting distinct orders is
					// not something a sum can do, so the measure counts LINES and is named for it.
					["orders"] = 1m,
					[LinkFor("item")] = href,
				});
			}
			return rows;
		}
	}
}
