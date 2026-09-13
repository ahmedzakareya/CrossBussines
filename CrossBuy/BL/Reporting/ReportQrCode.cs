using QRCoder;

namespace CrossBuy.BL.Reporting
{
    // ============================================================================================
    // A QR CODE ON A PRINTED DOCUMENT.
    //
    // WHY IT IS A RENDERED IMAGE AND NOT A URL. HtmlReportRenderer's rule at the top of that file is
    // that a report is self-contained: inline styles, no external stylesheet, no font url, no script.
    // A QR served from an endpoint would break that in the worst place — the PDF converter is a
    // headless browser that may have no route back to the application, so the code would be missing
    // from exactly the copy that gets filed. It is generated here and embedded as bytes, the same way
    // every other image in a report already is.
    //
    // PNG rather than SVG. QRCoder can emit both; the renderer already has ONE way to place an image
    // (a data URI in an <img>), and a second path that injects raw markup into the document would be
    // the only place in this renderer where a string is not HTML-encoded on its way to the page.
    // Eight pixels per module at these sizes is past what a phone camera resolves on paper.
    //
    // WHAT IT IS NOT: a tax-authority payload. ZATCA's TLV, Egypt's ETA UUID and every other scheme
    // are BUSINESS values with their own rules and their own signing, and inventing one here would
    // put a number on an invoice that no authority agreed to. This encodes what the document already
    // knows: a field the author binds, or text they type. When a specific authority is adopted, its
    // payload becomes a DATASET FIELD computed by the module that owns the rule, and the author binds
    // this element to it — no change here.
    //
    // CACHED BY PAYLOAD. A register of 400 rows with a QR in the detail band would otherwise encode
    // 400 times; a document repeats the same payload on every row of its header band.
    // ============================================================================================
    public static class ReportQrCode
    {
        // A QR carries 2,953 bytes at the most permissive settings, and a payload near that ceiling
        // needs a module count no printed box resolves. This is the practical limit for something a
        // camera reads off paper — past it, the code is a decoration that never scans.
        public const int MaxPayloadChars = 900;

        private const int PixelsPerModule = 8;

        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> Cache = new();

        /// <summary>A data: URI for the payload, or null when there is nothing to encode.</summary>
        public static string? DataUri(string? payload)
        {
            if (string.IsNullOrWhiteSpace(payload)) return null;

            var text = payload.Trim();
            if (text.Length > MaxPayloadChars) return null;

            if (Cache.TryGetValue(text, out var cached)) return cached;

            try
            {
                // ECC Q (25% recovery) is what a printed code wants: paper creases, ink spreads and a
                // stamp lands on the corner. L would fit more data in fewer modules and fail the first
                // time someone folds the invoice.
                using var generator = new QRCodeGenerator();
                using var data = generator.CreateQrCode(text, QRCodeGenerator.ECCLevel.Q);

                var png = new PngByteQRCode(data).GetGraphic(PixelsPerModule);
                var uri = "data:image/png;base64," + Convert.ToBase64String(png);

                // Bounded on purpose: a document has a handful of distinct payloads, and an unbounded
                // cache keyed by arbitrary text is a memory leak with a business name on it.
                if (Cache.Count > 256) Cache.Clear();
                Cache[text] = uri;
                return uri;
            }
            catch (Exception)
            {
                // A CODE IS NOT WORTH A FAILED DOCUMENT. An unencodable payload prints as an empty box,
                // the same as an image whose file has moved.
                return null;
            }
        }
    }
}
