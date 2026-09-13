namespace CrossBuy.BL.Reporting
{
    // ============================================================================================
    // WHICH REPORTS BELONG TO WHICH SCREEN.
    //
    // Print on a document screen must offer EVERY layout bound to that screen — not the layouts of one
    // report the view happened to name. Those are different things and the difference shows the first
    // time a screen prints two documents: a journal entry has a voucher today and will have a ledger
    // extract or an audit copy tomorrow, and a screen that hardcodes one report cannot grow a second
    // without an edit to its markup, a build and a deployment.
    //
    // So the binding is DECLARED HERE and the screen says only "I am Accounting.JournalEntry, and the
    // document in front of me is 15158". Adding a report to a screen is one line in this file; adding a
    // LAYOUT to a report is not even that — it is a template someone saves in Report Studio.
    //
    // ------------------------------------------------------------------------------------------------
    // WHY A REGISTRY AND NOT AN ATTRIBUTE ON THE CONTROLLER ACTION
    //
    // The binding is read by a VIEW, which has no access to the action's attributes, and by anything
    // else that later wants to ask "what can this screen print" — a menu, a permissions audit, a
    // deployment check that every bound report is actually registered. One list answers all of them.
    //
    // THE ID PARAMETER IS NAMED PER BINDING because reports do not agree on what to call it: the
    // voucher takes JournalId, an invoice print would take InvoiceId. The screen passes a document id
    // and does not need to know which.
    // ============================================================================================
    public sealed class ReportScreenBinding
    {
        // The screen, by a stable key rather than a route: a route can be renamed and a binding that
        // followed it would break silently, printing nothing and saying nothing.
        public required string ScreenKey { get; init; }

        // A registered report — the same code the catalog and Report Studio use.
        public required string ReportCode { get; init; }

        // The parameter that pins the report to the document on screen — or NULL for a register.
        //
        // A LIST SCREEN PRINTS TOO, and what it prints is not a document: it is the rows it is showing,
        // with the filters it is showing them under. That report has no document id to pin, so a
        // binding without one is a register and the screen supplies its filters instead. Modelling it
        // as "the same thing with a missing id" is what keeps one menu, one dialog and one rule for
        // both, rather than a second print mechanism for lists.
        public string? IdParameter { get; init; }

        // Ordering within the screen's menu. Explicit, because the order a person reads these in is a
        // decision and alphabetical order by report code is not one.
        public int SortOrder { get; init; }
    }

    public static class ReportScreenKeys
    {
        // One constant per screen, so a typo is a compile error rather than an empty menu. An empty
        // menu is the failure mode this whole file exists to make impossible, and a string literal in
        // a view would reintroduce it.
        public const string AccountingJournalEntry = "Accounting.JournalEntry";
        public const string AccountingSalesInvoiceDetail = "Accounting.SalesInvoiceDetail";
        public const string AccountingPurchaseInvoiceDetail = "Accounting.PurchaseInvoiceDetail";
        public const string InventoryQuotationDetails = "Inventory.QuotationDetails";
        public const string InventoryPurchaseOrderDetail = "Inventory.PurchaseOrderDetail";
        public const string InventoryPurchaseOrders = "Inventory.PurchaseOrders";
        public const string InventorySalesOrders = "Inventory.SalesOrders";
        public const string InventoryGoodsReceipts = "Inventory.GoodsReceipts";
        public const string AccountingPurchaseInvoices = "Accounting.PurchaseInvoices";
        public const string AccountingSalesInvoices = "Accounting.SalesInvoices";
        public const string AccountingSalesReturnDetail = "Accounting.SalesReturnDetail";
        public const string AccountingPurchaseReturnDetail = "Accounting.PurchaseReturnDetail";

        // The inventory document screens — all eight share one view, and each names its own key.
        public const string InventorySalesOrderDetail = "Inventory.SalesOrderDetail";
        public const string InventoryReceiptDetail = "Inventory.ReceiptDetail";
        public const string InventoryDeliveryDetail = "Inventory.DeliveryDetail";
        public const string InventoryTransferDetail = "Inventory.TransferDetail";
        public const string InventoryWriteOffDetail = "Inventory.WriteOffDetail";
        public const string InventoryCountDetail = "Inventory.CountDetail";
    }

    public static class ReportScreenBindings
    {
        private static readonly ReportScreenBinding[] All =
        {
            new()
            {
                ScreenKey = ReportScreenKeys.AccountingJournalEntry,
                ReportCode = AccountingDatasetCodes.JournalVoucher,
                IdParameter = "JournalId",
                SortOrder = 10,
            },

            new()
            {
                ScreenKey = ReportScreenKeys.AccountingSalesInvoiceDetail,
                ReportCode = TradeDocumentDatasetCodes.SalesInvoice,
                IdParameter = "InvoiceId",
                SortOrder = 10,
            },
            new()
            {
                ScreenKey = ReportScreenKeys.AccountingPurchaseInvoiceDetail,
                ReportCode = TradeDocumentDatasetCodes.PurchaseInvoice,
                IdParameter = "InvoiceId",
                SortOrder = 10,
            },
            new()
            {
                ScreenKey = ReportScreenKeys.InventoryQuotationDetails,
                ReportCode = TradeDocumentDatasetCodes.Quotation,
                IdParameter = "QuotationId",
                SortOrder = 10,
            },
            new()
            {
                ScreenKey = ReportScreenKeys.AccountingSalesReturnDetail,
                ReportCode = TradeDocumentDatasetCodes.SalesReturn,
                IdParameter = "ReturnId",
                SortOrder = 10,
            },
            new()
            {
                ScreenKey = ReportScreenKeys.AccountingPurchaseReturnDetail,
                ReportCode = TradeDocumentDatasetCodes.PurchaseReturn,
                IdParameter = "ReturnId",
                SortOrder = 10,
            },
            new()
            {
                ScreenKey = ReportScreenKeys.InventoryPurchaseOrderDetail,
                ReportCode = TradeDocumentDatasetCodes.PurchaseOrder,
                IdParameter = "OrderId",
                SortOrder = 10,
            },

            new()
            {
                ScreenKey = ReportScreenKeys.InventorySalesOrderDetail,
                ReportCode = TradeDocumentDatasetCodes.SalesOrder,
                IdParameter = "OrderId",
                SortOrder = 10,
            },
            new()
            {
                ScreenKey = ReportScreenKeys.InventoryReceiptDetail,
                ReportCode = StockDocumentDatasetCodes.GoodsReceipt,
                IdParameter = "ReceiptId",
                SortOrder = 10,
            },
            new()
            {
                ScreenKey = ReportScreenKeys.InventoryDeliveryDetail,
                ReportCode = StockDocumentDatasetCodes.DeliveryNote,
                IdParameter = "DeliveryId",
                SortOrder = 10,
            },
            new()
            {
                ScreenKey = ReportScreenKeys.InventoryTransferDetail,
                ReportCode = StockDocumentDatasetCodes.StockTransfer,
                IdParameter = "TransferId",
                SortOrder = 10,
            },
            new()
            {
                ScreenKey = ReportScreenKeys.InventoryWriteOffDetail,
                ReportCode = StockDocumentDatasetCodes.StockWriteOff,
                IdParameter = "WriteOffId",
                SortOrder = 10,
            },
            new()
            {
                ScreenKey = ReportScreenKeys.InventoryCountDetail,
                ReportCode = StockDocumentDatasetCodes.StockCount,
                IdParameter = "CountId",
                SortOrder = 10,
            },

            // THE LISTS print their own rows under their own filters. No IdParameter on any of them:
            // there is no single document to pin, and that absence is what tells the menu it is looking
            // at a register.
            new() { ScreenKey = ReportScreenKeys.InventoryPurchaseOrders,
                    ReportCode = TradeRegisterCodes.PurchaseOrders, SortOrder = 10 },
            new() { ScreenKey = ReportScreenKeys.InventorySalesOrders,
                    ReportCode = TradeRegisterCodes.SalesOrders, SortOrder = 10 },
            new() { ScreenKey = ReportScreenKeys.InventoryGoodsReceipts,
                    ReportCode = TradeRegisterCodes.GoodsReceipts, SortOrder = 10 },
            new() { ScreenKey = ReportScreenKeys.AccountingPurchaseInvoices,
                    ReportCode = TradeRegisterCodes.PurchaseInvoices, SortOrder = 10 },
            new() { ScreenKey = ReportScreenKeys.AccountingSalesInvoices,
                    ReportCode = TradeRegisterCodes.SalesInvoices, SortOrder = 10 },
        };

        // Everything bound to a screen, in the order it should be offered. Returns empty for an unknown
        // key rather than throwing: a screen asking for a binding that does not exist yet should render
        // without a print control, not fail to render.
        public static IReadOnlyList<ReportScreenBinding> For(string? screenKey) =>
            string.IsNullOrWhiteSpace(screenKey)
                ? Array.Empty<ReportScreenBinding>()
                : All.Where(b => string.Equals(b.ScreenKey, screenKey, StringComparison.Ordinal))
                     .OrderBy(b => b.SortOrder)
                     .ToArray();
    }
}
