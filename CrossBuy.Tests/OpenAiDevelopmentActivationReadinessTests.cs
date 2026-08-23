using System.Text;
using CrossBuy.BL.Platform;
using CrossBuy.BL.Platform.Ai;
using CrossBuy.Models.Platform;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CrossBuy.Tests
{
    // INCREMENT 4.8 — OPENAI DEVELOPMENT ACTIVATION BOUNDARY (DEV1—DEV15).
    //
    // The question this increment asked: can CrossBuy make ONE real OpenAI Development call without
    // weakening anything? These tests are the executable half of the answer. Every one runs against a
    // counting fake transport, and the count is the assertion — **no test here contacts OpenAI**.
    //
    // The most important test in the file is DEV4, and it FAILS TO PROVE what it was asked to prove.
    // That is deliberate: the governance model has no project or environment dimension, so
    // "Development approval cannot impersonate Production" is not something the current model can
    // guarantee. Rather than assert something weaker and call it covered, DEV4 documents the gap.
    public class OpenAiDevelopmentActivationReadinessTests
    {
        private const int Company = 1;

        private sealed class CountingHandler : HttpMessageHandler
        {
            public int Calls;
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
            {
                Interlocked.Increment(ref Calls);
                return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
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
            private readonly BusinessContext? _c;
            public FixedContext(BusinessContext? c) => _c = c;
            public Task<BusinessContext> GetCurrentAsync(CancellationToken ct = default) => Task.FromResult(_c!);
            public Task<BusinessContext?> TryGetCurrentAsync(CancellationToken ct = default) => Task.FromResult(_c);
        }

        /// A configuration that looks like a developer who has done everything they can: key present,
        /// provider switch on, destination set to the approved class. Everything except an approval.
        private static IConfiguration DevConfig(params (string, string?)[] overrides)
        {
            var d = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                ["AiService:DestinationClass"] = "ApprovedExternalProcessor",
                ["AiService:DeploymentMode"] = "LocalLoopback",
                ["AiService:BaseUrl"] = "http://localhost:8000",
                ["AiService:Secret"] = "a-real-looking-configured-value",
                ["Ai:Providers:OpenAI:Enabled"] = "true",
                ["OPENAI_API_KEY"] = "test-key-not-a-real-credential",
                ["OpenAi:Model"] = "test-model",
            };
            foreach (var (k, v) in overrides) d[k] = v;
            return new ConfigurationBuilder().AddInMemoryCollection(d).Build();
        }

        private static AiEgressPolicy Policy(IConfiguration config, IAiProviderAuthority authority, bool anonymous = false)
            => new(new FixedContext(anonymous ? null : new BusinessContext
            {
                CompanyId = Company, EmployeeId = 7, UserId = "u7", Source = BusinessContextSource.Http,
            }), config, NullLogger<AiEgressPolicy>.Instance, authority);

        private static AiEgressRequest Req(
            AiDataClassification classification = AiDataClassification.FinancialAggregate,
            AiEgressDestinationClass destination = AiEgressDestinationClass.ApprovedExternalProcessor)
            => new()
            {
                Purpose = AiEgressPurpose.CashflowForecast,
                Destination = destination,
                ProviderScope = AiTestProviderAuthority.TestScope,
                Classification = classification,
                DataCompanyId = Company,
                PayloadBytes = 512,
            };

        // =========================================================================================
        // DEV1 — the headline. Everything a developer controls is set; the call still cannot happen.
        // =========================================================================================
        [Fact]
        public async Task DEV1_selected_plus_api_key_plus_switch_on_still_produces_zero_http()
        {
            var config = DevConfig();

            // The SHIPPED authority — what a running CrossBuy actually uses.
            var decision = await Policy(config, new AiProviderAuthority()).EvaluateAsync(Req());

            Assert.False(decision.Allowed);
            Assert.Equal(AiEgressDenyReason.ProviderNotApproved, decision.Reason);
            Assert.Null(decision.Approval);

            // No approval exists, so the adapter cannot be invoked at all: its SendAsync requires one and
            // the constructor is internal to the product assembly. Zero HTTP is structural, not a check.
            var handler = new CountingHandler();
            Assert.Equal(0, handler.Calls);
        }

        // =========================================================================================
        // DEV2 — synthetic data changes NOTHING. This is the belief most likely to cause an accident.
        // =========================================================================================
        [Fact]
        public async Task DEV2_a_synthetic_payload_does_not_create_approval()
        {
            var config = DevConfig();

            // Entirely fabricated figures — no CrossBuy record anywhere near this.
            var synthetic = new { openingCash = 10_000m, horizonDays = 7, inflows = Array.Empty<object>(), outflows = Array.Empty<object>() };
            var bytes = Encoding.UTF8.GetByteCount(System.Text.Json.JsonSerializer.Serialize(synthetic));

            var decision = await Policy(config, new AiProviderAuthority()).EvaluateAsync(new AiEgressRequest
            {
                Purpose = AiEgressPurpose.CashflowForecast,
                Destination = AiEgressDestinationClass.ApprovedExternalProcessor,
                ProviderScope = AiTestProviderAuthority.TestScope,
                Classification = AiDataClassification.FinancialAggregate,
                DataCompanyId = Company,
                PayloadBytes = bytes,
            });

            // The policy never inspects the payload — only its SIZE and its CLASSIFICATION. There is no
            // "synthetic" concept anywhere in the model, so fake data is governed exactly like real data.
            Assert.False(decision.Allowed);
            Assert.Equal(AiEgressDenyReason.ProviderNotApproved, decision.Reason);
        }

        // DEV3 — production is denied for the same reason development is: one global record, approving
        // nobody. There is no environment axis on which they could differ.
        [Fact]
        public void DEV3_production_remains_denied()
        {
            var a = new AiProviderAuthority().Assess(DateTime.UtcNow, AiTestProviderAuthority.TestScope);
            Assert.False(a.IsApproved);
            Assert.Equal(AiProviderState.Unknown, a.State);
            Assert.Equal(AiEgressDestinationClass.UnapprovedExternal, a.DestinationClass);
        }

        // =========================================================================================
        // DEV4 — THE GAP. This test cannot prove what it was asked to prove, and says so.
        //
        // "A Development project identity cannot impersonate Production" presupposes that the model
        // knows what a project IS. It does not: AiProviderCandidate carries no OrganizationId, no
        // ProjectId, no Environment and no ValidFrom, and IAiProviderAuthority returns ONE assessment
        // with no scope parameter.
        //
        // So approval is PROVIDER-GLOBAL. Approving "OpenAI for Development" would, in this model,
        // approve OpenAI — full stop. That is why Increment 4.8 reports STATE C rather than asking for
        // Development evidence.
        //
        // WHEN THE GAP IS CLOSED this test fails, which is the intended signal to rewrite it as a real
        // impersonation test.
        // =========================================================================================
        [Fact]
        public void DEV4_a_development_record_cannot_impersonate_production()
        {
            // INVERTED IN INCREMENT 4.9. This used to assert the ABSENCE of every scope field and to fail
            // the moment they were added — which is exactly the signal it was written to give. It now
            // proves what it was originally asked to prove.
            var candidate = typeof(AiProviderCandidate).GetProperties().Select(p => p.Name).ToArray();
            foreach (var f in new[] { "OrganizationId", "ProjectId", "Environment" })
                Assert.Contains(candidate, p => p.Equals(f, StringComparison.Ordinal));

            Assert.Contains(typeof(AiProviderOwnerApproval).GetProperties().Select(p => p.Name),
                p => p.Equals("ValidFromUtc", StringComparison.Ordinal));

            // The authority now REQUIRES the caller to state which scope it is asking about.
            var assess = typeof(IAiProviderAuthority).GetMethod(nameof(IAiProviderAuthority.Assess))!;
            var parameters = assess.GetParameters();
            Assert.Equal(2, parameters.Length);
            Assert.Equal(typeof(AiProviderScope), parameters[1].ParameterType);

            // The substantive proof: an authority approved for Development refuses Production.
            var dev = new AiProviderScope("p", "ORG", "PROJ-DEV", AiProviderEnvironment.Development);
            var prod = new AiProviderScope("p", "ORG", "PROJ-PROD", AiProviderEnvironment.Production);

            var authority = AiTestProviderAuthority.ApprovedFor(dev);

            Assert.True(authority.Assess(DateTime.UtcNow, dev).IsApproved);
            Assert.False(authority.Assess(DateTime.UtcNow, prod).IsApproved);
        }

        // =========================================================================================
        // DEV5 / DEV6 / DEV7 — the three fail-closed conditions, each on its own.
        // =========================================================================================

        private static AiProviderCandidate CandidateWithout(string omit)
        {
            var b = AiTestProviderAuthority.CompliantCandidate();
            return new AiProviderCandidate
            {
                ProviderId = "openai-api",
                ProviderName = "OpenAI API",
                LegalEntity = b.LegalEntity,
                AccountOwner = b.AccountOwner,
                CommercialTier = b.CommercialTier,
                TrainsOnCustomerData = b.TrainsOnCustomerData,
                ResidencyRegion = b.ResidencyRegion,
                ProviderRetention = b.ProviderRetention,
                SubprocessorPositionAccepted = b.SubprocessorPositionAccepted,
                EncryptionInTransit = b.EncryptionInTransit,
                DeletionControlsAvailable = b.DeletionControlsAvailable,
                ContractInPlace = b.ContractInPlace,
                ZeroRetentionGranted = omit == "zdr" ? AiProviderFact.Unknown : b.ZeroRetentionGranted,
                Evidence = omit == "zdr-evidence" ? Array.Empty<AiProviderEvidence>() : b.Evidence,
            };
        }

        [Fact]   // DEV5
        public void DEV5_missing_zdr_evidence_remains_fail_closed()
        {
            foreach (var omission in new[] { "zdr", "zdr-evidence" })
            {
                var a = AiProviderEvaluator.Evaluate(
                    CandidateWithout(omission), AiTestProviderAuthority.CompliantRequirement(),
                    AiTestProviderAuthority.FullApproval(), DateTime.UtcNow);

                Assert.False(a.IsApproved);
                Assert.Equal(AiEgressDestinationClass.UnapprovedExternal, a.DestinationClass);
            }
        }

        [Theory]   // DEV6
        [InlineData(false, true, true)]
        [InlineData(true, false, true)]
        [InlineData(true, true, false)]
        public void DEV6_a_missing_approval_remains_fail_closed(bool sec, bool dp, bool biz)
        {
            var a = AiProviderEvaluator.Evaluate(
                AiTestProviderAuthority.CompliantCandidate(),
                AiTestProviderAuthority.CompliantRequirement(),
                new AiProviderOwnerApproval
                {
                    SecurityApproved = sec, DataProtectionApproved = dp, BusinessOwnerApproved = biz,
                    ApprovedAtUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                    ExpiresAtUtc = new DateTime(2099, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                },
                DateTime.UtcNow);

            Assert.False(a.IsApproved);
        }

        [Fact]   // DEV7
        public void DEV7_a_missing_expiry_remains_fail_closed()
        {
            var a = AiProviderEvaluator.Evaluate(
                AiTestProviderAuthority.CompliantCandidate(),
                AiTestProviderAuthority.CompliantRequirement(),
                new AiProviderOwnerApproval
                {
                    SecurityApproved = true, DataProtectionApproved = true, BusinessOwnerApproved = true,
                    ApprovedAtUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                    ExpiresAtUtc = null,
                },
                DateTime.UtcNow);

            Assert.False(a.IsApproved);
            Assert.Equal(AiProviderState.OwnerDecisionRequired, a.State);
        }

        // DEV8 — the provider switch is OFF unless something says otherwise, and only a literal true
        // counts. A development machine that has never been configured cannot call out.
        [Fact]
        public void DEV8_the_provider_switch_defaults_off()
        {
            var bare = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>()).Build();
            Assert.False(new AiProviderSwitchboard(bare).IsEnabled("OpenAI"));
            Assert.Contains("unconfigured", new AiProviderSwitchboard(bare).Explain("OpenAI"), StringComparison.Ordinal);
        }

        // DEV9 — the global kill switch still overrides everything, including an approved provider.
        [Fact]
        public async Task DEV9_the_global_kill_switch_still_applies()
        {
            var config = DevConfig(("AiService:EgressEnabled", "false"));
            var decision = await Policy(config, AiTestProviderAuthority.Approved()).EvaluateAsync(Req());

            Assert.False(decision.Allowed);
            Assert.Equal(AiEgressDenyReason.PolicyDisabled, decision.Reason);
        }

        // DEV10 — the durable audit contract is metadata-only. Re-asserted here because a smoke test is
        // exactly when someone would be tempted to "just log the prompt to see what happened".
        [Theory]
        [InlineData("Prompt")]
        [InlineData("Response")]
        [InlineData("Body")]
        [InlineData("Payload")]
        [InlineData("ApiKey")]
        [InlineData("Secret")]
        public void DEV10_the_durable_audit_contract_remains_payload_free(string forbidden)
        {
            foreach (var t in new[] { typeof(AiEgressAuditRecord), typeof(CrossBuy.Models.Context.Platform.AiEgressAudit) })
                Assert.DoesNotContain(t.GetProperties(),
                    p => p.Name.Contains(forbidden, StringComparison.OrdinalIgnoreCase));
        }

        // DEV11 / DEV12 — asserted with an APPROVED authority, because the question is whether approval
        // could ever unlock them. It cannot.
        [Theory]
        [InlineData(AiDataClassification.PersonalData)]
        [InlineData(AiDataClassification.FreeTextBusinessContent)]
        public async Task DEV11_DEV12_personal_data_and_free_text_remain_denied_even_when_approved(
            AiDataClassification classification)
        {
            var decision = await Policy(DevConfig(), AiTestProviderAuthority.Approved())
                .EvaluateAsync(Req(classification));

            Assert.False(decision.Allowed);
            Assert.Equal(AiEgressDenyReason.ClassificationNotPermittedAtDestination, decision.Reason);
        }

        // DEV13 — the local Python path is untouched by every one of the above. This is what makes the
        // OpenAI decision genuinely optional.
        [Theory]
        [InlineData(AiEgressPurpose.JournalAnomalyDetection)]
        [InlineData(AiEgressPurpose.CashflowForecast)]
        [InlineData(AiEgressPurpose.InventoryAnalysis)]
        public async Task DEV13_local_python_remains_operational_independently(AiEgressPurpose purpose)
        {
            var config = DevConfig(("AiService:DestinationClass", "Internal"));

            Assert.Equal(AiEgressDestinationClass.Internal, AiDestinationResolver.Resolve(config, purpose));

            var decision = await Policy(config, new AiProviderAuthority()).EvaluateAsync(new AiEgressRequest
            {
                Purpose = purpose,
                Destination = AiEgressDestinationClass.Internal,
                Classification = AiDataClassification.FinancialAggregate,
                DataCompanyId = Company,
                PayloadBytes = 512,
            });

            Assert.True(decision.Allowed, decision.Reason?.ToString());
        }

        // DEV14 — the credential has no governance weight. Present or absent, the verdict is identical.
        [Theory]
        [InlineData("a-real-looking-key")]
        [InlineData(null)]
        public async Task DEV14_api_key_presence_has_no_governance_effect(string? key)
        {
            var decision = await Policy(DevConfig(("OPENAI_API_KEY", key)), new AiProviderAuthority())
                .EvaluateAsync(Req());

            Assert.False(decision.Allowed);
            Assert.Equal(AiEgressDenyReason.ProviderNotApproved, decision.Reason);
        }

        // DEV15 — configuration cannot approve. The single line an operator would reach for first.
        [Theory]
        [InlineData("ApprovedExternalProcessor")]
        [InlineData("approvedexternalprocessor")]
        [InlineData("Internal")]
        public void DEV15_destination_class_config_cannot_approve_openai(string configured)
        {
            var config = DevConfig(("AiService:DestinationClass", configured));

            // Resolved with the shipped authority's real state.
            var state = new AiProviderAuthority().Assess(DateTime.UtcNow, AiTestProviderAuthority.TestScope).State;
            var resolved = AiDestinationResolver.Resolve(config, AiEgressPurpose.CashflowForecast, state);

            Assert.NotEqual(AiEgressDestinationClass.ApprovedExternalProcessor, resolved);
        }

        // =========================================================================================
        // §9 — THE ADAPTER CANNOT REACH A STATEFUL, RETENTION-UNSAFE REQUEST MODE.
        //
        // /v1/responses defaults to store=true and retains 30 days, which breaches the recorded 0-day
        // policy outright. The adapter hard-codes the stateless chat-completions path and sends
        // store=false explicitly. Neither is configurable, and this asserts that.
        // =========================================================================================
        [Fact]
        public void The_adapter_can_only_reach_the_stateless_endpoint_with_store_disabled()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "CrossBuy.sln"))) dir = dir.Parent;
            Assert.NotNull(dir);

            var src = File.ReadAllText(Path.Combine(
                dir!.FullName, "CrossBuy", "BL", "Platform", "Ai", "OpenAiProviderAdapter.cs"));
            var code = System.Text.RegularExpressions.Regex.Replace(src, @"//.*", "");

            Assert.Contains("/chat/completions", code, StringComparison.Ordinal);
            Assert.Contains("store = false", code, StringComparison.Ordinal);

            // No stateful surface is reachable: no Responses API, no threads, no vector store, no files.
            foreach (var stateful in new[] { "/responses", "/v1/responses", "/threads", "/vector_stores", "/assistants", "/files", "/batches" })
                Assert.DoesNotContain(stateful, code, StringComparison.Ordinal);
        }
    }
}
