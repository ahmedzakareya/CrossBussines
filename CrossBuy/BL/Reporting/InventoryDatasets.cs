using CrossBuy.Models.Context;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL.Reporting
{
    // ============================================================================================
    // Reporting Platform R2 — THE INVENTORY DATASETS.
    //
    // THE ONE RULE THAT MATTERS HERE, because Inventory is where a reporting platform is most tempted to
    // reimplement the business:
    //
    //      StockService IS THE STOCK WRITER AND THE OWNER OF EVERY STOCK CALCULATION.
    //      Reporting never recomputes a quantity, a cost or a valuation.
    //
    // That is not a slogan; it is what these two datasets are shaped around.
    //
    //   `StockBalance` ALREADY CARRIES QtyOnHand, AvgCost and TotalValue as STORED columns, written by
    //   StockService as movements are posted. So "stock on hand" and "item valuation" are a projection of
    //   authoritative stored values — a read, not a calculation. Reporting summing AvgCost × QtyOnHand itself
    //   would produce a second valuation that disagrees with the ledger the first time a costing method
    //   changed, which is exactly the failure this arrangement avoids.
    //
    //   `StockMovement` is the append-only movement register with its own stored UnitCost/TotalCost. Again a
    //   projection.
    //
    // WHAT IS DELIBERATELY ABSENT: there is no re-derived closing balance, no recomputed moving average, no
    // FIFO/LIFO layer walk (StockCostLayers exists and is StockService's business), and no reorder suggestion.
    // "Low stock" is offered as a THRESHOLD FILTER over the stored quantity — the user supplies the threshold —
    // rather than as a reorder rule Reporting invented. Negative stock is likewise a filter over the stored
    // sign, not a judgement.
    //
    // COMPANY ISOLATION and AUTHORIZATION follow the Accounting datasets exactly: context-derived CompanyId on
    // every query, no companyId parameter anywhere, empty on an unresolved company, one module key through the
    // single IReportPermissionEvaluator seam, fail-closed when unmapped.
    // ============================================================================================
    public static class InventoryReportPermissions
    {
        // The module's reporting key. Mapped in Program.cs; unmapped means denied.
        public const string View = "inventory.reports.view";

        // Cost and valuation are a narrower grant than quantity. A branch storekeeper has every reason to see
        // how many units are on the shelf and no automatic right to see what the company paid for them —
        // purchase cost is commercially sensitive and, in a group, negotiable per supplier.
        public const string Cost = "inventory.reports.cost";
    }

    public static class InventoryDatasetCodes
    {
        public const string StockOnHand = "Inventory.StockOnHand";
        public const string StockMovements = "Inventory.StockMovements";

        public const string CategoryKey = "inventory.reporting";
    }

    public static class InventoryDatasets
    {
        // On-hand is one row per (item, warehouse). A 20 000-item catalogue across 5 warehouses is 100 000 rows,
        // so the ceiling is generous but real.
        public const int OnHandMaxRows = 100_000;

        // The movement register grows without bound — it is the busiest table Inventory owns. A month of a busy
        // hypermarket is well inside this; a year is not, and the truncation notice is the honest answer.
        public const int MovementMaxRows = 50_000;

        private static ReportParameterDescriptor CompanyParam() => new()
        {
            Key = ReportSystemParameters.CompanyId, TitleAr = "الشركة", TitleEn = "Company",
            Type = ReportFieldType.Integer, SystemSupplied = true,
        };

        // ---- Inventory.StockOnHand ---------------------------------------------------------------
        //
        // Covers four of the brief's five questions with ONE dataset rather than four near-duplicates:
        // stock on hand, warehouse stock, low/negative stock (threshold parameters) and item valuation
        // (the stored cost columns). Splitting them would have produced four datasets differing only by a
        // WHERE clause the user can already express.
        public static ReportDatasetDefinition StockOnHand() => new()
        {
            DatasetCode = InventoryDatasetCodes.StockOnHand,
            Module = "Inventory",
            TitleAr = "أرصدة المخزون",
            TitleEn = "Stock on hand",
            DescriptionAr = "الكمية المتاحة لكل صنف في كل مخزن، مع القيمة والتكلفة المتوسطة كما سجّلتها خدمة المخزون.",
            DescriptionEn = "Quantity available per item per warehouse, with value and average cost exactly as the "
                          + "stock service recorded them. Filter for negative or below-threshold stock.",
            DataSourceKey = InventoryDatasetCodes.StockOnHand,
            RequiredPermissionKey = InventoryReportPermissions.View,
            MaxRows = OnHandMaxRows,
            RowCapPolicy = ReportRowCapPolicy.TruncateAndDeclare,

            // Cost columns are gated fields, so a CSV of this dataset can carry them. Exporting therefore needs
            // the export right too — same policy the Business Event log applies to its payload.
            ExportPolicy = ReportDatasetExportPolicy.RequirePermissionForDataExport,

            Fields = new[]
            {
                new ReportDatasetField
                {
                    Key = "ItemCode", TitleAr = "كود الصنف", TitleEn = "Item code", WidthMm = 30,
                },
                new ReportDatasetField
                {
                    Key = "ItemName", TitleAr = "الصنف", TitleEn = "Item",
                    Groupable = true, WidthMm = 60,
                    SupportedAggregates = new[] { ReportAggregate.CountDistinct },
                },
                new ReportDatasetField
                {
                    Key = "ItemId", TitleAr = "رقم الصنف", TitleEn = "Item id",
                    Type = ReportFieldType.Integer, VisibleByDefault = false, WidthMm = 20,
                    DrillThroughKey = "item",
                },
                new ReportDatasetField
                {
                    Key = "WarehouseName", TitleAr = "المخزن", TitleEn = "Warehouse",
                    Groupable = true, WidthMm = 40,
                    SupportedAggregates = new[] { ReportAggregate.CountDistinct },
                },
                new ReportDatasetField
                {
                    Key = "WarehouseCode", TitleAr = "كود المخزن", TitleEn = "Warehouse code",
                    VisibleByDefault = false, WidthMm = 22,
                },
                new ReportDatasetField
                {
                    Key = "QtyOnHand", TitleAr = "الكمية", TitleEn = "Qty on hand",
                    Type = ReportFieldType.Decimal, Align = ReportAlign.End, WidthMm = 26,
                    SupportedAggregates = new[] { ReportAggregate.Sum, ReportAggregate.Min, ReportAggregate.Max },
                },
                new ReportDatasetField
                {
                    Key = "AvgCost", TitleAr = "متوسط التكلفة", TitleEn = "Avg cost",
                    Type = ReportFieldType.Money, Align = ReportAlign.End, WidthMm = 26,
                    VisibleByDefault = false,
                    Sensitivity = ReportFieldSensitivity.Confidential,
                    RequiredPermissionKey = InventoryReportPermissions.Cost,
                    // NOT summed: adding average costs across items is meaningless. Average of averages is
                    // itself weak, but it is at least a summary somebody can reason about.
                    SupportedAggregates = new[] { ReportAggregate.Average },
                },
                new ReportDatasetField
                {
                    Key = "TotalValue", TitleAr = "قيمة المخزون", TitleEn = "Stock value",
                    Type = ReportFieldType.Money, Align = ReportAlign.End, WidthMm = 30,
                    Sensitivity = ReportFieldSensitivity.Confidential,
                    RequiredPermissionKey = InventoryReportPermissions.Cost,
                    SupportedAggregates = new[] { ReportAggregate.Sum },
                },
                new ReportDatasetField
                {
                    Key = "ItemType", TitleAr = "نوع الصنف", TitleEn = "Item type",
                    Groupable = true, VisibleByDefault = false, WidthMm = 24,
                    SupportedAggregates = new[] { ReportAggregate.Count },
                },
                new ReportDatasetField
                {
                    Key = "LastMovementAt", TitleAr = "آخر حركة", TitleEn = "Last movement",
                    Type = ReportFieldType.DateTime, Format = "yyyy-MM-dd HH:mm", WidthMm = 32,
                    SupportedAggregates = new[] { ReportAggregate.Min, ReportAggregate.Max },
                },
            },

            Parameters = new[]
            {
                new ReportParameterDescriptor
                {
                    Key = "WarehouseId", TitleAr = "المخزن", TitleEn = "Warehouse",
                    Type = ReportFieldType.Integer,
                    HelpTextAr = "اتركه فارغًا لكل المخازن.", HelpTextEn = "Leave empty for every warehouse.",
                },
                new ReportParameterDescriptor
                {
                    Key = "ItemId", TitleAr = "الصنف", TitleEn = "Item", Type = ReportFieldType.Integer,
                },
                new ReportParameterDescriptor
                {
                    // The "low / negative stock" answer, expressed as data the USER supplies. Reporting does not
                    // own a reorder rule, and inventing one here would put a purchasing decision inside a
                    // reporting screen.
                    Key = "StockState", TitleAr = "حالة الرصيد", TitleEn = "Stock state",
                    Options = new[]
                    {
                        new ReportParameterOption { Value = "", LabelAr = "الكل", LabelEn = "Any" },
                        new ReportParameterOption { Value = "Negative", LabelAr = "سالب", LabelEn = "Negative" },
                        new ReportParameterOption { Value = "Zero", LabelAr = "صفر", LabelEn = "Zero" },
                        new ReportParameterOption { Value = "Positive", LabelAr = "موجب", LabelEn = "In stock" },
                    },
                    DefaultValue = "",
                    HelpTextAr = "«سالب» يكشف الأرصدة التي تحتاج تسوية.",
                    HelpTextEn = "\"Negative\" surfaces balances that need investigating.",
                },
                new ReportParameterDescriptor
                {
                    Key = "BelowQty", TitleAr = "أقل من كمية", TitleEn = "Below quantity",
                    Type = ReportFieldType.Decimal,
                    HelpTextAr = "يعرض الأصناف التي كميتها أقل من هذا الحد — الحد من عندك، لا من قاعدة إعادة طلب.",
                    HelpTextEn = "Shows items under this quantity. The threshold is yours — Reporting owns no "
                               + "reorder rule.",
                },
                CompanyParam(),
            },

            DrillDowns = new[]
            {
                new ReportDatasetDrillDown
                {
                    Key = "by-warehouse", TitleAr = "حسب المخزن", TitleEn = "By warehouse",
                    Levels = new[] { "WarehouseName", "ItemType", "ItemName" },
                },
            },
        };

        // ---- Inventory.StockMovements ------------------------------------------------------------
        public static ReportDatasetDefinition StockMovements() => new()
        {
            DatasetCode = InventoryDatasetCodes.StockMovements,
            Module = "Inventory",
            TitleAr = "حركات المخزون",
            TitleEn = "Stock movements",
            DescriptionAr = "سجل الحركات: الوارد والصادر لكل صنف ومخزن، بالكمية والتكلفة ومصدر الحركة.",
            DescriptionEn = "The movement register: in and out per item and warehouse, with quantity, cost and the "
                          + "document that caused it.",
            DataSourceKey = InventoryDatasetCodes.StockMovements,
            RequiredPermissionKey = InventoryReportPermissions.View,
            MaxRows = MovementMaxRows,
            RowCapPolicy = ReportRowCapPolicy.TruncateAndDeclare,
            ExportPolicy = ReportDatasetExportPolicy.RequirePermissionForDataExport,

            Fields = new[]
            {
                new ReportDatasetField
                {
                    Key = "MovementDate", TitleAr = "التاريخ", TitleEn = "Date",
                    Type = ReportFieldType.Date, Format = "yyyy-MM-dd", Groupable = true, WidthMm = 26,
                    SupportedAggregates = new[] { ReportAggregate.Min, ReportAggregate.Max, ReportAggregate.Count },
                },
                new ReportDatasetField
                {
                    Key = "MovementNo", TitleAr = "رقم الحركة", TitleEn = "Movement no.", WidthMm = 30,
                },
                new ReportDatasetField
                {
                    Key = "ItemName", TitleAr = "الصنف", TitleEn = "Item",
                    Groupable = true, WidthMm = 56,
                    SupportedAggregates = new[] { ReportAggregate.CountDistinct },
                },
                new ReportDatasetField
                {
                    Key = "ItemCode", TitleAr = "كود الصنف", TitleEn = "Item code",
                    VisibleByDefault = false, WidthMm = 26,
                },
                new ReportDatasetField
                {
                    Key = "WarehouseName", TitleAr = "المخزن", TitleEn = "Warehouse",
                    Groupable = true, WidthMm = 38,
                    SupportedAggregates = new[] { ReportAggregate.CountDistinct },
                },
                new ReportDatasetField
                {
                    // The stored +1/-1 rendered as a word. A raw short in a column headed "Direction" is a
                    // puzzle on a printed page; "In"/"Out" is the fact the reader wants.
                    Key = "DirectionText", TitleAr = "الاتجاه", TitleEn = "Direction",
                    Groupable = true, WidthMm = 20,
                    SupportedAggregates = new[] { ReportAggregate.Count },
                },
                new ReportDatasetField
                {
                    Key = "QtyBase", TitleAr = "الكمية", TitleEn = "Qty",
                    Type = ReportFieldType.Decimal, Align = ReportAlign.End, WidthMm = 24,
                    SupportedAggregates = new[] { ReportAggregate.Sum, ReportAggregate.Average },
                },
                new ReportDatasetField
                {
                    Key = "UnitCost", TitleAr = "تكلفة الوحدة", TitleEn = "Unit cost",
                    Type = ReportFieldType.Money, Align = ReportAlign.End, WidthMm = 26,
                    VisibleByDefault = false,
                    Sensitivity = ReportFieldSensitivity.Confidential,
                    RequiredPermissionKey = InventoryReportPermissions.Cost,
                    SupportedAggregates = new[] { ReportAggregate.Average },
                },
                new ReportDatasetField
                {
                    Key = "TotalCost", TitleAr = "قيمة الحركة", TitleEn = "Movement value",
                    Type = ReportFieldType.Money, Align = ReportAlign.End, WidthMm = 28,
                    Sensitivity = ReportFieldSensitivity.Confidential,
                    RequiredPermissionKey = InventoryReportPermissions.Cost,
                    SupportedAggregates = new[] { ReportAggregate.Sum },
                },
                new ReportDatasetField
                {
                    Key = "SourceType", TitleAr = "المصدر", TitleEn = "Source",
                    Groupable = true, WidthMm = 30, SupportedAggregates = new[] { ReportAggregate.CountDistinct },
                },
                new ReportDatasetField
                {
                    Key = "SourceId", TitleAr = "رقم المستند", TitleEn = "Document id",
                    Type = ReportFieldType.Integer, VisibleByDefault = false, WidthMm = 20,
                },
                new ReportDatasetField
                {
                    Key = "JournalEntryId", TitleAr = "قيد اليومية", TitleEn = "Journal entry",
                    Type = ReportFieldType.Integer, VisibleByDefault = false, WidthMm = 22,
                },
                new ReportDatasetField
                {
                    Key = "BatchId", TitleAr = "الدفعة", TitleEn = "Batch",
                    Type = ReportFieldType.Integer, VisibleByDefault = false, WidthMm = 20,
                },
                new ReportDatasetField
                {
                    Key = "SerialNo", TitleAr = "الرقم التسلسلي", TitleEn = "Serial",
                    VisibleByDefault = false, WidthMm = 30,
                },
            },

            Parameters = new[]
            {
                new ReportParameterDescriptor
                {
                    Key = "From", TitleAr = "من تاريخ", TitleEn = "From",
                    Type = ReportFieldType.Date, Required = true, DefaultValue = "month-start",
                    HelpTextAr = "بداية الفترة (شاملة).", HelpTextEn = "Start of the period (inclusive).",
                },
                new ReportParameterDescriptor
                {
                    Key = "To", TitleAr = "إلى تاريخ", TitleEn = "To",
                    Type = ReportFieldType.Date, Required = true, DefaultValue = "today",
                    HelpTextAr = "نهاية الفترة (شاملة).", HelpTextEn = "End of the period (inclusive).",
                },
                new ReportParameterDescriptor
                {
                    Key = "WarehouseId", TitleAr = "المخزن", TitleEn = "Warehouse", Type = ReportFieldType.Integer,
                },
                new ReportParameterDescriptor
                {
                    Key = "ItemId", TitleAr = "الصنف", TitleEn = "Item", Type = ReportFieldType.Integer,
                },
                new ReportParameterDescriptor
                {
                    Key = "Direction", TitleAr = "الاتجاه", TitleEn = "Direction",
                    Options = new[]
                    {
                        new ReportParameterOption { Value = "", LabelAr = "الكل", LabelEn = "Any" },
                        new ReportParameterOption { Value = "In", LabelAr = "وارد", LabelEn = "In" },
                        new ReportParameterOption { Value = "Out", LabelAr = "صادر", LabelEn = "Out" },
                    },
                    DefaultValue = "",
                },
                new ReportParameterDescriptor
                {
                    Key = "SourceType", TitleAr = "المصدر", TitleEn = "Source type", AllowMultiple = true,
                    HelpTextAr = "اتركه فارغًا لكل المصادر.", HelpTextEn = "Leave empty for every source.",
                },
                CompanyParam(),
            },

            DrillDowns = new[]
            {
                new ReportDatasetDrillDown
                {
                    Key = "by-item", TitleAr = "حسب الصنف", TitleEn = "By item",
                    Levels = new[] { "ItemName", "WarehouseName", "DirectionText" },
                },
            },
        };

        public static IEnumerable<ReportDatasetDefinition> All()
        {
            yield return StockOnHand();
            yield return StockMovements();
        }
    }

    public sealed class InventoryReportDefinitionProvider : IReportDefinitionProvider
    {
        public string ProviderName => "Inventory";

        public IEnumerable<ReportDefinition> GetDefinitions()
        {
            yield return Build(InventoryDatasets.StockOnHand(), "ki-outline ki-package", "primary", 200,
                ReportSort.By("ItemName"));
            yield return Build(InventoryDatasets.StockMovements(), "ki-outline ki-arrow-two-diagonals", "info", 210,
                ReportSort.By("MovementDate", descending: true));
        }

        private static ReportDefinition Build(ReportDatasetDefinition dataset, string icon, string color,
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
            CategoryKey = InventoryDatasetCodes.CategoryKey,
            Tags = new[] { "inventory", "stock" },
            Columns = dataset.Fields.Select(f => f.ToColumn()).ToList(),
            Parameters = dataset.Parameters,
            DefaultSorts = defaultSorts,
            DefaultFormat = ReportOutputFormat.Html,
            Capabilities = new ReportCapabilities
            {
                Formats = new[]
                {
                    ReportOutputFormat.Html, ReportOutputFormat.PrintHtml,
                    ReportOutputFormat.Csv, ReportOutputFormat.Xlsx, ReportOutputFormat.Pdf,
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
    // ============================================================================================
    public sealed class StockOnHandDataSource : IReportDataSource
    {
        private readonly CrossDbContext _db;
        public StockOnHandDataSource(CrossDbContext db) { _db = db; }

        public string Key => InventoryDatasetCodes.StockOnHand;

        public async Task<ReportDataSet> FetchAsync(ReportDataQuery query, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(query);
            var context = query.Context;
            var columns = query.RequestedColumns.Count > 0 ? query.RequestedColumns : query.Definition.Columns;
            var builder = new ReportDataSetBuilder(columns);

            // FAIL CLOSED on an unresolved tenant.
            if (context.CompanyId <= 0) return builder.Build(totalRowCount: 0);

            var warehouseId = query.Parameters.GetInt("WarehouseId");
            var itemId = query.Parameters.GetInt("ItemId");
            var state = query.Parameters.GetString("StockState");
            var belowQty = query.Parameters.GetDecimal("BelowQty");

            var rows = _db.StockBalances.AsNoTracking()
                .Where(b => b.CompanyID == context.CompanyId);

            var applied = new List<ReportFilter>();

            if (warehouseId is > 0)
            {
                rows = rows.Where(b => b.WarehouseId == warehouseId.Value);
                applied.Add(ReportFilter.Eq("WarehouseId", warehouseId.Value.ToString()));
            }
            if (itemId is > 0)
            {
                rows = rows.Where(b => b.ItemId == itemId.Value);
                applied.Add(ReportFilter.Eq("ItemId", itemId.Value.ToString()));
            }

            // Pushed into SQL, not filtered in memory: on a 100 000-row balance table, capping first and
            // filtering second would search the wrong slice and report "no negatives" for a company that has
            // plenty. Same reasoning the Business Event log records for its delivery filter.
            rows = state switch
            {
                "Negative" => rows.Where(b => b.QtyOnHand < 0),
                "Zero" => rows.Where(b => b.QtyOnHand == 0),
                "Positive" => rows.Where(b => b.QtyOnHand > 0),
                _ => rows,
            };
            if (belowQty.HasValue) rows = rows.Where(b => b.QtyOnHand < belowQty.Value);

            // §10 PUSHDOWN — the same reasoning as the StockState filter above, generalised. A Studio filter now
            // reaches SQL BEFORE the cap instead of searching the slice the cap already chose.
            rows = ReportFilterPushdown.Apply(rows, query.Filters, applied,
                new Dictionary<string, ReportFilterPushdown.Push<Models.Context.Inventory.StockBalance>>(StringComparer.Ordinal)
                {
                    ["ItemId"] = (q, op, v) => ReportFilterPushdown.Integer(q, b => b.ItemId, op, v),
                    ["WarehouseId"] = (q, op, v) => ReportFilterPushdown.Integer(q, b => b.WarehouseId, op, v),
                    ["QtyOnHand"] = (q, op, v) => ReportFilterPushdown.Number(q, b => b.QtyOnHand, op, v),
                    ["AvgCost"] = (q, op, v) => ReportFilterPushdown.Number(q, b => b.AvgCost, op, v),
                    ["TotalValue"] = (q, op, v) => ReportFilterPushdown.Number(q, b => b.TotalValue, op, v),
                });

            int cap = query.MaxRows > 0 ? query.MaxRows : InventoryDatasets.OnHandMaxRows;

            var fetched = await rows
                .OrderBy(b => b.ItemId).ThenBy(b => b.WarehouseId)
                .Take(cap + 1)
                .Select(b => new
                {
                    b.ItemId, b.WarehouseId, b.QtyOnHand, b.AvgCost, b.TotalValue, b.LastMovementAt,
                })
                .ToListAsync(cancellationToken);

            bool truncated = fetched.Count > cap;
            if (truncated) fetched = fetched.Take(cap).ToList();

            bool arabic = AccountingSourceHelpers.Arabic(query);

            // Item and warehouse names resolved ONCE for the page, both company-filtered so a mis-keyed row
            // cannot pull a name from another tenant.
            var itemIds = fetched.Select(f => f.ItemId).Distinct().ToList();
            var items = itemIds.Count == 0
                ? new Dictionary<int, (string Code, string Name, string Type)>()
                : (await _db.Items.AsNoTracking()
                        .Where(i => i.CompanyID == context.CompanyId && itemIds.Contains(i.ID))
                        .Select(i => new { i.ID, i.ItemCode, i.Name, i.NameEn, i.ItemType })
                        .ToListAsync(cancellationToken))
                    .ToDictionary(i => i.ID,
                        i => (i.ItemCode, AccountingSourceHelpers.Pick(arabic, i.Name, i.NameEn), i.ItemType));

            var whIds = fetched.Select(f => f.WarehouseId).Distinct().ToList();
            var warehouses = whIds.Count == 0
                ? new Dictionary<int, (string Code, string Name)>()
                : (await _db.Warehouses.AsNoTracking()
                        .Where(w => w.CompanyID == context.CompanyId && whIds.Contains(w.ID))
                        .Select(w => new { w.ID, w.Code, w.Name, w.NameEn })
                        .ToListAsync(cancellationToken))
                    .ToDictionary(w => w.ID,
                        w => (w.Code, AccountingSourceHelpers.Pick(arabic, w.Name, w.NameEn)));

            foreach (var b in fetched)
            {
                // Explicitly typed: GetValueOrDefault with an unnamed fallback tuple erases the element names,
                // and "item.Item2" at the call site below would be unreadable for no gain.
                (string Code, string Name, string Type) item = items.TryGetValue(b.ItemId, out var itemHit)
                    ? itemHit
                    : ("", "#" + b.ItemId, "");
                (string Code, string Name) wh = warehouses.TryGetValue(b.WarehouseId, out var whHit)
                    ? whHit
                    : ("", "#" + b.WarehouseId);

                builder.AddRow(new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["ItemCode"] = item.Code,
                    ["ItemName"] = item.Name,
                    ["ItemId"] = b.ItemId,
                    ["WarehouseName"] = wh.Name,
                    ["WarehouseCode"] = wh.Code,
                    ["QtyOnHand"] = b.QtyOnHand,
                    ["AvgCost"] = b.AvgCost,
                    ["TotalValue"] = b.TotalValue,
                    ["ItemType"] = item.Type,
                    ["LastMovementAt"] = b.LastMovementAt,
                });
            }

            return builder.Build(truncated, truncated ? null : fetched.Count, applied,
                new[] { ReportSort.By("ItemName") });
        }
    }

    public sealed class StockMovementsDataSource : IReportDataSource
    {
        private readonly CrossDbContext _db;
        public StockMovementsDataSource(CrossDbContext db) { _db = db; }

        public string Key => InventoryDatasetCodes.StockMovements;

        public async Task<ReportDataSet> FetchAsync(ReportDataQuery query, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(query);
            var context = query.Context;
            var columns = query.RequestedColumns.Count > 0 ? query.RequestedColumns : query.Definition.Columns;
            var builder = new ReportDataSetBuilder(columns);

            if (context.CompanyId <= 0) return builder.Build(totalRowCount: 0);

            var from = query.Parameters.GetDate("From");
            var to = query.Parameters.GetDate("To");
            var warehouseId = query.Parameters.GetInt("WarehouseId");
            var itemId = query.Parameters.GetInt("ItemId");
            var direction = query.Parameters.GetString("Direction");
            var sourceTypes = query.Parameters.GetList("SourceType")
                .Select(v => v as string)
                .Where(v => !string.IsNullOrWhiteSpace(v))
                .Select(v => v!)
                .ToList();

            var rows = _db.StockMovements.AsNoTracking()
                .Where(m => m.CompanyID == context.CompanyId);

            if (from.HasValue) rows = rows.Where(m => m.MovementDate >= from.Value.Date);
            if (to.HasValue)
            {
                var end = AccountingSourceHelpers.ExclusiveEnd(to);
                rows = rows.Where(m => m.MovementDate < end);
            }

            var applied = new List<ReportFilter>();
            if (warehouseId is > 0)
            {
                rows = rows.Where(m => m.WarehouseId == warehouseId.Value);
                applied.Add(ReportFilter.Eq("WarehouseId", warehouseId.Value.ToString()));
            }
            if (itemId is > 0)
            {
                rows = rows.Where(m => m.ItemId == itemId.Value);
                applied.Add(ReportFilter.Eq("ItemId", itemId.Value.ToString()));
            }

            // The stored column is a short (+1 / -1); the parameter is a word. Translating here keeps the
            // vocabulary out of the URL and the filter in SQL.
            if (direction == "In") rows = rows.Where(m => m.Direction > 0);
            else if (direction == "Out") rows = rows.Where(m => m.Direction < 0);

            if (sourceTypes.Count > 0) rows = rows.Where(m => m.SourceType != null && sourceTypes.Contains(m.SourceType));

            rows = ReportFilterPushdown.Apply(rows, query.Filters, applied,
                new Dictionary<string, ReportFilterPushdown.Push<Models.Context.Inventory.StockMovement>>(StringComparer.Ordinal)
                {
                    ["ItemId"] = (q, op, v) => ReportFilterPushdown.Integer(q, m => m.ItemId, op, v),
                    ["WarehouseId"] = (q, op, v) => ReportFilterPushdown.Integer(q, m => m.WarehouseId, op, v),
                    ["MovementDate"] = (q, op, v) => ReportFilterPushdown.Date(q, m => m.MovementDate, op, v),
                    ["SourceType"] = (q, op, v) => ReportFilterPushdown.Text(q, m => m.SourceType, op, v),
                    ["MovementNo"] = (q, op, v) => ReportFilterPushdown.Text(q, m => m.MovementNo, op, v),
                    ["QtyBase"] = (q, op, v) => ReportFilterPushdown.Number(q, m => m.QtyBase, op, v),
                    ["UnitCost"] = (q, op, v) => ReportFilterPushdown.Number(q, m => m.UnitCost, op, v),
                    ["TotalCost"] = (q, op, v) => ReportFilterPushdown.Number(q, m => m.TotalCost, op, v),
                });

            int cap = query.MaxRows > 0 ? query.MaxRows : InventoryDatasets.MovementMaxRows;

            var fetched = await rows
                .OrderByDescending(m => m.MovementDate).ThenByDescending(m => m.ID)
                .Take(cap + 1)
                .Select(m => new
                {
                    m.MovementNo, m.MovementDate, m.ItemId, m.WarehouseId, m.Direction, m.QtyBase,
                    m.UnitCost, m.TotalCost, m.SourceType, m.SourceId, m.JournalEntryId, m.BatchId, m.SerialNo,
                })
                .ToListAsync(cancellationToken);

            bool truncated = fetched.Count > cap;
            if (truncated) fetched = fetched.Take(cap).ToList();

            bool arabic = AccountingSourceHelpers.Arabic(query);

            var itemIds = fetched.Select(f => f.ItemId).Distinct().ToList();
            var items = itemIds.Count == 0
                ? new Dictionary<int, (string Code, string Name)>()
                : (await _db.Items.AsNoTracking()
                        .Where(i => i.CompanyID == context.CompanyId && itemIds.Contains(i.ID))
                        .Select(i => new { i.ID, i.ItemCode, i.Name, i.NameEn })
                        .ToListAsync(cancellationToken))
                    .ToDictionary(i => i.ID,
                        i => (i.ItemCode, AccountingSourceHelpers.Pick(arabic, i.Name, i.NameEn)));

            var whIds = fetched.Select(f => f.WarehouseId).Distinct().ToList();
            var warehouses = whIds.Count == 0
                ? new Dictionary<int, string>()
                : (await _db.Warehouses.AsNoTracking()
                        .Where(w => w.CompanyID == context.CompanyId && whIds.Contains(w.ID))
                        .Select(w => new { w.ID, w.Name, w.NameEn })
                        .ToListAsync(cancellationToken))
                    .ToDictionary(w => w.ID, w => AccountingSourceHelpers.Pick(arabic, w.Name, w.NameEn));

            foreach (var m in fetched)
            {
                (string Code, string Name) item = items.TryGetValue(m.ItemId, out var itemHit)
                    ? itemHit
                    : ("", "#" + m.ItemId);

                builder.AddRow(new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["MovementDate"] = m.MovementDate,
                    ["MovementNo"] = m.MovementNo,
                    ["ItemName"] = item.Name,
                    ["ItemCode"] = item.Code,
                    ["WarehouseName"] = warehouses.GetValueOrDefault(m.WarehouseId, "#" + m.WarehouseId),
                    ["DirectionText"] = m.Direction > 0 ? (arabic ? "وارد" : "In") : (arabic ? "صادر" : "Out"),
                    ["QtyBase"] = m.QtyBase,
                    ["UnitCost"] = m.UnitCost,
                    ["TotalCost"] = m.TotalCost,
                    ["SourceType"] = m.SourceType,
                    ["SourceId"] = m.SourceId,
                    ["JournalEntryId"] = m.JournalEntryId,
                    ["BatchId"] = m.BatchId,
                    ["SerialNo"] = m.SerialNo,
                });
            }

            return builder.Build(truncated, truncated ? null : fetched.Count, applied,
                new[] { ReportSort.By("MovementDate", descending: true) });
        }
    }
}
