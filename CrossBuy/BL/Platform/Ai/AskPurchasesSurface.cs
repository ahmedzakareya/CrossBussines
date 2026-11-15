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
	// THE PURCHASES SURFACE — what we bought, from whom, for how much.
	//
	// ADDED BECAUSE IT WAS NOT THERE AT ALL. The owner asked "طيب هات الاصناف المشتراه" and was
	// refused. The refusal was honest — there was no purchases capability of any kind — but a system
	// that can tell you what it SOLD and not what it BOUGHT is half an answer to every question about
	// money.
	//
	// MEASURED BEFORE BUILT, as everything here is:
	//
	//   110 posted purchase invoices · 117 lines · 17,829 in line value
	//   14,258 of that (80%) sits on a line that names a catalogue item — far better attribution
	//   than sales, where only 6.5% does
	//   5 distinct catalogue items have ever been purchased, three of them obviously test items
	//   10 vendors
	//
	// So the honest answer to "what did we buy" is a short list. Five items is a real finding about
	// this company's data, not a defect in this code, and nothing here inflates it.
	//
	// THE JUDGEMENTS, as with sales:
	//
	//   1. POSTED ONLY. A draft purchase is not a purchase.
	//
	//   2. CONVERTED AT ITS OWN INVOICE'S RATE. There is no raw `lineTotal` column to reach, so
	//      nobody can add one currency to another here.
	//
	//   3. A LINE WITH NO CATALOGUE ITEM STILL HAS A NAME. This is the one place purchases are
	//      BETTER off than sales: PurchaseInvoiceLine carries its own ItemDescription, and all 71
	//      unlinked lines have one — only 3 distinct values, none blank. So instead of collapsing
	//      61% of lines into one opaque "(no item)" bucket, each is grouped under what was actually
	//      written on it, marked so nobody mistakes it for a catalogue item. The money stays
	//      attributed to something a person can read.
	//
	//   4. NO MARGIN, AND NO PRETENCE OF ONE. A purchase has a cost and no revenue. Exposing a
	//      margin column here would invite a question whose answer could only be wrong.
	// =================================================================================================
	public sealed class AskPurchasesSurface : AskSurface
	{
		public const string SurfaceCode = "purchases.lines";

		private readonly CrossDbContext _db;
		public AskPurchasesSurface(CrossDbContext db) => _db = db ?? throw new ArgumentNullException(nameof(db));

		public override string Code => SurfaceCode;

		// Accounting, like sales: these are supplier invoices and what we paid, which is not the
		// same permission as being allowed to see stock on a shelf.
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
			"سطور فواتير الشراء المرحَّلة: المبلغ والكمية، حسب الصنف أو المورد أو الشهر",
			"Posted purchase invoice lines: amount and quantity, by item, vendor or month");

		public override AskText Rules => AskText.Of(
			"فواتير شراء مرحَّلة فقط (المسودات غير مشمولة) · كل المبالغ محوَّلة إلى العملة الأساسية "
			+ "بسعر صرف فاتورتها · السطر غير المرتبط بصنف من الكتالوج يظهر بالوصف المكتوب عليه "
			+ "ومعلَّمًا بذلك، ولا يُستبعَد · لا يوجد هامش ربح في الشراء.",
			"Posted purchase invoices only (drafts excluded) · every amount converted to the base "
			+ "currency at its own invoice's rate · a line with no catalogue item is shown under the "
			+ "description written on it, marked as such, and is never dropped · a purchase has no "
			+ "profit margin.");

		/// Marks a label that came from the line's own text rather than from the item catalogue.
		/// Shown so a reader can tell a real item from a typed description at a glance.
		public static string Described(string text, bool arabic) =>
			arabic ? text + " (وصف سطر)" : text + " (line text)";

		public static readonly string NoDescriptionAr = "(سطر بلا وصف)";
		public static readonly string NoDescriptionEn = "(line with no description)";

		public override IReadOnlyList<AskColumn> Columns { get; } = new[]
		{
			// A WORD THAT NAMES THE SURFACE MUST NOT NAME A COLUMN.
			//
			// "اشترينا", "المشتراه", "bought" and "purchased" were listed here as item synonyms, and
			// they broke "من مين اشترينا": the composer takes the LONGEST match, "اشترينا" (7) beat
			// "من مين" (6), and a question about WHO was answered by WHAT. By the time the composer
			// runs, the buying verb has already done its job — it is what routed the question to this
			// surface at all — so leaving it attached to a column lets it vote twice, and the second
			// vote is always noise.
			new AskColumn { Name = "item", Kind = AskColumnKind.Dimension, Label = AskText.Of("الصنف", "Item"),
				Synonyms = new[] { "صنف", "اصناف", "الاصناف", "بضاعة", "البضاعة",
					"منتج", "منتجات", "المنتجات",
					"item", "items", "product", "products", "sku", "goods" } },
			new AskColumn { Name = "itemcode", Kind = AskColumnKind.Dimension, Label = AskText.Of("كود الصنف", "Item code"),
				Synonyms = new[] { "كود", "الكود", "code", "item code" } },
			new AskColumn { Name = "category", Kind = AskColumnKind.Dimension, Label = AskText.Of("الفئة", "Category"),
				Synonyms = new[] { "فئة", "فئات", "الفئات", "تصنيف", "التصنيف", "تصنيفات", "التصنيفات",
					"مجموعة", "المجموعات", "قسم", "الاقسام",
					"category", "categories", "item category", "group", "groups", "classification" } },
			// "WHO" CAN ONLY MEAN THE VENDOR HERE, and that is why it is safe to claim the word.
			//
			// "من مين اشترينا" asks by WHOM and was answered by WHAT: it names no vendor word at all,
			// so nothing matched and the surface fell back to its default grouping by item. A
			// confident answer to a different question.
			//
			// A synonym belongs to ONE surface, not to the vocabulary at large. On purchases there is
			// no other person a question can be about — there is no customer here — so "مين" resolves
			// to the vendor without ambiguity. The same word on the sales surface would be a coin
			// toss, and it is deliberately not claimed there.
			new AskColumn { Name = "vendor", Kind = AskColumnKind.Dimension, Label = AskText.Of("المورد", "Vendor"),
				Synonyms = new[] { "مورد", "موردين", "الموردين", "المورد", "موردينا", "تاجر", "بياع",
					"من مين", "مين", "ممن", "من انهي", "جابنا", "بناخد من",
					"vendor", "vendors", "supplier", "suppliers", "from whom", "who from", "who do we buy from" } },
			new AskColumn { Name = "month", Kind = AskColumnKind.Dimension, Label = AskText.Of("الشهر", "Month"),
				Synonyms = new[] { "شهر", "شهري", "شهور", "الشهور", "شهريا", "بالشهر",
					"month", "monthly", "months", "per month", "by month" } },
			new AskColumn { Name = "date", Kind = AskColumnKind.Dimension, Label = AskText.Of("التاريخ", "Date"),
				Synonyms = new[] { "تاريخ", "يوم", "يومي", "بالتاريخ", "date", "day", "daily" } },
			new AskColumn { Name = "invoice", Kind = AskColumnKind.Dimension, Label = AskText.Of("الفاتورة", "Invoice"),
				Synonyms = new[] { "فاتورة", "فواتير", "الفواتير", "invoice", "invoices", "bill", "bills" } },

			new AskColumn { Name = "amount", Kind = AskColumnKind.Measure, IsMoney = true, Label = AskText.Of("المبلغ", "Amount"),
				// Same reasoning as the item column: the bare buying verbs are surface words, not
				// measure words, and are left out so they cannot outbid a dimension. "صرفنا" and
				// "دفعنا" stay because they name money specifically rather than the act of buying.
				Synonyms = new[] { "مبلغ", "المبلغ", "قيمة", "القيمة", "تكلفة", "التكلفة", "كلفة", "تكاليف",
					"صرفنا", "دفعنا",
					"amount", "spend", "spent", "cost", "purchase value", "paid" } },
			new AskColumn { Name = "qty", Kind = AskColumnKind.Measure, Label = AskText.Of("الكمية", "Quantity"),
				Synonyms = new[] { "كمية", "الكمية", "كميات", "كميه", "عدد", "العدد", "قطع",
					"quantity", "qty", "units", "volume", "count", "how many" } },
		};

		public override async Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> RowsAsync(
			AskScope scope, CancellationToken ct = default)
		{
			var arabic = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ar";

			var lines = await (
				from l in _db.PurchaseInvoiceLines.AsNoTracking()
				join p in _db.PurchaseInvoices.AsNoTracking() on l.PurchaseInvoiceId equals p.ID
				where p.CompanyID == scope.CompanyId && p.Status == "Posted"
				select new
				{
					l.ItemId,
					l.ItemDescription,
					l.ItemDescriptionEn,
					l.Qty,
					l.LineTotal,
					Rate = p.ExchangeRate ?? 1m,
					p.InvoiceDate,
					p.InvoiceNo,
					p.VendorId,
					InvoiceId = p.ID,
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

			var vendorIds = lines.Select(l => l.VendorId).Distinct().ToList();
			var vendors = (await _db.Vendors.AsNoTracking()
					.Where(v => v.CompanyID == scope.CompanyId && vendorIds.Contains(v.ID))
					.Select(v => new { v.ID, v.Name, v.NameEn })
					.ToListAsync(ct))
				.ToDictionary(v => v.ID);

			var rows = new List<IReadOnlyDictionary<string, object?>>(lines.Count);
			foreach (var l in lines)
			{
				string itemLabel;
				string? itemCode;
				string? href = null;
				var categoryLabel = arabic ? "(بلا فئة)" : "(no category)";

				if (l.ItemId != null && items.TryGetValue(l.ItemId.Value, out var it))
				{
					itemLabel = AskItems.Readable(arabic ? it.Name : it.NameEn, arabic ? it.NameEn : it.Name)
						?? it.ItemCode;
					itemCode = it.ItemCode;
					// THE ROW CAN BE OPENED. A catalogue item has a real screen, so the answer links
					// to it rather than naming something the reader then has to go and find.
					href = "/Inventory/EditItem/" + l.ItemId.Value.ToString(CultureInfo.InvariantCulture);
					categoryLabel = it.ItemCategoryId != 0
						&& categories.TryGetValue(it.ItemCategoryId, out var cat)
							? (AskItems.Readable(arabic ? cat.Name : cat.NameEn, arabic ? cat.NameEn : cat.Name)
								?? ("#" + cat.ID))
							: categoryLabel;
				}
				else
				{
					// NOT A CATALOGUE ITEM, BUT NOT ANONYMOUS EITHER. The line's own text is used and
					// marked, so 61% of lines stay readable instead of collapsing into one bucket.
					var text = AskItems.Readable(
						arabic ? l.ItemDescription : l.ItemDescriptionEn,
						arabic ? l.ItemDescriptionEn : l.ItemDescription);
					itemLabel = string.IsNullOrWhiteSpace(text)
						? (arabic ? NoDescriptionAr : NoDescriptionEn)
						: Described(text!, arabic);
					itemCode = null;
					categoryLabel = arabic ? "(ليس صنفًا من الكتالوج)" : "(not a catalogue item)";
				}

				vendors.TryGetValue(l.VendorId, out var ven);
				var vendorLabel = ven == null
					? "#" + l.VendorId
					: AskItems.Readable(arabic ? ven.Name : ven.NameEn, arabic ? ven.NameEn : ven.Name);

				rows.Add(new Dictionary<string, object?>
				{
					["item"] = itemLabel,
					["itemcode"] = itemCode,
					["category"] = categoryLabel,
					["vendor"] = vendorLabel,
					// Both spellings, for matching only - the displayed label stays the one above.
					[SearchKeyFor("vendor")] = ven == null ? null
						: ((ven.Name ?? "") + " " + (ven.NameEn ?? "")).Trim(),
					["month"] = l.InvoiceDate.ToString("yyyy-MM", CultureInfo.InvariantCulture),
					["date"] = l.InvoiceDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
					["invoice"] = l.InvoiceNo,
					["amount"] = l.LineTotal * l.Rate,
					["qty"] = l.Qty,
					// Carried on the row, not computed later: only here is it known which single
					// thing this line is about. The runner decides whether a GROUP may keep it.
					// EVERY DIMENSION THAT HAS A SCREEN GETS ONE. Which of these the answer shows is
					// decided by what the question grouped on, not here.
					[LinkFor("item")] = href,
					[LinkFor("vendor")] = ven == null
						? null
						: "/Accounting/Vendors?q=" + Uri.EscapeDataString(vendorLabel),
					[LinkFor("invoice")] = string.IsNullOrWhiteSpace(l.InvoiceNo)
						? null
						: "/Accounting/PurchaseInvoices?q=" + Uri.EscapeDataString(l.InvoiceNo!),
				});
			}
			return rows;
		}
	}
}
