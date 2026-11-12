using CrossBuy.BL.Hospital;
using CrossBuy.BL.Reporting;
using Xunit;

namespace CrossBuy.Tests
{
	// ==========================================================================================
	// HOSPITAL H111 — a clinical drawing's paper, with no database: the picture is kept only as a cleaned SVG (no script,
	// no event, no link outside the drawing, no DTD), and a report image bound to a field prints only a strict image data
	// URI — never a URL, never markup.
	// ==========================================================================================
	public class HospitalCanvasPrintTests
	{
		private const string Ns = "http://www.w3.org/2000/svg";

		[Fact]
		public void The_picture_is_cleaned()
		{
			var dirty = $"<svg xmlns=\"{Ns}\" viewBox=\"0 0 10 10\" onload=\"alert(1)\"><script>alert(1)</script><rect width=\"5\" height=\"5\" onclick=\"x()\" fill=\"#3A5795\"/>" +
						"<a href=\"https://evil.example\"><text>x</text></a><use href=\"#m\"/><foreignObject><div>x</div></foreignObject><circle style=\"fill:url(https://evil/x)\"/><circle fill=\"url(#g)\"/></svg>";
			var (svg, err) = HospitalCanvasSnapshot.Clean(dirty);
			Assert.Null(err);
			Assert.DoesNotContain("script", svg);
			Assert.DoesNotContain("onload", svg); Assert.DoesNotContain("onclick", svg);
			Assert.DoesNotContain("evil", svg);
			Assert.DoesNotContain("foreignObject", svg);
			Assert.Contains("href=\"#m\"", svg);        // a link inside the drawing stays
			Assert.Contains("url(#g)", svg);            // and so does a gradient of its own
			Assert.Contains("fill=\"#3A5795\"", svg);   // the drawing itself is untouched
		}

		[Fact]
		public void Only_a_drawing_is_kept()
		{
			Assert.Equal((null, null), HospitalCanvasSnapshot.Clean(null));   // no picture sent: nothing changes
			Assert.Equal("The drawing's picture could not be read", HospitalCanvasSnapshot.Clean("<html><body/></html>").Error);
			Assert.Equal("The drawing's picture could not be read", HospitalCanvasSnapshot.Clean("<svg><rect/></svg>").Error);   // no SVG namespace
			Assert.Equal("The drawing's picture could not be read", HospitalCanvasSnapshot.Clean("<!DOCTYPE x [<!ENTITY e SYSTEM \"file:///c:/x\">]><svg xmlns=\"" + Ns + "\">&e;</svg>").Error);
			Assert.Equal("The drawing's picture is too large to keep", HospitalCanvasSnapshot.Clean(new string('x', HospitalCanvasSnapshot.MaxChars + 1)).Error);
		}

		[Fact]
		public void A_report_image_from_a_field_is_only_an_image()
		{
			Assert.True(ReportImageData.IsSafe(ReportImageData.SvgUri($"<svg xmlns=\"{Ns}\"/>")));
			Assert.True(ReportImageData.IsSafe("data:image/png;base64,iVBORw0KGgo="));
			Assert.False(ReportImageData.IsSafe("https://example.com/x.png"));
			Assert.False(ReportImageData.IsSafe("data:text/html;base64,PHNjcmlwdD4="));
			Assert.False(ReportImageData.IsSafe("data:image/svg+xml;utf8,<svg onload=alert(1)>"));
			Assert.False(ReportImageData.IsSafe("data:image/png;base64,AAA\" onerror=\"x"));
			Assert.False(ReportImageData.IsSafe(null));
		}
	}
}
