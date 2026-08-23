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
    // INCREMENT 4.9 — THE PROVIDER SCOPE MATRIX (SCOPE1—SCOPE25).
    //
    // THE DEFECT THIS FILE EXISTS FOR. Until this increment a governance approval named a PROVIDER and
    // nothing else. "OpenAI is approved" was the strongest sentence the model could express, and it was
    // true of every organisation, every project and every environment at once. So a development approval
    // — the cheap one, the one obtained first, the one signed while the real review is still running —
    // authorised production traffic, and no code anywhere could tell the difference. The evidence had the
    // same hole: a zero-retention grant obtained for a development project satisfied a production
    // candidate, because evidence recorded WHAT was granted and never FOR WHICH ACCOUNT.
    //
    // An approval is now bound to a four-part scope — provider, organisation, project, environment — and
    // every one of the four must match exactly. There is no wildcard, no inheritance between
    // environments, and no default: an unstated environment is Unknown and Unknown denies. Development is
    // emphatically NOT the default, because assuming the less-dangerous-sounding value is precisely how a
    // production request gets served by a development approval.
    //
    // HOW THESE TESTS ARE BUILT. Nothing here hand-builds an approved assessment and nothing hand-builds
    // an AiEgressApproval — the token's constructor is internal to the product assembly, so a test cannot
    // reach around the boundary even if it wanted to. Positive cases run the REAL evaluator over a
    // complete record and the REAL policy over a real request. If a rule tightens, the fixtures stop
    // being approved and these tests fail loudly rather than passing against a lie.
    //
    // NO TEST IN THIS FILE CONTACTS OPENAI. The adapter cases run against a counting fake transport and
    // the count is itself an assertion.
    public class AiProviderScopeMatrixTests
    {
        private const int CompanyA = 1;
        private const int CompanyB = 65;

        // ---- The two scopes the whole matrix turns on -------------------------------------------
        //
        // Obvious TEST placeholders. A real organisation or project identifier in tracked source would be
        // a live account identifier in git, and worse: it would make a fixture indistinguishable from a
        // genuine approval record.
        private static readonly AiProviderScope Dev = AiTestProviderAuthority.OpenAiTestScope;

        private static readonly AiProviderScope Prod = new(
            OpenAiOptions.ProviderId,
            AiTestProviderAuthority.TestOrganizationId,
            "TEST-PROJECT-PROD",
            AiProviderEnvironment.Production);

        private static readonly AiProviderScope OtherDevProject = new(
            OpenAiOptions.ProviderId,
            AiTestProviderAuthority.TestOrganizationId,
            "TEST-PROJECT-DEV-B",
            AiProviderEnvironment.Development);

        private static readonly AiProviderScope OtherOrganization = new(
            OpenAiOptions.ProviderId,
            "TEST-ORG-B",
            AiTestProviderAuthority.TestProjectId,
            AiProviderEnvironment.Development);

        private static readonly DateTime Now = new(2026, 8, 16, 0, 0, 0, DateTimeKind.Utc);

        // =========================================================================================
        // Harness
        // =========================================================================================

        private sealed class FixedContext : IBusinessContextAccessor
        {
            private readonly BusinessContext? _c;
            public FixedContext(BusinessContext? c) => _c = c;
            public Task<BusinessContext> GetCurrentAsync(CancellationToken ct = default) => Task.FromResult(_c!);
            public Task<BusinessContext?> TryGetCurrentAsync(CancellationToken ct = default) => Task.FromResult(_c);
        }

        private sealed class CountingHandler : HttpMessageHandler
        {
            public int Calls;

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            {
                Interlocked.Increment(ref Calls);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        "{\"model\":\"test-model\",\"choices\":[{\"message\":{\"content\":\"{}\"}}]," +
                        "\"usage\":{\"prompt_tokens\":1,\"completion_tokens\":1}}",
                        Encoding.UTF8, "application/json"),
                });
            }
        }

        private sealed class SingleClientFactory : IHttpClientFactory
        {
            private readonly HttpMessageHandler _h;
            public SingleClientFactory(HttpMessageHandler h) => _h = h;
            public HttpClient CreateClient(string name) => new(_h, disposeHandler: false);
        }

        /// The configuration an operator would write if they believed the settings file granted approval:
        /// destination pinned to ApprovedExternalProcessor, both switches on, a credential present. It is
        /// never the variable in this file — every green test here says that belief is false.
        private static IConfiguration Config(AiProviderScope callScope, params (string, string?)[] overrides)
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

                ["OpenAi:OrganizationId"] = callScope.OrganizationId,
                ["OpenAi:ProjectId"] = callScope.ProjectId,
                ["OpenAi:Environment"] = callScope.Environment.ToString(),
                ["OPENAI_API_KEY"] = "test-key-not-a-real-credential",
                ["OpenAi:Model"] = "test-model",
                ["OpenAi:TimeoutSeconds"] = "5",
            };
            foreach (var (k, v) in overrides) d[k] = v;
            return new ConfigurationBuilder().AddInMemoryCollection(d).Build();
        }

        private static AiEgressPolicy Policy(IAiProviderAuthority authority, IConfiguration config, int company = CompanyA)
            => new(new FixedContext(new BusinessContext
            {
                CompanyId = company, EmployeeId = 7, UserId = "u7", Source = BusinessContextSource.Http,
            }), config, NullLogger<AiEgressPolicy>.Instance, authority);

        private static AiEgressRequest Request(
            object? providerScope,
            AiDataClassification classification = AiDataClassification.FinancialAggregate,
            AiEgressDestinationClass destination = AiEgressDestinationClass.ApprovedExternalProcessor,
            int dataCompany = CompanyA)
            => new()
            {
                Purpose = AiEgressPurpose.CashflowForecast,
                Destination = destination,
                ProviderScope = providerScope,
                Classification = classification,
                DataCompanyId = dataCompany,
                PayloadBytes = 512,
            };

        /// <summary>Sentinel for "leave this scope field as the scope says".</summary>
        /// <remarks>
        /// The product's candidate and approval are sealed CLASSES, not records, so `with` is unavailable
        /// and a null override cannot be told apart from "not supplied". A const sentinel keeps the
        /// builder usable as an optional-parameter default, which a null-object could not be.
        /// </remarks>
        private const string Keep = " keep";

        /// A complete, compliant candidate for one scope. Built here rather than reused from the shared
        /// fixture because several cases below need to break exactly one scope field while leaving every
        /// other fact genuinely satisfactory.
        private static AiProviderCandidate CandidateFor(
            AiProviderScope scope,
            AiProviderScope? evidenceScope = null,
            string? organizationId = Keep,
            string? projectId = Keep,
            AiProviderEnvironment? environment = null,
            AiProviderEvidence[]? evidence = null)
            => new()
            {
                ProviderId = scope.ProviderId,
                ProviderName = "TEST-ONLY FIXTURE — not a real provider",
                OrganizationId = organizationId == Keep ? scope.OrganizationId : organizationId,
                ProjectId = projectId == Keep ? scope.ProjectId : projectId,
                Environment = environment ?? scope.Environment,
                LegalEntity = "Fixture Ltd",
                AccountOwner = "fixture-owner",
                CommercialTier = "Fixture Enterprise",
                TrainsOnCustomerData = false,
                ProviderRetention = TimeSpan.Zero,
                ResidencyRegion = AiTestProviderAuthority.Region,
                SubprocessorPositionAccepted = AiProviderFact.Yes,
                EncryptionInTransit = AiProviderFact.Yes,
                DeletionControlsAvailable = AiProviderFact.Yes,
                ContractInPlace = AiProviderFact.Yes,
                ZeroRetentionGranted = AiProviderFact.Yes,
                Evidence = evidence ?? new[]
                {
                    new AiProviderEvidence
                    {
                        Fact = AiProviderEvidenceKeys.ZeroRetentionGranted,
                        Level = AiEvidenceLevel.OwnerAttested,
                        Owner = AiFactOwner.Procurement,
                        Reference = "TEST FIXTURE — no real grant exists",
                        Scope = evidenceScope ?? scope,
                    },
                },
            };

        /// <param name="omitValidFrom">Distinct from a null <paramref name="validFrom"/>, which means
        /// "use the default". Only this flag removes the start date.</param>
        private static AiProviderOwnerApproval Approval(
            DateTime? validFrom = null, DateTime? expires = null, bool revoked = false,
            bool omitValidFrom = false, bool omitExpiry = false)
            => new()
            {
                SecurityApproved = true,
                DataProtectionApproved = true,
                BusinessOwnerApproved = true,
                ApprovedBy = "test-fixture",
                ApprovedAtUtc = Now.AddDays(-30),
                ValidFromUtc = omitValidFrom ? null : validFrom ?? Now.AddDays(-30),
                ExpiresAtUtc = omitExpiry ? null : expires ?? Now.AddYears(1),
                Revoked = revoked,
            };

        private static AiProviderAssessment Assess(
            AiProviderCandidate candidate, AiProviderOwnerApproval? approval = null)
            => AiProviderEvaluator.Evaluate(
                candidate, AiTestProviderAuthority.CompliantRequirement(), approval ?? Approval(), Now);

        // =========================================================================================
        // SCOPE1—SCOPE3 — an incomplete scope is not a scope.
        //
        // These are MissingFacts rather than policy breaches: the record has not said what it is about,
        // which is an absence of information, not a judgement about the provider.
        // =========================================================================================

        [Fact]   // SCOPE1
        public void SCOPE1_an_unknown_environment_is_not_approved()
        {
            var candidate = CandidateFor(Dev, environment: AiProviderEnvironment.Unknown);
            var a = Assess(candidate);

            Assert.False(a.IsApproved);
            Assert.Contains("Environment", a.MissingFacts);

            // And the scope object itself refuses to call that complete, which is what the runtime gates on.
            Assert.False(new AiProviderScope(
                Dev.ProviderId, Dev.OrganizationId, Dev.ProjectId, AiProviderEnvironment.Unknown).IsComplete);
        }

        [Fact]   // SCOPE2
        public void SCOPE2_a_missing_organization_id_is_not_approved()
        {
            var a = Assess(CandidateFor(Dev, organizationId: null));

            Assert.False(a.IsApproved);
            Assert.Contains("OrganizationId", a.MissingFacts);
            Assert.False(new AiProviderScope(Dev.ProviderId, null, Dev.ProjectId, Dev.Environment).IsComplete);
        }

        [Fact]   // SCOPE3
        public void SCOPE3_a_missing_project_id_is_not_approved()
        {
            var a = Assess(CandidateFor(Dev, projectId: "   "));

            Assert.False(a.IsApproved);
            Assert.Contains("ProjectId", a.MissingFacts);
            Assert.False(new AiProviderScope(Dev.ProviderId, Dev.OrganizationId, "   ", Dev.Environment).IsComplete);
        }

        // =========================================================================================
        // SCOPE4—SCOPE8 — the matching rules. Exact on all four parts, in both directions.
        // =========================================================================================

        [Fact]   // SCOPE4
        public void SCOPE4_a_development_record_covers_a_matching_development_request()
        {
            var a = AiTestProviderAuthority.ApprovedFor(Dev).Assess(Now, Dev);

            Assert.Equal(AiProviderState.ApprovedExternalProcessor, a.State);
            Assert.True(a.IsApproved);
            Assert.True(CandidateFor(Dev).Scope.Covers(Dev));
        }

        [Fact]   // SCOPE5
        public void SCOPE5_a_development_record_does_not_cover_a_production_request()
        {
            Assert.False(AiTestProviderAuthority.ApprovedFor(Dev).Assess(Now, Prod).IsApproved);
            Assert.False(Dev.Covers(Prod));
        }

        // The reverse is equally forbidden, and it is worth its own case: it is tempting to read
        // production approval as "the stricter one, therefore it must include development". It does not.
        // A production approval is about a production project, and it says nothing about any other.
        [Fact]   // SCOPE6
        public void SCOPE6_a_production_record_does_not_cover_a_development_request()
        {
            Assert.False(AiTestProviderAuthority.ApprovedFor(Prod).Assess(Now, Dev).IsApproved);
            Assert.False(Prod.Covers(Dev));
        }

        [Fact]   // SCOPE7
        public void SCOPE7_one_development_project_does_not_cover_another()
        {
            Assert.False(AiTestProviderAuthority.ApprovedFor(Dev).Assess(Now, OtherDevProject).IsApproved);
            Assert.False(Dev.Covers(OtherDevProject));
        }

        [Fact]   // SCOPE8
        public void SCOPE8_one_organization_does_not_cover_another()
        {
            Assert.False(AiTestProviderAuthority.ApprovedFor(Dev).Assess(Now, OtherOrganization).IsApproved);
            Assert.False(Dev.Covers(OtherOrganization));
        }

        // =========================================================================================
        // SCOPE9—SCOPE10 — EVIDENCE is scope-bound too, and this is the subtler half of the increment.
        //
        // A zero-retention grant is issued to a named organisation and project. Before Increment 4.9 the
        // evidence recorded WHAT was granted and never FOR WHICH ACCOUNT, so a genuine, correctly-levelled
        // grant obtained for the development project satisfied a production candidate. The evidence was
        // real; it was about the wrong thing.
        // =========================================================================================

        [Fact]   // SCOPE9
        public void SCOPE9_development_zdr_evidence_cannot_satisfy_a_production_project()
        {
            // A PRODUCTION candidate, complete in every other respect, holding a DEVELOPMENT grant.
            var candidate = CandidateFor(Prod, evidenceScope: Dev);

            Assert.False(AiProviderEvaluator.ZeroRetentionEvidenceIsSufficient(candidate));

            var a = Assess(candidate);
            Assert.False(a.IsApproved);
            Assert.Contains("ZeroRetentionGrantedEvidence", a.MissingFacts);
        }

        // The same rule in the other direction, plus the unscoped case: evidence that names no account at
        // all cannot establish a fact that is about an account.
        [Fact]   // SCOPE10
        public void SCOPE10_wrong_environment_or_unscoped_zdr_evidence_is_not_sufficient()
        {
            Assert.False(AiProviderEvaluator.ZeroRetentionEvidenceIsSufficient(
                CandidateFor(Dev, evidenceScope: Prod)));

            var unscoped = CandidateFor(Dev, evidence: new[]
            {
                new AiProviderEvidence
                {
                    Fact = AiProviderEvidenceKeys.ZeroRetentionGranted,
                    Level = AiEvidenceLevel.ContractProven,   // the STRONGEST level, and it still fails
                    Owner = AiFactOwner.DataProtectionLegal,
                    Reference = "TEST FIXTURE — grant with no account named",
                    Scope = null,
                },
            });

            Assert.False(AiProviderEvaluator.ZeroRetentionEvidenceIsSufficient(unscoped));
            Assert.Contains("ZeroRetentionGrantedEvidence", Assess(unscoped).MissingFacts);
        }

        // =========================================================================================
        // SCOPE11—SCOPE15 — THE VALIDITY WINDOW, both ends.
        //
        // Increment 4.2 closed the missing-expiry hole. A missing START is its mirror and was still open:
        // an approval with no beginning is valid retroactively for all time, so a record signed today
        // silently covers a call made last month and "we approved this on the 14th" is unprovable.
        // =========================================================================================

        [Fact]   // SCOPE11
        public void SCOPE11_an_approval_with_no_start_date_is_not_approval()
        {
            var a = Assess(CandidateFor(Dev), Approval(omitValidFrom: true));

            Assert.False(a.IsApproved);
            Assert.Equal(AiProviderState.OwnerDecisionRequired, a.State);
            Assert.Contains("start date", a.Reason, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]   // SCOPE12
        public void SCOPE12_an_approval_with_no_expiry_is_not_approval()
        {
            var a = Assess(CandidateFor(Dev), Approval(omitExpiry: true));

            Assert.False(a.IsApproved);
            Assert.Equal(AiProviderState.OwnerDecisionRequired, a.State);
            Assert.Contains("expiry", a.Reason, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]   // SCOPE13
        public void SCOPE13_an_approval_that_is_not_yet_in_force_is_not_approval()
        {
            var a = Assess(CandidateFor(Dev), Approval(validFrom: Now.AddDays(1), expires: Now.AddDays(30)));

            Assert.False(a.IsApproved);
            Assert.Equal(AiProviderState.OwnerDecisionRequired, a.State);
            Assert.Contains("not yet in force", a.Reason, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]   // SCOPE14
        public void SCOPE14_an_expired_approval_suspends()
        {
            var a = Assess(CandidateFor(Dev), Approval(validFrom: Now.AddDays(-30), expires: Now.AddDays(-1)));

            Assert.False(a.IsApproved);

            // Suspended, not Rejected: approval once existed, and an audit of what was sent and when
            // needs to be able to tell those two apart.
            Assert.Equal(AiProviderState.Suspended, a.State);
        }

        // A window that ends before — or exactly when — it begins authorises nothing, and it is almost
        // certainly a transcription error. REJECTED rather than silently read as expired: the record
        // itself is wrong and needs re-issuing, not re-confirming.
        [Theory]   // SCOPE15
        [InlineData(0)]     // starts exactly when it ends
        [InlineData(10)]    // starts after it ends
        public void SCOPE15_a_window_that_does_not_start_before_it_ends_is_invalid(int daysAfter)
        {
            var end = Now.AddDays(5);
            var a = Assess(CandidateFor(Dev), Approval(validFrom: end.AddDays(daysAfter), expires: end));

            Assert.False(a.IsApproved);
            Assert.Equal(AiProviderState.Rejected, a.State);
            Assert.Contains("validity window is invalid", a.Reason, StringComparison.OrdinalIgnoreCase);
        }

        // =========================================================================================
        // SCOPE16—SCOPE17 — the capability token carries the scope, and the adapter re-checks it.
        // =========================================================================================

        [Fact]   // SCOPE16
        public async Task SCOPE16_a_development_approval_cannot_mint_a_production_egress_approval()
        {
            var authority = AiTestProviderAuthority.ApprovedFor(Dev);
            var config = Config(Dev);

            // The same authority, the same configuration, the same everything — one field differs.
            var allowed = await Policy(authority, config).EvaluateAsync(Request(Dev));
            Assert.True(allowed.Allowed, allowed.Reason?.ToString());
            Assert.Equal(Dev, allowed.Approval!.ProviderScope);

            var denied = await Policy(authority, config).EvaluateAsync(Request(Prod));
            Assert.False(denied.Allowed);
            Assert.Equal(AiEgressDenyReason.ProviderNotApproved, denied.Reason);
            Assert.Null(denied.Approval);
        }

        // The replay case. The token is AUTHENTIC — minted by the real policy from a real approval — and
        // the adapter still refuses it, because the scope it was minted for is not the scope this call is
        // about. That is exactly what a capability token exists to prevent, and it is the reason the
        // adapter re-checks something the policy already checked.
        [Fact]   // SCOPE17
        public async Task SCOPE17_a_development_approval_cannot_be_replayed_against_a_production_call()
        {
            var devConfig = Config(Dev);
            var decision = await Policy(AiTestProviderAuthority.ApprovedFor(Dev), devConfig)
                .EvaluateAsync(Request(Dev));
            Assert.True(decision.Allowed, decision.Reason?.ToString());

            // Now point the ADAPTER at the production project while handing it the development token.
            var prodConfig = Config(Prod);
            var handler = new CountingHandler();
            var adapter = new OpenAiProviderAdapter(
                new SingleClientFactory(handler), prodConfig,
                new AiProviderSwitchboard(prodConfig), new AiCircuitBreaker(),
                new AiRateLimiter(prodConfig),
                new AiUsageGuard(prodConfig, new AiConfiguredPricingProvider(prodConfig)),
                new LoggingAiEgressAuditSink(NullLogger<LoggingAiEgressAuditSink>.Instance),
                NullLogger<OpenAiProviderAdapter>.Instance);

            var result = await adapter.SendAsync(new AiProviderRequest
            {
                Approval = decision.Approval!,
                SystemPrompt = AiCashflowForecastValidator.SystemPrompt,
                PayloadJson = "{\"openingCash\":100}",
                MaxOutputTokens = 200,
                CorrelationId = "scope17",
            });

            Assert.Equal(AiProviderOutcome.Refused, result.Outcome);
            Assert.Equal("approval:scope-mismatch", result.Reason);

            // The count is the assertion: nothing left the process.
            Assert.Equal(0, handler.Calls);
        }

        // =========================================================================================
        // SCOPE18—SCOPE20 — three things that look like approval and are not.
        //
        // Configuration states INTENT. A credential states CAPABILITY. A switch states AVAILABILITY.
        // Only the governance record states AUTHORITY, and none of the other three can manufacture it.
        // =========================================================================================

        [Fact]   // SCOPE18
        public async Task SCOPE18_configuration_cannot_create_a_project_approval()
        {
            // Every settings line an operator could reach for, all set to their most permissive value.
            var config = Config(Dev,
                (AiDestinationResolver.DestinationClassKey, "ApprovedExternalProcessor"),
                ("AiService:EgressEnabled", "true"),
                ("Ai:Providers:OpenAI:Enabled", "true"),
                ("Ai:Providers:OpenAI:Development:Enabled", "true"));

            // The PRODUCT authority — the record a running CrossBuy actually uses — approves nobody.
            var authority = new AiProviderAuthority();

            Assert.False(authority.Assess(Now, Dev).IsApproved);
            Assert.Equal(AiEgressDestinationClass.UnapprovedExternal,
                AiDestinationResolver.Resolve(config, AiEgressPurpose.CashflowForecast,
                    authority.Assess(Now, Dev).State));

            var d = await Policy(authority, config).EvaluateAsync(Request(Dev));
            Assert.False(d.Allowed);
            Assert.Equal(AiEgressDenyReason.ProviderNotApproved, d.Reason);
            Assert.Null(d.Approval);
        }

        // Having the key means the call COULD be made. It has never meant the call MAY be made, and the
        // presence of a credential must not move any scope one step closer to approved.
        [Fact]   // SCOPE19
        public async Task SCOPE19_an_api_key_cannot_create_a_project_approval()
        {
            // NOT written in the `sk-...` shape, deliberately. A test literal that looks like a real
            // credential trains the secret scanner's reader to skip a hit in this file, and the scanner is
            // only useful while every hit is worth reading. The gate under test is "a key is present",
            // which any non-blank value satisfies.
            var config = Config(Dev, ("OPENAI_API_KEY", "test-key-not-a-real-credential"));
            var authority = new AiProviderAuthority();

            Assert.True(OpenAiOptions.HasApiKey(config));
            Assert.False(authority.Assess(Now, Dev).IsApproved);

            var d = await Policy(authority, config).EvaluateAsync(Request(Dev));
            Assert.False(d.Allowed);
            Assert.Equal(AiEgressDenyReason.ProviderNotApproved, d.Reason);
        }

        [Fact]   // SCOPE20
        public async Task SCOPE20_the_provider_switch_cannot_create_a_project_approval()
        {
            var config = Config(Dev);
            var switchboard = new AiProviderSwitchboard(config);

            // Availability: on. For both environments, deliberately.
            Assert.True(switchboard.IsEnabled(OpenAiOptions.ProviderId, AiProviderEnvironment.Development));
            Assert.True(switchboard.IsEnabled(OpenAiOptions.ProviderId, AiProviderEnvironment.Production));

            // Authority: absent. The switch has no route to the governance record and never had one.
            var authority = new AiProviderAuthority();
            Assert.False(authority.Assess(Now, Dev).IsApproved);
            Assert.False(authority.Assess(Now, Prod).IsApproved);

            Assert.False((await Policy(authority, config).EvaluateAsync(Request(Dev))).Allowed);

            // And the reverse, so the two directions cannot be confused: an UNSTATED environment is not
            // enabled either, whatever the provider-wide key says.
            Assert.False(switchboard.IsEnabled(OpenAiOptions.ProviderId, AiProviderEnvironment.Unknown));
        }

        // =========================================================================================
        // SCOPE21—SCOPE25 — everything the increment must NOT have changed.
        //
        // A scope model is a new gate in front of external providers. If it altered the local path, the
        // classification matrix or company isolation, it would have traded one hole for another.
        // =========================================================================================

        // The local Python service is an INTERNAL destination. It has no organisation and no project, so
        // requiring a provider scope of it would have broken the one AI path that actually works today.
        [Fact]   // SCOPE21
        public async Task SCOPE21_the_local_internal_path_is_unaffected_by_provider_scope()
        {
            var config = Config(Dev, (AiDestinationResolver.DestinationClassKey, "Internal"));

            // No ProviderScope on the request AT ALL, and the unapproved shipped record.
            var d = await Policy(AiTestProviderAuthority.Unapproved(), config)
                .EvaluateAsync(Request(providerScope: null, destination: AiEgressDestinationClass.Internal));

            Assert.True(d.Allowed, d.Reason?.ToString());
            Assert.Equal(AiEgressDestinationClass.Internal, d.Approval!.Destination);

            // The internal token carries no provider scope, which is what stops it being replayed
            // externally: the adapter refuses any approval whose scope is absent.
            Assert.Null(d.Approval.ProviderScope);
        }

        [Fact]   // SCOPE22
        public async Task SCOPE22_personal_data_is_still_denied_externally_with_a_matching_scope()
        {
            var d = await Policy(AiTestProviderAuthority.ApprovedFor(Dev), Config(Dev))
                .EvaluateAsync(Request(Dev, AiDataClassification.PersonalData));

            Assert.False(d.Allowed);
            Assert.Equal(AiEgressDenyReason.ClassificationNotPermittedAtDestination, d.Reason);
            Assert.Null(d.Approval);
        }

        [Fact]   // SCOPE23
        public async Task SCOPE23_free_text_business_content_is_still_denied_externally_by_default()
        {
            var d = await Policy(AiTestProviderAuthority.ApprovedFor(Dev), Config(Dev))
                .EvaluateAsync(Request(Dev, AiDataClassification.FreeTextBusinessContent));

            Assert.False(d.Allowed);
            Assert.Equal(AiEgressDenyReason.ClassificationNotPermittedAtDestination, d.Reason);

            // Unknown is not a middle ground either.
            var unknown = await Policy(AiTestProviderAuthority.ApprovedFor(Dev), Config(Dev))
                .EvaluateAsync(Request(Dev, AiDataClassification.Unknown));
            Assert.False(unknown.Allowed);
        }

        // Provider scope and company isolation are ORTHOGONAL, and this test exists to keep them so. A
        // perfectly matching provider scope says which account the call goes to; it says nothing about
        // whose data may be in it.
        [Fact]   // SCOPE24
        public async Task SCOPE24_a_company_mismatch_still_denies_despite_a_matching_provider_scope()
        {
            var d = await Policy(AiTestProviderAuthority.ApprovedFor(Dev), Config(Dev), company: CompanyA)
                .EvaluateAsync(Request(Dev, dataCompany: CompanyB));

            Assert.False(d.Allowed);
            Assert.Equal(AiEgressDenyReason.CompanyMismatch, d.Reason);
            Assert.Null(d.Approval);
        }

        [Theory]   // SCOPE25
        [InlineData("expired")]
        [InlineData("revoked")]
        public async Task SCOPE25_an_expired_or_revoked_approval_denies_despite_a_matching_scope(string how)
        {
            var approval = how == "expired"
                ? Approval(validFrom: Now.AddDays(-30), expires: Now.AddDays(-1))
                : Approval(revoked: true);

            var authority = AiTestProviderAuthority.From(
                CandidateFor(Dev), AiTestProviderAuthority.CompliantRequirement(), approval, Now);

            var a = authority.Assess(Now, Dev);
            Assert.Equal(AiProviderState.Suspended, a.State);
            Assert.False(a.IsApproved);

            var d = await Policy(authority, Config(Dev)).EvaluateAsync(Request(Dev));
            Assert.False(d.Allowed);
            Assert.Equal(AiEgressDenyReason.ProviderNotApproved, d.Reason);
            Assert.Null(d.Approval);
        }

        // =========================================================================================
        // The structural guarantees the matrix above rests on. If these break, every case above could
        // pass while meaning something else.
        // =========================================================================================

        [Fact]
        public void Scope_matching_is_ordinal_and_case_sensitive()
        {
            // Project identifiers are opaque provider-issued strings. Case-insensitive matching would be
            // a guess about the provider's identifier semantics, and the safe guess is no guess.
            var upper = new AiProviderScope(
                Dev.ProviderId, Dev.OrganizationId, Dev.ProjectId!.ToUpperInvariant(), Dev.Environment);

            if (!string.Equals(Dev.ProjectId, upper.ProjectId, StringComparison.Ordinal))
                Assert.False(Dev.Covers(upper));

            Assert.True(Dev.Covers(new AiProviderScope(
                Dev.ProviderId, Dev.OrganizationId, Dev.ProjectId, Dev.Environment)));
        }

        [Fact]
        public void An_untrimmed_or_overlong_identifier_is_not_a_complete_scope()
        {
            Assert.False(new AiProviderScope(
                Dev.ProviderId, " TEST-ORG", Dev.ProjectId, Dev.Environment).IsComplete);

            Assert.False(new AiProviderScope(
                Dev.ProviderId, Dev.OrganizationId,
                new string('p', AiProviderScope.MaxIdentifierLength + 1), Dev.Environment).IsComplete);
        }

        // The request-side mirror of SCOPE1—SCOPE3: a caller that names no scope is DENIED rather than
        // defaulted. §22 of the brief is explicit that backward compatibility must not mean unsafe
        // compatibility, and silently reading a missing scope as Development would be exactly that.
        [Fact]
        public async Task An_external_request_that_names_no_scope_is_denied_rather_than_defaulted()
        {
            foreach (var scope in new object?[]
                     {
                         null,
                         "OpenAI/TEST-ORG/TEST-PROJECT-DEV/Development",       // a string, not a scope
                         new AiProviderScope(OpenAiOptions.ProviderId, AiTestProviderAuthority.TestOrganizationId,
                             AiTestProviderAuthority.TestProjectId, AiProviderEnvironment.Unknown),
                     })
            {
                var d = await Policy(AiTestProviderAuthority.ApprovedFor(Dev), Config(Dev))
                    .EvaluateAsync(Request(scope));

                Assert.False(d.Allowed);
                Assert.Equal(AiEgressDenyReason.ProviderScopeMissing, d.Reason);
                Assert.Null(d.Approval);
            }
        }
    }
}
