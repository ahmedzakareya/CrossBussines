using System.Globalization;

namespace CrossBuy.BL.Reporting
{
    // ============================================================================================
    // Reporting Platform (ADR-037) — THE SHAPER: filtering, sorting, grouping, aggregation.
    //
    // Pure and deterministic: same data set + same intent → same view, with no clock, no DbContext, no culture
    // beyond the one passed in. That is why every grouping and subtotal rule in this platform is unit-testable
    // without a database, and why a shaping bug can be reproduced from a saved layout alone.
    //
    // It is also the field WHITELIST. Every Field on a filter, sort or grouping is looked up in the DEFINITION.
    // A field the definition does not declare never reaches a comparison, an identifier, or a renderer. Because
    // of that, a stored template — which is user-editable data — cannot widen what a report reads.
    //
    // The asymmetry between dropping a SORT and dropping a FILTER is deliberate and is the most important rule
    // in this file:
    //   * an unusable SORT or GROUPING is dropped with a Warning — it is cosmetic, and a stale template should
    //     still render;
    //   * an unusable FILTER is an ERROR that fails the run — dropping a filter WIDENS the result set, so
    //     "ignore what you don't understand" would silently show a user more data than the layout asked for.
    // ============================================================================================

    // One computed aggregate. Count is carried alongside Value because Average needs it and because "sum 0 over
    // 0 rows" and "sum 0 over 400 rows" are different facts.
    public sealed class ReportAggregateValue
    {
        public required string ColumnKey { get; init; }
        public ReportAggregate Aggregate { get; init; }
        public decimal Value { get; init; }
        public int Count { get; init; }
    }

    // One band in the grouped view. A tree, not a flat list of levels, so a renderer can walk it without
    // reconstructing the hierarchy from repeated key columns.
    public sealed class ReportGroupNode
    {
        public required ReportColumn Column { get; init; }
        public object? Key { get; init; }

        // Display text of the key, formatted once here rather than by each renderer — so the HTML, the PDF and
        // the Excel sheet all band on exactly the same label.
        public required string KeyText { get; init; }

        // 0 = outermost grouping level.
        public int Level { get; init; }

        public IReadOnlyList<ReportGroupNode> Children { get; init; } = Array.Empty<ReportGroupNode>();

        // Detail rows. Populated only on the DEEPEST level — an intermediate node's rows are its children's, and
        // duplicating them would double every subtotal computed by a naive renderer.
        public IReadOnlyList<ReportRow> Rows { get; init; } = Array.Empty<ReportRow>();

        public IReadOnlyList<ReportAggregateValue> Subtotals { get; init; } = Array.Empty<ReportAggregateValue>();

        // Detail rows beneath this node at any depth.
        public int RowCount { get; init; }

        public bool Collapsed { get; init; }
        public bool IsLeafLevel => Children.Count == 0;
    }

    // The shaped, render-ready result. Renderers and exporters consume ONLY this — none of them sees a
    // ReportDataSet, a filter or a parameter. That is what keeps a renderer swappable.
    public sealed class ReportView
    {
        public required ReportDefinition Definition { get; init; }

        // Visible columns in display order, internal columns already removed.
        public required IReadOnlyList<ReportColumn> Columns { get; init; }

        // The filtered, sorted, capped detail rows — flat, in render order. Present even when grouped, because
        // exports (CSV) are flat and would otherwise have to re-flatten the tree.
        public required IReadOnlyList<ReportRow> Rows { get; init; }

        // Top-level group nodes. Empty when the view is ungrouped.
        public IReadOnlyList<ReportGroupNode> Groups { get; init; } = Array.Empty<ReportGroupNode>();

        public IReadOnlyList<ReportGrouping> Groupings { get; init; } = Array.Empty<ReportGrouping>();

        public IReadOnlyList<ReportAggregateValue> GrandTotals { get; init; } = Array.Empty<ReportAggregateValue>();

        public int RowCount => Rows.Count;

        // true = rows are missing because a cap was reached. Carried all the way to the rendered output, which
        // prints it: a truncated report that does not say so is a wrong report.
        public bool Truncated { get; init; }

        public int? TotalAvailableRows { get; init; }

        public bool IsGrouped => Groups.Count > 0;

        public ReportAggregateValue? GrandTotalFor(string columnKey) =>
            GrandTotals.FirstOrDefault(g => string.Equals(g.ColumnKey, columnKey, StringComparison.Ordinal));
    }

    // The intent handed to the shaper — already merged by the engine from request + template + definition.
    public sealed class ReportShapeRequest
    {
        public IReadOnlyList<ReportFilter> Filters { get; init; } = Array.Empty<ReportFilter>();
        public IReadOnlyList<ReportSort> Sorts { get; init; } = Array.Empty<ReportSort>();
        public IReadOnlyList<ReportGrouping> Groupings { get; init; } = Array.Empty<ReportGrouping>();

        // Empty = the definition's default visible set.
        public IReadOnlyList<string> VisibleColumns { get; init; } = Array.Empty<string>();

        // 0 = no cap.
        public int MaxRows { get; init; }

        public bool ShowGrandTotals { get; init; } = true;
    }

    public sealed class ReportShapeResult
    {
        public ReportView? View { get; init; }
        public IReadOnlyList<ReportDiagnostic> Diagnostics { get; init; } = Array.Empty<ReportDiagnostic>();

        public bool IsValid => View != null &&
            !Diagnostics.Any(d => d.Severity == ReportDiagnosticSeverity.Error);
    }

    public interface IReportDataShaper
    {
        ReportShapeResult Shape(ReportDefinition definition, ReportDataSet data, ReportShapeRequest request,
            CultureInfo culture);
    }

    public class ReportDataShaper : IReportDataShaper
    {
        public const string CodeUnknownFilterField = "filter_field_unknown";
        public const string CodeFieldNotFilterable = "filter_field_not_filterable";
        public const string CodeBadFilterValue = "filter_value_invalid";
        public const string CodeSortDropped = "sort_dropped";
        public const string CodeGroupingDropped = "grouping_dropped";
        public const string CodeColumnDropped = "column_dropped";
        public const string CodeTruncated = "rows_truncated";

        public ReportShapeResult Shape(ReportDefinition definition, ReportDataSet data,
            ReportShapeRequest request, CultureInfo culture)
        {
            var diagnostics = new List<ReportDiagnostic>();

            // ---- 1. Visible columns -----------------------------------------------------------------------
            var columns = ResolveColumns(definition, request.VisibleColumns, diagnostics);
            if (columns.Count == 0)
            {
                // Falling back to the definition default rather than failing: a layout that names only removed
                // columns should still show the report, and the warnings above already say what was dropped.
                columns = definition.DefaultVisibleColumns.ToList();
                if (columns.Count == 0)
                    columns = definition.Columns.Where(c => !c.Internal).ToList();
            }

            // ---- 2. Filters -------------------------------------------------------------------------------
            var rows = data.Rows;
            var pending = FiltersNotAlreadyApplied(request.Filters, data.AppliedFilters);

            foreach (var filter in pending)
            {
                var column = definition.FindColumn(filter.Field);
                if (column == null)
                {
                    diagnostics.Add(ReportDiagnostic.Error(CodeUnknownFilterField,
                        $"Filter references column '{filter.Field}', which report '{definition.Code}' does not " +
                        "declare. A filter is never dropped, because dropping it would widen the result.",
                        filter.Field));
                    continue;
                }
                // INTERNAL is checked here as well as Filterable, and it is not redundant. Filtering on a hidden
                // column leaks it by inference: filter Cost > 100, see which rows survive, and you have learned
                // the hidden costs without ever displaying the column. So an internal column is unfilterable
                // regardless of its Filterable flag — the same reason the catalog refuses a default filter on one.
                if (column.Internal || !column.Filterable)
                {
                    diagnostics.Add(ReportDiagnostic.Error(CodeFieldNotFilterable,
                        $"Column '{filter.Field}' is not filterable" +
                        (column.Internal ? " (it is internal to the report; filtering on it would leak its " +
                                           "values by inference)." : "."),
                        filter.Field));
                    continue;
                }

                if (!TryBuildPredicate(filter, column, culture, out var predicate, out var error))
                {
                    diagnostics.Add(ReportDiagnostic.Error(CodeBadFilterValue, error!, filter.Field));
                    continue;
                }

                rows = rows.Where(predicate!).ToList();
            }

            // A filter error must stop the run BEFORE any rows are handed on. Returning the partially filtered
            // rows would be the exact leak the Error exists to prevent.
            if (diagnostics.Any(d => d.Severity == ReportDiagnosticSeverity.Error))
                return new ReportShapeResult { Diagnostics = diagnostics };

            // ---- 3. Sorting -------------------------------------------------------------------------------
            //
            // Grouping keys sort FIRST, in grouping order, then the requested sorts. That is not a preference:
            // the group builder walks rows in order and starts a new band on a key change, so unsorted input
            // would produce one band per run of equal keys instead of one band per key.
            var groupings = ResolveGroupings(definition, request.Groupings, diagnostics);
            var sorts = ResolveSorts(definition, request.Sorts, diagnostics);

            var effectiveSorts = groupings
                .Select(g => ReportSort.By(g.Field, g.Descending))
                .Concat(sorts.Where(s => groupings.All(g => !string.Equals(g.Field, s.Field, StringComparison.Ordinal))))
                .ToList();

            if (effectiveSorts.Count > 0 && !AlreadySortedBy(data.AppliedSorts, effectiveSorts))
                rows = SortRows(rows, effectiveSorts, definition, culture);

            // ---- 4. Row cap -------------------------------------------------------------------------------
            var truncated = data.Truncated;
            if (request.MaxRows > 0 && rows.Count > request.MaxRows)
            {
                rows = rows.Take(request.MaxRows).ToList();
                truncated = true;
            }

            if (truncated)
                diagnostics.Add(ReportDiagnostic.Warning(CodeTruncated,
                    $"The result was truncated at {rows.Count} rows; rows are missing from this output."));

            // ---- 5. Groups + totals -----------------------------------------------------------------------
            var groups = groupings.Count == 0
                ? Array.Empty<ReportGroupNode>()
                : BuildGroups(rows, groupings, 0, definition, columns, culture);

            var grandTotals = request.ShowGrandTotals
                ? Aggregate(rows, columns)
                : Array.Empty<ReportAggregateValue>();

            return new ReportShapeResult
            {
                Diagnostics = diagnostics,
                View = new ReportView
                {
                    Definition = definition,
                    Columns = columns,
                    Rows = rows,
                    Groups = groups,
                    Groupings = groupings,
                    GrandTotals = grandTotals,
                    Truncated = truncated,
                    TotalAvailableRows = data.TotalRowCount,
                },
            };
        }

        // ------------------------------------------------------------------------------------------------
        // Column / sort / grouping resolution
        // ------------------------------------------------------------------------------------------------
        private static List<ReportColumn> ResolveColumns(ReportDefinition definition,
            IReadOnlyList<string> requested, List<ReportDiagnostic> diagnostics)
        {
            if (requested.Count == 0) return definition.DefaultVisibleColumns.ToList();

            var result = new List<ReportColumn>();
            foreach (var key in requested)
            {
                var column = definition.FindColumn(key);
                if (column == null)
                {
                    diagnostics.Add(ReportDiagnostic.Warning(CodeColumnDropped,
                        $"Column '{key}' is not declared by report '{definition.Code}' and was dropped.", key));
                    continue;
                }

                // An internal column named by a layout is dropped, always. This is the export-leak guard: a
                // hidden cost column must not become visible because someone edited a saved layout.
                if (column.Internal)
                {
                    diagnostics.Add(ReportDiagnostic.Warning(CodeColumnDropped,
                        $"Column '{key}' is internal to report '{definition.Code}' and cannot be displayed.", key));
                    continue;
                }

                if (result.Any(c => string.Equals(c.Key, column.Key, StringComparison.Ordinal))) continue;
                result.Add(column);
            }
            return result;
        }

        private static List<ReportSort> ResolveSorts(ReportDefinition definition,
            IReadOnlyList<ReportSort> requested, List<ReportDiagnostic> diagnostics)
        {
            var result = new List<ReportSort>();
            foreach (var sort in requested)
            {
                var column = definition.FindColumn(sort.Field);
                if (column == null || column.Internal || !column.Sortable)
                {
                    diagnostics.Add(ReportDiagnostic.Warning(CodeSortDropped,
                        $"Sort on '{sort.Field}' was dropped (unknown, internal, or not sortable).", sort.Field));
                    continue;
                }
                result.Add(sort);
            }
            return result;
        }

        private static List<ReportGrouping> ResolveGroupings(ReportDefinition definition,
            IReadOnlyList<ReportGrouping> requested, List<ReportDiagnostic> diagnostics)
        {
            var result = new List<ReportGrouping>();
            foreach (var grouping in requested)
            {
                var column = definition.FindColumn(grouping.Field);
                if (column == null || column.Internal || !column.Groupable)
                {
                    diagnostics.Add(ReportDiagnostic.Warning(CodeGroupingDropped,
                        $"Grouping on '{grouping.Field}' was dropped (unknown, internal, or not groupable).",
                        grouping.Field));
                    continue;
                }
                if (result.Any(g => string.Equals(g.Field, grouping.Field, StringComparison.Ordinal))) continue;
                result.Add(grouping);
            }
            return result;
        }

        // Two filters are "the same" when field, operator and value list match. Compared structurally rather than
        // by reference because a data source may rebuild the filter objects it pushed down (and a deserialized
        // template's filters are new instances by definition).
        private static List<ReportFilter> FiltersNotAlreadyApplied(IReadOnlyList<ReportFilter> requested,
            IReadOnlyList<ReportFilter> applied)
        {
            if (applied.Count == 0) return requested.ToList();

            bool Same(ReportFilter a, ReportFilter b) =>
                string.Equals(a.Field, b.Field, StringComparison.Ordinal) &&
                a.Operator == b.Operator &&
                a.Values.Count == b.Values.Count &&
                a.Values.Zip(b.Values).All(p => string.Equals(p.First, p.Second, StringComparison.Ordinal));

            return requested.Where(r => !applied.Any(a => Same(a, r))).ToList();
        }

        private static bool AlreadySortedBy(IReadOnlyList<ReportSort> applied, IReadOnlyList<ReportSort> wanted)
        {
            if (applied.Count < wanted.Count) return false;
            for (var i = 0; i < wanted.Count; i++)
            {
                if (!string.Equals(applied[i].Field, wanted[i].Field, StringComparison.Ordinal)) return false;
                if (applied[i].Descending != wanted[i].Descending) return false;
            }
            return true;
        }

        // ------------------------------------------------------------------------------------------------
        // Sorting
        // ------------------------------------------------------------------------------------------------
        private static List<ReportRow> SortRows(IReadOnlyList<ReportRow> rows, IReadOnlyList<ReportSort> sorts,
            ReportDefinition definition, CultureInfo culture)
        {
            // One comparer over the whole key list rather than chained OrderBy/ThenBy: the key list is dynamic,
            // and List.Sort with a composite comparer avoids materialising an ordered enumerable per level.
            // List.Sort is unstable, so the comparer falls back to nothing — every sort key is explicit, which
            // makes the order reproducible regardless of stability.
            var comparers = sorts
                .Select(s => (Sort: s, Column: definition.FindColumn(s.Field)))
                .Where(x => x.Column != null)
                .ToList();

            var copy = rows.ToList();
            copy.Sort((left, right) =>
            {
                foreach (var (sort, column) in comparers)
                {
                    var c = ReportValues.Compare(left[column!.Key], right[column.Key], column.Type, culture);
                    if (c != 0) return sort.Descending ? -c : c;
                }
                return 0;
            });
            return copy;
        }

        // ------------------------------------------------------------------------------------------------
        // Grouping — recursive, over rows already ordered by the grouping keys.
        // ------------------------------------------------------------------------------------------------
        private static IReadOnlyList<ReportGroupNode> BuildGroups(IReadOnlyList<ReportRow> rows,
            IReadOnlyList<ReportGrouping> groupings, int level, ReportDefinition definition,
            IReadOnlyList<ReportColumn> visibleColumns, CultureInfo culture)
        {
            if (level >= groupings.Count || rows.Count == 0) return Array.Empty<ReportGroupNode>();

            var grouping = groupings[level];
            var column = definition.FindColumn(grouping.Field)!;
            var nodes = new List<ReportGroupNode>();

            var start = 0;
            while (start < rows.Count)
            {
                var key = rows[start][column.Key];
                var end = start + 1;
                while (end < rows.Count && ReportValues.AreEqual(rows[end][column.Key], key, column.Type, culture))
                    end++;

                var slice = rows.Skip(start).Take(end - start).ToList();
                var children = BuildGroups(slice, groupings, level + 1, definition, visibleColumns, culture);

                nodes.Add(new ReportGroupNode
                {
                    Column = column,
                    Key = key,
                    KeyText = ReportValues.Format(key, column, culture),
                    Level = level,
                    Children = children,

                    // Detail rows only at the deepest level — see the note on ReportGroupNode.Rows.
                    Rows = children.Count == 0 ? slice : Array.Empty<ReportRow>(),

                    Subtotals = grouping.IncludeSubtotals
                        ? Aggregate(slice, visibleColumns)
                        : Array.Empty<ReportAggregateValue>(),
                    RowCount = slice.Count,
                    Collapsed = grouping.Collapsed,
                });

                start = end;
            }

            return nodes;
        }

        // ------------------------------------------------------------------------------------------------
        // Aggregation
        // ------------------------------------------------------------------------------------------------
        private static IReadOnlyList<ReportAggregateValue> Aggregate(IReadOnlyList<ReportRow> rows,
            IReadOnlyList<ReportColumn> columns)
        {
            var result = new List<ReportAggregateValue>();

            foreach (var column in columns.Where(c => c.Aggregate != ReportAggregate.None))
            {
                var values = rows.Select(r => r[column.Key]).ToList();
                var present = values.Where(v => v != null).ToList();

                decimal value;
                switch (column.Aggregate)
                {
                    case ReportAggregate.Sum:
                        value = present.Sum(v => ReportValues.AsDecimal(v));
                        break;

                    case ReportAggregate.Average:
                        // Averaged over rows that HAVE a value, not over all rows. A null is "not measured",
                        // and treating it as zero would drag every average toward zero.
                        value = present.Count == 0 ? 0m : present.Sum(v => ReportValues.AsDecimal(v)) / present.Count;
                        break;

                    case ReportAggregate.Min:
                        value = present.Count == 0 ? 0m : present.Min(v => ReportValues.AsDecimal(v));
                        break;

                    case ReportAggregate.Max:
                        value = present.Count == 0 ? 0m : present.Max(v => ReportValues.AsDecimal(v));
                        break;

                    case ReportAggregate.Count:
                        value = present.Count;
                        break;

                    case ReportAggregate.CountDistinct:
                        value = present
                            .Select(v => ReportValues.AsString(v, CultureInfo.InvariantCulture))
                            .Distinct(StringComparer.Ordinal)
                            .Count();
                        break;

                    default:
                        continue;
                }

                result.Add(new ReportAggregateValue
                {
                    ColumnKey = column.Key,
                    Aggregate = column.Aggregate,
                    Value = value,
                    Count = present.Count,
                });
            }

            return result;
        }

        // ------------------------------------------------------------------------------------------------
        // Filter predicates
        // ------------------------------------------------------------------------------------------------
        private static bool TryBuildPredicate(ReportFilter filter, ReportColumn column, CultureInfo culture,
            out Func<ReportRow, bool>? predicate, out string? error)
        {
            predicate = null;
            error = null;
            var key = column.Key;
            // Filter values are literals from a layout or a URL, never relative tokens resolved against "now":
            // a filter is part of a saved layout and must mean the same thing on every run. Relative dates are a
            // PARAMETER feature (see ReportValues.DateTokens), where the binder resolves them against the clock.
            var epoch = DateTime.MinValue;

            bool Convert(string? text, out object? value) =>
                ReportValues.TryConvert(text, column.Type, culture, epoch, out value);

            switch (filter.Operator)
            {
                case ReportFilterOperator.IsNull:
                    predicate = r => r[key] == null;
                    return true;

                case ReportFilterOperator.IsNotNull:
                    predicate = r => r[key] != null;
                    return true;

                case ReportFilterOperator.Contains:
                case ReportFilterOperator.StartsWith:
                {
                    var needle = filter.FirstValue ?? "";
                    var comparison = StringComparison.OrdinalIgnoreCase;
                    predicate = filter.Operator == ReportFilterOperator.Contains
                        ? r => ReportValues.AsString(r[key], culture).Contains(needle, comparison)
                        : r => ReportValues.AsString(r[key], culture).StartsWith(needle, comparison);
                    return true;
                }

                case ReportFilterOperator.Between:
                {
                    if (filter.Values.Count < 2)
                    {
                        error = $"Filter 'Between' on '{key}' needs two values.";
                        return false;
                    }
                    if (!Convert(filter.Values[0], out var from) || !Convert(filter.Values[1], out var to))
                    {
                        error = $"Filter 'Between' on '{key}' has a value that is not a valid {column.Type}.";
                        return false;
                    }

                    // Inclusive on both ends — see ReportFilterOperator.Between. A null cell is NOT in any range;
                    // "unknown" must not satisfy a bounded question.
                    predicate = r =>
                    {
                        var v = r[key];
                        if (v == null) return false;
                        if (from != null && ReportValues.Compare(v, from, column.Type, culture) < 0) return false;
                        if (to != null && ReportValues.Compare(v, to, column.Type, culture) > 0) return false;
                        return true;
                    };
                    return true;
                }

                case ReportFilterOperator.In:
                {
                    var wanted = new List<object?>();
                    foreach (var text in filter.Values)
                    {
                        if (!Convert(text, out var v))
                        {
                            error = $"Filter 'In' on '{key}' has value '{text}', which is not a valid {column.Type}.";
                            return false;
                        }
                        wanted.Add(v);
                    }
                    if (wanted.Count == 0)
                    {
                        // An empty IN list matches nothing. Explicit, because SQL's answer here is also "no
                        // rows" and a platform that returned ALL rows instead would be a spectacular leak.
                        predicate = _ => false;
                        return true;
                    }
                    predicate = r => wanted.Any(w => ReportValues.AreEqual(r[key], w, column.Type, culture));
                    return true;
                }

                default:
                {
                    if (!Convert(filter.FirstValue, out var operand))
                    {
                        error = $"Filter on '{key}' has value '{filter.FirstValue}', which is not a valid " +
                                $"{column.Type}.";
                        return false;
                    }

                    var op = filter.Operator;
                    predicate = r =>
                    {
                        var v = r[key];
                        if (op == ReportFilterOperator.Equals)
                            return ReportValues.AreEqual(v, operand, column.Type, culture);
                        if (op == ReportFilterOperator.NotEquals)
                            return !ReportValues.AreEqual(v, operand, column.Type, culture);

                        // Ordering comparisons exclude nulls entirely: "unknown > 100" is not true, and it is
                        // not false-because-zero either. Excluding it is the only answer that does not invent data.
                        if (v == null) return false;
                        var c = ReportValues.Compare(v, operand, column.Type, culture);
                        return op switch
                        {
                            ReportFilterOperator.GreaterThan => c > 0,
                            ReportFilterOperator.GreaterOrEqual => c >= 0,
                            ReportFilterOperator.LessThan => c < 0,
                            ReportFilterOperator.LessOrEqual => c <= 0,
                            _ => true,
                        };
                    };
                    return true;
                }
            }
        }
    }
}