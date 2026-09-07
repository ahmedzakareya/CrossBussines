namespace CrossBuy.BL.Reporting
{
    // ============================================================================================
    // Reporting Platform (ADR-037) — XLSX EXPORT.
    //
    // REUSES the existing BL/ExcelExporter rather than introducing a second workbook builder. That is a
    // deliberate architecture decision: ExcelExporter already owns the product's spreadsheet conventions (RTL
    // sheet, brand-green header, autofilter, frozen header row, auto-width) and is used by existing screens. A
    // parallel builder here would fork those conventions, and the two would drift on the first styling change.
    //
    // It is also why this file is short: the reporting platform's contribution is deciding WHAT goes in the sheet
    // (flat detail rows, machine values, a totals row) — not how a workbook is styled.
    //
    // DECLARED LIMITATIONS of the reuse, so nobody discovers them as bugs:
    //   * The shared builder writes an RTL sheet unconditionally. An English-culture export therefore still comes
    //     out right-to-left. Fixing that means adding a direction parameter to a shared production file, which is
    //     out of scope for this slice; it is recorded as an open item in ADR-037 rather than silently patched.
    //   * The builder styles one header row, so grouping/subtotal bands cannot be expressed as Excel outline
    //     groups. Consistent with the export contract (exports are flat), but native outlining would be a real
    //     enhancement and needs a builder change.
    // ============================================================================================
    public class ExcelReportExporter : IReportExporter
    {
        public string EngineName => "CrossBusiness.Xlsx(ClosedXML)";

        public IReadOnlyList<ReportOutputFormat> Formats { get; } = new[] { ReportOutputFormat.Xlsx };

        // ClosedXML is a compile-time package reference, so this is always true. Unlike the PDF path there is
        // nothing to install.
        public bool IsAvailable => true;
        public string? UnavailableReason => null;

        public Task<ReportArtifact> ExportAsync(ReportRenderContext context,
            CancellationToken cancellationToken = default)
        {
            var view = context.View;

            var headers = view.Columns.Select(context.ColumnTitle).ToList();

            var rows = new List<IReadOnlyList<object?>>(view.RowCount + 1);
            foreach (var row in view.Rows)
            {
                cancellationToken.ThrowIfCancellationRequested();

                // ForExport, NOT Format: the cell must carry a decimal/DateTime/bool so Excel can sum, sort and
                // filter it. A formatted string would look identical on screen and be useless in a pivot table.
                rows.Add(view.Columns.Select(c => ReportValues.ForExport(row[c.Key], c)).ToList());
            }

            if (view.GrandTotals.Count > 0)
            {
                var totals = new List<object?>(view.Columns.Count);
                for (var i = 0; i < view.Columns.Count; i++)
                {
                    var column = view.Columns[i];
                    var total = view.GrandTotalFor(column.Key);
                    if (total == null)
                    {
                        totals.Add(i == 0 ? (context.IsArabic ? "الإجمالي" : "Total") : null);
                        continue;
                    }

                    // Counts are whole numbers regardless of the column's own scale.
                    totals.Add(total.Aggregate is ReportAggregate.Count or ReportAggregate.CountDistinct
                        ? (int)total.Value
                        : total.Value);
                }
                rows.Add(totals);
            }

            // Sheet name is the report CODE, not its title: worksheet names are limited to 31 characters and
            // forbid several punctuation marks, and the code is already a safe short identifier. (The builder
            // sanitises and truncates too — this just gives it something sensible to work with.)
            var sheetName = ReportFileName.Sanitise(view.Definition.Code.Replace('.', ' '));

            // The title line carries the parameters, because a spreadsheet detached from its report needs to say
            // which period it covers — the same reason the HTML header prints them.
            var title = BuildTitleLine(context);

            var bytes = ExcelExporter.Build(sheetName, headers, rows, title);
            var fileName = ReportFileName.For(view.Definition, ReportOutputFormat.Xlsx, context.GeneratedAt);
            return Task.FromResult(ReportArtifact.FromBytes(fileName, ReportOutputFormat.Xlsx, bytes));
        }

        private static string BuildTitleLine(ReportRenderContext context)
        {
            var parts = new List<string> { context.Title };

            if (context.Parameters.Count > 0)
                parts.Add(string.Join("  ·  ", context.Parameters.Select(p => $"{p.Label}: {p.Value}")));

            if (context.View.Truncated)
                parts.Add(context.IsArabic
                    ? "(The result was truncated — some rows are not shown)"
                    : "(TRUNCATED — rows are missing)");

            if (context.IsPreview)
                parts.Add(context.IsArabic ? "(معاينة)" : "(PREVIEW)");

            return string.Join("  —  ", parts);
        }
    }
}