using System.Text;

namespace CrossBuy.BL
{
	// HM-4: shelf-label barcode rendering. Draws a scannable EAN-13 as a pure SVG string — ZERO external dependency
	// (no JS library, no image library). The bars are computed from the digits via the standard EAN-13 encoding.
	//
	// Scannability is decided by two things the spec calls out: correct bar/space module ratios (each symbol digit is
	// exactly 7 equal modules here, so ratios are exact by construction) and adequate QUIET ZONES (the blank margins).
	// EAN-13 requires ≥ 11 modules left and ≥ 7 right; we use exactly those. A round-trip self-test (encode → decode →
	// compare + mod-10) proves the drawn symbol carries the intended digits. FINAL validation on a real scanner stays a
	// human step — this service can prove the encoding is correct, not that a given printer/DPI renders it crisply.
	public interface IShelfLabelService
	{
		// (svg, ok, reason). ok=false when the barcode is not a valid EAN-13 (wrong length, non-digit, bad check digit);
		// the caller then prints the digits as text with no bars rather than a broken symbol.
		(string? svg, bool ok, string? reason) BuildEan13Svg(string? barcode, int moduleWidth = 2, int barHeight = 60);
		// Self-test used by acceptance: encode the digits, decode the drawn modules back, assert equality + valid check.
		bool VerifyRoundTrip(string? barcode);
	}

	// Public view models — passed to Razor via ViewBag. They MUST be public (not anonymous types): a Razor view is a
	// separate assembly and cannot late-bind the members of an internal anonymous type (RuntimeBinderException).
	public class ShelfLabelCard
	{
		public string Name { get; set; } = "";
		public string Price { get; set; } = "";      // already formatted to the currency's decimals
		public string Unit { get; set; } = "";
		public bool Weighted { get; set; }
		public int? ScaleCode { get; set; }
		public string? BarcodeSvg { get; set; }       // null → print the digits as text (BarcodeText)
		public string BarcodeText { get; set; } = "";
	}

	public class PriceCheckResult
	{
		public string? Name { get; set; }
		public decimal Price { get; set; }
		public int Dp { get; set; }
		public string? Unit { get; set; }
		public bool Weighted { get; set; }
		public int? ScaleCode { get; set; }
		public string Source { get; set; } = "";
	}

	public class ShelfLabelService : IShelfLabelService
	{
		// 7-module symbols. L = left-odd, G = left-even, R = right. G is the reverse of R; L is the complement of R.
		private static readonly string[] L = { "0001101","0011001","0010011","0111101","0100011","0110001","0101111","0111011","0110111","0001011" };
		private static readonly string[] G = { "0100111","0110011","0011011","0100001","0011101","0111001","0000101","0010001","0001001","0010111" };
		private static readonly string[] R = { "1110010","1100110","1101100","1000010","1011100","1001110","1010000","1000100","1001000","1110100" };
		// The first digit is not drawn as bars — it is carried by the L/G parity pattern of the six LEFT digits.
		private static readonly string[] Parity = { "LLLLLL","LLGLGG","LLGGLG","LLGGGL","LGLLGG","LGGLLG","LGGGLL","LGLGLG","LGLGGL","LGGLGL" };

		private const int QuietLeft = 11;   // EAN-13 minimum
		private const int QuietRight = 7;   // EAN-13 minimum

		// Check digit — SAME convention as BL/ScaleBarcodeParser.EanCheckDigit (from the left, even index ×1, odd ×3),
		// kept identical so both agree on every EAN-13.
		private static int CheckDigit(string body12)
		{
			int s = 0;
			for (int i = 0; i < 12; i++) { int d = body12[i] - '0'; s += (i % 2 == 0) ? d : d * 3; }
			return (10 - s % 10) % 10;
		}

		private static bool IsValidEan13(string? bc)
		{
			if (string.IsNullOrEmpty(bc) || bc.Length != 13) return false;
			foreach (var c in bc) if (c < '0' || c > '9') return false;
			return (bc[12] - '0') == CheckDigit(bc.Substring(0, 12));
		}

		// The 95-module bar/space string for a valid 13-digit code: start(101) + 6 left(L/G) + center(01010) + 6 right(R) + end(101).
		private static string BuildModules(string d)
		{
			var sb = new StringBuilder(95);
			sb.Append("101");
			string par = Parity[d[0] - '0'];
			for (int i = 0; i < 6; i++) { int dig = d[1 + i] - '0'; sb.Append(par[i] == 'L' ? L[dig] : G[dig]); }
			sb.Append("01010");
			for (int i = 0; i < 6; i++) { int dig = d[7 + i] - '0'; sb.Append(R[dig]); }
			sb.Append("101");
			return sb.ToString();
		}

		public (string? svg, bool ok, string? reason) BuildEan13Svg(string? barcode, int moduleWidth = 2, int barHeight = 60)
		{
			if (!IsValidEan13(barcode)) return (null, false, "not a valid EAN-13");
			string d = barcode!;
			string modules = BuildModules(d);
			int textH = 14;
			int totalModules = QuietLeft + 95 + QuietRight;
			int w = totalModules * moduleWidth;
			int h = barHeight + textH;
			var sb = new StringBuilder();
			sb.Append($"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"{w}\" height=\"{h}\" viewBox=\"0 0 {w} {h}\" role=\"img\">");
			sb.Append($"<rect width=\"{w}\" height=\"{h}\" fill=\"#ffffff\"/>");   // white background = the quiet zones are real white
			int x = QuietLeft * moduleWidth;
			// draw runs of consecutive '1' modules as single black bars
			int i2 = 0;
			while (i2 < modules.Length)
			{
				if (modules[i2] == '1')
				{
					int run = 1;
					while (i2 + run < modules.Length && modules[i2 + run] == '1') run++;
					sb.Append($"<rect x=\"{x + i2 * moduleWidth}\" y=\"0\" width=\"{run * moduleWidth}\" height=\"{barHeight}\" fill=\"#000000\"/>");
					i2 += run;
				}
				else i2++;
			}
			sb.Append($"<text x=\"{w / 2}\" y=\"{barHeight + textH - 2}\" text-anchor=\"middle\" font-family=\"monospace\" font-size=\"12\" fill=\"#000000\">{d}</text>");
			sb.Append("</svg>");
			return (sb.ToString(), true, null);
		}

		// Decode a 95-module string back to 13 digits (or null if malformed). Used only by VerifyRoundTrip.
		private static string? DecodeModules(string m)
		{
			if (m.Length != 95 || m.Substring(0, 3) != "101" || m.Substring(45, 5) != "01010" || m.Substring(92, 3) != "101") return null;
			var parity = new StringBuilder(6);
			var digits = new StringBuilder(13);
			digits.Append('?');   // placeholder for the parity-derived first digit
			// six left digits at offset 3
			for (int i = 0; i < 6; i++)
			{
				string sym = m.Substring(3 + i * 7, 7);
				int li = Array.IndexOf(L, sym), gi = Array.IndexOf(G, sym);
				if (li >= 0) { parity.Append('L'); digits.Append((char)('0' + li)); }
				else if (gi >= 0) { parity.Append('G'); digits.Append((char)('0' + gi)); }
				else return null;
			}
			// six right digits at offset 50
			for (int i = 0; i < 6; i++)
			{
				string sym = m.Substring(50 + i * 7, 7);
				int ri = Array.IndexOf(R, sym);
				if (ri < 0) return null;
				digits.Append((char)('0' + ri));
			}
			int first = Array.IndexOf(Parity, parity.ToString());
			if (first < 0) return null;
			digits[0] = (char)('0' + first);
			return digits.ToString();
		}

		public bool VerifyRoundTrip(string? barcode)
		{
			if (!IsValidEan13(barcode)) return false;
			string modules = BuildModules(barcode!);
			string? decoded = DecodeModules(modules);
			return decoded == barcode && IsValidEan13(decoded);
		}
	}
}
