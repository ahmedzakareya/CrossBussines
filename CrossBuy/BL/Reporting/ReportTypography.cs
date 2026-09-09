namespace CrossBuy.BL.Reporting
{
    // ============================================================================================
    // Reporting Platform — ONE TYPEFACE DECISION, IN ONE PLACE.
    //
    // Three renderers each carried their own hardcoded font stack, and they did not agree:
    //
    //     HtmlReportRenderer          'Segoe UI', Tahoma, Arial, sans-serif
    //     PlaywrightPdfReportRenderer 'Segoe UI', Tahoma, Arial, sans-serif   (header/footer template)
    //     ReportVisualRenderer        Inter, Tahoma, Arial, sans-serif
    //
    // So the same report was set in one face on screen and another through the visual designer, and
    // NONE of them was the application's own: the product is Inter for Latin (Metronic's
    // --bs-font-sans-serif) and Cairo for Arabic (_LayoutInventory sets `html[lang="ar"] body`).
    // A printed document that silently picks a face nobody chose is the thing this file removes.
    //
    // ------------------------------------------------------------------------------------------------
    // WHY A STACK AND NOT A WEBFONT LINK
    //
    // HtmlReportRenderer's own rule, at the top of that file: "Self-contained by rule: inline <style>,
    // no external stylesheet, no font URL, no script." That rule is right — an archived PDF has to
    // render the same in five years, and a Google Fonts URL makes it depend on the network and on a
    // third party. So the families are NAMED and the host has to have them.
    //
    // CONSEQUENCE, measured on this machine rather than assumed: Dubai, Segoe UI, Tahoma, Arial,
    // Sakkal Majalla, Simplified Arabic and Traditional Arabic are installed; CAIRO IS NOT. The
    // application gets Cairo from Google Fonts in the BROWSER, which the server-side PDF renderer
    // cannot do. So Cairo stays first in the Arabic stack — installing it (OFL, free) on the host
    // switches every report over with no code change — and DUBAI carries the document until then: a
    // Windows-shipped Arabic geometric sans, close in character to Cairo, that also sets Latin.
    //
    // THE ONE RULE THIS FILE EXISTS TO ENFORCE: the first face in a stack must be able to set BOTH
    // scripts. A font is resolved per GLYPH, so a Latin-only face at the head of the list hands the
    // Arabic to the next entry and the document comes out in two typefaces. That is not a theory — a
    // three-page Arabic report embedded SegoeUI, SegoeUI-Bold, Inter-Regular and ArialMT, and the PDF
    // named all four itself. Reading the output is how this was found; reasoning about the stack had
    // already produced two wrong answers.
    // ============================================================================================
    public static class ReportTypography
    {
        // EVERY ENTRY HERE CARRIES BOTH LATIN AND ARABIC. That is the property the whole stack turns on:
        // a browser resolves a font per GLYPH, so the moment an entry cannot set a script the next one does,
        // and the document ends up in two typefaces.
        //
        // Dubai first: a modern Arabic geometric sans that ships with Windows, is installed on this host,
        // and covers Latin too. Then the faces every Windows machine has.
        private const string Fallbacks = "Dubai, \"Segoe UI\", Tahoma, Arial, sans-serif";

        // TWO ATTEMPTS AT THIS WERE WRONG, and the PDF settled it. A three-page Arabic report embedded four
        // faces — SegoeUI, SegoeUI-Bold, Inter-Regular and ArialMT — because the Latin stack led with Inter.
        //
        // Inter is installed and is LATIN-ONLY. A per-glyph walk therefore took Latin from Inter and Arabic
        // from whatever came next, which is a two-family document in every report that mixes scripts — and in
        // this product nearly every report does: Arabic labels, Latin codes, Latin digits. My earlier note
        // claimed the fallbacks "carry both scripts"; the entry in FRONT of them did not, so it never
        // mattered. The rule is that the FIRST face must be dual-script.
        //
        // Cairo stays at the head of the Arabic stack on purpose: it is the application's own face, and
        // installing it on the render host makes it take over with no code change at all.
        //
        // Inter is gone from both stacks. It remains in DesignerFaces, where an author who wants a
        // Latin-only report can choose it deliberately rather than inherit the split by default.
        public const string Latin = Fallbacks;
        public const string Arabic = "Cairo, " + Fallbacks;

        // Figures in a report are columns of digits, so they are set in a face whose digits line up.
        public const string Monospace = "Consolas, \"Courier New\", monospace";

        public static string FamilyFor(bool arabic) => arabic ? Arabic : Latin;

        // Every face this platform is willing to name, in preference order.
        //
        // DUAL-SCRIPT FACES FIRST, because those are the ones that produce a single-family document.
        // The Arabic-designed families below are all Windows-shipped and were verified present on this host
        // by measuring a real Arabic glyph against the tofu box — not by assuming a name exists.
        private static readonly string[] Candidates =
        {
            "Cairo", "Dubai", "Segoe UI", "Tahoma", "Arial",
            "Sakkal Majalla", "Simplified Arabic", "Traditional Arabic",

            // Latin-only from here down. Legitimate for a report that carries no Arabic, and a split
            // document if it does — which is why they are last rather than absent.
            "Inter", "Times New Roman", "Courier New",
        };

        // ---- what the RENDER HOST can actually draw ------------------------------------------------------
        //
        // OFFERING A FONT THE MACHINE DOES NOT HAVE IS WORSE THAN NOT OFFERING IT. Cairo is the case that
        // proved it: the application loads Cairo from Google Fonts in the BROWSER, so it looks installed —
        // but the renderer runs server-side under a rule that forbids a font URL, and Cairo is not among the
        // system fonts. So an author could pick Cairo, save, and see no change at all, with nothing anywhere
        // to explain why. "I changed the font and the report did not" was exactly right.
        //
        // Probed ONCE, lazily, and never again: enumerating installed families is a system call, and the set
        // does not change while the process runs. A failure to enumerate falls back to the full list rather
        // than to an empty one — a host that cannot answer should not lose its font picker.
        private static readonly Lazy<string[]> Available = new(() =>
        {
            try
            {
                var installed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                using (var fonts = new System.Drawing.Text.InstalledFontCollection())
                    foreach (var family in fonts.Families)
                        installed.Add(family.Name);

                var present = Candidates.Where(installed.Contains).ToArray();
                return present.Length > 0 ? present : Candidates;
            }
            catch
            {
                return Candidates;
            }
        });

        // The faces a Studio author may choose. Filtered to what the host has, so a pick always changes
        // something. ReportVisualLayoutValidator accepts exactly this list, so the designer cannot offer a
        // face the save would reject either.
        public static string[] DesignerFaces => Available.Value;

        // ---- WHAT A DOCUMENT IS ACTUALLY SET IN ----------------------------------------------------------
        //
        // The template decides. Every renderer calls THIS, so a report cannot come out in one face through
        // the table renderer and another through the visual designer — the drift this file was created to
        // end, and which came back the moment each renderer built its own string.
        //
        // WHY THE PLATFORM STACK IS NOT APPENDED TO A CHOSEN FACE. It used to be: the author's face, then
        // Cairo, Dubai, Segoe UI, Tahoma, Arial. That reads like prudence and behaves like a silent veto —
        // a font is resolved per GLYPH, so any character the chosen face happens to lack was quietly taken
        // from a family the CODE picked, and the document came out in two typefaces neither of which the
        // author could see in Studio. Choosing a face has to mean the document is in that face.
        //
        // ONE generic keyword remains, and it is not a second opinion: `sans-serif` is what the browser
        // falls back to anyway when a named family is absent. Spelling it out changes nothing about which
        // font is used and keeps the declaration valid, which matters — an invalid font-family is DROPPED
        // whole, and that is exactly how a footer ended up in Arial.
        //
        // A template that names NO face still gets the platform stack, because something has to be said and
        // the stack is at least dual-script. After BackfillTemplateTypographyAsync no live template is in
        // that state, so this is the path for a row written by an older build, not the normal one.
        public static string DocumentFamily(string? templateFace, bool arabic) =>
            string.IsNullOrWhiteSpace(templateFace)
                ? FamilyFor(arabic)
                : "\"" + templateFace.Trim() + "\", sans-serif";

        // ---- the face a NEW TEMPLATE is born with --------------------------------------------------------
        //
        // "كله بيعرض من القالب ممنوع من بره القالب" — everything renders from the template, nothing from
        // outside it. A seeded template that stored FontFamily = null met the letter of that and broke its
        // point: null means "ask the code", so the document's typeface was still a constant in this file
        // that no author could see or change. Storing a real face name puts the decision in the row, where
        // Report Studio shows it, edits it and versions it like every other part of the design.
        //
        // RESOLVED AGAINST THE HOST rather than hardcoded, because naming an absent face is what caused the
        // original complaint: pick Cairo when the host has it, else the first installed dual-script face.
        // Candidates is ordered so that walk lands on Dubai here, and on Cairo the day it is installed.
        public static string DefaultDocumentFace => Available.Value.FirstOrDefault(IsDualScript) ?? "Segoe UI";

        // Latin-only faces are legitimate CHOICES and terrible DEFAULTS, so they are excluded from the walk
        // above but stay in DesignerFaces. Kept as an explicit list because there is no way to ask GDI
        // whether a family covers Arabic without rasterising a glyph and comparing it to the tofu box.
        private static bool IsDualScript(string face) =>
            !LatinOnly.Contains(face, StringComparer.OrdinalIgnoreCase);

        private static readonly string[] LatinOnly = { "Inter", "Times New Roman", "Courier New" };

        // Named separately for a caller that wants to explain the gap — the difference between this and
        // DesignerFaces is the list of faces this product would use if they were installed.
        public static IEnumerable<string> MissingFromHost => Candidates.Except(Available.Value, StringComparer.OrdinalIgnoreCase);
    }
}
