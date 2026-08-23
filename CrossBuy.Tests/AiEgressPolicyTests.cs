using CrossBuy.BL;
using CrossBuy.BL.Platform;
using CrossBuy.BL.Platform.Ai;
using CrossBuy.Models.Platform;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CrossBuy.Tests
{
    // AI Foundation Increment 3 — the egress policy matrix (E1–E12).
    //
    // Every case answers one question: may this data leave CrossBuy, for this purpose, to this
    // destination, under this authenticated context? The decision is a pure function, so it is tested
    // as one.
    public class AiEgressPolicyTests
    {
        private const int CompanyA = 1;
        private const int CompanyB = 65;

        private sealed class FixedContext : IBusinessContextAccessor
        {
            private readonly BusinessContext? _ctx;
            public FixedContext(BusinessContext? c) => _ctx = c;
            public Task<BusinessContext> GetCurrentAsync(CancellationToken ct = default)
                => _ctx == null ? throw new BusinessContextUnresolvedException("none") : Task.FromResult(_ctx);
            public Task<BusinessContext?> TryGetCurrentAsync(CancellationToken ct = default) => Task.FromResult(_ctx);
        }

        private static BusinessContext User(int company) => new()
        {
            CompanyId = company, EmployeeId = 7, UserId = "u7", Source = BusinessContextSource.Http,
        };

        // Increment 3.1 added two rules these fixtures predate, and both are STRICTER, so the fixtures
        // supply what a real deployment supplies rather than the rules being relaxed:
        //
        //   * a usable AiService:Secret — egress now fails closed without one (it used to send `?? ""`,
        //     an empty credential, which meant the payload left the estate and was merely rejected at the
        //     far end);
        //   * a LOOPBACK AiService:BaseUrl — "Internal" now means "actually local", so a deployment
        //     cannot label a remote host Internal by editing a config value.
        //
        // Both defaults are overridable per test, and the tests that exist to prove the new rules supply
        // their own values.
        private static IConfiguration Config(params (string Key, string? Value)[] pairs)
        {
            var settings = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                ["AiService:Secret"] = "test-configured-secret",
                ["AiService:BaseUrl"] = "http://localhost:8000",
                // Increment 4 made a DECLARED deployment mode mandatory. A real deployment must now say
                // what it is, so the fixture says it too.
                ["AiService:DeploymentMode"] = "LocalLoopback",
            };
            foreach (var (k, v) in pairs) settings[k] = v;

            return new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        }

        // Increment 4.3: the boundary now requires a governance authority. These tests default to an
        // APPROVED one because they are about the OTHER gates — purpose, classification, tenancy, size.
        // Testing them under an unapproved provider would mean every request denied for the same reason
        // and none of these rules exercised at all. The provider gate itself has its own file.
        private static AiEgressPolicy Policy(
            BusinessContext? user, IConfiguration? config = null, IAiProviderAuthority? authority = null)
            => new(new FixedContext(user), config ?? Config(), NullLogger<AiEgressPolicy>.Instance,
                authority ?? AiTestProviderAuthority.Approved());

        private static AiEgressRequest Req(
            AiEgressPurpose purpose = AiEgressPurpose.CashflowForecast,
            AiEgressDestinationClass destination = AiEgressDestinationClass.ApprovedExternalProcessor,
            AiDataClassification classification = AiDataClassification.FinancialAggregate,
            int company = CompanyA, int bytes = 1024)
            => new()
            {
                Purpose = purpose, Destination = destination, Classification = classification,
                // Increment 4.9 - external requests must name their account context.
                ProviderScope = AiTestProviderAuthority.TestScope,
                DataCompanyId = company, PayloadBytes = bytes,
            };

        // ---- E1: known purpose + allowed classification + approved destination -> ALLOW ----
        [Fact]
        public async Task E1_a_fully_known_request_to_an_approved_destination_is_allowed()
        {
            var d = await Policy(User(CompanyA)).EvaluateAsync(Req());

            Assert.True(d.Allowed, d.Reason?.ToString());
            Assert.NotNull(d.Approval);
            Assert.Equal(CompanyA, d.Approval!.CompanyId);
            Assert.Equal(AiEgressPurpose.CashflowForecast, d.Approval.Purpose);
        }

        // ---- E2: unknown purpose -> DENY ----
        [Fact]
        public async Task E2_an_unknown_purpose_is_denied()
        {
            var d = await Policy(User(CompanyA)).EvaluateAsync(Req(purpose: AiEgressPurpose.Unknown));
            Assert.False(d.Allowed);
            Assert.Equal(AiEgressDenyReason.UnknownPurpose, d.Reason);
            Assert.Null(d.Approval);
        }

        // ---- E3: unknown destination -> DENY ----
        [Fact]
        public async Task E3_an_unknown_destination_is_denied()
        {
            var d = await Policy(User(CompanyA)).EvaluateAsync(Req(destination: AiEgressDestinationClass.Unknown));
            Assert.False(d.Allowed);
            Assert.Equal(AiEgressDenyReason.UnknownDestination, d.Reason);
        }

        // ---- E4: unknown classification -> DENY ----
        [Fact]
        public async Task E4_an_unknown_classification_is_denied()
        {
            var d = await Policy(User(CompanyA)).EvaluateAsync(Req(classification: AiDataClassification.Unknown));
            Assert.False(d.Allowed);
            Assert.Equal(AiEgressDenyReason.UnknownClassification, d.Reason);
        }

        // A default-constructed request must be refused outright — every enum's zero is Unknown precisely
        // so a partially-populated request cannot inherit a real member's meaning.
        [Fact]
        public async Task A_default_constructed_request_is_denied()
        {
            var d = await Policy(User(CompanyA)).EvaluateAsync(new AiEgressRequest
            {
                Purpose = default, Destination = default, Classification = default,
                DataCompanyId = CompanyA, PayloadBytes = 10,
            });
            Assert.False(d.Allowed);
        }

        // ---- E5: disallowed classification/destination combination -> DENY ----
        //
        // The matrix in full. Free text is refused at an EXTERNAL destination even an approved one, and
        // personal data is refused everywhere — including Internal.
        [Theory]
        // OperationalMetadata
        [InlineData(AiDataClassification.OperationalMetadata, AiEgressDestinationClass.Internal, true)]
        [InlineData(AiDataClassification.OperationalMetadata, AiEgressDestinationClass.ApprovedExternalProcessor, true)]
        // FinancialAggregate
        [InlineData(AiDataClassification.FinancialAggregate, AiEgressDestinationClass.Internal, true)]
        [InlineData(AiDataClassification.FinancialAggregate, AiEgressDestinationClass.ApprovedExternalProcessor, true)]
        // FreeTextBusinessContent — internal only
        [InlineData(AiDataClassification.FreeTextBusinessContent, AiEgressDestinationClass.Internal, true)]
        [InlineData(AiDataClassification.FreeTextBusinessContent, AiEgressDestinationClass.ApprovedExternalProcessor, false)]
        // PersonalData — nowhere
        [InlineData(AiDataClassification.PersonalData, AiEgressDestinationClass.Internal, false)]
        [InlineData(AiDataClassification.PersonalData, AiEgressDestinationClass.ApprovedExternalProcessor, false)]
        public async Task E5_the_classification_destination_matrix_is_enforced(
            AiDataClassification classification, AiEgressDestinationClass destination, bool expectAllowed)
        {
            var d = await Policy(User(CompanyA)).EvaluateAsync(
                Req(classification: classification, destination: destination));

            Assert.Equal(expectAllowed, d.Allowed);
            if (!expectAllowed)
                Assert.Equal(AiEgressDenyReason.ClassificationNotPermittedAtDestination, d.Reason);
        }

        // An UNAPPROVED external destination takes nothing at all, whatever the classification.
        [Theory]
        [InlineData(AiDataClassification.OperationalMetadata)]
        [InlineData(AiDataClassification.FinancialAggregate)]
        [InlineData(AiDataClassification.FreeTextBusinessContent)]
        [InlineData(AiDataClassification.PersonalData)]
        public async Task An_unapproved_external_destination_receives_nothing(AiDataClassification classification)
        {
            var d = await Policy(User(CompanyA)).EvaluateAsync(
                Req(classification: classification, destination: AiEgressDestinationClass.UnapprovedExternal));

            Assert.False(d.Allowed);
            Assert.Equal(AiEgressDenyReason.DestinationNotApproved, d.Reason);
        }

        // ---- E6: payload exceeds limit -> DENY ----
        [Fact]
        public async Task E6_an_oversized_payload_is_denied()
        {
            var d = await Policy(User(CompanyA)).EvaluateAsync(Req(bytes: AiEgressLimits.MaxPayloadBytes + 1));
            Assert.False(d.Allowed);
            Assert.Equal(AiEgressDenyReason.PayloadTooLarge, d.Reason);
        }

        [Fact]
        public async Task A_payload_exactly_at_the_limit_is_allowed()
        {
            var d = await Policy(User(CompanyA)).EvaluateAsync(Req(bytes: AiEgressLimits.MaxPayloadBytes));
            Assert.True(d.Allowed);
        }

        [Fact]
        public async Task A_negative_payload_size_is_denied()
        {
            var d = await Policy(User(CompanyA)).EvaluateAsync(Req(bytes: -1));
            Assert.False(d.Allowed);
            Assert.Equal(AiEgressDenyReason.PayloadTooLarge, d.Reason);
        }

        // ---- E7: unresolved company -> DENY ----
        [Fact]
        public async Task E7_an_unresolved_company_is_denied()
        {
            var d = await Policy(user: null).EvaluateAsync(Req());
            Assert.False(d.Allowed);
            Assert.Equal(AiEgressDenyReason.CompanyUnresolved, d.Reason);
        }

        [Fact]
        public async Task A_context_with_no_company_is_denied()
        {
            var noCompany = new BusinessContext { CompanyId = 0, Source = BusinessContextSource.Http };
            var d = await Policy(noCompany).EvaluateAsync(Req());
            Assert.False(d.Allowed);
            Assert.Equal(AiEgressDenyReason.CompanyUnresolved, d.Reason);
        }

        // ---- E8: cross-company mismatch -> DENY ----
        //
        // The structural defence against a client-supplied company reaching an outbound call: even if a
        // caller passed one through, the policy compares it against the authenticated context and refuses
        // rather than correcting it. Coercion would hide the attempt.
        [Fact]
        public async Task E8_a_request_naming_another_company_is_denied_not_corrected()
        {
            var d = await Policy(User(CompanyB)).EvaluateAsync(Req(company: CompanyA));

            Assert.False(d.Allowed);
            Assert.Equal(AiEgressDenyReason.CompanyMismatch, d.Reason);
            Assert.Null(d.Approval);
        }

        // ---- E12: deterministic ----
        [Fact]
        public async Task E12_the_same_request_always_produces_the_same_decision()
        {
            var policy = Policy(User(CompanyA));
            var request = Req();

            var a = await policy.EvaluateAsync(request);
            var b = await policy.EvaluateAsync(request);
            var c = await policy.EvaluateAsync(request);

            Assert.Equal(a.Allowed, b.Allowed);
            Assert.Equal(b.Allowed, c.Allowed);
            Assert.Equal(a.AppliedPolicy, b.AppliedPolicy);
            Assert.Equal(b.AppliedPolicy, c.AppliedPolicy);
        }

        // ---- the master switch is SERVER-owned ----
        [Fact]
        public async Task Egress_can_be_disabled_by_server_configuration()
        {
            var off = Config(("AiService:EgressEnabled", "false"));
            var d = await Policy(User(CompanyA), off).EvaluateAsync(Req());

            Assert.False(d.Allowed);
            Assert.Equal(AiEgressDenyReason.PolicyDisabled, d.Reason);
        }

        // Absent configuration must not silently disable a working deployment.
        [Fact]
        public async Task Absent_configuration_leaves_egress_enabled()
            => Assert.True((await Policy(User(CompanyA), Config()).EvaluateAsync(Req())).Allowed);

        // ---- destination resolution is conservative by default ----
        //
        // Unset configuration resolves to UnapprovedExternal, NOT Internal. The service is CrossBuy's own
        // Python process, which invites calling it internal — but its own contracts describe the path as
        // ".NET -> Python -> Claude", i.e. it relays to a third party. An unassessed relay is not internal.
        [Fact]
        public void An_unconfigured_destination_resolves_to_unapproved_external()
            => Assert.Equal(AiEgressDestinationClass.UnapprovedExternal, AiDestinationResolver.Resolve(Config()));

        [Fact]
        public void A_misspelled_destination_resolves_to_unknown_never_to_a_grant()
            => Assert.Equal(AiEgressDestinationClass.Unknown,
                AiDestinationResolver.Resolve(Config((AiDestinationResolver.DestinationClassKey, "ApprovedExternal_typo"))));

        [Theory]
        [InlineData("Internal", AiEgressDestinationClass.Internal)]
        [InlineData("UnapprovedExternal", AiEgressDestinationClass.UnapprovedExternal)]
        public void A_configured_destination_resolves_explicitly(string configured, AiEgressDestinationClass expected)
            => Assert.Equal(expected,
                AiDestinationResolver.Resolve(Config((AiDestinationResolver.DestinationClassKey, configured))));

        // INCREMENT 4.3 (BLOCKER-1) — THIS CASE USED TO LIVE IN THE THEORY ABOVE, asserting that
        // configuring "ApprovedExternalProcessor" produced ApprovedExternalProcessor. It passed, and that
        // pass WAS the defect: a settings line was the entire approval, with no provider candidate, no
        // owner policy, no signatures and no expiry anywhere in the system.
        //
        // It is kept — not deleted — because a removed test leaves no trace of the rule that replaced it.
        // Configuration now states an INTENT; only the governance record grants the authority.
        [Fact]
        public void The_approved_class_cannot_be_configured_into_existence()
            => Assert.Equal(AiEgressDestinationClass.UnapprovedExternal,
                AiDestinationResolver.Resolve(
                    Config((AiDestinationResolver.DestinationClassKey, "ApprovedExternalProcessor"))));

        // Increment 3.1 — "Internal" is only honoured when the service is actually reachable at a
        // LOOPBACK address. A deployment that labels a remote host Internal is mislabelling it, most
        // likely by copying a development config, and the label must not be taken at face value.
        [Theory]
        [InlineData("http://ai.example.com:8000")]
        [InlineData("https://10.0.0.5:8000")]
        [InlineData("not-a-url")]
        [InlineData(null)]
        public void Internal_is_refused_when_the_service_is_not_actually_local(string? baseUrl)
            => Assert.Equal(AiEgressDestinationClass.UnapprovedExternal,
                AiDestinationResolver.Resolve(Config(
                    (AiDestinationResolver.DestinationClassKey, "Internal"),
                    (AiDestinationResolver.BaseUrlKey, baseUrl))));

        [Theory]
        [InlineData("http://localhost:8000", true)]
        [InlineData("http://127.0.0.1:8000", true)]
        [InlineData("http://[::1]:8000", true)]
        [InlineData("http://ai.example.com", false)]
        [InlineData("", false)]
        [InlineData(null, false)]
        public void The_loopback_rule_is_explicit(string? url, bool loopback)
            => Assert.Equal(loopback, AiDestinationResolver.IsLoopback(url));

        // The DIAGNOSTIC route relays caller-supplied text to Anthropic — established by reading the
        // Python service's own source, where only app/routers/diag.py imports the Anthropic client. It can
        // therefore never be Internal, whatever the configuration says, because "Internal" means "does not
        // forward anywhere else".
        [Fact]
        public void The_diagnostic_purpose_can_never_be_treated_as_internal()
        {
            var config = Config(
                (AiDestinationResolver.DestinationClassKey, "Internal"),
                (AiDestinationResolver.BaseUrlKey, "http://localhost:8000"));

            // The three business purposes terminate at local ML and MAY be Internal...
            Assert.Equal(AiEgressDestinationClass.Internal,
                AiDestinationResolver.Resolve(config, AiEgressPurpose.CashflowForecast));

            // ...but the relay route may not.
            Assert.NotEqual(AiEgressDestinationClass.Internal,
                AiDestinationResolver.Resolve(config, AiEgressPurpose.ConnectivityDiagnostic));
        }

        // ---- E11: the approval token carries no payload, so logging it cannot leak ----
        [Fact]
        public void E11_the_approval_token_carries_no_business_payload()
        {
            var names = typeof(AiEgressApproval).GetProperties().Select(p => p.Name).ToArray();

            // PayloadBytes is a SIZE and is deliberately present — it is what the client re-checks the
            // actual body against. It is excluded by name rather than by loosening the rule, so a
            // property genuinely carrying payload could not slip through on the same substring.
            var carriers = names.Where(n => !string.Equals(n, "PayloadBytes", StringComparison.Ordinal)).ToArray();

            foreach (var banned in new[] { "Payload", "Body", "Content", "Json", "Data", "Entries", "Items" })
                Assert.DoesNotContain(carriers, n => n.Contains(banned, StringComparison.OrdinalIgnoreCase));

            Assert.Contains("PayloadBytes", names);
        }

        // The request likewise carries only a SIZE, never the bytes themselves.
        [Fact]
        public void The_egress_request_carries_only_a_payload_size_never_the_payload()
        {
            var names = typeof(AiEgressRequest).GetProperties().Select(p => p.Name).ToArray();
            Assert.Contains("PayloadBytes", names);
            foreach (var banned in new[] { "PayloadJson", "Body", "Content", "Entries", "Items" })
                Assert.DoesNotContain(names, n => string.Equals(n, banned, StringComparison.Ordinal));
        }

        // The approval token can only be minted by the policy — the guarantee that makes the boundary
        // impossible to bypass rather than merely inconvenient to bypass.
        [Fact]
        public void The_approval_token_cannot_be_constructed_outside_the_assembly()
        {
            var ctors = typeof(AiEgressApproval).GetConstructors();   // public only
            Assert.Empty(ctors);
        }

        // IAiService must REQUIRE an approval on every outbound method. If a parameterless overload ever
        // appeared, the boundary would be optional again.
        [Fact]
        public void Every_ai_service_method_requires_an_egress_approval()
        {
            foreach (var m in typeof(IAiService).GetMethods())
                Assert.True(
                    m.GetParameters().Any(p => p.ParameterType == typeof(AiEgressApproval)),
                    $"IAiService.{m.Name} does not require an AiEgressApproval — egress could bypass the policy.");
        }
    }
}

