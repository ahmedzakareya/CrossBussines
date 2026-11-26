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
        // ---- SCR-49: the Work subjects, admitted CONDITIONALLY -------------------------------------
        //
        // WHY WORK IS NOT SIMPLY ADDED TO `allowed` AND LEFT THERE. The Work dataset pack is opt-in:
        // Program.cs calls AddWorkReporting() only when "Reporting:Work:Enabled" is true, and that key
        // is absent from every appsettings file. So in every deployment as it stands, work.tasks and
        // work.approvals are NOT in the dataset registry.
        //
        // `datasets.Resolve` below THROWS for an unregistered code. This constructor runs when the DI
        // graph is built, so an unconditional Work KPI would turn a disabled optional pack into an
        // application that cannot start — the same class of failure CLAUDE.md records for Batch C's
        // PermissionScopeStartupValidator, and a far worse outcome than a missing tile.
        //
        // So a Work KPI is DROPPED when its dataset is absent, and kept when it is present.
        //
        // ACCOUNTING AND PROJECTS KEEP THE OLD BEHAVIOUR EXACTLY, and deliberately: they are
        // unconditional packs, so a missing dataset there is a genuine wiring bug that must still fail
        // loudly at graph construction rather than quietly producing a dashboard with fewer tiles.
        // The work KPI key namespace. Declared here beside the code set it pairs with, so the two
        // halves of "is this definition conditional" cannot drift apart.
        const string WorkKpiKeyPrefix = "work.";

        var conditional = new HashSet<string>(
            WorkReportingDatasets.All().Select(d => d.DatasetCode), StringComparer.Ordinal);

        // THE DROP IS A PROPERTY OF THE KPI, NOT OF THE DATASET CODE IT NAMES. Keying this off the
        // DatasetCode alone silently swallowed any KPI that pointed at a work code - including an
        // ACCOUNTING KPI whose DatasetCode was wrong. That is a wiring bug which must still fail
        // loudly, and Invalid_definitions_fail_at_registry_construction caught exactly that case
        // (an accounting KPI mutated to DatasetCode = "work.tasks" stopped throwing).
        //
        // A definition is conditional only when BOTH halves belong to the opt-in pack: its own Key
        // sits in the work namespace AND the dataset it names is one the pack declares. Anything
        // else keeps the old, loud behaviour.
        static bool IsConditional(KpiDefinition k, HashSet<string> codes) =>
            k.Key.StartsWith(WorkKpiKeyPrefix, StringComparison.Ordinal) && codes.Contains(k.DatasetCode);

        var items = definitions
            .Where(k => !IsConditional(k, conditional) || datasets.TryResolve(k.DatasetCode, out _))
            .ToArray();

        var keys = new HashSet<string>(StringComparer.Ordinal);
        var allowed = new HashSet<string>(AccountingDatasets.All().Select(d => d.DatasetCode)
            .Concat(ProjectsDatasets.All().Select(d => d.DatasetCode))
            .Concat(HospitalManagementDatasets.Datasets().Select(d => d.DatasetCode))   // H50: the hospital dashboard
            .Concat(conditional), StringComparer.Ordinal);
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

        // ---- SCR-49: WORK -------------------------------------------------------------------------
        //
        // These are the first approved-dataset metrics over task data, and they exist so the Workspace
        // dashboard can stop sourcing its task tiles from ITaskService. Every one of them REUSES a
        // field the Work datasets already publish; none introduces a formula, a query or a second
        // definition of anything.
        //
        // THE SEMANTICS ARE BORROWED, NOT RESTATED, and that is the point of each WindowSemantics
        // string below: "overdue", "waiting approval" and "on hold" are decided by TaskStatuses,
        // TaskApprovalProjection and TaskLifecycle respectively. The dataset columns ask those
        // authorities; a KPI that re-derived "due < now and not Done" would be a second answer to a
        // question the product has already settled, free to drift from the screen it sits beside.
        //
        // ONE PERMISSION TIER, matching WorkReportPermissions: these are operational counts, not
        // commercial disclosures. A billable or rate-bearing Work metric would need its own key AND
        // its own module action, not room left over in this one.
        yield return new("work.tasks.overdue", "Work", WorkDatasetCodes.Tasks,
            "IsOverdue", ReportAggregate.Count, WorkReportPermissions.View,
            "Canonical TaskStatuses.IsOverdue; due date passed and not Done",
            "Company and the caller's own task read scope; one row per task",
            new[] { ReportFilter.Eq("IsOverdue", "true") });

        yield return new("work.tasks.waiting-approval", "Work", WorkDatasetCodes.Tasks,
            "IsWaitingApproval", ReportAggregate.Count, WorkReportPermissions.View,
            "TaskApprovalProjection's five approval flows; waiting on a decision",
            "Company and the caller's own task read scope; one row per task",
            new[] { ReportFilter.Eq("IsWaitingApproval", "true") });

        // APPROVED, NOT MERELY REQUESTED — TaskLifecycle draws that line and the column honours it. A
        // pending hold REQUEST pauses nothing and its SLA keeps running, so counting it here would
        // report a stopped clock that is still ticking.
        yield return new("work.tasks.on-hold", "Work", WorkDatasetCodes.Tasks,
            "IsOnHold", ReportAggregate.Count, WorkReportPermissions.View,
            "Approved holds only (HoldApprovedAtUtc), not pending hold requests",
            "Company and the caller's own task read scope; one row per task",
            Array.Empty<ReportFilter>());

        // NO FILTER, AND THAT IS NOT AN OMISSION: the work.approvals dataset is "Pending approvals" —
        // it already contains only open work waiting on a decision. Adding a status filter here would
        // narrow a set that is already narrowed, and would couple this KPI to status spellings the
        // dataset deliberately keeps to itself.
        yield return new("work.approvals.pending", "Work", WorkDatasetCodes.Approvals,
            "Status", ReportAggregate.Count, WorkReportPermissions.View,
            "Open work awaiting a decision across the five approval flows",
            "Company and the caller's own approval read scope; one row per pending approval",
            Array.Empty<ReportFilter>());

        // MAX, NOT AVERAGE. An average age hides the one request that has been sitting for three
        // weeks behind a dozen opened this morning; the oldest item is the one an SLA is breached by.
        yield return new("work.approvals.oldest-age-days", "Work", WorkDatasetCodes.Approvals,
            "AgeDays", ReportAggregate.Max, WorkReportPermissions.View,
            "Age in days of the OLDEST pending approval, not the mean",
            "Company and the caller's own approval read scope; days since SubmittedAt",
            Array.Empty<ReportFilter>());

        // H50: the hospital dashboard's tiles over its management reports (HospitalKpis) — last, so the catalogue's
        // first entry stays the accounting KPI its tests mutate.
        foreach (var hospital in HospitalKpis.All()) yield return hospital;
    }
}
