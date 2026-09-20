using System.Globalization;
using System.Text;
using CrossBuy.BL.Reporting;
using Xunit;

namespace CrossBuy.Tests
{
    // Reporting Platform (ADR-037) — RENDERERS, EXPORTERS and the PLUGGABILITY claim.
    //
    // The pluggability tests are the ones that matter architecturally: they prove a caller never names an engine,
    // and that substituting one is a registration change.
    public class ReportingOutputTests
    {
        private static readonly CultureInfo Arabic = CultureInfo.GetCultureInfo("ar-KW");
        private static readonly CultureInfo English = CultureInfo.GetCultureInfo("en-US");

        private static ReportRenderContext Context(ReportOutputFormat format, CultureInfo? culture = null,
            bool grouped = false, bool preview = false, bool truncated = false,
            IReadOnlyList<string>? visibleColumns = null)
        {
            var definition = TestReportDefinitions.Sales();
            var builder = new ReportDataSetBuilder(definition.Columns);
            foreach (var row in TestReportDefinitions.Rows()) builder.AddRow(row);

            var shaped = new ReportDataShaper().Shape(definition, builder.Build(truncated), new ReportShapeRequest
            {
                Groupings = grouped ? new[] { ReportGrouping.By("Branch") } : Array.Empty<ReportGrouping>(),
                VisibleColumns = visibleColumns ?? Array.Empty<string>(),
            }, culture ?? Arabic);

            return new ReportRenderContext
            {
                View = shaped.View!,
                Format = format,
                Culture = culture ?? Arabic,
                Title = "تقرير المبيعات",
                GeneratedAt = ReportingTestHost.FixedNow,
                IsPreview = preview,
                Parameters = new[]
                {
                    new ReportParameterDisplay { Label = "من", Value = "2026-05-01" },
                },
                Branding = new ReportBranding { CompanyName = "شركة الاختبار", CompanyNameEn = "Test Co" },
            };
        }

        // ================================================================================================
        // 1. PLUGGABILITY — the mandate
        // ================================================================================================

        [Fact]
        public void The_output_pipeline_routes_a_format_to_a_producer_without_the_caller_naming_one()
        {
            using var host = new ReportingTestHost();

            // Document formats go to a renderer, data formats to an exporter — and the CALLER only ever said
            // "Html" or "Csv".
            Assert.True(ReportFormats.IsDocument(ReportOutputFormat.Html));
            Assert.True(ReportFormats.IsDocument(ReportOutputFormat.Pdf));
            Assert.False(ReportFormats.IsDocument(ReportOutputFormat.Csv));
            Assert.False(ReportFormats.IsDocument(ReportOutputFormat.Xlsx));

            var formats = host.Output.AvailableFormats(TestReportDefinitions.Sales());

            Assert.Contains(ReportOutputFormat.Html, formats);
            Assert.Contains(ReportOutputFormat.PrintHtml, formats);
            Assert.Contains(ReportOutputFormat.Csv, formats);
            Assert.Contains(ReportOutputFormat.Xlsx, formats);

            // PDF is registered but its converter is unbound, so it is NOT offered. An unbound engine means no
            // button, not a button that errors.
            Assert.DoesNotContain(ReportOutputFormat.Pdf, formats);
        }

        [Fact]
        public void Registering_another_renderer_for_a_format_replaces_it_with_no_caller_change()
        {
            var html = new HtmlReportRenderer();
            var substitute = new FakeRenderer(ReportOutputFormat.Pdf, "Fake.Stimulsoft");

            // Last registration wins — this IS the substitution mechanism a future StimulsoftRenderer uses.
            var registry = new ReportRendererRegistry(new IReportRenderer[]
            {
                html,
                new PlaywrightPdfReportRenderer(html, new UnconfiguredHtmlToPdfConverter()),
                substitute,
            });

            Assert.Equal("Fake.Stimulsoft", registry.Resolve(ReportOutputFormat.Pdf).EngineName);

            // ...and now PDF IS available, purely because of a registration order change.
            Assert.Contains(ReportOutputFormat.Pdf, registry.AvailableFormats);
        }

        [Fact]
        public async Task An_unbound_pdf_engine_refuses_clearly_instead_of_producing_a_broken_file()
        {
            var html = new HtmlReportRenderer();
            var pdf = new PlaywrightPdfReportRenderer(html, new UnconfiguredHtmlToPdfConverter());

            Assert.False(pdf.IsAvailable);

            var ex = await Assert.ThrowsAsync<ReportRendererUnavailableException>(() =>
                pdf.RenderAsync(Context(ReportOutputFormat.Pdf)));

            Assert.Equal(ReportOutputFormat.Pdf, ex.Format);

            // The message must be ACTIONABLE. A zero-byte or HTML-masquerading-as-PDF artifact would be worse than
            // an error: it reaches a user's inbox looking like a delivered report.
            Assert.Contains("Playwright", ex.Message);
            Assert.Contains("PrintHtml", ex.Message);
        }

        [Fact]
        public void The_registry_reports_a_registered_but_unavailable_renderer_as_unavailable_before_any_work()
        {
            var html = new HtmlReportRenderer();
            var registry = new ReportRendererRegistry(new IReportRenderer[]
            {
                html, new PlaywrightPdfReportRenderer(html, new UnconfiguredHtmlToPdfConverter()),
            });

            Assert.Throws<ReportRendererUnavailableException>(() => registry.Resolve(ReportOutputFormat.Pdf));
            Assert.False(registry.TryResolve(ReportOutputFormat.Pdf, out _));
        }

        [Fact]
        public void A_format_nobody_claims_is_a_wiring_error_distinct_from_unavailability()
        {
            var registry = new ReportRendererRegistry(Array.Empty<IReportRenderer>());

            // NotRegistered vs Unavailable: the fix is different (write code vs configure the environment), so the
            // exceptions are different.
            Assert.Throws<ReportRendererNotRegisteredException>(() => registry.Resolve(ReportOutputFormat.Html));
        }

        // ================================================================================================
        // 2. HTML RENDERER
        // ================================================================================================

        [Fact]
        public async Task An_html_PREVIEW_is_a_fragment_and_everything_meant_to_leave_the_screen_is_a_document()
        {
            // THE LINE IS "DOES THIS LEAVE THE SCREEN", NOT "IS THIS PrintHtml".
            //
            // A preview is injected into the Studio page, which has already declared its charset and its
            // direction; a full document there would nest <html> inside <html>. An EXPORT is a file
            // somebody opens on its own, and as a fragment it carried neither - so an Arabic report
            // downloaded and double-clicked opened as mojibake, laid out left-to-right. Same format,
            // opposite requirement, and the format alone could not tell them apart.
            var renderer = new HtmlReportRenderer();

            var preview  = (await renderer.RenderAsync(Context(ReportOutputFormat.Html, preview: true))).AsText();
            var export   = (await renderer.RenderAsync(Context(ReportOutputFormat.Html))).AsText();
            var document = (await renderer.RenderAsync(Context(ReportOutputFormat.PrintHtml))).AsText();

            Assert.DoesNotContain("<!DOCTYPE html>", preview);
            Assert.Contains("<!DOCTYPE html>", export);
            Assert.Contains("<!DOCTYPE html>", document);

            // The two things a file opened on its own has nobody else to supply.
            foreach (var standalone in new[] { export, document })
            {
                Assert.Contains("charset", standalone);
                Assert.Contains("dir=\"rtl\"", standalone);   // the context is Arabic
            }

            // @page carries the geometry so the browser paginates identically whether a user prints it or the PDF
            // converter rasterises it.
            Assert.Contains("@page", document);
            Assert.Contains("display:table-header-group", document);   // thead repeats per page

            // Self-contained: no external stylesheet, font or script. A headless browser may have no route back.
            foreach (var html in new[] { preview, export, document })
            {
                Assert.Contains("<style>", html);
                Assert.DoesNotContain("<link", html);
                Assert.DoesNotContain("<script", html);
                Assert.DoesNotContain("http://", html);
            }
        }

        [Fact]
        public async Task Every_interpolated_value_is_html_escaped()
        {
            var definition = TestReportDefinitions.Sales();
            var builder = new ReportDataSetBuilder(definition.Columns);
            builder.AddRow(new Dictionary<string, object?>
            {
                ["Branch"] = "<script>alert(1)</script>",
                ["Item"] = "\"quoted\" & <b>bold</b>",
                ["Amount"] = 1m,
            });

            var shaped = new ReportDataShaper().Shape(definition, builder.Build(), new ReportShapeRequest(), Arabic);

            var context = new ReportRenderContext
            {
                View = shaped.View!,
                Format = ReportOutputFormat.Html,
                Culture = Arabic,
                Title = "<img src=x onerror=alert(1)>",
                GeneratedAt = ReportingTestHost.FixedNow,
                Branding = new ReportBranding { CompanyName = "<b>Co</b>" },
            };

            var html = (await new HtmlReportRenderer().RenderAsync(context)).AsText();

            // Report data IS business data. A customer named `<script>` must render as text — "it's only a report"
            // is precisely how stored XSS ships.
            //
            // The assertion is on the ANGLE BRACKETS, not on the payload text: HTML-encoding escapes < > & " ',
            // so the literal string "onerror=alert(1)" survives as inert text. What matters is that no TAG can
            // form — `<img` and `<script` must never appear unescaped, because without a tag there is no
            // attribute to fire and no script to run.
            Assert.DoesNotContain("<script", html.Replace("<style>", "").Replace("</style>", ""));
            Assert.DoesNotContain("<img", html);
            Assert.Contains("&lt;script&gt;", html);
            Assert.Contains("&lt;img", html);
            Assert.Contains("&quot;quoted&quot;", html);
        }

        [Fact]
        public async Task A_preview_and_a_truncated_result_both_say_so_on_the_page()
        {
            var renderer = new HtmlReportRenderer();

            var preview = (await renderer.RenderAsync(Context(ReportOutputFormat.Html, preview: true))).AsText();
            var truncated = (await renderer.RenderAsync(Context(ReportOutputFormat.Html, truncated: true))).AsText();

            // Rendered, not logged: the person holding the paper is the one who needs to know it is provisional or
            // incomplete.
            // cbrep-banner-* became cbrep-notice-* when the notices were given the house shape and drawn
            // from one definition. The class is the contract a stylesheet binds to, so the test follows it.
            Assert.Contains("cbrep-notice-preview", preview);
            Assert.Contains("معاينة", preview);
            Assert.Contains("cbrep-notice-truncated", truncated);
        }

        [Fact]
        public async Task Direction_and_language_follow_the_culture_with_one_stylesheet()
        {
            var renderer = new HtmlReportRenderer();

            var ar = (await renderer.RenderAsync(Context(ReportOutputFormat.PrintHtml, Arabic))).AsText();
            var en = (await renderer.RenderAsync(Context(ReportOutputFormat.PrintHtml, English))).AsText();

            Assert.Contains("dir=\"rtl\"", ar);
            Assert.Contains("lang=\"ar\"", ar);
            Assert.Contains("lang=\"en\"", en);

            // Logical properties, so ONE stylesheet is correct in both directions — duplicating it per direction is
            // how RTL parity rots.
            Assert.Contains("text-align:start", ar);
            Assert.DoesNotContain("text-align:left", ar);

            // Column titles come from the culture's language.
            Assert.Contains("الفرع", ar);
            Assert.Contains("Branch", en);
        }

        [Fact]
        public async Task Group_bands_subtotals_and_a_grand_total_are_rendered()
        {
            var html = (await new HtmlReportRenderer()
                .RenderAsync(Context(ReportOutputFormat.Html, grouped: true))).AsText();

            Assert.Contains("cbrep-group", html);
            Assert.Contains("cbrep-subtotal", html);
            Assert.Contains("cbrep-grand", html);
            Assert.Contains("North", html);
        }

        [Fact]
        public async Task An_internal_column_never_reaches_the_rendered_output_even_if_a_layout_names_it()
        {
            var html = (await new HtmlReportRenderer().RenderAsync(
                Context(ReportOutputFormat.Html, visibleColumns: new[] { "Branch", "Amount", "Cost" }))).AsText();

            // The Cost values (18, 30, 9 …) must not be on the page.
            Assert.DoesNotContain("التكلفة", html);
            Assert.DoesNotContain("Cost", html);
        }

        // ================================================================================================
        // 3. CSV EXPORT
        // ================================================================================================

        [Fact]
        public async Task Csv_starts_with_a_utf8_bom_so_excel_opens_arabic_correctly()
        {
            var artifact = await new CsvReportExporter().ExportAsync(Context(ReportOutputFormat.Csv));

            // Without the BOM, Excel opens the file in the system ANSI codepage and every Arabic label becomes
            // mojibake. It is the only thing that makes an Arabic CSV open correctly by double-click.
            Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, artifact.Content.Take(3).ToArray());
            Assert.Contains("الفرع", Encoding.UTF8.GetString(artifact.Content));
        }

        [Theory]
        [InlineData("=1+1")]
        [InlineData("+cmd")]
        [InlineData("-2+3")]
        [InlineData("@SUM(A1)")]
        public async Task Csv_neutralises_formula_injection(string dangerous)
        {
            var definition = TestReportDefinitions.Sales();
            var builder = new ReportDataSetBuilder(definition.Columns);
            builder.AddRow(new Dictionary<string, object?> { ["Branch"] = dangerous, ["Amount"] = 1m });

            var shaped = new ReportDataShaper().Shape(definition, builder.Build(), new ReportShapeRequest(), English);

            var artifact = await new CsvReportExporter().ExportAsync(new ReportRenderContext
            {
                View = shaped.View!,
                Format = ReportOutputFormat.Csv,
                Culture = English,
                Title = "t",
                GeneratedAt = ReportingTestHost.FixedNow,
            });

            var text = Encoding.UTF8.GetString(artifact.Content);

            // A customer named `=cmd|'/c calc'!A1` would otherwise become an executable formula in whoever opens
            // the export — the reporting equivalent of stored XSS, and exports are its classic delivery vector.
            Assert.Contains("'" + dangerous, text);
        }

        [Fact]
        public async Task Csv_writes_machine_numbers_and_iso_dates_not_display_strings()
        {
            var artifact = await new CsvReportExporter().ExportAsync(Context(ReportOutputFormat.Csv, English));
            var text = Encoding.UTF8.GetString(artifact.Content);

            // "1,234.56" in a spreadsheet cell is TEXT, and a user who sums the column gets zero. This is the
            // single most common reporting export defect.
            Assert.Contains("25.5", text);
            Assert.DoesNotContain("\"25.50\"", text);
            Assert.Contains("2026-05-02", text);
        }

        [Fact]
        public async Task Csv_quotes_and_escapes_per_rfc4180()
        {
            var definition = TestReportDefinitions.Sales();
            var builder = new ReportDataSetBuilder(definition.Columns);
            builder.AddRow(new Dictionary<string, object?>
            {
                ["Branch"] = "has, comma", ["Item"] = "has \"quote\"", ["Amount"] = 1m,
            });

            var shaped = new ReportDataShaper().Shape(definition, builder.Build(), new ReportShapeRequest(), English);

            var artifact = await new CsvReportExporter().ExportAsync(new ReportRenderContext
            {
                View = shaped.View!, Format = ReportOutputFormat.Csv, Culture = English,
                Title = "t", GeneratedAt = ReportingTestHost.FixedNow,
            });

            var text = Encoding.UTF8.GetString(artifact.Content);
            Assert.Contains("\"has, comma\"", text);
            Assert.Contains("\"has \"\"quote\"\"\"", text);
        }

        [Fact]
        public async Task Csv_is_flat_and_carries_a_totals_row_but_no_group_bands()
        {
            var artifact = await new CsvReportExporter()
                .ExportAsync(Context(ReportOutputFormat.Csv, English, grouped: true));

            var lines = Encoding.UTF8.GetString(artifact.Content)
                .Split('\n', StringSplitOptions.RemoveEmptyEntries);

            // header + 6 details + 1 totals. Interleaving group bands would break every pivot table built over it;
            // a totals row is one row and does not interleave.
            Assert.Equal(8, lines.Length);
            Assert.Contains("Total", lines[^1]);
        }

        // ================================================================================================
        // 4. XLSX EXPORT
        // ================================================================================================

        [Fact]
        public async Task Xlsx_produces_a_real_workbook()
        {
            var artifact = await new ExcelReportExporter().ExportAsync(Context(ReportOutputFormat.Xlsx));

            Assert.Equal(ReportFormats.ContentType(ReportOutputFormat.Xlsx), artifact.ContentType);
            Assert.EndsWith(".xlsx", artifact.FileName);

            // "PK" — a valid OOXML package is a zip. Cheap, and it catches "we wrote HTML with an .xlsx name".
            Assert.Equal((byte)'P', artifact.Content[0]);
            Assert.Equal((byte)'K', artifact.Content[1]);
            Assert.True(artifact.Content.Length > 1000);
        }

        // ================================================================================================
        // 5. ARTIFACT IDENTITY
        // ================================================================================================

        [Fact]
        public async Task An_artifact_is_content_addressed_and_reproducible()
        {
            var renderer = new HtmlReportRenderer();

            var first = await renderer.RenderAsync(Context(ReportOutputFormat.Html));
            var second = await renderer.RenderAsync(Context(ReportOutputFormat.Html));

            // Identical inputs → identical bytes → identical hash. This is what lets the archive be
            // content-addressed and lets a caller prove two downloads are the same document.
            Assert.Equal(first.ContentHash, second.ContentHash);
            Assert.Equal(64, first.ContentHash.Length);
            Assert.True(first.IsInline);
        }

        [Fact]
        public void A_file_name_is_ascii_safe_because_it_travels_through_headers_and_paths()
        {
            var name = ReportFileName.For(TestReportDefinitions.Sales(), ReportOutputFormat.Pdf,
                ReportingTestHost.FixedNow);

            // Built from the CODE, not the Arabic title: a raw Arabic filename works in one of
            // {Content-Disposition, filesystem path, email attachment} and fails in the other two.
            Assert.Equal("Test_Sales_20260514-103000.pdf", name);
            Assert.All(name, ch => Assert.True(ch < 128));
        }

        // ------------------------------------------------------------------------------------------------
        private sealed class FakeRenderer : IReportRenderer
        {
            public FakeRenderer(ReportOutputFormat format, string name)
            {
                Formats = new[] { format };
                EngineName = name;
            }

            public string EngineName { get; }
            public IReadOnlyList<ReportOutputFormat> Formats { get; }
            public bool IsAvailable => true;
            public string? UnavailableReason => null;

            public Task<ReportArtifact> RenderAsync(ReportRenderContext context,
                CancellationToken cancellationToken = default) =>
                Task.FromResult(ReportArtifact.FromText("fake.pdf", ReportOutputFormat.Pdf, "fake"));
        }
    }
}
