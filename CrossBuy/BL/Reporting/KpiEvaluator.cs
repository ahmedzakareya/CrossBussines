using System.Globalization;
using CrossBuy.BL.Platform;
using CrossBuy.Models.Platform;

namespace CrossBuy.BL.Reporting;

public sealed record KpiResult(string Key, string Status, decimal? Value, int CompanyId, DateTime EvaluatedAt);
public interface IKpiEvaluator
{
    Task<KpiResult> EvaluateAsync(string key, IReadOnlyDictionary<string, string?> parameters, CancellationToken ct = default);
}

// Reuses the dataset permission evaluator, parameter binder, data sources and shaper.
// A partial fetch never becomes a plausible but incomplete management number.
public sealed class KpiEvaluator(IKpiRegistry registry, IReportDatasetRegistry datasets,
    IReportDataSourceRegistry sources, IReportPermissionEvaluator permissions,
    IReportParameterBinder binder, IReportDataShaper shaper, IBusinessContextAccessor contexts,
    IReportClock clock) : IKpiEvaluator
{
    public async Task<KpiResult> EvaluateAsync(string key, IReadOnlyDictionary<string, string?> parameters, CancellationToken ct = default)
    {
        var context = await contexts.TryGetCurrentAsync(ct);
        KpiResult Result(string status, decimal? value = null)
            => new(key, status, value, context?.CompanyId ?? 0, clock.UtcNow);
        if (context == null || context.CompanyId <= 0 || context.EmployeeId is not > 0 || context.IsExternalPortal)
            return Result("DENIED");
        var k = registry.Resolve(key);
        var d = datasets.Resolve(k.DatasetCode);
        foreach (var permission in new[] { d.RequiredPermissionKey, k.RequiredPermissionKey }.Distinct())
            if (!await permissions.HasPermissionAsync(permission, context, ct)) return Result("DENIED");

        var names = new[] { k.ValueField }.Concat(k.Filters.Select(f => f.Field)).Distinct().ToArray();
        var fields = names.Select(n => d.Fields.Single(f => f.Key == n)).ToArray();
        foreach (var field in fields)
        {
            if (field.Sensitivity == ReportFieldSensitivity.Never) return Result("DENIED");
            if (field.Sensitivity != ReportFieldSensitivity.Normal
                && (string.IsNullOrWhiteSpace(field.RequiredPermissionKey)
                    || !await permissions.HasPermissionAsync(field.RequiredPermissionKey, context, ct))) return Result("DENIED");
        }
        var columns = fields.Select(f => new ReportColumn
        {
            Key = f.Key, TitleAr = f.TitleAr, TitleEn = f.TitleEn, Type = f.Type, Filterable = f.Filterable,
            Aggregate = f.Key == k.ValueField ? k.Aggregate : ReportAggregate.None,
            Sensitivity = f.Sensitivity, RequiredPermissionKey = f.RequiredPermissionKey
        }).ToArray();
        var definition = new ReportDefinition
        {
            Code = k.Key, Module = d.Module, TitleAr = d.TitleAr, TitleEn = d.TitleEn,
            DataSourceKey = d.DataSourceKey, PermissionKey = k.RequiredPermissionKey,
            Columns = columns, Parameters = d.Parameters
        };
        var supplied = new Dictionary<string, string?>(parameters);
        foreach (var fixedParameter in k.FixedParameters) supplied[fixedParameter.Key] = fixedParameter.Value;
        var bound = binder.Bind(definition, supplied, context, CultureInfo.InvariantCulture);
        if (!bound.IsValid) return Result("INVALID_PARAMETERS");
        var data = await sources.Resolve(d.DataSourceKey).FetchAsync(new ReportDataQuery
        {
            Definition = definition, Context = context, Parameters = bound.Parameters!,
            RequestedColumns = columns, Filters = k.Filters, MaxRows = d.MaxRows,
            Culture = CultureInfo.InvariantCulture
        }, ct);
        if (data.Truncated || d.MaxRows > 0 && data.Rows.Count > d.MaxRows) return Result("TRUNCATED");
        if (names.Any(n => !data.Columns.Any(c => c.Key == n))) return Result("INVALID_SOURCE");
        if (k.Aggregate == ReportAggregate.Sum && data.Rows.Any(row => row[k.ValueField] == null))
            return Result("INVALID_SOURCE");
        var shaped = shaper.Shape(definition, data, new ReportShapeRequest
        { Filters = k.Filters, VisibleColumns = names, MaxRows = d.MaxRows }, CultureInfo.InvariantCulture);
        if (!shaped.IsValid || shaped.View!.Truncated) return Result("INVALID_SOURCE");
        return Result("READY", shaped.View.GrandTotals.Single(a => a.ColumnKey == k.ValueField).Value);
    }
}
