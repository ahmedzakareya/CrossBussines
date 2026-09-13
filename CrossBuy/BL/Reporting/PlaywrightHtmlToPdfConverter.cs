using Microsoft.Playwright;

namespace CrossBuy.BL.Reporting
{
    // ============================================================================================
    // THE BROWSER SEAM, BOUND.
    //
    // PlaywrightPdfReportRenderer has always been registered; its converter was UnconfiguredHtmlToPdfConverter,
    // so every PDF request answered "engine unavailable" with a reason instead of a file. This binds the real
    // one, and it is deliberately the implementation that file already documented rather than a new design.
    //
    // WHY THIS IS NOT A SECOND RENDER PATH: it converts the HTML the visual renderer produced. Designer,
    // print preview and PDF all consume the same persisted ReportVisualLayout — §12's requirement — and the
    // browser only turns that one rendering into paper.
    //
    // ONE BROWSER PER PROCESS, not per request. Chromium launch is ~300ms and a report screen can fire several
    // exports in a row; launching per call is how a reporting feature becomes the thing that exhausts the box.
    // The instance is created lazily under a lock and reused, and the class is a singleton in DI.
    //
    // IsAvailable IS RESOLVED ONCE, EAGERLY-ISH, AND CACHED. It must not throw and must not block a page
    // render: the Reports Center asks it on every screen to decide whether to show a PDF button, so a probe
    // that launched a browser to answer would make the catalogue slow.
    // ============================================================================================
    public sealed class PlaywrightHtmlToPdfConverter : IHtmlToPdfConverter, IAsyncDisposable, IDisposable
    {
        private readonly SemaphoreSlim _gate = new(1, 1);
        private IPlaywright? _playwright;
        private IBrowser? _browser;
        private string? _unavailable;

        public string EngineName => "Playwright.Chromium";

        // Optimistic until a launch actually fails. A false negative here would hide the PDF button from
        // everyone; a false positive costs one clear error on one download.
        public bool IsAvailable => _unavailable == null;

        public string? UnavailableReason => _unavailable;

        public async Task<byte[]> ConvertAsync(string html, ReportPdfOptions options,
            CancellationToken cancellationToken = default)
        {
            var browser = await BrowserAsync(cancellationToken);

            var page = await browser.NewPageAsync();
            try
            {
                // SetContent, not a navigation: the HTML is already complete and self-contained (the renderer
                // inlines its CSS and embeds images as data URIs), so there is no server to reach and no
                // opportunity for the page to fetch anything.
                await page.SetContentAsync(html, new PageSetContentOptions { WaitUntil = WaitUntilState.Load });

                // WAIT FOR THE DOCUMENT'S OWN FONT. `Load` does not cover it: an @font-face is fetched when
                // the first glyph needs it, so printing immediately can capture a page still laid out in the
                // fallback — which is how a report whose HTML says Cairo produced a PDF embedding only
                // Segoe UI. Read off the artifact, not inferred: the PDF's /BaseFont list named one face and
                // it was not the one the document declares.
                //
                // Bounded, and a failure here is not a failed document: if the promise never settles the
                // report still prints, in the fallback face, rather than timing out the whole export.
                try
                {
                    await page.EvaluateAsync("() => document.fonts && document.fonts.ready")
                        .WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch (TimeoutException) { }
                catch (Exception) { }

                var pdf = new PagePdfOptions
                {
                    Landscape = options.Landscape,
                    PrintBackground = options.PrintBackground,
                    Margin = new Margin
                    {
                        Top = $"{options.MarginTopMm}mm",
                        Bottom = $"{options.MarginBottomMm}mm",
                        Left = $"{options.MarginLeftMm}mm",
                        Right = $"{options.MarginRightMm}mm",
                    },
                };

                // A named paper size when the platform has one, explicit millimetres otherwise. PaperName is
                // null for Thermal80 — the one size with no ISO name — so the geometry table answers instead.
                if (!string.IsNullOrWhiteSpace(options.PaperName))
                {
                    pdf.Format = options.PaperName;
                }
                else
                {
                    var (w, h) = ReportPaper.SizeOf(options.PageSize);
                    pdf.Width = $"{w}mm";
                    pdf.Height = $"{h}mm";
                }

                if (!string.IsNullOrWhiteSpace(options.FooterTemplate))
                {
                    pdf.DisplayHeaderFooter = true;
                    pdf.FooterTemplate = options.FooterTemplate;
                    pdf.HeaderTemplate = options.HeaderTemplate ?? "<span></span>";
                }

                return await page.PdfAsync(pdf);
            }
            finally
            {
                await page.CloseAsync();
            }
        }

        private async Task<IBrowser> BrowserAsync(CancellationToken cancellationToken)
        {
            if (_browser is { IsConnected: true }) return _browser;

            await _gate.WaitAsync(cancellationToken);
            try
            {
                if (_browser is { IsConnected: true }) return _browser;

                try
                {
                    _playwright ??= await Playwright.CreateAsync();
                    _browser = await _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
                    {
                        Headless = true,
                        // --no-sandbox is required in containers and harmless on a dev box. Without it the
                        // launch fails on most Linux images with a message nobody reads as "sandbox".
                        Args = new[] { "--no-sandbox", "--disable-dev-shm-usage" },
                    });
                    _unavailable = null;
                    return _browser;
                }
                catch (Exception ex)
                {
                    // CACHED, so a deployment without the browser installed does not pay a failed launch on
                    // every request. The message names the fix because "PDF failed" is not actionable.
                    _unavailable =
                        "The PDF engine could not start a browser. Run 'pwsh bin/Debug/net8.0/playwright.ps1 " +
                        $"install chromium' on this host. ({ex.GetType().Name}: {ex.Message})";
                    throw new InvalidOperationException(_unavailable, ex);
                }
            }
            finally
            {
                _gate.Release();
            }
        }

        // BOTH interfaces, and the second one is not decoration.
        //
        // A SINGLETON that implements only IAsyncDisposable makes the container itself refuse to be disposed
        // synchronously: ServiceProvider.Dispose() throws "type only implements IAsyncDisposable". That is not
        // a hypothetical — the real-graph DI test builds the container, validates it and disposes it, and this
        // converter broke that test the moment it was registered. Any host or test that disposes a provider
        // synchronously would have hit the same wall.
        //
        // The synchronous path is best-effort by design: closing a browser is I/O, and blocking a Dispose on it
        // is how a shutdown hangs. It gives the close a bounded moment and then releases the local handles,
        // which the OS reclaims with the process anyway.
        public void Dispose()
        {
            var browser = _browser;
            _browser = null;

            if (browser != null)
            {
                try { browser.CloseAsync().Wait(TimeSpan.FromSeconds(5)); }
                catch { /* a browser that will not close is not a reason to fail a shutdown */ }
            }

            _playwright?.Dispose();
            _playwright = null;
            _gate.Dispose();
        }

        public async ValueTask DisposeAsync()
        {
            if (_browser != null) await _browser.CloseAsync();
            _playwright?.Dispose();
            _gate.Dispose();
        }
    }
}
