using CrossBuy.BL;
using CrossBuy.Models;
using CrossBuy.Models.Context;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using QRCoder;

namespace CrossBuy.Controllers
{
	// Operations platform (POS) — SETUP only (activity type + capabilities, sales warehouse, dining areas,
	// kitchen stations, table floor plan + QR). No cashier here — that's a later phase. Configuration only: no GL/stock.
	[SessionValidation]
	public class PosController : Controller
	{
		private const int DefaultCompanyId = 1;
		private readonly IPosSetupService _pos;
		private readonly IWarehouseService _warehouses;
		private readonly CrossDbContext _context;
		private readonly IPosOrderService _orders;       // POS-A: shared floor builder (reused from the cashier)
		private readonly IReceivableService _receivables; // POS-A: shared customer search/add (reused from the cashier POS-4e)
		private readonly IStringLocalizer<CrossBuy.SharedResources> L;
		public PosController(IPosSetupService pos, IWarehouseService warehouses, CrossDbContext context, IPosOrderService orders, IReceivableService receivables, IStringLocalizer<CrossBuy.SharedResources> localizer)
		{ _pos = pos; _warehouses = warehouses; _context = context; _orders = orders; _receivables = receivables; L = localizer; }

		// POS-A2: every restaurant-setup screen shows the unified "Restaurant" sidebar (not the generic Admin menu).
		public override void OnActionExecuting(Microsoft.AspNetCore.Mvc.Filters.ActionExecutingContext context)
		{
			ViewBag.SidebarMenu = CrossBuy.Models.Menu.MainMenu.Restaurant();
			base.OnActionExecuting(context);
		}

		public static readonly (string key, string ar, string en)[] Capabilities = new[]
		{
			("Tables", "طاولات", "Tables"), ("Kitchen", "مطبخ", "Kitchen"), ("Manufacturing", "تصنيع/وصفات", "Manufacturing"),
			("Modifiers", "إضافات (Modifiers)", "Modifiers"), ("QrOrder", "طلب عبر QR", "QR ordering"),
			("Barcode", "باركود", "Barcode"), ("Weight", "وزن", "Weight"),
		};

		// Company first, then its branches. Branch data carries its own CompanyID (this DB's branches live under
		// several companies), so the admin picks the company, then a branch scoped to it.
		private async Task PopulateBranchesAsync(int? companyId, int? branchId)
		{
			// POS always needs a branch, so only offer companies that actually have one (avoids picking an empty company)
			var branchCompanyIds = await _context.Branches.AsNoTracking().Select(b => b.CompanyID).Distinct().ToListAsync();
			ViewBag.Companies = await _context.Companies.AsNoTracking().Where(c => branchCompanyIds.Contains(c.CompanyID)).OrderBy(c => c.CompanyName).ToListAsync();
			ViewBag.FilterCompanyId = companyId;
			ViewBag.Branches = companyId != null
				? await _context.Branches.AsNoTracking().Where(b => b.CompanyID == companyId).OrderBy(b => b.Name).ToListAsync()
				: new List<CrossBuy.Models.Context.Admin.Branch>();
			ViewBag.FilterBranchId = branchId;
		}

		// ---------------- Main dashboard (system landing) — live POS stats, no fabricated data ----------------
		[HttpGet]
		public async Task<IActionResult> Dashboard(string? activity = null)
		{
			int companyId = DefaultCompanyId;
			var today = DateTime.Today;
			var monthStart = new DateTime(today.Year, today.Month, 1);
			var yearStart = new DateTime(today.Year, 1, 1);
			var since = today.AddDays(-6);

			// HM-10 slice B: OPTIONAL activity filter. Default (null) = ALL company-1 orders — the current behavior, ZERO
			// regression. activity=="restaurant" EXCLUDES hyper-activity branches so this restaurant dashboard shows only
			// restaurant sales (and ByType — Dine-in/Takeaway/Delivery — becomes accurate, no hyper Takeaway leaking in).
			var hyperIds = await _context.Branches.AsNoTracking().Where(b => b.CompanyID == companyId && b.ActivityPresetCode == "Hyper").Select(b => b.ID).ToListAsync();
			bool restaurantOnly = activity == "restaurant";
			ViewBag.Activity = activity;

			var paid = await _context.PosOrders.AsNoTracking()
				.Where(o => o.CompanyId == companyId && o.Status == "Paid" && (!restaurantOnly || !hyperIds.Contains(o.BranchId)))
				.Select(o => new { o.GrandTotal, o.OrderType, o.ClosedAt, o.OpenedAt, o.ReceiptNo, o.Status })
				.ToListAsync();
			DateTime When(DateTime? closed, DateTime opened) => (closed ?? opened);

			var dto = new PosDashboardDto { Year = today.Year };
			dto.SalesToday = paid.Where(o => When(o.ClosedAt, o.OpenedAt).Date == today).Sum(o => o.GrandTotal);
			dto.SalesMonth = paid.Where(o => When(o.ClosedAt, o.OpenedAt).Date >= monthStart).Sum(o => o.GrandTotal);
			dto.SalesYtd = paid.Where(o => When(o.ClosedAt, o.OpenedAt).Date >= yearStart).Sum(o => o.GrandTotal);
			dto.OrdersToday = paid.Count(o => When(o.ClosedAt, o.OpenedAt).Date == today);
			dto.OrdersMonth = paid.Count(o => When(o.ClosedAt, o.OpenedAt).Date >= monthStart);

			// last 7 days sales
			for (int i = 0; i < 7; i++)
			{
				var d = since.AddDays(i);
				dto.Last7.Add(new PosDayPoint { Date = d, Total = paid.Where(o => When(o.ClosedAt, o.OpenedAt).Date == d).Sum(o => o.GrandTotal) });
			}

			// orders by type (this month)
			var monthPaid = paid.Where(o => When(o.ClosedAt, o.OpenedAt).Date >= monthStart).ToList();
			var maxTypeAmt = new[] { "Dine-in", "Takeaway", "Delivery" }.Select(t => monthPaid.Where(o => o.OrderType == t).Sum(o => o.GrandTotal)).DefaultIfEmpty(0m).Max();
			dto.ByType = new[] { "Dine-in", "Takeaway", "Delivery" }
				.Select(t => { var amt = monthPaid.Where(o => o.OrderType == t).Sum(o => o.GrandTotal); return new PosTypeSlice { Type = t, Count = monthPaid.Count(o => o.OrderType == t), Amount = amt, Pct = maxTypeAmt > 0m ? (int)Math.Round(100m * amt / maxTypeAmt) : 0 }; })
				.Where(x => x.Count > 0).ToList();

			// live operational figures
			var openOrders = await _context.PosOrders.AsNoTracking().Where(o => o.CompanyId == companyId && o.Status == "Open" && !o.IsHeld && (!restaurantOnly || !hyperIds.Contains(o.BranchId))).ToListAsync();
			dto.OpenOrders = openOrders.Count;
			dto.OccupiedTables = openOrders.Where(o => o.TableId != null).Select(o => o.TableId).Distinct().Count();
			var branchIds = await _context.Branches.AsNoTracking().Where(b => b.CompanyID == companyId && (!restaurantOnly || b.ActivityPresetCode != "Hyper")).Select(b => b.ID).ToListAsync();
			var terminalIds = await _context.PosTerminals.AsNoTracking().Where(t => branchIds.Contains(t.BranchId)).Select(t => t.ID).ToListAsync();
			dto.Terminals = terminalIds.Count;
			dto.OpenShifts = await _context.PosShifts.AsNoTracking().CountAsync(s => terminalIds.Contains(s.TerminalId) && s.Status == "Open");

			// recent paid orders
			dto.Recent = paid.OrderByDescending(o => When(o.ClosedAt, o.OpenedAt)).Take(8)
				.Select(o => new PosRecentOrder { ReceiptNo = o.ReceiptNo, Type = o.OrderType, Total = o.GrandTotal, When = When(o.ClosedAt, o.OpenedAt), Status = o.Status }).ToList();

			// top items this month (from order lines of paid orders)
			var paidIds = await _context.PosOrders.AsNoTracking()
				.Where(o => o.CompanyId == companyId && o.Status == "Paid" && (o.ClosedAt ?? o.OpenedAt) >= monthStart && (!restaurantOnly || !hyperIds.Contains(o.BranchId)))
				.Select(o => o.ID).ToListAsync();
			if (paidIds.Count > 0)
			{
				var lines = await _context.PosOrderLines.AsNoTracking().Where(l => paidIds.Contains(l.OrderId)).ToListAsync();
				var grouped = lines.GroupBy(l => l.ItemName)
					.Select(g => new { Name = g.Key, Qty = g.Sum(x => x.Qty), Amount = g.Sum(x => x.LineTotal) })
					.OrderByDescending(x => x.Amount).Take(6).ToList();
				var maxAmt = grouped.Select(x => x.Amount).DefaultIfEmpty(0m).Max();
				dto.TopItems = grouped.Select(x => new PosTopItem { Name = x.Name, Qty = x.Qty, Amount = x.Amount, Pct = maxAmt > 0m ? (int)Math.Round(100m * x.Amount / maxAmt) : 0 }).ToList();
			}

			return View(dto);
		}

		// ---------------- Setup hub: activity type + capabilities + sales warehouse ----------------
		[HttpGet]
		public async Task<IActionResult> Setup(int? companyId, int? branchId)
		{
			if (companyId == null && branchId != null) companyId = await _context.Branches.Where(b => b.ID == branchId).Select(b => (int?)b.CompanyID).FirstOrDefaultAsync();
			await PopulateBranchesAsync(companyId, branchId);
			ViewBag.Presets = await _pos.GetPresetsAsync();
			ViewBag.CapabilityDefs = Capabilities;
			if (branchId != null)
			{
				var branch = await _context.Branches.AsNoTracking().FirstOrDefaultAsync(b => b.ID == branchId);
				ViewBag.Branch = branch;
				ViewBag.Caps = await _pos.GetCapabilitiesAsync(branchId.Value);
				ViewBag.PosSetting = await _pos.GetPosSettingAsync(branchId.Value);
				ViewBag.Warehouses = await _warehouses.GetWarehousesAsync(DefaultCompanyId);
				ViewBag.PriceLists = await _context.PriceLists.AsNoTracking().Where(p => p.CompanyID == DefaultCompanyId).OrderBy(p => p.Name).ToListAsync();
				ViewBag.Currencies = await _context.Currencies.AsNoTracking().OrderBy(c => c.Code).ToListAsync();
			}
			return View();
		}

		[HttpPost][ValidateAntiForgeryToken]
		public async Task<IActionResult> ApplyPreset(int branchId, string presetCode)
		{
			var (ok, err) = await _pos.ApplyPresetAsync(branchId, presetCode);
			TempData[ok ? "PosMsg" : "PosErr"] = ok ? L["Activity type and its default capabilities have been enabled"].Value : err;
			return RedirectToAction(nameof(Setup), new { branchId });
		}

		[HttpPost][ValidateAntiForgeryToken]
		public async Task<IActionResult> SaveCapabilities(int branchId, string[] enabled)
		{
			var set = new HashSet<string>(enabled ?? Array.Empty<string>());
			var caps = Capabilities.ToDictionary(c => c.key, c => set.Contains(c.key));
			var (ok, err) = await _pos.SaveCapabilitiesAsync(branchId, caps);
			TempData[ok ? "PosMsg" : "PosErr"] = ok ? L["Capabilities saved"].Value : err;
			return RedirectToAction(nameof(Setup), new { branchId });
		}

		[HttpPost][ValidateAntiForgeryToken]
		public async Task<IActionResult> SavePosSetting(int branchId, int? defaultSalesWarehouseId, int? defaultPriceListId, decimal? serviceChargePct, int? defaultCurrencyId)
		{
			var (ok, err) = await _pos.SavePosSettingAsync(branchId, defaultSalesWarehouseId, defaultPriceListId, serviceChargePct, defaultCurrencyId);
			TempData[ok ? "PosMsg" : "PosErr"] = ok ? L["Sales settings saved"].Value : err;
			return RedirectToAction(nameof(Setup), new { branchId });
		}

		// ---------------- Dining areas + kitchen stations ----------------
		[HttpGet]
		public async Task<IActionResult> Areas(int? companyId, int? branchId)
		{
			if (companyId == null && branchId != null) companyId = await _context.Branches.Where(b => b.ID == branchId).Select(b => (int?)b.CompanyID).FirstOrDefaultAsync();
			await PopulateBranchesAsync(companyId, branchId);
			if (branchId != null)
			{
				ViewBag.Areas = await _pos.GetDiningAreasAsync(branchId.Value);
				ViewBag.Stations = await _pos.GetStationsAsync(branchId.Value);
			}
			return View();
		}

		[HttpPost][ValidateAntiForgeryToken]
		public async Task<IActionResult> SaveDiningArea(int branchId, int id, string code, string name, int sort, bool isActive = true, string? nameEn = null)
		{
			var (ok, err) = await _pos.SaveDiningAreaAsync(branchId, id, code, name, sort, isActive, nameEn);
			TempData[ok ? "PosMsg" : "PosErr"] = ok ? L["Dining area saved"].Value : err;
			return RedirectToAction(nameof(Areas), new { branchId });
		}

		[HttpPost][ValidateAntiForgeryToken]
		public async Task<IActionResult> DeleteDiningArea(int id, int branchId)
		{
			var (ok, err) = await _pos.DeleteDiningAreaAsync(id);
			TempData[ok ? "PosMsg" : "PosErr"] = ok ? L["Dining area deleted"].Value : err;
			return RedirectToAction(nameof(Areas), new { branchId });
		}

		[HttpPost][ValidateAntiForgeryToken]
		public async Task<IActionResult> SaveStation(int branchId, int id, string code, string name, string? nameEn, string stationType, bool isActive = true)
		{
			var (ok, err) = await _pos.SaveStationAsync(branchId, id, code, name, nameEn, stationType, isActive);
			TempData[ok ? "PosMsg" : "PosErr"] = ok ? L["Station saved"].Value : err;
			return RedirectToAction(nameof(Areas), new { branchId });
		}

		[HttpPost][ValidateAntiForgeryToken]
		public async Task<IActionResult> DeleteStation(int id, int branchId)
		{
			var (ok, err) = await _pos.DeleteStationAsync(id);
			TempData[ok ? "PosMsg" : "PosErr"] = ok ? L["Station deleted"].Value : err;
			return RedirectToAction(nameof(Areas), new { branchId });
		}

		// ---------------- POS-C2: delivery drivers ----------------
		[HttpGet]
		public async Task<IActionResult> Drivers(int? companyId, int? branchId)
		{
			if (companyId == null && branchId != null) companyId = await _context.Branches.Where(b => b.ID == branchId).Select(b => (int?)b.CompanyID).FirstOrDefaultAsync();
			await PopulateBranchesAsync(companyId, branchId);
			if (branchId != null) ViewBag.Drivers = await _pos.GetDriversAsync(branchId.Value);
			return View();
		}

		[HttpPost][ValidateAntiForgeryToken]
		public async Task<IActionResult> SaveDriver(int branchId, int id, string name, string? phone, bool isActive = true)
		{
			var (ok, err) = await _pos.SaveDriverAsync(branchId, id, name, phone ?? "", isActive);
			TempData[ok ? "PosMsg" : "PosErr"] = ok ? L["Driver saved"].Value : err;
			return RedirectToAction(nameof(Drivers), new { branchId });
		}

		[HttpPost][ValidateAntiForgeryToken]
		public async Task<IActionResult> DeleteDriver(int branchId, int id)
		{
			var (ok, err) = await _pos.DeleteDriverAsync(id);
			TempData[ok ? "PosMsg" : "PosErr"] = ok ? L["Driver deleted"].Value : err;
			return RedirectToAction(nameof(Drivers), new { branchId });
		}

		// ---------------- POS-A1: delivery zones (name + fee, per-branch) ----------------
		[HttpGet]
		public async Task<IActionResult> DeliveryZones(int? companyId, int? branchId)
		{
			if (companyId == null && branchId != null) companyId = await _context.Branches.Where(b => b.ID == branchId).Select(b => (int?)b.CompanyID).FirstOrDefaultAsync();
			await PopulateBranchesAsync(companyId, branchId);
			if (branchId != null) ViewBag.Zones = await _pos.GetAllDeliveryZonesAsync(branchId.Value);
			return View();
		}

		[HttpPost][ValidateAntiForgeryToken]
		public async Task<IActionResult> SaveDeliveryZone(int branchId, int id, string name, string? nameEn, decimal fee, bool isActive = true)
		{
			var (ok, err) = await _pos.SaveDeliveryZoneAsync(branchId, id, name, nameEn, fee, isActive);
			TempData[ok ? "PosMsg" : "PosErr"] = ok ? L["Delivery zone saved"].Value : err;
			return RedirectToAction(nameof(DeliveryZones), new { branchId });
		}

		[HttpPost][ValidateAntiForgeryToken]
		public async Task<IActionResult> DeleteDeliveryZone(int branchId, int id)
		{
			var (ok, err) = await _pos.DeleteDeliveryZoneAsync(id);
			TempData[ok ? "PosMsg" : "PosErr"] = ok ? L["Delivery zone deleted"].Value : err;
			return RedirectToAction(nameof(DeliveryZones), new { branchId });
		}

		// ---------------- BIS-1: per-branch item sourcing (setup only, no GL/stock) ----------------
		[HttpGet]
		public async Task<IActionResult> ItemSourcing(int? companyId, int? branchId)
		{
			if (companyId == null && branchId != null) companyId = await _context.Branches.Where(b => b.ID == branchId).Select(b => (int?)b.CompanyID).FirstOrDefaultAsync();
			await PopulateBranchesAsync(companyId, branchId);
			if (branchId != null)
			{
				ViewBag.Rows = await _pos.GetBranchSourcingAsync(DefaultCompanyId, branchId.Value);
				// semi-finished candidates + source-branch options (POS runs under company 1; branches for source = all branches)
				ViewBag.SemiItems = await _context.Items.AsNoTracking().Where(i => i.CompanyID == DefaultCompanyId && i.IsActive)
					.OrderBy(i => i.ItemCode).Select(i => new PosSemiItem { ID = i.ID, ItemCode = i.ItemCode, Name = i.Name }).ToListAsync();
				ViewBag.AllBranches = await _context.Branches.AsNoTracking().OrderBy(b => b.Name).Select(b => new PosBranchOption { ID = b.ID, Name = b.Name }).ToListAsync();
			}
			return View();
		}

		[HttpPost][ValidateAntiForgeryToken]
		public async Task<IActionResult> SaveItemSourcing(int branchId, int itemId, string method, int? sourceBranchId, int? semiFinishedItemId, string? transferTiming)
		{
			var (ok, err) = await _pos.SaveBranchItemSourcingAsync(DefaultCompanyId, branchId, itemId, method, sourceBranchId, semiFinishedItemId, transferTiming);
			TempData[ok ? "PosMsg" : "PosErr"] = ok ? L["Sourcing saved"].Value : err;
			return RedirectToAction(nameof(ItemSourcing), new { branchId });
		}

		// BIS-4: read-only overview of every branch's item sourcing (for review). Writes nothing.
		[HttpGet]
		public async Task<IActionResult> SourcingOverview()
		{
			ViewBag.Overview = await _pos.GetSourcingOverviewAsync(DefaultCompanyId);
			return View();
		}

		// POS-9e: manager review of sync conflicts noticed while replaying offline orders (non-blocking record only).
		[HttpGet]
		public async Task<IActionResult> SyncConflicts(bool includeAcknowledged = false)
		{
			ViewBag.IncludeAck = includeAcknowledged;
			ViewBag.Conflicts = await _pos.GetSyncConflictsAsync(DefaultCompanyId, includeAcknowledged);
			return View();
		}

		[HttpPost][ValidateAntiForgeryToken]
		public async Task<IActionResult> AckSyncConflict(int id, bool includeAcknowledged = false)
		{
			var (ok, err) = await _pos.AcknowledgeSyncConflictAsync(DefaultCompanyId, id, null);
			TempData[ok ? "PosMsg" : "PosErr"] = ok ? L["Conflict acknowledged"].Value : err;
			return RedirectToAction(nameof(SyncConflicts), new { includeAcknowledged });
		}

		// BIS-3 (method 2): Prepaid replenishment — transfer finished stock from the source branch. Reuses TransferAsync.
		[HttpPost][ValidateAntiForgeryToken]
		public async Task<IActionResult> PrepareFinished(int branchId, int itemId, decimal qty)
		{
			var (ok, err, _) = await _orders.ReplenishFinishedFromBranchAsync(DefaultCompanyId, branchId, itemId, qty, DateTime.Today, null);
			TempData[ok ? "PosMsg" : "PosErr"] = ok ? L["Stock transferred from the source branch"].Value : err;
			return RedirectToAction(nameof(ItemSourcing), new { branchId });
		}

		// BIS-3 (method 3): transfer the semi-finished + complete a WO here. Reuses TransferAsync + ManufService.
		[HttpPost][ValidateAntiForgeryToken]
		public async Task<IActionResult> PrepareSemi(int branchId, int itemId, decimal qty, decimal labor = 0, decimal overhead = 0)
		{
			var (ok, err, _) = await _orders.PrepareSemiFinishedAsync(DefaultCompanyId, branchId, itemId, qty, labor, overhead, DateTime.Today, null);
			TempData[ok ? "PosMsg" : "PosErr"] = ok ? L["Semi transferred and finished produced"].Value : err;
			return RedirectToAction(nameof(ItemSourcing), new { branchId });
		}

		// ---------------- POS-B1: table reservations ----------------
		[HttpGet]
		public async Task<IActionResult> Reservations(int? companyId, int? branchId)
		{
			if (companyId == null && branchId != null) companyId = await _context.Branches.Where(b => b.ID == branchId).Select(b => (int?)b.CompanyID).FirstOrDefaultAsync();
			await PopulateBranchesAsync(companyId, branchId);
			// the POS operates under DefaultCompanyId (1) regardless of the branch's admin company — same as the cashier (PosCompanyId)
			if (branchId != null)
				ViewBag.Reservations = await _pos.GetReservationsAsync(DefaultCompanyId, branchId.Value);
			return View();
		}

		[HttpPost][ValidateAntiForgeryToken]
		public async Task<IActionResult> SaveReservation(int branchId, int id, int tableId, int? customerId, string guestName, string? guestPhone, DateTime reservedAt, int durationMinutes, int partySize, string? notes)
		{
			var (ok, err) = await _pos.SaveReservationAsync(DefaultCompanyId, branchId, id, tableId, customerId, guestName, guestPhone ?? "", reservedAt, durationMinutes, partySize, notes);
			TempData[ok ? "PosMsg" : "PosErr"] = ok ? L["Reservation saved"].Value : err;
			return RedirectToAction(nameof(Reservations), new { branchId });
		}

		[HttpPost][ValidateAntiForgeryToken]
		public async Task<IActionResult> SetReservationStatus(int branchId, int id, string status)
		{
			var (ok, err) = await _pos.SetReservationStatusAsync(DefaultCompanyId, id, status);
			TempData[ok ? "PosMsg" : "PosErr"] = ok ? L["Reservation updated"].Value : err;
			return RedirectToAction(nameof(Reservations), new { branchId });
		}

		// POS-B: reservations as a FullCalendar event feed (view + book surface). Naive-local ISO (no Z/offset) so the
		// calendar shows the same wall-clock the reservation was entered with. Colours track status.
		[HttpGet]
		public async Task<IActionResult> ReservationEvents(int branchId, int? tableId)
		{
			var evs = await _pos.GetReservationEventsAsync(DefaultCompanyId, branchId, tableId);
			var isAr = (HttpContext.Items["Culture"]?.ToString() == "ar");
			var walkIn = isAr ? "عميل نقدي" : "Walk-in";
			var data = evs.Select(e =>
			{
				var who = string.IsNullOrWhiteSpace(e.CustomerName) ? e.GuestName : e.CustomerName;
				if (who == "عميل نقدي (كاشير)") who = walkIn;
				// Metronic themed event classes (fc-event-*) — the SAME the FullCalendar demo uses.
				var cls = e.Status switch { "Booked" => "fc-event-primary", "Arrived" => "fc-event-success", "Cancelled" => "fc-event-secondary", "NoShow" => "fc-event-danger", _ => "fc-event-primary" };
				return new
				{
					id = e.Id,
					title = e.TableCode + " · " + who + (e.PartySize > 0 ? " (" + e.PartySize + ")" : ""),
					start = e.Start.ToString("yyyy-MM-ddTHH:mm:ss"),
					end = e.Start.AddMinutes(e.DurationMinutes).ToString("yyyy-MM-ddTHH:mm:ss"),
					className = cls,
					extendedProps = new { status = e.Status, tableId = e.TableId, tableCode = e.TableCode, customer = who, party = e.PartySize }
				};
			});
			return Json(data);
		}

		// POS-A: floor board data for the reservations screen — the SAME builder the cashier uses (3-way status).
		[HttpGet]
		public async Task<IActionResult> FloorData(int branchId)
			=> Json(new { ok = true, halls = await _orders.GetFloorAsync(DefaultCompanyId, branchId) });

		// POS-A: customer search + quick-add — the SAME receivables service the cashier uses (POS-4e). New customer persists in the admin.
		[HttpGet]
		public async Task<IActionResult> CustomerSearch(string? q)
		{
			var (rows, _) = await _receivables.SearchCustomersAsync(DefaultCompanyId, q, true, 1, 15);
			return Json(new { ok = true, customers = rows.Select(x => new { id = x.ID, name = x.Name, phone = x.Phone }) });
		}

		[HttpPost][ValidateAntiForgeryToken]
		public async Task<IActionResult> CustomerQuickAdd(string name, string? phone)
		{
			if (string.IsNullOrWhiteSpace(name)) return Json(new { ok = false, error = L["Enter the customer name"].Value });
			var cust = await _receivables.CreateCustomerAsync(DefaultCompanyId, name.Trim(), null, null, null);
			if (!string.IsNullOrWhiteSpace(phone)) { cust.Phone = phone.Trim(); await _context.SaveChangesAsync(); }
			return Json(new { ok = true, customer = new { id = cust.ID, name = cust.Name, phone = cust.Phone } });
		}

		// ---------------- Floor plan (tables) ----------------
		[HttpGet]
		public async Task<IActionResult> FloorPlan(int? companyId, int? branchId, int? areaId)
		{
			if (companyId == null && branchId != null) companyId = await _context.Branches.Where(b => b.ID == branchId).Select(b => (int?)b.CompanyID).FirstOrDefaultAsync();
			await PopulateBranchesAsync(companyId, branchId);
			if (branchId != null)
			{
				var areas = await _pos.GetDiningAreasAsync(branchId.Value);
				ViewBag.Areas = areas;
				var area = areaId != null ? areas.FirstOrDefault(a => a.ID == areaId) : areas.FirstOrDefault();
				ViewBag.Area = area;
				ViewBag.Tables = area != null ? await _pos.GetTablesAsync(area.ID) : new List<CrossBuy.Models.Context.Pos.RestaurantTable>();
			}
			return View();
		}

		[HttpPost][ValidateAntiForgeryToken]
		public async Task<IActionResult> SaveTable(int diningAreaId, int branchId, int id, string code, int seats, decimal x, decimal y, decimal w, decimal h, string shape, bool isActive = true)
		{
			var (ok, err) = await _pos.SaveTableAsync(diningAreaId, id, code, seats, x, y, w, h, shape, isActive);
			TempData[ok ? "PosMsg" : "PosErr"] = ok ? L["Table saved"].Value : err;
			return RedirectToAction(nameof(FloorPlan), new { branchId, areaId = diningAreaId });
		}

		[HttpPost][ValidateAntiForgeryToken]
		public async Task<IActionResult> SaveLayout(int branchId, int areaId, string? layoutJson)
		{
			List<LayoutRow> rows;
			try { rows = System.Text.Json.JsonSerializer.Deserialize<List<LayoutRow>>(layoutJson ?? "[]", new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new(); } catch { rows = new(); }
			await _pos.SaveTableLayoutAsync(rows.Select(r => (r.Id, r.X, r.Y, r.W, r.H)).ToList());
			TempData["PosMsg"] = L["Table positions saved"].Value;
			return RedirectToAction(nameof(FloorPlan), new { branchId, areaId });
		}
		public class LayoutRow { public int Id { get; set; } public decimal X { get; set; } public decimal Y { get; set; } public decimal W { get; set; } public decimal H { get; set; } }

		[HttpPost][ValidateAntiForgeryToken]
		public async Task<IActionResult> DeleteTable(int id, int branchId, int areaId)
		{
			var (ok, err) = await _pos.DeleteTableAsync(id);
			TempData[ok ? "PosMsg" : "PosErr"] = ok ? L["Table deleted"].Value : err;
			return RedirectToAction(nameof(FloorPlan), new { branchId, areaId });
		}

		[HttpPost][ValidateAntiForgeryToken]
		public async Task<IActionResult> RegenerateQr(int id, int branchId, int areaId)
		{
			var (ok, err) = await _pos.RegenerateQrAsync(id);
			TempData[ok ? "PosMsg" : "PosErr"] = ok ? L["QR code regenerated"].Value : err;
			return RedirectToAction(nameof(FloorPlan), new { branchId, areaId });
		}

		// QR image generated ON DEMAND from the stored opaque token (no image stored). Reserved URL: /t/{token}.
		[HttpGet]
		public async Task<IActionResult> TableQr(int id)
		{
			var t = await _pos.GetTableAsync(id);
			if (t == null || string.IsNullOrEmpty(t.QrToken)) return NotFound();
			var url = $"{Request.Scheme}://{Request.Host}/t/{t.QrToken}";
			using var gen = new QRCodeGenerator();
			using var data = gen.CreateQrCode(url, QRCodeGenerator.ECCLevel.Q);
			var png = new PngByteQRCode(data).GetGraphic(10);
			return File(png, "image/png");
		}

		// ---------------- Cashier setup (RC-1): quick-touch menu ----------------
		[HttpGet]
		public async Task<IActionResult> QuickMenu(int? companyId, int? branchId)
		{
			if (companyId == null && branchId != null) companyId = await _context.Branches.Where(b => b.ID == branchId).Select(b => (int?)b.CompanyID).FirstOrDefaultAsync();
			await PopulateBranchesAsync(companyId, branchId);
			if (branchId != null)
			{
				ViewBag.Groups = await _pos.GetMenuGroupsAsync(branchId.Value);
				ViewBag.Stations = await _pos.GetStationsAsync(branchId.Value);   // RC-3e: for the per-tab station dropdown
				ViewBag.QuickItems = await _pos.GetQuickItemsAsync(branchId.Value);
				// only the items already used as buttons are loaded for display (NOT the whole catalog — picking is via the search popup)
				var usedIds = ((List<CrossBuy.Models.Context.Pos.PosQuickItem>)ViewBag.QuickItems).Select(q => q.ItemId).ToList();
				ViewBag.ItemsById = await _context.Items.AsNoTracking().Where(i => usedIds.Contains(i.ID)).ToDictionaryAsync(i => i.ID, i => i);
				ViewBag.Categories = await _context.ItemCategories.AsNoTracking().Where(c => c.CompanyID == DefaultCompanyId && c.IsActive).OrderBy(c => c.Kind).ThenBy(c => c.Code).ToListAsync();
			}
			return View();
		}

		// server-side item search for the picker popup — data-table style, paged; filters: group / category(+its groups) / barcode / name
		[HttpGet]
		public async Task<IActionResult> ItemSearch(string? term, string? barcode, int? categoryId, int? groupId, int page = 1, int pageSize = 10)
		{
			var q = _context.Items.AsNoTracking().Where(i => i.CompanyID == DefaultCompanyId && i.ItemType == "Stockable");
			if (groupId.HasValue && groupId.Value > 0)
				q = q.Where(i => i.ItemCategoryId == groupId.Value);
			else if (categoryId.HasValue && categoryId.Value > 0)
			{
				var childIds = await _context.ItemCategories.Where(c => c.CompanyID == DefaultCompanyId && c.ParentId == categoryId.Value).Select(c => c.ID).ToListAsync();
				childIds.Add(categoryId.Value);
				q = q.Where(i => childIds.Contains(i.ItemCategoryId));
			}
			var bc = (barcode ?? "").Trim();
			if (bc.Length > 0) q = q.Where(i => i.Barcode == bc);   // exact barcode (for scan/Enter add)
			var t = (term ?? "").Trim();
			if (t.Length > 0) q = q.Where(i => i.ItemCode.Contains(t) || i.Name.Contains(t) || (i.NameEn != null && i.NameEn.Contains(t)) || (i.QuickCode != null && i.QuickCode.Contains(t)));
			if (page < 1) page = 1; if (pageSize < 1 || pageSize > 100) pageSize = 10;
			var total = await q.CountAsync();
			var pageItems = await q.OrderBy(i => i.ItemCode).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync();
			// resolve category/group/uom names in memory
			var cats = await _context.ItemCategories.AsNoTracking().Where(c => c.CompanyID == DefaultCompanyId).ToDictionaryAsync(c => c.ID, c => c);
            var uoms = await _context.UnitsOfMeasure.AsNoTracking().Where(u => u.CompanyID == DefaultCompanyId).ToDictionaryAsync(u => u.ID, u => u.Name);
			var isAr = Request.HttpContext.Items["Culture"]?.ToString() == "ar";
			var rows = pageItems.Select(i =>
			{
				cats.TryGetValue(i.ItemCategoryId, out var leaf);
				string cat = "—", grp = "—";
				if (leaf != null)
				{
					if (leaf.Kind == "Group") { grp = leaf.Name; if (leaf.ParentId != null && cats.TryGetValue(leaf.ParentId.Value, out var par)) cat = par.Name; }
					else cat = leaf.Name;
				}
				return new
				{
					id = i.ID, code = i.ItemCode, name = i.Name, nameEn = i.NameEn, barcode = i.Barcode,
					category = cat, group = grp, uom = uoms.TryGetValue(i.BaseUoMId, out var un) ? un : "—",
					price = i.SalesPrice, image = i.ImagePath, active = i.IsActive
				};
			}).ToList();
			return Json(new { total, page, pageSize, pages = (int)Math.Ceiling(total / (double)pageSize), rows });
		}

		[HttpPost][ValidateAntiForgeryToken]
		public async Task<IActionResult> AddQuickItemJson(int branchId, int? groupId, int itemId)
		{
			var (ok, err) = await _pos.AddQuickItemAsync(branchId, groupId, itemId);
			return Json(new { ok, error = err });
		}

		// ==================== MODIFIERS (setup only — reusable option groups, no GL/stock) ====================
		[HttpGet]
		public async Task<IActionResult> Modifiers(int? groupId)
		{
			ViewBag.Groups = await _pos.GetModifierGroupsAsync(DefaultCompanyId);
			if (groupId != null)
			{
				var g = await _pos.GetModifierGroupAsync(DefaultCompanyId, groupId.Value);
				if (g != null)
				{
					ViewBag.Group = g;
					ViewBag.Options = await _pos.GetOptionsAsync(DefaultCompanyId, groupId.Value);
					ViewBag.GroupItems = await _pos.GetGroupItemsAsync(DefaultCompanyId, groupId.Value);
				}
			}
			return View();
		}

		[HttpPost][ValidateAntiForgeryToken]
		public async Task<IActionResult> SaveModifierGroup(int id, string name, string? nameEn, string type, int minSelect, int maxSelect, int sort, bool isActive = true)
		{
			var (ok, err, gid) = await _pos.SaveModifierGroupAsync(DefaultCompanyId, new CrossBuy.Models.Context.Pos.ModifierGroup { ID = id, Name = name, NameEn = nameEn, Type = type, MinSelect = minSelect, MaxSelect = maxSelect, Sort = sort, IsActive = isActive });
			TempData[ok ? "PosMsg" : "PosErr"] = ok ? L["Group saved"].Value : err;
			return RedirectToAction(nameof(Modifiers), new { groupId = ok ? gid : id });
		}

		[HttpPost][ValidateAntiForgeryToken]
		public async Task<IActionResult> DeleteModifierGroup(int id)
		{
			var (ok, err) = await _pos.DeleteModifierGroupAsync(DefaultCompanyId, id);
			TempData[ok ? "PosMsg" : "PosErr"] = ok ? L["Group deleted"].Value : err;
			return RedirectToAction(nameof(Modifiers));
		}

		[HttpPost][ValidateAntiForgeryToken]
		public async Task<IActionResult> SaveModifierOption(int groupId, int id, string? name, string? nameEn, int linkedItemId, decimal qtyDeducted, decimal extraPrice, bool isDefault = false)
		{
			var (ok, err) = await _pos.SaveOptionAsync(DefaultCompanyId, groupId, id, name, nameEn, linkedItemId, qtyDeducted, extraPrice, isDefault);
			TempData[ok ? "PosMsg" : "PosErr"] = ok ? L["Option saved"].Value : err;
			return RedirectToAction(nameof(Modifiers), new { groupId });
		}

		[HttpPost][ValidateAntiForgeryToken]
		public async Task<IActionResult> DeleteModifierOption(int groupId, int id)
		{
			var (ok, err) = await _pos.DeleteOptionAsync(DefaultCompanyId, id);
			TempData[ok ? "PosMsg" : "PosErr"] = ok ? L["Option deleted"].Value : err;
			return RedirectToAction(nameof(Modifiers), new { groupId });
		}

		[HttpPost][ValidateAntiForgeryToken]
		public async Task<IActionResult> AttachModifierItemJson(int groupId, int itemId)
		{
			var (ok, err) = await _pos.AttachGroupToItemAsync(DefaultCompanyId, groupId, itemId);
			return Json(new { ok, error = err });
		}

		[HttpPost][ValidateAntiForgeryToken]
		public async Task<IActionResult> DetachModifierItem(int groupId, int linkId)
		{
			var (ok, err) = await _pos.DetachGroupFromItemAsync(DefaultCompanyId, linkId);
			TempData[ok ? "PosMsg" : "PosErr"] = ok ? L["Unlinked"].Value : err;
			return RedirectToAction(nameof(Modifiers), new { groupId });
		}

		// ==================== PAYMENT METHODS (setup only — method → GL account, no payment/GL now) ====================
		[HttpGet]
		public async Task<IActionResult> PaymentMethods(int? companyId, int? branchId)
		{
			if (companyId == null && branchId != null) companyId = await _context.Branches.Where(b => b.ID == branchId).Select(b => (int?)b.CompanyID).FirstOrDefaultAsync();
			await PopulateBranchesAsync(companyId, branchId);
			if (branchId != null)
			{
				ViewBag.Methods = await _pos.GetPaymentMethodsAsync(branchId.Value);
				var accs = await _context.Accounts.AsNoTracking().Where(a => a.CompanyID == DefaultCompanyId && a.IsPostable && a.IsActive).OrderBy(a => a.Code).ToListAsync();
				ViewBag.Accounts = accs;
				ViewBag.AccountsById = accs.ToDictionary(a => a.ID, a => a);
			}
			return View();
		}

		[HttpPost][ValidateAntiForgeryToken]
		public async Task<IActionResult> SavePaymentMethod(int branchId, int id, string paymentMethod, string? displayName, int? targetAccountId, int sort, bool isActive = true)
		{
			var (ok, err) = await _pos.SavePaymentMethodAsync(branchId, id, paymentMethod, displayName, targetAccountId, isActive, sort);
			TempData[ok ? "PosMsg" : "PosErr"] = ok ? L["Payment method saved"].Value : err;
			return RedirectToAction(nameof(PaymentMethods), new { branchId });
		}

		[HttpPost][ValidateAntiForgeryToken]
		public async Task<IActionResult> DeletePaymentMethod(int branchId, int id)
		{
			var (ok, err) = await _pos.DeletePaymentMethodAsync(branchId, id);
			TempData[ok ? "PosMsg" : "PosErr"] = ok ? L["Deleted"].Value : err;
			return RedirectToAction(nameof(PaymentMethods), new { branchId });
		}

		// ==================== CASHIER ROLES (setup only — assign branch employees to POS roles) ====================
		public static readonly (string key, string ar, string en)[] PosRoleDefs = new[]
		{
			("pos-waiter", "جرسون", "Waiter"), ("pos-kitchen", "مطبخ", "Kitchen"),
			("pos-cashier", "كاشير", "Cashier"), ("pos-manager", "مدير", "Manager"),
		};

		[HttpGet]
		public async Task<IActionResult> CashierRoles(int? companyId, int? branchId)
		{
			if (companyId == null && branchId != null) companyId = await _context.Branches.Where(b => b.ID == branchId).Select(b => (int?)b.CompanyID).FirstOrDefaultAsync();
			await PopulateBranchesAsync(companyId, branchId);
			ViewBag.RoleDefs = PosRoleDefs;
			if (branchId != null)
			{
				ViewBag.Employees = await _pos.GetBranchEmployeesAsync(branchId.Value);
				ViewBag.Roles = await _pos.GetBranchRolesAsync(branchId.Value);
			}
			return View();
		}

		[HttpPost][ValidateAntiForgeryToken]
		public async Task<IActionResult> AssignPosRole(int branchId, int employeeId, string posRole)
		{
			var (ok, err) = await _pos.AssignPosRoleAsync(branchId, employeeId, posRole);
			TempData[ok ? "PosMsg" : "PosErr"] = ok ? L["Role assigned"].Value : err;
			return RedirectToAction(nameof(CashierRoles), new { branchId });
		}

		[HttpPost][ValidateAntiForgeryToken]
		public async Task<IActionResult> RemovePosRole(int branchId, int id)
		{
			var (ok, err) = await _pos.RemovePosRoleAsync(branchId, id);
			TempData[ok ? "PosMsg" : "PosErr"] = ok ? L["Role removed"].Value : err;
			return RedirectToAction(nameof(CashierRoles), new { branchId });
		}

		// ==================== POS-1: CASHIER TERMINALS (isolated till) — setup only ====================
		[HttpGet]
		public async Task<IActionResult> Terminals(int? companyId, int? branchId)
		{
			if (companyId == null && branchId != null) companyId = await _context.Branches.Where(b => b.ID == branchId).Select(b => (int?)b.CompanyID).FirstOrDefaultAsync();
			await PopulateBranchesAsync(companyId, branchId);
			if (branchId != null)
			{
				var terminals = await _pos.GetTerminalsAsync(branchId.Value);
				ViewBag.Terminals = terminals;
				ViewBag.CashAccounts = await _pos.GetCashAccountsAsync();
				var accIds = terminals.Where(t => t.CashAccountId != null).Select(t => t.CashAccountId!.Value).ToList();
				ViewBag.AccNames = await _context.Accounts.AsNoTracking().Where(a => accIds.Contains(a.ID)).ToDictionaryAsync(a => a.ID, a => a.Code + " — " + a.Name);
				var openShifts = new Dictionary<int, CrossBuy.Models.Context.Pos.PosShift?>();
				foreach (var t in terminals) openShifts[t.ID] = await _pos.GetOpenShiftAsync(t.ID);
				ViewBag.OpenShifts = openShifts;
			}
			return View();
		}

		[HttpPost][ValidateAntiForgeryToken]
		public async Task<IActionResult> SaveTerminal(int branchId, int id, string code, string name, string? receiptPrefix, int? cashAccountId, bool autoCreateCash = true, bool isActive = true, int paperWidthMm = 80, int copies = 1, string? printerName = null)
		{
			var (ok, err, _) = await _pos.SaveTerminalAsync(branchId, id, code, name, receiptPrefix, cashAccountId, autoCreateCash, isActive, paperWidthMm, copies, printerName);
			TempData[ok ? "PosMsg" : "PosErr"] = ok ? L["Device saved"].Value : err;
			return RedirectToAction(nameof(Terminals), new { branchId });
		}

		[HttpPost][ValidateAntiForgeryToken]
		public async Task<IActionResult> DeleteTerminal(int branchId, int id)
		{
			var (ok, err) = await _pos.DeleteTerminalAsync(branchId, id);
			TempData[ok ? "PosMsg" : "PosErr"] = ok ? L["Device deleted"].Value : err;
			return RedirectToAction(nameof(Terminals), new { branchId });
		}

		[HttpPost][ValidateAntiForgeryToken]
		public async Task<IActionResult> OpenShift(int branchId, int terminalId, string shiftType, int? employeeId, decimal openingFloat)
		{
			var (ok, err) = await _pos.OpenShiftAsync(terminalId, shiftType, employeeId, openingFloat);
			TempData[ok ? "PosMsg" : "PosErr"] = ok ? L["Shift opened"].Value : err;
			return RedirectToAction(nameof(Terminals), new { branchId });
		}

		[HttpPost][ValidateAntiForgeryToken]
		public async Task<IActionResult> CloseShift(int branchId, int terminalId, int shiftId, decimal? closingFloat = null)
		{
			// admin close: if no counted cash is provided, close at expected (variance 0 → no JE)
			decimal cf = closingFloat ?? 0m;
			if (closingFloat == null)
			{
				var sh = await _context.PosShifts.AsNoTracking().FirstOrDefaultAsync(x => x.ID == shiftId && x.TerminalId == terminalId);
				if (sh != null) cf = await _pos.ExpectedCashAsync(DefaultCompanyId, sh);
			}
			var (ok, err) = await _pos.CloseShiftAsync(DefaultCompanyId, terminalId, shiftId, cf, null, DateTime.Today, null);
			TempData[ok ? "PosMsg" : "PosErr"] = ok ? L["Shift closed"].Value : err;
			return RedirectToAction(nameof(Terminals), new { branchId });
		}

		// NOTE: cashier OPERATION (selling) moved OUT of the admin panel into the independent /pos environment
		// (PosAppController). This admin controller keeps SETUP + the read-only Preview only.

		// ==================== CASHIER LAYOUT PREVIEW (setup phase — NO selling) ====================
		// Admin-only, read-only. Shows how the configured quick buttons + tabs will look to the cashier.
		// Deliberately NO order/pay/invoice/stock here — cashier OPERATION is a later, separate phase.
		[HttpGet]
		public async Task<IActionResult> Preview(int? companyId, int? branchId)
		{
			if (companyId == null && branchId != null) companyId = await _context.Branches.Where(b => b.ID == branchId).Select(b => (int?)b.CompanyID).FirstOrDefaultAsync();
			await PopulateBranchesAsync(companyId, branchId);
			if (branchId != null)
			{
				ViewBag.Menu = await _pos.GetQuickMenuAsync(DefaultCompanyId, branchId.Value);
				ViewBag.PosSetting = await _pos.GetPosSettingAsync(branchId.Value);
				var menu = (List<PosQuickMenuGroupDto>)ViewBag.Menu;
				var ids = menu.SelectMany(g => g.Items).Select(i => i.ItemId).Distinct().ToList();
				ViewBag.Images = await _context.Items.AsNoTracking().Where(i => ids.Contains(i.ID)).ToDictionaryAsync(i => i.ID, i => i.ImagePath);
			}
			return View();
		}

		[HttpPost][ValidateAntiForgeryToken]
		public async Task<IActionResult> SaveMenuGroup(int branchId, int id, string name, string? nameEn, int sort, bool isActive = true, int? kitchenStationId = null)
		{
			var (ok, err) = await _pos.SaveMenuGroupAsync(branchId, id, name, nameEn, sort, isActive, kitchenStationId);
			TempData[ok ? "PosMsg" : "PosErr"] = ok ? L["Tab saved"].Value : err;
			return RedirectToAction(nameof(QuickMenu), new { branchId });
		}

		[HttpPost][ValidateAntiForgeryToken]
		public async Task<IActionResult> DeleteMenuGroup(int id, int branchId)
		{
			var (ok, err) = await _pos.DeleteMenuGroupAsync(id);
			TempData[ok ? "PosMsg" : "PosErr"] = ok ? L["Tab deleted"].Value : err;
			return RedirectToAction(nameof(QuickMenu), new { branchId });
		}

		[HttpPost][ValidateAntiForgeryToken]
		public async Task<IActionResult> AddQuickItem(int branchId, int? groupId, int itemId)
		{
			var (ok, err) = await _pos.AddQuickItemAsync(branchId, groupId, itemId);
			TempData[ok ? "PosMsg" : "PosErr"] = ok ? L["Quick button added"].Value : err;
			return RedirectToAction(nameof(QuickMenu), new { branchId });
		}

		[HttpPost][ValidateAntiForgeryToken]
		public async Task<IActionResult> RemoveQuickItem(int id, int branchId)
		{
			var (ok, err) = await _pos.RemoveQuickItemAsync(id);
			TempData[ok ? "PosMsg" : "PosErr"] = ok ? L["Button deleted"].Value : err;
			return RedirectToAction(nameof(QuickMenu), new { branchId });
		}

		[HttpPost][ValidateAntiForgeryToken]
		public async Task<IActionResult> SetQuickCode(int branchId, int itemId, string? code)
		{
			var (ok, err) = await _pos.SetQuickCodeAsync(DefaultCompanyId, itemId, code);
			TempData[ok ? "PosMsg" : "PosErr"] = ok ? L["Quick code saved"].Value : err;
			return RedirectToAction(nameof(QuickMenu), new { branchId });
		}
	}

	// public DTOs for ItemSourcing ViewBag (avoid anonymous-type dynamic binding across the Views assembly)
	public class PosBranchOption { public int ID { get; set; } public string Name { get; set; } = ""; }
	public class PosSemiItem { public int ID { get; set; } public string ItemCode { get; set; } = ""; public string Name { get; set; } = ""; }
}
