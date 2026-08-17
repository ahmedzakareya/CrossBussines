using System.Text.Json;
using System.Text.Json.Serialization;
using CrossBuy.Models.Platform;

namespace CrossBuy.BL.Platform.Ai
{
    // AI Foundation — Increment 4.5, Phase 8. THE CASHFLOW FORECAST PROVIDER BOUNDARY.
    //
    // WHAT THIS IS NOT: a switch. AiInsightsService.ForecastCashflowAsync is UNCHANGED and still calls the
    // local Python ML service. Nothing here is wired into that path, because OpenAI is unapproved and
    // rewiring a working feature to an unreachable provider would break it for no gain.
    //
    // WHAT THIS IS: the typed contract that path would use afterwards, so that when approval exists the
    // change is a call site rather than a design.
    //
    // THE PAYLOAD IS ALREADY MINIMAL AND IS NOT WIDENED. AiInsightsService computes the schedule from
    // customers, vendors, invoices, receipts and payments — and emits only opening cash plus dated
    // amounts. The per-party detail is CONSUMED, not forwarded. This contract mirrors that shape exactly;
    // if it were a superset it would quietly become the reason a wider payload got built.

    /// The outbound payload. Classification: FinancialAggregate.
    public sealed class AiCashflowForecastRequest
    {
        [JsonPropertyName("openingCash")] public required decimal OpeningCash { get; init; }
        [JsonPropertyName("asOf")] public required DateTime AsOf { get; init; }
        [JsonPropertyName("horizonDays")] public required int HorizonDays { get; init; }
        [JsonPropertyName("currency")] public string? Currency { get; init; }
        [JsonPropertyName("inflows")] public required IReadOnlyList<AiCashflowPoint> Inflows { get; init; }
        [JsonPropertyName("outflows")] public required IReadOnlyList<AiCashflowPoint> Outflows { get; init; }

        // -----------------------------------------------------------------------------------------
        // EXCLUDED BY CONSTRUCTION, not by convention. There is no property for any of these, so
        // including one is a compile error rather than a review finding:
        //
        //   customer name · vendor name · invoice number · counterparty id · employee data ·
        //   email · phone · address · journal description · any free text · document content ·
        //   credentials · arbitrary identifiers
        //
        // A dated amount is not attributable to a party. That is what makes the payload an aggregate
        // rather than a ledger extract.
        // -----------------------------------------------------------------------------------------

        public string ToPayloadJson() => JsonSerializer.Serialize(this);
    }

    /// A date and an amount. Nothing else — deliberately.
    public sealed class AiCashflowPoint
    {
        [JsonPropertyName("date")] public required DateTime Date { get; init; }
        [JsonPropertyName("amount")] public required decimal Amount { get; init; }
    }

    public enum AiCashflowRiskLevel
    {
        Unknown = 0,   // an unparsed or unrecognised rating is never "Low"
        Low,
        Medium,
        High,
    }

    /// <summary>The validated advisory result. Advisory ONLY — it changes no financial record.</summary>
    public sealed class AiCashflowForecastAdvice
    {
        public required string Summary { get; init; }
        public decimal? ExpectedClosingCash { get; init; }
        public required AiCashflowRiskLevel RiskLevel { get; init; }
        public required IReadOnlyList<string> Risks { get; init; }
        public required IReadOnlyList<string> Drivers { get; init; }
        public required IReadOnlyList<string> Recommendations { get; init; }
        public required IReadOnlyList<string> Limitations { get; init; }
    }

    /// <summary>
    /// Turns untrusted model text into a validated advice object, or refuses.
    /// </summary>
    /// <remarks>
    /// <para><b>Model output is untrusted external input</b> and is treated exactly as a request body from
    /// an unauthenticated caller would be. This parser only ever produces strings and an enum. It does not
    /// deserialise into a domain entity, does not evaluate anything, and cannot reach SQL, a shell, a URL
    /// or a permission check.</para>
    /// <para>Every string is length-capped and control characters are stripped, so a model that returns a
    /// megabyte of text or embeds terminal escapes cannot turn a summary field into a rendering or logging
    /// problem downstream.</para>
    /// </remarks>
    public static class AiCashflowForecastValidator
    {
        public const int MaxSummaryChars = 2_000;
        public const int MaxItemChars = 400;
        public const int MaxItems = 10;

        public static bool TryParse(string? content, out AiCashflowForecastAdvice? advice, out string reason)
        {
            advice = null;

            if (string.IsNullOrWhiteSpace(content)) { reason = "advice:empty"; return false; }
            if (content.Length > AiEgressLimits.MaxResponseBytes) { reason = "advice:too-large"; return false; }

            JsonDocument doc;
            try { doc = JsonDocument.Parse(content); }
            catch (JsonException) { reason = "advice:not-json"; return false; }

            using (doc)
            {
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object) { reason = "advice:not-object"; return false; }

                var summary = Clean(Str(root, "summary"), MaxSummaryChars);
                if (string.IsNullOrWhiteSpace(summary)) { reason = "advice:missing-summary"; return false; }

                // An unrecognised rating becomes Unknown, never a default of Low. A model that answers
                // "moderate-to-severe" must not be read as reassurance.
                var risk = Str(root, "riskLevel")?.Trim().ToLowerInvariant() switch
                {
                    "low" => AiCashflowRiskLevel.Low,
                    "medium" => AiCashflowRiskLevel.Medium,
                    "high" => AiCashflowRiskLevel.High,
                    _ => AiCashflowRiskLevel.Unknown,
                };

                decimal? closing = null;
                if (root.TryGetProperty("expectedClosingCash", out var c)
                    && c.ValueKind == JsonValueKind.Number
                    && c.TryGetDecimal(out var d)) closing = d;

                advice = new AiCashflowForecastAdvice
                {
                    Summary = summary!,
                    ExpectedClosingCash = closing,
                    RiskLevel = risk,
                    Risks = Array_(root, "risks"),
                    Drivers = Array_(root, "drivers"),
                    Recommendations = Array_(root, "recommendations"),
                    Limitations = Array_(root, "limitations"),
                };

                reason = "advice:ok";
                return true;
            }
        }

        private static string? Str(JsonElement o, string name)
            => o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

        private static IReadOnlyList<string> Array_(JsonElement o, string name)
        {
            if (!o.TryGetProperty(name, out var arr) || arr.ValueKind != JsonValueKind.Array)
                return Array.Empty<string>();

            var list = new List<string>();
            foreach (var e in arr.EnumerateArray())
            {
                if (list.Count >= MaxItems) break;   // bounded, not truncated silently per item
                if (e.ValueKind != JsonValueKind.String) continue;
                var s = Clean(e.GetString(), MaxItemChars);
                if (!string.IsNullOrWhiteSpace(s)) list.Add(s!);
            }
            return list;
        }

        /// Strips control characters and caps length. Guards the log and the view, not the model.
        private static string? Clean(string? s, int max)
        {
            if (s is null) return null;
            var sb = new System.Text.StringBuilder(Math.Min(s.Length, max));
            foreach (var ch in s)
            {
                if (sb.Length >= max) break;
                if (char.IsControl(ch) && ch is not ('\n' or '\t')) continue;
                sb.Append(ch);
            }
            return sb.ToString().Trim();
        }

        /// <summary>The system prompt. A CrossBuy CONSTANT — no user text is ever interpolated into it.</summary>
        public const string SystemPrompt =
            "You are a financial analyst assistant for an ERP system. You receive an aggregated cash " +
            "position: an opening cash balance, an as-of date, a horizon in days, and two lists of dated " +
            "amounts (expected inflows and outflows). No counterparty names or identifiers are provided " +
            "and you must not invent any. Respond with a single JSON object and nothing else, using " +
            "exactly these keys: summary (string), expectedClosingCash (number), riskLevel (one of " +
            "\"low\", \"medium\", \"high\"), risks (array of strings), drivers (array of strings), " +
            "recommendations (array of strings), limitations (array of strings). Your output is advisory " +
            "only and will not modify any financial record.";
    }
}
