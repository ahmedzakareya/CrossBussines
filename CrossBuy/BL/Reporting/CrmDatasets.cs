using CrossBuy.Models.Context;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL.Reporting
{
    // ============================================================================================
    // Reporting Platform R2 — THE CRM DATASETS.
    //
    // THE DISCIPLINE THIS FILE IS ABOUT: a CRM dataset is where invented KPIs breed. "Conversion rate",
    // "velocity", "win rate", "days in stage" all sound obligatory and none of them are free — each needs a
    // field the source may not have, and a definition somebody has to own.
    //
    // So the rule applied here is narrow and literal: EVERY COLUMN IS A STORED FIELD, or a rendering of one.
    //
    //   Lead.Status and Opportunity.Stage are stored, so "pipeline stage/status" is answered by projecting them
    //   and letting the shaper group and count. That IS the pipeline report, and it needs no new arithmetic.
    //
    //   `Opportunity.Probability` and `Amount` are both stored, so weighted pipeline value is *expressible* —
    //   but it is NOT a column here, because Amount × Probability ÷ 100 is a forecasting convention and CRM has
    //   not declared one. Offering it would mean Reporting picked the convention.
    //
    //   CONVERSION is deliberately NOT a column. `Lead.Status` reaches "Converted" and `Lead.AccountId` /
    //   `CustomerId` fill in on conversion, so a conversion COUNT is available by grouping Status — which is
    //   exactly what the dataset offers. A conversion RATE would need a denominator decision (leads created in
    //   the period? leads touched? excluding Lost?) that belongs to CRM, not here.
    //
    //   ACTIVITY measures are absent for a simpler reason: the source rows carry CreatedAt and an owner, but
    //   there is no per-lead activity/touch count on these entities to measure. Manufacturing one from
    //   CrmTickets would be a different question wearing this dataset's name.
    //
    // What that leaves is two honest datasets that answer the questions the data supports, and a named gap for
    // the ones it does not.
    //
    // COMPANY ISOLATION and AUTHORIZATION: identical to the other two families — context-derived CompanyId,
    // no companyId parameter, empty on an unresolved company, one module key through the single evaluator seam,
    // fail-closed when unmapped.
    // ============================================================================================
    public static class CrmReportPermissions
    {
        // The module's reporting key. Mapped in Program.cs; unmapped means denied.
        public const string View = "crm.reports.view";
    }

    public static class CrmDatasetCodes
    {
        public const string Leads = "Crm.Leads";
        public const string Opportunities = "Crm.Opportunities";

        public const string CategoryKey = "crm.reporting";
    }

    public static class CrmDatasets
    {
        // A lead/opportunity table is small next to a movement register. 25 000 covers a large pipeline and keeps
        // an unfiltered request cheap.
        public const int MaxRows = 25_000;

        private static ReportParameterDescriptor CompanyParam() => new()
        {
            Key = ReportSystemParameters.CompanyId, TitleAr = "الشركة", TitleEn = "Company",
            Type = ReportFieldType.Integer, SystemSupplied = true,
        };

        private static ReportParameterDescriptor OwnerParam() => new()
        {
            Key = "OwnerEmployeeId", TitleAr = "المسؤول", TitleEn = "Owner",
            Type = ReportFieldType.EntityRef, LookupEntityCode = "Employee",
            HelpTextAr = "اتركه فارغًا لكل المسؤولين.", HelpTextEn = "Leave empty for every owner.",
        };

        // ---- Crm.Leads ---------------------------------------------------------------------------
        public static ReportDatasetDefinition Leads() => new()
        {
            DatasetCode = CrmDatasetCodes.Leads,
            Module = "Crm",
            TitleAr = "العملاء المحتملون",
            TitleEn = "Leads",
            DescriptionAr = "العملاء المحتملون: المصدر والتصنيف والحالة والقيمة المتوقعة والمسؤول.",
            DescriptionEn = "Leads with their source, segment, status, estimated value and owner. Group by status "
                          + "for the funnel, or by source to see where they come from.",
            DataSourceKey = CrmDatasetCodes.Leads,
            RequiredPermissionKey = CrmReportPermissions.View,
            MaxRows = MaxRows,
            RowCapPolicy = ReportRowCapPolicy.TruncateAndDeclare,

            Fields = new[]
            {
                new ReportDatasetField
                {
                    Key = "CreatedAt", TitleAr = "تاريخ الإضافة", TitleEn = "Created",
                    Type = ReportFieldType.Date, Format = "yyyy-MM-dd", Groupable = true, WidthMm = 26,
                    SupportedAggregates = new[] { ReportAggregate.Min, ReportAggregate.Max, ReportAggregate.Count },
                },
                new ReportDatasetField
                {
                    Key = "Name", TitleAr = "الاسم", TitleEn = "Name", WidthMm = 52,
                    SupportedAggregates = new[] { ReportAggregate.Count },
                },
                new ReportDatasetField
                {
                    Key = "Company", TitleAr = "الجهة", TitleEn = "Company",
                    Groupable = true, WidthMm = 44,
                    SupportedAggregates = new[] { ReportAggregate.CountDistinct },
                },
                new ReportDatasetField
                {
                    // The funnel column. Grouping on it and counting IS the funnel — no rate invented.
                    Key = "Status", TitleAr = "الحالة", TitleEn = "Status",
                    Groupable = true, WidthMm = 24, SupportedAggregates = new[] { ReportAggregate.Count },
                },
                new ReportDatasetField
                {
                    Key = "Source", TitleAr = "المصدر", TitleEn = "Source",
                    Groupable = true, WidthMm = 28, SupportedAggregates = new[] { ReportAggregate.Count },
                },
                new ReportDatasetField
                {
                    Key = "Segment", TitleAr = "التصنيف", TitleEn = "Segment",
                    Groupable = true, WidthMm = 24, SupportedAggregates = new[] { ReportAggregate.Count },
                },
                new ReportDatasetField
                {
                    Key = "EstimatedValue", TitleAr = "القيمة المتوقعة", TitleEn = "Estimated value",
                    Type = ReportFieldType.Money, Align = ReportAlign.End, WidthMm = 30,
                    SupportedAggregates = new[] { ReportAggregate.Sum, ReportAggregate.Average },
                },
                new ReportDatasetField
                {
                    // Stored, and CRM owns the scoring rules that produce it. Reporting shows the number; it does
                    // not re-score.
                    Key = "Score", TitleAr = "التقييم", TitleEn = "Score",
                    Type = ReportFieldType.Integer, Align = ReportAlign.End, WidthMm = 20,
                    SupportedAggregates = new[] { ReportAggregate.Average, ReportAggregate.Max },
                },
                new ReportDatasetField
                {
                    Key = "OwnerName", TitleAr = "المسؤول", TitleEn = "Owner",
                    Groupable = true, WidthMm = 40,
                    SupportedAggregates = new[] { ReportAggregate.CountDistinct },
                },
                new ReportDatasetField
                {
                    // Present because it is the only honest conversion evidence on the row: a lead that
                    // converted has an account. Hidden by default — it is an id, not a story.
                    Key = "AccountId", TitleAr = "الحساب", TitleEn = "Account",
                    Type = ReportFieldType.Integer, VisibleByDefault = false, WidthMm = 20,
                },
                new ReportDatasetField
                {
                    Key = "CustomerId", TitleAr = "العميل المالي", TitleEn = "Financial customer",
                    Type = ReportFieldType.Integer, VisibleByDefault = false, WidthMm = 22,
                },
                new ReportDatasetField
                {
                    Key = "Phone", TitleAr = "الهاتف", TitleEn = "Phone",
                    VisibleByDefault = false, WidthMm = 28, Sortable = false,
                },
                new ReportDatasetField
                {
                    Key = "Email", TitleAr = "البريد", TitleEn = "Email",
                    VisibleByDefault = false, WidthMm = 40, Sortable = false,
                },
            },

            Parameters = new[]
            {
                new ReportParameterDescriptor
                {
                    Key = "From", TitleAr = "من تاريخ", TitleEn = "From",
                    Type = ReportFieldType.Date, DefaultValue = "month-start",
                    HelpTextAr = "بحسب تاريخ الإضافة.", HelpTextEn = "By creation date.",
                },
                new ReportParameterDescriptor
                {
                    Key = "To", TitleAr = "إلى تاريخ", TitleEn = "To",
                    Type = ReportFieldType.Date, DefaultValue = "today",
                },
                new ReportParameterDescriptor
                {
                    Key = "Status", TitleAr = "الحالة", TitleEn = "Status", AllowMultiple = true,
                    Options = new[]
                    {
                        new ReportParameterOption { Value = "New", LabelAr = "جديد", LabelEn = "New" },
                        new ReportParameterOption { Value = "Contacted", LabelAr = "تم التواصل", LabelEn = "Contacted" },
                        new ReportParameterOption { Value = "Qualified", LabelAr = "مؤهَّل", LabelEn = "Qualified" },
                        new ReportParameterOption { Value = "Converted", LabelAr = "محوَّل", LabelEn = "Converted" },
                        new ReportParameterOption { Value = "Lost", LabelAr = "مفقود", LabelEn = "Lost" },
                    },
                    HelpTextAr = "اتركه فارغًا لكل الحالات.", HelpTextEn = "Leave empty for every status.",
                },
                new ReportParameterDescriptor
                {
                    Key = "Source", TitleAr = "المصدر", TitleEn = "Source", AllowMultiple = true,
                    HelpTextAr = "مثال: Website أو Referral.", HelpTextEn = "e.g. Website or Referral.",
                },
                OwnerParam(),
                CompanyParam(),
            },

            DrillDowns = new[]
            {
                new ReportDatasetDrillDown
                {
                    Key = "by-status", TitleAr = "حسب الحالة", TitleEn = "By status",
                    Levels = new[] { "Status", "Source", "OwnerName" },
                },
                new ReportDatasetDrillDown
                {
                    Key = "by-source", TitleAr = "حسب المصدر", TitleEn = "By source",
                    Levels = new[] { "Source", "Status" },
                },
            },
        };

        // ---- Crm.Opportunities -------------------------------------------------------------------
        public static ReportDatasetDefinition Opportunities() => new()
        {
            DatasetCode = CrmDatasetCodes.Opportunities,
            Module = "Crm",
            TitleAr = "الفرص البيعية",
            TitleEn = "Opportunities",
            DescriptionAr = "الفرص البيعية: المرحلة والقيمة واحتمال الإغلاق وتاريخه المتوقّع والمسؤول.",
            DescriptionEn = "The sales pipeline: stage, amount, probability, expected close date and owner. Group by "
                          + "stage for pipeline value.",
            DataSourceKey = CrmDatasetCodes.Opportunities,
            RequiredPermissionKey = CrmReportPermissions.View,
            MaxRows = MaxRows,
            RowCapPolicy = ReportRowCapPolicy.TruncateAndDeclare,

            Fields = new[]
            {
                new ReportDatasetField
                {
                    Key = "CreatedAt", TitleAr = "تاريخ الإضافة", TitleEn = "Created",
                    Type = ReportFieldType.Date, Format = "yyyy-MM-dd", Groupable = true, WidthMm = 26,
                    SupportedAggregates = new[] { ReportAggregate.Min, ReportAggregate.Max, ReportAggregate.Count },
                },
                new ReportDatasetField
                {
                    Key = "Title", TitleAr = "الفرصة", TitleEn = "Opportunity", WidthMm = 56,
                    SupportedAggregates = new[] { ReportAggregate.Count },
                },
                new ReportDatasetField
                {
                    // The CONFIGURED stage name where the opportunity sits on a pipeline, falling back to the
                    // stored free Stage string when no pipeline stage is set. Both are stored; neither is derived.
                    Key = "StageName", TitleAr = "المرحلة", TitleEn = "Stage",
                    Groupable = true, WidthMm = 30, SupportedAggregates = new[] { ReportAggregate.Count },
                },
                new ReportDatasetField
                {
                    Key = "Amount", TitleAr = "القيمة", TitleEn = "Amount",
                    Type = ReportFieldType.Money, Align = ReportAlign.End, WidthMm = 30,
                    SupportedAggregates = new[] { ReportAggregate.Sum, ReportAggregate.Average },
                },
                new ReportDatasetField
                {
                    // Shown, never multiplied by Amount. Weighted pipeline is a forecasting convention CRM has
                    // not declared — see the file header.
                    Key = "Probability", TitleAr = "الاحتمال %", TitleEn = "Probability %",
                    Type = ReportFieldType.Integer, Align = ReportAlign.End, WidthMm = 22,
                    SupportedAggregates = new[] { ReportAggregate.Average },
                },
                new ReportDatasetField
                {
                    Key = "ExpectedCloseDate", TitleAr = "الإغلاق المتوقّع", TitleEn = "Expected close",
                    Type = ReportFieldType.Date, Format = "yyyy-MM-dd", Groupable = true, WidthMm = 28,
                    SupportedAggregates = new[] { ReportAggregate.Min, ReportAggregate.Max },
                },
                new ReportDatasetField
                {
                    Key = "OwnerName", TitleAr = "المسؤول", TitleEn = "Owner",
                    Groupable = true, WidthMm = 38,
                    SupportedAggregates = new[] { ReportAggregate.CountDistinct },
                },
                new ReportDatasetField
                {
                    // Stored on the row and only meaningful once closed. The reason a deal was lost is the single
                    // most useful free-text field on this entity.
                    Key = "WinLossReason", TitleAr = "سبب الكسب/الخسارة", TitleEn = "Win/loss reason",
                    WidthMm = 50, VisibleByDefault = false, Sortable = false,
                },
                new ReportDatasetField
                {
                    Key = "AccountId", TitleAr = "الحساب", TitleEn = "Account",
                    Type = ReportFieldType.Integer, VisibleByDefault = false, WidthMm = 20,
                },
                new ReportDatasetField
                {
                    Key = "LeadId", TitleAr = "من عميل محتمل", TitleEn = "From lead",
                    Type = ReportFieldType.Integer, VisibleByDefault = false, WidthMm = 22,
                },
                new ReportDatasetField
                {
                    Key = "QuotationId", TitleAr = "عرض السعر", TitleEn = "Quotation",
                    Type = ReportFieldType.Integer, VisibleByDefault = false, WidthMm = 22,
                },
            },

            Parameters = new[]
            {
                new ReportParameterDescriptor
                {
                    Key = "From", TitleAr = "من تاريخ", TitleEn = "From",
                    Type = ReportFieldType.Date, DefaultValue = "month-start",
                    HelpTextAr = "بحسب تاريخ الإضافة.", HelpTextEn = "By creation date.",
                },
                new ReportParameterDescriptor
                {
                    Key = "To", TitleAr = "إلى تاريخ", TitleEn = "To",
                    Type = ReportFieldType.Date, DefaultValue = "today",
                },
                new ReportParameterDescriptor
                {
                    Key = "Stage", TitleAr = "المرحلة", TitleEn = "Stage", AllowMultiple = true,
                    HelpTextAr = "مثال: Proposal أو Won. اتركه فارغًا للكل.",
                    HelpTextEn = "e.g. Proposal or Won. Leave empty for every stage.",
                },
                new ReportParameterDescriptor
                {
                    Key = "PipelineId", TitleAr = "المسار", TitleEn = "Pipeline", Type = ReportFieldType.Integer,
                },
                OwnerParam(),
                CompanyParam(),
            },

            DrillDowns = new[]
            {
                new ReportDatasetDrillDown
                {
                    // Levels must be GROUPABLE fields — the validator enforces it, and it is right to: a
                    // drill-down level is a GROUP BY, and Title is one value per row. Grouping by it would
                    // produce a "hierarchy" with exactly one opportunity per node, which is not a drill-down.
                    Key = "by-stage", TitleAr = "حسب المرحلة", TitleEn = "By stage",
                    Levels = new[] { "StageName", "OwnerName" },
                },
                new ReportDatasetDrillDown
                {
                    Key = "by-owner", TitleAr = "حسب المسؤول", TitleEn = "By owner",
                    Levels = new[] { "OwnerName", "StageName" },
                },
            },
        };

        public static IEnumerable<ReportDatasetDefinition> All()
        {
            yield return Leads();
            yield return Opportunities();
        }
    }

    public sealed class CrmReportDefinitionProvider : IReportDefinitionProvider
    {
        public string ProviderName => "Crm";

        public IEnumerable<ReportDefinition> GetDefinitions()
        {
            yield return Build(CrmDatasets.Leads(), "ki-outline ki-profile-user", "info", 300,
                ReportSort.By("CreatedAt", descending: true));
            yield return Build(CrmDatasets.Opportunities(), "ki-outline ki-chart-pie-simple", "success", 310,
                ReportSort.By("Amount", descending: true));
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
            CategoryKey = CrmDatasetCodes.CategoryKey,
            Tags = new[] { "crm", "sales" },
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
    internal static class CrmSourceHelpers
    {
        // Owner names for a page, resolved once and COMPANY FILTERED. An employee id on a CRM row that pointed
        // at another tenant's employee must not resolve to that person's name.
        internal static async Task<Dictionary<int, string>> OwnerNamesAsync(
            CrossDbContext db, int companyId, IReadOnlyCollection<int> ids, CancellationToken cancellationToken)
        {
            if (ids.Count == 0) return new Dictionary<int, string>();

            var rows = await db.Employee.AsNoTracking()
                .Where(e => e.EmpCompanyID == companyId && ids.Contains(e.ID))
                .Select(e => new { e.ID, e.FullName, e.FullNameEn })
                .ToListAsync(cancellationToken);

            return rows.ToDictionary(e => e.ID, e => e.FullNameEn ?? e.FullName ?? ("#" + e.ID));
        }
    }

    public sealed class CrmLeadsDataSource : IReportDataSource
    {
        private readonly CrossDbContext _db;
        public CrmLeadsDataSource(CrossDbContext db) { _db = db; }

        public string Key => CrmDatasetCodes.Leads;

        public async Task<ReportDataSet> FetchAsync(ReportDataQuery query, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(query);
            var context = query.Context;
            var columns = query.RequestedColumns.Count > 0 ? query.RequestedColumns : query.Definition.Columns;
            var builder = new ReportDataSetBuilder(columns);

            if (context.CompanyId <= 0) return builder.Build(totalRowCount: 0);

            var from = query.Parameters.GetDate("From");
            var to = query.Parameters.GetDate("To");
            var ownerId = query.Parameters.GetInt("OwnerEmployeeId");
            var statuses = StringList(query, "Status");
            var sources = StringList(query, "Source");

            var rows = _db.Leads.AsNoTracking()
                .Where(l => l.CompanyID == context.CompanyId);

            // CreatedAt is nullable on this entity, so a date filter must not silently discard rows that simply
            // have no creation stamp when the user did not ask for a range. It only applies when asked.
            if (from.HasValue) rows = rows.Where(l => l.CreatedAt != null && l.CreatedAt >= from.Value.Date);
            if (to.HasValue)
            {
                var end = AccountingSourceHelpers.ExclusiveEnd(to);
                rows = rows.Where(l => l.CreatedAt != null && l.CreatedAt < end);
            }

            var applied = new List<ReportFilter>();
            if (statuses.Count > 0) rows = rows.Where(l => statuses.Contains(l.Status));
            if (sources.Count > 0) rows = rows.Where(l => l.Source != null && sources.Contains(l.Source));
            if (ownerId is > 0)
            {
                rows = rows.Where(l => l.OwnerEmployeeId == ownerId.Value);
                applied.Add(ReportFilter.Eq("OwnerEmployeeId", ownerId.Value.ToString()));
            }

            // §10 PUSHDOWN: reaches SQL before the cap below.
            rows = ReportFilterPushdown.Apply(rows, query.Filters, applied,
                new Dictionary<string, ReportFilterPushdown.Push<Models.Context.Crm.Lead>>(StringComparer.Ordinal)
                {
                    ["Status"] = (q, op, v) => ReportFilterPushdown.Text(q, l => l.Status, op, v),
                    ["Source"] = (q, op, v) => ReportFilterPushdown.Text(q, l => l.Source, op, v),
                    ["Name"] = (q, op, v) => ReportFilterPushdown.Text(q, l => l.Name, op, v),
                });

            int cap = query.MaxRows > 0 ? query.MaxRows : CrmDatasets.MaxRows;

            var fetched = await rows
                .OrderByDescending(l => l.CreatedAt).ThenByDescending(l => l.ID)
                .Take(cap + 1)
                .Select(l => new
                {
                    l.CreatedAt, l.Name, l.NameEn, l.Company, l.Status, l.Source, l.Segment,
                    l.EstimatedValue, l.Score, l.OwnerEmployeeId, l.AccountId, l.CustomerId, l.Phone, l.Email,
                })
                .ToListAsync(cancellationToken);

            bool truncated = fetched.Count > cap;
            if (truncated) fetched = fetched.Take(cap).ToList();

            bool arabic = AccountingSourceHelpers.Arabic(query);
            var owners = await CrmSourceHelpers.OwnerNamesAsync(_db, context.CompanyId,
                fetched.Where(f => f.OwnerEmployeeId.HasValue).Select(f => f.OwnerEmployeeId!.Value).Distinct().ToList(),
                cancellationToken);

            foreach (var l in fetched)
                builder.AddRow(new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["CreatedAt"] = l.CreatedAt,
                    ["Name"] = AccountingSourceHelpers.Pick(arabic, l.Name, l.NameEn),
                    ["Company"] = l.Company,
                    ["Status"] = l.Status,
                    ["Source"] = l.Source,
                    ["Segment"] = l.Segment,
                    ["EstimatedValue"] = l.EstimatedValue,
                    ["Score"] = l.Score,

                    // An unassigned lead is a FACT worth reading, not a blank cell. A blank reads as missing data;
                    // "Unassigned" reads as the thing a sales manager needs to act on.
                    ["OwnerName"] = l.OwnerEmployeeId.HasValue
                        ? owners.GetValueOrDefault(l.OwnerEmployeeId.Value, "#" + l.OwnerEmployeeId.Value)
                        : (arabic ? "غير مُسند" : "Unassigned"),

                    ["AccountId"] = l.AccountId,
                    ["CustomerId"] = l.CustomerId,
                    ["Phone"] = l.Phone,
                    ["Email"] = l.Email,
                });

            return builder.Build(truncated, truncated ? null : fetched.Count, applied,
                new[] { ReportSort.By("CreatedAt", descending: true) });
        }

        internal static List<string> StringList(ReportDataQuery query, string key) =>
            query.Parameters.GetList(key)
                .Select(v => v as string)
                .Where(v => !string.IsNullOrWhiteSpace(v))
                .Select(v => v!)
                .ToList();
    }

    public sealed class CrmOpportunitiesDataSource : IReportDataSource
    {
        private readonly CrossDbContext _db;
        public CrmOpportunitiesDataSource(CrossDbContext db) { _db = db; }

        public string Key => CrmDatasetCodes.Opportunities;

        public async Task<ReportDataSet> FetchAsync(ReportDataQuery query, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(query);
            var context = query.Context;
            var columns = query.RequestedColumns.Count > 0 ? query.RequestedColumns : query.Definition.Columns;
            var builder = new ReportDataSetBuilder(columns);

            if (context.CompanyId <= 0) return builder.Build(totalRowCount: 0);

            var from = query.Parameters.GetDate("From");
            var to = query.Parameters.GetDate("To");
            var ownerId = query.Parameters.GetInt("OwnerEmployeeId");
            var pipelineId = query.Parameters.GetInt("PipelineId");
            var stages = CrmLeadsDataSource.StringList(query, "Stage");

            var rows = _db.Opportunities.AsNoTracking()
                .Where(o => o.CompanyID == context.CompanyId);

            if (from.HasValue) rows = rows.Where(o => o.CreatedAt != null && o.CreatedAt >= from.Value.Date);
            if (to.HasValue)
            {
                var end = AccountingSourceHelpers.ExclusiveEnd(to);
                rows = rows.Where(o => o.CreatedAt != null && o.CreatedAt < end);
            }

            var applied = new List<ReportFilter>();
            if (stages.Count > 0) rows = rows.Where(o => stages.Contains(o.Stage));
            if (pipelineId is > 0)
            {
                rows = rows.Where(o => o.PipelineId == pipelineId.Value);
                applied.Add(ReportFilter.Eq("PipelineId", pipelineId.Value.ToString()));
            }
            if (ownerId is > 0)
            {
                rows = rows.Where(o => o.OwnerEmployeeId == ownerId.Value);
                applied.Add(ReportFilter.Eq("OwnerEmployeeId", ownerId.Value.ToString()));
            }

            rows = ReportFilterPushdown.Apply(rows, query.Filters, applied,
                new Dictionary<string, ReportFilterPushdown.Push<Models.Context.Crm.Opportunity>>(StringComparer.Ordinal)
                {
                    ["Stage"] = (q, op, v) => ReportFilterPushdown.Text(q, o => o.Stage, op, v),
                    ["Title"] = (q, op, v) => ReportFilterPushdown.Text(q, o => o.Title, op, v),
                    ["PipelineId"] = (q, op, v) => ReportFilterPushdown.Integer(q, o => o.PipelineId, op, v),
                    ["Amount"] = (q, op, v) => ReportFilterPushdown.Number(q, o => o.Amount, op, v),
                });

            int cap = query.MaxRows > 0 ? query.MaxRows : CrmDatasets.MaxRows;

            // ORDERED BY DATE, NOT BY AMOUNT, and the reason is worth stating because the dataset's DEFAULT sort
            // IS by amount:
            //
            //   · SQLite cannot ORDER BY a decimal at all ("SQLite does not support expressions of type
            //     'decimal' in ORDER BY clauses"), so an amount-ordered fetch fails outright in the test suite —
            //     a provider difference that would have hidden every CRM assertion behind an infrastructure error.
            //   · More importantly, the source's job here is to choose WHICH rows the cap keeps, and "the most
            //     recent" is a defensible, provider-portable, deterministic choice. Presentation order is the
            //     SHAPER's job, and the definition's DefaultSorts already say amount-descending.
            //
            // The appliedSorts declaration below is therefore EMPTY: declaring a sort this query did not perform
            // would tell the shaper to skip the sort it is the only one now doing, and the page would come back
            // in date order while the header claimed amount order.
            var fetched = await rows
                .OrderByDescending(o => o.CreatedAt).ThenByDescending(o => o.ID)
                .Take(cap + 1)
                .Select(o => new
                {
                    o.CreatedAt, o.Title, o.TitleEn, o.Stage, o.StageId, o.Amount, o.Probability,
                    o.ExpectedCloseDate, o.OwnerEmployeeId, o.WinLossReason, o.AccountId, o.LeadId, o.QuotationId,
                })
                .ToListAsync(cancellationToken);

            bool truncated = fetched.Count > cap;
            if (truncated) fetched = fetched.Take(cap).ToList();

            bool arabic = AccountingSourceHelpers.Arabic(query);

            var owners = await CrmSourceHelpers.OwnerNamesAsync(_db, context.CompanyId,
                fetched.Where(f => f.OwnerEmployeeId.HasValue).Select(f => f.OwnerEmployeeId!.Value).Distinct().ToList(),
                cancellationToken);

            // Configured stage names, company filtered. Resolved once for the page.
            var stageIds = fetched.Where(f => f.StageId.HasValue).Select(f => f.StageId!.Value).Distinct().ToList();
            var stageNames = stageIds.Count == 0
                ? new Dictionary<int, string>()
                : (await _db.CrmPipelineStages.AsNoTracking()
                        .Where(s => s.CompanyID == context.CompanyId && stageIds.Contains(s.ID))
                        .Select(s => new { s.ID, s.Name, s.NameEn })
                        .ToListAsync(cancellationToken))
                    .ToDictionary(s => s.ID, s => AccountingSourceHelpers.Pick(arabic, s.Name, s.NameEn));

            foreach (var o in fetched)
                builder.AddRow(new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["CreatedAt"] = o.CreatedAt,
                    ["Title"] = AccountingSourceHelpers.Pick(arabic, o.Title, o.TitleEn),

                    // The configured stage wins when the opportunity is on a pipeline; the stored free-text Stage
                    // is the fallback. Both are stored values — nothing here is derived.
                    ["StageName"] = o.StageId.HasValue
                        ? stageNames.GetValueOrDefault(o.StageId.Value, o.Stage)
                        : o.Stage,

                    ["Amount"] = o.Amount,
                    ["Probability"] = o.Probability,
                    ["ExpectedCloseDate"] = o.ExpectedCloseDate,
                    ["OwnerName"] = o.OwnerEmployeeId.HasValue
                        ? owners.GetValueOrDefault(o.OwnerEmployeeId.Value, "#" + o.OwnerEmployeeId.Value)
                        : (arabic ? "غير مُسند" : "Unassigned"),
                    ["WinLossReason"] = o.WinLossReason,
                    ["AccountId"] = o.AccountId,
                    ["LeadId"] = o.LeadId,
                    ["QuotationId"] = o.QuotationId,
                });

            // No applied sort declared — see the fetch above. The shaper applies the definition's amount-descending
            // default, which is what the reader sees.
            return builder.Build(truncated, truncated ? null : fetched.Count, applied);
        }
    }
}
