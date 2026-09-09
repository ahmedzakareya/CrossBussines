using System.Net;
using System.Text;

namespace CrossBuy.BL.Reporting
{
    // ============================================================================================
    // Reporting Platform (ADR-037) — THE HTML RENDERER.
    //
    // Claims TWO formats because they are the same layout with different chrome:
    //   * Html      — a self-contained FRAGMENT for embedding in a screen or an email body.
    //   * PrintHtml — a complete DOCUMENT with @page rules, meant for the browser print dialog and for the PDF
    //                 renderer, which converts exactly this HTML. That is deliberate: what you print, what you
    //                 preview and what the PDF contains are one artifact, so they cannot drift apart.
    //
    // Self-contained by rule: inline <style>, no external stylesheet, no font URL, no script. A headless browser
    // converting this to PDF may have no route back to the application, and an external reference would either
    // hang the conversion or silently produce an unstyled page.
    //
    // ZERO third-party dependency. Building the HTML with a StringBuilder rather than a Razor view is a
    // deliberate architectural choice: a BL renderer that needed the MVC view engine would need an HttpContext,
    // which would make report generation impossible from a scheduler or a test.
    // ============================================================================================
    public class HtmlReportRenderer : IReportRenderer
    {
        public string EngineName => "CrossBusiness.Html";

        public IReadOnlyList<ReportOutputFormat> Formats { get; } =
            new[] { ReportOutputFormat.Html, ReportOutputFormat.PrintHtml };

        // No external requirement, so it is always usable. The property exists for the renderers that are not.
        public bool IsAvailable => true;
        public string? UnavailableReason => null;

        // REPORT STUDIO V2. Optional so a composition root that constructs this renderer by hand keeps compiling;
        // when it is absent a visual layout falls back to the column table rather than failing, which is the safe
        // direction — the report still renders, it is simply not positioned.
        private readonly IReportVisualRenderer? _visual;

        public HtmlReportRenderer(IReportVisualRenderer? visual = null) => _visual = visual;

        public Task<ReportArtifact> RenderAsync(ReportRenderContext context,
            CancellationToken cancellationToken = default)
        {
            var full = context.Format == ReportOutputFormat.PrintHtml;
            var html = Build(context, fullDocument: full);
            var fileName = ReportFileName.For(context.View.Definition, context.Format, context.GeneratedAt);
            return Task.FromResult(ReportArtifact.FromText(fileName, context.Format, html));
        }

        // Exposed so the PDF renderer converts THIS html rather than building its own. One layout, two outputs.
        public string BuildPrintDocument(ReportRenderContext context) => Build(context, fullDocument: true);

        // ------------------------------------------------------------------------------------------------
        private string Build(ReportRenderContext context, bool fullDocument)
        {
            // ---- THE V2 BRANCH ----------------------------------------------------------------------
            //
            // A resolved template that carries a positioned design is rendered by the visual renderer instead of
            // the column table. It sits HERE, in the one builder both Html and PrintHtml go through and which the
            // PDF renderer also converts, so all three outputs take the branch together and cannot disagree.
            if (context.Visual is not null && _visual is not null)
            {
                return _visual.Render(new ReportVisualRenderContext
                {
                    Layout = context.Visual,
                    Definition = context.View.Definition,
                    Data = context.View,
                    Assets = context.Assets,
                    ReportTitle = context.Title,
                    Arabic = context.IsArabic,
                    Now = context.GeneratedAt,

                    // A FRAGMENT for the embedded preview pane, a standalone DOCUMENT for print and PDF — the
                    // same distinction fullDocument already draws for the table renderer.
                    ScreenPreview = !fullDocument,
                });
            }

            var sb = new StringBuilder(16 * 1024);
            var view = context.View;
            var dir = context.Rtl ? "rtl" : "ltr";
            var lang = context.IsArabic ? "ar" : "en";

            if (fullDocument)
            {
                sb.Append("<!DOCTYPE html>\n")
                  .Append($"<html lang=\"{lang}\" dir=\"{dir}\"><head><meta charset=\"utf-8\">")
                  .Append($"<title>{E(context.Title)}</title>")
                  .Append("<style>").Append(Css(context, print: true)).Append("</style>")
                  .Append("</head><body class=\"cbrep-body\">");
            }
            else
            {
                // A fragment still carries its own <style>: it is embedded in screens whose stylesheets we do not
                // control, and an unstyled report table inside a themed page looks broken.
                sb.Append($"<div class=\"cbrep\" dir=\"{dir}\" lang=\"{lang}\">")
                  .Append("<style>").Append(Css(context, print: false)).Append("</style>");
            }

            AppendHeader(sb, context);
            AppendTable(sb, context);
            AppendFooter(sb, context);

            sb.Append(fullDocument ? "</body></html>" : "</div>");
            return sb.ToString();
        }

        private static void AppendHeader(StringBuilder sb, ReportRenderContext context)
        {
            if (!context.PageSetup.ShowHeader) return;

            sb.Append("<header class=\"cbrep-head\">");

            if (!string.IsNullOrWhiteSpace(context.Branding.LogoDataUri))
                sb.Append($"<img class=\"cbrep-logo\" src=\"{E(context.Branding.LogoDataUri!)}\" alt=\"\">");

            sb.Append("<div class=\"cbrep-titles\">");

            // NO COMPANY NAME. It used to print the caller's own company above the title, which read as
            // the report's scope rather than as its issuer - «شركة 1» over a document covering eight
            // companies - and the owner's decision is that it comes off every report, not just that one.
            //
            // Nothing identifying is lost: the title, the subtitle, the parameter strip, the generated-at
            // stamp and the footer (CR / TRN numbers from ReportEngine's branding provider) all stay.
            // ReportBranding still CARRIES the name - it is a DTO, and a later letterhead decision may want
            // it - but no renderer prints it.

            sb.Append($"<h1 class=\"cbrep-title\">{E(context.Title)}</h1>");

            if (!string.IsNullOrWhiteSpace(context.Subtitle))
                sb.Append($"<div class=\"cbrep-subtitle\">{E(context.Subtitle!)}</div>");

            // The parameter strip. See ReportParameterDisplay: a document without its parameters cannot be
            // interpreted later, so this is not optional decoration.
            if (context.Parameters.Count > 0)
            {
                sb.Append("<div class=\"cbrep-params\">");
                foreach (var p in context.Parameters)
                    sb.Append($"<span class=\"cbrep-param\"><b>{E(p.Label)}:</b> {E(p.Value)}</span>");
                sb.Append("</div>");
            }

            sb.Append("</div>");   // titles

            sb.Append("<div class=\"cbrep-meta\">");
            sb.Append($"<div>{E(context.GeneratedAt.ToString("yyyy-MM-dd HH:mm", context.Culture))}</div>");
            if (!string.IsNullOrWhiteSpace(context.GeneratedBy))
                sb.Append($"<div>{E(context.GeneratedBy!)}</div>");
            sb.Append("</div>");

            sb.Append("</header>");

            // Two honesty banners. Both are rendered, not logged: the person holding the paper is the one who
            // needs to know the document is provisional or incomplete.
            //
            // BOTH BRANCHES USED TO BE ENGLISH. The ternary tests IsArabic, and whoever wrote it varied the
            // TONE — a quiet sentence and a shouting one — rather than the language, so an Arabic report
            // carried an English warning and nobody noticed because it still read as a sentence. That is
            // also why there is no French: the strings live in code, so there are only ever two.
            if (context.IsPreview)
                sb.Append($"<div class=\"cbrep-banner cbrep-banner-preview\">{E(context.IsArabic
                    ? "معاينة — عدد الصفوف محدود، وهذه ليست النسخة النهائية"
                    : "PREVIEW — row count is limited; this is not the final document")}</div>");

            if (context.View.Truncated)
                sb.Append($"<div class=\"cbrep-banner cbrep-banner-truncated\">{E(context.IsArabic
                    ? "النتيجة مقطوعة — بعض الصفوف غير معروضة"
                    : "TRUNCATED — rows are missing from this output")}</div>");
        }

        private static void AppendTable(StringBuilder sb, ReportRenderContext context)
        {
            var view = context.View;

            sb.Append("<table class=\"cbrep-table\"><thead><tr>");
            foreach (var column in view.Columns)
            {
                var style = column.WidthMm > 0
                    ? $" style=\"width:{column.WidthMm.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)}mm\""
                    : "";
                sb.Append($"<th class=\"{AlignClass(column)}\"{style}>{E(context.ColumnTitle(column))}</th>");
            }
            sb.Append("</tr></thead><tbody>");

            if (view.RowCount == 0)
            {
                sb.Append($"<tr class=\"cbrep-empty\"><td colspan=\"{view.Columns.Count}\">")
                  .Append(E(context.IsArabic ? "لا توجد بيانات" : "No data"))
                  .Append("</td></tr>");
            }
            else if (view.IsGrouped)
            {
                foreach (var group in view.Groups) AppendGroup(sb, context, group);
            }
            else
            {
                foreach (var row in view.Rows) AppendRow(sb, context, row);
            }

            sb.Append("</tbody>");

            if (view.GrandTotals.Count > 0)
            {
                sb.Append("<tfoot><tr class=\"cbrep-grand\">");
                for (var i = 0; i < view.Columns.Count; i++)
                {
                    var column = view.Columns[i];
                    var total = view.GrandTotalFor(column.Key);

                    if (i == 0 && total == null)
                    {
                        sb.Append($"<td class=\"cbrep-total-label\">{E(context.IsArabic ? "الإجمالي" : "Total")}</td>");
                        continue;
                    }
                    sb.Append(total == null
                        ? "<td></td>"
                        : $"<td class=\"{AlignClass(column)}\">{E(FormatAggregate(total, column, context))}</td>");
                }
                sb.Append("</tr></tfoot>");
            }

            sb.Append("</table>");
        }

        private static void AppendGroup(StringBuilder sb, ReportRenderContext context, ReportGroupNode group)
        {
            var columns = context.View.Columns;

            sb.Append($"<tr class=\"cbrep-group cbrep-group-{group.Level}\">")
              .Append($"<td colspan=\"{columns.Count}\">")
              .Append($"<span class=\"cbrep-group-col\">{E(context.ColumnTitle(group.Column))}:</span> ")
              .Append($"<span class=\"cbrep-group-key\">{E(string.IsNullOrEmpty(group.KeyText)
                  ? (context.IsArabic ? "(فارغ)" : "(blank)")
                  : group.KeyText)}</span>")
              .Append($"<span class=\"cbrep-group-count\">({group.RowCount})</span>")
              .Append("</td></tr>");

            if (!group.Collapsed)
            {
                foreach (var child in group.Children) AppendGroup(sb, context, child);
                foreach (var row in group.Rows) AppendRow(sb, context, row);
            }

            if (group.Subtotals.Count > 0)
            {
                sb.Append($"<tr class=\"cbrep-subtotal cbrep-subtotal-{group.Level}\">");
                for (var i = 0; i < columns.Count; i++)
                {
                    var column = columns[i];
                    var subtotal = group.Subtotals.FirstOrDefault(s =>
                        string.Equals(s.ColumnKey, column.Key, StringComparison.Ordinal));

                    if (i == 0 && subtotal == null)
                    {
                        sb.Append($"<td class=\"cbrep-total-label\">{E(context.IsArabic ? "مجموع" : "Subtotal")} ")
                          .Append(E(group.KeyText)).Append("</td>");
                        continue;
                    }
                    sb.Append(subtotal == null
                        ? "<td></td>"
                        : $"<td class=\"{AlignClass(column)}\">{E(FormatAggregate(subtotal, column, context))}</td>");
                }
                sb.Append("</tr>");
            }
        }

        private static void AppendRow(StringBuilder sb, ReportRenderContext context, ReportRow row)
        {
            sb.Append("<tr>");
            foreach (var column in context.View.Columns)
            {
                var text = ReportValues.Format(row[column.Key], column, context.Culture);
                sb.Append($"<td class=\"{AlignClass(column)}\">{E(text)}</td>");
            }
            sb.Append("</tr>");
        }

        private static void AppendFooter(StringBuilder sb, ReportRenderContext context)
        {
            if (!context.PageSetup.ShowFooter) return;

            sb.Append("<footer class=\"cbrep-foot\">");
            if (!string.IsNullOrWhiteSpace(context.Branding.FooterNote))
                sb.Append($"<span>{E(context.Branding.FooterNote!)}</span>");

            var rowsLabel = context.IsArabic ? "عدد الصفوف" : "Rows";
            sb.Append($"<span>{E(rowsLabel)}: {context.View.RowCount.ToString("N0", context.Culture)}");
            if (context.View.TotalAvailableRows is { } total && total > context.View.RowCount)
                sb.Append($" / {total.ToString("N0", context.Culture)}");
            sb.Append("</span>");

            sb.Append($"<span class=\"cbrep-code\">{E(context.View.Definition.Code)}</span>");
            sb.Append("</footer>");
        }

        // ------------------------------------------------------------------------------------------------
        // Aggregate labels. A subtotal cell shows the NUMBER, formatted like its column — except Count, which is
        // a row count and must not be formatted with the column's money scale ("Count: 12.00" is nonsense).
        private static string FormatAggregate(ReportAggregateValue aggregate, ReportColumn column,
            ReportRenderContext context) =>
            aggregate.Aggregate is ReportAggregate.Count or ReportAggregate.CountDistinct
                ? aggregate.Value.ToString("N0", context.Culture)
                : ReportValues.Format(aggregate.Value, column, context.Culture);

        private static string AlignClass(ReportColumn column) => column.EffectiveAlign switch
        {
            ReportAlign.End => "cbrep-end",
            ReportAlign.Center => "cbrep-center",
            _ => "cbrep-start",
        };

        // HTML escaping on EVERY interpolated value without exception — titles, group keys, cell text, company
        // name, footer note. Report data is business data: a customer named `<script>` must render as text, and
        // "it's only a report" is precisely how stored XSS gets shipped.
        private static string E(string? value) => WebUtility.HtmlEncode(value ?? "");

        // ------------------------------------------------------------------------------------------------
        // CSS. Logical properties (inline-start/inline-end) rather than left/right, so ONE stylesheet is correct
        // in both RTL and LTR — the dir attribute flips it. Duplicating the sheet per direction is how RTL parity
        // rots.
        // ------------------------------------------------------------------------------------------------
        private static string Css(ReportRenderContext context, bool print)
        {
            var brand = context.Branding.PrimaryColor;
            var setup = context.PageSetup;
            var inv = System.Globalization.CultureInfo.InvariantCulture;

            var sb = new StringBuilder();

            if (print)
            {
                // @page carries the geometry so the browser paginates identically whether the user prints it or
                // the PDF converter rasterises it. The converter is ALSO told the same size/margins, because a
                // converter that ignores @page must still produce the right page.
                sb.Append($"@page{{size:{PageSizeCss(setup)};")
                  .Append($"margin:{setup.MarginTopMm.ToString(inv)}mm {setup.MarginRightMm.ToString(inv)}mm ")
                  .Append($"{setup.MarginBottomMm.ToString(inv)}mm {setup.MarginLeftMm.ToString(inv)}mm;}}");
                sb.Append("html,body{margin:0;padding:0;}");
            }

            // THE TEMPLATE'S FACE, and only that. ReportTypography.DocumentFamily is the single place the
            // decision is made, so this renderer and the visual one cannot disagree again; it also explains
            // why the platform stack is no longer appended behind an author's choice. A face chosen in
            // Report Studio is validated against the approved list before it is ever stored, so what
            // arrives here is a bare family name and safe to interpolate.
            var docFamily = ReportTypography.DocumentFamily(setup.FontFamily, context.IsArabic);

            // The same embedded face. A report without a positioned design is still rendered from its
            // template, and its typography must be just as portable — otherwise the two renderers would
            // disagree about the font again, in a way that only shows up on someone else's machine.
            ReportFontLibrary.AppendFaceFor(sb, setup.FontFamily);
            var docSizePt = setup.FontSizePt is > 0 ? setup.FontSizePt!.Value : 9.5;
            sb.Append(".cbrep,.cbrep-body{font-family:").Append(docFamily)
              .Append(";font-size:").Append(docSizePt.ToString("0.##", inv)).Append("pt;color:#181c32;}");
            sb.Append(".cbrep-head{display:flex;align-items:flex-start;gap:12px;border-block-end:2px solid ")
              .Append(brand).Append(";padding-block-end:8px;margin-block-end:10px;}");
            sb.Append(".cbrep-logo{max-height:52px;max-width:160px;object-fit:contain;}");
            sb.Append(".cbrep-titles{flex:1;min-width:0;}");
            sb.Append(".cbrep-title{font-size:15pt;font-weight:700;margin:2px 0 0;color:").Append(brand).Append(";}");
            sb.Append(".cbrep-subtitle{font-size:9pt;color:#5e6278;margin-block-start:2px;}");
            sb.Append(".cbrep-params{margin-block-start:5px;font-size:8.5pt;color:#3f4254;}");
            sb.Append(".cbrep-param{display:inline-block;margin-inline-end:14px;}");
            sb.Append(".cbrep-meta{font-size:8pt;color:#7e8299;text-align:end;white-space:nowrap;}");

            sb.Append(".cbrep-banner{padding:5px 8px;margin-block-end:8px;font-size:8.5pt;font-weight:600;")
              .Append("border-inline-start:3px solid;}");
            // FROM THE BRANDING CONTRACT, not from literals. These two rules were the only colours in the
            // document that a tenant could not change: rebrand everything and the warnings stayed amber.
            sb.Append(".cbrep-banner-preview{background:").Append(context.Branding.WarningSurface)
              .Append(";border-color:").Append(context.Branding.WarningColor)
              .Append(";color:").Append(context.Branding.WarningText).Append(";}");
            sb.Append(".cbrep-banner-truncated{background:").Append(context.Branding.DangerSurface)
              .Append(";border-color:").Append(context.Branding.DangerColor)
              .Append(";color:").Append(context.Branding.DangerText).Append(";}");

            sb.Append(".cbrep-table{width:100%;border-collapse:collapse;}");
            sb.Append(".cbrep-table th{background:").Append(brand)
              .Append(";color:#fff;font-weight:600;padding:5px 6px;border:1px solid ").Append(brand).Append(";}");
            sb.Append(".cbrep-table td{padding:4px 6px;border:1px solid #e4e6ef;vertical-align:top;}");
            sb.Append(".cbrep-table tbody tr:nth-child(even) td{background:#fbfbfc;}");

            sb.Append(".cbrep-start{text-align:start;}.cbrep-end{text-align:end;}.cbrep-center{text-align:center;}");
            sb.Append(".cbrep-empty td{text-align:center;color:#a1a5b7;padding:14px;}");

            sb.Append(".cbrep-group td{background:#E7F0FF;font-weight:600;color:").Append(brand).Append(";}");
            sb.Append(".cbrep-group-1 td{background:#F3F7FF;}");
            sb.Append(".cbrep-group-col{color:#7e8299;font-weight:500;}");
            sb.Append(".cbrep-group-count{color:#a1a5b7;font-weight:400;margin-inline-start:6px;}");
            sb.Append(".cbrep-subtotal td{background:#F3F7FF;font-weight:600;border-block-start:1px solid ")
              .Append(brand).Append("33;}");
            sb.Append(".cbrep-grand td{background:").Append(brand)
              .Append(";color:#fff;font-weight:700;border:1px solid ").Append(brand).Append(";}");
            sb.Append(".cbrep-total-label{text-align:start;}");

            sb.Append(".cbrep-foot{display:flex;justify-content:space-between;gap:10px;margin-block-start:8px;")
              .Append("padding-block-start:5px;border-block-start:1px solid #e4e6ef;font-size:7.5pt;color:#7e8299;}");
            sb.Append(".cbrep-code{font-family:").Append(ReportTypography.Monospace).Append(";}");

            if (print && setup.RepeatHeaderRow)
            {
                // thead repeats per page and a row is never split across a page break. Both are one line here and
                // both are the difference between a readable 40-page trial balance and an unusable one.
                sb.Append("thead{display:table-header-group;}tfoot{display:table-footer-group;}");
                sb.Append("tr{page-break-inside:avoid;break-inside:avoid;}");
            }

            return sb.ToString();
        }

        private static string PageSizeCss(ReportPageSetup setup)
        {
            var landscape = setup.Orientation == ReportOrientation.Landscape ? " landscape" : "";
            return setup.PageSize switch
            {
                ReportPageSize.A4 => "A4" + landscape,
                ReportPageSize.A5 => "A5" + landscape,
                ReportPageSize.Letter => "Letter" + landscape,
                ReportPageSize.Legal => "Legal" + landscape,

                // A continuous roll has no fixed height. 80mm × auto is the standard receipt profile; a fixed
                // height would either clip a long receipt or pad a short one with blank paper.
                ReportPageSize.Thermal80 => "80mm auto",
                _ => "A4" + landscape,
            };
        }
    }

    // Report file names in one place.
    //
    // Sanitised to ASCII-safe characters because these names travel through Content-Disposition headers, the
    // archive store's filesystem paths and email attachments. An Arabic report title in a raw filename works in
    // one of those three and fails in the other two, so the CODE is used and the title is not.
    public static class ReportFileName
    {
        public static string For(ReportDefinition definition, ReportOutputFormat format, DateTime generatedAt)
        {
            var stem = definition.Code.Replace('.', '_');
            var stamp = generatedAt.ToString("yyyyMMdd-HHmmss");
            return $"{Sanitise(stem)}_{stamp}.{ReportFormats.Extension(format)}";
        }

        public static string Sanitise(string value)
        {
            var sb = new StringBuilder(value.Length);
            foreach (var ch in value)
                sb.Append(char.IsLetterOrDigit(ch) && ch < 128 ? ch : (ch is '-' or '_' ? ch : '_'));
            return sb.ToString().Trim('_');
        }
    }
}