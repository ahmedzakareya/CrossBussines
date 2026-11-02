using System.Text;
using CrossBuy.BL.Reporting;
using Xunit;
using Xunit.Abstractions;

namespace CrossBuy.Tests
{
    // ============================================================================================
    // REP-04 — THE PDF RUNTIME, MEASURED RATHER THAN ASSUMED.
    //
    // WHAT WAS UNVERIFIABLE BEFORE. PlaywrightHtmlToPdfConverter.IsAvailable is "optimistic until a
    // launch actually fails" by design, so reading the source can never tell you whether Chromium is
    // actually present in a given environment. The capability catalogue recorded REP-04 as "Not
    // covered end to end" for exactly that reason. These tests close it the only way it can be
    // closed: by launching the browser and reading the bytes that come back.
    //
    // THEY SKIP, VISIBLY, RATHER THAN FAIL ON A MACHINE WITHOUT THE BROWSER. `playwright install
    // chromium` is an environment provisioning step, and a CI box that has not run it is not a
    // defect in this code.
    //
    // THE SKIP IS [SkippableFact] + Skip.If, NOT `Assert.True(true, reason)`. The difference is the
    // whole point. The Assert form PASSES, so a machine with no browser reports these four as green
    // and inflates the suite's count with tests that did nothing — which is exactly how a sibling
    // suite (ReportingSchemaDeploymentTests) hid a real stale-schema defect behind a 586/586 that
    // should have read 595/1. Skip.If produces a REAL Skipped outcome that appears in its own column,
    // so "no browser here" can never be mistaken for "PDF verified".
    //
    // NOTHING HERE TOUCHES THE NETWORK. SetContentAsync renders a self-contained string; there is no
    // server to reach and the converter's own comment records that as deliberate.
    // ============================================================================================
    public class ReportingPdfRuntimeTests : IAsyncLifetime
    {
        private PlaywrightHtmlToPdfConverter _converter = null!;
        private readonly ITestOutputHelper _out;

        // EVERY TEST PRINTS WHAT IT MEASURED, and that is not decoration. Skip.If already keeps a
        // browserless machine out of the PASSED column, so these lines answer the second question:
        // what the artifact actually contained — byte count, page box, embedded faces — so a pass is
        // auditable rather than taken on trust. Run with `--logger:"console;verbosity=detailed"`.
        public ReportingPdfRuntimeTests(ITestOutputHelper output) => _out = output;

        public Task InitializeAsync()
        {
            _converter = new PlaywrightHtmlToPdfConverter();
            return Task.CompletedTask;
        }

        public async Task DisposeAsync() => await _converter.DisposeAsync();

        /// Minimal self-contained document. `family` is injected through the SAME helper the renderers
        /// use, so what is proven here is the path the product actually takes.
        private static string Document(string text, string? family = null)
        {
            var css = new StringBuilder();
            if (family is not null) ReportFontLibrary.AppendFaceFor(css, family);

            return $@"<!doctype html><html><head><meta charset=""utf-8""><style>{css}
body {{ font-family: {(family is null ? "sans-serif" : $"'{family}', sans-serif")}; font-size: 14pt; }}
</style></head><body><p>{text}</p></body></html>";
        }

        /// Convert, or return null when this machine has no browser. The reason is surfaced by the
        /// caller so a skip is always explained.
        private async Task<(byte[]? Pdf, string? Unavailable)> TryConvertAsync(
            string html, ReportPdfOptions? options = null)
        {
            try
            {
                var pdf = await _converter.ConvertAsync(html, options ?? new ReportPdfOptions());
                return (pdf, null);
            }
            catch (Exception ex) when (!_converter.IsAvailable)
            {
                return (null, _converter.UnavailableReason ?? ex.Message);
            }
        }

        // ---- the capability itself -------------------------------------------------------------

        // REP-04's acceptance in one assertion: real bytes, with a PDF header and an EOF marker, from
        // the real browser. A truncated or empty buffer would satisfy neither.
        [SkippableFact]
        public async Task The_browser_produces_a_structurally_valid_pdf()
        {
            var (pdf, unavailable) = await TryConvertAsync(Document("Runtime evidence."));

            Skip.If(pdf is null, $"No browser on this machine: {unavailable}");

            _out.WriteLine($"EVIDENCE engine={_converter.EngineName} bytes={pdf.Length} " +
                           $"header={Encoding.ASCII.GetString(pdf, 0, 8)}");

            Assert.StartsWith("%PDF-", Encoding.ASCII.GetString(pdf, 0, 5));
            Assert.Contains("%%EOF", Encoding.ASCII.GetString(pdf[^32..]));
            Assert.True(pdf.Length > 800, $"PDF was only {pdf.Length} bytes, which is not a rendered page.");
            Assert.True(_converter.IsAvailable);
            Assert.Null(_converter.UnavailableReason);
        }

        // ---- the options actually reach Chromium -----------------------------------------------

        // A5 and A4 must not produce the same geometry. If PaperName were dropped on the way through,
        // every export would silently be A4 and no other test in the suite would notice.
        //
        // ASSERTED ON /MediaBox, NOT ON BYTE LENGTH. The first version of this test compared
        // a4.Length to a5.Length and passed by a two-byte margin — close enough to zero that it
        // could have gone either way on a different string, which makes it a coin toss wearing a
        // test's name. The page box is the thing that actually changed, so it is the thing measured.
        [SkippableFact]
        public async Task A_different_page_size_produces_a_different_page_box()
        {
            var html = Document("Geometry.");

            var (a4, unavailable) = await TryConvertAsync(html,
                new ReportPdfOptions { PageSize = ReportPageSize.A4 });
            Skip.If(a4 is null, $"No browser on this machine: {unavailable}");

            var (a5, _) = await TryConvertAsync(html, new ReportPdfOptions { PageSize = ReportPageSize.A5 });
            Assert.NotNull(a5);

            var boxA4 = MediaBox(a4);
            var boxA5 = MediaBox(a5!);

            _out.WriteLine($"EVIDENCE a4MediaBox={boxA4} a5MediaBox={boxA5}");

            Assert.NotNull(boxA4);
            Assert.NotNull(boxA5);
            Assert.NotEqual(boxA4, boxA5);

            // A5 is half of A4, so its height must be smaller — not merely different. This catches a
            // swap that produced two valid-but-exchanged boxes.
            Assert.True(HeightOf(boxA5!) < HeightOf(boxA4!),
                $"A5 ({boxA5}) should be shorter than A4 ({boxA4}).");
        }

        /// The first /MediaBox in the document, as its four raw numbers. PDF page boxes are written
        /// as plain text, so this needs no PDF library.
        private static string? MediaBox(byte[] pdf)
        {
            var match = System.Text.RegularExpressions.Regex.Match(
                Encoding.Latin1.GetString(pdf), @"/MediaBox\s*\[\s*([\d.\s-]+?)\s*\]");
            return match.Success
                ? System.Text.RegularExpressions.Regex.Replace(match.Groups[1].Value, @"\s+", " ")
                : null;
        }

        private static double HeightOf(string mediaBox)
        {
            var n = mediaBox.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Select(v => double.Parse(v, System.Globalization.CultureInfo.InvariantCulture))
                .ToArray();
            return Math.Abs(n[3] - n[1]);
        }

        // ---- the font defect the converter was written to prevent -------------------------------

        // THE DEFECT THIS PINS, and it is recorded in the converter's own comment: `Load` does not
        // wait for an @font-face, so printing immediately captured a page still laid out in the
        // fallback. The evidence is read off the ARTIFACT — the PDF's own /BaseFont entries — never
        // inferred from the HTML, because the HTML always says what we asked for.
        //
        // The assertion is deliberately about the DECLARED face appearing, not about the fallback
        // being absent: Chromium legitimately embeds more than one face for a mixed document.
        [SkippableFact]
        public async Task A_declared_embedded_face_reaches_the_artifact()
        {
            const string Family = "Cairo";

            Skip.IfNot(ReportFontLibrary.IsEmbedded(Family),
                $"'{Family}' is not an embedded face in this build.");

            var (pdf, unavailable) = await TryConvertAsync(Document("شهادة", Family));
            Skip.If(pdf is null, $"No browser on this machine: {unavailable}");

            // Latin1 keeps every byte addressable as a char, so a name split across the buffer is
            // still found. The font table is plain text inside the PDF even when streams are deflated.
            var raw = Encoding.Latin1.GetString(pdf);

            // The artifact's OWN font list, printed. This is the evidence the memory rule demands:
            // read the /BaseFont entries off the PDF rather than trusting the HTML that asked for them.
            var faces = System.Text.RegularExpressions.Regex
                .Matches(raw, @"/BaseFont\s*/([A-Za-z0-9+,\-]+)")
                .Select(m => m.Groups[1].Value)
                .Distinct(StringComparer.Ordinal)
                .ToList();

            _out.WriteLine($"EVIDENCE bytes={pdf.Length} baseFonts=[{string.Join(", ", faces)}]");

            Assert.Contains("/BaseFont", raw);
            Assert.Contains(Family, raw);
        }

        // ---- the availability flag tells the truth ----------------------------------------------

        // IsAvailable is optimistic BEFORE a launch, which is correct — a probe that launched a
        // browser to answer would make the catalogue slow. What matters is that it is accurate AFTER
        // one, so a UI that hides the PDF button is hiding it for a real reason.
        [Fact]
        public async Task Availability_is_accurate_once_a_launch_has_been_attempted()
        {
            var (pdf, unavailable) = await TryConvertAsync(Document("Availability."));

            if (pdf is null)
            {
                _out.WriteLine($"SKIPPED: {unavailable}");
                Assert.False(_converter.IsAvailable);
                Assert.False(string.IsNullOrWhiteSpace(_converter.UnavailableReason));
                return;
            }

            _out.WriteLine($"EVIDENCE available={_converter.IsAvailable} engine={_converter.EngineName}");

            Assert.True(_converter.IsAvailable);
            Assert.Equal("Playwright.Chromium", _converter.EngineName);
        }
    }
}
