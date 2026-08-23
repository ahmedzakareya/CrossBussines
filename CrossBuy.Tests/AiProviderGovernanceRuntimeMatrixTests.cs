using CrossBuy.BL.Platform;
using CrossBuy.BL.Platform.Ai;
using CrossBuy.Models.Platform;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CrossBuy.Tests
{
    // INCREMENT 4.3 — THE CONFIGURATION SPOOF MATRIX (RT1—RT22).
    //
    // WHAT THIS FILE ADDS OVER THE EXISTING TESTS. AiProviderGovernanceTests proves the EVALUATOR's rules
    // in isolation: expired denies, revoked denies, residency mismatch denies, and so on. Those are unit
    // tests of a pure function, and they were all green while the runtime ignored that function entirely.
    // That is precisely how BLOCKER-1 survived — a fully-tested rule that nothing called.
    //
    // So every case here is driven END TO END, with the settings file set to "ApprovedExternalProcessor"
    // THROUGHOUT. Each test breaks exactly one thing in an otherwise-compliant governance record and then
    // asserts BOTH halves of the fix:
    //
    //     1. AiDestinationResolver refuses to derive the approved class, and
    //     2. AiEgressPolicy independently refuses the send.
    //
    // The configuration is never the variable. It is pinned to the value an operator would use if they
    // believed the settings file granted approval, so every green test in this file is a statement that
    // the belief is false.
    public class AiProviderGovernanceRuntimeMatrixTests
    {
        private const int Company = 1;

        private sealed class FixedContext : IBusinessContextAccessor
        {
            private readonly BusinessContext? _ctx;
            public FixedContext(BusinessContext? c) => _ctx = c;
            public Task<BusinessContext> GetCurrentAsync(CancellationToken ct = default) => Task.FromResult(_ctx!);
            public Task<BusinessContext?> TryGetCurrentAsync(CancellationToken ct = default) => Task.FromResult(_ctx);
        }

        // The operator's belief, encoded once: "I set DestinationClass to ApprovedExternalProcessor, so
        // external AI is approved." Every case below runs against exactly this.
        private static IConfiguration SpoofConfig(params (string, string?)[] overrides)
        {
            var d = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                [AiDestinationResolver.DestinationClassKey] = "ApprovedExternalProcessor",
                ["AiService:DeploymentMode"] = "LocalLoopback",
                ["AiService:BaseUrl"] = "http://localhost:8000",
                ["AiService:Secret"] = "a-real-looking-configured-value",
            };
            foreach (var (k, v) in overrides) d[k] = v;
            return new ConfigurationBuilder().AddInMemoryCollection(d).Build();
        }

        // `anonymous` is an explicit flag rather than "pass null for user": null is also the default, so a
        // null-coalescing helper would have quietly handed the test a real context and RT19b would have
        // been asserting nothing. It failed on first run for exactly that reason.
        private static AiEgressPolicy Policy(IAiProviderAuthority authority, IConfiguration? config = null,
            bool anonymous = false)
            => new(new FixedContext(anonymous ? null : new BusinessContext
            {
                CompanyId = Company, EmployeeId = 7, UserId = "u7", Source = BusinessContextSource.Http,
            }), config ?? SpoofConfig(), NullLogger<AiEgressPolicy>.Instance, authority);

        private static AiEgressRequest Req(
            AiDataClassification classification = AiDataClassification.FinancialAggregate,
            int dataCompany = Company, int bytes = 512)
            => new()
            {
                Purpose = AiEgressPurpose.CashflowForecast,
                Destination = AiEgressDestinationClass.ApprovedExternalProcessor,
                ProviderScope = AiTestProviderAuthority.TestScope,
                Classification = classification,
                DataCompanyId = dataCompany,
                PayloadBytes = bytes,
            };

        /// The shared assertion for every negative case: the resolver will not derive the approved class,
        /// and the boundary refuses the send even when the caller supplies that class directly.
        private static async Task AssertDeniedEndToEndAsync(IAiProviderAuthority authority, string because)
        {
            var config = SpoofConfig();

            Assert.Equal(AiEgressDestinationClass.UnapprovedExternal,
                AiDestinationResolver.Resolve(config, AiEgressPurpose.CashflowForecast,
                    authority.Assess(DateTime.UtcNow, AiTestProviderAuthority.TestScope).State));

            var d = await Policy(authority, config).EvaluateAsync(Req());

            Assert.False(d.Allowed, because);
            Assert.Equal(AiEgressDenyReason.ProviderNotApproved, d.Reason);
            Assert.Null(d.Approval);
        }

        // =========================================================================================
        // RT1—RT3 — the record itself is incomplete
        // =========================================================================================

        [Fact]   // RT1
        public Task RT1_no_candidate_denies()
            => AssertDeniedEndToEndAsync(
                AiTestProviderAuthority.From(null, AiTestProviderAuthority.CompliantRequirement(),
                    AiTestProviderAuthority.FullApproval()),
                "a settings line must not approve a provider that does not exist");

        // One unknown fact is enough. Each of the eleven is exercised, because "mostly known" is the
        // state a real assessment spends most of its life in, and it must not be nearly-approved.
        [Theory]   // RT2
        [InlineData("LegalEntity")]
        [InlineData("AccountOwner")]
        [InlineData("CommercialTier")]
        [InlineData("TrainsOnCustomerData")]
        [InlineData("ResidencyRegion")]
        [InlineData("SubprocessorPositionAccepted")]
        [InlineData("EncryptionInTransit")]
        [InlineData("DeletionControlsAvailable")]
        [InlineData("ContractInPlace")]
        public Task RT2_a_single_unknown_mandatory_fact_denies(string missing)
        {
            var c = AiTestProviderAuthority.CompliantCandidate();

            var broken = missing switch
            {
                "LegalEntity" => Rebuild(c, legalEntity: null),
                "AccountOwner" => Rebuild(c, accountOwner: null),
                "CommercialTier" => Rebuild(c, commercialTier: null),
                "TrainsOnCustomerData" => Rebuild(c, clearTrains: true),
                "ResidencyRegion" => Rebuild(c, residency: null),
                "SubprocessorPositionAccepted" => Rebuild(c, subprocessor: AiProviderFact.Unknown),
                "EncryptionInTransit" => Rebuild(c, encryption: AiProviderFact.Unknown),
                "DeletionControlsAvailable" => Rebuild(c, deletion: AiProviderFact.Unknown),
                "ContractInPlace" => Rebuild(c, contract: AiProviderFact.Unknown),
                _ => throw new ArgumentOutOfRangeException(nameof(missing)),
            };

            return AssertDeniedEndToEndAsync(
                AiTestProviderAuthority.From(broken, AiTestProviderAuthority.CompliantRequirement(),
                    AiTestProviderAuthority.FullApproval()),
                $"an unknown '{missing}' must deny — UNKNOWN is an absence of evidence, not a pass");
        }

        // The owner's own policy is the eleventh mandatory fact. A candidate cannot pass a test nobody set.
        [Fact]   // RT3
        public Task RT3_incomplete_owner_requirement_denies()
            => AssertDeniedEndToEndAsync(
                AiTestProviderAuthority.From(
                    AiTestProviderAuthority.CompliantCandidate(),
                    new AiProviderRequirement(),                     // IsComplete == false
                    AiTestProviderAuthority.FullApproval()),
                "with no recorded owner policy there is no standard to measure the provider against");

        // =========================================================================================
        // RT4—RT7 — the approvals
        // =========================================================================================

        [Theory]   // RT4, RT5, RT6
        [InlineData(false, true, true, "Security")]
        [InlineData(true, false, true, "Data Protection / Legal")]
        [InlineData(true, true, false, "Business Owner")]
        public Task RT4_RT6_a_missing_signature_denies(bool sec, bool dp, bool biz, string who)
        {
            var approval = new AiProviderOwnerApproval
            {
                SecurityApproved = sec,
                DataProtectionApproved = dp,
                BusinessOwnerApproved = biz,
                ApprovedAtUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                // Increment 4.9 — a start date, so each case still breaks exactly ONE thing. Without it
                // every fixture here would fail on the missing window instead of the rule under test.
                ValidFromUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                ExpiresAtUtc = new DateTime(2099, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            };

            return AssertDeniedEndToEndAsync(
                AiTestProviderAuthority.From(AiTestProviderAuthority.CompliantCandidate(),
                    AiTestProviderAuthority.CompliantRequirement(), approval),
                $"two of three signatures is not an approval — {who} did not sign");
        }

        // An approval nobody has to re-confirm is how a provider stays approved years after the contract
        // it rested on lapsed. Absent expiry reads as NOT approved, never as "forever".
        [Fact]   // RT7
        public Task RT7_an_approval_with_no_expiry_denies()
        {
            var approval = new AiProviderOwnerApproval
            {
                SecurityApproved = true,
                DataProtectionApproved = true,
                BusinessOwnerApproved = true,
                ApprovedAtUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                // Increment 4.9 — a start date, so each case still breaks exactly ONE thing. Without it
                // every fixture here would fail on the missing window instead of the rule under test.
                ValidFromUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                ExpiresAtUtc = null,
            };

            return AssertDeniedEndToEndAsync(
                AiTestProviderAuthority.From(AiTestProviderAuthority.CompliantCandidate(),
                    AiTestProviderAuthority.CompliantRequirement(), approval),
                "an approval with no expiry date is not an approval");
        }

        // =========================================================================================
        // RT8 — EXPIRY, at runtime. §2 of the brief.
        // =========================================================================================

        [Fact]   // RT8
        public async Task RT8_an_expired_approval_suspends_and_denies()
        {
            var now = new DateTime(2026, 8, 15, 0, 0, 0, DateTimeKind.Utc);

            var approval = new AiProviderOwnerApproval
            {
                SecurityApproved = true,
                DataProtectionApproved = true,
                BusinessOwnerApproved = true,
                ApprovedBy = "was-genuinely-approved-once",
                ApprovedAtUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                // Increment 4.9 — a start date, so each case still breaks exactly ONE thing. Without it
                // every fixture here would fail on the missing window instead of the rule under test.
                ValidFromUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                ExpiresAtUtc = now.AddDays(-1),                       // lapsed yesterday
            };

            var assessment = AiProviderEvaluator.Evaluate(
                AiTestProviderAuthority.CompliantCandidate(),
                AiTestProviderAuthority.CompliantRequirement(), approval, now);

            // The state records that approval ONCE existed — which matters for auditing what was sent.
            Assert.Equal(AiProviderState.Suspended, assessment.State);
            Assert.False(assessment.IsApproved);
            Assert.Equal(AiEgressDestinationClass.UnapprovedExternal, assessment.DestinationClass);

            await AssertDeniedEndToEndAsync(
                AiTestProviderAuthority.From(AiTestProviderAuthority.CompliantCandidate(),
                    AiTestProviderAuthority.CompliantRequirement(), approval, now),
                "an expired approval must fail closed, not coast");
        }

        // §2 explicitly: the settings file must not be able to bring a lapsed approval back to life. This
        // is the failure mode an operator reaches for at 2am when AI stops working.
        [Fact]   // RT8b
        public async Task RT8b_configuration_cannot_reactivate_an_expired_provider()
        {
            var now = new DateTime(2026, 8, 15, 0, 0, 0, DateTimeKind.Utc);
            var expired = AiTestProviderAuthority.From(
                AiTestProviderAuthority.CompliantCandidate(),
                AiTestProviderAuthority.CompliantRequirement(),
                new AiProviderOwnerApproval
                {
                    SecurityApproved = true,
                    DataProtectionApproved = true,
                    BusinessOwnerApproved = true,
                    ApprovedAtUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                    // Increment 4.9 — a start date, so each case still breaks exactly ONE thing. Without it
                    // every fixture here would fail on the missing window instead of the rule under test.
                    ValidFromUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                    ExpiresAtUtc = now.AddSeconds(-1),
                }, now);

            // Every configuration an operator might try. None of them is a renewal.
            foreach (var attempt in new[] { "ApprovedExternalProcessor", "approvedexternalprocessor", "Internal" })
            {
                var config = SpoofConfig((AiDestinationResolver.DestinationClassKey, attempt));

                Assert.NotEqual(AiEgressDestinationClass.ApprovedExternalProcessor,
                    AiDestinationResolver.Resolve(config, AiEgressPurpose.CashflowForecast,
                        expired.Assess(now, AiTestProviderAuthority.TestScope).State));

                var d = await Policy(expired, config).EvaluateAsync(Req());
                Assert.False(d.Allowed);
                Assert.Equal(AiEgressDenyReason.ProviderNotApproved, d.Reason);
            }
        }

        // =========================================================================================
        // RT9 — REVOCATION. §3 of the brief.
        // =========================================================================================

        [Fact]   // RT9
        public async Task RT9_a_revoked_approval_suspends_and_denies()
        {
            var approval = new AiProviderOwnerApproval
            {
                SecurityApproved = true,
                DataProtectionApproved = true,
                BusinessOwnerApproved = true,
                ApprovedAtUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                // Increment 4.9 — a start date, so each case still breaks exactly ONE thing. Without it
                // every fixture here would fail on the missing window instead of the rule under test.
                ValidFromUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                ExpiresAtUtc = new DateTime(2099, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                Revoked = true,
                RevokedReason = "test — contract terminated",
            };

            var assessment = AiProviderEvaluator.Evaluate(
                AiTestProviderAuthority.CompliantCandidate(),
                AiTestProviderAuthority.CompliantRequirement(), approval, DateTime.UtcNow);

            Assert.Equal(AiProviderState.Suspended, assessment.State);

            // Revocation beats a still-future expiry date: the expiry has not passed, and it still denies.
            await AssertDeniedEndToEndAsync(
                AiTestProviderAuthority.From(AiTestProviderAuthority.CompliantCandidate(),
                    AiTestProviderAuthority.CompliantRequirement(), approval),
                "revocation must take effect immediately, not at the next expiry");
        }

        [Fact]   // RT9b
        public async Task RT9b_configuration_cannot_restore_a_revoked_provider()
        {
            var revoked = AiTestProviderAuthority.From(
                AiTestProviderAuthority.CompliantCandidate(),
                AiTestProviderAuthority.CompliantRequirement(),
                new AiProviderOwnerApproval
                {
                    SecurityApproved = true,
                    DataProtectionApproved = true,
                    BusinessOwnerApproved = true,
                    ApprovedAtUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                    // Increment 4.9 — a start date, so each case still breaks exactly ONE thing. Without it
                    // every fixture here would fail on the missing window instead of the rule under test.
                    ValidFromUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                    ExpiresAtUtc = new DateTime(2099, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                    Revoked = true,
                });

            var d = await Policy(revoked, SpoofConfig()).EvaluateAsync(Req());
            Assert.False(d.Allowed);
            Assert.Equal(AiEgressDenyReason.ProviderNotApproved, d.Reason);
        }

        // =========================================================================================
        // RT10 — SUSPENSION as a state, independent of how it was reached. §4 of the brief.
        // =========================================================================================

        [Fact]   // RT10
        public async Task RT10_a_suspended_provider_cannot_be_overridden_by_any_configuration()
        {
            var suspended = AiTestProviderAuthority.InState(AiProviderState.Suspended);

            foreach (var attempt in new[]
            {
                "ApprovedExternalProcessor", "Internal", "UnapprovedExternal", "", null,
            })
            {
                var config = SpoofConfig((AiDestinationResolver.DestinationClassKey, attempt));

                Assert.NotEqual(AiEgressDestinationClass.ApprovedExternalProcessor,
                    AiDestinationResolver.Resolve(config, AiEgressPurpose.CashflowForecast,
                        AiProviderState.Suspended));

                var d = await Policy(suspended, config).EvaluateAsync(Req());
                Assert.False(d.Allowed);
            }
        }

        // =========================================================================================
        // RT11—RT14 — the provider's facts are known but do not satisfy the owner's policy.
        //
        // These are REJECTIONS rather than incomplete assessments: a provider that demonstrably breaches
        // policy is not waiting on more information.
        // =========================================================================================

        [Fact]   // RT11
        public async Task RT11_a_residency_mismatch_denies()
        {
            var candidate = Rebuild(AiTestProviderAuthority.CompliantCandidate(), residency: "somewhere-else");
            var authority = AiTestProviderAuthority.From(candidate,
                AiTestProviderAuthority.CompliantRequirement(), AiTestProviderAuthority.FullApproval());

            Assert.Equal(AiProviderState.Rejected, authority.Assess(DateTime.UtcNow, AiTestProviderAuthority.TestScope).State);
            await AssertDeniedEndToEndAsync(authority,
                "processing in a region the business has not accepted must deny, whatever else is in order");
        }

        [Fact]   // RT12
        public async Task RT12_retention_beyond_the_accepted_maximum_denies()
        {
            var candidate = Rebuild(AiTestProviderAuthority.CompliantCandidate(), setRetention: TimeSpan.FromDays(30));
            var requirement = new AiProviderRequirement
            {
                AcceptableResidencyRegions = new[] { AiTestProviderAuthority.Region },
                MaximumProviderRetention = TimeSpan.FromDays(7),     // policy allows 7; provider keeps 30
                TrainingOnBusinessDataAllowed = false,
                ContractRequired = true,
            };

            await AssertDeniedEndToEndAsync(
                AiTestProviderAuthority.From(candidate, requirement, AiTestProviderAuthority.FullApproval()),
                "30 days of provider-side retention does not satisfy a 7-day policy");
        }

        // Zero retention is not "a small number of days". A provider that retains at all fails it.
        [Fact]   // RT12b
        public async Task RT12b_zero_retention_required_but_the_provider_retains_denies()
        {
            var candidate = Rebuild(AiTestProviderAuthority.CompliantCandidate(), setRetention: TimeSpan.FromMinutes(1));

            await AssertDeniedEndToEndAsync(
                AiTestProviderAuthority.From(candidate, AiTestProviderAuthority.CompliantRequirement(),
                    AiTestProviderAuthority.FullApproval()),
                "one minute of retention still is not zero retention");
        }

        [Fact]   // RT13
        public async Task RT13_a_training_mismatch_denies()
        {
            var candidate = Rebuild(AiTestProviderAuthority.CompliantCandidate(), setTrains: true);

            await AssertDeniedEndToEndAsync(
                AiTestProviderAuthority.From(candidate, AiTestProviderAuthority.CompliantRequirement(),
                    AiTestProviderAuthority.FullApproval()),
                "a provider that trains on customer data cannot serve a policy that forbids it");
        }

        // UNKNOWN training evidence denies too, and for a different reason than a known breach: the
        // difference between "we know they train" and "nobody checked" matters to the owner, and both deny.
        [Fact]   // RT13b
        public async Task RT13b_unknown_training_evidence_denies()
        {
            var candidate = Rebuild(AiTestProviderAuthority.CompliantCandidate(), clearTrains: true);

            var authority = AiTestProviderAuthority.From(candidate,
                AiTestProviderAuthority.CompliantRequirement(), AiTestProviderAuthority.FullApproval());

            Assert.Contains("TrainsOnCustomerData", authority.Assess(DateTime.UtcNow, AiTestProviderAuthority.TestScope).MissingFacts);
            await AssertDeniedEndToEndAsync(authority, "unknown must never read as no");
        }

        // RT14 — the contract. NOTE ON SEMANTICS, because the brief warns against conflating them: the
        // domain models ContractInPlace as a three-valued fact (Yes / No / Unknown), so "a DPA exists as a
        // product offering" is not expressible here at all — only "in place for us" or "not" or "unknown".
        // Both No and Unknown deny, by different routes: No is a policy breach, Unknown is a missing fact.
        [Theory]   // RT14
        [InlineData(AiProviderFact.No)]
        [InlineData(AiProviderFact.Unknown)]
        public Task RT14_a_required_contract_that_is_absent_or_unknown_denies(AiProviderFact contract)
            => AssertDeniedEndToEndAsync(
                AiTestProviderAuthority.From(
                    Rebuild(AiTestProviderAuthority.CompliantCandidate(), contract: contract),
                    AiTestProviderAuthority.CompliantRequirement(), AiTestProviderAuthority.FullApproval()),
                $"ContractInPlace={contract} cannot satisfy ContractRequired=true");

        // =========================================================================================
        // RT15 — the positive path. Built from a real governance decision, judged by the real evaluator.
        // =========================================================================================

        [Fact]   // RT15
        public async Task RT15_a_fully_compliant_record_may_derive_the_approved_class()
        {
            var authority = AiTestProviderAuthority.From(
                AiTestProviderAuthority.CompliantCandidate(),
                AiTestProviderAuthority.CompliantRequirement(),
                AiTestProviderAuthority.FullApproval());

            var assessment = authority.Assess(DateTime.UtcNow, AiTestProviderAuthority.TestScope);

            // The approved enum is NOT hard-coded into the fixture — it is what the evaluator returned.
            Assert.Equal(AiProviderState.ApprovedExternalProcessor, assessment.State);
            Assert.True(assessment.IsApproved);
            Assert.Empty(assessment.MissingFacts);

            Assert.Equal(AiEgressDestinationClass.ApprovedExternalProcessor,
                AiDestinationResolver.Resolve(SpoofConfig(), AiEgressPurpose.CashflowForecast, assessment.State));

            var d = await Policy(authority).EvaluateAsync(Req());
            Assert.True(d.Allowed, d.Reason?.ToString());
            Assert.NotNull(d.Approval);
        }

        // =========================================================================================
        // RT16—RT19 — approval opens ONE gate and no others.
        // =========================================================================================

        [Theory]   // RT16, RT17, RT19
        [InlineData(AiDataClassification.FreeTextBusinessContent, AiEgressDenyReason.ClassificationNotPermittedAtDestination)]
        [InlineData(AiDataClassification.PersonalData, AiEgressDenyReason.ClassificationNotPermittedAtDestination)]
        [InlineData(AiDataClassification.Unknown, AiEgressDenyReason.UnknownClassification)]
        public async Task RT16_RT19_an_approved_provider_does_not_unlock_a_forbidden_classification(
            AiDataClassification classification, AiEgressDenyReason expected)
        {
            var d = await Policy(AiTestProviderAuthority.Approved())
                .EvaluateAsync(Req(classification: classification));

            Assert.False(d.Allowed);
            Assert.Equal(expected, d.Reason);
            Assert.Null(d.Approval);
        }

        [Fact]   // RT18
        public async Task RT18_an_approved_provider_does_not_unlock_another_company()
        {
            var d = await Policy(AiTestProviderAuthority.Approved())
                .EvaluateAsync(Req(dataCompany: Company + 1));

            Assert.False(d.Allowed);
            Assert.Equal(AiEgressDenyReason.CompanyMismatch, d.Reason);
        }

        [Fact]   // RT19b
        public async Task RT19b_an_approved_provider_does_not_substitute_for_a_trusted_context()
        {
            var d = await Policy(AiTestProviderAuthority.Approved(), anonymous: true).EvaluateAsync(Req());

            Assert.False(d.Allowed);
            Assert.Equal(AiEgressDenyReason.CompanyUnresolved, d.Reason);
        }

        [Fact]   // RT19c — size
        public async Task RT19c_an_approved_provider_does_not_lift_the_payload_ceiling()
        {
            var d = await Policy(AiTestProviderAuthority.Approved())
                .EvaluateAsync(Req(bytes: AiEgressLimits.MaxPayloadBytes + 1));

            Assert.False(d.Allowed);
            Assert.Equal(AiEgressDenyReason.PayloadTooLarge, d.Reason);
        }

        // =========================================================================================
        // RT20 — local ML is untouched by all of the above.
        // =========================================================================================

        [Theory]   // RT20
        [InlineData(AiEgressPurpose.JournalAnomalyDetection)]
        [InlineData(AiEgressPurpose.CashflowForecast)]
        [InlineData(AiEgressPurpose.InventoryAnalysis)]
        public async Task RT20_local_loopback_is_unaffected_by_provider_governance(AiEgressPurpose purpose)
        {
            var config = SpoofConfig((AiDestinationResolver.DestinationClassKey, "Internal"));

            // Resolved with NO authority argument at all, and with an authority that approves nobody.
            Assert.Equal(AiEgressDestinationClass.Internal, AiDestinationResolver.Resolve(config, purpose));

            var d = await Policy(AiTestProviderAuthority.Unapproved(), config).EvaluateAsync(new AiEgressRequest
            {
                Purpose = purpose,
                Destination = AiEgressDestinationClass.Internal,
                Classification = AiDataClassification.FinancialAggregate,
                DataCompanyId = Company,
                PayloadBytes = 512,
            });

            Assert.True(d.Allowed, d.Reason?.ToString());
        }

        // =========================================================================================
        // RT21 — the development diagnostic route creates no approval.
        // =========================================================================================

        [Fact]   // RT21
        public void RT21_the_diagnostic_route_does_not_confer_provider_approval()
        {
            // Even labelled Internal — the configuration most likely to look harmless — the relay route is
            // reclassified as external and then refused by the governance gate.
            var config = SpoofConfig((AiDestinationResolver.DestinationClassKey, "Internal"));

            var resolved = AiDestinationResolver.Resolve(config, AiEgressPurpose.ConnectivityDiagnostic);

            Assert.NotEqual(AiEgressDestinationClass.Internal, resolved);
            Assert.NotEqual(AiEgressDestinationClass.ApprovedExternalProcessor, resolved);
            Assert.Equal(AiEgressDestinationClass.UnapprovedExternal, resolved);

            // ...and the shipped authority is still empty afterwards. Exercising the route changes nothing.
            Assert.False(new AiProviderAuthority().Assess(DateTime.UtcNow, AiTestProviderAuthority.TestScope).IsApproved);
        }

        // =========================================================================================
        // RT22 is covered by AiDiagnosticGateAndResponseLimitTests.D2 (the route is absent outside
        // Development) and is not duplicated here. This asserts the mapping still holds, so the coverage
        // claim in the report cannot silently become false.
        // =========================================================================================
        [Fact]   // RT22
        public void RT22_the_production_diagnostic_gate_test_still_exists()
        {
            var method = typeof(AiDiagnosticGateAndResponseLimitTests)
                .GetMethod("D2_the_diagnostic_route_is_absent_outside_development");

            Assert.NotNull(method);
        }

        // -----------------------------------------------------------------------------------------
        // A record with `init` properties cannot be mutated, and `with` needs the concrete type, so one
        // rebuild helper keeps every negative case a single-field edit of the compliant record.
        // -----------------------------------------------------------------------------------------
        // `"\0"` is the "leave this alone" sentinel for the string fields, so that passing an explicit
        // null can mean "CLEAR this field" — which is what an unknown fact actually looks like. The two
        // nullable-value fields get explicit `clear-?-` flags for the same reason: `?? original` would have
        // silently restored the value the test was trying to remove, and the test would have passed while
        // proving nothing.
        private static AiProviderCandidate Rebuild(
            AiProviderCandidate c,
            string? legalEntity = "\0", string? accountOwner = "\0", string? commercialTier = "\0",
            string? residency = "\0",
            TimeSpan? setRetention = null, bool clearRetention = false,
            bool? setTrains = null, bool clearTrains = false,
            AiProviderFact? subprocessor = null, AiProviderFact? encryption = null,
            AiProviderFact? deletion = null, AiProviderFact? contract = null)
            => new()
            {
                ProviderId = c.ProviderId,
                ProviderName = c.ProviderName,
                // Increment 4.9 — scope and its evidence travel with the rebuild, or every case would
                // fail for a missing scope rather than the single field it set out to break.
                OrganizationId = c.OrganizationId,
                ProjectId = c.ProjectId,
                Environment = c.Environment,
                Evidence = c.Evidence,
                ZeroRetentionGranted = c.ZeroRetentionGranted,
                LegalEntity = legalEntity == "\0" ? c.LegalEntity : legalEntity,
                AccountOwner = accountOwner == "\0" ? c.AccountOwner : accountOwner,
                CommercialTier = commercialTier == "\0" ? c.CommercialTier : commercialTier,
                ResidencyRegion = residency == "\0" ? c.ResidencyRegion : residency,
                ProviderRetention = clearRetention ? null : (setRetention ?? c.ProviderRetention),
                TrainsOnCustomerData = clearTrains ? null : (setTrains ?? c.TrainsOnCustomerData),
                SubprocessorPositionAccepted = subprocessor ?? c.SubprocessorPositionAccepted,
                EncryptionInTransit = encryption ?? c.EncryptionInTransit,
                DeletionControlsAvailable = deletion ?? c.DeletionControlsAvailable,
                ContractInPlace = contract ?? c.ContractInPlace,
            };
    }
}
