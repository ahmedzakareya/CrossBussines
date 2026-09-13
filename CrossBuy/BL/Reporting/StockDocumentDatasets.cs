namespace CrossBuy.BL.Reporting
{
    // ============================================================================================
    // THE WAREHOUSE DOCUMENTS AS PRINTABLE REPORTS — receipt, delivery, transfer, write-off, count.
    //
    // WHY THIS IS NOT TradeDocumentDatasets. That builder describes a document with a PARTY and a
    // PRICE: a customer or a vendor, a unit price, a discount, a tax rate, three money totals. A
    // stock document has none of those. It has a WAREHOUSE (two, for a transfer), a quantity and a
    // unit COST, and one value. Forcing them through the trade builder would print a column headed
    // "customer" holding a warehouse name and one headed "price" holding an average cost — a report
    // that is wrong in the only way a report must never be wrong, in what it says its numbers ARE.
    //
    // So: the same idea, a second vocabulary. One Build, five documents, one shape out — a change to
    // the stock-document shape lands on all five at once, exactly as the trade builder does for the
    // six priced ones.
    //
    // ROW-LEVEL HEADER FIELDS, deliberately, and for the same reason as the trade documents: a visual
    // template binds a Field element to a column and reads it from the FIRST row, so the number, the
    // date, the warehouse and the total have to be present on every line or a design cannot place
    // them in a header band. They repeat; a document is a handful of rows and the cost is nothing.
    //
    // THE COUNT IS THE ODD ONE and it is modelled honestly rather than squeezed: a count line has a
    // book quantity, a counted quantity and a difference, so Qty carries the COUNTED quantity and the
    // two extra fields are declared for it. The other four leave them null, which a template renders
    // as empty — not as a zero, which would read as "counted nothing".
    // ============================================================================================
    public static class StockDocumentDatasetCodes
    {
        public const string GoodsReceipt = "Inventory.ReceiptDocument";
        public const string DeliveryNote = "Inventory.DeliveryDocument";
        public const string StockTransfer = "Inventory.TransferDocument";
        public const string StockWriteOff = "Inventory.WriteOffDocument";
        public const string StockCount = "Inventory.CountDocument";
    }

    public static class StockDocumentDatasets
    {
        // Bounded by the document's own lines. The cap exists for the pathological import, and
        // TruncateAndDeclare means a document that hits it SAYS SO on its face rather than quietly
        // printing a subset — which on a valued document is the difference between a total that foots
        // and one that does not.
        public const int MaxRows = 2000;

        public static ReportDatasetDefinition Build(
            string datasetCode, string titleAr, string titleEn,
            string descriptionAr, string descriptionEn,
            string idParameterKey, string idParameterTitleAr, string idParameterTitleEn,
            bool isCount = false) => new()
        {
            DatasetCode = datasetCode,
            Module = "Inventory",
            TitleAr = titleAr,
            TitleEn = titleEn,
            DescriptionAr = descriptionAr,
            DescriptionEn = descriptionEn,
            DataSourceKey = datasetCode,

            // The module's ordinary reporting grant, not a key per document: a person who may read the
            // stock registers may read a stock document. Inventing nine grants would be nine decisions
            // nobody wants to make separately.
            RequiredPermissionKey = InventoryReportPermissions.View,
            MaxRows = MaxRows,
            RowCapPolicy = ReportRowCapPolicy.TruncateAndDeclare,

            Fields = new[]
            {
                // ---- the lines ---------------------------------------------------------------------
                new ReportDatasetField
                {
                    Key = "LineNo", TitleAr = "م", TitleEn = "#",
                    Type = ReportFieldType.Integer, Align = ReportAlign.Center, WidthMm = 10,
                },
                new ReportDatasetField
                {
                    Key = "ItemCode", TitleAr = "كود الصنف", TitleEn = "Item code", WidthMm = 26,
                },
                new ReportDatasetField
                {
                    Key = "ItemName", TitleAr = "الصنف", TitleEn = "Item", WidthMm = 62,
                },
                new ReportDatasetField
                {
                    Key = "Qty", TitleAr = isCount ? "الكمية المجرودة" : "الكمية", TitleEn = isCount ? "Counted" : "Qty",
                    Type = ReportFieldType.Decimal, Align = ReportAlign.End, WidthMm = 20,
                    SupportedAggregates = new[] { ReportAggregate.Sum },
                },
                new ReportDatasetField
                {
                    Key = "BookQty", TitleAr = "الكمية الدفترية", TitleEn = "Book qty",
                    Type = ReportFieldType.Decimal, Align = ReportAlign.End, WidthMm = 22,
                    VisibleByDefault = isCount,
                    SupportedAggregates = new[] { ReportAggregate.Sum },
                },
                new ReportDatasetField
                {
                    Key = "DiffQty", TitleAr = "الفرق", TitleEn = "Difference",
                    Type = ReportFieldType.Decimal, Align = ReportAlign.End, WidthMm = 20,
                    VisibleByDefault = isCount,
                    SupportedAggregates = new[] { ReportAggregate.Sum },
                },
                new ReportDatasetField
                {
                    Key = "UnitCost", TitleAr = "التكلفة", TitleEn = "Unit cost",
                    Type = ReportFieldType.Money, Align = ReportAlign.End, WidthMm = 24,
                },
                new ReportDatasetField
                {
                    Key = "LineTotal", TitleAr = "القيمة", TitleEn = "Value",
                    Type = ReportFieldType.Money, Align = ReportAlign.End, WidthMm = 26,
                    SupportedAggregates = new[] { ReportAggregate.Sum },
                },
                new ReportDatasetField
                {
                    Key = "BatchNo", TitleAr = "الدفعة", TitleEn = "Batch",
                    WidthMm = 24, VisibleByDefault = false,
                },
                new ReportDatasetField
                {
                    Key = "SerialNo", TitleAr = "السيريال", TitleEn = "Serial",
                    WidthMm = 26, VisibleByDefault = false,
                },
                new ReportDatasetField
                {
                    Key = "ExpiryDate", TitleAr = "الصلاحية", TitleEn = "Expiry",
                    Type = ReportFieldType.Date, Format = "yyyy-MM-dd", WidthMm = 24, VisibleByDefault = false,
                },
                new ReportDatasetField
                {
                    Key = "LineReason", TitleAr = "السبب", TitleEn = "Reason",
                    WidthMm = 40, VisibleByDefault = false,
                },

                // ---- the header, on every row so a design can place it ------------------------------
                new ReportDatasetField
                {
                    Key = "DocumentNo", TitleAr = "رقم المستند", TitleEn = "Document no.", WidthMm = 30,
                },
                new ReportDatasetField
                {
                    Key = "DocumentDate", TitleAr = "التاريخ", TitleEn = "Date",
                    Type = ReportFieldType.Date, Format = "yyyy-MM-dd", WidthMm = 26,
                },
                new ReportDatasetField
                {
                    Key = "WarehouseName", TitleAr = "المخزن", TitleEn = "Warehouse", WidthMm = 44,
                },
                new ReportDatasetField
                {
                    // A transfer has two. Everything else leaves it null, and a template that does not
                    // place it simply never asks.
                    Key = "ToWarehouseName", TitleAr = "إلى مخزن", TitleEn = "To warehouse",
                    WidthMm = 44, VisibleByDefault = false,
                },
                new ReportDatasetField
                {
                    Key = "PartyName", TitleAr = "المورّد / العميل", TitleEn = "Party",
                    WidthMm = 44, VisibleByDefault = false,
                },
                new ReportDatasetField
                {
                    Key = "Status", TitleAr = "الحالة", TitleEn = "Status", WidthMm = 22,
                },
                new ReportDatasetField
                {
                    Key = "Notes", TitleAr = "ملاحظات", TitleEn = "Notes",
                    WidthMm = 60, VisibleByDefault = false,
                },

                // The document's OWN total, not a sum of the lines: a stored total can differ from the
                // arithmetic, and the document must print what the business recorded.
                new ReportDatasetField
                {
                    Key = "DocumentTotal", TitleAr = "إجمالي القيمة", TitleEn = "Total value",
                    Type = ReportFieldType.Money, Align = ReportAlign.End, WidthMm = 28,
                    VisibleByDefault = false,
                },

                // ---- the issuer, so a design can head the document with it --------------------------
                new ReportDatasetField
                {
                    Key = "CompanyName", TitleAr = "الشركة", TitleEn = "Company",
                    WidthMm = 55, VisibleByDefault = false,
                },
                new ReportDatasetField
                {
                    Key = "CompanyTaxNo", TitleAr = "الرقم الضريبي", TitleEn = "Tax no.",
                    WidthMm = 30, VisibleByDefault = false,
                },
                new ReportDatasetField
                {
                    Key = "CompanyAddress", TitleAr = "عنوان الشركة", TitleEn = "Company address",
                    WidthMm = 60, VisibleByDefault = false,
                },
                new ReportDatasetField
                {
                    Key = "CompanyPhone", TitleAr = "هاتف الشركة", TitleEn = "Company phone",
                    WidthMm = 30, VisibleByDefault = false,
                },
                new ReportDatasetField
                {
                    Key = "BranchName", TitleAr = "الفرع", TitleEn = "Branch",
                    WidthMm = 40, VisibleByDefault = false,
                },
                new ReportDatasetField
                {
                    Key = "BranchLocation", TitleAr = "موقع الفرع", TitleEn = "Branch location",
                    WidthMm = 50, VisibleByDefault = false,
                },
                new ReportDatasetField
                {
                    Key = "BranchPhone", TitleAr = "هاتف الفرع", TitleEn = "Branch phone",
                    WidthMm = 30, VisibleByDefault = false,
                },
            },

            Parameters = new[]
            {
                new ReportParameterDescriptor
                {
                    Key = idParameterKey,
                    TitleAr = idParameterTitleAr, TitleEn = idParameterTitleEn,
                    Type = ReportFieldType.Integer, Required = true,
                    HelpTextAr = "معرّف المستند في النظام — تصل إليه الشاشة تلقائيًا.",
                    HelpTextEn = "The document's internal id — the screen fills this in for you.",
                },
                AccountingDatasets.CompanyParam(),
            },
        };

        public static ReportDatasetDefinition GoodsReceipt() => Build(
            StockDocumentDatasetCodes.GoodsReceipt,
            "إذن استلام", "Goods receipt",
            "إذن استلام واحد بكل بنوده: الصنف والكمية والتكلفة، مع المخزن والمورّد.",
            "One goods receipt with every line: item, quantity and cost, with the warehouse and vendor.",
            "ReceiptId", "رقم الإذن الداخلي", "Receipt id");

        public static ReportDatasetDefinition DeliveryNote() => Build(
            StockDocumentDatasetCodes.DeliveryNote,
            "إذن صرف", "Delivery note",
            "إذن صرف واحد بكل بنوده، مع المخزن والعميل.",
            "One delivery note with every line, with the warehouse and customer.",
            "DeliveryId", "رقم الإذن الداخلي", "Delivery id");

        public static ReportDatasetDefinition StockTransfer() => Build(
            StockDocumentDatasetCodes.StockTransfer,
            "تحويل مخزني", "Stock transfer",
            "تحويل مخزني واحد بكل بنوده، من مخزن إلى مخزن.",
            "One stock transfer with every line, from one warehouse to another.",
            "TransferId", "رقم التحويل الداخلي", "Transfer id");

        public static ReportDatasetDefinition StockWriteOff() => Build(
            StockDocumentDatasetCodes.StockWriteOff,
            "إهلاك مخزون", "Stock write-off",
            "مستند إهلاك واحد بكل بنوده وأسبابه.",
            "One write-off document with every line and its reason.",
            "WriteOffId", "رقم المستند الداخلي", "Write-off id");

        public static ReportDatasetDefinition StockCount() => Build(
            StockDocumentDatasetCodes.StockCount,
            "جرد مخزني", "Stock count",
            "جرد واحد بكل بنوده: الكمية الدفترية والمجرودة والفرق وقيمته.",
            "One stock count with every line: book quantity, counted quantity, the difference and its value.",
            "CountId", "رقم الجرد الداخلي", "Count id",
            isCount: true);

        public static IEnumerable<ReportDatasetDefinition> All()
        {
            yield return GoodsReceipt();
            yield return DeliveryNote();
            yield return StockTransfer();
            yield return StockWriteOff();
            yield return StockCount();
        }
    }
}
