using System.Net;
using System.Text;
using CrossBuy.BL.Platform;
using CrossBuy.BL.Platform.Ai;
using CrossBuy.Models.Platform;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CrossBuy.Tests
{
    // INCREMENT 4.5, Phase 7 — THE INERT OPENAI ADAPTER.
    //
    // NO TEST IN THIS FILE CONTACTS OPENAI. Every one runs against a counting fake transport, and the
    // count is itself the assertion: the most important property of this adapter is not what it sends but
    // that it sends NOTHING when any gate refuses.
    //
    // "Inert" is not a flag. SendAsync demands an AiEgressApproval, whose constructor is internal to the
    // product assembly and is only ever called by AiEgressPolicy. These tests obtain one the only way
    // anything can — by running the real policy with a genuinely approved test authority — which is why
    // they exercise the real boundary rather than a mock of it.
    public class OpenAiProviderAdapterTests
    {
        private const int CompanyA = 1;
        private const int CompanyB = 65;

        // -----------------------------------------------------------------------------------------
        // Fake transport. Counts every attempt, so "zero external requests" is provable rather than
        // asserted.
        // -----------------------------------------------------------------------------------------
        private sealed class CountingHandler : HttpMessageHandler
        {
            public int Calls;
            public HttpRequestMessage? Last;
            public string? LastBody;

            public Func<HttpRequestMessage, HttpResponseMessage>? Respond;
            public Exception? Throw;
            public TimeSpan Delay = TimeSpan.Zero;

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            {
                Interlocked.Increment(ref Calls);
                Last = request;
                if (request.Content is not null) LastBody = await request.Content.ReadAsStringAsync(ct);

                if (Delay > TimeSpan.Zero) await Task.Delay(Delay, ct);
                if (Throw is not null) throw Throw;

                return Respond?.Invoke(request) ?? Json(HttpStatusCode.OK, Ok());
            }

            public static HttpResponseMessage Json(HttpStatusCode code, string body)
                => new(code) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

            public static string Ok(string content = "{\"summary\":\"ok\",\"riskLevel\":\"low\"}", int inTok = 100, int outTok = 50)
                => "{\"model\":\"test-model\",\"choices\":[{\"message\":{\"content\":" +
                   System.Text.Json.JsonSerializer.Serialize(content) +
                   "}}],\"usage\":{\"prompt_tokens\":" + inTok + ",\"completion_tokens\":" + outTok + "}}";
        }

        private sealed class SingleClientFactory : IHttpClientFactory
        {
            private readonly HttpMessageHandler _handler;
            public SingleClientFactory(HttpMessageHandler h) => _handler = h;
            public HttpClient CreateClient(string name) => new(_handler, disposeHandler: false);
        }

        private sealed class FixedContext : IBusinessContextAccessor
        {
            private readonly BusinessContext? _c;
            public FixedContext(BusinessContext? c) => _c = c;
            public Task<BusinessContext> GetCurrentAsync(CancellationToken ct = default) => Task.FromResult(_c!);
            public Task<BusinessContext?> TryGetCurrentAsync(CancellationToken ct = default) => Task.FromResult(_c);
        }

        private static IConfiguration Config(params (string, string?)[] overrides)
        {
            var d = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                ["AiService:DestinationClass"] = "ApprovedExternalProcessor",
                ["AiService:DeploymentMode"] = "LocalLoopback",
                ["AiService:BaseUrl"] = "http://localhost:8000",
                ["AiService:Secret"] = "a-real-looking-configured-value",

                ["Ai:Providers:OpenAI:Enabled"] = "true",
                // Increment 4.9 - the switch is now per environment as well as per provider.
                ["Ai:Providers:OpenAI:Development:Enabled"] = "true",
                // ...and the adapter's call scope comes from configuration, which must match the scope
                // the approval was minted for. A mismatch is refused, which is the point.
                ["OpenAi:OrganizationId"] = AiTestProviderAuthority.TestOrganizationId,
                ["OpenAi:ProjectId"] = AiTestProviderAuthority.TestProjectId,
                ["OpenAi:Environment"] = "Development",
                ["OPENAI_API_KEY"] = "test-key-not-a-real-credential",
                ["OpenAi:Model"] = "test-model",
                ["OpenAi:TimeoutSeconds"] = "5",
            };
            foreach (var (k, v) in overrides) d[k] = v;
            return new ConfigurationBuilder().AddInMemoryCollection(d).Build();
        }

        /// <summary>
        /// Mints a REAL approval by running the REAL policy with a genuinely-approved test authority.
        /// </summary>
        /// <remarks>
        /// Never hand-constructed: the constructor is internal to the product assembly and cannot be
        /// called from here at all. That is the point — an approval in a test is as hard to obtain as one
        /// in production, minus the governance record.
        /// </remarks>
        private static async Task<AiEgressApproval> MintApprovalAsync(
            IConfiguration config, int company = CompanyA,
            AiDataClassification classification = AiDataClassification.FinancialAggregate,
            int payloadBytes = 512)
        {
            var policy = new AiEgressPolicy(
                new FixedContext(new BusinessContext
                {
                    CompanyId = company, EmployeeId = 7, UserId = "u7", Source = BusinessContextSource.Http,
                }),
                config, NullLogger<AiEgressPolicy>.Instance, AiTestProviderAuthority.ApprovedFor(AiTestProviderAuthority.OpenAiTestScope));

            var decision = await policy.EvaluateAsync(new AiEgressRequest
            {
                Purpose = AiEgressPurpose.CashflowForecast,
                Destination = AiEgressDestinationClass.ApprovedExternalProcessor,
                ProviderScope = AiTestProviderAuthority.OpenAiTestScope,
                Classification = classification,
                DataCompanyId = company,
                PayloadBytes = payloadBytes,
            });

            Assert.True(decision.Allowed, decision.Reason?.ToString());
            return decision.Approval!;
        }

        private static (OpenAiProviderAdapter Adapter, CountingHandler Handler) Build(IConfiguration config)
        {
            var handler = new CountingHandler();
            var cfg = config;

            var adapter = new OpenAiProviderAdapter(
                new SingleClientFactory(handler),
                cfg,
                new AiProviderSwitchboard(cfg),
                new AiCircuitBreaker(),
                new AiRateLimiter(cfg),
                new AiUsageGuard(cfg, new AiConfiguredPricingProvider(cfg)),
                new LoggingAiEgressAuditSink(NullLogger<LoggingAiEgressAuditSink>.Instance),
                NullLogger<OpenAiProviderAdapter>.Instance);

            return (adapter, handler);
        }

        private static AiProviderRequest Request(AiEgressApproval approval, string payload = "{\"openingCash\":100}")
            => new()
            {
                Approval = approval,
                SystemPrompt = AiCashflowForecastValidator.SystemPrompt,
                PayloadJson = payload,
                MaxOutputTokens = 200,
                CorrelationId = "test-correlation",
            };

        // =========================================================================================
        // THE HEADLINE: OpenAI is unapproved, so the real pipeline never produces an approval at all.
        // =========================================================================================

        [Fact]
        public async Task The_shipped_governance_record_refuses_to_mint_an_approval_for_openai()
        {
            var config = Config();

            // The PRODUCT authority — not the test one — is what a running CrossBuy uses.
            var policy = new AiEgressPolicy(
                new FixedContext(new BusinessContext
                {
                    CompanyId = CompanyA, EmployeeId = 7, UserId = "u7", Source = BusinessContextSource.Http,
                }),
                config, NullLogger<AiEgressPolicy>.Instance, new AiProviderAuthority());

            var decision = await policy.EvaluateAsync(new AiEgressRequest
            {
                Purpose = AiEgressPurpose.CashflowForecast,
                Destination = AiEgressDestinationClass.ApprovedExternalProcessor,
                ProviderScope = AiTestProviderAuthority.OpenAiTestScope,
                Classification = AiDataClassification.FinancialAggregate,
                DataCompanyId = CompanyA,
                PayloadBytes = 512,
            });

            Assert.False(decision.Allowed);
            Assert.Equal(AiEgressDenyReason.ProviderNotApproved, decision.Reason);
            Assert.Null(decision.Approval);

            // No approval exists, so the adapter cannot even be invoked. That is the inertness.
            Assert.False(new AiProviderAuthority().Assess(DateTime.UtcNow, AiTestProviderAuthority.TestScope).IsApproved);
        }

        // =========================================================================================
        // ZERO EXTERNAL REQUESTS whenever a gate refuses.
        // =========================================================================================

        [Fact]
        public async Task A_disabled_provider_produces_zero_http_requests()
        {
            var config = Config(("Ai:Providers:OpenAI:Enabled", "false"));
            var approval = await MintApprovalAsync(config);
            var (adapter, handler) = Build(config);

            var result = await adapter.SendAsync(Request(approval));

            Assert.Equal(AiProviderOutcome.Refused, result.Outcome);
            Assert.Equal(0, handler.Calls);
        }

        [Fact]
        public async Task An_unconfigured_provider_switch_produces_zero_http_requests()
        {
            var config = Config(("Ai:Providers:OpenAI:Enabled", null));
            var approval = await MintApprovalAsync(config);
            var (adapter, handler) = Build(config);

            Assert.Equal(AiProviderOutcome.Refused, (await adapter.SendAsync(Request(approval))).Outcome);
            Assert.Equal(0, handler.Calls);
        }

        // A missing credential must refuse LOCALLY. Sending unauthenticated would put the payload on the
        // wire and have it rejected at the far end — by which point the data has left.
        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("__SET_ON_SERVER__")]
        public async Task A_missing_or_placeholder_api_key_produces_zero_http_requests(string? key)
        {
            var config = Config(("OPENAI_API_KEY", key));
            var approval = await MintApprovalAsync(config);
            var (adapter, handler) = Build(config);

            var result = await adapter.SendAsync(Request(approval));

            Assert.Equal(AiProviderOutcome.Refused, result.Outcome);
            Assert.Equal("credential:missing", result.Reason);
            Assert.Equal(0, handler.Calls);
        }

        [Fact]
        public async Task An_exhausted_rate_limit_produces_zero_http_requests()
        {
            var config = Config(("Ai:RateLimit:MaxRequests", "1"));
            var approval = await MintApprovalAsync(config);
            var (adapter, handler) = Build(config);

            Assert.True((await adapter.SendAsync(Request(approval))).Succeeded);
            Assert.Equal(1, handler.Calls);

            var second = await adapter.SendAsync(Request(approval));
            Assert.Equal(AiProviderOutcome.Refused, second.Outcome);
            Assert.Contains("rate:exceeded", second.Reason, StringComparison.Ordinal);
            Assert.Equal(1, handler.Calls);   // unchanged
        }

        [Fact]
        public async Task An_exhausted_usage_ceiling_produces_zero_http_requests()
        {
            var config = Config(("Ai:Usage:MaxRequestsPerWindow", "1"));
            var approval = await MintApprovalAsync(config);
            var (adapter, handler) = Build(config);

            Assert.True((await adapter.SendAsync(Request(approval))).Succeeded);
            var second = await adapter.SendAsync(Request(approval));

            Assert.Contains("usage:", second.Reason, StringComparison.Ordinal);
            Assert.Equal(1, handler.Calls);
        }

        [Fact]
        public async Task An_open_circuit_produces_zero_http_requests()
        {
            var config = Config();
            var approval = await MintApprovalAsync(config);
            var handler = new CountingHandler
            {
                Respond = _ => CountingHandler.Json(HttpStatusCode.ServiceUnavailable, "{}"),
            };

            var adapter = new OpenAiProviderAdapter(
                new SingleClientFactory(handler), config, new AiProviderSwitchboard(config),
                new AiCircuitBreaker(failureThreshold: 2, openDuration: TimeSpan.FromMinutes(5)),
                new AiRateLimiter(config), new AiUsageGuard(config, new AiConfiguredPricingProvider(config)),
                new LoggingAiEgressAuditSink(NullLogger<LoggingAiEgressAuditSink>.Instance),
                NullLogger<OpenAiProviderAdapter>.Instance);

            await adapter.SendAsync(Request(approval));
            await adapter.SendAsync(Request(approval));
            Assert.Equal(2, handler.Calls);

            var blocked = await adapter.SendAsync(Request(approval));
            Assert.Equal(AiProviderOutcome.Refused, blocked.Outcome);
            Assert.Equal("circuit:open", blocked.Reason);
            Assert.Equal(2, handler.Calls);   // the circuit spared a third call
        }

        // =========================================================================================
        // THE APPROVAL MUST MATCH THE REQUEST. A valid token replayed for different data is a wrong send
        // with an authentic token.
        // =========================================================================================

        [Fact]
        public async Task An_approval_minted_for_an_internal_destination_cannot_drive_an_external_send()
        {
            var config = Config(("AiService:DestinationClass", "Internal"));

            var policy = new AiEgressPolicy(
                new FixedContext(new BusinessContext
                {
                    CompanyId = CompanyA, EmployeeId = 7, UserId = "u7", Source = BusinessContextSource.Http,
                }),
                config, NullLogger<AiEgressPolicy>.Instance, AiTestProviderAuthority.ApprovedFor(AiTestProviderAuthority.OpenAiTestScope));

            var internalApproval = (await policy.EvaluateAsync(new AiEgressRequest
            {
                Purpose = AiEgressPurpose.CashflowForecast,
                Destination = AiEgressDestinationClass.Internal,
                Classification = AiDataClassification.FinancialAggregate,
                DataCompanyId = CompanyA,
                PayloadBytes = 512,
            })).Approval!;

            var (adapter, handler) = Build(config);
            var result = await adapter.SendAsync(Request(internalApproval));

            Assert.Equal("approval:not-for-external-processor", result.Reason);
            Assert.Equal(0, handler.Calls);
        }

        [Fact]
        public async Task A_payload_that_grew_after_approval_is_refused()
        {
            var config = Config();
            var approval = await MintApprovalAsync(config, payloadBytes: 16);   // approved for 16 bytes
            var (adapter, handler) = Build(config);

            var oversized = new string('x', 500);
            var result = await adapter.SendAsync(Request(approval, $"{{\"pad\":\"{oversized}\"}}"));

            Assert.Equal("approval:payload-grew-after-approval", result.Reason);
            Assert.Equal(0, handler.Calls);
        }

        [Fact]
        public async Task An_empty_payload_is_refused()
        {
            var config = Config();
            var approval = await MintApprovalAsync(config);
            var (adapter, handler) = Build(config);

            Assert.Equal("request:empty-payload", (await adapter.SendAsync(Request(approval, "  "))).Reason);
            Assert.Equal(0, handler.Calls);
        }

        // Company isolation survives into the adapter. The approval carries the company the policy
        // verified; the adapter refuses one that carries none.
        [Fact]
        public async Task The_approval_carries_the_company_the_policy_verified()
        {
            var config = Config();

            var a = await MintApprovalAsync(config, company: CompanyA);
            var b = await MintApprovalAsync(config, company: CompanyB);

            Assert.Equal(CompanyA, a.CompanyId);
            Assert.Equal(CompanyB, b.CompanyId);

            // ...and rate/usage buckets follow the approval's company, so one tenant cannot exhaust
            // another's budget through the adapter.
            var config1 = Config(("Ai:RateLimit:MaxRequests", "1"));
            var approvalA = await MintApprovalAsync(config1, company: CompanyA);
            var approvalB = await MintApprovalAsync(config1, company: CompanyB);
            var (adapter, handler) = Build(config1);

            Assert.True((await adapter.SendAsync(Request(approvalA))).Succeeded);
            Assert.Equal(AiProviderOutcome.Refused, (await adapter.SendAsync(Request(approvalA))).Outcome);
            Assert.True((await adapter.SendAsync(Request(approvalB))).Succeeded);   // B unaffected
            Assert.Equal(2, handler.Calls);
        }

        // =========================================================================================
        // PROVIDER FAILURES
        // =========================================================================================

        [Theory]
        [InlineData(HttpStatusCode.Unauthorized, "provider-error:401")]
        [InlineData(HttpStatusCode.TooManyRequests, "provider-error:429")]
        [InlineData(HttpStatusCode.InternalServerError, "provider-error:500")]
        public async Task A_provider_error_is_reported_as_a_category_and_never_as_a_body(
            HttpStatusCode code, string expected)
        {
            var config = Config();
            var approval = await MintApprovalAsync(config);
            var handler = new CountingHandler
            {
                // A provider error body can quote the request back. It must never reach the reason.
                Respond = _ => CountingHandler.Json(code, "{\"error\":{\"message\":\"SENSITIVE-ECHO-OF-REQUEST\"}}"),
            };

            var adapter = new OpenAiProviderAdapter(
                new SingleClientFactory(handler), config, new AiProviderSwitchboard(config),
                new AiCircuitBreaker(), new AiRateLimiter(config),
                new AiUsageGuard(config, new AiConfiguredPricingProvider(config)),
                new LoggingAiEgressAuditSink(NullLogger<LoggingAiEgressAuditSink>.Instance),
                NullLogger<OpenAiProviderAdapter>.Instance);

            var result = await adapter.SendAsync(Request(approval));

            Assert.Equal(AiProviderOutcome.ProviderError, result.Outcome);
            Assert.Equal(expected, result.Reason);
            Assert.DoesNotContain("SENSITIVE-ECHO-OF-REQUEST", result.Reason, StringComparison.Ordinal);
            Assert.Null(result.Content);
        }

        [Fact]
        public async Task A_transport_failure_is_handled_safely()
        {
            var config = Config();
            var approval = await MintApprovalAsync(config);
            var handler = new CountingHandler { Throw = new HttpRequestException("connection refused to https://api.openai.com") };

            var adapter = new OpenAiProviderAdapter(
                new SingleClientFactory(handler), config, new AiProviderSwitchboard(config),
                new AiCircuitBreaker(), new AiRateLimiter(config),
                new AiUsageGuard(config, new AiConfiguredPricingProvider(config)),
                new LoggingAiEgressAuditSink(NullLogger<LoggingAiEgressAuditSink>.Instance),
                NullLogger<OpenAiProviderAdapter>.Instance);

            var result = await adapter.SendAsync(Request(approval));

            Assert.Equal(AiProviderOutcome.ProviderError, result.Outcome);
            Assert.Equal("transport-error", result.Reason);   // the exception message is not surfaced
        }

        [Fact]
        public async Task A_timeout_is_handled_safely()
        {
            var config = Config(("OpenAi:TimeoutSeconds", "1"));
            var approval = await MintApprovalAsync(config);
            var handler = new CountingHandler { Delay = TimeSpan.FromSeconds(10) };

            var adapter = new OpenAiProviderAdapter(
                new SingleClientFactory(handler), config, new AiProviderSwitchboard(config),
                new AiCircuitBreaker(), new AiRateLimiter(config),
                new AiUsageGuard(config, new AiConfiguredPricingProvider(config)),
                new LoggingAiEgressAuditSink(NullLogger<LoggingAiEgressAuditSink>.Instance),
                NullLogger<OpenAiProviderAdapter>.Instance);

            var result = await adapter.SendAsync(Request(approval));

            Assert.Equal(AiProviderOutcome.Timeout, result.Outcome);
            Assert.Contains("timeout:", result.Reason, StringComparison.Ordinal);
        }

        // Caller cancellation is distinguished from our own timeout: the user navigating away is not a
        // provider fault and must not move the circuit breaker.
        [Fact]
        public async Task Caller_cancellation_is_reported_as_cancelled_not_as_a_provider_failure()
        {
            var config = Config();
            var approval = await MintApprovalAsync(config);
            var handler = new CountingHandler { Delay = TimeSpan.FromSeconds(10) };
            var breaker = new AiCircuitBreaker(failureThreshold: 1, openDuration: TimeSpan.FromMinutes(5));

            var adapter = new OpenAiProviderAdapter(
                new SingleClientFactory(handler), config, new AiProviderSwitchboard(config),
                breaker, new AiRateLimiter(config),
                new AiUsageGuard(config, new AiConfiguredPricingProvider(config)),
                new LoggingAiEgressAuditSink(NullLogger<LoggingAiEgressAuditSink>.Instance),
                NullLogger<OpenAiProviderAdapter>.Instance);

            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
            var result = await adapter.SendAsync(Request(approval), cts.Token);

            Assert.Equal(AiProviderOutcome.Cancelled, result.Outcome);
            Assert.Equal(AiCircuitState.Closed, breaker.StateOf("OpenAI", DateTime.UtcNow));
        }

        // =========================================================================================
        // RESPONSE HANDLING — untrusted external input
        // =========================================================================================

        [Theory]
        [InlineData("not json at all")]
        [InlineData("{}")]
        [InlineData("{\"choices\":[]}")]
        [InlineData("{\"choices\":[{\"message\":{}}]}")]
        [InlineData("{\"choices\":[{\"message\":{\"content\":\"\"}}]}")]
        [InlineData("{\"choices\":[{\"message\":{\"content\":123}}]}")]
        public async Task A_malformed_provider_response_fails_safely(string body)
        {
            var config = Config();
            var approval = await MintApprovalAsync(config);
            var handler = new CountingHandler { Respond = _ => CountingHandler.Json(HttpStatusCode.OK, body) };

            var adapter = new OpenAiProviderAdapter(
                new SingleClientFactory(handler), config, new AiProviderSwitchboard(config),
                new AiCircuitBreaker(), new AiRateLimiter(config),
                new AiUsageGuard(config, new AiConfiguredPricingProvider(config)),
                new LoggingAiEgressAuditSink(NullLogger<LoggingAiEgressAuditSink>.Instance),
                NullLogger<OpenAiProviderAdapter>.Instance);

            var result = await adapter.SendAsync(Request(approval));

            Assert.Equal(AiProviderOutcome.InvalidResponse, result.Outcome);
            Assert.Null(result.Content);
        }

        [Fact]
        public async Task An_oversized_response_is_refused_rather_than_truncated()
        {
            var config = Config();
            var approval = await MintApprovalAsync(config);
            var huge = new string('x', AiEgressLimits.MaxResponseBytes + 1024);
            var handler = new CountingHandler { Respond = _ => CountingHandler.Json(HttpStatusCode.OK, CountingHandler.Ok(huge)) };

            var adapter = new OpenAiProviderAdapter(
                new SingleClientFactory(handler), config, new AiProviderSwitchboard(config),
                new AiCircuitBreaker(), new AiRateLimiter(config),
                new AiUsageGuard(config, new AiConfiguredPricingProvider(config)),
                new LoggingAiEgressAuditSink(NullLogger<LoggingAiEgressAuditSink>.Instance),
                NullLogger<OpenAiProviderAdapter>.Instance);

            var result = await adapter.SendAsync(Request(approval));

            Assert.Equal(AiProviderOutcome.InvalidResponse, result.Outcome);
            Assert.Equal("response:too-large", result.Reason);
            Assert.Null(result.Content);   // never a partial body
        }

        [Fact]
        public async Task A_valid_response_returns_content_and_usage()
        {
            var config = Config();
            var approval = await MintApprovalAsync(config);
            var (adapter, handler) = Build(config);

            var result = await adapter.SendAsync(Request(approval));

            Assert.True(result.Succeeded);
            Assert.Equal(1, handler.Calls);
            Assert.NotNull(result.Usage);
            Assert.Equal(100, result.Usage!.InputTokens);
            Assert.Equal(50, result.Usage.OutputTokens);
            Assert.Equal(150, result.Usage.TotalTokens);
        }

        // =========================================================================================
        // WHAT GOES ON THE WIRE
        // =========================================================================================

        [Fact]
        public async Task The_credential_travels_only_as_a_per_request_header_and_never_in_the_body()
        {
            var config = Config();
            var approval = await MintApprovalAsync(config);
            var (adapter, handler) = Build(config);

            await adapter.SendAsync(Request(approval));

            Assert.Equal("Bearer", handler.Last!.Headers.Authorization!.Scheme);
            Assert.DoesNotContain("test-key-not-a-real-credential", handler.LastBody!, StringComparison.Ordinal);
        }

        [Fact]
        public async Task The_request_body_pins_store_false_and_deterministic_output()
        {
            var config = Config();
            var approval = await MintApprovalAsync(config);
            var (adapter, handler) = Build(config);

            await adapter.SendAsync(Request(approval));

            // The recorded owner policy is zero retention. `store:false` is belt-and-braces against a
            // default that points the other way.
            Assert.Contains("\"store\":false", handler.LastBody!, StringComparison.Ordinal);
            Assert.Contains("\"temperature\":0", handler.LastBody!, StringComparison.Ordinal);
        }

        [Fact]
        public async Task The_output_token_ceiling_is_the_smaller_of_request_and_configuration()
        {
            var config = Config(("OpenAi:MaxOutputTokens", "50"));
            var approval = await MintApprovalAsync(config);
            var (adapter, handler) = Build(config);

            await adapter.SendAsync(Request(approval));   // request asks for 200; config caps at 50

            Assert.Contains("\"max_completion_tokens\":50", handler.LastBody!, StringComparison.Ordinal);
        }

        // =========================================================================================
        // The adapter cannot approve itself.
        // =========================================================================================

        [Fact]
        public void The_adapter_source_contains_no_approval_decision()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "CrossBuy.sln"))) dir = dir.Parent;
            Assert.NotNull(dir);

            var src = File.ReadAllText(Path.Combine(
                dir!.FullName, "CrossBuy", "BL", "Platform", "Ai", "OpenAiProviderAdapter.cs"));
            var code = System.Text.RegularExpressions.Regex.Replace(src, @"//.*", "");

            // It may not construct an approval, mutate the governance record, or consult the authority to
            // second-guess it. It consumes an approval; it never produces one.
            Assert.DoesNotContain("new AiEgressApproval", code, StringComparison.Ordinal);
            Assert.DoesNotContain("AiProviderAuthority", code, StringComparison.Ordinal);
            Assert.DoesNotContain("ApprovedExternalProcessor =", code, StringComparison.Ordinal);
        }
    }
}
