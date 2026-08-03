using System;
using System.Linq;

namespace CrossBuy.BL
{
    // HM-3: the branch-level scale-barcode format. Built from BranchPosSetting; NULL/empty prefix ⇒ not configured.
    public class ScaleBarcodeConfig
    {
        public string? Prefix { get; set; }
        public int ItemCodeLength { get; set; }
        public int ValueLength { get; set; }
        public int ValueDecimals { get; set; }
        public string ValueType { get; set; } = "Weight";   // Weight (implemented) | Price (rejected: not supported yet)
        public string CheckAlgo { get; set; } = "EanMod10";

        public bool IsConfigured => !string.IsNullOrEmpty(Prefix) && ItemCodeLength > 0 && ValueLength > 0;
        // prefix + item code + value + 1 check digit (so the parser validates the actual length, never assumes 13).
        public int TotalLength => (Prefix?.Length ?? 0) + ItemCodeLength + ValueLength + 1;
    }

    public class ScaleParseResult
    {
        public bool Ok { get; set; }
        public string? ErrorCode { get; set; }   // "length" | "check" | "priceType"
        public int? ItemCode { get; set; }
        public decimal? WeightKg { get; set; }
    }

    /// <summary>
    /// HM-3: parses a variable-measure scale barcode against a branch config. Pure (no DB). The value is embedded WEIGHT
    /// (grams to ValueDecimals); "Price" is rejected as not-supported. Check digit is standard GS1 mod-10, so the same code
    /// validates EAN-13 (default 1+5+6+1=13) or any other length the config declares (length = sum of field lengths + 1).
    /// </summary>
    public static class ScaleBarcodeParser
    {
        public static bool MatchesPrefix(string? barcode, ScaleBarcodeConfig cfg)
            => cfg.IsConfigured && !string.IsNullOrEmpty(barcode) && barcode!.StartsWith(cfg.Prefix!, StringComparison.Ordinal);

        public static ScaleParseResult Parse(string? barcode, ScaleBarcodeConfig cfg)
        {
            var r = new ScaleParseResult();
            barcode = (barcode ?? "").Trim();
            if (!cfg.IsConfigured) { r.ErrorCode = "length"; return r; }
            if (barcode.Length != cfg.TotalLength || !barcode.All(char.IsDigit)) { r.ErrorCode = "length"; return r; }
            if (!string.Equals(cfg.ValueType, "Weight", StringComparison.OrdinalIgnoreCase)) { r.ErrorCode = "priceType"; return r; }
            if (string.Equals(cfg.CheckAlgo, "EanMod10", StringComparison.OrdinalIgnoreCase))
            {
                var body = barcode.Substring(0, barcode.Length - 1);
                if (barcode[barcode.Length - 1] - '0' != EanCheckDigit(body)) { r.ErrorCode = "check"; return r; }
            }
            int p = cfg.Prefix!.Length;
            var itemPart = barcode.Substring(p, cfg.ItemCodeLength);
            var valuePart = barcode.Substring(p + cfg.ItemCodeLength, cfg.ValueLength);
            r.ItemCode = int.Parse(itemPart);
            var raw = decimal.Parse(valuePart);
            r.WeightKg = raw / Pow10(cfg.ValueDecimals);
            r.Ok = true;
            return r;
        }

        // GS1 mod-10 over <body> (all digits except the check): from the left, odd 1-based positions ×1, even ×3.
        public static int EanCheckDigit(string body)
        {
            int sum = 0;
            for (int i = 0; i < body.Length; i++)
            {
                int d = body[i] - '0';
                sum += (i % 2 == 0) ? d : d * 3;
            }
            return (10 - (sum % 10)) % 10;
        }

        // Build a full scale barcode (seeds/tests): prefix + zero-padded item + zero-padded weight-value + check.
        public static string Build(ScaleBarcodeConfig cfg, int itemCode, decimal weightKg)
        {
            var value = (long)Math.Round(weightKg * Pow10(cfg.ValueDecimals), 0, MidpointRounding.AwayFromZero);
            var body = cfg.Prefix + itemCode.ToString().PadLeft(cfg.ItemCodeLength, '0') + value.ToString().PadLeft(cfg.ValueLength, '0');
            return body + EanCheckDigit(body).ToString();
        }

        private static decimal Pow10(int n) { decimal r = 1m; for (int i = 0; i < n; i++) r *= 10m; return r; }
    }
}
