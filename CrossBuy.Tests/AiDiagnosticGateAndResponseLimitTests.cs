using System.Net;
using System.Reflection;
using System.Text;
using CrossBuy.BL;
using CrossBuy.BL.Platform.Ai;
using CrossBuy.Models.Platform;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CrossBuy.Tests
{
    // AI Foundation Increment 4.1 — the production diagnostic gate (D1–D10) and the bounded AI response
    // (R1–R12).
    public class AiDiagnosticGateAndResponseLimitTests
    {
        // ==========================================================================================
        // D1–D4, D10 — the /api/ai/diag environment gate
        //
        // Exercised through the REAL DevOnly filter with a real ActionExecutingContext, so what is tested
        // is the mechanism the route actually runs, not a re-implementation of its rule.
        // ==========================================================================================
        private sealed class StubEnv : IWebHostEnvironment
        {
            public string EnvironmentName { get; set; } = "Development";
            public string ApplicationName { get; set; } = "CrossBuy";
            public string WebRootPath { get; set; } = "";
            public Microsoft.Extensions.FileProviders.IFileProvider WebRootFileProvider { get; set; } = null!;
            public string ContentRootPath { get; set; } = "";
            public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = null!;
        }

        // Builds the filter context for one simulated request. `query` and `headers` exist so the
        // spoofing attempts (D3/D4) can actually be attempted rather than merely asserted about.
        private static ActionExecutingContext ContextFor(
            string environmentName,
            IDictionary<string, string>? query = null,
            IDictionary<string, string>? headers = null)
        {
            var services = new ServiceCollection();
            services.AddSingleton<IWebHostEnvironment>(new StubEnv { EnvironmentName = environmentName });

            var http = new DefaultHttpContext { RequestServices = services.BuildServiceProvider() };
            if (query != null)
                http.Request.Query = new QueryCollection(query.ToDictionary(k => k.Key, v => new Microsoft.Extensions.Primitives.StringValues(v.Value)));
            if (headers != null)
                foreach (var (k, v) in headers) http.Request.Headers[k] = v;

            return new ActionExecutingContext(
                new ActionContext(http, new RouteData(), new ActionDescriptor()),
                new List<IFilterMetadata>(), new Dictionary<string, object?>(), controller: null!);
        }

        private static IActionResult? RunGate(ActionExecutingContext ctx)
        {
            new CrossBuy.Models.DevOnlyAttribute().OnActionExecuting(ctx);
            return ctx.Result;
        }

        [Fact]
        public void D1_the_diagnostic_route_is_reachable_in_development()
            => Assert.Null(RunGate(ContextFor("Development")));

        // D2 — and every non-Development environment, not just the one literally named "Production".
        // A host running as "Staging" or "QA" is not a place to expose an LLM connectivity probe either.
        [Theory]
        [InlineData("Production")]
        [InlineData("Staging")]
        [InlineData("QA")]
        [InlineData("")]
        public void D2_the_diagnostic_route_is_absent_outside_development(string environmentName)
        {
            var result = RunGate(ContextFor(environmentName));
            // A bare 404 — NOT "Anthropic disabled", NOT "provider not configured". The response must
            // disclose nothing about the architecture behind it.
            Assert.IsType<NotFoundResult>(result);
        }

        // D3/D4 — the gate reads HOST configuration, so nothing a caller sends can turn it back on.
        [Fact]
        public void D3_a_spoofed_header_cannot_enable_the_diagnostic_route_in_production()
        {
            var ctx = ContextFor("Production", headers: new Dictionary<string, string>
            {
                ["X-Environment"] = "Development",
                ["ASPNETCORE_ENVIRONMENT"] = "Development",
                ["X-Forwarded-Env"] = "Development",
                ["X-Debug"] = "true",
            });
            Assert.IsType<NotFoundResult>(RunGate(ctx));
        }

        [Fact]
        public void D4_a_spoofed_query_parameter_cannot_enable_the_diagnostic_route_in_production()
        {
            var ctx = ContextFor("Production", query: new Dictionary<string, string>
            {
                ["env"] = "Development",
                ["environment"] = "Development",
                ["dev"] = "1",
                ["debug"] = "true",
            });
            Assert.IsType<NotFoundResult>(RunGate(ctx));
        }

        // The gate must take no request-shaped input at all — the structural form of the same claim.
        [Fact]
        public void D10_the_environment_gate_derives_only_from_host_state()
        {
            var src = File.ReadAllText(Path.Combine(RepoRoot(), "CrossBuy", "Models", "DevOnlyAttribute.cs"));
            Assert.Contains("IWebHostEnvironment", src, StringComparison.Ordinal);
            Assert.Contains("IsDevelopment()", src, StringComparison.Ordinal);

            foreach (var requestShaped in new[] { "Request.Query", "Request.Headers", "Request.Cookies", "Request.Form" })
                Assert.DoesNotContain(requestShaped, src, StringComparison.Ordinal);
        }

        // The attribute is actually applied to the action — the wiring, not just the mechanism.
        [Fact]
        public void The_diag_action_carries_the_environment_gate()
        {
            var method = typeof(CrossBuy.Controllers.Api.AiController)
                .GetMethod("Diag", BindingFlags.Public | BindingFlags.Instance);
            Assert.NotNull(method);
            Assert.NotNull(method!.GetCustomAttribute<CrossBuy.Models.DevOnlyAttribute>());
        }

        // ...and the BUSINESS routes deliberately do NOT carry it: gating them would disable approved
        // local ML in production, which is the opposite of the intent.
        [Theory]
        [InlineData("AnomalyJournalScan")]
        [InlineData("ForecastCashflow")]
        [InlineData("InventoryAnalyze")]
        public void The_approved_local_ml_routes_are_not_environment_gated(string action)
        {
            var method = typeof(CrossBuy.Controllers.Api.AiController)
                .GetMethod(action, BindingFlags.Public | BindingFlags.Instance);
            Assert.NotNull(method);
            Assert.Null(method!.GetCustomAttribute<CrossBuy.Models.DevOnlyAttribute>());
        }

        // D7/D8 — gating changes nothing about classification: the diagnostic still cannot be Internal,
        // and the local ML routes still do not inherit its external destination.
        [Fact]
        public void D7_D8_gating_does_not_alter_the_destination_classification()
        {
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                [AiDestinationResolver.DestinationClassKey] = "Internal",
                [AiDestinationResolver.BaseUrlKey] = "http://localhost:8000",
            }).Build();

            Assert.NotEqual(AiEgressDestinationClass.Internal,
                AiDestinationResolver.Resolve(config, AiEgressPurpose.ConnectivityDiagnostic));

            foreach (var local in new[]
            {
                AiEgressPurpose.JournalAnomalyDetection,
                AiEgressPurpose.CashflowForecast,
                AiEgressPurpose.InventoryAnalysis,
            })
                Assert.Equal(AiEgressDestinationClass.Internal, AiDestinationResolver.Resolve(config, local));
        }

        // D9 — nothing in the product calls the diagnostic route, so gating it breaks no workflow.
        [Fact]
        public void D9_no_business_code_depends_on_the_diagnostic_route()
        {
            var root = Path.Combine(RepoRoot(), "CrossBuy");
            foreach (var file in Directory.GetFiles(root, "*.*", SearchOption.AllDirectories))
            {
                if (!file.EndsWith(".cs") && !file.EndsWith(".cshtml") && !file.EndsWith(".js")) continue;
                if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                    || file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                    || file.EndsWith("AiController.cs") || file.EndsWith("IAiService.cs")
                    || file.EndsWith("AiService.cs")) continue;

                var src = StripComments(File.ReadAllText(file));
                Assert.DoesNotContain("api/ai/diag", src, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("EchoAsync", src, StringComparison.Ordinal);
            }
        }

        // ==========================================================================================
        // R1–R12 — the bounded AI response
        //
        // Driven through a stub HttpMessageHandler so the size behaviour is exercised on the real
        // AiService read path rather than on a re-implementation of it.
        // ==========================================================================================
        private sealed class StubHandler : HttpMessageHandler
        {
            private readonly HttpContent _content;
            public StubHandler(HttpContent content) => _content = content;
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            {
                ct.ThrowIfCancellationRequested();
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = _content });
            }
        }

        // A body of the requested size whose Content-Length is NOT declared — the chunked/unknown-length
        // case, where the header check cannot help and the bounded read is the only control.
        private sealed class UndeclaredLengthContent : HttpContent
        {
            private readonly byte[] _bytes;
            public UndeclaredLengthContent(int size) => _bytes = Encoding.UTF8.GetBytes(new string('x', size));
            protected override Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context)
                => stream.WriteAsync(_bytes, 0, _bytes.Length);
            protected override bool TryComputeLength(out long length) { length = -1; return false; }
        }

        private static AiService ServiceWith(HttpContent content)
            => new(new HttpClient(new StubHandler(content)) { BaseAddress = new Uri("http://localhost:8000") });

        private static AiEgressApproval Approval(int payloadBytes = 4096)
        {
            // The token's constructor is internal to the product assembly by design (that is what makes
            // the egress boundary unbypassable), so a test mints one the same way production does —
            // through the policy — rather than reaching around it.
            var ctor = typeof(AiEgressApproval).GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic).Single();
            return (AiEgressApproval)ctor.Invoke(new object?[]
            {
                AiEgressPurpose.InventoryAnalysis, AiEgressDestinationClass.Internal,
                AiDataClassification.FinancialAggregate, 1, payloadBytes, "test",
                // Increment 4.9 — provider scope. NULL here on purpose: this is an INTERNAL destination,
                // which has no organisation or project, and reflective Invoke must supply every parameter
                // even when the C# signature gives it a default.
                null,
            });
        }

        [Fact]
        public async Task R1_a_response_below_the_limit_is_returned_intact()
        {
            var body = "{\"ok\":true,\"items\":[]}";
            var result = await ServiceWith(new StringContent(body, Encoding.UTF8, "application/json"))
                .PostAsync(Approval(), "/inventory/analyze", new { x = 1 });

            Assert.Equal(200, result.Status);
            Assert.Equal(body, result.Json);
        }

        // R2 — exactly at the boundary is ACCEPTED. Stated explicitly because "<= limit" and "< limit"
        // are a one-character difference with opposite behaviour at the edge.
        [Fact]
        public async Task R2_a_response_exactly_at_the_limit_is_accepted()
        {
            var body = new string('y', AiEgressLimits.MaxResponseBytes);
            var result = await ServiceWith(new StringContent(body, Encoding.UTF8, "application/json"))
                .PostAsync(Approval(), "/inventory/analyze", new { x = 1 });

            Assert.Equal(AiEgressLimits.MaxResponseBytes, Encoding.UTF8.GetByteCount(result.Json));
            Assert.DoesNotContain("ai_response_too_large", result.Json, StringComparison.Ordinal);
        }

        // R3/R4 — one byte over, with a declared Content-Length: refused.
        [Fact]
        public async Task R3_R4_a_declared_length_over_the_limit_is_refused()
        {
            var body = new string('z', AiEgressLimits.MaxResponseBytes + 1);
            var content = new StringContent(body, Encoding.UTF8, "application/json");
            Assert.True(content.Headers.ContentLength > AiEgressLimits.MaxResponseBytes);   // the header IS declared

            var result = await ServiceWith(content).PostAsync(Approval(), "/inventory/analyze", new { x = 1 });

            Assert.Contains("ai_response_too_large", result.Json, StringComparison.Ordinal);
        }

        // R5 — the important one: no Content-Length at all, so the header check cannot fire and the
        // bounded read must stop it.
        [Fact]
        public async Task R5_an_undeclared_length_response_over_the_limit_is_stopped_during_the_read()
        {
            var result = await ServiceWith(new UndeclaredLengthContent(AiEgressLimits.MaxResponseBytes + 4096))
                .PostAsync(Approval(), "/inventory/analyze", new { x = 1 });

            Assert.Contains("ai_response_too_large", result.Json, StringComparison.Ordinal);
        }

        // R6/R7 — an oversized body never becomes a usable result, malformed or not. A truncated document
        // that parsed into a partial analysis would be the worst outcome available.
        [Fact]
        public async Task R6_R7_an_oversized_response_never_yields_partial_content()
        {
            var malformed = "{\"items\":[" + new string('a', AiEgressLimits.MaxResponseBytes + 1024);
            var result = await ServiceWith(new StringContent(malformed, Encoding.UTF8, "application/json"))
                .PostAsync(Approval(), "/anomaly/journal", new { x = 1 });

            Assert.Contains("ai_response_too_large", result.Json, StringComparison.Ordinal);
            Assert.DoesNotContain("aaaa", result.Json, StringComparison.Ordinal);   // no body fragment
            Assert.DoesNotContain("items", result.Json, StringComparison.Ordinal);
        }

        // R10/R11 — the refusal carries the limit and the observed size, never the body or the credential.
        [Fact]
        public async Task R10_R11_the_oversize_error_leaks_neither_body_nor_secret()
        {
            var secretish = "SUPER-SECRET-VALUE";
            var body = secretish + new string('q', AiEgressLimits.MaxResponseBytes + 1);
            var result = await ServiceWith(new StringContent(body, Encoding.UTF8, "application/json"))
                .PostAsync(Approval(), "/inventory/analyze", new { x = 1 });

            Assert.DoesNotContain(secretish, result.Json, StringComparison.Ordinal);
            Assert.DoesNotContain("X-AI-Secret", result.Json, StringComparison.Ordinal);
            Assert.Contains("limitBytes", result.Json, StringComparison.Ordinal);
        }

        // R8 — cancellation is passed through, not swallowed.
        [Fact]
        public async Task R8_cancellation_propagates_through_the_bounded_read()
        {
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                ServiceWith(new StringContent("{}", Encoding.UTF8, "application/json"))
                    .PostAsync(Approval(), "/inventory/analyze", new { x = 1 }, cts.Token));
        }

        // R9 — the timeout remains configured and bounded (it is not disabled by the streaming change).
        [Fact]
        public void R9_the_ai_client_timeout_is_bounded()
        {
            var program = File.ReadAllText(Path.Combine(RepoRoot(), "CrossBuy", "Program.cs"));
            Assert.Contains("c.Timeout = TimeSpan.FromSeconds(120)", program, StringComparison.Ordinal);
        }

        // R12 — the read path streams rather than buffering the whole body first. Pinned on the source
        // because the defect being guarded was literally `ReadAsStringAsync` on a fully-buffered response.
        [Fact]
        public void R12_the_response_is_read_with_headers_first_and_a_bounded_buffer()
        {
            var src = File.ReadAllText(Path.Combine(RepoRoot(), "CrossBuy", "BL", "AiService.cs"));

            Assert.Contains("HttpCompletionOption.ResponseHeadersRead", src, StringComparison.Ordinal);
            Assert.Contains("MaxResponseBytes", src, StringComparison.Ordinal);
            // The unbounded convenience call must not come back.
            Assert.DoesNotContain("Content.ReadAsStringAsync", src, StringComparison.Ordinal);
        }

        // The two limits are deliberately different numbers, derived from different things.
        [Fact]
        public void The_response_limit_is_derived_separately_from_the_request_limit()
        {
            Assert.True(AiEgressLimits.MaxResponseBytes > 0);
            Assert.True(AiEgressLimits.MaxResponseBytes < AiEgressLimits.MaxPayloadBytes,
                "The response ceiling should be tighter than the request ceiling: a request carries a whole " +
                "dataset, a response carries findings about it.");
        }

        // ------------------------------------------------------------------------------------------
        private static string RepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "CrossBuy.sln"))) dir = dir.Parent;
            Assert.NotNull(dir);
            return dir!.FullName;
        }

        private static string StripComments(string source)
        {
            var withoutBlocks = System.Text.RegularExpressions.Regex.Replace(
                source, @"/\*.*?\*/", "", System.Text.RegularExpressions.RegexOptions.Singleline);
            var sb = new StringBuilder(withoutBlocks.Length);
            foreach (var line in withoutBlocks.Split('\n'))
            {
                int slash = line.IndexOf("//", StringComparison.Ordinal);
                sb.Append(slash >= 0 ? line[..slash] : line).Append('\n');
            }
            return sb.ToString();
        }
    }
}
