using System.Reflection;
using System.Text;

namespace CrossBuy.BL.Reporting
{
    // ============================================================================================
    // Reporting Platform — THE DOCUMENT CARRIES ITS OWN TYPEFACE.
    //
    // A template used to NAME a font and hope the machine had it, and every failure this week was that
    // one assumption breaking in a different place:
    //
    //   · Cairo was not installed on the host      -> the report silently printed in something else;
    //   · it was installed, and the reader's browser had been open since the previous day, so its font
    //     list predated the install                -> the SAME document rendered Cairo on the server and
    //                                                  a fallback on the screen, from identical CSS;
    //   · a deployment server, or any second user  -> the whole thing starts over.
    //
    // A design that depends on the viewer's machine is not portable, and "the report renders from the
    // template" is not true while the template carries only a font's NAME.
    //
    // ------------------------------------------------------------------------------------------------
    // WHY THE PDF ALREADY WORKED, which is the clue that settled the design
    //
    // The PDF is produced server-side by Chromium, which EMBEDS the faces it used into the file — that is
    // why an Arabic PDF weighed 42 KB with a system font and 107 KB with Cairo. The document carries the
    // typeface, so it looks the same wherever it is opened, forever. This class gives the HTML the same
    // property by the same means: the bytes travel with the document.
    //
    // ------------------------------------------------------------------------------------------------
    // A data: URI, NOT a URL, and that is not a detail
    //
    // HtmlReportRenderer's rule at the top of that file is "self-contained: inline <style>, no external
    // stylesheet, NO FONT URL, no script", and it is right — an archived report has to render identically
    // in five years, and a link to a CDN makes it depend on a third party staying up. A data: URI is not a
    // network fetch; it is part of the document. The rule is kept, not bent. The preview's own CSP already
    // says `font-src data:`, which is the same decision written down somewhere else.
    //
    // EMBEDDED IN THE ASSEMBLY rather than read from wwwroot: a renderer has no content root and no
    // IWebHostEnvironment, and a path that resolves in development and not under IIS is exactly the kind
    // of thing that fails only after deployment. A manifest resource cannot go missing.
    //
    // ------------------------------------------------------------------------------------------------
    // STATIC FACES, NOT A VARIABLE FONT — and this one cost a day twice, so it is written down
    //
    // The file shipped here was Cairo-Variable.ttf, declared `font-weight:100 900`. On screen it is
    // perfect: document.fonts reports the face LOADED, document.fonts.check('12pt Cairo') is true, and
    // the computed family is Cairo. THE PDF WAS NOT. Its /BaseFont list named exactly one face —
    // `AAAAAA+SegoeUI-Bold` — on every designed report, which is the "الخط الزبالة" the owner kept
    // seeing while every on-screen check said the font was fine.
    //
    // Chromium's print-to-PDF does not embed a variable font: it lays the page out with it and then
    // falls back to a system face when writing the file. Measured both ways on the same machine, same
    // browser, same HTML:
    //
    //     Cairo-Variable.ttf (fvar present)  ->  PDF fonts: [AAAAAA+SegoeUI-Bold]
    //     Cairo-Regular.ttf  (static)        ->  PDF fonts: [AAAAAA+Cairo-Regular]
    //
    // So the two static faces are shipped instead. They are also SMALLER together (91 + 92 KB against
    // 345 KB), and a real bold beats a synthesised one at 8pt Arabic.
    //
    // THE LESSON, because it is the trap: document.fonts, getComputedStyle and a screenshot all agree
    // with each other and all of them can be wrong about the PDF. The only honest check is the PDF's own
    // /BaseFont list.
    // ------------------------------------------------------------------------------------------------
    //
    // COST, stated because it is real: ~183 KB of TTF becomes ~245 KB of base64 in each HTML export. It
    // is read and encoded ONCE per process. The PDF is unaffected — Chromium subsets what it uses.
    // ============================================================================================
    public static class ReportFontLibrary
    {
        // Family name (as an author picks it in Studio) -> the resource shipped for it.
        //
        // Only faces the product actually ships belong here. Everything else still resolves the ordinary
        // way, from the machine — embedding is an upgrade for the faces we can guarantee, not a
        // replacement for the font stack.
        // A family ships as one face PER WEIGHT. Not a variable file: see the note above — a variable
        // font renders on screen and is silently dropped on the way into the PDF.
        private static readonly Dictionary<string, (int Weight, string Resource)[]> Embedded =
            new(StringComparer.OrdinalIgnoreCase)
            {
                ["Cairo"] = new[]
                {
                    (400, "CrossBuy.BL.Reporting.Fonts.Cairo-Regular.ttf"),
                    (700, "CrossBuy.BL.Reporting.Fonts.Cairo-Bold.ttf"),
                },
            };

        public static bool IsEmbedded(string? family) =>
            !string.IsNullOrWhiteSpace(family) && Embedded.ContainsKey(family.Trim());

        // The @font-face blocks for a document set in this family, or "" when nothing is shipped for it.
        //
        // Returns the CSS rather than the bytes so the caller cannot get the declaration subtly wrong.
        // ONE RULE PER WEIGHT, each naming its own weight exactly: that is what lets the browser pick the
        // real bold for a heading instead of smearing the regular, and it is also the shape that survives
        // print-to-PDF.
        //
        // A face whose bytes could not be read is simply left out, so a missing bold still leaves a
        // document with its regular rather than with nothing.
        public static string FaceCss(string? family)
        {
            if (string.IsNullOrWhiteSpace(family)) return "";
            var key = family.Trim();
            if (!Embedded.TryGetValue(key, out var faces)) return "";

            var sb = new StringBuilder();
            foreach (var (weight, resource) in faces)
            {
                var data = Base64.GetOrAdd(resource, Encode);
                if (data.Length == 0) continue;

                sb.Append("@font-face{font-family:'").Append(key).Append("';")
                  .Append("src:url(data:font/ttf;base64,").Append(data).Append(") format('truetype');")
                  .Append("font-weight:").Append(weight.ToString(System.Globalization.CultureInfo.InvariantCulture))
                  .Append(";font-style:normal;font-display:block;}");
            }
            return sb.ToString();
        }

        // Read and encoded ONCE per process. A report can render many times a minute and this is half a
        // megabyte of base64; doing it per render would be the kind of cost nobody notices until a
        // scheduled batch runs.
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> Base64 =
            new(StringComparer.Ordinal);

        private static string Encode(string resource)
        {
            try
            {
                using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resource);

                // A MISSING RESOURCE IS NOT AN ERROR HERE. The font is an enhancement: without it the
                // document falls back to naming the family, which is exactly what it did before. Failing
                // the render because a typeface could not be embedded would turn a cosmetic gap into an
                // outage.
                if (stream is null) return "";

                using var buffer = new MemoryStream();
                stream.CopyTo(buffer);
                return Convert.ToBase64String(buffer.ToArray());
            }
            catch (Exception)
            {
                return "";
            }
        }

        // Convenience for a renderer building a stylesheet: the face block for whichever family the page
        // resolved to, ready to prepend. Kept here so both renderers ask the same question the same way.
        public static void AppendFaceFor(StringBuilder css, string? templateFace)
        {
            var face = FaceCss(templateFace);
            if (face.Length > 0) css.Append(face);
        }
    }
}
