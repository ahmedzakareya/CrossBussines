using CrossBuy.BL.Platform;
using CrossBuy.BL.Platform.Ai;
using CrossBuy.Models.Platform;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CrossBuy.Tests
{
    // INCREMENT 4.4 — ZDR RUNTIME EVIDENCE ENFORCEMENT (ZDR1—ZDR16).
    //
    // THE DEFECT THIS FILE EXISTS FOR. The owner made Zero Data Retention a hard gate (Decision 6A), and
    // the model could not express it. `ZeroRetentionRequired = true` made `ProviderRetention` optional,
    // and the breach check fired only on a KNOWN non-zero duration — so an unknown retention passed both.
    //
    // Proven before it was fixed, by a temporary file that asserted the broken behaviour and passed:
    //
    //   CASE A  ZDR required, grant unknown, retention absent, everything else complete and signed
    //           -> ApprovedExternalProcessor.  A provider was FULLY APPROVED with no ZDR evidence at all.
    //   CASE B  ZDR required, retention known to be 30 days                     -> Rejected  (already worked)
    //   CASE C  no field on the candidate could express an account-specific grant; the only near
    //           substitute, ProviderRetention = 0, ALSO approved — so "granted", "unknown" and
    //           "documented as zero" were indistinguishable.
    //
    // That temporary file was deleted after remediation. Keeping a green test that asserts a hole is open
    // is worse than having no test. Its cases live on below as ZDR1, ZDR2 and ZDR9/ZDR10.
    //
    // THE FIX. `ZeroRetentionGranted` is an explicit three-valued provider fact, consulted only when the
    // owner's policy requires ZDR, and a claim of "granted" must be backed by evidence at OwnerAttested
    // or ContractProven — a strength a public documentation page cannot reach.
    public class ZdrEvidenceEnforcementTests
    {
        private const int Company = 1;

        // The owner's recorded policy: ZDR required, 0 days (docs/ai/provider-owner-decision.md §13.1).
        private static AiProviderRequirement ZdrRequired() => AiTestProviderAuthority.CompliantRequirement();

        // A policy that accepts bounded retention instead — for ZDR8.
        private static AiProviderRequirement BoundedRetentionPolicy() => new()
        {
            AcceptableResidencyRegions = new[] { AiTestProviderAuthority.Region },
            ZeroRetentionRequired = false,
            MaximumProviderRetention = TimeSpan.FromDays(30),
            TrainingOnBusinessDataAllowed = false,
            ContractRequired = true,
        };

        /// The compliant candidate with the ZDR fact and its evidence replaced. Everything else is held
        /// constant so each test varies exactly one thing.
        private static AiProviderCandidate Candidate(
            AiProviderFact granted = AiProviderFact.Yes,
            AiEvidenceLevel? evidenceLevel = AiEvidenceLevel.OwnerAttested,
            string evidenceKey = AiProviderEvidenceKeys.ZeroRetentionGranted,
            TimeSpan? retention = null,
            bool clearRetention = false)
        {
            var b = AiTestProviderAuthority.CompliantCandidate();

            return new AiProviderCandidate
            {
                ProviderId = b.ProviderId,
                ProviderName = b.ProviderName,
                // Increment 4.9 — the account context travels with the rebuild, or every case here would
                // fail for a missing scope instead of the ZDR reason it exists to test.
                OrganizationId = b.OrganizationId,
                ProjectId = b.ProjectId,
                Environment = b.Environment,
                LegalEntity = b.LegalEntity,
                AccountOwner = b.AccountOwner,
                CommercialTier = b.CommercialTier,
                TrainsOnCustomerData = b.TrainsOnCustomerData,
                ResidencyRegion = b.ResidencyRegion,
                ProviderRetention = clearRetention ? null : (retention ?? b.ProviderRetention),
                SubprocessorPositionAccepted = b.SubprocessorPositionAccepted,
                EncryptionInTransit = b.EncryptionInTransit,
                DeletionControlsAvailable = b.DeletionControlsAvailable,
                ContractInPlace = b.ContractInPlace,

                ZeroRetentionGranted = granted,
                Evidence = evidenceLevel is null
                    ? Array.Empty<AiProviderEvidence>()
                    : new[]
                    {
                        new AiProviderEvidence
                        {
                            Fact = evidenceKey,
                            Level = evidenceLevel.Value,
                            Owner = AiFactOwner.Procurement,
                            Reference = "TEST FIXTURE — no real grant exists",
                            // Increment 4.9 — evidence must be ABOUT this candidate's account context.
                            Scope = AiTestProviderAuthority.TestScope,
                        },
                    },
            };
        }

        private static AiProviderAssessment Assess(
            AiProviderCandidate candidate,
            AiProviderRequirement? requirement = null,
            AiProviderOwnerApproval? approval = null,
            DateTime? now = null)
            => AiProviderEvaluator.Evaluate(
                candidate, requirement ?? ZdrRequired(),
                approval ?? AiTestProviderAuthority.FullApproval(), now ?? DateTime.UtcNow);

        // =========================================================================================
        // ZDR1 — required + UNKNOWN. The exact CASE A that used to approve.
        // =========================================================================================
        [Fact]
        public void ZDR1_required_and_unknown_is_not_approved()
        {
            var a = Assess(Candidate(granted: AiProviderFact.Unknown, evidenceLevel: null, clearRetention: true));

            Assert.False(a.IsApproved);
            Assert.Equal(AiEgressDestinationClass.UnapprovedExternal, a.DestinationClass);
            Assert.Contains("ZeroRetentionGranted", a.MissingFacts);
        }

        // WHICH fail-closed state, and why the distinction is real. Both deny; they say different things
        // to whoever has to chase the gap:
        //
        //   no signatures yet   -> UnderAssessment       "we are still gathering evidence"
        //   all three signed    -> OwnerDecisionRequired "the humans signed; evidence is still outstanding"
        //
        // The second is the more uncomfortable one and the more important to surface: it means approvals
        // were collected ahead of the facts they were meant to be approving.
        [Fact]
        public void ZDR1b_unknown_is_incomplete_rather_than_rejected()
        {
            var unsigned = Assess(
                Candidate(granted: AiProviderFact.Unknown, evidenceLevel: null),
                approval: new AiProviderOwnerApproval());

            var signed = Assess(Candidate(granted: AiProviderFact.Unknown, evidenceLevel: null));

            Assert.Equal(AiProviderState.UnderAssessment, unsigned.State);
            Assert.Equal(AiProviderState.OwnerDecisionRequired, signed.State);

            // Neither is a rejection — nobody has said no — and neither is approval.
            Assert.False(unsigned.IsApproved);
            Assert.False(signed.IsApproved);
            Assert.NotEqual(AiProviderState.Rejected, unsigned.State);
            Assert.NotEqual(AiProviderState.Rejected, signed.State);
        }

        // =========================================================================================
        // ZDR2 — required + explicitly NOT GRANTED. A known policy mismatch, so REJECTED.
        // =========================================================================================
        [Fact]
        public void ZDR2_required_and_explicitly_not_granted_is_rejected()
        {
            var a = Assess(Candidate(granted: AiProviderFact.No, evidenceLevel: null));

            Assert.Equal(AiProviderState.Rejected, a.State);
            Assert.False(a.IsApproved);
            Assert.Contains("not granted it for this account", a.Reason, StringComparison.OrdinalIgnoreCase);
        }

        // A refusal is final while it stands: it outranks every other fact still being in order, and it
        // is not softened by a full set of signatures.
        [Fact]
        public void ZDR2b_a_refusal_outranks_a_complete_and_signed_record()
            => Assert.Equal(AiProviderState.Rejected,
                Assess(Candidate(granted: AiProviderFact.No), approval: AiTestProviderAuthority.FullApproval()).State);

        // =========================================================================================
        // ZDR3 / ZDR9 — "granted" without evidence of the required strength.
        // =========================================================================================

        // ZDR3 — claimed granted, no evidence at all.
        [Fact]
        public void ZDR3_granted_without_any_evidence_is_not_approved()
        {
            var a = Assess(Candidate(granted: AiProviderFact.Yes, evidenceLevel: null));

            Assert.False(a.IsApproved);
            Assert.Contains("ZeroRetentionGrantedEvidence", a.MissingFacts);
        }

        // ZDR9 — the important one. A link to the provider's public ZDR page is the sort of thing that
        // gets recorded as ConfigProven or CodeProven, and "the provider offers ZDR" is exactly the claim
        // that must not pass for "CrossBuy has ZDR".
        [Theory]
        [InlineData(AiEvidenceLevel.Unknown)]
        [InlineData(AiEvidenceLevel.CodeProven)]
        [InlineData(AiEvidenceLevel.ConfigProven)]
        public void ZDR9_public_documentation_strength_evidence_is_not_sufficient(AiEvidenceLevel level)
        {
            var a = Assess(Candidate(granted: AiProviderFact.Yes, evidenceLevel: level));

            Assert.False(a.IsApproved);
            Assert.Contains("ZeroRetentionGrantedEvidence", a.MissingFacts);
        }

        // Evidence recorded against a DIFFERENT fact does not satisfy this one. Without the key check, any
        // unrelated attestation on the record would have counted.
        [Fact]
        public void ZDR16_evidence_for_another_fact_does_not_satisfy_the_zdr_gate()
        {
            var a = Assess(Candidate(
                granted: AiProviderFact.Yes,
                evidenceLevel: AiEvidenceLevel.ContractProven,
                evidenceKey: "SomeOtherFact"));

            Assert.False(a.IsApproved);
            Assert.Contains("ZeroRetentionGrantedEvidence", a.MissingFacts);
        }

        // =========================================================================================
        // ZDR10 — account/project-specific evidence at an acceptable strength passes the gate.
        // =========================================================================================
        [Theory]
        [InlineData(AiEvidenceLevel.OwnerAttested)]
        [InlineData(AiEvidenceLevel.ContractProven)]
        public void ZDR10_account_specific_evidence_passes_the_zdr_gate(AiEvidenceLevel level)
        {
            var a = Assess(Candidate(granted: AiProviderFact.Yes, evidenceLevel: level));

            Assert.DoesNotContain("ZeroRetentionGranted", a.MissingFacts);
            Assert.DoesNotContain("ZeroRetentionGrantedEvidence", a.MissingFacts);
        }

        // =========================================================================================
        // ZDR4 — the gate passing is NECESSARY, not SUFFICIENT.
        // =========================================================================================
        [Fact]
        public void ZDR4_a_passing_zdr_gate_does_not_approve_an_otherwise_incomplete_record()
        {
            var withGrantButNoContract = new AiProviderCandidate
            {
                ProviderId = "fixture",
                ProviderName = "FIXTURE",
                LegalEntity = "Fixture Ltd",
                AccountOwner = "fixture-owner",
                CommercialTier = "Fixture Enterprise",
                TrainsOnCustomerData = false,
                ResidencyRegion = AiTestProviderAuthority.Region,
                SubprocessorPositionAccepted = AiProviderFact.Yes,
                EncryptionInTransit = AiProviderFact.Yes,
                DeletionControlsAvailable = AiProviderFact.Yes,
                ContractInPlace = AiProviderFact.Unknown,          // <- the one thing missing
                ZeroRetentionGranted = AiProviderFact.Yes,
                Evidence = new[]
                {
                    new AiProviderEvidence
                    {
                        Fact = AiProviderEvidenceKeys.ZeroRetentionGranted,
                        Level = AiEvidenceLevel.ContractProven,
                        Owner = AiFactOwner.Procurement,
                    },
                },
            };

            var a = Assess(withGrantButNoContract);

            Assert.DoesNotContain("ZeroRetentionGranted", a.MissingFacts);   // the ZDR gate passed...
            Assert.Contains("ContractInPlace", a.MissingFacts);              // ...and approval did not follow
            Assert.False(a.IsApproved);
        }

        // ZDR5 — with everything else genuinely in order, approval is reachable. Without this the fix
        // could have been "deny everything", which is not a fix.
        [Fact]
        public void ZDR5_a_complete_record_with_a_granted_and_evidenced_zdr_can_be_approved()
        {
            var a = Assess(Candidate());

            Assert.Equal(AiProviderState.ApprovedExternalProcessor, a.State);
            Assert.True(a.IsApproved);
            Assert.Empty(a.MissingFacts);
        }

        // =========================================================================================
        // ZDR6 / ZDR7 — ZDR does not bypass the approval lifecycle.
        // =========================================================================================
        [Fact]
        public void ZDR6_a_granted_zdr_does_not_survive_an_expired_approval()
        {
            var now = new DateTime(2026, 8, 15, 0, 0, 0, DateTimeKind.Utc);
            var expired = new AiProviderOwnerApproval
            {
                SecurityApproved = true,
                DataProtectionApproved = true,
                BusinessOwnerApproved = true,
                ApprovedAtUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                // Increment 4.9 — a start date, so each case still breaks exactly ONE thing. Without it
                // every fixture here would fail on the missing window instead of the rule under test.
                ValidFromUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                ExpiresAtUtc = now.AddDays(-1),
            };

            var a = Assess(Candidate(), approval: expired, now: now);

            Assert.Equal(AiProviderState.Suspended, a.State);
            Assert.False(a.IsApproved);
        }

        [Fact]
        public void ZDR7_a_granted_zdr_does_not_survive_a_revoked_approval()
        {
            var revoked = new AiProviderOwnerApproval
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
            };

            Assert.Equal(AiProviderState.Suspended, Assess(Candidate(), approval: revoked).State);
        }

        [Fact]
        public void ZDR7b_a_granted_zdr_does_not_substitute_for_a_missing_signature()
        {
            var twoOfThree = new AiProviderOwnerApproval
            {
                SecurityApproved = true,
                DataProtectionApproved = true,
                BusinessOwnerApproved = false,
                ApprovedAtUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                // Increment 4.9 — a start date, so each case still breaks exactly ONE thing. Without it
                // every fixture here would fail on the missing window instead of the rule under test.
                ValidFromUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                ExpiresAtUtc = new DateTime(2099, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            };

            Assert.False(Assess(Candidate(), approval: twoOfThree).IsApproved);
        }

        [Fact]
        public void ZDR7c_a_granted_zdr_does_not_substitute_for_a_missing_expiry()
        {
            var noExpiry = new AiProviderOwnerApproval
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

            Assert.False(Assess(Candidate(), approval: noExpiry).IsApproved);
        }

        // =========================================================================================
        // ZDR8 — a policy that does NOT require ZDR is unaffected.
        //
        // The new fact must not become a universal mandatory gate. A future tenant accepting 30-day
        // retention must not be blocked by a field their policy never invoked.
        // =========================================================================================
        [Fact]
        public void ZDR8_the_grant_is_not_mandatory_when_the_policy_does_not_require_zdr()
        {
            var a = Assess(
                Candidate(granted: AiProviderFact.Unknown, evidenceLevel: null, retention: TimeSpan.FromDays(7)),
                requirement: BoundedRetentionPolicy());

            Assert.DoesNotContain("ZeroRetentionGranted", a.MissingFacts);
            Assert.DoesNotContain("ZeroRetentionGrantedEvidence", a.MissingFacts);
            Assert.Equal(AiProviderState.ApprovedExternalProcessor, a.State);
        }

        // ...and a bounded policy still enforces its own maximum. The new gate must not have displaced it.
        [Fact]
        public void ZDR8b_a_bounded_policy_still_rejects_retention_beyond_its_maximum()
            => Assert.Equal(AiProviderState.Rejected,
                Assess(
                    Candidate(granted: AiProviderFact.Unknown, evidenceLevel: null, retention: TimeSpan.FromDays(90)),
                    requirement: BoundedRetentionPolicy()).State);

        // Even under a bounded policy, an explicit refusal is not a rejection — the policy never asked.
        [Fact]
        public void ZDR8c_an_ungranted_zdr_is_harmless_when_the_policy_does_not_require_it()
            => Assert.Equal(AiProviderState.ApprovedExternalProcessor,
                Assess(
                    Candidate(granted: AiProviderFact.No, evidenceLevel: null, retention: TimeSpan.FromDays(7)),
                    requirement: BoundedRetentionPolicy()).State);

        // =========================================================================================
        // §7 — the four retention concepts must not be conflated.
        // =========================================================================================

        // A documented zero duration is NOT a grant. This is CASE C from the proof: before the fix these
        // were the same thing, and quoting a web page approved a provider.
        [Fact]
        public void A_documented_zero_retention_does_not_imply_a_granted_zdr()
        {
            var a = Assess(Candidate(
                granted: AiProviderFact.Unknown, evidenceLevel: null, retention: TimeSpan.Zero));

            Assert.False(a.IsApproved);
            Assert.Contains("ZeroRetentionGranted", a.MissingFacts);
        }

        // Nor does an absent duration.
        [Fact]
        public void An_absent_retention_duration_does_not_imply_a_granted_zdr()
            => Assert.Contains("ZeroRetentionGranted",
                Assess(Candidate(granted: AiProviderFact.Unknown, evidenceLevel: null, clearRetention: true))
                    .MissingFacts);

        // And a granted ZDR does not excuse a contradictory documented duration. Both rules apply; the
        // more specific refusal is reported.
        [Fact]
        public void A_granted_zdr_alongside_a_contradictory_documented_retention_still_fails()
            => Assert.Equal(AiProviderState.Rejected,
                Assess(Candidate(granted: AiProviderFact.Yes, retention: TimeSpan.FromDays(30))).State);

        // =========================================================================================
        // ZDR11—ZDR15 — the runtime, end to end.
        // =========================================================================================

        private sealed class FixedContext : IBusinessContextAccessor
        {
            private readonly BusinessContext _c;
            public FixedContext(BusinessContext c) => _c = c;
            public Task<BusinessContext> GetCurrentAsync(CancellationToken ct = default) => Task.FromResult(_c);
            public Task<BusinessContext?> TryGetCurrentAsync(CancellationToken ct = default) => Task.FromResult<BusinessContext?>(_c);
        }

        // Increment 4.9 — the stub honours scope, so a ZDR test cannot pass by ignoring it.
        private sealed class Stub : IAiProviderAuthority
        {
            private readonly AiProviderAssessment _a;
            public Stub(AiProviderAssessment a) => _a = a;

            public AiProviderAssessment Assess(DateTime nowUtc, AiProviderScope requested)
                => requested.IsComplete
                    ? _a
                    : new AiProviderAssessment
                    {
                        State = AiProviderState.UnderAssessment,
                        Reason = "incomplete scope",
                        MissingFacts = Array.Empty<string>(),
                    };
        }

        // The configuration an operator writes believing the settings file turns external AI on.
        private static IConfiguration SpoofConfig() => new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                ["AiService:DestinationClass"] = "ApprovedExternalProcessor",
                ["AiService:DeploymentMode"] = "LocalLoopback",
                ["AiService:BaseUrl"] = "http://localhost:8000",
                ["AiService:Secret"] = "a-real-looking-configured-value",
            }).Build();

        private static AiEgressPolicy Policy(IAiProviderAuthority authority)
            => new(new FixedContext(new BusinessContext
            {
                CompanyId = Company, EmployeeId = 7, UserId = "u7", Source = BusinessContextSource.Http,
            }), SpoofConfig(), NullLogger<AiEgressPolicy>.Instance, authority);

        private static AiEgressRequest Req(
            AiDataClassification classification = AiDataClassification.FinancialAggregate,
            AiEgressDestinationClass destination = AiEgressDestinationClass.ApprovedExternalProcessor,
            AiEgressPurpose purpose = AiEgressPurpose.CashflowForecast) => new()
            {
                Purpose = purpose,
                Destination = destination,
                ProviderScope = AiTestProviderAuthority.TestScope,
                Classification = classification,
                DataCompanyId = Company,
                PayloadBytes = 512,
            };

        // ZDR11 — configuration claims approval, ZDR unknown -> DENY.
        [Fact]
        public async Task ZDR11_configuration_cannot_approve_when_zdr_is_unknown()
        {
            var authority = new Stub(Assess(
                Candidate(granted: AiProviderFact.Unknown, evidenceLevel: null, clearRetention: true)));

            Assert.Equal(AiEgressDestinationClass.UnapprovedExternal,
                AiDestinationResolver.Resolve(SpoofConfig(), AiEgressPurpose.CashflowForecast,
                    authority.Assess(DateTime.UtcNow, AiTestProviderAuthority.TestScope).State));

            var d = await Policy(authority).EvaluateAsync(Req());
            Assert.False(d.Allowed);
            Assert.Equal(AiEgressDenyReason.ProviderNotApproved, d.Reason);
        }

        // ZDR12 — configuration claims approval, ZDR explicitly refused -> DENY.
        [Fact]
        public async Task ZDR12_configuration_cannot_approve_when_zdr_is_refused()
        {
            var authority = new Stub(Assess(Candidate(granted: AiProviderFact.No, evidenceLevel: null)));

            Assert.Equal(AiProviderState.Rejected, authority.Assess(DateTime.UtcNow, AiTestProviderAuthority.TestScope).State);

            var d = await Policy(authority).EvaluateAsync(Req());
            Assert.False(d.Allowed);
            Assert.Equal(AiEgressDenyReason.ProviderNotApproved, d.Reason);
        }

        // ZDR13 — a granted ZDR does not unlock personal data.
        [Fact]
        public async Task ZDR13_a_granted_zdr_does_not_unlock_personal_data()
        {
            var d = await Policy(new Stub(Assess(Candidate())))
                .EvaluateAsync(Req(AiDataClassification.PersonalData));

            Assert.False(d.Allowed);
            Assert.Equal(AiEgressDenyReason.ClassificationNotPermittedAtDestination, d.Reason);
        }

        // ZDR14 — nor free text. Decision 5 is a CONDITIONAL FUTURE allow, not an allow.
        [Fact]
        public async Task ZDR14_a_granted_zdr_does_not_unlock_free_text()
        {
            var d = await Policy(new Stub(Assess(Candidate())))
                .EvaluateAsync(Req(AiDataClassification.FreeTextBusinessContent));

            Assert.False(d.Allowed);
            Assert.Equal(AiEgressDenyReason.ClassificationNotPermittedAtDestination, d.Reason);
        }

        // ZDR15 — local loopback ML is untouched by any external ZDR state.
        [Theory]
        [InlineData(AiEgressPurpose.JournalAnomalyDetection)]
        [InlineData(AiEgressPurpose.CashflowForecast)]
        [InlineData(AiEgressPurpose.InventoryAnalysis)]
        public async Task ZDR15_local_ml_is_independent_of_every_external_zdr_state(AiEgressPurpose purpose)
        {
            foreach (var authority in new IAiProviderAuthority[]
            {
                new Stub(Assess(Candidate(granted: AiProviderFact.Unknown, evidenceLevel: null))),   // unknown
                new Stub(Assess(Candidate(granted: AiProviderFact.No, evidenceLevel: null))),        // rejected
                AiTestProviderAuthority.Unapproved(),                                                // nothing at all
            })
            {
                var d = await Policy(authority).EvaluateAsync(
                    Req(destination: AiEgressDestinationClass.Internal, purpose: purpose));

                Assert.True(d.Allowed, d.Reason?.ToString());
            }
        }

        // The shipped governance record must still approve nobody — recording a new fact type must not
        // have populated one.
        [Fact]
        public void The_shipped_authority_still_approves_nobody()
        {
            var a = new AiProviderAuthority().Assess(DateTime.UtcNow, AiTestProviderAuthority.TestScope);

            Assert.False(a.IsApproved);
            Assert.Equal(AiProviderState.Unknown, a.State);
        }
    }
}
