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
		private readonly CrossDbContext _db;
		private readonly IStringLocalizer<CrossBuy.SharedResources> L;
		public HyperPosController(SignInManager<Users> signIn, UserManager<Users> users, IPosAccessService access,
			IPosSetupService pos, CrossDbContext db, IStringLocalizer<CrossBuy.SharedResources> localizer)
		{ _signIn = signIn; _users = users; _access = access; _pos = pos; _db = db; L = localizer; }

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
	}
}
