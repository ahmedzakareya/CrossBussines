using CrossBuy.BL.Platform.Ai;
using CrossBuy.Models.Platform;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace CrossBuy.Tests
{
    // INCREMENT 4.5 — Phase 1 (the restored local development path) and Phase 8 (the cashflow contract).
    //
    // PHASE 1 CONTEXT. Development had NO `AiService:DestinationClass` key, so the resolver returned
    // UnapprovedExternal and the local Python ML path was denied — the three insight screens reported
    // "service unavailable" for a reason unrelated to any external provider. Fail-closed behaviour working
    // correctly, and the wrong outcome. The fix is configuration only: the gitignored dev appsettings now
    // says `Internal`, which the resolver honours ONLY because the BaseUrl is genuinely loopback.
    //
    // The global default is unchanged: absent still means denied.
    public class AiLocalDevPathAndCashflowContractTests
    {
        private static IConfiguration Config(params (string, string?)[] pairs)
        {
            var d = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                ["AiService:DeploymentMode"] = "LocalLoopback",
                ["AiService:BaseUrl"] = "http://localhost:8000",
                ["AiService:Secret"] = "dev-secret",
            };
            foreach (var (k, v) in pairs) d[k] = v;
            return new ConfigurationBuilder().AddInMemoryCollection(d).Build();
        }

        private static readonly AiEgressPurpose[] LocalMlPurposes =
        {
            AiEgressPurpose.JournalAnomalyDetection,
            AiEgressPurpose.CashflowForecast,
            AiEgressPurpose.InventoryAnalysis,
        };

        // ---- the global default is untouched ----
        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void A_missing_destination_class_still_fails_closed(string? configured)
        {
            var config = Config(("AiService:DestinationClass", configured));

            foreach (var purpose in LocalMlPurposes)
                Assert.Equal(AiEgressDestinationClass.UnapprovedExternal,
                    AiDestinationResolver.Resolve(config, purpose));
        }

        [Fact]
        public void An_explicitly_internal_local_service_is_honoured_for_the_local_ml_purposes()
        {
            var config = Config(("AiService:DestinationClass", "Internal"));

            foreach (var purpose in LocalMlPurposes)
                Assert.Equal(AiEgressDestinationClass.Internal, AiDestinationResolver.Resolve(config, purpose));
        }

        // "Internal" is a claim about the SERVICE, not a trust of the word localhost. The resolver
        // re-checks that the configured BaseUrl really is loopback and downgrades when it is not, so a dev
        // config copied to a server that points at a real host does not carry the label with it.
        [Theory]
        [InlineData("http://ai.example.com:8000")]
        [InlineData("https://10.0.0.5:8000")]
        [InlineData("not-a-url")]
        [InlineData(null)]
        public void Internal_is_refused_when_the_configured_service_is_not_actually_local(string? baseUrl)
        {
            var config = Config(("AiService:DestinationClass", "Internal"), ("AiService:BaseUrl", baseUrl));

            Assert.Equal(AiEgressDestinationClass.UnapprovedExternal,
                AiDestinationResolver.Resolve(config, AiEgressPurpose.CashflowForecast));
        }

        [Fact]
        public void UnapprovedExternal_remains_denied_when_configured_explicitly()
            => Assert.Equal(AiEgressDestinationClass.UnapprovedExternal,
                AiDestinationResolver.Resolve(
                    Config(("AiService:DestinationClass", "UnapprovedExternal")), AiEgressPurpose.CashflowForecast));

        // THE POINT OF PHASE 1: restoring the local path grants nothing to OpenAI. Even with the dev
        // configuration in place — Internal, loopback, secret present — an external processor still needs
        // a governance record, and the shipped record approves nobody.
        [Fact]
        public void Restoring_the_local_path_does_not_make_openai_internal_or_approved()
        {
            var config = Config(("AiService:DestinationClass", "Internal"));

            // The diagnostic relay is reclassified external whatever the config says, and then refused.
            Assert.Equal(AiEgressDestinationClass.UnapprovedExternal,
                AiDestinationResolver.Resolve(config, AiEgressPurpose.ConnectivityDiagnostic));

            // And configuring the approved class outright still cannot produce it.
            Assert.Equal(AiEgressDestinationClass.UnapprovedExternal,
                AiDestinationResolver.Resolve(
                    Config(("AiService:DestinationClass", "ApprovedExternalProcessor")),
                    AiEgressPurpose.CashflowForecast));

            Assert.False(new AiProviderAuthority().Assess(DateTime.UtcNow, AiTestProviderAuthority.TestScope).IsApproved);
        }

        // The dev configuration file itself must carry the fix, or the finding returns silently.
        [Fact]
        public void The_development_configuration_declares_an_internal_loopback_destination()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "CrossBuy.sln"))) dir = dir.Parent;
            Assert.NotNull(dir);

            var path = Path.Combine(dir!.FullName, "CrossBuy", "appsettings.json");
            if (!File.Exists(path)) return;   // gitignored and per-machine; absent on a clean checkout

            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
            if (!doc.RootElement.TryGetProperty("AiService", out var ai)) return;

            if (ai.TryGetProperty("DestinationClass", out var dc) && dc.GetString() == "Internal")
            {
                // If it claims Internal, the BaseUrl beside it must genuinely be loopback — otherwise the
                // resolver would silently downgrade and the local path would be broken again.
                var baseUrl = ai.TryGetProperty("BaseUrl", out var b) ? b.GetString() : null;
                Assert.True(AiDestinationResolver.IsLoopback(baseUrl),
                    "appsettings.json declares Internal but its BaseUrl is not loopback.");
            }
        }

        // =========================================================================================
        // PHASE 8 — the cashflow contract. The payload shape is the control.
        // =========================================================================================

        private static AiCashflowForecastRequest SampleRequest() => new()
        {
            OpeningCash = 125_000.500m,
            AsOf = new DateTime(2026, 8, 16, 0, 0, 0, DateTimeKind.Utc),
            HorizonDays = 90,
            Currency = "KWD",
            Inflows = new[] { new AiCashflowPoint { Date = new DateTime(2026, 9, 1), Amount = 20_000m } },
            Outflows = new[] { new AiCashflowPoint { Date = new DateTime(2026, 9, 5), Amount = 8_000m } },
        };

        // Every permitted field, and NOTHING else. Asserted against the serialised JSON rather than the
        // type, because what leaves is the JSON.
        [Fact]
        public void The_cashflow_payload_carries_only_permitted_aggregate_fields()
        {
            using var doc = System.Text.Json.JsonDocument.Parse(SampleRequest().ToPayloadJson());

            var names = doc.RootElement.EnumerateObject().Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray();
            Assert.Equal(new[] { "asOf", "currency", "horizonDays", "inflows", "openingCash", "outflows" }, names);

            // A flow point is a date and an amount. Anything else would make it attributable to a party.
            foreach (var flow in new[] { "inflows", "outflows" })
                foreach (var point in doc.RootElement.GetProperty(flow).EnumerateArray())
                    Assert.Equal(new[] { "amount", "date" },
                        point.EnumerateObject().Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray());
        }

        // The forbidden categories are absent BY CONSTRUCTION — there is no property for any of them, so
        // adding one is a compile error rather than a review finding. This asserts the type, not a value.
        [Theory]
        [InlineData("Customer")]
        [InlineData("Vendor")]
        [InlineData("Name")]
        [InlineData("Invoice")]
        [InlineData("Email")]
        [InlineData("Phone")]
        [InlineData("Address")]
        [InlineData("Description")]
        [InlineData("Note")]
        [InlineData("Employee")]
        [InlineData("Secret")]
        [InlineData("Id")]
        public void The_cashflow_contract_has_no_property_for_a_forbidden_category(string forbidden)
        {
            var properties = typeof(AiCashflowForecastRequest).GetProperties().Select(p => p.Name)
                .Concat(typeof(AiCashflowPoint).GetProperties().Select(p => p.Name))
                .ToArray();

            Assert.DoesNotContain(properties, p => p.Contains(forbidden, StringComparison.OrdinalIgnoreCase));
        }

        // The contract mirrors the LIVE payload built by AiInsightsService. If that widens, this fails.
        [Fact]
        public void The_contract_matches_the_shape_the_live_service_already_sends()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "CrossBuy.sln"))) dir = dir.Parent;
            Assert.NotNull(dir);

            var src = File.ReadAllText(Path.Combine(dir!.FullName, "CrossBuy", "BL", "AiInsightsService.cs"));

            Assert.Contains("new { openingCash, asOf = today, horizonDays, inflows, outflows }", src, StringComparison.Ordinal);
            Assert.Contains("AiDataClassification.FinancialAggregate", src, StringComparison.Ordinal);
        }

        // ---- response validation: model output is untrusted external input ----

        [Fact]
        public void A_well_formed_advice_response_is_accepted()
        {
            var json = """
            {"summary":"Cash position tightens in week 6.","expectedClosingCash":137000.5,
             "riskLevel":"medium","risks":["Concentration of outflows"],"drivers":["Large payable"],
             "recommendations":["Stagger payments"],"limitations":["Excludes unposted documents"]}
            """;

            Assert.True(AiCashflowForecastValidator.TryParse(json, out var advice, out var reason), reason);
            Assert.Equal(AiCashflowRiskLevel.Medium, advice!.RiskLevel);
            Assert.Equal(137000.5m, advice.ExpectedClosingCash);
            Assert.Single(advice.Risks);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("not json")]
        [InlineData("[]")]
        [InlineData("{}")]
        [InlineData("{\"summary\":\"\"}")]
        public void A_malformed_advice_response_is_refused(string? body)
        {
            Assert.False(AiCashflowForecastValidator.TryParse(body, out var advice, out _));
            Assert.Null(advice);
        }

        // An unrecognised rating becomes Unknown, NEVER Low. A model answering "moderate-to-severe" must
        // not be read as reassurance.
        [Theory]
        [InlineData("critical")]
        [InlineData("moderate-to-severe")]
        [InlineData("LOW ")]
        [InlineData("")]
        public void An_unrecognised_risk_level_becomes_unknown_rather_than_low(string level)
        {
            var json = $"{{\"summary\":\"s\",\"riskLevel\":\"{level}\"}}";
            Assert.True(AiCashflowForecastValidator.TryParse(json, out var advice, out _));

            if (level.Trim().Equals("low", StringComparison.OrdinalIgnoreCase))
                Assert.Equal(AiCashflowRiskLevel.Low, advice!.RiskLevel);
            else
                Assert.Equal(AiCashflowRiskLevel.Unknown, advice!.RiskLevel);
        }

        [Fact]
        public void Advice_strings_are_length_capped_and_stripped_of_control_characters()
        {
            var huge = new string('x', AiCashflowForecastValidator.MaxSummaryChars + 500);
            var json = System.Text.Json.JsonSerializer.Serialize(new { summary = huge + "", riskLevel = "low" });

            Assert.True(AiCashflowForecastValidator.TryParse(json, out var advice, out _));
            Assert.True(advice!.Summary.Length <= AiCashflowForecastValidator.MaxSummaryChars);
            Assert.DoesNotContain('', advice.Summary);
            Assert.DoesNotContain('', advice.Summary);
        }

        [Fact]
        public void Advice_arrays_are_bounded()
        {
            var many = Enumerable.Range(0, 100).Select(i => $"item {i}").ToArray();
            var json = System.Text.Json.JsonSerializer.Serialize(new { summary = "s", riskLevel = "low", risks = many });

            Assert.True(AiCashflowForecastValidator.TryParse(json, out var advice, out _));
            Assert.True(advice!.Risks.Count <= AiCashflowForecastValidator.MaxItems);
        }

        // The system prompt is a CrossBuy constant. No user text is interpolated into it, so there is no
        // path by which a user-entered string becomes an instruction.
        [Fact]
        public void The_system_prompt_is_a_constant_with_no_interpolation()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "CrossBuy.sln"))) dir = dir.Parent;
            Assert.NotNull(dir);

            var src = File.ReadAllText(Path.Combine(
                dir!.FullName, "CrossBuy", "BL", "Platform", "Ai", "AiCashflowForecastContract.cs"));

            Assert.Contains("public const string SystemPrompt", src, StringComparison.Ordinal);
            Assert.DoesNotContain("SystemPrompt = $\"", src, StringComparison.Ordinal);
        }

        // Phase 8 explicitly forbids switching the live feature. The local Python path stays active.
        [Fact]
        public void The_live_cashflow_feature_is_still_routed_to_the_local_service()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "CrossBuy.sln"))) dir = dir.Parent;
            Assert.NotNull(dir);

            var src = File.ReadAllText(Path.Combine(dir!.FullName, "CrossBuy", "BL", "AiInsightsService.cs"));

            Assert.Contains("\"/forecast/cashflow\"", src, StringComparison.Ordinal);

            // The substantive assertion: the service still posts through IAiService to the local Python
            // path and never touches a provider adapter.
            Assert.DoesNotContain("IAiExternalProvider", src, StringComparison.Ordinal);
            Assert.DoesNotContain("OpenAiProviderAdapter", src, StringComparison.Ordinal);
            Assert.DoesNotContain("api.openai.com", src, StringComparison.Ordinal);

            // NOTE ON THE WEAKENED CHECK. This used to assert the string "OpenAi" was absent entirely.
            // Increment 4.9 made that impossible without losing something better: the service must now
            // tell the authority WHICH account context it is asking about, and that scope comes from
            // OpenAiOptions. The reference is to a CONFIGURATION READER, not to a provider client — and
            // because the options are unconfigured on this path, the scope is incomplete and denies.
            // Asserting the three names above is a stronger statement than asserting one substring.
            Assert.DoesNotContain("HttpClient", src, StringComparison.Ordinal);
        }
    }
}
