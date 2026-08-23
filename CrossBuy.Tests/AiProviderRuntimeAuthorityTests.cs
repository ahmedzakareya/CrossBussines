using CrossBuy.BL.Platform;
using CrossBuy.BL.Platform.Ai;
using CrossBuy.Models.Platform;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CrossBuy.Tests
{
    // BLOCKER-1 — RUNTIME PROVIDER APPROVAL ENFORCEMENT (Increment 4.3).
    //
    // THE DEFECT THIS FILE EXISTS FOR. AiProviderEvaluator — the whole owner-approval state machine, its
    // eleven mandatory facts, its three signatures, its expiry and its revocation — was referenced by
    // exactly two files: its own definition and its own tests. It was NOT on any runtime path. The runtime
    // took its destination class from configuration alone:
    //
    //     AiService:DestinationClass  ->  AiDestinationResolver.Resolve  ->  AiEgressDestinationClass
    //
    // so an operator typing "ApprovedExternalProcessor" into a settings file obtained approved-processor
    // authority without a provider candidate existing, without the owner policy being complete, without a
    // single approval being recorded, and with no expiry to lapse. The governance model was documentation
    // that the running program did not consult.
    //
    // WHY "NO CURRENT EXPOSURE" WAS NOT AN ANSWER. The shipped production template holds
    // "__SET_ON_SERVER__", which does not parse, so the deployed default denied. That made the defect
    // latent, not absent — and latent is what it stays until the day someone configures a real endpoint,
    // at which point one settings line is the whole distance between "governed" and "sending".
    //
    // THE FIX. Governance authority is now a separate input from technical configuration, and the ONE
    // canonical boundary re-derives it independently rather than trusting whatever destination class its
    // caller arrived with. Configuration still says WHERE to send; only the governance record says WHETHER
    // an external processor is approved.
    //
    // These tests were written in their INVERTED form first, asserting the insecure behaviour, and they
    // passed — which is how the bypass was proven rather than argued. They are kept here permanently in
    // their corrected form so the bypass cannot return unnoticed.
    public class AiProviderRuntimeAuthorityTests
    {
        private const int Company = 1;

        private sealed class FixedContext : IBusinessContextAccessor
        {
            private readonly BusinessContext? _ctx;
            public FixedContext(BusinessContext? c) => _ctx = c;
            public Task<BusinessContext> GetCurrentAsync(CancellationToken ct = default) => Task.FromResult(_ctx!);
            public Task<BusinessContext?> TryGetCurrentAsync(CancellationToken ct = default) => Task.FromResult(_ctx);
        }

        // An authority that returns whatever assessment the test hands it. This is the ONLY way a test can
        // obtain approval — there is deliberately no configuration key, and no public constructor on the
        // product's own authority, that can produce one.
        // Increment 4.9 — the stub honours scope, so a test cannot pass by ignoring it.
        private sealed class StubAuthority : IAiProviderAuthority
        {
            private readonly AiProviderAssessment _assessment;
            public StubAuthority(AiProviderAssessment a) => _assessment = a;

            public AiProviderAssessment Assess(DateTime nowUtc, AiProviderScope requested)
                => requested.IsComplete
                    ? _assessment
                    : new AiProviderAssessment
                    {
                        State = AiProviderState.UnderAssessment,
                        Reason = "incomplete scope",
                        MissingFacts = Array.Empty<string>(),
                    };
        }

        /// A genuinely approved provider, built by running the REAL evaluator over a complete record —
        /// never by hand-constructing an "approved" assessment. If the evaluator's rules tighten, this
        /// fixture stops being approved and these tests fail, which is the point.
        ///
        /// INCREMENT 4.4: this was a second inline copy of the same fixture. It is now delegated to the
        /// shared builder, because keeping two copies meant a tightened rule updated one and silently
        /// left the other describing a provider that no longer qualified.
        internal static IAiProviderAuthority ApprovedAuthority() => AiTestProviderAuthority.Approved();

        private static IAiProviderAuthority UnapprovedAuthority()
            => new StubAuthority(AiProviderEvaluator.Evaluate(null, new AiProviderRequirement(), null, DateTime.UtcNow));

        private static IConfiguration Config(params (string, string?)[] pairs)
        {
            var d = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                ["AiService:DeploymentMode"] = "LocalLoopback",
                ["AiService:BaseUrl"] = "http://localhost:8000",
                ["AiService:Secret"] = "a-real-looking-configured-value",
            };
            foreach (var (k, v) in pairs) d[k] = v;
            return new ConfigurationBuilder().AddInMemoryCollection(d).Build();
        }

        private static AiEgressPolicy Policy(IConfiguration config, IAiProviderAuthority authority)
            => new(new FixedContext(new BusinessContext
            {
                CompanyId = Company,
                EmployeeId = 7,
                UserId = "u7",
                Source = BusinessContextSource.Http,
            }), config, NullLogger<AiEgressPolicy>.Instance, authority);

        private static AiEgressRequest Req(
            AiEgressDestinationClass destination = AiEgressDestinationClass.ApprovedExternalProcessor,
            AiDataClassification classification = AiDataClassification.FinancialAggregate,
            AiEgressPurpose purpose = AiEgressPurpose.CashflowForecast) => new()
            {
                Purpose = purpose,
                Destination = destination,
                ProviderScope = AiTestProviderAuthority.TestScope,
                Classification = classification,
                DataCompanyId = Company,
                PayloadBytes = 512,
            };

        // -----------------------------------------------------------------------------------------
        // B1 — THE BYPASS ITSELF. This is the exact scenario that used to succeed.
        // -----------------------------------------------------------------------------------------

        // Configuration says "ApprovedExternalProcessor". Nothing else in the world says so: no candidate,
        // no owner policy, no approvals. Before the fix, the resolver handed back the approved class and
        // the policy allowed the send. Now the settings file describes an INTENT that governance refuses.
        [Fact]
        public void B1_configuration_alone_cannot_produce_approved_processor_authority()
        {
            var config = Config((AiDestinationResolver.DestinationClassKey, "ApprovedExternalProcessor"));

            Assert.Equal(AiEgressDestinationClass.UnapprovedExternal,
                AiDestinationResolver.Resolve(config, AiEgressPurpose.CashflowForecast));
        }

        // The same value, with an authority that HAS been approved, does resolve — otherwise the fix would
        // simply have removed the capability rather than governed it.
        [Fact]
        public void B2_the_same_configuration_resolves_when_governance_has_approved()
        {
            var config = Config((AiDestinationResolver.DestinationClassKey, "ApprovedExternalProcessor"));

            Assert.Equal(AiEgressDestinationClass.ApprovedExternalProcessor,
                AiDestinationResolver.Resolve(config, AiEgressPurpose.CashflowForecast,
                    AiProviderState.ApprovedExternalProcessor));
        }

        // Every non-approved state is refused, not just the unknown one. A suspended or rejected provider
        // is as unapproved as one that was never assessed.
        [Theory]
        [InlineData(AiProviderState.Unknown)]
        [InlineData(AiProviderState.UnderAssessment)]
        [InlineData(AiProviderState.OwnerDecisionRequired)]
        [InlineData(AiProviderState.Rejected)]
        [InlineData(AiProviderState.Suspended)]
        public void B3_no_state_other_than_approved_yields_the_approved_class(AiProviderState state)
        {
            var config = Config((AiDestinationResolver.DestinationClassKey, "ApprovedExternalProcessor"));

            Assert.Equal(AiEgressDestinationClass.UnapprovedExternal,
                AiDestinationResolver.Resolve(config, AiEgressPurpose.CashflowForecast, state));
        }

        // -----------------------------------------------------------------------------------------
        // B4 — THE POLICY DOES NOT TRUST ITS CALLER.
        //
        // Hardening the resolver alone would leave the hole open to anyone who never called it: the
        // destination class arrives on the request object, and a caller can put anything there. The ONE
        // canonical boundary therefore re-derives authority itself.
        // -----------------------------------------------------------------------------------------
        [Fact]
        public async Task B4_a_caller_claiming_the_approved_class_is_refused_without_governance()
        {
            // Note the destination is passed DIRECTLY, bypassing the resolver entirely.
            var d = await Policy(Config(), UnapprovedAuthority())
                .EvaluateAsync(Req(AiEgressDestinationClass.ApprovedExternalProcessor));

            Assert.False(d.Allowed);
            Assert.Equal(AiEgressDenyReason.ProviderNotApproved, d.Reason);
            Assert.Null(d.Approval);
        }

        [Fact]
        public async Task B5_the_same_claim_is_honoured_once_governance_approves()
        {
            var d = await Policy(Config(), ApprovedAuthority())
                .EvaluateAsync(Req(AiEgressDestinationClass.ApprovedExternalProcessor));

            Assert.True(d.Allowed, d.Reason?.ToString());
            Assert.NotNull(d.Approval);
        }

        // -----------------------------------------------------------------------------------------
        // B6 — APPROVAL IS NECESSARY, NOT SUFFICIENT.
        //
        // The point of the whole increment is that adding a gate must not turn the other gates into a
        // formality. An approved provider still cannot receive what it was never approved for.
        // -----------------------------------------------------------------------------------------
        [Fact]
        public async Task B6_an_approved_provider_still_cannot_receive_free_text()
        {
            var d = await Policy(Config(), ApprovedAuthority()).EvaluateAsync(
                Req(classification: AiDataClassification.FreeTextBusinessContent));

            Assert.False(d.Allowed);
            Assert.Equal(AiEgressDenyReason.ClassificationNotPermittedAtDestination, d.Reason);
        }

        [Fact]
        public async Task B7_an_approved_provider_still_cannot_receive_personal_data()
        {
            var d = await Policy(Config(), ApprovedAuthority()).EvaluateAsync(
                Req(classification: AiDataClassification.PersonalData));

            Assert.False(d.Allowed);
            Assert.Equal(AiEgressDenyReason.ClassificationNotPermittedAtDestination, d.Reason);
        }

        [Fact]
        public async Task B8_an_approved_provider_still_cannot_cross_a_company_boundary()
        {
            var req = new AiEgressRequest
            {
                Purpose = AiEgressPurpose.CashflowForecast,
                Destination = AiEgressDestinationClass.ApprovedExternalProcessor,
                ProviderScope = AiTestProviderAuthority.TestScope,
                Classification = AiDataClassification.FinancialAggregate,
                DataCompanyId = Company + 1,          // a company the caller is not in
                PayloadBytes = 512,
            };

            var d = await Policy(Config(), ApprovedAuthority()).EvaluateAsync(req);

            Assert.False(d.Allowed);
            Assert.Equal(AiEgressDenyReason.CompanyMismatch, d.Reason);
        }

        [Fact]
        public async Task B9_an_approved_provider_still_requires_a_usable_credential()
        {
            var d = await Policy(Config(("AiService:Secret", "__SET_ON_SERVER__")), ApprovedAuthority())
                .EvaluateAsync(Req());

            Assert.False(d.Allowed);
            Assert.Equal(AiEgressDenyReason.DestinationCredentialMissing, d.Reason);
        }

        [Fact]
        public async Task B10_an_approved_provider_still_requires_a_valid_hop_one_deployment()
        {
            var d = await Policy(
                    Config(("AiService:DeploymentMode", "RemoteSecure"), ("AiService:BaseUrl", "http://ai.example.com")),
                    ApprovedAuthority())
                .EvaluateAsync(Req());

            Assert.False(d.Allowed);
            Assert.Equal(AiEgressDenyReason.HopOneDeploymentInvalid, d.Reason);
        }

        // -----------------------------------------------------------------------------------------
        // B11 — LOCAL ML IS UNTOUCHED.
        //
        // The gate applies to the EXTERNAL class only. Requiring provider approval for loopback ML would
        // have taken a working, approved, entirely-internal capability offline in the name of governing
        // something it never used.
        // -----------------------------------------------------------------------------------------
        [Theory]
        [InlineData(AiEgressPurpose.JournalAnomalyDetection)]
        [InlineData(AiEgressPurpose.CashflowForecast)]
        [InlineData(AiEgressPurpose.InventoryAnalysis)]
        public void B11_internal_loopback_needs_no_provider_approval(AiEgressPurpose purpose)
        {
            var config = Config((AiDestinationResolver.DestinationClassKey, "Internal"));

            // No authority argument at all — the deny-by-default overload — and Internal still resolves.
            Assert.Equal(AiEgressDestinationClass.Internal, AiDestinationResolver.Resolve(config, purpose));
        }

        [Fact]
        public async Task B12_internal_egress_is_allowed_with_no_provider_approved()
        {
            var d = await Policy(Config(), UnapprovedAuthority()).EvaluateAsync(
                Req(AiEgressDestinationClass.Internal, AiDataClassification.FinancialAggregate));

            Assert.True(d.Allowed, d.Reason?.ToString());
        }

        // The diagnostic relay keeps its own rule: it can never be Internal. Under the new gate it lands
        // on UnapprovedExternal rather than on the approved class, which is stricter than before and
        // correct — a relay to a third party is not approved merely because it is a relay.
        [Fact]
        public void B13_the_diagnostic_relay_is_external_and_unapproved_by_default()
        {
            var config = Config((AiDestinationResolver.DestinationClassKey, "Internal"));

            var resolved = AiDestinationResolver.Resolve(config, AiEgressPurpose.ConnectivityDiagnostic);

            Assert.NotEqual(AiEgressDestinationClass.Internal, resolved);
            Assert.Equal(AiEgressDestinationClass.UnapprovedExternal, resolved);
        }

        // -----------------------------------------------------------------------------------------
        // B14 — THE SHIPPED GOVERNANCE RECORD IS EMPTY, AND CANNOT BE FILLED FROM OUTSIDE THE SOURCE.
        // -----------------------------------------------------------------------------------------

        // What the product actually ships: no provider approved, today or on any date.
        [Fact]
        public void B14_the_shipped_authority_approves_nobody()
        {
            var authority = new AiProviderAuthority();

            foreach (var when in new[]
            {
                new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                DateTime.UtcNow,
                new DateTime(2099, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            })
            {
                var a = authority.Assess(when, AiTestProviderAuthority.TestScope);
                Assert.False(a.IsApproved);
                Assert.Equal(AiProviderState.Unknown, a.State);
                Assert.Equal(AiEgressDestinationClass.UnapprovedExternal, a.DestinationClass);
            }
        }

        // The authority must not grow a configuration seam. This is the rule the whole increment exists to
        // enforce, so it is asserted structurally rather than trusted to review.
        [Fact]
        public void B15_the_authority_reads_no_configuration_environment_or_request()
        {
            var forbidden = new[]
            {
                "IConfiguration", "IConfigurationRoot", "IConfigurationSection",
                "HttpContext", "IHttpContextAccessor", "Environment",
            };

            foreach (var ctor in typeof(AiProviderAuthority).GetConstructors())
                foreach (var p in ctor.GetParameters())
                    Assert.DoesNotContain(p.ParameterType.Name, forbidden);

            var src = ReadSource("CrossBuy", "BL", "Platform", "Ai", "AiProviderAuthority.cs");
            var code = StripComments(src);

            foreach (var name in forbidden)
                Assert.DoesNotContain(name, code, StringComparison.Ordinal);

            // ...and specifically not the key that used to be the whole authority.
            Assert.DoesNotContain("DestinationClass", code, StringComparison.Ordinal);
        }

        // No provider name may be pre-wired into the authority. A named provider sitting in the governance
        // record is an approval nobody signed.
        [Theory]
        [InlineData("Azure")]
        [InlineData("OpenAI")]
        [InlineData("Anthropic")]
        [InlineData("Claude")]
        [InlineData("Gemini")]
        public void B16_no_provider_is_pre_wired_into_the_governance_record(string provider)
        {
            var code = StripComments(ReadSource("CrossBuy", "BL", "Platform", "Ai", "AiProviderAuthority.cs"));
            Assert.DoesNotContain(provider, code, StringComparison.OrdinalIgnoreCase);
        }

        // -----------------------------------------------------------------------------------------
        // B17 — THE COMPOSITION ROOT ACTUALLY WIRES IT.
        //
        // The gate is only real if the running application has an authority to ask. A missing registration
        // would not fail the build — it would throw on first request to an AI endpoint, in production,
        // which is the worst place to discover it. Stage1DiWiringTests reconstructs its own graph and does
        // not cover the AI surface, so this is asserted here against Program.cs itself.
        // -----------------------------------------------------------------------------------------
        [Fact]
        public void B17_the_composition_root_registers_the_provider_authority()
        {
            var program = StripComments(ReadSource("CrossBuy", "Program.cs"));

            Assert.Contains("IAiProviderAuthority", program, StringComparison.Ordinal);
            Assert.Contains("AiProviderAuthority>()", program, StringComparison.Ordinal);
        }

        // The dependency must stay REQUIRED. A defaulted or nullable parameter would let a future call site
        // construct a policy with no governance at all — the same shape of hole BLOCKER-1 was, reopened by
        // convenience rather than by intent.
        [Fact]
        public void B18_the_authority_is_a_required_constructor_dependency()
        {
            foreach (var type in new[] { typeof(AiEgressPolicy), typeof(CrossBuy.BL.AiInsightsService) })
            {
                var ctor = Assert.Single(type.GetConstructors());
                var p = Assert.Single(ctor.GetParameters(), x => x.ParameterType == typeof(IAiProviderAuthority));

                Assert.False(p.HasDefaultValue,
                    $"{type.Name}'s provider authority must not be optional — an omitted governance " +
                    "dependency is an ungoverned egress path.");
            }
        }

        private static string ReadSource(params string[] parts)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "CrossBuy.sln"))) dir = dir.Parent;
            Assert.NotNull(dir);
            return File.ReadAllText(Path.Combine(new[] { dir!.FullName }.Concat(parts).ToArray()));
        }

        // Comments explain the rule and therefore quote the very words the rule forbids. Stripping them
        // first is the same lesson three earlier guards in this suite had to learn.
        private static string StripComments(string source)
        {
            var withoutBlocks = System.Text.RegularExpressions.Regex.Replace(
                source, @"/\*.*?\*/", "", System.Text.RegularExpressions.RegexOptions.Singleline);
            var sb = new System.Text.StringBuilder(withoutBlocks.Length);
            foreach (var line in withoutBlocks.Split('\n'))
            {
                int slash = line.IndexOf("//", StringComparison.Ordinal);
                sb.Append(slash >= 0 ? line[..slash] : line).Append('\n');
            }
            return sb.ToString();
        }
    }
}
