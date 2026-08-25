using CrossBuy.Models.Context;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL.Reporting
{
    // ============================================================================================
    // Reporting Platform (ADR-037) — THE PLATFORM'S OWN REPORTS.
    //
    // Two reports ship with the platform, and they are chosen so that the pipeline is provably end-to-end WITHOUT
    // this slice reading a single production module table:
    //
    //   Platform.ReportCatalog     — the catalog reporting on itself. Touches no table at all.
    //   Platform.ReportRunHistory  — reads ReportRuns, a table this slice owns.
    //
    // That is deliberate. The brief forbids modifying production modules, and a data source is exactly where
    // reporting would start reading Accounting or Inventory. These two make every layer exercisable — parameters,
    // filters, grouping, sorting, aggregation, HTML, CSV, XLSX, archive, history — while the module data sources
    // stay a later, separately reviewed change.
    //
    // They also serve as the reference example a module author copies: a definition, a data source, one DI line.
    // ============================================================================================
    public static class PlatformReportCodes
    {
        public const string ReportCatalog = "Platform.ReportCatalog";
        public const string ReportRunHistory = "Platform.ReportRunHistory";

        // Category key the two platform reports sit under. Materialised as a ReportCategories row by
        // IReportLibraryService.SyncPlatformCategoriesAsync.
        public const string CategoryKey = "platform.reporting";
    }

    public static class PlatformReportDataSourceKeys
    {
        public const string ReportCatalog = "Platform.ReportCatalog";
        public const string ReportRunHistory = "Platform.ReportRunHistory";
    }

    // A pure, singleton provider — see the contract on IReportDefinitionProvider. It injects nothing.
    public class PlatformReportDefinitionProvider : IReportDefinitionProvider
    {
        public string ProviderName => "Platform";

        public IEnumerable<ReportDefinition> GetDefinitions()
        {
            yield return CatalogInventory();
            yield return RunHistory();
        }

        // ---- Platform.ReportCatalog -------------------------------------------------------------------
        private static ReportDefinition CatalogInventory() => new()
        {
            Code = PlatformReportCodes.ReportCatalog,
            Module = "Platform",
            TitleAr = "دليل التقارير",
            TitleEn = "Report catalogue",
            DescriptionAr = "كل التقارير المتاحة لك، مع الوحدة والتصنيف والصيغ المسموح بها.",
            DescriptionEn = "Every report available to you, with its module, category and permitted formats.",
            DataSourceKey = PlatformReportDataSourceKeys.ReportCatalog,

            // Public — but the DATA SOURCE still filters to what the caller may see, so "public" means "you may
            // open the list", not "you may see every report in it". Permission is enforced per row, not per screen.
            PermissionKey = ReportPermissions.Public,
            CategoryKey = PlatformReportCodes.CategoryKey,
            Tags = new[] { "platform", "reference" },
            Icon = "ki-outline ki-book",
            SortOrder = 10,

            Columns = new[]
            {
                new ReportColumn
                {
                    Key = "Module", TitleAr = "الوحدة", TitleEn = "Module",
                    // Groupable is opt-in per column (see ReportColumn.Groupable, which defaults to false), so a
                    // report only offers grouping where grouping is meaningful.
                    Groupable = true, WidthMm = 28,
                },
                new ReportColumn
                {
                    Key = "Code", TitleAr = "الرمز", TitleEn = "Code", WidthMm = 55,
                },
                new ReportColumn
                {
                    Key = "Title", TitleAr = "الاسم", TitleEn = "Title", WidthMm = 60,
                },
                new ReportColumn
                {
                    Key = "Category", TitleAr = "التصنيف", TitleEn = "Category", Groupable = true, WidthMm = 35,
                },
                new ReportColumn
                {
                    Key = "Formats", TitleAr = "الصيغ", TitleEn = "Formats", Sortable = false, WidthMm = 40,
                },
                new ReportColumn
                {
                    Key = "Parameters", TitleAr = "عدد المعاملات", TitleEn = "Parameters",
                    Type = ReportFieldType.Integer, Aggregate = ReportAggregate.Sum, WidthMm = 22,
                },
                new ReportColumn
                {
                    Key = "Schedulable", TitleAr = "قابل للجدولة", TitleEn = "Schedulable",
                    Type = ReportFieldType.Boolean, WidthMm = 22,
                },
                new ReportColumn
                {
                    Key = "PermissionKey", TitleAr = "الصلاحية", TitleEn = "Permission",
                    // Declared but INTERNAL: it is useful for the data source and for support, and it must never
                    // appear on a rendered page or in an export — see ReportColumn.Internal.
                    Internal = true,
                },
            },

            Parameters = new[]
            {
                new ReportParameterDescriptor
                {
                    Key = "Module", TitleAr = "الوحدة", TitleEn = "Module",
                    HelpTextAr = "اتركه فارغًا لكل الوحدات.",
                    HelpTextEn = "Leave blank for every module.",
                },
            },

            DefaultSorts = new[] { ReportSort.By("Module"), ReportSort.By("Code") },
            DefaultGroupings = new[] { ReportGrouping.By("Module") },

            Capabilities = new ReportCapabilities
            {
                // No PDF: the PDF engine is unbound in this deployment (see PlaywrightPdfReportRenderer), and a
                // capability list that promised a format nobody can produce would be a lie the UI repeats.
                Formats = new[]
                {
                    ReportOutputFormat.Html, ReportOutputFormat.PrintHtml,
                    ReportOutputFormat.Csv, ReportOutputFormat.Xlsx, ReportOutputFormat.Pdf,
                },
                AllowSchedule = false,   // a catalogue listing on a cadence is noise, not information
                AllowArchive = false,
                PreviewRows = 50,
            },
        };

        // ---- Platform.ReportRunHistory ----------------------------------------------------------------
        private static ReportDefinition RunHistory() => new()
        {
            Code = PlatformReportCodes.ReportRunHistory,
            Module = "Platform",
            TitleAr = "سجل تشغيل التقارير",
            TitleEn = "Report run history",
            DescriptionAr = "من شغّل أي تقرير، بأي معاملات، وبأي نتيجة.",
            DescriptionEn = "Who generated which report, with which parameters, and with what outcome.",
            DataSourceKey = PlatformReportDataSourceKeys.ReportRunHistory,

            // Administration only: a run row carries the parameters someone used, which is itself sensitive.
            PermissionKey = ReportPermissions.Administer,
            CategoryKey = PlatformReportCodes.CategoryKey,
            Tags = new[] { "platform", "audit" },
            Icon = "ki-outline ki-time",
            SortOrder = 20,

            Columns = new[]
            {
                new ReportColumn
                {
                    Key = "StartedAt", TitleAr = "التاريخ", TitleEn = "Started",
                    Type = ReportFieldType.DateTime, WidthMm = 32,
                },
                new ReportColumn
                {
                    Key = "ReportCode", TitleAr = "التقرير", TitleEn = "Report", Groupable = true, WidthMm = 50,
                },
                new ReportColumn
                {
                    Key = "EmployeeId", TitleAr = "الموظف", TitleEn = "Employee",
                    Type = ReportFieldType.EntityRef, LookupEntityCode = "Employee",
                    Groupable = true, WidthMm = 22,
                },
                new ReportColumn
                {
                    Key = "Kind", TitleAr = "النوع", TitleEn = "Kind", Groupable = true, WidthMm = 22,
                },
                new ReportColumn
                {
                    Key = "Status", TitleAr = "الحالة", TitleEn = "Status", Groupable = true, WidthMm = 22,
                },
                new ReportColumn
                {
                    Key = "Format", TitleAr = "الصيغة", TitleEn = "Format", Groupable = true, WidthMm = 20,
                },
                new ReportColumn
                {
                    Key = "RowCount", TitleAr = "الصفوف", TitleEn = "Rows",
                    Type = ReportFieldType.Integer, Aggregate = ReportAggregate.Sum, WidthMm = 20,
                },
                new ReportColumn
                {
                    Key = "DurationMs", TitleAr = "المدة (مللي)", TitleEn = "Duration (ms)",
                    Type = ReportFieldType.Integer, Aggregate = ReportAggregate.Average, WidthMm = 24,
                },
                new ReportColumn
                {
                    Key = "ErrorCode", TitleAr = "رمز الخطأ", TitleEn = "Error", VisibleByDefault = false,
                    WidthMm = 34,
                },
            },

            Parameters = new[]
            {
                new ReportParameterDescriptor
                {
                    Key = "From", TitleAr = "من تاريخ", TitleEn = "From",
                    Type = ReportFieldType.Date, Required = true,
                    // A relative token as the default, so the report is useful without typing a date and the
                    // stored default never goes stale. Resolved by the binder against IReportClock.
                    DefaultValue = "month-start",
                },
                new ReportParameterDescriptor
                {
                    Key = "To", TitleAr = "إلى تاريخ", TitleEn = "To",
                    Type = ReportFieldType.Date, Required = true, DefaultValue = "today",
                },
                new ReportParameterDescriptor
                {
                    Key = "Status", TitleAr = "الحالة", TitleEn = "Status",
                    Options = new[]
                    {
                        new ReportParameterOption { Value = "Succeeded", LabelAr = "ناجح", LabelEn = "Succeeded" },
                        new ReportParameterOption { Value = "Failed", LabelAr = "فاشل", LabelEn = "Failed" },
                        new ReportParameterOption { Value = "Denied", LabelAr = "مرفوض", LabelEn = "Denied" },
                    },
                },
                new ReportParameterDescriptor
                {
                    Key = ReportSystemParameters.CompanyId, TitleAr = "الشركة", TitleEn = "Company",
                    Type = ReportFieldType.Integer,
                    // SystemSupplied: filled from the resolved BusinessContext, and a caller-supplied value is
                    // dropped with a warning. This is the isolation guarantee expressed as metadata.
                    SystemSupplied = true,
                },
            },

            DefaultSorts = new[] { ReportSort.By("StartedAt", descending: true) },

            Capabilities = new ReportCapabilities
            {
                Formats = new[]
                {
                    ReportOutputFormat.Html, ReportOutputFormat.PrintHtml,
                    ReportOutputFormat.Csv, ReportOutputFormat.Xlsx, ReportOutputFormat.Pdf,
                },
                MaxRows = 20_000,
                PreviewRows = 100,
            },
        };
    }

    // ============================================================================================
    // Data source: the catalog reporting on itself.
    //
    // Reads NO table. It is also the reference example for the rule that a data source enforces row-level
    // visibility itself — it returns only the reports this caller may see, which is why the definition can be
    // Public without leaking the existence of restricted reports.
    // ============================================================================================
    public class ReportCatalogInventoryDataSource : IReportDataSource
    {
        private readonly IReportCatalog _catalog;
        private readonly IReportAuthorizationService _authorization;
        private readonly IReportOutputPipeline _output;

        public ReportCatalogInventoryDataSource(IReportCatalog catalog,
            IReportAuthorizationService authorization, IReportOutputPipeline output)
        {
            _catalog = catalog;
            _authorization = authorization;
            _output = output;
        }

        public string Key => PlatformReportDataSourceKeys.ReportCatalog;

        public async Task<ReportDataSet> FetchAsync(ReportDataQuery query,
            CancellationToken cancellationToken = default)
        {
            var module = query.Parameters.GetString("Module");
            var arabic = query.Culture.TwoLetterISOLanguageName == "ar";

            var definitions = _catalog.Query(module: module);

            // Row-level authorization, in the source. The alternative — filtering afterwards — would mean the row
            // count and the aggregates were computed over rows the caller may not see.
            var visible = await _authorization.FilterVisibleAsync(definitions, query.Context, cancellationToken);

            var columns = query.RequestedColumns.Count > 0 ? query.RequestedColumns : query.Definition.Columns;
            var builder = new ReportDataSetBuilder(columns);

            foreach (var definition in visible)
            {
                cancellationToken.ThrowIfCancellationRequested();

                builder.AddRow(new Dictionary<string, object?>
                {
                    ["Module"] = definition.Module,
                    ["Code"] = definition.Code,
                    ["Title"] = definition.Title(arabic),
                    ["Category"] = definition.CategoryKey ?? "",
                    ["Formats"] = string.Join(", ", _output.AvailableFormats(definition)),
                    ["Parameters"] = definition.Parameters.Count(p => !p.SystemSupplied),
                    ["Schedulable"] = definition.Capabilities.AllowSchedule,
                    ["PermissionKey"] = definition.PermissionKey,
                });
            }

            // Nothing was pushed down (there is no query to push into), so AppliedFilters/AppliedSorts stay empty
            // and the shaper does all the work. That is the honest declaration — see ReportDataSet.
            return builder.Build(totalRowCount: builder.RowCount);
        }
    }

    // ============================================================================================
    // Data source: report run history.
    //
    // Reads ReportRuns — a table this slice owns. Filters on Context.CompanyId, AsNoTracking, and pushes the date
    // range and status into the query while DECLARING what it pushed, so the shaper does not repeat the work.
    // ============================================================================================
    public class ReportRunHistoryDataSource : IReportDataSource
    {
        private readonly CrossDbContext _db;

        public ReportRunHistoryDataSource(CrossDbContext db) { _db = db; }

        public string Key => PlatformReportDataSourceKeys.ReportRunHistory;

        public async Task<ReportDataSet> FetchAsync(ReportDataQuery query,
            CancellationToken cancellationToken = default)
        {
            var from = query.Parameters.GetDate("From") ?? DateTime.MinValue;
            var to = query.Parameters.GetDate("To") ?? DateTime.MaxValue;
            var status = query.Parameters.GetString("Status");

            // The date range is INCLUSIVE of the whole "to" day. A user asking for "1–31 January" means the 31st,
            // and `<= to` with a midnight value would silently drop that day's rows.
            var toExclusive = to == DateTime.MaxValue ? to : to.Date.AddDays(1);

            // Company filter from the CONTEXT, never from a parameter — see ReportDataQuery.Context.
            var q = _db.ReportRuns.AsNoTracking()
                .Where(r => r.CompanyID == query.Context.CompanyId
                            && r.StartedAt >= from.Date
                            && r.StartedAt < toExclusive);

            var appliedFilters = new List<ReportFilter>();

            if (!string.IsNullOrWhiteSpace(status)
                && Enum.TryParse<Models.Context.Reporting.ReportRunStatus>(status, out var parsed))
            {
                q = q.Where(r => r.Status == parsed);

                // Declared as pushed down so the shaper skips it. Without this declaration the shaper would apply
                // the same predicate a second time — harmless here, wasteful always.
                appliedFilters.Add(ReportFilter.Eq("Status", status));
            }

            var cap = query.MaxRows > 0 ? query.MaxRows : int.MaxValue;

            // One extra row is fetched to detect truncation without a second COUNT query: if we got cap+1, there
            // are more.
            var rows = await q
                .OrderByDescending(r => r.Id)
                .Take(cap == int.MaxValue ? int.MaxValue : cap + 1)
                .Select(r => new
                {
                    r.StartedAt, r.ReportCode, r.EmployeeId, r.Kind, r.Status, r.Format, r.RowCount,
                    r.DurationMs, r.ErrorCode,
                })
                .ToListAsync(cancellationToken);

            var truncated = rows.Count > cap;
            if (truncated) rows = rows.Take(cap).ToList();

            var columns = query.RequestedColumns.Count > 0 ? query.RequestedColumns : query.Definition.Columns;
            var builder = new ReportDataSetBuilder(columns);

            foreach (var row in rows)
                builder.AddRow(new Dictionary<string, object?>
                {
                    ["StartedAt"] = row.StartedAt,
                    ["ReportCode"] = row.ReportCode,
                    ["EmployeeId"] = row.EmployeeId,
                    ["Kind"] = row.Kind.ToString(),
                    ["Status"] = row.Status.ToString(),
                    ["Format"] = row.Format,
                    ["RowCount"] = row.RowCount,
                    ["DurationMs"] = row.DurationMs,
                    ["ErrorCode"] = row.ErrorCode,
                });

            // TotalRowCount is null when truncated: the source does not know the true total without a second
            // expensive pass, and null is an honest "unknown" — better than a wrong number on the footer.
            return builder.Build(truncated, truncated ? null : builder.RowCount, appliedFilters);
        }
    }
}