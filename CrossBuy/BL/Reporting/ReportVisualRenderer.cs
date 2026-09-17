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

        // role → data URI: the company's own mark and the branch's, resolved from the tenant's records
        // rather than uploaded per template. Same rule as Assets — the renderer gets bytes, never a path.
        public IReadOnlyDictionary<ReportImageRole, string> RoleImages { get; init; } =
            new Dictionary<ReportImageRole, string>();

        public string ReportTitle { get; init; } = "";
        public bool Arabic { get; init; }
        public DateTime Now { get; init; } = DateTime.Now;

        // true = the on-screen designer preview (page shadows, no @page). false = a print/PDF document.
        public bool ScreenPreview { get; init; }

        // THE RUN'S OWN KIND, which is not the same question as ScreenPreview.
        //
        // ScreenPreview says "this is a fragment for an embedded pane" — a matter of markup. IsPreview
        // says "this run fetched fewer rows than the real thing would", which is a matter of TRUTH, and a
        // document that does not admit it is a document someone will act on.
        public bool IsPreview { get; init; }

        // Whether the fetch hit its ceiling. Read from the view rather than passed twice, so the notice
        // and the data cannot disagree.
        public bool Truncated { get; init; }

        // The tones and the identity, so the notice looks like the product rather than like this file.
        public ReportBranding Branding { get; init; } = ReportBranding.Default;

        /// Resolved sub-reports, keyed by the element id that asked for one.
        public IReadOnlyDictionary<string, ReportSubReportData> SubReports { get; init; } =
            new Dictionary<string, ReportSubReportData>(StringComparer.Ordinal);
    }

    // ============================================================================================
    // A RESOLVED SUB-REPORT — the child's rows, already fetched, already authorized, already bucketed.
    //
    // THE RENDERER IS HANDED THIS; IT NEVER FETCHES IT. That split is the whole design. A renderer that
    // could reach a data source would be a second data path past the engine's permission gate, and it
    // would run one query per group — the N+1 that turns a 40-group report into 40 round trips. The
    // engine runs ONE query filtered to the groups on the page and buckets the answer here.
    //
    // Denied is a STATE, not an absence. A child the reader may not run prints a refusal where the table
    // would have been, because a silently empty sub-report reads as "this customer has no invoices".
    // ============================================================================================
    public sealed class ReportSubReportData
    {
        public required string Title { get; init; }
        public IReadOnlyList<ReportColumn> Columns { get; init; } = Array.Empty<ReportColumn>();

        /// Child rows keyed by the parent group's value. Empty for an unlinked sub-report.
        public IReadOnlyDictionary<string, IReadOnlyList<ReportRow>> ByLink { get; init; } =
            new Dictionary<string, IReadOnlyList<ReportRow>>(StringComparer.Ordinal);

        /// The whole child, for a sub-report embedded in a report band.
        public IReadOnlyList<ReportRow> Unlinked { get; init; } = Array.Empty<ReportRow>();

        public bool Denied { get; init; }
        public bool Truncated { get; init; }
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
            // THE AUTHOR'S CHOICE, and the language when they have not made one. `page.Rtl` used to
            // decide this: a bool stored with the design and defaulting to true, so an Arabic-authored
            // template printed English right to left — columns mirrored, totals on the wrong edge.
            // The saved design still mirrors itself either way, because element X is a distance from
            // the CONTENT START edge emitted as `inset-inline-start`: one layout, resolved by `dir`.
            var dir = page.Direction switch
            {
                ReportPageDirection.Rtl => "rtl",
                ReportPageDirection.Ltr => "ltr",
                _ => ctx.Arabic ? "rtl" : "ltr",
            };

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
            // AN EMPTY BAND RESERVES NO PAPER.
            //
            // Blank() gives all seven bands a height, and a layout typically fills two or three. The
            // rest were still charged: 24mm off every page for an empty page header and footer, and a
            // whole extra sheet for an empty report footer that would not fit on the last one — which
            // is exactly the blank "page 4 of 4" a four-page report ended with.
            //
            // Nothing can be lost by giving that space back: a band with no elements has nothing to
            // draw. A band the designer HAS put something in is charged exactly as before.
            static double Charged(ReportBand? band) =>
                band is { Elements.Count: > 0 } ? band.HeightMm : 0;

            double repeatH = Charged(pageHeader) + Charged(pageFooter);
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
            double used = Charged(reportHeader);   // the report header prints once, on page 1

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

            // The report footer needs room on the last page, or it gets one of its own — but ONLY if
            // there is a footer to print. An empty band asking for a sheet of its own is the blank
            // trailing page this rule removes.
            double footerH = Charged(reportFooter);
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
                Css(sb, paperW, paperH, contentW, contentH, page, ctx.ScreenPreview, ctx.Arabic, ctx.Branding);
                sb.Append("</style></head><body class=\"cbv\">");
            }
            else
            {
                sb.Append("<style>");
                Css(sb, paperW, paperH, contentW, contentH, page, ctx.ScreenPreview, ctx.Arabic, ctx.Branding);
                sb.Append("</style><div class=\"cbv\" dir=\"").Append(dir).Append("\">");
            }

            // THE NOTICES, ABOVE THE SHEETS. A designed report printed none at all: these lived only in
            // the column renderer, so the same run was honest as a plain table and silent as a designed
            // document — and the designed one is what people hand to someone.
            //
            // OUTSIDE the .cbv-page boxes, deliberately. A sheet is a fixed geometry in millimetres and
            // the engine has already decided how many of them there are; a block placed INSIDE the first
            // one would push its content past the printable box and add a page nobody asked for. Above
            // them, the notice sits in the preview pane where it is read and is simply not part of the
            // paper.
            //
            // WHICH MEANS a truncated PRINTED document still says nothing, and that gap is real rather
            // than closed. Its right home is a band the author controls in Studio, not a block this
            // renderer injects into someone's layout.
            if (ctx.IsPreview) ReportNotice.Render(sb, ReportNotice.Kind.Preview, ctx.Arabic);
            if (ctx.Truncated) ReportNotice.Render(sb, ReportNotice.Kind.Truncated, ctx.Arabic);

            for (int p = 0; p < totalPages; p++)
            {
                bool first = p == 0, last = p == totalPages - 1;
                sb.Append("<section class=\"cbv-page\"><div class=\"cbv-content\">");

                double y = 0;

                if (first && reportHeader != null)
                {
                    // THE FIRST ROW, so a Field element in the letterhead resolves.
                    //
                    // This band was rendered with a null row, which meant a bound field in it could never
                    // print anything — and that is exactly where a DOCUMENT states itself: its number, its
                    // date, the party it is for. The starter layout puts those in the lines table instead,
                    // repeating them down the page as columns, which is why a purchase order read as a
                    // query result rather than as a purchase order.
                    //
                    // A document's header fields carry the SAME value on every row by construction — the
                    // trade and voucher datasets repeat the header deliberately, for this — so the first
                    // row is the document's own header. The whole set goes in as scope so a Summary works
                    // here too.
                    //
                    // Safe by inspection: these elements previously rendered EMPTY, so nothing that reads
                    // correctly today can start reading differently.
                    Band(sb, ctx, reportHeader, rows.FirstOrDefault(), rows.ToList(), y, contentW,
                         p + 1, totalPages);
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
                    // Same reasoning for the closing band: a Summary already worked here because the
                    // scope was passed, but a Field did not — and a document's stored totals are FIELDS,
                    // not sums of the lines. A footer that could only add up the rows would print a total
                    // that disagrees with the invoice whenever there is a header discount.
                    Band(sb, ctx, reportFooter, rows.FirstOrDefault(), rows.ToList(), footerY, contentW,
                         p + 1, totalPages);
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
                    // A data URI the caller resolved — from the asset store when the author picked a
                    // picture, otherwise from the element's ROLE, which is how "the company logo" and "the
                    // branch logo" become the tenant's own marks instead of a copy pasted into a template.
                    // Either way there is no src the browser fetches and no path the server reads here.
                    //
                    // THE UPLOADED ASSET WINS. An author who chose a picture chose it; the role is the
                    // fallback, not an override.
                    string? uri = null;
                    if (e.AssetId is > 0) ctx.Assets.TryGetValue(e.AssetId.Value, out uri);
                    if (string.IsNullOrEmpty(uri) && e.ImageRole != ReportImageRole.Custom)
                        ctx.RoleImages.TryGetValue(e.ImageRole, out uri);
                    if (string.IsNullOrEmpty(uri)) return;

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

                case ReportElementKind.QrCode:
                {
                    // The payload is the BOUND FIELD when there is one, otherwise the typed text. A
                    // field wins because a document's code is a fact about that document; the text is
                    // for a fixed destination, like a portal address the same on every copy.
                    var payload = !string.IsNullOrWhiteSpace(e.FieldKey)
                        ? Text(Value(row, e.FieldKey))
                        : TextFor(ctx, e);

                    var qr = ReportQrCode.DataUri(payload, e.QrEcc, e.QrModulePixels);
                    if (string.IsNullOrEmpty(qr)) return;

                    // contain, always: a QR stretched to a non-square box is a QR that does not scan.
                    sb.Append("<div class=\"cbv-el\" style=\"").Append(style).Append("\">")
                      .Append("<img src=\"").Append(qr)
                      .Append("\" alt=\"\" style=\"width:100%;height:100%;object-fit:contain\"></div>");
                    return;
                }

                case ReportElementKind.Table:
                {
                    Table(sb, ctx, e, style.ToString(), scope, contentW, showTableTotals, tableTotalScope);
                    return;
                }

                case ReportElementKind.Chart:
                {
                    Chart(sb, ctx, e, style.ToString(), scope);
                    return;
                }

                case ReportElementKind.CrossTab:
                {
                    CrossTab(sb, ctx, e, style.ToString(), scope);
                    return;
                }

                case ReportElementKind.SubReport:
                {
                    SubReport(sb, ctx, e, style.ToString(), groupKey);
                    return;
                }
            }

            var text = e.Kind switch
            {
                // The author's own words, in the reader's language when there is one for it.
                ReportElementKind.Text => TextFor(ctx, e),
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

        // ---- charts and cross-tabs -------------------------------------------------------------------
        //
        // THE ONE BUCKETING ROUTINE BOTH USE. A chart's categories and a cross-tab's axes are the same
        // question asked of different fields, so they are grouped by the same code and capped by the same
        // ceiling — two implementations would have drifted the first time one of them learned about nulls.
        //
        // THE CEILING NEVER DROPS DATA SILENTLY. Everything past it folds into one labelled bucket, so a
        // total under a capped chart still adds up to the total above it.
        private const double PxPerMm = 96.0 / 25.4;

        private static List<(string Label, decimal Value)> Buckets(
            ReportVisualRenderContext ctx, IReadOnlyList<ReportRow> rows, ReportElement e, bool naturalOrder)
        {
            var groups = rows
                .GroupBy(r => Key(ctx, e, Value(r, e.CategoryFieldKey)))
                .Select(g => (
                    Label: g.Key,
                    Value: Dec(Summarise(g.ToList(), e.FieldKey, e.Aggregate)),
                    // The earliest underlying date in the bucket, kept only to order a time axis. It is the
                    // RAW value rather than the label, because the label is whatever the author's date format
                    // produced and "Mar 2025" does not sort.
                    At: g.Select(r => Value(r, e.CategoryFieldKey)).OfType<DateTime>()
                         .DefaultIfEmpty(DateTime.MinValue).Min()))
                .ToList();

            // A TIME AXIS RUNS FORWARDS, WHATEVER ORDER THE ROWS ARRIVED IN.
            //
            // "Natural order" used to mean the order the source happened to return, and the sales source
            // returns newest first - so a trend line put September on the left and the previous March on the
            // right, and every rise in it was a fall. A reader does not check the axis direction before
            // believing a line; they read the slope. That made it not a cosmetic problem.
            //
            // Sorting by the underlying date rather than by the label is what makes this work for any date
            // format an author chooses. A non-date category keeps the source's order exactly as before:
            // there the sequence is the author's own doing and nothing here can improve on it.
            var grouped = !naturalOrder
                ? groups.OrderByDescending(x => x.Value).Select(x => (x.Label, x.Value)).ToList()
                : groups.Any(x => x.At > DateTime.MinValue)
                    ? groups.OrderBy(x => x.At).Select(x => (x.Label, x.Value)).ToList()
                    : groups.Select(x => (x.Label, x.Value)).ToList();

            var cap = Math.Max(2, e.MaxCategories);
            if (grouped.Count <= cap) return grouped;

            var kept = grouped.Take(cap - 1).ToList();
            var rest = grouped.Skip(cap - 1).ToList();
            kept.Add((Other(ctx, rest.Count), rest.Sum(x => x.Value)));
            return kept;
        }

        // A CHART WITH A SECOND FIELD. One measure, one category axis, and now optionally a series field
        // that splits each category. "Sales by month" and "sales by month per branch" are the same question
        // at two levels of detail, and until this existed only the first could be drawn - the second had to
        // become a cross-tab, which is a table, and a table is not what a trend is for.
        //
        // BOTH AXES ARE CAPPED, AND FOR DIFFERENT REASONS. Categories are capped because the drawing has a
        // finite width; series are capped because the EYE has a finite number of colours it can tell apart,
        // and a legend of twenty shades of one hue is a legend nobody reads. So the series ceiling is its own
        // small number rather than MaxCategories, which an author sets with the horizontal axis in mind.
        //
        // NEITHER CEILING DROPS DATA. Both fold their tail into one labelled bucket, so a grouped column
        // chart still totals what an ungrouped one would - which is the property that lets a reader put this
        // chart beside the summary above it and have the two agree.
        private const int SeriesCeiling = 6;

        private sealed class ChartSeries
        {
            public string Name = "";
            public decimal[] Values = Array.Empty<decimal>();
        }

        private static (List<string> Categories, List<ChartSeries> Series) SeriesBuckets(
            ReportVisualRenderContext ctx, IReadOnlyList<ReportRow> rows, ReportElement e, bool naturalOrder)
        {
            // The category axis is chosen by the SAME routine and the same ceiling a single-series chart
            // uses, so adding a series field never silently re-picks or re-orders the categories. A reader
            // comparing the two charts is comparing the same axis.
            var categories = Buckets(ctx, rows, e, naturalOrder).Select(b => b.Label).ToList();
            var folded = categories.Count > 0 && categories[^1].StartsWith(
                ctx.Arabic ? "أخرى (" : "Other (", StringComparison.Ordinal);
            var index = new Dictionary<string, int>(StringComparer.Ordinal);
            for (var i = 0; i < categories.Count; i++) index[categories[i]] = i;

            // Series are ranked by their own total, so the largest keep their identity and the long tail
            // folds - the opposite order would hand the six colours to whichever value sorted first.
            var seriesTotals = rows
                .GroupBy(r => Key(ctx, e, Value(r, e.SeriesFieldKey)))
                .Select(g => (Name: g.Key, Total: Math.Abs(Dec(Summarise(g.ToList(), e.FieldKey, e.Aggregate)))))
                .OrderByDescending(x => x.Total)
                .ToList();

            var keptNames = seriesTotals.Take(SeriesCeiling - (seriesTotals.Count > SeriesCeiling ? 1 : 0))
                                        .Select(x => x.Name).ToList();
            var keptSet = new HashSet<string>(keptNames, StringComparer.Ordinal);
            var tailCount = seriesTotals.Count - keptNames.Count;
            var otherName = Other(ctx, tailCount);

            var names = new List<string>(keptNames);
            if (tailCount > 0) names.Add(otherName);

            var series = names.Select(n => new ChartSeries { Name = n, Values = new decimal[categories.Count] }).ToList();
            var slot = new Dictionary<string, ChartSeries>(StringComparer.Ordinal);
            for (var i = 0; i < names.Count; i++) slot[names[i]] = series[i];

            // ONE PASS, GROUPED ON THE PAIR. Summarise runs per (category, series) cell rather than per row,
            // because the aggregate is not always a sum: an Average over a cell is the average of that cell,
            // and adding row values would have given the right answer only for Sum and Count.
            foreach (var cell in rows.GroupBy(r => (
                Cat: Key(ctx, e, Value(r, e.CategoryFieldKey)),
                Ser: Key(ctx, e, Value(r, e.SeriesFieldKey)))))
            {
                // A row whose category folded into "Other" belongs to the LAST slot, not to nothing. Dropping
                // it here is what would make the grouped chart stop agreeing with the plain one.
                if (!index.TryGetValue(cell.Key.Cat, out var ci))
                {
                    if (!folded) continue;
                    ci = categories.Count - 1;
                }
                var target = slot.TryGetValue(cell.Key.Ser, out var s) ? s
                           : tailCount > 0 ? slot[otherName] : null;
                if (target == null) continue;
                target.Values[ci] += Dec(Summarise(cell.ToList(), e.FieldKey, e.Aggregate));
            }

            return (categories, series);
        }

        // The legend. Drawn only when there is more than one series, because a legend naming one thing is a
        // caption that costs the drawing a strip of its height.
        private static double Legend(StringBuilder sb, List<ChartSeries> series, double w, double h,
            string color, string ink, double fs)
        {
            if (series.Count <= 1) return 0;

            var boxH = fs * 1.5;
            var y = h - boxH + fs * 0.15;
            var swatch = fs * 0.8;
            var x = 0.0;

            for (var i = 0; i < series.Count; i++)
            {
                // The label is clipped to what is left of the strip rather than to a fixed count: the last
                // entry of a six-series legend has far less room than the first, and a fixed clip would run
                // it off the edge of the drawing.
                var room = Math.Max(fs * 2, w - x - swatch - fs * 0.8);
                var label = Clip(series[i].Name, Math.Max(3, (int)(room / (fs * 0.55))));
                sb.Append("<rect x=\"").Append(Num(x)).Append("\" y=\"").Append(Num(y))
                  .Append("\" width=\"").Append(Num(swatch)).Append("\" height=\"").Append(Num(swatch))
                  .Append("\" rx=\"1\" fill=\"").Append(SeriesColor(color, i, series.Count)).Append("\"></rect>");
                sb.Append("<text x=\"").Append(Num(x + swatch + fs * 0.3)).Append("\" y=\"")
                  .Append(Num(y + swatch * 0.9)).Append("\" font-size=\"").Append(Num(fs * 0.85))
                  .Append("\" fill=\"").Append(ink).Append("\">").Append(Enc(label)).Append("</text>");
                x += swatch + fs * 0.3 + label.Length * fs * 0.5 + fs * 0.9;
                if (x > w - fs * 3 && i < series.Count - 1) break;   // out of strip: the rest stay undrawn rather than overlap
            }
            return boxH;
        }

        private static decimal Dec(object? v) => v == null ? 0m : ReportValues.AsDecimal(v);

        private static string Other(ReportVisualRenderContext ctx, int count) =>
            ctx.Arabic ? $"أخرى ({count})" : $"Other ({count})";

        private static string Empty(ReportVisualRenderContext ctx) =>
            ctx.Arabic ? "لا توجد بيانات لعرضها." : "No data to chart.";

        private static void Chart(StringBuilder sb, ReportVisualRenderContext ctx, ReportElement e,
            string style, List<ReportRow>? scope)
        {
            var rows = scope ?? new List<ReportRow>();
            var s = e.Style ?? new ReportElementStyle();
            var natural = e.ChartKind == ReportChartKind.Line;
            var multi = !string.IsNullOrWhiteSpace(e.SeriesFieldKey) && e.ChartKind != ReportChartKind.Pie;
            var data = Buckets(ctx, rows, e, naturalOrder: natural);

            sb.Append("<div class=\"cbv-el cbv-chart\" style=\"").Append(style).Append("\">");

            if (data.Count == 0)
            {
                sb.Append("<span class=\"cbv-chart-empty\">").Append(Enc(Empty(ctx))).Append("</span></div>");
                return;
            }

            var w = Math.Max(20.0, e.WidthMm) * PxPerMm;
            var h = Math.Max(15.0, e.HeightMm) * PxPerMm;
            var baseColor = Hex(s.Color) ?? "#1877F2";
            var ink = Hex(s.Color) == null ? "#3F4254" : baseColor;
            var fs = Math.Max(6.0, s.FontSizePt) * 96.0 / 72.0 * 0.85;

            // A DRAWN CHART IS NOT A SCRIPTED ONE. No <script>, no external href, no foreignObject — the
            // same three things ReportAssetService refuses in an uploaded SVG are absent here by construction.
            // direction:ltr ON THE DRAWING, and it is not a language decision.
            //
            // Every x below is an absolute coordinate this method computed, and text-anchor is resolved
            // against the INHERITED direction: under the document's dir="rtl" an anchor of "end" means
            // the text's logical end, which for an Arabic run is its LEFT edge - so a label anchored to
            // sit beside a bar grew across it instead. Category names printed on top of their own bars.
            //
            // Pinning the drawing to ltr makes "end" mean "ends at x, extends left" for every label,
            // which is what the arithmetic assumes. It does NOT affect the Arabic itself: glyph shaping
            // and the right-to-left order inside a text run are properties of the text, not of this
            // attribute, so the labels still read correctly - they just stop moving.
            sb.Append("<svg class=\"cbv-chart-svg\" direction=\"ltr\" viewBox=\"0 0 ").Append(Num(w)).Append(' ').Append(Num(h))
              .Append("\" width=\"100%\" height=\"100%\" preserveAspectRatio=\"xMidYMid meet\" role=\"img\">");

            if (multi)
            {
                // The series split is resolved only now, once the drawing is known to have categories at all -
                // an empty scope has already returned above, so nothing below has to handle a zero-width axis.
                var (cats, series) = SeriesBuckets(ctx, rows, e, natural);
                var legendH = Legend(sb, series, w, h, baseColor, ink, fs);
                var plot = Math.Max(fs * 4, h - legendH);

                if (e.ChartKind == ReportChartKind.Line)
                    MultiLine(sb, ctx, e, cats, series, w, plot, baseColor, ink, fs);
                else
                    GroupedBars(sb, ctx, e, cats, series, w, plot, baseColor, ink, fs,
                                horizontal: e.ChartKind == ReportChartKind.Bar);
            }
            else
            {
                switch (e.ChartKind)
                {
                    case ReportChartKind.Pie: Pie(sb, ctx, e, data, w, h, baseColor, ink, fs); break;
                    case ReportChartKind.Bar: Bars(sb, ctx, e, data, w, h, baseColor, ink, fs, horizontal: true); break;
                    case ReportChartKind.Line: Line(sb, ctx, e, data, w, h, baseColor, ink, fs); break;
                    default: Bars(sb, ctx, e, data, w, h, baseColor, ink, fs, horizontal: false); break;
                }
            }

            sb.Append("</svg></div>");
        }

        private static void Bars(StringBuilder sb, ReportVisualRenderContext ctx, ReportElement e,
            List<(string Label, decimal Value)> data, double w, double h, string color, string ink,
            double fs, bool horizontal)
        {
            var max = data.Max(d => Math.Abs(d.Value));
            if (max <= 0) max = 1;
            var pad = fs * 0.6;

            if (horizontal)
            {
                // LABELS DOWN THE SIDE, which is why this kind exists: an Arabic category name has nowhere
                // to go under a vertical column, and rotating it is not reading.
                // HALF THE WIDTH FOR NAMES. 42% was measured against English keys and is not enough for a
                // real Arabic customer name, which arrived clipped to an ambiguous stub - two customers
                // whose names differ only past the cut are two bars a reader cannot tell apart.
                var labelW = Math.Min(w * 0.5, w - fs * 6);
                var trackW = Math.Max(fs, w - labelW - pad * 2 - (e.ShowValues ? fs * 5.5 : 0));
                var rowH = (h - pad) / data.Count;
                var barH = Math.Max(2.0, rowH * 0.62);

                for (var i = 0; i < data.Count; i++)
                {
                    var y = pad / 2 + i * rowH;
                    var len = trackW * (double)(Math.Abs(data[i].Value) / max);
                    sb.Append("<text x=\"").Append(Num(labelW)).Append("\" y=\"").Append(Num(y + rowH / 2 + fs * .35))
                      .Append("\" text-anchor=\"end\" font-size=\"").Append(Num(fs)).Append("\" fill=\"").Append(ink)
                      .Append("\">").Append(Enc(Clip(data[i].Label, Math.Max(6, (int)(labelW / (fs * 0.5)))))).Append("</text>");
                    sb.Append("<rect x=\"").Append(Num(labelW + pad)).Append("\" y=\"").Append(Num(y + (rowH - barH) / 2))
                      .Append("\" width=\"").Append(Num(len)).Append("\" height=\"").Append(Num(barH))
                      .Append("\" fill=\"").Append(Shade(color, i, data.Count)).Append("\" rx=\"1\"></rect>");
                    if (e.ShowValues)
                        sb.Append("<text x=\"").Append(Num(labelW + pad + len + pad * .6))
                          .Append("\" y=\"").Append(Num(y + rowH / 2 + fs * .35))
                          .Append("\" font-size=\"").Append(Num(fs * .9)).Append("\" fill=\"").Append(ink)
                          .Append("\">").Append(Enc(Format(data[i].Value, e, ctx))).Append("</text>");
                }
                return;
            }

            var axisH = fs * 1.6;
            var valueH = e.ShowValues ? fs * 1.3 : 0;
            var plotH = Math.Max(fs, h - axisH - valueH - pad);
            var colW = w / data.Count;
            var barW = Math.Max(2.0, colW * 0.6);

            sb.Append("<line x1=\"0\" y1=\"").Append(Num(valueH + plotH)).Append("\" x2=\"").Append(Num(w))
              .Append("\" y2=\"").Append(Num(valueH + plotH)).Append("\" stroke=\"").Append(ink)
              .Append("\" stroke-opacity=\".25\" stroke-width=\"1\"></line>");

            for (var i = 0; i < data.Count; i++)
            {
                var bh = plotH * (double)(Math.Abs(data[i].Value) / max);
                var x = i * colW + (colW - barW) / 2;
                var y = valueH + plotH - bh;
                sb.Append("<rect x=\"").Append(Num(x)).Append("\" y=\"").Append(Num(y))
                  .Append("\" width=\"").Append(Num(barW)).Append("\" height=\"").Append(Num(bh))
                  .Append("\" fill=\"").Append(Shade(color, i, data.Count)).Append("\" rx=\"1\"></rect>");
                if (e.ShowValues)
                    sb.Append("<text x=\"").Append(Num(i * colW + colW / 2)).Append("\" y=\"").Append(Num(y - fs * .3))
                      .Append("\" text-anchor=\"middle\" font-size=\"").Append(Num(fs * .85)).Append("\" fill=\"")
                      .Append(ink).Append("\">").Append(Enc(Format(data[i].Value, e, ctx))).Append("</text>");
                sb.Append("<text x=\"").Append(Num(i * colW + colW / 2)).Append("\" y=\"")
                  .Append(Num(valueH + plotH + fs * 1.15)).Append("\" text-anchor=\"middle\" font-size=\"")
                  .Append(Num(fs)).Append("\" fill=\"").Append(ink).Append("\">")
                  .Append(Enc(Clip(data[i].Label, Math.Max(4, (int)(colW / (fs * .55)))))).Append("</text>");
            }
        }

        // GROUPED BARS. Each category holds one slot; the slot is divided between the series. The single-
        // series routine is left exactly as it was rather than generalised into this one: it is the common
        // case, its arithmetic is simpler, and a chart nobody asked to split should not start paying for a
        // loop over a list of one.
        private static void GroupedBars(StringBuilder sb, ReportVisualRenderContext ctx, ReportElement e,
            List<string> cats, List<ChartSeries> series, double w, double h, string color, string ink,
            double fs, bool horizontal)
        {
            if (cats.Count == 0 || series.Count == 0) return;

            // THE SCALE IS THE LARGEST SINGLE BAR, not the largest category total. These are grouped bars and
            // not stacked ones, so nothing is ever drawn at the sum - scaling to a total would leave every
            // bar short by however much its neighbours contributed.
            var max = 0m;
            foreach (var s in series) foreach (var v in s.Values) max = Math.Max(max, Math.Abs(v));
            if (max <= 0) max = 1;

            var pad = fs * 0.6;
            // VALUE LABELS ARE OFF IN A GROUPED CHART, whatever the element says. Six numbers across one
            // category slot is the label collision this renderer has already had to fix once; the legend and
            // the axis carry the reading instead.
            var showValues = e.ShowValues && series.Count == 1;

            if (horizontal)
            {
                var labelW = Math.Min(w * 0.5, w - fs * 6);
                var trackW = Math.Max(fs, w - labelW - pad * 2 - (showValues ? fs * 5.5 : 0));
                var rowH = (h - pad) / cats.Count;
                var slotH = Math.Max(1.2, (rowH * 0.72) / series.Count);

                for (var i = 0; i < cats.Count; i++)
                {
                    var y = pad / 2 + i * rowH;
                    sb.Append("<text x=\"").Append(Num(labelW)).Append("\" y=\"").Append(Num(y + rowH / 2 + fs * .35))
                      .Append("\" text-anchor=\"end\" font-size=\"").Append(Num(fs)).Append("\" fill=\"").Append(ink)
                      .Append("\">").Append(Enc(Clip(cats[i], Math.Max(6, (int)(labelW / (fs * 0.5)))))).Append("</text>");

                    var top = y + (rowH - slotH * series.Count) / 2;
                    for (var k = 0; k < series.Count; k++)
                    {
                        var len = trackW * (double)(Math.Abs(series[k].Values[i]) / max);
                        if (len <= 0) continue;
                        sb.Append("<rect x=\"").Append(Num(labelW + pad)).Append("\" y=\"").Append(Num(top + k * slotH))
                          .Append("\" width=\"").Append(Num(len)).Append("\" height=\"").Append(Num(Math.Max(1.0, slotH - 0.4)))
                          .Append("\" fill=\"").Append(SeriesColor(color, k, series.Count)).Append("\" rx=\"1\"></rect>");
                    }
                }
                return;
            }

            var axisH = fs * 1.6;
            var plotH = Math.Max(fs, h - axisH - pad);
            var colW = w / cats.Count;
            var slotW = Math.Max(1.2, (colW * 0.72) / series.Count);

            sb.Append("<line x1=\"0\" y1=\"").Append(Num(plotH)).Append("\" x2=\"").Append(Num(w))
              .Append("\" y2=\"").Append(Num(plotH)).Append("\" stroke=\"").Append(ink)
              .Append("\" stroke-opacity=\".25\" stroke-width=\"1\"></line>");

            for (var i = 0; i < cats.Count; i++)
            {
                var left = i * colW + (colW - slotW * series.Count) / 2;
                for (var k = 0; k < series.Count; k++)
                {
                    var bh = plotH * (double)(Math.Abs(series[k].Values[i]) / max);
                    if (bh <= 0) continue;
                    sb.Append("<rect x=\"").Append(Num(left + k * slotW)).Append("\" y=\"").Append(Num(plotH - bh))
                      .Append("\" width=\"").Append(Num(Math.Max(1.0, slotW - 0.4))).Append("\" height=\"").Append(Num(bh))
                      .Append("\" fill=\"").Append(SeriesColor(color, k, series.Count)).Append("\" rx=\"1\"></rect>");
                }
                sb.Append("<text x=\"").Append(Num(i * colW + colW / 2)).Append("\" y=\"")
                  .Append(Num(plotH + fs * 1.15)).Append("\" text-anchor=\"middle\" font-size=\"")
                  .Append(Num(fs)).Append("\" fill=\"").Append(ink).Append("\">")
                  .Append(Enc(Clip(cats[i], Math.Max(4, (int)(colW / (fs * .55)))))).Append("</text>");
            }
        }

        // SEVERAL LINES ON ONE PAIR OF AXES - the drawing a trend comparison actually wants. They share a
        // scale, which is the entire point: lines drawn each to its own maximum would cross and separate in
        // ways that mean nothing, and a reader would take that shape for the data.
        private static void MultiLine(StringBuilder sb, ReportVisualRenderContext ctx, ReportElement e,
            List<string> cats, List<ChartSeries> series, double w, double h, string color, string ink, double fs)
        {
            if (cats.Count == 0 || series.Count == 0) return;

            var max = 0m;
            foreach (var s in series) foreach (var v in s.Values) max = Math.Max(max, Math.Abs(v));
            if (max <= 0) max = 1;

            var axisH = fs * 1.6;
            var plotH = Math.Max(fs, h - axisH - fs);
            // INSET, so the first and last labels have somewhere to sit. Centred on x=0 and x=w they were
            // half outside the viewBox and arrived clipped - the two labels a reader of a trend most wants.
            var inset = Math.Min(w * 0.08, fs * 3.2);
            var step = cats.Count == 1 ? 0 : (w - inset * 2) / (cats.Count - 1);
            var x0 = cats.Count == 1 ? w / 2 : inset;

            double Y(decimal v) => fs + plotH - plotH * (double)(Math.Abs(v) / max);

            for (var k = 0; k < series.Count; k++)
            {
                var stroke = SeriesColor(color, k, series.Count);
                var points = new StringBuilder();
                for (var i = 0; i < cats.Count; i++)
                {
                    if (i > 0) points.Append(' ');
                    points.Append(Num(x0 + i * step)).Append(',').Append(Num(Y(series[k].Values[i])));
                }
                sb.Append("<polyline points=\"").Append(points).Append("\" fill=\"none\" stroke=\"").Append(stroke)
                  .Append("\" stroke-width=\"1.4\" stroke-linejoin=\"round\" stroke-linecap=\"round\"></polyline>");

                // Markers only when the axis is short enough for them to be points rather than a thick line.
                if (cats.Count <= 20)
                    for (var i = 0; i < cats.Count; i++)
                        sb.Append("<circle cx=\"").Append(Num(x0 + i * step)).Append("\" cy=\"")
                          .Append(Num(Y(series[k].Values[i]))).Append("\" r=\"1.8\" fill=\"").Append(stroke).Append("\"></circle>");
            }

            // The axis is drawn ONCE, after the lines, so a marker sitting on a tick does not hide the label.
            for (var i = 0; i < cats.Count; i++)
                sb.Append("<text x=\"").Append(Num(x0 + i * step)).Append("\" y=\"").Append(Num(fs + plotH + fs * 1.15))
                  .Append("\" text-anchor=\"middle\" font-size=\"").Append(Num(fs)).Append("\" fill=\"").Append(ink)
                  .Append("\">").Append(Enc(Clip(cats[i], 10))).Append("</text>");
        }

        private static void Line(StringBuilder sb, ReportVisualRenderContext ctx, ReportElement e,
            List<(string Label, decimal Value)> data, double w, double h, string color, string ink, double fs)
        {
            var max = data.Max(d => Math.Abs(d.Value));
            if (max <= 0) max = 1;
            var axisH = fs * 1.6;
            var plotH = Math.Max(fs, h - axisH - fs);
            // INSET, so the first and last labels have somewhere to sit. Centred on x=0 and x=w they were
            // half outside the viewBox and arrived clipped - the two labels a reader of a trend most wants.
            var inset = Math.Min(w * 0.08, fs * 3.2);
            var step = data.Count == 1 ? 0 : (w - inset * 2) / (data.Count - 1);
            var x0 = data.Count == 1 ? w / 2 : inset;

            var points = new StringBuilder();
            for (var i = 0; i < data.Count; i++)
            {
                var x = x0 + i * step;
                var y = fs + plotH - plotH * (double)(Math.Abs(data[i].Value) / max);
                if (i > 0) points.Append(' ');
                points.Append(Num(x)).Append(',').Append(Num(y));
            }

            sb.Append("<polyline points=\"").Append(points).Append("\" fill=\"none\" stroke=\"").Append(color)
              .Append("\" stroke-width=\"1.6\" stroke-linejoin=\"round\" stroke-linecap=\"round\"></polyline>");

            for (var i = 0; i < data.Count; i++)
            {
                var x = x0 + i * step;
                var y = fs + plotH - plotH * (double)(Math.Abs(data[i].Value) / max);
                sb.Append("<circle cx=\"").Append(Num(x)).Append("\" cy=\"").Append(Num(y))
                  .Append("\" r=\"2\" fill=\"").Append(color).Append("\"></circle>");
                sb.Append("<text x=\"").Append(Num(x)).Append("\" y=\"").Append(Num(fs + plotH + fs * 1.15))
                  .Append("\" text-anchor=\"middle\" font-size=\"").Append(Num(fs)).Append("\" fill=\"").Append(ink)
                  .Append("\">").Append(Enc(Clip(data[i].Label, 10))).Append("</text>");
            }
        }

        private static void Pie(StringBuilder sb, ReportVisualRenderContext ctx, ReportElement e,
            List<(string Label, decimal Value)> data, double w, double h, string color, string ink, double fs)
        {
            var total = data.Sum(d => Math.Abs(d.Value));
            if (total <= 0) total = 1;

            var legendW = Math.Min(w * 0.45, fs * 12);
            var plotW = w - legendW;
            var r = Math.Max(4.0, Math.Min(plotW, h) / 2 - 2);
            var cx = plotW / 2;
            var cy = h / 2;

            double angle = -Math.PI / 2;
            for (var i = 0; i < data.Count; i++)
            {
                var frac = (double)(Math.Abs(data[i].Value) / total);
                var sweep = frac * Math.PI * 2;
                var x1 = cx + r * Math.Cos(angle);
                var y1 = cy + r * Math.Sin(angle);
                angle += sweep;
                var x2 = cx + r * Math.Cos(angle);
                var y2 = cy + r * Math.Sin(angle);

                // A SINGLE CATEGORY IS A WHOLE CIRCLE, and an arc cannot draw one: start and end land on the
                // same point and the path collapses to nothing. That case gets a real circle instead.
                if (data.Count == 1 || frac >= 0.999)
                    sb.Append("<circle cx=\"").Append(Num(cx)).Append("\" cy=\"").Append(Num(cy))
                      .Append("\" r=\"").Append(Num(r)).Append("\" fill=\"").Append(Shade(color, i, data.Count))
                      .Append("\"></circle>");
                else
                    sb.Append("<path d=\"M").Append(Num(cx)).Append(' ').Append(Num(cy))
                      .Append(" L").Append(Num(x1)).Append(' ').Append(Num(y1))
                      .Append(" A").Append(Num(r)).Append(' ').Append(Num(r)).Append(" 0 ")
                      .Append(sweep > Math.PI ? '1' : '0').Append(" 1 ")
                      .Append(Num(x2)).Append(' ').Append(Num(y2)).Append(" Z\" fill=\"")
                      .Append(Shade(color, i, data.Count)).Append("\"></path>");

                var ly = fs * 1.4 * i + fs;
                if (ly > h - 2) continue;
                sb.Append("<rect x=\"").Append(Num(plotW + 2)).Append("\" y=\"").Append(Num(ly - fs * .75))
                  .Append("\" width=\"").Append(Num(fs * .8)).Append("\" height=\"").Append(Num(fs * .8))
                  .Append("\" fill=\"").Append(Shade(color, i, data.Count)).Append("\" rx=\"1\"></rect>");
                var label = e.ShowValues
                    ? $"{Clip(data[i].Label, 16)} · {Format(data[i].Value, e, ctx)}"
                    : Clip(data[i].Label, 22);
                sb.Append("<text x=\"").Append(Num(plotW + 2 + fs * 1.2)).Append("\" y=\"").Append(Num(ly))
                  .Append("\" font-size=\"").Append(Num(fs * .9)).Append("\" fill=\"").Append(ink).Append("\">")
                  .Append(Enc(label)).Append("</text>");
            }
        }

        // ---- the cross-tab element -------------------------------------------------------------------
        private static void CrossTab(StringBuilder sb, ReportVisualRenderContext ctx, ReportElement e,
            string style, List<ReportRow>? scope)
        {
            var rows = scope ?? new List<ReportRow>();
            sb.Append("<div class=\"cbv-el cbv-table-wrap\" style=\"").Append(style).Append("\">");

            if (rows.Count == 0)
            {
                sb.Append("<span class=\"cbv-chart-empty\">").Append(Enc(Empty(ctx))).Append("</span></div>");
                return;
            }

            // COLUMNS ARE DISCOVERED, NOT AUTHORED — the difference between this and a Table. The same
            // ceiling applies, folding the tail into one column so the grand total still reconciles.
            var colBuckets = Buckets(ctx, rows, new ReportElement
            {
                CategoryFieldKey = e.SeriesFieldKey,
                FieldKey = e.FieldKey,
                Aggregate = e.Aggregate,
                MaxCategories = e.MaxCategories,
                Style = e.Style,
            }, naturalOrder: false);
            var cols = colBuckets.Select(c => c.Label).ToList();
            var folded = cols.Count > 0 && cols[^1].StartsWith(ctx.Arabic ? "أخرى (" : "Other (", StringComparison.Ordinal);
            var namedCols = folded ? cols.Take(cols.Count - 1).ToHashSet(StringComparer.Ordinal) : cols.ToHashSet(StringComparer.Ordinal);

            // THE CEILING APPLIES TO ROWS TOO, and it did not - which made it half a ceiling.
            //
            // Columns were capped and rows were taken whole, so a cross-tab over a high-cardinality row
            // field grew without limit: it outgrew the box the author sized for it and printed straight
            // over whatever was placed underneath. That is exactly the unbounded document the ceiling
            // exists to prevent, so rows now fold the same way columns do - biggest kept, the tail
            // gathered into one labelled row rather than dropped, so the corner total still reconciles.
            var rowKeys = Buckets(ctx, rows, new ReportElement
            {
                CategoryFieldKey = e.CategoryFieldKey,
                FieldKey = e.FieldKey,
                Aggregate = e.Aggregate,
                MaxCategories = e.MaxCategories,
                Style = e.Style,
            }, naturalOrder: false).Select(b => b.Label).ToList();

            var rowsFolded = rowKeys.Count > 0
                && rowKeys[^1].StartsWith(ctx.Arabic ? "أخرى (" : "Other (", StringComparison.Ordinal);
            var namedRows = rowsFolded
                ? rowKeys.Take(rowKeys.Count - 1).ToHashSet(StringComparer.Ordinal)
                : rowKeys.ToHashSet(StringComparer.Ordinal);

            sb.Append("<table class=\"cbv-table cbv-crosstab\"><thead><tr><th></th>");
            foreach (var c in cols) sb.Append("<th>").Append(Enc(c)).Append("</th>");
            if (e.ShowGrandTotals)
                sb.Append("<th>").Append(Enc(ctx.Arabic ? "الإجمالي" : "Total")).Append("</th>");
            sb.Append("</tr></thead><tbody>");

            foreach (var rk in rowKeys)
            {
                var band = rowsFolded && ReferenceEquals(rk, rowKeys[^1])
                    ? rows.Where(r => !namedRows.Contains(Text(Value(r, e.CategoryFieldKey)) ?? "")).ToList()
                    : rows.Where(r => string.Equals(Text(Value(r, e.CategoryFieldKey)) ?? "", rk,
                                                    StringComparison.Ordinal)).ToList();
                sb.Append("<tr><th scope=\"row\">").Append(Enc(rk)).Append("</th>");
                foreach (var c in cols)
                {
                    var cell = folded && ReferenceEquals(c, cols[^1])
                        ? band.Where(r => !namedCols.Contains(Key(ctx, e, Value(r, e.SeriesFieldKey)))).ToList()
                        : band.Where(r => string.Equals(Key(ctx, e, Value(r, e.SeriesFieldKey)), c,
                                                        StringComparison.Ordinal)).ToList();
                    sb.Append("<td>").Append(Enc(cell.Count == 0
                        ? ""
                        : Format(Summarise(cell, e.FieldKey, e.Aggregate), e, ctx))).Append("</td>");
                }
                if (e.ShowGrandTotals)
                    sb.Append("<td class=\"cbv-ct-total\">")
                      .Append(Enc(Format(Summarise(band, e.FieldKey, e.Aggregate), e, ctx))).Append("</td>");
                sb.Append("</tr>");
            }
            sb.Append("</tbody>");

            if (e.ShowGrandTotals)
            {
                sb.Append("<tfoot><tr><th scope=\"row\">").Append(Enc(ctx.Arabic ? "الإجمالي" : "Total")).Append("</th>");
                foreach (var c in cols)
                {
                    var cell = folded && ReferenceEquals(c, cols[^1])
                        ? rows.Where(r => !namedCols.Contains(Key(ctx, e, Value(r, e.SeriesFieldKey)))).ToList()
                        : rows.Where(r => string.Equals(Key(ctx, e, Value(r, e.SeriesFieldKey)), c,
                                                        StringComparison.Ordinal)).ToList();
                    sb.Append("<td>").Append(Enc(cell.Count == 0
                        ? ""
                        : Format(Summarise(cell, e.FieldKey, e.Aggregate), e, ctx))).Append("</td>");
                }
                sb.Append("<td class=\"cbv-ct-total\">")
                  .Append(Enc(Format(Summarise(rows, e.FieldKey, e.Aggregate), e, ctx))).Append("</td></tr></tfoot>");
            }

            sb.Append("</table></div>");
        }

        // ---- the sub-report element ------------------------------------------------------------------
        //
        // DRAWS WHAT IT WAS HANDED, and says so when it was handed nothing. The three outcomes are
        // deliberately distinguishable on paper, because they mean different things to a reader:
        //
        //   refused   — you may not run this report        (never looks like "no rows")
        //   no rows   — you may, and this group has none
        //   truncated — you are not seeing all of them
        private static void SubReport(StringBuilder sb, ReportVisualRenderContext ctx, ReportElement e,
            string style, string? groupKey)
        {
            sb.Append("<div class=\"cbv-el cbv-table-wrap cbv-subreport\" style=\"").Append(style).Append("\">");

            if (!ctx.SubReports.TryGetValue(e.Id, out var sub))
            {
                // The engine resolves every sub-report the layout places, so a miss means the child could
                // not be found at all — a renamed or retired report code in an old template.
                sb.Append("<span class=\"cbv-sub-note\">")
                  .Append(Enc(ctx.Arabic ? "التقرير الفرعي غير متاح." : "This sub-report is unavailable."))
                  .Append("</span></div>");
                return;
            }

            sb.Append("<div class=\"cbv-sub-title\">").Append(Enc(sub.Title)).Append("</div>");

            if (sub.Denied)
            {
                sb.Append("<span class=\"cbv-sub-note\">")
                  .Append(Enc(ctx.Arabic ? "لا تملك صلاحية عرض هذا التقرير." : "You may not view this report."))
                  .Append("</span></div>");
                return;
            }

            var rows = e.LinkChildFieldKey == null
                ? sub.Unlinked
                : (groupKey != null && sub.ByLink.TryGetValue(groupKey, out var hit)
                    ? hit
                    : Array.Empty<ReportRow>());

            if (rows.Count == 0 || sub.Columns.Count == 0)
            {
                sb.Append("<span class=\"cbv-sub-note\">")
                  .Append(Enc(ctx.Arabic ? "لا توجد سجلات." : "No records."))
                  .Append("</span></div>");
                return;
            }

            sb.Append("<table class=\"cbv-table\"><thead><tr>");
            foreach (var c in sub.Columns)
                sb.Append("<th>").Append(Enc(ctx.Arabic ? c.TitleAr : c.TitleEn)).Append("</th>");
            sb.Append("</tr></thead><tbody>");

            foreach (var r in rows)
            {
                sb.Append("<tr>");
                foreach (var c in sub.Columns)
                    sb.Append("<td>").Append(Enc(FormatValue(Value(r, c.Key), c.Format, ctx))).Append("</td>");
                sb.Append("</tr>");
            }
            sb.Append("</tbody></table>");

            if (sub.Truncated)
                sb.Append("<span class=\"cbv-sub-note\">")
                  .Append(Enc(ctx.Arabic ? "القائمة مقطوعة — هناك سجلات أخرى." : "Truncated — more records exist."))
                  .Append("</span>");

            sb.Append("</div>");
        }

        // ---- drawing helpers -------------------------------------------------------------------------
        private static string Num(double v) =>
            Math.Round(v, 2).ToString(CultureInfo.InvariantCulture);

        private static string? Hex(string? c) =>
            !string.IsNullOrWhiteSpace(c) && c.Length == 7 && c[0] == '#' ? c : null;

        private static string Clip(string s, int max) =>
            string.IsNullOrEmpty(s) ? "" : (s.Length <= max ? s : s[..Math.Max(1, max - 1)] + "…");

        /// One authored colour becomes a readable series, by walking lightness rather than hue.
        ///
        /// A generated hue ramp would have produced colours the author never chose and the brand sheet never
        /// measured — and the platform already refuses any colour that is not an authored #rrggbb. Tints of
        /// the one colour keep that promise: every slice is demonstrably the author's colour.
        // SERIES ARE TOLD APART BY HUE; CATEGORIES BY SHADE. Both look like "pick colour number i", and
        // using one for the other is what made the first grouped chart unreadable: six customers drawn in
        // six tints of the same blue, with a legend that named them and a drawing in which nobody could
        // find them.
        //
        // The distinction is in what the colours MEAN. The bars of a single-series chart are one quantity
        // measured at several points, so a ramp is honest - it says "same thing, different amount", and a
        // reader who ignores the colour loses nothing. Series are different entities measured the same
        // way, and there the colour is the only thing carrying WHICH; it has to be categorical.
        //
        // THE AUTHOR'S COLOUR STAYS FIRST. Rotation starts from the hue they chose, so a report set to the
        // brand blue still opens on the brand blue and the other series fan out from it, rather than the
        // palette quietly replacing a deliberate choice.
        //
        // The rotation is deliberately not the full circle divided by n: at n = 2 that would put the second
        // series on the exact complement, which reads as an alert rather than a peer. Just over a third of
        // the wheel separates neighbours at any n while keeping every hue in the same family of weight.
        private static string SeriesColor(string hex, int i, int n)
        {
            if (n <= 1 || i == 0 || hex.Length != 7) return hex;

            var (h, s, l) = ToHsl(hex);
            h = (h + i * 137.0) % 360.0;                       // the golden angle: no two of six land near each other

            // Saturation and lightness are pulled toward a legible band rather than inherited: a very pale
            // or very dark author colour would otherwise produce a set of colours that differ in hue and
            // are all equally invisible on white.
            s = Math.Clamp(s < 0.35 ? 0.55 : s, 0.42, 0.82);
            l = Math.Clamp(l < 0.28 || l > 0.68 ? 0.48 : l, 0.34, 0.62);
            return FromHsl(h, s, l);
        }

        private static (double H, double S, double L) ToHsl(string hex)
        {
            double r = Convert.ToInt32(hex.Substring(1, 2), 16) / 255.0,
                   g = Convert.ToInt32(hex.Substring(3, 2), 16) / 255.0,
                   b = Convert.ToInt32(hex.Substring(5, 2), 16) / 255.0;
            double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), d = max - min;
            double l = (max + min) / 2, s = d == 0 ? 0 : d / (1 - Math.Abs(2 * l - 1));
            double h = d == 0 ? 0
                     : max == r ? 60 * (((g - b) / d) % 6)
                     : max == g ? 60 * (((b - r) / d) + 2)
                                : 60 * (((r - g) / d) + 4);
            if (h < 0) h += 360;
            return (h, s, l);
        }

        private static string FromHsl(double h, double s, double l)
        {
            double c = (1 - Math.Abs(2 * l - 1)) * s, x = c * (1 - Math.Abs((h / 60 % 2) - 1)), m = l - c / 2;
            (double r, double g, double b) t =
                  h < 60  ? (c, x, 0) : h < 120 ? (x, c, 0) : h < 180 ? (0, c, x)
                : h < 240 ? (0, x, c) : h < 300 ? (x, 0, c) : (c, 0, x);
            int B(double v) => (int)Math.Clamp(Math.Round((v + m) * 255), 0, 255);
            return $"#{B(t.r):x2}{B(t.g):x2}{B(t.b):x2}";
        }

        private static string Shade(string hex, int i, int n)
        {
            if (n <= 1 || hex.Length != 7) return hex;

            int R = Convert.ToInt32(hex.Substring(1, 2), 16),
                G = Convert.ToInt32(hex.Substring(3, 2), 16),
                B = Convert.ToInt32(hex.Substring(5, 2), 16);

            var t = i / (double)(n - 1) * 0.72 - 0.26;   // -0.26 (darker) .. +0.46 (lighter)
            double Mix(int c) => t >= 0 ? c + (255 - c) * t : c * (1 + t);

            return $"#{(int)Math.Clamp(Mix(R), 0, 255):x2}{(int)Math.Clamp(Mix(G), 0, 255):x2}{(int)Math.Clamp(Mix(B), 0, 255):x2}";
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
                      .Append(Enc(CellText(v, c, ctx))).Append("</td>");
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
        /// The author's static text in the reader's language. Arabic is the fallback, never the
        /// other way round: this product is authored in Arabic and an untranslated label must still
        /// print rather than vanish.
        private static string TextFor(ReportVisualRenderContext ctx, ReportElement e)
        {
            if (ctx.Arabic) return e.Text ?? e.TextEn ?? "";
            return DisplayName.Or(e.TextEn, e.Text) ?? "";
        }

        private static string HeaderFor(ReportVisualRenderContext ctx, ReportTableColumn c)
        {
            if (!string.IsNullOrWhiteSpace(c.HeaderText) || !string.IsNullOrWhiteSpace(c.HeaderTextEn))
                return ctx.Arabic
                    ? (c.HeaderText ?? c.HeaderTextEn)!
                    : (DisplayName.Or(c.HeaderTextEn, c.HeaderText) ?? c.FieldKey);

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

        // A TABLE CELL IS FORMATTED BY ITS COLUMN'S TYPE, through the platform's one formatter.
        //
        // It used to go straight to FormatValue, which knows only what the CLR handed it, and the result was
        // a designed report printing raw machine values: a Boolean came out "True" instead of نعم/Yes, and
        // an Integer came out "6.00" because the fallback format is "N2" and a count is IFormattable like
        // any other number. The plain column renderer never had either defect — it calls
        // ReportValues.Format with the column — so the SAME report read correctly until its author gave it a
        // design, which is the worst possible way for this to fail.
        //
        // The element's own format string still wins where the author set one; ReportValues.Format takes it
        // as the column's override, so a deliberate pattern is honoured and everything else is typed.
        private static string CellText(object? value, ReportTableColumn c, ReportVisualRenderContext ctx)
        {
            if (value == null) return "";

            var column = ctx.Definition.Columns
                .FirstOrDefault(x => string.Equals(x.Key, c.FieldKey, StringComparison.Ordinal));

            // A column the definition no longer declares keeps the old behaviour rather than throwing: a
            // saved design outliving a renamed field must still render.
            if (column == null) return FormatValue(value, c.Format, ctx);

            var culture = ctx.Arabic ? new CultureInfo("ar") : CultureInfo.InvariantCulture;

            return string.IsNullOrWhiteSpace(c.Format)
                ? ReportValues.Format(value, column, culture)
                : ReportValues.Format(value, Overridden(column, c.Format!), culture);
        }

        // ReportColumn is a class the caller does not own, so an override is applied to a COPY. Mutating the
        // definition's column would change the format for every other report sharing that dataset.
        private static ReportColumn Overridden(ReportColumn column, string format) => new()
        {
            Key = column.Key,
            TitleAr = column.TitleAr,
            TitleEn = column.TitleEn,
            Type = column.Type,
            Format = format,
        };

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

        // THE LABEL A CHART GROUPS BY. Text() on a raw DateTime yields "9/13/2026 12:00:00 AM" - a label
        // that is unreadable on an axis, differs by machine culture, and makes every row its own bucket
        // because the time component is never equal twice.
        //
        // Running it through the element's own date format fixes all three, and does something more useful
        // besides: since the bucket key IS this string, an author who sets the format to "yyyy-MM" gets a
        // chart grouped BY MONTH, and "yyyy" gets one grouped by year. Period bucketing therefore needs no
        // new property and no new element kind - it is the formatting decision the author was already
        // making, now applied one step earlier than it used to be.
        //
        // Non-date values are untouched: their ToString() was already the value, and changing how they
        // group would move data between buckets in a chart nobody asked to change.
        private static string Key(ReportVisualRenderContext ctx, ReportElement e, object? v) =>
            v is DateTime ? FormatValue(v, e.Style?.DateFormat, ctx) : Text(v);

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

        // `arabic` is here only for the font stack: the faces are preferred per culture, so the sheet has
        // to know which language the run is in. See ReportTypography.
        private static void Css(StringBuilder sb, double paperW, double paperH, double contentW, double contentH,
            ReportPageSetup page, bool screen, bool arabic, ReportBranding branding)
        {
            // THE FACE ITSELF, FIRST, before anything can ask for it.
            //
            // Naming a family only works if the machine looking at the document happens to have it, and
            // that assumption broke three different ways in one afternoon — absent on the host, present on
            // the host but missing from a browser whose font list predated the install, and absent again on
            // any second machine. Writing the bytes into the document is what already makes the PDF immune,
            // because Chromium embeds what it used; this gives the HTML the same immunity.
            ReportFontLibrary.AppendFaceFor(sb, page.FontFamily);

            sb.Append("*{box-sizing:border-box;margin:0;padding:0}");
            ReportNotice.Css(sb, branding);
            // THE TEMPLATE'S FACE, through the same one function the table renderer uses. This sheet once
            // hardcoded Inter,Tahoma,Arial while HtmlReportRenderer hardcoded 'Segoe UI',Tahoma,Arial — the
            // same report in two faces depending on which renderer produced it, and neither changeable by
            // the person designing it.
            var docFamily = ReportTypography.DocumentFamily(page.FontFamily, arabic);
            var docSizePt = page.FontSizePt is > 0 ? page.FontSizePt!.Value : 9;
            // THE SELECTOR HAS TO MATCH THE ROOT THIS RENDER ACTUALLY EMITS, and it did not.
            //
            // A full document opens `<body class="cbv">`; a screen preview is a FRAGMENT and opens
            // `<div class="cbv">`. Both took the same stylesheet, and that stylesheet said `body.cbv` — so
            // on screen it matched nothing at all. Not the font, not the size, not the colour: the preview
            // fell all the way back to the browser's initial values, which is why an Arabic report designed
            // in Cairo displayed in TIMES NEW ROMAN while the identical PDF came out in Cairo. Measured with
            // CDP's platform-font report: same CSS rule in both, 4868 glyphs of Times New Roman in one and
            // 4819 of Cairo in the other.
            //
            // "In Studio I chose Cairo and here you show whatever you like" was exactly right, and the
            // answer was never in the font stack — the rule was being written to an element that did not
            // exist. The `screen` flag already tells us which root was emitted, so the selector follows it.
            sb.Append(screen ? ".cbv{font-family:" : "body.cbv{font-family:").Append(docFamily)
              .Append(";font-size:").Append(docSizePt.ToString("0.##", CultureInfo.InvariantCulture))
              .Append("pt;color:#111;background:")
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

            // A CROSS-TAB'S ROW HEADINGS ARE <th scope=row>, so they need the column headings' weight
            // without their bottom rule — the grid reads as a matrix, not as a stack of tables.
            sb.Append(".cbv-crosstab th[scope=row]{text-align:start;font-weight:600;}");
            sb.Append(".cbv-crosstab td{text-align:end;}");
            sb.Append(".cbv-crosstab .cbv-ct-total{font-weight:700;}");

            // The chart box does not clip its drawing: an SVG sized to the element already fits, and
            // `overflow:hidden` on the shared .cbv-el would cut a value label sitting on the top bar.
            sb.Append(".cbv-chart{overflow:visible;align-items:stretch;}");
            sb.Append(".cbv-chart-svg{display:block;width:100%;height:100%;}");
            sb.Append(".cbv-chart-empty{color:#7E8299;font-style:italic;align-self:center;}");
            sb.Append(".cbv-subreport{display:block;overflow:visible;}");
            sb.Append(".cbv-sub-title{font-weight:700;color:#0E4A9E;margin-block-end:.8mm;}");
            sb.Append(".cbv-sub-note{color:#7E8299;font-style:italic;}");
        }

        private static string Mm(double v) => v.ToString("0.###", CultureInfo.InvariantCulture) + "mm";

        // The one encoder. Every user-authored string on the page goes through it.
        private static string Enc(string? v) => WebUtility.HtmlEncode(v ?? "");
    }
}
