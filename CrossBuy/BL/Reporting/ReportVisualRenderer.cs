using System.Globalization;
using System.Net;
using System.Text;

namespace CrossBuy.BL.Reporting
{
    // ============================================================================================
    // THE VISUAL RENDERER — one definition, three outputs.
    //
    // §12 is explicit: the designer and the renderer must consume the same persisted definition, and Print
    // Preview must not be a separately maintained template. So this is the ONLY thing that turns a
    // ReportVisualLayout into a document, and the preview pane, the print view and the PDF all call it. A
    // difference between what the designer showed and what printed is therefore not expressible.
    //
    // PAGINATION IS COMPUTED HERE, NOT LEFT TO CSS. `page-break` alone cannot tell a footer what page it is on,
    // and "Page 3 of 7" needs the 7 before the first page is emitted. So the renderer measures the printable
    // box, works out how many detail rows fit once the bands that repeat are subtracted, and emits explicit
    // pages. That also makes the preview honest: what you see paginated on screen is what comes out.
    //
    // RTL IS ONE LAYOUT, NOT TWO. Element X is stored as a distance from the CONTENT START edge and emitted as
    // `inset-inline-start`, which the browser resolves against `dir`. The same saved document is therefore
    // correct in both directions with no mirrored copy — §2's requirement that there not be two designers.
    //
    // EVERY TEXT NODE IS HTML-ENCODED. That is the single defence against script in a text element, and it is
    // here rather than at save time on purpose: encoding at the boundary that builds markup means a value
    // reaching the page by any route is encoded, including one an older layout stored before a validator
    // existed.
    // ============================================================================================
    public sealed class ReportVisualRenderContext
    {
        public required ReportVisualLayout Layout { get; init; }
        public required ReportDefinition Definition { get; init; }
        public required ReportView Data { get; init; }

        // assetId → data URI. Resolved by the caller through IReportAssetService, so the renderer never reads
        // a file and never holds a path.
        public IReadOnlyDictionary<int, string> Assets { get; init; } = new Dictionary<int, string>();

        public string ReportTitle { get; init; } = "";
        public bool Arabic { get; init; }
        public DateTime Now { get; init; } = DateTime.Now;

        // true = the on-screen designer preview (page shadows, no @page). false = a print/PDF document.
        public bool ScreenPreview { get; init; }
    }

    public interface IReportVisualRenderer
    {
        string Render(ReportVisualRenderContext context);
    }

    public sealed class ReportVisualRenderer : IReportVisualRenderer
    {
        public string Render(ReportVisualRenderContext ctx)
        {
            var layout = ctx.Layout;
            var page = layout.Page;
            var (paperW, paperH) = ReportPaper.Oriented(page);
            var contentW = ReportPaper.ContentWidthMm(page);
            var contentH = ReportPaper.ContentHeightMm(page);
            var dir = page.Rtl ? "rtl" : "ltr";

            var reportHeader = layout.Band(ReportBandKind.ReportHeader);
            var pageHeader = layout.Band(ReportBandKind.PageHeader);
            var groupHeader = layout.Band(ReportBandKind.GroupHeader);
            var detail = layout.Band(ReportBandKind.Detail);
            var groupFooter = layout.Band(ReportBandKind.GroupFooter);
            var pageFooter = layout.Band(ReportBandKind.PageFooter);
            var reportFooter = layout.Band(ReportBandKind.ReportFooter);

            // ---- rows, grouped if a group band names a field ------------------------------------------
            var rows = ctx.Data.Rows;
            var groupKey = groupHeader?.GroupFieldKey ?? groupFooter?.GroupFieldKey;

            // GROUPING IS THE ENGINE'S SEMANTICS, applied here only as presentation order: rows are bucketed
            // by the stored value of one groupable field. §8 forbids a second grouping implementation, so this
            // does not re-sort, re-aggregate or re-shape — it walks what it was given.
            var groups = groupKey == null
                ? new List<(string? Key, List<ReportRow> Rows)> { (null, rows.ToList()) }
                : rows.GroupBy(r => Text(Value(r, groupKey)))
                      .Select(g => ((string?)g.Key, g.ToList()))
                      .ToList();

            // ---- pagination ---------------------------------------------------------------------------
            double repeatH = (pageHeader?.HeightMm ?? 0) + (pageFooter?.HeightMm ?? 0);
            double detailH = Math.Max(1, detail?.HeightMm ?? 8);

            // ────────────────────────────────────────────────────────────────────────────────────────
            // A TABLE IS ITS OWN REPEATER, and getting this wrong is quadratic rather than merely untidy.
            //
            // A Detail band normally prints ONCE PER ROW: that is what a banded report means, and it is
            // right for a band made of field elements. A TABLE element is different — it lays out the rows
            // itself. Rendering both repetitions multiplies them: the first runtime document, over nine
            // invoices, emitted 10 000 table rows and a hundred totals rows, because every one of the
            // hundred detail repetitions re-drew the whole set.
            //
            // So a Detail band that CONTAINS a table prints once per run of consecutive rows, and the table
            // is handed exactly that run. Band height then means "the height of ONE row", which is what the
            // designer's own grid already implies, and the band stretches to the run it was given.
            // ────────────────────────────────────────────────────────────────────────────────────────
            bool detailIsTable = detail != null
                && detail.Elements.Any(e => e.Kind == ReportElementKind.Table);

            // A table's header row prints at the top of every run, so it costs one row of space.
            double tableHeadH = detailIsTable ? detailH : 0;

            // A "unit" is one printable strip: a group header, a detail row, or a group footer. Treating them
            // uniformly is what keeps a group from being split across a page break in a way nobody intended.
            var units = new List<(string Kind, ReportRow? Row, string? GroupKey, List<ReportRow>? GroupRows)>();
            foreach (var (key, groupRows) in groups)
            {
                if (groupHeader != null && key != null) units.Add(("gh", null, key, groupRows));
                foreach (var row in groupRows) units.Add(("d", row, key, groupRows));
                if (groupFooter != null && key != null) units.Add(("gf", null, key, groupRows));
            }

            double UnitHeight(string kind) => kind switch
            {
                "gh" => groupHeader?.HeightMm ?? 0,
                "gf" => groupFooter?.HeightMm ?? 0,
                _ => detailH,
            };

            var pages = new List<List<(string Kind, ReportRow? Row, string? GroupKey, List<ReportRow>? GroupRows)>>();
            var current = new List<(string, ReportRow?, string?, List<ReportRow>?)>();
            double used = (reportHeader?.HeightMm ?? 0);   // the report header prints once, on page 1

            bool headCharged = false;

            foreach (var unit in units)
            {
                var h = UnitHeight(unit.Kind);

                // The header row is charged once per page, the first time a detail row lands on it.
                if (detailIsTable && unit.Kind == "d" && !headCharged) { h += tableHeadH; headCharged = true; }

                if (used + h > contentH - repeatH && current.Count > 0)
                {
                    pages.Add(current.Select(x => (x.Item1, x.Item2, x.Item3, x.Item4)).ToList());
                    current = new List<(string, ReportRow?, string?, List<ReportRow>?)>();
                    used = 0;
                    headCharged = false;
                    if (detailIsTable && unit.Kind == "d") { h = UnitHeight(unit.Kind) + tableHeadH; headCharged = true; }
                }
                current.Add((unit.Kind, unit.Row, unit.GroupKey, unit.GroupRows));
                used += h;
            }

            // The report footer needs room on the last page, or it gets one of its own.
            double footerH = reportFooter?.HeightMm ?? 0;
            if (current.Count > 0) pages.Add(current.Select(x => (x.Item1, x.Item2, x.Item3, x.Item4)).ToList());
            if (pages.Count == 0) pages.Add(new List<(string, ReportRow?, string?, List<ReportRow>?)>());

            if (footerH > 0 && used + footerH > contentH - repeatH) pages.Add(new());

            int totalPages = pages.Count;

            // WHERE THE TOTALS ROW GOES: after the last row, decided by COUNTING rows rather than by
            // guessing at a position. Two attempts at guessing were both wrong in a way only a real document
            // showed:
            //
            //   "on the last page"      — an unfittable report footer appends an empty page, so the anchor
            //                             landed on a page with no table and the totals vanished.
            //   "on the last run of a
            //    page"                  — a group footer follows the final rows, so the last run was flushed
            //                             as a non-final one and the totals vanished again.
            //
            // A count cannot be wrong about this: the run that carries the last row is the final run, whatever
            // happens to follow it.
            int totalDetailRows = units.Count(u => u.Kind == "d");
            int emittedRows = 0;

            // ---- document -----------------------------------------------------------------------------
            var sb = new StringBuilder(16 * 1024);

            // FRAGMENT vs DOCUMENT, decided by the one flag that already distinguishes them. A screen preview is
            // embedded INSIDE the designer's own page, so it must not carry a second <html>/<head>; a print or PDF
            // document is standalone and must. Both come out of the same builder, which is what keeps the preview
            // honest — there is no second markup path that could drift.
            if (!ctx.ScreenPreview)
            {
                sb.Append("<!DOCTYPE html><html dir=\"").Append(dir).Append("\" lang=\"")
                  .Append(ctx.Arabic ? "ar" : "en").Append("\"><head><meta charset=\"utf-8\">");
                sb.Append("<title>").Append(Enc(ctx.ReportTitle)).Append("</title>");
                sb.Append("<style>");
                Css(sb, paperW, paperH, contentW, contentH, page, ctx.ScreenPreview);
                sb.Append("</style></head><body class=\"cbv\">");
            }
            else
            {
                sb.Append("<style>");
                Css(sb, paperW, paperH, contentW, contentH, page, ctx.ScreenPreview);
                sb.Append("</style><div class=\"cbv\" dir=\"").Append(dir).Append("\">");
            }

            for (int p = 0; p < totalPages; p++)
            {
                bool first = p == 0, last = p == totalPages - 1;
                sb.Append("<section class=\"cbv-page\"><div class=\"cbv-content\">");

                double y = 0;

                if (first && reportHeader != null)
                {
                    Band(sb, ctx, reportHeader, null, null, y, contentW, p + 1, totalPages);
                    y += reportHeader.HeightMm;
                }

                if (pageHeader != null)
                {
                    Band(sb, ctx, pageHeader, null, null, y, contentW, p + 1, totalPages);
                    y += pageHeader.HeightMm;
                }

                var run = new List<ReportRow>();
                string? runKey = null;

                void FlushRun()
                {
                    if (run.Count == 0 || detail == null) return;

                    // ONE render for the whole run, with the run as the table's scope and a band tall enough
                    // to hold it. Height is rows + one header row, matching what pagination charged.
                    var runHeight = (run.Count + 1) * detailH;

                    // The totals row belongs after the LAST ROW OF THE REPORT, and it sums every row rather
                    // than the run in front of it.
                    emittedRows += run.Count;
                    var isFinal = emittedRows >= totalDetailRows;

                    Band(sb, ctx, detail, null, run.ToList(), y, contentW, p + 1, totalPages, runKey,
                         heightMm: runHeight, stretchTable: true,
                         showTableTotals: isFinal, tableTotalScope: rows.ToList());
                    y += runHeight;
                    run.Clear();
                }

                foreach (var (kind, row, gkey, grows) in pages[p])
                {
                    if (detailIsTable && kind == "d")
                    {
                        if (row != null) { run.Add(row); runKey = gkey; }
                        continue;
                    }

                    FlushRun();

                    var band = kind switch
                    {
                        "gh" => groupHeader,
                        "gf" => groupFooter,
                        _ => detail,
                    };
                    if (band == null) continue;

                    Band(sb, ctx, band, row, grows, y, contentW, p + 1, totalPages, gkey);
                    y += band.HeightMm;
                }

                FlushRun();

                if (last && reportFooter != null)
                {
                    // Anchored to the bottom of the printable box rather than after the last row: a totals
                    // block that floats up the page when a report is short looks like a bug to the reader.
                    var footerY = Math.Max(y, contentH - (pageFooter?.HeightMm ?? 0) - reportFooter.HeightMm);
                    Band(sb, ctx, reportFooter, null, rows.ToList(), footerY, contentW, p + 1, totalPages);
                }

                if (pageFooter != null)
                {
                    Band(sb, ctx, pageFooter, null, null, contentH - pageFooter.HeightMm, contentW,
                         p + 1, totalPages);
                }

                sb.Append("</div></section>");
            }

            sb.Append(ctx.ScreenPreview ? "</div>" : "</body></html>");
            return sb.ToString();
        }

        // ---- one band ---------------------------------------------------------------------------------
        private static void Band(StringBuilder sb, ReportVisualRenderContext ctx, ReportBand band,
            ReportRow? row, List<ReportRow>? scope, double topMm, double contentW, int pageNo, int totalPages,
            string? groupKey = null, double? heightMm = null, bool stretchTable = false,
            bool showTableTotals = true, List<ReportRow>? tableTotalScope = null)
        {
            var height = heightMm ?? band.HeightMm;

            sb.Append("<div class=\"cbv-band cbv-band-").Append(band.Kind.ToString().ToLowerInvariant())
              .Append("\" style=\"top:").Append(Mm(topMm)).Append(";height:").Append(Mm(height))
              .Append("\">");

            // Z-ORDER IS EMITTED AS z-index, in the persisted order. §11's stamp-over-signature case is exactly
            // this, and sorting here means the DOM order does not silently decide it instead.
            foreach (var e in band.Elements.OrderBy(x => x.Z))
                Element(sb, ctx, e, row, scope, contentW, pageNo, totalPages, groupKey,
                        stretchTable ? height : (double?)null, showTableTotals, tableTotalScope);

            sb.Append("</div>");
        }

        private static void Element(StringBuilder sb, ReportVisualRenderContext ctx, ReportElement e,
            ReportRow? row, List<ReportRow>? scope, double contentW, int pageNo, int totalPages, string? groupKey,
            double? bandHeightMm = null, bool showTableTotals = true, List<ReportRow>? tableTotalScope = null)
        {
            var s = e.Style ?? new ReportElementStyle();
            if (!s.Visible) return;

            // A TABLE IN A STRETCHED DETAIL BAND takes the band's height rather than the designer's box, and
            // it should: the designer sized that box for ONE row, and the band now holds a whole run. Keeping
            // the authored height would clip every row after the first.
            var heightMm = e.HeightMm;
            if (bandHeightMm is > 0 && e.Kind == ReportElementKind.Table)
                heightMm = Math.Max(1, bandHeightMm.Value - e.YMm);

            var style = new StringBuilder();
            // inset-inline-start, NOT left. This one choice is what makes a single stored layout correct in
            // both LTR and RTL.
            style.Append("inset-inline-start:").Append(Mm(e.XMm)).Append(';');
            style.Append("top:").Append(Mm(e.YMm)).Append(';');
            style.Append("width:").Append(Mm(e.WidthMm)).Append(';');
            if (heightMm > 0) style.Append("height:").Append(Mm(heightMm)).Append(';');
            style.Append("z-index:").Append(e.Z).Append(';');
            Style(style, s);

            switch (e.Kind)
            {
                case ReportElementKind.Line:
                    sb.Append("<div class=\"cbv-el cbv-line\" style=\"").Append(style)
                      .Append("border-block-start:").Append(Mm(Math.Max(0.2, s.BorderWidthMm))).Append(' ')
                      .Append(s.BorderStyle == ReportBorderStyle.None ? "solid" : s.BorderStyle.ToString().ToLowerInvariant())
                      .Append(' ').Append(s.BorderColor ?? "#333").Append(";\"></div>");
                    return;

                case ReportElementKind.Rectangle:
                    sb.Append("<div class=\"cbv-el\" style=\"").Append(style).Append("\"></div>");
                    return;

                case ReportElementKind.Image:
                {
                    // A data URI the caller resolved from the asset store. There is no src the browser fetches
                    // and no path the server reads at render time.
                    if (e.AssetId is not > 0 || !ctx.Assets.TryGetValue(e.AssetId.Value, out var uri)) return;

                    var fit = e.Fit switch
                    {
                        ReportImageFit.Cover => "cover",
                        ReportImageFit.Stretch => "fill",
                        _ => "contain",
                    };
                    if (!e.PreserveAspect && e.Fit != ReportImageFit.Stretch) fit = "fill";

                    sb.Append("<div class=\"cbv-el\" style=\"").Append(style).Append("\">")
                      .Append("<img src=\"").Append(uri).Append("\" alt=\"\" style=\"width:100%;height:100%;object-fit:")
                      .Append(fit).Append("\"></div>");
                    return;
                }

                case ReportElementKind.Table:
                {
                    Table(sb, ctx, e, style.ToString(), scope, contentW, showTableTotals, tableTotalScope);
                    return;
                }
            }

            var text = e.Kind switch
            {
                ReportElementKind.Text => e.Text ?? "",
                ReportElementKind.Field => Format(Value(row, e.FieldKey), e, ctx),
                ReportElementKind.SystemField => System(e.SystemField, ctx, pageNo, totalPages),
                ReportElementKind.Summary => Format(Summarise(scope, e.FieldKey, e.Aggregate), e, ctx),
                _ => "",
            };

            // A group header showing its own value without binding a field is the common case, so an empty
            // Text element inside a group band falls back to the group key rather than rendering blank.
            if (e.Kind == ReportElementKind.Text && string.IsNullOrEmpty(text) && groupKey != null) text = groupKey;

            sb.Append("<div class=\"cbv-el\" style=\"").Append(style).Append("\"><span>")
              .Append(Enc(text)).Append("</span></div>");
        }

        // ---- the table element ------------------------------------------------------------------------
        private static void Table(StringBuilder sb, ReportVisualRenderContext ctx, ReportElement e,
            string style, List<ReportRow>? scope, double contentW,
            bool showTotals = true, List<ReportRow>? totalScope = null)
        {
            var rows = scope ?? new List<ReportRow>();
            var s = e.Style ?? new ReportElementStyle();

            sb.Append("<div class=\"cbv-el cbv-table-wrap\" style=\"").Append(style).Append("\">");
            sb.Append("<table class=\"cbv-table\"><thead><tr>");
            foreach (var c in e.Columns)
                sb.Append("<th style=\"width:").Append(Mm(c.WidthMm)).Append(";text-align:").Append(Align(c.Align))
                  .Append("\">").Append(Enc(HeaderFor(ctx, c))).Append("</th>");
            sb.Append("</tr></thead><tbody>");

            // ROWS REPEAT FROM THE DATASET. §7 is explicit that a user must not hand-place one text element per
            // row, and this is where that promise is kept.
            foreach (var r in rows)
            {
                sb.Append("<tr>");
                foreach (var c in e.Columns)
                {
                    var v = Value(r, c.FieldKey);
                    sb.Append("<td style=\"text-align:").Append(Align(c.Align)).Append("\">")
                      .Append(Enc(FormatValue(v, c.Format, ctx))).Append("</td>");
                }
                sb.Append("</tr>");
            }
            sb.Append("</tbody>");

            // TOTALS PRINT ONCE, AT THE END, OVER EVERY ROW — and it took a real document to see why that
            // has to be stated. A paginated table renders one run per page, so summing "the rows I was given"
            // put a totals row at the foot of EVERY page, each covering only that page. Nothing said so, and a
            // reader would take the last one for the grand total and be wrong by three pages' worth.
            //
            // A per-page subtotal is a legitimate feature. It is not this one, and printing one while the
            // column header says "Total" is the kind of quiet wrongness a financial report cannot carry.
            if (showTotals && e.Columns.Any(c => c.Total != ReportAggregate.None))
            {
                var over = totalScope ?? rows;
                sb.Append("<tfoot><tr>");
                foreach (var c in e.Columns)
                {
                    var total = c.Total == ReportAggregate.None
                        ? ""
                        : FormatValue(Summarise(over, c.FieldKey, c.Total), c.Format, ctx);
                    sb.Append("<td style=\"text-align:").Append(Align(c.Align)).Append("\">")
                      .Append(Enc(total)).Append("</td>");
                }
                sb.Append("</tr></tfoot>");
            }

            sb.Append("</table></div>");
        }

        // ---- values -----------------------------------------------------------------------------------
        // A COLUMN HEADER THE READER RECOGNISES.
        //
        // An author who types a header gets theirs. One who does not used to get the raw field key —
        // "GrandTotal" printed on a document going to a customer, while the DESIGNER showed "Total" the whole
        // time. The definition already carries the bilingual title for every column, so falling back to it
        // makes the printed table agree with the screen the author was looking at, in the reader's language.
        private static string HeaderFor(ReportVisualRenderContext ctx, ReportTableColumn c)
        {
            if (!string.IsNullOrWhiteSpace(c.HeaderText)) return c.HeaderText!;

            var column = ctx.Definition.Columns
                .FirstOrDefault(x => string.Equals(x.Key, c.FieldKey, StringComparison.Ordinal));

            if (column == null) return c.FieldKey;
            return ctx.Arabic ? column.TitleAr : DisplayName.Or(column.TitleEn, column.TitleAr);
        }

        private static object? Value(ReportRow? row, string? key)
        {
            if (row == null || string.IsNullOrEmpty(key)) return null;
            return row[key];
        }

        private static object? Summarise(IReadOnlyList<ReportRow>? rows, string? key, ReportAggregate aggregate)
        {
            if (rows == null || rows.Count == 0 || string.IsNullOrEmpty(key)) return null;

            var values = rows.Select(r => Value(r, key)).Where(v => v != null).ToList();
            if (values.Count == 0) return aggregate == ReportAggregate.Count ? 0 : null;

            switch (aggregate)
            {
                case ReportAggregate.Count: return values.Count;
                case ReportAggregate.CountDistinct: return values.Select(Text).Distinct().Count();
            }

            var numbers = values.Select(v => ReportValues.AsDecimal(v!)).ToList();
            if (numbers.Count == 0) return null;

            return aggregate switch
            {
                ReportAggregate.Sum => numbers.Sum(),
                ReportAggregate.Average => numbers.Average(),
                ReportAggregate.Min => numbers.Min(),
                ReportAggregate.Max => numbers.Max(),
                _ => null,
            };
        }

        private static string System(ReportSystemField field, ReportVisualRenderContext ctx, int page, int total)
        {
            var culture = ctx.Arabic ? new CultureInfo("ar") : CultureInfo.InvariantCulture;
            return field switch
            {
                ReportSystemField.CurrentDate => ctx.Now.ToString("yyyy-MM-dd", culture),
                ReportSystemField.CurrentDateTime => ctx.Now.ToString("yyyy-MM-dd HH:mm", culture),
                ReportSystemField.PageNumber => page.ToString(culture),
                ReportSystemField.TotalPages => total.ToString(culture),
                ReportSystemField.PageXOfY => ctx.Arabic ? $"Page {page} of {total}" : $"Page {page} of {total}",
                ReportSystemField.ReportName => ctx.ReportTitle,
                _ => "",
            };
        }

        private static string Format(object? value, ReportElement e, ReportVisualRenderContext ctx)
        {
            var s = e.Style;
            var fmt = value is DateTime ? s?.DateFormat : s?.NumberFormat;
            return FormatValue(value, fmt, ctx);
        }

        private static string FormatValue(object? value, string? format, ReportVisualRenderContext ctx)
        {
            if (value == null) return "";
            var culture = ctx.Arabic ? new CultureInfo("ar") : CultureInfo.InvariantCulture;

            if (value is DateTime dt) return dt.ToString(format ?? "yyyy-MM-dd", culture);

            if (value is not string && value is IFormattable)
            {
                var dec = ReportValues.AsDecimal(value);
                return dec.ToString(format ?? "N2", culture);
            }

            return Convert.ToString(value, culture) ?? "";
        }

        private static string Text(object? v) => v?.ToString() ?? "";

        // ---- css --------------------------------------------------------------------------------------
        private static void Style(StringBuilder style, ReportElementStyle s)
        {
            if (!string.IsNullOrWhiteSpace(s.FontFamily)) style.Append("font-family:'").Append(s.FontFamily).Append("';");
            style.Append("font-size:").Append(s.FontSizePt.ToString("0.##", CultureInfo.InvariantCulture)).Append("pt;");
            if (s.Bold) style.Append("font-weight:700;");
            if (s.Italic) style.Append("font-style:italic;");
            if (s.Underline) style.Append("text-decoration:underline;");
            style.Append("text-align:").Append(Align(s.Align)).Append(';');
            style.Append("align-items:").Append(s.VerticalAlign switch
            {
                ReportVerticalAlign.Middle => "center",
                ReportVerticalAlign.Bottom => "flex-end",
                _ => "flex-start",
            }).Append(';');
            if (!string.IsNullOrWhiteSpace(s.Color)) style.Append("color:").Append(s.Color).Append(';');
            if (!string.IsNullOrWhiteSpace(s.Background)) style.Append("background:").Append(s.Background).Append(';');
            if (s.BorderStyle != ReportBorderStyle.None)
                style.Append("border:").Append(Mm(s.BorderWidthMm)).Append(' ')
                     .Append(s.BorderStyle.ToString().ToLowerInvariant()).Append(' ')
                     .Append(s.BorderColor ?? "#333").Append(';');
            style.Append("padding:").Append(Mm(s.PaddingMm)).Append(';');
        }

        private static string Align(ReportTextAlign a) => a switch
        {
            ReportTextAlign.Center => "center",
            ReportTextAlign.End => "end",
            ReportTextAlign.Justify => "justify",
            _ => "start",
        };

        private static void Css(StringBuilder sb, double paperW, double paperH, double contentW, double contentH,
            ReportPageSetup page, bool screen)
        {
            sb.Append("*{box-sizing:border-box;margin:0;padding:0}");
            sb.Append("body.cbv{font-family:Inter,Tahoma,Arial,sans-serif;font-size:9pt;color:#111;background:")
              .Append(screen ? "#e9ecef" : "#fff").Append(";}");

            if (!screen)
            {
                // @page is what makes the browser's own print pipeline — and Chromium's PDF — use the designed
                // paper rather than the user's default.
                sb.Append("@page{size:").Append(Mm(paperW)).Append(' ').Append(Mm(paperH)).Append(";margin:0}");
                sb.Append("@media print{.cbv-page{break-after:page}.cbv-page:last-child{break-after:auto}}");
            }

            sb.Append(".cbv-page{position:relative;width:").Append(Mm(paperW)).Append(";height:").Append(Mm(paperH))
              .Append(";background:#fff;overflow:hidden;");
            if (screen) sb.Append("margin:8mm auto;box-shadow:0 2px 12px rgba(0,0,0,.18);");
            sb.Append("}");

            sb.Append(".cbv-content{position:absolute;top:").Append(Mm(page.MarginTopMm))
              .Append(";inset-inline-start:").Append(Mm(page.MarginLeftMm))
              .Append(";width:").Append(Mm(contentW)).Append(";height:").Append(Mm(contentH)).Append(";}");

            sb.Append(".cbv-band{position:absolute;inset-inline-start:0;width:100%;}");
            sb.Append(".cbv-el{position:absolute;display:flex;overflow:hidden;white-space:pre-wrap;}");
            sb.Append(".cbv-el>span{width:100%;}");
            sb.Append(".cbv-table-wrap{display:block;overflow:visible;}");
            sb.Append(".cbv-table{width:100%;border-collapse:collapse;font-size:inherit;}");
            sb.Append(".cbv-table th,.cbv-table td{border-block-end:.2mm solid #ccc;padding:.6mm 1mm;}");
            sb.Append(".cbv-table thead th{border-block-end:.4mm solid #0E4A9E;font-weight:700;}");
            sb.Append(".cbv-table tfoot td{border-block-start:.4mm solid #0E4A9E;font-weight:700;}");
        }

        private static string Mm(double v) => v.ToString("0.###", CultureInfo.InvariantCulture) + "mm";

        // The one encoder. Every user-authored string on the page goes through it.
        private static string Enc(string? v) => WebUtility.HtmlEncode(v ?? "");
    }
}
