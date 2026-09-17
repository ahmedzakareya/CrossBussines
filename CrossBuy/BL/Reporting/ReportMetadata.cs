namespace CrossBuy.BL.Reporting
{
    // ============================================================================================
    // Reporting Platform (ADR-037) — REPORT METADATA.
    //
    // A ReportDefinition is the contract for one report: its frozen code, the data source that feeds it, the
    // permission that guards it, and the columns and parameters that exist. It is CODE-FIRST and immutable at
    // runtime — the same discipline IEntityRegistry applies to entity codes, and for the same reason: a report
    // whose shape can be edited in the database is a report whose permission and data contract can be edited
    // in the database.
    //
    // The database side of the platform (ReportTemplates) may only choose among what a definition declares.
    // ============================================================================================

    // The value kinds a report column or parameter can hold. Deliberately a small closed set: every one of
    // these has a defined text form (for filters/parameters/URLs), a defined comparison, and a defined
    // presentation. A free-form "object" type would break all three.
    public enum ReportFieldType
    {
        String = 0,
        Integer = 1,
        Decimal = 2,

        // A monetary amount. Distinct from Decimal because it is right-aligned, thousand-separated and rendered
        // with the report's currency scale.
        //
        // RULE (ADR-037 §Numbers): the reporting platform never ROUNDS a money value — it formats what the data
        // source gave it. Rounding is a business decision owned by ICurrencyRounding at the point the number is
        // computed. A report that re-rounded would produce totals that disagree with the ledger.
        Money = 3,

        Date = 4,
        DateTime = 5,
        Boolean = 6,

        // A percentage already expressed as a percentage (12.5 means 12.5%), not a ratio. Stated because the
        // two conventions silently differ by 100x.
        Percent = 7,

        // A reference to a business record. LookupEntityCode names the IEntityRegistry code, which lets a
        // renderer turn the cell into a deep link and a parameter into a record picker — without the reporting
        // platform knowing anything about the entity itself.
        EntityRef = 8,
    }

    // Column-level aggregation for group subtotals and grand totals.
    public enum ReportAggregate
    {
        None = 0,
        Sum = 1,
        Average = 2,
        Min = 3,
        Max = 4,
        Count = 5,

        // Counts distinct non-null values. Useful for "how many customers" on a line-level report, where Count
        // would count lines.
        CountDistinct = 6,
    }

    public enum ReportAlign
    {
        // Resolved from the field type: numbers/dates end-aligned, text start-aligned. The default, so a
        // definition does not have to restate the obvious for every column.
        Auto = 0,
        Start = 1,
        Center = 2,
        End = 3,
    }

    // One declared column of a report.
    public sealed class ReportColumn
    {
        // Machine key. This is what filters, sorts, groupings and layouts reference, and what a data source's
        // row dictionary is keyed by. Frozen once the report ships — renaming one breaks stored templates.
        public required string Key { get; init; }

        public required string TitleAr { get; init; }
        public required string TitleEn { get; init; }

        public ReportFieldType Type { get; init; } = ReportFieldType.String;

        // .NET format string applied to the value ("N2", "yyyy-MM-dd", "P1"). null = the type's default.
        public string? Format { get; init; }

        public ReportAlign Align { get; init; } = ReportAlign.Auto;

        // Column width hint in millimetres for paged output; 0 = let the renderer decide.
        public double WidthMm { get; init; }

        // false = declared but hidden by default. A layout can turn it on; that is how a report ships with
        // optional detail columns without cluttering the default view.
        public bool VisibleByDefault { get; init; } = true;

        public bool Filterable { get; init; } = true;
        public bool Sortable { get; init; } = true;
        public bool Groupable { get; init; }

        public ReportAggregate Aggregate { get; init; } = ReportAggregate.None;

        // Set only for Type = EntityRef — the IEntityRegistry code of the referenced entity.
        public string? LookupEntityCode { get; init; }

        // Columns the user must never see even if a layout names them (an internal id a data source needs for
        // grouping, a cost a viewer is not entitled to). Excluded from rendering AND from exports, because an
        // export that includes a column the screen hides is the classic reporting data leak.
        public bool Internal { get; init; }

        public ReportAlign EffectiveAlign => Align != ReportAlign.Auto
            ? Align
            : Type switch
            {
                ReportFieldType.Integer or ReportFieldType.Decimal or ReportFieldType.Money or ReportFieldType.Percent => ReportAlign.End,
                ReportFieldType.Date or ReportFieldType.DateTime => ReportAlign.Center,
                ReportFieldType.Boolean => ReportAlign.Center,
                _ => ReportAlign.Start,
            };

        public bool IsNumeric => Type is ReportFieldType.Integer or ReportFieldType.Decimal
            or ReportFieldType.Money or ReportFieldType.Percent;
    }

    // One declared parameter of a report — the questions the report asks before it can run.
    public sealed class ReportParameterDescriptor
    {
        public required string Key { get; init; }
        public required string TitleAr { get; init; }
        public required string TitleEn { get; init; }

        public ReportFieldType Type { get; init; } = ReportFieldType.String;

        // A required parameter with no default and no supplied value FAILS the run. It does not fall back to a
        // "sensible" value: a trial balance silently defaulting to "this month" is how a report gets signed for
        // the wrong period.
        public bool Required { get; init; }

        // Text form of the default, in the same notation a request supplies. Kept as text so the default goes
        // through exactly the same binder as user input and cannot bypass its validation.
        public string? DefaultValue { get; init; }

        // Accepts a comma-separated list. Bound to a value list rather than a scalar.
        public bool AllowMultiple { get; init; }

        // Closed value set (key → bilingual label). When set, a value outside it is rejected.
        public IReadOnlyList<ReportParameterOption> Options { get; init; } = Array.Empty<ReportParameterOption>();

        // For EntityRef parameters — the IEntityRegistry code to pick from.
        public string? LookupEntityCode { get; init; }

        // Inclusive numeric/date bounds, in text form. Checked by the binder.
        public string? MinValue { get; init; }
        public string? MaxValue { get; init; }

        // A parameter the ENGINE supplies, not the user: CompanyId, EmployeeId, BranchId, Culture, AsOfDate.
        // Marked so the UI never renders an input for it and — more importantly — so a request-supplied value
        // for it is IGNORED. A caller-supplied CompanyId is exactly the compatibility hole CLAUDE.md warns
        // about; here it cannot even be expressed.
        public bool SystemSupplied { get; init; }

        public string? HelpTextAr { get; init; }
        public string? HelpTextEn { get; init; }
    }

    public sealed class ReportParameterOption
    {
        public required string Value { get; init; }
        public required string LabelAr { get; init; }
        public required string LabelEn { get; init; }
    }

    // What a report is allowed to do. The engine enforces every one of these; a definition cannot be a
    // suggestion.
    public sealed class ReportCapabilities
    {
        // Formats this report may be produced in. A report with heavy per-row detail can refuse Pdf; a
        // cross-tab can refuse Csv. Empty = every registered format.
        public IReadOnlyList<ReportOutputFormat> Formats { get; init; } = Array.Empty<ReportOutputFormat>();

        public bool AllowSchedule { get; init; } = true;
        public bool AllowArchive { get; init; } = true;
        public bool AllowShare { get; init; } = true;

        // Which template scopes may exist for this report. A statutory report can forbid Personal templates so
        // nobody files a VAT return through a layout they invented.
        public IReadOnlyList<Models.Context.Reporting.ReportTemplateScope> TemplateScopes { get; init; } =
            Array.Empty<Models.Context.Reporting.ReportTemplateScope>();

        // Hard row ceiling for a full run. 0 = the engine default. A report that would exceed it fails with a
        // diagnostic rather than truncating: a silently truncated report is a wrong report.
        public int MaxRows { get; init; }

        // Row ceiling for a preview render. Previews truncate on purpose and say so.
        public int PreviewRows { get; init; } = 100;

        public static ReportCapabilities Default { get; } = new();

        public bool SupportsFormat(ReportOutputFormat format) =>
            Formats.Count == 0 || Formats.Contains(format);

        public bool SupportsScope(Models.Context.Reporting.ReportTemplateScope scope) =>
            TemplateScopes.Count == 0 || TemplateScopes.Contains(scope);
    }

    // ============================================================================================
    // The definition itself.
    // ============================================================================================
    public sealed class ReportDefinition
    {
        // Frozen dotted code, "<Module>.<Report>", e.g. "Accounting.TrialBalance". Case-sensitive, unique
        // across the catalog, and persisted in ReportTemplates/ReportRuns/ReportSchedules — so it is a
        // published contract, not an internal name.
        public required string Code { get; init; }

        // Owning functional module, matching the vocabulary IEntityRegistry already uses (Accounting |
        // Inventory | Manufacturing | Pos | Hr | Projects | Crm | Platform).
        public required string Module { get; init; }

        public required string TitleAr { get; init; }
        public required string TitleEn { get; init; }

        public string? DescriptionAr { get; init; }
        public string? DescriptionEn { get; init; }

        // Resolves the IReportDataSource that feeds this report. A key rather than a type so a module can
        // replace its own data source without the catalog entry changing.
        public required string DataSourceKey { get; init; }

        // The permission the caller must hold, evaluated by IReportPermissionEvaluator. REQUIRED — a definition
        // with no permission key would be a report anyone can run, and "forgot to set it" must not be the way
        // that happens. Use ReportPermissions.Public explicitly for a genuinely unrestricted report.
        public required string PermissionKey { get; init; }

        // ReportCategories.Key of the folder this report appears in. null = uncategorised.
        public string? CategoryKey { get; init; }

        public IReadOnlyList<string> Tags { get; init; } = Array.Empty<string>();

        public required IReadOnlyList<ReportColumn> Columns { get; init; }
        public IReadOnlyList<ReportParameterDescriptor> Parameters { get; init; } = Array.Empty<ReportParameterDescriptor>();

        public IReadOnlyList<ReportSort> DefaultSorts { get; init; } = Array.Empty<ReportSort>();
        public IReadOnlyList<ReportGrouping> DefaultGroupings { get; init; } = Array.Empty<ReportGrouping>();
        public IReadOnlyList<ReportFilter> DefaultFilters { get; init; } = Array.Empty<ReportFilter>();

        public ReportOutputFormat DefaultFormat { get; init; } = ReportOutputFormat.Html;
        public ReportPageSetup DefaultPageSetup { get; init; } = ReportPageSetup.Default;
        public ReportCapabilities Capabilities { get; init; } = ReportCapabilities.Default;

        // Bumped by the module owner when Columns or Parameters change incompatibly. Recorded on every run so
        // an old archived artifact can be explained ("this ran against definition v1, which had no VAT column").
        public int DefinitionVersion { get; init; } = 1;

        // Metronic KI icon class + contextual colour token, for the report browser. Presentation only.
        public string Icon { get; init; } = "ki-outline ki-chart-simple";
        public string Color { get; init; } = "primary";

        // Display order inside its category.
        public int SortOrder { get; init; }


        // ---- lookups the engine and shaper use constantly -------------------------------------------------

        public ReportColumn? FindColumn(string? key) => key == null
            ? null
            : Columns.FirstOrDefault(c => string.Equals(c.Key, key, StringComparison.Ordinal));

        public ReportParameterDescriptor? FindParameter(string? key) => key == null
            ? null
            : Parameters.FirstOrDefault(p => string.Equals(p.Key, key, StringComparison.Ordinal));

        public IEnumerable<ReportColumn> DefaultVisibleColumns =>
            Columns.Where(c => c.VisibleByDefault && !c.Internal);

        public string Title(bool arabic) => arabic ? TitleAr : TitleEn;
    }

    // Well-known permission keys owned by the reporting platform itself.
    //
    // A MODULE report uses its own module's key (e.g. "accounting.reports.view"), resolved by the pluggable
    // evaluator. These two exist so the platform's own reports do not have to borrow a module's permission.
    public static class ReportPermissions
    {
        // Any authenticated caller with a resolved BusinessContext. Still not "anonymous": there is no
        // anonymous report.
        public const string Public = "reporting.public";

        // Administering the reporting platform (platform templates, categories, other people's schedules).
        public const string Administer = "reporting.administer";

        // AUTHORING A DERIVED DATASET — narrowing a code-authored dataset into a new named one.
        //
        // ITS OWN KEY, NOT Administer AND NOT A MODULE'S View KEY. Two reasons, and both are about what
        // the holder can do rather than what they can see:
        //
        //   · It is not "run a report". An author shapes what EVERYONE ELSE in the company is offered in
        //     the Studio, which is an administrative act even though the result grants no new access.
        //   · It is not Administer either. Administer reaches other people's schedules and platform
        //     templates; a company wanting someone to define report shapes should not have to hand over
        //     the rest of the platform to do it.
        //
        // The key grants NO data. A derived dataset inherits its parent's permission, so an author who
        // holds this and nothing else can define a shape they are still refused at run time.
        public const string AuthorDatasets = "reporting.datasets.author";
    }
}