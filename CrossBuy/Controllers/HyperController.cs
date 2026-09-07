using CrossBuy.BL;
using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Pos;
using CrossBuy.Models.Menu;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.Controllers
{
	// HM-0: the hypermarket BACK-OFFICE at /hyper. Gated by the normal Employee session (SessionValidationMiddleware) —
	// this is the switcher tile target. Its OWN sidebar (MainMenu.Hyper) on the shared back-office shell (_LayoutBackend),
	// exactly the pattern the other systems use. The independent cashier LANE lives separately at /hyper/pos.
	// HM-0 ships ONE screen: a read-only system diagnostic panel. No selling.
	[Route("hyper")]
	public class HyperController : Controller
	{
		private const int PosCompanyId = 1;   // catalog/accounts company (matches PosAppController.PosCompanyId)
		private readonly IPosSetupService _pos;
		private readonly CrossDbContext _db;
		private readonly ICurrencyService _currency;      // HM-10 slice B: today's rate for the INDICATIVE document-currency margin column
		private readonly ICurrencyRounding _rounding;     // HM-10 slice B: document (KWD 3dp) vs functional (EGP 2dp) precision
		public HyperController(IPosSetupService pos, CrossDbContext db, ICurrencyService currency, ICurrencyRounding rounding)
		{ _pos = pos; _db = db; _currency = currency; _rounding = rounding; }

		// HM-0: the hypermarket capability catalog — SEPARATE from the restaurant list (PosController.Capabilities),
		// so the two systems never share a capability editor. KEYS ONLY — no business logic sits behind any of them
		// in HM-0. "Weight" reuses the existing key; the rest are the hypermarket-specific keys.
		public static readonly (string key, string ar, string en)[] Capabilities = new[]
		{
			("Weight",        "Weight",            "Weight"),
			("BarcodeMulti",  "باركود متعدّد",     "Multi-barcode"),
			("ExpiryControl", "ضبط الصلاحية",      "Expiry control"),
			("Promotions",    "العروض",           "Promotions"),
			("CustomerIdentity", "تعريف العميل",   "Customer identity"),   // HM-9 slice 1: link a real customer to the order (identity only)
			("Loyalty",       "الولاء",           "Loyalty"),               // HM-9 slice 2: points earning (kept OFF until redemption ships)
			("ShelfLabels",   "ملصقات الرفوف",     "Shelf labels"),
			("PriceCheck",    "استعلام السعر",     "Price check"),
			("SuspendResume", "تعليق/استئناف",     "Suspend / resume"),
			("CashDrawer",    "درج النقد",         "Cash drawer"),
		};

		// GET /hyper  and  GET /hyper/dashboard — the system diagnostic panel.
		[HttpGet("")]
		[HttpGet("dashboard")]
		public async Task<IActionResult> Dashboard(int? branchId = null)
		{
			ViewBag.SidebarMenu = MainMenu.Hyper();   // _LayoutBackend renders this as the sidebar

			// ISOLATION: only hyper-activity branches ever appear here (a restaurant branch is never listed).
			var hyperBranches = await _db.Branches.AsNoTracking()
				.Where(b => b.ActivityPresetCode == "Hyper")
				.OrderBy(b => b.ID)
				.Select(b => new HyperBranchItem { Id = b.ID, Name = b.Name, NameAr = b.NameAr })
				.ToListAsync();
			ViewBag.HyperBranches = hyperBranches;

			var cur = branchId ?? (hyperBranches.Count > 0 ? hyperBranches[0].Id : (int?)null);
			if (cur == null) { ViewBag.NoBranch = true; return View(); }
			int bid = cur.Value;

			ViewBag.Branch = await _db.Branches.AsNoTracking().FirstOrDefaultAsync(b => b.ID == bid);

			// Capabilities read through the ONE central gate (HM-0's first real consumer) — never a direct EF query.
			var caps = new List<HyperCapRow>();
			foreach (var c in Capabilities)
				caps.Add(new HyperCapRow { Key = c.key, Ar = c.ar, En = c.en, On = await _pos.IsCapabilityEnabledAsync(bid, c.key) });
			ViewBag.Caps = caps;
			// distinguish "no preset applied at all" from "configured but off" (default stays false either way)
			ViewBag.HasCapConfig = await _pos.HasCapabilityConfigAsync(bid);

			ViewBag.Setting = await _pos.GetPosSettingAsync(bid);

			var terminals = await _pos.GetTerminalsAsync(bid);
			ViewBag.Terminals = terminals;
			var open = new Dictionary<int, PosShift?>();
			foreach (var t in terminals) open[t.ID] = await _pos.GetOpenShiftAsync(t.ID);
			ViewBag.OpenShifts = open;

			return View();
		}

		// ==================== HM-10 slice B: HYPER SALES REPORTS (read-only, hyper-activity only) ====================
		// All of these filter to Branch.ActivityPresetCode == "Hyper" (never a restaurant order). Read-only: no write, no GL,
		// no stock — the server computes, the view displays. Revenue is DOCUMENT currency (KWD 3dp, what was rung); COGS and
		// the authoritative margin are FUNCTIONAL (EGP 2dp) per the HM-2 boundary; a SECOND indicative margin column is shown
		// in the document currency converted at TODAY's rate, blank if today's rate is missing/stale (HM-D23 — never a bait rate).
		public class ItemMarginRow
		{
			public int ItemId; public string ItemName = ""; public int? CategoryId; public string CategoryName = "";
			public decimal Qty; public decimal RevenueDoc; public decimal Cogs; public decimal MarginFunc;
			public decimal? MarginPct; public decimal? MarginDocIndicative; public string Status = "";   // Costed | ZeroCost | Unposted
		}
		public class CategoryRow { public string CategoryName = ""; public decimal RevenueDoc; public decimal MarginFunc; public bool HasUnposted; }

		private async Task<List<HyperBranchItem>> HyperBranchListAsync() =>
			await _db.Branches.AsNoTracking().Where(b => b.CompanyID == PosCompanyId && b.ActivityPresetCode == "Hyper")
				.OrderBy(b => b.ID).Select(b => new HyperBranchItem { Id = b.ID, Name = b.Name, NameAr = b.NameAr }).ToListAsync();

		private async Task<List<int>> HyperBranchIdsAsync(int? branchId)
		{
			var all = await _db.Branches.AsNoTracking().Where(b => b.CompanyID == PosCompanyId && b.ActivityPresetCode == "Hyper").Select(b => b.ID).ToListAsync();
			return (branchId != null && all.Contains(branchId.Value)) ? new List<int> { branchId.Value } : all;
		}

		private async Task<int> DocDpAsync(List<int> branchIds)
		{
			int? ccy = branchIds.Count > 0 ? await _db.BranchPosSettings.AsNoTracking().Where(s => s.BranchId == branchIds[0]).Select(s => s.DefaultCurrencyId).FirstOrDefaultAsync() : null;
			return await _rounding.DecimalsAsync(PosCompanyId, (ccy == null || ccy == 0) ? (int?)null : ccy);
		}

		// today's EGP-per-KWD rate for the INDICATIVE column — null (blank column) if the rate is MISSING or stale (HM-D23:
		// never a bait rate). A missing rate is checked EXPLICITLY (ToBaseAsync would otherwise throw); staleness is honored
		// only when a max-age is configured.
		private async Task<decimal?> IndicativeEgpPerKwdAsync()
		{
			int funcId = await _currency.GetFunctionalCurrencyIdAsync(PosCompanyId, null);
			int kwdId = await _db.Currencies.AsNoTracking().Where(c => c.Code == "KWD").Select(c => c.ID).FirstOrDefaultAsync();
			if (kwdId == 0 || kwdId == funcId) return null;
			bool rateExists = await _db.ExchangeRates.AsNoTracking().AnyAsync(r => r.CurrencyId == kwdId && r.RateDate <= DateTime.Today);
			if (!rateExists) return null;   // no rate at all ⇒ blank (never fabricate one)
			var (stale, _a, _m, _b) = await _currency.RateStalenessAsync(PosCompanyId, kwdId, DateTime.Today);
			if (stale) return null;         // configured max-age exceeded ⇒ blank
			var (_amt, rate) = await _currency.ToBaseAsync(1m, kwdId, funcId, DateTime.Today, "Sell");
			return rate > 0m ? rate : (decimal?)null;
		}

		private async Task<List<ItemMarginRow>> ComputeItemRowsAsync(List<int> branchIds, DateTime from, DateTime toExcl, decimal? egpPerKwd, int funcDp, int docDp)
		{
			var invIds = await _db.PosOrders.AsNoTracking()
				.Where(o => o.CompanyId == PosCompanyId && branchIds.Contains(o.BranchId) && o.Status == "Paid" && o.InvoiceId != null && o.ClosedAt >= from && o.ClosedAt < toExcl)
				.Select(o => o.InvoiceId!.Value).ToListAsync();
			if (invIds.Count == 0) return new();
			var invRate = await _db.SalesInvoices.AsNoTracking().Where(i => invIds.Contains(i.ID)).Select(i => new { i.ID, i.ExchangeRate }).ToDictionaryAsync(x => x.ID, x => x.ExchangeRate ?? 1m);
			var lines = await _db.SalesInvoiceLines.AsNoTracking().Where(l => invIds.Contains(l.SalesInvoiceId) && l.ItemId != null)
				.Select(l => new { l.ID, l.SalesInvoiceId, ItemId = l.ItemId!.Value, l.Qty, l.LineTotal }).ToListAsync();
			var lineIds = lines.Select(l => l.ID).ToList();
			// per-line COGS signal, reusing the cogs_impact shape: a movement keyed (SourceType=SalesInvoice, SourceLineId).
			var moves = await _db.StockMovements.AsNoTracking()
				.Where(m => m.SourceType == "SalesInvoice" && m.SourceLineId != null && lineIds.Contains(m.SourceLineId.Value))
				.Select(m => new { LineId = m.SourceLineId!.Value, m.TotalCost }).ToListAsync();
			var costByLine = moves.GroupBy(m => m.LineId).ToDictionary(g => g.Key, g => g.Sum(x => x.TotalCost));
			var lineHasMove = new HashSet<int>(moves.Select(m => m.LineId));
			var itemIds = lines.Select(l => l.ItemId).Distinct().ToList();
			bool isAr = System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ar";
			var items = await _db.Items.AsNoTracking().Where(i => itemIds.Contains(i.ID)).Select(i => new { i.ID, i.Name, i.NameEn, i.ItemCategoryId }).ToListAsync();
			var catIds = items.Select(i => i.ItemCategoryId).Distinct().ToList();
			var cats = await _db.ItemCategories.AsNoTracking().Where(c => catIds.Contains(c.ID)).ToDictionaryAsync(c => c.ID, c => isAr ? c.Name : (c.NameEn ?? c.Name));
			var info = items.ToDictionary(i => i.ID, i => (name: isAr ? i.Name : (i.NameEn ?? i.Name), catId: i.ItemCategoryId, catName: cats.TryGetValue(i.ItemCategoryId, out var cn) ? cn : ""));

			var agg = new Dictionary<int, (decimal qty, decimal revDoc, decimal revFunc, decimal cogs, bool anyUnposted, bool anyCosted)>();
			foreach (var l in lines)
			{
				var rate = invRate.TryGetValue(l.SalesInvoiceId, out var r) ? r : 1m;
				agg.TryGetValue(l.ItemId, out var a);
				a.qty += l.Qty; a.revDoc += l.LineTotal; a.revFunc += l.LineTotal * rate;
				if (!lineHasMove.Contains(l.ID)) a.anyUnposted = true;                       // no COGS movement at all ⇒ un-posted cost
				else { var tc = costByLine.TryGetValue(l.ID, out var c) ? c : 0m; a.cogs += tc; if (tc > 0m) a.anyCosted = true; }   // movement TotalCost=0 ⇒ zero-cost
				agg[l.ItemId] = a;
			}
			decimal Rf(decimal v) => Math.Round(v, funcDp, MidpointRounding.AwayFromZero);
			decimal Rd(decimal v) => Math.Round(v, docDp, MidpointRounding.AwayFromZero);
			var rows = new List<ItemMarginRow>();
			foreach (var kv in agg)
			{
				var a = kv.Value; var it = info.TryGetValue(kv.Key, out var ii) ? ii : (name: "#" + kv.Key, catId: 0, catName: "");
				string status = a.anyUnposted ? "Unposted" : (a.anyCosted ? "Costed" : "ZeroCost");
				var row = new ItemMarginRow { ItemId = kv.Key, ItemName = it.name, CategoryId = it.catId, CategoryName = it.catName, Qty = a.qty, RevenueDoc = Rd(a.revDoc), Cogs = Rf(a.cogs), Status = status };
				if (status == "Unposted") { row.MarginFunc = 0m; row.MarginPct = null; row.MarginDocIndicative = null; }   // margin UNKNOWN — never 0 and never 100
				else
				{
					decimal marginFunc = a.revFunc - a.cogs;
					row.MarginFunc = Rf(marginFunc);
					row.MarginPct = a.revFunc != 0m ? Math.Round(marginFunc / a.revFunc * 100m, 1) : (decimal?)null;
					row.MarginDocIndicative = (egpPerKwd != null && egpPerKwd.Value > 0m) ? Rd(marginFunc / egpPerKwd.Value) : (decimal?)null;
				}
				rows.Add(row);
			}
			return rows.OrderByDescending(r => r.RevenueDoc).ToList();
		}

		// GET /hyper/reports/item-margin — per-item sales + margin for hyper branches, free date range.
		[HttpGet("reports/item-margin")]
		public async Task<IActionResult> ItemMargin(int? branchId = null, DateTime? from = null, DateTime? to = null)
		{
			ViewBag.SidebarMenu = MainMenu.Hyper();
			var f = (from ?? new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1)).Date;
			var toDay = (to ?? DateTime.Today).Date;
			var branchIds = await HyperBranchIdsAsync(branchId);
			int funcDp = await _rounding.DecimalsAsync(PosCompanyId, null);
			int docDp = await DocDpAsync(branchIds);
			var egpPerKwd = await IndicativeEgpPerKwdAsync();
			var rows = branchIds.Count == 0 ? new List<ItemMarginRow>() : await ComputeItemRowsAsync(branchIds, f, toDay.AddDays(1), egpPerKwd, funcDp, docDp);
			ViewBag.Rows = rows; ViewBag.From = f; ViewBag.To = toDay; ViewBag.FuncDp = funcDp; ViewBag.DocDp = docDp;
			ViewBag.HasRate = egpPerKwd != null; ViewBag.BranchId = branchId; ViewBag.HyperBranches = await HyperBranchListAsync();
			return View("~/Views/Hyper/HyperItemMargin.cshtml");
		}

		// GET /hyper/reports/sales — hyper sales overview (total, count, top items, by category), free date range. NO ByType.
		[HttpGet("reports/sales")]
		public async Task<IActionResult> Sales(int? branchId = null, DateTime? from = null, DateTime? to = null)
		{
			ViewBag.SidebarMenu = MainMenu.Hyper();
			var f = (from ?? new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1)).Date;
			var toDay = (to ?? DateTime.Today).Date; var toExcl = toDay.AddDays(1);
			var branchIds = await HyperBranchIdsAsync(branchId);
			int funcDp = await _rounding.DecimalsAsync(PosCompanyId, null);
			int docDp = await DocDpAsync(branchIds);
			var egpPerKwd = await IndicativeEgpPerKwdAsync();
			var rows = branchIds.Count == 0 ? new List<ItemMarginRow>() : await ComputeItemRowsAsync(branchIds, f, toExcl, egpPerKwd, funcDp, docDp);
			ViewBag.OrderCount = branchIds.Count == 0 ? 0 : await _db.PosOrders.AsNoTracking().CountAsync(o => o.CompanyId == PosCompanyId && branchIds.Contains(o.BranchId) && o.Status == "Paid" && o.InvoiceId != null && o.ClosedAt >= f && o.ClosedAt < toExcl);
			ViewBag.TotalRevenue = rows.Sum(r => r.RevenueDoc);                                    // document currency (KWD)
			ViewBag.TotalMargin = rows.Where(r => r.Status != "Unposted").Sum(r => r.MarginFunc);   // functional (authoritative)
			ViewBag.AnyUnposted = rows.Any(r => r.Status == "Unposted");
			ViewBag.TopItems = rows.Take(10).ToList();
			ViewBag.Categories = rows.GroupBy(r => r.CategoryName).Select(g => new CategoryRow { CategoryName = g.Key, RevenueDoc = g.Sum(x => x.RevenueDoc), MarginFunc = g.Where(x => x.Status != "Unposted").Sum(x => x.MarginFunc), HasUnposted = g.Any(x => x.Status == "Unposted") }).OrderByDescending(c => c.RevenueDoc).ToList();
			ViewBag.From = f; ViewBag.To = toDay; ViewBag.FuncDp = funcDp; ViewBag.DocDp = docDp; ViewBag.BranchId = branchId; ViewBag.HyperBranches = await HyperBranchListAsync();
			return View("~/Views/Hyper/HyperSales.cshtml");
		}

		public class HyperBranchItem { public int Id { get; set; } public string? Name { get; set; } public string? NameAr { get; set; } }
		public class HyperCapRow { public string Key { get; set; } = ""; public string Ar { get; set; } = ""; public string En { get; set; } = ""; public bool On { get; set; } }
	}
}
