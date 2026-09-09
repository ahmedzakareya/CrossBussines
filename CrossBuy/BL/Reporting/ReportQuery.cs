using System.Text.Json.Serialization;

namespace CrossBuy.BL.Reporting
{
    // ============================================================================================
    // Reporting Platform (ADR-037) — QUERY INTENT value objects.
    //
    // These describe WHAT a caller wants to see. They are pure data: immutable, serializable, and free of any
    // provider, SQL or EF concept. That is not tidiness — it is the reason a saved template can be replayed
    // months later against a data source that has since been rewritten, and the reason the shaper can be
    // unit-tested without a database.
    //
    // SAFETY RULE, enforced by ReportDataShaper and the parameter binder: every `Field` here is matched against
    // the report definition's declared columns. A field the definition does not declare is REJECTED. No string
    // from a filter, sort or grouping ever reaches a query builder as an identifier, so this layer cannot be a
    // SQL-injection surface even when a data source is SQL-backed.
    // ============================================================================================

    public enum ReportFilterOperator
    {
        Equals = 0,
        NotEquals = 1,
        GreaterThan = 2,
        GreaterOrEqual = 3,
        LessThan = 4,
        LessOrEqual = 5,
        Contains = 6,
        StartsWith = 7,

        // Two values, inclusive on both ends. Inclusive because every accounting date range a user types
        // ("1 Jan to 31 Jan") means both endpoints, and a half-open range silently drops the last day.
        Between = 8,

        In = 9,
        IsNull = 10,
        IsNotNull = 11,
    }

    // One filter condition. Values are TEXT, converted by the shaper using the column's declared type — the
    // same conversion the parameter binder uses, so "2026-01-31" means the same thing in a filter and in a
    // parameter.
    public sealed class ReportFilter
    {
        public required string Field { get; init; }
        public ReportFilterOperator Operator { get; init; } = ReportFilterOperator.Equals;
        public IReadOnlyList<string?> Values { get; init; } = Array.Empty<string?>();

        [JsonIgnore]
        public string? FirstValue => Values.Count > 0 ? Values[0] : null;

        public static ReportFilter Eq(string field, string? value) =>
            new() { Field = field, Operator = ReportFilterOperator.Equals, Values = new[] { value } };

        public static ReportFilter Between(string field, string? from, string? to) =>
            new() { Field = field, Operator = ReportFilterOperator.Between, Values = new[] { from, to } };

        public static ReportFilter In(string field, params string?[] values) =>
            new() { Field = field, Operator = ReportFilterOperator.In, Values = values };
    }

    // One sort key. Applied in list order, so [Branch asc, Total desc] means "by branch, biggest first inside".
    public sealed class ReportSort
    {
        public required string Field { get; init; }
        public bool Descending { get; init; }

        public static ReportSort By(string field, bool descending = false) =>
            new() { Field = field, Descending = descending };
    }

    // One grouping level. Applied in list order — [Branch, Category] gives branch bands containing category
    // bands. Subtotals are per level, so a caller can group without asking for the subtotal rows.
    public sealed class ReportGrouping
    {
        public required string Field { get; init; }
        public bool Descending { get; init; }
        public bool IncludeSubtotals { get; init; } = true;

        // Collapsed in the rendered output (the group header shows, the detail rows are hidden). Presentation
        // intent, honoured by renderers that can express it and ignored by the ones that cannot (CSV).
        public bool Collapsed { get; init; }

        public static ReportGrouping By(string field, bool includeSubtotals = true) =>
            new() { Field = field, IncludeSubtotals = includeSubtotals };
    }

    public enum ReportPageSize
    {
        A4 = 0,
        A5 = 1,
        Letter = 2,
        Legal = 3,

        // 80 mm continuous roll — the POS/thermal receipt profile. Present in the page model because the
        // hypermarket lane prints receipts through the same abstraction; the renderer decides how to honour it.
        Thermal80 = 4,
    }

    public enum ReportOrientation
    {
        Portrait = 0,
        Landscape = 1,
    }

    // Page geometry and direction for paged output (PDF, print). Ignored by data exports (CSV/XLSX), which have
    // no pages — the pipeline does not pretend otherwise.
    public sealed class ReportPageSetup
    {
        public ReportPageSize PageSize { get; init; } = ReportPageSize.A4;
        public ReportOrientation Orientation { get; init; } = ReportOrientation.Portrait;

        // Millimetres. Defaults leave room for the running header/footer.
        public double MarginTopMm { get; init; } = 18;
        public double MarginBottomMm { get; init; } = 16;
        public double MarginLeftMm { get; init; } = 12;
        public double MarginRightMm { get; init; } = 12;

        // true = right-to-left layout. Defaults to true: the product's primary language is Arabic and every
        // existing report screen is RTL, so RTL is the default rather than the option.
        public bool Rtl { get; init; } = true;

        public bool ShowHeader { get; init; } = true;
        public bool ShowFooter { get; init; } = true;
        public bool ShowPageNumbers { get; init; } = true;

        // Repeat the column header row on every page. Costs nothing in HTML/PDF and is what makes a 40-page
        // trial balance readable.
        public bool RepeatHeaderRow { get; init; } = true;

        // ---- typography, CHOSEN BY THE AUTHOR ------------------------------------------------------------
        //
        // null = the platform's own stack for the run's language (ReportTypography). A value here overrides
        // it for the whole document: the body, the bands, the table, everything that does not carry its own
        // element font.
        //
        // It sits on the PAGE SETUP because that is the one object already persisted in both layout shapes
        // and already handed to every renderer — so one property covers HTML, the visual designer and the
        // PDF header instead of three that could drift.
        //
        // The VALUE IS VALIDATED against ReportTypography.DesignerFaces before it reaches any CSS. A font
        // name is a string that ends up inside a style attribute, and an approved list is the only reason
        // that is safe; see ReportVisualLayoutValidator.
        public string? FontFamily { get; init; }

        // Base point size for the document. null = the renderer's own default, which differs per renderer
        // because a data table and a designed page are not read at the same size.
        public double? FontSizePt { get; init; }

        public static ReportPageSetup Default { get; } = new();

        // A copy with the typography replaced. The validator needs it: these are init-only properties, so
        // sanitising a stored font means producing a new setup rather than assigning over the old one.
        public ReportPageSetup WithTypography(string? fontFamily, double? fontSizePt) => new()
        {
            PageSize = PageSize,
            Orientation = Orientation,
            MarginTopMm = MarginTopMm,
            MarginBottomMm = MarginBottomMm,
            MarginLeftMm = MarginLeftMm,
            MarginRightMm = MarginRightMm,
            Rtl = Rtl,
            FontFamily = fontFamily,
            FontSizePt = fontSizePt,
            ShowHeader = ShowHeader,
            ShowFooter = ShowFooter,
            ShowPageNumbers = ShowPageNumbers,
            RepeatHeaderRow = RepeatHeaderRow,
        };

        public ReportPageSetup With(ReportPageSize? size = null, ReportOrientation? orientation = null, bool? rtl = null) => new()
        {
            PageSize = size ?? PageSize,
            Orientation = orientation ?? Orientation,
            MarginTopMm = MarginTopMm,
            MarginBottomMm = MarginBottomMm,
            MarginLeftMm = MarginLeftMm,
            MarginRightMm = MarginRightMm,
            Rtl = rtl ?? Rtl,
            FontFamily = FontFamily,
            FontSizePt = FontSizePt,
            ShowHeader = ShowHeader,
            ShowFooter = ShowFooter,
            ShowPageNumbers = ShowPageNumbers,
            RepeatHeaderRow = RepeatHeaderRow,
        };
    }

    // ============================================================================================
    // The serialized body of a template version.
    //
    // A layout is query INTENT plus presentation — never data, never a data-source key, never a permission.
    // Those live in the code-first definition, which a template may not override. So the worst a malicious or
    // corrupt layout can do is ask for columns/filters the definition does not declare, which the shaper
    // rejects. This containment is the whole reason templates are user-editable at all.
    // ============================================================================================
    public sealed class ReportLayout
    {
        // Layout schema version, not the template version. Bumped only if this shape changes incompatibly, so
        // an old stored layout can be read by a newer engine.
        public int SchemaVersion { get; init; } = 1;

        // Visible columns in display order. Empty = "the definition's default visible set", which is what makes
        // a layout survive a definition gaining a column.
        public IReadOnlyList<string> VisibleColumns { get; init; } = Array.Empty<string>();

        public IReadOnlyList<ReportFilter> Filters { get; init; } = Array.Empty<ReportFilter>();
        public IReadOnlyList<ReportSort> Sorts { get; init; } = Array.Empty<ReportSort>();
        public IReadOnlyList<ReportGrouping> Groupings { get; init; } = Array.Empty<ReportGrouping>();

        // Parameter values baked into the template ("this layout is always for branch 3"). A request may
        // override any of them — see ReportEngine's precedence rule.
        public IReadOnlyDictionary<string, string?> Parameters { get; init; } = new Dictionary<string, string?>();

        public ReportPageSetup PageSetup { get; init; } = ReportPageSetup.Default;

        public string? TitleOverride { get; init; }
        public string? TitleOverrideEn { get; init; }

        public bool ShowGrandTotals { get; init; } = true;

        // REPORT STUDIO V2 — the positioned, banded document design.
        //
        // Added to ReportLayout rather than to a new table because §14 prefers it and because the layout is
        // ALREADY persisted as JSON on the template version: a new property round-trips through
        // ReportLayoutJson with no migration, no second saved-report path and no schema change. Null means a
        // V1 column-list report, which keeps every existing template readable.
        public ReportVisualLayout? Visual { get; init; }

        public static ReportLayout Empty { get; } = new();
    }
}