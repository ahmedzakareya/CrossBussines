using System.Xml;
using System.Xml.Linq;

namespace CrossBuy.BL.Hospital
{
	// ==========================================================================================
	// HOSPITAL H111 — THE PICTURE OF A DRAWING, for its printed paper (Report Studio). When a drawing is saved the screen
	// sends the SVG the doctor sees; the server keeps it only as a picture and only after cleaning it:
	//   * it must parse as XML with an <svg> root (no DTD), within a size bound;
	//   * scripts, foreignObject, iframes, embedded images and animations are dropped; every on* attribute goes; a link
	//     or url() that points anywhere but inside the drawing (#…) goes.
	// It is printed as an <img> data URI, where nothing runs anyway — the cleaning is the second wall, not the first.
	// The drawing's MEANING stays where it was: the marks and the state the server computed. The picture is the view.
	// ==========================================================================================
	public static class HospitalCanvasSnapshot
	{
		public const int MaxChars = 600_000;
		private static readonly HashSet<string> Banned = new(StringComparer.OrdinalIgnoreCase)
			{ "script", "foreignObject", "iframe", "image", "animate", "animateMotion", "animateTransform", "set", "audio", "video", "object", "embed", "handler", "listener" };

		/// The cleaned SVG, or (null, error) when it is not a drawing; null input → (null, null): no picture.
		public static (string? Svg, string? Error) Clean(string? svg)
		{
			if (string.IsNullOrWhiteSpace(svg)) return (null, null);
			if (svg.Length > MaxChars) return (null, "The drawing's picture is too large to keep");
			XDocument doc;
			try
			{
				using var r = XmlReader.Create(new StringReader(svg), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
				doc = XDocument.Load(r);
			}
			catch (XmlException) { return (null, "The drawing's picture could not be read"); }
			// the SVG namespace, as a browser's serializer writes it — a picture without it does not render in an <img>
			if (doc.Root == null || doc.Root.Name != XName.Get("svg", "http://www.w3.org/2000/svg")) return (null, "The drawing's picture could not be read");
			foreach (var el in doc.Descendants().Where(e => Banned.Contains(e.Name.LocalName)).ToList()) el.Remove();
			foreach (var el in doc.Root.DescendantsAndSelf())
				foreach (var a in el.Attributes().ToList())
				{
					string n = a.Name.LocalName, v = a.Value.Trim();
					if (n.StartsWith("on", StringComparison.OrdinalIgnoreCase)) { a.Remove(); continue; }
					if ((n is "href" or "src") && !v.StartsWith('#')) { a.Remove(); continue; }
					if (v.Contains("url(", StringComparison.OrdinalIgnoreCase) && !System.Text.RegularExpressions.Regex.IsMatch(v, @"url\(\s*['""]?#", System.Text.RegularExpressions.RegexOptions.IgnoreCase)) { a.Remove(); continue; }
					if (v.Contains("javascript:", StringComparison.OrdinalIgnoreCase)) a.Remove();
				}
			// a <style> may only style: anything that reaches outside is dropped with it
			foreach (var st in doc.Descendants().Where(e => e.Name.LocalName == "style").ToList())
				if (st.Value.Contains("url(", StringComparison.OrdinalIgnoreCase) || st.Value.Contains("@import", StringComparison.OrdinalIgnoreCase) || st.Value.Contains("expression", StringComparison.OrdinalIgnoreCase)) st.Remove();
			return (doc.Root.ToString(SaveOptions.DisableFormatting), null);
		}
	}
}
