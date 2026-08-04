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
		public HyperController(IPosSetupService pos, CrossDbContext db) { _pos = pos; _db = db; }

		// HM-0: the hypermarket capability catalog — SEPARATE from the restaurant list (PosController.Capabilities),
		// so the two systems never share a capability editor. KEYS ONLY — no business logic sits behind any of them
		// in HM-0. "Weight" reuses the existing key; the rest are the hypermarket-specific keys.
		public static readonly (string key, string ar, string en)[] Capabilities = new[]
		{
			("Weight",        "الوزن",            "Weight"),
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

		public class HyperBranchItem { public int Id { get; set; } public string? Name { get; set; } public string? NameAr { get; set; } }
		public class HyperCapRow { public string Key { get; set; } = ""; public string Ar { get; set; } = ""; public string En { get; set; } = ""; public bool On { get; set; } }
	}
}
