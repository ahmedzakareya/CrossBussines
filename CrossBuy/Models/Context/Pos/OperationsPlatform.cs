namespace CrossBuy.Models.Context.Pos
{
	// ===== Operations platform — SETUP entities (admin configures before any sale) =====
	// All additive/optional. Branches with no activity type / capabilities keep working unchanged.
	// These are configuration entities only — they NEVER post GL or write stock (the 8 invariants are untouched).

	// Catalog: an activity type and the capabilities its preset turns on by default.
	public class ActivityPreset
	{
		public int ID { get; set; }
		public string Code { get; set; } = "";     // Restaurant / Cafe / Hyper / Retail
		public string Name { get; set; } = "";
		public string NameEn { get; set; } = "";
		public int Sort { get; set; }
	}

	public class ActivityPresetCapability
	{
		public int ID { get; set; }
		public int PresetId { get; set; }
		public string CapabilityKey { get; set; } = "";   // Tables/Kitchen/Manufacturing/Barcode/Weight/Modifiers/QrOrder
		public bool DefaultEnabled { get; set; }
	}

	// Actual capabilities enabled for a branch (seeded from the preset, then editable by the admin).
	public class BranchCapability
	{
		public int ID { get; set; }
		public int BranchId { get; set; }
		public string CapabilityKey { get; set; } = "";
		public bool Enabled { get; set; }
	}

	// POS settings per branch. DefaultPriceListId flows through the EXISTING PricingService (no new pricing logic).
	public class BranchPosSetting
	{
		public int ID { get; set; }
		public int BranchId { get; set; }
		public int? DefaultSalesWarehouseId { get; set; }   // the cashier deducts stock from this warehouse
		public int? DefaultPriceListId { get; set; }         // applied via PricingService (currency/promotions/priority unchanged)
		public int? DefaultTaxCodeId { get; set; }           // HM-D38: branch-level tax (Kuwait branch ⇒ VATEX); resolution order item→branch→company
		// HM-3: scale-barcode format (the scale is a per-branch device). Null prefix = no scale barcodes at this branch.
		public string? ScaleBarcodePrefix { get; set; }
		public int? ScaleItemCodeLength { get; set; }
		public int? ScaleValueLength { get; set; }
		public int? ScaleValueDecimals { get; set; }
		public string? ScaleValueType { get; set; }          // 'Weight' (implemented) | 'Price' (rejected: not supported yet)
		public string? ScaleCheckAlgo { get; set; }          // 'EanMod10'
		public decimal? ServiceChargePct { get; set; }
		public int? DefaultCurrencyId { get; set; }
		// POS-C1 Delivery
		public decimal? DefaultDeliveryFee { get; set; }   // fallback when a zone has no fee / no zone chosen
		public int? DeliveryRevenueAccountId { get; set; } // GL account for delivery income (fallback = sales revenue)
		public bool DeliveryTaxExempt { get; set; } = false;
	}

	// A dining hall / zone. A branch has one or more.
	public class DiningArea
	{
		public int ID { get; set; }
		public int BranchId { get; set; }
		public string Code { get; set; } = "";
		public string Name { get; set; } = "";
		public string? NameEn { get; set; }   // optional English name — shown when UI culture is English (like KitchenStation/DeliveryZone)
		public int Sort { get; set; }
		public bool IsActive { get; set; } = true;
	}

	// A prep station (kitchen/bar/grill). Prepares for KDS routing later — routing map is NOT built now.
	public class KitchenStation
	{
		public int ID { get; set; }
		public int BranchId { get; set; }
		public string Code { get; set; } = "";
		public string Name { get; set; } = "";
		public string? NameEn { get; set; }   // English display name (KDS shows this in EN; falls back to Name)
		public string StationType { get; set; } = "Kitchen";   // Kitchen / Bar / Grill / Prep
		public bool IsActive { get; set; } = true;
	}

	// ===== Cashier setup (RC-1): quick-touch buttons per branch =====
	// A tab/group of quick buttons on the cashier screen (e.g., Drinks / Sandwiches). Per branch.
	public class PosMenuGroup
	{
		public int ID { get; set; }
		public int BranchId { get; set; }
		public string Name { get; set; } = "";
		public string? NameEn { get; set; }
		public int Sort { get; set; }
		public bool IsActive { get; set; } = true;
		public int? KitchenStationId { get; set; }   // RC-3e: items under this tab route to this station (per-branch); null → default station
	}

	// A quick button = a branch's item placed under a group at an order. (Item↔branch↔group↔sort)
	public class PosQuickItem
	{
		public int ID { get; set; }
		public int BranchId { get; set; }
		public int? GroupId { get; set; }
		public int ItemId { get; set; }
		public int Sort { get; set; }
		public bool IsActive { get; set; } = true;
	}

	// A physical table on the floor plan. Belongs to a DiningArea. Status is COMPUTED from open orders (not stored).
	public class RestaurantTable
	{
		public int ID { get; set; }
		public int DiningAreaId { get; set; }
		public string Code { get; set; } = "";
		public int Seats { get; set; } = 4;
		public decimal X { get; set; }        // floor-plan coordinates
		public decimal Y { get; set; }
		public decimal W { get; set; } = 80;
		public decimal H { get; set; } = 80;
		public string Shape { get; set; } = "Square";   // Square / Round / Rect
		public string? QrToken { get; set; }             // opaque token; QR image generated on demand from it
		public bool IsActive { get; set; } = true;
		// Manual floor status set by staff (Available / Reserved / Cleaning / Closed).
		// "Occupied" is NOT stored here — it is derived at runtime from an Open order on the table.
		public string Status { get; set; } = "Available";
	}

	// ===== Cashier order (RC-2): critical path — order → pay → invoice + stock + GL =====
	// An OPEN order writes NOTHING to GL/stock. All accounting happens ONLY at pay (settlement),
	// reusing ReceivableService (invoice + AR/revenue/VAT + stock issue + COGS) and CreateReceiptAsync (settle to cash).
	public class PosOrder
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public int BranchId { get; set; }
		public int? BrandId { get; set; }
		public string OrderType { get; set; } = "Takeaway";   // Dine-in / Takeaway / Delivery — designed in from the start
		public int? TableId { get; set; }                     // null for takeaway; filled for dine-in later (no schema change needed)
		public int? GuestCount { get; set; }
		public string Status { get; set; } = "Open";          // Open → Paid → (Void). No accounting effect until Paid.
		public int? CurrencyId { get; set; }
		public decimal SubTotal { get; set; }
		public decimal ServiceAmount { get; set; }
		public decimal TaxTotal { get; set; }
		public decimal GrandTotal { get; set; }
		public int? InvoiceId { get; set; }                   // set at pay (SalesInvoice.ID)
		public int? ReceiptId { get; set; }                   // set at pay (Receipt.ID)
		public int? TerminalId { get; set; }                  // POS-1: which cashier terminal (nullable — operation fills it)
		public int? ShiftId { get; set; }                     // POS-1: which shift (nullable)
		public string? ReceiptNo { get; set; }                // POS-1: terminal-local receipt no (offline-safe; distinct from accounting InvoiceNo)
		public int? CashierUserId { get; set; }
		public string? Notes { get; set; }
		public DateTime OpenedAt { get; set; }
		public DateTime? ClosedAt { get; set; }
		// POS-4c Hold/Recall (takeaway parking): a held order stays Open (NO accounting) but is set aside
		// so the cashier can start a new order, then recall it later. IsHeld=false = the active order.
		public bool IsHeld { get; set; } = false;
		public DateTime? HeldAt { get; set; }
		// POS-4d-2 merge: when this order's lines were merged into another, it becomes Status='Void' and points here.
		public int? MergedIntoOrderId { get; set; }
		// Customer link (nullable → backward-compat; null resolves to the global Walk-in at pay). Set on create.
		public int? CustomerId { get; set; }
		// Number of guests seated on THIS bill (party). Drives how many chairs light up on the floor. Default 1.
		public int Guests { get; set; } = 1;
		// POS-C1 Delivery — FROZEN on the order when delivery info is set (address may change on the customer later).
		public int? DeliveryZoneId { get; set; }
		public string? DeliveryAddress { get; set; }
		public string? DeliveryArea { get; set; }
		public string? DeliveryPhone { get; set; }
		public decimal DeliveryFee { get; set; } = 0m;   // frozen zone fee (or branch default); enters the invoice at pay
		public int? DriverId { get; set; }               // POS-C2: assigned delivery driver (operational)
		// POS-C3: delivery lifecycle AFTER the kitchen is Ready — null → OutForDelivery → Delivered.
		// OPERATIONAL only, independent of payment (COD: Delivered ≠ Paid).
		public string? DeliveryStatus { get; set; }

		// RC-5 (tip): a gratuity collected at pay — a LIABILITY to staff (210207), NOT revenue, NOT taxed, NEVER touches stock.
		public decimal TipAmount { get; set; } = 0m;
		public string? TipMethod { get; set; }            // Cash / Card
		public int? TipJournalEntryId { get; set; }       // the tip JE (reversed on void)
	}

	// POS-C2: a simple per-branch delivery driver. Assignment to an order is operational (no GL).
	public class Driver
	{
		public int ID { get; set; }
		public int BranchId { get; set; }
		public string Name { get; set; } = "";
		public string Phone { get; set; } = "";
		public bool IsActive { get; set; } = true;
	}

	// POS-B: a table reservation. OPERATIONAL only (no GL). A reserved table shows "Reserved" on the floor (derived);
	// on arrival it converts to a Dine-in order (Status=Arrived, OrderId linked) — that's POS-B2.
	public class Reservation
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public int BranchId { get; set; }
		public int TableId { get; set; }
		public int? CustomerId { get; set; }          // optional link to a known customer (else just name/phone)
		public string GuestName { get; set; } = "";
		public string GuestPhone { get; set; } = "";
		public DateTime ReservedAtUtc { get; set; }   // wall-clock reservation time (single-timezone app)
		public int DurationMinutes { get; set; } = 120;
		public int PartySize { get; set; } = 2;
		public string Status { get; set; } = "Booked";   // Booked → Arrived / Cancelled / NoShow
		public string? Notes { get; set; }
		public int? OrderId { get; set; }              // set on arrival (POS-B2)
		public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
	}

	// POS-C1: a saved delivery address for a customer (a customer can have many). Master data — reusable.
	public class CustomerAddress
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public int CustomerId { get; set; }
		public int? DeliveryZoneId { get; set; }
		public string Area { get; set; } = "";
		public string Address { get; set; } = "";
		public string Phone { get; set; } = "";
		public bool IsDefault { get; set; } = false;
		public bool IsActive { get; set; } = true;
		public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
	}

	// POS-C1: a per-branch delivery zone with a fee. Frozen onto the order at order time.
	public class DeliveryZone
	{
		public int ID { get; set; }
		public int BranchId { get; set; }
		public string Name { get; set; } = "";
		public string? NameEn { get; set; }
		public decimal Fee { get; set; } = 0m;
		public bool IsActive { get; set; } = true;
	}

	public class PosOrderLine
	{
		public int ID { get; set; }
		public int OrderId { get; set; }
		public int ItemId { get; set; }
		public string ItemName { get; set; } = "";            // snapshot at add time (name may change later)
		public decimal Qty { get; set; } = 1;                 // in UoMId (or the item's base unit when null)
		public int? UoMId { get; set; }                       // HM-2: the unit this line is sold in (null = base); used at pay-time deduction
		public decimal UnitPrice { get; set; }
		public decimal DiscountAmount { get; set; }
		public decimal TaxRate { get; set; }                  // resolved from Item.DefaultTaxCodeId → else company default VAT code → else 0
		public decimal LineTotal { get; set; }                // Qty*UnitPrice - DiscountAmount (pre-tax)
		public string? Notes { get; set; }
		public int Sort { get; set; }
		// POS-4b send-to-kitchen (operational, NO accounting): how much of Qty has already gone to the kitchen.
		// SentQty==0 → nothing sent; 0<SentQty<Qty → some new qty still to send; SentQty==Qty → fully sent.
		// You can add more (Qty grows above SentQty) but cannot reduce below SentQty or delete a sent line.
		public decimal SentQty { get; set; } = 0;
		public DateTime? SentAt { get; set; }
		// RC-3a KDS (operational, NO accounting): kitchen prep state of this line.
		// NULL before it's sent; set to "New" on send-to-kitchen; kitchen advances New→Preparing→Ready (forward-only).
		public string? KdsStatus { get; set; }
		// RC-3e: which kitchen station prepares this line — FROZEN at send-to-kitchen (later config changes don't move a sent line).
		public int? StationId { get; set; }
	}

	// ===== POS-1: cashier terminals (isolated till) — setup only, no GL/stock. =====
	// Each terminal = its own cash drawer account + its own receipt sequence + its own shifts.
	// The isolation is what prevents cash/number clashes and makes offline possible later.
	public class PosTerminal
	{
		public int ID { get; set; }
		public int BranchId { get; set; }
		public string Code { get; set; } = "";          // unique per branch (e.g. T1)
		public string Name { get; set; } = "";
		public int? CashAccountId { get; set; }          // the terminal's own cash drawer (auto-created child of main cash, or admin-picked)
		public string ReceiptPrefix { get; set; } = "";  // e.g. "T1-" — makes the local sequence globally unique (offline-safe)
		public int NextReceiptNo { get; set; } = 1;       // per-terminal counter; consumed at sale (operation phase), not now
		// ---- POS-3 printing (behind a client print abstraction; window.print now, ESC/POS bridge later) ----
		public string? ReceiptPrinterName { get; set; }   // informational / future ESC-POS bridge (window.print uses the OS default)
		public int ReceiptPaperWidthMm { get; set; } = 80; // 58 or 80
		public int ReceiptCopies { get; set; } = 1;
		public bool IsActive { get; set; } = true;
		public DateTime? CreatedAt { get; set; }
	}

	// A cashier shift on a terminal. POS-1: entity + basic open/close only (Z report + counted cash come later).
	public class PosShift
	{
		public int ID { get; set; }
		public int TerminalId { get; set; }
		public string ShiftType { get; set; } = "Morning";   // Morning / Evening / ...
		public string Status { get; set; } = "Open";          // Open / Closed
		public int? OpenedByEmployeeId { get; set; }
		public decimal OpeningFloat { get; set; }             // drawer opening cash — a figure only (no GL movement)
		public DateTime OpenedAt { get; set; }
		public DateTime? ClosedAt { get; set; }
		public string? Notes { get; set; }

		// RC-6a: cash-drawer close reconciliation
		public decimal? ClosingFloat { get; set; }            // counted cash at close
		public decimal? ExpectedCash { get; set; }            // OpeningFloat + Σ cash payments − Σ cash refunds (this shift)
		public decimal? CashVariance { get; set; }            // ClosingFloat − ExpectedCash (over>0 / short<0); posted to 520111
		public int? ClosedByEmployeeId { get; set; }
		public int? VarianceJournalEntryId { get; set; }      // the over/short JE (null if variance == 0)
	}

	// ===== Payment methods (setup only) — which methods a branch accepts + their target GL account. =====
	// Definition/mapping ONLY — no payment is recorded and no GL posts here. At sale time the cashier settles
	// via the existing ReceivableService/JournalEntryService using the TargetAccountId configured here.
	public class BranchPaymentMethod
	{
		public int ID { get; set; }
		public int BranchId { get; set; }
		public string PaymentMethod { get; set; } = "Cash";   // Cash / Card / KNet / Mada / Meeza / OnAccount / Voucher ... (market-configurable, not hard-coded)
		public string? DisplayName { get; set; }                // optional label shown on the cashier (e.g. "كي-نت")
		public int? TargetAccountId { get; set; }               // cash box / bank / card-clearing / (AR for OnAccount)
		public bool IsActive { get; set; } = true;
		public int Sort { get; set; }
	}

	// ===== Cashier roles (setup only) — assign a branch's EMPLOYEES to POS roles. =====
	// Mirrors the existing role tables (AccountingUserRole/InventoryUserRole) which key by EmployeeId.
	public class BranchUserRole
	{
		public int ID { get; set; }
		public int BranchId { get; set; }
		public int EmployeeId { get; set; }                     // FK to Employee (which links to the Identity user)
		public string PosRole { get; set; } = "pos-cashier";    // pos-waiter / pos-kitchen / pos-cashier / pos-manager
		public bool IsActive { get; set; } = true;
		public DateTime? CreatedAt { get; set; }
	}

	// ===== Modifiers (setup only) — reusable option groups attached to items (M:N). =====
	// Definition/linking ONLY here — no stock deduction now; at sale time each chosen option deducts its
	// LinkedItem by QtyDeducted (per ProductionMethod) and adds ExtraPrice. Never touches GL/stock at setup.
	public class ModifierGroup
	{
		public int ID { get; set; }
		public int CompanyID { get; set; }
		public string Name { get; set; } = "";
		public string? NameEn { get; set; }
		public string Type { get; set; } = "AddOn";   // Choice (mandatory alternative, no price) / AddOn (optional, priced)
		public int MinSelect { get; set; } = 0;        // Choice → typically 1 ; AddOn → 0
		public int MaxSelect { get; set; } = 0;        // 0 = unlimited (AddOn) ; Choice → 1
		public int Sort { get; set; }
		public bool IsActive { get; set; } = true;
		public DateTime? CreatedAt { get; set; }
	}

	public class ModifierOption
	{
		public int ID { get; set; }
		public int GroupId { get; set; }
		public string Name { get; set; } = "";        // display label; blank → falls back to linked item name
		public string? NameEn { get; set; }
		public int LinkedItemId { get; set; }          // a stockable Item (what gets deducted at sale)
		public decimal QtyDeducted { get; set; } = 1;  // qty of LinkedItem consumed when chosen
		public decimal ExtraPrice { get; set; } = 0;   // added to the line price (0 for Choice alternatives)
		public bool IsDefault { get; set; }
		public int Sort { get; set; }
		public bool IsActive { get; set; } = true;
	}

	// M:N — which groups apply to which item, ordered.
	public class ItemModifierGroup
	{
		public int ID { get; set; }
		public int ItemId { get; set; }
		public int GroupId { get; set; }
		public int Sort { get; set; }
	}

	// POS-9d: idempotency log for replayed OFFLINE orders — one row per synced localGuid so a re-sent order is NEVER posted twice.
	public class PosSyncLog
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public string LocalGuid { get; set; } = "";   // the device-generated id of the settled offline order (unique)
		public int? OrderId { get; set; }               // the server order it created
		public int? InvoiceId { get; set; }             // the accounting invoice posted
		public string? ReceiptNo { get; set; }          // the offline local receipt no (kept for reference)
		public DateTime SyncedAt { get; set; }
	}

	// POS-9e: a non-blocking anomaly noticed WHILE replaying an offline order (negative stock, an item that went inactive
	// during the outage, or a price the customer paid that differs from the current catalog). The sync ALWAYS posts — this
	// is a record for the manager to review/acknowledge, never a gate. No GL/stock here.
	public class PosSyncConflict
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public int SyncLogId { get; set; }              // the replayed order (PosSyncLog)
		public int? OrderId { get; set; }
		public string ConflictType { get; set; } = "";  // NegativeStock | InactiveItem | PriceDiff
		public int? ItemId { get; set; }
		public string? Detail { get; set; }
		public decimal? OfflineValue { get; set; }       // e.g. the price the customer paid
		public decimal? ServerValue { get; set; }        // e.g. current catalog price / resulting stock
		public string Status { get; set; } = "Open";     // Open | Acknowledged
		public DateTime CreatedAt { get; set; }
		public int? AckedByEmployeeId { get; set; }
		public DateTime? AckedAt { get; set; }
	}

	// BIS-1: how a BRANCH sources a given ITEM (user decides per branch+item). Setup only — no GL/stock here
	// (the sale-time behaviour reading this table lands in BIS-2). Purely additive.
	public class BranchItemSourcing
	{
		public int ID { get; set; }
		public int BranchId { get; set; }
		public int ItemId { get; set; }
		// WorkOrder = manufacture from raw here | FinishedFromBranch = transfer finished in | SemiFromBranchComplete = transfer semi + finish here | RecipeAtSale = backflush recipe at sale
		public string Method { get; set; } = "WorkOrder";
		public int? SourceBranchId { get; set; }        // methods 2 & 3: the branch that supplies
		public int? SemiFinishedItemId { get; set; }    // method 3: the semi-finished item pulled from the source
		public string? TransferTiming { get; set; }      // methods 2 & 3: Prepaid / AtSale
		public bool IsActive { get; set; } = true;
		public string? Notes { get; set; }
		public DateTime? CreatedAt { get; set; }
	}

	// RC-4: a modifier CHOSEN on an order line (child of PosOrderLine). Purely additive — recording only.
	// The chosen option's ExtraPrice is FOLDED into PosOrderLine.UnitPrice at add time (so Pay/Split price
	// correctly with no change); this row snapshots the choice for display + drives the backflush at pay
	// (LinkedItem × QtyDeducted × line qty issued as a 0-price invoice line via StockService). No GL/stock at setup.
	public class PosOrderLineModifier
	{
		public int ID { get; set; }
		public int OrderLineId { get; set; }
		public int GroupId { get; set; }
		public int OptionId { get; set; }
		public string Name { get; set; } = "";          // snapshot label at choose time (option/linked-item name may change later)
		public int LinkedItemId { get; set; }            // the stockable Item deducted at sale
		public decimal QtyDeducted { get; set; } = 1;    // per parent-unit qty of LinkedItem consumed
		public decimal ExtraPrice { get; set; } = 0;     // per parent-unit added price (already folded into the line UnitPrice)
		public int Sort { get; set; }
	}

	// Flexible payment: an order can carry one or more payments (supports future split without rebuild).
	// Each method = a type + a target account, settled via the EXISTING ReceivableService/JournalEntryService (no new GL writer).
	// RC-2 implements Cash only; Card/KNet/Mada/Meeza/OnAccount/Subscription are added later as types over this same structure.
	public class PosPayment
	{
		public int ID { get; set; }
		public int OrderId { get; set; }
		public string PaymentMethod { get; set; } = "Cash";   // Cash / Card / KNet / Mada / Meeza / OnAccount / Subscription ...
		public decimal Amount { get; set; }
		public int? TargetAccountId { get; set; }             // cash box / bank / card-clearing / (AR for OnAccount)
		public string? Reference { get; set; }                // txn ref / approval code (card networks)
		public int? ReceiptId { get; set; }                   // RC-6c: the AR receipt this tender created (for clean reversal)
		public DateTime CreatedAt { get; set; }
	}
}
