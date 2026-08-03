using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Pos;
using CrossBuy.Models.Context.Inventory;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL
{
	// RC-2 critical path. An OPEN order writes NOTHING to GL/stock.
	// Settlement (PayAsync) reuses the EXISTING engines only:
	//   ReceivableService.CreateSalesInvoiceAsync  → invoice + AR/revenue/VAT GL + stock issue + COGS (via StockService)
	//   ReceivableService.CreateReceiptAsync        → settle AR → cash
	//   PricingService.GetPriceAsync                → unit price
	//   TaxCode (Item.DefaultTaxCodeId → else company default VAT) → tax rate per line
	// No new GL/stock writer. All settlement in ONE transaction (atomic).
	public class PosOrderLineDto
	{
		public int Id { get; set; }
		public int ItemId { get; set; }
		public string Name { get; set; } = "";
		public string? Image { get; set; }
		public int? UoMId { get; set; }            // HM-2: the sold unit (null = base)
		public string? UoMName { get; set; }       // HM-2: unit name for the cart line (null when base)
		public decimal Qty { get; set; }
		public decimal UnitPrice { get; set; }
		public decimal DiscountAmount { get; set; }
		public decimal TaxRate { get; set; }
		public decimal LineTotal { get; set; }
		public decimal SentQty { get; set; }   // POS-4b: how much of this line is already at the kitchen
		public string? KdsStatus { get; set; }  // RC-3a: kitchen prep state (null before sent → New → Preparing → Ready)
		public List<PosOrderLineModifierDto> Modifiers { get; set; } = new();   // RC-4: chosen add-ons/alternatives on this line
	}

	// RC-4: a chosen modifier shown under its order line (display + carries the folded extra price for reference).
	public class PosOrderLineModifierDto
	{
		public int Id { get; set; }
		public int GroupId { get; set; }
		public int OptionId { get; set; }
		public string Name { get; set; } = "";
		public int LinkedItemId { get; set; }
		public decimal QtyDeducted { get; set; }
		public decimal ExtraPrice { get; set; }
	}

	// RC-4b: the add-time chooser — an item's modifier groups with their options.
	public class ModChooserGroupDto
	{
		public int GroupId { get; set; }
		public string Name { get; set; } = "";
		public string Type { get; set; } = "AddOn";   // Choice / AddOn
		public int MinSelect { get; set; }
		public int MaxSelect { get; set; }
		public List<ModChooserOptionDto> Options { get; set; } = new();
	}
	public class ModChooserOptionDto
	{
		public int OptionId { get; set; }
		public string Name { get; set; } = "";
		public decimal ExtraPrice { get; set; }
		public bool IsDefault { get; set; }
	}

	// Lightweight row for the per-table order list (a table may host several open orders — new + old — up to its seats).
	public class TableOrderDto
	{
		public int Id { get; set; }
		public string CustomerName { get; set; } = "";
		public decimal GrandTotal { get; set; }
		public int ItemCount { get; set; }
		public bool HasUnsent { get; set; }
		public int Guests { get; set; }
		public string OpenedAtUtc { get; set; } = "";
	}

	public class PosOrderDto
	{
		public int Id { get; set; }
		public string Status { get; set; } = "Open";
		public string OrderType { get; set; } = "Takeaway";
		public int? TableId { get; set; }
		public decimal SubTotal { get; set; }
		public decimal ServiceAmount { get; set; }
		public decimal TaxTotal { get; set; }
		public decimal GrandTotal { get; set; }
		public int? InvoiceId { get; set; }
		public int? CustomerId { get; set; }
		public string CustomerName { get; set; } = "";
		public int Guests { get; set; } = 1;
		public string? KdsStatus { get; set; }   // RC-3a: derived kitchen status of the whole order (from its sent lines)
		// POS-C1 Delivery (frozen on the order)
		public decimal DeliveryFee { get; set; }
		public int? DeliveryZoneId { get; set; }
		public string? DeliveryArea { get; set; }
		public string? DeliveryAddress { get; set; }
		public string? DeliveryPhone { get; set; }
		public int? DriverId { get; set; }
		public string? DriverName { get; set; }
		public string? DeliveryStatus { get; set; }
		public List<PosOrderLineDto> Lines { get; set; } = new();
	}

	public interface IPosOrderService
	{
		// HM-1-أ (ب-1-2): atomic per-terminal receipt-number allocation (single UPDATE … OUTPUT).
		Task<string> AllocateReceiptNoAsync(int terminalId);
		// HM-D5-أ: advance a terminal's counter past an offline receipt by parsing ONLY the numeric suffix after its prefix.
		// Returns (advanced, anomaly): anomaly != null ⇒ record a conflict but NEVER fail the sync.
		Task<(bool advanced, string? anomaly)> AdvanceCounterPastOfflineReceiptAsync(PosTerminal term, string? receiptNo);
		Task<(bool ok, string? error, int orderId)> CreateOrderAsync(int companyId, int branchId, string orderType, int? tableId, int? userId, int? terminalId = null, int? shiftId = null);
		Task<(bool ok, string? error)> AddLineAsync(int companyId, int orderId, int itemId, decimal qty, List<int>? optionIds = null, int? uomId = null);   // RC-4: optionIds = chosen modifier options
		Task<List<ModChooserGroupDto>> GetItemModifiersAsync(int companyId, int itemId);   // RC-4b: groups+options for the add-time chooser
		Task<(bool ok, string? error)> SetLineQtyAsync(int companyId, int orderId, int lineId, decimal qty);
		Task<(bool ok, string? error)> RemoveLineAsync(int companyId, int orderId, int lineId);
		Task<PosOrderDto?> GetOrderAsync(int companyId, int orderId);
		Task<int?> GetOpenOrderAsync(int companyId, int branchId);
		Task<int?> GetOpenOrderByTableAsync(int companyId, int branchId, int tableId);
		Task<List<TableOrderDto>> GetOpenOrdersByTableAsync(int companyId, int branchId, int tableId);
		Task<(bool ok, string? error, int orderId)> AddOrderOnTableAsync(int companyId, int branchId, int tableId, int? userId, int? terminalId = null, int? shiftId = null);
		Task CollapseEmptyTableDuplicatesAsync(int companyId, int branchId, int tableId);
		Task ParkOrphanWalkInsAsync(int companyId, int branchId);
		Task<(bool ok, string? error, int? invoiceId)> PayAsync(int companyId, int orderId, string method, int? userId, int splitParts = 1, decimal tipAmount = 0, string? tipMethod = null);
		Task<(bool ok, string? error, List<int> invoiceIds)> PaySplitByItemAsync(int companyId, int orderId, List<List<SplitAllocation>> bills, string method, int? userId, decimal tipAmount = 0, string? tipMethod = null);
		Task<(bool ok, string? error)> VoidPaidOrderAsync(int companyId, int orderId, int? userId);   // RC-6c-1
		Task<(bool ok, string? error, int? returnId)> ReturnOrderLinesAsync(int companyId, int orderId, List<SplitAllocation> allocations, int? userId);   // RC-6c-2
		Task<(bool ok, string? error, int? invoiceId, bool alreadySynced)> SyncPaidOrderAsync(int companyId, PosSyncOrderInput payload, int? userId);   // POS-9d
		Task<(bool ok, string? error, int? transferId)> ReplenishFinishedFromBranchAsync(int companyId, int branchId, int itemId, decimal qty, DateTime date, string? userId);   // BIS-3 method 2
		Task<(bool ok, string? error, int? workOrderId)> PrepareSemiFinishedAsync(int companyId, int branchId, int itemId, decimal qty, decimal labor, decimal overhead, DateTime date, string? userId);   // BIS-3 method 3
		Task<(bool ok, string? error, int? invoiceId)> PayTendersAsync(int companyId, int orderId, List<PosTenderInput> tenders, int? userId, decimal tipAmount = 0, string? tipMethod = null);
		Task<(bool ok, string? error, int sentLines)> SendToKitchenAsync(int companyId, int orderId);
		Task<(bool ok, string? error)> SetLineKdsStatusAsync(int companyId, int orderId, int lineId, string status);   // RC-3a: kitchen advances New→Preparing→Ready
		Task<List<KitchenTicketDto>> GetKitchenTicketsAsync(int companyId, int branchId);   // RC-3b: open orders with sent lines, for the KDS
		Task<(bool ok, string? error)> MoveOrderToTableAsync(int companyId, int orderId, int toTableId);
		Task<(bool ok, string? error)> SeatOrderOnTableAsync(int companyId, int orderId, int toTableId);
		Task<(bool ok, string? error)> MergeOrdersAsync(int companyId, int sourceOrderId, int targetOrderId);
		Task<(bool ok, string? error)> MergeTablesAsync(int companyId, int branchId, int sourceTableId, int targetTableId);
		Task<(bool ok, string? error)> VoidOrderAsync(int companyId, int orderId);
		Task<(bool ok, string? error)> SetOrderCustomerAsync(int companyId, int orderId, int customerId);
		Task<(bool ok, string? error)> SetGuestsAsync(int companyId, int orderId, int guests);
		Task<(bool ok, string? error)> HoldOrderAsync(int companyId, int orderId);
		Task<List<HeldOrderDto>> GetHeldOrdersAsync(int companyId, int branchId);
		Task<List<HeldOrderDto>> GetOpenInvoicesAsync(int companyId, int branchId);
		Task<(bool ok, string? error)> RecallHeldAsync(int companyId, int orderId);
		// POS-C1 Delivery
		Task<(bool ok, string? error)> SetOrderDeliveryAsync(int companyId, int orderId, int? customerId, int? zoneId, string? address, string? area, string? phone);
		Task<List<DeliveryZoneDto>> GetDeliveryZonesAsync(int branchId);
		Task<List<CustomerAddressDto>> GetCustomerAddressesAsync(int companyId, int customerId);
		Task<(bool ok, string? error, int? id)> AddCustomerAddressAsync(int companyId, int customerId, int? zoneId, string area, string address, string phone, bool isDefault);
		// POS-C2
		Task<(bool ok, string? error)> AssignDriverAsync(int companyId, int orderId, int? driverId);
		// POS-C3
		Task<(bool ok, string? error, string? deliveryStatus)> SetDeliveryStatusAsync(int companyId, int orderId, string status);
		// POS-C4
		Task<List<DeliveryOrderDto>> GetDeliveryOrdersAsync(int companyId, int branchId);
		// POS-B2
		Task<(bool ok, string? error, int? orderId)> ArriveReservationAsync(int companyId, int reservationId, int? terminalId, int? shiftId);
		// shared floor board (cashier + reservations)
		Task<List<FloorHallDto>> GetFloorAsync(int companyId, int branchId);
	}

	public class HeldOrderDto { public int Id { get; set; } public DateTime HeldAtUtc { get; set; } public int ItemCount { get; set; } public decimal GrandTotal { get; set; } public string FirstItem { get; set; } = ""; public string CustomerName { get; set; } = ""; public string HeldAtUtcIso { get; set; } = ""; public int? TableId { get; set; } public string TableCode { get; set; } = ""; public bool IsHeld { get; set; } public List<object> Items { get; set; } = new(); }
	// POS-4d-3b: one unit-allocation of an order line to a split "bill" (person). A bill = List<SplitAllocation>.
	public class SplitAllocation { public int LineId { get; set; } public decimal Qty { get; set; } }
	public class PosTenderInput { public string Method { get; set; } = ""; public decimal Amount { get; set; } }
	// POS-9d: a settled OFFLINE order replayed at sync (the device's queue entry)
	public class PosSyncLineInput { public int ItemId { get; set; } public decimal Qty { get; set; } = 1; public decimal UnitPrice { get; set; } public decimal DiscountAmount { get; set; } public decimal TaxRate { get; set; } public List<int>? OptionIds { get; set; } }
	public class PosSyncTipInput { public decimal Amount { get; set; } public string? Method { get; set; } }
	public class PosSyncOrderInput
	{
		public string LocalGuid { get; set; } = "";
		public int? TerminalId { get; set; } public int? ShiftId { get; set; } public int? CustomerId { get; set; }
		public string? OrderType { get; set; } public int? TableId { get; set; }
		public string? Method { get; set; } public PosSyncTipInput? Tip { get; set; }
		public string? ReceiptNo { get; set; }
		public decimal GrandTotal { get; set; }
		public List<PosSyncLineInput> Lines { get; set; } = new();
	}

	// RC-3b: a KDS "ticket" = one open order with its SENT lines + prep state (operational, read-only for the kitchen).
	public class KitchenLineDto { public int LineId { get; set; } public string Name { get; set; } = ""; public decimal Qty { get; set; } public string? KdsStatus { get; set; } public string SinceUtcIso { get; set; } = ""; public int? StationId { get; set; } public string StationName { get; set; } = ""; public string? Image { get; set; } public List<string> Modifiers { get; set; } = new(); }
	public class KitchenTicketDto { public int OrderId { get; set; } public string OrderType { get; set; } = ""; public string TableCode { get; set; } = ""; public string OrderKds { get; set; } = "New"; public string SinceUtcIso { get; set; } = ""; public List<KitchenLineDto> Lines { get; set; } = new(); }
	// POS-C1 Delivery DTOs
	public class DeliveryZoneDto { public int Id { get; set; } public string Name { get; set; } = ""; public decimal Fee { get; set; } }
	public class CustomerAddressDto { public int Id { get; set; } public int? ZoneId { get; set; } public string Area { get; set; } = ""; public string Address { get; set; } = ""; public string Phone { get; set; } = ""; public bool IsDefault { get; set; } }
	// POS-B2/A: shared floor board (halls + tables + 3-way derived status). Used by the cashier terminal AND the reservations screen.
	public class FloorTableDto
	{
		public int Id { get; set; } public string Code { get; set; } = ""; public int Seats { get; set; } public string Shape { get; set; } = "";
		public decimal X { get; set; } public decimal Y { get; set; } public decimal W { get; set; } public decimal H { get; set; }
		public string Status { get; set; } = "Available";
		public int? ReservationId { get; set; } public string? ReservedFor { get; set; } public string? ReservedAt { get; set; } public int ReservedParty { get; set; }
		public string? SinceUtc { get; set; } public decimal Amount { get; set; } public int? OrderId { get; set; } public int OrderCount { get; set; } public int Guests { get; set; } public string CustomerName { get; set; } = "";
		public List<object> Items { get; set; } = new();
	}
	public class FloorHallDto { public int Id { get; set; } public string Name { get; set; } = ""; public string? NameEn { get; set; } public List<FloorTableDto> Tables { get; set; } = new(); }
	// POS-C4: a delivery-board row = one open Delivery order + its delivery/kitchen state (operational, read-only).
	public class DeliveryOrderDto { public int OrderId { get; set; } public string CustomerName { get; set; } = ""; public string Area { get; set; } = ""; public string Address { get; set; } = ""; public string Phone { get; set; } = ""; public int? DriverId { get; set; } public string DriverName { get; set; } = ""; public string? DeliveryStatus { get; set; } public string? KitchenStatus { get; set; } public string SinceUtcIso { get; set; } = ""; public int ItemCount { get; set; } public decimal GrandTotal { get; set; } public decimal DeliveryFee { get; set; } }

	public class PosOrderService : IPosOrderService
	{
		private readonly CrossDbContext _db;
		private readonly IReceivableService _receivables;
		private readonly IStockService _stock;             // RC-6c: reverse stock at void
		private readonly IJournalEntryService _journals;   // RC-6c: reverse invoice/receipt JEs
		private readonly IPricingService _pricing;
		private readonly IManufService _manuf;              // BIS-3: WO completion for method 3
		private readonly Microsoft.Extensions.Logging.ILogger<PosOrderService> _logger;   // HM-D5-أ 5ب-3: independent app-log channel for non-blocking anomalies
		private readonly ICurrencyRounding _rounding;
		private readonly ICurrencyService _currency;
		private readonly Microsoft.Extensions.Localization.IStringLocalizer<CrossBuy.SharedResources> L;
		public PosOrderService(CrossDbContext db, IReceivableService receivables, IPricingService pricing, IStockService stock, IJournalEntryService journals, IManufService manuf, Microsoft.Extensions.Logging.ILogger<PosOrderService> logger, ICurrencyRounding rounding, ICurrencyService currency, Microsoft.Extensions.Localization.IStringLocalizer<CrossBuy.SharedResources> localizer)
		{ _db = db; _receivables = receivables; _pricing = pricing; _stock = stock; _journals = journals; _manuf = manuf; _logger = logger; _rounding = rounding; _currency = currency; L = localizer; }

		// HM-D5-أ 5ب-4: TEST-ONLY fault seam (default null ⇒ no-op in production). Set by a dev self-test to force a
		// failure right before the sync-log commit, proving the whole replay rolls back atomically. Never set in prod.
#if DEBUG
		// TEST-ONLY fault injectors — COMPILED OUT of Release ⇒ unreachable in production BY BUILD. Removal after Phase د.
		internal static Func<Task>? _testFaultBeforeSyncLogCommit;
		internal static Func<Task>? _testFaultBeforeConflictSave;   // forces ONLY the post-commit conflict save to fail (proves order still succeeds)
#endif


		// HM-1-أ (ب-1-2): ATOMIC receipt-number allocation. ONE `UPDATE … OUTPUT deleted.NextReceiptNo` takes an
		// exclusive row lock and returns the pre-increment value — no read-then-write race across concurrent lanes.
		// Runs on the ambient transaction. Gaps on rollback are acceptable; duplicates are impossible.
		public async Task<string> AllocateReceiptNoAsync(int terminalId)
		{
			var n = (await _db.Database.SqlQueryRaw<int>(
				"UPDATE dbo.PosTerminals SET NextReceiptNo = NextReceiptNo + 1 OUTPUT deleted.NextReceiptNo AS [Value] WHERE ID = {0}",
				terminalId).ToListAsync()).Single();
			var prefix = await _db.PosTerminals.AsNoTracking().Where(t => t.ID == terminalId).Select(t => t.ReceiptPrefix).FirstAsync();
			return prefix + n.ToString("D6");
		}

		// HM-D5-أ: keep the server counter ahead of an offline-generated receipt number WITHOUT the old all-digits scrape
		// (which read "T1-9E01" as 1901 and burned terminal 1004 to 1902, and would turn ANY normal 6-digit receipt into a
		// 7-digit value because every prefix contains a digit). We strip the terminal's OWN ReceiptPrefix and parse ONLY the
		// numeric suffix; the atomic conditional UPDATE sets NextReceiptNo = max(current, n+1). On a prefix mismatch or an
		// unparseable suffix we DELIBERATELY do NOT advance the counter (never burn it) and DO NOT fail the sync (the sale is
		// already posted) — we return a non-null anomaly message so the caller records a manager-visible PosSyncConflict.
		// Returns (advanced, anomaly) EXPLICITLY — the two states are separate signals, NOT encoded in a message string:
		//   (true , null)    → counter advanced (or already ahead); nothing to record.
		//   (false, null)    → no receipt number to process; nothing to record.
		//   (false, "…")     → prefix mismatch / unparseable suffix: counter deliberately NOT advanced, anomaly to record.
		// The caller MUST NOT treat a non-null anomaly as a sync failure — the sale is already posted; it only records a conflict.
		public async Task<(bool advanced, string? anomaly)> AdvanceCounterPastOfflineReceiptAsync(PosTerminal term, string? receiptNo)
		{
			if (string.IsNullOrWhiteSpace(receiptNo)) return (false, null);
			var rno = receiptNo.Trim();
			var pfx = (term.ReceiptPrefix ?? "").Trim();
			// HM-1 culture rule: parse with NumberStyles.None + InvariantCulture — the suffix is pure ASCII digits, no sign/space/separator.
			if (pfx.Length > 0 && rno.StartsWith(pfx, StringComparison.OrdinalIgnoreCase)
				&& int.TryParse(rno.Substring(pfx.Length), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var n) && n > 0)
			{
				await _db.Database.ExecuteSqlRawAsync("UPDATE dbo.PosTerminals SET NextReceiptNo = CASE WHEN NextReceiptNo <= {1} THEN {1} + 1 ELSE NextReceiptNo END WHERE ID = {0}", term.ID, n);
				return (true, null);
			}
			return (false, $"رقم الإيصال أوفلاين «{rno}» لا يطابق بادئة سلسلة الترمينال «{pfx}» — لم يُقدَّم العدّاد (يلزم مراجعة)");
		}

		// RC-6a: re-stamp the order with the terminal's CURRENTLY-open shift at pay time, so Z-report aggregation
		// (which groups by order.ShiftId) reflects when the cash actually entered the drawer, not when the order opened.
		private async Task StampCurrentShiftAsync(PosOrder o)
		{
			if (o.TerminalId == null) return;
			var sid = await _db.PosShifts.Where(x => x.TerminalId == o.TerminalId && x.Status == "Open").Select(x => (int?)x.ID).FirstOrDefaultAsync();
			if (sid != null) o.ShiftId = sid;
		}

		// RC-4c: load the chosen modifiers for a set of order lines, keyed by line id.
		private async Task<ILookup<int, PosOrderLineModifier>> LoadLineModifiersAsync(List<int> lineIds)
		{
			if (lineIds.Count == 0) return Enumerable.Empty<PosOrderLineModifier>().ToLookup(m => m.OrderLineId);
			var rows = await _db.PosOrderLineModifiers.AsNoTracking().Where(m => lineIds.Contains(m.OrderLineId)).OrderBy(m => m.Sort).ThenBy(m => m.ID).ToListAsync();
			return rows.ToLookup(m => m.OrderLineId);
		}

		// RC-4c: for a parent line invoiced at `qty` units, append each chosen modifier as a 0-PRICE invoice line
		// (ItemId=LinkedItem, Qty=QtyDeducted×qty) → the EXISTING invoice path issues its stock + COGS via StockService
		// (the sole writer), with NO revenue/VAT (those ride the parent line). Proportional-safe: Σ over split allocations
		// of QtyDeducted×qty == QtyDeducted×lineQty exactly, so the linked item is deducted once — no double, no short.
		private static void AppendModifierLines(ILookup<int, PosOrderLineModifier> modsByLine, int lineId, decimal qty, int revenue, int? whId, List<SalesLineInput> invLines)
		{
			foreach (var m in modsByLine[lineId])
			{
				decimal dqty = Math.Round(m.QtyDeducted * qty, 4, MidpointRounding.AwayFromZero);
				if (dqty <= 0) continue;
				invLines.Add(new SalesLineInput { ItemDescription = m.Name, Qty = dqty, UnitPrice = 0, DiscountAmount = 0, TaxRate = 0, RevenueAccountId = revenue, ItemId = m.LinkedItemId, WarehouseId = whId });
			}
		}

		// BIS-2: sourcing map for an order's items in its branch → per-item method + (for RecipeAtSale) the item's recipe.
		public class RecipeComp { public int CompId; public decimal Qty; public decimal Scrap; public string Name = ""; }
		private async Task<(Dictionary<int, string> methodByItem, ILookup<int, RecipeComp> bomByItem)> LoadSourcingAsync(int companyId, int branchId, List<int> itemIds)
		{
			var method = itemIds.Count == 0 ? new Dictionary<int, string>()
				: await _db.BranchItemSourcings.AsNoTracking().Where(s => s.BranchId == branchId && s.IsActive && itemIds.Contains(s.ItemId))
					.ToDictionaryAsync(s => s.ItemId, s => s.Method);
			var recipeItems = method.Where(kv => kv.Value == "RecipeAtSale").Select(kv => kv.Key).ToList();
			ILookup<int, RecipeComp> bom = Enumerable.Empty<RecipeComp>().ToLookup(x => 0);
			if (recipeItems.Count > 0)
			{
				var comps = await (from c in _db.ItemComponents.AsNoTracking()
								   join i in _db.Items.AsNoTracking() on c.ComponentItemId equals i.ID into gi
								   from i in gi.DefaultIfEmpty()
								   where c.CompanyID == companyId && recipeItems.Contains(c.ParentItemId)
								   select new { c.ParentItemId, c.ComponentItemId, c.Quantity, c.ScrapPct, Name = i != null ? i.Name : "" }).ToListAsync();
				bom = comps.ToLookup(c => c.ParentItemId, c => new RecipeComp { CompId = c.ComponentItemId, Qty = c.Quantity, Scrap = c.ScrapPct, Name = c.Name });
			}
			return (method, bom);
		}

		// BIS-2: emit invoice line(s) for one order line, routed by its branch sourcing method:
		//  • RecipeAtSale (method 4): parent = REVENUE ONLY (ItemId=null, no stock) + recipe components as 0-price lines
		//    (ItemId=component, Qty=recipeQty×qty×(1+scrap)) → StockService issues them + COGS (RC-4c pattern). No finished stock.
		//  • all other methods / no row: parent = deduct the finished item ITSELF (ItemId=finished, WarehouseId=wh) — current behaviour.
		// Then the line's chosen modifiers backflush as usual (regardless of method).
		private void AppendSaleLines(Dictionary<int, string> methodByItem, ILookup<int, RecipeComp> bomByItem, ILookup<int, PosOrderLineModifier> modsByLine,
			PosOrderLine l, decimal qty, decimal discount, int revenue, int? whId, List<SalesLineInput> invLines)
		{
			methodByItem.TryGetValue(l.ItemId, out var method);
			if (method == "RecipeAtSale")
			{
				invLines.Add(new SalesLineInput { ItemDescription = l.ItemName, Qty = qty, UnitPrice = l.UnitPrice, DiscountAmount = discount, TaxRate = l.TaxRate, RevenueAccountId = revenue, ItemId = null, WarehouseId = null });   // revenue only
				foreach (var c in bomByItem[l.ItemId])
				{
					decimal dq = Math.Round(c.Qty * (1 + c.Scrap / 100m) * qty, 4, MidpointRounding.AwayFromZero);
					if (dq > 0) invLines.Add(new SalesLineInput { ItemDescription = c.Name, Qty = dq, UnitPrice = 0, DiscountAmount = 0, TaxRate = 0, RevenueAccountId = revenue, ItemId = c.CompId, WarehouseId = whId });
				}
			}
			else
			{
				invLines.Add(new SalesLineInput { ItemDescription = l.ItemName, Qty = qty, UnitPrice = l.UnitPrice, DiscountAmount = discount, TaxRate = l.TaxRate, RevenueAccountId = revenue, ItemId = l.ItemId, WarehouseId = whId, UoMId = l.UoMId });   // HM-2: carry the sold unit to the stock movement
			}
			AppendModifierLines(modsByLine, l.ID, qty, revenue, whId, invLines);
		}

		// RC-5 (tip): post the gratuity as a STANDALONE liability JE — Dr [drawer if Cash else the method's bank/card account]
		// / Cr 210207 tips-payable. NOT revenue, NOT taxed, NO stock, NO AR — the sale settlement is untouched. Sets the order's
		// tip fields (+ TipJournalEntryId for reversal on void). Returns ok=false only if the accounts can't be resolved.
		private async Task<(bool ok, string? error)> PostTipAsync(int companyId, PosOrder o, decimal tipAmount, string? tipMethod, PosTerminal? terminal, int? userId)
		{
			if (tipAmount <= 0) return (true, null);
			var m = string.IsNullOrWhiteSpace(tipMethod) ? "Cash" : tipMethod.Trim();
			int debit;
			if (m == "Cash")
			{
				debit = terminal?.CashAccountId ?? 0;
				if (debit == 0) debit = await _db.BranchPaymentMethods.Where(p => p.BranchId == o.BranchId && p.IsActive && p.PaymentMethod == "Cash" && p.TargetAccountId != null).Select(p => p.TargetAccountId!.Value).FirstOrDefaultAsync();
				if (debit == 0) debit = await CashAccountAsync(companyId);
			}
			else
			{
				debit = await _db.BranchPaymentMethods.Where(p => p.BranchId == o.BranchId && p.IsActive && p.PaymentMethod == m && p.TargetAccountId != null).Select(p => p.TargetAccountId!.Value).FirstOrDefaultAsync();
				if (debit == 0) debit = await _db.Accounts.Where(a => a.CompanyID == companyId && a.Code == "110102").Select(a => a.ID).FirstOrDefaultAsync();
			}
			var tips = await _db.Accounts.Where(a => a.CompanyID == companyId && a.Code == "210207").Select(a => (int?)a.ID).FirstOrDefaultAsync();
			if (debit == 0 || tips == null) return (false, "حساب النقدية/البطاقة أو حساب الإكراميات المستحقة (210207) غير موجود");
			int __dp = await _rounding.DecimalsAsync(companyId, o.CurrencyId, o.BranchId);   // HM-2: tip in the order's document currency
			decimal R(decimal v) => Math.Round(v, __dp, MidpointRounding.AwayFromZero);
			decimal amt = R(tipAmount);
			var (jok, jerr, je) = await _journals.CreateAndPostAsync(new JournalEntryInput
			{
				CompanyID = companyId, EntryDate = DateTime.Today, JournalType = "Auto", SourceType = "PosTip", SourceId = o.ID,
				Description = $"إكرامية — طلب كاشير #{o.ID}",
				Lines = new List<JournalLineInput>
				{
					new JournalLineInput { AccountId = debit, Debit = amt, Credit = 0, Description = "إكرامية مستلمة" },
					new JournalLineInput { AccountId = tips.Value, Debit = 0, Credit = amt, Description = "إكراميات مستحقة للعاملين" },
				},
			}, userId);
			if (!jok) return (false, "تعذّر ترحيل قيد الإكرامية: " + jerr);
			o.TipAmount = amt; o.TipMethod = m; o.TipJournalEntryId = je!.ID;
			return (true, null);
		}

		private async Task<decimal> DefaultVatRateAsync(int companyId) =>
			await _db.TaxCodes.Where(t => t.CompanyID == companyId && t.Kind == "VAT" && t.IsDefault && t.IsActive)
				.Select(t => (decimal?)t.Rate).FirstOrDefaultAsync() ?? 0m;

		// Item.DefaultTaxCodeId → its rate; else company default VAT; else 0. Supports per-item, 0% and exempt.
		// HM-D38: resolution order — item's own tax code → the branch's default tax code → the company default VAT.
		// A branch with no override (DefaultTaxCodeId null, e.g. restaurant) falls straight through to the company default,
		// i.e. the exact pre-D38 behaviour. A Kuwait hyper branch pins VATEX (0%) so its items need no per-item tag.
		private async Task<decimal> ResolveTaxRateAsync(int companyId, Item item, int? branchId)
		{
			if (item.DefaultTaxCodeId != null)
			{
				var r = await _db.TaxCodes.Where(t => t.ID == item.DefaultTaxCodeId && t.CompanyID == companyId)
					.Select(t => (decimal?)t.Rate).FirstOrDefaultAsync();
				if (r != null) return r.Value;
			}
			if (branchId != null)
			{
				var bt = await _db.BranchPosSettings.AsNoTracking().Where(s => s.BranchId == branchId.Value)
					.Select(s => s.DefaultTaxCodeId).FirstOrDefaultAsync();
				if (bt != null)
				{
					var r = await _db.TaxCodes.Where(t => t.ID == bt.Value && t.CompanyID == companyId)
						.Select(t => (decimal?)t.Rate).FirstOrDefaultAsync();
					if (r != null) return r.Value;
				}
			}
			return await DefaultVatRateAsync(companyId);
		}

		private async Task<int> RevenueAccountAsync(int companyId)
		{
			int rev = await _db.Accounts.Where(a => a.CompanyID == companyId && a.Code == "4101").Select(a => a.ID).FirstOrDefaultAsync();
			if (rev == 0) rev = await _db.Accounts.Where(a => a.CompanyID == companyId && a.IsPostable && a.Code.StartsWith("4")).OrderBy(a => a.Code).Select(a => a.ID).FirstOrDefaultAsync();
			return rev;
		}

		private async Task<int> CashAccountAsync(int companyId)
		{
			int cash = await _db.Accounts.Where(a => a.CompanyID == companyId && a.Code == "110101").Select(a => a.ID).FirstOrDefaultAsync();
			if (cash == 0) cash = await _db.Accounts.Where(a => a.CompanyID == companyId && a.IsPostable && a.Code.StartsWith("1101")).OrderBy(a => a.Code).Select(a => a.ID).FirstOrDefaultAsync();
			return cash;
		}

		// A single reusable walk-in customer per company (cash sales). Keyed by a stable NameEn marker.
		private async Task<Models.Context.Accounting.Customer> EnsureWalkInAsync(int companyId)
		{
			var c = await _db.Customers.FirstOrDefaultAsync(x => x.CompanyID == companyId && x.NameEn == "POS Walk-in");
			if (c != null) return c;
			return await _receivables.CreateCustomerAsync(companyId, "عميل نقدي (كاشير)", "POS Walk-in", null, null);
		}

		public async Task<(bool ok, string? error, int orderId)> CreateOrderAsync(int companyId, int branchId, string orderType, int? tableId, int? userId, int? terminalId = null, int? shiftId = null)
		{
			orderType = orderType switch { "Dine-in" => "Dine-in", "Delivery" => "Delivery", _ => "Takeaway" };
			// HM-1/HM-D34: cross-company guard (HARD REJECT). After the HM-D34 relabel every legitimate branch is company 1, so a
			// branch whose company differs from the operating company is a real cross-company leak — reject, never swallow.
			var branchCo = await _db.Branches.Where(b => b.ID == branchId).Select(b => (int?)b.CompanyID).FirstOrDefaultAsync();
			if (branchCo == null) return (false, L["Branch not found."], 0);
			if (branchCo.Value != companyId) return (false, L["This branch belongs to another company — cross-company operations are blocked."], 0);
			var setting = await _db.BranchPosSettings.AsNoTracking().FirstOrDefaultAsync(s => s.BranchId == branchId);
			var brandId = await _db.Branches.Where(b => b.ID == branchId).Select(b => b.BrandId).FirstOrDefaultAsync();   // Brand dimension carried on the order
			var walkIn = await EnsureWalkInAsync(companyId);   // new orders default to the global cash customer
			var o = new PosOrder
			{
				CompanyId = companyId, BranchId = branchId, BrandId = brandId, OrderType = orderType, TableId = tableId,
				TerminalId = terminalId, ShiftId = shiftId, CustomerId = walkIn.ID,
				Status = "Open", CurrencyId = setting?.DefaultCurrencyId, OpenedAt = DateTime.UtcNow, CashierUserId = userId,
			};
			_db.PosOrders.Add(o);
			await _db.SaveChangesAsync();
			return (true, null, o.ID);
		}

		// The active WALK-IN (takeaway) order to resume on load: open, no table, not held.
		// Table orders are recalled from the floor; held orders from the held list.
		public async Task<int?> GetOpenOrderAsync(int companyId, int branchId) =>
			await _db.PosOrders.Where(o => o.CompanyId == companyId && o.BranchId == branchId && o.Status == "Open" && o.TableId == null && !o.IsHeld)
				.OrderByDescending(o => o.ID).Select(o => (int?)o.ID).FirstOrDefaultAsync();

		// Housekeeping on terminal load. NO GL/stock. ONLY walk-in (tableless) orders are touched:
		// non-empty → Held («معلّقة», nothing lost), empty → Void — so the terminal ALWAYS starts empty and
		// nothing "carries over". TABLE (dine-in) orders are LEFT ALONE — an empty table order is a REAL
		// seated party that just hasn't ordered yet («لم يُطلب بعد»), so it must NOT be deleted.
		public async Task ParkOrphanWalkInsAsync(int companyId, int branchId)
		{
			var orphans = await _db.PosOrders
				.Where(o => o.CompanyId == companyId && o.BranchId == branchId && o.Status == "Open" && o.TableId == null && !o.IsHeld)
				.ToListAsync();
			if (orphans.Count == 0) return;
			foreach (var o in orphans)
			{
				bool hasLines = await _db.PosOrderLines.AnyAsync(l => l.OrderId == o.ID);
				if (hasLines) { o.IsHeld = true; o.HeldAt = DateTime.UtcNow; }         // park it → shows in «معلّقة»
				else { o.Status = "Void"; o.ClosedAt = DateTime.UtcNow; }               // empty → discard
			}
			await _db.SaveChangesAsync();
		}

		// Per-table open order (POS-4 dine-in): the MOST RECENT open order on the table (used as default for merge/DnD).
		public async Task<int?> GetOpenOrderByTableAsync(int companyId, int branchId, int tableId) =>
			await _db.PosOrders.Where(o => o.CompanyId == companyId && o.BranchId == branchId && o.Status == "Open" && o.TableId == tableId)
				.OrderByDescending(o => o.ID).Select(o => (int?)o.ID).FirstOrDefaultAsync();

		// ALL open orders on a table — a table can host several parties (new + old) up to its seat count.
		public async Task<List<TableOrderDto>> GetOpenOrdersByTableAsync(int companyId, int branchId, int tableId)
		{
			var orders = await _db.PosOrders.AsNoTracking()
				.Where(o => o.CompanyId == companyId && o.BranchId == branchId && o.Status == "Open" && o.TableId == tableId)
				.OrderBy(o => o.ID).ToListAsync();
			var result = new List<TableOrderDto>();
			foreach (var o in orders)
			{
				var lines = await _db.PosOrderLines.AsNoTracking().Where(l => l.OrderId == o.ID).ToListAsync();
				var cust = o.CustomerId != null ? await _db.Customers.Where(x => x.ID == o.CustomerId).Select(x => x.Name).FirstOrDefaultAsync() ?? "" : "";
				result.Add(new TableOrderDto
				{
					Id = o.ID, CustomerName = cust, GrandTotal = o.GrandTotal, ItemCount = lines.Count, Guests = o.Guests,
					HasUnsent = lines.Any(l => l.SentQty < l.Qty),
					OpenedAtUtc = DateTime.SpecifyKind(o.OpenedAt, DateTimeKind.Utc).ToString("o"),
				});
			}
			return result;
		}

		// Collapse accidental DUPLICATE empty parties on a table (from double-taps) → keep the newest empty, void the rest.
		// 0-item orders carry nothing, so nothing is lost. Parties WITH items are never touched.
		public async Task CollapseEmptyTableDuplicatesAsync(int companyId, int branchId, int tableId)
		{
			var ids = await _db.PosOrders.Where(o => o.CompanyId == companyId && o.BranchId == branchId && o.Status == "Open" && o.TableId == tableId)
				.OrderByDescending(o => o.ID).Select(o => o.ID).ToListAsync();
			bool keptEmpty = false, changed = false;
			foreach (var oid in ids)   // newest first
			{
				if (await _db.PosOrderLines.AnyAsync(l => l.OrderId == oid)) continue;   // has items → real party, keep
				if (!keptEmpty) { keptEmpty = true; continue; }                           // keep the newest empty
				var o = await _db.PosOrders.FirstAsync(x => x.ID == oid);
				o.Status = "Void"; o.ClosedAt = DateTime.UtcNow; changed = true;          // void extra empties
			}
			if (changed) await _db.SaveChangesAsync();
		}

		// Open an ADDITIONAL order on a table (a new party) — allowed while open-order count < seats.
		public async Task<(bool ok, string? error, int orderId)> AddOrderOnTableAsync(int companyId, int branchId, int tableId, int? userId, int? terminalId = null, int? shiftId = null)
		{
			var tbl = await _db.RestaurantTables.AsNoTracking().FirstOrDefaultAsync(t => t.ID == tableId);
			if (tbl == null) return (false, "الطاولة غير موجودة", 0);
			if (tbl.Status == "Closed") return (false, "الطاولة مغلقة", 0);
			// «طلب جديد على نفس الطاولة» is an EXPLICIT action → always seats a DISTINCT party (even if empty),
			// up to the seat count. (Accidental duplicates are prevented at the client by the tap-lock, and because
			// tapping an occupied table recalls instead of creating.)
			int seats = tbl.Seats > 0 ? tbl.Seats : 1;
			// cap by FREE CHAIRS (guests already seated across all parties), NOT by party count — free chairs ⇒ allow another party
			int usedGuests = await _db.PosOrders.Where(o => o.CompanyId == companyId && o.BranchId == branchId && o.Status == "Open" && o.TableId == tableId).SumAsync(o => (int?)o.Guests) ?? 0;
			if (usedGuests >= seats) return (false, $"لا توجد مقاعد فارغة على الطاولة ({seats})", 0);
			return await CreateOrderAsync(companyId, branchId, "Dine-in", tableId, userId, terminalId, shiftId);
		}

		public async Task<(bool ok, string? error)> AddLineAsync(int companyId, int orderId, int itemId, decimal qty, List<int>? optionIds = null, int? uomId = null)
		{
			if (qty <= 0) qty = 1;
			var o = await _db.PosOrders.FirstOrDefaultAsync(x => x.ID == orderId && x.CompanyId == companyId);
			if (o == null) return (false, "الطلب غير موجود");
			if (o.Status != "Open") return (false, "لا يمكن التعديل على طلب غير مفتوح");
			var item = await _db.Items.FirstOrDefaultAsync(i => i.ID == itemId && i.CompanyID == companyId);
			if (item == null) return (false, "الصنف غير موجود");
			// HM-2 UNIT GUARD: the sold unit must be the item's base unit OR have a defined conversion to base on THIS item —
			// else reject (never a silent factor-1). The unit is stored on the line and re-read at pay time (not re-derived from
			// the barcode, which may change/vanish between add and pay).
			if (uomId != null && uomId != item.BaseUoMId)
			{
				bool hasConv = await _db.UoMConversions.AsNoTracking().AnyAsync(cv => cv.ItemId == item.ID && cv.FromUoMId == uomId.Value && cv.ToUoMId == item.BaseUoMId);
				if (!hasConv) return (false, L["This unit has no conversion defined for the item — it cannot be sold."]);
			}
			// HM-2: line amounts round to the order's document currency.
			int __dp = await _rounding.DecimalsAsync(companyId, o.CurrencyId, o.BranchId);
			decimal R(decimal v) => Math.Round(v, __dp, MidpointRounding.AwayFromZero);

			// RC-4: does this item have modifier groups? (active groups linked to the item)
			var groupIds = await (from lnk in _db.ItemModifierGroups.AsNoTracking()
								  join g in _db.ModifierGroups.AsNoTracking() on lnk.GroupId equals g.ID
								  where lnk.ItemId == itemId && g.CompanyID == companyId && g.IsActive
								  orderby lnk.Sort, g.ID
								  select g.ID).ToListAsync();
			var chosen = new List<ModifierOption>();
			if (groupIds.Count > 0)
			{
				var groups = await _db.ModifierGroups.AsNoTracking().Where(g => groupIds.Contains(g.ID)).ToListAsync();
				// resolve the chosen options — must belong to this item's active groups and be active
				var wanted = (optionIds ?? new List<int>()).Distinct().ToList();
				if (wanted.Count > 0)
					chosen = await _db.ModifierOptions.AsNoTracking()
						.Where(op => wanted.Contains(op.ID) && groupIds.Contains(op.GroupId) && op.IsActive).ToListAsync();
				// SERVER-SIDE enforcement of Min/MaxSelect per group (the Modal also enforces, but never trust the client)
				foreach (var g in groups)
				{
					int cnt = chosen.Count(x => x.GroupId == g.ID);
					if (g.Type == "Choice")
					{
						if (cnt != 1) return (false, $"يجب اختيار عنصر واحد من: {g.Name}");   // no "item without a size"
					}
					else // AddOn
					{
						int min = Math.Max(0, g.MinSelect);
						int max = g.MaxSelect <= 0 ? int.MaxValue : g.MaxSelect;   // 0 = unlimited
						if (cnt < min) return (false, $"اختر على الأقل {min} من: {g.Name}");
						if (cnt > max) return (false, $"الحد الأقصى {max} من: {g.Name}");
					}
				}
			}

			// HM-D18: a branch may pin a document-currency price list (BranchPosSetting.DefaultPriceListId). When set, the price
			// MUST come from that list — an item absent from it is REJECTED (never silently converted from the functional SalesPrice
			// nor priced at zero). Branches with no list (e.g. restaurant, DefaultPriceListId=null) behave exactly as before.
			int? branchListId = await _db.BranchPosSettings.AsNoTracking().Where(s => s.BranchId == o.BranchId).Select(s => s.DefaultPriceListId).FirstOrDefaultAsync();
			// HM-5: promotions apply ONLY where the branch's Promotions capability is enabled (management is central,
			// activation is per-branch). Same semantics as IsCapabilityEnabledAsync — the enabled row must exist. A branch
			// with the capability OFF (or unset) prices without promotions; creating promotions from admin is never blocked.
			bool promoOn = await _db.BranchCapabilities.AsNoTracking().AnyAsync(cx => cx.BranchId == o.BranchId && cx.CapabilityKey == "Promotions" && cx.Enabled);
			var price = await _pricing.GetPriceAsync(companyId, itemId, null, null, o.CurrencyId, qty, DateTime.Today, branchListId, uomId, promoOn);   // HM-2: unit; HM-5: promo gate
			if (branchListId != null && price.Source != "list" && price.Source != "costplus")
				return (false, L["This item is not in the branch price list — it cannot be sold until it is priced."]);
			decimal basePrice = price.UnitPrice > 0 ? price.UnitPrice : (item.SalesPrice ?? 0m);
			decimal extras = chosen.Sum(x => x.ExtraPrice);          // per-unit add-on price folded into UnitPrice
			decimal unit = basePrice + extras;                       // ← the "price includes modifiers" fold
			decimal disc = R(basePrice * qty * (price.DiscountPercent) / 100m);   // discount tied to the base item promo
			decimal taxR = await ResolveTaxRateAsync(companyId, item, o.BranchId);
			// HM-5 sell-time safety net: a promotion/discount that drives a PRICED line to zero-or-below is REJECTED with a
			// clear message, never a silent zero/negative line. A genuinely zero-priced item (basePrice==0, e.g. a free
			// modifier component) is unaffected by this guard.
			if (basePrice > 0m && R(unit * qty - disc) <= 0m)
				return (false, L["The discount reduces the price to zero or below — the line was rejected."]);

			// RC-4: an item WITH modifier groups NEVER merges — each add is its own line (different choices = different lines).
			// A plain item (no groups) keeps the merge-into-existing behaviour.
			// HM-2: merge only lines of the SAME item AND SAME unit — a carton line and a piece line are distinct.
			PosOrderLine? existing = groupIds.Count == 0
				? await _db.PosOrderLines.FirstOrDefaultAsync(l => l.OrderId == orderId && l.ItemId == itemId && l.UoMId == uomId)
				: null;
			if (existing != null) { existing.Qty += qty; }
			else
			{
				var sort = (await _db.PosOrderLines.Where(l => l.OrderId == orderId).MaxAsync(l => (int?)l.Sort) ?? 0) + 1;
				var line = new PosOrderLine
				{
					OrderId = orderId, ItemId = itemId, ItemName = item.Name, Qty = qty, UoMId = uomId, UnitPrice = unit,
					DiscountAmount = disc, TaxRate = taxR, LineTotal = R(unit * qty - disc), Sort = sort,
				};
				_db.PosOrderLines.Add(line);
				await _db.SaveChangesAsync();   // need line.ID for the modifier rows
				if (chosen.Count > 0)
				{
					int ms = 0;
					foreach (var op in chosen)
						_db.PosOrderLineModifiers.Add(new PosOrderLineModifier
						{
							OrderLineId = line.ID, GroupId = op.GroupId, OptionId = op.ID,
							Name = string.IsNullOrWhiteSpace(op.Name) ? ("#" + op.LinkedItemId) : op.Name,
							LinkedItemId = op.LinkedItemId, QtyDeducted = op.QtyDeducted, ExtraPrice = op.ExtraPrice, Sort = ms++,
						});
				}
			}
			await _db.SaveChangesAsync();
			await RecomputeAsync(o);
			await _db.SaveChangesAsync();
			return (true, null);
		}

		// RC-4b: groups + options for an item, for the add-time chooser Modal (culture-aware names). Empty ⇒ add instantly.
		public async Task<List<ModChooserGroupDto>> GetItemModifiersAsync(int companyId, int itemId)
		{
			var isAr = System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ar";
			var groups = await (from lnk in _db.ItemModifierGroups.AsNoTracking()
								join g in _db.ModifierGroups.AsNoTracking() on lnk.GroupId equals g.ID
								where lnk.ItemId == itemId && g.CompanyID == companyId && g.IsActive
								orderby lnk.Sort, g.ID
								select g).ToListAsync();
			var result = new List<ModChooserGroupDto>();
			foreach (var g in groups)
			{
				var opts = await _db.ModifierOptions.AsNoTracking()
					.Where(op => op.GroupId == g.ID && op.IsActive).OrderBy(op => op.Sort).ThenBy(op => op.ID).ToListAsync();
				result.Add(new ModChooserGroupDto
				{
					GroupId = g.ID,
					Name = isAr ? g.Name : (!string.IsNullOrWhiteSpace(g.NameEn) ? g.NameEn! : g.Name),
					Type = g.Type, MinSelect = g.MinSelect, MaxSelect = g.MaxSelect,
					Options = opts.Select(op => new ModChooserOptionDto
					{
						OptionId = op.ID,
						Name = isAr ? (string.IsNullOrWhiteSpace(op.Name) ? "" : op.Name) : (!string.IsNullOrWhiteSpace(op.NameEn) ? op.NameEn! : op.Name),
						ExtraPrice = op.ExtraPrice, IsDefault = op.IsDefault,
					}).ToList(),
				});
			}
			return result;
		}

		public async Task<(bool ok, string? error)> SetLineQtyAsync(int companyId, int orderId, int lineId, decimal qty)
		{
			var o = await _db.PosOrders.FirstOrDefaultAsync(x => x.ID == orderId && x.CompanyId == companyId);
			if (o == null) return (false, "الطلب غير موجود");
			if (o.Status != "Open") return (false, "لا يمكن التعديل على طلب غير مفتوح");
			var l = await _db.PosOrderLines.FirstOrDefaultAsync(x => x.ID == lineId && x.OrderId == orderId);
			if (l == null) return (false, "السطر غير موجود");
			int __dp = await _rounding.DecimalsAsync(companyId, o.CurrencyId, o.BranchId);   // HM-2: document currency
			decimal R(decimal v) => Math.Round(v, __dp, MidpointRounding.AwayFromZero);
			// POS-4b: can't reduce below (or delete) what's already gone to the kitchen
			if (l.SentQty > 0 && qty < l.SentQty) return (false, "لا يمكن تقليل كمية صنف مُرسل للمطبخ");
			if (qty <= 0) { _db.PosOrderLines.Remove(l); }
			else { l.Qty = qty; l.DiscountAmount = R(l.DiscountAmount / (l.Qty == 0 ? 1 : l.Qty) * qty); l.LineTotal = R(l.UnitPrice * qty - l.DiscountAmount); }
			await _db.SaveChangesAsync();
			await RecomputeAsync(o);
			await _db.SaveChangesAsync();
			return (true, null);
		}

		public async Task<(bool ok, string? error)> RemoveLineAsync(int companyId, int orderId, int lineId)
		{
			var o = await _db.PosOrders.FirstOrDefaultAsync(x => x.ID == orderId && x.CompanyId == companyId);
			if (o == null) return (false, "الطلب غير موجود");
			if (o.Status != "Open") return (false, "لا يمكن التعديل على طلب غير مفتوح");
			var l = await _db.PosOrderLines.FirstOrDefaultAsync(x => x.ID == lineId && x.OrderId == orderId);
			if (l == null) return (false, "السطر غير موجود");
			if (l.SentQty > 0) return (false, "لا يمكن حذف صنف مُرسل للمطبخ");   // POS-4b
			_db.PosOrderLines.Remove(l);
			await _db.SaveChangesAsync();
			await RecomputeAsync(o);
			await _db.SaveChangesAsync();
			return (true, null);
		}

		// POS-4d-1 Move: move an open order to another table (operational only, NO GL). Destination must be free.
		public async Task<(bool ok, string? error)> MoveOrderToTableAsync(int companyId, int orderId, int toTableId)
		{
			var o = await _db.PosOrders.FirstOrDefaultAsync(x => x.ID == orderId && x.CompanyId == companyId);
			if (o == null) return (false, "الطلب غير موجود");
			if (o.Status != "Open") return (false, "الطلب ليس مفتوحًا");
			if (o.TableId == toTableId) return (false, "نفس الطاولة");
			var areaIds = _db.DiningAreas.Where(a => a.BranchId == o.BranchId).Select(a => a.ID);
			var t = await _db.RestaurantTables.FirstOrDefaultAsync(x => x.ID == toTableId && areaIds.Contains(x.DiningAreaId) && x.IsActive);
			if (t == null) return (false, "الطاولة غير موجودة");
			if (t.Status == "Closed") return (false, "الطاولة مغلقة");
			var occupied = await _db.PosOrders.AnyAsync(x => x.BranchId == o.BranchId && x.Status == "Open" && x.TableId == toTableId && x.ID != orderId);
			if (occupied) return (false, "الطاولة مشغولة — استخدم الدمج");
			o.OrderType = "Dine-in"; o.TableId = toTableId;
			await _db.SaveChangesAsync();   // just re-tags the table; no journal entry, no stock
			return (true, null);
		}

		// SEAT the current in-progress order onto a table AS A PARTY. Unlike Move, this ALLOWS an already-occupied
		// table (the order just becomes an additional party on it), up to the seat count. NO GL/stock.
		public async Task<(bool ok, string? error)> SeatOrderOnTableAsync(int companyId, int orderId, int toTableId)
		{
			var o = await _db.PosOrders.FirstOrDefaultAsync(x => x.ID == orderId && x.CompanyId == companyId);
			if (o == null) return (false, "الطلب غير موجود");
			if (o.Status != "Open") return (false, "الطلب ليس مفتوحًا");
			if (o.TableId == toTableId) return (true, null);   // already seated here
			var areaIds = _db.DiningAreas.Where(a => a.BranchId == o.BranchId).Select(a => a.ID);
			var t = await _db.RestaurantTables.FirstOrDefaultAsync(x => x.ID == toTableId && areaIds.Contains(x.DiningAreaId) && x.IsActive);
			if (t == null) return (false, "الطاولة غير موجودة");
			if (t.Status == "Closed") return (false, "الطاولة مغلقة");
			int seats = t.Seats > 0 ? t.Seats : 1;
			// cap by FREE CHAIRS (guests seated), not party count
			int usedGuests = await _db.PosOrders.Where(x => x.BranchId == o.BranchId && x.Status == "Open" && x.TableId == toTableId && x.ID != orderId).SumAsync(x => (int?)x.Guests) ?? 0;
			if (usedGuests >= seats) return (false, $"لا توجد مقاعد فارغة على الطاولة ({seats})");
			o.OrderType = "Dine-in"; o.TableId = toTableId;
			await _db.SaveChangesAsync();
			return (true, null);
		}

		// Link an open order to a customer (defaults to Walk-in). Invoice at pay goes to this customer. NO GL now.
		public async Task<(bool ok, string? error)> SetOrderCustomerAsync(int companyId, int orderId, int customerId)
		{
			var o = await _db.PosOrders.FirstOrDefaultAsync(x => x.ID == orderId && x.CompanyId == companyId);
			if (o == null) return (false, "الطلب غير موجود");
			if (o.Status != "Open") return (false, "الطلب ليس مفتوحًا");
			var exists = await _db.Customers.AnyAsync(x => x.ID == customerId && x.CompanyID == companyId);
			if (!exists) return (false, "العميل غير موجود");
			o.CustomerId = customerId;
			await _db.SaveChangesAsync();
			return (true, null);
		}

		// Set the guest headcount on a bill (party) — clamped to 1..50. Drives the red-chair count on the floor.
		public async Task<(bool ok, string? error)> SetGuestsAsync(int companyId, int orderId, int guests)
		{
			var o = await _db.PosOrders.FirstOrDefaultAsync(x => x.ID == orderId && x.CompanyId == companyId);
			if (o == null) return (false, "الطلب غير موجود");
			if (o.Status != "Open") return (false, "الطلب ليس مفتوحًا");
			// a dine-in order can't have more guests than the TABLE's seats; takeaway keeps a sane 1..50 cap
			int max = 50;
			if (o.TableId != null) { var seats = await _db.RestaurantTables.Where(t => t.ID == o.TableId).Select(t => t.Seats).FirstOrDefaultAsync(); if (seats > 0) max = seats; }
			o.Guests = guests < 1 ? 1 : (guests > max ? max : guests);   // clamp to the seat limit
			await _db.SaveChangesAsync();
			return (true, null);
		}

		// Discard an open (unpaid) order — e.g. «New» on a walk-in the cashier abandons. Void, NO GL.
		public async Task<(bool ok, string? error)> VoidOrderAsync(int companyId, int orderId)
		{
			var o = await _db.PosOrders.FirstOrDefaultAsync(x => x.ID == orderId && x.CompanyId == companyId);
			if (o == null) return (false, "الطلب غير موجود");
			if (o.Status != "Open") return (false, "الطلب ليس مفتوحًا");
			o.Status = "Void"; o.ClosedAt = DateTime.UtcNow;
			await _db.SaveChangesAsync();
			return (true, null);
		}

		// MERGE TABLES: bring ALL open parties from the source table onto the target table AS SEPARATE bills
		// (just re-tag their TableId — no line combining, no void). Source table frees; target now holds both
		// parties (orderCount reflects the combined parties). NO GL/stock.
		public async Task<(bool ok, string? error)> MergeTablesAsync(int companyId, int branchId, int sourceTableId, int targetTableId)
		{
			if (sourceTableId == targetTableId) return (false, "نفس الطاولة");
			var areaIds = _db.DiningAreas.Where(a => a.BranchId == branchId).Select(a => a.ID);
			var tgt = await _db.RestaurantTables.FirstOrDefaultAsync(x => x.ID == targetTableId && areaIds.Contains(x.DiningAreaId) && x.IsActive);
			if (tgt == null) return (false, "الطاولة الهدف غير موجودة");
			if (tgt.Status == "Closed") return (false, "الطاولة الهدف مغلقة");
			var srcOrders = await _db.PosOrders.Where(o => o.CompanyId == companyId && o.BranchId == branchId && o.Status == "Open" && o.TableId == sourceTableId).ToListAsync();
			if (srcOrders.Count == 0) return (false, "الطاولة المصدر لا تحمل طلبًا مفتوحًا");
			foreach (var o in srcOrders) { o.TableId = targetTableId; o.OrderType = "Dine-in"; }
			await _db.SaveChangesAsync();   // just re-tags the table; parties stay as separate bills; no GL/stock
			return (true, null);
		}

		// POS-4d-2 Merge (bill-combine, kept for reference/other uses): move ALL of source's lines into target
		// (kept as SEPARATE lines, preserving SentQty/SentAt/prices), recompute target, then Void the source. NO GL/stock.
		public async Task<(bool ok, string? error)> MergeOrdersAsync(int companyId, int sourceOrderId, int targetOrderId)
		{
			if (sourceOrderId == targetOrderId) return (false, "لا يمكن دمج الطلب مع نفسه");
			var src = await _db.PosOrders.FirstOrDefaultAsync(x => x.ID == sourceOrderId && x.CompanyId == companyId);
			var tgt = await _db.PosOrders.FirstOrDefaultAsync(x => x.ID == targetOrderId && x.CompanyId == companyId);
			if (src == null || tgt == null) return (false, "الطلب غير موجود");
			if (src.Status != "Open" || tgt.Status != "Open") return (false, "الطلبان يجب أن يكونا مفتوحين");
			if (src.BranchId != tgt.BranchId) return (false, "لا يمكن الدمج بين فرعين");
			var srcLines = await _db.PosOrderLines.Where(l => l.OrderId == sourceOrderId).OrderBy(l => l.Sort).ToListAsync();
			if (srcLines.Count == 0) return (false, "الطلب المصدر فارغ");
			var sort = (await _db.PosOrderLines.Where(l => l.OrderId == targetOrderId).MaxAsync(l => (int?)l.Sort) ?? 0);
			// re-point each source line to the target as-is (kept separate; SentQty/SentAt/prices untouched)
			foreach (var l in srcLines) { l.OrderId = targetOrderId; l.Sort = ++sort; }
			// void the source (archived, no GL)
			src.Status = "Void"; src.MergedIntoOrderId = targetOrderId; src.ClosedAt = DateTime.UtcNow;   // its table frees (occupancy is derived from Open orders)
			await _db.SaveChangesAsync();
			await RecomputeAsync(tgt);
			await _db.SaveChangesAsync();
			return (true, null);
		}

		// POS-4c Hold: park an open walk-in (takeaway) order so a new one can start. Stays Open → NO accounting.
		public async Task<(bool ok, string? error)> HoldOrderAsync(int companyId, int orderId)
		{
			var o = await _db.PosOrders.FirstOrDefaultAsync(x => x.ID == orderId && x.CompanyId == companyId);
			if (o == null) return (false, "الطلب غير موجود");
			if (o.Status != "Open") return (false, "الطلب ليس مفتوحًا");
			if (o.TableId != null) return (false, "طلبات الصالة تُدار من مخطط الطاولات");
			if (!await _db.PosOrderLines.AnyAsync(l => l.OrderId == orderId)) return (false, "لا يمكن تعليق طلب فارغ");
			o.IsHeld = true; o.HeldAt = DateTime.UtcNow;
			await _db.SaveChangesAsync();
			return (true, null);
		}

		// POS-4c: the parked walk-in orders waiting to be recalled (branch-wide).
		public async Task<List<HeldOrderDto>> GetHeldOrdersAsync(int companyId, int branchId)
		{
			var held = await _db.PosOrders.AsNoTracking()
				.Where(o => o.CompanyId == companyId && o.BranchId == branchId && o.Status == "Open" && o.TableId == null && o.IsHeld)
				.OrderByDescending(o => o.HeldAt).ToListAsync();
			var ids = held.Select(o => o.ID).ToList();
			// lines + item thumbnail for every held order in one query (grouped per order)
			var allLines = await (from l in _db.PosOrderLines.AsNoTracking()
								  join it in _db.Items.AsNoTracking() on l.ItemId equals it.ID into gi
								  from it in gi.DefaultIfEmpty()
								  where ids.Contains(l.OrderId)
								  orderby l.Sort
								  select new { l.OrderId, l.ItemName, Img = it != null ? it.ImagePath : null }).ToListAsync();
			var byOrder = allLines.GroupBy(x => x.OrderId).ToDictionary(g => g.Key, g => g.ToList());
			var result = new List<HeldOrderDto>();
			foreach (var o in held)
			{
				byOrder.TryGetValue(o.ID, out var ls); ls ??= new();
				var cust = o.CustomerId != null ? await _db.Customers.Where(x => x.ID == o.CustomerId).Select(x => x.Name).FirstOrDefaultAsync() ?? "" : "";
				var heldAt = o.HeldAt ?? o.OpenedAt;
				result.Add(new HeldOrderDto
				{
					Id = o.ID, HeldAtUtc = heldAt, ItemCount = ls.Count,
					GrandTotal = o.GrandTotal, FirstItem = ls.FirstOrDefault()?.ItemName ?? "",
					CustomerName = cust, HeldAtUtcIso = DateTime.SpecifyKind(heldAt, DateTimeKind.Utc).ToString("o"), IsHeld = true,
					Items = ls.Where(x => !string.IsNullOrEmpty(x.ItemName)).GroupBy(x => x.ItemName).Select(g => g.First())
						.Take(6).Select(x => (object)new { n = x.ItemName, img = x.Img }).ToList()
				});
			}
			return result;
		}

		// ALL open invoices for the branch (held + active takeaway + table orders) so NOTHING can be "lost".
		// Each row says WHERE it is (table code / takeaway / held) so the cashier can always find & recall it.
		public async Task<List<HeldOrderDto>> GetOpenInvoicesAsync(int companyId, int branchId)
		{
			var orders = await _db.PosOrders.AsNoTracking()
				.Where(o => o.CompanyId == companyId && o.BranchId == branchId && o.Status == "Open")
				.OrderByDescending(o => o.ID).ToListAsync();
			var result = new List<HeldOrderDto>();
			foreach (var o in orders)
			{
				var lines = await _db.PosOrderLines.AsNoTracking().Where(l => l.OrderId == o.ID).OrderBy(l => l.Sort).ToListAsync();
				var cust = o.CustomerId != null ? await _db.Customers.Where(x => x.ID == o.CustomerId).Select(x => x.Name).FirstOrDefaultAsync() ?? "" : "";
				var tcode = o.TableId != null ? await _db.RestaurantTables.Where(t => t.ID == o.TableId).Select(t => t.Code).FirstOrDefaultAsync() ?? "" : "";
				var when = o.HeldAt ?? o.OpenedAt;
				result.Add(new HeldOrderDto
				{
					Id = o.ID, HeldAtUtc = when, ItemCount = lines.Count,
					GrandTotal = o.GrandTotal, FirstItem = lines.FirstOrDefault()?.ItemName ?? "",
					CustomerName = cust, HeldAtUtcIso = DateTime.SpecifyKind(when, DateTimeKind.Utc).ToString("o"),
					TableId = o.TableId, TableCode = tcode, IsHeld = o.IsHeld
				});
			}
			return result;
		}

		// POS-4c Recall: bring a held order back as the active one (SentQty on its lines is preserved).
		public async Task<(bool ok, string? error)> RecallHeldAsync(int companyId, int orderId)
		{
			var o = await _db.PosOrders.FirstOrDefaultAsync(x => x.ID == orderId && x.CompanyId == companyId);
			if (o == null) return (false, "الطلب غير موجود");
			if (o.Status != "Open") return (false, "الطلب ليس مفتوحًا");
			o.IsHeld = false; o.HeldAt = null;
			await _db.SaveChangesAsync();
			return (true, null);
		}

		// POS-4b: mark all not-yet-sent quantity of an open order as sent to the kitchen (operational only, NO GL/stock).
		public async Task<(bool ok, string? error, int sentLines)> SendToKitchenAsync(int companyId, int orderId)
		{
			var o = await _db.PosOrders.FirstOrDefaultAsync(x => x.ID == orderId && x.CompanyId == companyId);
			if (o == null) return (false, "الطلب غير موجود", 0);
			if (o.Status != "Open") return (false, "الطلب ليس مفتوحًا", 0);
			var lines = await _db.PosOrderLines.Where(l => l.OrderId == orderId && l.SentQty < l.Qty).ToListAsync();
			if (lines.Count == 0) return (false, "لا توجد أصناف جديدة لإرسالها", 0);
			var now = DateTime.UtcNow;
			// RC-3e: resolve each newly-sent line's kitchen station (its menu tab → station; else default = first active Kitchen station).
			var stations = await _db.KitchenStations.AsNoTracking().Where(s => s.BranchId == o.BranchId && s.IsActive).OrderBy(s => s.ID).ToListAsync();
			int? defStation = (stations.FirstOrDefault(s => s.StationType == "Kitchen") ?? stations.FirstOrDefault())?.ID;
			var itemIds = lines.Select(l => l.ItemId).Distinct().ToList();
			var quicks = await _db.PosQuickItems.AsNoTracking().Where(q => q.BranchId == o.BranchId && itemIds.Contains(q.ItemId)).ToListAsync();
			var grpIds = quicks.Where(q => q.GroupId != null).Select(q => q.GroupId!.Value).Distinct().ToList();
			var grpStation = await _db.PosMenuGroups.AsNoTracking().Where(g => grpIds.Contains(g.ID)).ToDictionaryAsync(g => g.ID, g => g.KitchenStationId);
			foreach (var l in lines)
			{
				l.SentQty = l.Qty; l.SentAt = now; if (string.IsNullOrEmpty(l.KdsStatus)) l.KdsStatus = "New";   // RC-3a: enter the kitchen queue as New
				if (l.StationId == null)   // RC-3e: freeze the station on FIRST send (later config changes never move a sent line)
				{
					int? st = null; var q = quicks.FirstOrDefault(x => x.ItemId == l.ItemId);
					if (q?.GroupId != null && grpStation.TryGetValue(q.GroupId.Value, out var gs)) st = gs;
					l.StationId = st ?? defStation;
				}
			}
			await _db.SaveChangesAsync();   // no journal entry, no stock movement — purely operational
			return (true, null, lines.Count);
		}

		// RC-3a: kitchen advances a SENT line's prep state — New → Preparing → Ready (forward-only). Operational, NO GL/stock.
		private static readonly string[] KdsFlow = { "New", "Preparing", "Ready" };
		public static string? DeriveOrderKds(IEnumerable<(decimal sentQty, string? kds)> lines)
		{
			var sent = lines.Where(x => x.sentQty > 0).ToList();
			if (sent.Count == 0) return null;                                   // nothing sent → no kitchen status
			if (sent.All(x => x.kds == "Ready")) return "Ready";                // everything done
			if (sent.Any(x => x.kds == "Preparing") || (sent.Any(x => x.kds == "Ready") && sent.Any(x => x.kds != "Ready"))) return "Preparing";  // in progress / mixed
			return "New";                                                       // all sent, none started
		}
		public async Task<(bool ok, string? error)> SetLineKdsStatusAsync(int companyId, int orderId, int lineId, string status)
		{
			var o = await _db.PosOrders.FirstOrDefaultAsync(x => x.ID == orderId && x.CompanyId == companyId);
			if (o == null) return (false, "الطلب غير موجود");
			if (o.Status != "Open") return (false, "الطلب ليس مفتوحًا");
			var l = await _db.PosOrderLines.FirstOrDefaultAsync(x => x.ID == lineId && x.OrderId == orderId);
			if (l == null) return (false, "السطر غير موجود");
			if (l.SentQty <= 0) return (false, "الصنف لم يُرسل للمطبخ");
			var ti = Array.IndexOf(KdsFlow, status);
			if (ti < 0) return (false, "حالة غير صحيحة");
			var ci = Array.IndexOf(KdsFlow, string.IsNullOrEmpty(l.KdsStatus) ? "New" : l.KdsStatus); if (ci < 0) ci = 0;
			if (ti < ci) return (false, "لا يمكن إرجاع حالة الصنف");   // forward-only
			l.KdsStatus = status;
			await _db.SaveChangesAsync();   // operational only — no journal entry, no stock movement
			return (true, null);
		}

		// RC-3b: KDS feed — every OPEN order in the branch that has ≥1 sent line, oldest-first (FIFO). Read-only, no GL.
		public async Task<List<KitchenTicketDto>> GetKitchenTicketsAsync(int companyId, int branchId)
		{
			var isAr = System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ar";
			var orders = await _db.PosOrders.AsNoTracking()
				.Where(o => o.CompanyId == companyId && o.BranchId == branchId && o.Status == "Open")
				.ToListAsync();
			var orderIds = orders.Select(o => o.ID).ToList();
			var sent = await (from l in _db.PosOrderLines.AsNoTracking()
							  join i in _db.Items.AsNoTracking() on l.ItemId equals i.ID into gi
							  from i in gi.DefaultIfEmpty()
							  where orderIds.Contains(l.OrderId) && l.SentQty > 0
							  orderby l.Sort
							  select new { l.OrderId, l.ID, l.ItemName, NameEn = i != null ? i.NameEn : null, Img = i != null ? i.ImagePath : null, l.SentQty, l.KdsStatus, l.SentAt, l.StationId }).ToListAsync();
			var byOrder = sent.GroupBy(x => x.OrderId).ToDictionary(g => g.Key, g => g.ToList());
			// RC-4c: chosen modifiers per sent line → shown on the kitchen ticket
			var sentLineIds = sent.Select(x => x.ID).ToList();
			var modsByLine = (await _db.PosOrderLineModifiers.AsNoTracking().Where(m => sentLineIds.Contains(m.OrderLineId)).OrderBy(m => m.Sort).ThenBy(m => m.ID).Select(m => new { m.OrderLineId, m.Name }).ToListAsync()).ToLookup(x => x.OrderLineId, x => x.Name);
			var tblIds = orders.Where(o => o.TableId != null).Select(o => o.TableId!.Value).Distinct().ToList();
			var tcodes = await _db.RestaurantTables.AsNoTracking().Where(t => tblIds.Contains(t.ID)).ToDictionaryAsync(t => t.ID, t => t.Code);
			var stNames = await _db.KitchenStations.AsNoTracking().Where(s => s.BranchId == branchId)
				.ToDictionaryAsync(s => s.ID, s => isAr ? s.Name : (!string.IsNullOrWhiteSpace(s.NameEn) ? s.NameEn! : s.Name));   // RC-3e: culture-aware station name
			var result = new List<KitchenTicketDto>();
			foreach (var o in orders)
			{
				if (!byOrder.TryGetValue(o.ID, out var ls) || ls.Count == 0) continue;
				var lines = ls.Select(x => new KitchenLineDto
				{
					LineId = x.ID,
					Name = isAr ? x.ItemName : (!string.IsNullOrEmpty(x.NameEn) ? x.NameEn! : x.ItemName),
					Qty = x.SentQty, KdsStatus = x.KdsStatus, Image = x.Img,
					SinceUtcIso = x.SentAt.HasValue ? DateTime.SpecifyKind(x.SentAt.Value, DateTimeKind.Utc).ToString("o") : "",
					StationId = x.StationId, StationName = (x.StationId != null && stNames.ContainsKey(x.StationId.Value)) ? stNames[x.StationId.Value] : "",
					Modifiers = modsByLine[x.ID].ToList()
				}).ToList();
				var since = ls.Where(x => x.SentAt.HasValue).Select(x => x.SentAt!.Value).DefaultIfEmpty(o.OpenedAt).Min();
				result.Add(new KitchenTicketDto
				{
					OrderId = o.ID, OrderType = o.OrderType,
					TableCode = (o.TableId != null && tcodes.ContainsKey(o.TableId.Value)) ? tcodes[o.TableId.Value] : "",
					OrderKds = DeriveOrderKds(ls.Select(x => (x.SentQty, x.KdsStatus))) ?? "New",
					SinceUtcIso = DateTime.SpecifyKind(since, DateTimeKind.Utc).ToString("o"),
					Lines = lines
				});
			}
			return result.OrderBy(r => r.SinceUtcIso).ToList();   // FIFO — oldest ticket first
		}

		private async Task RecomputeAsync(PosOrder o)
		{
			// HM-2: round to the ORDER's document currency (o.CurrencyId stamped from branch DefaultCurrencyId; null → functional).
			int __dp = await _rounding.DecimalsAsync(o.CompanyId, o.CurrencyId, o.BranchId);
			decimal R(decimal v) => Math.Round(v, __dp, MidpointRounding.AwayFromZero);
			var lines = await _db.PosOrderLines.Where(l => l.OrderId == o.ID).ToListAsync();
			decimal sub = 0, tax = 0;
			foreach (var l in lines) { l.LineTotal = R(l.Qty * l.UnitPrice - l.DiscountAmount); sub += l.LineTotal; tax += R(l.LineTotal * l.TaxRate / 100m); }
			var setting = await _db.BranchPosSettings.AsNoTracking().FirstOrDefaultAsync(s => s.BranchId == o.BranchId);
			var svcPct = setting?.ServiceChargePct ?? 0m;
			o.SubTotal = R(sub);
			o.ServiceAmount = svcPct > 0 ? R(sub * svcPct / 100m) : 0m;
			if (o.ServiceAmount > 0) { var dv = await DefaultVatRateAsync(o.CompanyId); tax += R(o.ServiceAmount * dv / 100m); }
			// POS-C1: frozen delivery fee is part of the order total (taxed unless the branch marks delivery tax-exempt)
			if (o.DeliveryFee > 0 && setting?.DeliveryTaxExempt != true) { var dv = await DefaultVatRateAsync(o.CompanyId); tax += R(o.DeliveryFee * dv / 100m); }
			o.TaxTotal = R(tax);
			o.GrandTotal = R(o.SubTotal + o.ServiceAmount + o.DeliveryFee + o.TaxTotal);
		}

		public async Task<PosOrderDto?> GetOrderAsync(int companyId, int orderId)
		{
			var o = await _db.PosOrders.AsNoTracking().FirstOrDefaultAsync(x => x.ID == orderId && x.CompanyId == companyId);
			if (o == null) return null;
			// on-screen line name follows the UI language (English uses the item's NameEn, falling back to the stored Arabic snapshot); the snapshot stays the invoice/receipt record
			var isAr = System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ar";
			var lines = await (from l in _db.PosOrderLines.AsNoTracking()
							   join i in _db.Items.AsNoTracking() on l.ItemId equals i.ID into gi
							   from i in gi.DefaultIfEmpty()
							   where l.OrderId == orderId
							   orderby l.Sort
							   select new PosOrderLineDto
							   {
								   Id = l.ID, ItemId = l.ItemId, Name = isAr ? l.ItemName : (i.NameEn != null && i.NameEn != "" ? i.NameEn : l.ItemName), Image = i.ImagePath,
								   UoMId = l.UoMId, UoMName = l.UoMId == null ? null : _db.UnitsOfMeasure.Where(u => u.ID == l.UoMId).Select(u => isAr ? u.Name : u.NameEn).FirstOrDefault(),
								   Qty = l.Qty, UnitPrice = l.UnitPrice, DiscountAmount = l.DiscountAmount, TaxRate = l.TaxRate, LineTotal = l.LineTotal,
								   SentQty = l.SentQty, KdsStatus = l.KdsStatus,
							   }).ToListAsync();
			// RC-4: attach chosen modifiers to their lines (one query for the whole order)
			if (lines.Count > 0)
			{
				var lineIds = lines.Select(x => x.Id).ToList();
				var mods = await _db.PosOrderLineModifiers.AsNoTracking()
					.Where(m => lineIds.Contains(m.OrderLineId))
					.OrderBy(m => m.Sort).ThenBy(m => m.ID)
					.Select(m => new { m.OrderLineId, Dto = new PosOrderLineModifierDto
					{
						Id = m.ID, GroupId = m.GroupId, OptionId = m.OptionId, Name = m.Name,
						LinkedItemId = m.LinkedItemId, QtyDeducted = m.QtyDeducted, ExtraPrice = m.ExtraPrice,
					} }).ToListAsync();
				var byLine = mods.ToLookup(x => x.OrderLineId, x => x.Dto);
				foreach (var ln in lines) ln.Modifiers = byLine[ln.Id].ToList();
			}
			var custName = o.CustomerId != null
				? await _db.Customers.Where(x => x.ID == o.CustomerId).Select(x => x.Name).FirstOrDefaultAsync() ?? ""
				: "";
			var driverName = o.DriverId != null
				? await _db.Drivers.Where(d => d.ID == o.DriverId).Select(d => d.Name).FirstOrDefaultAsync()
				: null;
			return new PosOrderDto
			{
				Id = o.ID, Status = o.Status, OrderType = o.OrderType, TableId = o.TableId,
				SubTotal = o.SubTotal, ServiceAmount = o.ServiceAmount, TaxTotal = o.TaxTotal, GrandTotal = o.GrandTotal, InvoiceId = o.InvoiceId,
				CustomerId = o.CustomerId, CustomerName = custName, Guests = o.Guests,
				KdsStatus = DeriveOrderKds(lines.Select(x => (x.SentQty, x.KdsStatus))),
				DeliveryFee = o.DeliveryFee, DeliveryZoneId = o.DeliveryZoneId, DeliveryArea = o.DeliveryArea, DeliveryAddress = o.DeliveryAddress, DeliveryPhone = o.DeliveryPhone,
				DriverId = o.DriverId, DriverName = driverName, DeliveryStatus = o.DeliveryStatus,
				Lines = lines,
			};
		}

		public async Task<(bool ok, string? error, int? invoiceId)> PayAsync(int companyId, int orderId, string method, int? userId, int splitParts = 1, decimal tipAmount = 0, string? tipMethod = null)
		{
			var o = await _db.PosOrders.FirstOrDefaultAsync(x => x.ID == orderId && x.CompanyId == companyId);
			if (o == null) return (false, "الطلب غير موجود", null);
			if (o.Status != "Open") return (false, "الطلب ليس مفتوحًا", null);
			// RC-2: Cash only, on top of the flexible PosPayment structure. Other methods added later as types.
			if (method != "Cash") return (false, "طريقة الدفع غير مدعومة بعد في هذه المرحلة (النقدي فقط)", null);
			// HM-2: split-receipt portions round to the order's document currency.
			int __dp = await _rounding.DecimalsAsync(companyId, o.CurrencyId, o.BranchId);
			decimal R(decimal v) => Math.Round(v, __dp, MidpointRounding.AwayFromZero);
			var lines = await _db.PosOrderLines.Where(l => l.OrderId == orderId).OrderBy(l => l.Sort).ToListAsync();
			if (lines.Count == 0) return (false, "لا يمكن دفع طلب فارغ", null);

			await RecomputeAsync(o);
			await _db.SaveChangesAsync();

			int revenue = await RevenueAccountAsync(companyId);
			if (revenue == 0) return (false, "لا يوجد حساب إيراد مُعرّف", null);
			// cash target account priority: the TERMINAL's own drawer (POS-2) → branch Cash payment method → default cash box
			var terminal = o.TerminalId != null ? await _db.PosTerminals.FirstOrDefaultAsync(t => t.ID == o.TerminalId) : null;
			int cash = terminal?.CashAccountId ?? 0;
			if (cash == 0) cash = await _db.BranchPaymentMethods.Where(p => p.BranchId == o.BranchId && p.IsActive && p.PaymentMethod == "Cash" && p.TargetAccountId != null)
				.Select(p => p.TargetAccountId!.Value).FirstOrDefaultAsync();
			if (cash == 0) cash = await CashAccountAsync(companyId);
			if (cash == 0) return (false, "لا يوجد حساب نقدية مُعرّف (خزنة الجهاز أو طريقة دفع «نقدي» أو 110101)", null);
			var setting = await _db.BranchPosSettings.AsNoTracking().FirstOrDefaultAsync(s => s.BranchId == o.BranchId);
			int? whId = setting?.DefaultSalesWarehouseId;
			if (whId == null) return (false, "لم يُحدَّد مخزن البيع الافتراضي للفرع (إعدادات نقاط البيع)", null);
			var currencyId = o.CurrencyId ?? setting?.DefaultCurrencyId;

			// invoice on the ORDER's customer (defaults to the global Walk-in); null → Walk-in for legacy rows
			var cust = o.CustomerId != null
				? (await _db.Customers.FirstOrDefaultAsync(x => x.ID == o.CustomerId && x.CompanyID == companyId) ?? await EnsureWalkInAsync(companyId))
				: await EnsureWalkInAsync(companyId);

			var modsByLine = await LoadLineModifiersAsync(lines.Select(l => l.ID).ToList());   // RC-4c
			var (methodByItem, bomByItem) = await LoadSourcingAsync(companyId, o.BranchId, lines.Select(l => l.ItemId).Distinct().ToList());   // BIS-2
			var invLines = new List<SalesLineInput>();
			foreach (var l in lines)
				AppendSaleLines(methodByItem, bomByItem, modsByLine, l, l.Qty, l.DiscountAmount, revenue, whId, invLines);   // BIS-2: routes by sourcing method (+RC-4c modifiers)
			if (o.ServiceAmount > 0)
			{
				var dv = await DefaultVatRateAsync(companyId);
				invLines.Add(new SalesLineInput { ItemDescription = "رسوم خدمة", Qty = 1, UnitPrice = o.ServiceAmount, DiscountAmount = 0, TaxRate = dv, RevenueAccountId = revenue, ItemId = null, WarehouseId = null });
			}
			// POS-C1: delivery fee → its own invoice line, mapped to the delivery-income account (fallback = sales revenue),
			// taxed unless exempt. Flows through the SAME sales-invoice path — NO new GL writer.
			if (o.DeliveryFee > 0)
			{
				int delAcct = setting?.DeliveryRevenueAccountId ?? revenue;
				decimal delTax = (setting?.DeliveryTaxExempt == true) ? 0m : await DefaultVatRateAsync(companyId);
				invLines.Add(new SalesLineInput { ItemDescription = "رسوم توصيل", Qty = 1, UnitPrice = o.DeliveryFee, DiscountAmount = 0, TaxRate = delTax, RevenueAccountId = delAcct, ItemId = null, WarehouseId = null });
			}

			// HM-1-أ ب-3: ONE ambient transaction wraps the WHOLE settlement (invoice + COGS + receipts + tip + receipt-no)
			// so a cashier sale is all-or-nothing. own-or-join: the inner services (CreateSalesInvoiceAsync / CreateReceiptAsync
			// / PostTip → CreateAndPost/PostMovement) JOIN this transaction instead of opening their own.
			await using var tx = await ScopedTx.BeginOrJoinAsync(_db);
			var (iok, ierr, inv) = await _receivables.CreateSalesInvoiceAsync(companyId, cust.ID, DateTime.Today, invLines, $"طلب كاشير #{o.ID}", userId, currencyId);
			if (!iok || inv == null) return (false, ierr ?? "فشل إنشاء الفاتورة", null);

			// settle the full invoice to cash (document currency, so AR nets to zero).
			// POS-4d-3a EQUAL SPLIT: ONE invoice (revenue/stock/tax once) but N cash receipts (total ÷ N),
			// the rounding remainder loaded on the LAST part so the receipts sum to the total EXACTLY.
			int parts = splitParts < 1 ? 1 : (splitParts > 50 ? 50 : splitParts);
			decimal total = inv.GrandTotal;
			decimal each = R(total / parts);
			var portions = new decimal[parts];
			for (int i = 0; i < parts; i++) portions[i] = each;
			// HM-2 (4-أ): the rounding remainder loads on the LARGEST portion (consistent with JES "largest line bears remainder").
			// In an EQUAL split every portion is identical, so the tie-break (lowest index) puts it on the FIRST part — was the last.
			portions[0] = R(portions[0] + (total - each * parts));   // remainder → largest (first on tie); Σ == total
			var note = parts > 1 ? $"تحصيل نقدي (تقسيم {parts}) — طلب كاشير #{o.ID}" : $"تحصيل نقدي — طلب كاشير #{o.ID}";
			foreach (var p in portions)
			{
				var (rok, rerr) = await _receivables.CreateReceiptAsync(companyId, cust.ID, DateTime.Today, p, "Cash", cash, note, userId, inv.CurrencyId);
				if (!rok) return (false, rerr ?? "فشل التحصيل النقدي", null);
				var rid = await _db.Receipts.Where(r => r.CompanyID == companyId && r.CustomerId == cust.ID).OrderByDescending(r => r.ID).Select(r => (int?)r.ID).FirstOrDefaultAsync();
				_db.PosPayments.Add(new PosPayment { OrderId = o.ID, PaymentMethod = "Cash", Amount = p, TargetAccountId = cash, ReceiptId = rid, CreatedAt = DateTime.UtcNow });   // RC-6c link
			}
			var receiptId = await _db.Receipts.Where(r => r.CompanyID == companyId && r.CustomerId == cust.ID).OrderByDescending(r => r.ID).Select(r => (int?)r.ID).FirstOrDefaultAsync();

			var (tipOk, tipErr) = await PostTipAsync(companyId, o, tipAmount, tipMethod, terminal, userId);   // RC-5
			if (!tipOk) return (false, tipErr, null);
			await StampCurrentShiftAsync(o);   // RC-6a
			// HM-1-أ (ب-1-2): allocate the terminal-local receipt number ATOMICALLY, as the LAST step before commit
			// (minimises sequence gaps if an earlier step returned). Distinct from the accounting InvoiceNo.
			if (terminal != null) o.ReceiptNo = await AllocateReceiptNoAsync(terminal.ID);
			o.Status = "Paid"; o.InvoiceId = inv.ID; o.ReceiptId = receiptId; o.ClosedAt = DateTime.UtcNow; o.CashierUserId = userId;
			await _db.SaveChangesAsync();
			await tx.CommitAsync();
			return (true, null, inv.ID);
		}

		// POS-7: MULTI-TENDER settlement — pay ONE order with one OR several payment methods (Cash + Card + Wallet…).
		// Builds a single sales invoice (revenue/stock/tax once) then settles it with one receipt per tender to that
		// method's configured GL account (BranchPaymentMethod.TargetAccountId; Cash → terminal drawer → branch cash → 110101).
		// The tender amounts (the portions ALLOCATED to the invoice) must sum to the grand total.
		public async Task<(bool ok, string? error, int? invoiceId)> PayTendersAsync(int companyId, int orderId, List<PosTenderInput> tenders, int? userId, decimal tipAmount = 0, string? tipMethod = null)
		{
			var o = await _db.PosOrders.FirstOrDefaultAsync(x => x.ID == orderId && x.CompanyId == companyId);
			if (o == null) return (false, "الطلب غير موجود", null);
			if (o.Status != "Open") return (false, "الطلب ليس مفتوحًا", null);
			tenders = (tenders ?? new()).Where(t => t.Amount > 0 && !string.IsNullOrWhiteSpace(t.Method)).ToList();
			if (tenders.Count == 0) return (false, "لا توجد وسيلة دفع", null);
			// HM-2 (4-أ): tender amounts round to the order's DOCUMENT currency (was the static 2dp R → dropped fils on KWD).
			int __dp = await _rounding.DecimalsAsync(companyId, o.CurrencyId, o.BranchId);
			decimal R(decimal v) => Math.Round(v, __dp, MidpointRounding.AwayFromZero);
			var lines = await _db.PosOrderLines.Where(l => l.OrderId == orderId).OrderBy(l => l.Sort).ToListAsync();
			if (lines.Count == 0) return (false, "لا يمكن دفع طلب فارغ", null);

			await RecomputeAsync(o); await _db.SaveChangesAsync();
			int revenue = await RevenueAccountAsync(companyId);
			if (revenue == 0) return (false, "لا يوجد حساب إيراد مُعرّف", null);
			var terminal = o.TerminalId != null ? await _db.PosTerminals.FirstOrDefaultAsync(t => t.ID == o.TerminalId) : null;
			var setting = await _db.BranchPosSettings.AsNoTracking().FirstOrDefaultAsync(s => s.BranchId == o.BranchId);
			int? whId = setting?.DefaultSalesWarehouseId;
			if (whId == null) return (false, "لم يُحدَّد مخزن البيع الافتراضي للفرع (إعدادات نقاط البيع)", null);
			var currencyId = o.CurrencyId ?? setting?.DefaultCurrencyId;

			// resolve the GL account each tender settles to
			async Task<int> CashAcct()
			{
				int cash = terminal?.CashAccountId ?? 0;
				if (cash == 0) cash = await _db.BranchPaymentMethods.Where(p => p.BranchId == o.BranchId && p.IsActive && p.PaymentMethod == "Cash" && p.TargetAccountId != null).Select(p => p.TargetAccountId!.Value).FirstOrDefaultAsync();
				if (cash == 0) cash = await CashAccountAsync(companyId);
				return cash;
			}
			var acctFor = new Dictionary<string, int>();
			foreach (var method in tenders.Select(t => t.Method).Distinct())
			{
				int acct = method == "Cash" ? await CashAcct()
					: await _db.BranchPaymentMethods.Where(p => p.BranchId == o.BranchId && p.IsActive && p.PaymentMethod == method && p.TargetAccountId != null).Select(p => p.TargetAccountId!.Value).FirstOrDefaultAsync();
				if (acct == 0) return (false, $"طريقة الدفع «{method}» غير مُعرّفة أو بلا حساب لهذا الفرع (إعداد طرق الدفع)", null);
				acctFor[method] = acct;
			}

			var cust = o.CustomerId != null
				? (await _db.Customers.FirstOrDefaultAsync(x => x.ID == o.CustomerId && x.CompanyID == companyId) ?? await EnsureWalkInAsync(companyId))
				: await EnsureWalkInAsync(companyId);
			var modsByLine = await LoadLineModifiersAsync(lines.Select(l => l.ID).ToList());   // RC-4c
			var (methodByItem, bomByItem) = await LoadSourcingAsync(companyId, o.BranchId, lines.Select(l => l.ItemId).Distinct().ToList());   // BIS-2
			var invLines = new List<SalesLineInput>();
			foreach (var l in lines)
				AppendSaleLines(methodByItem, bomByItem, modsByLine, l, l.Qty, l.DiscountAmount, revenue, whId, invLines);   // BIS-2
			if (o.ServiceAmount > 0) { var dv = await DefaultVatRateAsync(companyId); invLines.Add(new SalesLineInput { ItemDescription = "رسوم خدمة", Qty = 1, UnitPrice = o.ServiceAmount, DiscountAmount = 0, TaxRate = dv, RevenueAccountId = revenue }); }
			if (o.DeliveryFee > 0) { int delAcct = setting?.DeliveryRevenueAccountId ?? revenue; decimal delTax = (setting?.DeliveryTaxExempt == true) ? 0m : await DefaultVatRateAsync(companyId); invLines.Add(new SalesLineInput { ItemDescription = "رسوم توصيل", Qty = 1, UnitPrice = o.DeliveryFee, DiscountAmount = 0, TaxRate = delTax, RevenueAccountId = delAcct }); }

			// HM-1-أ ب-3: ONE ambient transaction wraps the whole multi-tender settlement (invoice + COGS + receipts + tip).
			await using var tx = await ScopedTx.BeginOrJoinAsync(_db);
			var (iok, ierr, inv) = await _receivables.CreateSalesInvoiceAsync(companyId, cust.ID, DateTime.Today, invLines, $"طلب كاشير #{o.ID}", userId, currencyId);
			if (!iok || inv == null) return (false, ierr ?? "فشل إنشاء الفاتورة", null);

			// tenders must cover the grand total; the LARGEST tender absorbs the rounding/change so Σ receipts == grand EXACTLY.
			// HM-2 (4-أ): remainder → LARGEST tender (was the LAST), consistent with JES "largest line bears remainder". The sum is
			// validated at the DOCUMENT-currency unit so a legitimate 3dp KWD tender sum is neither rejected nor truncated.
			decimal grand = inv.GrandTotal, sum = R(tenders.Sum(t => t.Amount));
			decimal unit = 1m; for (int u = 0; u < __dp; u++) unit /= 10m;
			if (sum < grand - unit) return (false, $"المدفوع {sum} أقل من الإجمالي {grand}", null);
			var parts = tenders.Select(t => new { t.Method, Amount = R(t.Amount) }).ToList();
			int big = 0; for (int i = 1; i < parts.Count; i++) if (parts[i].Amount > parts[big].Amount) big = i;   // lowest-index tie-break
			decimal others = R(parts.Where((p, i) => i != big).Sum(p => p.Amount));
			var recv = new List<(string method, decimal amt)>();
			for (int i = 0; i < parts.Count; i++)
			{
				decimal amt = i == big ? R(grand - others) : parts[i].Amount;   // largest = grand − Σ(others) → Σ == grand
				if (amt <= 0) continue;
				recv.Add((parts[i].Method, amt));
			}
			foreach (var (method, amt) in recv)
			{
				var (rok, rerr) = await _receivables.CreateReceiptAsync(companyId, cust.ID, DateTime.Today, amt, method, acctFor[method], $"تحصيل {method} — طلب كاشير #{o.ID}", userId, inv.CurrencyId);
				if (!rok) return (false, rerr ?? "فشل التحصيل", null);
				var rid = await _db.Receipts.Where(r => r.CompanyID == companyId && r.CustomerId == cust.ID).OrderByDescending(r => r.ID).Select(r => (int?)r.ID).FirstOrDefaultAsync();
				_db.PosPayments.Add(new PosPayment { OrderId = o.ID, PaymentMethod = method, Amount = amt, TargetAccountId = acctFor[method], ReceiptId = rid, CreatedAt = DateTime.UtcNow });   // RC-6c link
			}
			var receiptId = await _db.Receipts.Where(r => r.CompanyID == companyId && r.CustomerId == cust.ID).OrderByDescending(r => r.ID).Select(r => (int?)r.ID).FirstOrDefaultAsync();
			var (tipOk, tipErr) = await PostTipAsync(companyId, o, tipAmount, tipMethod, terminal, userId);   // RC-5
			if (!tipOk) return (false, tipErr, null);
			await StampCurrentShiftAsync(o);   // RC-6a
			// HM-1-أ (ب-1-2): atomic receipt-number allocation as the last step before commit.
			if (terminal != null) o.ReceiptNo = await AllocateReceiptNoAsync(terminal.ID);
			o.Status = "Paid"; o.InvoiceId = inv.ID; o.ReceiptId = receiptId; o.ClosedAt = DateTime.UtcNow; o.CashierUserId = userId;
			await _db.SaveChangesAsync();
			await tx.CommitAsync();
			return (true, null, inv.ID);
		}

		// POS-4d-3b: split BY ITEM → N invoices (each = its allocated lines' revenue/stock/tax) + N cash receipts,
		// all on the ORDER's customer. Whole-unit allocation. GUARD: every unit of every line covered exactly once.
		// Rounding residual (order total − Σ bill totals) lands on the LAST bill so Σ invoices == order total EXACTLY.
		public async Task<(bool ok, string? error, List<int> invoiceIds)> PaySplitByItemAsync(int companyId, int orderId, List<List<SplitAllocation>> bills, string method, int? userId, decimal tipAmount = 0, string? tipMethod = null)
		{
			var o = await _db.PosOrders.FirstOrDefaultAsync(x => x.ID == orderId && x.CompanyId == companyId);
			if (o == null) return (false, "الطلب غير موجود", new());
			if (o.Status != "Open") return (false, "الطلب ليس مفتوحًا", new());
			if (method != "Cash") return (false, "طريقة الدفع غير مدعومة بعد (النقدي فقط)", new());
			// HM-2 (4-أ): per-bill amounts round to the order's DOCUMENT currency (was the static 2dp R → dropped fils on KWD).
			int __dp = await _rounding.DecimalsAsync(companyId, o.CurrencyId, o.BranchId);
			decimal R(decimal v) => Math.Round(v, __dp, MidpointRounding.AwayFromZero);
			var lines = await _db.PosOrderLines.Where(l => l.OrderId == orderId).OrderBy(l => l.Sort).ToListAsync();
			if (lines.Count == 0) return (false, "لا يمكن دفع طلب فارغ", new());
			if (bills == null || bills.Count == 0) return (false, "لا توجد حسابات للتقسيم", new());

			// ---- STRICT GUARD: every unit covered exactly once (no unit double-billed or missed) ----
			var allocByLine = new Dictionary<int, decimal>();
			foreach (var bill in bills)
			{
				if (bill == null || bill.Count == 0) return (false, "يوجد حساب فارغ في التقسيم", new());
				foreach (var a in bill)
				{
					if (a.Qty <= 0) return (false, "كمية تخصيص غير صحيحة", new());
					var ln = lines.FirstOrDefault(l => l.ID == a.LineId);
					if (ln == null) return (false, "سطر لا يخص هذا الطلب", new());
					allocByLine[a.LineId] = (allocByLine.TryGetValue(a.LineId, out var v) ? v : 0) + a.Qty;
				}
			}
			foreach (var l in lines)
			{
				var alloc = allocByLine.TryGetValue(l.ID, out var v) ? v : 0m;
				if (Math.Abs(alloc - l.Qty) > 0.0001m)
					return (false, $"توزيع «{l.ItemName}» غير مطابق (المخصَّص {alloc} ≠ الكمية {l.Qty})", new());
			}

			await RecomputeAsync(o); await _db.SaveChangesAsync();

			int revenue = await RevenueAccountAsync(companyId);
			if (revenue == 0) return (false, "لا يوجد حساب إيراد مُعرّف", new());
			var terminal = o.TerminalId != null ? await _db.PosTerminals.FirstOrDefaultAsync(t => t.ID == o.TerminalId) : null;
			int cash = terminal?.CashAccountId ?? 0;
			if (cash == 0) cash = await _db.BranchPaymentMethods.Where(p => p.BranchId == o.BranchId && p.IsActive && p.PaymentMethod == "Cash" && p.TargetAccountId != null).Select(p => p.TargetAccountId!.Value).FirstOrDefaultAsync();
			if (cash == 0) cash = await CashAccountAsync(companyId);
			if (cash == 0) return (false, "لا يوجد حساب نقدية مُعرّف", new());
			var setting = await _db.BranchPosSettings.AsNoTracking().FirstOrDefaultAsync(s => s.BranchId == o.BranchId);
			int? whId = setting?.DefaultSalesWarehouseId;
			if (whId == null) return (false, "لم يُحدَّد مخزن البيع الافتراضي للفرع", new());
			var currencyId = o.CurrencyId ?? setting?.DefaultCurrencyId;
			decimal svcPct = setting?.ServiceChargePct ?? 0m;
			decimal vat = await DefaultVatRateAsync(companyId);
			var cust = o.CustomerId != null
				? (await _db.Customers.FirstOrDefaultAsync(x => x.ID == o.CustomerId && x.CompanyID == companyId) ?? await EnsureWalkInAsync(companyId))
				: await EnsureWalkInAsync(companyId);

			// mirror RecomputeAsync per bill to find the rounding residual → last bill
			decimal BillGrand(List<SplitAllocation> bill)
			{
				decimal sub = 0, tax = 0;
				foreach (var a in bill) { var l = lines.First(x => x.ID == a.LineId); var disc = R(l.DiscountAmount * a.Qty / l.Qty); var lt = R(a.Qty * l.UnitPrice - disc); sub += lt; tax += R(lt * l.TaxRate / 100m); }
				var svc = svcPct > 0 ? R(sub * svcPct / 100m) : 0m; if (svc > 0) tax += R(svc * vat / 100m);
				return R(sub + svc + tax);
			}
			// HM-2 (4-أ): the rounding residual loads on the LARGEST bill (was the last), consistent with JES "largest bears remainder".
			var billGrands = bills.Select(b => BillGrand(b)).ToList();
			int bigBill = 0; for (int i = 1; i < billGrands.Count; i++) if (billGrands[i] > billGrands[bigBill]) bigBill = i;   // lowest-index tie-break
			decimal sumBills = billGrands.Sum();
			// POS-C1: order-level delivery fee is billed ONCE (on the first bill); include it so the residual is pure rounding
			int delAcct = setting?.DeliveryRevenueAccountId ?? revenue;
			decimal delTax = (setting?.DeliveryTaxExempt == true) ? 0m : vat;
			decimal deliveryGrand = o.DeliveryFee > 0 ? R(o.DeliveryFee + R(o.DeliveryFee * delTax / 100m)) : 0m;
			decimal residual = R(o.GrandTotal - sumBills - deliveryGrand);   // piasters from per-bill rounding → last bill

			var modsByLine = await LoadLineModifiersAsync(lines.Select(l => l.ID).ToList());   // RC-4c
			var (methodByItem, bomByItem) = await LoadSourcingAsync(companyId, o.BranchId, lines.Select(l => l.ItemId).Distinct().ToList());   // BIS-2
			// HM-1-أ ب-3: ONE ambient transaction wraps ALL split bills (each invoice + COGS + receipt) so a split payment is all-or-nothing.
			await using var tx = await ScopedTx.BeginOrJoinAsync(_db);
			var invoiceIds = new List<int>();
			for (int bi = 0; bi < bills.Count; bi++)
			{
				var bill = bills[bi]; decimal billSub = 0; var invLines = new List<SalesLineInput>();
				foreach (var a in bill)
				{
					var l = lines.First(x => x.ID == a.LineId); var disc = R(l.DiscountAmount * a.Qty / l.Qty);
					AppendSaleLines(methodByItem, bomByItem, modsByLine, l, a.Qty, disc, revenue, whId, invLines);   // BIS-2: recipe components split PROPORTIONALLY (+RC-4c modifiers)
					billSub += R(a.Qty * l.UnitPrice - disc);
				}
				if (svcPct > 0) { var svc = R(billSub * svcPct / 100m); if (svc > 0) invLines.Add(new SalesLineInput { ItemDescription = "رسوم خدمة", Qty = 1, UnitPrice = svc, DiscountAmount = 0, TaxRate = vat, RevenueAccountId = revenue, ItemId = null, WarehouseId = null }); }
				if (bi == 0 && o.DeliveryFee > 0) invLines.Add(new SalesLineInput { ItemDescription = "رسوم توصيل", Qty = 1, UnitPrice = o.DeliveryFee, DiscountAmount = 0, TaxRate = delTax, RevenueAccountId = delAcct, ItemId = null, WarehouseId = null });
					if (bi == bigBill && residual != 0m) invLines.Add(new SalesLineInput { ItemDescription = "تسوية تقريب", Qty = 1, UnitPrice = residual, DiscountAmount = 0, TaxRate = 0, RevenueAccountId = revenue, ItemId = null, WarehouseId = null });

				var (iok, ierr, inv) = await _receivables.CreateSalesInvoiceAsync(companyId, cust.ID, DateTime.Today, invLines, $"طلب كاشير #{o.ID} — تقسيم {bi + 1}/{bills.Count}", userId, currencyId);
				if (!iok || inv == null) return (false, ierr ?? "فشل إنشاء فاتورة التقسيم", new());
				var (rok, rerr) = await _receivables.CreateReceiptAsync(companyId, cust.ID, DateTime.Today, inv.GrandTotal, "Cash", cash, $"تحصيل تقسيم {bi + 1}/{bills.Count} — طلب #{o.ID}", userId, inv.CurrencyId);
				if (!rok) return (false, rerr ?? "فشل تحصيل التقسيم", new());
				var rid = await _db.Receipts.Where(r => r.CompanyID == companyId && r.CustomerId == cust.ID).OrderByDescending(r => r.ID).Select(r => (int?)r.ID).FirstOrDefaultAsync();
				_db.PosPayments.Add(new PosPayment { OrderId = o.ID, PaymentMethod = "Cash", Amount = inv.GrandTotal, TargetAccountId = cash, ReceiptId = rid, CreatedAt = DateTime.UtcNow });   // RC-6c link
				invoiceIds.Add(inv.ID);
			}

			var (tipOk, tipErr) = await PostTipAsync(companyId, o, tipAmount, tipMethod, terminal, userId);   // RC-5
			if (!tipOk) return (false, tipErr, new());
			await StampCurrentShiftAsync(o);   // RC-6a
			// HM-1-أ (ب-1-2): atomic receipt-number allocation as the last step before commit.
			if (terminal != null) o.ReceiptNo = await AllocateReceiptNoAsync(terminal.ID);
			o.Status = "Paid"; o.InvoiceId = invoiceIds.First(); o.ClosedAt = DateTime.UtcNow; o.CashierUserId = userId;
			await _db.SaveChangesAsync();
			await tx.CommitAsync();
			return (true, null, invoiceIds);
		}

		// RC-6c-1: FULL VOID of a paid order — clean reversal, NO delete, via StockService + JournalEntryService only.
		// (1) For EVERY invoice of the order: return its stock movements (+1 at the EXACT original UnitCost, PostToGl=true →
		//     Dr Inventory / Cr COGS — returns finished item + RC-4c modifier linked items exactly, NO BOM re-explode) then
		//     ReverseAsync the invoice JE (Cr AR / Dr Revenue / Dr VAT); mark invoice Reversed.
		// (2) For EVERY receipt of the order (via PosPayment.ReceiptId): ReverseAsync its JE (Cr Cash-drawer / Dr AR → cash OUT);
		//     mark receipt Reversed. → AR nets to 0, cash leaves the drawer, stock back, revenue/VAT/COGS reversed. Order → Voided.
		public async Task<(bool ok, string? error)> VoidPaidOrderAsync(int companyId, int orderId, int? userId)
		{
			var o = await _db.PosOrders.FirstOrDefaultAsync(x => x.ID == orderId && x.CompanyId == companyId);
			if (o == null) return (false, "الطلب غير موجود");
			if (o.Status == "Voided") return (false, "الفاتورة ملغاة بالفعل");
			if (o.Status != "Paid") return (false, "لا يمكن إلغاء إلا فاتورة مدفوعة");

			// all invoices of this order (single for PayAsync/PayTenders; N for split-by-item) — matched by the cashier note
			string note = $"طلب كاشير #{o.ID}";
			var invoices = await _db.SalesInvoices.Where(i => i.CompanyID == companyId && i.Notes != null && (i.Notes == note || i.Notes.StartsWith(note + " —"))).ToListAsync();
			if (invoices.Count == 0 && o.InvoiceId != null) { var iv = await _db.SalesInvoices.FirstOrDefaultAsync(i => i.ID == o.InvoiceId); if (iv != null) invoices.Add(iv); }
			if (invoices.Count == 0) return (false, "لم يُعثر على فاتورة الطلب");
			var date = DateTime.Today;

			// HM-1-أ ب-3: ONE ambient transaction wraps the whole void (stock return + all JE reversals) so it is all-or-nothing.
			await using var tx = await ScopedTx.BeginOrJoinAsync(_db);
			foreach (var inv in invoices)
			{
				if (inv.Status == "Reversed") continue;
				// return this invoice's stock exactly as it left (finished + modifier linked items), at original cost
				var moves = await _db.StockMovements.AsNoTracking().Where(m => m.CompanyID == companyId && m.SourceType == "SalesInvoice" && m.SourceId == inv.ID && m.Direction == -1).ToListAsync();
				foreach (var m in moves)
				{
					var (sok, serr, _) = await _stock.PostMovementAsync(companyId, new MovementRequest
					{ Date = date, ItemId = m.ItemId, WarehouseId = m.WarehouseId, Direction = 1, Qty = m.QtyBase, UnitCostInBase = m.UnitCost, SourceType = "SalesInvoiceReversal", SourceId = inv.ID, PostToGl = true, Notes = $"إلغاء فاتورة {inv.InvoiceNo}" }, userId?.ToString());
					if (!sok) return (false, "تعذّر إرجاع المخزون: " + serr);
				}
				// reverse the invoice JE (AR/revenue/VAT)
				if (inv.JournalEntryId != null)
				{
					var (jok, jerr, _) = await _journals.ReverseAsync(inv.JournalEntryId.Value, userId, $"إلغاء فاتورة كاشير {inv.InvoiceNo}");
					if (!jok) return (false, "تعذّر عكس قيد الفاتورة: " + jerr);
				}
				inv.Status = "Reversed";
			}

			// reverse every receipt (cash out of the drawer) — via the PosPayment→ReceiptId links
			var receiptIds = await _db.PosPayments.Where(p => p.OrderId == o.ID && p.ReceiptId != null).Select(p => p.ReceiptId!.Value).Distinct().ToListAsync();
			foreach (var rid in receiptIds)
			{
				var rc = await _db.Receipts.FirstOrDefaultAsync(r => r.ID == rid && r.CompanyID == companyId);
				if (rc == null || rc.Status == "Reversed") continue;
				if (rc.JournalEntryId != null)
				{
					var (jok, jerr, _) = await _journals.ReverseAsync(rc.JournalEntryId.Value, userId, $"إلغاء سند قبض {rc.ReceiptNo}");
					if (!jok) return (false, "تعذّر عكس قيد السند: " + jerr);
				}
				rc.Status = "Reversed";
			}

			// RC-5: reverse the tip JE too (tip is part of the operation being voided → cash out / liability cleared)
			if (o.TipJournalEntryId != null)
			{
				var (jok, jerr, _) = await _journals.ReverseAsync(o.TipJournalEntryId.Value, userId, $"إلغاء إكرامية — طلب #{o.ID}");
				if (!jok) return (false, "تعذّر عكس قيد الإكرامية: " + jerr);
				o.TipJournalEntryId = null; o.TipAmount = 0m;
			}

			o.Status = "Voided"; o.ClosedAt = DateTime.UtcNow;
			await _db.SaveChangesAsync();
			await tx.CommitAsync();
			return (true, null);
		}

		// RC-6c-2: PARTIAL return of selected lines/qty from a paid order → CreateSalesReturnAsync (returns their stock +1,
		// reverses their revenue/VAT, opens an AR credit) + a cash-refund JE (Dr AR-control / Cr drawer → closes AR + cash out).
		// Returns each line's RC-4c modifiers proportionally (0-price lines) so the linked-item stock comes back too. No new writer.
		public async Task<(bool ok, string? error, int? returnId)> ReturnOrderLinesAsync(int companyId, int orderId, List<SplitAllocation> allocations, int? userId)
		{
			var o = await _db.PosOrders.FirstOrDefaultAsync(x => x.ID == orderId && x.CompanyId == companyId);
			if (o == null) return (false, "الطلب غير موجود", null);
			if (o.Status != "Paid") return (false, "المرتجع الجزئي متاح للفواتير المدفوعة فقط", null);
			int __ddp = await _rounding.DecimalsAsync(companyId, o.CurrencyId, o.BranchId);   // HM-2 Batch 5: order document dp (no static R)
			decimal R(decimal v) => Math.Round(v, __ddp, MidpointRounding.AwayFromZero);
			allocations = (allocations ?? new()).Where(a => a.Qty > 0).ToList();
			if (allocations.Count == 0) return (false, "اختر صنفًا وكمية للإرجاع", null);

			var lines = await _db.PosOrderLines.Where(l => l.OrderId == orderId).ToListAsync();
			var setting = await _db.BranchPosSettings.AsNoTracking().FirstOrDefaultAsync(s => s.BranchId == o.BranchId);
			int? whId = setting?.DefaultSalesWarehouseId;
			if (whId == null) return (false, "لم يُحدَّد مخزن البيع الافتراضي للفرع", null);
			int revenue = await RevenueAccountAsync(companyId);
			if (revenue == 0) return (false, "لا يوجد حساب إيراد مُعرّف", null);
			var cust = o.CustomerId != null ? await _db.Customers.FirstOrDefaultAsync(x => x.ID == o.CustomerId && x.CompanyID == companyId) : await EnsureWalkInAsync(companyId);
			if (cust == null) return (false, "العميل غير موجود", null);
			// drawer to refund from: terminal drawer → branch Cash method → 110101
			var terminal = o.TerminalId != null ? await _db.PosTerminals.AsNoTracking().FirstOrDefaultAsync(t => t.ID == o.TerminalId) : null;
			int drawer = terminal?.CashAccountId ?? 0;
			if (drawer == 0) drawer = await _db.BranchPaymentMethods.Where(p => p.BranchId == o.BranchId && p.IsActive && p.PaymentMethod == "Cash" && p.TargetAccountId != null).Select(p => p.TargetAccountId!.Value).FirstOrDefaultAsync();
			if (drawer == 0) drawer = await CashAccountAsync(companyId);
			if (drawer == 0) return (false, "لا يوجد حساب نقدية مُعرّف", null);

			var modsByLine = await LoadLineModifiersAsync(lines.Select(l => l.ID).ToList());
			// BIS-4: build the return lines by MIRRORING the sale (AppendSaleLines) — so a RecipeAtSale (method 4) item returns
			// its recipe COMPONENTS (never the never-stocked finished), and deduct-itself items return the finished. Same routing,
			// so the return exactly reverses whatever the sale issued (finished/components + RC-4c modifiers) at their cost.
			var (methodByItem, bomByItem) = await LoadSourcingAsync(companyId, o.BranchId, lines.Select(l => l.ItemId).Distinct().ToList());
			var retLines = new List<SalesLineInput>();
			foreach (var a in allocations)
			{
				var l = lines.FirstOrDefault(x => x.ID == a.LineId);
				if (l == null) return (false, "سطر لا يخص هذا الطلب", null);
				if (a.Qty > l.Qty + 0.0001m) return (false, $"كمية الإرجاع أكبر من المُباع لـ«{l.ItemName}»", null);
				var disc = R(l.DiscountAmount * a.Qty / (l.Qty == 0 ? 1 : l.Qty));
				AppendSaleLines(methodByItem, bomByItem, modsByLine, l, a.Qty, disc, revenue, whId, retLines);
			}

			var (rok, rerr, ret) = await _receivables.CreateSalesReturnAsync(companyId, cust.ID, o.InvoiceId, DateTime.Today, retLines, $"مرتجع جزئي — طلب كاشير #{o.ID}", userId, o.CurrencyId);
			if (!rok || ret == null) return (false, rerr ?? "فشل إنشاء المرتجع", null);

			// cash refund: close the AR credit the return opened + take the cash out of the drawer.
			// Modeled as a NEGATIVE receipt: the JE is Dr AR-control / Cr drawer (via JournalEntryService), and a matching
			// Receipt row (Amount = −grand) is written so the AR subledger check (Σinv − Σreceipts − Σreturns) stays balanced
			// — otherwise GL AR (0 after refund) would diverge from the subledger (which still carries the credit note).
			if (ret.GrandTotal > 0)
			{
				// HM-2 (3-ج-0): refund JE lines are FUNCTIONAL (base). AR closes at the INVOICE rate (= the credit note's GrandTotalBase);
				// the drawer pays document currency valued at the RETURN-DAY rate; the difference is realized FX (4902/5902). No new account.
				int __fdp = await _rounding.DecimalsAsync(companyId, null);
				decimal Rf(decimal v) => Math.Round(v, __fdp, MidpointRounding.AwayFromZero);
				var functional = await _currency.GetFunctionalCurrencyIdAsync(companyId, null);
				var ordCur = o.CurrencyId ?? functional;
				decimal todayRate = ordCur == functional ? 1m : (await _currency.ToBaseAsync(1m, ordCur, functional, DateTime.Today, "Sell")).effectiveRate;
				decimal arBase = ret.GrandTotalBase ?? ret.GrandTotal;      // AR at the invoice rate (single source = credit-note column)
				decimal cashOutBase = Rf(ret.GrandTotal * todayRate);       // cash paid, valued at the return-day rate
				decimal fxNet = cashOutBase - arBase;
				var refund = new List<JournalLineInput>
				{
					new JournalLineInput { AccountId = cust.ControlAccountId, Debit = arBase, Credit = 0, Description = $"رد نقدي مرتجع {ret.ReturnNo}" },
					new JournalLineInput { AccountId = drawer, Debit = 0, Credit = cashOutBase, Description = $"رد نقدي من الدرج — مرتجع {ret.ReturnNo}" },
				};
				if (fxNet != 0)
				{
					var fxAcc = await _db.Accounts.Where(a => a.CompanyID == companyId && a.Code == (fxNet > 0 ? "5902" : "4902")).Select(a => (int?)a.ID).FirstOrDefaultAsync();
					if (fxAcc == null) return (false, "حساب فروق العملة المحققة (4902/5902) غير مُهيّأ", null);
					if (fxNet > 0) refund.Add(new JournalLineInput { AccountId = fxAcc.Value, Debit = fxNet, Credit = 0, Description = "خسارة فرق عملة محققة — مرتجع" });
					else refund.Add(new JournalLineInput { AccountId = fxAcc.Value, Debit = 0, Credit = -fxNet, Description = "ربح فرق عملة محقق — مرتجع" });
				}
				var (jok, jerr, jentry) = await _journals.CreateAndPostAsync(new JournalEntryInput
				{ CompanyID = companyId, EntryDate = DateTime.Today, JournalType = "Auto", SourceType = "PosRefund", SourceId = ret.ID, CurrencyId = ordCur, Description = $"رد نقدي مرتجع {ret.ReturnNo} — طلب #{o.ID}", Lines = refund }, userId);
				if (!jok) return (false, "تعذّر ترحيل قيد الرد النقدي: " + jerr, null);
				var refundReceipt = new CrossBuy.Models.Context.Accounting.Receipt
				{
					CompanyID = companyId, CustomerId = cust.ID, ReceiptDate = DateTime.Today, Amount = -ret.GrandTotal, AmountBase = -arBase,
					Method = "Cash", CashAccountId = drawer, Status = "Posted", JournalEntryId = jentry!.ID, CurrencyId = ordCur, ExchangeRate = todayRate,
					Notes = $"رد نقدي مرتجع {ret.ReturnNo} — طلب #{o.ID}", CreatedAt = DateTime.UtcNow,
				};
				_db.Receipts.Add(refundReceipt);
				await _db.SaveChangesAsync();
				refundReceipt.ReceiptNo = $"RF-{DateTime.Today:yyyy}-{refundReceipt.ID:D5}";
				await _db.SaveChangesAsync();
			}
			return (true, null, ret.ID);
		}

		// POS-9d: replay a SETTLED offline order → rebuild it on the server + pay via the EXISTING services (invoice + stock +
		// GL + tip). Idempotent via PosSyncLog(localGuid) — a re-sent order is skipped (never posted twice). No new writer;
		// invariants sourced from the server. Honors the OFFLINE line prices the customer paid; COGS uses server cost at post.
		public async Task<(bool ok, string? error, int? invoiceId, bool alreadySynced)> SyncPaidOrderAsync(int companyId, PosSyncOrderInput p, int? userId)
		{
			if (p == null || string.IsNullOrWhiteSpace(p.LocalGuid)) return (false, "localGuid مطلوب", null, false);
			if (p.Lines == null || p.Lines.Count == 0) return (false, "الطلب فارغ", null, false);

			var term = p.TerminalId != null ? await _db.PosTerminals.FirstOrDefaultAsync(t => t.ID == p.TerminalId) : null;
			int branchId = term?.BranchId ?? 0;
			if (branchId == 0) return (false, "الترمينال غير معروف", null, false);

			// HM-D5-أ 5ب-1: the WHOLE replay — rebuild + pay (invoice/COGS/receipt/tip) + the PosSyncLog idempotency KEY —
			// is ONE ambient own-or-join transaction. Nothing is durable until the sync-log commits WITH the post, so a failure
			// anywhere rolls the whole sale back (never a "posted-without-key" order ⇒ the device's retry cannot double-post).
			int orderId = 0; int? invId = null; int syncLogId = 0; string? receiptAnomaly = null;
			await using (var tx = await ScopedTx.BeginOrJoinAsync(_db))
			{
				// AUTHORITATIVE idempotency check INSIDE the transaction (the unique index on LocalGuid is the race backstop).
				var dup = await _db.PosSyncLogs.FirstOrDefaultAsync(x => x.CompanyId == companyId && x.LocalGuid == p.LocalGuid);
				if (dup != null) { await tx.CommitAsync(); return (true, null, dup.InvoiceId, true); }   // already synced — nothing written

				// 1) rebuild the OPEN order (re-resolves modifiers + BIS sourcing server-side)
				var (cok, cerr, oid) = await CreateOrderAsync(companyId, branchId, p.OrderType ?? "Takeaway", p.TableId, userId, p.TerminalId, p.ShiftId);
				if (!cok) { await tx.RollbackAsync(); return (false, cerr, null, false); }
				orderId = oid;
				// HM-2 Batch 5: offline-replay line totals + price-conflict checks round to the order's document dp (no static R). RecomputeAsync re-does LineTotal authoritatively.
				int __ddp = await _rounding.DecimalsAsync(companyId, await _db.PosOrders.AsNoTracking().Where(x => x.ID == orderId).Select(x => x.CurrencyId).FirstAsync(), branchId);
				decimal R(decimal v) => Math.Round(v, __ddp, MidpointRounding.AwayFromZero);
				if (p.CustomerId != null) await SetOrderCustomerAsync(companyId, orderId, p.CustomerId.Value);
				foreach (var l in p.Lines)
				{
					var (aok, aerr) = await AddLineAsync(companyId, orderId, l.ItemId, l.Qty, l.OptionIds);
					if (!aok) { await tx.RollbackAsync(); return (false, "تعذّرت إعادة بناء السطر: " + aerr, null, false); }
				}
				// 2) honor the OFFLINE prices the customer actually paid (server re-pricing may differ if prices changed mid-outage)
				var lines = await _db.PosOrderLines.Where(x => x.OrderId == orderId).OrderBy(x => x.Sort).ThenBy(x => x.ID).ToListAsync();
				if (lines.Count == p.Lines.Count)
				{
					for (int i = 0; i < lines.Count; i++) { lines[i].UnitPrice = p.Lines[i].UnitPrice; lines[i].DiscountAmount = p.Lines[i].DiscountAmount; lines[i].TaxRate = p.Lines[i].TaxRate; lines[i].LineTotal = R(lines[i].Qty * lines[i].UnitPrice - lines[i].DiscountAmount); }
					await _db.SaveChangesAsync();
					var oRec = await _db.PosOrders.FirstAsync(x => x.ID == orderId); await RecomputeAsync(oRec); await _db.SaveChangesAsync();
				}
				decimal grand = await _db.PosOrders.AsNoTracking().Where(x => x.ID == orderId).Select(x => x.GrandTotal).FirstAsync();

				// 3) pay via the EXISTING service — PayAsync / PayTendersAsync JOIN this ambient transaction (own-or-join)
				decimal tipA = p.Tip?.Amount ?? 0m; var tipM = p.Tip?.Method;
				bool payOk; string? payErr;
				if ((p.Method ?? "Cash") == "Cash") { var r = await PayAsync(companyId, orderId, "Cash", userId, 1, tipA, tipM); payOk = r.ok; payErr = r.error; invId = r.invoiceId; }
				else { var r = await PayTendersAsync(companyId, orderId, new List<PosTenderInput> { new PosTenderInput { Method = p.Method!, Amount = grand } }, userId, tipA, tipM); payOk = r.ok; payErr = r.error; invId = r.invoiceId; }
				if (!payOk) { await tx.RollbackAsync(); return (false, "تعذّر الترحيل: " + payErr, null, false); }

				// 4) keep the offline receipt no + advance the terminal counter (suffix-only; anomaly recorded AFTER commit)
				var ord = await _db.PosOrders.FirstAsync(x => x.ID == orderId);
				if (!string.IsNullOrWhiteSpace(p.ReceiptNo)) ord.ReceiptNo = p.ReceiptNo;
				if (term != null) { var (_, anom) = await AdvanceCounterPastOfflineReceiptAsync(term, p.ReceiptNo); receiptAnomaly = anom; }

				// 5) the idempotency KEY — committed ATOMICALLY with the post above
				var syncLog = new PosSyncLog { CompanyId = companyId, LocalGuid = p.LocalGuid, OrderId = orderId, InvoiceId = invId, ReceiptNo = p.ReceiptNo, SyncedAt = DateTime.UtcNow };
				_db.PosSyncLogs.Add(syncLog);
#if DEBUG
				if (_testFaultBeforeSyncLogCommit != null) await _testFaultBeforeSyncLogCommit();   // TEST-ONLY fault injection (Debug builds only)
#endif
				await _db.SaveChangesAsync();
				await tx.CommitAsync();
				syncLogId = syncLog.ID;
			}

			// HM-D5-أ 5ب-3: everything below is DIAGNOSTIC and runs AFTER the money commit — it must NEVER fail the order.
			// The ReceiptNoMismatch anomaly is derived from the order's OWN data (independently surfaced by the
			// receiptno_out_of_series integrity check), so if this one best-effort save fails we log it to the app-log —
			// no silent swallow, no exception to the device. Return value stays success unless the POST itself failed above.
			if (receiptAnomaly != null)
			{
				var conflict = new PosSyncConflict { CompanyId = companyId, SyncLogId = syncLogId, OrderId = orderId, ConflictType = "ReceiptNoMismatch", Detail = receiptAnomaly, Status = "Open", CreatedAt = DateTime.UtcNow };
				try {
#if DEBUG
					if (_testFaultBeforeConflictSave != null) await _testFaultBeforeConflictSave();   // TEST-ONLY (Debug builds only)
#endif
					_db.PosSyncConflicts.Add(conflict); await _db.SaveChangesAsync(); }
				catch (Exception ex)
				{
					_db.Entry(conflict).State = Microsoft.EntityFrameworkCore.EntityState.Detached;   // don't let a later SaveChanges retry it
					_logger.LogError(ex, "HM-D5-أ ReceiptNoMismatch conflict NOT persisted for order {OrderId} terminal {TerminalId} receipt {ReceiptNo}: {Detail}", orderId, term?.ID, p.ReceiptNo, receiptAnomaly);
				}
			}
			// POS-9e non-blocking conflict detection (best-effort internally; also after commit)
			await DetectSyncConflictsAsync(companyId, syncLogId, orderId, invId, p);
			return (true, null, invId, false);
		}

		// POS-9e: after a successful replay, note anomalies WITHOUT gating the sale — an item that went inactive during the
		// outage, a price the customer paid that no longer matches the catalog, or any deducted item that ended negative.
		private async Task DetectSyncConflictsAsync(int companyId, int syncLogId, int orderId, int? invoiceId, PosSyncOrderInput p)
		{
			try
			{
				var conflicts = new List<PosSyncConflict>();
				DateTime now = DateTime.UtcNow;
				var itemIds = p.Lines.Select(l => l.ItemId).Distinct().ToList();
				var items = await _db.Items.AsNoTracking().Where(i => itemIds.Contains(i.ID)).ToDictionaryAsync(i => i.ID);
				// option → extra price (to reconstruct the expected catalog unit price for a modified line)
				var allOpt = p.Lines.Where(l => l.OptionIds != null).SelectMany(l => l.OptionIds!).Distinct().ToList();
				var optExtra = allOpt.Count == 0 ? new Dictionary<int, decimal>() : await _db.ModifierOptions.AsNoTracking().Where(o => allOpt.Contains(o.ID)).ToDictionaryAsync(o => o.ID, o => o.ExtraPrice);
				// HM-2 Batch 5: expected catalog price compared in the order's document dp (no static R). Post-tx scope → own shadow.
				int __cdp = await _rounding.DecimalsAsync(companyId, await _db.PosOrders.AsNoTracking().Where(x => x.ID == orderId).Select(x => x.CurrencyId).FirstAsync());
				decimal R(decimal v) => Math.Round(v, __cdp, MidpointRounding.AwayFromZero);
				foreach (var l in p.Lines)
				{
					if (!items.TryGetValue(l.ItemId, out var it)) continue;
					if (!it.IsActive)
						conflicts.Add(new PosSyncConflict { CompanyId = companyId, SyncLogId = syncLogId, OrderId = orderId, ConflictType = "InactiveItem", ItemId = l.ItemId, Detail = $"«{it.Name}» أصبح غير نشط بعد البيع أوفلاين", Status = "Open", CreatedAt = now });
					decimal extras = (l.OptionIds ?? new List<int>()).Sum(oid => optExtra.TryGetValue(oid, out var e) ? e : 0m);
					decimal expected = R((it.SalesPrice ?? 0m) + extras);
					if (Math.Abs(expected - l.UnitPrice) > 0.01m)
						conflicts.Add(new PosSyncConflict { CompanyId = companyId, SyncLogId = syncLogId, OrderId = orderId, ConflictType = "PriceDiff", ItemId = l.ItemId, Detail = $"سعر «{it.Name}» أوفلاين {l.UnitPrice:0.##} ≠ الكتالوج {expected:0.##}", OfflineValue = l.UnitPrice, ServerValue = expected, Status = "Open", CreatedAt = now });
				}
				// negative stock: check every item the invoice actually deducted (exact deduction set incl. recipe/modifier lines)
				if (invoiceId != null)
				{
					var deducted = await _db.SalesInvoiceLines.AsNoTracking().Where(sl => sl.SalesInvoiceId == invoiceId && sl.ItemId != null && sl.WarehouseId != null).Select(sl => new { sl.ItemId, sl.WarehouseId }).Distinct().ToListAsync();
					var nameById = await _db.Items.AsNoTracking().Where(i => deducted.Select(d => d.ItemId).Contains(i.ID)).ToDictionaryAsync(i => i.ID, i => i.Name);
					foreach (var d in deducted)
					{
						var (bal, _, _) = await _stock.GetBalanceAsync(companyId, d.ItemId!.Value, d.WarehouseId!.Value);
						if (bal < 0)
							conflicts.Add(new PosSyncConflict { CompanyId = companyId, SyncLogId = syncLogId, OrderId = orderId, ConflictType = "NegativeStock", ItemId = d.ItemId, Detail = $"رصيد «{(nameById.TryGetValue(d.ItemId!.Value, out var nm) ? nm : d.ItemId)}» أصبح سالبًا ({bal:0.##}) بعد الترحيل", ServerValue = bal, Status = "Open", CreatedAt = now });
					}
				}
				if (conflicts.Count > 0) { _db.PosSyncConflicts.AddRange(conflicts); await _db.SaveChangesAsync(); }
			}
			catch { /* detection is best-effort; it must never fail the sale */ }
		}

		// BIS-3: a branch's sales warehouse (branch↔warehouse mapping)
		private async Task<int> BranchWarehouseAsync(int branchId) =>
			await _db.BranchPosSettings.AsNoTracking().Where(s => s.BranchId == branchId).Select(s => s.DefaultSalesWarehouseId ?? 0).FirstOrDefaultAsync();

		// BIS-3 (method 2): Prepaid replenishment — transfer FINISHED stock from the source branch to this branch via the
		// EXISTING TransferAsync (goods-in-transit 110302 nets to 0). The sale then deducts the finished (BIS-2). No new writer.
		public async Task<(bool ok, string? error, int? transferId)> ReplenishFinishedFromBranchAsync(int companyId, int branchId, int itemId, decimal qty, DateTime date, string? userId)
		{
			if (qty <= 0) return (false, "الكمية يجب أن تكون أكبر من صفر", null);
			var s = await _db.BranchItemSourcings.AsNoTracking().FirstOrDefaultAsync(x => x.BranchId == branchId && x.ItemId == itemId && x.IsActive);
			if (s == null || s.Method != "FinishedFromBranch") return (false, "الصنف ليس «جاهز من فرع آخر» في هذا الفرع", null);
			if (s.SourceBranchId == null) return (false, "لا يوجد فرع مصدر", null);
			int toWh = await BranchWarehouseAsync(branchId), fromWh = await BranchWarehouseAsync(s.SourceBranchId.Value);
			if (toWh == 0 || fromWh == 0) return (false, "مخزن البيع غير محدَّد لأحد الفرعين", null);
			var (ok, err, tr) = await _stock.TransferAsync(companyId, fromWh, toWh, date, $"تجهيز صنف جاهز — فرع {branchId} من فرع {s.SourceBranchId}",
				new List<TransferLineInput> { new() { ItemId = itemId, Qty = qty } }, userId);
			return (ok, err, tr?.ID);
		}

		// BIS-3 (method 3): transfer the SEMI-finished from the source branch (exact amount the WO will consume), then complete
		// a work order (BOM-includes-the-semi) at this branch via the EXISTING ManufService → cost rolls up (semi transferred
		// cost + local components + labor/overhead). The sale then deducts the finished (BIS-2). No new writer.
		public async Task<(bool ok, string? error, int? workOrderId)> PrepareSemiFinishedAsync(int companyId, int branchId, int itemId, decimal qty, decimal labor, decimal overhead, DateTime date, string? userId)
		{
			if (qty <= 0) return (false, "الكمية يجب أن تكون أكبر من صفر", null);
			var s = await _db.BranchItemSourcings.AsNoTracking().FirstOrDefaultAsync(x => x.BranchId == branchId && x.ItemId == itemId && x.IsActive);
			if (s == null || s.Method != "SemiFromBranchComplete") return (false, "الصنف ليس «نصف-مصنّع + إكمال» في هذا الفرع", null);
			if (s.SourceBranchId == null || s.SemiFinishedItemId == null) return (false, "الفرع المصدر أو نصف-المصنّع غير محدَّد", null);
			int toWh = await BranchWarehouseAsync(branchId), fromWh = await BranchWarehouseAsync(s.SourceBranchId.Value);
			if (toWh == 0 || fromWh == 0) return (false, "مخزن البيع غير محدَّد لأحد الفرعين", null);
			// how much semi the WO will consume = its BOM qty × qty × (1+scrap)
			var semiComp = await _db.ItemComponents.AsNoTracking().FirstOrDefaultAsync(c => c.CompanyID == companyId && c.ParentItemId == itemId && c.ComponentItemId == s.SemiFinishedItemId);
			if (semiComp == null) return (false, "نصف-المصنّع ليس ضمن قائمة مواد الصنف التام", null);
			decimal semiNeed = Math.Round(semiComp.Quantity * qty * (1 + semiComp.ScrapPct / 100m), 4, MidpointRounding.AwayFromZero);
			var (tok, terr, _) = await _stock.TransferAsync(companyId, fromWh, toWh, date, $"تحويل نصف-مصنّع للإكمال — فرع {branchId}",
				new List<TransferLineInput> { new() { ItemId = s.SemiFinishedItemId.Value, Qty = semiNeed } }, userId);
			if (!tok) return (false, "تعذّر تحويل نصف-المصنّع: " + terr, null);
			// complete a work order at this branch (BOM incl. the semi) — cost rolls up
			var (cok, cerr, woId) = await _manuf.CreateAsync(companyId, itemId, qty, toWh, date, date, labor, overhead, $"إكمال من نصف-مصنّع — فرع {branchId}", userId);
			if (!cok) return (false, "تعذّر إنشاء أمر التشغيل: " + cerr, null);
			var (dok, derr, _) = await _manuf.CompleteAsync(companyId, woId, date, userId);
			if (!dok) return (false, "تعذّر إكمال أمر التشغيل: " + derr, null);
			return (true, null, woId);
		}

		// POS-C1: set + FREEZE delivery info on an open Delivery order. Operational, no GL (the fee enters the invoice at pay).
		public async Task<(bool ok, string? error)> SetOrderDeliveryAsync(int companyId, int orderId, int? customerId, int? zoneId, string? address, string? area, string? phone)
		{
			var o = await _db.PosOrders.FirstOrDefaultAsync(x => x.ID == orderId && x.CompanyId == companyId);
			if (o == null) return (false, "الطلب غير موجود");
			if (o.Status != "Open") return (false, "الطلب ليس مفتوحًا");
			if (o.OrderType != "Delivery") return (false, "هذا الطلب ليس توصيلًا");
			int __ddp = await _rounding.DecimalsAsync(companyId, o.CurrencyId, o.BranchId);   // HM-2 Batch 5: order document dp (no static R)
			decimal R(decimal v) => Math.Round(v, __ddp, MidpointRounding.AwayFromZero);
			if (customerId != null && await _db.Customers.AnyAsync(c => c.ID == customerId && c.CompanyID == companyId)) o.CustomerId = customerId;
			DeliveryZone? zone = null;
			if (zoneId != null) { zone = await _db.DeliveryZones.FirstOrDefaultAsync(z => z.ID == zoneId && z.BranchId == o.BranchId && z.IsActive); if (zone == null) return (false, "منطقة التوصيل غير صحيحة"); }
			var setting = await _db.BranchPosSettings.AsNoTracking().FirstOrDefaultAsync(s => s.BranchId == o.BranchId);
			// fee = the zone's fee; else the branch default; else 0. FROZEN on the order.
			o.DeliveryZoneId = zone?.ID;
			o.DeliveryFee = R(zone?.Fee ?? setting?.DefaultDeliveryFee ?? 0m);
			o.DeliveryArea = !string.IsNullOrWhiteSpace(area) ? area!.Trim() : (zone?.Name ?? "");
			o.DeliveryAddress = address?.Trim() ?? o.DeliveryAddress;
			o.DeliveryPhone = phone?.Trim() ?? o.DeliveryPhone;
			await RecomputeAsync(o);
			await _db.SaveChangesAsync();
			return (true, null);
		}

		public async Task<List<DeliveryZoneDto>> GetDeliveryZonesAsync(int branchId) =>
			await _db.DeliveryZones.AsNoTracking().Where(z => z.BranchId == branchId && z.IsActive).OrderBy(z => z.Name)
				.Select(z => new DeliveryZoneDto { Id = z.ID, Name = z.Name, Fee = z.Fee }).ToListAsync();

		public async Task<List<CustomerAddressDto>> GetCustomerAddressesAsync(int companyId, int customerId) =>
			await _db.CustomerAddresses.AsNoTracking().Where(a => a.CompanyId == companyId && a.CustomerId == customerId && a.IsActive)
				.OrderByDescending(a => a.IsDefault).ThenByDescending(a => a.ID)
				.Select(a => new CustomerAddressDto { Id = a.ID, ZoneId = a.DeliveryZoneId, Area = a.Area, Address = a.Address, Phone = a.Phone, IsDefault = a.IsDefault }).ToListAsync();

		public async Task<(bool ok, string? error, int? id)> AddCustomerAddressAsync(int companyId, int customerId, int? zoneId, string area, string address, string phone, bool isDefault)
		{
			if (!await _db.Customers.AnyAsync(c => c.ID == customerId && c.CompanyID == companyId)) return (false, "العميل غير موجود", null);
			if (string.IsNullOrWhiteSpace(address)) return (false, "العنوان مطلوب", null);
			if (isDefault)
			{
				var others = await _db.CustomerAddresses.Where(a => a.CompanyId == companyId && a.CustomerId == customerId && a.IsDefault).ToListAsync();
				foreach (var x in others) x.IsDefault = false;
			}
			var na = new CustomerAddress { CompanyId = companyId, CustomerId = customerId, DeliveryZoneId = zoneId, Area = area?.Trim() ?? "", Address = address.Trim(), Phone = phone?.Trim() ?? "", IsDefault = isDefault, IsActive = true };
			_db.CustomerAddresses.Add(na);
			await _db.SaveChangesAsync();
			return (true, null, na.ID);
		}

		// Shared floor board (halls + tables + 3-way DERIVED status: Occupied > Reserved > Available). Reused by
		// the cashier terminal (BuildHallsAsync) AND the reservations screen — one source, no divergence.
		public async Task<List<FloorHallDto>> GetFloorAsync(int companyId, int branchId)
		{
			var areas = await _db.DiningAreas.AsNoTracking().Where(a => a.BranchId == branchId && a.IsActive)
				.OrderBy(a => a.Sort).ThenBy(a => a.ID).ToListAsync();
			var areaIds = areas.Select(a => a.ID).ToList();
			var tables = await _db.RestaurantTables.AsNoTracking()
				.Where(t => areaIds.Contains(t.DiningAreaId) && t.IsActive).OrderBy(t => t.Code).ToListAsync();
			var open = await (from o in _db.PosOrders
							  where o.BranchId == branchId && o.Status == "Open" && o.TableId != null
							  join cu in _db.Customers on o.CustomerId equals cu.ID into gc
							  from cu in gc.DefaultIfEmpty()
							  select new { OrderId = o.ID, TableId = o.TableId!.Value, o.OpenedAt, o.GrandTotal, o.Guests, CustomerName = cu != null ? cu.Name : "" }).ToListAsync();
			var occ = open.GroupBy(o => o.TableId).ToDictionary(g => g.Key, g => g.ToList());
			// today's active Booked reservations (window+30m grace not elapsed) → "Reserved" (below Occupied in priority)
			var today = DateTime.Now.Date; var nowLocal = DateTime.Now; const int graceMin = 30;
			var resvAll = await _db.Reservations.AsNoTracking()
				.Where(r => r.BranchId == branchId && r.Status == "Booked" && r.ReservedAtUtc >= today && r.ReservedAtUtc < today.AddDays(1)).ToListAsync();
			var resByTable = resvAll.Where(r => nowLocal <= r.ReservedAtUtc.AddMinutes(r.DurationMinutes + graceMin))
				.GroupBy(r => r.TableId).ToDictionary(g => g.Key, g => g.OrderBy(x => x.ReservedAtUtc).First());
			var openIds = open.Select(o => o.OrderId).ToList();
			var lineItems = await (from l in _db.PosOrderLines.AsNoTracking()
								   join it in _db.Items.AsNoTracking() on l.ItemId equals it.ID into gi
								   from it in gi.DefaultIfEmpty()
								   where openIds.Contains(l.OrderId)
								   orderby l.Sort
								   select new { l.OrderId, l.ItemName, Img = it != null ? it.ImagePath : null }).ToListAsync();
			var orderToTable = open.ToDictionary(o => o.OrderId, o => o.TableId);
			var itemsByTable = lineItems
				.Where(l => orderToTable.ContainsKey(l.OrderId) && !string.IsNullOrEmpty(l.ItemName))
				.GroupBy(l => orderToTable[l.OrderId])
				.ToDictionary(g => g.Key, g => g.GroupBy(x => x.ItemName).Select(gg => gg.First())
					.Take(6).Select(x => (object)new { n = x.ItemName, img = x.Img }).ToList());
			return areas.Select(a => new FloorHallDto
			{
				Id = a.ID,
				Name = a.Name,
				NameEn = a.NameEn,
				Tables = tables.Where(t => t.DiningAreaId == a.ID).Select(t =>
				{
					var isOcc = occ.TryGetValue(t.ID, out var os);
					var rep = isOcc ? os!.OrderByDescending(x => x.OrderId).First() : null;
					var res = (!isOcc && resByTable.TryGetValue(t.ID, out var rr0)) ? rr0 : null;
					return new FloorTableDto
					{
						Id = t.ID, Code = t.Code, Seats = t.Seats, Shape = t.Shape,
						X = t.X, Y = t.Y, W = t.W, H = t.H,
						Status = isOcc ? "Occupied" : (res != null ? "Reserved" : t.Status),
						ReservationId = res?.ID, ReservedFor = res?.GuestName,
						ReservedAt = res != null ? res.ReservedAtUtc.ToString("HH:mm") : null, ReservedParty = res?.PartySize ?? 0,
						SinceUtc = isOcc ? DateTime.SpecifyKind(os!.Min(x => x.OpenedAt), DateTimeKind.Utc).ToString("o") : null,
						Amount = isOcc ? os!.Sum(x => x.GrandTotal) : 0m,
						OrderId = isOcc ? rep!.OrderId : (int?)null,
						OrderCount = isOcc ? os!.Count : 0,
						Guests = isOcc ? Math.Min(os!.Sum(x => x.Guests), t.Seats) : 0,
						CustomerName = isOcc ? rep!.CustomerName : "",
						Items = (isOcc && itemsByTable.TryGetValue(t.ID, out var its)) ? its : new List<object>()
					};
				}).ToList()
			}).ToList();
		}

		// POS-B2: guest arrived → open a Dine-in order on the reserved table, link it, mark Arrived. Operational — no GL.
		// The table auto-becomes "Occupied" on the floor (an open order takes priority over "Reserved"), so it drops the badge.
		public async Task<(bool ok, string? error, int? orderId)> ArriveReservationAsync(int companyId, int reservationId, int? terminalId, int? shiftId)
		{
			var r = await _db.Reservations.FirstOrDefaultAsync(x => x.ID == reservationId && x.CompanyId == companyId);
			if (r == null) return (false, "الحجز غير موجود", null);
			if (r.Status != "Booked") return (false, "الحجز غير نشط", null);
			var (ok, err, oid) = await CreateOrderAsync(companyId, r.BranchId, "Dine-in", r.TableId, r.CustomerId, terminalId, shiftId);
			if (!ok) return (false, err, null);
			if (r.PartySize > 1) await SetGuestsAsync(companyId, oid, r.PartySize);   // party size → guests (clamped to seats)
			r.OrderId = oid; r.Status = "Arrived";
			await _db.SaveChangesAsync();
			return (true, null, oid);
		}

		// POS-C2: assign (or clear) a delivery driver on an open Delivery order. Operational — no GL.
		public async Task<(bool ok, string? error)> AssignDriverAsync(int companyId, int orderId, int? driverId)
		{
			var o = await _db.PosOrders.FirstOrDefaultAsync(x => x.ID == orderId && x.CompanyId == companyId);
			if (o == null) return (false, "الطلب غير موجود");
			if (o.Status != "Open") return (false, "الطلب ليس مفتوحًا");
			if (o.OrderType != "Delivery") return (false, "هذا الطلب ليس توصيلًا");
			if (driverId != null && !await _db.Drivers.AnyAsync(d => d.ID == driverId && d.BranchId == o.BranchId && d.IsActive)) return (false, "السائق غير صحيح");
			o.DriverId = driverId;
			await _db.SaveChangesAsync();
			return (true, null);
		}

		// POS-C3: delivery lifecycle after Ready — null → OutForDelivery → Delivered. Forward-only. OPERATIONAL, no GL.
		// Independent of payment (COD: Delivered ≠ Paid). OutForDelivery requires the kitchen to be Ready first.
		private static readonly string[] DeliveryFlow = { "OutForDelivery", "Delivered" };
		public async Task<(bool ok, string? error, string? deliveryStatus)> SetDeliveryStatusAsync(int companyId, int orderId, string status)
		{
			var o = await _db.PosOrders.FirstOrDefaultAsync(x => x.ID == orderId && x.CompanyId == companyId);
			if (o == null) return (false, "الطلب غير موجود", null);
			if (o.Status != "Open") return (false, "الطلب ليس مفتوحًا", null);
			if (o.OrderType != "Delivery") return (false, "هذا الطلب ليس توصيلًا", null);
			int ti = Array.IndexOf(DeliveryFlow, status);
			if (ti < 0) return (false, "حالة توصيل غير صحيحة", null);
			int ci = o.DeliveryStatus == null ? -1 : Array.IndexOf(DeliveryFlow, o.DeliveryStatus);
			if (ti <= ci) return (false, "لا يمكن إرجاع أو تكرار حالة التوصيل", null);   // forward-only, no repeat/back
			if (status == "OutForDelivery")
			{
				var sent = await _db.PosOrderLines.AsNoTracking().Where(l => l.OrderId == orderId && l.SentQty > 0).Select(l => new { l.SentQty, l.KdsStatus }).ToListAsync();
				var kds = DeriveOrderKds(sent.Select(x => (x.SentQty, x.KdsStatus)));
				if (kds != "Ready") return (false, "لا يمكن الخروج للتوصيل قبل أن يصبح الطلب جاهزًا", null);
			}
			o.DeliveryStatus = status;
			await _db.SaveChangesAsync();
			return (true, null, status);
		}

		// POS-C4: the delivery board feed — OPEN Delivery orders for the branch with their delivery + kitchen state.
		// A COD order stays here (Open) even after Delivered, until it is paid. Read-only, no GL.
		public async Task<List<DeliveryOrderDto>> GetDeliveryOrdersAsync(int companyId, int branchId)
		{
			var orders = await _db.PosOrders.AsNoTracking()
				.Where(o => o.CompanyId == companyId && o.BranchId == branchId && o.OrderType == "Delivery" && o.Status == "Open")
				.OrderBy(o => o.OpenedAt).ToListAsync();
			if (orders.Count == 0) return new();
			var ids = orders.Select(o => o.ID).ToList();
			var allLines = await _db.PosOrderLines.AsNoTracking().Where(l => ids.Contains(l.OrderId)).Select(l => new { l.OrderId, l.SentQty, l.KdsStatus }).ToListAsync();
			var byOrder = allLines.GroupBy(x => x.OrderId).ToDictionary(g => g.Key, g => g.ToList());
			var custIds = orders.Where(o => o.CustomerId != null).Select(o => o.CustomerId!.Value).Distinct().ToList();
			var custMap = await _db.Customers.AsNoTracking().Where(c => custIds.Contains(c.ID)).ToDictionaryAsync(c => c.ID, c => c.Name);
			var drvIds = orders.Where(o => o.DriverId != null).Select(o => o.DriverId!.Value).Distinct().ToList();
			var drvMap = await _db.Drivers.AsNoTracking().Where(d => drvIds.Contains(d.ID)).ToDictionaryAsync(d => d.ID, d => d.Name);
			var result = new List<DeliveryOrderDto>();
			foreach (var o in orders)
			{
				var ls = byOrder.TryGetValue(o.ID, out var v) ? v : new();
				var kds = DeriveOrderKds(ls.Where(x => x.SentQty > 0).Select(x => (x.SentQty, x.KdsStatus)));
				result.Add(new DeliveryOrderDto
				{
					OrderId = o.ID,
					CustomerName = (o.CustomerId != null && custMap.ContainsKey(o.CustomerId.Value)) ? custMap[o.CustomerId.Value] : "",
					Area = o.DeliveryArea ?? "", Address = o.DeliveryAddress ?? "", Phone = o.DeliveryPhone ?? "",
					DriverId = o.DriverId, DriverName = (o.DriverId != null && drvMap.ContainsKey(o.DriverId.Value)) ? drvMap[o.DriverId.Value] : "",
					DeliveryStatus = o.DeliveryStatus, KitchenStatus = kds,
					SinceUtcIso = DateTime.SpecifyKind(o.OpenedAt, DateTimeKind.Utc).ToString("o"),
					ItemCount = ls.Count, GrandTotal = o.GrandTotal, DeliveryFee = o.DeliveryFee
				});
			}
			return result;
		}
	}
}
