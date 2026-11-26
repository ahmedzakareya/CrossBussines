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

        // ---- pagination -------------------------------------------------------------------------

        /// Content guaranteed to overflow one A4 page, and deliberately not a loop of identical text:
        /// numbered lines make a truncation visible in the artifact if one ever creeps in.
        private static string LongDocument(int lines) =>
            Document(string.Join("</p><p>", Enumerable.Range(1, lines).Select(i => $"Line {i} of {lines}")));

        /// Pages, counted off the artifact's own page objects. `/Type /Page` appears once per page and
        /// `/Type /Pages` once for the tree, so the negative lookahead is what keeps the tree out of
        /// the count. Read from the PDF rather than inferred from how much HTML went in.
        private static int PageCount(byte[] pdf) => System.Text.RegularExpressions.Regex
            .Matches(Encoding.Latin1.GetString(pdf), @"/Type\s*/Page(?![s])")
            .Count;

        /// Every FlateDecode stream in the document, inflated and concatenated.
        ///
        /// A PDF's drawn text is compressed, so it is invisible to a scan of the file's raw bytes.
        /// This inflates each `stream`/`endstream` region — skipping the two-byte zlib header, since
        /// DeflateStream expects raw deflate — and ignores any region that does not inflate, because
        /// images and font programs are streams too and are not meant to be read as text.
        private static string ContentText(byte[] pdf)
        {
            var raw = Encoding.Latin1.GetString(pdf);
            var text = new System.Text.StringBuilder();

            foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex
                .Matches(raw, @"stream\r?\n(.*?)\r?\nendstream",
                    System.Text.RegularExpressions.RegexOptions.Singleline))
            {
                var bytes = Encoding.Latin1.GetBytes(m.Groups[1].Value);
                if (bytes.Length < 3) continue;

                try
                {
                    using var source = new MemoryStream(bytes, 2, bytes.Length - 2);   // skip zlib header
                    using var inflate = new System.IO.Compression.DeflateStream(
                        source, System.IO.Compression.CompressionMode.Decompress);
                    using var decoded = new MemoryStream();
                    inflate.CopyTo(decoded);
                    text.Append(Encoding.Latin1.GetString(decoded.ToArray()));
                }
                catch
                {
                    // Not a deflate stream, or not one that decodes standalone. An image or a font
                    // program lands here; neither is text and neither is what this method is for.
                }
            }

            return text.ToString();
        }

        // A short document is one page. The control for the multi-page test below: without it, a
        // page-count assertion of "> 1" could be satisfied by a renderer that always emits many.
        [SkippableFact]
        public async Task A_short_document_is_a_single_page()
        {
            var (pdf, unavailable) = await TryConvertAsync(Document("One line."));
            Skip.If(pdf is null, $"No browser on this machine: {unavailable}");

            _out.WriteLine($"EVIDENCE shortPages={PageCount(pdf!)} bytes={pdf!.Length}");

            Assert.Equal(1, PageCount(pdf));
        }

        // THE PAGINATION PROPERTY. Content that overflows must actually break across pages rather than
        // being clipped at the bottom of page one — a silent clip is the failure mode that loses the
        // end of a register and looks like a shorter report.
        [SkippableFact]
        public async Task Overflowing_content_breaks_across_pages_instead_of_clipping()
        {
            var (pdf, unavailable) = await TryConvertAsync(LongDocument(300));
            Skip.If(pdf is null, $"No browser on this machine: {unavailable}");

            var pages = PageCount(pdf!);
            _out.WriteLine($"EVIDENCE longPages={pages} bytes={pdf!.Length}");

            Assert.True(pages > 1, $"300 lines produced {pages} page(s); content is being clipped, not paginated.");
        }

        // More content means more pages, monotonically. Catches a renderer that paginates once and
        // then stops, which a single "> 1" assertion cannot see.
        [SkippableFact]
        public async Task More_content_produces_more_pages()
        {
            var (small, unavailable) = await TryConvertAsync(LongDocument(120));
            Skip.If(small is null, $"No browser on this machine: {unavailable}");

            var (large, _) = await TryConvertAsync(LongDocument(600));
            Assert.NotNull(large);

            var fewer = PageCount(small!);
            var more = PageCount(large!);
            _out.WriteLine($"EVIDENCE pages120={fewer} pages600={more}");

            Assert.True(more > fewer, $"600 lines gave {more} pages, 120 gave {fewer}.");
        }

        // The running footer is the reporting platform's own decision — page numbers on paper — so it
        // must survive into the artifact when a layout asks for one.
        //
        // ASSERTED AS A DIFFERENCE, NOT BY READING THE WORDS, and the two failures that led here are
        // worth recording because each looked like a product defect and neither was:
        //
        //   1. Scanning the raw bytes for "CrossBuy" found nothing — drawn text lives in FlateDecode
        //      streams, so a raw scan cannot see it. The /BaseFont check elsewhere in this file works
        //      on raw bytes only because font names sit in an UNCOMPRESSED object dictionary, which is
        //      an easy and wrong thing to generalise from.
        //   2. Inflating those streams and searching again ALSO found nothing, with 116k characters
        //      decoded. The text is set in a SUBSET font, so the content stream holds glyph indices,
        //      not readable characters. The evidence line below prints whether ordinary body text is
        //      findable, so this stays a measured fact rather than my explanation of one.
        //
        // What is both observable and meaningful is that asking for a footer CHANGES the document:
        // identical content, one with DisplayHeaderFooter and one without, must not be the same
        // artifact. That is the property — the option reaches Chromium and something more is drawn.
        [SkippableFact]
        public async Task A_requested_footer_changes_the_rendered_document()
        {
            var html = LongDocument(200);

            var (plain, unavailable) = await TryConvertAsync(html, new ReportPdfOptions());
            Skip.If(plain is null, $"No browser on this machine: {unavailable}");

            var (footed, _) = await TryConvertAsync(html, new ReportPdfOptions
            {
                FooterTemplate = "<span style=\"font-size:8pt\">CrossBuy report footer</span>",
            });
            Assert.NotNull(footed);

            var plainText = ContentText(plain!);
            var footedText = ContentText(footed!);

            _out.WriteLine($"EVIDENCE plainBytes={plain!.Length} footedBytes={footed!.Length} " +
                           $"plainChars={plainText.Length} footedChars={footedText.Length} " +
                           $"bodyTextReadable={plainText.Contains("Line 1 of", StringComparison.Ordinal)}");

            // The footer draws on EVERY page, so it adds content wherever it is honoured.
            Assert.True(footedText.Length > plainText.Length,
                $"with a footer the drawn content was {footedText.Length} chars and without it " +
                $"{plainText.Length}; the FooterTemplate did not reach the renderer.");
        }

        // ---- document security --------------------------------------------------------------------

        // NOTHING EXECUTABLE REACHES THE ARTIFACT. A PDF can carry JavaScript, an auto-run OpenAction
        // and embedded file attachments; a report is a document to be read, and a reader opening one
        // from an email must not be running anything. Asserted on the artifact, not on the renderer's
        // intentions, because the HTML is caller-influenced via the visual layout.
        [SkippableFact]
        public async Task The_artifact_carries_no_script_no_autorun_and_no_attachment()
        {
            const string Hostile =
                "<script>window.x=1;</script>"
                + "<a href=\"javascript:alert(1)\">x</a>"
                + "<embed src=\"evil.pdf\">";

            var (pdf, unavailable) = await TryConvertAsync(Document(Hostile));
            Skip.If(pdf is null, $"No browser on this machine: {unavailable}");

            var raw = Encoding.Latin1.GetString(pdf!);
            var findings = new[] { "/JavaScript", "/JS", "/OpenAction", "/EmbeddedFile", "/Launch" }
                .Where(marker => raw.Contains(marker, StringComparison.Ordinal))
                .ToArray();

            _out.WriteLine($"EVIDENCE bytes={pdf!.Length} activeMarkers=[{string.Join(", ", findings)}]");

            Assert.Empty(findings);
        }

        // NO NETWORK, AND THE DOCUMENT STILL RENDERS. The converter uses SetContentAsync precisely so
        // there is no server to reach; a reference to an unreachable host must therefore neither hang
        // the export nor fail it. The host is a .invalid domain, which RFC 2606 guarantees never
        // resolves — so if this returns a PDF at all, nothing was fetched.
        [SkippableFact]
        public async Task An_unreachable_external_reference_neither_hangs_nor_fails_the_export()
        {
            const string External =
                "<img src=\"http://nothing.example.invalid/logo.png\" alt=\"logo\">"
                + "<link rel=\"stylesheet\" href=\"http://nothing.example.invalid/x.css\">";

            // THE BOUND WAS 30 SECONDS OF WALL CLOCK AND IT FAILED ON A BUSY MACHINE (2026-11-09) while
            // passing alone in 15s - a 2x headroom that several tabs building at once erases. Worse, the
            // message blamed the unreachable host for what was really contention: a false diagnosis wearing
            // the clothes of a finding, which is the most expensive kind of failing test.
            //
            // A CONTROL RENDER FIXES IT. The same document WITHOUT the external references, rendered on the
            // same machine moments apart, carries the same load. If nothing was fetched the two take
            // comparable time; a real wait on an unresolvable host cannot hide, because a DNS timeout is
            // seconds at minimum and shows up as the external render taking far longer than its control.
            // The absolute token survives only as a HANG STOP, now generous enough never to fire on load.
            using var bounded = new CancellationTokenSource(TimeSpan.FromMinutes(3));

            byte[]? pdf;
            TimeSpan controlTime, externalTime;
            try
            {
                var clock = System.Diagnostics.Stopwatch.StartNew();
                var control = await _converter.ConvertAsync(Document(""), new ReportPdfOptions(), bounded.Token);
                controlTime = clock.Elapsed;
                Assert.StartsWith("%PDF-", Encoding.ASCII.GetString(control, 0, 5));   // the control must itself be real

                clock.Restart();
                pdf = await _converter.ConvertAsync(Document(External), new ReportPdfOptions(), bounded.Token);
                externalTime = clock.Elapsed;
            }
            catch (Exception ex) when (!_converter.IsAvailable)
            {
                Skip.If(true, $"No browser on this machine: {_converter.UnavailableReason ?? ex.Message}");
                return;
            }

            _out.WriteLine($"EVIDENCE bytes={pdf.Length} control={controlTime.TotalSeconds:0.0}s external={externalTime.TotalSeconds:0.0}s cancelled={bounded.IsCancellationRequested}");

            Assert.False(bounded.IsCancellationRequested, "the export hung: a render did not finish inside three minutes.");
            Assert.StartsWith("%PDF-", Encoding.ASCII.GetString(pdf, 0, 5));

            // Deliberately loose. This discriminates a DNS timeout from render jitter; it is not a
            // performance budget, and tightening it would rebuild the flakiness it was written to remove.
            var budget = TimeSpan.FromSeconds(controlTime.TotalSeconds * 2 + 10);
            Assert.True(externalTime <= budget,
                $"the export appears to have waited on the unreachable host: {externalTime.TotalSeconds:0.0}s against a control render of {controlTime.TotalSeconds:0.0}s.");
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
