using CrossBuy.BL.Platform;
using CrossBuy.BL.Platform.Ai;
using CrossBuy.Models.Platform;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CrossBuy.Tests
{
    // AI Foundation Increment 4 — HOP 1 (CrossBuy -> Python) and the provider decision engine (P1–P15).
    //
    // The property under test is that the trust boundary is ENFORCED BY CODE rather than assumed from a
    // configuration value. Every dangerous combination below is a plausible configuration typo, and every
    // one of them is invisible to a per-field check.
    public class AiHopOnePolicyTests
    {
        private const string RealSecret = "a-configured-shared-secret";

        private static IConfiguration Config(
            string? mode = "LocalLoopback",
            string? baseUrl = "http://localhost:8000",
            string? destinationClass = "Internal",
            string? secret = RealSecret)
        {
            var d = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                [AiHopOnePolicy.DeploymentModeKey] = mode,
                [AiDestinationResolver.BaseUrlKey] = baseUrl,
                [AiDestinationResolver.DestinationClassKey] = destinationClass,
                ["AiService:Secret"] = secret,
            };
            return new ConfigurationBuilder().AddInMemoryCollection(d).Build();
        }

        // ---- P1/P2/P3: a genuinely local deployment is valid ----
        [Theory]
        [InlineData("http://localhost:8000")]
        [InlineData("http://127.0.0.1:8000")]
        [InlineData("http://[::1]:8000")]
        [InlineData("https://localhost:8443")]
        public void P1_P3_a_loopback_host_is_a_valid_local_deployment(string baseUrl)
        {
            var v = AiHopOnePolicy.Validate(Config(baseUrl: baseUrl));
            Assert.True(v.Ok, v.Reason);
            Assert.Equal(AiDeploymentMode.LocalLoopback, v.Mode);
        }

        // ---- P4/P5: "local" pointed at anything that is not loopback is REFUSED ----
        //
        // A private LAN address is the dangerous case: it looks internal to a human reading the config,
        // but the traffic crosses a network where plaintext is readable.
        [Theory]
        [InlineData("http://ai.example.com:8000")]          // P4 — remote hostname
        [InlineData("http://10.0.0.5:8000")]                // P5 — private LAN
        [InlineData("http://192.168.1.20:8000")]            // P5 — private LAN
        [InlineData("http://172.16.4.9:8000")]              // P5 — private LAN
        [InlineData("https://ai.example.com")]              // remote, even over HTTPS, is not "local"
        public void P4_P5_local_mode_refuses_a_non_loopback_host(string baseUrl)
        {
            var v = AiHopOnePolicy.Validate(Config(baseUrl: baseUrl));
            Assert.False(v.Ok);
            Assert.Contains("loopback", v.Reason!, StringComparison.OrdinalIgnoreCase);
        }

        // ---- P6: remote over plaintext is REFUSED, and the URL is never auto-upgraded ----
        [Theory]
        [InlineData("http://ai.example.com:8000")]
        [InlineData("http://10.0.0.5:8000")]
        public void P6_remote_mode_refuses_plaintext_http(string baseUrl)
        {
            var v = AiHopOnePolicy.Validate(Config(mode: "RemoteSecure", baseUrl: baseUrl, destinationClass: "ApprovedExternalProcessor"));
            Assert.False(v.Ok);
            Assert.Contains("HTTPS", v.Reason!, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("NOT upgraded", v.Reason!, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void Remote_over_https_is_a_valid_deployment()
        {
            var v = AiHopOnePolicy.Validate(Config(
                mode: "RemoteSecure", baseUrl: "https://ai.example.com", destinationClass: "ApprovedExternalProcessor"));
            Assert.True(v.Ok, v.Reason);
            Assert.Equal(AiDeploymentMode.RemoteSecure, v.Mode);
        }

        // A remote host declared Internal is refused as a COMBINATION — the operator's intent is wrong,
        // and silently downgrading the class would hide that.
        [Fact]
        public void A_remote_host_may_not_be_declared_internal()
        {
            var v = AiHopOnePolicy.Validate(Config(
                mode: "RemoteSecure", baseUrl: "https://ai.example.com", destinationClass: "Internal"));
            Assert.False(v.Ok);
            Assert.Contains("not internal", v.Reason!, StringComparison.OrdinalIgnoreCase);
        }

        // ---- P13/P14: undeclared mode and malformed URL both DENY ----
        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("Local")]
        [InlineData("REMOTE")]
        [InlineData("Whatever")]
        public void P13_an_undeclared_or_unrecognised_mode_is_denied(string? mode)
        {
            var v = AiHopOnePolicy.Validate(Config(mode: mode));
            Assert.False(v.Ok);
            Assert.Equal(AiDeploymentMode.Unknown, v.Mode);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("not-a-url")]
        [InlineData("localhost:8000")]          // no scheme -> not absolute
        [InlineData("//localhost:8000")]
        public void P14_a_malformed_base_url_is_denied(string? baseUrl)
            => Assert.False(AiHopOnePolicy.Validate(Config(baseUrl: baseUrl)).Ok);

        // ---- P8/P9: missing or placeholder secret denies, in BOTH modes ----
        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("__SET_ON_SERVER__-rotate-from-the-dev-secret")]
        public void P8_P9_a_missing_or_placeholder_secret_denies_the_hop(string? secret)
        {
            Assert.False(AiHopOnePolicy.Validate(Config(secret: secret)).Ok);
            Assert.False(AiHopOnePolicy.Validate(Config(
                mode: "RemoteSecure", baseUrl: "https://ai.example.com",
                destinationClass: "ApprovedExternalProcessor", secret: secret)).Ok);
        }

        // Loopback narrows WHO can reach the service; it does not authenticate the caller, so the secret
        // is required even locally. Stated as its own test because "it's only localhost" is exactly the
        // argument that would remove it.
        [Fact]
        public void The_secret_is_required_even_for_a_loopback_deployment()
            => Assert.False(AiHopOnePolicy.Validate(Config(secret: null)).Ok);

        // ---- P15: deterministic ----
        [Fact]
        public void P15_validation_is_deterministic()
        {
            var config = Config();
            var a = AiHopOnePolicy.Validate(config);
            var b = AiHopOnePolicy.Validate(config);
            var c = AiHopOnePolicy.Validate(config);
            Assert.Equal(a.Ok, b.Ok); Assert.Equal(b.Ok, c.Ok);
            Assert.Equal(a.Mode, b.Mode); Assert.Equal(b.Mode, c.Mode);
        }

        // The reason is operator-facing and must never carry the URL (the host name may itself be
        // sensitive) or the secret.
        [Fact]
        public void A_refusal_reason_never_contains_the_url_or_the_secret()
        {
            var v = AiHopOnePolicy.Validate(Config(baseUrl: "http://secret-internal-host.corp.example", secret: RealSecret));
            Assert.False(v.Ok);
            Assert.DoesNotContain("secret-internal-host", v.Reason!, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(RealSecret, v.Reason!, StringComparison.Ordinal);
        }

        // ==========================================================================================
        // The egress policy must actually REFUSE when hop 1 is invalid — the validation is only worth
        // having if it is wired in.
        // ==========================================================================================
        private sealed class FixedContext : IBusinessContextAccessor
        {
            private readonly BusinessContext _c;
            public FixedContext(BusinessContext c) => _c = c;
            public Task<BusinessContext> GetCurrentAsync(CancellationToken ct = default) => Task.FromResult(_c);
            public Task<BusinessContext?> TryGetCurrentAsync(CancellationToken ct = default) => Task.FromResult<BusinessContext?>(_c);
        }

        private static async Task<AiEgressDecision> EvaluateAsync(IConfiguration config)
            => await new AiEgressPolicy(
                new FixedContext(new BusinessContext
                {
                    CompanyId = 1, EmployeeId = 7, UserId = "u7", Source = BusinessContextSource.Http,
                }),
                // Increment 4.3: an approved governance authority, so a denial here is attributable to the
                // HOP-1 rule these tests exist for rather than to the provider gate.
                config, NullLogger<AiEgressPolicy>.Instance, AiTestProviderAuthority.Approved())
            .EvaluateAsync(new AiEgressRequest
            {
                Purpose = AiEgressPurpose.CashflowForecast,
                Destination = AiEgressDestinationClass.Internal,
                Classification = AiDataClassification.FinancialAggregate,
                DataCompanyId = 1,
                PayloadBytes = 256,
            });

        [Theory]
        [InlineData(null, "http://localhost:8000")]                    // undeclared mode
        [InlineData("LocalLoopback", "http://10.0.0.5:8000")]          // "local" but remote
        [InlineData("RemoteSecure", "http://ai.example.com")]          // remote plaintext
        [InlineData("LocalLoopback", "not-a-url")]                     // malformed
        public async Task An_invalid_hop_one_deployment_denies_egress(string? mode, string baseUrl)
        {
            var d = await EvaluateAsync(Config(mode: mode, baseUrl: baseUrl));
            Assert.False(d.Allowed);
            Assert.Equal(AiEgressDenyReason.HopOneDeploymentInvalid, d.Reason);
            Assert.Null(d.Approval);
        }

        [Fact]
        public async Task A_valid_local_deployment_permits_egress()
            => Assert.True((await EvaluateAsync(Config())).Allowed);

        // ---- P11/P12: the diagnostic relay and the local ML routes cannot be confused ----
        [Fact]
        public void P11_the_diagnostic_relay_can_never_be_classified_internal()
        {
            var config = Config();   // Internal + loopback: valid for local ML
            Assert.Equal(AiEgressDestinationClass.Internal,
                AiDestinationResolver.Resolve(config, AiEgressPurpose.InventoryAnalysis));

            // ...but the route that forwards to Anthropic may not claim it.
            Assert.NotEqual(AiEgressDestinationClass.Internal,
                AiDestinationResolver.Resolve(config, AiEgressPurpose.ConnectivityDiagnostic));
        }

        [Fact]
        public void P12_a_local_ml_route_does_not_inherit_the_diagnostic_external_destination()
        {
            var config = Config();
            foreach (var local in new[]
            {
                AiEgressPurpose.JournalAnomalyDetection,
                AiEgressPurpose.CashflowForecast,
                AiEgressPurpose.InventoryAnalysis,
            })
                Assert.Equal(AiEgressDestinationClass.Internal, AiDestinationResolver.Resolve(config, local));
        }

        // ---- P10: TLS validation is never bypassed ----
        //
        // Structural: the hooks that could weaken TLS must not appear anywhere in the product.
        [Fact]
        public void P10_no_source_file_bypasses_tls_certificate_validation()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "CrossBuy.sln"))) dir = dir.Parent;
            Assert.NotNull(dir);

            foreach (var file in Directory.GetFiles(Path.Combine(dir!.FullName, "CrossBuy"), "*.cs", SearchOption.AllDirectories))
            {
                if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                    || file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")) continue;

                // Comments are stripped before matching. Program.cs deliberately NAMES these hooks in a
                // comment explaining that they are absent, and a naive text search flags that comment as
                // the very defect it documents — the same self-inflicted false positive the
                // CORRECTION-005 ratchet had to learn about.
                var src = StripComments(File.ReadAllText(file));

                foreach (var hook in new[]
                {
                    "ServerCertificateCustomValidationCallback",
                    "DangerousAcceptAnyServerCertificateValidator",
                    "ServicePointManager.ServerCertificateValidationCallback",
                })
                    Assert.False(src.Contains(hook, StringComparison.Ordinal),
                        $"{Path.GetFileName(file)} weakens TLS certificate validation ('{hook}').");
            }
        }

        // ==========================================================================================
        // §24 — the PROVIDER DECISION ENGINE. No provider is connected; this tests the rule.
        // ==========================================================================================

        // Unknown facts can never yield approval. This is the whole design: a provider becomes
        // approvable by someone SUPPLYING facts, never by the absence of an objection.
        [Fact]
        public void P7_a_provider_with_unknown_mandatory_facts_is_not_approved()
        {
            var profile = new AiProviderProfile { ProviderName = "SomeProvider", OwnerApproved = true };
            var decision = profile.Decide();

            Assert.False(decision.IsApproved);
            Assert.Equal(AiEgressDestinationClass.UnapprovedExternal, decision.Class);
            Assert.Contains("OWNER DECISION REQUIRED", decision.Reason, StringComparison.Ordinal);
            Assert.Equal(AiProviderProfile.MandatoryFactNames.Length, profile.MissingMandatoryFacts().Count);
        }

        [Fact]
        public void A_provider_with_no_name_is_not_approved()
            => Assert.False(new AiProviderProfile().Decide().IsApproved);

        // Facts alone do not approve: somebody must accept the residual risk, and that acceptance is
        // recorded separately so it is auditable.
        [Fact]
        public void All_facts_known_but_no_owner_approval_is_still_not_approved()
        {
            var decision = AllFactsKnown(ownerApproved: false).Decide();
            Assert.False(decision.IsApproved);
            Assert.Contains("no owner approval", decision.Reason, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void All_facts_known_plus_recorded_owner_approval_is_eligible()
        {
            var decision = AllFactsKnown(ownerApproved: true).Decide();
            Assert.True(decision.IsApproved);
            Assert.Equal(AiEgressDestinationClass.ApprovedExternalProcessor, decision.Class);
        }

        // A fact answered the UNSAFE way is a refusal, not a missing answer — and the two are reported
        // differently so an operator can tell "we don't know" from "we know, and it's no".
        [Theory]
        [InlineData(nameof(AiProviderProfile.TrainingOnCustomerDataDisabled))]
        [InlineData(nameof(AiProviderProfile.EncryptionInTransit))]
        public void A_disqualifying_answer_is_denied_rather_than_pending(string which)
        {
            var p = which == nameof(AiProviderProfile.TrainingOnCustomerDataDisabled)
                ? Profile(training: AiProviderFact.No)
                : Profile(encryption: AiProviderFact.No);

            var decision = p.Decide();
            Assert.False(decision.IsApproved);
            Assert.Contains("DENIED", decision.Reason, StringComparison.Ordinal);
        }

        // No provider is hardcoded as approved anywhere — the engine must not have a favourite.
        [Fact]
        public void No_provider_is_hardcoded_as_approved()
        {
            foreach (var name in new[] { "Anthropic", "Claude", "OpenAI", "Azure", "Gemini" })
                Assert.False(new AiProviderProfile { ProviderName = name, OwnerApproved = true }.Decide().IsApproved,
                    $"'{name}' must not be approved without its mandatory facts.");
        }

        // Shared with the TLS guard above.
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

        private static AiProviderProfile AllFactsKnown(bool ownerApproved) => Profile(ownerApproved: ownerApproved);

        private static AiProviderProfile Profile(
            AiProviderFact training = AiProviderFact.Yes,
            AiProviderFact encryption = AiProviderFact.Yes,
            bool ownerApproved = true) => new()
            {
                ProviderName = "ExampleProvider",
                AccountOrProject = "example-project",
                Region = "example-region",
                TrainingOnCustomerDataDisabled = training,
                PromptAndOutputRetentionBounded = AiProviderFact.Yes,
                DataResidencyCommitted = AiProviderFact.Yes,
                EncryptionInTransit = encryption,
                DeletionControlsAvailable = AiProviderFact.Yes,
                DataProcessingAgreementInPlace = AiProviderFact.Yes,
                OwnerApproved = ownerApproved,
            };
    }
}
