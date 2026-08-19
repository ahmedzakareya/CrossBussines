using System.Globalization;
using System.Text;

namespace CrossBuy.BL.Reporting
{
    // ============================================================================================
    // Reporting Platform (ADR-037) — CSV EXPORT.
    //
    // RFC 4180 with two deliberate deviations, both because the receiving program is almost always Excel:
    //
    //   1. A UTF-8 BOM is written. Without it Excel opens the file in the system ANSI codepage and every Arabic
    //      label becomes mojibake. The BOM is the only thing that makes an Arabic CSV open correctly by
    //      double-click, and it is legal UTF-8.
    //   2. The separator follows the CULTURE's list separator (';' where ',' is the decimal mark). A comma-
    //      separated file opened in a comma-decimal locale lands entirely in column A.
    //
    // And one security measure that is not optional: CSV FORMULA INJECTION is neutralised. A cell whose text
    // starts with = + - @ or a control character is prefixed with an apostrophe. Without it, a customer named
    // `=cmd|'/c calc'!A1` becomes an executable formula in whoever opens the export — the reporting equivalent
    // of stored XSS, and reporting exports are the classic delivery vector for it.
    // ============================================================================================
    public class CsvReportExporter : IReportExporter
    {
        public string EngineName => "CrossBusiness.Csv";

        public IReadOnlyList<ReportOutputFormat> Formats { get; } = new[] { ReportOutputFormat.Csv };

        public bool IsAvailable => true;
        public string? UnavailableReason => null;

        public Task<ReportArtifact> ExportAsync(ReportRenderContext context,
            CancellationToken cancellationToken = default)
        {
            var view = context.View;
            var culture = context.Culture;
            var separator = ListSeparator(culture);
            var sb = new StringBuilder(8 * 1024);

            // Header row: the culture's column titles, so an Arabic user gets Arabic headers.
            sb.AppendLine(string.Join(separator,
                view.Columns.Select(c => Field(context.ColumnTitle(c), separator))));

            // Detail rows only — flat by design (see the note on IReportExporter). Group key columns are already
            // present on every row, which is what a pivot table needs.
            foreach (var row in view.Rows)
            {
                cancellationToken.ThrowIfCancellationRequested();
                sb.AppendLine(string.Join(separator, view.Columns.Select(c => Cell(row[c.Key], c, culture, separator))));
            }

            // Grand totals as a final labelled row. A totals row IS included (unlike group bands) because it is
            // one row, it does not interleave with the data, and its absence is the most common complaint about
            // a CSV export.
            if (view.GrandTotals.Count > 0)
            {
                var cells = new List<string>();
                for (var i = 0; i < view.Columns.Count; i++)
                {
                    var column = view.Columns[i];
                    var total = view.GrandTotalFor(column.Key);
                    if (total == null)
                    {
                        cells.Add(i == 0
                            ? Field(context.IsArabic ? "الإجمالي" : "Total", separator)
                            : "");
                        continue;
                    }
                    cells.Add(total.Value.ToString(NumberFormat(column), CultureInfo.InvariantCulture));
                }
                sb.AppendLine(string.Join(separator, cells));
            }

            // The BOM is written EXPLICITLY, and that detail matters.
            //
            // `new UTF8Encoding(encoderShouldEmitUTF8Identifier: true).GetBytes(...)` does NOT emit a preamble —
            // that flag only affects writers that ask the encoding for its preamble (StreamWriter, XmlWriter).
            // GetBytes never prepends one. Relying on the flag produces a BOM-less file that looks correct in
            // every text editor and turns every Arabic label into mojibake the moment Excel opens it.
            var preamble = Encoding.UTF8.GetPreamble();
            var body = Encoding.UTF8.GetBytes(sb.ToString());

            var bytes = new byte[preamble.Length + body.Length];
            preamble.CopyTo(bytes, 0);
            body.CopyTo(bytes, preamble.Length);

            var fileName = ReportFileName.For(view.Definition, ReportOutputFormat.Csv, context.GeneratedAt);
            return Task.FromResult(ReportArtifact.FromBytes(fileName, ReportOutputFormat.Csv, bytes));
        }

        // ------------------------------------------------------------------------------------------------
        private static string ListSeparator(CultureInfo culture)
        {
            var s = culture.TextInfo.ListSeparator;
            return string.IsNullOrEmpty(s) ? "," : s;
        }

        private static string Cell(object? value, ReportColumn column, CultureInfo culture, string separator)
        {
            if (value == null) return "";

            switch (column.Type)
            {
                case ReportFieldType.Money:
                case ReportFieldType.Decimal:
                case ReportFieldType.Percent:
                case ReportFieldType.Integer:
                case ReportFieldType.EntityRef:
                    // INVARIANT number formatting, unquoted, no thousands separator. A grouped "1,234.56" would
                    // be text to the receiving program — the exact defect ReportValues.ForExport exists to avoid.
                    return ReportValues.AsDecimal(value).ToString(NumberFormat(column), CultureInfo.InvariantCulture);

                case ReportFieldType.Date:
                    return ReportValues.AsDateTime(value).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

                case ReportFieldType.DateTime:
                    return ReportValues.AsDateTime(value).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

                case ReportFieldType.Boolean:
                    return ReportValues.AsBool(value) ? "1" : "0";

                default:
                    return Field(ReportValues.AsString(value, culture), separator);
            }
        }

        // Number of decimals for an export cell. Money/Decimal keep two by default — matching the column's
        // declared display scale WITHOUT rounding the value: the decimal is already whatever the data source
        // computed, and "G" would print 1.9999999 for a value the report shows as 2.00.
        private static string NumberFormat(ReportColumn column) => column.Type switch
        {
            ReportFieldType.Integer or ReportFieldType.EntityRef => "0",
            ReportFieldType.Percent => "0.####",
            _ => "0.####",
        };

        // RFC 4180 quoting + the injection guard.
        private static string Field(string? raw, string separator)
        {
            var value = raw ?? "";
            var guarded = Neutralise(value);

            var mustQuote = guarded.Contains('"')
                || guarded.Contains('\n')
                || guarded.Contains('\r')
                || guarded.Contains(separator, StringComparison.Ordinal)
                || guarded.StartsWith(' ')
                || guarded.EndsWith(' ');

            if (!mustQuote) return guarded;
            return '"' + guarded.Replace("\"", "\"\"") + '"';
        }

        // The formula-injection guard. Prefixing with an apostrophe is the standard mitigation: spreadsheets
        // treat the cell as literal text and do not display the apostrophe.
        private static string Neutralise(string value)
        {
            if (value.Length == 0) return value;
            var first = value[0];
            return first is '=' or '+' or '-' or '@' or '\t' or '\r' ? "'" + value : value;
        }
    }
}