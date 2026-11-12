using System.Text.RegularExpressions;

namespace CrossBuy.BL.Reporting
{
    // ============================================================================================
    // H111 — AN IMAGE A DATASET ROW CARRIES. The value of a field bound to an Image element is printed as an <img>
    // source, so it must be a picture and nothing else: a base64 data URI of an image type, within a size bound.
    // A URL is refused (the platform never fetches), and SVG inside an <img> runs no script and loads nothing.
    // ============================================================================================
    public static class ReportImageData
    {
        public const int MaxChars = 4_000_000;
        private static readonly Regex Shape = new(@"^data:image/(svg\+xml|png|jpeg);base64,[A-Za-z0-9+/]+={0,2}$", RegexOptions.CultureInvariant);

        public static bool IsSafe(string? value) =>
            !string.IsNullOrEmpty(value) && value.Length <= MaxChars && Shape.IsMatch(value);

        /// An SVG document as the data URI an Image element prints.
        public static string SvgUri(string svg) => "data:image/svg+xml;base64," + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(svg));
    }
}
