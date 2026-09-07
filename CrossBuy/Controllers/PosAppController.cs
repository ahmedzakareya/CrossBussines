using System.Text.Json;
using CrossBuy.BL;
using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Admin;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;

namespace CrossBuy.Controllers
{
	// POS-2: the INDEPENDENT cashier environment at /pos.
	// Separate full-screen layout (_LayoutPos), separate login, gated by a dedicated session key "PosCtx"
	// (so it never mixes with the admin gate which uses "Employee"). Roles are ENFORCED here.
	[Route("pos")]
	[CrossBuy.Models.PosLaneActivityGuard("PosCtx", "restaurant", "/pos/login")]
	public class PosAppController : Controller
	{
		private const int PosCompanyId = 1;
		private readonly SignInManager<Users> _signIn;
		private readonly UserManager<Users> _users;
		private readonly IPosAccessService _access;
		private readonly IPosSetupService _pos;
		private readonly IPosOrderService _orders;
		private readonly IBrandService _brand;
		private readonly IReceivableService _receivables;
		private readonly CrossDbContext _db;
		private readonly Microsoft.AspNetCore.SignalR.IHubContext<CrossBuy.Hubs.PosHub> _hub;
		private readonly IStringLocalizer<CrossBuy.SharedResources> L;
		public PosAppController(SignInManager<Users> signIn, UserManager<Users> users, IPosAccessService access,
			IPosSetupService pos, IPosOrderService orders, IBrandService brand, IReceivableService receivables, CrossDbContext db,
			Microsoft.AspNetCore.SignalR.IHubContext<CrossBuy.Hubs.PosHub> hub, IStringLocalizer<CrossBuy.SharedResources> localizer)
		{ _signIn = signIn; _users = users; _access = access; _pos = pos; _orders = orders; _brand = brand; _receivables = receivables; _db = db; _hub = hub; L = localizer; }

		// RC-3c: broadcast a POS event to the branch group. Fire-and-safe — SignalR is a notification layer only,
		// called AFTER a completed service op; a broadcast failure never affects the operation/GL.
		private async Task PosBroadcast(int branchId, string ev, object payload)
		{
			try { await _hub.Clients.Group(CrossBuy.Hubs.PosHub.BranchGroup(branchId)).SendAsync(ev, payload); } catch { }
		}

		// ---- session context ----
		public class PosCtx
		{
			public int EmployeeId { get; set; }
			public string EmployeeName { get; set; } = "";
			public string? EmployeeNameEn { get; set; }
			public string? EmployeePhoto { get; set; }
			public int BranchId { get; set; }
			public string BranchName { get; set; } = "";
			// Stage 1 Batch A: the branch's real company, carried so the screens that seed Session["Employee"]
			// can state their tenancy instead of leaving it unresolved (which used to become company 1).
			// A PosCtx blob written before this field existed deserialises it as null; the context factory then
			// falls through to claims → the Employee row, which resolves correctly. Safe degradation, no re-login.
			public int? BranchCompanyId { get; set; }
			public List<string> Roles { get; set; } = new();
			public int? TerminalId { get; set; }
			public string? TerminalCode { get; set; }
			public int? ShiftId { get; set; }
		}
		private PosCtx? Ctx()
		{
			var s = HttpContext.Session.GetString("PosCtx");
			return string.IsNullOrEmpty(s) ? null : JsonSerializer.Deserialize<PosCtx>(s);
		}
		private void SetCtx(PosCtx c) => HttpContext.Session.SetString("PosCtx", JsonSerializer.Serialize(c));

		// RC-3d: where a signed-in POS user lands. A KITCHEN-ONLY user (kitchen but not waiter/cashier/manager)
		// goes straight to the KDS — no terminal/shift/drawer. Everyone else takes the normal terminal path.
		private IActionResult HomeFor(PosCtx c)
		{
			if (_access.IsKitchen(c.Roles) && !_access.CanOrder(c.Roles)) return RedirectToAction(nameof(Kitchen));
			if (c.TerminalId != null && c.ShiftId != null) return RedirectToAction(nameof(Terminal));
			return RedirectToAction(nameof(Start));
		}

		// ==================== LOGIN (independent) ====================
		[HttpGet("login")][AllowAnonymous]
		public IActionResult Login()
		{
			var c = Ctx();
			if (c != null) return HomeFor(c);
			return View();
		}

		[HttpPost("login")][AllowAnonymous][ValidateAntiForgeryToken]
		public async Task<IActionResult> Login(string username, string password)
		{
			var user = await _users.FindByNameAsync(username ?? "");
			if (user == null || !user.IsActive) { TempData["PosErr"] = L["Invalid login credentials"].Value; return RedirectToAction(nameof(Login)); }
			var result = await _signIn.PasswordSignInAsync(user, password ?? "", false, false);
			if (!result.Succeeded) { TempData["PosErr"] = L["Invalid login credentials"].Value; return RedirectToAction(nameof(Login)); }
			var acc = await _access.ResolveByUserIdAsync(user.Id);
			if (acc == null) { await _signIn.SignOutAsync(); TempData["PosErr"] = L["You do not have cashier permission for any branch"].Value; return RedirectToAction(nameof(Login)); }
			// HM-1-أ (صفر-1/صفر-2): WHITELIST — the restaurant lane serves { Restaurant, Cafe } + no-activity branches
			// only, via the single shared helper. A hypermarket (or any other) branch is rejected with a clear message.
			var actPreset = await _db.Branches.AsNoTracking().Where(b => b.ID == acc.BranchId).Select(b => b.ActivityPresetCode).FirstOrDefaultAsync();
			var (laneOk, _) = _access.IsActivityAllowedForLane(actPreset, "restaurant");
			if (!laneOk) { await _signIn.SignOutAsync(); TempData["PosErr"] = L["This branch does not belong to the restaurant system"].Value; return RedirectToAction(nameof(Login)); }
			// HM-1/HM-D34: COMPANY GUARD at the gateway (HARD REJECT). After the HM-D34 relabel every legitimate employee/branch is
			// company 1, so an employee whose company differs from the branch's is a real cross-company login — refuse.
			var gEmpCo = await _db.Employee.AsNoTracking().Where(e => e.ID == acc.EmployeeId).Select(e => (int?)e.EmpCompanyID).FirstOrDefaultAsync();
			var gBrCo = await _db.Branches.AsNoTracking().Where(b => b.ID == acc.BranchId).Select(b => (int?)b.CompanyID).FirstOrDefaultAsync();
			if (gEmpCo == null || gBrCo == null || gEmpCo.Value != gBrCo.Value) { await _signIn.SignOutAsync(); TempData["PosErr"] = L["Your account belongs to another company than this branch — cross-company operations are blocked."].Value; return RedirectToAction(nameof(Login)); }
			var ctx = new PosCtx { EmployeeId = acc.EmployeeId, EmployeeName = acc.EmployeeName, EmployeeNameEn = acc.EmployeeNameEn, EmployeePhoto = acc.EmployeePhoto, BranchId = acc.BranchId, BranchName = acc.BranchName, BranchCompanyId = acc.BranchCompanyId, Roles = acc.Roles };
			SetCtx(ctx);
			return HomeFor(ctx);   // kitchen-only → KDS; others → start
		}

		[HttpGet("logout")]
		public async Task<IActionResult> Logout()
		{
			await _signIn.SignOutAsync();
			HttpContext.Session.Remove("PosCtx");
			return RedirectToAction(nameof(Login));
		}

		// ==================== START (pick terminal + shift) ====================
		[HttpGet("start")]
		public async Task<IActionResult> Start()
		{
			var c = Ctx(); if (c == null) return RedirectToAction(nameof(Login));
			var terminals = await _pos.GetTerminalsAsync(c.BranchId);
			var open = new Dictionary<int, CrossBuy.Models.Context.Pos.PosShift?>();
			foreach (var t in terminals) open[t.ID] = await _pos.GetOpenShiftAsync(t.ID);
			// HM-1-أ (صفر-1): a no-activity (NULL) branch is allowed here for backward compat, but surfaced as a warning.
			var actCode = await _db.Branches.AsNoTracking().Where(b => b.ID == c.BranchId).Select(b => b.ActivityPresetCode).FirstOrDefaultAsync();
			ViewBag.NoActivity = string.IsNullOrWhiteSpace(actCode);
			ViewBag.Ctx = c; ViewBag.Terminals = terminals; ViewBag.OpenShifts = open;
			return View();
		}

		[HttpPost("start")][ValidateAntiForgeryToken]
		public async Task<IActionResult> Start(int terminalId, string? shiftType, decimal openingFloat, int? resumeShiftId)
		{
			var c = Ctx(); if (c == null) return RedirectToAction(nameof(Login));
			var term = await _db.PosTerminals.FirstOrDefaultAsync(t => t.ID == terminalId && t.BranchId == c.BranchId && t.IsActive);
			if (term == null) { TempData["PosErr"] = L["Device not found"].Value; return RedirectToAction(nameof(Start)); }
			var open = await _pos.GetOpenShiftAsync(terminalId);
			if (open == null)
			{
				var (ok, err) = await _pos.OpenShiftAsync(terminalId, shiftType ?? "Morning", c.EmployeeId, openingFloat);
				if (!ok) { TempData["PosErr"] = err; return RedirectToAction(nameof(Start)); }
				open = await _pos.GetOpenShiftAsync(terminalId);
			}
			c.TerminalId = term.ID; c.TerminalCode = term.Code; c.ShiftId = open!.ID; SetCtx(c);
			return RedirectToAction(nameof(Terminal));
		}

		// ==================== TERMINAL (full-screen SPA) ====================
		[HttpGet("terminal")]
		public async Task<IActionResult> Terminal()
		{
			var c = Ctx(); if (c == null) return RedirectToAction(nameof(Login));
			if (c.TerminalId == null || c.ShiftId == null) return RedirectToAction(nameof(Start));
			ViewBag.Ctx = c;
			ViewBag.CanSell = _access.CanSell(c.Roles);
			ViewBag.IsManager = _access.IsManager(c.Roles);   // RC-6c: void / partial-return are manager-only
			ViewBag.Menu = await _pos.GetQuickMenuAsync(PosCompanyId, c.BranchId);
			var menu = (List<PosQuickMenuGroupDto>)ViewBag.Menu;
			var ids = menu.SelectMany(g => g.Items).Select(i => i.ItemId).Distinct().ToList();
			ViewBag.Images = await _db.Items.AsNoTracking().Where(i => ids.Contains(i.ID)).ToDictionaryAsync(i => i.ID, i => i.ImagePath);
			// RC-4b: which of the shown items have modifier groups → the client opens the chooser Modal only for these (fast path otherwise)
			ViewBag.ModItemIds = await (from lnk in _db.ItemModifierGroups.AsNoTracking()
										join g in _db.ModifierGroups.AsNoTracking() on lnk.GroupId equals g.ID
										where ids.Contains(lnk.ItemId) && g.CompanyID == PosCompanyId && g.IsActive
										select lnk.ItemId).Distinct().ToListAsync();
			ViewBag.PosSetting = await _pos.GetPosSettingAsync(c.BranchId);
			// The terminal ALWAYS starts empty: park any leftover open takeaway order (→ «معلّقة» if it has
			// items, void if empty) so nothing carries over from a previous order and nothing is lost.
			await _orders.ParkOrphanWalkInsAsync(PosCompanyId, c.BranchId);
			ViewBag.OpenOrder = null;
			// Dine-in FLOOR BOARD: halls (dining areas) each with their tables laid out by X/Y/W/H.
			// Per-table status = Occupied (derived from an Open order, carries the sit-down time) OR the
			// manual staff status (Available/Reserved/Cleaning/Closed). Stats computed client-side.
			ViewBag.Halls = await BuildHallsAsync(c.BranchId);
			// payment methods configured for this branch (RC-2 posts Cash only; others shown, activated later)
			ViewBag.PayMethods = await _pos.GetPaymentMethodsAsync(c.BranchId);
			// POS-3: receipt header (Brand identity w/ company fallback) + tax no + terminal print settings — embedded for offline printing
			var idn = await _brand.ResolveIdentityAsync(c.BranchId);
			var taxNo = await (from b in _db.Branches join co in _db.Companies on b.CompanyID equals co.CompanyID where b.ID == c.BranchId select co.TaxNumber).FirstOrDefaultAsync();
			var term = await _db.PosTerminals.AsNoTracking().FirstOrDefaultAsync(t => t.ID == c.TerminalId);
			var ar = HttpContext.Items["Culture"]?.ToString() == "ar";
			ViewBag.Receipt = new
			{
				name = idn.TradeName ?? idn.Name ?? c.BranchName,
				logo = idn.LogoPath,
				address = idn.Address,
				phone = idn.Phone,
				taxNo,
				footer = ar ? (idn.ReceiptFooterAr ?? idn.ReceiptFooterEn) : (idn.ReceiptFooterEn ?? idn.ReceiptFooterAr),
				paperWidth = term?.ReceiptPaperWidthMm ?? 80,
				copies = term?.ReceiptCopies ?? 1,
			};
			return View();
		}

		// ---- JSON sell endpoints (all gated; pay enforces role) ----
		[HttpPost("order/create")][ValidateAntiForgeryToken]
		public async Task<IActionResult> CreateOrder(string orderType = "Takeaway", int? tableId = null)
		{
			var c = Ctx(); if (c?.TerminalId == null || c.ShiftId == null) return Json(new { ok = false, error = L["Session expired"].Value }); if (!_access.CanOrder(c.Roles)) return Json(new { ok = false, error = L["This role is not allowed to operate orders"].Value });
			var (ok, err, id) = await _orders.CreateOrderAsync(PosCompanyId, c.BranchId, orderType, tableId, null, c.TerminalId, c.ShiftId);
			return Json(new { ok, error = err, order = ok ? await _orders.GetOrderAsync(PosCompanyId, id) : null });
		}

		// Dine-in: open the table's existing order (recall) or start a new one for it.
		// This is what makes several tables run concurrently — each keeps its own open order.
		// orderId>0 → recall THAT specific order (a table may host several parties, so the menu passes the exact one).
		[HttpPost("order/open-table")][ValidateAntiForgeryToken]
		public async Task<IActionResult> OpenTable(int tableId, int orderId = 0)
		{
			var c = Ctx(); if (c?.TerminalId == null || c.ShiftId == null) return Json(new { ok = false, error = L["Session expired"].Value }); if (!_access.CanOrder(c.Roles)) return Json(new { ok = false, error = L["This role is not allowed to operate orders"].Value });
			var areaIds = _db.DiningAreas.Where(a => a.BranchId == c.BranchId).Select(a => a.ID);
			var tbl = await _db.RestaurantTables.FirstOrDefaultAsync(t => t.ID == tableId && areaIds.Contains(t.DiningAreaId) && t.IsActive);
			if (tbl == null) return Json(new { ok = false, error = L["Table not found"].Value });
			if (tbl.Status == "Closed") return Json(new { ok = false, error = L["Table is closed"].Value });
			int oid; bool recalled;
			if (orderId > 0)
			{
				var chk = await _orders.GetOrderAsync(PosCompanyId, orderId);
				if (chk == null || chk.Status != "Open" || chk.TableId != tableId) return Json(new { ok = false, error = L["The order is not available on this table"].Value });
				oid = orderId; recalled = true;
			}
			else
			{
				var existing = await _orders.GetOpenOrderByTableAsync(PosCompanyId, c.BranchId, tableId);
				if (existing != null) { oid = existing.Value; recalled = true; }
				else
				{
					var (ok, err, id) = await _orders.CreateOrderAsync(PosCompanyId, c.BranchId, "Dine-in", tableId, null, c.TerminalId, c.ShiftId);
					if (!ok) return Json(new { ok = false, error = err });
					oid = id; recalled = false;
				}
			}
			return Json(new { ok = true, recalled, order = await _orders.GetOrderAsync(PosCompanyId, oid) });
		}

		// All open orders currently on a table (for the table menu: a table may hold several parties).
		[HttpGet("table/orders")]
		public async Task<IActionResult> TableOrders(int tableId)
		{
			var c = Ctx(); if (c == null) return Json(new { ok = false });
			var orders = await _orders.GetOpenOrdersByTableAsync(PosCompanyId, c.BranchId, tableId);
			var seats = await _db.RestaurantTables.Where(t => t.ID == tableId).Select(t => t.Seats).FirstOrDefaultAsync();
			return Json(new { ok = true, seats, orders });
		}

		// Open an ADDITIONAL (new-party) order on a table — allowed while the open-order count is below the seat count.
		[HttpPost("order/new-on-table")][ValidateAntiForgeryToken]
		public async Task<IActionResult> NewOrderOnTable(int tableId)
		{
			var c = Ctx(); if (c?.TerminalId == null || c.ShiftId == null) return Json(new { ok = false, error = L["Session expired"].Value }); if (!_access.CanOrder(c.Roles)) return Json(new { ok = false, error = L["This role is not allowed to operate orders"].Value });
			var (ok, err, id) = await _orders.AddOrderOnTableAsync(PosCompanyId, c.BranchId, tableId, null, c.TerminalId, c.ShiftId);
			return Json(new { ok, error = err, order = ok ? await _orders.GetOrderAsync(PosCompanyId, id) : null });
		}

		[HttpPost("order/add")][ValidateAntiForgeryToken]
		public async Task<IActionResult> AddLine(int orderId, int itemId, decimal qty = 1, string? optionIds = null)
		{
			var c = Ctx(); if (c == null) return Json(new { ok = false, error = L["Session expired"].Value }); if (!_access.CanOrder(c.Roles)) return Json(new { ok = false, error = L["This role is not allowed to operate orders"].Value });
			// RC-4: optionIds = comma-separated chosen modifier option ids (null/empty for a plain item)
			var opts = string.IsNullOrWhiteSpace(optionIds) ? null
				: optionIds.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(s => int.TryParse(s.Trim(), out var v) ? v : 0).Where(v => v > 0).ToList();
			var (ok, err) = await _orders.AddLineAsync(PosCompanyId, orderId, itemId, qty, opts);
			return Json(new { ok, error = err, order = await _orders.GetOrderAsync(PosCompanyId, orderId) });
		}

		// RC-4b: the add-time modifier chooser for an item (empty ⇒ the client adds instantly).
		[HttpGet("item/modifiers")]
		public async Task<IActionResult> ItemModifiers(int itemId)
		{
			var c = Ctx(); if (c == null) return Json(new { ok = false, error = L["Session expired"].Value }); if (!_access.CanOrder(c.Roles)) return Json(new { ok = false, error = L["This role is not allowed to operate orders"].Value });
			var groups = await _orders.GetItemModifiersAsync(PosCompanyId, itemId);
			return Json(new { ok = true, groups });
		}

		// RC-6c-1: FULL VOID of a paid order — pos-manager ONLY. Clean reversal (stock back + GL reversed + cash out).
		[HttpPost("order/void-paid")][ValidateAntiForgeryToken]
		public async Task<IActionResult> VoidPaid(int orderId)
		{
			var c = Ctx(); if (c == null) return Json(new { ok = false, error = L["Session expired"].Value });
			if (!_access.IsManager(c.Roles)) return Json(new { ok = false, error = L["Manager permission required"].Value });
			var (ok, err) = await _orders.VoidPaidOrderAsync(PosCompanyId, orderId, c.EmployeeId);
			return Json(new { ok, error = err });
		}

		// RC-6c-2: PARTIAL return of selected lines/qty from a paid order — pos-manager ONLY. allocations = JSON [{lineId,qty}].
		[HttpPost("order/return-lines")][ValidateAntiForgeryToken]
		public async Task<IActionResult> ReturnLines(int orderId, string allocations)
		{
			var c = Ctx(); if (c == null) return Json(new { ok = false, error = L["Session expired"].Value });
			if (!_access.IsManager(c.Roles)) return Json(new { ok = false, error = L["Manager permission required"].Value });
			List<CrossBuy.BL.SplitAllocation> allocs;
			try { allocs = System.Text.Json.JsonSerializer.Deserialize<List<CrossBuy.BL.SplitAllocation>>(allocations ?? "[]", new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new(); }
			catch { return Json(new { ok = false, error = L["Invalid data"].Value }); }
			var (ok, err, retId) = await _orders.ReturnOrderLinesAsync(PosCompanyId, orderId, allocs, c.EmployeeId);
			return Json(new { ok, error = err, returnId = retId });
		}

		// POS-9a: the OFFLINE BUNDLE — everything the cashier needs to run without the network, in one JSON.
		// Read-only; the client pulls this while online and stores it in IndexedDB (menu/prices/setup/capabilities/
		// modifiers/sourcing+recipes/tables/payment methods/receipt header/terminal). No GL/stock.
		[HttpGet("offline/bundle")]
		public async Task<IActionResult> OfflineBundle()
		{
			var c = Ctx(); if (c?.TerminalId == null) return Json(new { ok = false, error = L["Session expired"].Value });
			var menu = await _pos.GetQuickMenuAsync(PosCompanyId, c.BranchId);
			var ids = menu.SelectMany(g => g.Items).Select(i => i.ItemId).Distinct().ToList();
			var images = await _db.Items.AsNoTracking().Where(i => ids.Contains(i.ID)).ToDictionaryAsync(i => i.ID, i => i.ImagePath);
			var modItemIds = await (from lnk in _db.ItemModifierGroups.AsNoTracking()
									join g in _db.ModifierGroups.AsNoTracking() on lnk.GroupId equals g.ID
									where ids.Contains(lnk.ItemId) && g.CompanyID == PosCompanyId && g.IsActive
									select lnk.ItemId).Distinct().ToListAsync();
			// modifier chooser data per modifier-item (so the Modal works offline in 9b)
			var modifiers = new Dictionary<int, object>();
			foreach (var iid in modItemIds) modifiers[iid] = await _orders.GetItemModifiersAsync(PosCompanyId, iid);
			var sourcing = await _pos.GetBranchSourcingAsync(PosCompanyId, c.BranchId);
			// recipes for method-4 (RecipeAtSale) items — needed for offline backflush preview / order build (9b)
			var recipeItemIds = sourcing.Where(s => s.Method == "RecipeAtSale").Select(s => s.ItemId).ToList();
			var recipes = recipeItemIds.Count == 0 ? new List<object>() : await (from cc in _db.ItemComponents.AsNoTracking()
						  join it in _db.Items.AsNoTracking() on cc.ComponentItemId equals it.ID into gi from it in gi.DefaultIfEmpty()
						  where cc.CompanyID == PosCompanyId && recipeItemIds.Contains(cc.ParentItemId)
						  select (object)new { parentId = cc.ParentItemId, componentId = cc.ComponentItemId, qty = cc.Quantity, scrap = cc.ScrapPct, name = it != null ? it.Name : "" }).ToListAsync();
			var setting = await _pos.GetPosSettingAsync(c.BranchId);
			// POS-9b: tax rates so the LOCAL order engine computes totals offline (item DefaultTaxCode → else company default VAT)
			decimal defaultVatRate = await _db.TaxCodes.AsNoTracking().Where(t => t.CompanyID == PosCompanyId && t.Kind == "VAT" && t.IsDefault && t.IsActive).Select(t => (decimal?)t.Rate).FirstOrDefaultAsync() ?? 0m;
			var taxByItem = await (from it in _db.Items.AsNoTracking()
								   where ids.Contains(it.ID)
								   join tc in _db.TaxCodes.AsNoTracking() on it.DefaultTaxCodeId equals (int?)tc.ID into gt
								   from tc in gt.DefaultIfEmpty()
								   select new { it.ID, rate = tc != null ? tc.Rate : defaultVatRate }).ToDictionaryAsync(x => x.ID, x => x.rate);
			var capabilities = await _db.BranchCapabilities.AsNoTracking().Where(x => x.BranchId == c.BranchId).Select(x => new { x.CapabilityKey, x.Enabled }).ToListAsync();
			var payMethods = await _pos.GetPaymentMethodsAsync(c.BranchId);
			var halls = await BuildHallsAsync(c.BranchId);
			var idn = await _brand.ResolveIdentityAsync(c.BranchId);
			var taxNo = await (from b in _db.Branches join co in _db.Companies on b.CompanyID equals co.CompanyID where b.ID == c.BranchId select co.TaxNumber).FirstOrDefaultAsync();
			var term = await _db.PosTerminals.AsNoTracking().FirstOrDefaultAsync(t => t.ID == c.TerminalId);
			return Json(new
			{
				ok = true,
				fetchedAtMs = DateTimeOffset.Now.ToUnixTimeMilliseconds(),
				terminalId = c.TerminalId, branchId = c.BranchId, shiftId = c.ShiftId,
				menu, images, modItemIds, modifiers, sourcing, recipes, setting, capabilities, payMethods, halls, defaultVatRate, taxByItem,
				receipt = new { name = idn.TradeName ?? idn.Name ?? c.BranchName, logo = idn.LogoPath, address = idn.Address, phone = idn.Phone, footer = idn.ReceiptFooterAr ?? idn.ReceiptFooterEn, taxNo },
				terminal = term == null ? null : new { term.Code, term.ReceiptPrefix, term.NextReceiptNo, term.ReceiptPaperWidthMm, term.ReceiptCopies }
			});
		}

		// POS-9d: replay ONE settled offline order (from the device queue) → server posts it via the existing pay services.
		// Idempotent (PosSyncLog on localGuid). JSON body; session-gated (no antiforgery — internal replay of the user's own sale).
		[HttpPost("sync/paid-order")]
		public async Task<IActionResult> SyncPaidOrder([FromBody] CrossBuy.BL.PosSyncOrderInput payload)
		{
			var c = Ctx(); if (c == null) return Json(new { ok = false, error = L["Session expired"].Value });
			if (!_access.CanSell(c.Roles)) return Json(new { ok = false, error = L["This role is not allowed to take payment/collection"].Value });
			if (payload == null) return Json(new { ok = false, error = L["Invalid data"].Value });
			var (ok, err, invoiceId, already) = await _orders.SyncPaidOrderAsync(PosCompanyId, payload, c.EmployeeId);
			return Json(new { ok, error = err, invoiceId, alreadySynced = already, localGuid = payload?.LocalGuid });
		}

		public class ShiftCloseSyncInput { public int TerminalId { get; set; } public int ShiftId { get; set; } public decimal ClosingFloat { get; set; } }

		// POS-9e: replay an OFFLINE shift close (queued on the device) → server posts the variance JE via CloseShiftAsync.
		// Idempotent (already-closed → alreadyClosed). No accounting on the device.
		[HttpPost("sync/shift-close")]
		public async Task<IActionResult> SyncShiftClose([FromBody] ShiftCloseSyncInput payload)
		{
			var c = Ctx(); if (c == null) return Json(new { ok = false, error = L["Session expired"].Value });
			if (!_access.CanSell(c.Roles)) return Json(new { ok = false, error = L["This role is not allowed to operate orders"].Value });
			if (payload == null) return Json(new { ok = false, error = L["Invalid data"].Value });
			var (ok, err, alreadyClosed) = await _pos.SyncShiftCloseAsync(PosCompanyId, payload.TerminalId, payload.ShiftId, payload.ClosingFloat, c.EmployeeId, DateTime.Today, null);
			return Json(new { ok, error = err, alreadyClosed });
		}

		// RC-6c-2: read a paid order's lines for the partial-return picker (any status) — manager only, read-only.
		[HttpGet("order/detail")]
		public async Task<IActionResult> OrderDetail(int orderId)
		{
			var c = Ctx(); if (c == null) return Json(new { ok = false, error = L["Session expired"].Value });
			if (!_access.IsManager(c.Roles)) return Json(new { ok = false, error = L["Manager permission required"].Value });
			var order = await _orders.GetOrderAsync(PosCompanyId, orderId);
			return Json(new { ok = order != null, order });
		}

		// RC-6c-2: recent paid/voided orders for the current shift — manager picks one to void or partially return.
		[HttpGet("orders/recent")]
		public async Task<IActionResult> RecentOrders()
		{
			var c = Ctx(); if (c?.TerminalId == null) return Json(new { ok = false, error = L["Session expired"].Value });
			if (!_access.IsManager(c.Roles)) return Json(new { ok = false, error = L["Manager permission required"].Value });
			var rows = await (from o in _db.PosOrders.AsNoTracking()
							  where o.CompanyId == PosCompanyId && o.TerminalId == c.TerminalId && (o.Status == "Paid" || o.Status == "Voided")
							  orderby o.ClosedAt descending
							  select new { o.ID, o.ReceiptNo, o.GrandTotal, o.Status, o.ClosedAt, o.CustomerId }).Take(30).ToListAsync();
			var custIds = rows.Where(r => r.CustomerId != null).Select(r => r.CustomerId!.Value).Distinct().ToList();
			var names = await _db.Customers.AsNoTracking().Where(x => custIds.Contains(x.ID)).ToDictionaryAsync(x => x.ID, x => x.Name);
			var list = rows.Select(r => new { id = r.ID, receiptNo = r.ReceiptNo, grandTotal = r.GrandTotal, status = r.Status, closedAt = r.ClosedAt, customerName = r.CustomerId != null && names.ContainsKey(r.CustomerId.Value) ? names[r.CustomerId.Value] : "" });
			return Json(new { ok = true, orders = list });
		}

		// RC-6b: Z report for the current (or a given) shift — READ-ONLY, writes nothing.
		[HttpGet("shift/z")]
		public async Task<IActionResult> ShiftZ(int? shiftId = null)
		{
			var c = Ctx(); if (c?.TerminalId == null) return Json(new { ok = false, error = L["Session expired"].Value });
			if (!_access.CanSell(c.Roles)) return Json(new { ok = false, error = L["This role is not allowed to operate orders"].Value });
			int sid = shiftId ?? c.ShiftId ?? 0;
			if (sid == 0) return Json(new { ok = false, error = L["No open shift"].Value });
			var z = await _pos.GetShiftZReportAsync(PosCompanyId, c.TerminalId.Value, sid);
			if (z == null) return Json(new { ok = false, error = L["Shift not found"].Value });
			return Json(new { ok = true, report = z });
		}

		// RC-6b: cashier closes their own shift with a counted drawer → RC-6a CloseShiftAsync (posts the variance JE),
		// then returns the final Z report. After this the shift is closed → the client redirects to /pos/start.
		[HttpPost("shift/close")][ValidateAntiForgeryToken]
		public async Task<IActionResult> ShiftClose(decimal closingFloat)
		{
			var c = Ctx(); if (c?.TerminalId == null || c.ShiftId == null) return Json(new { ok = false, error = L["Session expired"].Value });
			if (!_access.CanSell(c.Roles)) return Json(new { ok = false, error = L["This role is not allowed to operate orders"].Value });
			var (ok, err) = await _pos.CloseShiftAsync(PosCompanyId, c.TerminalId.Value, c.ShiftId.Value, closingFloat, c.EmployeeId, DateTime.Today, null);
			if (!ok) return Json(new { ok = false, error = err });
			var z = await _pos.GetShiftZReportAsync(PosCompanyId, c.TerminalId.Value, c.ShiftId.Value);
			return Json(new { ok = true, report = z });
		}

		[HttpPost("order/setqty")][ValidateAntiForgeryToken]
		public async Task<IActionResult> SetQty(int orderId, int lineId, decimal qty)
		{
			var c = Ctx(); if (c == null) return Json(new { ok = false, error = L["Session expired"].Value }); if (!_access.CanOrder(c.Roles)) return Json(new { ok = false, error = L["This role is not allowed to operate orders"].Value });
			var (ok, err) = await _orders.SetLineQtyAsync(PosCompanyId, orderId, lineId, qty);
			return Json(new { ok, error = err, order = await _orders.GetOrderAsync(PosCompanyId, orderId) });
		}

		[HttpPost("order/remove")][ValidateAntiForgeryToken]
		public async Task<IActionResult> RemoveLine(int orderId, int lineId)
		{
			var c = Ctx(); if (c == null) return Json(new { ok = false, error = L["Session expired"].Value }); if (!_access.CanOrder(c.Roles)) return Json(new { ok = false, error = L["This role is not allowed to operate orders"].Value });
			var (ok, err) = await _orders.RemoveLineAsync(PosCompanyId, orderId, lineId);
			return Json(new { ok, error = err, order = await _orders.GetOrderAsync(PosCompanyId, orderId) });
		}

		// POS-4b: send the order's new items to the kitchen (operational marker only — no invoice/stock/GL).
		[HttpPost("order/send-kitchen")][ValidateAntiForgeryToken]
		public async Task<IActionResult> SendKitchen(int orderId)
		{
			var c = Ctx(); if (c?.TerminalId == null || c.ShiftId == null) return Json(new { ok = false, error = L["Session expired"].Value }); if (!_access.CanOrder(c.Roles)) return Json(new { ok = false, error = L["This role is not allowed to operate orders"].Value });
			var (ok, err, sent) = await _orders.SendToKitchenAsync(PosCompanyId, orderId);
			if (ok) await PosBroadcast(c.BranchId, "OrderSentToKitchen", new { orderId });   // RC-3c: KDS shows the ticket instantly
			return Json(new { ok, error = err, sentLines = sent, order = await _orders.GetOrderAsync(PosCompanyId, orderId) });
		}

		// RC-3b: Kitchen Display Screen (KDS) — separate environment, pos-kitchen/manager only.
		[HttpGet("kitchen")]
		public async Task<IActionResult> Kitchen()
		{
			var c = Ctx(); if (c == null) return RedirectToAction(nameof(Login));
			if (!_access.IsKitchen(c.Roles)) { TempData["PosErr"] = L["The kitchen screen is for kitchen/manager roles only"].Value; return RedirectToAction(nameof(Start)); }
			// KDS now renders under _LayoutAccounting; that shell reads Session["Employee"] for the header user.
			// A POS user signs in via Identity (not the admin Session), so seed it from the POS context.
			if (string.IsNullOrEmpty(HttpContext.Session.GetString("Employee")))
			{
				// STAGE 1 BATCH A — this blob used to carry FullName/Email/ProfileImage and NOTHING else: no
				// employee id, no company, no branch. BusinessContextAccessor read it, resolved nothing, and
				// silently fell back to company 1 — so a kitchen screen on a branch belonging to company 71
				// operated as company 1 for every event, notification and timeline read. The fallback is gone,
				// which would now make this path throw, so the blob states the identity it actually has.
				//
				// BranchCompanyId (the branch's real company) is used, NOT PosLoginContext.CompanyId (the POS
				// catalog company, deliberately 1 — see PosCompanyPolicy). Tenancy and catalog are different
				// questions and this is the tenancy one.
				HttpContext.Session.SetString("Employee", JsonSerializer.Serialize(
					new CrossBuy.ViewModel.EmployeeViewModel
					{
						ID = c.EmployeeId,
						FullName = c.EmployeeName,
						FullNameEn = c.EmployeeNameEn,
						Email = "",
						ProfileImage = c.EmployeePhoto ?? "",
						UserId = User?.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value ?? "",
						EmpCompanyID = c.BranchCompanyId,
						BranchID = c.BranchId,
					}));
			}
			ViewBag.Ctx = c;
			ViewBag.SidebarMenu = CrossBuy.Models.Menu.MainMenu.Restaurant();   // POS/restaurant sidebar — NOT the accounting menu
			// RC-3e: branch stations for the KDS station tabs (client filters lines by StationId)
			ViewBag.Stations = await _db.KitchenStations.AsNoTracking().Where(s => s.BranchId == c.BranchId && s.IsActive).OrderBy(s => s.ID).ToListAsync();
			ViewBag.IsManager = _access.IsManager(c.Roles);
			return View();
		}

		// RC-3b: KDS poll feed — open orders with sent lines for this branch.
		[HttpGet("kds/tickets")]
		public async Task<IActionResult> KdsTickets()
		{
			var c = Ctx(); if (c == null) return Json(new { ok = false, error = L["Session expired"].Value });
			if (!_access.IsKitchen(c.Roles)) return Json(new { ok = false, error = L["Not authorized"].Value });
			return Json(new { ok = true, tickets = await _orders.GetKitchenTicketsAsync(PosCompanyId, c.BranchId) });
		}

		// RC-3a: kitchen advances a SENT line's prep state (New→Preparing→Ready). Gated to pos-kitchen/manager (kitchen never edits items/prices). Operational, no GL.
		[HttpPost("kds/line-status")][ValidateAntiForgeryToken]
		public async Task<IActionResult> KdsLineStatus(int orderId, int lineId, string status)
		{
			var c = Ctx(); if (c == null) return Json(new { ok = false, error = L["Session expired"].Value });
			if (!_access.IsKitchen(c.Roles)) return Json(new { ok = false, error = L["This action is for kitchen/manager roles only"].Value });
			var (ok, err) = await _orders.SetLineKdsStatusAsync(PosCompanyId, orderId, lineId, status);
			var ord = ok ? await _orders.GetOrderAsync(PosCompanyId, orderId) : null;
			if (ok)
			{
				await PosBroadcast(c.BranchId, "LineKdsStatusChanged", new { orderId, lineId, status });   // KDS/terminal sync
				if (ord?.KdsStatus == "Ready") await PosBroadcast(c.BranchId, "OrderReady", new { orderId });  // whole order ready → waiter/cashier alert
			}
			return Json(new { ok, error = err, order = ord });
		}

		// ==================== CUSTOMERS (over the existing Customer entity) ====================
		// Link the order to a chosen customer (instead of the default Walk-in).
		[HttpPost("order/customer")][ValidateAntiForgeryToken]
		public async Task<IActionResult> SetOrderCustomer(int orderId, int customerId)
		{
			var c = Ctx(); if (c == null) return Json(new { ok = false, error = L["Session expired"].Value }); if (!_access.CanOrder(c.Roles)) return Json(new { ok = false, error = L["This role is not allowed to operate orders"].Value });
			var (ok, err) = await _orders.SetOrderCustomerAsync(PosCompanyId, orderId, customerId);
			return Json(new { ok, error = err, order = ok ? await _orders.GetOrderAsync(PosCompanyId, orderId) : null });
		}

		// Set the guest headcount on the current bill (party) — drives the red-chair count on the floor.
		[HttpPost("order/guests")][ValidateAntiForgeryToken]
		public async Task<IActionResult> SetGuests(int orderId, int guests)
		{
			var c = Ctx(); if (c == null) return Json(new { ok = false, error = L["Session expired"].Value }); if (!_access.CanOrder(c.Roles)) return Json(new { ok = false, error = L["This role is not allowed to operate orders"].Value });
			var (ok, err) = await _orders.SetGuestsAsync(PosCompanyId, orderId, guests);
			return Json(new { ok, error = err, order = ok ? await _orders.GetOrderAsync(PosCompanyId, orderId) : null });
		}

		// POS-C1 Delivery — set/freeze delivery info on a Delivery order (operational, no GL; the fee enters the invoice at pay).
		[HttpPost("order/delivery")][ValidateAntiForgeryToken]
		public async Task<IActionResult> SetDelivery(int orderId, int? customerId, int? zoneId, string? address, string? area, string? phone)
		{
			var c = Ctx(); if (c == null) return Json(new { ok = false, error = L["Session expired"].Value }); if (!_access.CanOrder(c.Roles)) return Json(new { ok = false, error = L["This role is not allowed to operate orders"].Value });
			var (ok, err) = await _orders.SetOrderDeliveryAsync(PosCompanyId, orderId, customerId, zoneId, address, area, phone);
			return Json(new { ok, error = err, order = ok ? await _orders.GetOrderAsync(PosCompanyId, orderId) : null });
		}

		[HttpGet("delivery/zones")]
		public async Task<IActionResult> DeliveryZones()
		{
			var c = Ctx(); if (c == null) return Json(new { ok = false, error = L["Session expired"].Value });
			return Json(new { ok = true, zones = await _orders.GetDeliveryZonesAsync(c.BranchId) });
		}

		[HttpGet("customer/addresses")]
		public async Task<IActionResult> CustomerAddresses(int customerId)
		{
			var c = Ctx(); if (c == null) return Json(new { ok = false, error = L["Session expired"].Value });
			return Json(new { ok = true, addresses = await _orders.GetCustomerAddressesAsync(PosCompanyId, customerId) });
		}

		[HttpPost("customer/address")][ValidateAntiForgeryToken]
		public async Task<IActionResult> AddCustomerAddress(int customerId, int? zoneId, string area, string address, string phone, bool isDefault = false)
		{
			var c = Ctx(); if (c == null) return Json(new { ok = false, error = L["Session expired"].Value }); if (!_access.CanOrder(c.Roles)) return Json(new { ok = false, error = L["Not authorized"].Value });
			var (ok, err, id) = await _orders.AddCustomerAddressAsync(PosCompanyId, customerId, zoneId, area, address, phone, isDefault);
			return Json(new { ok, error = err, id });
		}

		// POS-C2 — active drivers for the branch (for the assign dropdown)
		[HttpGet("drivers")]
		public async Task<IActionResult> Drivers()
		{
			var c = Ctx(); if (c == null) return Json(new { ok = false, error = L["Session expired"].Value });
			var drivers = (await _pos.GetDriversAsync(c.BranchId)).Where(d => d.IsActive).Select(d => new { id = d.ID, name = CrossBuy.BL.DisplayName.Of(d.Name, d.NameEn), phone = d.Phone });
			return Json(new { ok = true, drivers });
		}

		// POS-C2 — assign/clear a driver on a delivery order (operational, no GL)
		[HttpPost("order/driver")][ValidateAntiForgeryToken]
		public async Task<IActionResult> AssignDriver(int orderId, int? driverId)
		{
			var c = Ctx(); if (c == null) return Json(new { ok = false, error = L["Session expired"].Value }); if (!_access.CanOrder(c.Roles)) return Json(new { ok = false, error = L["This role is not allowed to operate orders"].Value });
			var (ok, err) = await _orders.AssignDriverAsync(PosCompanyId, orderId, driverId);
			return Json(new { ok, error = err, order = ok ? await _orders.GetOrderAsync(PosCompanyId, orderId) : null });
		}

		// POS-C3 — advance delivery status (OutForDelivery → Delivered). Operational (no GL); broadcast on the SAME PosHub.
		[HttpPost("order/delivery-status")][ValidateAntiForgeryToken]
		public async Task<IActionResult> SetDeliveryStatus(int orderId, string status)
		{
			var c = Ctx(); if (c == null) return Json(new { ok = false, error = L["Session expired"].Value }); if (!_access.CanOrder(c.Roles)) return Json(new { ok = false, error = L["This role is not allowed to operate orders"].Value });
			var (ok, err, ds) = await _orders.SetDeliveryStatusAsync(PosCompanyId, orderId, status);
			if (ok) await PosBroadcast(c.BranchId, ds == "Delivered" ? "OrderDelivered" : "OrderOutForDelivery", new { orderId, status = ds });
			return Json(new { ok, error = err, order = ok ? await _orders.GetOrderAsync(PosCompanyId, orderId) : null });
		}

		// POS-C4 — delivery board screen (CanOrder). Restaurant sidebar under the accounting shell, like the KDS.
		[HttpGet("delivery")]
		public async Task<IActionResult> Delivery()
		{
			var c = Ctx(); if (c == null) return RedirectToAction(nameof(Login));
			if (!_access.CanOrder(c.Roles)) { TempData["PosErr"] = L["The delivery board is for cashier/manager roles"].Value; return RedirectToAction(nameof(Start)); }
			if (string.IsNullOrEmpty(HttpContext.Session.GetString("Employee")))
				// Same Stage 1 fix as the KDS screen above — see the comment there. An incomplete blob used to
				// resolve to company 1; it now states the POS user's real employee id, branch and branch company.
				HttpContext.Session.SetString("Employee", JsonSerializer.Serialize(
					new CrossBuy.ViewModel.EmployeeViewModel
					{
						ID = c.EmployeeId,
						FullName = c.EmployeeName,
						FullNameEn = c.EmployeeNameEn,
						Email = "",
						ProfileImage = c.EmployeePhoto ?? "",
						UserId = User?.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value ?? "",
						EmpCompanyID = c.BranchCompanyId,
						BranchID = c.BranchId,
					}));
			ViewBag.Ctx = c;
			ViewBag.SidebarMenu = CrossBuy.Models.Menu.MainMenu.Restaurant();
			ViewBag.IsManager = _access.IsManager(c.Roles);
			ViewBag.Drivers = (await _pos.GetDriversAsync(c.BranchId)).Where(d => d.IsActive).ToList();
			return View();
		}

		// POS-B2 — guest arrived: open a Dine-in order on the reserved table + link + Arrived (operational, no GL)
		[HttpPost("reservation/arrive")][ValidateAntiForgeryToken]
		public async Task<IActionResult> ReservationArrive(int reservationId)
		{
			var c = Ctx(); if (c?.TerminalId == null || c.ShiftId == null) return Json(new { ok = false, error = L["Session expired"].Value }); if (!_access.CanOrder(c.Roles)) return Json(new { ok = false, error = L["This role is not allowed to operate orders"].Value });
			var (ok, err, oid) = await _orders.ArriveReservationAsync(PosCompanyId, reservationId, c.TerminalId, c.ShiftId);
			return Json(new { ok, error = err, orderId = oid, order = ok && oid != null ? await _orders.GetOrderAsync(PosCompanyId, oid.Value) : null });
		}

		// POS-B2 — no-show: drop the reservation (its "Reserved" badge disappears from the floor)
		[HttpPost("reservation/noshow")][ValidateAntiForgeryToken]
		public async Task<IActionResult> ReservationNoShow(int reservationId)
		{
			var c = Ctx(); if (c == null) return Json(new { ok = false, error = L["Session expired"].Value }); if (!_access.CanOrder(c.Roles)) return Json(new { ok = false, error = L["Not authorized"].Value });
			var (ok, err) = await _pos.SetReservationStatusAsync(PosCompanyId, reservationId, "NoShow");
			return Json(new { ok, error = err });
		}

		// POS-C4 — delivery board feed (open Delivery orders + their state)
		[HttpGet("delivery/orders")]
		public async Task<IActionResult> DeliveryOrders()
		{
			var c = Ctx(); if (c == null) return Json(new { ok = false, error = L["Session expired"].Value });
			if (!_access.CanOrder(c.Roles)) return Json(new { ok = false, error = L["Not authorized"].Value });
			return Json(new { ok = true, orders = await _orders.GetDeliveryOrdersAsync(PosCompanyId, c.BranchId) });
		}

		// Quick customer search by name/phone.
		[HttpGet("customers/search")]
		public async Task<IActionResult> SearchCustomers(string? q)
		{
			var c = Ctx(); if (c == null) return Json(new { ok = false });
			var (rows, _) = await _receivables.SearchCustomersAsync(PosCompanyId, q, true, 1, 15);
			return Json(new { ok = true, customers = rows.Select(x => new { id = x.ID, name = CrossBuy.BL.DisplayName.Of(x.Name, x.NameEn), phone = x.Phone }) });
		}

		// Quick-add a real customer (same Customer entity → shows in admin). Name + optional phone.
		[HttpPost("customers/add")][ValidateAntiForgeryToken]
		public async Task<IActionResult> AddCustomer(string name, string? phone)
		{
			var c = Ctx(); if (c == null) return Json(new { ok = false, error = L["Session expired"].Value });
			if (string.IsNullOrWhiteSpace(name)) return Json(new { ok = false, error = L["Enter the customer name"].Value });
			var cust = await _receivables.CreateCustomerAsync(PosCompanyId, name.Trim(), null, null, null);   // sets up AR control account
			if (!string.IsNullOrWhiteSpace(phone)) { cust.Phone = phone.Trim(); await _db.SaveChangesAsync(); }
			return Json(new { ok = true, customer = new { id = cust.ID, name = cust.Name, phone = cust.Phone } });
		}

		// Discard the current open (unpaid) order so «New» starts truly empty.
		[HttpPost("order/discard")][ValidateAntiForgeryToken]
		public async Task<IActionResult> DiscardOrder(int orderId)
		{
			var c = Ctx(); if (c == null) return Json(new { ok = false, error = L["Session expired"].Value }); if (!_access.CanOrder(c.Roles)) return Json(new { ok = false, error = L["This role is not allowed to operate orders"].Value });
			var (ok, err) = await _orders.VoidOrderAsync(PosCompanyId, orderId);
			return Json(new { ok, error = err });
		}

		// POS-4d-1: move an open order to another (free) table.
		[HttpPost("order/move")][ValidateAntiForgeryToken]
		public async Task<IActionResult> MoveOrder(int orderId, int toTableId)
		{
			var c = Ctx(); if (c?.TerminalId == null || c.ShiftId == null) return Json(new { ok = false, error = L["Session expired"].Value }); if (!_access.CanOrder(c.Roles)) return Json(new { ok = false, error = L["This role is not allowed to operate orders"].Value });
			var (ok, err) = await _orders.MoveOrderToTableAsync(PosCompanyId, orderId, toTableId);
			return Json(new { ok, error = err, order = ok ? await _orders.GetOrderAsync(PosCompanyId, orderId) : null });
		}

		// SEAT the current in-progress order onto a table as a party (allows an occupied table → extra party, up to seats).
		[HttpPost("order/seat")][ValidateAntiForgeryToken]
		public async Task<IActionResult> SeatOrder(int orderId, int toTableId)
		{
			var c = Ctx(); if (c?.TerminalId == null || c.ShiftId == null) return Json(new { ok = false, error = L["Session expired"].Value }); if (!_access.CanOrder(c.Roles)) return Json(new { ok = false, error = L["This role is not allowed to operate orders"].Value });
			var (ok, err) = await _orders.SeatOrderOnTableAsync(PosCompanyId, orderId, toTableId);
			return Json(new { ok, error = err, order = ok ? await _orders.GetOrderAsync(PosCompanyId, orderId) : null });
		}

		// Merge tables: bring ALL of the source table's open parties onto the target table AS SEPARATE bills.
		[HttpPost("order/merge")][ValidateAntiForgeryToken]
		public async Task<IActionResult> MergeOrder(int sourceTableId, int targetOrderId = 0, int targetTableId = 0)
		{
			var c = Ctx(); if (c?.TerminalId == null || c.ShiftId == null) return Json(new { ok = false, error = L["Session expired"].Value }); if (!_access.CanOrder(c.Roles)) return Json(new { ok = false, error = L["This role is not allowed to operate orders"].Value });
			// resolve the target TABLE (picker sends targetTableId; fall back to the table of targetOrderId)
			int tgtTable = targetTableId;
			if (tgtTable == 0 && targetOrderId != 0)
			{
				var to = await _orders.GetOrderAsync(PosCompanyId, targetOrderId);
				if (to?.TableId != null) tgtTable = to.TableId.Value;
			}
			if (tgtTable == 0) return Json(new { ok = false, error = L["Target table not selected"].Value });
			var (ok, err) = await _orders.MergeTablesAsync(PosCompanyId, c.BranchId, sourceTableId, tgtTable);
			return Json(new { ok, error = err });
		}

		// POS-4c: hold the current walk-in order (park it), list held ones, and recall one.
		[HttpPost("order/hold")][ValidateAntiForgeryToken]
		public async Task<IActionResult> HoldOrder(int orderId)
		{
			var c = Ctx(); if (c?.TerminalId == null || c.ShiftId == null) return Json(new { ok = false, error = L["Session expired"].Value }); if (!_access.CanOrder(c.Roles)) return Json(new { ok = false, error = L["This role is not allowed to operate orders"].Value });
			var (ok, err) = await _orders.HoldOrderAsync(PosCompanyId, orderId);
			return Json(new { ok, error = err });
		}

		[HttpGet("orders/held")]
		public async Task<IActionResult> HeldOrders()
		{
			var c = Ctx(); if (c == null) return Json(new { ok = false });
			return Json(new { ok = true, held = await _orders.GetHeldOrdersAsync(PosCompanyId, c.BranchId) });
		}

		// ALL open invoices for the branch (held + active + on tables) — so nothing is ever "lost".
		[HttpGet("orders/open")]
		public async Task<IActionResult> OpenInvoices()
		{
			var c = Ctx(); if (c == null) return Json(new { ok = false });
			return Json(new { ok = true, held = await _orders.GetOpenInvoicesAsync(PosCompanyId, c.BranchId) });
		}

		[HttpPost("order/recall")][ValidateAntiForgeryToken]
		public async Task<IActionResult> RecallOrder(int orderId)
		{
			var c = Ctx(); if (c?.TerminalId == null || c.ShiftId == null) return Json(new { ok = false, error = L["Session expired"].Value }); if (!_access.CanOrder(c.Roles)) return Json(new { ok = false, error = L["This role is not allowed to operate orders"].Value });
			var (ok, err) = await _orders.RecallHeldAsync(PosCompanyId, orderId);
			return Json(new { ok, error = err, order = ok ? await _orders.GetOrderAsync(PosCompanyId, orderId) : null });
		}

		// View/continue an open order WITHOUT changing its status — tapping an invoice to SEE it must NOT un-hold it.
		[HttpPost("order/load")][ValidateAntiForgeryToken]
		public async Task<IActionResult> LoadOrder(int orderId)
		{
			var c = Ctx(); if (c?.TerminalId == null || c.ShiftId == null) return Json(new { ok = false, error = L["Session expired"].Value }); if (!_access.CanOrder(c.Roles)) return Json(new { ok = false, error = L["This role is not allowed to operate orders"].Value });
			var ord = await _orders.GetOrderAsync(PosCompanyId, orderId);
			if (ord == null || ord.Status != "Open") return Json(new { ok = false, error = L["The order is not available"].Value });
			return Json(new { ok = true, order = ord });   // read-only — no IsHeld / status mutation
		}

		[HttpPost("order/pay")][ValidateAntiForgeryToken]
		public async Task<IActionResult> Pay(int orderId, string method = "Cash", int parts = 1, decimal tipAmount = 0, string? tipMethod = null)
		{
			var c = Ctx(); if (c?.TerminalId == null || c.ShiftId == null) return Json(new { ok = false, error = L["Session expired"].Value }); if (!_access.CanOrder(c.Roles)) return Json(new { ok = false, error = L["This role is not allowed to operate orders"].Value });
			if (!_access.CanSell(c.Roles)) return Json(new { ok = false, error = L["This role is not allowed to take payment/collection"].Value });   // role ENFORCED
			var (ok, err, invoiceId) = await _orders.PayAsync(PosCompanyId, orderId, method, null, parts, tipAmount, tipMethod);
			string? receiptNo = ok ? await _db.PosOrders.Where(o => o.ID == orderId).Select(o => o.ReceiptNo).FirstOrDefaultAsync() : null;
			if (ok) await PosBroadcast(c.BranchId, "OrderPaid", new { orderId });   // RC-3c: KDS clears the ticket
			return Json(new { ok, error = err, invoiceId, receiptNo });
		}

		// POS-7: MULTI-TENDER pay — `tenders` = JSON array [{method, amount}] (one method or a split across several).
		[HttpPost("order/pay-tenders")][ValidateAntiForgeryToken]
		public async Task<IActionResult> PayTenders(int orderId, string tenders, decimal tipAmount = 0, string? tipMethod = null)
		{
			var c = Ctx(); if (c?.TerminalId == null || c.ShiftId == null) return Json(new { ok = false, error = L["Session expired"].Value }); if (!_access.CanOrder(c.Roles)) return Json(new { ok = false, error = L["This role is not allowed to operate orders"].Value });
			if (!_access.CanSell(c.Roles)) return Json(new { ok = false, error = L["This role is not allowed to take payment/collection"].Value });
			List<CrossBuy.BL.PosTenderInput>? parsed;
			try { parsed = System.Text.Json.JsonSerializer.Deserialize<List<CrossBuy.BL.PosTenderInput>>(tenders ?? "", new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true }); }
			catch { return Json(new { ok = false, error = L["Invalid payment data"].Value }); }
			if (parsed == null || parsed.Count == 0) return Json(new { ok = false, error = L["No payment method"].Value });
			var (ok, err, invoiceId) = await _orders.PayTendersAsync(PosCompanyId, orderId, parsed, null, tipAmount, tipMethod);
			string? receiptNo = ok ? await _db.PosOrders.Where(o => o.ID == orderId).Select(o => o.ReceiptNo).FirstOrDefaultAsync() : null;
			if (ok) await PosBroadcast(c.BranchId, "OrderPaid", new { orderId });
			return Json(new { ok, error = err, invoiceId, receiptNo });
		}

		// POS-4d-3b: split BY ITEM → N invoices. `bills` is a JSON array of bills, each an array of {lineId, qty}.
		[HttpPost("order/pay-split-items")][ValidateAntiForgeryToken]
		public async Task<IActionResult> PaySplitItems(int orderId, string bills, decimal tipAmount = 0, string? tipMethod = null)
		{
			var c = Ctx(); if (c?.TerminalId == null || c.ShiftId == null) return Json(new { ok = false, error = L["Session expired"].Value }); if (!_access.CanOrder(c.Roles)) return Json(new { ok = false, error = L["This role is not allowed to operate orders"].Value });
			if (!_access.CanSell(c.Roles)) return Json(new { ok = false, error = L["This role is not allowed to take payment/collection"].Value });
			List<List<CrossBuy.BL.SplitAllocation>>? parsed;
			try { parsed = System.Text.Json.JsonSerializer.Deserialize<List<List<CrossBuy.BL.SplitAllocation>>>(bills ?? "", new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true }); }
			catch { return Json(new { ok = false, error = L["Invalid split format"].Value }); }
			if (parsed == null || parsed.Count == 0) return Json(new { ok = false, error = L["No split data"].Value });
			var (ok, err, invIds) = await _orders.PaySplitByItemAsync(PosCompanyId, orderId, parsed, "Cash", null, tipAmount, tipMethod);
			string? receiptNo = ok ? await _db.PosOrders.Where(o => o.ID == orderId).Select(o => o.ReceiptNo).FirstOrDefaultAsync() : null;
			if (ok) await PosBroadcast(c.BranchId, "OrderPaid", new { orderId });   // RC-3c: KDS clears the ticket
			return Json(new { ok, error = err, invoiceIds = invIds, receiptNo });
		}

		// ==================== FLOOR BOARD (tables) ====================
		// Build halls + tables + live status. Reused by Terminal() and the refresh endpoint.
		// Reuses the shared floor builder (PosOrderService.GetFloorAsync) — same source as the reservations screen.
		private async Task<object> BuildHallsAsync(int branchId) => await _orders.GetFloorAsync(PosCompanyId, branchId);

		[HttpGet("tables")]
		public async Task<IActionResult> TablesBoard()
		{
			var c = Ctx(); if (c == null) return Json(new { ok = false });
			return Json(new { ok = true, halls = await BuildHallsAsync(c.BranchId) });
		}

		// Manual status change (تحديث الحالة): Available / Reserved / Cleaning / Closed.
		// An Occupied table (has an open order) cannot be overridden here — must be closed via the order.
		[HttpPost("tables/status")][ValidateAntiForgeryToken]
		public async Task<IActionResult> SetTableStatus(int tableId, string status)
		{
			var c = Ctx(); if (c == null) return Json(new { ok = false, error = L["Session expired"].Value }); if (!_access.CanOrder(c.Roles)) return Json(new { ok = false, error = L["This role is not allowed to operate orders"].Value });
			var allowed = new[] { "Available", "Reserved", "Cleaning", "Closed" };
			if (!allowed.Contains(status)) return Json(new { ok = false, error = L["Invalid status"].Value });
			var areaIds = _db.DiningAreas.Where(a => a.BranchId == c.BranchId).Select(a => a.ID);
			var t = await _db.RestaurantTables.FirstOrDefaultAsync(x => x.ID == tableId && areaIds.Contains(x.DiningAreaId));
			if (t == null) return Json(new { ok = false, error = L["Table not found"].Value });
			var hasOpen = await _db.PosOrders.AnyAsync(o => o.BranchId == c.BranchId && o.Status == "Open" && o.TableId == tableId);
			if (hasOpen) return Json(new { ok = false, error = L["The table is busy with an open order"].Value });
			t.Status = status; await _db.SaveChangesAsync();
			return Json(new { ok = true, halls = await BuildHallsAsync(c.BranchId) });
		}

		// Add a new table to a hall (طاولة جديدة) — quick add; full floor-plan editor stays in admin setup.
		[HttpPost("tables/add")][ValidateAntiForgeryToken]
		public async Task<IActionResult> AddTable(int areaId, string code, int seats, string shape = "Square")
		{
			var c = Ctx(); if (c == null) return Json(new { ok = false, error = L["Session expired"].Value });
			// adding/laying out tables is an ADMIN/manager capability (done from the admin floor-plan screen) — not the cashier
			if (!_access.IsManager(c.Roles)) return Json(new { ok = false, error = L["Adding tables is for manager/admin roles only"].Value });
			var area = await _db.DiningAreas.FirstOrDefaultAsync(a => a.ID == areaId && a.BranchId == c.BranchId && a.IsActive);
			if (area == null) return Json(new { ok = false, error = L["Dining area not found"].Value });
			if (string.IsNullOrWhiteSpace(code)) return Json(new { ok = false, error = L["Enter the table number"].Value });
			code = code.Trim();
			if (await _db.RestaurantTables.AnyAsync(t => t.DiningAreaId == areaId && t.Code == code))
				return Json(new { ok = false, error = L["Table number is already in use"].Value });
			// place it below the current tables so it doesn't overlap
			var maxY = await _db.RestaurantTables.Where(t => t.DiningAreaId == areaId).Select(t => (decimal?)(t.Y + t.H)).MaxAsync() ?? 0m;
			_db.RestaurantTables.Add(new CrossBuy.Models.Context.Pos.RestaurantTable
			{
				DiningAreaId = areaId, Code = code, Seats = seats < 1 ? 1 : seats,
				Shape = shape == "Round" ? "Round" : "Square",
				X = 80, Y = maxY + 40, W = shape == "Round" ? 126 : 104, H = shape == "Round" ? 126 : 104,
				Status = "Available", IsActive = true
			});
			await _db.SaveChangesAsync();
			return Json(new { ok = true, halls = await BuildHallsAsync(c.BranchId) });
		}
	}
}
