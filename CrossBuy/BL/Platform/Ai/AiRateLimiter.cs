using System.Collections.Concurrent;
using CrossBuy.Models.Platform;
using Microsoft.Extensions.Configuration;

namespace CrossBuy.BL.Platform.Ai
{
    // AI Foundation — Increment 4.5, Phase 2. RATE LIMITING for AI work.
    //
    // WHY IT DID NOT EXIST AND WHY IT DOES NOW. Every AI control built so far answers "is this SAFE to
    // send?" — classification, tenancy, permission, provider approval. Not one of them answers "is this
    // the four-hundredth time in a minute?". A loop over a large ledger is a working feature and, against
    // a paid provider, an unbounded invoice. Safety controls do not notice volume.
    //
    // DELIBERATELY PROVIDER-INDEPENDENT AND FEATURE-INDEPENDENT. It is keyed by (company, provider,
    // feature) rather than living inside any one feature, because the next feature must inherit the limit
    // without remembering to ask for it.
    //
    // COMPANY IS PART OF THE KEY, ALWAYS. One tenant exhausting a shared bucket would be a
    // denial-of-service against every other tenant, delivered by our own cost control.
    public sealed record AiRateLimitKey(int CompanyId, string ProviderId, AiEgressPurpose Feature)
    {
        public override string ToString() => $"{CompanyId}|{ProviderId}|{Feature}";
    }

    public sealed class AiRateLimitDecision
    {
        public required bool Allowed { get; init; }

        /// How many permits remain in the current window. Reporting only.
        public required int Remaining { get; init; }

        /// When the current window ends. Safe to surface to a caller — it is a clock value, not data.
        public required DateTime WindowEndsUtc { get; init; }

        /// Machine-readable, payload-free. Safe to log and to return.
        public required string Reason { get; init; }

        public static AiRateLimitDecision Allow(int remaining, DateTime endsUtc)
            => new() { Allowed = true, Remaining = remaining, WindowEndsUtc = endsUtc, Reason = "rate:ok" };

        public static AiRateLimitDecision Deny(DateTime endsUtc, string reason)
            => new() { Allowed = false, Remaining = 0, WindowEndsUtc = endsUtc, Reason = reason };
    }

    public sealed class AiRateLimitPolicy
    {
        public required int MaxRequests { get; init; }
        public required TimeSpan Window { get; init; }

        /// A policy is only usable if it actually bounds something.
        public bool IsUsable => MaxRequests > 0 && Window > TimeSpan.Zero;
    }

    public interface IAiRateLimiter
    {
        /// Consumes one permit when allowed. `nowUtc` is a parameter rather than read from the clock so
        /// window behaviour is testable without sleeping.
        AiRateLimitDecision TryAcquire(AiRateLimitKey key, DateTime nowUtc, CancellationToken ct = default);

        /// The policy that would apply, without consuming a permit.
        AiRateLimitPolicy PolicyFor(AiRateLimitKey key);
    }

    /// <summary>
    /// Fixed-window limiter held in process memory.
    /// </summary>
    /// <remarks>
    /// <para><b>KNOWN LIMITATION — SINGLE PROCESS.</b> Counters live in this process only. Behind a load
    /// balancer or with multiple IIS worker processes, each process enforces its own budget, so the
    /// effective ceiling is <c>limit × processes</c>. This is stated rather than hidden because the
    /// alternative was worse: the repository has no Redis, no distributed cache abstraction and no
    /// existing shared-counter mechanism, and inventing an infrastructure dependency inside a rate
    /// limiter would be a larger and less reviewable change than the control itself.</para>
    /// <para>The abstraction is the durable part. A distributed implementation replaces this class and
    /// nothing else. Note that <c>Runtime:RequireSingleWorkerProcess</c> is already <c>true</c> in this
    /// deployment, which narrows — but does not eliminate — the multi-process case.</para>
    /// <para><b>Concurrency.</b> Buckets are held in a <see cref="ConcurrentDictionary{TKey,TValue}"/> and
    /// each bucket mutates under its own lock, so two requests for the same key cannot both read the same
    /// count and both decide they are permit number N.</para>
    /// </remarks>
    public sealed class AiRateLimiter : IAiRateLimiter
    {
        // -----------------------------------------------------------------------------------------
        // DEFAULTS IN CODE, numbers overridable by configuration.
        //
        // The same reasoning as AiConsumerGrants: a limit that only exists in a settings file is a limit
        // that vanishes when the file is wrong, and there is no review attached to editing it. A
        // conservative bound therefore ships in the binary and configuration may only adjust it.
        //
        // 30/minute/company/provider/feature is chosen from the features that exist: an insight screen is
        // driven by a human pressing a button, and thirty presses a minute is already far past deliberate
        // use. It is not a throughput target — it is the point past which something is looping.
        // -----------------------------------------------------------------------------------------
        public const int DefaultMaxRequests = 30;
        public static readonly TimeSpan DefaultWindow = TimeSpan.FromMinutes(1);

        private const string ConfigRoot = "Ai:RateLimit";

        private readonly IConfiguration _config;
        private readonly ConcurrentDictionary<string, Bucket> _buckets = new(StringComparer.Ordinal);

        public AiRateLimiter(IConfiguration config) => _config = config;

        private sealed class Bucket
        {
            public readonly object Gate = new();
            public DateTime WindowStartUtc;
            public int Count;
        }

        /// Null when the key is absent or not a whole number. An unreadable value is treated as absent so
        /// a malformed setting falls through to the next level rather than throwing on a request path.
        private int? ReadInt(string path)
            => int.TryParse(_config[path], out var v) ? v : null;

        public AiRateLimitPolicy PolicyFor(AiRateLimitKey key)
        {
            // Most specific wins: provider+feature, then provider, then global, then the code default.
            var max = ReadInt($"{ConfigRoot}:{key.ProviderId}:{key.Feature}:MaxRequests")
                      ?? ReadInt($"{ConfigRoot}:{key.ProviderId}:MaxRequests")
                      ?? ReadInt($"{ConfigRoot}:MaxRequests")
                      ?? DefaultMaxRequests;

            var seconds = ReadInt($"{ConfigRoot}:{key.ProviderId}:{key.Feature}:WindowSeconds")
                          ?? ReadInt($"{ConfigRoot}:{key.ProviderId}:WindowSeconds")
                          ?? ReadInt($"{ConfigRoot}:WindowSeconds")
                          ?? (int)DefaultWindow.TotalSeconds;

            var policy = new AiRateLimitPolicy { MaxRequests = max, Window = TimeSpan.FromSeconds(seconds) };

            // A configured value that does not bound anything — zero, negative, absurd — is treated as a
            // MISCONFIGURATION and replaced by the code default. It is deliberately NOT honoured as
            // "unlimited": the one thing a rate limiter must never do is disappear because someone typed a
            // nought. Nor does it deny outright, which would take a working feature offline over a typo.
            return policy.IsUsable
                ? policy
                : new AiRateLimitPolicy { MaxRequests = DefaultMaxRequests, Window = DefaultWindow };
        }

        public AiRateLimitDecision TryAcquire(AiRateLimitKey key, DateTime nowUtc, CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(key);
            ct.ThrowIfCancellationRequested();

            // A request with no resolved company is not rate-limited — it is REFUSED. Bucketing it under
            // zero would give every unresolved caller in the system one shared budget, and the correct
            // answer to "which tenant is this?" being unknown is never "carry on".
            if (key.CompanyId <= 0)
                return AiRateLimitDecision.Deny(nowUtc, "rate:company-unresolved");

            var policy = PolicyFor(key);
            var bucket = _buckets.GetOrAdd(key.ToString(), _ => new Bucket { WindowStartUtc = nowUtc });

            lock (bucket.Gate)
            {
                if (nowUtc - bucket.WindowStartUtc >= policy.Window || nowUtc < bucket.WindowStartUtc)
                {
                    // Second condition matters: a clock that moved backwards must reset the window rather
                    // than leave a bucket that can never expire.
                    bucket.WindowStartUtc = nowUtc;
                    bucket.Count = 0;
                }

                var endsUtc = bucket.WindowStartUtc + policy.Window;

                if (bucket.Count >= policy.MaxRequests)
                    return AiRateLimitDecision.Deny(endsUtc, $"rate:exceeded:{policy.MaxRequests}/{(int)policy.Window.TotalSeconds}s");

                bucket.Count++;
                return AiRateLimitDecision.Allow(policy.MaxRequests - bucket.Count, endsUtc);
            }
        }
    }
}
