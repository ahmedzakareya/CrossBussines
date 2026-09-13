using System.Globalization;

namespace CrossBuy.BL.Reporting
{
    // ============================================================================================
    // Reporting Platform (ADR-037) — THE RENDERER ABSTRACTION.
    //
    // This is the pluggability mandate in code:
    //
    //      IReportRenderer
    //        ├── HtmlReportRenderer          (Html, PrintHtml)
    //        ├── PlaywrightPdfReportRenderer (Pdf)
    //        └── future StimulsoftRenderer   (Pdf, Xlsx, …) — no caller changes
    //
    // A renderer receives a ReportView — already filtered, sorted, grouped and totalled — plus presentation
    // context, and returns bytes. It cannot see a ReportRequest, a parameter dictionary, a DbContext, a data
    // source or a permission. That containment is what makes swapping engines a registration change rather than
    // a refactor: there is nothing else for a renderer to depend on.
    //
    // A renderer may NOT: read the database, decide authorization, recompute a number, or re-round money. It
    // formats what the shaper produced. (See ReportValues.Format — display never changes a value.)
    // ============================================================================================

    // Visual identity for rendered output.
    //
    // Defaults are the product's ledger green + gold. CLAUDE.md is explicit that the brand is green #0E4A9E and
    // gold, NOT blue, and that crossbuy-brand.css overrides Metronic's blue on purpose — so the reporting
    // defaults match the ledger, and the open A6.2 "preserve the blue identity" question is NOT silently
    // resolved here in either direction. Whoever owns that decision changes these two constants.
    public sealed class ReportBranding
    {
        public const string BrandGreen = "#0E4A9E";
        public const string BrandGold = "#b28b3c";

        public string? CompanyName { get; init; }
        public string? CompanyNameEn { get; init; }

        // Embedded image (data: URI) rather than a URL. A PDF renderer runs in a headless browser that may have
        // no route back to the application, and a report whose logo silently disappears in PDF is a support call.
        public string? LogoDataUri { get; init; }

        public string PrimaryColor { get; init; } = BrandGreen;
        public string AccentColor { get; init; } = BrandGold;

        // ---- SEMANTIC TONES, TAKEN FROM THE BRAND LAYER'S OWN VALUES ------------------------------
        //
        // A report is self-contained by rule — inline <style>, no external stylesheet — so it cannot read
        // crossbuy-brand.css the way a screen does. That is a reason to COPY the token values, not a
        // licence to invent them, and the banners had invented them: #fff8dd, #7a5c14, #f1416c, hexes
        // belonging to stock Metronic, which the brand layer overrides. The report was wearing the
        // theme's colours while every screen wore the product's.
        //
        // These are crossbuy-brand.css's values verbatim (--bs-warning*, --bs-danger*). The ratios that
        // file measures therefore still hold here; changing one of these without re-measuring silently
        // invalidates the argument recorded beside it there.
        //
        // SEMANTIC, NOT BRANDED, deliberately: a truncation notice says data is MISSING, and painting it
        // in the identity colour would make it read as decoration and be skipped.
        public string WarningColor { get; init; } = "#F59E0B";
        public string WarningSurface { get; init; } = "#FEF3C7";
        public string WarningBorder { get; init; } = "#FCD34D";
        public string WarningText { get; init; } = "#92400E";

        // WARNING KEEPS A DARK LABEL and every other family takes white. The brand layer states it —
        // `--bs-warning-inverse: #071437` — because amber cannot carry small white text. A white icon on
        // this tile would be the one thing on the page failing the standard the file exists to enforce.
        public string WarningInverse { get; init; } = "#071437";

        public string DangerColor { get; init; } = "#EF4444";
        public string DangerSurface { get; init; } = "#FEE2E2";
        public string DangerBorder { get; init; } = "#FCA5A5";
        public string DangerText { get; init; } = "#991B1B";
        public string DangerInverse { get; init; } = "#ffffff";

        // Free footer line (registration number, address). Rendered as text, escaped.
        public string? FooterNote { get; init; }

        public static ReportBranding Default { get; } = new();

        public string? Name(bool arabic) => arabic ? CompanyName : (CompanyNameEn ?? CompanyName);
    }

    // One parameter as it should appear in the report header ("Period: 2026-01-01 → 2026-01-31").
    //
    // Printed on every document on purpose: a report without its parameters on the page is unusable as evidence
    // three months later, and every reporting system that omits them grows a "which period is this?" problem.
    public sealed class ReportParameterDisplay
    {
        public required string Label { get; init; }
        public required string Value { get; init; }
    }

    // Everything a renderer is given.
    public sealed class ReportRenderContext
    {
        public required ReportView View { get; init; }
        public required ReportOutputFormat Format { get; init; }
        public ReportPageSetup PageSetup { get; init; } = ReportPageSetup.Default;
        public CultureInfo Culture { get; init; } = CultureInfo.CurrentUICulture;
        public ReportBranding Branding { get; init; } = ReportBranding.Default;

        // Resolved title (template override, else the definition's title in the culture's language).
        public required string Title { get; init; }
        public string? Subtitle { get; init; }

        public IReadOnlyList<ReportParameterDisplay> Parameters { get; init; } =
            Array.Empty<ReportParameterDisplay>();

        public DateTime GeneratedAt { get; init; }
        public string? GeneratedBy { get; init; }

        // true = a capped preview. Renderers mark it visibly; a preview must never be mistakable for the
        // finished document.
        public bool IsPreview { get; init; }

        // ---- REPORT STUDIO V2 ----------------------------------------------------------------------
        //
        // The positioned document design, when the resolved template has one. NULL is the normal case and means
        // "render the column table you always rendered", which is why every existing report is unaffected.
        //
        // It rides on the RENDER CONTEXT rather than on a separate pipeline because §12 requires the designer,
        // the print view and the PDF to consume ONE definition: the PDF renderer converts the print HTML, the
        // print HTML comes from the same builder as the preview, and all three read this property. A visual
        // report that printed differently from its preview is therefore not expressible.
        public ReportVisualLayout? Visual { get; init; }

        // assetId → data URI, resolved by the engine through IReportAssetService (company-scoped). Inlined so a
        // headless browser converting to PDF needs no route back to the application — the same self-containment
        // rule the HTML renderer already follows for styles.
        public IReadOnlyDictionary<int, string> Assets { get; init; } = new Dictionary<int, string>();

        // ROLE → data URI, for the pictures that are facts about the TENANT rather than choices made in a
        // template: the company's logo and the branch's. Resolved by the engine through
        // IReportOrgImageProvider. An Image element with an asset id still wins — an author who picked a
        // picture meant that picture — and a role with nothing behind it stays the empty box it was.
        public IReadOnlyDictionary<ReportImageRole, string> RoleImages { get; init; } =
            new Dictionary<ReportImageRole, string>();

        public bool IsArabic => Culture.TwoLetterISOLanguageName == "ar";

        // DIRECTION IS A PROPERTY OF THE LANGUAGE. This read PageSetup.Rtl — a flag stored with the
        // template and defaulting to true — so every report printed right-to-left whatever language
        // it was rendered in, and an English document came out mirrored. The stored flag stays in the
        // page setup as the DESIGN direction; what reaches paper follows the culture, and one saved
        // layout mirrors itself because positions are logical (inset-inline-start), not left/right.
        public bool Rtl => IsArabic;

        public string ColumnTitle(ReportColumn column) => IsArabic ? column.TitleAr : column.TitleEn;
    }

    public interface IReportRenderer
    {
        // Human name for diagnostics and for the run log ("which engine produced this PDF").
        string EngineName { get; }

        // The formats this renderer claims. A renderer may claim several (HtmlReportRenderer claims Html and
        // PrintHtml, which share all their layout logic and differ only in page chrome).
        IReadOnlyList<ReportOutputFormat> Formats { get; }

        // false = registered but unusable in this deployment (e.g. a PDF engine with no browser bound). Checked
        // by the registry BEFORE a render is attempted, so the caller gets a clear
        // ReportRendererUnavailableException instead of a stack trace from inside a converter.
        bool IsAvailable { get; }

        // Operator-facing reason when IsAvailable is false; null when available.
        //
        // The registry propagates this verbatim, because "PDF is unavailable" is not actionable and "add a
        // Playwright reference and run `playwright install chromium`" is. A renderer that reported itself
        // unavailable without saying why would turn a configuration problem into a support ticket.
        string? UnavailableReason { get; }

        Task<ReportArtifact> RenderAsync(ReportRenderContext context, CancellationToken cancellationToken = default);
    }

    public interface IReportRendererRegistry
    {
        // Throws ReportRendererNotRegisteredException when nothing claims the format, and
        // ReportRendererUnavailableException when the claiming renderer cannot run here.
        IReportRenderer Resolve(ReportOutputFormat format);

        bool TryResolve(ReportOutputFormat format, out IReportRenderer? renderer);

        // Formats with a registered AND available renderer. Drives "which download buttons to show" without a
        // caller hard-coding a format list.
        IReadOnlyList<ReportOutputFormat> AvailableFormats { get; }
    }

    public class ReportRendererRegistry : IReportRendererRegistry
    {
        private readonly Dictionary<ReportOutputFormat, IReportRenderer> _byFormat = new();

        public ReportRendererRegistry(IEnumerable<IReportRenderer> renderers)
        {
            // LAST registration wins, deliberately — and this is the substitution mechanism. Registering a
            // StimulsoftRenderer for Pdf after the Playwright one replaces PDF generation for the whole product
            // with a single DI line and no caller change. It is the one place in this platform where "last wins"
            // is a feature rather than a hazard, so it is stated here rather than discovered.
            foreach (var renderer in renderers)
                foreach (var format in renderer.Formats)
                    _byFormat[format] = renderer;
        }

        public IReadOnlyList<ReportOutputFormat> AvailableFormats => _byFormat
            .Where(kv => kv.Value.IsAvailable)
            .Select(kv => kv.Key)
            .OrderBy(f => (int)f)
            .ToList();

        public IReportRenderer Resolve(ReportOutputFormat format)
        {
            if (!_byFormat.TryGetValue(format, out var renderer))
                throw new ReportRendererNotRegisteredException(format);

            if (!renderer.IsAvailable)
                throw new ReportRendererUnavailableException(format,
                    renderer.UnavailableReason
                    ?? $"renderer '{renderer.EngineName}' reports itself unavailable in this deployment.");

            return renderer;
        }

        public bool TryResolve(ReportOutputFormat format, out IReportRenderer? renderer)
        {
            renderer = null;
            if (!_byFormat.TryGetValue(format, out var found)) return false;
            if (!found.IsAvailable) return false;
            renderer = found;
            return true;
        }
    }
}