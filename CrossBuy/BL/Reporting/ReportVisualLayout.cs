namespace CrossBuy.BL.Reporting
{
    // ============================================================================================
    // REPORT STUDIO V2 — THE VISUAL LAYOUT CONTRACT.
    //
    // V1 persisted a report as an ordered column list. A printable business document is not that: an invoice
    // has a logo at a position, a title at another, a repeating detail band, totals aligned to the right, a
    // signature and a stamp wherever the company puts them. So V2 adds a POSITIONED, BANDED definition.
    //
    // WHAT IT IS NOT: a second report engine. This describes only WHERE things sit and HOW they look. Every
    // value still comes from the V1 dataset pipeline, still filtered by V1's permissions, still executed by
    // IReportService. The designer composes what the engine already allows.
    //
    // WHY IT IS STRUCTURED AND NOT HTML: persisting DOM would make the browser the authority on the document,
    // and a saved layout would then be able to carry script, arbitrary URLs and field names nothing validated.
    // Every element here is a typed record with a bounded set of kinds, so the server can revalidate a
    // reopened layout completely — which §18 requires and which is also what makes a future governed AI
    // drafter possible: it would emit THIS, through the same validator, with no privileged path.
    //
    // SchemaVersion is present from the start so a later shape change can migrate rather than guess.
    // ============================================================================================

    public enum ReportBandKind
    {
        ReportHeader = 0,
        PageHeader = 1,
        GroupHeader = 2,
        Detail = 3,
        GroupFooter = 4,
        PageFooter = 5,
        ReportFooter = 6,
    }

    public enum ReportElementKind
    {
        Text = 0,          // static label
        Field = 1,         // a dataset field, bound by key
        Image = 2,         // a stored asset: logo, signature, stamp or any permitted image
        Line = 3,
        Rectangle = 4,
        SystemField = 5,   // date, page number, report name…
        Summary = 6,       // Sum/Count/Avg/Min/Max over a field, within the band's scope
        Table = 7,         // a real report table with bound columns; rows repeat from the dataset

        // A QR of a bound field or of typed text, rendered server-side into the document — see
        // ReportQrCode for why it is an embedded image and why it is not a tax-authority payload.
        // ADDITIVE at the end: a stored layout holds the integer, so this cannot change what an
        // existing element means.
        QrCode = 8,

        // A chart of ONE measure aggregated along ONE category axis, drawn server-side as inline SVG.
        // ADDITIVE at the end, same rule as QrCode: a stored layout holds the integer.
        //
        // SVG rather than a charting library: the same markup has to survive the HTML viewer, the print
        // view and Chromium's PDF pass, and a library that paints into a canvas after load paints nothing
        // into a PDF. It also keeps the platform's "no arbitrary server fetch" rule intact — there is no
        // script and no external asset in a drawn chart.
        Chart = 9,

        // A pivot: one measure aggregated across a ROW axis and a COLUMN axis. The columns are discovered
        // from the data, which is the whole point of a cross-tab and the reason it cannot be expressed as
        // a Table — a Table's columns are authored, a cross-tab's are found.
        CrossTab = 10,

        // ANOTHER REPORT, EMBEDDED. The first element whose rows come from a dataset this report is not
        // built on, which is why the engine resolves it and the renderer only draws what it was handed:
        // a renderer that could fetch would be a second data path, and the platform has exactly one.
        SubReport = 11,
    }

    // Which drawing a Chart element makes. Column/Bar are the same data with the axes swapped — kept as
    // two kinds rather than a flag because that is how an author names them.
    public enum ReportChartKind
    {
        Column = 0,   // vertical bars, categories along the bottom
        Bar = 1,      // horizontal bars, categories down the side — survives long Arabic labels
        Line = 2,
        Pie = 3,
    }

    public enum ReportSystemField
    {
        CurrentDate = 0,
        CurrentDateTime = 1,
        PageNumber = 2,
        TotalPages = 3,
        PageXOfY = 4,
        ReportName = 5,
    }

    // What an image is FOR. Not a position — a role. §5 is explicit that a logo may live anywhere; the role
    // exists so a template can default sensibly and so asset permissions can be reasoned about, never to pin
    // the element to a corner.
    public enum ReportImageRole
    {
        Custom = 0,
        CompanyLogo = 1,
        Signature = 2,
        Stamp = 3,

        // ADDITIVE, and the numbering is why it is safe: stored layouts hold the integer, so a new
        // member at the end cannot change what an existing template means. The branch is a separate
        // role rather than "the logo, resolved differently" because a document commonly carries BOTH —
        // the company on one side and the branch that issued it on the other.
        BranchLogo = 4,
    }

    // How much of a QR can be lost and still read. The four levels the format defines, in the format's
    // own order — named rather than numbered so a stored layout says what it means.
    public enum ReportQrEcc
    {
        L = 0,   // ~7%  recovery
        M = 1,   // ~15%
        Q = 2,   // ~25% — the default: printed paper creases, and a stamp lands on the corner
        H = 3,   // ~30% — for a code that will be stamped over or printed small
    }

    public enum ReportImageFit
    {
        Contain = 0,   // whole image inside the box, aspect preserved
        Cover = 1,     // fill the box, aspect preserved, overflow clipped
        Stretch = 2,   // fill the box, aspect ignored
    }

    public enum ReportTextAlign { Start = 0, Center = 1, End = 2, Justify = 3 }
    public enum ReportVerticalAlign { Top = 0, Middle = 1, Bottom = 2 }
    public enum ReportBorderStyle { None = 0, Solid = 1, Dashed = 2, Dotted = 3 }

    // ---- style ----------------------------------------------------------------------------------
    //
    // COLOURS ARE VALIDATED TOKENS, not free CSS. A user-supplied style string is an injection surface in
    // HTML and meaningless in CSV; a hex colour that the server checks against a pattern is neither.
    public sealed class ReportElementStyle
    {
        public string? FontFamily { get; set; }
        public double FontSizePt { get; set; } = 9;
        public bool Bold { get; set; }
        public bool Italic { get; set; }
        public bool Underline { get; set; }
        public ReportTextAlign Align { get; set; } = ReportTextAlign.Start;
        public ReportVerticalAlign VerticalAlign { get; set; } = ReportVerticalAlign.Top;

        public string? Color { get; set; }             // #rrggbb
        public string? Background { get; set; }        // #rrggbb
        public string? BorderColor { get; set; }       // #rrggbb
        public ReportBorderStyle BorderStyle { get; set; } = ReportBorderStyle.None;
        public double BorderWidthMm { get; set; } = 0.2;
        public double PaddingMm { get; set; } = 0.5;

        public string? NumberFormat { get; set; }      // e.g. "N2"
        public string? DateFormat { get; set; }        // e.g. "yyyy-MM-dd"
        public bool Visible { get; set; } = true;

        public ReportElementStyle Clone() => (ReportElementStyle)MemberwiseClone();
    }

    // ---- table ----------------------------------------------------------------------------------
    public sealed class ReportTableColumn
    {
        public string FieldKey { get; set; } = "";
        public string? HeaderText { get; set; }

        // Same rule as the element's text: a TYPED header is the author's own words and needs both
        // languages. An untyped one already resolves from the dataset's bilingual title.
        public string? HeaderTextEn { get; set; }
        public double WidthMm { get; set; } = 25;
        public ReportTextAlign Align { get; set; } = ReportTextAlign.Start;
        public string? Format { get; set; }
        public ReportAggregate Total { get; set; } = ReportAggregate.None;
    }

    // ---- element --------------------------------------------------------------------------------
    public sealed class ReportElement
    {
        public string Id { get; set; } = "";
        public ReportElementKind Kind { get; set; } = ReportElementKind.Text;

        // Millimetres, from the band's start corner. MM rather than pixels because the output is paper: a
        // position in px only means something at one zoom on one screen.
        public double XMm { get; set; }
        public double YMm { get; set; }
        public double WidthMm { get; set; } = 40;
        public double HeightMm { get; set; } = 6;

        public int Z { get; set; }

        public string? Text { get; set; }

        // THE ENGLISH TWIN. A label typed once printed in one language on a document that had been
        // switched to the other — beside column headers that DID translate, because those fall back
        // to the dataset's bilingual titles. Null means "no English yet", and the renderer falls back
        // to Text, so every existing template keeps printing exactly what it printed before.
        public string? TextEn { get; set; }
        public string? FieldKey { get; set; }
        public ReportSystemField SystemField { get; set; } = ReportSystemField.CurrentDate;
        public ReportAggregate Aggregate { get; set; } = ReportAggregate.Sum;

        public int? AssetId { get; set; }
        public ReportImageRole ImageRole { get; set; } = ReportImageRole.Custom;
        public ReportImageFit Fit { get; set; } = ReportImageFit.Contain;
        public bool PreserveAspect { get; set; } = true;

        // ---- the QR's own settings, which are the author's and not the code's ---------------------
        //
        // ERROR CORRECTION is a real trade about a real document: H survives a stamp landing on the
        // corner and spends modules doing it; L fits a longer payload and fails the first time someone
        // folds the invoice. Q is the default because printed paper is the normal case here.
        public ReportQrEcc QrEcc { get; set; } = ReportQrEcc.Q;

        // Pixels per module in the generated image. Higher is a crisper code on paper and a bigger
        // document; the renderer clamps it, so a value here cannot produce a 40 MB PNG.
        public int QrModulePixels { get; set; } = 8;

        // ---- the two grouping axes, shared by Chart and CrossTab -----------------------------------
        //
        // THE MEASURE IS `FieldKey` AND THE FUNCTION IS `Aggregate` — deliberately the same two properties
        // Summary already uses. A chart is a Summary drawn along an axis and a cross-tab is a Summary in a
        // grid; giving each its own measure property would have meant three validators answering the same
        // question "may this field be summed" and three chances to answer it differently.

        /// The axis a measure is grouped ALONG: a chart's categories, a cross-tab's rows.
        public string? CategoryFieldKey { get; set; }

        /// The SECOND axis, and it belongs to the cross-tab alone: the field whose distinct values become
        /// columns. A chart is single-series in this increment and the validator refuses a value here on
        /// one, rather than accepting it and drawing something the author did not ask for.
        public string? SeriesFieldKey { get; set; }

        public ReportChartKind ChartKind { get; set; } = ReportChartKind.Column;

        /// A CEILING ON DISCOVERED CATEGORIES, because the author cannot see the data when they place the
        /// element. A field that turns out to hold 4,000 distinct values would otherwise draw 4,000 bars —
        /// an unreadable chart and a 40MB document. Above the ceiling the largest are kept and the
        /// remainder collapses into one honest "other" slice, which is never silently dropped.
        public int MaxCategories { get; set; } = 12;

        /// Print the value beside each bar, slice or cell.
        public bool ShowValues { get; set; } = true;

        /// Cross-tab only: add a total row and a total column.
        public bool ShowGrandTotals { get; set; } = true;

        // ---- sub-report -----------------------------------------------------------------------------
        //
        // THE CHILD REPORT, BY ITS CATALOGUE CODE. Not a dataset code and not a query: naming a report
        // means the child arrives with the permission key, the column list and the tenancy its own
        // definition already declares, so embedding it can never show more than running it would.
        public string? SubReportCode { get; set; }

        /// The child column matched against the PARENT GROUP's value.
        ///
        /// The link is the group, not a row, because a Detail band in this platform renders once with the
        /// whole page run rather than once per row — there is no "current row" there to link to. Null in a
        /// report band, where the child is embedded whole and unlinked.
        public string? LinkChildFieldKey { get; set; }

        // NO COLUMN LIST. The sub-report prints the CHILD definition's own visible columns, so there is no
        // second place where "may this reader see this field" gets answered — and no way for a parent
        // layout to name a child column that the child report would itself have hidden.

        public List<ReportTableColumn> Columns { get; set; } = new();
        public ReportElementStyle Style { get; set; } = new();
    }

    // ---- band -----------------------------------------------------------------------------------
    public sealed class ReportBand
    {
        public ReportBandKind Kind { get; set; } = ReportBandKind.Detail;
        public double HeightMm { get; set; } = 20;

        // Only meaningful on GroupHeader/GroupFooter. Bound to a groupable dataset field, and it is the SAME
        // grouping the engine already owns — §8 forbids a second implementation, so this names a field and the
        // shaper does the grouping.
        public string? GroupFieldKey { get; set; }

        public List<ReportElement> Elements { get; set; } = new();
    }

    // ---- the layout -----------------------------------------------------------------------------
    public sealed class ReportVisualLayout
    {
        public const int CurrentSchemaVersion = 1;

        public int SchemaVersion { get; set; } = CurrentSchemaVersion;

        // REUSED, not redefined. ReportPageSetup already carries paper size, orientation, four margins in mm
        // and the RTL flag — everything §2 asks for — and the existing renderer already understands it.
        public ReportPageSetup Page { get; set; } = ReportPageSetup.Default;

        public double GridMm { get; set; } = 5;
        public bool SnapToGrid { get; set; } = true;

        public List<ReportBand> Bands { get; set; } = new();

        // §9: the dataset's own declared parameters, by key. Typed and validated server-side against the
        // dataset's ReportParameterDescriptor list — never free-form, never SQL.
        public Dictionary<string, string?> Parameters { get; set; } = new();

        public bool IsEmpty => Bands.Count == 0 || Bands.All(b => b.Elements.Count == 0);

        // The seven bands in print order, each once. A designer that let a user create two Detail bands would
        // have to answer which one repeats.
        public static ReportVisualLayout Blank() => new()
        {
            Bands = new List<ReportBand>
            {
                new() { Kind = ReportBandKind.ReportHeader, HeightMm = 35 },
                new() { Kind = ReportBandKind.PageHeader,   HeightMm = 12 },
                new() { Kind = ReportBandKind.GroupHeader,  HeightMm = 10 },
                new() { Kind = ReportBandKind.Detail,       HeightMm = 8  },
                new() { Kind = ReportBandKind.GroupFooter,  HeightMm = 10 },
                new() { Kind = ReportBandKind.PageFooter,   HeightMm = 12 },
                new() { Kind = ReportBandKind.ReportFooter, HeightMm = 25 },
            },
        };

        // ------------------------------------------------------------------------------------------
        // A STARTER LAYOUT: the report as it looks TODAY, ready to be edited.
        //
        // Blank() gives the seven empty bands, which is the right answer for "design something new" and
        // the wrong one for "change how this report looks" — the designer opened on an empty page and the
        // reader had to rebuild, from memory, the table they had just been looking at. This lays the
        // report's own title, date and column set onto the canvas so the first thing the designer shows
        // is the thing being edited.
        //
        // Deliberately PLAIN: a title, a date, and one table. It is a starting point to change, not a
        // house style to fight — anything more opinionated would have to be undone before it could be
        // used.
        public static ReportVisualLayout StarterFor(string title, IReadOnlyList<ReportColumn> columns,
            ReportPageSetup? page = null)
        {
            var layout = Blank();
            if (page is not null) layout.Page = page;

            var (paperW, _) = ReportPaper.SizeOf(layout.Page.PageSize);
            if (layout.Page.Orientation == ReportOrientation.Landscape)
                paperW = ReportPaper.SizeOf(layout.Page.PageSize).HeightMm;
            double contentW = Math.Max(20, paperW - layout.Page.MarginLeftMm - layout.Page.MarginRightMm);

            var header = layout.Band(ReportBandKind.ReportHeader);
            if (header is not null)
            {
                // A SYSTEM FIELD, NOT FROZEN TEXT. As a Text element the title was a copy of the report's
                // name taken at the moment the canvas was seeded, so renaming the report left the printed
                // heading saying the old thing — and the only way to correct it was to find the box and
                // retype it. ReportSystemField.ReportName resolves at render time, so the heading follows
                // the name for as long as nobody deliberately replaces it with their own text.
                header.Elements.Add(new ReportElement
                {
                    Id = "starter-title", Kind = ReportElementKind.SystemField,
                    SystemField = ReportSystemField.ReportName,
                    XMm = 0, YMm = 4, WidthMm = contentW * 0.7, HeightMm = 10,
                    Style = new ReportElementStyle { FontSizePt = 16, Bold = true },
                });
                header.Elements.Add(new ReportElement
                {
                    Id = "starter-date", Kind = ReportElementKind.SystemField,
                    SystemField = ReportSystemField.CurrentDateTime,
                    XMm = contentW * 0.7, YMm = 5, WidthMm = contentW * 0.3, HeightMm = 6,
                    Style = new ReportElementStyle { FontSizePt = 8, Align = ReportTextAlign.End },
                });
            }

            // The table goes in DETAIL and nowhere else — the validator rejects it anywhere else, because a
            // table repeats dataset rows and only that band repeats.
            var visible = columns.Where(c => c.VisibleByDefault && !c.Internal).ToList();
            if (visible.Count == 0) visible = columns.Where(c => !c.Internal).ToList();

            var detail = layout.Band(ReportBandKind.Detail);
            if (detail is not null && visible.Count > 0)
            {
                // THE BAND HEIGHT IS THE PRINT ROW HEIGHT — the renderer charges one detail height for
                // the table's header row and one for every data row. Blank()'s 8mm is tight for eight
                // columns of text at 9pt once a border and a little padding are in; 10mm is a row a
                // person can read and still fits about 24 rows on an A4 page.
                detail.HeightMm = 10;

                // The definition's own widths, scaled to the paper. A column that declared no width gets an
                // even share, so a dataset that never set WidthMm still opens on a usable table rather than
                // a row of 25mm stubs running off the page.
                double declared = visible.Sum(c => c.WidthMm > 0 ? c.WidthMm : 0);
                int unsized = visible.Count(c => c.WidthMm <= 0);
                double spare = Math.Max(0, contentW - declared);
                double each = unsized > 0 ? spare / unsized : 0;
                double total = declared + (unsized * each);
                double scale = total > 0 ? contentW / total : 1;

                var table = new ReportElement
                {
                    Id = "starter-table", Kind = ReportElementKind.Table,
                    // Fills its band exactly. An element taller than its band is drawn outside it in the
                    // designer, and the renderer overrides the height anyway (bandHeight - YMm), so the
                    // only value that is right in both places is the band's own.
                    XMm = 0, YMm = 0, WidthMm = contentW, HeightMm = detail.HeightMm,
                };
                foreach (var c in visible)
                {
                    double w = (c.WidthMm > 0 ? c.WidthMm : each) * scale;
                    table.Columns.Add(new ReportTableColumn
                    {
                        FieldKey = c.Key,
                        HeaderText = null,                    // null = the column's own title, in the run's language
                        WidthMm = Math.Clamp(w, 5, contentW),
                        Align = c.Align == ReportAlign.End ? ReportTextAlign.End
                              : c.Align == ReportAlign.Center ? ReportTextAlign.Center
                              : ReportTextAlign.Start,
                        Format = c.Format,
                    });
                }
                detail.Elements.Add(table);
            }

            return layout;
        }

        public ReportBand? Band(ReportBandKind kind) => Bands.FirstOrDefault(b => b.Kind == kind);

        public IEnumerable<ReportElement> AllElements => Bands.SelectMany(b => b.Elements);
    }

    // ============================================================================================
    // PAPER GEOMETRY — one table, used by the designer, the print renderer and the page-break maths.
    // ============================================================================================
    public static class ReportPaper
    {
        public static (double WidthMm, double HeightMm) SizeOf(ReportPageSize size) => size switch
        {
            ReportPageSize.A4 => (210, 297),
            ReportPageSize.A5 => (148, 210),
            ReportPageSize.Letter => (215.9, 279.4),
            ReportPageSize.Legal => (215.9, 355.6),
            ReportPageSize.Thermal80 => (80, 297),
            _ => (210, 297),
        };

        public static (double WidthMm, double HeightMm) Oriented(ReportPageSetup page)
        {
            var (w, h) = SizeOf(page.PageSize);
            return page.Orientation == ReportOrientation.Landscape ? (h, w) : (w, h);
        }

        // The printable box: paper minus margins. Element X is measured from the CONTENT start edge, which is
        // what makes one layout correct in both directions — see the validator's note on RTL.
        public static double ContentWidthMm(ReportPageSetup page)
        {
            var (w, _) = Oriented(page);
            return Math.Max(10, w - page.MarginLeftMm - page.MarginRightMm);
        }

        public static double ContentHeightMm(ReportPageSetup page)
        {
            var (_, h) = Oriented(page);
            return Math.Max(10, h - page.MarginTopMm - page.MarginBottomMm);
        }
    }
}
