using System.Text.Json;
using CrossBuy.BL;
using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Admin;
using CrossBuy.Models.Context.Pos;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;

namespace CrossBuy.Controllers
{
	// HM-0: the INDEPENDENT hypermarket LANE at /hyper/pos — mirrors the restaurant cashier app (PosAppController).
	// Its OWN gate: session key "HyperCtx" (never mixes with the admin "Employee" gate, nor with the restaurant "PosCtx").
	// SessionValidationMiddleware bypasses /hyper/pos so THIS controller enforces access. Access reuses the existing
	// BranchUserRole/PosRole mechanism (IPosAccessService) AND additionally requires the resolved branch to be a
	// Hyper-activity branch — so a restaurant cashier can never enter the hyper lane (isolation).
	// HM-0 has NO selling: no cart, no scan, no order line. Only: login → pick terminal + open shift → a minimal
	// lane panel (terminal/shift/capabilities) → close shift.
	[Route("hyper/pos")]
	[CrossBuy.Models.PosLaneActivityGuard("HyperCtx", "hyper", "/hyper/pos/login")]
	public class HyperPosController : Controller
	{
		// THE COMPANY THIS TILL SELLS FOR, FROM CONFIGURATION. It was `const int PosCompanyId = 1;` - a
		// deployment fact compiled into five separate files, where it could not be changed without a
		// rebuild and the five copies could drift apart in silence. Same treatment the storefront
		// company already had: PosCompanyOptions, bound to the "Pos" section, read here and nowhere else.
		private readonly int PosCompanyId;
		private readonly SignInManager<Users> _signIn;
		private readonly UserManager<Users> _users;
		private readonly IPosAccessService _access;
		private readonly IPosSetupService _pos;
		private readonly IPosOrderService _posOrders;
		private readonly CrossBuy.BL.ICurrencyRounding _rounding;
		private readonly CrossBuy.BL.IPricingService _pricing;   // HM-4: price-check reads prices (never AddLine)
		private readonly CrossBuy.BL.IReceivableService _ar;      // HM-9: REUSE the existing customer search/create (no new service)
		private readonly CrossDbContext _db;
		private readonly IStringLocalizer<CrossBuy.SharedResources> L;
		public HyperPosController(SignInManager<Users> signIn, UserManager<Users> users, IPosAccessService access,
			IPosSetupService pos, IPosOrderService posOrders, CrossBuy.BL.ICurrencyRounding rounding, CrossBuy.BL.IPricingService pricing, CrossBuy.BL.IReceivableService receivables, CrossDbContext db, IStringLocalizer<CrossBuy.SharedResources> localizer, Microsoft.Extensions.Options.IOptions<CrossBuy.Models.Platform.PosCompanyOptions> posCompany, CrossBuy.BL.IPosPromotionEngine promos, CrossBuy.BL.IItemService items, CrossBuy.BL.IPosCouponService coupons)
		{ _signIn = signIn; _users = users; _access = access; _pos = pos; _posOrders = posOrders; _rounding = rounding; _pricing = pricing; _ar = receivables; _db = db; L = localizer; PosCompanyId = posCompany.Value.CatalogCompanyId; PointValue = posCompany.Value.LoyaltyPointValue; _promos = promos; _items = items; _coupons = coupons; }

		private readonly CrossBuy.BL.IPosCouponService _coupons;   // HM-26

		// HM-22: what one point is worth at this till, from configuration. Zero = redemption is off, which
		// is the shipped default: the earn rate cannot supply this number without refunding the whole sale.
		private readonly decimal PointValue;

		private readonly CrossBuy.BL.IPosPromotionEngine _promos;   // HM-12
		private readonly CrossBuy.BL.IItemService _items;            // HM-16: the SHARED item search, not a second copy

		// ---- session context (own key, HyperCtx) ----
		public class HyperCtx
		{
			public int EmployeeId { get; set; }
			public string EmployeeName { get; set; } = "";
			public string? EmployeeNameEn { get; set; }
			public string? EmployeePhoto { get; set; }
			public int BranchId { get; set; }
			public string BranchName { get; set; } = "";
			public List<string> Roles { get; set; } = new();
			public int? TerminalId { get; set; }
			public string? TerminalCode { get; set; }
			public int? ShiftId { get; set; }
			public int? OrderId { get; set; }   // HM-1: the open cart for this lane session
		}
		private HyperCtx? Ctx()
		{
			var s = HttpContext.Session.GetString("HyperCtx");
			return string.IsNullOrEmpty(s) ? null : JsonSerializer.Deserialize<HyperCtx>(s);
		}
		private void SetCtx(HyperCtx c) => HttpContext.Session.SetString("HyperCtx", JsonSerializer.Serialize(c));

		private IActionResult HomeFor(HyperCtx c)
			=> (c.TerminalId != null && c.ShiftId != null) ? RedirectToAction(nameof(Lane)) : RedirectToAction(nameof(Start));

		// GET /hyper/pos — bare lane entry. With a session → home; without → the Lane action bounces to login.
		[HttpGet("")]
		public IActionResult Index()
		{
			var c = Ctx();
			return c != null ? HomeFor(c) : RedirectToAction(nameof(Lane));
		}

		// ==================== LOGIN (independent) ====================
		[HttpGet("login")][AllowAnonymous]
		public IActionResult Login()
		{
			var c = Ctx();
			if (c != null) return HomeFor(c);
			return View("~/Views/Hyper/PosLogin.cshtml");
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
			// ISOLATION (صفر-1/صفر-2): WHITELIST via the single shared helper — the hyper lane accepts ONLY 'Hyper'
			// branches. A restaurant/no-activity/other branch is rejected here with a clear message.
			var preset = await _db.Branches.AsNoTracking().Where(b => b.ID == acc.BranchId).Select(b => b.ActivityPresetCode).FirstOrDefaultAsync();
			var (laneOk, _) = _access.IsActivityAllowedForLane(preset, "hyper");
			if (!laneOk) { await _signIn.SignOutAsync(); TempData["PosErr"] = L["This branch does not belong to the hypermarket system"].Value; return RedirectToAction(nameof(Login)); }
			// HM-1 (HM-D33) COMPANY GUARD at the gateway: the employee must belong to the branch's company.
			// A cross-company login (employee of company A entering a branch of company B) is refused here with a clear message.
			var gEmpCo = await _db.Employee.AsNoTracking().Where(e => e.ID == acc.EmployeeId).Select(e => (int?)e.EmpCompanyID).FirstOrDefaultAsync();
			var gBrCo = await _db.Branches.AsNoTracking().Where(b => b.ID == acc.BranchId).Select(b => (int?)b.CompanyID).FirstOrDefaultAsync();
			if (gEmpCo == null || gBrCo == null || gEmpCo.Value != gBrCo.Value) { await _signIn.SignOutAsync(); TempData["PosErr"] = L["Your account belongs to another company than this branch — cross-company operations are blocked."].Value; return RedirectToAction(nameof(Login)); }
			var ctx = new HyperCtx { EmployeeId = acc.EmployeeId, EmployeeName = acc.EmployeeName, EmployeeNameEn = acc.EmployeeNameEn, EmployeePhoto = acc.EmployeePhoto, BranchId = acc.BranchId, BranchName = acc.BranchName, Roles = acc.Roles };
			SetCtx(ctx);
			return HomeFor(ctx);
		}

		[HttpGet("logout")]
		public async Task<IActionResult> Logout()
		{
			await _signIn.SignOutAsync();
			HttpContext.Session.Remove("HyperCtx");
			return RedirectToAction(nameof(Login));
		}

		// ==================== START (pick terminal + shift) — clone of the restaurant Start ====================
		[HttpGet("start")]
		public async Task<IActionResult> Start()
		{
			var c = Ctx(); if (c == null) return RedirectToAction(nameof(Login));
			var terminals = await _pos.GetTerminalsAsync(c.BranchId);
			var open = new Dictionary<int, PosShift?>();
			foreach (var t in terminals) open[t.ID] = await _pos.GetOpenShiftAsync(t.ID);
			ViewBag.Ctx = c; ViewBag.Terminals = terminals; ViewBag.OpenShifts = open;
			return View("~/Views/Hyper/PosStart.cshtml");
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
			return RedirectToAction(nameof(Lane));
		}

		// ==================== LANE (minimal HM-0 panel — NO selling) ====================
		[HttpGet("lane")]
		// HM-16: PLU / ITEM LOOKUP — the way to sell when the scanner cannot.
		//
		// A till that can only scan stops selling the moment a barcode is torn, a scanner dies, or an item
		// never had one — loose produce being the obvious case. The cashier's fallback today is to call
		// someone; this is the fallback.
		//
		// IT CONSUMES THE SHARED SEARCH. ItemService.SearchItemsAsync is the one the back office already
		// uses, so an item found here is the same item, matched the same way. PosController has its own
		// picker endpoint, but that is the RESTAURANT lane behind its own lane guard, and reaching across
		// would break the separation the two lanes are built on — so this shares the SERVICE, not the
		// endpoint. Copying its query instead would have been a second definition of "which item is that".
		public async Task<IActionResult> Lane(string? q = null)
		{
			var c = Ctx(); if (c == null) return RedirectToAction(nameof(Login));
			if (c.TerminalId == null) return RedirectToAction(nameof(Start));
			var term = await _db.PosTerminals.AsNoTracking().FirstOrDefaultAsync(t => t.ID == c.TerminalId);
			var openShift = await _pos.GetOpenShiftAsync(c.TerminalId.Value);   // null once closed
			// capabilities via the ONE central gate (never a direct EF query here)
			var caps = new List<HyperController.HyperCapRow>();
			foreach (var cap in HyperController.Capabilities)
				caps.Add(new HyperController.HyperCapRow { Key = cap.key, Ar = cap.ar, En = cap.en, On = await _pos.IsCapabilityEnabledAsync(c.BranchId, cap.key) });
			// Z report (read-only) for the current/last shift
			ZReportDto? z = null;
			int sid = openShift?.ID ?? c.ShiftId ?? 0;
			if (sid != 0) z = await _pos.GetShiftZReportAsync(PosCompanyId, c.TerminalId.Value, sid);
			ViewBag.HasCapConfig = await _pos.HasCapabilityConfigAsync(c.BranchId);
			// HM-1: the open cart for this lane session (server holds/computes it; the view only displays). Clear a stale ref.
			PosOrderDto? order = null;
			if (c.OrderId != null)
			{
				order = await _posOrders.GetOrderAsync(PosCompanyId, c.OrderId.Value);
				if (order == null || order.Status != "Open") { c.OrderId = null; SetCtx(c); order = null; }
			}
			// document-currency decimals from the SINGLE rounding source (KWD ⇒ 3) — the total is shown at this precision, not toFixed(2).
			int ccyId = await _db.BranchPosSettings.AsNoTracking().Where(s => s.BranchId == c.BranchId).Select(s => s.DefaultCurrencyId ?? 0).FirstOrDefaultAsync();
			ViewBag.DocDp = await _rounding.DecimalsAsync(PosCompanyId, ccyId == 0 ? (int?)null : ccyId, c.BranchId);
			// HM-12 WITHDRAWN — DO NOT RE-ENABLE WITHOUT READING THIS.
			//
			// This called a basket-level promotion engine I added. It was a DUPLICATE: promotions already
			// reach this lane through pricing. PosOrderService.AddLineAsync reads the branch's Promotions
			// capability and passes it to IPricingService.GetPriceAsync(..., applyPromotions), which applies
			// the Inventory `Promotion` rules into the unit price. Running both gave the SAME basket two
			// discounts - one in the unit price and one on the line - and the second was invisible on the
			// price it was taken from.
			//
			// How the duplicate got written: I searched for IsCapabilityEnabledAsync("Promotions") to find
			// the capability's consumer, found none, and concluded there was none. The real consumer reads
			// BranchCapabilities with a direct EF query instead of the central gate, so the search missed it.
			// The search answered a question about my pattern, not about the code.
			ViewBag.Promos = new List<CrossBuy.BL.AppliedPromotion>();

			// HM-22: what this customer can spend, and what a point is worth. Both are needed for the lane to
			// offer redemption at all; either one missing and the field is not drawn rather than drawn dead.
			ViewBag.Coupon = order != null ? await _coupons.CurrentAsync(PosCompanyId, order.Id) : null;
			ViewBag.PointValue = PointValue;
			// The «Points» tender must have a GL account on THIS branch or PayTendersAsync refuses the whole
			// settlement. Checking it here rather than at pay is the difference between a field that is absent
			// and a sale that dies after the cashier has already typed the points, with a queue waiting.
			ViewBag.PointsTenderOk = await _db.BranchPaymentMethods.AsNoTracking()
				.AnyAsync(m => m.BranchId == c.BranchId && m.IsActive && m.PaymentMethod == "Points" && m.TargetAccountId != null);
			ViewBag.LoyaltyOn = await _pos.IsCapabilityEnabledAsync(c.BranchId, "Loyalty");
			ViewBag.PointsBalance = 0L;
			if (order?.CustomerId is > 0 && !await CrossBuy.BL.LoyaltyPointsHelper.IsWalkInAsync(_db, PosCompanyId, order.CustomerId!.Value))
				ViewBag.PointsBalance = await CrossBuy.BL.LoyaltyPointsHelper.GetBalanceAsync(_db, PosCompanyId, order.CustomerId.Value);

			// HM-11: the branch's PARKED sales, so this till can recall one. Read through the same capability gate
			// the buttons use — capability OFF means an empty list, never a hidden-but-present recall path.
			ViewBag.Held = await _pos.IsCapabilityEnabledAsync(c.BranchId, "SuspendResume")
				? await _posOrders.GetHeldOrdersAsync(PosCompanyId, c.BranchId)
				: new List<CrossBuy.BL.HeldOrderDto>();
			// The lookup is a READ: it never adds anything by itself, so a mistyped search cannot sell.
			ViewBag.LookupQ = q;
			if (!string.IsNullOrWhiteSpace(q))
			{
				var (hits, _) = await _items.SearchItemsAsync(PosCompanyId, q!.Trim(), null, "Stockable", true, 1, 12);
				ViewBag.Lookup = hits;
			}
			// HM-AGE: the age bar lives on the ITEM, so the lane asks the catalogue rather than the
			// cashier's memory - a new cashier on a night shift cannot be expected to know which SKUs
			// carry one. The HIGHEST bar in the basket is what the gate must clear: a basket holding both
			// an 18 and a 21 item is a 21 sale, and taking the lower would clear the wrong one.
			ViewBag.AgeMin = order == null ? (int?)null : await _db.Items.AsNoTracking()
				.Where(i => i.MinAgeYears != null && _db.PosOrderLines.Any(l => l.OrderId == order.Id && l.ItemId == i.ID))
				.MaxAsync(i => (int?)i.MinAgeYears);
			// HM-EXP: the nearest usable date per item in the basket, so the row can print it. One probe per
			// DISTINCT item, and only while the capability is on - an ordinary basket of groceries must not
			// pay for a feature the branch has not asked for.
			var lineExpiry = new Dictionary<int, DateTime>();
			if (order != null && await _pos.IsCapabilityEnabledAsync(c.BranchId, "ExpiryControl"))
			{
				int? expWh = await _db.BranchPosSettings.AsNoTracking()
					.Where(x => x.BranchId == c.BranchId).Select(x => x.DefaultSalesWarehouseId).FirstOrDefaultAsync();
				if (expWh != null)
				{
					var dated = await _db.PosOrderLines.AsNoTracking()
						.Where(l => l.OrderId == order.Id)
						.Select(l => l.ItemId).Distinct()
						.Where(id => _db.Items.Any(i => i.ID == id && i.TrackExpiry))
						.ToListAsync();
					foreach (var id in dated)
					{
						var (_, _, near) = await _pos.ExpirySellCheckAsync(PosCompanyId, id, expWh.Value, 0m, DateTime.Today);
						if (near != null) lineExpiry[id] = near.Value;
					}
				}
			}
			ViewBag.LineExpiry = lineExpiry;
			ViewBag.Order = order;
			ViewBag.Ctx = c; ViewBag.Terminal = term; ViewBag.OpenShift = openShift; ViewBag.Caps = caps; ViewBag.Z = z;
			return View("~/Views/Hyper/PosLane.cshtml");
		}

		[HttpPost("shift/close")][ValidateAntiForgeryToken]
		public async Task<IActionResult> ShiftClose(decimal closingFloat)
		{
			var c = Ctx(); if (c?.TerminalId == null || c.ShiftId == null) return RedirectToAction(nameof(Login));
			if (!_access.CanSell(c.Roles)) { TempData["PosErr"] = L["This role is not allowed to operate orders"].Value; return RedirectToAction(nameof(Lane)); }
			var (ok, err) = await _pos.CloseShiftAsync(PosCompanyId, c.TerminalId.Value, c.ShiftId.Value, closingFloat, c.EmployeeId, DateTime.Today, null);
			if (!ok) { TempData["PosErr"] = err; return RedirectToAction(nameof(Lane)); }
			TempData["PosMsg"] = L["Shift closed"].Value;
			return RedirectToAction(nameof(Lane));
		}

		// ==================== HM-1 SELLING (pure delegation to PosOrderService; the controller computes NOTHING) ====================
		// Ensure an open cart exists for this lane session; returns its id. CreateOrderAsync carries the company guard + KWD currency.
		private async Task<(bool ok, string? err, int orderId)> EnsureOrderAsync(HyperCtx c)
		{
			if (c.OrderId != null)
			{
				var existing = await _posOrders.GetOrderAsync(PosCompanyId, c.OrderId.Value);
				if (existing != null && existing.Status == "Open") return (true, null, c.OrderId.Value);
			}
			var (ok, err, oid) = await _posOrders.CreateOrderAsync(PosCompanyId, c.BranchId, "Takeaway", null, null, c.TerminalId, c.ShiftId);
			if (!ok) return (false, err, 0);
			c.OrderId = oid; SetCtx(c);
			return (true, null, oid);
		}

		[HttpPost("scan")][ValidateAntiForgeryToken]
		public async Task<IActionResult> Scan(string barcode)
		{
			var c = Ctx(); if (c?.TerminalId == null || c.ShiftId == null) return RedirectToAction(nameof(Login));
			if (!_access.CanSell(c.Roles)) { TempData["PosErr"] = L["This role is not allowed to operate orders"].Value; return RedirectToAction(nameof(Lane)); }
			barcode = (barcode ?? "").Trim();
			if (barcode.Length == 0) return RedirectToAction(nameof(Lane));

			// HM-3: SCALE-BARCODE routing FIRST (by prefix). A barcode in the branch's scale range is parsed (not looked up);
			// non-scale barcodes fall through to the HM-2 lookup below. No silent fallback — every scale case is an explicit reject.
			var bps = await _db.BranchPosSettings.AsNoTracking().FirstOrDefaultAsync(s => s.BranchId == c.BranchId);
			var scaleCfg = new ScaleBarcodeConfig
			{
				Prefix = bps?.ScaleBarcodePrefix, ItemCodeLength = bps?.ScaleItemCodeLength ?? 0, ValueLength = bps?.ScaleValueLength ?? 0,
				ValueDecimals = bps?.ScaleValueDecimals ?? 0, ValueType = bps?.ScaleValueType ?? "Weight", CheckAlgo = bps?.ScaleCheckAlgo ?? "EanMod10",
			};
			if (ScaleBarcodeParser.MatchesPrefix(barcode, scaleCfg))
			{
				if (!await _pos.IsCapabilityEnabledAsync(c.BranchId, "Weight"))
				{ TempData["PosErr"] = L["Weighted selling is not enabled on this branch."].Value; return RedirectToAction(nameof(Lane)); }
				// a FIXED product barcode that happens to live in the scale range = data error (HM-D42) — reject explicitly.
				bool isFixed = await _db.Items.AsNoTracking().AnyAsync(i => i.CompanyID == PosCompanyId && i.Barcode == barcode)
					|| await _db.ItemBarcodes.AsNoTracking().AnyAsync(z => z.Barcode == barcode && _db.Items.Any(i => i.ID == z.ItemId && i.CompanyID == PosCompanyId));
				if (isFixed) { TempData["PosErr"] = L["This is a fixed product barcode inside the reserved scale range — the data must be corrected."].Value; return RedirectToAction(nameof(Lane)); }
				var pr = ScaleBarcodeParser.Parse(barcode, scaleCfg);
				if (!pr.Ok)
				{
					var m = pr.ErrorCode == "check" ? L["The scale barcode check digit is invalid."]
						  : pr.ErrorCode == "priceType" ? L["Price-embedded scale barcodes are not supported yet."]
						  : L["The scale barcode format is invalid."];
					TempData["PosErr"] = m.Value; return RedirectToAction(nameof(Lane));
				}
				var witem = await _db.Items.AsNoTracking().Where(i => i.CompanyID == PosCompanyId && i.ScaleCode == pr.ItemCode)
					.Select(i => new { i.ID, i.IsWeighted, i.IsActive }).FirstOrDefaultAsync();
				if (witem == null || !witem.IsActive) { TempData["PosErr"] = L["No item is linked to this scale code."].Value; return RedirectToAction(nameof(Lane)); }
				if (!witem.IsWeighted) { TempData["PosErr"] = L["This item is not sold by weight."].Value; return RedirectToAction(nameof(Lane)); }
				int kgUom = await _db.UnitsOfMeasure.AsNoTracking().Where(u => u.CompanyID == PosCompanyId && u.Code == "KG").Select(u => u.ID).FirstOrDefaultAsync();
				var (sok, serr, soid) = await EnsureOrderAsync(c);
				if (!sok) { TempData["PosErr"] = serr; return RedirectToAction(nameof(Lane)); }
				var (wok, werr) = await _posOrders.AddLineAsync(PosCompanyId, soid, witem.ID, pr.WeightKg!.Value, null, kgUom);   // qty = weight in KG
				if (!wok) TempData["PosErr"] = werr;
				return RedirectToAction(nameof(Lane));
			}

			// HM-2: resolve the barcode across Items.Barcode (base unit) AND ItemBarcodes (its own unit). BarcodeMulti gates the
			// secondary barcodes (and therefore multi-unit): OFF ⇒ only the primary (base) barcode resolves.
			bool multiOn = await _pos.IsCapabilityEnabledAsync(c.BranchId, "BarcodeMulti");
			var primary = await _db.Items.AsNoTracking().Where(i => i.CompanyID == PosCompanyId && i.IsActive && i.Barcode == barcode)
				.Select(i => new { i.ID, UoMId = (int?)i.BaseUoMId }).ToListAsync();
			var secondary = multiOn
				? await _db.ItemBarcodes.AsNoTracking().Where(b => b.Barcode == barcode && _db.Items.Any(i => i.ID == b.ItemId && i.CompanyID == PosCompanyId && i.IsActive))
					.Select(b => new { ID = b.ItemId, b.UoMId }).ToListAsync()
				: new();
			var resolved = primary.Concat(secondary).Select(m => new { m.ID, m.UoMId }).Distinct().ToList();
			if (resolved.Count > 1) { TempData["PosErr"] = L["This barcode is registered on more than one item — the data must be corrected."].Value; return RedirectToAction(nameof(Lane)); }
			if (resolved.Count == 0)
			{
				// distinguish "not enabled" from "not found": a secondary barcode exists but BarcodeMulti is OFF on this branch.
				if (!multiOn && await _db.ItemBarcodes.AsNoTracking().AnyAsync(b => b.Barcode == barcode && _db.Items.Any(i => i.ID == b.ItemId && i.CompanyID == PosCompanyId)))
				{ TempData["PosErr"] = L["Multiple barcodes are not enabled on this branch."].Value; return RedirectToAction(nameof(Lane)); }
				TempData["PosErr"] = L["No item matches this barcode."].Value; return RedirectToAction(nameof(Lane));
			}
			var match = resolved[0];
			var (eok, eerr, oid) = await EnsureOrderAsync(c);
			if (!eok) { TempData["PosErr"] = eerr; return RedirectToAction(nameof(Lane)); }
			var __exp = await ExpiryGateAsync(c, match.ID, await InBasketQtyAsync(oid, match.ID) + 1m);
			if (__exp != null) { TempData["PosErr"] = L["This item cannot be sold {0}", __exp].Value; return RedirectToAction(nameof(Lane)); }
			var (aok, aerr) = await _posOrders.AddLineAsync(PosCompanyId, oid, match.ID, 1m, null, match.UoMId);   // HM-2: pass the resolved unit
			if (!aok) TempData["PosErr"] = aerr;
			return RedirectToAction(nameof(Lane));
		}

		// HM-16: add by item id, from the lookup. It goes through EXACTLY the add path a scan goes through
		// — same order creation, same unit, same pricing and promotion resolution — so an item sold by PLU
		// and the same item scanned cannot be priced differently.
		[HttpPost("lookup/add")][ValidateAntiForgeryToken]
		public async Task<IActionResult> AddByItem(int itemId)
		{
			var c = Ctx(); if (c?.TerminalId == null || c.ShiftId == null) return RedirectToAction(nameof(Login));
			if (!_access.CanSell(c.Roles)) { TempData["PosErr"] = L["This role is not allowed to operate orders"].Value; return RedirectToAction(nameof(Lane)); }

			// Company-scoped and active-only, re-checked here: the result list is a convenience, never the
			// authority on what this till may sell.
			var item = await _db.Items.AsNoTracking()
				.Where(i => i.ID == itemId && i.CompanyID == PosCompanyId && i.IsActive)
				.Select(i => new { i.ID, i.IsWeighted, UoMId = (int?)i.BaseUoMId })
				.FirstOrDefaultAsync();
			if (item == null) { TempData["PosErr"] = L["No item matches this barcode."].Value; return RedirectToAction(nameof(Lane)); }

			// A WEIGHED ITEM HAS NO QUANTITY HERE. Its weight comes from the scale label; adding "one" of it
			// would invent a kilo. The cashier is told to weigh it rather than given a wrong line.
			if (item.IsWeighted) { TempData["PosErr"] = L["This item is sold by weight — weigh it and scan the printed label."].Value; return RedirectToAction(nameof(Lane)); }

			var (eok, eerr, oid) = await EnsureOrderAsync(c);
			if (!eok) { TempData["PosErr"] = eerr; return RedirectToAction(nameof(Lane)); }
			var __exp2 = await ExpiryGateAsync(c, item.ID, await InBasketQtyAsync(oid, item.ID) + 1m);
			if (__exp2 != null) { TempData["PosErr"] = L["This item cannot be sold {0}", __exp2].Value; return RedirectToAction(nameof(Lane)); }
			var (aok, aerr) = await _posOrders.AddLineAsync(PosCompanyId, oid, item.ID, 1m, null, item.UoMId);
			if (!aok) TempData["PosErr"] = aerr;
			return RedirectToAction(nameof(Lane));
		}

		[HttpPost("line/qty")][ValidateAntiForgeryToken]
		public async Task<IActionResult> SetQty(int lineId, decimal qty)
		{
			var c = Ctx(); if (c?.TerminalId == null || c.OrderId == null) return RedirectToAction(nameof(Lane));
			if (!_access.CanSell(c.Roles)) { TempData["PosErr"] = L["This role is not allowed to operate orders"].Value; return RedirectToAction(nameof(Lane)); }
			// Raising a quantity puts stock in the basket exactly as a scan does, so it meets the same gate.
			// The OTHER lines of the same item still count, which is why the basket is asked rather than this line.
			if (qty > 0m)
			{
				var __l = await _db.PosOrderLines.AsNoTracking().Where(l => l.ID == lineId && l.OrderId == c.OrderId)
					.Select(l => new { l.ItemId, l.Qty }).FirstOrDefaultAsync();
				if (__l != null && qty > __l.Qty)
				{
					decimal __others = await InBasketQtyAsync(c.OrderId, __l.ItemId) - __l.Qty;
					var __e = await ExpiryGateAsync(c, __l.ItemId, __others + qty);
					if (__e != null) { TempData["PosErr"] = L["This item cannot be sold {0}", __e].Value; return RedirectToAction(nameof(Lane)); }
				}
			}
			var (ok, err) = await _posOrders.SetLineQtyAsync(PosCompanyId, c.OrderId.Value, lineId, qty);
			if (!ok) TempData["PosErr"] = err;
			return RedirectToAction(nameof(Lane));
		}

		[HttpPost("line/remove")][ValidateAntiForgeryToken]
		public async Task<IActionResult> RemoveLine(int lineId)
		{
			var c = Ctx(); if (c?.TerminalId == null || c.OrderId == null) return RedirectToAction(nameof(Lane));
			if (!_access.CanSell(c.Roles)) { TempData["PosErr"] = L["This role is not allowed to operate orders"].Value; return RedirectToAction(nameof(Lane)); }
			var (ok, err) = await _posOrders.RemoveLineAsync(PosCompanyId, c.OrderId.Value, lineId);
			if (!ok) TempData["PosErr"] = err;
			return RedirectToAction(nameof(Lane));
		}

		// HM-10 slice A: the pay carries a client-generated idempotency token (payToken). A resubmit of the SAME intent
		// (opaque post-commit failure) returns the FIRST invoice, never a second charge. The action already does
		// Post-Redirect-Get (redirect to Lane) so a normal F5 never re-posts; the token covers the lost-302 window.
		[HttpPost("pay")][ValidateAntiForgeryToken]
		public async Task<IActionResult> Pay(string? payToken, string? method, decimal? cashTendered, decimal? cardAmount, long? redeemPoints = null, bool ageConfirmed = false)
		{
			var c = Ctx(); if (c?.TerminalId == null) return RedirectToAction(nameof(Login));
			// HM-1: an OPEN shift is required to take payment — hyper lane only (restaurant path untouched).
			if (c.ShiftId == null || await _pos.GetOpenShiftAsync(c.TerminalId.Value) == null) { TempData["PosErr"] = L["Open a shift before taking payment."].Value; return RedirectToAction(nameof(Lane)); }
			if (!_access.CanSell(c.Roles)) { TempData["PosErr"] = L["This role is not allowed to operate orders"].Value; return RedirectToAction(nameof(Lane)); }
			if (c.OrderId == null) { TempData["PosErr"] = L["The cart is empty."].Value; return RedirectToAction(nameof(Lane)); }

			// HM-11: TENDERS, NOT A HARDCODED "Cash".
			//
			// The settlement engine has taken a LIST of tenders since PayTendersAsync shipped — it resolves a GL
			// account per method, rounds at the DOCUMENT currency and makes the largest tender absorb the
			// remainder so Σ receipts == grand EXACTLY. This lane called PayAsync(..., "Cash", ...) and threw all
			// of that away: a hypermarket that cannot take a card is not a hypermarket. Nothing new is posted
			// here; the capability was already paid for and simply had no way in.
			//
			// CHANGE IS NOT A POSTING. Cash handed back is drawer movement, not revenue, so the engine is told
			// only what the sale is DUE and the change is computed here for the cashier to read. Sending the
			// over-tendered amount would either be rejected or silently clamped — neither is an answer a
			// cashier can act on with a queue waiting.
			var ord = await _posOrders.GetOrderAsync(PosCompanyId, c.OrderId.Value);
			if (ord == null) { TempData["PosErr"] = L["The cart is empty."].Value; return RedirectToAction(nameof(Lane)); }

			// HM-AGE: HIDING THE PAY BUTTON IS NOT A CONTROL. The lane puts a gate in front of payment, but
			// a gate drawn in the browser is advice; the refusal has to live here, where a crafted post, a
			// stale tab or a second window cannot get round it. The cashier's confirmation is a claim that
			// they checked an ID - the server decides whether a claim was even required.
			int? ageBar = await _db.Items.AsNoTracking()
				.Where(i => i.MinAgeYears != null && _db.PosOrderLines.Any(l => l.OrderId == ord.Id && l.ItemId == i.ID))
				.MaxAsync(i => (int?)i.MinAgeYears);
			if (ageBar != null && !ageConfirmed)
			{
				TempData["PosErr"] = L["This basket holds an item restricted to customers aged {0} and over. Check the customer's ID, then confirm the age before taking payment.", ageBar.Value].Value;
				return RedirectToAction(nameof(Lane));
			}
			decimal grand = ord.GrandTotal;
			int dp = await _rounding.DecimalsAsync(PosCompanyId, null, c.BranchId);
			decimal R(decimal v) => Math.Round(v, dp, MidpointRounding.AwayFromZero);

			method = string.IsNullOrWhiteSpace(method) ? "Cash" : method!.Trim();
			decimal card = R(Math.Max(0m, cardAmount ?? 0m));
			decimal tendered = R(Math.Max(0m, cashTendered ?? 0m));
			decimal change = 0m;
			// HM-22: the settlement was posting with a NULL author although the cashier's identity is right
			// here in the till session. It matters more now: a redemption writes a points movement, and a
			// points movement nobody is named on cannot be questioned later.
			int? cashierId = c.EmployeeId > 0 ? c.EmployeeId : (int?)null;

			var tenders = new List<CrossBuy.BL.PosTenderInput>();

			// The proof the gate ran, written only once the sale actually settled. A stamp before the call
			// would record a check for a sale that never happened. A cashier who waves every restricted
			// basket through is invisible without this row; with it, the question is answerable.
			int __ageOrderId = c.OrderId.Value;
			async Task StampAgeAsync()
			{
				if (ageBar == null) return;
				var po = await _db.PosOrders.FirstOrDefaultAsync(o => o.ID == __ageOrderId && o.CompanyId == PosCompanyId);
				if (po == null) return;
				po.AgeVerifiedByEmployeeId = cashierId;
				po.AgeVerifiedAtUtc = DateTime.UtcNow;
				po.AgeVerifiedMinYears = ageBar;
				await _db.SaveChangesAsync();
			}

			// ======================= HM-22: POINTS, SPENT AS A TENDER =======================
			//
			// Redemption does NOT change what the sale is worth. Revenue and tax stay exactly as rung up; the
			// points settle part of what is DUE, against the loyalty account the shop configured for the
			// «Points» payment method. Treating redemption as a discount instead would quietly reduce declared
			// revenue and the tax on it, which is a different and much worse kind of change.
			//
			// The engine spends the points inside its own settlement transaction. Everything computed here is
			// the cashier's arithmetic, re-checked there against a balance read under the transaction.
			long usePoints = 0;
			decimal pointsValue = 0m;
			if (redeemPoints is > 0)
			{
				if (PointValue <= 0m)
				{ TempData["PosErr"] = L["Points redemption is not configured on this deployment."].Value; return RedirectToAction(nameof(Lane)); }
				if (!await _pos.IsCapabilityEnabledAsync(c.BranchId, "Loyalty"))
				{ TempData["PosErr"] = L["Loyalty is not enabled on this branch."].Value; return RedirectToAction(nameof(Lane)); }
				if (!await _db.BranchPaymentMethods.AsNoTracking().AnyAsync(m => m.BranchId == c.BranchId && m.IsActive
					&& m.PaymentMethod == "Points" && m.TargetAccountId != null))
				{ TempData["PosErr"] = L["This branch has no «Points» payment method with an account — points cannot be settled."].Value; return RedirectToAction(nameof(Lane)); }

				int custId = ord.CustomerId ?? 0;
				long balance = custId > 0 ? await CrossBuy.BL.LoyaltyPointsHelper.GetBalanceAsync(_db, PosCompanyId, custId) : 0L;
				if (custId <= 0 || await CrossBuy.BL.LoyaltyPointsHelper.IsWalkInAsync(_db, PosCompanyId, custId))
				{ TempData["PosErr"] = L["Attach the customer to this sale before spending their points."].Value; return RedirectToAction(nameof(Lane)); }

				usePoints = redeemPoints!.Value;
				if (usePoints > balance)
				{ TempData["PosErr"] = L["That is more points than the customer has ({0}).", balance].Value; return RedirectToAction(nameof(Lane)); }

				// NEVER SPEND MORE THAN THE SALE IS WORTH. Points are whole, so the cap is computed in points and
				// the value re-derived from it — capping the money instead would charge the customer for a
				// fraction of a point they cannot see and cannot get back.
				long maxPoints = (long)Math.Floor(grand / PointValue);
				if (usePoints > maxPoints) usePoints = maxPoints;
				pointsValue = R(usePoints * PointValue);
				// TWO DIFFERENT REFUSALS, because they send the cashier to two different places. The cap
				// landing on zero means the sale is too small to absorb even one point; a zero VALUE after
				// rounding means the point is worth less than this currency can express. Telling a cashier
				// the second when the first is true sends them looking for a rounding problem that is not there.
				if (maxPoints <= 0)
				{ TempData["PosErr"] = L["This sale is worth less than a single point — there is nothing to redeem against."].Value; return RedirectToAction(nameof(Lane)); }
				if (usePoints <= 0 || pointsValue <= 0m)
				{ TempData["PosErr"] = L["Those points are worth less than this sale's smallest unit."].Value; return RedirectToAction(nameof(Lane)); }

				tenders.Add(new CrossBuy.BL.PosTenderInput { Method = "Points", Amount = pointsValue });
			}

			// What is left for the cashier to collect once the points have paid their part.
			decimal due = R(grand - pointsValue);

			// POINTS COVERED THE WHOLE SALE. There is nothing to take, and adding a zero tender would be
			// rejected by the engine, so settle on the points alone.
			if (due <= 0m)
			{
				var (pok, perr, pid) = await _posOrders.PayTendersAsync(PosCompanyId, c.OrderId.Value, tenders, cashierId, 0, null, usePoints);
				if (!pok) { TempData["PosErr"] = perr; return RedirectToAction(nameof(Lane)); }
				await StampAgeAsync();
				c.OrderId = null; SetCtx(c);
				TempData["PosMsg"] = L["Paid with points — invoice #{0} created.", pid ?? 0].Value;
				return RedirectToAction(nameof(Lane));
			}


			if (method == "Card")
			{
				tenders.Add(new CrossBuy.BL.PosTenderInput { Method = "Card", Amount = due });
			}
			else if (method == "Split")
			{
				if (card <= 0m) { TempData["PosErr"] = L["Enter the card amount for a split payment."].Value; return RedirectToAction(nameof(Lane)); }
				if (card >= due) { TempData["PosErr"] = L["The card amount covers the whole sale — use «Card» instead of a split."].Value; return RedirectToAction(nameof(Lane)); }
				decimal cashDue = R(due - card);
				// the cashier may still over-tender the CASH half; the change is on that half only.
				decimal cashIn = tendered > 0m ? tendered : cashDue;
				if (cashIn < cashDue) { TempData["PosErr"] = L["The cash tendered {0} is less than the cash due {1}.", cashIn.ToString("0." + new string('#', dp)), cashDue.ToString("0." + new string('#', dp))].Value; return RedirectToAction(nameof(Lane)); }
				change = R(cashIn - cashDue);
				tenders.Add(new CrossBuy.BL.PosTenderInput { Method = "Card", Amount = card });
				tenders.Add(new CrossBuy.BL.PosTenderInput { Method = "Cash", Amount = cashDue });
			}
			else
			{
				decimal cashIn = tendered > 0m ? tendered : due;
				if (cashIn < due) { TempData["PosErr"] = L["The cash tendered {0} is less than the total {1}.", cashIn.ToString("0." + new string('#', dp)), due.ToString("0." + new string('#', dp))].Value; return RedirectToAction(nameof(Lane)); }
				change = R(cashIn - due);
				tenders.Add(new CrossBuy.BL.PosTenderInput { Method = "Cash", Amount = due });
			}

			var (ok, err, invId) = await _posOrders.PayTendersAsync(PosCompanyId, c.OrderId.Value, tenders, cashierId, 0, null, usePoints);
			if (!ok) { TempData["PosErr"] = err; return RedirectToAction(nameof(Lane)); }
			await StampAgeAsync();
			c.OrderId = null; SetCtx(c);
			TempData["PosMsg"] = L["Paid — invoice #{0} created.", invId ?? 0].Value;
			if (change > 0m) TempData["PosChange"] = change.ToString("0." + new string('#', dp));
			return RedirectToAction(nameof(Lane));
		}

		// ==================== HM-26: COUPONS AT THE TILL ====================
		//
		// Entering a code is an ORDINARY SELLING ACTION, so it needs CanSell and not a manager. A coupon
		// the shop issued and the customer is holding is not a discretionary discount; refusing to let a
		// cashier take it without a supervisor is how queues form over a voucher the shop printed itself.
		//
		// Removal is the same authority, deliberately: a cashier who mis-keys a code must be able to undo
		// it at once. The use goes back to the pool, so the customer loses nothing by the mistake.
		[HttpPost("coupon/apply")][ValidateAntiForgeryToken]
		public async Task<IActionResult> ApplyCoupon(string? code)
		{
			var c = Ctx(); if (c == null) return RedirectToAction(nameof(Login));
			if (!_access.CanSell(c.Roles)) { TempData["PosErr"] = L["This role is not allowed to operate orders"].Value; return RedirectToAction(nameof(Lane)); }
			if (c.OrderId == null) { TempData["PosErr"] = L["The cart is empty."].Value; return RedirectToAction(nameof(Lane)); }

			int? emp = c.EmployeeId > 0 ? c.EmployeeId : (int?)null;
			var (ok, err, preview) = await _coupons.ApplyAsync(PosCompanyId, c.OrderId.Value, code ?? "", emp);
			if (!ok) { TempData["PosErr"] = err; return RedirectToAction(nameof(Lane)); }

			// The basket's totals and its TAX both move when a coupon lands, so the order is recomputed
			// before the cashier is shown anything — a stale total on a till is a dispute at the counter.
			await _posOrders.RecalcOrderAsync(PosCompanyId, c.OrderId.Value);
			TempData["PosMsg"] = L["Coupon applied — {0} off.", preview!.Discount.ToString()].Value;
			return RedirectToAction(nameof(Lane));
		}

		[HttpPost("coupon/remove")][ValidateAntiForgeryToken]
		public async Task<IActionResult> RemoveCoupon()
		{
			var c = Ctx(); if (c == null) return RedirectToAction(nameof(Login));
			if (!_access.CanSell(c.Roles)) { TempData["PosErr"] = L["This role is not allowed to operate orders"].Value; return RedirectToAction(nameof(Lane)); }
			if (c.OrderId == null) { TempData["PosErr"] = L["The cart is empty."].Value; return RedirectToAction(nameof(Lane)); }

			var (ok, err) = await _coupons.RemoveAsync(PosCompanyId, c.OrderId.Value);
			if (!ok) { TempData["PosErr"] = err; return RedirectToAction(nameof(Lane)); }
			await _posOrders.RecalcOrderAsync(PosCompanyId, c.OrderId.Value);
			TempData["PosMsg"] = L["The coupon was removed."].Value;
			return RedirectToAction(nameof(Lane));
		}

		// HM-11: PARK THE SALE. The forgotten-milk case: a customer leaves the queue to fetch one more item and
		// the next customer must not wait for them. The engine has had HoldOrderAsync/RecallHeldAsync and
		// GetHeldOrdersAsync all along, and "SuspendResume" has been rendered as an ENABLED green badge on this
		// very lane with nothing behind it. This is the consumer that badge always claimed to have.
		[HttpPost("hold")][ValidateAntiForgeryToken]
		public async Task<IActionResult> Hold()
		{
			var c = Ctx(); if (c?.TerminalId == null) return RedirectToAction(nameof(Login));
			if (!_access.CanSell(c.Roles)) { TempData["PosErr"] = L["This role is not allowed to operate orders"].Value; return RedirectToAction(nameof(Lane)); }
			if (!await _pos.IsCapabilityEnabledAsync(c.BranchId, "SuspendResume")) { TempData["PosErr"] = L["Suspend / resume is not enabled on this branch."].Value; return RedirectToAction(nameof(Lane)); }
			if (c.OrderId == null) { TempData["PosErr"] = L["The cart is empty."].Value; return RedirectToAction(nameof(Lane)); }
			var (ok, err) = await _posOrders.HoldOrderAsync(PosCompanyId, c.OrderId.Value);
			if (!ok) { TempData["PosErr"] = err; return RedirectToAction(nameof(Lane)); }
			c.OrderId = null; SetCtx(c);   // the till is free for the next customer
			TempData["PosMsg"] = L["The sale is parked."].Value;
			return RedirectToAction(nameof(Lane));
		}

		[HttpPost("resume")][ValidateAntiForgeryToken]
		public async Task<IActionResult> Resume(int orderId)
		{
			var c = Ctx(); if (c?.TerminalId == null) return RedirectToAction(nameof(Login));
			if (!_access.CanSell(c.Roles)) { TempData["PosErr"] = L["This role is not allowed to operate orders"].Value; return RedirectToAction(nameof(Lane)); }
			if (!await _pos.IsCapabilityEnabledAsync(c.BranchId, "SuspendResume")) { TempData["PosErr"] = L["Suspend / resume is not enabled on this branch."].Value; return RedirectToAction(nameof(Lane)); }
			// a parked sale may only be resumed onto an EMPTY till — otherwise the two baskets merge invisibly.
			if (c.OrderId != null) { TempData["PosErr"] = L["Finish or park the current sale before resuming another."].Value; return RedirectToAction(nameof(Lane)); }
			// BRANCH-SCOPED: the recall list is this branch's, so a till cannot resume another store's parked sale.
			var mine = await _posOrders.GetHeldOrdersAsync(PosCompanyId, c.BranchId);
			if (!mine.Any(h => h.Id == orderId)) { TempData["PosErr"] = L["That parked sale is not available on this branch."].Value; return RedirectToAction(nameof(Lane)); }
			var (ok, err) = await _posOrders.RecallHeldAsync(PosCompanyId, orderId);
			if (!ok) { TempData["PosErr"] = err; return RedirectToAction(nameof(Lane)); }
			c.OrderId = orderId; SetCtx(c);
			TempData["PosMsg"] = L["The parked sale is resumed."].Value;
			return RedirectToAction(nameof(Lane));
		}

		// HM-11: ABANDON THE WHOLE BASKET. Removing lines one by one was the only way out of a mistaken sale,
		// which on a thirty-line trolley is not a way out at all.
		[HttpPost("void")][ValidateAntiForgeryToken]
		public async Task<IActionResult> VoidOrder()
		{
			var c = Ctx(); if (c?.TerminalId == null) return RedirectToAction(nameof(Login));
			if (!_access.CanSell(c.Roles)) { TempData["PosErr"] = L["This role is not allowed to operate orders"].Value; return RedirectToAction(nameof(Lane)); }
			if (c.OrderId == null) { TempData["PosErr"] = L["The cart is empty."].Value; return RedirectToAction(nameof(Lane)); }
			var (ok, err) = await _posOrders.VoidOrderAsync(PosCompanyId, c.OrderId.Value);
			if (!ok) { TempData["PosErr"] = err; return RedirectToAction(nameof(Lane)); }
			c.OrderId = null; SetCtx(c);
			TempData["PosMsg"] = L["The sale was voided."].Value;
			return RedirectToAction(nameof(Lane));
		}

		// HM-10 slice A: receipt recovery — after an opaque failure the cashier confirms whether the last sale(s) posted
		// and can reprint. READ-ONLY (zero posting). Scoped to THIS terminal's own paid orders (company + branch + terminal).
		[HttpGet("receipts")]
		public async Task<IActionResult> Receipts()
		{
			var c = Ctx(); if (c == null) return RedirectToAction(nameof(Login));
			if (c.TerminalId == null) return RedirectToAction(nameof(Start));
			var rows = await _db.PosOrders.AsNoTracking()
				.Where(o => o.CompanyId == PosCompanyId && o.BranchId == c.BranchId && o.TerminalId == c.TerminalId && o.Status == "Paid" && o.InvoiceId != null)
				.OrderByDescending(o => o.ClosedAt)
				.Select(o => new { o.ID, o.InvoiceId, o.ReceiptNo, o.GrandTotal, o.ClosedAt })
				.Take(10).ToListAsync();
			int? ccyId = await _db.BranchPosSettings.AsNoTracking().Where(s => s.BranchId == c.BranchId).Select(s => s.DefaultCurrencyId).FirstOrDefaultAsync();
			ViewBag.Ctx = c;
			ViewBag.Rows = rows.Select(r => new object[] { r.InvoiceId!, r.ReceiptNo ?? "", r.GrandTotal, r.ClosedAt, r.ID }).ToList();
			ViewBag.CanReverse = _access.IsManager(c.Roles);   // selling and un-selling are not the same right
			ViewBag.Dp = await _rounding.DecimalsAsync(PosCompanyId, ccyId == 0 ? (int?)null : ccyId, c.BranchId);
			return View("~/Views/Hyper/Receipts.cshtml");
		}

		// ==================== HM-14: RETURN A SETTLED SALE ====================
		//
		// WHAT THIS IS AND IS NOT. The engine reverses a WHOLE paid sale — stock back at its original
		// cost, every journal entry reversed, in one transaction. It cannot yet take one line back out of
		// a basket of thirty. So this is a full receipt reversal and the screen says so; offering a
		// "return" that silently cancels the entire sale would be the worse of the two by a long way.
		//
		// A MANAGER'S RIGHT, NOT A CASHIER'S. Selling and un-selling are different authorities: the till
		// that can take money must not be able to hand it back unsupervised, which is the oldest control
		// in retail. IsManager is the same predicate the lane already uses for its other guarded actions.
		//
		// SCOPED TO THIS TILL'S OWN BRANCH. The order must belong to this company, this branch and this
		// terminal — the same three-part guard the receipt list is built on, re-checked here because a
		// list is a convenience and never the authority.
		[HttpPost("receipt/void")]
		[ValidateAntiForgeryToken]
		public async Task<IActionResult> ReverseSale(int orderId)
		{
			var c = Ctx(); if (c?.TerminalId == null) return RedirectToAction(nameof(Login));
			if (!_access.IsManager(c.Roles)) { TempData["PosErr"] = L["Only a manager may reverse a settled sale."].Value; return RedirectToAction(nameof(Receipts)); }

			var owned = await _db.PosOrders.AsNoTracking().AnyAsync(o =>
				o.ID == orderId && o.CompanyId == PosCompanyId && o.BranchId == c.BranchId && o.TerminalId == c.TerminalId && o.Status == "Paid");
			if (!owned) { TempData["PosErr"] = L["That sale is not one this till settled."].Value; return RedirectToAction(nameof(Receipts)); }

			var (ok, err) = await _posOrders.VoidPaidOrderAsync(PosCompanyId, orderId, null);
			if (!ok) { TempData["PosErr"] = err; return RedirectToAction(nameof(Receipts)); }
			TempData["PosMsg"] = L["The sale was reversed — stock and entries are back."].Value;
			return RedirectToAction(nameof(Receipts));
		}

		// ==================== HM-RET: TAKING PART OF A SALE BACK ====================
		//
		// WHY THIS EXISTS. Reversal above can only undo a WHOLE receipt. A customer returning one yoghurt
		// out of a basket of thirty had to have the entire sale cancelled and the other twenty-nine items
		// re-scanned, at the till, with a queue behind them. That is not a return policy; it is the absence
		// of one, and it is why the screen above had to say so.
		//
		// NOTHING NEW IS INVENTED HERE. The return document, its journal entry, the VAT reversal and the
		// stock coming back at original cost are all CreateSalesReturnAsync, which has shipped for a long
		// time. The cash leaving the drawer is RecordShiftCashMovementAsync, which already exists so that a
		// mid-shift payout cannot make a drawer look short. Putting a refund anywhere else - a negative
		// receipt, a bare journal entry - would settle the ledger while leaving ExpectedCash blind, and the
		// drawer would reconcile tonight and show an unexplained shortage tomorrow.
		//
		// THE QUANTITY CAP IS READ, NOT TRUSTED. What was sold comes from the INVOICE, and what has already
		// come back is summed from the returns against it. A screen that offers a maximum is a convenience;
		// the cap is re-derived here because a second tab, a back button and a crafted post all reach the
		// same action.
		[HttpGet("return")]
		public async Task<IActionResult> PartialReturn(int orderId)
		{
			var c = Ctx(); if (c?.TerminalId == null) return RedirectToAction(nameof(Login));
			if (!_access.CanSell(c.Roles)) { TempData["PosErr"] = L["This role is not allowed to operate orders"].Value; return RedirectToAction(nameof(Receipts)); }

			var (err, ord, lines) = await LoadReturnableAsync(c, orderId);
			if (err != null) { TempData["PosErr"] = err; return RedirectToAction(nameof(Receipts)); }

			ViewBag.Ctx = c; ViewBag.Head = ord; ViewBag.Lines = lines;
			ViewBag.Dp = await _rounding.DecimalsAsync(PosCompanyId, null, c.BranchId);
			return View("~/Views/Hyper/PosReturn.cshtml");
		}

		[HttpPost("return")][ValidateAntiForgeryToken]
		public async Task<IActionResult> PartialReturn(int orderId, int[]? lineId, decimal[]? qty)
		{
			var c = Ctx(); if (c?.TerminalId == null) return RedirectToAction(nameof(Login));
			if (!_access.CanSell(c.Roles)) { TempData["PosErr"] = L["This role is not allowed to operate orders"].Value; return RedirectToAction(nameof(Receipts)); }

			// CASH COMES OUT OF A DRAWER, SO THERE MUST BE ONE. Without an open shift the refund has nowhere
			// to be recorded against and the money leaves an unattributed till.
			if (c.ShiftId == null || await _pos.GetOpenShiftAsync(c.TerminalId.Value) == null)
			{ TempData["PosErr"] = L["Open a shift before taking a return."].Value; return RedirectToAction(nameof(Receipts)); }

			var (err, ord, rows) = await LoadReturnableAsync(c, orderId);
			if (err != null) { TempData["PosErr"] = err; return RedirectToAction(nameof(Receipts)); }

			var want = new Dictionary<int, decimal>();
			for (int i = 0; lineId != null && qty != null && i < lineId.Length && i < qty.Length; i++)
				if (qty[i] > 0m) want[lineId[i]] = qty[i];
			if (want.Count == 0) { TempData["PosErr"] = L["Choose at least one line and a quantity to return."].Value; return RedirectToAction(nameof(PartialReturn), new { orderId }); }

			var picked = new List<CrossBuy.BL.SalesLineInput>();
			foreach (var r in rows!)
			{
				if (!want.TryGetValue(r.LineId, out var q)) continue;
				if (q > r.Returnable)
				{ TempData["PosErr"] = L["Only {0} of {1} is still returnable.", r.Returnable.ToString("0.##"), r.Name].Value; return RedirectToAction(nameof(PartialReturn), new { orderId }); }
				picked.Add(new CrossBuy.BL.SalesLineInput
				{
					ItemDescription = r.Name,
					Qty = q,
					UnitPrice = r.UnitPrice,
					// The discount is apportioned to the quantity coming back, never carried whole: refunding a
					// whole line discount on one returned unit hands back money that was never taken for it.
					DiscountAmount = r.Qty > 0m ? Math.Round(r.Discount * (q / r.Qty), 4, MidpointRounding.AwayFromZero) : 0m,
					TaxRate = r.TaxRate,
					RevenueAccountId = r.RevenueAccountId,
					ItemId = r.ItemId,
					WarehouseId = r.WarehouseId,
					UoMId = r.UoMId,
				});
			}

			int? userId = c.EmployeeId > 0 ? c.EmployeeId : (int?)null;
			var (rok, rerr, ret) = await _ar.CreateSalesReturnAsync(
				PosCompanyId, ord!.CustomerId, ord.InvoiceId, DateTime.Today, picked,
				"POS return against receipt " + (ord.ReceiptNo ?? ""), userId);
			if (!rok || ret == null) { TempData["PosErr"] = rerr; return RedirectToAction(nameof(PartialReturn), new { orderId }); }

			// THE MONEY. The return credited the customer's control account; handing the cash back debits it
			// again and takes the drawer down by the same figure, so the customer nets to zero and the shift
			// expectation moves with the real money. A refund of zero posts nothing rather than an empty entry.
			if (ret.GrandTotal > 0m)
			{
				int ctrl = await _db.Customers.AsNoTracking()
					.Where(x => x.ID == ord.CustomerId && x.CompanyID == PosCompanyId)
					.Select(x => x.ControlAccountId).FirstOrDefaultAsync();
				if (ctrl == 0)
				{ TempData["PosErr"] = L["The customer has no receivables account, so the refund cannot be paid out."].Value; return RedirectToAction(nameof(Receipts)); }

				var (mok, merr, moveJeId) = await _pos.RecordShiftCashMovementAsync(
					PosCompanyId, c.TerminalId.Value, c.ShiftId.Value, "Out", ret.GrandTotal, ctrl,
					"Refund " + (ret.ReturnNo ?? ""), DateTime.Today, userId, systemCounter: true);
				// The return is already posted. Reporting success would be a lie, and silently reversing a
				// posted document to hide a drawer problem would be worse - the cashier is told exactly which
				// half failed, so the money can be settled by hand against a document that exists.
				if (!mok)
				{ TempData["PosErr"] = L["Return {0} was posted, but the cash could not be taken out of the drawer: {1}", ret.ReturnNo ?? "", merr ?? ""].Value; return RedirectToAction(nameof(Receipts)); }

				// THE SUBSIDIARY LEDGER MUST SEE THE MONEY GO BACK.
				//
				// The drawer movement settles the GENERAL ledger - the credit note credits the customer's
				// control account and this debits it again, so the customer nets to zero there. But the
				// customer subledger is built from DOCUMENTS, not from journal lines (a JournalEntryLine
				// carries no customer at all), so without this row the credit note is seen and the refund
				// is not: the customer reads as owed the money for ever, the aging ages it, the statement
				// never shows it, and ar_sub reports the gap to the decimal. It did - 0.750, exactly one
				// test refund.
				//
				// A NEGATIVE amount, deliberately. Every reader of this table subtracts receipts from what
				// the customer owes, so money handed BACK is a receipt of less than nothing - and that one
				// sign makes the balance, the aging and the statement all agree without any of them being
				// taught about refunds. It carries no ReceiptAllocation rows: there is no invoice being
				// settled here, and inventing an allocation would corrupt the FIFO clearing of real ones.
				var refundRow = new CrossBuy.Models.Context.Accounting.Receipt
				{
					CompanyID = PosCompanyId, CustomerId = ord.CustomerId, ReceiptDate = DateTime.Today,
					Amount = -ret.GrandTotal, AmountBase = -ret.GrandTotal,
					Method = "Cash", CashAccountId = ctrl, Status = "Posted",
					JournalEntryId = moveJeId, CreatedAt = DateTime.UtcNow,
				};
				_db.Receipts.Add(refundRow);
				await _db.SaveChangesAsync();
				refundRow.ReceiptNo = $"RF-{DateTime.Today:yyyy}-{refundRow.ID:D5}";
				await _db.SaveChangesAsync();
			}

			TempData["PosMsg"] = L["Return {0} posted - refund {1}.", ret.ReturnNo ?? "", ret.GrandTotal.ToString("0.###")].Value;
			return RedirectToAction(nameof(Receipts));
		}

		// ==================== HM-EXP: the till's expiry gate ====================
		//
		// Capability-gated, because a branch that does not sell dated goods should not be made to carry the
		// check. Off, or not an expiry-tracked item, and this is silent - it never slows an ordinary scan.
		//
		// It asks about the quantity that WOULD be in the basket, not the one being added, because three
		// scans of the last two units in date must fail on the third and not on the first.
		private async Task<string?> ExpiryGateAsync(HyperCtx c, int itemId, decimal wantQty)
		{
			if (!await _pos.IsCapabilityEnabledAsync(c.BranchId, "ExpiryControl")) return null;
			int? wh = await _db.BranchPosSettings.AsNoTracking()
				.Where(x => x.BranchId == c.BranchId).Select(x => x.DefaultSalesWarehouseId).FirstOrDefaultAsync();
			// No sales warehouse configured: the payment path already refuses with a clearer message about
			// exactly that, and duplicating it here would only move the confusion earlier.
			if (wh == null) return null;

			var (ok, reason, _) = await _pos.ExpirySellCheckAsync(PosCompanyId, itemId, wh.Value, wantQty, DateTime.Today);
			return ok ? null : reason;
		}

		// What is already in the basket for this item, so the gate can judge the TOTAL.
		private async Task<decimal> InBasketQtyAsync(int? orderId, int itemId) =>
			orderId == null ? 0m
			: await _db.PosOrderLines.AsNoTracking().Where(l => l.OrderId == orderId && l.ItemId == itemId)
				.Select(l => (decimal?)l.Qty).SumAsync() ?? 0m;

		public class ReturnableLine
		{
			public int LineId { get; set; }
			public string Name { get; set; } = "";
			public decimal Qty { get; set; }
			public decimal Returned { get; set; }
			public decimal Returnable { get; set; }
			public decimal UnitPrice { get; set; }
			public decimal Discount { get; set; }
			public decimal TaxRate { get; set; }
			public int RevenueAccountId { get; set; }
			public int? ItemId { get; set; }
			public int? WarehouseId { get; set; }
			public int? UoMId { get; set; }
		}

		public class PosOrderReturnHead
		{
			public int OrderId { get; set; }
			public int? InvoiceId { get; set; }
			public int CustomerId { get; set; }
			public string? ReceiptNo { get; set; }
			public decimal GrandTotal { get; set; }
			public DateTime? ClosedAt { get; set; }
		}

		// Shared by the screen and the post, so the two can never disagree about what is still returnable.
		private async Task<(string? err, PosOrderReturnHead? head, List<ReturnableLine>? lines)> LoadReturnableAsync(HyperCtx c, int orderId)
		{
			var o = await _db.PosOrders.AsNoTracking().FirstOrDefaultAsync(x =>
				x.ID == orderId && x.CompanyId == PosCompanyId && x.BranchId == c.BranchId
				&& x.TerminalId == c.TerminalId && x.Status == "Paid");
			if (o == null) return (L["That sale is not one this till settled."].Value, null, null);
			if (o.InvoiceId == null) return (L["That sale has no invoice, so nothing can be returned against it."].Value, null, null);
			if (o.CustomerId == null) return (L["That sale has no customer, so a return cannot be credited."].Value, null, null);

			var inv = await _db.SalesInvoiceLines.AsNoTracking()
				.Where(l => l.SalesInvoiceId == o.InvoiceId).OrderBy(l => l.LineNo).ToListAsync();
			if (inv.Count == 0) return (L["That invoice has no lines."].Value, null, null);

			// What has already come back, by item, across every return raised against this invoice.
			var back = await (from rl in _db.SalesReturnLines.AsNoTracking()
							  join r in _db.SalesReturns.AsNoTracking() on rl.SalesReturnId equals r.ID
							  where r.CompanyID == PosCompanyId && r.OriginalInvoiceId == o.InvoiceId
							  group rl by rl.ItemId into g
							  select new { ItemId = g.Key, Q = g.Sum(x => x.Qty) }).ToListAsync();
			var backBy = back.Where(x => x.ItemId != null).ToDictionary(x => x.ItemId!.Value, x => x.Q);

			bool isAr = System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ar";
			var rows = inv.Select(l => new ReturnableLine
			{
				LineId = l.ID,
				Name = isAr ? l.ItemDescription : (string.IsNullOrWhiteSpace(l.ItemDescriptionEn) ? l.ItemDescription : l.ItemDescriptionEn!),
				Qty = l.Qty,
				Returned = (l.ItemId != null && backBy.TryGetValue(l.ItemId.Value, out var q)) ? q : 0m,
				UnitPrice = l.UnitPrice,
				Discount = l.DiscountAmount,
				TaxRate = l.TaxRate,
				RevenueAccountId = l.RevenueAccountId,
				ItemId = l.ItemId,
				WarehouseId = l.WarehouseId,
				UoMId = l.UoMId,
			}).ToList();
			foreach (var r in rows) r.Returnable = Math.Max(0m, r.Qty - r.Returned);

			return (null, new PosOrderReturnHead
			{
				OrderId = o.ID,
				InvoiceId = o.InvoiceId,
				CustomerId = o.CustomerId.Value,
				ReceiptNo = o.ReceiptNo,
				GrandTotal = o.GrandTotal,
				ClosedAt = o.ClosedAt,
			}, rows);
		}

		// ==================== HM-8: OFFICIAL A4 INVOICE (cashier path) ====================
		// Gate = HyperCtx (controller guard). Capability = OfficialInvoice (branch-scoped). Branch+company guard: the
		// invoice must be the pay result of a POS order on THIS cashier's branch — a cashier can never reach another
		// branch's invoice. The view is the SHARED Accounting/SalesInvoicePrint (one template, two thin actions).
		[HttpGet("invoice/print/{invoiceId:int}")]
		public async Task<IActionResult> PrintInvoice(int invoiceId)
		{
			var c = Ctx(); if (c == null) return RedirectToAction(nameof(Login));
			if (!await _pos.IsCapabilityEnabledAsync(c.BranchId, "OfficialInvoice"))
			{ TempData["PosErr"] = L["The official invoice is not enabled on this branch."].Value; return RedirectToAction(nameof(Lane)); }
			if (!await BranchOwnsInvoiceAsync(c.BranchId, invoiceId))
			{ TempData["PosErr"] = L["This invoice does not belong to your branch."].Value; return RedirectToAction(nameof(Lane)); }
			var inv = await _db.SalesInvoices.AsNoTracking().Include(i => i.Lines)
				.FirstOrDefaultAsync(i => i.ID == invoiceId && i.CompanyID == PosCompanyId);
			if (inv == null) { TempData["PosErr"] = L["Sales invoice not found"].Value; return RedirectToAction(nameof(Lane)); }
			bool isAr = System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ar";
			var pd = await OfficialInvoiceHelper.LoadPrintDataAsync(_db, inv, isAr);
			ViewBag.Company = pd.Company; ViewBag.Customer = pd.Customer; ViewBag.Dp = pd.Dp;
			ViewBag.CurrencyCode = pd.CurrencyCode; ViewBag.Uoms = pd.Uoms;
			return View("~/Views/Accounting/SalesInvoicePrint.cshtml", inv);
		}

		// HM-8: stamp a walk-in beneficiary (set-once, tax-zero only). Same capability + branch guard; financials untouched.
		[HttpPost("invoice/stamp")][ValidateAntiForgeryToken]
		// STAGE 1 BATCH D1 WAVE 1 — HIGH (not Critical: it was already branch- and company-bounded).
		//
		// What was already right, and is PRESERVED unchanged: the `OfficialInvoice` capability gate, the
		// branch-ownership check (`BranchOwnsInvoiceAsync` — the invoice must be the pay result of an order on
		// THIS branch), and the company predicate on the invoice row. Those three are why this endpoint was
		// re-classified down from the Critical rank the brief opened with: a caller could only ever reach
		// invoices of their own branch and company.
		//
		// What was MISSING is a role check: every lane role could stamp — including `pos-waiter` and
		// `pos-kitchen`, who have no business touching an issued invoice. `CanSell` (pos-cashier or pos-manager)
		// is the minimum that is both real and compatible: stamping a walk-in beneficiary is part of the ordinary
		// cashier printing flow, so requiring a manager would break a working counter workflow rather than
		// securing anything.
		//
		// The lane identity comes from `HyperCtx` (session) and its roles from `BranchUserRoles` — the DOCUMENTED
		// PERMANENT POS EXCEPTION. It is used here because this is the POS lane; it is deliberately NOT copied
		// into the accounting twin of this action, which resolves a real BusinessContext instead.
		public async Task<IActionResult> StampInvoiceCustomer(int invoiceId, string name, string? taxNo)
		{
			var c = Ctx(); if (c == null) return RedirectToAction(nameof(Login));
			if (!_access.CanSell(c.Roles))
			{ TempData["PosErr"] = L["You do not have permission to perform this action"].Value; return RedirectToAction(nameof(Lane)); }
			if (!await _pos.IsCapabilityEnabledAsync(c.BranchId, "OfficialInvoice"))
			{ TempData["PosErr"] = L["The official invoice is not enabled on this branch."].Value; return RedirectToAction(nameof(Lane)); }
			if (!await BranchOwnsInvoiceAsync(c.BranchId, invoiceId))
			{ TempData["PosErr"] = L["This invoice does not belong to your branch."].Value; return RedirectToAction(nameof(Lane)); }
			var inv = await _db.SalesInvoices.FirstOrDefaultAsync(i => i.ID == invoiceId && i.CompanyID == PosCompanyId);
			if (inv == null) { TempData["PosErr"] = L["Sales invoice not found"].Value; return RedirectToAction(nameof(Lane)); }
			var (ok, err) = OfficialInvoiceHelper.StampCustomer(inv, name, taxNo, c.EmployeeId.ToString());
			if (!ok) { TempData["PosErr"] = err; return RedirectToAction(nameof(PrintInvoice), new { invoiceId }); }
			await _db.SaveChangesAsync();
			return RedirectToAction(nameof(PrintInvoice), new { invoiceId });
		}

		// Branch guard: the invoice must be the pay result of an order on THIS branch (and this company).
		private Task<bool> BranchOwnsInvoiceAsync(int branchId, int invoiceId) =>
			_db.PosOrders.AsNoTracking().AnyAsync(o => o.InvoiceId == invoiceId && o.BranchId == branchId && o.CompanyId == PosCompanyId);

		// ==================== HM-9 slice 1: CUSTOMER IDENTITY (the loyalty prerequisite) ====================
		// The hyper lane force-links every order to the Walk-in customer (PosOrderService.CreateOrderAsync). Loyalty needs a
		// real, identifiable customer ON THE ORDER before pay. This slice REUSES the existing building blocks —
		// _ar.SearchCustomersAsync, _ar.CreateCustomerAsync, _posOrders.SetOrderCustomerAsync — with NO new service and NO
		// copy of the restaurant lane. Gated by the reserved "Loyalty" capability (its FIRST consumer). A fail-closed guard
		// refuses to link a customer whose AR control account is missing/invalid. Linking is PRE-PAY only — SetOrderCustomerAsync
		// itself refuses a non-Open order. Posting is untouched (every customer's AR control resolves to 1102 today, so the JE
		// is byte-identical; if a customer legitimately had a different valid control account, the receivable posts there — the
		// guard only blocks a MISSING/invalid account, never a different valid one).

		// Fail-closed control-account guard: the customer's AR control account must EXIST and be an ACTIVE account in THIS
		// company. It does NOT hard-code 1102 — a future customer with a different LEGITIMATE control account still passes;
		// only a missing/zero/inactive account (a corruption, e.g. legacy customer 1025 with ControlAccountId=0) is refused.
		// NOTE: we do NOT require IsPostable — the AR control account (1102) is intentionally a non-postable CONTROL account,
		// yet it is exactly where the receivable posts. IsPostable governs the manual-JE leaf rule, not the AR control target.
		private async Task<bool> CustomerControlAccountValidAsync(int customerId)
		{
			var ctrl = await _db.Customers.AsNoTracking().Where(c => c.ID == customerId && c.CompanyID == PosCompanyId)
				.Select(c => (int?)c.ControlAccountId).FirstOrDefaultAsync();
			if (ctrl == null || ctrl.Value <= 0) return false;
			return await _db.Accounts.AsNoTracking().AnyAsync(a => a.ID == ctrl.Value && a.CompanyID == PosCompanyId && a.IsActive);
		}

		[HttpGet("customer")]
		public async Task<IActionResult> Customer()
		{
			var c = Ctx(); if (c == null) return RedirectToAction(nameof(Login));
			ViewBag.Ctx = c;
			ViewBag.Enabled = await _pos.IsCapabilityEnabledAsync(c.BranchId, "CustomerIdentity");
			CrossBuy.Models.Context.Accounting.Customer? cur = null;
			if (c.OrderId != null)
			{
				var oCustId = await _db.PosOrders.AsNoTracking().Where(o => o.ID == c.OrderId && o.CompanyId == PosCompanyId).Select(o => (int?)o.CustomerId).FirstOrDefaultAsync();
				if (oCustId != null) cur = await _db.Customers.AsNoTracking().FirstOrDefaultAsync(x => x.ID == oCustId.Value && x.CompanyID == PosCompanyId && x.NameEn != "POS Walk-in");
			}
			ViewBag.Current = cur;
			// HM-9 slice 2: read-only derived points balance (shown only when loyalty is enabled and a real customer is linked)
			ViewBag.LoyaltyOn = await _pos.IsCapabilityEnabledAsync(c.BranchId, "Loyalty");
			if (cur != null) ViewBag.Balance = await CrossBuy.BL.LoyaltyPointsHelper.GetBalanceAsync(_db, PosCompanyId, cur.ID);
			return View("~/Views/Hyper/Customer.cshtml");
		}

		[HttpGet("customer/search")]
		public async Task<IActionResult> CustomerSearch(string? q)
		{
			var c = Ctx(); if (c == null) return Json(new { ok = false });
			if (!await _pos.IsCapabilityEnabledAsync(c.BranchId, "CustomerIdentity"))
				return Json(new { ok = false, error = L["Customer identification is not enabled on this branch."].Value });
			var (rows, _) = await _ar.SearchCustomersAsync(PosCompanyId, q, true, 1, 15);
			return Json(new { ok = true, items = rows.Where(x => x.NameEn != "POS Walk-in").Select(x => new { id = x.ID, name = x.Name, phone = x.Phone }) });
		}

		[HttpPost("customer/add")][ValidateAntiForgeryToken]
		public async Task<IActionResult> CustomerAdd(string name, string? phone)
		{
			var c = Ctx(); if (c == null) return RedirectToAction(nameof(Login));
			if (!_access.CanSell(c.Roles)) { TempData["PosErr"] = L["This role is not allowed to operate orders"].Value; return RedirectToAction(nameof(Customer)); }
			if (!await _pos.IsCapabilityEnabledAsync(c.BranchId, "CustomerIdentity"))
			{ TempData["PosErr"] = L["Customer identification is not enabled on this branch."].Value; return RedirectToAction(nameof(Customer)); }
			name = (name ?? "").Trim();
			if (name.Length == 0) { TempData["PosErr"] = L["The customer name is required."].Value; return RedirectToAction(nameof(Customer)); }
			var cust = await _ar.CreateCustomerAsync(PosCompanyId, name, null, null, null);   // assigns the AR control account (1102)
			if (!string.IsNullOrWhiteSpace(phone))
			{
				var t = await _db.Customers.FirstAsync(x => x.ID == cust.ID);
				t.Phone = phone.Trim(); await _db.SaveChangesAsync();   // persist the phone — the loyalty lookup key (decision ب)
			}
			return await AttachCustomer(cust.ID);
		}

		[HttpPost("customer/attach")][ValidateAntiForgeryToken]
		public async Task<IActionResult> AttachCustomer(int customerId)
		{
			var c = Ctx(); if (c == null) return RedirectToAction(nameof(Login));
			if (!_access.CanSell(c.Roles)) { TempData["PosErr"] = L["This role is not allowed to operate orders"].Value; return RedirectToAction(nameof(Customer)); }
			if (!await _pos.IsCapabilityEnabledAsync(c.BranchId, "CustomerIdentity"))
			{ TempData["PosErr"] = L["Customer identification is not enabled on this branch."].Value; return RedirectToAction(nameof(Customer)); }
			// fail-closed: never link a customer whose AR control account is missing/invalid
			if (!await CustomerControlAccountValidAsync(customerId))
			{ TempData["PosErr"] = L["This customer has no valid receivable control account and cannot be linked."].Value; return RedirectToAction(nameof(Customer)); }
			var (ok, err, oid) = await EnsureOrderAsync(c);
			if (!ok) { TempData["PosErr"] = err; return RedirectToAction(nameof(Lane)); }
			var (sok, serr) = await _posOrders.SetOrderCustomerAsync(PosCompanyId, oid, customerId);   // refuses a non-Open order (pre-pay only)
			if (!sok) { TempData["PosErr"] = serr; return RedirectToAction(nameof(Customer)); }
			TempData["PosMsg"] = L["The customer was linked to the order."].Value;
			return RedirectToAction(nameof(Customer));
		}

		// ==================== HM-4: PRICE CHECK (read-only — no order, no cart, no shift) ====================
		// Gated by the PriceCheck capability. Reuses the unified scan resolution (HM-2 fixed barcodes + HM-3 scale
		// barcodes) but calls GetPriceAsync — NEVER AddLineAsync. Requires a cashier login (HyperCtx via the controller
		// guard); a shift is NOT required because this writes nothing.
		[HttpGet("pricecheck")]
		public async Task<IActionResult> PriceCheck()
		{
			var c = Ctx(); if (c == null) return RedirectToAction(nameof(Login));
			ViewBag.Ctx = c;
			ViewBag.Enabled = await _pos.IsCapabilityEnabledAsync(c.BranchId, "PriceCheck");
			return View("~/Views/Hyper/PriceCheck.cshtml");
		}

		[HttpPost("pricecheck")][ValidateAntiForgeryToken]
		public async Task<IActionResult> PriceCheck(string barcode)
		{
			var c = Ctx(); if (c == null) return RedirectToAction(nameof(Login));
			ViewBag.Ctx = c; ViewBag.Enabled = true;
			if (!await _pos.IsCapabilityEnabledAsync(c.BranchId, "PriceCheck"))
			{ ViewBag.Enabled = false; ViewBag.Error = L["Price check is not enabled on this branch."].Value; return View("~/Views/Hyper/PriceCheck.cshtml"); }
			barcode = (barcode ?? "").Trim();
			if (barcode.Length == 0) return View("~/Views/Hyper/PriceCheck.cshtml");

			var bps = await _db.BranchPosSettings.AsNoTracking().FirstOrDefaultAsync(s => s.BranchId == c.BranchId);
			int? docCur = bps?.DefaultCurrencyId;
			int? branchList = bps?.DefaultPriceListId;

			int itemId = 0; int? uomId = null; bool weighted = false; int? scaleCode = null;
			var scaleCfg = new ScaleBarcodeConfig
			{
				Prefix = bps?.ScaleBarcodePrefix, ItemCodeLength = bps?.ScaleItemCodeLength ?? 0, ValueLength = bps?.ScaleValueLength ?? 0,
				ValueDecimals = bps?.ScaleValueDecimals ?? 0, ValueType = bps?.ScaleValueType ?? "Weight", CheckAlgo = bps?.ScaleCheckAlgo ?? "EanMod10",
			};
			if (ScaleBarcodeParser.MatchesPrefix(barcode, scaleCfg))
			{
				var pr = ScaleBarcodeParser.Parse(barcode, scaleCfg);
				if (!pr.Ok) { ViewBag.Error = L["The scale barcode format is invalid."].Value; return View("~/Views/Hyper/PriceCheck.cshtml"); }
				var wi = await _db.Items.AsNoTracking().Where(i => i.CompanyID == PosCompanyId && i.ScaleCode == pr.ItemCode)
					.Select(i => new { i.ID, i.IsWeighted, i.IsActive, i.BaseUoMId, i.ScaleCode }).FirstOrDefaultAsync();
				if (wi == null || !wi.IsActive) { ViewBag.Error = L["No item is linked to this scale code."].Value; return View("~/Views/Hyper/PriceCheck.cshtml"); }
				itemId = wi.ID; uomId = wi.BaseUoMId; weighted = wi.IsWeighted; scaleCode = wi.ScaleCode;
			}
			else
			{
				var m = await _db.Items.AsNoTracking().Where(i => i.CompanyID == PosCompanyId && i.IsActive && i.Barcode == barcode)
					.Select(i => new { i.ID, i.BaseUoMId, i.IsWeighted, i.ScaleCode }).FirstOrDefaultAsync();
				if (m == null)
				{
					var sb = await _db.ItemBarcodes.AsNoTracking().Where(b => b.Barcode == barcode && _db.Items.Any(i => i.ID == b.ItemId && i.CompanyID == PosCompanyId && i.IsActive))
						.Select(b => new { b.ItemId, b.UoMId }).FirstOrDefaultAsync();
					if (sb == null) { ViewBag.Error = L["No item matches this barcode."].Value; return View("~/Views/Hyper/PriceCheck.cshtml"); }
					itemId = sb.ItemId; uomId = sb.UoMId;
					var it = await _db.Items.AsNoTracking().Where(i => i.ID == sb.ItemId).Select(i => new { i.IsWeighted, i.ScaleCode }).FirstOrDefaultAsync();
					weighted = it?.IsWeighted ?? false; scaleCode = it?.ScaleCode;
				}
				else { itemId = m.ID; uomId = m.BaseUoMId; weighted = m.IsWeighted; scaleCode = m.ScaleCode; }
			}

			// qty = 1 → the unit price (per-kg for a weighted item). NO order, NO AddLine.
			var price = await _pricing.GetPriceAsync(PosCompanyId, itemId, null, null, docCur, 1m, DateTime.Today, branchList, uomId);
			var item = await _db.Items.AsNoTracking().Where(i => i.ID == itemId).Select(i => new { i.Name, i.NameEn }).FirstOrDefaultAsync();
			var uom = uomId != null ? await _db.UnitsOfMeasure.AsNoTracking().Where(u => u.ID == uomId).Select(u => new { u.Code, u.Name }).FirstOrDefaultAsync() : null;
			int dp = await _rounding.DecimalsAsync(PosCompanyId, price.CurrencyId, c.BranchId);
			ViewBag.Result = new CrossBuy.BL.PriceCheckResult
			{
				Name = item?.Name, Price = price.UnitPrice, Dp = dp, Unit = uom?.Name,
				Weighted = weighted, ScaleCode = scaleCode, Source = price.Source
			};
			return View("~/Views/Hyper/PriceCheck.cshtml");
		}
	}
}
