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
        public string? FieldKey { get; set; }
        public ReportSystemField SystemField { get; set; } = ReportSystemField.CurrentDate;
        public ReportAggregate Aggregate { get; set; } = ReportAggregate.Sum;

        public int? AssetId { get; set; }
        public ReportImageRole ImageRole { get; set; } = ReportImageRole.Custom;
        public ReportImageFit Fit { get; set; } = ReportImageFit.Contain;
        public bool PreserveAspect { get; set; } = true;

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
