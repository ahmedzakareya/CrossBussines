namespace CrossBuy.Models.Context.Inventory
{
	// Phase-I0 Inventory master data. Column names == property names (EF maps by name).

	// HM-1-أ ب-3 (Step 5): the ONE source of the stock-item classification. A sale/purchase line requires a stock
	// movement ONLY when its item is Stockable; Service/NonStockable/Asset never move stock. Consume RequiresStock
	// everywhere instead of re-typing the literal "Stockable".
	public static class ItemTypes
	{
		public const string Stockable = "Stockable";
		public const string NonStockable = "NonStockable";
		public const string Service = "Service";
		public const string Asset = "Asset";
		public static bool RequiresStock(string? itemType) => itemType == Stockable;
	}

	public class ItemCategory
	{
		public int ID { get; set; }
		public int CompanyID { get; set; }
		public string Code { get; set; } = "";
		public string Name { get; set; } = "";
		public string NameEn { get; set; } = "";
		public int? ParentId { get; set; }
		public string Kind { get; set; } = "Category";   // Category (root) | Group (child under a Category). Backward-compatible: existing rows default to Category (treated as root).
		// GL mapping (like accounting PostingRules)
		public int? InventoryAccountId { get; set; }
		public int? CogsAccountId { get; set; }
		public int? AdjustmentAccountId { get; set; }
		public int? GrniAccountId { get; set; }
		public string? DefaultCostingMethod { get; set; }   // Moving / FIFO
		public bool IsActive { get; set; } = true;
		public string? CreatedBy { get; set; }
		public DateTime? CreatedAt { get; set; }
		public string? ModifiedBy { get; set; }
		public DateTime? ModifiedAt { get; set; }
		// E-commerce storefront (nullable, additive): icon + display count for the featured-categories carousel.
		public string? StoreIcon { get; set; }
		public int? StoreItemsCount { get; set; }
	}

	public class UnitOfMeasure
	{
		public int ID { get; set; }
		public int CompanyID { get; set; }
		public string Code { get; set; } = "";
		public string Name { get; set; } = "";
		public string NameEn { get; set; } = "";
		public bool IsActive { get; set; } = true;
		public DateTime? CreatedAt { get; set; }
	}

	public class Item
	{
		public int ID { get; set; }
		public int CompanyID { get; set; }
		public string ItemCode { get; set; } = "";   // required + unique
		public string Barcode { get; set; } = "";     // required + unique
		public string Name { get; set; } = "";
		public string? NameEn { get; set; }
		public int ItemCategoryId { get; set; }
		public string ItemType { get; set; } = "Stockable";   // Stockable/NonStockable/Service/Asset
		public int BaseUoMId { get; set; }
		public int? PurchaseUoMId { get; set; }
		public int? SalesUoMId { get; set; }
		public string? CostingMethod { get; set; }            // inherits category if null
		public bool TrackBatch { get; set; }
		public bool TrackExpiry { get; set; }
		public bool TrackSerial { get; set; }
		public bool IsWeighted { get; set; }                  // HM-3: sold by weight (scale barcode → weight qty in KG)
		public int? ScaleCode { get; set; }                   // HM-3: numeric code embedded in the scale barcode; unique per company (filtered index)
		public int? DefaultTaxCodeId { get; set; }
		public string? ItemEgsCode { get; set; }
		public decimal? SalesPrice { get; set; }
		public decimal? MinMarginPct { get; set; }           // pricing 2A: per-item margin floor override (null → use InventorySettings)
		public decimal? OpeningCost { get; set; }
		public string? ImagePath { get; set; }
		public string? QuickCode { get; set; }                // POS quick/PLU code for fast keyboard entry (nullable, optional, unique per company)
		public bool IsComposite { get; set; }                 // kit / bundle parent
		public string? CompositeType { get; set; }            // Bundle (no stock) / Assembly (own stock)
		public string ProductionMethod { get; set; } = "OrderBased";  // Immediate (quick assemble) | OrderBased (staged work order)
		public bool IsActive { get; set; } = true;
		public string? CreatedBy { get; set; }
		public DateTime? CreatedAt { get; set; }
		public string? ModifiedBy { get; set; }
		public DateTime? ModifiedAt { get; set; }
		// E-commerce storefront display extras (nullable, additive — do NOT affect inventory/accounting). Item already has
		// SalesPrice (current price), ImagePath (default image), ItemCategoryId. These add the theme's decorative fields.
		public decimal? StoreOldPrice { get; set; }
		public string? StoreBadge { get; set; }      // Hot | Sale | New | Best
		public decimal? StoreRating { get; set; }    // 0..5 (bar width = Rating*20 %)
		public string? StoreVendor { get; set; }
		public string? StoreHoverImage { get; set; } // hover image web path
	}

	// Additional storefront images for a product (gallery). Display only — additive, no inventory/GL impact.
	// The product's primary picture stays on Item.ImagePath; these are the EXTRA gallery images (0..N).
	public class ItemImage
	{
		public int ID { get; set; }
		public int CompanyID { get; set; }
		public int ItemId { get; set; }
		public string Path { get; set; } = "";   // web path, e.g. /uploads/items/xxxx.jpg
		public int SortOrder { get; set; }
		public DateTime? CreatedAt { get; set; }
	}

	public class UoMConversion
	{
		public int ID { get; set; }
		public int ItemId { get; set; }
		public int FromUoMId { get; set; }
		public int ToUoMId { get; set; }
		public decimal Factor { get; set; }
	}

	// Bill of materials — components of a composite (kit/bundle) item
	public class ItemComponent
	{
		public int ID { get; set; }
		public int CompanyID { get; set; }
		public int ParentItemId { get; set; }
		public int ComponentItemId { get; set; }
		public decimal Quantity { get; set; } = 1;
		public decimal ScrapPct { get; set; }   // 4-5: extra material % to cover scrap/yield loss
		public int? UoMId { get; set; }
		public int SortOrder { get; set; }
		public DateTime? CreatedAt { get; set; }
	}

	public class ItemBarcode
	{
		public int ID { get; set; }
		public int ItemId { get; set; }
		public string Barcode { get; set; } = "";
		public int? UoMId { get; set; }
	}

	public class Warehouse
	{
		public int ID { get; set; }
		public int CompanyID { get; set; }
		public string Code { get; set; } = "";
		public string Name { get; set; } = "";
		public string NameEn { get; set; } = "";
		public string WarehouseType { get; set; } = "Main";   // Main/Transit/Quarantine/Scrap/Consignment/Virtual
		public int? BranchHierarchicalId { get; set; }
		public int? KeeperEmployeeId { get; set; }
		public bool AllowNegativeStock { get; set; }
		public bool IsActive { get; set; } = true;
		public string? CreatedBy { get; set; }
		public DateTime? CreatedAt { get; set; }
		public string? ModifiedBy { get; set; }
		public DateTime? ModifiedAt { get; set; }
	}

	public class BinLocation
	{
		public int ID { get; set; }
		public int WarehouseId { get; set; }
		public string Code { get; set; } = "";
		public string? Name { get; set; }
		public int? ParentId { get; set; }               // Rack: parent = its Section
		public string LocationType { get; set; } = "Section";   // Section (top under warehouse) | Rack (under a Section) | Bin. Pure locational dimension — NEVER carries value/GL.
		public bool IsActive { get; set; } = true;
	}

	public class ItemWarehouseSetting
	{
		public int ID { get; set; }
		public int ItemId { get; set; }
		public int WarehouseId { get; set; }
		public decimal? ReorderPoint { get; set; }
		public decimal? MinQty { get; set; }
		public decimal? MaxQty { get; set; }
		public decimal? SafetyStock { get; set; }
		public int? LeadTimeDays { get; set; }
		public int? DefaultSectionId { get; set; }        // default Section (BinLocation.LocationType=Section) — required by UI on setup, nullable in DB for backward compat
		public int? DefaultBinLocationId { get; set; }    // optional default Rack (BinLocation.LocationType=Rack) under the chosen section
	}

	// ===== Phase I1: stock ledger, costing & GL =====

	// immutable movement ledger row (one in/out per item-warehouse)
	public class StockMovement
	{
		public int ID { get; set; }
		public int CompanyID { get; set; }
		public string? MovementNo { get; set; }
		public DateTime MovementDate { get; set; }
		public int ItemId { get; set; }
		public int WarehouseId { get; set; }
		public int? BinLocationId { get; set; }
		public int? BatchId { get; set; }
		public string? SerialNo { get; set; }
		public short Direction { get; set; }              // +1 in / -1 out
		public decimal QtyBase { get; set; }              // base unit, positive
		public int? UoMId { get; set; }
		public decimal? QtyInUoM { get; set; }
		public decimal UnitCost { get; set; }             // per base unit
		public decimal TotalCost { get; set; }            // movement value
		public string? SourceType { get; set; }
		public int? SourceId { get; set; }
		public int? SourceLineId { get; set; }
		public int? JournalEntryId { get; set; }
		public string? Notes { get; set; }
		public string? CreatedBy { get; set; }
		public DateTime? CreatedAt { get; set; }
	}

	// current valuation balance per (item, warehouse) — Moving Average
	public class StockBalance
	{
		public int ID { get; set; }
		public int CompanyID { get; set; }
		public int ItemId { get; set; }
		public int WarehouseId { get; set; }
		public decimal QtyOnHand { get; set; }
		public decimal TotalValue { get; set; }
		public decimal AvgCost { get; set; }
		public DateTime? LastMovementAt { get; set; }
	}

	// per-(item, warehouse, bin) QUANTITY only — locational, NO value, NO GL. Maintained by StockService when a movement carries a BinLocationId.
	// Σ BinStock.QtyOnHand for an (item, warehouse) is always <= StockBalance.QtyOnHand; the remainder is "unlocated" stock.
	public class BinStock
	{
		public int ID { get; set; }
		public int CompanyID { get; set; }
		public int WarehouseId { get; set; }
		public int BinLocationId { get; set; }
		public int ItemId { get; set; }
		public decimal QtyOnHand { get; set; }
		public DateTime? LastMovementAt { get; set; }
	}

	// FIFO cost layer (remaining qty of a receipt)
	public class StockCostLayer
	{
		public int ID { get; set; }
		public int CompanyID { get; set; }
		public int ItemId { get; set; }
		public int WarehouseId { get; set; }
		public int? BatchId { get; set; }
		public DateTime ReceiptDate { get; set; }
		public decimal QtyRemaining { get; set; }
		public decimal UnitCost { get; set; }
		public int? SourceMovementId { get; set; }
		public DateTime? CreatedAt { get; set; }
	}

	// HM-D6 item 5: PERMANENT audit trail of every inv-reconcile fix (one row per changed StockBalance). Without this the
	// reconcile tool would silently erase the evidence of this family of bugs. Records before/after qty+value, who, when, JE.
	public class InventoryReconcileLog
	{
		public int ID { get; set; }
		public int CompanyID { get; set; }
		public int ItemId { get; set; }
		public int WarehouseId { get; set; }
		public decimal QtyBefore { get; set; }
		public decimal QtyAfter { get; set; }
		public decimal ValueBefore { get; set; }
		public decimal ValueAfter { get; set; }
		public decimal AvgBefore { get; set; }
		public decimal AvgAfter { get; set; }
		public string? RanBy { get; set; }
		public DateTime RanAt { get; set; }
		public int? JournalEntryId { get; set; }
		public string? Reason { get; set; }
	}

	public class StockBatch
	{
		public int ID { get; set; }
		public int CompanyID { get; set; }
		public int ItemId { get; set; }
		public string BatchNo { get; set; } = "";
		public DateTime? ExpiryDate { get; set; }
		public DateTime? CreatedAt { get; set; }
	}

	public class StockSerial
	{
		public int ID { get; set; }
		public int CompanyID { get; set; }
		public int ItemId { get; set; }
		public string SerialNo { get; set; } = "";
		public int? WarehouseId { get; set; }
		public string Status { get; set; } = "InStock";   // InStock / Issued
		public int? LastMovementId { get; set; }
		public DateTime? CreatedAt { get; set; }
	}

	// ===== Phase I3: Procurement =====
	public class PurchaseOrder
	{
		public int ID { get; set; }
		public int CompanyID { get; set; }
		public string? OrderNo { get; set; }
		public DateTime OrderDate { get; set; }
		public DateTime? ExpectedDate { get; set; }
		public int VendorId { get; set; }
		public int? WarehouseId { get; set; }
		public int? CurrencyId { get; set; }
		public decimal SubTotal { get; set; }
		public decimal TaxTotal { get; set; }
		public decimal GrandTotal { get; set; }
		// Multi-Currency (1-1): rate captured at order date (GL conversion happens at invoice conversion)
		public decimal? ExchangeRate { get; set; }
		public string Status { get; set; } = "Draft";     // Draft/Approved/Received/Closed/Cancelled
		public int? ProjectId { get; set; }                // analytic dimension (carried to the vendor invoice on convert)
		public string? Notes { get; set; }
		public string? CreatedBy { get; set; }
		public DateTime? CreatedAt { get; set; }
		public ICollection<PurchaseOrderLine> Lines { get; set; } = new List<PurchaseOrderLine>();
	}

	public class PurchaseOrderLine
	{
		public int ID { get; set; }
		public int PurchaseOrderId { get; set; }
		public int LineNo { get; set; }
		public int? ItemId { get; set; }
		public string? ItemDescription { get; set; }
		public decimal Qty { get; set; } = 1;
		public int? UoMId { get; set; }
		public decimal UnitPrice { get; set; }
		public decimal DiscountAmount { get; set; }
		public decimal TaxRate { get; set; }
		public decimal LineTotal { get; set; }
		public decimal ReceivedQty { get; set; }
	}

	public class GoodsReceipt
	{
		public int ID { get; set; }
		public int CompanyID { get; set; }
		public string? ReceiptNo { get; set; }
		public DateTime ReceiptDate { get; set; }
		public int? VendorId { get; set; }
		public int WarehouseId { get; set; }
		public int? PurchaseOrderId { get; set; }
		public string Status { get; set; } = "Posted";    // Draft/Posted/Cancelled
		public decimal TotalCost { get; set; }             // always in branch functional currency
		// Multi-Currency (1-1): source (vendor/PO) currency + rate, for display; cost above is base
		public int? CurrencyId { get; set; }
		public decimal? ExchangeRate { get; set; }
		public int? InvoiceId { get; set; }
		public string? Notes { get; set; }
		public string? CreatedBy { get; set; }
		public DateTime? CreatedAt { get; set; }
		public ICollection<GoodsReceiptLine> Lines { get; set; } = new List<GoodsReceiptLine>();
	}

	public class GoodsReceiptLine
	{
		public int ID { get; set; }
		public int GoodsReceiptId { get; set; }
		public int LineNo { get; set; }
		public int ItemId { get; set; }
		public decimal Qty { get; set; } = 1;
		public int? UoMId { get; set; }
		public decimal UnitCost { get; set; }
		public decimal LineTotal { get; set; }
		public string? BatchNo { get; set; }
		public DateTime? ExpiryDate { get; set; }
		public string? SerialNo { get; set; }
		public int? PurchaseOrderLineId { get; set; }
		public int? StockMovementId { get; set; }
	}

	// ===== Phase I4: Sales =====
	public class SalesOrder
	{
		public int ID { get; set; }
		public int CompanyID { get; set; }
		public string? OrderNo { get; set; }
		public DateTime OrderDate { get; set; }
		public DateTime? ExpectedDate { get; set; }
		public int CustomerId { get; set; }
		public int? WarehouseId { get; set; }
		public int? CurrencyId { get; set; }
		public decimal SubTotal { get; set; }
		public decimal TaxTotal { get; set; }
		public decimal GrandTotal { get; set; }
		// Multi-Currency (1-1): rate captured at order date (GL conversion happens at invoice conversion)
		public decimal? ExchangeRate { get; set; }
		public string Status { get; set; } = "Draft";     // Draft/Approved/Delivered/Closed/Cancelled
		public int? ProjectId { get; set; }                // analytic dimension (carried to the sales invoice on convert)
		public string? Notes { get; set; }
		public string? CreatedBy { get; set; }
		public DateTime? CreatedAt { get; set; }
		public ICollection<SalesOrderLine> Lines { get; set; } = new List<SalesOrderLine>();
	}

	public class SalesOrderLine
	{
		public int ID { get; set; }
		public int SalesOrderId { get; set; }
		public int LineNo { get; set; }
		public int? ItemId { get; set; }
		public string? ItemDescription { get; set; }
		public decimal Qty { get; set; } = 1;
		public int? UoMId { get; set; }
		public decimal UnitPrice { get; set; }
		public decimal DiscountAmount { get; set; }
		public decimal TaxRate { get; set; }
		public decimal LineTotal { get; set; }
		public decimal DeliveredQty { get; set; }
	}

	public class DeliveryNote
	{
		public int ID { get; set; }
		public int CompanyID { get; set; }
		public string? DeliveryNo { get; set; }
		public DateTime DeliveryDate { get; set; }
		public int? CustomerId { get; set; }
		public int WarehouseId { get; set; }
		public int? SalesOrderId { get; set; }
		public string Status { get; set; } = "Posted";    // Draft/Posted/Cancelled
		public decimal TotalCost { get; set; }             // always in branch functional currency
		// Multi-Currency (1-1): source (customer/SO) currency + rate, for display; cost above is base
		public int? CurrencyId { get; set; }
		public decimal? ExchangeRate { get; set; }
		public int? InvoiceId { get; set; }
		public string? Notes { get; set; }
		public string? CreatedBy { get; set; }
		public DateTime? CreatedAt { get; set; }
		public ICollection<DeliveryNoteLine> Lines { get; set; } = new List<DeliveryNoteLine>();
	}

	public class DeliveryNoteLine
	{
		public int ID { get; set; }
		public int DeliveryNoteId { get; set; }
		public int LineNo { get; set; }
		public int ItemId { get; set; }
		public decimal Qty { get; set; } = 1;
		public int? UoMId { get; set; }
		public decimal UnitCost { get; set; }
		public decimal LineTotal { get; set; }
		public string? BatchNo { get; set; }
		public string? SerialNo { get; set; }
		public int? SalesOrderLineId { get; set; }
		public int? StockMovementId { get; set; }
	}

	// ===== Phase I6: Stock transfers between warehouses =====
	public class StockTransfer
	{
		public int ID { get; set; }
		public int CompanyID { get; set; }
		public string? TransferNo { get; set; }
		public DateTime TransferDate { get; set; }
		public int FromWarehouseId { get; set; }
		public int ToWarehouseId { get; set; }
		public string Status { get; set; } = "Posted";
		public decimal TotalCost { get; set; }
		public int? JournalEntryId { get; set; }
		public string? Notes { get; set; }
		public string? CreatedBy { get; set; }
		public DateTime? CreatedAt { get; set; }
		public ICollection<StockTransferLine> Lines { get; set; } = new List<StockTransferLine>();
	}

	public class StockTransferLine
	{
		public int ID { get; set; }
		public int StockTransferId { get; set; }
		public int LineNo { get; set; }
		public int ItemId { get; set; }
		public decimal Qty { get; set; } = 1;
		public int? UoMId { get; set; }
		public decimal UnitCost { get; set; }
		public decimal LineTotal { get; set; }
		public string? BatchNo { get; set; }
		public string? SerialNo { get; set; }
		public int? OutMovementId { get; set; }
		public int? InMovementId { get; set; }
	}

	// ===== Phase I7: Stock count & adjustment =====
	public class StockCount
	{
		public int ID { get; set; }
		public int CompanyID { get; set; }
		public string? CountNo { get; set; }
		public DateTime CountDate { get; set; }
		public int WarehouseId { get; set; }
		public string Status { get; set; } = "Posted";
		public decimal TotalAdjValue { get; set; }
		public string? Notes { get; set; }
		public string? CreatedBy { get; set; }
		public DateTime? CreatedAt { get; set; }
		public ICollection<StockCountLine> Lines { get; set; } = new List<StockCountLine>();
	}

	public class StockCountLine
	{
		public int ID { get; set; }
		public int StockCountId { get; set; }
		public int LineNo { get; set; }
		public int ItemId { get; set; }
		public decimal BookQty { get; set; }
		public decimal CountedQty { get; set; }
		public decimal DiffQty { get; set; }
		public decimal UnitCost { get; set; }
		public decimal DiffValue { get; set; }
		public int? AdjustmentMovementId { get; set; }
		public string? Reason { get; set; }              // null=routine count; "WriteOff" etc. when an adjustment is a write-off (AdjustmentReason mode)
		// HM-7 batch-aware count: the batch this line counted (null for a non-tracked item = the pre-HM-7 behaviour).
		public string? BatchNo { get; set; }
		public DateTime? ExpiryDate { get; set; }
		public bool BatchCreatedInCount { get; set; }    // true when the count itself created this batch (shelf stock with an unregistered batch)
	}

	// ===== Write-off / damage document (separate-document mode) =====
	public class StockWriteOff
	{
		public int ID { get; set; }
		public int CompanyID { get; set; }
		public string? WriteOffNo { get; set; }          // WOF-YYYY-NNNNN
		public DateTime WriteOffDate { get; set; }
		public int WarehouseId { get; set; }
		public string Status { get; set; } = "Posted";
		public string? Reason { get; set; }              // doc-level default reason: Damaged | Expired | Lost | Other
		public decimal TotalValue { get; set; }
		public string? Notes { get; set; }
		public string? CreatedBy { get; set; }
		public DateTime? CreatedAt { get; set; }
		public int? JournalEntryId { get; set; }
		public ICollection<StockWriteOffLine> Lines { get; set; } = new List<StockWriteOffLine>();
	}

	public class StockWriteOffLine
	{
		public int ID { get; set; }
		public int StockWriteOffId { get; set; }
		public int LineNo { get; set; }
		public int ItemId { get; set; }
		public decimal Qty { get; set; }
		public int? UoMId { get; set; }
		public string? BatchNo { get; set; }
		public string? SerialNo { get; set; }
		public string? Reason { get; set; }              // per-line reason (overrides doc default)
		public decimal UnitCost { get; set; }
		public decimal LineValue { get; set; }
		public int? MovementId { get; set; }
	}

	// inventory document approval (governance): high-value docs are held until approved, then executed
	public class InventoryApproval
	{
		public int ID { get; set; }
		public int CompanyID { get; set; }
		public string DocType { get; set; } = "";       // PurchaseOrder | StockTransfer | StockCount
		public decimal Amount { get; set; }
		public string? PayloadJson { get; set; }
		public string Status { get; set; } = "Pending"; // Pending | Approved | Rejected
		public int? RequestedByEmployeeId { get; set; }
		public DateTime? RequestedAt { get; set; }
		public int? DecidedByEmployeeId { get; set; }
		public DateTime? DecidedAt { get; set; }
		public string? DecisionNote { get; set; }
		public string? ResultDocNo { get; set; }
	}

	// inventory RBAC: which employee holds which inventory role (+ optional branch scope for keepers)
	public class InventoryUserRole
	{
		public int ID { get; set; }
		public int CompanyID { get; set; }
		public int EmployeeId { get; set; }
		public string Role { get; set; } = "";        // WarehouseKeeper | PurchasingOfficer | InventoryManager | InventoryAuditor
		public int? ScopeBranchId { get; set; }
		public DateTime? CreatedAt { get; set; }
	}

	// per-company inventory settings (transfer GL mode, approval threshold)
	public class InventorySettings
	{
		public int ID { get; set; }
		public int CompanyID { get; set; }
		public string InterBranchTransferMode { get; set; } = "CostCenterPosting";   // NoGL | CostCenterPosting
		public string WriteOffMode { get; set; } = "SeparateDocument";               // SeparateDocument | AdjustmentReason
		public decimal ApprovalThreshold { get; set; }
		// Pricing 2-1: when a sales doc is in a foreign currency with no price list in that currency,
		// suggest a TEMPORARY converted price from Item.SalesPrice (true) or force manual entry (false).
		public bool ConvertBasePriceForForeignDocs { get; set; } = true;
		// Pricing 2A: global gross-margin floor. Net sell price (converted to functional) must be ≥ cost×(1+MinMarginPct/100).
		public decimal MinMarginPct { get; set; } = 0m;
		public string MinMarginMode { get; set; } = "Off";   // Off (no check) | Warn (allow + notify) | Block (reject)
		// Pricing 2D: discount approval — a sales line whose discount% exceeds MaxLineDiscountPct needs manager authority.
		public decimal MaxLineDiscountPct { get; set; } = 0m;
		public string DiscountApprovalMode { get; set; } = "Off";   // Off | Warn (notify) | Block (require "manage")
		public DateTime? CreatedAt { get; set; }
	}

	// ===== Go-Live opening balances (cross-module: stock/AR/AP/assets/GL) =====
	public class OpeningBalance
	{
		public int ID { get; set; }
		public int CompanyID { get; set; }
		public string Kind { get; set; } = "";          // Stock | AR | AP | Asset | GL
		public string EntityRef { get; set; } = "";      // item:5/wh:1 | cust:3 | vend:4 | asset:9 | acc:110101
		public string? Description { get; set; }
		public decimal Amount { get; set; }
		public int? JournalEntryId { get; set; }
		public DateTime CutoffDate { get; set; }
		public string? CreatedBy { get; set; }
		public DateTime? CreatedAt { get; set; }
	}

	public class IntegrityCheckRun
	{
		public int ID { get; set; }
		public int CompanyID { get; set; }
		public DateTime RunAt { get; set; }
		public string Source { get; set; } = "Manual";   // Manual | Scheduled
		public bool AllOk { get; set; }
		public int FailedCount { get; set; }
		public string? Summary { get; set; }              // JSON snapshot of the checks
	}

	public class OpeningBalanceControl
	{
		[System.ComponentModel.DataAnnotations.Key]
		public int CompanyID { get; set; }              // PK
		public DateTime? CutoffDate { get; set; }
		public bool Finalized { get; set; }
		public DateTime? FinalizedAt { get; set; }
		public string? FinalizedBy { get; set; }
	}

	// ===== Phase I9: Landed cost =====
	public class LandedCost
	{
		public int ID { get; set; }
		public int CompanyID { get; set; }
		public string? LandedNo { get; set; }
		public DateTime LandedDate { get; set; }
		public int GoodsReceiptId { get; set; }
		public string AllocationMethod { get; set; } = "Value";   // Value / Qty
		public decimal TotalAmount { get; set; }
		public string Status { get; set; } = "Posted";
		public int? JournalEntryId { get; set; }
		public string? Notes { get; set; }
		public string? CreatedBy { get; set; }
		public DateTime? CreatedAt { get; set; }
		public ICollection<LandedCostCharge> Charges { get; set; } = new List<LandedCostCharge>();
	}

	public class LandedCostCharge
	{
		public int ID { get; set; }
		public int LandedCostId { get; set; }
		public int LineNo { get; set; }
		public string? Description { get; set; }
		public decimal Amount { get; set; }
		public int AccountId { get; set; }
	}

	// Module 4 (Manufacturing) 4-1: a production work order. BOM = the manufactured item's ItemComponents.
	// Completion backflushes components → WIP → finished goods (+ optional labor/overhead), posting one balanced JE.
	public class ManufWorkOrder
	{
		public int ID { get; set; }
		public int CompanyID { get; set; }
		public string? WoNo { get; set; }
		public int ItemId { get; set; }                // manufactured item
		public decimal Qty { get; set; }
		public decimal ProducedQty { get; set; }
		public int WarehouseId { get; set; }
		public string Status { get; set; } = "Draft";  // Draft | Released | Completed | Cancelled
		public DateTime? PlannedStart { get; set; }
		public DateTime? PlannedEnd { get; set; }
		public decimal LaborCost { get; set; }
		public decimal OverheadCost { get; set; }
		public decimal MaterialCost { get; set; }
		public decimal UnitCost { get; set; }
		public string? Notes { get; set; }
		public int? OwnerEmployeeId { get; set; }
		public int? JournalEntryId { get; set; }
		public DateTime? CompletedAt { get; set; }
		public string? CreatedBy { get; set; }
		public DateTime? CreatedAt { get; set; }
		// staged WIP (Module 4 staged): Mode = Immediate | OrderBased (stamped at create from item, overridable)
		public string Mode { get; set; } = "OrderBased";
		public decimal WipBalance { get; set; }   // running WIP held by THIS order; must net to 0 at Done/Cancel
		public DateTime? ReleasedAt { get; set; }
		public DateTime? ClosedAt { get; set; }
	}

	public class ManufWorkOrderComponent
	{
		public int ID { get; set; }
		public int CompanyID { get; set; }
		public int WorkOrderId { get; set; }
		public int ItemId { get; set; }
		public decimal PlannedQty { get; set; }
		public decimal IssuedQty { get; set; }
		public int? UoMId { get; set; }
		public decimal UnitCost { get; set; }
	}

	// Module 4 4-2: a work center with labor + overhead hourly rates.
	public class ManufWorkCenter
	{
		public int ID { get; set; }
		public int CompanyID { get; set; }
		public string? Code { get; set; }
		public string Name { get; set; } = "";
		public decimal CostPerHour { get; set; }
		public decimal OverheadPerHour { get; set; }
		public bool IsActive { get; set; } = true;
		public DateTime? CreatedAt { get; set; }
	}

	// Module 4 4-2: a routing operation for a manufactured item. Setup = per order; Run = per unit.
	public class ManufRoutingOp
	{
		public int ID { get; set; }
		public int CompanyID { get; set; }
		public int ItemId { get; set; }
		public int Seq { get; set; } = 1;
		public int WorkCenterId { get; set; }
		public string? OperationName { get; set; }
		public decimal SetupMins { get; set; }
		public decimal RunMinsPerUnit { get; set; }
		public DateTime? CreatedAt { get; set; }
	}

	// Module 4 4-3: a production plan (MRP-lite). Header + demand lines for finished items.
	public class ManufPlan
	{
		public int ID { get; set; }
		public int CompanyID { get; set; }
		public string Name { get; set; } = "";
		public DateTime? PlanDate { get; set; }
		public string Status { get; set; } = "Draft"; // Draft / Generated
		public string? CreatedBy { get; set; }
		public DateTime? CreatedAt { get; set; }
	}

	public class ManufPlanDemand
	{
		public int ID { get; set; }
		public int CompanyID { get; set; }
		public int PlanId { get; set; }
		public int ItemId { get; set; }
		public decimal Qty { get; set; }
		public DateTime? DueDate { get; set; }
		public DateTime? CreatedAt { get; set; }
	}

	// Module 4 (بند3): a labor line on a work order, by source.
	// Employee → Cr 520101 (reclass); External → Cr cash/payable (+ Cr 210202 WHT); Applied → Cr 520108.
	// Posted when added (during Released/InProgress); raises WIP (1105) and the order's WipBalance by Amount.
	public class ManufWorkOrderLabor
	{
		public int ID { get; set; }
		public int CompanyID { get; set; }
		public int WorkOrderId { get; set; }
		public string SourceType { get; set; } = "Applied";   // Employee | External | Applied
		public int? EmployeeId { get; set; }
		public string? WorkerName { get; set; }
		public decimal Hours { get; set; }
		public decimal RatePerHour { get; set; }
		public decimal Amount { get; set; }
		public int? WhtCodeId { get; set; }
		public decimal WhtAmount { get; set; }
		public int? CreditAccountId { get; set; }            // resolved Cr account (520101 / cash / 520108)
		// MC (بند ب): foreign currency for External lines. Amount stays FUNCTIONAL (what hits WIP).
		public int? CurrencyId { get; set; }                 // line currency (null/functional = functional)
		public decimal? ExchangeRate { get; set; }           // rate to functional (1 if functional)
		public decimal? AmountForeign { get; set; }          // Hours × RatePerHour in the line currency
		public int? JournalEntryId { get; set; }
		public string? CreatedBy { get; set; }
		public DateTime? CreatedAt { get; set; }
	}
}
