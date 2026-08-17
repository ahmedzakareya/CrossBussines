using System.Collections.Concurrent;
using CrossBuy.Models.Platform;
using Microsoft.Extensions.Configuration;

namespace CrossBuy.BL.Platform.Ai
{
    // AI Foundation — Increment 4.5, Phase 3. USAGE AND COST CEILING.
    //
    // DELIBERATELY SEPARATE FROM THE RATE LIMITER, because they fail differently:
    //
    //   RATE LIMIT  bounds HOW OFTEN.  Thirty small calls a minute is fine; it is a loop detector.
    //   USAGE GUARD bounds HOW MUCH.   One call can carry a two-megabyte payload and return the maximum
    //                                  output the model will produce. Thirty of those inside the rate
    //                                  limit is a bill, and the rate limiter would allow every one.
    //
    // A single control tuned to catch both would be too tight for the first or too loose for the second.
    //
    // TOKENS ARE THE CEILING, COST IS A REPORT. Prices change, differ per model and per contract, and are
    // not something business logic should believe it knows. So the guard enforces on REQUEST COUNT and
    // TOKEN COUNT — facts the provider returns — and computes money only when a price has been configured.
    // An unpriced model is not free; it is unpriced, and the token ceiling still applies.

    public sealed record AiUsageKey(int CompanyId, string ProviderId, AiEgressPurpose Feature)
    {
        public override string ToString() => $"{CompanyId}|{ProviderId}|{Feature}";
    }

    /// What a provider reports after a call. Counts and identifiers only — never content.
    public sealed class AiProviderUsage
    {
        public required string ProviderId { get; init; }
        public required string Model { get; init; }
        public int InputTokens { get; init; }
        public int OutputTokens { get; init; }
        public int TotalTokens => InputTokens + OutputTokens;

        /// Null when no price is configured for (provider, model). Null means UNKNOWN, never zero.
        public decimal? EstimatedCost { get; init; }
        public string? Currency { get; init; }
    }

    public sealed class AiUsageDecision
    {
        public required bool Allowed { get; init; }
        public required string Reason { get; init; }
        public int RequestsUsed { get; init; }
        public long TokensUsed { get; init; }
        public required DateTime WindowEndsUtc { get; init; }

        public static AiUsageDecision Allow(int requests, long tokens, DateTime endsUtc)
            => new() { Allowed = true, Reason = "usage:ok", RequestsUsed = requests, TokensUsed = tokens, WindowEndsUtc = endsUtc };

        public static AiUsageDecision Deny(string reason, int requests, long tokens, DateTime endsUtc)
            => new() { Allowed = false, Reason = reason, RequestsUsed = requests, TokensUsed = tokens, WindowEndsUtc = endsUtc };
    }

    public sealed class AiUsageCeiling
    {
        public required int MaxRequestsPerWindow { get; init; }
        public required long MaxTotalTokensPerWindow { get; init; }
        public required TimeSpan Window { get; init; }
        public bool IsUsable => MaxRequestsPerWindow > 0 && MaxTotalTokensPerWindow > 0 && Window > TimeSpan.Zero;
    }

    /// Pricing is an injected lookup, never a constant in domain code.
    public interface IAiPricingProvider
    {
        /// Null when no price is configured. The caller must treat null as UNKNOWN, not as free.
        decimal? EstimateCost(string providerId, string model, int inputTokens, int outputTokens, out string? currency);
    }

    public interface IAiUsageGuard
    {
        /// Called BEFORE a provider call. Reserves one request against the ceiling.
        AiUsageDecision TryReserve(AiUsageKey key, DateTime nowUtc, CancellationToken ct = default);

        /// Called AFTER a provider call with whatever the provider reported. Tokens are only known
        /// afterwards, which is why reservation and accounting are two steps rather than one.
        void RecordUsage(AiUsageKey key, AiProviderUsage usage, DateTime nowUtc);

        AiUsageCeiling CeilingFor(AiUsageKey key);
        AiUsageSnapshot Snapshot(AiUsageKey key, DateTime nowUtc);
    }

    public sealed record AiUsageSnapshot(int Requests, long InputTokens, long OutputTokens, decimal? Cost, DateTime WindowStartUtc);

    /// <summary>
    /// Configuration-driven pricing. Prices live in configuration so they can be corrected without a
    /// deployment of business logic, and so an unpriced model is visibly unpriced.
    /// </summary>
    /// <remarks>
    /// Keys, per million tokens: <c>Ai:Pricing:{provider}:{model}:InputPerMillion</c>,
    /// <c>:OutputPerMillion</c>, <c>:Currency</c>. **No price is shipped in this repository** — quoting a
    /// provider's price list in source makes it authoritative-looking and stale within weeks.
    /// </remarks>
    public sealed class AiConfiguredPricingProvider : IAiPricingProvider
    {
        private readonly IConfiguration _config;
        public AiConfiguredPricingProvider(IConfiguration config) => _config = config;

        public decimal? EstimateCost(string providerId, string model, int inputTokens, int outputTokens, out string? currency)
        {
            currency = null;
            if (string.IsNullOrWhiteSpace(providerId) || string.IsNullOrWhiteSpace(model)) return null;

            var root = $"Ai:Pricing:{providerId}:{model}";
            if (!decimal.TryParse(_config[$"{root}:InputPerMillion"], out var inPer)) return null;
            if (!decimal.TryParse(_config[$"{root}:OutputPerMillion"], out var outPer)) return null;

            currency = _config[$"{root}:Currency"];
            return (inputTokens / 1_000_000m * inPer) + (outputTokens / 1_000_000m * outPer);
        }
    }

    /// <summary>
    /// In-process usage accounting. Same single-process limitation as <see cref="AiRateLimiter"/>, and the
    /// same reasoning: the abstraction is the durable part, the storage is replaceable.
    /// </summary>
    public sealed class AiUsageGuard : IAiUsageGuard
    {
        // Daily, because that is the period a runaway is noticed and paid for. Deliberately generous per
        // request and strict in aggregate: one legitimate analysis should never trip it, while a loop
        // reaches it quickly.
        public const int DefaultMaxRequestsPerWindow = 500;
        public const long DefaultMaxTotalTokensPerWindow = 2_000_000;
        public static readonly TimeSpan DefaultWindow = TimeSpan.FromHours(24);

        private const string ConfigRoot = "Ai:Usage";

        private readonly IConfiguration _config;
        private readonly IAiPricingProvider _pricing;
        private readonly ConcurrentDictionary<string, Counter> _counters = new(StringComparer.Ordinal);

        public AiUsageGuard(IConfiguration config, IAiPricingProvider pricing)
        {
            _config = config;
            _pricing = pricing;
        }

        private sealed class Counter
        {
            public readonly object Gate = new();
            public DateTime WindowStartUtc;
            public int Requests;
            public long InputTokens;
            public long OutputTokens;
            public decimal? Cost;
        }

        private long? ReadLong(string path) => long.TryParse(_config[path], out var v) ? v : null;

        public AiUsageCeiling CeilingFor(AiUsageKey key)
        {
            var requests = (int?)(ReadLong($"{ConfigRoot}:{key.ProviderId}:MaxRequestsPerWindow")
                                  ?? ReadLong($"{ConfigRoot}:MaxRequestsPerWindow"))
                           ?? DefaultMaxRequestsPerWindow;

            var tokens = ReadLong($"{ConfigRoot}:{key.ProviderId}:MaxTotalTokensPerWindow")
                         ?? ReadLong($"{ConfigRoot}:MaxTotalTokensPerWindow")
                         ?? DefaultMaxTotalTokensPerWindow;

            var hours = ReadLong($"{ConfigRoot}:WindowHours") ?? (long)DefaultWindow.TotalHours;

            var ceiling = new AiUsageCeiling
            {
                MaxRequestsPerWindow = requests,
                MaxTotalTokensPerWindow = tokens,
                Window = TimeSpan.FromHours(hours),
            };

            // Same rule as the rate limiter: a ceiling that bounds nothing is a misconfiguration, not a
            // grant of unlimited spend.
            return ceiling.IsUsable
                ? ceiling
                : new AiUsageCeiling
                {
                    MaxRequestsPerWindow = DefaultMaxRequestsPerWindow,
                    MaxTotalTokensPerWindow = DefaultMaxTotalTokensPerWindow,
                    Window = DefaultWindow,
                };
        }

        private Counter Bucket(AiUsageKey key, AiUsageCeiling ceiling, DateTime nowUtc)
        {
            var c = _counters.GetOrAdd(key.ToString(), _ => new Counter { WindowStartUtc = nowUtc });
            if (nowUtc - c.WindowStartUtc >= ceiling.Window || nowUtc < c.WindowStartUtc)
            {
                c.WindowStartUtc = nowUtc;
                c.Requests = 0; c.InputTokens = 0; c.OutputTokens = 0; c.Cost = null;
            }
            return c;
        }

        public AiUsageDecision TryReserve(AiUsageKey key, DateTime nowUtc, CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(key);
            ct.ThrowIfCancellationRequested();

            if (key.CompanyId <= 0)
                return AiUsageDecision.Deny("usage:company-unresolved", 0, 0, nowUtc);

            var ceiling = CeilingFor(key);
            var c = _counters.GetOrAdd(key.ToString(), _ => new Counter { WindowStartUtc = nowUtc });

            lock (c.Gate)
            {
                var b = Bucket(key, ceiling, nowUtc);
                var endsUtc = b.WindowStartUtc + ceiling.Window;
                var tokens = b.InputTokens + b.OutputTokens;

                if (b.Requests >= ceiling.MaxRequestsPerWindow)
                    return AiUsageDecision.Deny($"usage:requests-exceeded:{ceiling.MaxRequestsPerWindow}", b.Requests, tokens, endsUtc);

                // Checked BEFORE the call using tokens ALREADY SPENT. The cost of the call about to be
                // made is unknowable in advance, so the ceiling is enforced on arrival at it rather than
                // by predicting it — which means the ceiling can be crossed by at most one request. That
                // is the correct trade: refusing on a guess would refuse legitimate work.
                if (tokens >= ceiling.MaxTotalTokensPerWindow)
                    return AiUsageDecision.Deny($"usage:tokens-exceeded:{ceiling.MaxTotalTokensPerWindow}", b.Requests, tokens, endsUtc);

                b.Requests++;
                return AiUsageDecision.Allow(b.Requests, tokens, endsUtc);
            }
        }

        public void RecordUsage(AiUsageKey key, AiProviderUsage usage, DateTime nowUtc)
        {
            ArgumentNullException.ThrowIfNull(key);
            ArgumentNullException.ThrowIfNull(usage);
            if (key.CompanyId <= 0) return;

            var ceiling = CeilingFor(key);
            var c = _counters.GetOrAdd(key.ToString(), _ => new Counter { WindowStartUtc = nowUtc });

            lock (c.Gate)
            {
                var b = Bucket(key, ceiling, nowUtc);
                b.InputTokens += usage.InputTokens;
                b.OutputTokens += usage.OutputTokens;

                var cost = usage.EstimatedCost
                           ?? _pricing.EstimateCost(usage.ProviderId, usage.Model, usage.InputTokens, usage.OutputTokens, out _);

                // Null stays null. An unpriced model must not accumulate as zero — a running total of
                // "0.00" beside real token counts reads as free, which is the wrong thing to believe.
                if (cost.HasValue) b.Cost = (b.Cost ?? 0m) + cost.Value;
            }
        }

        public AiUsageSnapshot Snapshot(AiUsageKey key, DateTime nowUtc)
        {
            var ceiling = CeilingFor(key);
            var c = _counters.GetOrAdd(key.ToString(), _ => new Counter { WindowStartUtc = nowUtc });
            lock (c.Gate)
            {
                var b = Bucket(key, ceiling, nowUtc);
                return new AiUsageSnapshot(b.Requests, b.InputTokens, b.OutputTokens, b.Cost, b.WindowStartUtc);
            }
        }
    }
}
