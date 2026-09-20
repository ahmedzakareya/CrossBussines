namespace CrossBuy.BL.Reporting
{
    /// THE SIXTEEN DRAWINGS, IN ONE PLACE, because two places is where they would stop matching.
    ///
    /// The renderer needs them to draw the document. The DESIGNER needs the same ones to draw the
    /// element's face on the canvas — and unlike a chart or a QR, an icon is fully drawable at design
    /// time: it takes no data, so the canvas can show the author the actual mark rather than a caption
    /// naming it. That is the one element where the design-time face and the printed output are the
    /// same drawing, and it is only true while there is one definition of it.
    ///
    /// So this is not a helper class for tidiness. If the paths lived in the renderer and the view kept
    /// its own copy, the canvas would go on showing the old mark after the printed one changed, and the
    /// author would be designing against a picture that lies.
    ///
    /// WHY STROKE AND WHY ONE GRID. All sixteen share a 24-unit box, a 1.7 stroke and round joins. A set
    /// that does not share those reads as sixteen pictures rather than as a vocabulary, and a KPI row is
    /// exactly where that shows — four of them side by side, at the same size, an inch apart.
    ///
    /// Stroke rather than fill, and currentColor rather than a colour of their own, so an icon takes the
    /// element's Style.Color like every other mark on the page and a recoloured report stays one report.
    /// A filled icon also goes solid black in a monochrome print, where an outline still reads.
    ///
    /// Nothing here is fetched, scripted or linked: no script, no href, no foreignObject — the same three
    /// things ReportAssetService refuses in an uploaded SVG are absent by construction, which is what
    /// lets the same markup survive the HTML view, the print view and Chromium's PDF pass unchanged.
    public static class ReportIconArt
    {
        /// The path data for one icon, on the 24x24 grid. An undefined value answers with the default
        /// mark rather than with nothing: a layout written against a newer build draws something.
        public static string Path(ReportIcon icon) => icon switch
        {
            ReportIcon.Money      => "<rect x='2.5' y='6' width='19' height='12' rx='2'/><circle cx='12' cy='12' r='2.6'/><path d='M5.6 9.2h.01M18.4 14.8h.01'/>",
            ReportIcon.Invoice    => "<path d='M6 2.5h8l4 4v15H6z'/><path d='M14 2.5V7h4'/><path d='M9 12h6M9 15.5h6M9 8.5h2'/>",
            ReportIcon.Customer   => "<circle cx='12' cy='8' r='3.4'/><path d='M5 20a7 7 0 0 1 14 0'/>",
            ReportIcon.Vendor     => "<path d='M3 9.5 5 4h14l2 5.5'/><path d='M3 9.5h18V20H3z'/><path d='M9 9.5V20'/>",
            ReportIcon.Item       => "<path d='M12 2.6 20.5 7v10L12 21.4 3.5 17V7z'/><path d='M3.5 7 12 11.6 20.5 7'/><path d='M12 11.6v9.8'/>",
            ReportIcon.Warehouse  => "<path d='M2.8 10 12 4l9.2 6'/><path d='M4.6 10v10h14.8V10'/><path d='M9 20v-5.5h6V20'/>",
            ReportIcon.TrendUp    => "<path d='M3 16.5 9 10l4 4 7-7.5'/><path d='M15.5 6.5H20V11'/>",
            ReportIcon.TrendDown  => "<path d='M3 7.5 9 14l4-4 7 7.5'/><path d='M15.5 17.5H20V13'/>",
            ReportIcon.Chart      => "<path d='M3.5 20.5h17'/><rect x='5' y='11' width='3.4' height='7'/><rect x='10.3' y='6' width='3.4' height='12'/><rect x='15.6' y='13.5' width='3.4' height='4.5'/>",
            ReportIcon.Calendar   => "<rect x='3' y='5' width='18' height='16' rx='2'/><path d='M3 9.5h18'/><path d='M8 3v4M16 3v4'/>",
            ReportIcon.Clock      => "<circle cx='12' cy='12' r='9'/><path d='M12 6.8V12l3.4 2.2'/>",
            ReportIcon.Percent    => "<path d='M5.5 18.5 18.5 5.5'/><circle cx='7.6' cy='7.6' r='2.6'/><circle cx='16.4' cy='16.4' r='2.6'/>",
            ReportIcon.Bank       => "<path d='M3 9.5 12 4l9 5.5'/><path d='M4.5 20.5h15'/><path d='M6.5 9.5v9M10.8 9.5v9M15.1 9.5v9M19.4 9.5v9'/>",
            ReportIcon.Tax        => "<path d='M6 2.5h8l4 4v15H6z'/><path d='M14 2.5V7h4'/><path d='M9.2 16.6 15 10.8'/><circle cx='10' cy='11.4' r='1.2'/><circle cx='14.2' cy='16' r='1.2'/>",
            ReportIcon.Check      => "<circle cx='12' cy='12' r='9'/><path d='M7.8 12.3 10.8 15.3 16.4 9.6'/>",
            ReportIcon.Alert      => "<path d='M12 3.4 21.4 19.6H2.6z'/><path d='M12 9.6v4.4'/><path d='M12 17.2h.01'/>",
            _                     => "<rect x='2.5' y='6' width='19' height='12' rx='2'/><circle cx='12' cy='12' r='2.6'/>",
        };

        /// What an author may pick, in the order the picker shows them. Enum.GetValues rather than a
        /// hand-kept list, so adding an icon to the vocabulary cannot leave the picker behind.
        public static IReadOnlyList<ReportIcon> All { get; } =
            (ReportIcon[])System.Enum.GetValues(typeof(ReportIcon));

        /// The bilingual name, for the picker's tooltip and the drawing's aria-label. An icon that
        /// cannot be named cannot be chosen from a grid of sixteen, and a screen reader gets the same
        /// word the author saw.
        public static string Title(ReportIcon icon, bool arabic) => icon switch
        {
            ReportIcon.Money      => arabic ? "نقدية"        : "Money",
            ReportIcon.Invoice    => arabic ? "فاتورة"       : "Invoice",
            ReportIcon.Customer   => arabic ? "عميل"         : "Customer",
            ReportIcon.Vendor     => arabic ? "مورّد"        : "Vendor",
            ReportIcon.Item       => arabic ? "صنف"          : "Item",
            ReportIcon.Warehouse  => arabic ? "مخزن"         : "Warehouse",
            ReportIcon.TrendUp    => arabic ? "ارتفاع"       : "Trend up",
            ReportIcon.TrendDown  => arabic ? "انخفاض"       : "Trend down",
            ReportIcon.Chart      => arabic ? "رسم بياني"    : "Chart",
            ReportIcon.Calendar   => arabic ? "تاريخ"        : "Calendar",
            ReportIcon.Clock      => arabic ? "وقت"          : "Clock",
            ReportIcon.Percent    => arabic ? "نسبة"         : "Percent",
            ReportIcon.Bank       => arabic ? "بنك"          : "Bank",
            ReportIcon.Tax        => arabic ? "ضريبة"        : "Tax",
            ReportIcon.Check      => arabic ? "مُسوّى"        : "Settled",
            ReportIcon.Alert      => arabic ? "تنبيه"        : "Alert",
            _                     => icon.ToString(),
        };
    }
}
