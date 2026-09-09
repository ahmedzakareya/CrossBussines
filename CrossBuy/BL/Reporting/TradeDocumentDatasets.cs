namespace CrossBuy.BL.Reporting
{
    // ============================================================================================
    // TRADE DOCUMENTS AS PRINTABLE REPORTS — invoice, quotation, order, receipt.
    //
    // Each of these screens shows ONE document and needs to print it, and under the platform's rule
    // that means a registered dataset, not markup in a view. They are the same shape — a header, a
    // party, lines of item/quantity/price, and three totals — so the DEFINITION is built once here and
    // each document supplies only its own names and its own data source.
    //
    // WHY ONE BUILDER AND NOT SIX DEFINITIONS. Six copies of a fifteen-field list is six places for a
    // width, a format or an aggregate to drift, and this module has already paid for that four times
    // over: two page objects that disagreed, three font stacks, two cell formatters, a dialog in two
    // screens. The differences between these documents are their titles and their sources; everything
    // else being identical is the point, because a company's invoice and its quotation should print as
    // the same house document with a different name on it.
    //
    // ROW-LEVEL HEADER FIELDS, deliberately. A visual template binds a Field element to a column and
    // reads it from the FIRST row, so the document number, the date, the party and the totals have to
    // be present on every line or a design cannot place them in a header band. They repeat; a document
    // is a handful of rows and the cost is nothing.
    // ============================================================================================
    public static class TradeDocumentDatasetCodes
    {
        public const string SalesInvoice = "Sales.InvoiceDocument";
        public const string PurchaseInvoice = "Purchasing.InvoiceDocument";
        public const string Quotation = "Sales.QuotationDocument";
        public const string PurchaseOrder = "Purchasing.OrderDocument";
    }

    public static class TradeDocumentDatasets
    {
        // A document is bounded by its own lines. The cap exists for the pathological import, not for a
        // real invoice, and TruncateAndDeclare means a document that hits it SAYS SO on its face rather
        // than quietly printing a subset — which on a priced document is the difference between a total
        // that foots and one that does not.
        public const int MaxRows = 2000;

        public static ReportDatasetDefinition Build(
            string datasetCode, string titleAr, string titleEn,
            string descriptionAr, string descriptionEn,
            string idParameterKey, string idParameterTitleAr, string idParameterTitleEn,
            string partyTitleAr, string partyTitleEn) => new()
        {
            DatasetCode = datasetCode,
            Module = "Trade",
            TitleAr = titleAr,
            TitleEn = titleEn,
            DescriptionAr = descriptionAr,
            DescriptionEn = descriptionEn,
            DataSourceKey = datasetCode,

            // ONE KEY FOR ALL OF THEM, and it is the module's ordinary reporting grant rather than a new
            // one: a person who may read the sales register may read a sales invoice. Inventing a key per
            // document would mean nine more grants to administer for no decision anyone wants to make
            // separately.
            RequiredPermissionKey = AccountingReportPermissions.View,
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
                    Key = "ItemCode", TitleAr = "الصنف", TitleEn = "Item", WidthMm = 26,
                },
                new ReportDatasetField
                {
                    Key = "ItemDescription", TitleAr = "البيان", TitleEn = "Description", WidthMm = 62,
                },
                new ReportDatasetField
                {
                    Key = "Qty", TitleAr = "الكمية", TitleEn = "Qty",
                    Type = ReportFieldType.Decimal, Align = ReportAlign.End, WidthMm = 20,
                    SupportedAggregates = new[] { ReportAggregate.Sum },
                },
                new ReportDatasetField
                {
                    Key = "UnitPrice", TitleAr = "السعر", TitleEn = "Unit price",
                    Type = ReportFieldType.Money, Align = ReportAlign.End, WidthMm = 24,
                },
                new ReportDatasetField
                {
                    Key = "DiscountAmount", TitleAr = "الخصم", TitleEn = "Discount",
                    Type = ReportFieldType.Money, Align = ReportAlign.End, WidthMm = 22,
                    SupportedAggregates = new[] { ReportAggregate.Sum },
                },
                new ReportDatasetField
                {
                    Key = "TaxRate", TitleAr = "الضريبة %", TitleEn = "Tax %",
                    Type = ReportFieldType.Percent, Align = ReportAlign.End, WidthMm = 18,
                    VisibleByDefault = false,
                },
                new ReportDatasetField
                {
                    Key = "LineTotal", TitleAr = "الإجمالي", TitleEn = "Line total",
                    Type = ReportFieldType.Money, Align = ReportAlign.End, WidthMm = 26,
                    SupportedAggregates = new[] { ReportAggregate.Sum },
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
                    Key = "PartyName", TitleAr = partyTitleAr, TitleEn = partyTitleEn, WidthMm = 50,
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

                // The document's OWN totals, not a sum of the lines. A stored total can differ from the
                // arithmetic — a header discount, a rounding rule — and the document must print what the
                // business recorded, not what a report recomputed.
                new ReportDatasetField
                {
                    Key = "SubTotal", TitleAr = "الإجمالي قبل الضريبة", TitleEn = "Subtotal",
                    Type = ReportFieldType.Money, Align = ReportAlign.End, WidthMm = 28,
                    VisibleByDefault = false,
                },
                new ReportDatasetField
                {
                    Key = "TaxTotal", TitleAr = "إجمالي الضريبة", TitleEn = "Tax total",
                    Type = ReportFieldType.Money, Align = ReportAlign.End, WidthMm = 26,
                    VisibleByDefault = false,
                },
                new ReportDatasetField
                {
                    Key = "GrandTotal", TitleAr = "الإجمالي النهائي", TitleEn = "Grand total",
                    Type = ReportFieldType.Money, Align = ReportAlign.End, WidthMm = 28,
                    VisibleByDefault = false,
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

        public static ReportDatasetDefinition SalesInvoice() => Build(
            TradeDocumentDatasetCodes.SalesInvoice,
            "فاتورة مبيعات", "Sales invoice",
            "فاتورة مبيعات واحدة بكل بنودها: الصنف والكمية والسعر والخصم والضريبة، مع بيانات العميل والإجماليات.",
            "One sales invoice with every line: item, quantity, price, discount and tax, with the customer and the totals.",
            "InvoiceId", "رقم الفاتورة الداخلي", "Invoice id",
            "العميل", "Customer");

        public static ReportDatasetDefinition PurchaseInvoice() => Build(
            TradeDocumentDatasetCodes.PurchaseInvoice,
            "فاتورة مشتريات", "Purchase invoice",
            "فاتورة مشتريات واحدة بكل بنودها، مع بيانات المورّد والإجماليات.",
            "One purchase invoice with every line, with the vendor and the totals.",
            "InvoiceId", "رقم الفاتورة الداخلي", "Invoice id",
            "المورّد", "Vendor");

        public static ReportDatasetDefinition Quotation() => Build(
            TradeDocumentDatasetCodes.Quotation,
            "عرض سعر", "Quotation",
            "عرض سعر واحد بكل بنوده، مع بيانات العميل وتاريخ سريان العرض والإجماليات.",
            "One quotation with every line, with the customer, its validity and the totals.",
            "QuotationId", "رقم العرض الداخلي", "Quotation id",
            "العميل", "Customer");

        public static ReportDatasetDefinition PurchaseOrder() => Build(
            TradeDocumentDatasetCodes.PurchaseOrder,
            "أمر شراء", "Purchase order",
            "أمر شراء واحد بكل بنوده: الصنف والكمية والسعر، مع بيانات المورّد والإجماليات.",
            "One purchase order with every line: item, quantity and price, with the vendor and the totals.",
            "OrderId", "رقم الأمر الداخلي", "Order id",
            "المورّد", "Vendor");

        public static IEnumerable<ReportDatasetDefinition> All()
        {
            yield return SalesInvoice();
            yield return PurchaseInvoice();
            yield return Quotation();
            yield return PurchaseOrder();
        }
    }
}
