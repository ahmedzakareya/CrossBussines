using CrossBuy.BL.Platform;
using CrossBuy.BL.Platform.Ai;
using CrossBuy.Models.Platform;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CrossBuy.Tests
{
    // THE RECORDED OWNER POLICY, RUN THROUGH THE REAL EVALUATOR.
    //
    // The Business Owner has recorded eight policy decisions and selected OpenAI API as the candidate
    // (docs/ai/provider-owner-decision.md §13). This file encodes that policy and the candidate facts
    // that are actually EVIDENCED, runs AiProviderEvaluator over them, and pins the result.
    //
    // WHY THIS IS A TEST AND NOT A PARAGRAPH. A document claiming "the evaluator would return
    // UnderAssessment" is an assertion nobody re-checks. The whole lesson of BLOCKER-1 was that a
    // governance rule which nothing executes is a rule in name only. So the recorded policy is executed.
    //
    // WHAT IT IS NOT. This is not an approval and cannot become one. It lives in the test assembly; the
    // product's own AiProviderAuthority is untouched and still approves nobody. Nothing here is read at
    // runtime, and the two tests at the end of this file exist to keep it that way.
    //
    // NO ACCOUNT-SPECIFIC FACT IS INVENTED. Six facts are UNKNOWN because nobody has established them,
    // and they are left unknown deliberately — that is what makes the result meaningful.
    public class OpenAiCandidateAssessmentTests
    {
        private const int Company = 1;

        // ---- THE RECORDED OWNER POLICY (docs/ai/provider-owner-decision.md §13.1) ----
        //
        // D2 = GLOBAL, D3 = no training, D6A = ZDR required, D6B = 0 days, D7 = contract required.
        // D1, D4, D5 and D8 have no field on this type — see §13.5's mapping and the gaps it records.
        internal static AiProviderRequirement RecordedOwnerPolicy() => new()
        {
            AcceptableResidencyRegions = new[] { "Global" },   // D2
            TrainingOnBusinessDataAllowed = false,             // D3
            ZeroRetentionRequired = true,                      // D6A
            MaximumProviderRetention = TimeSpan.Zero,          // D6B
            ContractRequired = true,                           // D7
        };

        // ---- THE OPENAI CANDIDATE, EVIDENCED FACTS ONLY (provider-decision-package.md §30.1) ----
        //
        // INCREMENT 4.10 — TWO CANDIDATES, NOT ONE. The account now has a Development project and a
        // Production project, and a candidate describes ONE scope. Modelling them as a single record
        // would reintroduce, in the documentary record, exactly the provider-global assumption
        // Increment 4.9 removed from the code.
        //
        // THE PROVIDER ID WAS ALSO WRONG, AND SILENTLY SO. It read "openai-api" while the runtime scope
        // uses OpenAiOptions.ProviderId ("OpenAI"). Nothing failed, because this record is documentary
        // and the runtime record is empty — but ProviderId is one of the four parts of a scope, so the
        // day someone transcribed this candidate into AiProviderAuthority it would have covered no
        // request at all, and the denial would have looked like a governance decision rather than a
        // typo. Aligned here, and pinned by a test below.
        internal static AiProviderCandidate OpenAiAsEvidenced(
            AiProviderEnvironment environment = AiProviderEnvironment.Development) => new()
        {
            ProviderId = OpenAiOptions.ProviderId,
            ProviderName = "OpenAI API",

            // ---- INCREMENT 4.10: ESTABLISHED. Returned by verification Item 1A. ----
            //
            // These are the first facts on this record that anyone has actually established, and they
            // are identifiers rather than assurances — they say WHICH account is under assessment, not
            // that it satisfies anything. The missing-fact list below shrinks by three as a result, and
            // nothing else about the assessment changes: still UnderAssessment, still not approved.
            OrganizationId = OpenAiAccountScopeRegistrationTests.RealOrganizationId,
            ProjectId = environment == AiProviderEnvironment.Production
                ? OpenAiAccountScopeRegistrationTests.RealProductionProjectId
                : OpenAiAccountScopeRegistrationTests.RealDevelopmentProjectId,
            Environment = environment,

            // EVIDENCED. OpenAI's API documentation: data sent to the API is not used to train or
            // improve OpenAI models unless the customer explicitly opts in. Documentation-level, not a
            // contract term for CrossBuy — which is why D7 exists.
            TrainsOnCustomerData = false,

            // EVIDENCED. Default global processing; the owner's product-level policy is GLOBAL.
            ResidencyRegion = "Global",

            // CODE-PROVEN on our side: AiHopOnePolicy mandates HTTPS for RemoteSecure, and the OpenAI
            // API is HTTPS-only. The one fact here that does not depend on anyone's answer.
            EncryptionInTransit = AiProviderFact.Yes,

            // INCREMENT 4.4 — recorded EXPLICITLY as Unknown rather than left to the default, because
            // this is the owner's hard gate and its state should be visible where the record is read.
            //
            // OpenAI's documentation establishes that ZDR EXISTS and requires the provider's prior
            // approval. It establishes nothing about CrossBuy. No organisation or project has been
            // named, no grant has been requested, and no confirmation exists — so the honest value is
            // Unknown, and it stays Unknown until item 1 of the verification intake returns evidence at
            // OwnerAttested or better (docs/ai/provider-owner-decision.md §14.2).
            ZeroRetentionGranted = AiProviderFact.Unknown,

            // ---- everything below is UNKNOWN, and stays unknown until someone establishes it ----
            //
            // LegalEntity                   — which entity contracts with OpenAI. Legal/Procurement.
            // AccountOwner                  — organisation/project ownership. Infrastructure.
            // CommercialTier                — Procurement.
            // ProviderRetention             — depends on whether CrossBuy is GRANTED ZDR. See below.
            // SubprocessorPositionAccepted  — list is published; CrossBuy has not accepted it.
            // DeletionControlsAvailable     — documented; not accepted by Security/Legal.
            // ContractInPlace               — a DPA is AVAILABLE. We have not executed one.
            //
            // Each is left at its default (null / Unknown) rather than guessed.
        };

        private static AiProviderAssessment Assess()
            => AiProviderEvaluator.Evaluate(
                OpenAiAsEvidenced(), RecordedOwnerPolicy(), approval: null, DateTime.UtcNow);

        // ---------------------------------------------------------------------------------------
        // The result, pinned.
        // ---------------------------------------------------------------------------------------

        // UnderAssessment, not OwnerDecisionRequired: the code reaches OwnerDecisionRequired only when
        // every fact is satisfied and signatures are outstanding, or when facts are missing and all three
        // approvals are ALREADY signed. Here both are incomplete, so "still gathering evidence" is the
        // honest state and the one the evaluator returns.
        [Fact]
        public void The_recorded_policy_plus_evidenced_facts_yields_UnderAssessment()
        {
            var a = Assess();

            Assert.Equal(AiProviderState.UnderAssessment, a.State);
            Assert.False(a.IsApproved);
            Assert.Equal(AiEgressDestinationClass.UnapprovedExternal, a.DestinationClass);
        }

        // The exact seven. If this list shrinks, someone established a fact — and the documents must be
        // updated in the same change. If it grows, a rule tightened and the same applies.
        //
        // IT GREW BY ONE IN INCREMENT 4.4, exactly as this comment anticipated. `ZeroRetentionGranted`
        // joined the list when the ZDR gate became enforceable, and the owner's hard gate is now visible
        // in the evaluator's own output instead of only in prose.
        [Fact]
        public void The_missing_facts_are_exactly_the_seven_nobody_has_established()
        {
            var missing = Assess().MissingFacts.OrderBy(f => f, StringComparer.Ordinal).ToArray();

            // IT GREW TO TEN IN INCREMENT 4.9. Approval used to be provider-global: the record said
            // "OpenAI" and nothing about WHICH organisation, WHICH project or WHICH environment, so
            // "approve OpenAI for Development" could only be recorded as "approve OpenAI". Those three
            // became mandatory facts, and OpenAI had none of them.
            //
            // AND IT SHRANK TO SEVEN IN INCREMENT 4.10 — the first time this list has ever gone DOWN.
            // Infrastructure returned the organisation and both project identifiers, so Environment,
            // OrganizationId and ProjectId are established. Nothing was approved and no assurance was
            // obtained: what changed is that the record now says which account it is about. The seven
            // that remain are the ones that require a human to decide or a provider to confirm.
            Assert.Equal(new[]
            {
                "AccountOwner",
                "CommercialTier",
                "ContractInPlace",
                "DeletionControlsAvailable",
                "LegalEntity",
                "SubprocessorPositionAccepted",
                "ZeroRetentionGranted",
            }, missing);
        }

        // INCREMENT 4.10 — the documentary record and the running configuration must describe the SAME
        // account, or an owner signing this record would be approving something the deployment cannot
        // use. This is the check that would have caught the "openai-api" provider-id slip.
        [Theory]
        [InlineData(AiProviderEnvironment.Development)]
        [InlineData(AiProviderEnvironment.Production)]
        public void The_documented_candidate_scope_matches_what_a_deployment_would_request(
            AiProviderEnvironment environment)
        {
            var candidate = OpenAiAsEvidenced(environment);

            Assert.True(candidate.Scope.IsComplete);
            Assert.Equal(OpenAiOptions.ProviderId, candidate.Scope.ProviderId);
            Assert.Equal(environment, candidate.Scope.Environment);

            // The scope a host configured for this environment would ask for.
            var expected = new AiProviderScope(
                OpenAiOptions.ProviderId,
                OpenAiAccountScopeRegistrationTests.RealOrganizationId,
                environment == AiProviderEnvironment.Production
                    ? OpenAiAccountScopeRegistrationTests.RealProductionProjectId
                    : OpenAiAccountScopeRegistrationTests.RealDevelopmentProjectId,
                environment);

            Assert.True(candidate.Scope.Covers(expected));

            // And still covers nothing else — establishing the identifiers did not create a wildcard.
            var other = environment == AiProviderEnvironment.Production
                ? AiProviderEnvironment.Development
                : AiProviderEnvironment.Production;
            Assert.False(candidate.Scope.Covers(OpenAiAsEvidenced(other).Scope));
        }

        // The identifiers are established; the ASSESSMENT is not moved by them. Stated as its own test
        // because "we have the project id now" is exactly the sentence that precedes someone assuming
        // the provider is closer to usable than it is.
        [Theory]
        [InlineData(AiProviderEnvironment.Development)]
        [InlineData(AiProviderEnvironment.Production)]
        public void Establishing_the_identifiers_did_not_approve_either_environment(
            AiProviderEnvironment environment)
        {
            var a = AiProviderEvaluator.Evaluate(
                OpenAiAsEvidenced(environment), RecordedOwnerPolicy(), approval: null, DateTime.UtcNow);

            Assert.Equal(AiProviderState.UnderAssessment, a.State);
            Assert.False(a.IsApproved);
            Assert.Equal(AiEgressDestinationClass.UnapprovedExternal, a.DestinationClass);
            Assert.Contains("ZeroRetentionGranted", a.MissingFacts);
        }

        // The one thing recording the policy actually moved, and it moved inside the model rather than in
        // prose: the owner's own policy is complete for the first time, so the eleventh mandatory fact is
        // satisfied and no longer appears as missing.
        [Fact]
        public void The_owner_requirement_policy_is_now_complete()
        {
            Assert.True(RecordedOwnerPolicy().IsComplete);
            Assert.DoesNotContain("OwnerRequirementPolicy", Assess().MissingFacts);
        }

        // Selection is not approval. Naming OpenAI moved the SUBJECT of the assessment, not its OUTCOME.
        [Fact]
        public void Selecting_openai_does_not_approve_openai()
        {
            var a = Assess();

            Assert.Equal("OpenAI API", OpenAiAsEvidenced().ProviderName);
            Assert.False(a.IsApproved);
            Assert.NotEqual(AiProviderState.ApprovedExternalProcessor, a.State);
        }

        // ---------------------------------------------------------------------------------------
        // THE ENFORCEMENT GAP — CLOSED IN INCREMENT 4.4.
        //
        // This test previously asserted the DEFECT: under a ZDR policy, ProviderRetention stopped being
        // mandatory and the breach check fired only on a known non-zero duration, so an unknown retention
        // passed both checks and the owner's hard gate enforced nothing. It was written to fail the day
        // the gap closed. It did, and this is its corrected form.
        //
        // The duration is STILL not mandatory under a ZDR policy, and that is now correct rather than a
        // hole: the duration answers "what does the provider document?", while the gate now asks the
        // question the owner actually set — "has ZDR been GRANTED to us, and can we show it?".
        // ---------------------------------------------------------------------------------------
        [Fact]
        public void The_zdr_gate_is_now_enforced_by_the_grant_fact_not_by_the_duration()
        {
            var assessment = Assess();

            // The duration remains unestablished and remains unrequired — a documented duration is not
            // what a ZDR policy needs.
            Assert.Null(OpenAiAsEvidenced().ProviderRetention);
            Assert.DoesNotContain("ProviderRetention", assessment.MissingFacts);

            // ...but the GRANT is now demanded, and OpenAI's is unknown. This is the line that did not
            // exist before Increment 4.4, and it is the owner's hard gate.
            Assert.Equal(AiProviderFact.Unknown, OpenAiAsEvidenced().ZeroRetentionGranted);
            Assert.Contains("ZeroRetentionGranted", assessment.MissingFacts);

            Assert.False(assessment.IsApproved);
        }

        // The other side of the same rule, so the gap's boundary is documented too: a KNOWN retention
        // that breaches the zero-retention policy is caught, and caught as a rejection rather than as an
        // incomplete assessment.
        [Fact]
        public void A_known_thirty_day_retention_under_a_zdr_policy_is_rejected()
        {
            var withDefaultRetention = new AiProviderCandidate
            {
                ProviderId = "openai-api",
                ProviderName = "OpenAI API",
                TrainsOnCustomerData = false,
                ResidencyRegion = "Global",
                EncryptionInTransit = AiProviderFact.Yes,
                ProviderRetention = TimeSpan.FromDays(30),    // the documented default without ZDR
            };

            var a = AiProviderEvaluator.Evaluate(
                withDefaultRetention, RecordedOwnerPolicy(), approval: null, DateTime.UtcNow);

            Assert.Equal(AiProviderState.Rejected, a.State);
            Assert.Contains("zero provider-side retention", a.Reason, StringComparison.OrdinalIgnoreCase);
        }

        // ---------------------------------------------------------------------------------------
        // Recording a policy must not weaken anything. These re-assert the invariants at the BOUNDARY,
        // under the recorded policy, rather than trusting that untouched code stayed untouched.
        // ---------------------------------------------------------------------------------------

        private sealed class FixedContext : IBusinessContextAccessor
        {
            private readonly BusinessContext _c;
            public FixedContext(BusinessContext c) => _c = c;
            public Task<BusinessContext> GetCurrentAsync(CancellationToken ct = default) => Task.FromResult(_c);
            public Task<BusinessContext?> TryGetCurrentAsync(CancellationToken ct = default) => Task.FromResult<BusinessContext?>(_c);
        }

        private static IConfiguration Config() => new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                // The configuration an operator would write believing OpenAI is now approved.
                ["AiService:DestinationClass"] = "ApprovedExternalProcessor",
                ["AiService:DeploymentMode"] = "LocalLoopback",
                ["AiService:BaseUrl"] = "http://localhost:8000",
                ["AiService:Secret"] = "a-real-looking-configured-value",
            }).Build();

        private static AiEgressPolicy Policy(IAiProviderAuthority authority)
            => new(new FixedContext(new BusinessContext
            {
                CompanyId = Company, EmployeeId = 7, UserId = "u7", Source = BusinessContextSource.Http,
            }), Config(), NullLogger<AiEgressPolicy>.Instance, authority);

        // Selecting a provider in a document does not change what the runtime authority says.
        [Fact]
        public async Task Selecting_openai_does_not_change_the_runtime_authority()
        {
            var shipped = new AiProviderAuthority().Assess(DateTime.UtcNow, AiTestProviderAuthority.TestScope);
            Assert.False(shipped.IsApproved);
            Assert.Equal(AiProviderState.Unknown, shipped.State);

            // ...and the boundary still refuses, with the configuration set to the approved class.
            var d = await Policy(new AiProviderAuthority()).EvaluateAsync(new AiEgressRequest
            {
                Purpose = AiEgressPurpose.CashflowForecast,
                Destination = AiEgressDestinationClass.ApprovedExternalProcessor,
                ProviderScope = AiTestProviderAuthority.TestScope,
                Classification = AiDataClassification.FinancialAggregate,
                DataCompanyId = Company,
                PayloadBytes = 512,
            });

            Assert.False(d.Allowed);
            Assert.Equal(AiEgressDenyReason.ProviderNotApproved, d.Reason);
        }

        // D4 = personal data DENY and D5 = free text default DENY. Asserted with an APPROVED authority,
        // because the question is whether provider approval could ever unlock them. It cannot.
        [Theory]
        [InlineData(AiDataClassification.PersonalData)]
        [InlineData(AiDataClassification.FreeTextBusinessContent)]
        public async Task The_recorded_policy_does_not_unlock_personal_data_or_free_text(
            AiDataClassification classification)
        {
            var d = await Policy(AiTestProviderAuthority.Approved()).EvaluateAsync(new AiEgressRequest
            {
                Purpose = AiEgressPurpose.CashflowForecast,
                Destination = AiEgressDestinationClass.ApprovedExternalProcessor,
                ProviderScope = AiTestProviderAuthority.TestScope,
                Classification = classification,
                DataCompanyId = Company,
                PayloadBytes = 512,
            });

            Assert.False(d.Allowed);
            Assert.Equal(AiEgressDenyReason.ClassificationNotPermittedAtDestination, d.Reason);
        }

        // ---------------------------------------------------------------------------------------
        // This file must not become a backdoor.
        // ---------------------------------------------------------------------------------------

        // The shipped governance record stays empty. If someone ever copies the fixtures above into the
        // product, this fails.
        [Fact]
        public void The_product_governance_record_still_names_no_provider()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "CrossBuy.sln"))) dir = dir.Parent;
            Assert.NotNull(dir);

            var src = File.ReadAllText(Path.Combine(
                dir!.FullName, "CrossBuy", "BL", "Platform", "Ai", "AiProviderAuthority.cs"));

            // Comments are stripped first — the file explains the rule and would otherwise trip it.
            var code = System.Text.RegularExpressions.Regex.Replace(src, @"//.*", "");

            foreach (var name in new[] { "OpenAI", "openai", "Azure", "Anthropic", "Claude" })
                Assert.DoesNotContain(name, code, StringComparison.Ordinal);
        }
    }
}
