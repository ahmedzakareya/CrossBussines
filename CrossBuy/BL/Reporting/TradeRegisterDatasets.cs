namespace CrossBuy.BL.Reporting
{
    // ============================================================================================
    // THE REGISTERS — what a LIST screen prints.
    //
    // A document screen prints one document; a list prints the rows it is showing, under the filters
    // it is showing them under. Every trade list is the same shape — a number, a date, the other
    // party, a status and a total — so the definition is built once and each list supplies its own
    // names and its own source.
    //
    // The purchase-order register proved the shape and this generalises it rather than copying it four
    // more times. That matters here specifically: five registers whose columns drifted apart would be
    // five documents a company could not make look alike, which is the whole reason these are
    // templates and not markup.
    //
    // THE FILTERS ARE THE SCREEN'S, deliberately named to match what each list already sends its own
    // data endpoint. A printed register that filtered differently from the list it was printed from is
    // worse than no register: the reader has no way to see the disagreement and every reason to trust
    // the paper.
    // ============================================================================================
    public static class TradeRegisterCodes
    {
        public const string PurchaseOrders = "Purchasing.OrderRegister";
        public const string SalesOrders = "Sales.OrderRegister";
        public const string GoodsReceipts = "Purchasing.ReceiptRegister";
        public const string PurchaseInvoices = "Purchasing.InvoiceRegister";
        public const string SalesInvoices = "Sales.InvoiceRegister";
    }

    public static class TradeRegisterDatasets
    {
        // A register is a LIST, so its cap is much higher than a document's — and TruncateAndDeclare
        // still means one that hits the cap says so on its face rather than printing a quiet subset.
        public const int MaxRows = 5000;

        public static ReportDatasetDefinition Build(
            string datasetCode, string titleAr, string titleEn,
            string descriptionAr, string descriptionEn,
            string numberTitleAr, string numberTitleEn,
            string partyTitleAr, string partyTitleEn,
            IReadOnlyList<ReportParameterOption> statuses) => new()
        {
            DatasetCode = datasetCode,
            Module = "Trade",
            TitleAr = titleAr,
            TitleEn = titleEn,
            DescriptionAr = descriptionAr,
            DescriptionEn = descriptionEn,
            DataSourceKey = datasetCode,
            RequiredPermissionKey = AccountingReportPermissions.View,
            MaxRows = MaxRows,
            RowCapPolicy = ReportRowCapPolicy.TruncateAndDeclare,

            Fields = new[]
            {
                new ReportDatasetField
                {
                    Key = "DocumentNo", TitleAr = numberTitleAr, TitleEn = numberTitleEn, WidthMm = 34,
                },
                new ReportDatasetField
                {
                    Key = "DocumentDate", TitleAr = "التاريخ", TitleEn = "Date",
                    Type = ReportFieldType.Date, Format = "yyyy-MM-dd", Groupable = true, WidthMm = 26,
                },
                new ReportDatasetField
                {
                    Key = "PartyName", TitleAr = partyTitleAr, TitleEn = partyTitleEn,
                    Groupable = true, WidthMm = 60,
                },
                new ReportDatasetField
                {
                    Key = "Status", TitleAr = "الحالة", TitleEn = "Status",
                    Groupable = true, WidthMm = 24,
                    SupportedAggregates = new[] { ReportAggregate.Count },
                },

                // SUMMED, because the first question anyone asks a register is what it adds up to.
                new ReportDatasetField
                {
                    Key = "GrandTotal", TitleAr = "الإجمالي", TitleEn = "Total",
                    Type = ReportFieldType.Money, Align = ReportAlign.End, WidthMm = 30,
                    SupportedAggregates = new[] { ReportAggregate.Sum },
                },

                // ---- the ISSUER, for a design that heads the list with it ---------------------------
                // Hidden by default: these are the same value on every row, so they belong in a header
                // band, not in a column. A layout binds them; the ordinary grid never shows them.
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
                    Key = "BranchName", TitleAr = "الفرع", TitleEn = "Branch",
                    WidthMm = 40, VisibleByDefault = false,
                },
            },

            Parameters = new[]
            {
                new ReportParameterDescriptor
                {
                    Key = "Search", TitleAr = "بحث", TitleEn = "Search",
                    HelpTextAr = "رقم المستند أو اسم الطرف الآخر.",
                    HelpTextEn = "Document number or the other party's name.",
                },
                new ReportParameterDescriptor
                {
                    Key = "Status", TitleAr = "الحالة", TitleEn = "Status",
                    Options = statuses,
                },
                AccountingDatasets.CompanyParam(),
            },
        };

        // The statuses each family actually uses. Written out per family rather than shared, because a
        // status list is a LIFECYCLE and an invoice's is not an order's — offering "Received" on an
        // invoice filter would be a promise the data cannot keep.
        private static ReportParameterOption Opt(string value, string ar, string en) =>
            new() { Value = value, LabelAr = ar, LabelEn = en };

        private static readonly ReportParameterOption[] OrderStatuses =
        {
            Opt("Draft", "مسودة", "Draft"), Opt("Approved", "معتمد", "Approved"),
            Opt("Received", "مُستلَم", "Received"), Opt("Closed", "مُقفل", "Closed"),
            Opt("Cancelled", "ملغى", "Cancelled"),
        };

        private static readonly ReportParameterOption[] SalesOrderStatuses =
        {
            Opt("Draft", "مسودة", "Draft"), Opt("Approved", "معتمد", "Approved"),
            Opt("Delivered", "مُسلَّم", "Delivered"), Opt("Closed", "مُقفل", "Closed"),
            Opt("Cancelled", "ملغى", "Cancelled"),
        };

        private static readonly ReportParameterOption[] InvoiceStatuses =
        {
            Opt("Draft", "مسودة", "Draft"), Opt("Posted", "مُرحَّل", "Posted"),
            Opt("Cancelled", "ملغى", "Cancelled"),
        };

        private static readonly ReportParameterOption[] ReceiptStatuses =
        {
            Opt("Draft", "مسودة", "Draft"), Opt("Posted", "مُرحَّل", "Posted"),
        };

        public static ReportDatasetDefinition PurchaseOrders() => Build(
            TradeRegisterCodes.PurchaseOrders,
            "سجل أوامر الشراء", "Purchase order register",
            "أوامر الشراء بالحالة والمورّد والإجمالي — بنفس فلاتر الشاشة.",
            "Purchase orders with status, vendor and total — under the screen's own filters.",
            "رقم الأمر", "Order no.", "المورّد", "Vendor", OrderStatuses);

        public static ReportDatasetDefinition SalesOrders() => Build(
            TradeRegisterCodes.SalesOrders,
            "سجل أوامر البيع", "Sales order register",
            "أوامر البيع بالحالة والعميل والإجمالي — بنفس فلاتر الشاشة.",
            "Sales orders with status, customer and total — under the screen's own filters.",
            "رقم الأمر", "Order no.", "العميل", "Customer", SalesOrderStatuses);

        public static ReportDatasetDefinition GoodsReceipts() => Build(
            TradeRegisterCodes.GoodsReceipts,
            "سجل أذون الاستلام", "Goods receipt register",
            "أذون الاستلام بالحالة والمورّد والإجمالي — بنفس فلاتر الشاشة.",
            "Goods receipts with status, vendor and total — under the screen's own filters.",
            "رقم الإذن", "Receipt no.", "المورّد", "Vendor", ReceiptStatuses);

        public static ReportDatasetDefinition PurchaseInvoices() => Build(
            TradeRegisterCodes.PurchaseInvoices,
            "سجل فواتير المشتريات", "Purchase invoice register",
            "فواتير المشتريات بالحالة والمورّد والإجمالي — بنفس فلاتر الشاشة.",
            "Purchase invoices with status, vendor and total — under the screen's own filters.",
            "رقم الفاتورة", "Invoice no.", "المورّد", "Vendor", InvoiceStatuses);

        public static ReportDatasetDefinition SalesInvoices() => Build(
            TradeRegisterCodes.SalesInvoices,
            "سجل فواتير المبيعات", "Sales invoice register",
            "فواتير المبيعات بالحالة والعميل والإجمالي — بنفس فلاتر الشاشة.",
            "Sales invoices with status, customer and total — under the screen's own filters.",
            "رقم الفاتورة", "Invoice no.", "العميل", "Customer", InvoiceStatuses);

        public static IEnumerable<ReportDatasetDefinition> All()
        {
            yield return PurchaseOrders();
            yield return SalesOrders();
            yield return GoodsReceipts();
            yield return PurchaseInvoices();
            yield return SalesInvoices();
        }
    }
}
