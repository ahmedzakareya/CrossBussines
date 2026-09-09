using System.Text;

namespace CrossBuy.BL.Reporting
{
    // ============================================================================================
    // Reporting Platform — THE HOUSE NOTICE, DEFINED ONCE.
    //
    // "Preview — rows are limited" and "the result was truncated" are the two things a report must say
    // about ITSELF, and they were said in only one of the two renderers: a report with a visual design
    // printed no notice at all, so the same run was honest as a plain table and silent as a designed
    // document. The renderer that says nothing is the one people actually hand to someone.
    //
    // ONE DEFINITION, because the alternative was just demonstrated. This shape already existed in
    // HtmlReportRenderer; copying it into ReportVisualRenderer would have produced two notices free to
    // drift, which is the same mistake as the two page objects and the three font stacks that this
    // module has spent the week un-picking.
    //
    // ------------------------------------------------------------------------------------------------
    // WHY IT IS HAND-WRITTEN CSS AND AN INLINE SVG
    //
    // The house shape is /Accounting/JournalEntry's:
    //     notice d-flex bg-light-<tone> rounded border-<tone> border border-dashed p-6
    // with a 45px symbol tile, an h4 and the body, coloured BY KIND. Those are Metronic classes from a
    // stylesheet a self-contained report may not load, and `ki-outline ki-information-5` is an icon
    // FONT — a report referencing it renders an empty box everywhere the font is absent, which is every
    // archived PDF. So the shape is reproduced and the icon is drawn.
    //
    // The tones come from ReportBranding, which carries crossbuy-brand.css's own values. Note that
    // WARNING keeps a DARK label where every other family takes white: amber cannot carry small white
    // text, and that is measured in the brand layer, not a preference.
    // ============================================================================================
    public static class ReportNotice
    {
        public enum Kind
        {
            // The document is provisional: fewer rows than the real run would return.
            Preview = 0,

            // Rows are MISSING from a document that does not say so anywhere else. Danger rather than
            // warning on purpose — this one changes whether the numbers can be trusted.
            Truncated = 1,
        }

        public static string Heading(Kind kind, bool arabic) => kind switch
        {
            Kind.Truncated => arabic ? "النتيجة مقطوعة" : "Truncated",
            _ => arabic ? "معاينة" : "Preview",
        };

        public static string Body(Kind kind, bool arabic) => kind switch
        {
            Kind.Truncated => arabic
                ? "بعض الصفوف غير معروضة"
                : "Rows are missing from this output",
            _ => arabic
                ? "عدد الصفوف محدود، وهذه ليست النسخة النهائية"
                : "The row count is limited; this is not the final document",
        };

        public static void Render(StringBuilder sb, Kind kind, bool arabic)
        {
            var glyph = kind == Kind.Truncated
                // An exclamation: rows are missing, and that is not an informational aside.
                ? "<path d=\"M12 7v6\" stroke-width=\"2.4\" stroke-linecap=\"round\"/>"
                  + "<circle cx=\"12\" cy=\"16.6\" r=\"1.3\" fill=\"currentColor\" stroke=\"none\"/>"
                // An i-in-a-circle, matching ki-information-5 — what the house uses for a notice the
                // reader can act on.
                : "<circle cx=\"12\" cy=\"7.6\" r=\"1.3\" fill=\"currentColor\" stroke=\"none\"/>"
                  + "<path d=\"M12 11v6\" stroke-width=\"2.4\" stroke-linecap=\"round\"/>";

            sb.Append("<div class=\"cbrep-notice cbrep-notice-")
              .Append(kind == Kind.Truncated ? "truncated" : "preview").Append("\">")
              .Append("<span class=\"cbrep-notice-tile\">")
              .Append("<svg viewBox=\"0 0 24 24\" width=\"20\" height=\"20\" fill=\"none\" ")
              .Append("stroke=\"currentColor\" aria-hidden=\"true\">")
              .Append("<circle cx=\"12\" cy=\"12\" r=\"9.5\" stroke-width=\"1.8\"/>")
              .Append(glyph)
              .Append("</svg></span>")
              .Append("<div class=\"cbrep-notice-body\">")
              .Append("<h4>").Append(Enc(Heading(kind, arabic))).Append("</h4>")
              .Append("<div>").Append(Enc(Body(kind, arabic))).Append("</div>")
              .Append("</div></div>");
        }

        // Appended into whichever stylesheet the caller is building. Both renderers are self-contained,
        // so each carries its own copy of these rules in its own document — but from this one source.
        public static void Css(StringBuilder sb, ReportBranding b)
        {
            sb.Append(".cbrep-notice{display:flex;align-items:flex-start;gap:10px;")
              .Append("padding:10px 12px;margin-block-end:10px;border:1px dashed;border-radius:6px;")
              .Append("break-inside:avoid;}");
            sb.Append(".cbrep-notice-tile{flex:0 0 auto;width:34px;height:34px;border-radius:6px;")
              .Append("display:inline-flex;align-items:center;justify-content:center;}");
            sb.Append(".cbrep-notice-body h4{margin:0 0 1px;font-size:10pt;font-weight:700;}");
            sb.Append(".cbrep-notice-body div{font-size:8.5pt;font-weight:500;}");

            sb.Append(".cbrep-notice-preview{background:").Append(b.WarningSurface)
              .Append(";border-color:").Append(b.WarningBorder)
              .Append(";color:").Append(b.WarningText).Append(";}");
            sb.Append(".cbrep-notice-preview .cbrep-notice-tile{background:").Append(b.WarningColor)
              .Append(";color:").Append(b.WarningInverse).Append(";}");

            sb.Append(".cbrep-notice-truncated{background:").Append(b.DangerSurface)
              .Append(";border-color:").Append(b.DangerBorder)
              .Append(";color:").Append(b.DangerText).Append(";}");
            sb.Append(".cbrep-notice-truncated .cbrep-notice-tile{background:").Append(b.DangerColor)
              .Append(";color:").Append(b.DangerInverse).Append(";}");
        }

        private static string Enc(string? s) => System.Net.WebUtility.HtmlEncode(s ?? "");
    }
}
