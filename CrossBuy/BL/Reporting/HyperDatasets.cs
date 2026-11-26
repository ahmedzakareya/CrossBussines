using CrossBuy.Models.Context;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL.Reporting
{
    // ============================================================================================
    // HYPERMARKET — TOP SELLERS (2026-11-09). The first Hyper dataset; there was no Hyper reporting
    // file at all, so a store manager's weekly "what sold" had no answer inside Report Studio.
    //
    // WHY A DATASET AND NOT A SCREEN: every module reports through the Studio (Accounting, Inventory,
    // CRM, Hospital, HR). A bespoke page would have had to re-grow export, print, scheduling, the row
    // cap and the permission model, and it would have broken the owner rule that nothing prints from
    // a view.
    //
    // ---- WHERE EACH NUMBER COMES FROM, AND WHY NOT FROM WHERE IT LOOKS LIKE IT SHOULD ----
    //
    // The specification handed over said "cost is on the item". It is not. `Item` carries only
    // `OpeningCost` (an opening balance) and `CostingMethod` (which inherits from the category when
    // null) — an item's cost is a COMPUTED, costing-method-dependent figure, and InventoryDatasets
    // states the standing rule for this tree: "Reporting never recomputes a quantity, a cost or a
    // valuation." So:
    //
    //   REVENUE  <- PosOrderLine.LineTotal. Stored, pre-tax, already net of the per-line discount.
    //               This is what the customer actually paid, so the report and the receipt agree.
    //   QUANTITY <- StockMovement.QtyBase, NOT PosOrderLine.Qty. A POS line's Qty is expressed in that
    //               line's UoMId, so summing it adds cartons to pieces and produces a number that means
    //               nothing. QtyBase is stored in the item's base unit.
    //   COST     <- StockMovement.TotalCost. Stored at the moment of sale by StockService, so it is the
    //               cost that sale actually bore, not today's cost. Reached exactly as the product's own
    //               cancellation path reaches it (PosOrderService.cs:1509):
    //                   SourceType == "SalesInvoice" && SourceId == invoice.ID && Direction == -1
    //               A POS sale writes no movement of its own: it pays into a SalesInvoice and the
    //               movement hangs off that.
    //   MARGIN    = Revenue - Cost. A subtraction of two authoritative stored figures for display. It
    //               is NOT a second valuation, which is the thing the rule above forbids.
    //
    // A NON-STOCK ITEM HAS NO MOVEMENT, so Cost, Margin and Margin % are NULL for it — never 0. A
    // service line priced at 5.000 with no cost row is not a 100% margin; it is a figure this dataset
    // cannot source, and saying so is the only honest answer. Revenue and Qty sold still populate.
    //
    // MONEY IS NOT FORMATTED TO 2dp ANYWHERE HERE. A hyper branch settles in Kuwaiti dinar at three
    // decimals (the model agrees: PosOrderLines.SentQty is (18,3)), and a report that disagrees with
    // the receipt is worse than no report. The source emits raw decimals and the field Format carries
    // three places; rounding to a currency belongs to the Viewer, not to a data source. Deliberately
    // no ICurrencyRounding dependency: a reporting data source that injects a module service makes the
    // whole reporting pack unbuildable on its own, which is the defect this tree has already paid for.
    //
    // TENANT ISOLATION: PosOrderLine carries NO CompanyId and no BranchId — it hangs off OrderId alone.
    // So every line and every movement here is reached through PosOrder filtered on the resolved
    // company, and an unresolved tenant returns empty rather than everything.
    //
    // PERMISSION: hyper.reports.view. No Hyper module authority claims the `hyper.` namespace yet, so
    // the key falls through to the fail-closed role map and this dataset DENIES EVERYONE until the key
    // is mapped in Program.cs. That is the intended landing, not an oversight — see the registration
    // note below.
    // ============================================================================================
    public static class HyperDatasetCodes
    {
        public const string TopSellers = "Hyper.TopSellers";
        public const string BasketAnalysis = "Hyper.BasketAnalysis";
    }

    public static class HyperReportPermissions
    {
        // The namespace a future HyperReportModuleAuthority would claim. Kept beside the key so the two
        // cannot drift: a key outside its own prefix is routed to the role map and the module is never
        // asked at all.
        public const string Prefix = "hyper.reports.";
        public const string View = "hyper.reports.view";

        // THE SECOND TIER. Quantity sold and net sales are what a store manager runs the shop on;
        // cost and margin are what a competitor would want. Inventory splits exactly this way
        // (inventory.reports.view / .cost) and Accounting does the same with .profitability, so the
        // split is the house pattern rather than a new idea. Carried on the FIELDS below through
        // ReportDatasetField.RequiredPermissionKey, which the platform evaluates in addition to the
        // dataset key - so one report serves both audiences instead of two reports drifting apart.
        public const string Cost = "hyper.reports.cost";
    }

    public static class HyperDatasets
    {
        public const int TopSellersMaxRows = 5_000;

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

        // OPTIONAL, and optional on purpose: left empty the report covers every branch the company has,
        // which is the group view a head office asks for. Required would force a store manager's
        // question onto a report that cannot answer the owner's.
        private static ReportParameterDescriptor BranchParam() => new()
        {
            Key = ReportSystemParameters.BranchId, TitleAr = "الفرع", TitleEn = "Branch",
            Type = ReportFieldType.Integer, Required = false,
            HelpTextAr = "اتركه فارغًا لتشمل كل الفروع.", HelpTextEn = "Leave empty for every branch.",
        };

        // REQUIRED, with a default rather than optional: a co-occurrence report whose floor nobody
        // chose is one that shows coincidences. Two is the lowest defensible floor - it means "seen
        // more than once" - and a reader who wants the raw tail can lower it DELIBERATELY.
        private static ReportParameterDescriptor MinBasketsParam() => new()
        {
            Key = "MinBaskets", TitleAr = "أقل عدد سلال", TitleEn = "Minimum baskets",
            Type = ReportFieldType.Integer, Required = true, DefaultValue = "2",
            HelpTextAr = "لا يُعرض زوج ظهر في سلال أقل من هذا العدد.",
            HelpTextEn = "A pair seen in fewer baskets than this is not shown.",
        };

        public static ReportDatasetDefinition TopSellers() => new()
        {
            DatasetCode = HyperDatasetCodes.TopSellers,
            Module = "Hyper",
            TitleAr = "الأصناف الأكثر مبيعًا",
            TitleEn = "Top sellers",
            DescriptionAr = "الأصناف الأكثر مبيعًا خلال فترة وفرع: الكمية المباعة بالوحدة الأساسية، وصافي المبيعات قبل الضريبة وبعد الخصم، والتكلفة المسجَّلة وقت البيع، وهامش الربح. التكلفة والهامش فارغان للأصناف غير المخزنية.",
            DescriptionEn = "Best-selling items over a period and a branch: quantity sold in the base unit, net sales before tax and after discount, the cost recorded at the time of sale, and the margin. Cost and margin are empty for non-stock items.",
            DataSourceKey = HyperDatasetCodes.TopSellers,
            RequiredPermissionKey = HyperReportPermissions.View,
            MaxRows = TopSellersMaxRows,
            // The source orders by quantity before the cap applies, so a truncated run keeps the TOP
            // sellers. Truncating an unordered set would silently drop the answer to the question.
            RowCapPolicy = ReportRowCapPolicy.TruncateAndDeclare,
            AvailableInStudio = true,
            AvailableAsWidget = true,
            Parameters = new[] { FromDate(), ToDate(), BranchParam(), AccountingDatasets.CompanyParam() },
            Fields = new[]
            {
                new ReportDatasetField { Key = "ItemCode", TitleAr = "كود الصنف", TitleEn = "Item code", Groupable = true, WidthMm = 24 },
                new ReportDatasetField { Key = "Item", TitleAr = "الصنف", TitleEn = "Item", Groupable = true, WidthMm = 52 },
                new ReportDatasetField { Key = "ItemEn", TitleAr = "الصنف (إنجليزي)", TitleEn = "Item (English)", VisibleByDefault = false, WidthMm = 52 },
                new ReportDatasetField { Key = "ItemType", TitleAr = "نوع الصنف", TitleEn = "Item type", Groupable = true, VisibleByDefault = false, WidthMm = 24 },
                new ReportDatasetField
                {
                    Key = "QtySold", TitleAr = "الكمية المباعة", TitleEn = "Qty sold",
                    Type = ReportFieldType.Decimal, Format = "0.###", Align = ReportAlign.End, WidthMm = 24,
                    SupportedAggregates = new[] { ReportAggregate.Sum },
                },
                new ReportDatasetField
                {
                    Key = "Orders", TitleAr = "عدد الطلبات", TitleEn = "Orders",
                    Type = ReportFieldType.Integer, WidthMm = 20,
                    SupportedAggregates = new[] { ReportAggregate.Sum },
                },
                new ReportDatasetField
                {
                    Key = "Revenue", TitleAr = "صافي المبيعات", TitleEn = "Net sales",
                    // Money, but the Format STAYS: ReportValues falls back to "N2" when a money field
                    // carries none, and two decimals is the wrong currency for a hyper branch.
                    Type = ReportFieldType.Money, Format = "0.000", Align = ReportAlign.End, WidthMm = 28,
                    SupportedAggregates = new[] { ReportAggregate.Sum },
                },
                new ReportDatasetField
                {
                    Key = "Cost", TitleAr = "التكلفة", TitleEn = "Cost",
                    Type = ReportFieldType.Money, Format = "0.000", Align = ReportAlign.End, WidthMm = 28,
                    VisibleByDefault = false,
                    Sensitivity = ReportFieldSensitivity.Confidential,
                    RequiredPermissionKey = HyperReportPermissions.Cost,
                    SupportedAggregates = new[] { ReportAggregate.Sum },
                },
                new ReportDatasetField
                {
                    Key = "Margin", TitleAr = "هامش الربح", TitleEn = "Margin",
                    Type = ReportFieldType.Money, Format = "0.000", Align = ReportAlign.End, WidthMm = 28,
                    VisibleByDefault = false,
                    Sensitivity = ReportFieldSensitivity.Confidential,
                    RequiredPermissionKey = HyperReportPermissions.Cost,
                    SupportedAggregates = new[] { ReportAggregate.Sum },
                },
                new ReportDatasetField
                {
                    // Deliberately NOT aggregatable: summing or averaging a percentage column across
                    // items produces a figure that is not the overall margin and reads as if it were.
                    Key = "MarginPct", TitleAr = "نسبة الهامش %", TitleEn = "Margin %",
                    Type = ReportFieldType.Decimal, Format = "0.0", Align = ReportAlign.End, WidthMm = 20,
                    VisibleByDefault = false,
                    Sensitivity = ReportFieldSensitivity.Confidential,
                    RequiredPermissionKey = HyperReportPermissions.Cost,
                },
            },
        };

        // ---- Hyper.BasketAnalysis -----------------------------------------------------------
        //
        // WHAT SELLS TOGETHER, and honestly about how little it rests on. Measured on this database
        // before the dataset was written: 239 paid orders, but only 54 contain two different items,
        // 31 distinct items sold, 23 distinct pairs - 15 seen twice, 10 seen three times or more,
        // the best seen 11 times. There IS a signal there; it is thin, and a co-occurrence report
        // that prints every pair would show mostly pairs seen ONCE, which is a coincidence wearing
        // the clothes of a pattern. That is the same failure as a shrinkage report over a branch
        // with no counts: a confident answer about nothing.
        //
        // So MinBaskets is a REQUIRED parameter with a default of 2, and the basket count the
        // analysis rests on is a field, not a footnote. A reader who cannot see how many baskets
        // produced a pair cannot judge it.
        public static ReportDatasetDefinition BasketAnalysis() => new()
        {
            DatasetCode = HyperDatasetCodes.BasketAnalysis,
            Module = "Hyper",
            TitleAr = "تحليل سلّة المشتريات",
            TitleEn = "Basket analysis",
            DescriptionAr = "الأصناف التي تُشترى معاً خلال فترة وفرع: عدد السلال التي جمعت الصنفين، وعدد سلال كل صنف على حدة، ونسبة الاقتران. لا يُعرض زوج ظهر أقل من الحدّ الأدنى للسلال، لأن ظهوراً واحداً مصادفة لا نمط.",
            DescriptionEn = "Items bought together over a period and a branch: how many baskets held both, how many held each on its own, and the attachment rate. A pair seen fewer times than the minimum is not shown - a single co-occurrence is a coincidence, not a pattern.",
            DataSourceKey = HyperDatasetCodes.BasketAnalysis,
            RequiredPermissionKey = HyperReportPermissions.View,
            MaxRows = 2_000,
            RowCapPolicy = ReportRowCapPolicy.TruncateAndDeclare,
            AvailableInStudio = true,
            AvailableAsWidget = false,
            Parameters = new[] { FromDate(), ToDate(), BranchParam(), MinBasketsParam(), AccountingDatasets.CompanyParam() },
            Fields = new[]
            {
                new ReportDatasetField { Key = "ItemA", TitleAr = "الصنف الأول", TitleEn = "Item A", Groupable = true, WidthMm = 46 },
                new ReportDatasetField { Key = "ItemB", TitleAr = "الصنف الثاني", TitleEn = "Item B", Groupable = true, WidthMm = 46 },
                new ReportDatasetField
                {
                    Key = "Baskets", TitleAr = "سلال جمعتهما", TitleEn = "Baskets together",
                    Type = ReportFieldType.Integer, Align = ReportAlign.End, WidthMm = 24,
                    SupportedAggregates = new[] { ReportAggregate.Sum },
                },
                new ReportDatasetField
                {
                    Key = "BasketsA", TitleAr = "سلال الصنف الأول", TitleEn = "Baskets with A",
                    Type = ReportFieldType.Integer, Align = ReportAlign.End, WidthMm = 24,
                    VisibleByDefault = false,
                },
                new ReportDatasetField
                {
                    Key = "BasketsB", TitleAr = "سلال الصنف الثاني", TitleEn = "Baskets with B",
                    Type = ReportFieldType.Integer, Align = ReportAlign.End, WidthMm = 24,
                    VisibleByDefault = false,
                },
                new ReportDatasetField
                {
                    // Deliberately NOT aggregatable: averaging attachment rates across pairs is not
                    // the attachment rate of anything, and it would read as if it were.
                    Key = "AttachmentPct", TitleAr = "نسبة الاقتران %", TitleEn = "Attachment %",
                    Type = ReportFieldType.Decimal, Format = "0.0", Align = ReportAlign.End, WidthMm = 22,
                },
                new ReportDatasetField
                {
                    // THE DENOMINATOR, on every row. Without it "seen in 3 baskets" means nothing:
                    // three out of four is a habit, three out of four thousand is noise.
                    Key = "BasketsInPeriod", TitleAr = "إجمالي السلال", TitleEn = "Baskets in period",
                    Type = ReportFieldType.Integer, Align = ReportAlign.End, WidthMm = 24,
                },
                new ReportDatasetField
                {
                    // THE SAME DENOMINATOR ARGUMENT, APPLIED TO TIME (TAB-6, 2026-11-10). A pair in six
                    // baskets across six days is a habit; a pair in six baskets on ONE day is one
                    // afternoon. MinBaskets cannot separate those - both look like frequency - and this
                    // data is burst-seeded: two day/branch buckets hold 165 of the 239 paid orders, so a
                    // pair seen eleven times may be eleven repetitions of one seeding run rather than
                    // eleven shoppers.
                    Key = "PairDays", TitleAr = "أيام ظهور الزوج", TitleEn = "Days seen",
                    Type = ReportFieldType.Integer, Align = ReportAlign.End, WidthMm = 22,
                },
                new ReportDatasetField
                {
                    // THE SPAN OF THE DATA ITSELF, repeated on every row the way a document header is.
                    // Measured 2026-11-10: every paid order in this database falls between 2026-07-02
                    // and 2026-08-04, so a report asked for any recent window comes back EMPTY - and an
                    // empty basket-analysis screen reads as "we have no cross-sell", which is a worse
                    // lie than a noisy one. A reader who can see the span knows which of the two it is.
                    Key = "DataFrom", TitleAr = "بيانات من", TitleEn = "Data from",
                    Type = ReportFieldType.Date, VisibleByDefault = false, WidthMm = 24,
                },
                new ReportDatasetField
                {
                    Key = "DataTo", TitleAr = "بيانات حتى", TitleEn = "Data to",
                    Type = ReportFieldType.Date, VisibleByDefault = false, WidthMm = 24,
                },
            },
        };

        public static IEnumerable<ReportDatasetDefinition> All()
        {
            yield return TopSellers();
            yield return BasketAnalysis();
        }
    }

    public sealed class HyperReportDefinitionProvider : IReportDefinitionProvider
    {
        public string ProviderName => "Hyper";

        public IEnumerable<ReportDefinition> GetDefinitions()
        {
            var dataset = HyperDatasets.TopSellers();
            yield return new ReportDefinition
            {
                Code = dataset.DatasetCode, Module = dataset.Module,
                TitleAr = dataset.TitleAr, TitleEn = dataset.TitleEn,
                DescriptionAr = dataset.DescriptionAr, DescriptionEn = dataset.DescriptionEn,
                DataSourceKey = dataset.DataSourceKey, PermissionKey = dataset.RequiredPermissionKey,
                CategoryKey = "hyper.reporting", Tags = new[] { "hyper", "pos", "sales", "margin" },
                Columns = dataset.Fields.Select(f => f.ToColumn()).ToList(),
                Parameters = dataset.Parameters,
                DefaultSorts = new[] { ReportSort.By("QtySold", descending: true) },
                DefaultFormat = ReportOutputFormat.Html,
                Capabilities = new ReportCapabilities
                {
                    Formats = new[] { ReportOutputFormat.Html, ReportOutputFormat.PrintHtml, ReportOutputFormat.Pdf, ReportOutputFormat.Xlsx },
                    MaxRows = dataset.MaxRows, PreviewRows = 200, AllowSchedule = true, AllowArchive = true, AllowShare = true,
                },
                Icon = "ki-outline ki-basket", Color = "success", SortOrder = 760,
            };
        }
    }

    public sealed class HyperBasketAnalysisDataSource : IReportDataSource
    {
        private readonly CrossDbContext _db;
        public HyperBasketAnalysisDataSource(CrossDbContext db) { _db = db; }
        public string Key => HyperDatasetCodes.BasketAnalysis;

        // THE BOUND. Pairing is quadratic in the number of items considered, so the analysis is
        // restricted to the items appearing in the most baskets. 60 items is at most 1,770 pairs,
        // which sits under the dataset row cap - so the cap can never be the thing that decides
        // WHICH pairs a reader sees. Items outside the top 60 are by definition the ones with too
        // few baskets to say anything about.
        private const int MaxItems = 60;

        public async Task<ReportDataSet> FetchAsync(ReportDataQuery query, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(query);
            var columns = query.RequestedColumns.Count > 0 ? query.RequestedColumns : query.Definition.Columns;
            var builder = new ReportDataSetBuilder(columns);
            var ctx = query.Context;
            if (ctx.CompanyId <= 0) return builder.Build(totalRowCount: 0);   // fail closed on an unresolved tenant

            var from = query.Parameters.GetDate("From");
            var to = query.Parameters.GetDate("To");
            if (from == null || to == null) return builder.Build(totalRowCount: 0);
            var fromDay = from.Value.Date;
            var toExclusive = to.Value.Date.AddDays(1);   // half-open: a 23:59 sale is inside the period
            var branch = query.Parameters.GetInt(ReportSystemParameters.BranchId);
            var minBaskets = Math.Max(1, query.Parameters.GetInt("MinBaskets") ?? 2);

            // The same gate as Top sellers: an Open order is not a sale, a Void one never was, and a
            // merged order has handed its lines to the order it merged into.
            var orders = _db.PosOrders.AsNoTracking()
                .Where(o => o.CompanyId == ctx.CompanyId
                         && o.Status == "Paid"
                         && o.MergedIntoOrderId == null
                         && o.ClosedAt != null && o.ClosedAt >= fromDay && o.ClosedAt < toExclusive);
            if (branch is > 0) orders = orders.Where(o => o.BranchId == branch.Value);

            var orderIds = orders.Select(o => o.ID);   // a subquery, never a materialised id list
            var basketsInPeriod = await orders.CountAsync(cancellationToken);
            if (basketsInPeriod == 0) return builder.Build(totalRowCount: 0);

            // Which items are worth pairing at all, and how many baskets each is in.
            var itemBaskets = await _db.PosOrderLines.AsNoTracking()
                .Where(l => orderIds.Contains(l.OrderId))
                .GroupBy(l => l.ItemId)
                .Select(g => new { ItemId = g.Key, Baskets = g.Select(x => x.OrderId).Distinct().Count() })
                .OrderByDescending(x => x.Baskets)
                .Take(MaxItems)
                .ToListAsync(cancellationToken);
            if (itemBaskets.Count < 2) return builder.Build(totalRowCount: 0);

            var considered = itemBaskets.Select(x => x.ItemId).ToList();
            var basketCount = itemBaskets.ToDictionary(x => x.ItemId, x => x.Baskets);

            // One row per (basket, item), restricted to the considered items. DISTINCT because a
            // basket holding the same item on two lines is still ONE basket for co-occurrence.
            var membership = await _db.PosOrderLines.AsNoTracking()
                .Where(l => orderIds.Contains(l.OrderId) && considered.Contains(l.ItemId))
                .Select(l => new { l.OrderId, l.ItemId })
                .Distinct()
                .ToListAsync(cancellationToken);

            // The DAY each basket was closed on, so a pair can be counted in days as well as baskets.
            var basketDay = await orders
                .Select(o => new { o.ID, o.ClosedAt })
                .ToDictionaryAsync(x => x.ID, x => x.ClosedAt!.Value.Date, cancellationToken);

            var pairs = new Dictionary<(int A, int B), int>();
            var pairDays = new Dictionary<(int A, int B), HashSet<DateTime>>();
            foreach (var basket in membership.GroupBy(m => m.OrderId))
            {
                var day = basketDay.TryGetValue(basket.Key, out var d) ? d : default;
                var items = basket.Select(m => m.ItemId).Distinct().OrderBy(i => i).ToList();
                for (var i = 0; i < items.Count; i++)
                    for (var j = i + 1; j < items.Count; j++)
                    {
                        var key = (items[i], items[j]);
                        pairs[key] = pairs.TryGetValue(key, out var seen) ? seen + 1 : 1;
                        if (!pairDays.TryGetValue(key, out var days)) pairDays[key] = days = new HashSet<DateTime>();
                        days.Add(day);
                    }
            }

            var kept = pairs.Where(kv => kv.Value >= minBaskets)
                            .OrderByDescending(kv => kv.Value)
                            .ToList();
            if (kept.Count == 0) return builder.Build(totalRowCount: 0);

            // The span of PAID data for this company, not of the selected window - the point is to let
            // a reader tell "nothing sold in your period" from "nothing sold at all".
            var span = await _db.PosOrders.AsNoTracking()
                .Where(o => o.CompanyId == ctx.CompanyId && o.Status == "Paid" && o.ClosedAt != null)
                .GroupBy(o => 1)
                .Select(g => new { From = g.Min(o => o.ClosedAt), To = g.Max(o => o.ClosedAt) })
                .FirstOrDefaultAsync(cancellationToken);

            var names = await _db.Items.AsNoTracking()
                .Where(i => i.CompanyID == ctx.CompanyId && considered.Contains(i.ID))
                .Select(i => new { i.ID, i.Name, i.NameEn })
                .ToListAsync(cancellationToken);
            var nameById = names.ToDictionary(i => i.ID);

            bool ar = System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ar";
            string? Label(int id) => nameById.TryGetValue(id, out var it)
                ? (ar ? it.Name : (string.IsNullOrWhiteSpace(it.NameEn) ? it.Name : it.NameEn))
                : null;

            foreach (var entry in kept)
            {
                var a = entry.Key.A;
                var b = entry.Key.B;
                var together = entry.Value;
                var withA = basketCount.TryGetValue(a, out var ca) ? ca : 0;
                var withB = basketCount.TryGetValue(b, out var cb) ? cb : 0;

                // Attachment is DIRECTIONAL, so it is reported against the rarer of the two: when the
                // less common item is in a basket, how often is the other one too. Measured against
                // the commoner item the number flatters itself.
                var denominator = withA == 0 || withB == 0 ? 0 : Math.Min(withA, withB);

                builder.AddRow(new Dictionary<string, object?>
                {
                    ["ItemA"] = Label(a),
                    ["ItemB"] = Label(b),
                    ["Baskets"] = together,
                    ["BasketsA"] = withA,
                    ["BasketsB"] = withB,
                    ["AttachmentPct"] = denominator == 0 ? (decimal?)null : (decimal)together / denominator * 100m,
                    ["BasketsInPeriod"] = basketsInPeriod,
                    ["PairDays"] = pairDays.TryGetValue(entry.Key, out var daysSeen) ? daysSeen.Count : 0,
                    ["DataFrom"] = span?.From,
                    ["DataTo"] = span?.To,
                });
            }

            return builder.Build();
        }
    }
    public sealed class HyperTopSellersDataSource : IReportDataSource
    {
        private readonly CrossDbContext _db;
        public HyperTopSellersDataSource(CrossDbContext db) { _db = db; }
        public string Key => HyperDatasetCodes.TopSellers;

        public async Task<ReportDataSet> FetchAsync(ReportDataQuery query, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(query);
            var columns = query.RequestedColumns.Count > 0 ? query.RequestedColumns : query.Definition.Columns;
            var builder = new ReportDataSetBuilder(columns);
            var ctx = query.Context;
            if (ctx.CompanyId <= 0) return builder.Build(totalRowCount: 0);   // fail closed on an unresolved tenant

            var from = query.Parameters.GetDate("From");
            var to = query.Parameters.GetDate("To");
            if (from == null || to == null) return builder.Build(totalRowCount: 0);

            // Half-open on the upper bound so a sale rung up at 23:59 on the last day is inside the period.
            // `<= to.Date` would silently drop almost a whole day of trading on the busiest date of a report.
            var fromDay = from.Value.Date;
            var toExclusive = to.Value.Date.AddDays(1);
            var branch = query.Parameters.GetInt(ReportSystemParameters.BranchId);

            // STATUS IS THE GATE. An Open order is not a sale; a Void one was never one; and a merged
            // order's lines now live on the order it was merged into, so counting both double-counts.
            var orders = _db.PosOrders.AsNoTracking()
                .Where(o => o.CompanyId == ctx.CompanyId
                         && o.Status == "Paid"
                         && o.MergedIntoOrderId == null
                         && o.ClosedAt != null && o.ClosedAt >= fromDay && o.ClosedAt < toExclusive);
            if (branch is > 0) orders = orders.Where(o => o.BranchId == branch.Value);

            // Kept as QUERIES, never materialised into id lists. A busy month is tens of thousands of
            // orders and `Contains(aList)` would put every id into the SQL as a parameter — the bounded
            // query-string failure this codebase has already hit once.
            var orderIds = orders.Select(o => o.ID);
            var invoiceIds = orders.Where(o => o.InvoiceId != null).Select(o => o.InvoiceId!.Value);

            var revenue = await _db.PosOrderLines.AsNoTracking()
                .Where(l => orderIds.Contains(l.OrderId))
                .GroupBy(l => l.ItemId)
                .Select(g => new
                {
                    ItemId = g.Key,
                    Revenue = g.Sum(x => x.LineTotal),
                    Orders = g.Select(x => x.OrderId).Distinct().Count(),
                })
                .ToListAsync(cancellationToken);
            if (revenue.Count == 0) return builder.Build(totalRowCount: 0);

            var movements = await _db.StockMovements.AsNoTracking()
                .Where(m => m.CompanyID == ctx.CompanyId
                         && m.SourceType == "SalesInvoice"
                         && m.Direction == -1
                         && m.SourceId != null && invoiceIds.Contains(m.SourceId.Value))
                .GroupBy(m => m.ItemId)
                .Select(g => new { ItemId = g.Key, QtyBase = g.Sum(x => x.QtyBase), Cost = g.Sum(x => x.TotalCost) })
                .ToListAsync(cancellationToken);
            var byItem = movements.ToDictionary(m => m.ItemId);

            var itemIds = revenue.Select(r => r.ItemId).ToList();
            var items = await _db.Items.AsNoTracking()
                .Where(i => i.CompanyID == ctx.CompanyId && itemIds.Contains(i.ID))
                .Select(i => new { i.ID, i.ItemCode, i.Name, i.NameEn, i.ItemType })
                .ToListAsync(cancellationToken);
            var itemById = items.ToDictionary(i => i.ID);

            bool ar = System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ar";

            // Ordered BEFORE the row cap, so truncation keeps the top sellers rather than an arbitrary slice.
            // Quantity first (the question as asked), net sales as the tie-break so two items that moved the
            // same number of units do not come back in storage order.
            var rows = revenue
                .Select(r => new
                {
                    r.ItemId,
                    r.Revenue,
                    r.Orders,
                    Qty = byItem.TryGetValue(r.ItemId, out var m) ? m.QtyBase : (decimal?)null,
                    Cost = byItem.TryGetValue(r.ItemId, out var c) ? c.Cost : (decimal?)null,
                })
                .OrderByDescending(r => r.Qty ?? 0m)
                .ThenByDescending(r => r.Revenue)
                .ToList();

            foreach (var r in rows)
            {
                itemById.TryGetValue(r.ItemId, out var item);
                // NULL, never 0, when no movement backs this item: see the header. A margin invented for a
                // non-stock line is a number a buyer would act on.
                decimal? margin = r.Cost == null ? null : r.Revenue - r.Cost.Value;
                decimal? marginPct = margin == null || r.Revenue == 0m ? null : margin.Value / r.Revenue * 100m;

                builder.AddRow(new Dictionary<string, object?>
                {
                    ["ItemCode"] = item?.ItemCode,
                    ["Item"] = item == null ? null : (ar ? item.Name : (string.IsNullOrWhiteSpace(item.NameEn) ? item.Name : item.NameEn)),
                    ["ItemEn"] = item?.NameEn,
                    ["ItemType"] = item?.ItemType,
                    ["QtySold"] = r.Qty,
                    ["Orders"] = r.Orders,
                    ["Revenue"] = r.Revenue,
                    ["Cost"] = r.Cost,
                    ["Margin"] = margin,
                    ["MarginPct"] = marginPct,
                });
            }

            return builder.Build();
        }
    }
}
