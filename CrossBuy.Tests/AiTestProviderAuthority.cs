using CrossBuy.BL.Platform.Ai;
using Xunit;

namespace CrossBuy.Tests
{
    // Shared test fixture for AI PROVIDER GOVERNANCE AUTHORITY (Increment 4.3, BLOCKER-1).
    //
    // Increment 4.3 made IAiProviderAuthority a required dependency of AiEgressPolicy, so every test that
    // exercises the boundary must now say which governance state it is testing under. That is the point:
    // before this, "is a provider approved?" had no answer at all in the runtime, and tests could not have
    // asked it.
    //
    // TWO FIXTURES, AND THE DIFFERENCE MATTERS.
    //
    //   Unapproved() — what the product actually ships. Used wherever the test is about refusal.
    //   Approved()   — a provider that has genuinely satisfied the REAL evaluator. Used by tests that are
    //                  about some OTHER rule (secrets, hop-1, classification, tenancy) and merely need to
    //                  get past the provider gate to reach the rule under test.
    //
    // Approved() is built by RUNNING AiProviderEvaluator over a complete record — never by hand-building
    // an assessment with State = ApprovedExternalProcessor. If the evaluator's rules tighten, this fixture
    // stops being approved and every test that leans on it fails loudly. A hand-built assessment would
    // have gone on passing while the real rule changed underneath it, which is how a fixture quietly
    // becomes a lie.
    //
    // This supplies what a REAL approved deployment would supply. It does not relax any rule, and it is
    // not an owner decision: nothing here appears in the product's own AiProviderAuthority, which remains
    // empty and is asserted empty by AiProviderRuntimeAuthorityTests.
    internal static class AiTestProviderAuthority
    {
        // ---- Increment 4.9: TEST SCOPE. Obvious placeholders — never a real OpenAI identifier ----
        //
        // A test that used a real organisation or project id would put a live account identifier into
        // tracked source and, worse, make a fixture indistinguishable from a genuine approval record.
        public const string TestOrganizationId = "TEST-ORG";
        public const string TestProjectId = "TEST-PROJECT-DEV";
        public const string TestProviderId = "test-fixture";
        public const AiProviderEnvironment TestEnvironment = AiProviderEnvironment.Development;

        public static AiProviderScope TestScope { get; } =
            new(TestProviderId, TestOrganizationId, TestProjectId, TestEnvironment);

        /// <summary>
        /// The same placeholder account, but under the OpenAI provider id.
        /// </summary>
        /// <remarks>
        /// The adapter derives its call scope from <c>OpenAiOptions.ProviderId</c> ("OpenAI"), so an
        /// approval carrying the generic "test-fixture" provider id correctly does NOT cover it — the
        /// provider id is part of the scope. Adapter tests therefore need this one. Discovering that by
        /// watching them fail was the scope check doing its job.
        /// </remarks>
        public static AiProviderScope OpenAiTestScope { get; } =
            new(OpenAiOptions.ProviderId, TestOrganizationId, TestProjectId, TestEnvironment);

        /// <summary>
        /// A stub that honours SCOPE, exactly as the product authority does.
        /// </summary>
        /// <remarks>
        /// It would have been easier to return the same assessment for every scope. That would have made
        /// every scope test pass vacuously, which is worse than having no scope tests — the fixture must
        /// enforce the rule it is helping to prove.
        /// </remarks>
        private sealed class Stub : IAiProviderAuthority
        {
            private readonly AiProviderAssessment _assessment;
            private readonly AiProviderScope? _covers;

            public Stub(AiProviderAssessment a, AiProviderScope? covers = null)
            {
                _assessment = a;
                _covers = covers;
            }

            public AiProviderAssessment Assess(DateTime nowUtc, AiProviderScope requested)
            {
                ArgumentNullException.ThrowIfNull(requested);

                if (!requested.IsComplete) return NotCovered(requested);

                // The SAME ORDER as AiProviderAuthority: the record's own verdict first, coverage only
                // after. A non-approved record reports WHY it is not approved — Rejected stays Rejected,
                // and the missing-fact list survives. An earlier version short-circuited every
                // non-approved record to "not covered", which flattened Rejected into UnderAssessment and
                // emptied MissingFacts, so the matrix tests stopped asserting the rule in their names.
                if (!_assessment.IsApproved) return _assessment;

                if (_covers is null || !_covers.Covers(requested)) return NotCovered(requested);

                return _assessment;
            }

            private static AiProviderAssessment NotCovered(AiProviderScope requested) => new()
            {
                State = AiProviderState.UnderAssessment,
                Reason = $"test fixture does not cover {requested}",
                MissingFacts = Array.Empty<string>(),
            };
        }

        /// No candidate, no policy, no approvals — the shipped state. Denies.
        public static IAiProviderAuthority Unapproved()
            => new Stub(AiProviderEvaluator.Evaluate(null, new AiProviderRequirement(), null, DateTime.UtcNow));

        /// A complete, compliant, currently-approved record, verified through the real evaluator.
        ///
        /// BUILT FROM THE SHARED BUILDERS BELOW, not from its own inline copy. It had one, and Increment
        /// 4.4 proved why that was a mistake: adding the ZeroRetentionGranted requirement updated the
        /// builder and left the duplicate behind, so twenty-odd unrelated tests failed on a fixture that
        /// looked correct in the file everyone would think to edit. One definition, one place to change.
        public static IAiProviderAuthority Approved()
        {
            var assessment = AiProviderEvaluator.Evaluate(
                CompliantCandidate(), CompliantRequirement(), FullApproval(), DateTime.UtcNow);

            // If this ever fails, the fixture — not the rule — is what must change.
            Assert.True(assessment.IsApproved,
                "the approved test fixture must actually satisfy the real evaluator: " + assessment.Reason);

            // Increment 4.9 — the stub covers exactly the scope the candidate was approved for, so a
            // request for any other project or environment is refused by the fixture too.
            return new Stub(assessment, CompliantCandidate().Scope);
        }

        // -----------------------------------------------------------------------------------------
        // BUILDERS for the runtime governance matrix (Increment 4.3 continuation).
        //
        // Every negative case is built by taking the COMPLIANT record and breaking exactly ONE thing.
        // That is deliberate: a fixture assembled independently per case can fail for a reason the test
        // did not intend, and then the test proves nothing about the rule in its name.
        // -----------------------------------------------------------------------------------------

        public const string Region = "fixture-region";

        public static AiProviderCandidate CompliantCandidate() => new()
        {
            ProviderId = TestProviderId,
            ProviderName = "TEST-ONLY FIXTURE — not a real provider",

            // Increment 4.9 — a compliant candidate must now name its account context.
            OrganizationId = TestOrganizationId,
            ProjectId = TestProjectId,
            Environment = TestEnvironment,
            LegalEntity = "Fixture Ltd",
            AccountOwner = "fixture-owner",
            CommercialTier = "Fixture Enterprise",
            TrainsOnCustomerData = false,
            ProviderRetention = TimeSpan.Zero,
            ResidencyRegion = Region,
            SubprocessorPositionAccepted = AiProviderFact.Yes,
            EncryptionInTransit = AiProviderFact.Yes,
            DeletionControlsAvailable = AiProviderFact.Yes,
            ContractInPlace = AiProviderFact.Yes,

            // Increment 4.4: the requirement below sets ZeroRetentionRequired, so a genuinely compliant
            // candidate must now carry the GRANT and the evidence behind it. Supplying what a real
            // approved provider would supply — not relaxing the rule to keep old fixtures green.
            ZeroRetentionGranted = AiProviderFact.Yes,
            Evidence = new[]
            {
                new AiProviderEvidence
                {
                    Fact = AiProviderEvidenceKeys.ZeroRetentionGranted,
                    Level = AiEvidenceLevel.OwnerAttested,
                    Owner = AiFactOwner.Procurement,
                    Reference = "TEST FIXTURE — no real grant exists",
                    // Increment 4.9 — the evidence must be ABOUT this candidate's account context. A
                    // grant for another project no longer satisfies it.
                    Scope = TestScope,
                },
            },
        };

        public static AiProviderRequirement CompliantRequirement() => new()
        {
            AcceptableResidencyRegions = new[] { Region },
            ZeroRetentionRequired = true,
            TrainingOnBusinessDataAllowed = false,
            ContractRequired = true,
        };

        public static AiProviderOwnerApproval FullApproval() => new()
        {
            SecurityApproved = true,
            DataProtectionApproved = true,
            BusinessOwnerApproved = true,
            ApprovedBy = "test-fixture",
            ApprovedAtUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            // Increment 4.9 — both ends of the window are now required.
            ValidFromUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            ExpiresAtUtc = new DateTime(2099, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        };

        /// Runs the REAL evaluator over the supplied record and wraps the verdict. Nothing here decides
        /// approval; the evaluator does, which is what makes these tests worth running.
        public static IAiProviderAuthority From(
            AiProviderCandidate? candidate,
            AiProviderRequirement requirement,
            AiProviderOwnerApproval? approval,
            DateTime? nowUtc = null)
            => new Stub(
                AiProviderEvaluator.Evaluate(candidate, requirement, approval, nowUtc ?? DateTime.UtcNow),
                // Increment 4.9 — the stub covers the scope the CANDIDATE names, exactly as the product
                // authority covers the scope its recorded candidate names. Never a wildcard: a fixture that
                // covered everything would make every scope test pass without testing anything.
                candidate?.Scope);

        /// An arbitrary governance state, for tests that sweep the state machine.
        public static IAiProviderAuthority InState(AiProviderState state)
            => new Stub(new AiProviderAssessment
            {
                State = state,
                Reason = "test fixture",
                MissingFacts = Array.Empty<string>(),
            }, TestScope);

        /// An authority approved for a SPECIFIC scope — the tool the SCOPE tests are built from.
        public static IAiProviderAuthority ApprovedFor(AiProviderScope scope)
        {
            ArgumentNullException.ThrowIfNull(scope);

            var candidate = new AiProviderCandidate
            {
                ProviderId = scope.ProviderId,
                ProviderName = "TEST-ONLY FIXTURE — not a real provider",
                OrganizationId = scope.OrganizationId,
                ProjectId = scope.ProjectId,
                Environment = scope.Environment,
                LegalEntity = "Fixture Ltd",
                AccountOwner = "fixture-owner",
                CommercialTier = "Fixture Enterprise",
                TrainsOnCustomerData = false,
                ProviderRetention = TimeSpan.Zero,
                ResidencyRegion = Region,
                SubprocessorPositionAccepted = AiProviderFact.Yes,
                EncryptionInTransit = AiProviderFact.Yes,
                DeletionControlsAvailable = AiProviderFact.Yes,
                ContractInPlace = AiProviderFact.Yes,
                ZeroRetentionGranted = AiProviderFact.Yes,
                Evidence = new[]
                {
                    new AiProviderEvidence
                    {
                        Fact = AiProviderEvidenceKeys.ZeroRetentionGranted,
                        Level = AiEvidenceLevel.OwnerAttested,
                        Owner = AiFactOwner.Procurement,
                        Reference = "TEST FIXTURE",
                        Scope = scope,
                    },
                },
            };

            var assessment = AiProviderEvaluator.Evaluate(
                candidate, CompliantRequirement(), FullApproval(), DateTime.UtcNow);

            Assert.True(assessment.IsApproved, "scoped fixture must satisfy the real evaluator: " + assessment.Reason);
            return new Stub(assessment, scope);
        }
    }
}
