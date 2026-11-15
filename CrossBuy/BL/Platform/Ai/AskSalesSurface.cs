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
	// THE SALES SURFACE — one governed view, four judgements already made.
	//
	// Everything this surface exposes has survived the four mistakes that this data invites, and it
	// exposes NOTHING that would let a caller make them again:
	//
	//   1. THREE CURRENCIES, rates 1, ~50 and 163. There is no `lineTotal` column. There is
	//      `revenue`, already multiplied by its own invoice's rate. A caller cannot add pounds to
	//      dollars here because the raw amount is not reachable.
	//
	//   2. DRAFTS AND REVERSALS. Only Status == "Posted" rows enter. A reversed invoice's original
	//      is flagged Reversed and never arrives, so nothing can double-count it, and a draft is
	//      not a sale.
	//
	//   3. A QUARTER OF LINES NAME NO ITEM. 144 of 585 posted lines have a null ItemId. They are
	//      NOT dropped — dropping them makes a per-item total silently miss a quarter of revenue.
	//      They arrive with `item` set to an explicit "not linked to an item" label, so a group by
	//      item shows them as their own visible row and the total still adds up.
	//
	//   4. COST IS THE ONE FROM THE TIME OF THE SALE, read from the outbound movement the invoice
	//      produced, and it is NULL where none was recorded — never zero. `margin` is null with it.
	//      A missing cost subtracted from revenue looks exactly like a free product, and the item
	//      with the worst data would otherwise top every profitability answer.
	//
	// WHY THAT MATTERS MORE HERE THAN IN A HANDLER. A handler's judgement protects the one question
	// it answers. A surface's judgement protects every question anyone ever asks of it, including
	// the ones nobody has thought of yet — which is the whole reason this layer exists.
	// =================================================================================================
	public sealed class AskSalesSurface : AskSurface
	{
		public const string SurfaceCode = "sales.lines";

		private readonly CrossDbContext _db;
		public AskSalesSurface(CrossDbContext db) => _db = db ?? throw new ArgumentNullException(nameof(db));

		public override string Code => SurfaceCode;

		// READ UNDER ACCOUNTING AUTHORITY, because the rows are sales invoices. Deliberately not
		// inventory: being allowed to see stock is not the same permission as seeing what customers
		// paid for it.
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
			"سطور فواتير المبيعات المرحَّلة: الإيراد والتكلفة والهامش والكمية، حسب الصنف أو العميل أو الشهر",
			"Posted sales invoice lines: revenue, cost, margin and quantity, by item, customer or month");

		public override AskText Rules => AskText.Of(
			"فواتير مرحَّلة فقط (المسودات والفواتير الملغاة غير مشمولة) · كل المبالغ محوَّلة إلى العملة "
			+ "الأساسية بسعر صرف فاتورتها · السطور غير المرتبطة بصنف مشمولة وظاهرة باسمها الخاص · "
			+ "التكلفة هي المسجَّلة وقت البيع، وتكون غير معروفة لا صفرًا إذا لم تُسجَّل.",
			"Posted invoices only (drafts and reversals excluded) · every amount converted to the base "
			+ "currency at its own invoice's rate · lines with no item link are included and shown under "
			+ "their own label · cost is the one recorded at the time of sale, and is unknown rather than "
			+ "zero where none was recorded.");

		public static readonly string NotLinkedAr = "(سطر غير مرتبط بصنف)";
		public static readonly string NotLinkedEn = "(line with no item)";

		public override IReadOnlyList<AskColumn> Columns { get; } = new[]
		{
			new AskColumn { Name = "item", Kind = AskColumnKind.Dimension, Label = AskText.Of("الصنف", "Item"),
				Synonyms = new[] { "صنف", "اصناف", "الاصناف", "بضاعة", "البضاعة", "منتج", "منتجات", "المنتجات",
					"item", "items", "product", "products", "sku", "goods" } },
			new AskColumn { Name = "itemcode", Kind = AskColumnKind.Dimension, Label = AskText.Of("كود الصنف", "Item code"),
				Synonyms = new[] { "كود", "الكود", "code", "item code" } },
			// CATEGORY IS THE BEST-COVERED DIMENSION IN THE SYSTEM: 49 of them, every one of the 237
			// active items carries one, and no corrupted names. It was also completely unreachable
			// until now, so "المبيعات حسب الفئة" could not be asked at all.
			new AskColumn { Name = "category", Kind = AskColumnKind.Dimension, Label = AskText.Of("الفئة", "Category"),
				Synonyms = new[] { "فئة", "فئات", "الفئات", "تصنيف", "التصنيف", "تصنيفات", "التصنيفات",
					"مجموعة", "المجموعات", "قسم", "الاقسام",
					"category", "categories", "product category", "item category", "group", "groups",
					"classification" } },
			new AskColumn { Name = "customer", Kind = AskColumnKind.Dimension, Label = AskText.Of("العميل", "Customer"),
				Synonyms = new[] { "عميل", "عملاء", "العملاء", "زبون", "زباين", "الزباين", "زبائن",
					"customer", "customers", "client", "clients", "account", "accounts", "buyer", "buyers" } },
			new AskColumn { Name = "month", Kind = AskColumnKind.Dimension, Label = AskText.Of("الشهر", "Month"),
				Synonyms = new[] { "شهر", "شهري", "شهور", "الشهور", "شهريا", "بالشهر",
					"month", "monthly", "months", "per month", "by month" } },
			new AskColumn { Name = "date", Kind = AskColumnKind.Dimension, Label = AskText.Of("التاريخ", "Date"),
				Synonyms = new[] { "تاريخ", "يوم", "يومي", "بالتاريخ", "date", "day", "daily" } },
			new AskColumn { Name = "invoice", Kind = AskColumnKind.Dimension, Label = AskText.Of("الفاتورة", "Invoice"),
				Synonyms = new[] { "فاتورة", "فواتير", "الفواتير", "invoice", "invoices", "bill", "bills" } },

			new AskColumn { Name = "revenue", Kind = AskColumnKind.Measure, IsMoney = true, Label = AskText.Of("الإيراد", "Revenue"),
				Synonyms = new[] { "ايراد", "الايراد", "ايرادات", "ايرادا", "ايراداً", "ايرادًا", "مبيعات", "المبيعات", "مبيعا", "مبيعاً", "مبيعًا", "بعنا", "بيعنا",
					"مبيعاتنا", "دخل", "الدخل", "حصيلة", "فلوس", "revenue", "sales", "turnover", "income",
					"sold", "money", "takings" } },
			// THE SAME TIER AS MARGIN, BECAUSE REVENUE MINUS COST IS MARGIN. Guarding the margin
			// column while leaving cost open would be theatre: anyone who can read both figures
			// computes the third in their head, and the surface would have protected a name rather
			// than a fact.
			new AskColumn { Name = "cost", Kind = AskColumnKind.Measure, IsMoney = true, Nullable = true,
				Action = CrossBuy.BL.AccountingActions.ProfitabilityReportRead, Label = AskText.Of("التكلفة", "Cost"),
				Synonyms = new[] { "تكلفة", "التكلفة", "تكلفه", "كلفة", "تكاليف", "تكلفه", "تكلفةً", "كلفه", "اشترينا",
					"cost", "costs", "cogs", "cost of goods", "purchase cost" } },
			// WHAT THE COMPANY EARNED, NOT WHAT IT BILLED — the confidential tier, and accounting
			// states that it is NEVER implied by report-read: an Accountant and an Auditor both hold
			// that and neither holds this. The assistant was handing the margin on every invoice to
			// anyone who could open a screen.
			new AskColumn { Name = "margin", Kind = AskColumnKind.Measure, IsMoney = true, Nullable = true,
				Action = CrossBuy.BL.AccountingActions.ProfitabilityReportRead, Label = AskText.Of("الهامش", "Margin"),
				Synonyms = new[] { "هامش", "الهامش", "ربح", "الربح", "ارباح", "مكسب", "بنكسب", "كسب",
					"ربحية", "ربحا", "ربحًا", "مكسبا", "margin", "profit", "profitability", "gross margin", "markup", "earn" } },
			new AskColumn { Name = "qty", Kind = AskColumnKind.Measure, Label = AskText.Of("الكمية", "Quantity"),
				Synonyms = new[] { "كمية", "الكمية", "كميات", "كميه", "عددا", "عدد", "العدد", "اتباع", "اتباعت", "قطع",
					"quantity", "qty", "units", "volume", "count", "how many" } },
		};

		public override async Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> RowsAsync(
			AskScope scope, CancellationToken ct = default)
		{
			var arabic = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ar";

			var lines = await (
				from l in _db.SalesInvoiceLines.AsNoTracking()
				join s in _db.SalesInvoices.AsNoTracking() on l.SalesInvoiceId equals s.ID
				where s.CompanyID == scope.CompanyId && s.Status == "Posted"
				select new
				{
					l.ItemId,
					// THE LINE'S OWN TEXT. Carried because 144 posted lines name no catalogue item and
					// every one of them has a description — see the labelling below.
					l.ItemDescription,
					l.ItemDescriptionEn,
					l.Qty,
					l.LineTotal,
					Rate = s.ExchangeRate ?? 1m,
					s.InvoiceDate,
					s.InvoiceNo,
					s.CustomerId,
					InvoiceId = s.ID,
				}).ToListAsync(ct);

			if (lines.Count == 0) return Array.Empty<IReadOnlyDictionary<string, object?>>();

			var itemIds = lines.Where(l => l.ItemId != null).Select(l => l.ItemId!.Value).Distinct().ToList();
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

			var customerIds = lines.Select(l => l.CustomerId).Distinct().ToList();
			var customers = (await _db.Customers.AsNoTracking()
					.Where(c => c.CompanyID == scope.CompanyId && customerIds.Contains(c.ID))
					.Select(c => new { c.ID, c.Name, c.NameEn })
					.ToListAsync(ct))
				.ToDictionary(c => c.ID);

			// COST PER INVOICE AND ITEM, from the movement the posting produced. Keyed by both, so a
			// cost is attributed to the sale that caused it rather than smeared across an item's
			// whole history.
			var movements = await _db.StockMovements.AsNoTracking()
				.Where(m => m.CompanyID == scope.CompanyId && m.SourceType == "SalesInvoice" && m.Direction < 0)
				.Select(m => new { m.SourceId, m.ItemId, m.TotalCost })
				.ToListAsync(ct);

			var costs = movements
				.GroupBy(m => (m.SourceId, m.ItemId))
				.ToDictionary(g => g.Key, g => g.Sum(x => x.TotalCost));

			var rows = new List<IReadOnlyDictionary<string, object?>>(lines.Count);
			foreach (var l in lines)
			{
				string itemLabel;
				string? itemCode;
				// THE ROW'S OWN SCREEN. Built here because this is the only point at which the line
				// is known to be about one item; after grouping that is no longer true.
				string? href = null;
				// A LINE WITH NO ITEM HAS NO CATEGORY EITHER, and is labelled as such rather than
				// swept into a blank group — 93.5% of revenue sits on these lines, so a category
				// total that hid them would be describing six per cent of the money.
				var categoryLabel = arabic ? NotLinkedAr : NotLinkedEn;
				if (l.ItemId != null && items.TryGetValue(l.ItemId.Value, out var it))
				{
					itemLabel = AskItems.Readable(arabic ? it.Name : it.NameEn, arabic ? it.NameEn : it.Name)
						?? it.ItemCode;
					itemCode = it.ItemCode;
					href = "/Inventory/EditItem/" + l.ItemId.Value.ToString(CultureInfo.InvariantCulture);
					categoryLabel = it.ItemCategoryId != 0
						&& categories.TryGetValue(it.ItemCategoryId, out var cat)
							? (AskItems.Readable(arabic ? cat.Name : cat.NameEn, arabic ? cat.NameEn : cat.Name)
								?? ("#" + cat.ID))
							: (arabic ? "(بلا فئة)" : "(no category)");
				}
				else
				{
					// A LINE WITH NO CATALOGUE ITEM IS NOT ANONYMOUS, AND HIDING IT COST A REAL ANSWER.
					//
					// These 144 lines used to collapse into one opaque "(not linked to an item)" row
					// carrying 1,843,196 — 93.5% of all revenue — which read as a data-quality problem
					// nobody could act on. Showing each line's own text instead answers it outright:
					// 1,754,000 of that is on lines called "MC test (USD)", "FX test" and "Reval test",
					// and 85,000 more is project demo billing. The genuinely unattributed revenue is
					// 4,196.
					//
					// So the money was never unexplained — it was unlabelled, and the label was sitting
					// on the row the whole time. Marked as line text so nobody mistakes it for a
					// catalogue item.
					var text = AskItems.Readable(
						arabic ? l.ItemDescription : l.ItemDescriptionEn,
						arabic ? l.ItemDescriptionEn : l.ItemDescription);
					itemLabel = string.IsNullOrWhiteSpace(text)
						? (arabic ? NotLinkedAr : NotLinkedEn)
						: (arabic ? text + " (وصف سطر)" : text + " (line text)");
					itemCode = null;
				}

				customers.TryGetValue(l.CustomerId, out var cust);
				var customerLabel = cust == null
					? "#" + l.CustomerId
					: AskItems.Readable(arabic ? cust.Name : cust.NameEn, arabic ? cust.NameEn : cust.Name);

				// ---- the money, already converted. There is no un-converted column to expose. ----
				var revenue = l.LineTotal * l.Rate;

				decimal? cost = null;
				if (l.ItemId != null && costs.TryGetValue((l.InvoiceId, l.ItemId.Value), out var c) && c > 0)
					cost = c;

				rows.Add(new Dictionary<string, object?>
				{
					["item"] = itemLabel,
					["itemcode"] = itemCode,
					["category"] = categoryLabel,
					["customer"] = customerLabel,
					// BOTH SPELLINGS, FOR MATCHING ONLY. A customer displayed as "Nile Co" is
					// «شركة النيل» in the other column, and the asker types the one they think in.
					// It also rescues a name whose Arabic was destroyed by a codepage write: the
					// English spelling is still findable even when the Arabic reads as «?????».
					[SearchKeyFor("customer")] = cust == null ? null
						: ((cust.Name ?? "") + " " + (cust.NameEn ?? "")).Trim(),
					["month"] = l.InvoiceDate.ToString("yyyy-MM", CultureInfo.InvariantCulture),
					["date"] = l.InvoiceDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
					["invoice"] = l.InvoiceNo,
					["revenue"] = revenue,
					["cost"] = cost,
					// NULL WITH THE COST, not revenue-minus-nothing.
					["margin"] = cost == null ? (decimal?)null : revenue - cost.Value,
					["qty"] = l.Qty,
					[LinkFor("item")] = href,
					[LinkFor("customer")] = cust == null
						? null
						: "/Accounting/Customers?q=" + Uri.EscapeDataString(customerLabel),
					[LinkFor("invoice")] = string.IsNullOrWhiteSpace(l.InvoiceNo)
						? null
						: "/Accounting/SalesInvoices?q=" + Uri.EscapeDataString(l.InvoiceNo!),
				});
			}
			return rows;
		}
	}
}
