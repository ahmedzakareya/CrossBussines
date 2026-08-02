using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Pos;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL
{
	// Operations platform SETUP service. Configuration only — never posts GL or writes stock.
	public interface IPosSetupService
	{
		Task<List<ActivityPreset>> GetPresetsAsync();
		Task<Dictionary<string, bool>> GetCapabilitiesAsync(int branchId);
		// HM-0: the ONE central capability gate. Every feature gate (HM-1 onward) reads through this — never a
		// direct EF query for capabilities in a controller. Declared default when no row exists: DISABLED (false).
		Task<bool> IsCapabilityEnabledAsync(int branchId, string key);
		// HM-1-أ: true if the branch has ANY capability rows at all (a preset was applied). Distinguishes
		// "configured but this key is off" from "never configured" — the default (false) stays unchanged either way.
		Task<bool> HasCapabilityConfigAsync(int branchId);
		// HM-1-أ (صفر-5/صفر-تكميلي-3): allowActivityChange is the CONSCIOUS-PATH flag. Left false (the setup hub),
		// a risky activity change is refused — see the impl for the two risky cases. A deliberate migration passes true.
		Task<(bool ok, string? error)> ApplyPresetAsync(int branchId, string presetCode, bool allowActivityChange = false);
		Task<(bool ok, string? error)> SaveCapabilitiesAsync(int branchId, Dictionary<string, bool> caps);
		Task<BranchPosSetting> GetPosSettingAsync(int branchId);
		Task<(bool ok, string? error)> SavePosSettingAsync(int branchId, int? warehouseId, int? priceListId, decimal? serviceChargePct, int? currencyId);
		// dining areas
		Task<List<DiningArea>> GetDiningAreasAsync(int branchId);
		Task<(bool ok, string? error)> SaveDiningAreaAsync(int branchId, int id, string code, string name, int sort, bool isActive, string? nameEn = null);
		Task<(bool ok, string? error)> DeleteDiningAreaAsync(int id);
		// kitchen stations
		Task<List<KitchenStation>> GetStationsAsync(int branchId);
		Task<(bool ok, string? error)> SaveStationAsync(int branchId, int id, string code, string name, string? nameEn, string type, bool isActive);
		Task<(bool ok, string? error)> DeleteStationAsync(int id);
		// POS-C2: delivery drivers (per-branch)
		Task<List<Driver>> GetDriversAsync(int branchId);
		Task<(bool ok, string? error)> SaveDriverAsync(int branchId, int id, string name, string phone, bool isActive);
		Task<(bool ok, string? error)> DeleteDriverAsync(int id);
		// POS-A1: delivery zones (per-branch, name + fee) — full CRUD for management
		Task<List<DeliveryZone>> GetAllDeliveryZonesAsync(int branchId);
		Task<(bool ok, string? error)> SaveDeliveryZoneAsync(int branchId, int id, string name, string? nameEn, decimal fee, bool isActive);
		// BIS-1: per-branch item sourcing (setup only)
		Task<List<BranchSourcingRowDto>> GetBranchSourcingAsync(int companyId, int branchId);
		Task<(bool ok, string? error)> SaveBranchItemSourcingAsync(int companyId, int branchId, int itemId, string method, int? sourceBranchId, int? semiFinishedItemId, string? transferTiming);
		Task<List<SourcingOverviewRow>> GetSourcingOverviewAsync(int companyId);   // BIS-4 (read-only)
		Task<(bool ok, string? error)> DeleteDeliveryZoneAsync(int id);
		// POS-B1: table reservations (operational, no GL)
		Task<List<ReservationDto>> GetReservationsAsync(int companyId, int branchId);
		Task<List<ReservationEventDto>> GetReservationEventsAsync(int companyId, int branchId, int? tableId = null);
		Task<(bool ok, string? error)> SaveReservationAsync(int companyId, int branchId, int id, int tableId, int? customerId, string guestName, string guestPhone, DateTime reservedAt, int durationMinutes, int partySize, string? notes);
		Task<(bool ok, string? error)> SetReservationStatusAsync(int companyId, int id, string status);
		// tables (floor plan)
		Task<List<RestaurantTable>> GetTablesAsync(int diningAreaId);
		Task<(bool ok, string? error)> SaveTableAsync(int diningAreaId, int id, string code, int seats, decimal x, decimal y, decimal w, decimal h, string shape, bool isActive);
		Task<(bool ok, string? error)> SaveTableLayoutAsync(List<(int id, decimal x, decimal y, decimal w, decimal h)> layout);
		Task<(bool ok, string? error)> DeleteTableAsync(int id);
		Task<(bool ok, string? error)> RegenerateQrAsync(int id);
		Task<RestaurantTable?> GetTableAsync(int id);
		// cashier setup (RC-1): quick-touch menu
		Task<List<PosMenuGroup>> GetMenuGroupsAsync(int branchId);
		Task<(bool ok, string? error)> SaveMenuGroupAsync(int branchId, int id, string name, string? nameEn, int sort, bool isActive, int? kitchenStationId = null);
		Task<(bool ok, string? error)> DeleteMenuGroupAsync(int id);
		Task<List<PosQuickItem>> GetQuickItemsAsync(int branchId);
		Task<(bool ok, string? error)> AddQuickItemAsync(int branchId, int? groupId, int itemId);
		Task<(bool ok, string? error)> RemoveQuickItemAsync(int id);
		Task<(bool ok, string? error)> SaveQuickItemAsync(int id, int? groupId, int sort);
		Task<(bool ok, string? error)> SetQuickCodeAsync(int companyId, int itemId, string? code);
		Task<List<PosQuickMenuGroupDto>> GetQuickMenuAsync(int companyId, int branchId);   // for the cashier (RC-2)

		// ---- Modifiers (setup only) ----
		Task<List<ModifierGroup>> GetModifierGroupsAsync(int companyId);
		Task<ModifierGroup?> GetModifierGroupAsync(int companyId, int id);
		Task<(bool ok, string? error, int id)> SaveModifierGroupAsync(int companyId, ModifierGroup dto);
		Task<(bool ok, string? error)> DeleteModifierGroupAsync(int companyId, int id);
		Task<List<ModifierOptionDto>> GetOptionsAsync(int companyId, int groupId);
		Task<(bool ok, string? error)> SaveOptionAsync(int companyId, int groupId, int id, string? name, string? nameEn, int linkedItemId, decimal qtyDeducted, decimal extraPrice, bool isDefault);
		Task<(bool ok, string? error)> DeleteOptionAsync(int companyId, int id);
		Task<List<ModifierAttachDto>> GetGroupItemsAsync(int companyId, int groupId);   // items this group is attached to
		Task<(bool ok, string? error)> AttachGroupToItemAsync(int companyId, int groupId, int itemId);
		Task<(bool ok, string? error)> DetachGroupFromItemAsync(int companyId, int linkId);

		// ---- Payment methods (setup only): per-branch method → target GL account ----
		Task<List<BranchPaymentMethod>> GetPaymentMethodsAsync(int branchId);
		Task<(bool ok, string? error)> SavePaymentMethodAsync(int branchId, int id, string paymentMethod, string? displayName, int? targetAccountId, bool isActive, int sort);
		Task<(bool ok, string? error)> DeletePaymentMethodAsync(int branchId, int id);

		// ---- Cashier roles (setup only): assign branch employees to POS roles ----
		Task<List<BranchEmployeeDto>> GetBranchEmployeesAsync(int branchId);
		Task<List<BranchRoleDto>> GetBranchRolesAsync(int branchId);
		Task<(bool ok, string? error)> AssignPosRoleAsync(int branchId, int employeeId, string posRole);
		Task<(bool ok, string? error)> RemovePosRoleAsync(int branchId, int id);

		// ---- POS-1: terminals (isolated till) + shifts (setup only) ----
		Task<List<PosTerminal>> GetTerminalsAsync(int branchId);
		Task<List<CashAccountDto>> GetCashAccountsAsync();                  // postable cash/bank accounts for the override dropdown
		Task<(bool ok, string? error, int id)> SaveTerminalAsync(int branchId, int id, string code, string name, string? receiptPrefix, int? cashAccountId, bool autoCreateCash, bool isActive, int paperWidthMm = 80, int copies = 1, string? printerName = null);
		Task<(bool ok, string? error)> DeleteTerminalAsync(int branchId, int id);
		Task<PosShift?> GetOpenShiftAsync(int terminalId);
		Task<List<PosShift>> GetShiftsAsync(int terminalId, int take);
		Task<(bool ok, string? error)> OpenShiftAsync(int terminalId, string shiftType, int? employeeId, decimal openingFloat);
		Task<decimal> ExpectedCashAsync(int companyId, PosShift s);   // RC-6a
		Task<ZReportDto?> GetShiftZReportAsync(int companyId, int terminalId, int shiftId);   // RC-6b (read-only)
		Task<(bool ok, string? error)> CloseShiftAsync(int companyId, int terminalId, int shiftId, decimal closingFloat, int? closedByEmployeeId, DateTime date, string? userId);
		Task<(bool ok, string? error, bool alreadyClosed)> SyncShiftCloseAsync(int companyId, int terminalId, int shiftId, decimal closingFloat, int? closedByEmployeeId, DateTime date, string? userId);   // POS-9e
		Task<List<SyncConflictDto>> GetSyncConflictsAsync(int companyId, bool includeAcknowledged);   // POS-9e manager review
		Task<(bool ok, string? error)> AcknowledgeSyncConflictAsync(int companyId, int conflictId, int? employeeId);   // POS-9e
	}

	// cashier-facing quick menu (groups + their buttons)
	public class PosQuickMenuGroupDto { public int? GroupId { get; set; } public string GroupName { get; set; } = ""; public string? GroupNameEn { get; set; } public List<PosQuickMenuItemDto> Items { get; set; } = new(); }
	public class PosQuickMenuItemDto { public int ItemId { get; set; } public string Code { get; set; } = ""; public string? QuickCode { get; set; } public string Name { get; set; } = ""; public string? NameEn { get; set; } public decimal? Price { get; set; } }

	// modifier option joined with its linked item (for the setup screen)
	public class ModifierOptionDto { public int Id { get; set; } public string Name { get; set; } = ""; public string? NameEn { get; set; } public int LinkedItemId { get; set; } public string LinkedItemName { get; set; } = ""; public string LinkedItemCode { get; set; } = ""; public decimal QtyDeducted { get; set; } public decimal ExtraPrice { get; set; } public bool IsDefault { get; set; } }
	public class ModifierAttachDto { public int LinkId { get; set; } public int ItemId { get; set; } public string ItemName { get; set; } = ""; public string ItemCode { get; set; } = ""; public string? Image { get; set; } }
	public class CashAccountDto { public int Id { get; set; } public string Code { get; set; } = ""; public string Name { get; set; } = ""; }
	// BIS-1: one row per branch quick-item, carrying its current sourcing choice (null Method = not set yet → defaults to deduct-self)
	public class BranchSourcingRowDto
	{
		public int ItemId { get; set; } public string ItemName { get; set; } = ""; public string ItemCode { get; set; } = ""; public bool HasBom { get; set; }
		public string? Method { get; set; } public int? SourceBranchId { get; set; } public int? SemiFinishedItemId { get; set; } public string? TransferTiming { get; set; }
	}
	// BIS-4: read-only overview row (per branch, per item)
	public class SourcingOverviewRow
	{
		public int BranchId { get; set; } public string BranchName { get; set; } = ""; public string ItemCode { get; set; } = ""; public string ItemName { get; set; } = "";
		public string Method { get; set; } = ""; public string? SourceBranchName { get; set; } public string? SemiName { get; set; } public string? TransferTiming { get; set; }
	}
	// RC-6b: Z report DTOs (read-only)
	public class ZPayLine { public string Method { get; set; } = ""; public decimal Amount { get; set; } public int Count { get; set; } }
	public class ZReportDto
	{
		public int ShiftId { get; set; } public string TerminalCode { get; set; } = ""; public string ShiftType { get; set; } = ""; public string Status { get; set; } = "";
		public DateTime OpenedAt { get; set; } public DateTime? ClosedAt { get; set; } public int? ClosedByEmployeeId { get; set; }
		public int OrderCount { get; set; } public decimal SubTotal { get; set; } public decimal ServiceAmount { get; set; } public decimal TaxTotal { get; set; } public decimal GrandTotal { get; set; }
		public List<ZPayLine> Payments { get; set; } = new(); public decimal ReturnsTotal { get; set; } public decimal TipsTotal { get; set; }
		public decimal OpeningFloat { get; set; } public decimal ExpectedCash { get; set; } public decimal? ClosingFloat { get; set; } public decimal? CashVariance { get; set; }
	}
	// POS-9e: a sync conflict shaped for the manager review screen (read/acknowledge; never gates selling)
	public class SyncConflictDto
	{
		public int Id { get; set; } public string ConflictType { get; set; } = ""; public int? OrderId { get; set; } public string? ReceiptNo { get; set; }
		public string? ItemName { get; set; } public string? Detail { get; set; } public decimal? OfflineValue { get; set; } public decimal? ServerValue { get; set; }
		public string Status { get; set; } = ""; public DateTime CreatedAt { get; set; } public DateTime? AckedAt { get; set; }
	}
	public class BranchEmployeeDto { public int Id { get; set; } public string Name { get; set; } = ""; }
	public class BranchRoleDto { public int Id { get; set; } public int EmployeeId { get; set; } public string EmployeeName { get; set; } = ""; public string PosRole { get; set; } = ""; }
	public class ReservationDto { public int Id { get; set; } public int TableId { get; set; } public string TableCode { get; set; } = ""; public int? CustomerId { get; set; } public string CustomerName { get; set; } = ""; public string GuestName { get; set; } = ""; public string GuestPhone { get; set; } = ""; public DateTime ReservedAt { get; set; } public int DurationMinutes { get; set; } public int PartySize { get; set; } public string Status { get; set; } = ""; public string? Notes { get; set; } public int? OrderId { get; set; } }
	// A reservation shaped for a FullCalendar event feed (used by the calendar view/booking surface).
	public class ReservationEventDto { public int Id { get; set; } public int TableId { get; set; } public string TableCode { get; set; } = ""; public string CustomerName { get; set; } = ""; public string GuestName { get; set; } = ""; public DateTime Start { get; set; } public int DurationMinutes { get; set; } public int PartySize { get; set; } public string Status { get; set; } = ""; }

	public class PosSetupService : IPosSetupService
	{
		private readonly CrossDbContext _db;
		private readonly IJournalEntryService _journals;   // RC-6a: cash-drawer variance JE at shift close
		private readonly ICurrencyService _currency;
		private readonly ICurrencyRounding _rounding;
		private readonly Microsoft.Extensions.Localization.IStringLocalizer<CrossBuy.SharedResources> L;
		public PosSetupService(CrossDbContext db, IJournalEntryService journals, ICurrencyService currency, ICurrencyRounding rounding, Microsoft.Extensions.Localization.IStringLocalizer<CrossBuy.SharedResources> localizer) { _db = db; _journals = journals; _currency = currency; _rounding = rounding; L = localizer; }

		// HM-2 (4-ب): the shift's DOCUMENT currency = the terminal's branch DefaultCurrencyId (KWD for a hyper), else the functional.
		private async Task<(int cur, decimal rate, int ddp, int fdp)> ShiftCurrencyAsync(int companyId, int terminalId)
		{
			int functional = await _currency.GetFunctionalCurrencyIdAsync(companyId, null);
			int? branchId = await _db.PosTerminals.AsNoTracking().Where(t => t.ID == terminalId).Select(t => (int?)t.BranchId).FirstOrDefaultAsync();
			int? branchCur = branchId == null ? null : await _db.BranchPosSettings.AsNoTracking().Where(s => s.BranchId == branchId.Value).Select(s => s.DefaultCurrencyId).FirstOrDefaultAsync();
			int cur = branchCur ?? functional;
			decimal rate = cur == functional ? 1m : (await _currency.ToBaseAsync(1m, cur, functional, DateTime.Today, "Sell")).effectiveRate;
			return (cur, rate, await _rounding.DecimalsAsync(companyId, cur), await _rounding.DecimalsAsync(companyId, null));
		}

		private static string NewToken() => Guid.NewGuid().ToString("N").Substring(0, 16);

		public Task<List<ActivityPreset>> GetPresetsAsync() =>
			_db.ActivityPresets.AsNoTracking().OrderBy(p => p.Sort).ToListAsync();

		public async Task<Dictionary<string, bool>> GetCapabilitiesAsync(int branchId) =>
			(await _db.BranchCapabilities.AsNoTracking().Where(c => c.BranchId == branchId).ToListAsync())
				.ToDictionary(c => c.CapabilityKey, c => c.Enabled);

		// HM-0: per-request capability cache. PosSetupService is registered Scoped ⇒ one instance per request,
		// so this instance field is a per-request memo (Program.cs:123 AddScoped).
		private readonly Dictionary<int, Dictionary<string, bool>> _capCache = new();

		// HM-0 central capability gate — the single read path for "does this branch have capability X enabled".
		// DECLARED DEFAULT when the branch has no row for the key: DISABLED (returns false). A capability is on
		// only when an explicit row says Enabled = true.
		public async Task<bool> IsCapabilityEnabledAsync(int branchId, string key)
		{
			if (!_capCache.TryGetValue(branchId, out var caps))
			{
				caps = await GetCapabilitiesAsync(branchId);
				_capCache[branchId] = caps;
			}
			return caps.TryGetValue(key, out var v) && v;
		}

		// HM-1-أ: has the branch any capability rows at all (i.e. was a preset ever applied)?
		public Task<bool> HasCapabilityConfigAsync(int branchId) =>
			_db.BranchCapabilities.AnyAsync(c => c.BranchId == branchId);

		// Apply a preset: set the branch's activity type and (re)seed capabilities from the preset defaults.
		// Existing capability toggles the admin already changed are preserved (only missing keys are added).
		public async Task<(bool ok, string? error)> ApplyPresetAsync(int branchId, string presetCode, bool allowActivityChange = false)
		{
			var branch = await _db.Branches.FirstOrDefaultAsync(b => b.ID == branchId);
			if (branch == null) return (false, "الفرع غير موجود");
			var preset = await _db.ActivityPresets.AsNoTracking().FirstOrDefaultAsync(p => p.Code == presetCode);
			if (preset == null) return (false, "نوع النشاط غير موجود");
			// HM-1-أ (صفر-5 + صفر-تكميلي-3): guard DESTRUCTIVE activity assignment. Two risky cases:
			//  (A) the branch already has an activity set and a DIFFERENT one is applied (would wipe & overwrite its caps);
			//  (B) FIRST assignment (currently null) on a branch that ALREADY OPERATES (has terminals/orders) to an
			//      activity of a DIFFERENT FAMILY than its actual use — a no-activity operating branch is used as a
			//      restaurant (the only lane that serves NULL), so anything other than {Restaurant,Cafe} would break it
			//      (wipe caps + the whitelist guard then locks its cashiers out of their lane).
			// Both are refused unless the CONSCIOUS-PATH flag is passed. Same-preset re-apply and a first assignment on a
			// clean (no-history) branch stay allowed.
			bool hasCurrent = !string.IsNullOrWhiteSpace(branch.ActivityPresetCode);
			bool restaurantFamily = presetCode == "Restaurant" || presetCode == "Cafe";
			bool hasHistory = await _db.PosTerminals.AnyAsync(t => t.BranchId == branchId)
							|| await _db.PosOrders.AnyAsync(o => o.BranchId == branchId);
			bool riskyChange = (hasCurrent && branch.ActivityPresetCode != presetCode)
							|| (!hasCurrent && hasHistory && !restaurantFamily);
			if (riskyChange && !allowActivityChange)
				return (false, hasCurrent
					? $"نشاط هذا الفرع مضبوط بالفعل ({branch.ActivityPresetCode})؛ تغيير النشاط يتطلّب مسارًا واعيًا منفصلًا، لا تطبيق نشاط مختلف من هنا."
					: $"هذا الفرع عامل فعليًّا (له ترمينالات/طلبات) ويُستخدم كمطعم؛ تعيين نشاط «{presetCode}» من عائلة مختلفة سيُلغي إعداده الحالي ويُقفل كاشيريه خارج ممرّهم — يتطلّب مسارًا واعيًا منفصلًا.");
			var defaults = await _db.ActivityPresetCapabilities.AsNoTracking().Where(c => c.PresetId == preset.ID).ToListAsync();
			var existing = await _db.BranchCapabilities.Where(c => c.BranchId == branchId).ToListAsync();
			foreach (var d in defaults)
			{
				var ex = existing.FirstOrDefault(x => x.CapabilityKey == d.CapabilityKey);
				if (ex == null) _db.BranchCapabilities.Add(new BranchCapability { BranchId = branchId, CapabilityKey = d.CapabilityKey, Enabled = d.DefaultEnabled });
				else ex.Enabled = d.DefaultEnabled;   // re-applying the preset resets to its defaults
			}
			branch.ActivityPresetCode = presetCode;
			await _db.SaveChangesAsync();
			return (true, null);
		}

		public async Task<(bool ok, string? error)> SaveCapabilitiesAsync(int branchId, Dictionary<string, bool> caps)
		{
			var existing = await _db.BranchCapabilities.Where(c => c.BranchId == branchId).ToListAsync();
			foreach (var kv in caps)
			{
				var ex = existing.FirstOrDefault(x => x.CapabilityKey == kv.Key);
				if (ex == null) _db.BranchCapabilities.Add(new BranchCapability { BranchId = branchId, CapabilityKey = kv.Key, Enabled = kv.Value });
				else ex.Enabled = kv.Value;
			}
			await _db.SaveChangesAsync();
			return (true, null);
		}

		public async Task<BranchPosSetting> GetPosSettingAsync(int branchId)
		{
			var s = await _db.BranchPosSettings.AsNoTracking().FirstOrDefaultAsync(x => x.BranchId == branchId);
			return s ?? new BranchPosSetting { BranchId = branchId };
		}

		public async Task<(bool ok, string? error)> SavePosSettingAsync(int branchId, int? warehouseId, int? priceListId, decimal? serviceChargePct, int? currencyId)
		{
			var s = await _db.BranchPosSettings.FirstOrDefaultAsync(x => x.BranchId == branchId);
			if (s == null) { s = new BranchPosSetting { BranchId = branchId }; _db.BranchPosSettings.Add(s); }
			s.DefaultSalesWarehouseId = warehouseId; s.DefaultPriceListId = priceListId;
			s.ServiceChargePct = serviceChargePct; s.DefaultCurrencyId = currencyId;
			await _db.SaveChangesAsync();
			return (true, null);
		}

		// ---- dining areas ----
		public Task<List<DiningArea>> GetDiningAreasAsync(int branchId) =>
			_db.DiningAreas.AsNoTracking().Where(a => a.BranchId == branchId).OrderBy(a => a.Sort).ThenBy(a => a.Code).ToListAsync();

		public async Task<(bool ok, string? error)> SaveDiningAreaAsync(int branchId, int id, string code, string name, int sort, bool isActive, string? nameEn = null)
		{
			if (string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(name)) return (false, "الكود والاسم مطلوبان");
			if (await _db.DiningAreas.AnyAsync(a => a.BranchId == branchId && a.Code == code && a.ID != id)) return (false, "كود الصالة مستخدم في هذا الفرع");
			var enTrim = string.IsNullOrWhiteSpace(nameEn) ? null : nameEn.Trim();
			if (id > 0)
			{
				var ex = await _db.DiningAreas.FirstOrDefaultAsync(a => a.ID == id && a.BranchId == branchId);
				if (ex == null) return (false, "الصالة غير موجودة");
				ex.Code = code.Trim(); ex.Name = name.Trim(); ex.NameEn = enTrim; ex.Sort = sort; ex.IsActive = isActive;
			}
			else _db.DiningAreas.Add(new DiningArea { BranchId = branchId, Code = code.Trim(), Name = name.Trim(), NameEn = enTrim, Sort = sort, IsActive = isActive });
			await _db.SaveChangesAsync();
			return (true, null);
		}

		public async Task<(bool ok, string? error)> DeleteDiningAreaAsync(int id)
		{
			var ex = await _db.DiningAreas.FindAsync(id);
			if (ex == null) return (false, "الصالة غير موجودة");
			if (await _db.RestaurantTables.AnyAsync(t => t.DiningAreaId == id)) return (false, "لا يمكن الحذف: توجد طاولات في هذه الصالة");
			_db.DiningAreas.Remove(ex); await _db.SaveChangesAsync();
			return (true, null);
		}

		// ---- kitchen stations ----
		public Task<List<KitchenStation>> GetStationsAsync(int branchId) =>
			_db.KitchenStations.AsNoTracking().Where(s => s.BranchId == branchId).OrderBy(s => s.Code).ToListAsync();

		public async Task<(bool ok, string? error)> SaveStationAsync(int branchId, int id, string code, string name, string? nameEn, string type, bool isActive)
		{
			if (string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(name)) return (false, "الكود والاسم مطلوبان");
			if (await _db.KitchenStations.AnyAsync(s => s.BranchId == branchId && s.Code == code && s.ID != id)) return (false, "كود المحطة مستخدم في هذا الفرع");
			type = new[] { "Kitchen", "Bar", "Grill", "Prep" }.Contains(type) ? type : "Kitchen";
			var en = string.IsNullOrWhiteSpace(nameEn) ? null : nameEn.Trim();
			if (id > 0)
			{
				var ex = await _db.KitchenStations.FirstOrDefaultAsync(s => s.ID == id && s.BranchId == branchId);
				if (ex == null) return (false, "المحطة غير موجودة");
				ex.Code = code.Trim(); ex.Name = name.Trim(); ex.NameEn = en; ex.StationType = type; ex.IsActive = isActive;
			}
			else _db.KitchenStations.Add(new KitchenStation { BranchId = branchId, Code = code.Trim(), Name = name.Trim(), NameEn = en, StationType = type, IsActive = isActive });
			await _db.SaveChangesAsync();
			return (true, null);
		}

		// POS-C2: delivery drivers (per-branch)
		public async Task<List<Driver>> GetDriversAsync(int branchId) =>
			await _db.Drivers.AsNoTracking().Where(d => d.BranchId == branchId).OrderByDescending(d => d.IsActive).ThenBy(d => d.Name).ToListAsync();

		public async Task<(bool ok, string? error)> SaveDriverAsync(int branchId, int id, string name, string phone, bool isActive)
		{
			if (string.IsNullOrWhiteSpace(name)) return (false, "اسم السائق مطلوب");
			if (id > 0)
			{
				var ex = await _db.Drivers.FirstOrDefaultAsync(d => d.ID == id && d.BranchId == branchId);
				if (ex == null) return (false, "السائق غير موجود");
				ex.Name = name.Trim(); ex.Phone = phone?.Trim() ?? ""; ex.IsActive = isActive;
			}
			else _db.Drivers.Add(new Driver { BranchId = branchId, Name = name.Trim(), Phone = phone?.Trim() ?? "", IsActive = isActive });
			await _db.SaveChangesAsync();
			return (true, null);
		}

		public async Task<(bool ok, string? error)> DeleteDriverAsync(int id)
		{
			var ex = await _db.Drivers.FirstOrDefaultAsync(d => d.ID == id);
			if (ex == null) return (false, "السائق غير موجود");
			// keep history: if the driver is referenced by any order, deactivate instead of delete
			if (await _db.PosOrders.AnyAsync(o => o.DriverId == id)) { ex.IsActive = false; await _db.SaveChangesAsync(); return (true, null); }
			_db.Drivers.Remove(ex); await _db.SaveChangesAsync();
			return (true, null);
		}

		// POS-A1: delivery zones (per-branch, name + fee) — management CRUD (the cashier keeps the read-only GetDeliveryZonesAsync)
		public async Task<List<DeliveryZone>> GetAllDeliveryZonesAsync(int branchId) =>
			await _db.DeliveryZones.AsNoTracking().Where(z => z.BranchId == branchId).OrderByDescending(z => z.IsActive).ThenBy(z => z.Name).ToListAsync();

		public async Task<(bool ok, string? error)> SaveDeliveryZoneAsync(int branchId, int id, string name, string? nameEn, decimal fee, bool isActive)
		{
			if (string.IsNullOrWhiteSpace(name)) return (false, "اسم المنطقة مطلوب");
			if (fee < 0) return (false, "الرسم لا يكون سالبًا");
			var en = string.IsNullOrWhiteSpace(nameEn) ? null : nameEn.Trim();
			if (id > 0)
			{
				var ex = await _db.DeliveryZones.FirstOrDefaultAsync(z => z.ID == id && z.BranchId == branchId);
				if (ex == null) return (false, "المنطقة غير موجودة");
				ex.Name = name.Trim(); ex.NameEn = en; ex.Fee = Math.Round(fee, 4); ex.IsActive = isActive;
			}
			else _db.DeliveryZones.Add(new DeliveryZone { BranchId = branchId, Name = name.Trim(), NameEn = en, Fee = Math.Round(fee, 4), IsActive = isActive });
			await _db.SaveChangesAsync();
			return (true, null);
		}

		public async Task<(bool ok, string? error)> DeleteDeliveryZoneAsync(int id)
		{
			var ex = await _db.DeliveryZones.FirstOrDefaultAsync(z => z.ID == id);
			if (ex == null) return (false, "المنطقة غير موجودة");
			// keep history: a zone frozen onto any order → deactivate instead of delete
			if (await _db.PosOrders.AnyAsync(o => o.DeliveryZoneId == id)) { ex.IsActive = false; await _db.SaveChangesAsync(); return (true, null); }
			_db.DeliveryZones.Remove(ex); await _db.SaveChangesAsync();
			return (true, null);
		}

		// ===== BIS-1: per-branch item sourcing (SETUP ONLY — never posts GL/stock) =====
		private static readonly string[] SourcingMethods = { "WorkOrder", "FinishedFromBranch", "SemiFromBranchComplete", "RecipeAtSale" };

		public async Task<List<BranchSourcingRowDto>> GetBranchSourcingAsync(int companyId, int branchId)
		{
			var isAr = System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ar";
			// the branch's sellable items = its quick-menu items
			var itemIds = await _db.PosQuickItems.AsNoTracking().Where(q => q.BranchId == branchId).Select(q => q.ItemId).Distinct().ToListAsync();
			if (itemIds.Count == 0) return new();
			var items = await _db.Items.AsNoTracking().Where(i => i.CompanyID == companyId && itemIds.Contains(i.ID))
				.Select(i => new { i.ID, i.Name, i.NameEn, i.ItemCode }).ToListAsync();
			var src = (await _db.BranchItemSourcings.AsNoTracking().Where(s => s.BranchId == branchId && itemIds.Contains(s.ItemId)).ToListAsync())
				.ToDictionary(s => s.ItemId);
			var bomItemIds = (await _db.ItemComponents.AsNoTracking().Where(c => c.CompanyID == companyId && itemIds.Contains(c.ParentItemId)).Select(c => c.ParentItemId).Distinct().ToListAsync()).ToHashSet();
			return items.OrderBy(i => i.ItemCode).Select(i =>
			{
				src.TryGetValue(i.ID, out var s);
				return new BranchSourcingRowDto
				{
					ItemId = i.ID, ItemName = isAr ? i.Name : (!string.IsNullOrWhiteSpace(i.NameEn) ? i.NameEn! : i.Name), ItemCode = i.ItemCode,
					HasBom = bomItemIds.Contains(i.ID),
					Method = s?.Method, SourceBranchId = s?.SourceBranchId, SemiFinishedItemId = s?.SemiFinishedItemId, TransferTiming = s?.TransferTiming,
				};
			}).ToList();
		}

		public async Task<(bool ok, string? error)> SaveBranchItemSourcingAsync(int companyId, int branchId, int itemId, string method, int? sourceBranchId, int? semiFinishedItemId, string? transferTiming)
		{
			method = (method ?? "").Trim();
			if (!SourcingMethods.Contains(method)) return (false, "طريقة توفير غير معروفة");
			if (!await _db.Items.AnyAsync(i => i.ID == itemId && i.CompanyID == companyId)) return (false, "الصنف غير موجود");
			bool hasBom = await _db.ItemComponents.AnyAsync(c => c.CompanyID == companyId && c.ParentItemId == itemId);

			// per-method guards (the user's decisions from the design doc)
			if (method == "WorkOrder" && !hasBom) return (false, "أمر التصنيع يتطلب قائمة مواد (BOM) للصنف");
			if (method == "RecipeAtSale" && !hasBom) return (false, "الوصفة عند البيع تتطلب قائمة مواد (BOM) للصنف");
			if (method == "FinishedFromBranch" || method == "SemiFromBranchComplete")
			{
				if (sourceBranchId == null) return (false, "يجب اختيار الفرع المصدر");
				if (sourceBranchId == branchId) return (false, "الفرع المصدر لا يكون نفس الفرع");
				if (!await _db.Branches.AnyAsync(b => b.ID == sourceBranchId)) return (false, "الفرع المصدر غير موجود");
				if (string.IsNullOrWhiteSpace(transferTiming)) transferTiming = "Prepaid";
				if (transferTiming != "Prepaid" && transferTiming != "AtSale") return (false, "توقيت تحويل غير صحيح");
			}
			else { sourceBranchId = null; transferTiming = null; }   // helper fields only apply to 2/3
			if (method == "SemiFromBranchComplete")
			{
				if (semiFinishedItemId == null) return (false, "يجب اختيار الصنف نصف-المصنّع");
				if (semiFinishedItemId == itemId) return (false, "نصف-المصنّع لا يكون نفس الصنف التام");
				if (!await _db.Items.AnyAsync(i => i.ID == semiFinishedItemId && i.CompanyID == companyId)) return (false, "الصنف نصف-المصنّع غير موجود");
				if (!hasBom) return (false, "الإكمال يتطلب قائمة مواد (BOM) للصنف التام");
				if (!await _db.ItemComponents.AnyAsync(c => c.CompanyID == companyId && c.ParentItemId == itemId && c.ComponentItemId == semiFinishedItemId))
					return (false, "نصف-المصنّع يجب أن يكون ضمن قائمة مواد الصنف التام");
			}
			else semiFinishedItemId = null;   // only method 3

			var ex = await _db.BranchItemSourcings.FirstOrDefaultAsync(s => s.BranchId == branchId && s.ItemId == itemId);
			if (ex == null) { ex = new BranchItemSourcing { BranchId = branchId, ItemId = itemId, CreatedAt = DateTime.UtcNow }; _db.BranchItemSourcings.Add(ex); }
			ex.Method = method; ex.SourceBranchId = sourceBranchId; ex.SemiFinishedItemId = semiFinishedItemId; ex.TransferTiming = transferTiming; ex.IsActive = true;
			await _db.SaveChangesAsync();
			return (true, null);
		}

		// BIS-4: read-only overview of every branch's item sourcing (for review). Writes nothing.
		public async Task<List<SourcingOverviewRow>> GetSourcingOverviewAsync(int companyId)
		{
			var isAr = System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ar";
			var rows = await (from s in _db.BranchItemSourcings.AsNoTracking()
							  where s.IsActive
							  join b in _db.Branches.AsNoTracking() on s.BranchId equals b.ID into gb
							  from b in gb.DefaultIfEmpty()
							  join it in _db.Items.AsNoTracking() on s.ItemId equals it.ID into gi
							  from it in gi.DefaultIfEmpty()
							  join sb in _db.Branches.AsNoTracking() on s.SourceBranchId equals (int?)sb.ID into gsb
							  from sb in gsb.DefaultIfEmpty()
							  join si in _db.Items.AsNoTracking() on s.SemiFinishedItemId equals (int?)si.ID into gsi
							  from si in gsi.DefaultIfEmpty()
							  select new { s.BranchId, BName = b != null ? b.Name : "", BNameAr = b != null ? b.NameAr : null, it, s.Method, s.TransferTiming, SbName = sb != null ? sb.Name : null, SbNameAr = sb != null ? sb.NameAr : null, si }).ToListAsync();
			return rows.Select(r => new SourcingOverviewRow
			{
				BranchId = r.BranchId, BranchName = isAr ? (r.BNameAr ?? r.BName) : r.BName,
				ItemCode = r.it != null ? r.it.ItemCode : "", ItemName = r.it == null ? "" : (isAr ? r.it.Name : (!string.IsNullOrWhiteSpace(r.it.NameEn) ? r.it.NameEn! : r.it.Name)),
				Method = r.Method, TransferTiming = r.TransferTiming,
				SourceBranchName = isAr ? (r.SbNameAr ?? r.SbName) : r.SbName,
				SemiName = r.si == null ? null : r.si.Name,
			}).OrderBy(r => r.BranchName).ThenBy(r => r.ItemCode).ToList();
		}

		public async Task<(bool ok, string? error)> DeleteStationAsync(int id)
		{
			var ex = await _db.KitchenStations.FindAsync(id);
			if (ex == null) return (false, "المحطة غير موجودة");
			_db.KitchenStations.Remove(ex); await _db.SaveChangesAsync();
			return (true, null);
		}

		// ---- tables / floor plan ----
		public Task<List<RestaurantTable>> GetTablesAsync(int diningAreaId) =>
			_db.RestaurantTables.AsNoTracking().Where(t => t.DiningAreaId == diningAreaId).OrderBy(t => t.Code).ToListAsync();

		public Task<RestaurantTable?> GetTableAsync(int id) =>
			_db.RestaurantTables.AsNoTracking().FirstOrDefaultAsync(t => t.ID == id);

		public async Task<(bool ok, string? error)> SaveTableAsync(int diningAreaId, int id, string code, int seats, decimal x, decimal y, decimal w, decimal h, string shape, bool isActive)
		{
			if (string.IsNullOrWhiteSpace(code)) return (false, "كود الطاولة مطلوب");
			code = code.Trim();
			shape = new[] { "Square", "Round", "Rect" }.Contains(shape) ? shape : "Square";
			var area = await _db.DiningAreas.AsNoTracking().FirstOrDefaultAsync(a => a.ID == diningAreaId);
			if (area == null) return (false, "الصالة غير موجودة");
			// code unique within the branch (across its areas) — case/space-insensitive
			var branchAreaIds = await _db.DiningAreas.Where(a => a.BranchId == area.BranchId).Select(a => a.ID).ToListAsync();
			if (await _db.RestaurantTables.AnyAsync(t => branchAreaIds.Contains(t.DiningAreaId) && t.Code == code && t.ID != id))
				return (false, $"كود الطاولة «{code}» مستخدم بالفعل في هذا الفرع");
			if (id > 0)
			{
				var ex = await _db.RestaurantTables.FirstOrDefaultAsync(t => t.ID == id);
				if (ex == null) return (false, "الطاولة غير موجودة");
				ex.DiningAreaId = diningAreaId; ex.Code = code.Trim(); ex.Seats = seats; ex.X = x; ex.Y = y; ex.W = w; ex.H = h; ex.Shape = shape; ex.IsActive = isActive;
			}
			else _db.RestaurantTables.Add(new RestaurantTable { DiningAreaId = diningAreaId, Code = code.Trim(), Seats = seats, X = x, Y = y, W = w, H = h, Shape = shape, QrToken = NewToken(), IsActive = isActive });
			await _db.SaveChangesAsync();
			return (true, null);
		}

		// bulk-persist coordinates from the drag editor
		public async Task<(bool ok, string? error)> SaveTableLayoutAsync(List<(int id, decimal x, decimal y, decimal w, decimal h)> layout)
		{
			var ids = layout.Select(l => l.id).ToList();
			var tables = await _db.RestaurantTables.Where(t => ids.Contains(t.ID)).ToListAsync();
			foreach (var t in tables)
			{
				var l = layout.First(x => x.id == t.ID);
				t.X = l.x; t.Y = l.y; t.W = l.w; t.H = l.h;
			}
			await _db.SaveChangesAsync();
			return (true, null);
		}

		public async Task<(bool ok, string? error)> DeleteTableAsync(int id)
		{
			var ex = await _db.RestaurantTables.FindAsync(id);
			if (ex == null) return (false, "الطاولة غير موجودة");
			_db.RestaurantTables.Remove(ex); await _db.SaveChangesAsync();
			return (true, null);
		}

		public async Task<(bool ok, string? error)> RegenerateQrAsync(int id)
		{
			var ex = await _db.RestaurantTables.FindAsync(id);
			if (ex == null) return (false, "الطاولة غير موجودة");
			ex.QrToken = NewToken(); await _db.SaveChangesAsync();
			return (true, null);
		}

		// ---- cashier setup (RC-1): quick-touch menu ----
		public Task<List<PosMenuGroup>> GetMenuGroupsAsync(int branchId) =>
			_db.PosMenuGroups.AsNoTracking().Where(g => g.BranchId == branchId).OrderBy(g => g.Sort).ThenBy(g => g.ID).ToListAsync();

		public async Task<(bool ok, string? error)> SaveMenuGroupAsync(int branchId, int id, string name, string? nameEn, int sort, bool isActive, int? kitchenStationId = null)
		{
			if (string.IsNullOrWhiteSpace(name)) return (false, "اسم المجموعة مطلوب");
			// RC-3e: guard the station belongs to THIS branch (else ignore → default station applies)
			if (kitchenStationId != null && !await _db.KitchenStations.AnyAsync(s => s.ID == kitchenStationId && s.BranchId == branchId)) kitchenStationId = null;
			if (id > 0)
			{
				var ex = await _db.PosMenuGroups.FirstOrDefaultAsync(g => g.ID == id && g.BranchId == branchId);
				if (ex == null) return (false, "المجموعة غير موجودة");
				ex.Name = name.Trim(); ex.NameEn = nameEn?.Trim(); ex.Sort = sort; ex.IsActive = isActive; ex.KitchenStationId = kitchenStationId;
			}
			else _db.PosMenuGroups.Add(new PosMenuGroup { BranchId = branchId, Name = name.Trim(), NameEn = nameEn?.Trim(), Sort = sort, IsActive = isActive, KitchenStationId = kitchenStationId });
			await _db.SaveChangesAsync();
			return (true, null);
		}

		public async Task<(bool ok, string? error)> DeleteMenuGroupAsync(int id)
		{
			var ex = await _db.PosMenuGroups.FindAsync(id);
			if (ex == null) return (false, "المجموعة غير موجودة");
			// ungroup its quick items (don't delete the buttons, just detach the tab)
			var items = await _db.PosQuickItems.Where(q => q.GroupId == id).ToListAsync();
			foreach (var q in items) q.GroupId = null;
			_db.PosMenuGroups.Remove(ex);
			await _db.SaveChangesAsync();
			return (true, null);
		}

		public Task<List<PosQuickItem>> GetQuickItemsAsync(int branchId) =>
			_db.PosQuickItems.AsNoTracking().Where(q => q.BranchId == branchId).OrderBy(q => q.Sort).ThenBy(q => q.ID).ToListAsync();

		public async Task<(bool ok, string? error)> AddQuickItemAsync(int branchId, int? groupId, int itemId)
		{
			if (itemId <= 0) return (false, "اختر صنفًا");
			if (await _db.PosQuickItems.AnyAsync(q => q.BranchId == branchId && q.ItemId == itemId)) return (false, "الصنف مضاف بالفعل كزر سريع في هذا الفرع");
			var maxSort = await _db.PosQuickItems.Where(q => q.BranchId == branchId && q.GroupId == groupId).Select(q => (int?)q.Sort).MaxAsync() ?? 0;
			_db.PosQuickItems.Add(new PosQuickItem { BranchId = branchId, GroupId = groupId, ItemId = itemId, Sort = maxSort + 1, IsActive = true });
			await _db.SaveChangesAsync();
			return (true, null);
		}

		public async Task<(bool ok, string? error)> RemoveQuickItemAsync(int id)
		{
			var ex = await _db.PosQuickItems.FindAsync(id);
			if (ex == null) return (false, "الزر غير موجود");
			_db.PosQuickItems.Remove(ex); await _db.SaveChangesAsync();
			return (true, null);
		}

		public async Task<(bool ok, string? error)> SaveQuickItemAsync(int id, int? groupId, int sort)
		{
			var ex = await _db.PosQuickItems.FindAsync(id);
			if (ex == null) return (false, "الزر غير موجود");
			ex.GroupId = groupId; ex.Sort = sort;
			await _db.SaveChangesAsync();
			return (true, null);
		}

		public async Task<(bool ok, string? error)> SetQuickCodeAsync(int companyId, int itemId, string? code)
		{
			var item = await _db.Items.FirstOrDefaultAsync(i => i.ID == itemId && i.CompanyID == companyId);
			if (item == null) return (false, "الصنف غير موجود");
			code = string.IsNullOrWhiteSpace(code) ? null : code.Trim();
			if (code != null && await _db.Items.AnyAsync(i => i.CompanyID == companyId && i.QuickCode == code && i.ID != itemId))
				return (false, $"الكود السريع «{code}» مستخدم لصنف آخر");
			item.QuickCode = code;
			await _db.SaveChangesAsync();
			return (true, null);
		}

		// cashier-facing menu: groups (+ an "ungrouped" bucket) with their buttons (code/name/price)
		public async Task<List<PosQuickMenuGroupDto>> GetQuickMenuAsync(int companyId, int branchId)
		{
			var groups = await GetMenuGroupsAsync(branchId);
			var quicks = await _db.PosQuickItems.AsNoTracking().Where(q => q.BranchId == branchId && q.IsActive).OrderBy(q => q.Sort).ToListAsync();
			var itemIds = quicks.Select(q => q.ItemId).Distinct().ToList();
			var items = await _db.Items.AsNoTracking().Where(i => i.CompanyID == companyId && itemIds.Contains(i.ID))
				.ToDictionaryAsync(i => i.ID, i => i);
			PosQuickMenuItemDto Map(PosQuickItem q) { items.TryGetValue(q.ItemId, out var it); return new PosQuickMenuItemDto { ItemId = q.ItemId, Code = it?.ItemCode ?? "", QuickCode = it?.QuickCode, Name = it?.Name ?? ("#" + q.ItemId), NameEn = it?.NameEn, Price = it?.SalesPrice }; }
			var result = new List<PosQuickMenuGroupDto>();
			foreach (var g in groups)
				result.Add(new PosQuickMenuGroupDto { GroupId = g.ID, GroupName = g.Name, GroupNameEn = g.NameEn, Items = quicks.Where(q => q.GroupId == g.ID).Select(Map).ToList() });
			var ungrouped = quicks.Where(q => q.GroupId == null).Select(Map).ToList();
			if (ungrouped.Count > 0) result.Add(new PosQuickMenuGroupDto { GroupId = null, GroupName = "بدون تبويب", GroupNameEn = "Uncategorized", Items = ungrouped });
			return result;
		}

		// ================= Modifiers (setup only — no GL/stock) =================
		public async Task<List<ModifierGroup>> GetModifierGroupsAsync(int companyId) =>
			await _db.ModifierGroups.AsNoTracking().Where(g => g.CompanyID == companyId).OrderBy(g => g.Sort).ThenBy(g => g.ID).ToListAsync();

		public async Task<ModifierGroup?> GetModifierGroupAsync(int companyId, int id) =>
			await _db.ModifierGroups.AsNoTracking().FirstOrDefaultAsync(g => g.ID == id && g.CompanyID == companyId);

		public async Task<(bool ok, string? error, int id)> SaveModifierGroupAsync(int companyId, ModifierGroup dto)
		{
			if (string.IsNullOrWhiteSpace(dto.Name)) return (false, "اسم المجموعة مطلوب", 0);
			var type = dto.Type == "Choice" ? "Choice" : "AddOn";
			var g = dto.ID > 0 ? await _db.ModifierGroups.FirstOrDefaultAsync(x => x.ID == dto.ID && x.CompanyID == companyId) : null;
			if (g == null) { g = new ModifierGroup { CompanyID = companyId, CreatedAt = DateTime.UtcNow }; _db.ModifierGroups.Add(g); }
			g.Name = dto.Name.Trim(); g.NameEn = dto.NameEn; g.Type = type; g.Sort = dto.Sort; g.IsActive = dto.IsActive;
			// Choice = mandatory single alternative by default; AddOn = optional, unlimited
			g.MinSelect = type == "Choice" ? (dto.MinSelect <= 0 ? 1 : dto.MinSelect) : Math.Max(0, dto.MinSelect);
			g.MaxSelect = type == "Choice" ? 1 : Math.Max(0, dto.MaxSelect);
			await _db.SaveChangesAsync();
			return (true, null, g.ID);
		}

		public async Task<(bool ok, string? error)> DeleteModifierGroupAsync(int companyId, int id)
		{
			var g = await _db.ModifierGroups.FirstOrDefaultAsync(x => x.ID == id && x.CompanyID == companyId);
			if (g == null) return (false, "المجموعة غير موجودة");
			var opts = await _db.ModifierOptions.Where(o => o.GroupId == id).ToListAsync();
			var links = await _db.ItemModifierGroups.Where(l => l.GroupId == id).ToListAsync();
			_db.ModifierOptions.RemoveRange(opts);
			_db.ItemModifierGroups.RemoveRange(links);
			_db.ModifierGroups.Remove(g);
			await _db.SaveChangesAsync();
			return (true, null);
		}

		public async Task<List<ModifierOptionDto>> GetOptionsAsync(int companyId, int groupId)
		{
			var opts = await _db.ModifierOptions.AsNoTracking().Where(o => o.GroupId == groupId).OrderBy(o => o.Sort).ThenBy(o => o.ID).ToListAsync();
			var ids = opts.Select(o => o.LinkedItemId).Distinct().ToList();
			var items = await _db.Items.AsNoTracking().Where(i => i.CompanyID == companyId && ids.Contains(i.ID)).ToDictionaryAsync(i => i.ID, i => i);
			return opts.Select(o =>
			{
				items.TryGetValue(o.LinkedItemId, out var it);
				return new ModifierOptionDto
				{
					Id = o.ID, Name = string.IsNullOrWhiteSpace(o.Name) ? (it?.Name ?? "") : o.Name, NameEn = o.NameEn,
					LinkedItemId = o.LinkedItemId, LinkedItemName = it?.Name ?? ("#" + o.LinkedItemId), LinkedItemCode = it?.ItemCode ?? "",
					QtyDeducted = o.QtyDeducted, ExtraPrice = o.ExtraPrice, IsDefault = o.IsDefault,
				};
			}).ToList();
		}

		public async Task<(bool ok, string? error)> SaveOptionAsync(int companyId, int groupId, int id, string? name, string? nameEn, int linkedItemId, decimal qtyDeducted, decimal extraPrice, bool isDefault)
		{
			var g = await _db.ModifierGroups.FirstOrDefaultAsync(x => x.ID == groupId && x.CompanyID == companyId);
			if (g == null) return (false, "المجموعة غير موجودة");
			var item = await _db.Items.FirstOrDefaultAsync(i => i.ID == linkedItemId && i.CompanyID == companyId);
			if (item == null) return (false, "الصنف المرتبط غير موجود");
			if (qtyDeducted <= 0) qtyDeducted = 1;
			if (extraPrice < 0) extraPrice = 0;
			if (g.Type == "Choice") extraPrice = 0;   // Choice alternatives carry no extra price
			var o = id > 0 ? await _db.ModifierOptions.FirstOrDefaultAsync(x => x.ID == id && x.GroupId == groupId) : null;
			if (o == null) { o = new ModifierOption { GroupId = groupId, Sort = (await _db.ModifierOptions.Where(x => x.GroupId == groupId).MaxAsync(x => (int?)x.Sort) ?? 0) + 1 }; _db.ModifierOptions.Add(o); }
			o.Name = (name ?? "").Trim(); o.NameEn = nameEn; o.LinkedItemId = linkedItemId; o.QtyDeducted = qtyDeducted; o.ExtraPrice = extraPrice;
			if (isDefault)
			{
				// only one default per group
				var others = await _db.ModifierOptions.Where(x => x.GroupId == groupId && x.IsDefault).ToListAsync();
				foreach (var x in others) x.IsDefault = false;
			}
			o.IsDefault = isDefault;
			await _db.SaveChangesAsync();
			return (true, null);
		}

		public async Task<(bool ok, string? error)> DeleteOptionAsync(int companyId, int id)
		{
			var o = await _db.ModifierOptions.FirstOrDefaultAsync(x => x.ID == id);
			if (o == null) return (false, "الخيار غير موجود");
			// guard company via its group
			var g = await _db.ModifierGroups.FirstOrDefaultAsync(x => x.ID == o.GroupId && x.CompanyID == companyId);
			if (g == null) return (false, "غير مسموح");
			_db.ModifierOptions.Remove(o);
			await _db.SaveChangesAsync();
			return (true, null);
		}

		public async Task<List<ModifierAttachDto>> GetGroupItemsAsync(int companyId, int groupId)
		{
			var links = await _db.ItemModifierGroups.AsNoTracking().Where(l => l.GroupId == groupId).OrderBy(l => l.Sort).ToListAsync();
			var ids = links.Select(l => l.ItemId).ToList();
			var items = await _db.Items.AsNoTracking().Where(i => i.CompanyID == companyId && ids.Contains(i.ID)).ToDictionaryAsync(i => i.ID, i => i);
			return links.Select(l => { items.TryGetValue(l.ItemId, out var it); return new ModifierAttachDto { LinkId = l.ID, ItemId = l.ItemId, ItemName = it?.Name ?? ("#" + l.ItemId), ItemCode = it?.ItemCode ?? "", Image = it?.ImagePath }; }).ToList();
		}

		public async Task<(bool ok, string? error)> AttachGroupToItemAsync(int companyId, int groupId, int itemId)
		{
			var g = await _db.ModifierGroups.FirstOrDefaultAsync(x => x.ID == groupId && x.CompanyID == companyId);
			if (g == null) return (false, "المجموعة غير موجودة");
			if (!await _db.Items.AnyAsync(i => i.ID == itemId && i.CompanyID == companyId)) return (false, "الصنف غير موجود");
			if (await _db.ItemModifierGroups.AnyAsync(l => l.GroupId == groupId && l.ItemId == itemId)) return (true, null);   // already linked
			var sort = (await _db.ItemModifierGroups.Where(l => l.ItemId == itemId).MaxAsync(l => (int?)l.Sort) ?? 0) + 1;
			_db.ItemModifierGroups.Add(new ItemModifierGroup { GroupId = groupId, ItemId = itemId, Sort = sort });
			await _db.SaveChangesAsync();
			return (true, null);
		}

		public async Task<(bool ok, string? error)> DetachGroupFromItemAsync(int companyId, int linkId)
		{
			var l = await _db.ItemModifierGroups.FirstOrDefaultAsync(x => x.ID == linkId);
			if (l == null) return (false, "الرابط غير موجود");
			var g = await _db.ModifierGroups.FirstOrDefaultAsync(x => x.ID == l.GroupId && x.CompanyID == companyId);
			if (g == null) return (false, "غير مسموح");
			_db.ItemModifierGroups.Remove(l);
			await _db.SaveChangesAsync();
			return (true, null);
		}

		// ================= Payment methods (setup only — no GL/stock) =================
		public async Task<List<BranchPaymentMethod>> GetPaymentMethodsAsync(int branchId) =>
			await _db.BranchPaymentMethods.AsNoTracking().Where(p => p.BranchId == branchId).OrderBy(p => p.Sort).ThenBy(p => p.ID).ToListAsync();

		public async Task<(bool ok, string? error)> SavePaymentMethodAsync(int branchId, int id, string paymentMethod, string? displayName, int? targetAccountId, bool isActive, int sort)
		{
			paymentMethod = (paymentMethod ?? "").Trim();
			if (paymentMethod.Length == 0) return (false, "نوع طريقة الدفع مطلوب");
			var p = id > 0 ? await _db.BranchPaymentMethods.FirstOrDefaultAsync(x => x.ID == id && x.BranchId == branchId) : null;
			if (p == null)
			{
				if (await _db.BranchPaymentMethods.AnyAsync(x => x.BranchId == branchId && x.PaymentMethod == paymentMethod))
					return (false, $"طريقة الدفع «{paymentMethod}» معرّفة بالفعل لهذا الفرع");
				p = new BranchPaymentMethod { BranchId = branchId }; _db.BranchPaymentMethods.Add(p);
			}
			p.PaymentMethod = paymentMethod; p.DisplayName = string.IsNullOrWhiteSpace(displayName) ? null : displayName.Trim();
			p.TargetAccountId = targetAccountId; p.IsActive = isActive; p.Sort = sort;
			await _db.SaveChangesAsync();
			return (true, null);
		}

		public async Task<(bool ok, string? error)> DeletePaymentMethodAsync(int branchId, int id)
		{
			var p = await _db.BranchPaymentMethods.FirstOrDefaultAsync(x => x.ID == id && x.BranchId == branchId);
			if (p == null) return (false, "غير موجود");
			_db.BranchPaymentMethods.Remove(p);
			await _db.SaveChangesAsync();
			return (true, null);
		}

		// ================= Cashier roles (setup only — no GL/stock) =================
		private static readonly string[] PosRoles = { "pos-waiter", "pos-kitchen", "pos-cashier", "pos-manager" };

		public async Task<List<BranchEmployeeDto>> GetBranchEmployeesAsync(int branchId) =>
			await _db.Employee.AsNoTracking().Where(e => e.BranchID == branchId)
				.OrderBy(e => e.FullName).Select(e => new BranchEmployeeDto { Id = e.ID, Name = e.FullName ?? ("#" + e.ID) }).ToListAsync();

		public async Task<List<BranchRoleDto>> GetBranchRolesAsync(int branchId)
		{
			var roles = await _db.BranchUserRoles.AsNoTracking().Where(r => r.BranchId == branchId).OrderBy(r => r.PosRole).ToListAsync();
			var empIds = roles.Select(r => r.EmployeeId).Distinct().ToList();
			var emps = await _db.Employee.AsNoTracking().Where(e => empIds.Contains(e.ID)).ToDictionaryAsync(e => e.ID, e => e.FullName);
			return roles.Select(r => new BranchRoleDto { Id = r.ID, EmployeeId = r.EmployeeId, EmployeeName = emps.TryGetValue(r.EmployeeId, out var n) ? (n ?? ("#" + r.EmployeeId)) : ("#" + r.EmployeeId), PosRole = r.PosRole }).ToList();
		}

		public async Task<(bool ok, string? error)> AssignPosRoleAsync(int branchId, int employeeId, string posRole)
		{
			if (!PosRoles.Contains(posRole)) return (false, "دور غير صالح");
			var emp = await _db.Employee.FirstOrDefaultAsync(e => e.ID == employeeId && e.BranchID == branchId);
			if (emp == null) return (false, "الموظف غير موجود في هذا الفرع");
			if (await _db.BranchUserRoles.AnyAsync(r => r.BranchId == branchId && r.EmployeeId == employeeId && r.PosRole == posRole))
				return (true, null);   // already assigned
			_db.BranchUserRoles.Add(new BranchUserRole { BranchId = branchId, EmployeeId = employeeId, PosRole = posRole, IsActive = true, CreatedAt = DateTime.UtcNow });
			await _db.SaveChangesAsync();
			return (true, null);
		}

		public async Task<(bool ok, string? error)> RemovePosRoleAsync(int branchId, int id)
		{
			var r = await _db.BranchUserRoles.FirstOrDefaultAsync(x => x.ID == id && x.BranchId == branchId);
			if (r == null) return (false, "غير موجود");
			_db.BranchUserRoles.Remove(r);
			await _db.SaveChangesAsync();
			return (true, null);
		}

		// ================= POS-1: terminals + shifts (setup only — no GL/stock) =================
		private const int PosCompanyId = 1;   // cash accounts / catalog live under company 1 (branches sit under 65–79)

		public async Task<List<PosTerminal>> GetTerminalsAsync(int branchId) =>
			await _db.PosTerminals.AsNoTracking().Where(t => t.BranchId == branchId).OrderBy(t => t.Code).ToListAsync();

		public async Task<List<CashAccountDto>> GetCashAccountsAsync() =>
			await _db.Accounts.AsNoTracking().Where(a => a.CompanyID == PosCompanyId && a.IsPostable && a.IsActive && a.Code.StartsWith("1101"))
				.OrderBy(a => a.Code).Select(a => new CashAccountDto { Id = a.ID, Code = a.Code, Name = a.Name }).ToListAsync();

		// Creates a dedicated child cash account under the main cash (110101) for a terminal's drawer.
		// This is a CHART node only (no journal entry) → invariants untouched.
		private async Task<int?> CreateTillAccountAsync(int branchId, string terminalCode)
		{
			var parent = await _db.Accounts.FirstOrDefaultAsync(a => a.CompanyID == PosCompanyId && a.Code == "110101")
					   ?? await _db.Accounts.Where(a => a.CompanyID == PosCompanyId && a.IsPostable && a.Code.StartsWith("1101")).OrderBy(a => a.Code).FirstOrDefaultAsync();
			if (parent == null) return null;
			var branchName = await _db.Branches.Where(b => b.ID == branchId).Select(b => b.Name).FirstOrDefaultAsync() ?? branchId.ToString();
			// child code = parent code + 2-digit sequence, first free
			string? code = null;
			for (int i = 1; i <= 99; i++)
			{
				var cand = parent.Code + i.ToString("D2");
				if (!await _db.Accounts.AnyAsync(a => a.CompanyID == PosCompanyId && a.Code == cand)) { code = cand; break; }
			}
			if (code == null) return null;
			var acc = new CrossBuy.Models.Context.Accounting.Account
			{
				CompanyID = PosCompanyId, Code = code, Name = $"صندوق {terminalCode} - {branchName}", NameEn = $"Till {terminalCode}",
				AccountTypeId = parent.AccountTypeId, ParentId = parent.ID, IsPostable = true, IsActive = true, CreatedAt = DateTime.UtcNow,
			};
			_db.Accounts.Add(acc);
			await _db.SaveChangesAsync();
			return acc.ID;
		}

		public async Task<(bool ok, string? error, int id)> SaveTerminalAsync(int branchId, int id, string code, string name, string? receiptPrefix, int? cashAccountId, bool autoCreateCash, bool isActive, int paperWidthMm = 80, int copies = 1, string? printerName = null)
		{
			code = (code ?? "").Trim();
			if (code.Length == 0) return (false, "كود الجهاز مطلوب", 0);
			if (await _db.PosTerminals.AnyAsync(t => t.BranchId == branchId && t.Code == code && t.ID != id))
				return (false, $"كود الجهاز «{code}» مستخدم في هذا الفرع", 0);
			var t = id > 0 ? await _db.PosTerminals.FirstOrDefaultAsync(x => x.ID == id && x.BranchId == branchId) : null;
			bool isNew = t == null;
			if (t == null) { t = new PosTerminal { BranchId = branchId, NextReceiptNo = 1, CreatedAt = DateTime.UtcNow }; _db.PosTerminals.Add(t); }
			// HM-D5-أ: the ReceiptPrefix must NOT change once the terminal has issued orders — an offline receipt already
			// generated under the old prefix would then fail the suffix-parse (become a ReceiptNoMismatch conflict) and stop
			// protecting the counter at the worst moment. Changing it is allowed only while the terminal has no orders.
			if (!isNew)
			{
				var newPfx = string.IsNullOrWhiteSpace(receiptPrefix) ? (code + "-") : receiptPrefix.Trim();
				if (!string.Equals(newPfx, t.ReceiptPrefix, StringComparison.Ordinal) && await _db.PosOrders.AnyAsync(o => o.TerminalId == t.ID))
					return (false, "لا يمكن تغيير بادئة سلسلة الإيصالات بعد إصدار طلبات على هذا الجهاز (تحمي من تصادم أرقام الإيصالات الأوفلاين)", t.ID);
			}
			t.Code = code; t.Name = string.IsNullOrWhiteSpace(name) ? code : name.Trim();
			t.ReceiptPrefix = string.IsNullOrWhiteSpace(receiptPrefix) ? (code + "-") : receiptPrefix.Trim();
			t.ReceiptPaperWidthMm = paperWidthMm == 58 ? 58 : 80;
			t.ReceiptCopies = copies < 1 ? 1 : (copies > 5 ? 5 : copies);
			t.ReceiptPrinterName = string.IsNullOrWhiteSpace(printerName) ? null : printerName.Trim();
			t.IsActive = isActive;
			// cash drawer: explicit pick wins; else auto-create a child account (only when none set yet)
			if (cashAccountId != null && cashAccountId > 0) t.CashAccountId = cashAccountId;
			else if (t.CashAccountId == null && autoCreateCash) t.CashAccountId = await CreateTillAccountAsync(branchId, code);
			await _db.SaveChangesAsync();
			return (true, null, t.ID);
		}

		public async Task<(bool ok, string? error)> DeleteTerminalAsync(int branchId, int id)
		{
			var t = await _db.PosTerminals.FirstOrDefaultAsync(x => x.ID == id && x.BranchId == branchId);
			if (t == null) return (false, "غير موجود");
			if (await _db.PosShifts.AnyAsync(s => s.TerminalId == id && s.Status == "Open")) return (false, "لا يمكن حذف جهاز به وردية مفتوحة");
			_db.PosTerminals.Remove(t);   // the auto-created cash account is left in the chart (may hold history)
			await _db.SaveChangesAsync();
			return (true, null);
		}

		public async Task<PosShift?> GetOpenShiftAsync(int terminalId) =>
			await _db.PosShifts.AsNoTracking().FirstOrDefaultAsync(s => s.TerminalId == terminalId && s.Status == "Open");

		public async Task<List<PosShift>> GetShiftsAsync(int terminalId, int take) =>
			await _db.PosShifts.AsNoTracking().Where(s => s.TerminalId == terminalId).OrderByDescending(s => s.ID).Take(take).ToListAsync();

		public async Task<(bool ok, string? error)> OpenShiftAsync(int terminalId, string shiftType, int? employeeId, decimal openingFloat)
		{
			if (!await _db.PosTerminals.AnyAsync(t => t.ID == terminalId)) return (false, "الجهاز غير موجود");
			if (await _db.PosShifts.AnyAsync(s => s.TerminalId == terminalId && s.Status == "Open")) return (false, "توجد وردية مفتوحة بالفعل على هذا الجهاز");
			_db.PosShifts.Add(new PosShift { TerminalId = terminalId, ShiftType = shiftType == "Evening" ? "Evening" : "Morning", Status = "Open", OpenedByEmployeeId = employeeId, OpeningFloat = openingFloat < 0 ? 0 : openingFloat, OpenedAt = DateTime.UtcNow });
			await _db.SaveChangesAsync();
			return (true, null);
		}

		// RC-6b: Z report — a READ-ONLY snapshot of a shift built from existing paid orders + payments. Writes NOTHING.
		public async Task<ZReportDto?> GetShiftZReportAsync(int companyId, int terminalId, int shiftId)
		{
			var s = await _db.PosShifts.AsNoTracking().FirstOrDefaultAsync(x => x.ID == shiftId && x.TerminalId == terminalId);
			if (s == null) return null;
			var term = await _db.PosTerminals.AsNoTracking().FirstOrDefaultAsync(t => t.ID == terminalId);
			// HM-2 (4-ب): every Z total is presented in the shift's DOCUMENT currency precision (Rd), not a hardcoded 2dp.
			var (_zc, _zr, zdp, _zf) = await ShiftCurrencyAsync(companyId, terminalId);

			// paid orders in this shift (sales); totals read straight off the order snapshot (RecomputeAsync-computed)
			var paid = await _db.PosOrders.AsNoTracking().Where(o => o.CompanyId == companyId && o.ShiftId == shiftId && o.Status == "Paid").ToListAsync();
			// payments in this shift, grouped by method (Cash/Card/KNet…), joined via the order's ShiftId
			var pays = await (from p in _db.PosPayments.AsNoTracking()
							  join o in _db.PosOrders.AsNoTracking() on p.OrderId equals o.ID
							  where o.CompanyId == companyId && o.ShiftId == shiftId && o.Status != "Voided"
							  group p by p.PaymentMethod into g
							  select new ZPayLine { Method = g.Key, Amount = g.Sum(x => x.Amount), Count = g.Count() }).ToListAsync();

			var z = new ZReportDto
			{
				ShiftId = s.ID, TerminalCode = term?.Code ?? "", ShiftType = s.ShiftType, Status = s.Status,
				OpenedAt = s.OpenedAt, ClosedAt = s.ClosedAt, ClosedByEmployeeId = s.ClosedByEmployeeId,
				OrderCount = paid.Count,
				SubTotal = Math.Round(paid.Sum(o => o.SubTotal), zdp, MidpointRounding.AwayFromZero),
				ServiceAmount = Math.Round(paid.Sum(o => o.ServiceAmount), zdp, MidpointRounding.AwayFromZero),
				TaxTotal = Math.Round(paid.Sum(o => o.TaxTotal), zdp, MidpointRounding.AwayFromZero),
				GrandTotal = Math.Round(paid.Sum(o => o.GrandTotal), zdp, MidpointRounding.AwayFromZero),
				Payments = pays.OrderByDescending(x => x.Amount).ToList(),
				ReturnsTotal = 0m,   // RC-6c will populate refunds/returns for the shift
				TipsTotal = Math.Round(paid.Sum(o => o.TipAmount), zdp, MidpointRounding.AwayFromZero),   // RC-5: total gratuities collected this shift

				OpeningFloat = s.OpeningFloat,
				ExpectedCash = s.Status == "Closed" ? (s.ExpectedCash ?? await ExpectedCashAsync(companyId, s)) : await ExpectedCashAsync(companyId, s),
				ClosingFloat = s.ClosingFloat, CashVariance = s.CashVariance,
			};
			return z;
		}

		// RC-6a: expected drawer cash for a shift = OpeningFloat + Σ cash payments − Σ cash refunds (refunds land in RC-6c).
		// Cash-only: card/KNet settle to the bank, not the drawer. Payments link to the shift via their order's ShiftId.
		public async Task<decimal> ExpectedCashAsync(int companyId, PosShift s)
		{
			decimal cashIn = await (from p in _db.PosPayments
									join o in _db.PosOrders on p.OrderId equals o.ID
									where o.CompanyId == companyId && o.ShiftId == s.ID && o.Status != "Voided" && p.PaymentMethod == "Cash"
									select (decimal?)p.Amount).SumAsync() ?? 0m;   // RC-6c: a voided order's cash was refunded OUT of the drawer → exclude
			// RC-5: cash tips physically land in the drawer → part of expected cash (card tips go to the bank)
			decimal cashTips = await _db.PosOrders.Where(o => o.CompanyId == companyId && o.ShiftId == s.ID && o.Status != "Voided" && o.TipMethod == "Cash").SumAsync(o => (decimal?)o.TipAmount) ?? 0m;
			var (_, _, ddp, _2) = await ShiftCurrencyAsync(companyId, s.TerminalId);   // HM-2: drawer cash is in the DOCUMENT currency
			return Math.Round(s.OpeningFloat + cashIn + cashTips, ddp, MidpointRounding.AwayFromZero);
		}

		// RC-6a: close a shift with a counted drawer → compute expected + variance; post the over/short to 520111 (via the
		// SOLE JournalEntryService) against the terminal's drawer-cash account. variance>0 = overage (Dr cash/Cr 520111),
		// variance<0 = shortage (Dr 520111/Cr cash). variance==0 → no JE. Data + one balanced JE only; no stock, integrity-safe.
		public async Task<(bool ok, string? error)> CloseShiftAsync(int companyId, int terminalId, int shiftId, decimal closingFloat, int? closedByEmployeeId, DateTime date, string? userId)
		{
			var s = await _db.PosShifts.FirstOrDefaultAsync(x => x.ID == shiftId && x.TerminalId == terminalId);
			if (s == null) return (false, "الوردية غير موجودة");
			if (s.Status == "Closed") return (false, "الوردية مُغلقة بالفعل");
			// HM-1/HM-D34: cross-company guard (HARD REJECT). After the HM-D34 relabel every legitimate terminal/branch is company 1,
			// so a terminal whose branch belongs to another company is a real cross-company shift-close JE — reject.
			int? shiftBranchCo = await _db.PosTerminals.Where(t => t.ID == terminalId).Join(_db.Branches, t => t.BranchId, b => b.ID, (t, b) => (int?)b.CompanyID).FirstOrDefaultAsync();
			if (shiftBranchCo != companyId) return (false, L["This terminal belongs to another company — cross-company operations are blocked."]);
			if (closingFloat < 0) closingFloat = 0;

			// HM-2 (4-ب): counted cash / expected / variance are in the DOCUMENT currency (Rd); the JE posts the SINGLE variance
			// converted ONCE to the functional (Rf) on both lines ⇒ balances by construction (no eligible P&L line — rejection-safe).
			var (_, rate, ddp, fdp) = await ShiftCurrencyAsync(companyId, terminalId);
			decimal expected = await ExpectedCashAsync(companyId, s);
			decimal variance = Math.Round(closingFloat - expected, ddp, MidpointRounding.AwayFromZero);   // document currency

			// HM-1-أ (هـ): the variance JE AND the close-field write (incl. VarianceJournalEntryId) must be ATOMIC — ONE
			// own-or-join transaction — else a "JE posted but close-fields unsaved" failure leaves an ORPHAN variance JE and
			// a shift that still looks Open (which could be closed again → a second variance JE). own-or-join: CreateAndPostAsync
			// JOINS this transaction instead of committing its own. When variance==0 there is no JE; the single write is trivially atomic.
			await using var tx = await ScopedTx.BeginOrJoinAsync(_db);
			if (variance != 0m)
			{
				// drawer cash account: terminal drawer → branch Cash method → 110101 (same priority as PayAsync)
				var term = await _db.PosTerminals.AsNoTracking().FirstOrDefaultAsync(t => t.ID == terminalId);
				int drawer = term?.CashAccountId ?? 0;
				if (drawer == 0) drawer = await _db.Accounts.Where(a => a.CompanyID == companyId && a.Code == "110101").Select(a => a.ID).FirstOrDefaultAsync();
				var over = await _db.Accounts.Where(a => a.CompanyID == companyId && a.Code == "520111").Select(a => (int?)a.ID).FirstOrDefaultAsync();
				if (drawer == 0 || over == null) { await tx.RollbackAsync(); return (false, "حساب النقدية أو حساب عجز/زيادة النقدية (520111) غير موجود — شغّل SQL"); }
				// SINGLE conversion of the variance to the functional currency ⇒ both JE lines use the SAME value ⇒ balances by construction.
				decimal amt = Math.Round(Math.Abs(variance) * rate, fdp, MidpointRounding.AwayFromZero);
				var lines = new List<JournalLineInput>
				{
					// overage (variance>0): more cash than book → Dr drawer / Cr 520111 (gain)
					// shortage (variance<0): missing cash → Dr 520111 (loss) / Cr drawer
					new JournalLineInput { AccountId = drawer, Debit = variance > 0 ? amt : 0, Credit = variance < 0 ? amt : 0, Description = "تسوية درج الوردية" },
					new JournalLineInput { AccountId = over.Value, Debit = variance < 0 ? amt : 0, Credit = variance > 0 ? amt : 0, Description = variance > 0 ? "زيادة نقدية درج" : "عجز نقدية درج" },
				};
				var (jok, jerr, je) = await _journals.CreateAndPostAsync(new JournalEntryInput
				{ CompanyID = companyId, EntryDate = date.Date, JournalType = "Auto", SourceType = "PosShiftClose", SourceId = s.ID, Description = $"إغلاق وردية #{s.ID} — فرق درج", Lines = lines }, null);
				if (!jok) { await tx.RollbackAsync(); return (false, "تعذّر ترحيل قيد فرق الدرج: " + jerr); }
				s.VarianceJournalEntryId = je!.ID;
			}

			s.ClosingFloat = closingFloat; s.ExpectedCash = expected; s.CashVariance = variance;
			s.ClosedByEmployeeId = closedByEmployeeId; s.Status = "Closed"; s.ClosedAt = DateTime.UtcNow;
			await _db.SaveChangesAsync();
			await tx.CommitAsync();
			return (true, null);
		}

		// POS-9e: replay an OFFLINE shift close (queued on the device). Server posts the variance JE via the existing
		// CloseShiftAsync — no accounting on the device. Idempotent: an already-closed shift returns alreadyClosed (no re-post).
		public async Task<(bool ok, string? error, bool alreadyClosed)> SyncShiftCloseAsync(int companyId, int terminalId, int shiftId, decimal closingFloat, int? closedByEmployeeId, DateTime date, string? userId)
		{
			var s = await _db.PosShifts.AsNoTracking().FirstOrDefaultAsync(x => x.ID == shiftId && x.TerminalId == terminalId);
			if (s == null) return (false, "الوردية غير موجودة", false);
			if (s.Status == "Closed") return (true, null, true);   // already synced/closed — idempotent
			var (ok, err) = await CloseShiftAsync(companyId, terminalId, shiftId, closingFloat, closedByEmployeeId, date, userId);
			return (ok, err, false);
		}

		// POS-9e: manager review of sync conflicts (Open first, most recent first). Read-only — never affects the posted sale.
		public async Task<List<SyncConflictDto>> GetSyncConflictsAsync(int companyId, bool includeAcknowledged)
		{
			var q = _db.PosSyncConflicts.AsNoTracking().Where(c => c.CompanyId == companyId);
			if (!includeAcknowledged) q = q.Where(c => c.Status == "Open");
			var rows = await q.OrderByDescending(c => c.Status == "Open").ThenByDescending(c => c.CreatedAt).Take(500).ToListAsync();
			var itemIds = rows.Where(r => r.ItemId != null).Select(r => r.ItemId!.Value).Distinct().ToList();
			var names = itemIds.Count == 0 ? new Dictionary<int, string>() : await _db.Items.AsNoTracking().Where(i => itemIds.Contains(i.ID)).ToDictionaryAsync(i => i.ID, i => i.Name);
			var orderIds = rows.Where(r => r.OrderId != null).Select(r => r.OrderId!.Value).Distinct().ToList();
			var receipts = orderIds.Count == 0 ? new Dictionary<int, string?>() : await _db.PosOrders.AsNoTracking().Where(o => orderIds.Contains(o.ID)).ToDictionaryAsync(o => o.ID, o => o.ReceiptNo);
			return rows.Select(c => new SyncConflictDto
			{
				Id = c.ID, ConflictType = c.ConflictType, OrderId = c.OrderId,
				ReceiptNo = c.OrderId != null && receipts.TryGetValue(c.OrderId.Value, out var rn) ? rn : null,
				ItemName = c.ItemId != null && names.TryGetValue(c.ItemId.Value, out var nm) ? nm : null,
				Detail = c.Detail, OfflineValue = c.OfflineValue, ServerValue = c.ServerValue,
				Status = c.Status, CreatedAt = c.CreatedAt, AckedAt = c.AckedAt
			}).ToList();
		}

		// POS-9e: mark a conflict reviewed. Purely a workflow flag — posts/reverses nothing.
		public async Task<(bool ok, string? error)> AcknowledgeSyncConflictAsync(int companyId, int conflictId, int? employeeId)
		{
			var c = await _db.PosSyncConflicts.FirstOrDefaultAsync(x => x.ID == conflictId && x.CompanyId == companyId);
			if (c == null) return (false, "التعارض غير موجود");
			if (c.Status != "Acknowledged") { c.Status = "Acknowledged"; c.AckedByEmployeeId = employeeId; c.AckedAt = DateTime.UtcNow; await _db.SaveChangesAsync(); }
			return (true, null);
		}

		// ===== POS-B1: table reservations (operational, no GL) =====
		public async Task<List<ReservationDto>> GetReservationsAsync(int companyId, int branchId)
		{
			// today + upcoming (from start-of-today), newest activity first by time
			var since = DateTime.Now.Date;
			var rows = await (from r in _db.Reservations.AsNoTracking()
							  join t in _db.RestaurantTables.AsNoTracking() on r.TableId equals t.ID into gt
							  from t in gt.DefaultIfEmpty()
							  join c in _db.Customers.AsNoTracking() on r.CustomerId equals (int?)c.ID into gc
							  from c in gc.DefaultIfEmpty()
							  where r.CompanyId == companyId && r.BranchId == branchId && r.ReservedAtUtc >= since
							  orderby r.ReservedAtUtc
							  select new ReservationDto
							  {
								  Id = r.ID, TableId = r.TableId, TableCode = t != null ? t.Code : "",
								  CustomerId = r.CustomerId, CustomerName = c != null ? c.Name : "",
								  GuestName = r.GuestName, GuestPhone = r.GuestPhone, ReservedAt = r.ReservedAtUtc,
								  DurationMinutes = r.DurationMinutes, PartySize = r.PartySize, Status = r.Status, Notes = r.Notes, OrderId = r.OrderId
							  }).ToListAsync();
			return rows;
		}

		// Reservations as calendar events — from 60 days ago onward (bounds history, tz-safe: stored time is naive local).
		// Optionally scoped to a single table (the calendar shows that table's bookings once it is picked on the floor).
		public async Task<List<ReservationEventDto>> GetReservationEventsAsync(int companyId, int branchId, int? tableId = null)
		{
			var since = DateTime.Now.Date.AddDays(-60);
			return await (from r in _db.Reservations.AsNoTracking()
						  join t in _db.RestaurantTables.AsNoTracking() on r.TableId equals t.ID into gt
						  from t in gt.DefaultIfEmpty()
						  join c in _db.Customers.AsNoTracking() on r.CustomerId equals (int?)c.ID into gc
						  from c in gc.DefaultIfEmpty()
						  where r.CompanyId == companyId && r.BranchId == branchId && r.ReservedAtUtc >= since
						        && (tableId == null || r.TableId == tableId)
						  orderby r.ReservedAtUtc
						  select new ReservationEventDto
						  {
							  Id = r.ID, TableId = r.TableId, TableCode = t != null ? t.Code : "",
							  CustomerName = c != null ? c.Name : "", GuestName = r.GuestName,
							  Start = r.ReservedAtUtc, DurationMinutes = r.DurationMinutes,
							  PartySize = r.PartySize, Status = r.Status
						  }).ToListAsync();
		}

		public async Task<(bool ok, string? error)> SaveReservationAsync(int companyId, int branchId, int id, int tableId, int? customerId, string guestName, string guestPhone, DateTime reservedAt, int durationMinutes, int partySize, string? notes)
		{
			if (string.IsNullOrWhiteSpace(guestName)) return (false, "اسم الضيف مطلوب");
			if (partySize < 1) return (false, "عدد الأشخاص غير صحيح");
			if (durationMinutes < 1) durationMinutes = 120;
			// no reservation for a time/day that has already passed (allow a small 1-minute skew)
			if (id == 0 && reservedAt < DateTime.Now.AddMinutes(-1)) return (false, "لا يمكن الحجز في وقت أو يوم مضى");
			// the table must belong to this branch
			var tableOk = await (from t in _db.RestaurantTables join a in _db.DiningAreas on t.DiningAreaId equals a.ID where t.ID == tableId && a.BranchId == branchId select t.ID).AnyAsync();
			if (!tableOk) return (false, "الطاولة غير صحيحة");
			if (customerId != null && !await _db.Customers.AnyAsync(c => c.ID == customerId && c.CompanyID == companyId)) customerId = null;
			// OVERLAP: no two BOOKED reservations on the same table whose windows [start, start+duration) intersect
			var start = reservedAt; var end = reservedAt.AddMinutes(durationMinutes);
			var others = await _db.Reservations.Where(r => r.BranchId == branchId && r.TableId == tableId && r.ID != id && r.Status == "Booked").ToListAsync();
			if (others.Any(r => r.ReservedAtUtc < end && start < r.ReservedAtUtc.AddMinutes(r.DurationMinutes)))
				return (false, "يوجد حجز متداخل على نفس الطاولة في هذا التوقيت");
			if (id > 0)
			{
				var ex = await _db.Reservations.FirstOrDefaultAsync(r => r.ID == id && r.BranchId == branchId);
				if (ex == null) return (false, "الحجز غير موجود");
				if (ex.Status != "Booked") return (false, "لا يمكن تعديل حجز غير نشط");
				ex.TableId = tableId; ex.CustomerId = customerId; ex.GuestName = guestName.Trim(); ex.GuestPhone = guestPhone?.Trim() ?? "";
				ex.ReservedAtUtc = reservedAt; ex.DurationMinutes = durationMinutes; ex.PartySize = partySize; ex.Notes = notes?.Trim();
			}
			else _db.Reservations.Add(new Reservation { CompanyId = companyId, BranchId = branchId, TableId = tableId, CustomerId = customerId, GuestName = guestName.Trim(), GuestPhone = guestPhone?.Trim() ?? "", ReservedAtUtc = reservedAt, DurationMinutes = durationMinutes, PartySize = partySize, Notes = notes?.Trim(), Status = "Booked", CreatedAt = DateTime.UtcNow });
			await _db.SaveChangesAsync();
			return (true, null);
		}

		// B1 handles Cancelled / NoShow (from Booked). Arrived is set by the arrival→order flow (POS-B2).
		public async Task<(bool ok, string? error)> SetReservationStatusAsync(int companyId, int id, string status)
		{
			if (status != "Cancelled" && status != "NoShow") return (false, "حالة غير صحيحة");
			var r = await _db.Reservations.FirstOrDefaultAsync(x => x.ID == id && x.CompanyId == companyId);
			if (r == null) return (false, "الحجز غير موجود");
			if (r.Status != "Booked") return (false, "لا يمكن تغيير حالة هذا الحجز");   // forward-only from Booked
			r.Status = status;
			await _db.SaveChangesAsync();
			return (true, null);
		}
	}
}
