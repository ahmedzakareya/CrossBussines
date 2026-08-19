using System.Globalization;
using CrossBuy.Models.Context.Reporting;
using CrossBuy.Models.Platform;

namespace CrossBuy.BL.Reporting
{
    // ============================================================================================
    // Reporting Platform (ADR-037) — THE DATA SEAM.
    //
    // This is where the reporting platform stops and a module begins. A module answers exactly one question —
    // "here are the rows for this report, for this company, with these parameters" — and knows nothing about
    // grouping, rendering, exporting, archiving or scheduling.
    //
    // The platform ships NO data source over a production table. That is a scope decision, not an omission:
    // this slice is architecture, and a data source is where reporting would start reading Accounting or
    // Inventory. Modules add theirs later, unchanged platform.
    // ============================================================================================

    // A single result row. Values are boxed objects keyed by column key.
    //
    // Backed by an array plus a SHARED column index (one dictionary per data set, not per row). With 50k rows a
    // dictionary each would be 50k dictionaries; this is the same access syntax at a fraction of the allocation.
    public sealed class ReportRow
    {
        private readonly object?[] _values;
        private readonly IReadOnlyDictionary<string, int> _index;

        public ReportRow(object?[] values, IReadOnlyDictionary<string, int> index)
        {
            _values = values ?? throw new ArgumentNullException(nameof(values));
            _index = index ?? throw new ArgumentNullException(nameof(index));
        }

        // Unknown key returns null rather than throwing: the shaper validates keys against the DEFINITION, so a
        // key that reaches here and is missing means the data source returned a narrower row than it declared —
        // a gap a report should render as blank, not a 500 on a user's screen.
        public object? this[string key] => _index.TryGetValue(key, out var i) && i < _values.Length
            ? _values[i]
            : null;

        public object? ValueAt(int ordinal) => ordinal >= 0 && ordinal < _values.Length ? _values[ordinal] : null;

        public int Count => _values.Length;

        public IReadOnlyList<object?> Values => _values;
    }

    // The tabular result of one data-source fetch, plus what the source already did to it.
    //
    // The "already did" part matters. A SQL-backed source will push filters and sorting into the query; an
    // in-memory one will not. Rather than guessing, the source DECLARES what it applied and the shaper does the
    // remainder. Without that declaration the platform would either double-apply (wrong when a filter is not
    // idempotent) or never push down (slow).
    public sealed class ReportDataSet
    {
        // The columns actually present, in row order. A subset of the definition's columns — a source may skip
        // columns nobody asked for.
        public required IReadOnlyList<ReportColumn> Columns { get; init; }

        public required IReadOnlyList<ReportRow> Rows { get; init; }

        // true = a row cap stopped the fetch and rows are missing. Never silently swallowed: the engine turns
        // this into a Warning diagnostic and a Truncated flag on the run summary.
        public bool Truncated { get; init; }

        // Total rows available before any cap, when the source can know it without a second expensive pass.
        // null = unknown, which is an honest answer and better than a wrong count.
        public int? TotalRowCount { get; init; }

        // Filters the source already applied. The shaper skips exactly these and applies the rest.
        public IReadOnlyList<ReportFilter> AppliedFilters { get; init; } = Array.Empty<ReportFilter>();

        // Sorts the source already applied, in order. The shaper re-sorts only if the requested order differs.
        public IReadOnlyList<ReportSort> AppliedSorts { get; init; } = Array.Empty<ReportSort>();

        public static ReportDataSet Empty(IReadOnlyList<ReportColumn> columns) =>
            new() { Columns = columns, Rows = Array.Empty<ReportRow>(), TotalRowCount = 0 };
    }

    // Convenience builder so a data source writes `builder.AddRow(...)` instead of hand-managing the shared
    // index. Not required — a source may construct a ReportDataSet directly.
    public sealed class ReportDataSetBuilder
    {
        private readonly List<ReportColumn> _columns;
        private readonly Dictionary<string, int> _index;
        private readonly List<ReportRow> _rows = new();

        public ReportDataSetBuilder(IEnumerable<ReportColumn> columns)
        {
            _columns = columns.ToList();
            _index = new Dictionary<string, int>(StringComparer.Ordinal);
            for (var i = 0; i < _columns.Count; i++) _index[_columns[i].Key] = i;
        }

        public int RowCount => _rows.Count;

        // Positional: values must be in the column order handed to the constructor.
        public ReportDataSetBuilder AddRow(params object?[] values)
        {
            var row = new object?[_columns.Count];
            Array.Copy(values, row, Math.Min(values.Length, row.Length));
            _rows.Add(new ReportRow(row, _index));
            return this;
        }

        // Keyed: unknown keys are ignored, missing keys stay null. The forgiving form, for a source that
        // projects from a dictionary.
        public ReportDataSetBuilder AddRow(IReadOnlyDictionary<string, object?> values)
        {
            var row = new object?[_columns.Count];
            foreach (var kv in values)
                if (_index.TryGetValue(kv.Key, out var i)) row[i] = kv.Value;
            _rows.Add(new ReportRow(row, _index));
            return this;
        }

        public ReportDataSet Build(bool truncated = false, int? totalRowCount = null,
            IReadOnlyList<ReportFilter>? appliedFilters = null, IReadOnlyList<ReportSort>? appliedSorts = null) => new()
            {
                Columns = _columns,
                Rows = _rows,
                Truncated = truncated,
                TotalRowCount = totalRowCount ?? _rows.Count,
                AppliedFilters = appliedFilters ?? Array.Empty<ReportFilter>(),
                AppliedSorts = appliedSorts ?? Array.Empty<ReportSort>(),
            };
    }

    // Everything a data source is told. Nothing here is caller-controlled except the parameters and query
    // intent — the COMPANY comes from the resolved BusinessContext, so a source cannot be talked into reading
    // another tenant even by a malicious request.
    public sealed class ReportDataQuery
    {
        public required ReportDefinition Definition { get; init; }

        // The resolved isolation + authorization context. A data source MUST filter on Context.CompanyId. It is
        // handed the context rather than a bare company id so a source can also honour BranchId / EmployeeId
        // narrowing, and so the fail-closed guarantee is one type, not a convention about an int.
        public required BusinessContext Context { get; init; }

        // Typed, validated parameter values — including the engine-supplied ones (CompanyId, EmployeeId, Culture).
        public required ReportParameterSet Parameters { get; init; }

        // Query intent the source MAY push down. Anything it pushes down it must list in the returned data set's
        // AppliedFilters / AppliedSorts. Anything it ignores, the shaper does in memory — so ignoring all of it
        // is always correct, just slower.
        public IReadOnlyList<ReportFilter> Filters { get; init; } = Array.Empty<ReportFilter>();
        public IReadOnlyList<ReportSort> Sorts { get; init; } = Array.Empty<ReportSort>();
        public IReadOnlyList<ReportGrouping> Groupings { get; init; } = Array.Empty<ReportGrouping>();

        // The columns the output will actually use (visible set + grouping + aggregate columns). A source may
        // project only these; it may also return everything and let the shaper drop the rest.
        public IReadOnlyList<ReportColumn> RequestedColumns { get; init; } = Array.Empty<ReportColumn>();

        // Hard row ceiling, already reconciled against the definition's MaxRows and the run kind (a Preview
        // arrives here with the small number). A source SHOULD apply it in the query; the shaper enforces it
        // regardless, so a source that ignores it cannot blow up a render — only waste a fetch.
        public int MaxRows { get; init; }

        public ReportRunKind Kind { get; init; }

        public CultureInfo Culture { get; init; } = CultureInfo.CurrentUICulture;

        public bool IsPreview => Kind == ReportRunKind.Preview;
    }

    // ============================================================================================
    // The data-source extension point.
    // ============================================================================================
    public interface IReportDataSource
    {
        // Matches ReportDefinition.DataSourceKey. Convention: "<Module>.<Thing>" — e.g. "Accounting.TrialBalance".
        string Key { get; }

        // Must be READ-ONLY. A data source may not write: not the GL (JournalEntryService is the only GL
        // writer), not stock (StockService is the only stock writer), and not its own audit row. Reads should be
        // AsNoTracking — a report has no business tracking entities it will never save.
        Task<ReportDataSet> FetchAsync(ReportDataQuery query, CancellationToken cancellationToken = default);
    }

    public interface IReportDataSourceRegistry
    {
        // Throws ReportDataSourceNotRegisteredException when nothing claims the key — a wiring bug, surfaced
        // where it can be understood.
        IReportDataSource Resolve(string dataSourceKey, string? reportCode = null);

        bool TryResolve(string? dataSourceKey, out IReportDataSource? dataSource);

        IReadOnlyCollection<string> RegisteredKeys { get; }
    }

    public class ReportDataSourceRegistry : IReportDataSourceRegistry
    {
        private readonly Dictionary<string, IReportDataSource> _byKey;

        public ReportDataSourceRegistry(IEnumerable<IReportDataSource> sources)
        {
            _byKey = new Dictionary<string, IReportDataSource>(StringComparer.Ordinal);
            foreach (var source in sources)
            {
                if (string.IsNullOrWhiteSpace(source.Key))
                    throw new ReportingException(
                        $"Data source '{source.GetType().Name}' has an empty Key.");

                // Last-registration-wins would let a module silently hijack another module's reports. Two
                // sources claiming one key is a wiring error, and it fails at graph construction.
                if (_byKey.TryGetValue(source.Key, out var existing))
                    throw new ReportingException(
                        $"Duplicate report data source key '{source.Key}': claimed by both " +
                        $"'{existing.GetType().Name}' and '{source.GetType().Name}'.");

                _byKey[source.Key] = source;
            }
        }

        public IReadOnlyCollection<string> RegisteredKeys => _byKey.Keys;

        public IReportDataSource Resolve(string dataSourceKey, string? reportCode = null) =>
            _byKey.TryGetValue(dataSourceKey ?? "", out var source)
                ? source
                : throw new ReportDataSourceNotRegisteredException(dataSourceKey, reportCode);

        public bool TryResolve(string? dataSourceKey, out IReportDataSource? dataSource)
        {
            dataSource = null;
            if (string.IsNullOrEmpty(dataSourceKey)) return false;
            if (!_byKey.TryGetValue(dataSourceKey, out var found)) return false;
            dataSource = found;
            return true;
        }
    }

    // A data source over rows supplied in code.
    //
    // Exists for two real jobs, not as a mock: it is what the unit tests drive the whole pipeline with, and it is
    // the two-line example a module author copies. It applies NO filters and NO sorts, which exercises the
    // "source pushed nothing down, shaper does everything" path — the path every naive source will take.
    public sealed class StaticReportDataSource : IReportDataSource
    {
        private readonly Func<ReportDataQuery, IEnumerable<IReadOnlyDictionary<string, object?>>> _rows;

        public StaticReportDataSource(string key,
            Func<ReportDataQuery, IEnumerable<IReadOnlyDictionary<string, object?>>> rows)
        {
            Key = key;
            _rows = rows;
        }

        public StaticReportDataSource(string key, IEnumerable<IReadOnlyDictionary<string, object?>> rows)
            : this(key, _ => rows) { }

        public string Key { get; }

        public Task<ReportDataSet> FetchAsync(ReportDataQuery query, CancellationToken cancellationToken = default)
        {
            var columns = query.RequestedColumns.Count > 0 ? query.RequestedColumns : query.Definition.Columns;
            var builder = new ReportDataSetBuilder(columns);

            var cap = query.MaxRows > 0 ? query.MaxRows : int.MaxValue;
            var truncated = false;
            var total = 0;

            foreach (var row in _rows(query))
            {
                total++;
                if (builder.RowCount >= cap) { truncated = true; continue; }
                builder.AddRow(row);
            }

            return Task.FromResult(builder.Build(truncated, total));
        }
    }
}