using CrossBuy.BL.Platform.Ai;
using CrossBuy.Models.Platform;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace CrossBuy.Tests
{
    // INCREMENT 4.5 — the three controls that did not exist: RATE LIMIT, USAGE/COST CEILING,
    // PER-PROVIDER KILL SWITCH, plus the CIRCUIT BREAKER.
    //
    // Every AI control built before this increment answers "is this SAFE to send?". None of them answers
    // "is this the four-hundredth time in a minute?" or "has this tenant spent its budget?". A loop over a
    // large ledger passes classification, tenancy, permission and provider approval on every iteration.
    //
    // NO TEST HERE CONTACTS OPENAI. These are pure in-process controls with an injected clock.
    public class AiCostAndResilienceControlTests
    {
        private const int CompanyA = 1;
        private const int CompanyB = 65;
        private const string Provider = "OpenAI";

        private static IConfiguration Config(params (string, string?)[] pairs)
        {
            var d = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            foreach (var (k, v) in pairs) d[k] = v;
            return new ConfigurationBuilder().AddInMemoryCollection(d).Build();
        }

        private static readonly DateTime T0 = new(2026, 8, 16, 12, 0, 0, DateTimeKind.Utc);

        // =========================================================================================
        // RATE LIMITING
        // =========================================================================================

        [Fact]
        public void Rate_limit_permits_up_to_the_ceiling_then_denies()
        {
            var limiter = new AiRateLimiter(Config(("Ai:RateLimit:MaxRequests", "3"), ("Ai:RateLimit:WindowSeconds", "60")));
            var key = new AiRateLimitKey(CompanyA, Provider, AiEgressPurpose.CashflowForecast);

            for (var i = 0; i < 3; i++)
                Assert.True(limiter.TryAcquire(key, T0).Allowed, $"permit {i + 1} should be granted");

            var denied = limiter.TryAcquire(key, T0);
            Assert.False(denied.Allowed);
            Assert.Contains("rate:exceeded", denied.Reason, StringComparison.Ordinal);
        }

        [Fact]
        public void Rate_limit_window_rolls_over()
        {
            var limiter = new AiRateLimiter(Config(("Ai:RateLimit:MaxRequests", "1"), ("Ai:RateLimit:WindowSeconds", "60")));
            var key = new AiRateLimitKey(CompanyA, Provider, AiEgressPurpose.CashflowForecast);

            Assert.True(limiter.TryAcquire(key, T0).Allowed);
            Assert.False(limiter.TryAcquire(key, T0.AddSeconds(59)).Allowed);
            Assert.True(limiter.TryAcquire(key, T0.AddSeconds(61)).Allowed);
        }

        // A clock that steps backwards must reset the window, not create a bucket that can never expire.
        [Fact]
        public void Rate_limit_survives_a_backwards_clock()
        {
            var limiter = new AiRateLimiter(Config(("Ai:RateLimit:MaxRequests", "1")));
            var key = new AiRateLimitKey(CompanyA, Provider, AiEgressPurpose.CashflowForecast);

            Assert.True(limiter.TryAcquire(key, T0).Allowed);
            Assert.True(limiter.TryAcquire(key, T0.AddHours(-1)).Allowed);
        }

        // THE ISOLATION TEST. One tenant exhausting a shared bucket would be a denial of service against
        // every other tenant, delivered by our own cost control.
        [Fact]
        public void Rate_limit_of_one_company_does_not_leak_into_another()
        {
            var limiter = new AiRateLimiter(Config(("Ai:RateLimit:MaxRequests", "1")));

            var a = new AiRateLimitKey(CompanyA, Provider, AiEgressPurpose.CashflowForecast);
            var b = new AiRateLimitKey(CompanyB, Provider, AiEgressPurpose.CashflowForecast);

            Assert.True(limiter.TryAcquire(a, T0).Allowed);
            Assert.False(limiter.TryAcquire(a, T0).Allowed);
            Assert.True(limiter.TryAcquire(b, T0).Allowed);   // B is untouched by A's exhaustion
        }

        [Fact]
        public void Rate_limit_separates_features_and_providers()
        {
            var limiter = new AiRateLimiter(Config(("Ai:RateLimit:MaxRequests", "1")));

            Assert.True(limiter.TryAcquire(new AiRateLimitKey(CompanyA, Provider, AiEgressPurpose.CashflowForecast), T0).Allowed);
            Assert.True(limiter.TryAcquire(new AiRateLimitKey(CompanyA, Provider, AiEgressPurpose.InventoryAnalysis), T0).Allowed);
            Assert.True(limiter.TryAcquire(new AiRateLimitKey(CompanyA, "OtherProvider", AiEgressPurpose.CashflowForecast), T0).Allowed);
        }

        // An unresolved company is REFUSED, not bucketed under zero — which would give every unresolved
        // caller in the system one shared budget.
        [Fact]
        public void Rate_limit_refuses_an_unresolved_company()
        {
            var limiter = new AiRateLimiter(Config());
            var d = limiter.TryAcquire(new AiRateLimitKey(0, Provider, AiEgressPurpose.CashflowForecast), T0);

            Assert.False(d.Allowed);
            Assert.Equal("rate:company-unresolved", d.Reason);
        }

        // A limit of zero is a typo, not a grant of unlimited traffic. The one thing a rate limiter must
        // never do is disappear because someone typed a nought.
        [Theory]
        [InlineData("0")]
        [InlineData("-5")]
        [InlineData("not-a-number")]
        public void An_unusable_configured_limit_falls_back_to_the_code_default(string configured)
        {
            var limiter = new AiRateLimiter(Config(("Ai:RateLimit:MaxRequests", configured)));
            var policy = limiter.PolicyFor(new AiRateLimitKey(CompanyA, Provider, AiEgressPurpose.CashflowForecast));

            Assert.Equal(AiRateLimiter.DefaultMaxRequests, policy.MaxRequests);
            Assert.True(policy.IsUsable);
        }

        [Fact]
        public void With_no_configuration_a_conservative_default_still_applies()
        {
            var limiter = new AiRateLimiter(Config());
            var policy = limiter.PolicyFor(new AiRateLimitKey(CompanyA, Provider, AiEgressPurpose.CashflowForecast));

            Assert.Equal(AiRateLimiter.DefaultMaxRequests, policy.MaxRequests);
            Assert.Equal(AiRateLimiter.DefaultWindow, policy.Window);
        }

        [Fact]
        public void Rate_limit_honours_cancellation()
        {
            var limiter = new AiRateLimiter(Config());
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            Assert.Throws<OperationCanceledException>(() =>
                limiter.TryAcquire(new AiRateLimitKey(CompanyA, Provider, AiEgressPurpose.CashflowForecast), T0, cts.Token));
        }

        // =========================================================================================
        // USAGE / COST CEILING
        // =========================================================================================

        private static AiUsageGuard Guard(params (string, string?)[] pairs)
        {
            var cfg = Config(pairs);
            return new AiUsageGuard(cfg, new AiConfiguredPricingProvider(cfg));
        }

        [Fact]
        public void Usage_guard_denies_once_the_request_ceiling_is_reached()
        {
            var guard = Guard(("Ai:Usage:MaxRequestsPerWindow", "2"));
            var key = new AiUsageKey(CompanyA, Provider, AiEgressPurpose.CashflowForecast);

            Assert.True(guard.TryReserve(key, T0).Allowed);
            Assert.True(guard.TryReserve(key, T0).Allowed);

            var denied = guard.TryReserve(key, T0);
            Assert.False(denied.Allowed);
            Assert.Contains("usage:requests-exceeded", denied.Reason, StringComparison.Ordinal);
        }

        [Fact]
        public void Usage_guard_denies_once_the_token_ceiling_is_reached()
        {
            var guard = Guard(("Ai:Usage:MaxTotalTokensPerWindow", "1000"));
            var key = new AiUsageKey(CompanyA, Provider, AiEgressPurpose.CashflowForecast);

            Assert.True(guard.TryReserve(key, T0).Allowed);
            guard.RecordUsage(key, new AiProviderUsage
            {
                ProviderId = Provider, Model = "test-model", InputTokens = 600, OutputTokens = 500,
            }, T0);

            var denied = guard.TryReserve(key, T0);
            Assert.False(denied.Allowed);
            Assert.Contains("usage:tokens-exceeded", denied.Reason, StringComparison.Ordinal);
        }

        [Fact]
        public void Usage_of_one_company_does_not_count_against_another()
        {
            var guard = Guard(("Ai:Usage:MaxRequestsPerWindow", "1"));

            Assert.True(guard.TryReserve(new AiUsageKey(CompanyA, Provider, AiEgressPurpose.CashflowForecast), T0).Allowed);
            Assert.False(guard.TryReserve(new AiUsageKey(CompanyA, Provider, AiEgressPurpose.CashflowForecast), T0).Allowed);
            Assert.True(guard.TryReserve(new AiUsageKey(CompanyB, Provider, AiEgressPurpose.CashflowForecast), T0).Allowed);
        }

        [Fact]
        public void Usage_guard_refuses_an_unresolved_company()
        {
            var d = Guard().TryReserve(new AiUsageKey(0, Provider, AiEgressPurpose.CashflowForecast), T0);
            Assert.False(d.Allowed);
            Assert.Equal("usage:company-unresolved", d.Reason);
        }

        [Theory]
        [InlineData("0")]
        [InlineData("-1")]
        public void An_unusable_configured_ceiling_falls_back_to_the_code_default(string configured)
        {
            var ceiling = Guard(("Ai:Usage:MaxRequestsPerWindow", configured))
                .CeilingFor(new AiUsageKey(CompanyA, Provider, AiEgressPurpose.CashflowForecast));

            Assert.Equal(AiUsageGuard.DefaultMaxRequestsPerWindow, ceiling.MaxRequestsPerWindow);
        }

        // An UNPRICED model is unpriced, not free. A running total of 0.00 beside real token counts reads
        // as "this cost nothing", which is a different and wrong claim.
        [Fact]
        public void An_unpriced_model_reports_unknown_cost_rather_than_zero()
        {
            var guard = Guard();
            var key = new AiUsageKey(CompanyA, Provider, AiEgressPurpose.CashflowForecast);

            guard.TryReserve(key, T0);
            guard.RecordUsage(key, new AiProviderUsage
            {
                ProviderId = Provider, Model = "unpriced-model", InputTokens = 1000, OutputTokens = 500,
            }, T0);

            var snap = guard.Snapshot(key, T0);
            Assert.Null(snap.Cost);                 // unknown
            Assert.Equal(1000, snap.InputTokens);   // ...but the tokens are still counted and still bound
            Assert.Equal(500, snap.OutputTokens);
        }

        // Prices are configuration, never constants in domain code.
        [Fact]
        public void Cost_is_computed_from_configured_prices_only()
        {
            var cfg = Config(
                ("Ai:Pricing:OpenAI:test-model:InputPerMillion", "1.00"),
                ("Ai:Pricing:OpenAI:test-model:OutputPerMillion", "2.00"),
                ("Ai:Pricing:OpenAI:test-model:Currency", "USD"));

            var pricing = new AiConfiguredPricingProvider(cfg);
            var cost = pricing.EstimateCost("OpenAI", "test-model", 1_000_000, 500_000, out var currency);

            Assert.Equal(2.00m, cost);   // 1.00 + (0.5 × 2.00)
            Assert.Equal("USD", currency);

            // A model with no configured price yields null, not zero.
            Assert.Null(pricing.EstimateCost("OpenAI", "other-model", 1000, 1000, out _));
        }

        [Fact]
        public void No_provider_price_is_hard_coded_in_the_repository()
        {
            // Pricing must come from configuration. A price compiled into the tree looks authoritative and
            // is stale within weeks.
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "CrossBuy.sln"))) dir = dir.Parent;
            Assert.NotNull(dir);

            var src = File.ReadAllText(Path.Combine(dir!.FullName, "CrossBuy", "BL", "Platform", "Ai", "AiUsageGuard.cs"));
            var code = System.Text.RegularExpressions.Regex.Replace(src, @"//.*", "");

            Assert.DoesNotContain("PerMillion =", code, StringComparison.Ordinal);
            Assert.DoesNotContain("0.15m", code, StringComparison.Ordinal);
            Assert.DoesNotContain("0.60m", code, StringComparison.Ordinal);
        }

        // =========================================================================================
        // PROVIDER KILL SWITCH
        // =========================================================================================

        [Fact]
        public void A_provider_is_disabled_unless_explicitly_enabled()
        {
            var board = new AiProviderSwitchboard(Config());

            Assert.False(board.IsEnabled("OpenAI"));
            Assert.Contains("unconfigured", board.Explain("OpenAI"), StringComparison.Ordinal);
        }

        [Theory]
        [InlineData("false")]
        [InlineData("")]
        [InlineData("yes")]        // not a bool — unparseable is OFF, never ON
        [InlineData("1")]
        [InlineData(null)]
        public void Only_a_literal_true_enables_a_provider(string? configured)
            => Assert.False(new AiProviderSwitchboard(Config(("Ai:Providers:OpenAI:Enabled", configured))).IsEnabled("OpenAI"));

        [Fact]
        public void An_explicitly_enabled_provider_is_enabled()
        {
            var board = new AiProviderSwitchboard(Config(("Ai:Providers:OpenAI:Enabled", "true")));

            Assert.True(board.IsEnabled("OpenAI"));

            // ...and enabling one provider says nothing about any other.
            Assert.False(board.IsEnabled("Anthropic"));
        }

        // The switch is not approval, and the default is the opposite way round from the global switch:
        // AiService:EgressEnabled defaults TRUE (it guards a subsystem that already worked); an external
        // provider has never worked, so absent means off.
        [Fact]
        public void The_provider_switch_defaults_off_while_the_global_switch_defaults_on()
        {
            Assert.False(new AiProviderSwitchboard(Config()).IsEnabled("OpenAI"));
            Assert.True(Config().GetValue("AiService:EgressEnabled", true));
        }

        // =========================================================================================
        // CIRCUIT BREAKER
        // =========================================================================================

        [Fact]
        public void The_circuit_opens_after_the_failure_threshold()
        {
            var breaker = new AiCircuitBreaker(failureThreshold: 3, openDuration: TimeSpan.FromMinutes(1));

            for (var i = 0; i < 3; i++)
            {
                Assert.True(breaker.TryEnter(Provider, T0).Allowed);
                breaker.RecordFailure(Provider, T0, transient: true);
            }

            var blocked = breaker.TryEnter(Provider, T0);
            Assert.False(blocked.Allowed);
            Assert.Equal(AiCircuitState.Open, blocked.State);
        }

        // A wrong API key must not open the circuit: it would replace a precise "unauthorized" with an
        // opaque "circuit open" for the next minute, and no amount of waiting fixes a wrong credential.
        [Fact]
        public void A_non_transient_failure_never_opens_the_circuit()
        {
            var breaker = new AiCircuitBreaker(failureThreshold: 2, openDuration: TimeSpan.FromMinutes(1));

            for (var i = 0; i < 10; i++)
                breaker.RecordFailure(Provider, T0, transient: false);

            Assert.Equal(AiCircuitState.Closed, breaker.StateOf(Provider, T0));
            Assert.True(breaker.TryEnter(Provider, T0).Allowed);
        }

        [Fact]
        public void An_open_circuit_half_opens_after_the_cool_down_and_admits_exactly_one_probe()
        {
            var breaker = new AiCircuitBreaker(failureThreshold: 1, openDuration: TimeSpan.FromMinutes(1));

            breaker.TryEnter(Provider, T0);
            breaker.RecordFailure(Provider, T0, transient: true);
            Assert.False(breaker.TryEnter(Provider, T0).Allowed);

            var later = T0.AddMinutes(2);
            var probe = breaker.TryEnter(Provider, later);
            Assert.True(probe.Allowed);
            Assert.Equal(AiCircuitState.HalfOpen, probe.State);

            // THE RETRY-STORM GUARD. Without it every queued caller is admitted the instant the cool-down
            // elapses — the storm arriving on schedule.
            var second = breaker.TryEnter(Provider, later);
            Assert.False(second.Allowed);
            Assert.Contains("probe-in-flight", second.Reason, StringComparison.Ordinal);
        }

        [Fact]
        public void A_successful_probe_closes_the_circuit()
        {
            var breaker = new AiCircuitBreaker(failureThreshold: 1, openDuration: TimeSpan.FromMinutes(1));

            breaker.TryEnter(Provider, T0);
            breaker.RecordFailure(Provider, T0, transient: true);

            var later = T0.AddMinutes(2);
            Assert.True(breaker.TryEnter(Provider, later).Allowed);
            breaker.RecordSuccess(Provider, later);

            Assert.Equal(AiCircuitState.Closed, breaker.StateOf(Provider, later));
            Assert.True(breaker.TryEnter(Provider, later).Allowed);
        }

        [Fact]
        public void A_failed_probe_reopens_the_circuit_immediately()
        {
            var breaker = new AiCircuitBreaker(failureThreshold: 3, openDuration: TimeSpan.FromMinutes(1));

            for (var i = 0; i < 3; i++) { breaker.TryEnter(Provider, T0); breaker.RecordFailure(Provider, T0, transient: true); }

            var later = T0.AddMinutes(2);
            Assert.True(breaker.TryEnter(Provider, later).Allowed);          // the probe
            breaker.RecordFailure(Provider, later, transient: true);          // it fails

            // Straight back to Open on ONE failure — the threshold is not restarted from zero.
            Assert.Equal(AiCircuitState.Open, breaker.StateOf(Provider, later));
        }

        [Fact]
        public void Circuits_are_per_provider()
        {
            var breaker = new AiCircuitBreaker(failureThreshold: 1, openDuration: TimeSpan.FromMinutes(1));

            breaker.TryEnter("OpenAI", T0);
            breaker.RecordFailure("OpenAI", T0, transient: true);

            Assert.False(breaker.TryEnter("OpenAI", T0).Allowed);
            Assert.True(breaker.TryEnter("Anthropic", T0).Allowed);
        }
    }
}
