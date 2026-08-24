using CrossBuy.Models.Context;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL.Reporting
{
    // ============================================================================================
    // Reporting Platform R2 — THE ACCOUNTING DATASETS.
    //
    // R1 shipped ONE dataset, over the platform's own event log, precisely so activation coupled Reporting to
    // no module's schema. R2 is where that changes on purpose: Report Studio is worth nothing over a single
    // dataset, so the module datasets are the prerequisite, not a follow-up.
    //
    // ────────────────────────────────────────────────────────────────────────────────────────────
    // THE RULE THAT SHAPES EVERY SOURCE BELOW: REPORTING READS, IT DOES NOT COMPUTE.
    //
    // A reporting platform that re-derives a business number becomes a second, competing source of truth, and
    // the day the two disagree is the day nobody trusts either. So:
    //
    //   · A STORED value is read and projected. SalesInvoice.GrandTotal, JournalEntry.Status — these are facts
    //     Accounting already wrote, and reading them is free of judgement.
    //   · A CALCULATED value is DELEGATED to the service that owns the calculation. Receivables aging is FIFO
    //     receipt application against posted invoices — a real accounting rule with a real opinion about
    //     multi-currency. Reporting does not hold that opinion; IReceivableService.AgingAsync does, so the
    //     aging dataset CALLS it. Same for customer profitability (Revenue/Cogs/Margin), which is
    //     GetCustomerAnalyticsAsync's answer, not ours.
    //   · Anything the module does not already answer is NOT invented here. It is left out and named, so the
    //     gap is visible rather than filled with something plausible.
    //
    // AGGREGATION IS NOT CALCULATION. Grouping a register by customer and summing a stored GrandTotal is the
    // shaper doing arithmetic over facts. That is allowed and is how "revenue by period" is produced without
    // Reporting owning a revenue rule.
    //
    // ────────────────────────────────────────────────────────────────────────────────────────────
    // COMPANY ISOLATION. Every source filters on `query.Context.CompanyId` — a value that comes from the
    // resolved BusinessContext and from nowhere else. There is no companyId parameter on any dataset, no
    // request-supplied tenant, and no default: an unresolved company returns an EMPTY set, never company 1.
    // The delegated services take companyId as an argument, and they are handed the context's value for the
    // same reason.
    //
    // AUTHORIZATION. One key per module, resolved by the SAME IReportPermissionEvaluator the platform already
    // uses, and fail-closed: unmapped means denied. This is not a second permission model — it is the module's
    // own key travelling through the single seam ReportAuthorizationService already consults.
    // ============================================================================================
    public static class AccountingReportPermissions
    {
        // The module's reporting key. Mapped in Program.cs; unmapped means denied, for everyone.
        public const string View = "accounting.reports.view";

        // Margin, COGS and revenue-net-of-tax are commercially sensitive in a way an invoice total is not: a
        // salesperson may legitimately see what a customer was billed and must not necessarily see what the
        // company earned on it. Separate key, separate grant, same mechanism the Business Event log uses for
        // its Payload column.
        public const string Profitability = "accounting.reports.profitability";
    }

    public static class AccountingDatasetCodes
    {
        public const string SalesRevenue = "Accounting.SalesRevenue";
        public const string Purchases = "Accounting.Purchases";
        public const string CustomerAging = "Accounting.CustomerAging";
        public const string CustomerProfitability = "Accounting.CustomerProfitability";
        public const string JournalActivity = "Accounting.JournalActivity";

        public const string CategoryKey = "accounting.reporting";
    }

    // ============================================================================================
    // DATASET DEFINITIONS
    // ============================================================================================
    public static class AccountingDatasets
    {
        // 50 000. An invoice register for a busy year, and low enough that an unfiltered request does not try to
        // materialise the whole ledger. TruncateAndDeclare rather than fail: a partial register that SAYS it is
        // partial is useful; refusing outright makes the tool unusable in December.
        public const int MaxRows = 50_000;

        // Aging and profitability are one row PER CUSTOMER, not per document, so the same ceiling would be
        // absurd. A company with 5 000 customers is a large company.
        public const int PartyMaxRows = 10_000;

        private static ReportParameterDescriptor FromDate() => new()
        {
            Key = "From", TitleAr = "من تاريخ", TitleEn = "From",
            Type = ReportFieldType.Date, Required = true, DefaultValue = "month-start",
            HelpTextAr = "بداية الفترة (شاملة).", HelpTextEn = "Start of the period (inclusive).",
        };

        private static ReportParameterDescriptor ToDate() => new()
        {
            Key = "To", TitleAr = "إلى تاريخ", TitleEn = "To",
            Type = ReportFieldType.Date, Required = true, DefaultValue = "today",
            HelpTextAr = "نهاية الفترة (شاملة).", HelpTextEn = "End of the period (inclusive).",
        };

        // SystemSupplied: the binder fills it from the resolved BusinessContext and DISCARDS anything the caller
        // sends. Declared on every dataset so the isolation guarantee is visible as metadata, not just as code.
        private static ReportParameterDescriptor CompanyParam() => new()
        {
            Key = ReportSystemParameters.CompanyId, TitleAr = "الشركة", TitleEn = "Company",
            Type = ReportFieldType.Integer, SystemSupplied = true,
        };

        // ---- Accounting.SalesRevenue -------------------------------------------------------------
        public static ReportDatasetDefinition SalesRevenue() => new()
        {
            DatasetCode = AccountingDatasetCodes.SalesRevenue,
            Module = "Accounting",
            TitleAr = "المبيعات والإيراد",
            TitleEn = "Sales and revenue",
            DescriptionAr = "سجل فواتير البيع: القيمة والضريبة والإجمالي لكل فاتورة، مع العميل والحالة.",
            DescriptionEn = "The sales invoice register: net, tax and total per invoice, with customer and status. "
                          + "Group by customer or period to get revenue totals.",
            DataSourceKey = AccountingDatasetCodes.SalesRevenue,
            RequiredPermissionKey = AccountingReportPermissions.View,
            MaxRows = MaxRows,
            RowCapPolicy = ReportRowCapPolicy.TruncateAndDeclare,
            AvailableInStudio = true,
            AvailableAsWidget = true,

            Fields = new[]
            {
                new ReportDatasetField
                {
                    Key = "InvoiceDate", TitleAr = "التاريخ", TitleEn = "Date",
                    Type = ReportFieldType.Date, Format = "yyyy-MM-dd", Groupable = true, WidthMm = 26,
                    SupportedAggregates = new[] { ReportAggregate.Min, ReportAggregate.Max, ReportAggregate.Count },
                },
                new ReportDatasetField
                {
                    Key = "InvoiceNo", TitleAr = "رقم الفاتورة", TitleEn = "Invoice no.", WidthMm = 32,
                },
                new ReportDatasetField
                {
                    Key = "CustomerName", TitleAr = "العميل", TitleEn = "Customer",
                    Groupable = true, WidthMm = 52,
                    SupportedAggregates = new[] { ReportAggregate.CountDistinct },
                },
                new ReportDatasetField
                {
                    Key = "CustomerId", TitleAr = "رقم العميل", TitleEn = "Customer id",
                    Type = ReportFieldType.Integer, VisibleByDefault = false, WidthMm = 20,
                },
                new ReportDatasetField
                {
                    Key = "Status", TitleAr = "الحالة", TitleEn = "Status", Groupable = true, WidthMm = 22,
                    SupportedAggregates = new[] { ReportAggregate.Count },
                },
                new ReportDatasetField
                {
                    Key = "SubTotal", TitleAr = "الصافي", TitleEn = "Net",
                    Type = ReportFieldType.Money, Align = ReportAlign.End, WidthMm = 28,
                    SupportedAggregates = new[] { ReportAggregate.Sum, ReportAggregate.Average },
                },
                new ReportDatasetField
                {
                    Key = "TaxTotal", TitleAr = "الضريبة", TitleEn = "Tax",
                    Type = ReportFieldType.Money, Align = ReportAlign.End, WidthMm = 26,
                    SupportedAggregates = new[] { ReportAggregate.Sum },
                },
                new ReportDatasetField
                {
                    Key = "GrandTotal", TitleAr = "الإجمالي", TitleEn = "Total",
                    Type = ReportFieldType.Money, Align = ReportAlign.End, WidthMm = 30,
                    SupportedAggregates = new[] { ReportAggregate.Sum, ReportAggregate.Average },
                },
                new ReportDatasetField
                {
                    // BASE currency total, hidden by default. Present because a multi-currency company cannot
                    // add GrandTotal across currencies and get a meaningful number, and the ledger's own
                    // functional-currency figure is the one that reconciles.
                    Key = "GrandTotalBase", TitleAr = "الإجمالي (عملة الأساس)", TitleEn = "Total (base)",
                    Type = ReportFieldType.Money, Align = ReportAlign.End, VisibleByDefault = false, WidthMm = 30,
                    SupportedAggregates = new[] { ReportAggregate.Sum },
                },
                new ReportDatasetField
                {
                    Key = "ProjectId", TitleAr = "المشروع", TitleEn = "Project",
                    Type = ReportFieldType.Integer, Groupable = true, VisibleByDefault = false, WidthMm = 20,
                },
                new ReportDatasetField
                {
                    Key = "JournalEntryId", TitleAr = "قيد اليومية", TitleEn = "Journal entry",
                    Type = ReportFieldType.Integer, VisibleByDefault = false, WidthMm = 22,
                },
            },

            Parameters = new[]
            {
                FromDate(), ToDate(),
                new ReportParameterDescriptor
                {
                    Key = "Status", TitleAr = "الحالة", TitleEn = "Status",
                    Options = new[]
                    {
                        new ReportParameterOption { Value = "Posted", LabelAr = "مُرحَّلة", LabelEn = "Posted" },
                        new ReportParameterOption { Value = "Draft", LabelAr = "مسودة", LabelEn = "Draft" },
                        new ReportParameterOption { Value = "Cancelled", LabelAr = "ملغاة", LabelEn = "Cancelled" },
                    },
                    HelpTextAr = "اتركه فارغًا لكل الحالات.", HelpTextEn = "Leave empty for every status.",
                },
                new ReportParameterDescriptor
                {
                    Key = "CustomerId", TitleAr = "العميل", TitleEn = "Customer",
                    Type = ReportFieldType.Integer,
                    HelpTextAr = "لعرض عميل واحد.", HelpTextEn = "To see one customer.",
                },
                CompanyParam(),
            },

            DrillDowns = new[]
            {
                new ReportDatasetDrillDown
                {
                    Key = "by-customer", TitleAr = "حسب العميل", TitleEn = "By customer",
                    Levels = new[] { "CustomerName", "Status", "InvoiceDate" },
                },
            },
        };

        // ---- Accounting.Purchases ----------------------------------------------------------------
        public static ReportDatasetDefinition Purchases() => new()
        {
            DatasetCode = AccountingDatasetCodes.Purchases,
            Module = "Accounting",
            TitleAr = "المشتريات",
            TitleEn = "Purchases",
            DescriptionAr = "سجل فواتير الشراء: القيمة والضريبة والإجمالي لكل فاتورة، مع المورّد والحالة.",
            DescriptionEn = "The purchase invoice register: net, tax and total per invoice, with vendor and status.",
            DataSourceKey = AccountingDatasetCodes.Purchases,
            RequiredPermissionKey = AccountingReportPermissions.View,
            MaxRows = MaxRows,
            RowCapPolicy = ReportRowCapPolicy.TruncateAndDeclare,

            Fields = new[]
            {
                new ReportDatasetField
                {
                    Key = "InvoiceDate", TitleAr = "التاريخ", TitleEn = "Date",
                    Type = ReportFieldType.Date, Format = "yyyy-MM-dd", Groupable = true, WidthMm = 26,
                    SupportedAggregates = new[] { ReportAggregate.Min, ReportAggregate.Max, ReportAggregate.Count },
                },
                new ReportDatasetField
                {
                    Key = "InvoiceNo", TitleAr = "رقم الفاتورة", TitleEn = "Invoice no.", WidthMm = 32,
                },
                new ReportDatasetField
                {
                    Key = "VendorName", TitleAr = "المورّد", TitleEn = "Vendor",
                    Groupable = true, WidthMm = 52,
                    SupportedAggregates = new[] { ReportAggregate.CountDistinct },
                },
                new ReportDatasetField
                {
                    Key = "VendorId", TitleAr = "رقم المورّد", TitleEn = "Vendor id",
                    Type = ReportFieldType.Integer, VisibleByDefault = false, WidthMm = 20,
                },
                new ReportDatasetField
                {
                    Key = "Status", TitleAr = "الحالة", TitleEn = "Status", Groupable = true, WidthMm = 22,
                    SupportedAggregates = new[] { ReportAggregate.Count },
                },
                new ReportDatasetField
                {
                    Key = "SubTotal", TitleAr = "الصافي", TitleEn = "Net",
                    Type = ReportFieldType.Money, Align = ReportAlign.End, WidthMm = 28,
                    SupportedAggregates = new[] { ReportAggregate.Sum, ReportAggregate.Average },
                },
                new ReportDatasetField
                {
                    Key = "TaxTotal", TitleAr = "الضريبة", TitleEn = "Tax",
                    Type = ReportFieldType.Money, Align = ReportAlign.End, WidthMm = 26,
                    SupportedAggregates = new[] { ReportAggregate.Sum },
                },
                new ReportDatasetField
                {
                    Key = "GrandTotal", TitleAr = "الإجمالي", TitleEn = "Total",
                    Type = ReportFieldType.Money, Align = ReportAlign.End, WidthMm = 30,
                    SupportedAggregates = new[] { ReportAggregate.Sum, ReportAggregate.Average },
                },
                new ReportDatasetField
                {
                    Key = "GrandTotalBase", TitleAr = "الإجمالي (عملة الأساس)", TitleEn = "Total (base)",
                    Type = ReportFieldType.Money, Align = ReportAlign.End, VisibleByDefault = false, WidthMm = 30,
                    SupportedAggregates = new[] { ReportAggregate.Sum },
                },
                new ReportDatasetField
                {
                    Key = "ProjectId", TitleAr = "المشروع", TitleEn = "Project",
                    Type = ReportFieldType.Integer, Groupable = true, VisibleByDefault = false, WidthMm = 20,
                },
            },

            Parameters = new[]
            {
                FromDate(), ToDate(),
                new ReportParameterDescriptor
                {
                    Key = "Status", TitleAr = "الحالة", TitleEn = "Status",
                    Options = new[]
                    {
                        new ReportParameterOption { Value = "Posted", LabelAr = "مُرحَّلة", LabelEn = "Posted" },
                        new ReportParameterOption { Value = "Draft", LabelAr = "مسودة", LabelEn = "Draft" },
                        new ReportParameterOption { Value = "Cancelled", LabelAr = "ملغاة", LabelEn = "Cancelled" },
                    },
                },
                new ReportParameterDescriptor
                {
                    Key = "VendorId", TitleAr = "المورّد", TitleEn = "Vendor", Type = ReportFieldType.Integer,
                },
                CompanyParam(),
            },

            DrillDowns = new[]
            {
                new ReportDatasetDrillDown
                {
                    Key = "by-vendor", TitleAr = "حسب المورّد", TitleEn = "By vendor",
                    Levels = new[] { "VendorName", "Status", "InvoiceDate" },
                },
            },
        };

        // ---- Accounting.CustomerAging ------------------------------------------------------------
        //
        // DELEGATED, not reimplemented. The buckets come from IReceivableService.AgingAsync, which applies
        // receipts FIFO against posted invoices and ages in the functional currency. Reporting reproducing that
        // would create a second aging with a different opinion about partial settlement.
        //
        // Consequences the metadata has to be honest about: the source produces one row per customer already
        // aged AS OF a date, so nothing here is filterable or sortable server-side and the row cap is a party
        // cap, not a document cap.
        public static ReportDatasetDefinition CustomerAging() => new()
        {
            DatasetCode = AccountingDatasetCodes.CustomerAging,
            Module = "Accounting",
            TitleAr = "أعمار أرصدة العملاء",
            TitleEn = "Customer receivables aging",
            DescriptionAr = "الأرصدة المستحقة على العملاء موزّعة على فترات العُمر، بحسب احتساب وحدة المحاسبة.",
            DescriptionEn = "Outstanding customer balances split into age buckets, as calculated by the Accounting "
                          + "module (receipts applied oldest-first, aged in the functional currency).",
            DataSourceKey = AccountingDatasetCodes.CustomerAging,
            RequiredPermissionKey = AccountingReportPermissions.View,
            MaxRows = PartyMaxRows,
            RowCapPolicy = ReportRowCapPolicy.TruncateAndDeclare,

            Fields = new[]
            {
                new ReportDatasetField
                {
                    Key = "CustomerName", TitleAr = "العميل", TitleEn = "Customer",
                    Groupable = true, WidthMm = 60,
                    SupportedAggregates = new[] { ReportAggregate.CountDistinct },
                },
                new ReportDatasetField
                {
                    Key = "CustomerId", TitleAr = "رقم العميل", TitleEn = "Customer id",
                    Type = ReportFieldType.Integer, VisibleByDefault = false, WidthMm = 20,
                },
                new ReportDatasetField
                {
                    Key = "Current", TitleAr = "جاري (٠-٣٠)", TitleEn = "Current (0-30)",
                    Type = ReportFieldType.Money, Align = ReportAlign.End, WidthMm = 28,
                    SupportedAggregates = new[] { ReportAggregate.Sum },
                },
                new ReportDatasetField
                {
                    Key = "Days30", TitleAr = "٣١-٦٠", TitleEn = "31-60",
                    Type = ReportFieldType.Money, Align = ReportAlign.End, WidthMm = 26,
                    SupportedAggregates = new[] { ReportAggregate.Sum },
                },
                new ReportDatasetField
                {
                    Key = "Days60", TitleAr = "٦١-٩٠", TitleEn = "61-90",
                    Type = ReportFieldType.Money, Align = ReportAlign.End, WidthMm = 26,
                    SupportedAggregates = new[] { ReportAggregate.Sum },
                },
                new ReportDatasetField
                {
                    Key = "Days90Plus", TitleAr = "أكثر من ٩٠", TitleEn = "90+",
                    Type = ReportFieldType.Money, Align = ReportAlign.End, WidthMm = 26,
                    SupportedAggregates = new[] { ReportAggregate.Sum },
                },
                new ReportDatasetField
                {
                    Key = "Total", TitleAr = "الإجمالي", TitleEn = "Total",
                    Type = ReportFieldType.Money, Align = ReportAlign.End, WidthMm = 30,
                    SupportedAggregates = new[] { ReportAggregate.Sum, ReportAggregate.Average },
                },
            },

            Parameters = new[]
            {
                new ReportParameterDescriptor
                {
                    // AS OF, not a range: aging is a snapshot question ("what is owed to me today"), and a
                    // from/to range over it would be meaningless.
                    Key = "AsOf", TitleAr = "حتى تاريخ", TitleEn = "As of",
                    Type = ReportFieldType.Date, Required = true, DefaultValue = "today",
                    HelpTextAr = "تُحسب الأعمار حتى هذا التاريخ.",
                    HelpTextEn = "Ages are calculated as of this date.",
                },
                CompanyParam(),
            },
        };

        // ---- Accounting.CustomerProfitability ----------------------------------------------------
        //
        // Also delegated: Revenue / Cogs / Margin / MarginPct are GetCustomerAnalyticsAsync's answer. The margin
        // columns are gated on their own key — see AccountingReportPermissions.Profitability.
        public static ReportDatasetDefinition CustomerProfitability() => new()
        {
            DatasetCode = AccountingDatasetCodes.CustomerProfitability,
            Module = "Accounting",
            TitleAr = "ربحية العملاء",
            TitleEn = "Customer profitability",
            DescriptionAr = "لكل عميل: المفوتر والمحصّل والمرتجع والمستحق، والإيراد والتكلفة والهامش لمن يملك الصلاحية.",
            DescriptionEn = "Per customer: invoiced, received, returns and outstanding — plus revenue, cost and "
                          + "margin for callers who hold the profitability right.",
            DataSourceKey = AccountingDatasetCodes.CustomerProfitability,
            RequiredPermissionKey = AccountingReportPermissions.View,
            MaxRows = PartyMaxRows,
            RowCapPolicy = ReportRowCapPolicy.TruncateAndDeclare,

            // A CSV of this dataset carries margin. Exporting it therefore needs the export right as well as the
            // field right — the same policy the Business Event log applies to its payload.
            ExportPolicy = ReportDatasetExportPolicy.RequirePermissionForDataExport,

            Fields = new[]
            {
                new ReportDatasetField
                {
                    Key = "CustomerName", TitleAr = "العميل", TitleEn = "Customer",
                    Groupable = true, WidthMm = 54,
                    SupportedAggregates = new[] { ReportAggregate.CountDistinct },
                },
                new ReportDatasetField
                {
                    Key = "Segment", TitleAr = "التصنيف", TitleEn = "Segment",
                    Groupable = true, WidthMm = 24, SupportedAggregates = new[] { ReportAggregate.Count },
                },
                new ReportDatasetField
                {
                    Key = "InvoiceCount", TitleAr = "عدد الفواتير", TitleEn = "Invoices",
                    Type = ReportFieldType.Integer, Align = ReportAlign.End, WidthMm = 20,
                    SupportedAggregates = new[] { ReportAggregate.Sum, ReportAggregate.Average },
                },
                new ReportDatasetField
                {
                    Key = "Invoiced", TitleAr = "المفوتر", TitleEn = "Invoiced",
                    Type = ReportFieldType.Money, Align = ReportAlign.End, WidthMm = 28,
                    SupportedAggregates = new[] { ReportAggregate.Sum },
                },
                new ReportDatasetField
                {
                    Key = "Received", TitleAr = "المحصّل", TitleEn = "Received",
                    Type = ReportFieldType.Money, Align = ReportAlign.End, WidthMm = 28,
                    SupportedAggregates = new[] { ReportAggregate.Sum },
                },
                new ReportDatasetField
                {
                    Key = "Outstanding", TitleAr = "المستحق", TitleEn = "Outstanding",
                    Type = ReportFieldType.Money, Align = ReportAlign.End, WidthMm = 28,
                    SupportedAggregates = new[] { ReportAggregate.Sum },
                },
                new ReportDatasetField
                {
                    Key = "Revenue", TitleAr = "الإيراد", TitleEn = "Revenue",
                    Type = ReportFieldType.Money, Align = ReportAlign.End, WidthMm = 28,
                    VisibleByDefault = false,
                    Sensitivity = ReportFieldSensitivity.Confidential,
                    RequiredPermissionKey = AccountingReportPermissions.Profitability,
                    SupportedAggregates = new[] { ReportAggregate.Sum },
                },
                new ReportDatasetField
                {
                    Key = "Cogs", TitleAr = "تكلفة المبيعات", TitleEn = "COGS",
                    Type = ReportFieldType.Money, Align = ReportAlign.End, WidthMm = 28,
                    VisibleByDefault = false,
                    Sensitivity = ReportFieldSensitivity.Confidential,
                    RequiredPermissionKey = AccountingReportPermissions.Profitability,
                    SupportedAggregates = new[] { ReportAggregate.Sum },
                },
                new ReportDatasetField
                {
                    Key = "Margin", TitleAr = "الهامش", TitleEn = "Margin",
                    Type = ReportFieldType.Money, Align = ReportAlign.End, WidthMm = 28,
                    VisibleByDefault = false,
                    Sensitivity = ReportFieldSensitivity.Confidential,
                    RequiredPermissionKey = AccountingReportPermissions.Profitability,
                    SupportedAggregates = new[] { ReportAggregate.Sum },
                },
                new ReportDatasetField
                {
                    Key = "MarginPct", TitleAr = "نسبة الهامش", TitleEn = "Margin %",
                    Type = ReportFieldType.Decimal, Align = ReportAlign.End, WidthMm = 22,
                    VisibleByDefault = false,
                    Sensitivity = ReportFieldSensitivity.Confidential,
                    RequiredPermissionKey = AccountingReportPermissions.Profitability,
                    // NOT summed. Adding percentages is arithmetic nonsense; averaging them is at least a
                    // defensible summary, so that is the only aggregate offered.
                    SupportedAggregates = new[] { ReportAggregate.Average },
                },
                new ReportDatasetField
                {
                    Key = "LastInvoice", TitleAr = "آخر فاتورة", TitleEn = "Last invoice",
                    Type = ReportFieldType.Date, Format = "yyyy-MM-dd", VisibleByDefault = false, WidthMm = 26,
                    SupportedAggregates = new[] { ReportAggregate.Max },
                },
            },

            Parameters = new[] { CompanyParam() },
        };

        // ---- Accounting.JournalActivity ----------------------------------------------------------
        public static ReportDatasetDefinition JournalActivity() => new()
        {
            DatasetCode = AccountingDatasetCodes.JournalActivity,
            Module = "Accounting",
            TitleAr = "حركة قيود اليومية",
            TitleEn = "Journal activity",
            DescriptionAr = "القيود المسجّلة: النوع والمصدر وحالة الترحيل ومَن رحّلها ومتى.",
            DescriptionEn = "Recorded journal entries: type, source, posting status, and who posted them when.",
            DataSourceKey = AccountingDatasetCodes.JournalActivity,
            RequiredPermissionKey = AccountingReportPermissions.View,
            MaxRows = MaxRows,
            RowCapPolicy = ReportRowCapPolicy.TruncateAndDeclare,

            Fields = new[]
            {
                new ReportDatasetField
                {
                    Key = "EntryDate", TitleAr = "تاريخ القيد", TitleEn = "Entry date",
                    Type = ReportFieldType.Date, Format = "yyyy-MM-dd", Groupable = true, WidthMm = 26,
                    SupportedAggregates = new[] { ReportAggregate.Min, ReportAggregate.Max, ReportAggregate.Count },
                },
                new ReportDatasetField
                {
                    Key = "EntryNo", TitleAr = "رقم القيد", TitleEn = "Entry no.", WidthMm = 34,
                },
                new ReportDatasetField
                {
                    Key = "JournalType", TitleAr = "النوع", TitleEn = "Type",
                    Groupable = true, WidthMm = 24, SupportedAggregates = new[] { ReportAggregate.Count },
                },
                new ReportDatasetField
                {
                    Key = "SourceType", TitleAr = "المصدر", TitleEn = "Source",
                    Groupable = true, WidthMm = 28, SupportedAggregates = new[] { ReportAggregate.CountDistinct },
                },
                new ReportDatasetField
                {
                    Key = "Status", TitleAr = "الحالة", TitleEn = "Status",
                    Groupable = true, WidthMm = 22, SupportedAggregates = new[] { ReportAggregate.Count },
                },
                new ReportDatasetField
                {
                    Key = "PostedAt", TitleAr = "تاريخ الترحيل", TitleEn = "Posted at",
                    Type = ReportFieldType.DateTime, Format = "yyyy-MM-dd HH:mm", WidthMm = 32,
                    SupportedAggregates = new[] { ReportAggregate.Max },
                },
                new ReportDatasetField
                {
                    Key = "Description", TitleAr = "البيان", TitleEn = "Description",
                    WidthMm = 70, Sortable = false,
                },
                new ReportDatasetField
                {
                    // A reversal points at the entry that reversed it. Hidden by default, but it is the field an
                    // auditor asks for the moment a posted entry looks wrong.
                    Key = "ReversedByEntryId", TitleAr = "عُكس بالقيد", TitleEn = "Reversed by",
                    Type = ReportFieldType.Integer, VisibleByDefault = false, WidthMm = 22,
                },
                new ReportDatasetField
                {
                    Key = "FiscalPeriodId", TitleAr = "الفترة المالية", TitleEn = "Fiscal period",
                    Type = ReportFieldType.Integer, Groupable = true, VisibleByDefault = false, WidthMm = 22,
                },
            },

            Parameters = new[]
            {
                FromDate(), ToDate(),
                new ReportParameterDescriptor
                {
                    Key = "Status", TitleAr = "الحالة", TitleEn = "Status",
                    Options = new[]
                    {
                        new ReportParameterOption { Value = "Posted", LabelAr = "مُرحَّل", LabelEn = "Posted" },
                        new ReportParameterOption { Value = "Draft", LabelAr = "مسودة", LabelEn = "Draft" },
                    },
                },
                new ReportParameterDescriptor
                {
                    Key = "JournalType", TitleAr = "النوع", TitleEn = "Type", AllowMultiple = true,
                    HelpTextAr = "مثال: Manual أو Auto.", HelpTextEn = "e.g. Manual or Auto.",
                },
                CompanyParam(),
            },

            DrillDowns = new[]
            {
                new ReportDatasetDrillDown
                {
                    Key = "by-source", TitleAr = "حسب المصدر", TitleEn = "By source",
                    Levels = new[] { "SourceType", "JournalType", "Status" },
                },
            },
        };

        public static IEnumerable<ReportDatasetDefinition> All()
        {
            yield return SalesRevenue();
            yield return Purchases();
            yield return CustomerAging();
            yield return CustomerProfitability();
            yield return JournalActivity();
        }
    }

    // ============================================================================================
    // REPORT DEFINITIONS — one per dataset, projected FROM the dataset.
    //
    // Columns and parameters are taken from the dataset via ToColumn(), so the two can never disagree about a
    // field's type, format, width or sensitivity. That projection is the whole reason ToColumn exists.
    // ============================================================================================
    public sealed class AccountingReportDefinitionProvider : IReportDefinitionProvider
    {
        public string ProviderName => "Accounting";

        public IEnumerable<ReportDefinition> GetDefinitions()
        {
            yield return Build(AccountingDatasets.SalesRevenue(), "ki-outline ki-chart-line-up", "success", 100,
                ReportSort.By("InvoiceDate", descending: true));
            yield return Build(AccountingDatasets.Purchases(), "ki-outline ki-handcart", "warning", 110,
                ReportSort.By("InvoiceDate", descending: true));
            yield return Build(AccountingDatasets.CustomerAging(), "ki-outline ki-time", "danger", 120,
                ReportSort.By("Total", descending: true));
            yield return Build(AccountingDatasets.CustomerProfitability(), "ki-outline ki-dollar", "primary", 130,
                ReportSort.By("Invoiced", descending: true));
            yield return Build(AccountingDatasets.JournalActivity(), "ki-outline ki-notepad", "info", 140,
                ReportSort.By("EntryDate", descending: true));
        }

        internal static ReportDefinition Build(ReportDatasetDefinition dataset, string icon, string color,
            int sortOrder, params ReportSort[] defaultSorts) => new()
        {
            Code = dataset.DatasetCode,
            Module = dataset.Module,
            TitleAr = dataset.TitleAr,
            TitleEn = dataset.TitleEn,
            DescriptionAr = dataset.DescriptionAr,
            DescriptionEn = dataset.DescriptionEn,
            DataSourceKey = dataset.DataSourceKey,
            PermissionKey = dataset.RequiredPermissionKey,
            CategoryKey = AccountingDatasetCodes.CategoryKey,
            Tags = new[] { "accounting", "finance" },
            Columns = dataset.Fields.Select(f => f.ToColumn()).ToList(),
            Parameters = dataset.Parameters,
            DefaultSorts = defaultSorts,
            DefaultFormat = ReportOutputFormat.Html,
            Capabilities = new ReportCapabilities
            {
                // No PDF: the converter is unbound in this deployment, and a capability list promising a format
                // nobody can produce is a lie the UI repeats.
                Formats = new[]
                {
                    ReportOutputFormat.Html, ReportOutputFormat.PrintHtml,
                    ReportOutputFormat.Csv, ReportOutputFormat.Xlsx,
                },
                MaxRows = dataset.MaxRows,
                PreviewRows = 100,
                AllowSchedule = true,
                AllowArchive = true,
                AllowShare = true,
            },
            Icon = icon,
            Color = color,
            SortOrder = sortOrder,
        };
    }

    // ============================================================================================
    // DATA SOURCES
    //
    // Every one of them: AsNoTracking, company filtered from the CONTEXT, +1 over the cap so truncation is
    // detected rather than guessed, and no write of any kind.
    // ============================================================================================
    internal static class AccountingSourceHelpers
    {
        // The inclusive upper bound, expressed once. A user asking for "1–31 January" means the 31st, and a
        // half-open range silently drops the last day — the bug this helper exists to prevent repeating.
        internal static DateTime ExclusiveEnd(DateTime? to) =>
            to.HasValue ? to.Value.Date.AddDays(1) : DateTime.MaxValue;

        internal static bool Arabic(ReportDataQuery query) =>
            query.Culture.TwoLetterISOLanguageName == "ar";

        internal static string Pick(bool arabic, string? ar, string? en) =>
            arabic ? (ar ?? en ?? "") : (en ?? ar ?? "");
    }

    public sealed class SalesRevenueDataSource : IReportDataSource
    {
        private readonly CrossDbContext _db;
        public SalesRevenueDataSource(CrossDbContext db) { _db = db; }

        public string Key => AccountingDatasetCodes.SalesRevenue;

        public async Task<ReportDataSet> FetchAsync(ReportDataQuery query, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(query);
            var context = query.Context;
            var columns = query.RequestedColumns.Count > 0 ? query.RequestedColumns : query.Definition.Columns;
            var builder = new ReportDataSetBuilder(columns);

            // FAIL CLOSED. An unresolved company reads no company-scoped data and never defaults to company 1.
            if (context.CompanyId <= 0) return builder.Build(totalRowCount: 0);

            var from = query.Parameters.GetDate("From");
            var to = query.Parameters.GetDate("To");
            var status = query.Parameters.GetString("Status");
            var customerId = query.Parameters.GetInt("CustomerId");

            var rows = _db.SalesInvoices.AsNoTracking()
                .Where(i => i.CompanyID == context.CompanyId);

            if (from.HasValue) rows = rows.Where(i => i.InvoiceDate >= from.Value.Date);
            if (to.HasValue)
            {
                var end = AccountingSourceHelpers.ExclusiveEnd(to);
                rows = rows.Where(i => i.InvoiceDate < end);
            }

            var applied = new List<ReportFilter>();
            if (!string.IsNullOrWhiteSpace(status))
            {
                rows = rows.Where(i => i.Status == status);
                applied.Add(ReportFilter.Eq("Status", status));
            }
            if (customerId is > 0)
            {
                rows = rows.Where(i => i.CustomerId == customerId.Value);
                applied.Add(ReportFilter.Eq("CustomerId", customerId.Value.ToString()));
            }

            int cap = query.MaxRows > 0 ? query.MaxRows : AccountingDatasets.MaxRows;

            var fetched = await rows
                .OrderByDescending(i => i.InvoiceDate).ThenByDescending(i => i.ID)
                .Take(cap + 1)
                .Select(i => new
                {
                    i.ID, i.InvoiceNo, i.InvoiceDate, i.CustomerId, i.Status,
                    i.SubTotal, i.TaxTotal, i.GrandTotal, i.GrandTotalBase, i.ProjectId, i.JournalEntryId,
                })
                .ToListAsync(cancellationToken);

            bool truncated = fetched.Count > cap;
            if (truncated) fetched = fetched.Take(cap).ToList();

            // Customer names resolved ONCE for the page, company-filtered so a mis-keyed row cannot pull a name
            // from another tenant.
            bool arabic = AccountingSourceHelpers.Arabic(query);
            var ids = fetched.Select(f => f.CustomerId).Distinct().ToList();
            var names = ids.Count == 0
                ? new Dictionary<int, string>()
                : (await _db.Customers.AsNoTracking()
                        .Where(c => c.CompanyID == context.CompanyId && ids.Contains(c.ID))
                        .Select(c => new { c.ID, c.Name, c.NameEn })
                        .ToListAsync(cancellationToken))
                    .ToDictionary(c => c.ID, c => AccountingSourceHelpers.Pick(arabic, c.Name, c.NameEn));

            foreach (var i in fetched)
                builder.AddRow(new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["InvoiceDate"] = i.InvoiceDate,
                    ["InvoiceNo"] = i.InvoiceNo,
                    ["CustomerName"] = names.GetValueOrDefault(i.CustomerId, "#" + i.CustomerId),
                    ["CustomerId"] = i.CustomerId,
                    ["Status"] = i.Status,
                    ["SubTotal"] = i.SubTotal,
                    ["TaxTotal"] = i.TaxTotal,
                    ["GrandTotal"] = i.GrandTotal,
                    ["GrandTotalBase"] = i.GrandTotalBase,
                    ["ProjectId"] = i.ProjectId,
                    ["JournalEntryId"] = i.JournalEntryId,
                });

            return builder.Build(truncated, truncated ? null : fetched.Count, applied,
                new[] { ReportSort.By("InvoiceDate", descending: true) });
        }
    }

    public sealed class PurchasesDataSource : IReportDataSource
    {
        private readonly CrossDbContext _db;
        public PurchasesDataSource(CrossDbContext db) { _db = db; }

        public string Key => AccountingDatasetCodes.Purchases;

        public async Task<ReportDataSet> FetchAsync(ReportDataQuery query, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(query);
            var context = query.Context;
            var columns = query.RequestedColumns.Count > 0 ? query.RequestedColumns : query.Definition.Columns;
            var builder = new ReportDataSetBuilder(columns);

            if (context.CompanyId <= 0) return builder.Build(totalRowCount: 0);

            var from = query.Parameters.GetDate("From");
            var to = query.Parameters.GetDate("To");
            var status = query.Parameters.GetString("Status");
            var vendorId = query.Parameters.GetInt("VendorId");

            var rows = _db.PurchaseInvoices.AsNoTracking()
                .Where(i => i.CompanyID == context.CompanyId);

            if (from.HasValue) rows = rows.Where(i => i.InvoiceDate >= from.Value.Date);
            if (to.HasValue)
            {
                var end = AccountingSourceHelpers.ExclusiveEnd(to);
                rows = rows.Where(i => i.InvoiceDate < end);
            }

            var applied = new List<ReportFilter>();
            if (!string.IsNullOrWhiteSpace(status))
            {
                rows = rows.Where(i => i.Status == status);
                applied.Add(ReportFilter.Eq("Status", status));
            }
            if (vendorId is > 0)
            {
                rows = rows.Where(i => i.VendorId == vendorId.Value);
                applied.Add(ReportFilter.Eq("VendorId", vendorId.Value.ToString()));
            }

            int cap = query.MaxRows > 0 ? query.MaxRows : AccountingDatasets.MaxRows;

            var fetched = await rows
                .OrderByDescending(i => i.InvoiceDate).ThenByDescending(i => i.ID)
                .Take(cap + 1)
                .Select(i => new
                {
                    i.ID, i.InvoiceNo, i.InvoiceDate, i.VendorId, i.Status,
                    i.SubTotal, i.TaxTotal, i.GrandTotal, i.GrandTotalBase, i.ProjectId,
                })
                .ToListAsync(cancellationToken);

            bool truncated = fetched.Count > cap;
            if (truncated) fetched = fetched.Take(cap).ToList();

            bool arabic = AccountingSourceHelpers.Arabic(query);
            var ids = fetched.Select(f => f.VendorId).Distinct().ToList();
            var names = ids.Count == 0
                ? new Dictionary<int, string>()
                : (await _db.Vendors.AsNoTracking()
                        .Where(v => v.CompanyID == context.CompanyId && ids.Contains(v.ID))
                        .Select(v => new { v.ID, v.Name, v.NameEn })
                        .ToListAsync(cancellationToken))
                    .ToDictionary(v => v.ID, v => AccountingSourceHelpers.Pick(arabic, v.Name, v.NameEn));

            foreach (var i in fetched)
                builder.AddRow(new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["InvoiceDate"] = i.InvoiceDate,
                    ["InvoiceNo"] = i.InvoiceNo,
                    ["VendorName"] = names.GetValueOrDefault(i.VendorId, "#" + i.VendorId),
                    ["VendorId"] = i.VendorId,
                    ["Status"] = i.Status,
                    ["SubTotal"] = i.SubTotal,
                    ["TaxTotal"] = i.TaxTotal,
                    ["GrandTotal"] = i.GrandTotal,
                    ["GrandTotalBase"] = i.GrandTotalBase,
                    ["ProjectId"] = i.ProjectId,
                });

            return builder.Build(truncated, truncated ? null : fetched.Count, applied,
                new[] { ReportSort.By("InvoiceDate", descending: true) });
        }
    }

    // ============================================================================================
    // THE TWO DELEGATING SOURCES.
    //
    // They reach Accounting's own calculation instead of reproducing it — that is the whole point. But they
    // resolve IReceivableService OPTIONALLY, and the reason is a contract this file must not break:
    //
    //      AddCrossBusinessReporting() IS SELF-SUFFICIENT. It registers a working reporting platform on its
    //      own, and ReportingDiWiringTests builds exactly that graph with ValidateOnBuild + ValidateScopes.
    //
    // A constructor dependency on IReceivableService made the whole platform unresolvable in any host that had
    // not already registered Accounting — including that test graph, and including Program.cs itself, where the
    // reporting block runs BEFORE the accounting registrations. Nine DI tests failed at once and they were
    // right to: a module dataset must not be able to take the platform down with it.
    //
    // So the dependency is resolved per request and its absence is an honest EMPTY RESULT, exactly as an
    // unresolved company is. The same reasoning ReportingRegistration records for the Workspace contributor:
    // "a deployment which has not activated Reporting simply has no contributor".
    // ============================================================================================
    public sealed class CustomerAgingDataSource : IReportDataSource
    {
        private readonly IServiceProvider _services;
        private readonly IReportClock _clock;

        public CustomerAgingDataSource(IServiceProvider services, IReportClock clock)
        {
            _services = services;
            _clock = clock;
        }

        public string Key => AccountingDatasetCodes.CustomerAging;

        public async Task<ReportDataSet> FetchAsync(ReportDataQuery query, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(query);
            var context = query.Context;
            var columns = query.RequestedColumns.Count > 0 ? query.RequestedColumns : query.Definition.Columns;
            var builder = new ReportDataSetBuilder(columns);

            if (context.CompanyId <= 0) return builder.Build(totalRowCount: 0);

            // The Accounting module is not registered in this host: no rows, no exception. See the block header.
            var receivables = _services.GetService(typeof(IReceivableService)) as IReceivableService;
            if (receivables is null) return builder.Build(totalRowCount: 0);

            // The clock, not DateTime.Now: the platform has one time source and the tests need to pin it.
            var asOf = query.Parameters.GetDate("AsOf") ?? _clock.LocalNow.Date;

            // THE COMPANY ARGUMENT IS THE CONTEXT'S. The service takes a companyId, so this is the exact place a
            // request-supplied tenant would leak in if one were ever accepted — it is not.
            var aging = await receivables.AgingAsync(context.CompanyId, asOf);

            int cap = query.MaxRows > 0 ? query.MaxRows : AccountingDatasets.PartyMaxRows;
            bool truncated = aging.Count > cap;
            var rows = truncated ? aging.Take(cap).ToList() : aging;

            foreach (var r in rows)
                builder.AddRow(new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["CustomerName"] = r.Name,
                    ["CustomerId"] = r.PartyId,
                    ["Current"] = r.Current,
                    ["Days30"] = r.D30,
                    ["Days60"] = r.D60,
                    ["Days90Plus"] = r.D90,
                    ["Total"] = r.Total,
                });

            return builder.Build(truncated, truncated ? null : rows.Count, Array.Empty<ReportFilter>(),
                new[] { ReportSort.By("Total", descending: true) });
        }
    }

    public sealed class CustomerProfitabilityDataSource : IReportDataSource
    {
        private readonly IServiceProvider _services;
        public CustomerProfitabilityDataSource(IServiceProvider services) { _services = services; }

        public string Key => AccountingDatasetCodes.CustomerProfitability;

        public async Task<ReportDataSet> FetchAsync(ReportDataQuery query, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(query);
            var context = query.Context;
            var columns = query.RequestedColumns.Count > 0 ? query.RequestedColumns : query.Definition.Columns;
            var builder = new ReportDataSetBuilder(columns);

            if (context.CompanyId <= 0) return builder.Build(totalRowCount: 0);

            var receivables = _services.GetService(typeof(IReceivableService)) as IReceivableService;
            if (receivables is null) return builder.Build(totalRowCount: 0);

            var analytics = await receivables.GetCustomerAnalyticsAsync(context.CompanyId);

            int cap = query.MaxRows > 0 ? query.MaxRows : AccountingDatasets.PartyMaxRows;
            var all = analytics.Rows;
            bool truncated = all.Count > cap;
            var rows = truncated ? all.Take(cap).ToList() : all;

            // The margin columns are PROJECTED here, and the ENGINE removes them for a caller who lacks the
            // profitability key (ReportDatasetDefinition.VisibleFields). Not projecting them at all would have
            // been the alternative, but the field-sensitivity mechanism is the platform's one answer to this
            // and a second, source-level answer is how the two drift apart.
            foreach (var r in rows)
                builder.AddRow(new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["CustomerName"] = r.Name,
                    ["Segment"] = r.Segment,
                    ["InvoiceCount"] = r.InvoiceCount,
                    ["Invoiced"] = r.Invoiced,
                    ["Received"] = r.Received,
                    ["Outstanding"] = r.Outstanding,
                    ["Revenue"] = r.Revenue,
                    ["Cogs"] = r.Cogs,
                    ["Margin"] = r.Margin,
                    ["MarginPct"] = r.MarginPct,
                    ["LastInvoice"] = r.LastInvoice,
                });

            return builder.Build(truncated, truncated ? null : rows.Count, Array.Empty<ReportFilter>(),
                new[] { ReportSort.By("Invoiced", descending: true) });
        }
    }

    public sealed class JournalActivityDataSource : IReportDataSource
    {
        private readonly CrossDbContext _db;
        public JournalActivityDataSource(CrossDbContext db) { _db = db; }

        public string Key => AccountingDatasetCodes.JournalActivity;

        public async Task<ReportDataSet> FetchAsync(ReportDataQuery query, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(query);
            var context = query.Context;
            var columns = query.RequestedColumns.Count > 0 ? query.RequestedColumns : query.Definition.Columns;
            var builder = new ReportDataSetBuilder(columns);

            if (context.CompanyId <= 0) return builder.Build(totalRowCount: 0);

            var from = query.Parameters.GetDate("From");
            var to = query.Parameters.GetDate("To");
            var status = query.Parameters.GetString("Status");
            var types = query.Parameters.GetList("JournalType")
                .Select(v => v as string)
                .Where(v => !string.IsNullOrWhiteSpace(v))
                .Select(v => v!)
                .ToList();

            var rows = _db.JournalEntries.AsNoTracking()
                .Where(j => j.CompanyID == context.CompanyId);

            if (from.HasValue) rows = rows.Where(j => j.EntryDate >= from.Value.Date);
            if (to.HasValue)
            {
                var end = AccountingSourceHelpers.ExclusiveEnd(to);
                rows = rows.Where(j => j.EntryDate < end);
            }

            var applied = new List<ReportFilter>();
            if (!string.IsNullOrWhiteSpace(status))
            {
                rows = rows.Where(j => j.Status == status);
                applied.Add(ReportFilter.Eq("Status", status));
            }
            if (types.Count > 0) rows = rows.Where(j => types.Contains(j.JournalType));

            int cap = query.MaxRows > 0 ? query.MaxRows : AccountingDatasets.MaxRows;

            var fetched = await rows
                .OrderByDescending(j => j.EntryDate).ThenByDescending(j => j.ID)
                .Take(cap + 1)
                .Select(j => new
                {
                    j.EntryNo, j.EntryDate, j.JournalType, j.SourceType, j.Status, j.PostedAt,
                    j.Description, j.DescriptionEn, j.ReversedByEntryId, j.FiscalPeriodId,
                })
                .ToListAsync(cancellationToken);

            bool truncated = fetched.Count > cap;
            if (truncated) fetched = fetched.Take(cap).ToList();

            bool arabic = AccountingSourceHelpers.Arabic(query);

            foreach (var j in fetched)
                builder.AddRow(new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["EntryDate"] = j.EntryDate,
                    ["EntryNo"] = j.EntryNo,
                    ["JournalType"] = j.JournalType,
                    ["SourceType"] = j.SourceType,
                    ["Status"] = j.Status,
                    ["PostedAt"] = j.PostedAt,
                    ["Description"] = AccountingSourceHelpers.Pick(arabic, j.Description, j.DescriptionEn),
                    ["ReversedByEntryId"] = j.ReversedByEntryId,
                    ["FiscalPeriodId"] = j.FiscalPeriodId,
                });

            return builder.Build(truncated, truncated ? null : fetched.Count, applied,
                new[] { ReportSort.By("EntryDate", descending: true) });
        }
    }
}
