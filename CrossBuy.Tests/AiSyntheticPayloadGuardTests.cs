using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using CrossBuy.BL.Platform;
using CrossBuy.BL.Platform.Ai;
using CrossBuy.Models.Platform;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CrossBuy.Tests
{
    // INCREMENT 4.10 — THE SYNTHETIC-ONLY DEVELOPMENT CONSTRAINT, AS A CHECK RATHER THAN A PROMISE.
    //
    // The owner recorded that a controlled OpenAI Development smoke test may use FULLY SYNTHETIC content
    // only: no personal data, no customer, vendor or employee names, no real invoice numbers, no
    // free-text business content, nothing read from a database, no credentials. That is a sentence in a
    // document until something enforces it.
    //
    // THE HOLE A CLASSIFICATION ALONE WOULD OPEN, which these tests exist to keep shut: the label is
    // chosen by the caller. `AiDataClassification.SyntheticTestData` is permitted at an external
    // destination, and `FreeTextBusinessContent` is not — so if the label were taken at face value,
    // relabelling a payload of real journal descriptions would carry it past the matrix. The label is
    // therefore worth nothing on its own: the payload must also BE the canonical synthetic payload, and
    // the adapter refuses it otherwise.
    //
    // NO TEST HERE CONTACTS OPENAI.
    public class AiSyntheticPayloadGuardTests
    {
        private const int Company = 1;

        private sealed class CountingHandler : HttpMessageHandler
        {
            public int Calls;

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
            {
                Interlocked.Increment(ref Calls);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        "{\"model\":\"m\",\"choices\":[{\"message\":{\"content\":\"{}\"}}]," +
                        "\"usage\":{\"prompt_tokens\":1,\"completion_tokens\":1}}",
                        Encoding.UTF8, "application/json"),
                });
            }
        }

        private sealed class Factory : IHttpClientFactory
        {
            private readonly HttpMessageHandler _h;
            public Factory(HttpMessageHandler h) => _h = h;
            public HttpClient CreateClient(string name) => new(_h, disposeHandler: false);
        }

        private sealed class FixedContext : IBusinessContextAccessor
        {
            private readonly BusinessContext _c;
            public FixedContext(BusinessContext c) => _c = c;
            public Task<BusinessContext> GetCurrentAsync(CancellationToken ct = default) => Task.FromResult(_c);
            public Task<BusinessContext?> TryGetCurrentAsync(CancellationToken ct = default) => Task.FromResult<BusinessContext?>(_c);
        }

        private static readonly AiProviderScope DevScope = new(
            OpenAiOptions.ProviderId,
            OpenAiAccountScopeRegistrationTests.RealOrganizationId,
            OpenAiAccountScopeRegistrationTests.RealDevelopmentProjectId,
            AiProviderEnvironment.Development);

        private static IConfiguration Config() => new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                [AiDestinationResolver.DestinationClassKey] = "ApprovedExternalProcessor",
                ["AiService:DeploymentMode"] = "LocalLoopback",
                ["AiService:BaseUrl"] = "http://localhost:8000",
                ["AiService:Secret"] = "a-real-looking-configured-value",
                ["Ai:Providers:OpenAI:Enabled"] = "true",
                ["Ai:Providers:OpenAI:Development:Enabled"] = "true",
                ["OpenAi:OrganizationId"] = DevScope.OrganizationId,
                ["OpenAi:ProjectId"] = DevScope.ProjectId,
                ["OpenAi:Environment"] = "Development",
                ["OpenAi:Model"] = "test-model",
                ["OpenAi:TimeoutSeconds"] = "5",
                ["OPENAI_API_KEY"] = "test-key-not-a-real-credential",
            }).Build();

        /// A genuine token for the synthetic classification, minted by the real policy.
        private static async Task<AiEgressApproval> MintSyntheticApprovalAsync(IConfiguration config)
        {
            var policy = new AiEgressPolicy(
                new FixedContext(new BusinessContext
                {
                    CompanyId = Company, EmployeeId = 7, UserId = "u7", Source = BusinessContextSource.Http,
                }),
                config, NullLogger<AiEgressPolicy>.Instance,
                AiTestProviderAuthority.ApprovedFor(DevScope));

            var decision = await policy.EvaluateAsync(new AiEgressRequest
            {
                Purpose = AiEgressPurpose.CashflowForecast,
                Destination = AiEgressDestinationClass.ApprovedExternalProcessor,
                ProviderScope = DevScope,
                Classification = AiSyntheticPayload.Classification,
                DataCompanyId = Company,
                PayloadBytes = Encoding.UTF8.GetByteCount(AiSyntheticPayload.Build()),
            });

            Assert.True(decision.Allowed, decision.Reason?.ToString());
            return decision.Approval!;
        }

        private static (OpenAiProviderAdapter Adapter, CountingHandler Handler) Adapter(IConfiguration config)
        {
            var handler = new CountingHandler();
            return (new OpenAiProviderAdapter(
                new Factory(handler), config,
                new AiProviderSwitchboard(config), new AiCircuitBreaker(),
                new AiRateLimiter(config),
                new AiUsageGuard(config, new AiConfiguredPricingProvider(config)),
                new LoggingAiEgressAuditSink(NullLogger<LoggingAiEgressAuditSink>.Instance),
                NullLogger<OpenAiProviderAdapter>.Instance), handler);
        }

        // =========================================================================================
        // The payload itself
        // =========================================================================================

        [Fact]
        public void The_payload_is_deterministic_and_obviously_synthetic()
        {
            var a = AiSyntheticPayload.Build();
            Assert.Equal(a, AiSyntheticPayload.Build());          // same bytes every time
            Assert.True(AiSyntheticPayload.IsSynthetic(a));

            Assert.Contains("SYNTHETIC-CROSSBUY-TEST", a, StringComparison.Ordinal);
            Assert.Contains("OPENAI-DEVELOPMENT-SMOKE-TEST", a, StringComparison.Ordinal);
            Assert.Contains("123.45", a, StringComparison.Ordinal);

            // Small enough that a reviewer can read the whole thing, which is part of it being auditable.
            Assert.True(Encoding.UTF8.GetByteCount(a) < 1024);
        }

        // The guard is an ALLOWLIST. Each case below is something a denylist built from intuition would
        // plausibly have missed.
        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("not json at all")]
        [InlineData("{}")]
        [InlineData("[]")]
        [InlineData("{\"company\":\"SYNTHETIC-CROSSBUY-TEST\"}")]                       // partial
        [InlineData("{\"company\":\"Acme Trading LLC\",\"scenario\":\"OPENAI-DEVELOPMENT-SMOKE-TEST\"," +
                    "\"amount\":123.45,\"currency\":\"XTS\",\"note\":\"x\"}")]           // a real-looking company
        public void A_payload_that_is_not_the_canonical_one_is_refused(string? candidate)
            => Assert.False(AiSyntheticPayload.IsSynthetic(candidate));

        // THE SMUGGLING CASE, and the reason UnmappedMemberHandling.Disallow is set. Every known field is
        // correct; a sixth field carries the real data. A guard that compared only the fields it knows
        // about would pass this.
        [Fact]
        public void A_canonical_payload_with_an_extra_field_is_refused()
        {
            var canonical = AiSyntheticPayload.Build();
            var smuggled = canonical[..^1] +
                           ",\"description\":\"Payment to a real named supplier, invoice 2026-00417\"}";

            Assert.Contains("SYNTHETIC-CROSSBUY-TEST", smuggled, StringComparison.Ordinal);
            Assert.False(AiSyntheticPayload.IsSynthetic(smuggled));
        }

        [Fact]
        public void A_canonical_payload_with_an_altered_value_is_refused()
        {
            var tampered = AiSyntheticPayload.Build().Replace("123.45", "48231.90", StringComparison.Ordinal);
            Assert.False(AiSyntheticPayload.IsSynthetic(tampered));
        }

        [Fact]
        public void An_enormous_payload_is_refused_without_being_parsed()
        {
            var huge = "{\"company\":\"" + new string('x', 100_000) + "\"}";
            Assert.False(AiSyntheticPayload.IsSynthetic(huge));
        }

        // =========================================================================================
        // Enforcement at the adapter — the label alone must not be enough
        // =========================================================================================

        [Fact]
        public async Task A_payload_labelled_synthetic_that_is_not_synthetic_is_refused_before_any_call()
        {
            var config = Config();
            var approval = await MintSyntheticApprovalAsync(config);
            var (adapter, handler) = Adapter(config);

            var result = await adapter.SendAsync(new AiProviderRequest
            {
                Approval = approval,
                SystemPrompt = AiCashflowForecastValidator.SystemPrompt,
                // Real-looking free text, wearing the synthetic label.
                PayloadJson = "{\"description\":\"Cheque 4471 to a named supplier for invoice INV-2026-0088\"}",
                MaxOutputTokens = 200,
                CorrelationId = "synthetic-guard",
            });

            Assert.Equal(AiProviderOutcome.Refused, result.Outcome);
            Assert.Equal("request:not-actually-synthetic", result.Reason);
            Assert.Equal(0, handler.Calls);
        }

        // The mirror: the canonical payload gets past the CONTENT guard and is then stopped by the next
        // gate anyway. Written so the guard cannot be mistaken for the thing that permits a call.
        [Fact]
        public async Task The_canonical_payload_passes_the_content_guard_and_is_still_not_a_licence_to_send()
        {
            // The PRODUCT authority. Nothing here can mint a token for it, so the send never begins.
            var config = Config();
            var policy = new AiEgressPolicy(
                new FixedContext(new BusinessContext
                {
                    CompanyId = Company, EmployeeId = 7, UserId = "u7", Source = BusinessContextSource.Http,
                }),
                config, NullLogger<AiEgressPolicy>.Instance, new AiProviderAuthority());

            var decision = await policy.EvaluateAsync(new AiEgressRequest
            {
                Purpose = AiEgressPurpose.CashflowForecast,
                Destination = AiEgressDestinationClass.ApprovedExternalProcessor,
                ProviderScope = DevScope,
                Classification = AiSyntheticPayload.Classification,
                DataCompanyId = Company,
                PayloadBytes = Encoding.UTF8.GetByteCount(AiSyntheticPayload.Build()),
            });

            Assert.True(AiSyntheticPayload.IsSynthetic(AiSyntheticPayload.Build()));
            Assert.False(decision.Allowed);
            Assert.Equal(AiEgressDenyReason.ProviderNotApproved, decision.Reason);
            Assert.Null(decision.Approval);
        }

        // =========================================================================================
        // §9 — zero database access, proven structurally rather than observed
        // =========================================================================================

        [Fact]
        public void The_payload_builder_has_no_route_to_any_data_source()
        {
            var type = typeof(AiSyntheticPayload);

            Assert.True(type.IsAbstract && type.IsSealed, "a static class has no instance state to hold a context");
            Assert.Empty(type.GetConstructors(BindingFlags.Public | BindingFlags.Instance));

            // No field, property or parameter anywhere on the type can carry a context, a connection or
            // caller-supplied business content. Build() takes NOTHING, which is the strongest form of
            // "it cannot read the database": there is no parameter through which data could arrive.
            var build = type.GetMethod(nameof(AiSyntheticPayload.Build), BindingFlags.Public | BindingFlags.Static);
            Assert.NotNull(build);
            Assert.Empty(build!.GetParameters());

            foreach (var f in type.GetFields(BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Public))
            {
                var n = f.FieldType.Name;
                Assert.False(n.Contains("DbContext", StringComparison.Ordinal), $"field {f.Name} is a {n}");
                Assert.False(n.Contains("Connection", StringComparison.Ordinal), $"field {f.Name} is a {n}");
                Assert.False(n.Contains("Repository", StringComparison.Ordinal), $"field {f.Name} is a {n}");
            }
        }

        [Fact]
        public void The_payload_source_names_no_business_table()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "CrossBuy.sln"))) dir = dir.Parent;
            Assert.NotNull(dir);

            var src = File.ReadAllText(Path.Combine(
                dir!.FullName, "CrossBuy", "BL", "Platform", "Ai", "AiSyntheticPayload.cs"));

            // Prose in the comments explains what is EXCLUDED, so the check runs over code only.
            var code = string.Join('\n', src.Split('\n').Where(l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal)));

            foreach (var forbidden in new[]
                     {
                         "CrossDbContext", "DbContext", "Set<", "FromSql", "SqlConnection",
                         "Customer", "Vendor", "Employee", "JournalEntry", "Invoice",
                         "Receipt", "Payment", "Lead", "Opportunity", "IConfiguration",
                     })
            {
                Assert.DoesNotContain(forbidden, code, StringComparison.Ordinal);
            }
        }

        // The classification exists to make the audit trail legible. If the audit stored it as something
        // indistinguishable from a real send, the whole reason for adding it would be gone.
        [Fact]
        public void The_audit_trail_records_the_synthetic_classification_by_name()
        {
            var record = new AiEgressAuditRecord
            {
                CompanyId = Company,
                ProviderId = OpenAiOptions.ProviderId,
                Feature = AiEgressPurpose.CashflowForecast,
                Classification = AiSyntheticPayload.Classification,
                Destination = AiEgressDestinationClass.ApprovedExternalProcessor,
                GovernanceDecision = "test",
                CorrelationId = "c",
                OccurredAtUtc = new DateTime(2026, 8, 16, 0, 0, 0, DateTimeKind.Utc),
                Outcome = AiProviderOutcome.Refused,
            };

            Assert.Equal("SyntheticTestData", record.Classification.ToString());
            Assert.NotEqual(AiDataClassification.FinancialAggregate, record.Classification);
        }

        // Adding a member to the classification enum must not have moved an existing one: the audit table
        // stores the NAME, but a stored integer anywhere else would be silently reinterpreted.
        [Fact]
        public void The_existing_classification_values_did_not_move()
        {
            Assert.Equal(0, (int)AiDataClassification.Unknown);
            Assert.Equal(1, (int)AiDataClassification.OperationalMetadata);
            Assert.Equal(2, (int)AiDataClassification.FinancialAggregate);
            Assert.Equal(3, (int)AiDataClassification.FreeTextBusinessContent);
            Assert.Equal(4, (int)AiDataClassification.PersonalData);
            Assert.Equal(5, (int)AiDataClassification.SyntheticTestData);
        }

        // The new row in the matrix must not have widened anything else. PersonalData and free text are
        // re-asserted here, next to the change, so a future edit to the matrix trips over them.
        [Fact]
        public async Task Adding_the_synthetic_row_did_not_widen_the_matrix()
        {
            var config = Config();

            foreach (var (classification, allowed) in new[]
                     {
                         (AiDataClassification.PersonalData, false),
                         (AiDataClassification.FreeTextBusinessContent, false),
                         (AiDataClassification.Unknown, false),
                         (AiDataClassification.SyntheticTestData, true),
                         (AiDataClassification.FinancialAggregate, true),
                         (AiDataClassification.OperationalMetadata, true),
                     })
            {
                var policy = new AiEgressPolicy(
                    new FixedContext(new BusinessContext
                    {
                        CompanyId = Company, EmployeeId = 7, UserId = "u7", Source = BusinessContextSource.Http,
                    }),
                    config, NullLogger<AiEgressPolicy>.Instance,
                    AiTestProviderAuthority.ApprovedFor(DevScope));

                var decision = await policy.EvaluateAsync(new AiEgressRequest
                {
                    Purpose = AiEgressPurpose.CashflowForecast,
                    Destination = AiEgressDestinationClass.ApprovedExternalProcessor,
                    ProviderScope = DevScope,
                    Classification = classification,
                    DataCompanyId = Company,
                    PayloadBytes = 512,
                });

                Assert.Equal(allowed, decision.Allowed);
            }
        }
    }
}
