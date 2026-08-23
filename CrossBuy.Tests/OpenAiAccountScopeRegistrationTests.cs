using System.Net;
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
    // INCREMENT 4.10 — REAL OPENAI ACCOUNT SCOPE REGISTRATION (ID1—ID18).
    //
    // WHAT CHANGED, AND WHAT DELIBERATELY DID NOT. The real OpenAI organisation and both project
    // identifiers now exist and are configured. Increment 4.9 built the machinery to bind an approval to
    // exactly one of them; this increment supplies the values and answers one question:
    //
    //     "Does having the identifiers bring CrossBuy any closer to making a call?"
    //
    // The answer these tests record is NO, and that is the point of writing them. Configuration states
    // WHICH account a deployment would call. It has never stated that the call may be made, and the
    // whole file exists to keep those two sentences from merging.
    //
    // ON THE IDENTIFIERS BEING IN SOURCE. An organisation id and a project id are IDENTIFIERS, not
    // secrets: they name an account, they do not authenticate to it, and they are inert without the API
    // key — which appears in no file, no test and no document, and whose value is never read. Asserting
    // the exact strings is the only way to prove the Development host cannot be pointed at the
    // Production project, which is the security property this increment is actually about.
    //
    // NO TEST IN THIS FILE CONTACTS OPENAI. Every adapter case runs against a counting fake transport and
    // the count is asserted to be zero.
    public class OpenAiAccountScopeRegistrationTests
    {
        private const int Company = 1;

        // ---- the real account, as supplied by verification Item 1A ----
        public const string RealOrganizationId = "org-aUyGCBpWvfaqal2KIUuYkU9t";
        public const string RealDevelopmentProjectId = "proj_GHPPdHzQWFh6VhXkPXV2dp7D";
        public const string RealProductionProjectId = "proj_tuL0VF1UOGgbPD1JX7wXW4k8";

        private static readonly AiProviderScope DevScope = new(
            OpenAiOptions.ProviderId, RealOrganizationId, RealDevelopmentProjectId,
            AiProviderEnvironment.Development);

        private static readonly AiProviderScope ProdScope = new(
            OpenAiOptions.ProviderId, RealOrganizationId, RealProductionProjectId,
            AiProviderEnvironment.Production);

        private static readonly DateTime Now = new(2026, 8, 16, 12, 0, 0, DateTimeKind.Utc);

        // =========================================================================================
        // Harness
        // =========================================================================================

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
            private readonly BusinessContext? _c;
            public FixedContext(BusinessContext? c) => _c = c;
            public Task<BusinessContext> GetCurrentAsync(CancellationToken ct = default) => Task.FromResult(_c!);
            public Task<BusinessContext?> TryGetCurrentAsync(CancellationToken ct = default) => Task.FromResult(_c);
        }

        /// A host configured as permissively as an operator could make it, for the scope given.
        private static IConfiguration ConfigFor(
            string? organizationId, string? projectId, string? environment,
            params (string, string?)[] overrides)
        {
            var d = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                [AiDestinationResolver.DestinationClassKey] = "ApprovedExternalProcessor",
                ["AiService:DeploymentMode"] = "LocalLoopback",
                ["AiService:BaseUrl"] = "http://localhost:8000",
                ["AiService:Secret"] = "a-real-looking-configured-value",

                ["Ai:Providers:OpenAI:Enabled"] = "true",
                ["Ai:Providers:OpenAI:Development:Enabled"] = "true",
                ["Ai:Providers:OpenAI:Production:Enabled"] = "true",

                ["OpenAi:OrganizationId"] = organizationId,
                ["OpenAi:ProjectId"] = projectId,
                ["OpenAi:Environment"] = environment,
                ["OpenAi:Model"] = "test-model",
                ["OpenAi:TimeoutSeconds"] = "5",
                ["OPENAI_API_KEY"] = "test-key-not-a-real-credential",
            };
            foreach (var (k, v) in overrides) d[k] = v;
            return new ConfigurationBuilder().AddInMemoryCollection(d).Build();
        }

        private static IConfiguration DevelopmentConfig(params (string, string?)[] overrides)
            => ConfigFor(RealOrganizationId, RealDevelopmentProjectId, "Development", overrides);

        private static IConfiguration ProductionConfig(params (string, string?)[] overrides)
            => ConfigFor(RealOrganizationId, RealProductionProjectId, "Production", overrides);

        private static AiEgressPolicy Policy(IAiProviderAuthority authority, IConfiguration config)
            => new(new FixedContext(new BusinessContext
            {
                CompanyId = Company, EmployeeId = 7, UserId = "u7", Source = BusinessContextSource.Http,
            }), config, NullLogger<AiEgressPolicy>.Instance, authority);

        private static AiEgressRequest Request(
            object? scope,
            AiDataClassification classification = AiDataClassification.FinancialAggregate,
            AiEgressDestinationClass destination = AiEgressDestinationClass.ApprovedExternalProcessor)
            => new()
            {
                Purpose = AiEgressPurpose.CashflowForecast,
                Destination = destination,
                ProviderScope = scope,
                Classification = classification,
                DataCompanyId = Company,
                PayloadBytes = 512,
            };

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

        private static AiProviderRequest ProviderRequest(AiEgressApproval approval, string? payload = null)
            => new()
            {
                Approval = approval,
                SystemPrompt = AiCashflowForecastValidator.SystemPrompt,
                PayloadJson = payload ?? "{\"openingCash\":100}",
                MaxOutputTokens = 200,
                CorrelationId = "id-matrix",
            };

        private static string RepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "CrossBuy.sln"))) dir = dir.Parent;
            Assert.NotNull(dir);
            return dir!.FullName;
        }

        // =========================================================================================
        // ID1—ID3 — the identifiers are configured, exact, and never interchangeable.
        // =========================================================================================

        [Fact]   // ID1
        public void ID1_development_configuration_resolves_the_exact_development_scope()
        {
            var scope = OpenAiOptions.FromConfiguration(DevelopmentConfig()).Scope;

            Assert.Equal(OpenAiOptions.ProviderId, scope.ProviderId);
            Assert.Equal(RealOrganizationId, scope.OrganizationId);
            Assert.Equal(RealDevelopmentProjectId, scope.ProjectId);
            Assert.Equal(AiProviderEnvironment.Development, scope.Environment);
            Assert.True(scope.IsComplete);
            Assert.True(scope.Covers(DevScope));

            // The gitignored per-machine file is checked when it exists. Skipped rather than asserted
            // absent: CI has no such file, and a test that failed there would be testing the checkout.
            var devFile = Path.Combine(RepoRoot(), "CrossBuy", "appsettings.Development.json");
            if (File.Exists(devFile))
            {
                var text = File.ReadAllText(devFile);
                Assert.Contains(RealDevelopmentProjectId, text, StringComparison.Ordinal);
                Assert.DoesNotContain(RealProductionProjectId, text, StringComparison.Ordinal);
            }
        }

        [Fact]   // ID2
        public void ID2_production_configuration_resolves_the_exact_production_scope()
        {
            var scope = OpenAiOptions.FromConfiguration(ProductionConfig()).Scope;

            Assert.Equal(RealOrganizationId, scope.OrganizationId);
            Assert.Equal(RealProductionProjectId, scope.ProjectId);
            Assert.Equal(AiProviderEnvironment.Production, scope.Environment);
            Assert.True(scope.IsComplete);

            // The TRACKED production template is asserted directly, because it is the file that actually
            // ships. A Development project id appearing in it would be the cross-contamination this
            // increment exists to prevent, and it would be invisible from configuration objects alone.
            var prodFile = Path.Combine(RepoRoot(), "CrossBuy", "appsettings.Production.json");
            var text = File.ReadAllText(prodFile);
            Assert.Contains(RealProductionProjectId, text, StringComparison.Ordinal);
            Assert.DoesNotContain(RealDevelopmentProjectId, text, StringComparison.Ordinal);

            // And the production template must not carry a credential of any shape.
            Assert.DoesNotContain("sk-", text, StringComparison.Ordinal);
        }

        [Fact]   // ID3
        public void ID3_the_two_project_ids_are_different_and_neither_scope_covers_the_other()
        {
            Assert.NotEqual(RealDevelopmentProjectId, RealProductionProjectId);

            // Same organisation, different project, different environment: two of the four parts differ,
            // and either one alone is enough to deny.
            Assert.Equal(DevScope.OrganizationId, ProdScope.OrganizationId);
            Assert.False(DevScope.Covers(ProdScope));
            Assert.False(ProdScope.Covers(DevScope));
        }

        // =========================================================================================
        // ID4—ID10 — the scope gate, driven with the REAL identifiers.
        // =========================================================================================

        [Fact]   // ID4
        public void ID4_a_development_approval_cannot_cover_the_production_scope()
            => Assert.False(AiTestProviderAuthority.ApprovedFor(DevScope).Assess(Now, ProdScope).IsApproved);

        [Fact]   // ID5
        public void ID5_a_production_approval_cannot_cover_the_development_scope()
            => Assert.False(AiTestProviderAuthority.ApprovedFor(ProdScope).Assess(Now, DevScope).IsApproved);

        [Fact]   // ID6
        public async Task ID6_a_wrong_organization_id_denies()
        {
            var wrong = new AiProviderScope(
                OpenAiOptions.ProviderId, "org-SOMEONE-ELSES-ORGANISATION",
                RealDevelopmentProjectId, AiProviderEnvironment.Development);

            var authority = AiTestProviderAuthority.ApprovedFor(DevScope);
            Assert.False(authority.Assess(Now, wrong).IsApproved);

            var d = await Policy(authority, DevelopmentConfig()).EvaluateAsync(Request(wrong));
            Assert.False(d.Allowed);
            Assert.Equal(AiEgressDenyReason.ProviderNotApproved, d.Reason);
        }

        [Fact]   // ID7
        public async Task ID7_a_wrong_project_id_denies()
        {
            var wrong = new AiProviderScope(
                OpenAiOptions.ProviderId, RealOrganizationId,
                "proj_SOMEONE_ELSES_PROJECT", AiProviderEnvironment.Development);

            var authority = AiTestProviderAuthority.ApprovedFor(DevScope);
            Assert.False(authority.Assess(Now, wrong).IsApproved);

            var d = await Policy(authority, DevelopmentConfig()).EvaluateAsync(Request(wrong));
            Assert.False(d.Allowed);
            Assert.Equal(AiEgressDenyReason.ProviderNotApproved, d.Reason);
        }

        [Fact]   // ID8
        public async Task ID8_an_unknown_environment_denies()
        {
            // Both identifiers correct; only the environment is unstated.
            var config = ConfigFor(RealOrganizationId, RealDevelopmentProjectId, environment: null);
            Assert.Equal(AiProviderEnvironment.Unknown, OpenAiOptions.FromConfiguration(config).Environment);
            Assert.False(OpenAiOptions.FromConfiguration(config).Scope.IsComplete);

            var d = await Policy(AiTestProviderAuthority.ApprovedFor(DevScope), config)
                .EvaluateAsync(Request(OpenAiOptions.FromConfiguration(config).Scope));

            Assert.False(d.Allowed);
            Assert.Equal(AiEgressDenyReason.ProviderScopeMissing, d.Reason);

            // An unrecognised value is Unknown too — it is not coerced to the nearest match.
            Assert.Equal(AiProviderEnvironment.Unknown, OpenAiOptions
                .FromConfiguration(ConfigFor(RealOrganizationId, RealDevelopmentProjectId, "Staging"))
                .Environment);
        }

        [Fact]   // ID9
        public async Task ID9_a_missing_organization_id_denies()
        {
            var config = ConfigFor(organizationId: null, projectId: RealDevelopmentProjectId, environment: "Development");
            var scope = OpenAiOptions.FromConfiguration(config).Scope;
            Assert.False(scope.IsComplete);

            var d = await Policy(AiTestProviderAuthority.ApprovedFor(DevScope), config).EvaluateAsync(Request(scope));
            Assert.False(d.Allowed);
            Assert.Equal(AiEgressDenyReason.ProviderScopeMissing, d.Reason);
        }

        [Fact]   // ID10
        public async Task ID10_a_missing_project_id_denies()
        {
            var config = ConfigFor(RealOrganizationId, projectId: "   ", environment: "Development");
            var scope = OpenAiOptions.FromConfiguration(config).Scope;
            Assert.False(scope.IsComplete);

            var d = await Policy(AiTestProviderAuthority.ApprovedFor(DevScope), config).EvaluateAsync(Request(scope));
            Assert.False(d.Allowed);
            Assert.Equal(AiEgressDenyReason.ProviderScopeMissing, d.Reason);
        }

        // =========================================================================================
        // ID11—ID15 — the three impostors, and the two states that must not move.
        //
        // Each of these is something that looks like approval to someone who wants to make a call today.
        // All of them run against the PRODUCT authority, which is the record a running CrossBuy uses.
        // =========================================================================================

        [Fact]   // ID11
        public async Task ID11_the_api_key_being_present_does_not_create_approval()
        {
            var config = DevelopmentConfig();

            // Presence only. The value is never read into an assertion, a message or a variable here.
            Assert.True(OpenAiOptions.HasApiKey(config));

            var authority = new AiProviderAuthority();
            Assert.False(authority.Assess(Now, DevScope).IsApproved);

            var d = await Policy(authority, config).EvaluateAsync(Request(DevScope));
            Assert.False(d.Allowed);
            Assert.Equal(AiEgressDenyReason.ProviderNotApproved, d.Reason);
            Assert.Null(d.Approval);
        }

        [Fact]   // ID12
        public async Task ID12_the_enabled_switch_does_not_create_approval()
        {
            var config = DevelopmentConfig();
            var switchboard = new AiProviderSwitchboard(config);

            Assert.True(switchboard.IsEnabled(OpenAiOptions.ProviderId, AiProviderEnvironment.Development));

            var authority = new AiProviderAuthority();
            Assert.False(authority.Assess(Now, DevScope).IsApproved);
            Assert.False((await Policy(authority, config).EvaluateAsync(Request(DevScope))).Allowed);
        }

        [Fact]   // ID13
        public async Task ID13_the_synthetic_classification_does_not_create_approval()
        {
            var d = await Policy(new AiProviderAuthority(), DevelopmentConfig())
                .EvaluateAsync(Request(DevScope, AiDataClassification.SyntheticTestData));

            Assert.False(d.Allowed);

            // The refusal is a GOVERNANCE refusal, not a classification refusal — which is the precise
            // claim: synthetic content is permitted by the matrix and still cannot leave, because the
            // matrix was never what was stopping it.
            Assert.Equal(AiEgressDenyReason.ProviderNotApproved, d.Reason);
            Assert.Null(d.Approval);
        }

        [Fact]   // ID14
        public void ID14_missing_zdr_evidence_still_denies_under_the_real_identifiers()
        {
            // A candidate carrying the REAL identifiers and every other fact satisfied, whose only defect
            // is that no zero-retention grant has been evidenced for this account.
            var candidate = new AiProviderCandidate
            {
                ProviderId = OpenAiOptions.ProviderId,
                ProviderName = "OpenAI API",
                OrganizationId = RealOrganizationId,
                ProjectId = RealDevelopmentProjectId,
                Environment = AiProviderEnvironment.Development,
                LegalEntity = "TEST FIXTURE — not a recorded fact",
                AccountOwner = "TEST FIXTURE — not a recorded fact",
                CommercialTier = "TEST FIXTURE — not a recorded fact",
                TrainsOnCustomerData = false,
                ProviderRetention = TimeSpan.Zero,
                ResidencyRegion = AiTestProviderAuthority.Region,
                SubprocessorPositionAccepted = AiProviderFact.Yes,
                EncryptionInTransit = AiProviderFact.Yes,
                DeletionControlsAvailable = AiProviderFact.Yes,
                ContractInPlace = AiProviderFact.Yes,
                ZeroRetentionGranted = AiProviderFact.Unknown,   // the one defect
            };

            var a = AiProviderEvaluator.Evaluate(
                candidate, AiTestProviderAuthority.CompliantRequirement(),
                AiTestProviderAuthority.FullApproval(), Now);

            Assert.False(a.IsApproved);
            Assert.Contains("ZeroRetentionGranted", a.MissingFacts);

            // And a grant claimed WITHOUT scoped evidence is still not a grant.
            var claimedButUnevidenced = new AiProviderCandidate
            {
                ProviderId = candidate.ProviderId,
                ProviderName = candidate.ProviderName,
                OrganizationId = candidate.OrganizationId,
                ProjectId = candidate.ProjectId,
                Environment = candidate.Environment,
                LegalEntity = candidate.LegalEntity,
                AccountOwner = candidate.AccountOwner,
                CommercialTier = candidate.CommercialTier,
                TrainsOnCustomerData = candidate.TrainsOnCustomerData,
                ProviderRetention = candidate.ProviderRetention,
                ResidencyRegion = candidate.ResidencyRegion,
                SubprocessorPositionAccepted = candidate.SubprocessorPositionAccepted,
                EncryptionInTransit = candidate.EncryptionInTransit,
                DeletionControlsAvailable = candidate.DeletionControlsAvailable,
                ContractInPlace = candidate.ContractInPlace,
                ZeroRetentionGranted = AiProviderFact.Yes,
                Evidence = Array.Empty<AiProviderEvidence>(),
            };

            Assert.False(AiProviderEvaluator.ZeroRetentionEvidenceIsSufficient(claimedButUnevidenced));
            Assert.Contains("ZeroRetentionGrantedEvidence", AiProviderEvaluator.Evaluate(
                claimedButUnevidenced, AiTestProviderAuthority.CompliantRequirement(),
                AiTestProviderAuthority.FullApproval(), Now).MissingFacts);
        }

        [Fact]   // ID15
        public async Task ID15_production_remains_denied_with_everything_configured()
        {
            var config = ProductionConfig();
            var authority = new AiProviderAuthority();

            Assert.False(authority.Assess(Now, ProdScope).IsApproved);
            Assert.Equal(AiProviderState.Unknown, authority.Assess(Now, ProdScope).State);

            var d = await Policy(authority, config).EvaluateAsync(Request(ProdScope));
            Assert.False(d.Allowed);
            Assert.Equal(AiEgressDenyReason.ProviderNotApproved, d.Reason);

            // And the shipped production template must not enable the provider in ANY environment.
            var text = File.ReadAllText(Path.Combine(RepoRoot(), "CrossBuy", "appsettings.Production.json"));
            using var doc = JsonDocument.Parse(text, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip });
            var openAi = doc.RootElement.GetProperty("Ai").GetProperty("Providers").GetProperty("OpenAI");
            Assert.False(openAi.GetProperty("Enabled").GetBoolean());
            Assert.False(openAi.GetProperty("Development").GetProperty("Enabled").GetBoolean());
            Assert.False(openAi.GetProperty("Production").GetProperty("Enabled").GetBoolean());
        }

        // =========================================================================================
        // ID16—ID18 — token replay, across every boundary a token could be carried over.
        //
        // These use REAL approvals minted by the REAL policy. The token is authentic in each case; what
        // is wrong is the destination it is being presented at.
        // =========================================================================================

        [Fact]   // ID16
        public async Task ID16_an_internal_approval_cannot_be_replayed_at_openai()
        {
            var config = DevelopmentConfig((AiDestinationResolver.DestinationClassKey, "Internal"));

            var internalDecision = await Policy(AiTestProviderAuthority.Unapproved(), config)
                .EvaluateAsync(Request(null, destination: AiEgressDestinationClass.Internal));

            Assert.True(internalDecision.Allowed, internalDecision.Reason?.ToString());
            Assert.Null(internalDecision.Approval!.ProviderScope);

            var (adapter, handler) = Adapter(DevelopmentConfig());
            var result = await adapter.SendAsync(ProviderRequest(internalDecision.Approval));

            Assert.Equal(AiProviderOutcome.Refused, result.Outcome);
            Assert.Equal("approval:not-for-external-processor", result.Reason);
            Assert.Equal(0, handler.Calls);
        }

        [Fact]   // ID17
        public async Task ID17_a_development_token_cannot_be_replayed_against_production()
        {
            var devDecision = await Policy(AiTestProviderAuthority.ApprovedFor(DevScope), DevelopmentConfig())
                .EvaluateAsync(Request(DevScope));
            Assert.True(devDecision.Allowed, devDecision.Reason?.ToString());

            var (adapter, handler) = Adapter(ProductionConfig());
            var result = await adapter.SendAsync(ProviderRequest(devDecision.Approval!));

            Assert.Equal(AiProviderOutcome.Refused, result.Outcome);
            Assert.Equal("approval:scope-mismatch", result.Reason);
            Assert.Equal(0, handler.Calls);
        }

        [Fact]   // ID18
        public async Task ID18_a_production_token_cannot_be_replayed_against_development()
        {
            var prodDecision = await Policy(AiTestProviderAuthority.ApprovedFor(ProdScope), ProductionConfig())
                .EvaluateAsync(Request(ProdScope));
            Assert.True(prodDecision.Allowed, prodDecision.Reason?.ToString());

            var (adapter, handler) = Adapter(DevelopmentConfig());
            var result = await adapter.SendAsync(ProviderRequest(prodDecision.Approval!));

            Assert.Equal(AiProviderOutcome.Refused, result.Outcome);
            Assert.Equal("approval:scope-mismatch", result.Reason);
            Assert.Equal(0, handler.Calls);
        }

        // =========================================================================================
        // THE HEADLINE. With the real identifiers configured, both switches on and a key present, the
        // product record still refuses — and the readiness report says exactly why.
        // =========================================================================================

        [Fact]
        public void The_development_readiness_report_separates_configured_from_approved()
        {
            var config = DevelopmentConfig();
            var report = OpenAiDevelopmentReadiness.Evaluate(
                config, new AiProviderAuthority(), new AiProviderSwitchboard(config), Now);

            // CONFIGURED and ENABLED are now true — this increment's actual result.
            Assert.True(report.Configured);
            Assert.True(report.Enabled);

            // APPROVED and CALL PERMITTED are false, and they are what matter.
            Assert.False(report.Approved);
            Assert.False(report.CallPermitted);
            Assert.Equal(AiProviderState.Unknown, report.AuthorityState);

            Assert.Equal(DevScope, report.RequestedScope);

            var byName = report.Gates.ToDictionary(g => g.Name, g => g.Satisfied);
            Assert.True(byName["ProviderScopeComplete"]);
            Assert.True(byName["OrganizationIdConfigured"]);
            Assert.True(byName["ProjectIdConfigured"]);
            Assert.True(byName["EnvironmentStated"]);
            Assert.True(byName["ApiKeyPresent"]);
            Assert.True(byName["ProviderSwitchEnabled"]);
            Assert.True(byName["SyntheticPayloadGuardReady"]);

            Assert.False(byName["ProviderApproved"]);
            Assert.False(byName["ZeroRetentionEvidence"]);
            Assert.False(byName["SecurityApproval"]);
            Assert.False(byName["LegalDataProtectionApproval"]);
            Assert.False(byName["BusinessOwnerApproval"]);
            Assert.False(byName["PricingConfigured"]);

            // The report names the next thing to fix rather than a generic refusal.
            Assert.NotNull(report.FirstBlocker);
            Assert.Equal("ProviderApproved", report.FirstBlocker!.Name);
        }

        // The report must never leak the credential, in any form. It is generated with a key present and
        // then searched for any trace of it.
        [Fact]
        public void The_readiness_report_never_reveals_the_api_key()
        {
            const string sentinel = "test-key-value-that-must-never-appear";
            var config = DevelopmentConfig(("OPENAI_API_KEY", sentinel));

            var text = OpenAiDevelopmentReadiness.Evaluate(
                config, new AiProviderAuthority(), new AiProviderSwitchboard(config), Now).Describe();

            Assert.DoesNotContain(sentinel, text, StringComparison.Ordinal);
            Assert.DoesNotContain(sentinel[..8], text, StringComparison.Ordinal);   // not even a prefix
            Assert.DoesNotContain(sentinel.Length.ToString(), text, StringComparison.Ordinal);
            Assert.Contains("ApiKeyPresent", text, StringComparison.Ordinal);
            Assert.Contains("present", text, StringComparison.Ordinal);
        }

        // A readiness report that could become a second authority would defeat the first one.
        [Fact]
        public void The_readiness_report_cannot_authorise_anything()
        {
            var src = File.ReadAllText(Path.Combine(
                RepoRoot(), "CrossBuy", "BL", "Platform", "Ai", "OpenAiDevelopmentReadiness.cs"));

            Assert.DoesNotContain("new AiEgressApproval", src, StringComparison.Ordinal);
            Assert.DoesNotContain("ApprovedExternalProcessor =", src, StringComparison.Ordinal);
            Assert.DoesNotContain("IAiExternalProvider", src, StringComparison.Ordinal);
            Assert.DoesNotContain("HttpClient", src, StringComparison.Ordinal);
        }
    }
}
