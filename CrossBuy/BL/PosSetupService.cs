using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Pos;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL
{
	// Operations platform SETUP service. Configuration only — never posts GL or writes stock.
	/// HM-17: one extra barcode of an item, in the unit it is printed on.
	public class ItemBarcodeRow { public int Id { get; set; } public string Barcode { get; set; } = ""; public int? UoMId { get; set; } }

	// HM-PROMO: one promotion as the list screen needs it - the engine's own fields, plus how many
	// targets it carries, because "basket-wide" and "three items" read identically otherwise.
	public class PosPromoRow
	{
		public int Id { get; set; }
		public string Code { get; set; } = "";
		public string NameAr { get; set; } = "";
		public string NameEn { get; set; } = "";
		public string PromoType { get; set; } = "";
		public int? BranchId { get; set; }
		public int Priority { get; set; }
		public bool IsExclusive { get; set; }
		public bool IsActive { get; set; }
		public decimal DiscountPct { get; set; }
		public decimal DiscountAmount { get; set; }
		public decimal MinBasket { get; set; }
		public decimal TriggerQty { get; set; }
		public decimal RewardQty { get; set; }
		public DateTime? StartsAt { get; set; }
		public DateTime? EndsAt { get; set; }
		public int TargetCount { get; set; }
	}

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

		// HM-13: the SCALE LABEL FORMAT for a branch. Separate from SavePosSettingAsync on purpose - that
		// method writes the trading defaults and is called from the restaurant setup screen too, while this
		// is a hypermarket-only concern that must not be reachable from there.
		Task<(bool ok, string? error)> SaveScaleSettingsAsync(int branchId, string? prefix, int itemCodeLength, int valueLength, int valueDecimals, string valueType, string checkAlgo);

		// HM-13: mark an item as sold by weight and give it the code the scale prints. Company-scoped: a
		// scale code is unique per company, and the caller never gets to pick which company.
		Task<(bool ok, string? error)> SaveWeighedItemAsync(int companyId, int itemId, bool isWeighted, int? scaleCode);

		// HM-17: the extra barcodes an item carries, each in its OWN unit (a case has a different barcode
		// and a different unit from the piece inside it). Written only by a dev seeding endpoint until now.
		Task<List<ItemBarcodeRow>> GetItemBarcodesAsync(int companyId, int itemId);
		Task<(bool ok, string? error)> AddItemBarcodeAsync(int companyId, int branchId, int itemId, string? barcode, int uomId);
		Task<(bool ok, string? error)> DeleteItemBarcodeAsync(int companyId, int id);

		// HM-18: the loyalty EARNING rate and what earns. Company sets a default, a branch may override it,
		// and a category (or a single item) may be excluded. Written only by a dev endpoint until now.
		Task<(bool ok, string? error)> SaveLoyaltyRateAsync(int companyId, int branchId, decimal? branchRate, decimal? companyRate);
		Task<(bool ok, string? error)> SaveCategoryLoyaltyAsync(int companyId, int categoryId, bool eligible);

		// HM-19: bind a BRANCH to a tax code. The middle step of the item -> branch -> company resolution
		// that PosOrderService.ResolveTaxRateAsync already performs; until now only a dev endpoint wrote it.
		Task<(bool ok, string? error)> SaveBranchTaxCodeAsync(int companyId, int branchId, int? taxCodeId);

		// HM-20: cash that enters or leaves the drawer DURING a shift (a safe drop, a float top-up, a small
		// cash payout). Until now ExpectedCash could not see any of it, so a legitimate drop closed the shift
		// as a cash SHORTAGE and posted a loss to 520111.
		// Returns the posted entry id so a caller that must ALSO write a subsidiary-ledger document can
		// link the two. Without it a refund receipt would exist with no entry behind it.
		Task<(bool ok, string? error, int? journalEntryId)> RecordShiftCashMovementAsync(int companyId, int terminalId, int shiftId,
			string kind, decimal amount, int counterAccountId, string? note, DateTime date, int? userId,
			bool systemCounter = false);

		// HM-EXP: can this item be sold from this warehouse today, and when does the nearest usable
		// batch expire? A READ, never a write.
		Task<(bool sellable, string? reason, DateTime? nearestExpiry)> ExpirySellCheckAsync(
			int companyId, int itemId, int warehouseId, decimal neededBase, DateTime date);

		// HM-PROMO: the till's promotions. The engine that READS these has shipped for a long time;
		// these are the four calls that let anyone put a row in front of it.
		Task<List<PosPromoRow>> GetPosPromotionsAsync(int companyId, int? branchId);
		Task<(Models.Context.Pos.PosPromotion? promo, List<Models.Context.Pos.PosPromotionItem> targets)> GetPosPromotionAsync(int companyId, int id);
		Task<(bool ok, string? error, int id)> SavePosPromotionAsync(int companyId, Models.Context.Pos.PosPromotion dto, List<Models.Context.Pos.PosPromotionItem> targets);
		Task<(bool ok, string? error)> SetPosPromotionActiveAsync(int companyId, int id, bool active);
		Task<List<ShiftCashMovementRow>> GetShiftCashMovementsAsync(int companyId, int shiftId);

		// HM-21: a priced-up list of what a bulk price change WOULD do, and the writer that does it. The two
		// share one computation on purpose — a preview produced by different code than the apply is a preview
		// of nothing.
		Task<List<BulkPriceRow>> PreviewBulkPriceChangeAsync(int companyId, int? categoryId, string? search,
			string mode, decimal value, int? currencyId);
		Task<(int changed, int blocked, int unchanged, int stale, string? error)> ApplyBulkPriceChangeAsync(int companyId,
			int? categoryId, string? search, string mode, decimal value, int? currencyId, Dictionary<int, decimal> seen);
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
		Task<(bool ok, string? error)> SaveDriverAsync(int branchId, int id, string name, string? nameEn, string phone, bool isActive);
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
		Task<(bool ok, string? error)> SavePaymentMethodAsync(int branchId, int id, string paymentMethod, string? displayName, string? displayNameEn, int? targetAccountId, bool isActive, int sort);
		Task<(bool ok, string? error)> DeletePaymentMethodAsync(int branchId, int id);

		// ---- Cashier roles (setup only): assign branch employees to POS roles ----
		Task<List<BranchEmployeeDto>> GetBranchEmployeesAsync(int branchId);
		Task<List<BranchRoleDto>> GetBranchRolesAsync(int branchId);
		Task<(bool ok, string? error)> AssignPosRoleAsync(int branchId, int employeeId, string posRole);
		Task<(bool ok, string? error)> RemovePosRoleAsync(int branchId, int id);

		// ---- POS-1: terminals (isolated till) + shifts (setup only) ----
		Task<List<PosTerminal>> GetTerminalsAsync(int branchId);
		Task<List<CashAccountDto>> GetCashAccountsAsync();                  // postable cash/bank accounts for the override dropdown
		Task<(bool ok, string? error, int id)> SaveTerminalAsync(int branchId, int id, string code, string name, string? nameEn, string? receiptPrefix, int? cashAccountId, bool autoCreateCash, bool isActive, int paperWidthMm = 80, int copies = 1, string? printerName = null);
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
		// HM-20 — the mid-shift cash movements, SHOWN because they now move ExpectedCash.
		// Without these two figures the headline is unexplainable: a manager reading «expected 1,500» cannot
		// reconcile it from an opening float of 200 and takings of 1,800 while a 500 safe drop stays invisible.
		// A number a reader cannot reproduce is one they stop trusting, variance and all.
		public decimal CashMovedIn { get; set; } public decimal CashMovedOut { get; set; }
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

	// HM-20: one mid-shift cash movement, read back OUT OF THE LEDGER rather than from a POS table of its own.
	public class ShiftCashMovementRow
	{
		public int JournalEntryId { get; set; }
		public string Kind { get; set; } = "";        // In / Out, as seen from the DRAWER
		public decimal Amount { get; set; }             // always positive; Kind carries the direction
		public int CounterAccountId { get; set; }
		public string? CounterAccountName { get; set; }
		// HM-20 fix: TRUE when the other side of this movement no longer resolves to an account row. The
		// movement still counts — it moved real money — but it is reported rather than quietly absorbed,
		// because a missing account is a data defect and a left join alone would hide it forever.
		public bool CounterAccountMissing { get; set; }
		public string? Note { get; set; }
		public DateTime EntryDate { get; set; }
		public string? EntryNo { get; set; }
	}

	// HM-21: one item's share of a proposed bulk price change, already judged against the margin floor.
	public class BulkPriceRow
	{
		public int ItemId { get; set; }
		public string? Code { get; set; }
		public string? Name { get; set; }
		public string? NameEn { get; set; }
		public decimal? OldPrice { get; set; }
		public decimal NewPrice { get; set; }
		public string MarginMode { get; set; } = "Off";   // Off | Warn | Block
		public bool BelowFloor { get; set; }                // the new price is under the gross-margin floor
		public bool CostUnknown { get; set; }               // no cost on hand — no judgement was possible
		public decimal FloorFunctional { get; set; }
		// Blocked means the SETTING says Block and this price is under the floor. A Warn-mode shop still writes it.
		public bool Blocked => MarginMode == "Block" && BelowFloor;
	}

	public class PosSetupService : IPosSetupService
	{
		private readonly CrossDbContext _db;
		private readonly IJournalEntryService _journals;   // RC-6a: cash-drawer variance JE at shift close
		private readonly ICurrencyService _currency;
		private readonly ICurrencyRounding _rounding;
		// HM-21: the gross-margin floor for a bulk price change. The till's own rule, not a second copy of it.
		private readonly IPricingService _pricing;
		private readonly Microsoft.Extensions.Localization.IStringLocalizer<CrossBuy.SharedResources> L;
		public PosSetupService(CrossDbContext db, IJournalEntryService journals, ICurrencyService currency, ICurrencyRounding rounding, IPricingService pricing, Microsoft.Extensions.Localization.IStringLocalizer<CrossBuy.SharedResources> localizer, Microsoft.Extensions.Options.IOptions<CrossBuy.Models.Platform.PosCompanyOptions> posCompany) { _db = db; _journals = journals; _currency = currency; _rounding = rounding; _pricing = pricing; L = localizer; PosCompanyId = posCompany.Value.CatalogCompanyId; }

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
			if (branch == null) return (false, "Branch not found");
			var preset = await _db.ActivityPresets.AsNoTracking().FirstOrDefaultAsync(p => p.Code == presetCode);
			if (preset == null) return (false, "Activity type not found");
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
					? $"This branch already has an activity set ({branch.ActivityPresetCode}); changing it requires a separate, deliberate path rather than applying a different preset from here."
					: $"This branch is actually live (it has terminals/orders) and is being used as a restaurant; applying preset «{presetCode}» from a different family would undo its current setup and lock its cashiers out of their lane — that requires a separate, deliberate path.");
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

		// HM-18 — THE LOYALTY EARNING RATE, and which categories earn.
		//
		// Points per unit of currency, on the NET value of eligible lines. The company holds the default
		// and a branch may override it, which is how one store runs a promotion the others do not.
		//
		// A ZERO RATE IS NOT THE SAME AS NO RATE. Null at branch level means "use the company's"; zero means
		// "this branch awards nothing". Collapsing the two would make a deliberate opt-out indistinguishable
		// from an unconfigured branch, so the screen offers Clear separately from setting zero.
		public async Task<(bool ok, string? error)> SaveLoyaltyRateAsync(int companyId, int branchId, decimal? branchRate, decimal? companyRate)
		{
			if (branchRate is < 0) return (false, "The branch rate cannot be negative.");
			if (companyRate is < 0) return (false, "The company rate cannot be negative.");

			var co = await _db.Companies.FirstOrDefaultAsync(c => c.CompanyID == companyId);
			if (co == null) return (false, "Company not found.");
			co.LoyaltyPointsPerCurrencyUnit = companyRate;

			var bs = await _db.BranchPosSettings.FirstOrDefaultAsync(x => x.BranchId == branchId);
			if (bs == null) { bs = new BranchPosSetting { BranchId = branchId }; _db.BranchPosSettings.Add(bs); }
			bs.LoyaltyPointsPerCurrencyUnit = branchRate;

			await _db.SaveChangesAsync();
			return (true, null);
		}

		// Category eligibility. An ITEM may still override its category (Item.LoyaltyEligible), and null
		// there means inherit - so switching a category off does not silently overrule an item that was
		// deliberately set either way.
		public async Task<(bool ok, string? error)> SaveCategoryLoyaltyAsync(int companyId, int categoryId, bool eligible)
		{
			var cat = await _db.ItemCategories.FirstOrDefaultAsync(c => c.ID == categoryId && c.CompanyID == companyId);
			if (cat == null) return (false, "Category not found.");
			cat.LoyaltyEligible = eligible;
			await _db.SaveChangesAsync();
			return (true, null);
		}

		// HM-19 — THE BRANCH'S TAX CODE.
		//
		// This writes ONE column. It does not compute tax, and it does not create tax codes: the codes are the
		// company's (Accounting > Tax codes) and the rate is resolved per line by PosOrderService, which has
		// looked at this column since HM-D38. What was missing was any way to SET it outside a dev endpoint,
		// so a branch in a different tax jurisdiction could not be configured by the people who run it.
		//
		// NULL IS A REAL VALUE: it means "this branch has no opinion, use the company default". It is not the
		// same as pinning a 0% code, which means "this branch is deliberately exempt" and survives a change to
		// the company default. The screen offers the two separately for that reason.
		public async Task<(bool ok, string? error)> SaveBranchTaxCodeAsync(int companyId, int branchId, int? taxCodeId)
		{
			// The code must belong to THIS company. Without this check a branch could be pinned to another
			// tenant's tax code, and every sale on it would then be rated from a rate nobody here can see.
			if (taxCodeId is > 0)
			{
				var tc = await _db.TaxCodes.AsNoTracking()
					.FirstOrDefaultAsync(t => t.ID == taxCodeId.Value && t.CompanyID == companyId);
				if (tc == null) return (false, "That tax code does not belong to this company.");
				if (!tc.IsActive) return (false, "That tax code is not active.");
				// WHT is withholding tax on what the company PAYS. Pinning it to a till would rate every sale
				// at a purchase-side rate, which is wrong in a way no one would notice until a VAT return.
				if (tc.Kind != "VAT") return (false, "Only a VAT code can be a branch's sales tax code.");
			}

			var bs = await _db.BranchPosSettings.FirstOrDefaultAsync(x => x.BranchId == branchId);
			if (bs == null) { bs = new BranchPosSetting { BranchId = branchId }; _db.BranchPosSettings.Add(bs); }
			bs.DefaultTaxCodeId = taxCodeId is > 0 ? taxCodeId : null;
			await _db.SaveChangesAsync();
			return (true, null);
		}

		// HM-17 — EXTRA BARCODES PER ITEM, each in its own unit.
		//
		// The till has resolved these since HM-2 and nothing has ever written one except a dev seeding
		// endpoint, so a shop that received a new case size had no way to make it scannable.
		//
		// TWO CHECKS, AND BOTH EXIST BECAUSE THE TILL ALREADY FAILS ON THEM AT SELL TIME:
		//   - A duplicate barcode makes the lane refuse the scan outright ("registered on more than one
		//     item — the data must be corrected"). Catching it here means the queue never meets it.
		//   - A barcode inside the branch's SCALE prefix range is refused the same way, because the lane
		//     parses that range instead of looking it up. A fixed product barcode printed there can never
		//     be sold, and the error surfaces at a till rather than at the desk that typed it.
		public Task<List<ItemBarcodeRow>> GetItemBarcodesAsync(int companyId, int itemId) =>
			_db.ItemBarcodes.AsNoTracking()
				.Where(b => b.ItemId == itemId && _db.Items.Any(i => i.ID == b.ItemId && i.CompanyID == companyId))
				.Select(b => new ItemBarcodeRow { Id = b.ID, Barcode = b.Barcode, UoMId = b.UoMId })
				.ToListAsync();

		public async Task<(bool ok, string? error)> AddItemBarcodeAsync(int companyId, int branchId, int itemId, string? barcode, int uomId)
		{
			barcode = (barcode ?? "").Trim();
			if (barcode.Length == 0) return (false, "A barcode is required.");
			if (uomId <= 0) return (false, "A barcode belongs to a unit — choose the one this barcode is printed on.");

			var item = await _db.Items.AsNoTracking().FirstOrDefaultAsync(i => i.ID == itemId && i.CompanyID == companyId);
			if (item == null) return (false, "Item not found.");

			// the scale range: the lane PARSES anything starting with this prefix, so a product barcode in it
			// is unsellable by construction.
			var setting = await _db.BranchPosSettings.AsNoTracking().FirstOrDefaultAsync(x => x.BranchId == branchId);
			var prefix = (setting?.ScaleBarcodePrefix ?? "").Trim();
			if (prefix.Length > 0 && barcode.StartsWith(prefix, StringComparison.Ordinal))
				return (false, $"A barcode starting with {prefix} is inside the scale range: the till parses it as a weight label instead of looking it up, so it could never be sold.");

			bool onItem = await _db.Items.AnyAsync(i => i.CompanyID == companyId && i.Barcode == barcode);
			bool onExtra = await _db.ItemBarcodes.AnyAsync(b => b.Barcode == barcode && _db.Items.Any(i => i.ID == b.ItemId && i.CompanyID == companyId));
			if (onItem || onExtra) return (false, $"Barcode {barcode} is already registered in this company — the till refuses a barcode it finds on two items.");

			_db.ItemBarcodes.Add(new Models.Context.Inventory.ItemBarcode { ItemId = itemId, Barcode = barcode, UoMId = uomId });
			await _db.SaveChangesAsync();
			return (true, null);
		}

		public async Task<(bool ok, string? error)> DeleteItemBarcodeAsync(int companyId, int id)
		{
			var row = await _db.ItemBarcodes.FirstOrDefaultAsync(b => b.ID == id
				&& _db.Items.Any(i => i.ID == b.ItemId && i.CompanyID == companyId));
			if (row == null) return (false, "Barcode not found.");
			_db.ItemBarcodes.Remove(row);
			await _db.SaveChangesAsync();
			return (true, null);
		}

		// HM-13 — THE SCALE LABEL FORMAT, from a screen instead of an UPDATE statement.
		//
		// Everything the lane needs to PARSE a scale label lived only in SQL until now: the prefix that
		// claims a barcode range, the field widths, and the check algorithm. A shop that bought a new scale
		// had to find someone with database access before it could sell a kilo of anything.
		//
		// IT VALIDATES THE SHAPE, because a wrong format does not fail loudly - it silently reads the wrong
		// digits as a weight and sells 8.5 kg of apples as 0.850. The total length is checked against the
		// EAN-13 the printer actually produces, and a prefix that is not digits is refused outright.
		public async Task<(bool ok, string? error)> SaveScaleSettingsAsync(int branchId, string? prefix, int itemCodeLength, int valueLength, int valueDecimals, string valueType, string checkAlgo)
		{
			prefix = (prefix ?? "").Trim();
			if (prefix.Length == 0) return (false, "The scale barcode prefix is required.");
			if (!prefix.All(char.IsDigit)) return (false, "The scale barcode prefix must be digits only.");
			if (itemCodeLength <= 0 || valueLength <= 0) return (false, "The item-code and value lengths must both be greater than zero.");
			if (valueDecimals < 0 || valueDecimals > valueLength) return (false, "The value decimals cannot exceed the value length.");
			// prefix + item code + value + ONE check digit is the label the printer emits.
			int total = prefix.Length + itemCodeLength + valueLength + 1;
			if (total != 13) return (false, $"The format describes a {total}-digit label; a scale prints 13 (prefix + item code + value + one check digit).");
			if (valueType != "Weight") return (false, "Only weight-embedded scale labels are supported; price-embedded labels are not.");
			if (checkAlgo != "EanMod10") return (false, "The only supported check algorithm is EanMod10.");

			var s = await _db.BranchPosSettings.FirstOrDefaultAsync(x => x.BranchId == branchId);
			if (s == null) { s = new BranchPosSetting { BranchId = branchId }; _db.BranchPosSettings.Add(s); }
			s.ScaleBarcodePrefix = prefix;
			s.ScaleItemCodeLength = itemCodeLength;
			s.ScaleValueLength = valueLength;
			s.ScaleValueDecimals = valueDecimals;
			s.ScaleValueType = valueType;
			s.ScaleCheckAlgo = checkAlgo;
			await _db.SaveChangesAsync();
			return (true, null);
		}

		// HM-13 — an item sold by weight, and the code its label carries.
		public async Task<(bool ok, string? error)> SaveWeighedItemAsync(int companyId, int itemId, bool isWeighted, int? scaleCode)
		{
			var item = await _db.Items.FirstOrDefaultAsync(i => i.ID == itemId && i.CompanyID == companyId);
			if (item == null) return (false, "Item not found.");

			if (isWeighted)
			{
				if (scaleCode is not > 0) return (false, "A weighed item needs the scale code its label carries.");
				// The code is what the lane looks the item up BY, so a duplicate would make one of the two
				// unreachable - and which one is unreachable would depend on row order.
				bool taken = await _db.Items.AnyAsync(i => i.CompanyID == companyId && i.ID != itemId && i.ScaleCode == scaleCode);
				if (taken) return (false, $"Scale code {scaleCode} is already used by another item in this company.");
				item.IsWeighted = true;
				item.ScaleCode = scaleCode;
			}
			else
			{
				// Clearing the flag clears the code with it: a code left behind on a non-weighed item is a
				// reserved number nobody can see and the next item that wants it is refused for no visible reason.
				item.IsWeighted = false;
				item.ScaleCode = null;
			}
			await _db.SaveChangesAsync();
			return (true, null);
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
			if (string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(name)) return (false, "Code and name are required");
			if (await _db.DiningAreas.AnyAsync(a => a.BranchId == branchId && a.Code == code && a.ID != id)) return (false, "That hall code is already used in this branch");
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
			if (ex == null) return (false, "Hall not found");
			if (await _db.RestaurantTables.AnyAsync(t => t.DiningAreaId == id)) return (false, "Cannot delete: this hall still has tables");
			_db.DiningAreas.Remove(ex); await _db.SaveChangesAsync();
			return (true, null);
		}

		// ---- kitchen stations ----
		public Task<List<KitchenStation>> GetStationsAsync(int branchId) =>
			_db.KitchenStations.AsNoTracking().Where(s => s.BranchId == branchId).OrderBy(s => s.Code).ToListAsync();

		public async Task<(bool ok, string? error)> SaveStationAsync(int branchId, int id, string code, string name, string? nameEn, string type, bool isActive)
		{
			if (string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(name)) return (false, "Code and name are required");
			if (await _db.KitchenStations.AnyAsync(s => s.BranchId == branchId && s.Code == code && s.ID != id)) return (false, "That station code is already used in this branch");
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

		public async Task<(bool ok, string? error)> SaveDriverAsync(int branchId, int id, string name, string? nameEn, string phone, bool isActive)
		{
			if (string.IsNullOrWhiteSpace(name)) return (false, "Driver name is required");
			if (id > 0)
			{
				var ex = await _db.Drivers.FirstOrDefaultAsync(d => d.ID == id && d.BranchId == branchId);
				if (ex == null) return (false, "Driver not found");
				ex.Name = name.Trim(); ex.NameEn = string.IsNullOrWhiteSpace(nameEn) ? null : nameEn.Trim(); ex.Phone = phone?.Trim() ?? ""; ex.IsActive = isActive;
			}
			else _db.Drivers.Add(new Driver { BranchId = branchId, Name = name.Trim(), NameEn = string.IsNullOrWhiteSpace(nameEn) ? null : nameEn.Trim(), Phone = phone?.Trim() ?? "", IsActive = isActive });
			await _db.SaveChangesAsync();
			return (true, null);
		}

		public async Task<(bool ok, string? error)> DeleteDriverAsync(int id)
		{
			var ex = await _db.Drivers.FirstOrDefaultAsync(d => d.ID == id);
			if (ex == null) return (false, "Driver not found");
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
			if (string.IsNullOrWhiteSpace(name)) return (false, "Zone name is required");
			if (fee < 0) return (false, "The fee cannot be negative");
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
			if (ex == null) return (false, "Zone not found");
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
			if (!SourcingMethods.Contains(method)) return (false, "Unknown sourcing method");
			if (!await _db.Items.AnyAsync(i => i.ID == itemId && i.CompanyID == companyId)) return (false, "Item not found");
			bool hasBom = await _db.ItemComponents.AnyAsync(c => c.CompanyID == companyId && c.ParentItemId == itemId);

			// per-method guards (the user's decisions from the design doc)
			if (method == "WorkOrder" && !hasBom) return (false, "A work order requires a bill of materials for the item");
			if (method == "RecipeAtSale" && !hasBom) return (false, "Recipe-on-sale requires a bill of materials for the item");
			if (method == "FinishedFromBranch" || method == "SemiFromBranchComplete")
			{
				if (sourceBranchId == null) return (false, "You must choose a source branch");
				if (sourceBranchId == branchId) return (false, "The source branch cannot be the same branch");
				if (!await _db.Branches.AnyAsync(b => b.ID == sourceBranchId)) return (false, "Source branch not found");
				if (string.IsNullOrWhiteSpace(transferTiming)) transferTiming = "Prepaid";
				if (transferTiming != "Prepaid" && transferTiming != "AtSale") return (false, "Invalid transfer timing");
			}
			else { sourceBranchId = null; transferTiming = null; }   // helper fields only apply to 2/3
			if (method == "SemiFromBranchComplete")
			{
				if (semiFinishedItemId == null) return (false, "You must choose the semi-finished item");
				if (semiFinishedItemId == itemId) return (false, "The semi-finished item cannot be the same as the finished item");
				if (!await _db.Items.AnyAsync(i => i.ID == semiFinishedItemId && i.CompanyID == companyId)) return (false, "Semi-finished item not found");
				if (!hasBom) return (false, "Finishing requires a bill of materials for the finished item");
				if (!await _db.ItemComponents.AnyAsync(c => c.CompanyID == companyId && c.ParentItemId == itemId && c.ComponentItemId == semiFinishedItemId))
					return (false, "The semi-finished item must be part of the finished item's bill of materials");
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
			if (ex == null) return (false, "Station not found");
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
			if (string.IsNullOrWhiteSpace(code)) return (false, "Table code is required");
			code = code.Trim();
			shape = new[] { "Square", "Round", "Rect" }.Contains(shape) ? shape : "Square";
			var area = await _db.DiningAreas.AsNoTracking().FirstOrDefaultAsync(a => a.ID == diningAreaId);
			if (area == null) return (false, "Hall not found");
			// code unique within the branch (across its areas) — case/space-insensitive
			var branchAreaIds = await _db.DiningAreas.Where(a => a.BranchId == area.BranchId).Select(a => a.ID).ToListAsync();
			if (await _db.RestaurantTables.AnyAsync(t => branchAreaIds.Contains(t.DiningAreaId) && t.Code == code && t.ID != id))
				return (false, $"Table code «{code}» is already used in this branch");
			if (id > 0)
			{
				var ex = await _db.RestaurantTables.FirstOrDefaultAsync(t => t.ID == id);
				if (ex == null) return (false, "Table not found");
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
			if (ex == null) return (false, "Table not found");
			_db.RestaurantTables.Remove(ex); await _db.SaveChangesAsync();
			return (true, null);
		}

		public async Task<(bool ok, string? error)> RegenerateQrAsync(int id)
		{
			var ex = await _db.RestaurantTables.FindAsync(id);
			if (ex == null) return (false, "Table not found");
			ex.QrToken = NewToken(); await _db.SaveChangesAsync();
			return (true, null);
		}

		// ---- cashier setup (RC-1): quick-touch menu ----
		public Task<List<PosMenuGroup>> GetMenuGroupsAsync(int branchId) =>
			_db.PosMenuGroups.AsNoTracking().Where(g => g.BranchId == branchId).OrderBy(g => g.Sort).ThenBy(g => g.ID).ToListAsync();

		public async Task<(bool ok, string? error)> SaveMenuGroupAsync(int branchId, int id, string name, string? nameEn, int sort, bool isActive, int? kitchenStationId = null)
		{
			if (string.IsNullOrWhiteSpace(name)) return (false, "Group name is required");
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
			if (ex == null) return (false, "Group not found");
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
			if (itemId <= 0) return (false, "Choose an item");
			if (await _db.PosQuickItems.AnyAsync(q => q.BranchId == branchId && q.ItemId == itemId)) return (false, "The item is already added as a quick button in this branch");
			var maxSort = await _db.PosQuickItems.Where(q => q.BranchId == branchId && q.GroupId == groupId).Select(q => (int?)q.Sort).MaxAsync() ?? 0;
			_db.PosQuickItems.Add(new PosQuickItem { BranchId = branchId, GroupId = groupId, ItemId = itemId, Sort = maxSort + 1, IsActive = true });
			await _db.SaveChangesAsync();
			return (true, null);
		}

		public async Task<(bool ok, string? error)> RemoveQuickItemAsync(int id)
		{
			var ex = await _db.PosQuickItems.FindAsync(id);
			if (ex == null) return (false, "Button not found");
			_db.PosQuickItems.Remove(ex); await _db.SaveChangesAsync();
			return (true, null);
		}

		public async Task<(bool ok, string? error)> SaveQuickItemAsync(int id, int? groupId, int sort)
		{
			var ex = await _db.PosQuickItems.FindAsync(id);
			if (ex == null) return (false, "Button not found");
			ex.GroupId = groupId; ex.Sort = sort;
			await _db.SaveChangesAsync();
			return (true, null);
		}

		public async Task<(bool ok, string? error)> SetQuickCodeAsync(int companyId, int itemId, string? code)
		{
			var item = await _db.Items.FirstOrDefaultAsync(i => i.ID == itemId && i.CompanyID == companyId);
			if (item == null) return (false, "Item not found");
			code = string.IsNullOrWhiteSpace(code) ? null : code.Trim();
			if (code != null && await _db.Items.AnyAsync(i => i.CompanyID == companyId && i.QuickCode == code && i.ID != itemId))
				return (false, $"Quick code «{code}» is already used by another item");
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
			if (string.IsNullOrWhiteSpace(dto.Name)) return (false, "Group name is required", 0);
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
			if (g == null) return (false, "Group not found");
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
			if (g == null) return (false, "Group not found");
			var item = await _db.Items.FirstOrDefaultAsync(i => i.ID == linkedItemId && i.CompanyID == companyId);
			if (item == null) return (false, "The linked item was not found");
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
			if (o == null) return (false, "Option not found");
			// guard company via its group
			var g = await _db.ModifierGroups.FirstOrDefaultAsync(x => x.ID == o.GroupId && x.CompanyID == companyId);
			if (g == null) return (false, "Not allowed");
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
			if (g == null) return (false, "Group not found");
			if (!await _db.Items.AnyAsync(i => i.ID == itemId && i.CompanyID == companyId)) return (false, "Item not found");
			if (await _db.ItemModifierGroups.AnyAsync(l => l.GroupId == groupId && l.ItemId == itemId)) return (true, null);   // already linked
			var sort = (await _db.ItemModifierGroups.Where(l => l.ItemId == itemId).MaxAsync(l => (int?)l.Sort) ?? 0) + 1;
			_db.ItemModifierGroups.Add(new ItemModifierGroup { GroupId = groupId, ItemId = itemId, Sort = sort });
			await _db.SaveChangesAsync();
			return (true, null);
		}

		public async Task<(bool ok, string? error)> DetachGroupFromItemAsync(int companyId, int linkId)
		{
			var l = await _db.ItemModifierGroups.FirstOrDefaultAsync(x => x.ID == linkId);
			if (l == null) return (false, "Link not found");
			var g = await _db.ModifierGroups.FirstOrDefaultAsync(x => x.ID == l.GroupId && x.CompanyID == companyId);
			if (g == null) return (false, "Not allowed");
			_db.ItemModifierGroups.Remove(l);
			await _db.SaveChangesAsync();
			return (true, null);
		}

		// ================= Payment methods (setup only — no GL/stock) =================
		public async Task<List<BranchPaymentMethod>> GetPaymentMethodsAsync(int branchId) =>
			await _db.BranchPaymentMethods.AsNoTracking().Where(p => p.BranchId == branchId).OrderBy(p => p.Sort).ThenBy(p => p.ID).ToListAsync();

		public async Task<(bool ok, string? error)> SavePaymentMethodAsync(int branchId, int id, string paymentMethod, string? displayName, string? displayNameEn, int? targetAccountId, bool isActive, int sort)
		{
			paymentMethod = (paymentMethod ?? "").Trim();
			if (paymentMethod.Length == 0) return (false, "Payment method type is required");

			// THE TENDER'S GL ACCOUNT IS NOT A FREE-TEXT ID. PosOrderService reads TargetAccountId and uses it
			// DIRECTLY as the debit account for every payment taken on this method, so an id accepted here
			// without a check becomes a journal line nobody reviewed. SaveBranchTaxCodeAsync in this same file
			// already guards its code this way and explains why; this method accepted any integer at all.
			//
			// TWO CHECKS, and the second is the one that was actually wrong in the data:
			//
			//   1. THE POS CATALOGUE COMPANY - not the branch's. POS cash and GL accounts are pinned to
			//      PosCompanyId by deliberate policy (see PosCompanyPolicy) while operating branches belong to
			//      their own companies, so "the account must belong to the branch's company" would refuse every
			//      correctly configured branch outside the catalogue company. The pin is the rule here.
			//
			//   2. POSTABLE. Branch 17's Cash tender pointed at 1101 - a header with two children and
			//      IsPostable = 0 - while branch 15's correctly pointed at its leaf 110101, which is also what
			//      this file's own comment describes as the end of the cash chain. It had never fired: zero
			//      PosPayments and zero journal lines referenced it. So this was a latent defect waiting for
			//      branch 17's first cash sale, not damage already done - which is exactly the kind that a
			//      validation catches cheaply and a reconciliation catches expensively.
			if (targetAccountId is > 0)
			{
				var acct = await _db.Accounts.AsNoTracking()
					.Where(a => a.ID == targetAccountId.Value)
					.Select(a => new { a.ID, a.Code, a.CompanyID, a.IsPostable, a.IsActive })
					.FirstOrDefaultAsync();
				if (acct == null) return (false, "That account does not exist.");
				if (acct.CompanyID != PosCompanyId) return (false, "That account does not belong to the POS catalogue company.");
				if (!acct.IsActive) return (false, $"Account {acct.Code} is not active.");
				if (!acct.IsPostable) return (false, $"Account {acct.Code} is a header account and cannot take postings; choose a postable account.");
			}
			var p = id > 0 ? await _db.BranchPaymentMethods.FirstOrDefaultAsync(x => x.ID == id && x.BranchId == branchId) : null;
			if (p == null)
			{
				if (await _db.BranchPaymentMethods.AnyAsync(x => x.BranchId == branchId && x.PaymentMethod == paymentMethod))
					return (false, $"Payment method «{paymentMethod}» is already defined for this branch");
				p = new BranchPaymentMethod { BranchId = branchId }; _db.BranchPaymentMethods.Add(p);
			}
			p.PaymentMethod = paymentMethod; p.DisplayName = string.IsNullOrWhiteSpace(displayName) ? null : displayName.Trim();
			p.DisplayNameEn = string.IsNullOrWhiteSpace(displayNameEn) ? null : displayNameEn.Trim();
			p.TargetAccountId = targetAccountId; p.IsActive = isActive; p.Sort = sort;
			await _db.SaveChangesAsync();
			return (true, null);
		}

		public async Task<(bool ok, string? error)> DeletePaymentMethodAsync(int branchId, int id)
		{
			var p = await _db.BranchPaymentMethods.FirstOrDefaultAsync(x => x.ID == id && x.BranchId == branchId);
			if (p == null) return (false, "Not found");
			_db.BranchPaymentMethods.Remove(p);
			await _db.SaveChangesAsync();
			return (true, null);
		}

		// ================= Cashier roles (setup only — no GL/stock) =================
		private static readonly string[] PosRoles = { "pos-waiter", "pos-kitchen", "pos-cashier", "pos-manager" };

		public async Task<List<BranchEmployeeDto>> GetBranchEmployeesAsync(int branchId) =>
			(await _db.Employee.AsNoTracking().Where(e => e.BranchID == branchId)
				.OrderByDisplayName()
				.Select(e => new { e.ID, e.FullName, e.FullNameEn })
				.ToListAsync())
				.Select(e => new BranchEmployeeDto { Id = e.ID, Name = EmployeeNames.Of(e.FullName, e.FullNameEn, e.ID) })
				.ToList();

		public async Task<List<BranchRoleDto>> GetBranchRolesAsync(int branchId)
		{
			var roles = await _db.BranchUserRoles.AsNoTracking().Where(r => r.BranchId == branchId).OrderBy(r => r.PosRole).ToListAsync();
			var empIds = roles.Select(r => r.EmployeeId).Distinct().ToList();
			var emps = await _db.Employee.AsNoTracking().Where(e => empIds.Contains(e.ID))
				.Select(e => new { e.ID, e.FullName, e.FullNameEn })
				.ToDictionaryAsync(e => e.ID, e => EmployeeNames.Of(e.FullName, e.FullNameEn));
			return roles.Select(r => new BranchRoleDto { Id = r.ID, EmployeeId = r.EmployeeId, EmployeeName = emps.TryGetValue(r.EmployeeId, out var n) ? (n ?? ("#" + r.EmployeeId)) : ("#" + r.EmployeeId), PosRole = r.PosRole }).ToList();
		}

		public async Task<(bool ok, string? error)> AssignPosRoleAsync(int branchId, int employeeId, string posRole)
		{
			if (!PosRoles.Contains(posRole)) return (false, "Invalid role");
			var emp = await _db.Employee.FirstOrDefaultAsync(e => e.ID == employeeId && e.BranchID == branchId);
			if (emp == null) return (false, "The employee was not found in this branch");
			if (await _db.BranchUserRoles.AnyAsync(r => r.BranchId == branchId && r.EmployeeId == employeeId && r.PosRole == posRole))
				return (true, null);   // already assigned
			_db.BranchUserRoles.Add(new BranchUserRole { BranchId = branchId, EmployeeId = employeeId, PosRole = posRole, IsActive = true, CreatedAt = DateTime.UtcNow });
			await _db.SaveChangesAsync();
			return (true, null);
		}

		public async Task<(bool ok, string? error)> RemovePosRoleAsync(int branchId, int id)
		{
			var r = await _db.BranchUserRoles.FirstOrDefaultAsync(x => x.ID == id && x.BranchId == branchId);
			if (r == null) return (false, "Not found");
			_db.BranchUserRoles.Remove(r);
			await _db.SaveChangesAsync();
			return (true, null);
		}

		// ================= POS-1: terminals + shifts (setup only — no GL/stock) =================
		// THE COMPANY THIS TILL SELLS FOR, FROM CONFIGURATION. It was `const int PosCompanyId = 1;` - a
		// deployment fact compiled into five separate files, where it could not be changed without a
		// rebuild and the five copies could drift apart in silence. Same treatment the storefront
		// company already had: PosCompanyOptions, bound to the "Pos" section, read here and nowhere else.
		private readonly int PosCompanyId;

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

		public async Task<(bool ok, string? error, int id)> SaveTerminalAsync(int branchId, int id, string code, string name, string? nameEn, string? receiptPrefix, int? cashAccountId, bool autoCreateCash, bool isActive, int paperWidthMm = 80, int copies = 1, string? printerName = null)
		{
			code = (code ?? "").Trim();
			if (code.Length == 0) return (false, "Terminal code is required", 0);
			if (await _db.PosTerminals.AnyAsync(t => t.BranchId == branchId && t.Code == code && t.ID != id))
				return (false, $"Terminal code «{code}» is already used in this branch", 0);
			var t = id > 0 ? await _db.PosTerminals.FirstOrDefaultAsync(x => x.ID == id && x.BranchId == branchId) : null;
			bool isNew = t == null;
			if (t == null) { t = new PosTerminal { BranchId = branchId, NextReceiptNo = 1, CreatedAt = DateTime.UtcNow }; _db.PosTerminals.Add(t); }
			t.NameEn = string.IsNullOrWhiteSpace(nameEn) ? null : nameEn.Trim();
			// HM-D5-أ: the ReceiptPrefix must NOT change once the terminal has issued orders — an offline receipt already
			// generated under the old prefix would then fail the suffix-parse (become a ReceiptNoMismatch conflict) and stop
			// protecting the counter at the worst moment. Changing it is allowed only while the terminal has no orders.
			if (!isNew)
			{
				var newPfx = string.IsNullOrWhiteSpace(receiptPrefix) ? (code + "-") : receiptPrefix.Trim();
				if (!string.Equals(newPfx, t.ReceiptPrefix, StringComparison.Ordinal) && await _db.PosOrders.AnyAsync(o => o.TerminalId == t.ID))
					return (false, "The receipt series prefix cannot be changed once orders have been issued on this terminal (it protects against offline receipt-number collisions)", t.ID);
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
			if (t == null) return (false, "Not found");
			if (await _db.PosShifts.AnyAsync(s => s.TerminalId == id && s.Status == "Open")) return (false, "A terminal with an open shift cannot be deleted");
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
			if (!await _db.PosTerminals.AnyAsync(t => t.ID == terminalId)) return (false, "Terminal not found");
			if (await _db.PosShifts.AnyAsync(s => s.TerminalId == terminalId && s.Status == "Open")) return (false, "There is already an open shift on this terminal");
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

			var zMoves = await GetShiftCashMovementsAsync(companyId, s.ID);
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
				CashMovedIn = zMoves.Where(m => m.Kind == "In").Sum(m => m.Amount),
				CashMovedOut = zMoves.Where(m => m.Kind == "Out").Sum(m => m.Amount),
				ClosingFloat = s.ClosingFloat, CashVariance = s.CashVariance,
			};
			return z;
		}

		// RC-6a: expected drawer cash for a shift = OpeningFloat + Σ cash payments − Σ cash refunds (refunds land in RC-6c).
		// Cash-only: card/KNet settle to the bank, not the drawer. Payments link to the shift via their order's ShiftId.
		// HM-21 — A BULK PRICE CHANGE, PREVIEWED BEFORE IT IS MADE.
		//
		// Re-pricing a category is the single most destructive routine action in a hypermarket: one wrong sign
		// or one misplaced decimal re-prices thousands of lines, and there is no undo because the old prices are
		// overwritten in place. So the screen shows exactly what will change BEFORE anything is written, and the
		// preview and the apply run THE SAME SELECTION AND THE SAME ARITHMETIC. A preview computed by separate
		// code is a preview of nothing: it agrees with the apply right up until the day one of them is edited.
		//
		// THE MARGIN FLOOR IS NOT RE-IMPLEMENTED HERE. IPricingService.CheckMarginAsync is the one place that
		// knows what a floor is, which cost it uses, and what Off/Warn/Block mean. A second copy of that rule
		// living in a price-change screen would drift from the one the till enforces, and the first anyone would
		// hear of it is a cashier selling under cost on a price this screen approved.
		private async Task<List<BulkPriceRow>> BuildBulkPriceRowsAsync(int companyId, int? categoryId, string? search,
			string mode, decimal value, int? currencyId, List<int>? restrictTo)
		{
			search = (search ?? "").Trim();
			var q = _db.Items.AsNoTracking().Where(i => i.CompanyID == companyId && i.IsActive);
			if (categoryId is > 0) q = q.Where(i => i.ItemCategoryId == categoryId.Value);
			if (search.Length > 0) q = q.Where(i => i.Name.Contains(search) || i.ItemCode.Contains(search) || i.Barcode.Contains(search));
			if (restrictTo != null) q = q.Where(i => restrictTo.Contains(i.ID));

			// An item with NO price is not re-priced. "+10%" of nothing is nothing, and setting it to a flat
			// amount here would quietly give a price to items somebody deliberately left unpriced.
			var items = await q.Where(i => i.SalesPrice != null)
				.OrderBy(i => i.Name)
				.Select(i => new { i.ID, i.ItemCode, i.Name, i.NameEn, i.SalesPrice })
				.Take(500).ToListAsync();

			int dp = await _rounding.DecimalsAsync(companyId, currencyId);
			var rows = new List<BulkPriceRow>();
			foreach (var it in items)
			{
				decimal oldP = it.SalesPrice!.Value;
				decimal raw = mode switch
				{
					"Percent" => oldP * (1m + value / 100m),
					"Amount"  => oldP + value,
					"Set"     => value,
					_          => oldP
				};
				// A price is money, so it rounds the way every other price in this company rounds.
				decimal newP = Math.Round(raw, dp, MidpointRounding.AwayFromZero);
				if (newP < 0m) newP = 0m;

				var chk = await _pricing.CheckMarginAsync(companyId, it.ID, newP, currencyId, null, DateTime.Today);
				rows.Add(new BulkPriceRow
				{
					ItemId = it.ID, Code = it.ItemCode, Name = it.Name, NameEn = it.NameEn,
					OldPrice = oldP, NewPrice = newP,
					MarginMode = chk.Mode, CostUnknown = chk.Skipped,
					BelowFloor = !chk.Skipped && !chk.Ok, FloorFunctional = chk.FloorFunctional
				});
			}
			return rows;
		}

		// HM-21 — THE ONE RULE ABOUT WHAT IS A LEGAL CHANGE, so the preview cannot show a table the
		// apply will refuse. A preview that renders a column of zeroes and is then rejected at the button
		// teaches the operator that the preview is decorative, which is the opposite of what it is for.
		public static string? ValidateBulkChange(string mode, decimal value)
		{
			if (mode != "Percent" && mode != "Amount" && mode != "Set") return "Unknown change type.";
			if (mode == "Set" && value < 0) return "A price cannot be negative.";
			if (mode == "Percent" && value <= -100m) return "A reduction of 100% or more is not a price change.";
			return null;
		}

		public Task<List<BulkPriceRow>> PreviewBulkPriceChangeAsync(int companyId, int? categoryId, string? search,
			string mode, decimal value, int? currencyId)
			=> BuildBulkPriceRowsAsync(companyId, categoryId, search, mode, value, currencyId, null);

		// APPLY. The caller sends back the exact item ids it was shown, so an item that entered the category
		// between the preview and the apply is NOT swept up in a change nobody looked at.
		// `seen` is the preview AS THE OPERATOR READ IT: item id -> the price shown beside it. An item whose
		// price has moved since then is REFUSED rather than re-priced, because the figure the operator
		// approved is no longer the figure that would be written. Without this, the claim that the preview
		// and the apply agree is true only while nobody else is working.
		public async Task<(int changed, int blocked, int unchanged, int stale, string? error)> ApplyBulkPriceChangeAsync(int companyId,
			int? categoryId, string? search, string mode, decimal value, int? currencyId, Dictionary<int, decimal> seen)
		{
			var bad = ValidateBulkChange(mode, value);
			if (bad != null) return (0, 0, 0, 0, bad);
			if (seen == null || seen.Count == 0) return (0, 0, 0, 0, "Nothing was selected to change.");

			var rows = await BuildBulkPriceRowsAsync(companyId, categoryId, search, mode, value, currencyId, seen.Keys.ToList());
			if (rows.Count == 0) return (0, 0, 0, 0, "Nothing matched — no price was changed.");

			int changed = 0, blocked = 0, unchanged = 0, stale = 0;
			foreach (var r in rows)
			{
				// Block mode refuses the WRITE, not just the display. A screen that shows a red row and saves it
				// anyway is worse than one with no check at all, because it has been seen to object.
				if (r.Blocked) { blocked++; continue; }
				// MOVED UNDER THE OPERATOR'S FEET since the preview was drawn.
				if (!seen.TryGetValue(r.ItemId, out var shown) || shown != (r.OldPrice ?? 0m)) { stale++; continue; }
				if (r.OldPrice == r.NewPrice) { unchanged++; continue; }
				var item = await _db.Items.FirstOrDefaultAsync(i => i.ID == r.ItemId && i.CompanyID == companyId);
				if (item == null) continue;
				item.SalesPrice = r.NewPrice;
				changed++;
			}
			if (changed > 0) await _db.SaveChangesAsync();
			return (changed, blocked, unchanged, stale, null);
		}

		// HM-20 — THE DRAWER ACCOUNT, in ONE place.
		//
		// Terminal drawer -> branch Cash method -> 110101, the same priority PayAsync and CloseShiftAsync use.
		// It was written out twice; a third copy for cash movements would be the one that drifts.
		private async Task<int> DrawerAccountAsync(int companyId, int terminalId)
		{
			var term = await _db.PosTerminals.AsNoTracking().FirstOrDefaultAsync(t => t.ID == terminalId);
			int drawer = term?.CashAccountId ?? 0;
			if (drawer == 0) drawer = await _db.Accounts.Where(a => a.CompanyID == companyId && a.Code == "110101").Select(a => a.ID).FirstOrDefaultAsync();
			return drawer;
		}

		// HM-20 — CASH IN OR OUT OF THE DRAWER, MID-SHIFT.
		//
		// THE DEFECT THIS FIXES: ExpectedCash was OpeningFloat + cash taken + cash tips, and nothing else. Any
		// cash that physically left the drawer before close — a safe drop, a supplier paid in cash, change
		// sent to another till — was invisible to it. The counted drawer was therefore short by exactly that
		// amount, and CloseShiftAsync booked the difference to 520111 as a CASH LOSS. A shop doing nightly drops
		// was accumulating a fictitious expense, and a genuine theft of the same size looked identical to it.
		//
		// WHY THERE IS NO NEW TABLE: cash leaving a till IS an accounting event — it moves money from the drawer
		// account to the safe, the bank or an expense. Recording it anywhere but the ledger would create a second
		// record of the same money that nothing reconciles. So it posts through the SOLE JournalEntryService with
		// SourceType "PosShiftCashMovement", and ExpectedCash reads it back from the ledger.
		// systemCounter: the counter account was resolved BY THE SYSTEM, not picked by a person from a list.
		// The IsPostable check below exists so an operator cannot drop cash onto a rollup heading they chose
		// by mistake. A POS refund has no such choice: it must debit the very account the credit note just
		// credited, and in this chart the receivables control is a parent that the AR subledger nonetheless
		// posts to - 1,343 lines of it. Refusing that one account would not protect anything; it would only
		// leave the refund outside the drawer expectation, which is the failure this whole method exists to
		// prevent. Every other guard - company, existence, not-the-drawer-itself - still applies.
		public async Task<(bool ok, string? error, int? journalEntryId)> RecordShiftCashMovementAsync(int companyId, int terminalId, int shiftId,
			string kind, decimal amount, int counterAccountId, string? note, DateTime date, int? userId,
			bool systemCounter = false)
		{
			kind = (kind ?? "").Trim();
			if (kind != "In" && kind != "Out") return (false, "The movement must be In or Out.", null);
			if (amount <= 0) return (false, "The amount must be greater than zero.", null);

			var s = await _db.PosShifts.AsNoTracking().FirstOrDefaultAsync(x => x.ID == shiftId && x.TerminalId == terminalId);
			if (s == null) return (false, "Shift not found", null);
			// A CLOSED SHIFT IS SETTLED. Its ExpectedCash and variance are already stored and its variance JE is
			// already posted; a movement added afterwards would change the expectation that was reconciled against
			// and silently invalidate a figure a manager has already signed off.
			if (s.Status == "Closed") return (false, "That shift is closed — a cash movement can only be recorded on an open shift.", null);

			int? shiftBranchCo = await _db.PosTerminals.Where(t => t.ID == terminalId)
				.Join(_db.Branches, t => t.BranchId, b => b.ID, (t, b) => (int?)b.CompanyID).FirstOrDefaultAsync();
			if (shiftBranchCo != companyId) return (false, L["This terminal belongs to another company — cross-company operations are blocked."].Value, null);

			int drawer = await DrawerAccountAsync(companyId, terminalId);
			if (drawer == 0) return (false, "The drawer cash account does not exist.", null);
			if (counterAccountId == drawer) return (false, "The other side cannot be the drawer itself.", null);
			var counter = await _db.Accounts.AsNoTracking().FirstOrDefaultAsync(a => a.ID == counterAccountId && a.CompanyID == companyId);
			if (counter == null) return (false, "That account does not belong to this company.", null);
			if (!counter.IsPostable && !systemCounter) return (false, "That account is a heading, not a postable account.", null);

			// The drawer figure is in the DOCUMENT currency; the JE posts in the functional. ONE conversion, the
			// same discipline CloseShiftAsync uses for the variance, so both lines carry an identical value and the
			// entry balances by construction.
			var (_, rate, ddp, fdp) = await ShiftCurrencyAsync(companyId, terminalId);
			decimal doc = Math.Round(amount, ddp, MidpointRounding.AwayFromZero);
			decimal amt = Math.Round(doc * rate, fdp, MidpointRounding.AwayFromZero);
			if (amt <= 0) return (false, "The amount rounds to zero in the functional currency.", null);

			bool outward = kind == "Out";
			string desc = (outward ? "\u0646\u0642\u062f \u062e\u0627\u0631\u062c \u0645\u0646 \u0627\u0644\u062f\u0631\u062c" : "\u0646\u0642\u062f \u062f\u0627\u062e\u0644 \u0625\u0644\u0649 \u0627\u0644\u062f\u0631\u062c")
				+ (string.IsNullOrWhiteSpace(note) ? "" : " — " + note!.Trim());
			var lines = new List<JournalLineInput>
			{
				// Out: the drawer gives the money up -> Cr drawer / Dr the receiving account (safe, bank, expense).
				// In:  the drawer receives it       -> Dr drawer / Cr the source account.
				new JournalLineInput { AccountId = drawer, Debit = outward ? 0 : amt, Credit = outward ? amt : 0, Description = desc },
				new JournalLineInput { AccountId = counterAccountId, Debit = outward ? amt : 0, Credit = outward ? 0 : amt, Description = desc },
			};
			var (jok, jerr, je) = await _journals.CreateAndPostAsync(new JournalEntryInput
			{
				CompanyID = companyId, EntryDate = date.Date, JournalType = "Auto",
				SourceType = "PosShiftCashMovement", SourceId = shiftId,
				Description = $"Shift #{shiftId} — " + desc, Lines = lines
			}, userId);
			if (!jok) return (false, "Could not post the cash movement: " + jerr, null);
			return (true, null, je?.ID);
		}

		// ==================== HM-PROMO: PUTTING A PROMOTION IN FRONT OF THE ENGINE ====================
		//
		// Every rule below is the ENGINE'S rule, read back from PosPromotionEngine and refused here instead
		// of silently doing nothing at the till. A promotion that cannot fire is worse than no promotion:
		// the shop believes an offer is running, the shelf label says so, and the customer is charged full
		// price. So BuyXGetY without a trigger, a percentage rule at 0%, a threshold with no bar - each one
		// is a refusal with the reason, not a saved row that no cashier will ever see take effect.
		public async Task<List<PosPromoRow>> GetPosPromotionsAsync(int companyId, int? branchId)
		{
			var q = _db.PosPromotions.AsNoTracking().Where(p => p.CompanyId == companyId);
			// A null BranchId on the ROW means "every branch", so a branch's list must include those too -
			// filtering on equality alone would hide the company-wide offers from the branch they apply to.
			if (branchId != null) q = q.Where(p => p.BranchId == null || p.BranchId == branchId);
			var rows = await q.OrderBy(p => p.Priority).ThenBy(p => p.Code).Select(p => new PosPromoRow
			{
				Id = p.ID, Code = p.Code, NameAr = p.NameAr, NameEn = p.NameEn, PromoType = p.PromoType,
				BranchId = p.BranchId, Priority = p.Priority, IsExclusive = p.IsExclusive, IsActive = p.IsActive,
				DiscountPct = p.DiscountPct, DiscountAmount = p.DiscountAmount, MinBasket = p.MinBasket,
				TriggerQty = p.TriggerQty, RewardQty = p.RewardQty, StartsAt = p.StartsAt, EndsAt = p.EndsAt,
			}).ToListAsync();

			var ids = rows.Select(r => r.Id).ToList();
			var counts = await _db.PosPromotionItems.AsNoTracking().Where(t => ids.Contains(t.PromotionId))
				.GroupBy(t => t.PromotionId).Select(g => new { g.Key, N = g.Count() }).ToListAsync();
			foreach (var r in rows) r.TargetCount = counts.FirstOrDefault(c => c.Key == r.Id)?.N ?? 0;
			return rows;
		}

		public async Task<(Models.Context.Pos.PosPromotion? promo, List<Models.Context.Pos.PosPromotionItem> targets)>
			GetPosPromotionAsync(int companyId, int id)
		{
			var p = await _db.PosPromotions.AsNoTracking().FirstOrDefaultAsync(x => x.ID == id && x.CompanyId == companyId);
			if (p == null) return (null, new List<Models.Context.Pos.PosPromotionItem>());
			var t = await _db.PosPromotionItems.AsNoTracking().Where(x => x.PromotionId == id).ToListAsync();
			return (p, t);
		}

		public async Task<(bool ok, string? error, int id)> SavePosPromotionAsync(
			int companyId, Models.Context.Pos.PosPromotion dto, List<Models.Context.Pos.PosPromotionItem> targets)
		{
			var code = (dto.Code ?? "").Trim();
			if (code.Length == 0) return (false, "The promotion needs a code - it is what sales reports filter on.", 0);
			var nameAr = (dto.NameAr ?? "").Trim();
			if (nameAr.Length == 0) return (false, "The Arabic name is required.", 0);
			// The English twin is never collapsed into the Arabic one; an empty twin falls back at READ time.
			var nameEn = (dto.NameEn ?? "").Trim();

			if (await _db.PosPromotions.AnyAsync(x => x.CompanyId == companyId && x.ID != dto.ID && x.Code == code))
				return (false, $"Another promotion already uses the code {code}.", 0);

			if (dto.BranchId != null)
			{
				bool okBranch = await _db.Branches.AnyAsync(b => b.ID == dto.BranchId && b.CompanyID == companyId);
				if (!okBranch) return (false, "That branch does not belong to this company.", 0);
			}

			// ---- the engine's own preconditions, refused here rather than at a silent till ----
			var type = (dto.PromoType ?? "PercentOff").Trim();
			switch (type)
			{
				case "BuyXGetY":
					if (dto.TriggerQty <= 0 || dto.RewardQty <= 0)
						return (false, "Buy X get Y needs both quantities above zero, or the engine skips it entirely.", 0);
					break;
				case "AmountOff":
					if (dto.DiscountAmount <= 0) return (false, "An amount-off promotion needs an amount above zero.", 0);
					break;
				case "ThresholdAmount":
					if (dto.MinBasket <= 0) return (false, "A threshold promotion needs the basket bar it unlocks at.", 0);
					if (dto.DiscountPct <= 0 && dto.DiscountAmount <= 0)
						return (false, "A threshold promotion needs either a percentage or an amount to hand back.", 0);
					break;
				default:   // PercentOff
					type = "PercentOff";
					if (dto.DiscountPct <= 0) return (false, "A percentage promotion needs a percentage above zero.", 0);
					break;
			}
			if (dto.DiscountPct < 0 || dto.DiscountPct > 100) return (false, "The percentage must be between 0 and 100.", 0);
			if (dto.MaxDiscountPct < 0 || dto.MaxDiscountPct > 100) return (false, "The ceiling must be between 0 and 100.", 0);
			if (dto.StartsAt != null && dto.EndsAt != null && dto.EndsAt <= dto.StartsAt)
				return (false, "The end must come after the start.", 0);

			// Targets: a row must name an item OR a category, never neither (which would match nothing) and
			// never both (which would be two rules wearing one row). No targets at all is legitimate and
			// means basket-wide - that is the engine's own reading of an empty membership list.
			var clean = new List<Models.Context.Pos.PosPromotionItem>();
			foreach (var t in targets ?? new List<Models.Context.Pos.PosPromotionItem>())
			{
				bool hasItem = t.ItemId is > 0, hasCat = t.CategoryId is > 0;
				if (!hasItem && !hasCat) continue;
				if (hasItem && hasCat) return (false, "A target names an item or a category, not both.", 0);
				var role = (t.Role ?? "Both").Trim();
				if (role != "Trigger" && role != "Reward" && role != "Both") role = "Both";
				clean.Add(new Models.Context.Pos.PosPromotionItem
				{
					ItemId = hasItem ? t.ItemId : null, CategoryId = hasCat ? t.CategoryId : null, Role = role,
				});
			}
			if (type == "BuyXGetY" && clean.Count > 0 && !clean.Any(t => t.Role == "Trigger" || t.Role == "Both"))
				return (false, "Buy X get Y needs at least one target that can TRIGGER it, or nothing ever counts towards the X.", 0);

			await using var tx = await ScopedTx.BeginOrJoinAsync(_db);
			Models.Context.Pos.PosPromotion row;
			if (dto.ID > 0)
			{
				row = await _db.PosPromotions.FirstOrDefaultAsync(x => x.ID == dto.ID && x.CompanyId == companyId)
					?? throw new InvalidOperationException("promotion not found");
				// The whole target set is replaced, so a target removed on the screen is removed here. Leaving
				// stale rows behind would keep an item in an offer the editor says it is no longer part of.
				var old = await _db.PosPromotionItems.Where(x => x.PromotionId == row.ID).ToListAsync();
				_db.PosPromotionItems.RemoveRange(old);
			}
			else
			{
				row = new Models.Context.Pos.PosPromotion { CompanyId = companyId, CreatedAt = DateTime.UtcNow };
				_db.PosPromotions.Add(row);
			}

			row.BranchId = dto.BranchId;
			row.Code = code;
			row.NameAr = nameAr;
			row.NameEn = nameEn;
			row.PromoType = type;
			row.TriggerQty = dto.TriggerQty;
			row.RewardQty = dto.RewardQty;
			row.DiscountPct = dto.DiscountPct;
			row.DiscountAmount = dto.DiscountAmount;
			row.MinBasket = dto.MinBasket;
			row.Priority = dto.Priority <= 0 ? 100 : dto.Priority;
			row.IsExclusive = dto.IsExclusive;
			row.MaxDiscountPct = dto.MaxDiscountPct;
			row.StartsAt = dto.StartsAt;
			row.EndsAt = dto.EndsAt;
			row.IsActive = dto.IsActive;
			await _db.SaveChangesAsync();

			foreach (var t in clean) { t.PromotionId = row.ID; _db.PosPromotionItems.Add(t); }
			await _db.SaveChangesAsync();
			await tx.CommitAsync();
			return (true, null, row.ID);
		}

		public async Task<(bool ok, string? error)> SetPosPromotionActiveAsync(int companyId, int id, bool active)
		{
			var row = await _db.PosPromotions.FirstOrDefaultAsync(x => x.ID == id && x.CompanyId == companyId);
			if (row == null) return (false, "That promotion does not belong to this company.");
			row.IsActive = active;
			await _db.SaveChangesAsync();
			return (true, null);
		}

		// ==================== HM-EXP: EXPIRY AT THE SCAN, NOT AT THE DRAWER ====================
		//
		// WHAT WAS WRONG. StockService already refuses to ISSUE an expired batch - FefoAllocateAsync
		// excludes them. But the lane only issues stock at PAYMENT, so a cashier could scan an item whose
		// every batch had expired, ring the basket, take the customer's money, and only then meet a refusal
		// from the stock writer. The control existed; it simply arrived after the drawer had opened.
		//
		// WHY THIS LIVES HERE AND NOT IN StockService. The allocator is inside one of the two architectural
		// writers, which CLAUDE.md says may not be changed without the owner's agreement, so this is a
		// deliberate, named READ of the same tables rather than an edit to that file. It writes nothing and
		// decides nothing about costing - it answers one question the till needs before it takes money.
		//
		// IT MUST STAY IN STEP WITH FefoAllocateAsync. The three things copied from it are: per-batch
		// balance = SUM(Direction * QtyBase) over movements WITH a batch; a batch is expired when
		// ExpiryDate.Date < today; and batched stock of zero beside physical on-hand means UNBATCHED stock,
		// which cannot be issued under expiry tracking at all. If that allocator's rule changes, this
		// changes with it - and the better long-term home for both is one shared read beside the allocator.
		public async Task<(bool sellable, string? reason, DateTime? nearestExpiry)> ExpirySellCheckAsync(
			int companyId, int itemId, int warehouseId, decimal neededBase, DateTime date)
		{
			var item = await _db.Items.AsNoTracking()
				.Where(i => i.ID == itemId && i.CompanyID == companyId)
				.Select(i => new { i.TrackExpiry, i.ItemCode })
				.FirstOrDefaultAsync();
			// Not expiry-tracked (or gone) - nothing for this check to say. Silence, not a refusal.
			if (item == null || !item.TrackExpiry) return (true, null, null);

			var perBatch = await _db.StockMovements.AsNoTracking()
				.Where(m => m.CompanyID == companyId && m.ItemId == itemId && m.WarehouseId == warehouseId && m.BatchId != null)
				.GroupBy(m => m.BatchId!.Value)
				.Select(g => new { BatchId = g.Key, Qty = g.Sum(x => x.Direction * x.QtyBase) })
				.Where(x => x.Qty > 0).ToListAsync();

			if (perBatch.Count == 0)
			{
				decimal onHand = await _db.StockBalances.AsNoTracking()
					.Where(b => b.CompanyID == companyId && b.ItemId == itemId && b.WarehouseId == warehouseId)
					.Select(b => (decimal?)b.QtyOnHand).FirstOrDefaultAsync() ?? 0m;
				// Physical stock with no batch behind it is invisible to expiry entirely. Selling it would
				// bypass the control rather than pass it, so the till refuses and says what to correct.
				if (onHand > 0)
					return (false, $"({item.ItemCode}) has stock that belongs to no batch, so its expiry cannot be checked - enter the batches before selling it.", null);
				return (true, null, null);   // genuinely no stock; the ordinary stock refusal will speak
			}

			var ids = perBatch.Select(p => p.BatchId).ToList();
			var batches = await _db.StockBatches.AsNoTracking().Where(b => ids.Contains(b.ID))
				.Select(b => new { b.ID, b.ExpiryDate }).ToListAsync();
			var today = date.Date;

			decimal valid = 0m, expired = 0m;
			DateTime? nearest = null;
			foreach (var p in perBatch)
			{
				var b = batches.FirstOrDefault(x => x.ID == p.BatchId);
				if (b == null) continue;
				bool isExpired = b.ExpiryDate != null && b.ExpiryDate.Value.Date < today;
				if (isExpired) { expired += p.Qty; continue; }
				valid += p.Qty;
				if (b.ExpiryDate != null && (nearest == null || b.ExpiryDate.Value.Date < nearest.Value))
					nearest = b.ExpiryDate.Value.Date;
			}

			if (valid < neededBase)
			{
				// Naming the expired quantity matters: "out of stock" sends a cashier to look on the shelf,
				// where the goods are sitting in plain sight. "Expired" sends them to take it off the shelf.
				string reason = expired > 0
					? $"({item.ItemCode}): only {valid:0.##} is within date; {expired:0.##} has expired and must come off the shelf."
					: $"({item.ItemCode}): only {valid:0.##} is available in date.";
				return (false, reason, nearest);
			}
			return (true, null, nearest);
		}

		// The movements of one shift, read back from the ledger.
		//
		// THE JOIN TO Accounts IS A LEFT JOIN, AND THAT IS LOAD-BEARING. It exists only to label the other side
		// of the movement, but THREE things derive from this one method: the drawer screen's list, the Z
		// report's in/out figures, and — through ShiftCashMovementNetAsync — ExpectedCash itself. As an inner
		// join, a counter account that no longer resolved would have dropped the whole movement from all three
		// at once. They would have agreed with each other and all been wrong, so no cross-check could catch it,
		// and a safe drop would vanish from the expectation: the drawer reconciles today and shows an
		// unexplained shortage tomorrow with the evidence for it deleted. A label is never worth a money figure.
		//
		// A REVERSED entry is excluded: reversing it is
		// how a mistaken movement is undone, and a reversed drop must stop affecting the drawer expectation.
		public async Task<List<ShiftCashMovementRow>> GetShiftCashMovementsAsync(int companyId, int shiftId)
		{
			var s = await _db.PosShifts.AsNoTracking().FirstOrDefaultAsync(x => x.ID == shiftId);
			if (s == null) return new List<ShiftCashMovementRow>();
			int drawer = await DrawerAccountAsync(companyId, s.TerminalId);
			if (drawer == 0) return new List<ShiftCashMovementRow>();

			var (_, rate, ddp, _f) = await ShiftCurrencyAsync(companyId, s.TerminalId);
			if (rate == 0m) rate = 1m;

			var rows = await (from je in _db.JournalEntries.AsNoTracking()
							  join dl in _db.JournalEntryLines.AsNoTracking() on je.ID equals dl.JournalEntryId
							  join cl in _db.JournalEntryLines.AsNoTracking() on je.ID equals cl.JournalEntryId
							  join acj in _db.Accounts.AsNoTracking() on cl.AccountId equals acj.ID into acg
							  from ac in acg.DefaultIfEmpty()
							  where je.CompanyID == companyId && je.SourceType == "PosShiftCashMovement" && je.SourceId == shiftId
								&& je.ReversedByEntryId == null && je.Status != "Reversed"
								&& dl.AccountId == drawer && cl.AccountId != drawer
							  orderby je.ID
							  select new ShiftCashMovementRow
							  {
								  JournalEntryId = je.ID, EntryNo = je.EntryNo, EntryDate = je.EntryDate,
								  Kind = dl.Credit > 0 ? "Out" : "In",
								  Amount = dl.Credit > 0 ? dl.Credit : dl.Debit,
								  CounterAccountId = cl.AccountId,
								  CounterAccountName = ac != null ? ac.Name : null,
								  CounterAccountMissing = ac == null,
								  Note = dl.Description
							  }).ToListAsync();

			// Back to the DOCUMENT currency the drawer is counted in — one division, mirroring the one multiplication.
			foreach (var r in rows) r.Amount = Math.Round(r.Amount / rate, ddp, MidpointRounding.AwayFromZero);
			return rows;
		}

		// HM-20: the NET of this shift's cash movements, in the document currency. Positive = the drawer gained.
		private async Task<decimal> ShiftCashMovementNetAsync(int companyId, PosShift s)
		{
			var rows = await GetShiftCashMovementsAsync(companyId, s.ID);
			decimal net = 0m;
			foreach (var r in rows) net += r.Kind == "Out" ? -r.Amount : r.Amount;
			return net;
		}

		public async Task<decimal> ExpectedCashAsync(int companyId, PosShift s)
		{
			decimal cashIn = await (from p in _db.PosPayments
									join o in _db.PosOrders on p.OrderId equals o.ID
									where o.CompanyId == companyId && o.ShiftId == s.ID && o.Status != "Voided" && p.PaymentMethod == "Cash"
									select (decimal?)p.Amount).SumAsync() ?? 0m;   // RC-6c: a voided order's cash was refunded OUT of the drawer → exclude
			// RC-5: cash tips physically land in the drawer → part of expected cash (card tips go to the bank)
			decimal cashTips = await _db.PosOrders.Where(o => o.CompanyId == companyId && o.ShiftId == s.ID && o.Status != "Voided" && o.TipMethod == "Cash").SumAsync(o => (decimal?)o.TipAmount) ?? 0m;
			var (_, _, ddp, _2) = await ShiftCurrencyAsync(companyId, s.TerminalId);   // HM-2: drawer cash is in the DOCUMENT currency
			// HM-20: cash that entered or left the drawer mid-shift (safe drops, float top-ups, cash payouts).
			// Zero when there are none, so every shift that never records one behaves exactly as it did before.
			decimal moved = await ShiftCashMovementNetAsync(companyId, s);
			return Math.Round(s.OpeningFloat + cashIn + cashTips + moved, ddp, MidpointRounding.AwayFromZero);
		}

		// RC-6a: close a shift with a counted drawer → compute expected + variance; post the over/short to 520111 (via the
		// SOLE JournalEntryService) against the terminal's drawer-cash account. variance>0 = overage (Dr cash/Cr 520111),
		// variance<0 = shortage (Dr 520111/Cr cash). variance==0 → no JE. Data + one balanced JE only; no stock, integrity-safe.
		public async Task<(bool ok, string? error)> CloseShiftAsync(int companyId, int terminalId, int shiftId, decimal closingFloat, int? closedByEmployeeId, DateTime date, string? userId)
		{
			var s = await _db.PosShifts.FirstOrDefaultAsync(x => x.ID == shiftId && x.TerminalId == terminalId);
			if (s == null) return (false, "Shift not found");
			if (s.Status == "Closed") return (false, "The shift is already closed");
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
				int drawer = await DrawerAccountAsync(companyId, terminalId);
				var over = await _db.Accounts.Where(a => a.CompanyID == companyId && a.Code == "520111").Select(a => (int?)a.ID).FirstOrDefaultAsync();
				if (drawer == 0 || over == null) { await tx.RollbackAsync(); return (false, "The cash account or the cash over/short account (520111) does not exist — run the SQL script"); }
				// SINGLE conversion of the variance to the functional currency ⇒ both JE lines use the SAME value ⇒ balances by construction.
				decimal amt = Math.Round(Math.Abs(variance) * rate, fdp, MidpointRounding.AwayFromZero);
				var lines = new List<JournalLineInput>
				{
					// overage (variance>0): more cash than book → Dr drawer / Cr 520111 (gain)
					// shortage (variance<0): missing cash → Dr 520111 (loss) / Cr drawer
					new JournalLineInput { AccountId = drawer, Debit = variance > 0 ? amt : 0, Credit = variance < 0 ? amt : 0, Description = "Shift drawer adjustment" },
					new JournalLineInput { AccountId = over.Value, Debit = variance < 0 ? amt : 0, Credit = variance > 0 ? amt : 0, Description = variance > 0 ? "زيادة نقدية درج" : "عجز نقدية درج" },
				};
				var (jok, jerr, je) = await _journals.CreateAndPostAsync(new JournalEntryInput
				{ CompanyID = companyId, EntryDate = date.Date, JournalType = "Auto", SourceType = "PosShiftClose", SourceId = s.ID, Description = $"Shift close #{s.ID} — drawer difference", Lines = lines }, null);
				if (!jok) { await tx.RollbackAsync(); return (false, "Could not post the drawer-difference entry: " + jerr); }
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
			if (s == null) return (false, "Shift not found", false);
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
			if (c == null) return (false, "Conflict not found");
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
			if (string.IsNullOrWhiteSpace(guestName)) return (false, "Guest name is required");
			if (partySize < 1) return (false, "Invalid party size");
			if (durationMinutes < 1) durationMinutes = 120;
			// no reservation for a time/day that has already passed (allow a small 1-minute skew)
			if (id == 0 && reservedAt < DateTime.Now.AddMinutes(-1)) return (false, "A reservation cannot be made in the past");
			// the table must belong to this branch
			var tableOk = await (from t in _db.RestaurantTables join a in _db.DiningAreas on t.DiningAreaId equals a.ID where t.ID == tableId && a.BranchId == branchId select t.ID).AnyAsync();
			if (!tableOk) return (false, "Invalid table");
			if (customerId != null && !await _db.Customers.AnyAsync(c => c.ID == customerId && c.CompanyID == companyId)) customerId = null;
			// OVERLAP: no two BOOKED reservations on the same table whose windows [start, start+duration) intersect
			var start = reservedAt; var end = reservedAt.AddMinutes(durationMinutes);
			var others = await _db.Reservations.Where(r => r.BranchId == branchId && r.TableId == tableId && r.ID != id && r.Status == "Booked").ToListAsync();
			if (others.Any(r => r.ReservedAtUtc < end && start < r.ReservedAtUtc.AddMinutes(r.DurationMinutes)))
				return (false, "There is an overlapping reservation on the same table at this time");
			if (id > 0)
			{
				var ex = await _db.Reservations.FirstOrDefaultAsync(r => r.ID == id && r.BranchId == branchId);
				if (ex == null) return (false, "Reservation not found");
				if (ex.Status != "Booked") return (false, "A reservation that is not active cannot be edited");
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
			if (status != "Cancelled" && status != "NoShow") return (false, "Invalid status");
			var r = await _db.Reservations.FirstOrDefaultAsync(x => x.ID == id && x.CompanyId == companyId);
			if (r == null) return (false, "Reservation not found");
			if (r.Status != "Booked") return (false, "The status of this reservation cannot be changed");   // forward-only from Booked
			r.Status = status;
			await _db.SaveChangesAsync();
			return (true, null);
		}
	}
}
