using CrossBuy.Models.Platform;

namespace CrossBuy.BL.Reporting
{
    // ============================================================================================
    // Reporting Platform (ADR-037 §Dataset) — THE REUSABLE DATASET LAYER.
    //
    // WHY THIS LAYER EXISTS
    //
    // Before it, a ReportDefinition named a DataSourceKey directly, so "the shape of the data" and "this
    // particular report" were the same object. That is fine for a fixed catalogue and fatal for a Report Studio:
    // twelve receivables reports would each redeclare the same twenty columns, the same six parameters and the
    // same permission — and would each drift.
    //
    // A DATASET is the reusable middle. One dataset ("Accounting.Receivables.Aging") declares the fields, the
    // parameters, the security, the filters and the aggregates ONCE; many reports and many Studio-built layouts
    // then select from it. A dataset is to a report what a database view is to a query — except that it is
    // code-first, permission-bearing and version-stamped, and it never emits SQL.
    //
    // THE PIPELINE, and each stage's single responsibility:
    //
    //     ReportDefinition   WHICH report this is: code, title, permission, its chosen dataset, its defaults.
    //            ↓
    //     DatasetDefinition  WHAT data is available: fields, parameters, sensitivity, allowed operations.   ← HERE
    //            ↓
    //     IReportDataSource  FETCHES authorized rows for one dataset. The only stage that touches a module.
    //            ↓
    //     ReportTemplate     HOW it is laid out: chosen columns, order, grouping, page setup. Stored, versioned.
    //            ↓
    //     IReportRenderer    Produces bytes: HTML, PDF, CSV, XLSX.
    //
    // Read that as: the DEFINITION picks a dataset, the DATASET says what is possible, the SOURCE gets the rows,
    // the TEMPLATE arranges them, the RENDERER prints them. A stage may narrow what the previous stage allowed;
    // no stage may widen it. That single rule is what makes the security review in §Security tractable.
    //
    // NOTHING IN THIS FILE CONNECTS A PRODUCTION MODULE. There is no Accounting, Inventory or CRM dataset here —
    // that is the next increment, and deliberately not this one.
    // ============================================================================================

    // --------------------------------------------------------------------------------------------
    // Sensitivity — the classification that drives field-level redaction.
    //
    // A dataset field is not merely visible or hidden: "cost" is readable by a purchasing manager and not by a
    // salesperson, while "IBAN" is readable by almost nobody and must never reach a CSV. One boolean cannot
    // express that, and a per-report ACL would have to be restated on every report over the same data.
    // --------------------------------------------------------------------------------------------
    public enum ReportFieldSensitivity
    {
        // Ordinary business data. Visible to anyone who may run the report.
        Normal = 0,

        // Commercially sensitive: cost, margin, supplier price, credit limit. Requires the field's
        // RequiredPermissionKey in addition to the report's own permission.
        Confidential = 1,

        // Personal or regulated: salary, national id, bank account, medical note. Same gate as Confidential
        // PLUS it is excluded from bulk exports unless the caller additionally holds the export permission —
        // an exported spreadsheet is the format that leaves the building.
        Restricted = 2,

        // Never rendered and never exported, for any caller. A technical key a dataset needs in order to group
        // or join, which has no business meaning on a page. Distinct from ReportColumn.Internal in that it is
        // declared on the DATA, so every report over this dataset inherits the exclusion.
        Never = 3,
    }

    // What a caller may do with the dataset's rows in bulk.
    public enum ReportDatasetExportPolicy
    {
        // Any format the report's capabilities allow.
        Unrestricted = 0,

        // Screen and PDF yes; CSV/XLSX only with the export permission. The default for anything with a
        // Confidential field, because a spreadsheet is a copy that leaves governance behind.
        RequirePermissionForDataExport = 1,

        // Never CSV/XLSX, whatever the caller holds. For datasets whose whole point is on-screen inspection of
        // regulated data.
        NoDataExport = 2,
    }

    // What happens when a dataset produces more rows than its cap.
    public enum ReportRowCapPolicy
    {
        // Return the cap and flag Truncated. The run still succeeds and SAYS it is partial.
        TruncateAndDeclare = 0,

        // Fail the run with a diagnostic telling the caller to narrow the parameters. Correct for anything
        // financial: a silently partial trial balance is a wrong trial balance, and "declared" is not enough
        // when somebody will sign it.
        FailTheRun = 1,
    }

    // --------------------------------------------------------------------------------------------
    // A dataset field. A superset of ReportColumn: everything a column needs, plus what only the DATA can know
    // — where the value comes from, how sensitive it is, and what operations it supports.
    //
    // ReportColumn stays the RENDERING contract; this is the DATA contract. `ToColumn()` projects one to the
    // other, so a report over a dataset does not restate column metadata by hand.
    // --------------------------------------------------------------------------------------------
    public sealed class ReportDatasetField
    {
        // Machine key, frozen for the life of the dataset version. Referenced by templates, filters, sorts,
        // groupings and expressions — renaming one breaks every stored template that named it, which is why
        // renaming is a MAJOR version change (see ReportDatasetVersioning).
        public required string Key { get; init; }

        public required string TitleAr { get; init; }
        public required string TitleEn { get; init; }

        public ReportFieldType Type { get; init; } = ReportFieldType.String;

        public string? Format { get; init; }
        public ReportAlign Align { get; init; } = ReportAlign.Auto;

        public ReportFieldSensitivity Sensitivity { get; init; } = ReportFieldSensitivity.Normal;

        // Additional permission required to SEE this field, beyond the report's own. Only meaningful for
        // Confidential/Restricted. Null on a sensitive field is a configuration error the validator rejects —
        // "sensitive but ungated" is the combination that looks safe and is not.
        public string? RequiredPermissionKey { get; init; }

        // ---- what may be DONE with the field --------------------------------------------------------------
        //
        // These are capabilities of the DATA, not preferences of a report. A computed running balance cannot be
        // filtered server-side however much a template wants to; a text blob cannot be grouped meaningfully.
        public bool Filterable { get; init; } = true;
        public bool Sortable { get; init; } = true;
        public bool Groupable { get; init; }

        // The aggregates this field supports. Empty = none. Summing a percentage or an exchange rate produces a
        // meaningless number, so the dataset says which aggregates make sense rather than letting a UI offer
        // Sum on everything.
        public IReadOnlyList<ReportAggregate> SupportedAggregates { get; init; } = Array.Empty<ReportAggregate>();

        // The subset of operators this field accepts. Empty = the type's natural set (see
        // ReportDatasetOperators.For). Narrowed for a field the source can only match exactly.
        public IReadOnlyList<ReportFilterOperator> SupportedOperators { get; init; } = Array.Empty<ReportFilterOperator>();

        // ---- calculated fields ----------------------------------------------------------------------------

        // Non-null makes this a CALCULATED field: the source does not return it; the shaper evaluates the
        // expression per row. See Stage-Reporting-Expression-Language-Design.md.
        //
        // A calculated field is NOT filterable or sortable server-side by default, because the source cannot
        // push down something it never produced. The validator enforces that pairing rather than trusting the
        // author to remember it.
        public string? Expression { get; init; }

        public bool IsCalculated => !string.IsNullOrWhiteSpace(Expression);

        // Fields this expression reads. Declared rather than parsed so the dataset can be validated at startup
        // without instantiating an evaluator, and so the source knows which underlying fields it must fetch
        // even when the calculated field is the only one displayed.
        public IReadOnlyList<string> DependsOn { get; init; } = Array.Empty<string>();

        // ---- navigation ------------------------------------------------------------------------------------

        // For Type = EntityRef: the IEntityRegistry code, which lets a renderer make the cell a deep link.
        public string? LookupEntityCode { get; init; }

        // Names a ReportDatasetDrillTarget on the parent dataset. Clicking this cell opens that report,
        // parameterised from this row.
        public string? DrillThroughKey { get; init; }

        public bool VisibleByDefault { get; init; } = true;
        public double WidthMm { get; init; }

        // Projection to the rendering contract. Sensitivity Never maps to Internal = true, so a field the data
        // layer forbids is also invisible to every renderer and exporter — the two layers agree by construction
        // rather than by a matching pair of flags somebody has to keep in step.
        public ReportColumn ToColumn() => new()
        {
            Key = Key,
            TitleAr = TitleAr,
            TitleEn = TitleEn,
            Type = Type,
            Format = Format,
            Align = Align,
            WidthMm = WidthMm,
            VisibleByDefault = VisibleByDefault,
            Filterable = Filterable && !IsCalculated,
            Sortable = Sortable && !IsCalculated,
            Groupable = Groupable,
            Aggregate = SupportedAggregates.Count > 0 ? SupportedAggregates[0] : ReportAggregate.None,
            LookupEntityCode = LookupEntityCode,
            Internal = Sensitivity == ReportFieldSensitivity.Never,
        };
    }

    // The operators each field type naturally supports. A dataset may narrow this; it may not widen it.
    public static class ReportDatasetOperators
    {
        private static readonly ReportFilterOperator[] Text =
        {
            ReportFilterOperator.Equals, ReportFilterOperator.NotEquals, ReportFilterOperator.Contains,
            ReportFilterOperator.StartsWith, ReportFilterOperator.In,
            ReportFilterOperator.IsNull, ReportFilterOperator.IsNotNull,
        };

        private static readonly ReportFilterOperator[] Ordered =
        {
            ReportFilterOperator.Equals, ReportFilterOperator.NotEquals,
            ReportFilterOperator.GreaterThan, ReportFilterOperator.GreaterOrEqual,
            ReportFilterOperator.LessThan, ReportFilterOperator.LessOrEqual,
            ReportFilterOperator.Between, ReportFilterOperator.In,
            ReportFilterOperator.IsNull, ReportFilterOperator.IsNotNull,
        };

        private static readonly ReportFilterOperator[] Flag =
        {
            ReportFilterOperator.Equals, ReportFilterOperator.NotEquals,
            ReportFilterOperator.IsNull, ReportFilterOperator.IsNotNull,
        };

        // Contains/StartsWith are absent for EntityRef on purpose: a substring match against an entity id is
        // never what the user meant, and offering it invites a report that half-works.
        private static readonly ReportFilterOperator[] Reference =
        {
            ReportFilterOperator.Equals, ReportFilterOperator.NotEquals, ReportFilterOperator.In,
            ReportFilterOperator.IsNull, ReportFilterOperator.IsNotNull,
        };

        public static IReadOnlyList<ReportFilterOperator> For(ReportFieldType type) => type switch
        {
            ReportFieldType.String => Text,
            ReportFieldType.Boolean => Flag,
            ReportFieldType.EntityRef => Reference,
            _ => Ordered,
        };

        // Is this operator legal for this field? Checks the field's own narrowing first, then the type default.
        public static bool IsAllowed(ReportDatasetField field, ReportFilterOperator op) =>
            field.SupportedOperators.Count > 0
                ? field.SupportedOperators.Contains(op)
                : For(field.Type).Contains(op);
    }

    // --------------------------------------------------------------------------------------------
    // Drill-down and drill-through — two different things that are constantly confused.
    //
    //   DRILL-DOWN     stays in the SAME dataset and expands a level: Region → Branch → Salesperson. It is a
    //                  grouping hierarchy, so it needs no target report.
    //   DRILL-THROUGH  navigates to a DIFFERENT report, carrying values from the clicked row as parameters:
    //                  a customer balance → that customer's statement.
    // --------------------------------------------------------------------------------------------
    public sealed class ReportDatasetDrillDown
    {
        public required string Key { get; init; }
        public required string TitleAr { get; init; }
        public required string TitleEn { get; init; }

        // Grouping levels, outermost first. Every entry must be a Groupable field of this dataset.
        public required IReadOnlyList<string> Levels { get; init; }
    }

    public sealed class ReportDatasetDrillTarget
    {
        public required string Key { get; init; }
        public required string TitleAr { get; init; }
        public required string TitleEn { get; init; }

        // ReportDefinition.Code of the report to open.
        public required string TargetReportCode { get; init; }

        // target parameter key → source field key. The engine reads the clicked row's field and binds it.
        //
        // Deliberately a field-to-parameter MAP and not a free-text URL template: a template would let an author
        // compose a query string, and a query string is where a caller-supplied CompanyId would sneak back in.
        // The target report re-authorizes independently in any case (see §Security invariant 13).
        public required IReadOnlyDictionary<string, string> ParameterMap { get; init; }
    }

    // --------------------------------------------------------------------------------------------
    // Versioning.
    //
    // A dataset is referenced by stored templates, saved Studio reports and schedules, so its shape is a
    // published contract. Two numbers, because the two kinds of change have different blast radii.
    // --------------------------------------------------------------------------------------------
    public sealed class ReportDatasetVersion
    {
        // ADDING a field, widening an operator set, adding a drill target. Stored templates keep working.
        public int Minor { get; init; } = 1;

        // REMOVING or renaming a field, narrowing a type, tightening sensitivity. Stored templates that named
        // the affected field must be migrated or will fail validation. A major bump is a deliberate,
        // owner-approved act.
        public int Major { get; init; } = 1;

        public override string ToString() => $"{Major}.{Minor}";
    }

    // --------------------------------------------------------------------------------------------
    // THE DATASET DEFINITION.
    // --------------------------------------------------------------------------------------------
    public interface IReportDatasetDefinition
    {
        // Frozen dotted code, "<Module>.<Area>.<Dataset>" — e.g. "Accounting.Receivables.Aging". Persisted in
        // saved Studio reports, so it is a published contract and not an internal name.
        string DatasetCode { get; }

        ReportDatasetVersion Version { get; }

        string Module { get; }
        string TitleAr { get; }
        string TitleEn { get; }
        string? DescriptionAr { get; }
        string? DescriptionEn { get; }

        // Resolves the IReportDataSource that fetches for this dataset. A dataset declares the SHAPE; the source
        // performs the READ. They are separate so a module can swap a source implementation — a cached one, a
        // read-replica one — without the dataset contract, and therefore every saved report, changing.
        string DataSourceKey { get; }

        // The permission required to use this dataset AT ALL, evaluated by the same IReportPermissionEvaluator
        // as a report. REQUIRED: a dataset with no key would be data anyone can query, and "forgot to set it"
        // must not be how that happens.
        //
        // A report over this dataset is guarded by BOTH its own PermissionKey and this one. The caller needs
        // both, which is what stops a low-permission report from becoming a window onto high-permission data.
        string RequiredPermissionKey { get; }

        IReadOnlyList<ReportDatasetField> Fields { get; }
        IReadOnlyList<ReportParameterDescriptor> Parameters { get; }

        IReadOnlyList<ReportDatasetDrillDown> DrillDowns { get; }
        IReadOnlyList<ReportDatasetDrillTarget> DrillThroughTargets { get; }

        // Hard ceiling for this dataset, independent of any report over it. The engine takes the MINIMUM of this
        // and the report's own cap — a report may be stricter, never looser.
        int MaxRows { get; }
        ReportRowCapPolicy RowCapPolicy { get; }
        ReportDatasetExportPolicy ExportPolicy { get; }

        // May a Studio-built report select this dataset? False keeps a dataset available to hand-written
        // platform reports while keeping it out of the self-service builder — the right answer for anything
        // whose correct interpretation depends on domain knowledge a builder cannot convey.
        bool AvailableInStudio { get; }

        // May a dashboard widget be built over it? Widgets run unattended and often on a shared screen, so this
        // is a separate decision from Studio availability.
        bool AvailableAsWidget { get; }

        // Non-null marks the dataset deprecated: existing saved reports keep working and the Studio stops
        // offering it. Bilingual guidance toward the replacement.
        string? DeprecationNoticeAr { get; }
        string? DeprecationNoticeEn { get; }
        string? SupersededByDatasetCode { get; }
    }

    // A plain, immutable implementation. A module writes `new ReportDatasetDefinition { … }` rather than a class
    // per dataset — the same code-first, no-ceremony shape ReportDefinition already uses.
    public sealed class ReportDatasetDefinition : IReportDatasetDefinition
    {
        public required string DatasetCode { get; init; }
        public ReportDatasetVersion Version { get; init; } = new();
        public required string Module { get; init; }
        public required string TitleAr { get; init; }
        public required string TitleEn { get; init; }
        public string? DescriptionAr { get; init; }
        public string? DescriptionEn { get; init; }
        public required string DataSourceKey { get; init; }
        public required string RequiredPermissionKey { get; init; }
        public required IReadOnlyList<ReportDatasetField> Fields { get; init; }
        public IReadOnlyList<ReportParameterDescriptor> Parameters { get; init; } = Array.Empty<ReportParameterDescriptor>();
        public IReadOnlyList<ReportDatasetDrillDown> DrillDowns { get; init; } = Array.Empty<ReportDatasetDrillDown>();
        public IReadOnlyList<ReportDatasetDrillTarget> DrillThroughTargets { get; init; } = Array.Empty<ReportDatasetDrillTarget>();
        public int MaxRows { get; init; }
        public ReportRowCapPolicy RowCapPolicy { get; init; } = ReportRowCapPolicy.TruncateAndDeclare;
        public ReportDatasetExportPolicy ExportPolicy { get; init; } = ReportDatasetExportPolicy.Unrestricted;
        public bool AvailableInStudio { get; init; } = true;
        public bool AvailableAsWidget { get; init; } = true;
        public string? DeprecationNoticeAr { get; init; }
        public string? DeprecationNoticeEn { get; init; }
        public string? SupersededByDatasetCode { get; init; }

        public bool IsDeprecated => !string.IsNullOrWhiteSpace(DeprecationNoticeEn)
                                    || !string.IsNullOrWhiteSpace(DeprecationNoticeAr);

        public ReportDatasetField? FindField(string? key) => key == null
            ? null
            : Fields.FirstOrDefault(f => string.Equals(f.Key, key, StringComparison.Ordinal));

        // The fields a caller may actually SEE. Sensitivity Never is dropped for everyone; the gated tiers are
        // dropped unless the caller holds the field's own permission key.
        //
        // `heldPermissions` is a resolved set rather than an evaluator call per field: field-level checks happen
        // once per run over a handful of fields, and threading an async evaluator through a projection would
        // make the shaper's hot path async for no benefit.
        public IEnumerable<ReportDatasetField> VisibleFields(IReadOnlySet<string> heldPermissions) =>
            Fields.Where(f => IsFieldVisible(f, heldPermissions));

        public static bool IsFieldVisible(ReportDatasetField field, IReadOnlySet<string> heldPermissions) =>
            field.Sensitivity switch
            {
                ReportFieldSensitivity.Never => false,
                ReportFieldSensitivity.Normal => true,
                _ => field.RequiredPermissionKey != null && heldPermissions.Contains(field.RequiredPermissionKey),
            };
    }

    // --------------------------------------------------------------------------------------------
    // Registry — the same shape and the same failure behaviour as IReportDataSourceRegistry, deliberately, so
    // there is one mental model for "how does the platform find a pluggable thing".
    // --------------------------------------------------------------------------------------------
    public interface IReportDatasetRegistry
    {
        IReportDatasetDefinition Resolve(string datasetCode);
        bool TryResolve(string? datasetCode, out IReportDatasetDefinition? dataset);
        IReadOnlyCollection<string> RegisteredCodes { get; }

        // Datasets a given caller may build a Studio report over: available, not deprecated, and permitted.
        Task<IReadOnlyList<IReportDatasetDefinition>> ListForStudioAsync(
            BusinessContext context, CancellationToken cancellationToken = default);
    }

    public sealed class ReportDatasetNotRegisteredException : ReportingException
    {
        public ReportDatasetNotRegisteredException(string? datasetCode)
            : base($"Report dataset '{datasetCode ?? "(null)"}' is not registered. A dataset must be added to " +
                   "IReportDatasetRegistry before a report or a Studio layout can select it.")
        { DatasetCode = datasetCode; }

        public string? DatasetCode { get; }
    }

    // --------------------------------------------------------------------------------------------
    // Startup validation.
    //
    // Every rule below is one that would otherwise fail at RUN time, on a user's screen, with a message about
    // an internal key. Checking at graph construction turns each into a wiring error the author sees first.
    // This is the same reasoning Stage1DiWiringTests records: a definition that cannot work should not boot.
    // --------------------------------------------------------------------------------------------
    public static class ReportDatasetValidator
    {
        public static IReadOnlyList<string> Validate(IReportDatasetDefinition dataset)
        {
            var errors = new List<string>();
            var d = dataset;

            if (string.IsNullOrWhiteSpace(d.DatasetCode)) errors.Add("DatasetCode is required.");
            if (string.IsNullOrWhiteSpace(d.DataSourceKey)) errors.Add($"[{d.DatasetCode}] DataSourceKey is required.");
            if (string.IsNullOrWhiteSpace(d.RequiredPermissionKey))
                errors.Add($"[{d.DatasetCode}] RequiredPermissionKey is required — an ungated dataset is data anyone can query.");
            if (d.Fields.Count == 0) errors.Add($"[{d.DatasetCode}] has no fields.");

            var keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var f in d.Fields)
            {
                if (string.IsNullOrWhiteSpace(f.Key)) { errors.Add($"[{d.DatasetCode}] a field has an empty Key."); continue; }
                if (!keys.Add(f.Key)) errors.Add($"[{d.DatasetCode}] duplicate field key '{f.Key}'.");

                // "Sensitive but ungated" is the combination that LOOKS safe and is not — the field is marked
                // Confidential, everybody assumes it is protected, and nothing checks anything.
                if (f.Sensitivity is ReportFieldSensitivity.Confidential or ReportFieldSensitivity.Restricted
                    && string.IsNullOrWhiteSpace(f.RequiredPermissionKey))
                    errors.Add($"[{d.DatasetCode}.{f.Key}] is {f.Sensitivity} but declares no RequiredPermissionKey.");

                // A source cannot push down a column it never produced, and the shaper computes calculated
                // fields AFTER filtering — so a filterable calculated field would silently filter on nulls.
                if (f.IsCalculated && (f.Filterable || f.Sortable))
                    errors.Add($"[{d.DatasetCode}.{f.Key}] is calculated, so it cannot be Filterable or Sortable " +
                               "(the source never returns it; the shaper computes it after filtering).");

                if (f.IsCalculated && f.DependsOn.Count == 0)
                    errors.Add($"[{d.DatasetCode}.{f.Key}] is calculated but declares no DependsOn — the source " +
                               "would not know which underlying fields to fetch.");

                foreach (var dep in f.DependsOn)
                    if (!d.Fields.Any(x => string.Equals(x.Key, dep, StringComparison.Ordinal)))
                        errors.Add($"[{d.DatasetCode}.{f.Key}] depends on unknown field '{dep}'.");

                if (f.Type == ReportFieldType.EntityRef && string.IsNullOrWhiteSpace(f.LookupEntityCode))
                    errors.Add($"[{d.DatasetCode}.{f.Key}] is EntityRef but names no LookupEntityCode.");

                foreach (var op in f.SupportedOperators)
                    if (!ReportDatasetOperators.For(f.Type).Contains(op))
                        errors.Add($"[{d.DatasetCode}.{f.Key}] declares operator {op}, which is not valid for {f.Type}.");
            }

            // A calculated field that (directly or transitively) depends on itself would loop the evaluator.
            foreach (var cycle in FindDependencyCycles(d))
                errors.Add($"[{d.DatasetCode}] calculated-field dependency cycle: {string.Join(" -> ", cycle)}.");

            foreach (var drill in d.DrillDowns)
            {
                if (drill.Levels.Count == 0) errors.Add($"[{d.DatasetCode}] drill-down '{drill.Key}' has no levels.");
                foreach (var level in drill.Levels)
                {
                    var field = d.Fields.FirstOrDefault(x => string.Equals(x.Key, level, StringComparison.Ordinal));
                    if (field == null) errors.Add($"[{d.DatasetCode}] drill-down '{drill.Key}' names unknown field '{level}'.");
                    else if (!field.Groupable) errors.Add($"[{d.DatasetCode}] drill-down '{drill.Key}' level '{level}' is not Groupable.");
                }
            }

            foreach (var target in d.DrillThroughTargets)
            {
                if (string.IsNullOrWhiteSpace(target.TargetReportCode))
                    errors.Add($"[{d.DatasetCode}] drill-through '{target.Key}' names no target report.");
                foreach (var source in target.ParameterMap.Values)
                    if (!d.Fields.Any(x => string.Equals(x.Key, source, StringComparison.Ordinal)))
                        errors.Add($"[{d.DatasetCode}] drill-through '{target.Key}' maps from unknown field '{source}'.");
            }

            // A parameter the ENGINE supplies cannot also be user-facing; SystemSupplied is what makes a
            // caller-supplied CompanyId inexpressible, and a required-but-system parameter is contradictory.
            foreach (var p in d.Parameters)
                if (p.SystemSupplied && p.Required && string.IsNullOrWhiteSpace(p.DefaultValue))
                    errors.Add($"[{d.DatasetCode}.{p.Key}] is SystemSupplied and Required with no default — " +
                               "the user cannot supply it and the engine has nothing to fall back on.");

            if (d.MaxRows < 0) errors.Add($"[{d.DatasetCode}] MaxRows cannot be negative.");

            if (d.SupersededByDatasetCode != null && !((ReportDatasetDefinition)d).IsDeprecated)
                errors.Add($"[{d.DatasetCode}] names a superseding dataset but carries no deprecation notice.");

            return errors;
        }

        // Iterative DFS with an explicit stack — a recursive walk would stack-overflow on the very input this
        // check exists to catch.
        private static IEnumerable<IReadOnlyList<string>> FindDependencyCycles(IReportDatasetDefinition dataset)
        {
            var edges = dataset.Fields
                .Where(f => f.IsCalculated)
                .ToDictionary(f => f.Key, f => f.DependsOn, StringComparer.Ordinal);

            var cycles = new List<IReadOnlyList<string>>();
            var settled = new HashSet<string>(StringComparer.Ordinal);

            foreach (var start in edges.Keys)
            {
                if (settled.Contains(start)) continue;

                var path = new List<string>();
                var onPath = new HashSet<string>(StringComparer.Ordinal);
                var stack = new Stack<(string Node, int Depth)>();
                stack.Push((start, 0));

                while (stack.Count > 0)
                {
                    var (node, depth) = stack.Pop();
                    while (path.Count > depth) { onPath.Remove(path[^1]); path.RemoveAt(path.Count - 1); }

                    if (!onPath.Add(node))
                    {
                        cycles.Add(path.Concat(new[] { node }).ToList());
                        continue;
                    }
                    path.Add(node);
                    settled.Add(node);

                    if (!edges.TryGetValue(node, out var deps)) continue;
                    foreach (var dep in deps)
                        if (edges.ContainsKey(dep)) stack.Push((dep, path.Count));
                }
            }
            return cycles;
        }
    }
}
