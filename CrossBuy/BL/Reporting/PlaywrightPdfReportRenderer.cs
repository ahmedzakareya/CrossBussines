using System.Globalization;
using System.Net;

namespace CrossBuy.BL.Reporting
{
    // ============================================================================================
    // Reporting Platform (ADR-037) — THE PDF RENDERER (Playwright profile).
    //
    // The renderer owns everything that is a REPORTING decision: page geometry, margins, RTL, the running
    // header/footer with page numbers, print CSS, and reusing the exact PrintHtml the print path uses. The
    // headless-browser call itself — the only part that needs a browser binary — sits behind
    // IHtmlToPdfConverter.
    //
    // WHY THE SEAM EXISTS (stated plainly, because it is a real limitation of this slice):
    // `Microsoft.Playwright` is NOT referenced by CrossBuy.csproj, and this slice does not add it. The csproj is
    // a file the parallel team is also changing, and a Playwright reference is not additive — it needs a NuGet
    // package plus a `playwright install` browser download in every environment that renders a PDF. Adding that
    // as a side effect of an architecture slice would make an architecture review into a deployment change.
    //
    // So: the PDF path is ARCHITECTURALLY COMPLETE and FUNCTIONALLY UNBOUND. Requesting Pdf today produces a
    // clean ReportRendererUnavailableException naming what is missing — never a corrupt file, never a silent
    // fallback to HTML. Binding it later is one class and one DI line, and NO caller changes. The concrete
    // binding is written out in the comment on IHtmlToPdfConverter below.
    // ============================================================================================
    public class PlaywrightPdfReportRenderer : IReportRenderer
    {
        private readonly HtmlReportRenderer _html;
        private readonly IHtmlToPdfConverter _converter;

        public PlaywrightPdfReportRenderer(HtmlReportRenderer html, IHtmlToPdfConverter converter)
        {
            _html = html;
            _converter = converter;
        }

        public string EngineName => $"CrossBusiness.Pdf({_converter.EngineName})";

        public IReadOnlyList<ReportOutputFormat> Formats { get; } = new[] { ReportOutputFormat.Pdf };

        // Delegated, not assumed. The registry checks this BEFORE calling RenderAsync, so an unbound converter
        // is reported as "unavailable" rather than throwing from inside a conversion.
        public bool IsAvailable => _converter.IsAvailable;

        // Propagated verbatim from the converter, so the registry's exception names the actual missing piece
        // rather than "PDF is unavailable".
        public string? UnavailableReason => _converter.UnavailableReason;

        public async Task<ReportArtifact> RenderAsync(ReportRenderContext context,
            CancellationToken cancellationToken = default)
        {
            if (!_converter.IsAvailable)
                throw new ReportRendererUnavailableException(ReportOutputFormat.Pdf, _converter.UnavailableReason
                    ?? "no HTML-to-PDF converter is bound in this deployment.");

            // The PDF converts the PrintHtml document — not a second layout built for PDF. One layout means the
            // preview, the printout and the PDF cannot disagree, which is the defect this design exists to avoid.
            var printContext = new ReportRenderContext
            {
                View = context.View,
                Format = ReportOutputFormat.PrintHtml,
                PageSetup = context.PageSetup,
                Culture = context.Culture,
                Branding = context.Branding,
                Title = context.Title,
                Subtitle = context.Subtitle,
                Parameters = context.Parameters,
                GeneratedAt = context.GeneratedAt,
                GeneratedBy = context.GeneratedBy,
                IsPreview = context.IsPreview,
            };

            var html = _html.BuildPrintDocument(printContext);
            var options = BuildOptions(context);

            var bytes = await _converter.ConvertAsync(html, options, cancellationToken);

            var fileName = ReportFileName.For(context.View.Definition, ReportOutputFormat.Pdf, context.GeneratedAt);
            return ReportArtifact.FromBytes(fileName, ReportOutputFormat.Pdf, bytes);
        }

        // Page geometry is passed to the converter AS WELL AS being in the document's @page rule. Belt and
        // braces on purpose: @page is authoritative in a real browser, but a converter that ignores it (or a
        // future non-browser engine) must still receive the size and margins explicitly.
        private ReportPdfOptions BuildOptions(ReportRenderContext context)
        {
            var setup = context.PageSetup;
            return new ReportPdfOptions
            {
                PageSize = setup.PageSize,
                Landscape = setup.Orientation == ReportOrientation.Landscape,
                MarginTopMm = setup.MarginTopMm,
                MarginBottomMm = setup.MarginBottomMm,
                MarginLeftMm = setup.MarginLeftMm,
                MarginRightMm = setup.MarginRightMm,

                // Without this the brand-green table header prints as white-on-white: headless browsers omit
                // background graphics by default.
                PrintBackground = true,

                HeaderTemplate = null,
                FooterTemplate = setup.ShowPageNumbers ? BuildFooterTemplate(context) : null,
                Rtl = setup.Rtl,
                DocumentTitle = context.Title,
            };
        }

        // The running footer. Kept as an HTML template rather than drawn into the body because a body element
        // cannot know the page number — only the paginating engine can.
        //
        // `.pageNumber` / `.totalPages` are the placeholder classes the Chromium print pipeline substitutes
        // (Playwright, Puppeteer and Chrome's own print-to-PDF all honour them), so this template is portable
        // across every Chromium-based binding rather than specific to Playwright.
        private static string BuildFooterTemplate(ReportRenderContext context)
        {
            var pageWord = context.IsArabic ? "صفحة" : "Page";
            var ofWord = context.IsArabic ? "من" : "of";
            var dir = context.Rtl ? "rtl" : "ltr";
            var code = WebUtility.HtmlEncode(context.View.Definition.Code);

            return $"<div dir=\"{dir}\" style=\"width:100%;font-size:7pt;color:#7e8299;" +
                   "font-family:'Segoe UI',Tahoma,Arial,sans-serif;padding:0 12mm;display:flex;" +
                   "justify-content:space-between;\">" +
                   $"<span>{code}</span>" +
                   $"<span>{pageWord} <span class=\"pageNumber\"></span> {ofWord} " +
                   "<span class=\"totalPages\"></span></span></div>";
        }
    }

    // Options handed to the conversion engine. Millimetres, because that is what the page model uses and what
    // print shops speak; the converter converts to whatever its own API wants.
    public sealed class ReportPdfOptions
    {
        public ReportPageSize PageSize { get; init; } = ReportPageSize.A4;
        public bool Landscape { get; init; }
        public double MarginTopMm { get; init; } = 18;
        public double MarginBottomMm { get; init; } = 16;
        public double MarginLeftMm { get; init; } = 12;
        public double MarginRightMm { get; init; } = 12;
        public bool PrintBackground { get; init; } = true;
        public string? HeaderTemplate { get; init; }
        public string? FooterTemplate { get; init; }
        public bool Rtl { get; init; } = true;
        public string? DocumentTitle { get; init; }

        // Chromium's paper-size vocabulary. Thermal80 has no named paper, so it is expressed as an explicit
        // width with an auto height — see WidthMm/HeightMm below.
        public string? PaperName => PageSize switch
        {
            ReportPageSize.A4 => "A4",
            ReportPageSize.A5 => "A5",
            ReportPageSize.Letter => "Letter",
            ReportPageSize.Legal => "Legal",
            _ => null,
        };

        public double? WidthMm => PageSize == ReportPageSize.Thermal80 ? 80 : null;

        // null = let the engine grow the page to fit the content (continuous roll).
        public double? HeightMm => null;

        public string MarginCss(CultureInfo? culture = null)
        {
            var c = culture ?? CultureInfo.InvariantCulture;
            return $"{MarginTopMm.ToString(c)}mm {MarginRightMm.ToString(c)}mm " +
                   $"{MarginBottomMm.ToString(c)}mm {MarginLeftMm.ToString(c)}mm";
        }
    }

    // ============================================================================================
    // THE BROWSER SEAM.
    //
    // Implementing this against Playwright is the whole of the remaining work, and it is deliberately small:
    //
    //     public sealed class PlaywrightHtmlToPdfConverter : IHtmlToPdfConverter, IAsyncDisposable
    //     {
    //         public string EngineName => "Playwright.Chromium";
    //         public bool IsAvailable => true;                 // after `playwright install chromium`
    //         public string? UnavailableReason => null;
    //
    //         public async Task<byte[]> ConvertAsync(string html, ReportPdfOptions o, CancellationToken ct)
    //         {
    //             var pw      = await Playwright.CreateAsync();
    //             await using var browser = await pw.Chromium.LaunchAsync();
    //             var page    = await browser.NewPageAsync();
    //             await page.SetContentAsync(html, new() { WaitUntil = WaitUntilState.Load });
    //             return await page.PdfAsync(new PagePdfOptions {
    //                 Format          = o.PaperName,                    // null for Thermal80
    //                 Width           = o.WidthMm is { } w ? $"{w}mm" : null,
    //                 Landscape       = o.Landscape,
    //                 PrintBackground = o.PrintBackground,
    //                 DisplayHeaderFooter = o.FooterTemplate != null,
    //                 FooterTemplate  = o.FooterTemplate,
    //                 HeaderTemplate  = o.HeaderTemplate ?? "<span></span>",
    //                 Margin = new() { Top = $"{o.MarginTopMm}mm", Bottom = $"{o.MarginBottomMm}mm",
    //                                  Left = $"{o.MarginLeftMm}mm", Right = $"{o.MarginRightMm}mm" },
    //             });
    //         }
    //     }
    //
    // Two operational notes for whoever binds it, both learned the hard way by everyone who has shipped this:
    //   * a browser launch per PDF is ~300–800 ms. A real deployment keeps ONE browser and opens a page per
    //     conversion, which makes the converter a singleton holding an IAsyncDisposable — a lifetime decision
    //     that belongs to whoever adds the package, not to this slice.
    //   * `playwright install` must run in the container image. A missing browser must surface through
    //     IsAvailable/UnavailableReason, not as an exception on a user's click.
    // ============================================================================================
    public interface IHtmlToPdfConverter
    {
        string EngineName { get; }

        // false = this deployment cannot produce a PDF. The registry turns that into a clear exception before
        // any work is done.
        bool IsAvailable { get; }

        // Operator-facing explanation shown when IsAvailable is false. Required to be specific: "no converter
        // bound" is actionable, "PDF failed" is not.
        string? UnavailableReason { get; }

        Task<byte[]> ConvertAsync(string html, ReportPdfOptions options,
            CancellationToken cancellationToken = default);
    }

    // The registered default: honest unavailability.
    //
    // This is NOT a null-object that quietly returns an empty file. A zero-byte or HTML-masquerading-as-PDF
    // artifact is worse than an error — it reaches a user's inbox and looks like a delivered report. So it
    // refuses, and it says exactly why and what to do.
    public sealed class UnconfiguredHtmlToPdfConverter : IHtmlToPdfConverter
    {
        public string EngineName => "none";
        public bool IsAvailable => false;

        public string? UnavailableReason =>
            "No HTML-to-PDF converter is bound. PDF rendering needs a headless-browser binding: add a " +
            "Microsoft.Playwright package reference, implement IHtmlToPdfConverter over it (see the worked " +
            "example in PlaywrightPdfReportRenderer.cs), register it in place of UnconfiguredHtmlToPdfConverter, " +
            "and run `playwright install chromium` in the deployment image. Until then, use PrintHtml — the " +
            "browser's own print dialog produces the same document from the same HTML.";

        public Task<byte[]> ConvertAsync(string html, ReportPdfOptions options,
            CancellationToken cancellationToken = default) =>
            throw new ReportRendererUnavailableException(ReportOutputFormat.Pdf, UnavailableReason!);
    }
}