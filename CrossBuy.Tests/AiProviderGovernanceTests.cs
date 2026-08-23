using CrossBuy.BL.Platform;
using CrossBuy.BL.Platform.Ai;
using CrossBuy.Models.Platform;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using System.Reflection;
using Xunit;

namespace CrossBuy.Tests
{
    // AI Foundation Increment 4.2 — the provider DECISION model (PD1–PD18).
    //
    // IMPORTANT: the expected current outcome is OWNER DECISION REQUIRED, and that is a PASS, not a
    // failure. These tests verify that incomplete facts and missing approvals correctly produce a state
    // that DENIES. Not one of them requires a provider to be approved, and no real provider is named as
    // approved anywhere.
    public class AiProviderGovernanceTests
    {
        private static readonly DateTime Now = new(2026, 8, 15, 12, 0, 0, DateTimeKind.Utc);

        // A fully-decided owner policy. Regions are placeholders for a test — this code does not choose
        // a region for the business, and the report says so.
        private static AiProviderRequirement Policy(
            bool trainingAllowed = false,
            bool zeroRetention = false,
            TimeSpan? maxRetention = null,
            bool contractRequired = true,
            params string[] regions) => new()
            {
                AcceptableResidencyRegions = regions.Length > 0 ? regions : new[] { "region-x" },
                MaximumProviderRetention = zeroRetention ? null : (maxRetention ?? TimeSpan.FromDays(30)),
                ZeroRetentionRequired = zeroRetention,
                TrainingOnBusinessDataAllowed = trainingAllowed,
                ContractRequired = contractRequired,
            };

        // A candidate whose facts are all known and all satisfy the default policy.
        private static AiProviderCandidate Compliant(
            bool? trains = false,
            TimeSpan? retention = null,
            string region = "region-x",
            AiProviderFact contract = AiProviderFact.Yes,
            AiProviderFact encryption = AiProviderFact.Yes) => new()
            {
                ProviderId = "candidate-1",
                ProviderName = "ExampleCandidate",
                // Increment 4.9 — a compliant candidate must now name its account context. Placeholder
                // identifiers: a real organisation or project id has no place in a test fixture.
                OrganizationId = "EXAMPLE-ORG",
                ProjectId = "EXAMPLE-PROJECT",
                Environment = AiProviderEnvironment.Development,
                LegalEntity = "Example Legal Entity Ltd",
                AccountOwner = "example-account",
                CommercialTier = "Enterprise",
                ResidencyRegion = region,
                ProviderRetention = retention ?? TimeSpan.Zero,
                TrainsOnCustomerData = trains,
                SubprocessorPositionAccepted = AiProviderFact.Yes,
                EncryptionInTransit = encryption,
                DeletionControlsAvailable = AiProviderFact.Yes,
                ContractInPlace = contract,
            };

        private static AiProviderOwnerApproval FullApproval(
            bool revoked = false, DateTime? expires = null) => new()
            {
                SecurityApproved = true,
                DataProtectionApproved = true,
                BusinessOwnerApproved = true,
                ApprovedBy = "example-approver",
                ApprovedAtUtc = Now.AddDays(-1),
                // Increment 4.9 — both ends of the validity window are required. An approval with no
                // start is valid retroactively for all time.
                //
                // Well BEFORE the default expiry, and before any expiry an expiry-test passes in: a start
                // that lands on or after the end is REJECTED as an invalid window, which would mask the
                // Suspended result those tests are actually asserting.
                ValidFromUtc = Now.AddDays(-30),
                ExpiresAtUtc = expires ?? Now.AddDays(180),
                Revoked = revoked,
            };

        private static AiProviderAssessment Evaluate(
            AiProviderCandidate? c, AiProviderRequirement? r = null, AiProviderOwnerApproval? a = null)
            => AiProviderEvaluator.Evaluate(c, r ?? Policy(), a, Now);

        // ---- PD1: nothing known -> not approved ----
        [Fact]
        public void PD1_a_candidate_with_no_facts_is_never_approved()
        {
            var bare = new AiProviderCandidate { ProviderId = "x", ProviderName = "Unassessed" };
            var result = Evaluate(bare);

            Assert.False(result.IsApproved);
            Assert.Equal(AiProviderState.UnderAssessment, result.State);
            Assert.Equal(AiEgressDestinationClass.UnapprovedExternal, result.DestinationClass);
            Assert.NotEmpty(result.MissingFacts);
        }

        [Fact]
        public void A_null_candidate_is_unknown_and_denies()
        {
            var result = Evaluate(null);
            Assert.Equal(AiProviderState.Unknown, result.State);
            Assert.False(result.IsApproved);
        }

        // ---- PD2: facts complete, no owner approval -> NOT approved ----
        [Fact]
        public void PD2_complete_facts_without_owner_approval_are_not_approved()
        {
            var result = Evaluate(Compliant(), Policy(), a: null);

            Assert.Equal(AiProviderState.OwnerDecisionRequired, result.State);
            Assert.False(result.IsApproved);
            Assert.Empty(result.MissingFacts);
        }

        [Theory]
        [InlineData(true, true, false)]
        [InlineData(true, false, true)]
        [InlineData(false, true, true)]
        public void A_partial_set_of_approvals_is_not_approval(bool sec, bool dp, bool biz)
        {
            var partial = new AiProviderOwnerApproval
            {
                SecurityApproved = sec, DataProtectionApproved = dp, BusinessOwnerApproved = biz,
                ExpiresAtUtc = Now.AddDays(180),
            };
            Assert.False(Evaluate(Compliant(), Policy(), partial).IsApproved);
        }

        // ---- PD3: owner approval but a fact unknown -> NOT approved ----
        [Fact]
        public void PD3_owner_approval_cannot_substitute_for_an_unknown_fact()
        {
            var incomplete = Compliant();
            var missingEntity = new AiProviderCandidate
            {
                ProviderId = incomplete.ProviderId, ProviderName = incomplete.ProviderName,
                LegalEntity = null,                                  // <- the unknown
                AccountOwner = incomplete.AccountOwner, CommercialTier = incomplete.CommercialTier,
                ResidencyRegion = incomplete.ResidencyRegion, ProviderRetention = incomplete.ProviderRetention,
                TrainsOnCustomerData = incomplete.TrainsOnCustomerData,
                SubprocessorPositionAccepted = incomplete.SubprocessorPositionAccepted,
                EncryptionInTransit = incomplete.EncryptionInTransit,
                DeletionControlsAvailable = incomplete.DeletionControlsAvailable,
                ContractInPlace = incomplete.ContractInPlace,
            };

            var result = Evaluate(missingEntity, Policy(), FullApproval());

            Assert.False(result.IsApproved);
            Assert.Equal(AiProviderState.OwnerDecisionRequired, result.State);
            Assert.Contains("LegalEntity", result.MissingFacts);
        }

        // The state machine has no Unknown -> Approved edge. Asserted directly, because that single
        // missing edge is the whole point of having states rather than a boolean.
        [Fact]
        public void Unknown_never_transitions_straight_to_approved()
        {
            var bare = new AiProviderCandidate { ProviderId = "x", ProviderName = "Unassessed" };
            Assert.NotEqual(AiProviderState.ApprovedExternalProcessor, Evaluate(bare, Policy(), FullApproval()).State);
        }

        // ---- PD4: explicit rejection ----
        [Fact]
        public void PD4_an_explicitly_rejected_provider_is_rejected_regardless_of_facts()
        {
            var rejected = new AiProviderCandidate
            {
                ProviderId = "x", ProviderName = "Rejected", ExplicitlyRejected = true,
                RejectionReason = "owner declined",
            };
            var result = Evaluate(rejected, Policy(), FullApproval());

            Assert.Equal(AiProviderState.Rejected, result.State);
            Assert.False(result.IsApproved);
        }

        // ---- PD5/PD6: expiry and revocation ----
        [Fact]
        public void PD5_an_expired_approval_suspends_the_provider()
        {
            var result = Evaluate(Compliant(), Policy(), FullApproval(expires: Now.AddDays(-1)));

            Assert.Equal(AiProviderState.Suspended, result.State);
            Assert.False(result.IsApproved);
        }

        [Fact]
        public void PD6_a_revoked_approval_suspends_the_provider()
        {
            var result = Evaluate(Compliant(), Policy(), FullApproval(revoked: true));

            Assert.Equal(AiProviderState.Suspended, result.State);
            Assert.False(result.IsApproved);
        }

        // An approval with no expiry is NOT permanent — it is incomplete. An approval nobody has to
        // re-confirm is how a provider stays approved long after the contract it rested on lapsed.
        [Fact]
        public void An_approval_with_no_expiry_date_is_not_approval()
        {
            var noExpiry = new AiProviderOwnerApproval
            {
                SecurityApproved = true, DataProtectionApproved = true, BusinessOwnerApproved = true,
                ExpiresAtUtc = null,
            };
            var result = Evaluate(Compliant(), Policy(), noExpiry);

            Assert.False(result.IsApproved);
            Assert.Contains("expiry", result.Reason, StringComparison.OrdinalIgnoreCase);
        }

        // ---- PD7/PD8/PD9/PD10: policy breaches are REJECTIONS, not pending assessments ----
        [Fact]
        public void PD7_a_residency_mismatch_is_not_approved()
        {
            var result = Evaluate(Compliant(region: "somewhere-else"), Policy(regions: "region-x"), FullApproval());
            Assert.False(result.IsApproved);
            Assert.Equal(AiProviderState.Rejected, result.State);
        }

        [Fact]
        public void PD8_retention_beyond_the_accepted_maximum_is_not_approved()
        {
            var result = Evaluate(
                Compliant(retention: TimeSpan.FromDays(90)),
                Policy(maxRetention: TimeSpan.FromDays(30)),
                FullApproval());

            Assert.False(result.IsApproved);
            Assert.Contains("retention", result.Reason, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void Zero_retention_required_but_provider_retains_is_not_approved()
        {
            var result = Evaluate(
                Compliant(retention: TimeSpan.FromDays(1)),
                Policy(zeroRetention: true),
                FullApproval());

            Assert.False(result.IsApproved);
        }

        [Fact]
        public void PD9_training_on_business_data_when_forbidden_is_not_approved()
        {
            var result = Evaluate(Compliant(trains: true), Policy(trainingAllowed: false), FullApproval());

            Assert.False(result.IsApproved);
            Assert.Equal(AiProviderState.Rejected, result.State);
            Assert.Contains("train", result.Reason, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void PD10_a_required_contract_that_is_absent_is_not_approved()
        {
            var result = Evaluate(
                Compliant(contract: AiProviderFact.No), Policy(contractRequired: true), FullApproval());

            Assert.False(result.IsApproved);
            Assert.Contains("DPA", result.Reason, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void A_provider_without_encryption_in_transit_is_not_approved()
            => Assert.False(Evaluate(Compliant(encryption: AiProviderFact.No), Policy(), FullApproval()).IsApproved);

        // An UNDECIDED owner policy blocks even a fully-described provider: a candidate cannot be
        // measured against a requirement nobody has set.
        [Fact]
        public void An_incomplete_owner_policy_blocks_approval()
        {
            var undecided = new AiProviderRequirement();      // nothing decided
            var result = Evaluate(Compliant(), undecided, FullApproval());

            Assert.False(result.IsApproved);
            Assert.Contains("OwnerRequirementPolicy", result.MissingFacts);
        }

        // ---- PD11: the only path to approval ----
        [Fact]
        public void PD11_complete_compliant_facts_plus_all_approvals_is_eligible()
        {
            var result = Evaluate(Compliant(), Policy(), FullApproval());

            Assert.True(result.IsApproved, result.Reason);
            Assert.Equal(AiProviderState.ApprovedExternalProcessor, result.State);
            Assert.Equal(AiEgressDestinationClass.ApprovedExternalProcessor, result.DestinationClass);
        }

        // ---- PD12–PD16: approval is NOT a master key ----
        //
        // These are the tests that matter most: an approved provider must still lose to every other gate.
        // Approval answers "may we use this processor at all", never "may this particular payload go".
        private sealed class FixedContext : IBusinessContextAccessor
        {
            private readonly BusinessContext? _c;
            public FixedContext(BusinessContext? c) => _c = c;
            public Task<BusinessContext> GetCurrentAsync(CancellationToken ct = default)
                => _c == null ? throw new BusinessContextUnresolvedException("none") : Task.FromResult(_c);
            public Task<BusinessContext?> TryGetCurrentAsync(CancellationToken ct = default) => Task.FromResult(_c);
        }

        private static IConfiguration ValidHost() => new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AiService:Secret"] = "configured",
                ["AiService:BaseUrl"] = "http://localhost:8000",
                ["AiService:DeploymentMode"] = "LocalLoopback",
            }).Build();

        private static async Task<AiEgressDecision> EgressAsync(
            AiDataClassification classification, int dataCompany = 1, int userCompany = 1)
            => await new AiEgressPolicy(
                    new FixedContext(new BusinessContext
                    {
                        CompanyId = userCompany, EmployeeId = 7, UserId = "u7", Source = BusinessContextSource.Http,
                    }),
                    // INCREMENT 4.3: these tests are named "an approved provider does not unlock X", and
                    // until now that title was aspirational — the runtime had no idea whether a provider
                    // was approved. It does now, so the fixture supplies a genuinely approved authority
                    // and the tests finally assert what they always claimed to.
                    ValidHost(), NullLogger<AiEgressPolicy>.Instance, AiTestProviderAuthority.Approved())
                .EvaluateAsync(new AiEgressRequest
                {
                    Purpose = AiEgressPurpose.CashflowForecast,
                    // The destination an APPROVED provider would yield.
                    Destination = AiEgressDestinationClass.ApprovedExternalProcessor,
                    ProviderScope = AiTestProviderAuthority.TestScope,
                    Classification = classification,
                    DataCompanyId = dataCompany,
                    PayloadBytes = 1024,
                });

        [Fact]
        public async Task PD12_PD16_an_approved_provider_does_not_unlock_free_text()
        {
            Assert.True(Evaluate(Compliant(), Policy(), FullApproval()).IsApproved);   // provider IS approved

            var d = await EgressAsync(AiDataClassification.FreeTextBusinessContent);
            Assert.False(d.Allowed);
            Assert.Equal(AiEgressDenyReason.ClassificationNotPermittedAtDestination, d.Reason);
        }

        [Fact]
        public async Task PD15_an_approved_provider_does_not_unlock_personal_data()
        {
            var d = await EgressAsync(AiDataClassification.PersonalData);
            Assert.False(d.Allowed);
            Assert.Equal(AiEgressDenyReason.ClassificationNotPermittedAtDestination, d.Reason);
        }

        [Fact]
        public async Task PD13_an_approved_provider_does_not_override_company_isolation()
        {
            var d = await EgressAsync(AiDataClassification.FinancialAggregate, dataCompany: 1, userCompany: 65);
            Assert.False(d.Allowed);
            Assert.Equal(AiEgressDenyReason.CompanyMismatch, d.Reason);
        }

        [Fact]
        public async Task PD14_an_approved_provider_does_not_override_an_unresolved_user_context()
        {
            var d = await new AiEgressPolicy(new FixedContext(null), ValidHost(),
                    NullLogger<AiEgressPolicy>.Instance, AiTestProviderAuthority.Approved())
                .EvaluateAsync(new AiEgressRequest
                {
                    Purpose = AiEgressPurpose.CashflowForecast,
                    Destination = AiEgressDestinationClass.ApprovedExternalProcessor,
                    ProviderScope = AiTestProviderAuthority.TestScope,
                    Classification = AiDataClassification.FinancialAggregate,
                    DataCompanyId = 1, PayloadBytes = 512,
                });

            Assert.False(d.Allowed);
            Assert.Equal(AiEgressDenyReason.CompanyUnresolved, d.Reason);
        }

        // ---- PD17: local ML is independent of any external approval ----
        [Fact]
        public async Task PD17_local_ml_remains_usable_with_no_provider_approved()
        {
            // No provider is approved anywhere in this test — the local route must not care.
            var d = await new AiEgressPolicy(
                    new FixedContext(new BusinessContext
                    {
                        CompanyId = 1, EmployeeId = 7, UserId = "u7", Source = BusinessContextSource.Http,
                    }),
                    // INCREMENT 4.3: literally unapproved now, not merely unmentioned. The runtime asks
                    // the governance record on every external request; this one is Internal, so it never
                    // reaches the gate — which is exactly the independence this test asserts.
                    ValidHost(), NullLogger<AiEgressPolicy>.Instance, AiTestProviderAuthority.Unapproved())
                .EvaluateAsync(new AiEgressRequest
                {
                    Purpose = AiEgressPurpose.InventoryAnalysis,
                    Destination = AiEgressDestinationClass.Internal,
                    Classification = AiDataClassification.FinancialAggregate,
                    DataCompanyId = 1, PayloadBytes = 1024,
                });

            Assert.True(d.Allowed, d.Reason?.ToString());
        }

        // ---- PD18: the diagnostic route stays non-production ----
        [Fact]
        public void PD18_the_diagnostic_route_remains_development_only()
        {
            var method = typeof(CrossBuy.Controllers.Api.AiController)
                .GetMethod("Diag", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
            Assert.NotNull(method);
            Assert.NotNull(method!.GetCustomAttribute<CrossBuy.Models.DevOnlyAttribute>());
        }

        // ---- §7: configuration alone cannot approve a provider ----
        //
        // Structural, and the strongest form of the claim: the evaluator's signature admits no
        // IConfiguration, so there is no file a developer could edit to turn approval on.
        [Fact]
        public void The_evaluator_reads_no_configuration_environment_or_request()
        {
            foreach (var m in typeof(AiProviderEvaluator).GetMethods(
                         System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static))
                foreach (var p in m.GetParameters())
                {
                    var n = p.ParameterType.FullName ?? "";
                    foreach (var banned in new[] { "IConfiguration", "HttpContext", "HttpRequest", "IWebHostEnvironment" })
                        Assert.DoesNotContain(banned, n, StringComparison.Ordinal);
                }
        }

        // No real provider may be named as approved in product source — the decision belongs to the
        // owner, and a hardcoded name would pre-empt it.
        [Fact]
        public void No_named_provider_is_pre_approved_in_source()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "CrossBuy.sln"))) dir = dir.Parent;
            Assert.NotNull(dir);

            var src = File.ReadAllText(Path.Combine(dir!.FullName, "CrossBuy", "BL", "Platform", "Ai", "AiProviderGovernance.cs"));
            foreach (var provider in new[] { "Anthropic", "OpenAI", "Azure", "Gemini", "Claude" })
                Assert.DoesNotContain(provider, src, StringComparison.OrdinalIgnoreCase);
        }

        // Evidence levels must distinguish "the code proves it" from "someone said so".
        [Fact]
        public void Unknown_evidence_is_never_treated_as_known()
        {
            var unknown = new AiProviderEvidence
            {
                Fact = "residency", Level = AiEvidenceLevel.Unknown, Owner = AiFactOwner.DataProtectionLegal,
            };
            Assert.False(unknown.IsKnown);
            Assert.Equal(0, (int)AiEvidenceLevel.Unknown);      // zero-default is the refusing value
            Assert.Equal(0, (int)AiFactOwner.Unknown);
        }
    }
}

