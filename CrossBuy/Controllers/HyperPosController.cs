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
		private const int PosCompanyId = 1;
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
			IPosSetupService pos, IPosOrderService posOrders, CrossBuy.BL.ICurrencyRounding rounding, CrossBuy.BL.IPricingService pricing, CrossBuy.BL.IReceivableService receivables, CrossDbContext db, IStringLocalizer<CrossBuy.SharedResources> localizer)
		{ _signIn = signIn; _users = users; _access = access; _pos = pos; _posOrders = posOrders; _rounding = rounding; _pricing = pricing; _ar = receivables; _db = db; L = localizer; }

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
		public async Task<IActionResult> Lane()
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
			var (aok, aerr) = await _posOrders.AddLineAsync(PosCompanyId, oid, match.ID, 1m, null, match.UoMId);   // HM-2: pass the resolved unit
			if (!aok) TempData["PosErr"] = aerr;
			return RedirectToAction(nameof(Lane));
		}

		[HttpPost("line/qty")][ValidateAntiForgeryToken]
		public async Task<IActionResult> SetQty(int lineId, decimal qty)
		{
			var c = Ctx(); if (c?.TerminalId == null || c.OrderId == null) return RedirectToAction(nameof(Lane));
			if (!_access.CanSell(c.Roles)) { TempData["PosErr"] = L["This role is not allowed to operate orders"].Value; return RedirectToAction(nameof(Lane)); }
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

		[HttpPost("pay")][ValidateAntiForgeryToken]
		public async Task<IActionResult> Pay()
		{
			var c = Ctx(); if (c?.TerminalId == null) return RedirectToAction(nameof(Login));
			// HM-1: an OPEN shift is required to take payment — hyper lane only (restaurant path untouched).
			if (c.ShiftId == null || await _pos.GetOpenShiftAsync(c.TerminalId.Value) == null) { TempData["PosErr"] = L["Open a shift before taking payment."].Value; return RedirectToAction(nameof(Lane)); }
			if (!_access.CanSell(c.Roles)) { TempData["PosErr"] = L["This role is not allowed to operate orders"].Value; return RedirectToAction(nameof(Lane)); }
			if (c.OrderId == null) { TempData["PosErr"] = L["The cart is empty."].Value; return RedirectToAction(nameof(Lane)); }
			var (ok, err, invId) = await _posOrders.PayAsync(PosCompanyId, c.OrderId.Value, "Cash", null);
			if (!ok) { TempData["PosErr"] = err; return RedirectToAction(nameof(Lane)); }
			c.OrderId = null; SetCtx(c);
			TempData["PosMsg"] = L["Paid — invoice #{0} created.", invId ?? 0].Value;
			return RedirectToAction(nameof(Lane));
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
		public async Task<IActionResult> StampInvoiceCustomer(int invoiceId, string name, string? taxNo)
		{
			var c = Ctx(); if (c == null) return RedirectToAction(nameof(Login));
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
