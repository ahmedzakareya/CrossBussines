using System.Text;
using CrossBuy.BL;
using CrossBuy.BL.Platform;
using CrossBuy.BL.Platform.Ai;
using CrossBuy.Models.Platform;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CrossBuy.Tests
{
    // INCREMENT 4.7 — CROSSBUY AGAINST THE **REAL** crossbuy_ai SERVICE.
    //
    // Increment 4.6 proved the CrossBuy leg over a loopback STUB. That proved the governance chain and the
    // socket; it did not prove the FastAPI app or the ML computation. This file closes that last gap by
    // driving the real service.
    //
    // ENVIRONMENT-GATED, NEVER SILENTLY PASSED — the same discipline SqlServerFixture uses for
    // CROSSBUY_TEST_SQL. These tests require a running crossbuy_ai, which CI does not have, so without
    // the opt-in they SKIP rather than pass. A test that quietly succeeds when its subject is absent is
    // worse than no test.
    //
    //     $env:CROSSBUY_TEST_PYTHON_AI = "1"
    //     $env:CROSSBUY_TEST_PYTHON_AI_SECRET = "<the AI_SHARED_SECRET from crossbuy_ai/.env>"
    //     # from crossbuy_ai:  .venv\Scripts\python -m uvicorn app.main:app --host 127.0.0.1 --port 8000
    //
    // THE SECRET COMES FROM THE ENVIRONMENT, never from a literal here. A shared secret committed to a
    // test file is a shared secret in the repository.
    //
    // NO EXTERNAL PROVIDER IS CONTACTED. Only 127.0.0.1. The three business routes import local ML only —
    // audited in Increment 4.7 Phase 8 and re-asserted here.
    public class AiRealPythonServiceE2ETests
    {
        private const int Company = 1;
        private const string BaseUrl = "http://127.0.0.1:8000";

        private static bool Enabled =>
            Environment.GetEnvironmentVariable("CROSSBUY_TEST_PYTHON_AI") == "1"
            && !string.IsNullOrWhiteSpace(Secret);

        private static string? Secret => Environment.GetEnvironmentVariable("CROSSBUY_TEST_PYTHON_AI_SECRET");

        private sealed class FixedContext : IBusinessContextAccessor
        {
            private readonly BusinessContext _c;
            public FixedContext(BusinessContext c) => _c = c;
            public Task<BusinessContext> GetCurrentAsync(CancellationToken ct = default) => Task.FromResult(_c);
            public Task<BusinessContext?> TryGetCurrentAsync(CancellationToken ct = default) => Task.FromResult<BusinessContext?>(_c);
        }

        /// The DEV configuration exactly as appsettings.json declares it.
        private static IConfiguration DevConfig() => new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                ["AiService:DestinationClass"] = "Internal",
                ["AiService:DeploymentMode"] = "LocalLoopback",
                ["AiService:BaseUrl"] = BaseUrl,
                ["AiService:Secret"] = Secret,
            }).Build();

        /// Runs the REAL policy with the SHIPPED, empty provider authority. An Internal destination never
        /// consults the provider governance record — which is precisely why local ML is independent of the
        /// OpenAI decision, and why this works while OpenAI remains denied.
        private static async Task<AiEgressApproval> ApproveAsync(IConfiguration config, object payload)
        {
            var destination = AiDestinationResolver.Resolve(config, AiEgressPurpose.CashflowForecast);
            Assert.Equal(AiEgressDestinationClass.Internal, destination);

            var policy = new AiEgressPolicy(
                new FixedContext(new BusinessContext
                {
                    CompanyId = Company, EmployeeId = 7, UserId = "u7", Source = BusinessContextSource.Http,
                }),
                config, NullLogger<AiEgressPolicy>.Instance, new AiProviderAuthority());

            var decision = await policy.EvaluateAsync(new AiEgressRequest
            {
                Purpose = AiEgressPurpose.CashflowForecast,
                Destination = destination,
                Classification = AiDataClassification.FinancialAggregate,
                DataCompanyId = Company,
                PayloadBytes = Encoding.UTF8.GetByteCount(System.Text.Json.JsonSerializer.Serialize(payload)),
            });

            Assert.True(decision.Allowed, decision.Reason?.ToString());
            return decision.Approval!;
        }

        private static AiService Client(IConfiguration config)
        {
            var http = new HttpClient { BaseAddress = new Uri(BaseUrl), Timeout = TimeSpan.FromSeconds(30) };
            http.DefaultRequestHeaders.Add("X-AI-Secret", config["AiService:Secret"]);
            return new AiService(http);
        }

        // =========================================================================================
        // CASHFLOW — the full chain, ending in real ML
        // =========================================================================================
        [SkippableFact]
        public async Task Cashflow_forecast_reaches_the_real_python_ml_and_returns_a_sane_projection()
        {
            Skip.IfNot(Enabled, "Set CROSSBUY_TEST_PYTHON_AI=1 and CROSSBUY_TEST_PYTHON_AI_SECRET, and run crossbuy_ai.");

            var config = DevConfig();

            // Synthetic, and shaped exactly like AiInsightsService's real outbound payload: opening cash
            // plus dated amounts. No counterparty, no free text, no identifier.
            var payload = new
            {
                openingCash = 125_000.50m,
                asOf = new DateTime(2026, 8, 16),
                horizonDays = 90,
                inflows = new[]
                {
                    new { date = new DateTime(2026, 9, 1), amount = 20_000m },
                    new { date = new DateTime(2026, 10, 1), amount = 15_000m },
                },
                outflows = new[]
                {
                    new { date = new DateTime(2026, 9, 5), amount = 8_000m },
                    new { date = new DateTime(2026, 9, 20), amount = 190_000m },
                },
            };

            var approval = await ApproveAsync(config, payload);
            var result = await Client(config).PostAsync(approval, "/forecast/cashflow", payload);

            Assert.Equal(200, result.Status);

            using var doc = System.Text.Json.JsonDocument.Parse(result.Json);
            var root = doc.RootElement;

            Assert.Equal(90, root.GetProperty("horizonDays").GetInt32());
            Assert.Equal(125_000.5, root.GetProperty("openingCash").GetDouble(), 3);

            // The arithmetic must actually be right: 125000.5 + 35000 - 198000 = -37999.5
            var end = root.GetProperty("projectedEndBalance").GetDouble();
            Assert.Equal(-37_999.5, end, 3);

            // ...and the trough must be BELOW the end, because the 190k outflow lands before the October
            // inflow. A projection that never dips below its own end balance has lost the ordering.
            var trough = root.GetProperty("minProjectedBalance").GetDouble();
            Assert.True(trough < end, $"trough {trough} should be below end {end}");

            Assert.True(root.GetProperty("negativeRisk").GetBoolean());

            // No NaN, no Infinity — the values that survive JSON and poison every downstream sum.
            foreach (var d in new[] { end, trough, root.GetProperty("totalExpectedInflow").GetDouble(),
                                      root.GetProperty("totalExpectedOutflow").GetDouble() })
            {
                Assert.False(double.IsNaN(d));
                Assert.False(double.IsInfinity(d));
            }

            // The horizon is respected rather than silently extended.
            var periods = root.GetProperty("periods");
            Assert.True(periods.GetArrayLength() > 0);
            Assert.True(periods.GetArrayLength() <= 14, $"90 days should not produce {periods.GetArrayLength()} weekly periods");
        }

        [SkippableFact]
        public async Task Inventory_analysis_reaches_the_real_python_ml()
        {
            Skip.IfNot(Enabled, "Set CROSSBUY_TEST_PYTHON_AI=1 and CROSSBUY_TEST_PYTHON_AI_SECRET, and run crossbuy_ai.");

            var config = DevConfig();
            var payload = new
            {
                asOf = new DateTime(2026, 8, 16),
                slowDays = 90,
                items = new[]
                {
                    new { itemId = 1, code = "ZZ-1", onHand = 100.0, value = 500.0, out90 = 90.0, out30 = 30.0 },
                    new { itemId = 2, code = "ZZ-2", onHand = 5000.0, value = 25000.0, out90 = 1.0, out30 = 0.0 },
                },
            };

            var approval = await ApproveAsync(config, payload);
            var result = await Client(config).PostAsync(approval, "/inventory/analyze", payload);

            Assert.Equal(200, result.Status);
            using var doc = System.Text.Json.JsonDocument.Parse(result.Json);
            Assert.Equal(2, doc.RootElement.GetProperty("itemsAnalyzed").GetInt32());
        }

        // JournalEntry.Description is DENIED to an external processor and always will be. It is NOT denied
        // to local loopback ML — the classification matrix permits FreeTextBusinessContent to Internal,
        // and that distinction is the whole reason local anomaly detection still works.
        [SkippableFact]
        public async Task Journal_anomaly_reaches_the_real_python_ml_and_flags_an_outlier()
        {
            Skip.IfNot(Enabled, "Set CROSSBUY_TEST_PYTHON_AI=1 and CROSSBUY_TEST_PYTHON_AI_SECRET, and run crossbuy_ai.");

            var config = DevConfig();

            // Enough entries for the detector to have a distribution: IsolationForest at contamination
            // 0.06 and the per-group z-score both need a real sample, so five rows legitimately flags
            // nothing. Dates and descriptions are varied so the DUPLICATE rule does not fire instead.
            var entries = Enumerable.Range(1, 40).Select(i => new
            {
                id = i,
                entryNo = $"ZZ-JV-{i}",
                date = new DateTime(2026, 8, 1).AddDays(i % 20),
                journalType = "JV",
                sourceType = "Manual",
                description = $"synthetic entry {i}",
                amount = 100.0 + i,
                lineCount = 2,
            }).ToList<object>();

            entries.Add(new
            {
                id = 999,
                entryNo = "ZZ-JV-999",
                date = new DateTime(2026, 8, 21),
                journalType = "JV",
                sourceType = "Manual",
                description = "synthetic extreme outlier",
                amount = 5_000_000.0,
                lineCount = 2,
            });

            var payload = new { entries };

            // Classified FreeTextBusinessContent BECAUSE the payload carries descriptions — and Internal
            // is the only destination that may receive it.
            var policy = new AiEgressPolicy(
                new FixedContext(new BusinessContext
                {
                    CompanyId = Company, EmployeeId = 7, UserId = "u7", Source = BusinessContextSource.Http,
                }),
                config, NullLogger<AiEgressPolicy>.Instance, new AiProviderAuthority());

            var decision = await policy.EvaluateAsync(new AiEgressRequest
            {
                Purpose = AiEgressPurpose.JournalAnomalyDetection,
                Destination = AiDestinationResolver.Resolve(config, AiEgressPurpose.JournalAnomalyDetection),
                Classification = AiDataClassification.FreeTextBusinessContent,
                DataCompanyId = Company,
                PayloadBytes = Encoding.UTF8.GetByteCount(System.Text.Json.JsonSerializer.Serialize(payload)),
            });

            Assert.True(decision.Allowed, decision.Reason?.ToString());

            var result = await Client(config).PostAsync(decision.Approval!, "/anomaly/journal", payload);
            Assert.Equal(200, result.Status);

            using var doc = System.Text.Json.JsonDocument.Parse(result.Json);
            Assert.Equal(41, doc.RootElement.GetProperty("scanned").GetInt32());
            Assert.True(doc.RootElement.GetProperty("summary").GetProperty("amountOutliers").GetInt32() >= 1,
                "the extreme outlier should be detected");
        }

        // The same free text that local ML may process must STILL be refused to an external processor.
        // Asserted here, beside the passing local case, so the two cannot drift apart.
        [SkippableFact]
        public async Task The_same_free_text_payload_is_refused_to_an_external_processor()
        {
            Skip.IfNot(Enabled, "Set CROSSBUY_TEST_PYTHON_AI=1 and CROSSBUY_TEST_PYTHON_AI_SECRET, and run crossbuy_ai.");

            var config = DevConfig();
            var policy = new AiEgressPolicy(
                new FixedContext(new BusinessContext
                {
                    CompanyId = Company, EmployeeId = 7, UserId = "u7", Source = BusinessContextSource.Http,
                }),
                config, NullLogger<AiEgressPolicy>.Instance, new AiProviderAuthority());

            var decision = await policy.EvaluateAsync(new AiEgressRequest
            {
                Purpose = AiEgressPurpose.JournalAnomalyDetection,
                Destination = AiEgressDestinationClass.ApprovedExternalProcessor,
                // Increment 4.9 — a COMPLETE scope, supplied so this test keeps measuring the rule in its
                // name. Without one the request is refused earlier, for want of a scope, and the refusal
                // would no longer be evidence that the provider is unapproved. The scope is a test
                // placeholder; the authority below is the REAL product record, which approves nobody.
                ProviderScope = AiTestProviderAuthority.OpenAiTestScope,
                Classification = AiDataClassification.FreeTextBusinessContent,
                DataCompanyId = Company,
                PayloadBytes = 1024,
            });

            Assert.False(decision.Allowed);
            Assert.Equal(AiEgressDenyReason.ProviderNotApproved, decision.Reason);
        }

        // The service must refuse an unauthenticated caller. Verified against the REAL service, because
        // the .NET side attaching a header proves nothing about whether the far end checks it.
        [SkippableFact]
        public async Task The_real_service_refuses_a_missing_or_wrong_shared_secret()
        {
            Skip.IfNot(Enabled, "Set CROSSBUY_TEST_PYTHON_AI=1 and CROSSBUY_TEST_PYTHON_AI_SECRET, and run crossbuy_ai.");

            using var noSecret = new HttpClient { BaseAddress = new Uri(BaseUrl), Timeout = TimeSpan.FromSeconds(15) };
            var a = await noSecret.GetAsync("/ping");
            Assert.Equal(System.Net.HttpStatusCode.Unauthorized, a.StatusCode);

            using var wrong = new HttpClient { BaseAddress = new Uri(BaseUrl), Timeout = TimeSpan.FromSeconds(15) };
            wrong.DefaultRequestHeaders.Add("X-AI-Secret", "definitely-not-the-secret");
            var b = await wrong.GetAsync("/ping");
            Assert.Equal(System.Net.HttpStatusCode.Unauthorized, b.StatusCode);

            // /health is deliberately open — it is a liveness probe, not a data route.
            using var open = new HttpClient { BaseAddress = new Uri(BaseUrl), Timeout = TimeSpan.FromSeconds(15) };
            Assert.Equal(System.Net.HttpStatusCode.OK, (await open.GetAsync("/health")).StatusCode);
        }

        // A malformed payload must be REJECTED by the contract, not coerced into a forecast. Pydantic
        // answers 422; the value of asserting it is that a future schema loosened to `Any` would start
        // silently accepting nonsense and returning a confident number.
        [SkippableFact]
        public async Task The_real_service_rejects_a_malformed_payload()
        {
            Skip.IfNot(Enabled, "Set CROSSBUY_TEST_PYTHON_AI=1 and CROSSBUY_TEST_PYTHON_AI_SECRET, and run crossbuy_ai.");

            using var http = new HttpClient { BaseAddress = new Uri(BaseUrl), Timeout = TimeSpan.FromSeconds(15) };
            http.DefaultRequestHeaders.Add("X-AI-Secret", Secret);

            var response = await http.PostAsync("/forecast/cashflow",
                new StringContent("{\"openingCash\":\"not-a-number\"}", Encoding.UTF8, "application/json"));

            Assert.Equal(System.Net.HttpStatusCode.UnprocessableEntity, response.StatusCode);
        }

        // STRUCTURAL, and it runs even when the service is not up: the three business ML modules must not
        // import a network client. This is the guarantee that "local" stays local.
        [Fact]
        public void The_local_ml_modules_import_no_network_client()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "CrossBuy.sln"))) dir = dir.Parent;
            Assert.NotNull(dir);

            var ml = Path.Combine(dir!.FullName, "crossbuy_ai", "app", "ml");
            Skip.IfNot(Directory.Exists(ml), "crossbuy_ai is not present in this checkout.");

            foreach (var file in Directory.GetFiles(ml, "*.py"))
            {
                var code = File.ReadAllText(file);
                foreach (var forbidden in new[] { "anthropic", "openai", "httpx", "requests", "urllib", "http.client" })
                    Assert.False(code.Contains(forbidden, StringComparison.OrdinalIgnoreCase),
                        $"{Path.GetFileName(file)} imports {forbidden} — local ML must make no network call.");
            }
        }
    }
}
