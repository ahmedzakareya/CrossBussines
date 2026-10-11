using CrossBuy.Models.Platform;

namespace CrossBuy.BL.Reporting;

public sealed record KpiDefinition(string Key, string BusinessOwner, string DatasetCode,
    string ValueField, ReportAggregate Aggregate, string RequiredPermissionKey,
    string WindowSemantics, string AggregationBoundary,
    IReadOnlyList<ReportFilter> Filters, string? DateField = null, string? DrillTargetKey = null)
{
    public IReadOnlyDictionary<string, string?> FixedParameters { get; init; } = new Dictionary<string, string?>();
}

public interface IKpiRegistry
{
    IReadOnlyList<KpiDefinition> Definitions { get; }
    KpiDefinition Resolve(string key);
}

// Code-authored only: no new queries, sources, screen, or business formula.
public sealed class KpiRegistry : IKpiRegistry
{
    public IReadOnlyList<KpiDefinition> Definitions { get; }
    public KpiRegistry(IReportDatasetRegistry datasets) : this(datasets, KpiCatalog.All()) { }
    public KpiRegistry(IReportDatasetRegistry datasets, IEnumerable<KpiDefinition> definitions)
    {
        var items = definitions.ToArray();
        var keys = new HashSet<string>(StringComparer.Ordinal);
        var allowed = new HashSet<string>(AccountingDatasets.All().Select(d => d.DatasetCode)
            .Concat(ProjectsDatasets.All().Select(d => d.DatasetCode)), StringComparer.Ordinal);
        foreach (var k in items)
        {
            if (string.IsNullOrWhiteSpace(k.Key) || !keys.Add(k.Key)) throw new ArgumentException("Empty or duplicate KPI key.");
            if (!allowed.Contains(k.DatasetCode)) throw new ArgumentException($"Unsupported KPI dataset: {k.DatasetCode}");
            var d = datasets.Resolve(k.DatasetCode);
            if (string.IsNullOrWhiteSpace(k.BusinessOwner) || string.IsNullOrWhiteSpace(k.RequiredPermissionKey)
                || k.RequiredPermissionKey == ReportPermissions.Public || string.IsNullOrWhiteSpace(k.WindowSemantics)
                || string.IsNullOrWhiteSpace(k.AggregationBoundary)) throw new ArgumentException($"Incomplete KPI: {k.Key}");
            var value = d.Fields.SingleOrDefault(f => f.Key == k.ValueField)
                ?? throw new ArgumentException($"Unknown KPI value field: {k.ValueField}");
            if (!value.SupportedAggregates.Contains(k.Aggregate) || k.Aggregate == ReportAggregate.None
                || value.IsCalculated || value.Sensitivity == ReportFieldSensitivity.Never)
                throw new ArgumentException($"Unsupported KPI aggregate/field: {k.Key}");
            foreach (var f in k.Filters)
            {
                var field = d.Fields.SingleOrDefault(x => x.Key == f.Field);
                if (field == null || !field.Filterable || field.IsCalculated
                    || field.Sensitivity == ReportFieldSensitivity.Never || !field.SupportedOperators.Contains(f.Operator))
                    throw new ArgumentException($"Invalid KPI filter: {k.Key}/{f.Field}");
            }
            if (k.DateField != null && !d.Fields.Any(f => f.Key == k.DateField
                    && f.Type is ReportFieldType.Date or ReportFieldType.DateTime))
                throw new ArgumentException($"Invalid KPI date field: {k.Key}");
            if (k.DrillTargetKey != null && !d.DrillThroughTargets.Any(t => t.Key == k.DrillTargetKey))
                throw new ArgumentException($"Invalid KPI drill target: {k.Key}");
            foreach (var parameter in k.FixedParameters)
            {
                var descriptor = d.Parameters.SingleOrDefault(p => p.Key == parameter.Key);
                if (descriptor == null || descriptor.SystemSupplied)
                    throw new ArgumentException($"Invalid fixed KPI parameter: {k.Key}/{parameter.Key}");
            }
            var shapeDefinition = new ReportDefinition
            {
                Code = k.Key, Module = d.Module, TitleAr = d.TitleAr, TitleEn = d.TitleEn,
                DataSourceKey = d.DataSourceKey, PermissionKey = k.RequiredPermissionKey,
                Columns = d.Fields.Select(f => f.ToColumn()).ToArray()
            };
            if (!new ReportDataShaper().Shape(shapeDefinition, ReportDataSet.Empty(shapeDefinition.Columns),
                    new ReportShapeRequest { Filters = k.Filters }, System.Globalization.CultureInfo.InvariantCulture).IsValid)
                throw new ArgumentException($"Invalid KPI filter value: {k.Key}");
            var parameterDefinition = new ReportDefinition
            {
                Code = k.Key, Module = d.Module, TitleAr = d.TitleAr, TitleEn = d.TitleEn,
                DataSourceKey = d.DataSourceKey, PermissionKey = k.RequiredPermissionKey,
                Columns = shapeDefinition.Columns, Parameters = d.Parameters.Where(p => k.FixedParameters.ContainsKey(p.Key)).ToArray()
            };
            if (!new ReportParameterBinder(new FixedReportClock(DateTime.UnixEpoch))
                    .Bind(parameterDefinition, k.FixedParameters, new BusinessContext { CompanyId = 0 },
                        System.Globalization.CultureInfo.InvariantCulture).IsValid)
                throw new ArgumentException($"Invalid fixed KPI parameter value: {k.Key}");
            // The dataset permission is always required in addition to this key at evaluation.
            // Any extra key narrows access; it cannot replace the dataset authority.
        }
        Definitions = Array.AsReadOnly(items.Select(k => k with
        {
            FixedParameters = new System.Collections.ObjectModel.ReadOnlyDictionary<string, string?>(
                new Dictionary<string, string?>(k.FixedParameters)),
            Filters = Array.AsReadOnly(k.Filters.Select(f => new ReportFilter
            { Field = f.Field, Operator = f.Operator, Values = Array.AsReadOnly(f.Values.ToArray()) }).ToArray())
        }).ToArray());
    }
    public KpiDefinition Resolve(string key) => Definitions.Single(k => k.Key == key);
}

public static class KpiCatalog
{
    public static IEnumerable<KpiDefinition> All()
    {
        yield return new("accounting.posted-sales-base", "Accounting", AccountingDatasetCodes.SalesRevenue,
            "GrandTotalBase", ReportAggregate.Sum, AccountingReportPermissions.View,
            "InvoiceDate within dataset From/To; posted only", "Company functional currency; posted invoice totals including tax",
            Array.Empty<ReportFilter>(), "InvoiceDate")
            { FixedParameters = new Dictionary<string, string?> { ["Status"] = "Posted" } };
        foreach (var field in new[] { "Current", "Days30", "Days60", "Days90Plus", "Total" })
            yield return new($"accounting.receivables-aging.{field}", "Accounting", AccountingDatasetCodes.CustomerAging,
                field, ReportAggregate.Sum, AccountingReportPermissions.View,
                "Dataset AsOf snapshot; invoice age, not payment lateness", "Company; authoritative AgingAsync amounts",
                Array.Empty<ReportFilter>());
        yield return new("accounting.customer-margin", "Accounting", AccountingDatasetCodes.CustomerProfitability,
            "Margin", ReportAggregate.Sum, AccountingReportPermissions.Profitability,
            "Dataset customer analytics snapshot", "Company; authoritative customer analytics amounts",
            Array.Empty<ReportFilter>());
        yield return new("projects.active", "Projects", ProjectsDatasetCodes.Projects,
            "IsActive", ReportAggregate.Count, ProjectsReportPermissions.View,
            "Current visible project register", "Company and resolved project scope; one row per project",
            new[] { ReportFilter.Eq("IsActive", "true") });
        yield return new("projects.milestones", "Projects", ProjectsDatasetCodes.Milestones,
            "Status", ReportAggregate.Count, ProjectsReportPermissions.View,
            "Current visible milestones", "Company and resolved project scope; one row per milestone", Array.Empty<ReportFilter>());
        yield return new("projects.overdue-milestones", "Projects", ProjectsDatasetCodes.Milestones,
            "IsOverdue", ReportAggregate.Count, ProjectsReportPermissions.View,
            "Existing ProjectMilestone.IsOverdue semantics", "Company and resolved project scope; one row per milestone",
            new[] { ReportFilter.Eq("IsOverdue", "true") });
    }
}
