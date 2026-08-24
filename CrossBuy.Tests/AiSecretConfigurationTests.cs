using CrossBuy.BL.Platform;
using CrossBuy.BL.Platform.Ai;
using CrossBuy.Models.Platform;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CrossBuy.Tests
{
    // AI SECRET CONFIGURATION — fail-closed behaviour and a regression guard against committing
    // credentials.
    //
    // CONTEXT, stated plainly because two of my own earlier reports got it wrong in opposite directions:
    // appsettings.json is NOT in source control. It is listed in .gitignore with the comment
    // "holds a real (dev) connection string + JWT Key + Secret -> never commit", and it has never been
    // committed. The tracked template is appsettings.Production.json, which ships only
    // "__SET_ON_SERVER__…" placeholders. The repository's secret hygiene was already correct.
    //
    // What was NOT correct is what happened when the secret was ABSENT: the HTTP client sent
    // `?? ""` — an empty credential — so the payload left the estate and was merely rejected at the far
    // end. These tests pin the fix and stop the credential ever being committed.
    public class AiSecretConfigurationTests
    {
        private const int Company = 1;

        private sealed class FixedContext : IBusinessContextAccessor
        {
            private readonly BusinessContext _ctx;
            public FixedContext(BusinessContext c) => _ctx = c;
            public Task<BusinessContext> GetCurrentAsync(CancellationToken ct = default) => Task.FromResult(_ctx);
            public Task<BusinessContext?> TryGetCurrentAsync(CancellationToken ct = default) => Task.FromResult<BusinessContext?>(_ctx);
        }

        private static IConfiguration Config(params (string, string?)[] pairs)
            {
            var d = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                // Increment 4: a declared, valid hop-1 deployment is now a precondition for egress, so the
                // fixture supplies one. These tests are about the SECRET rule, not the deployment rule.
                ["AiService:DeploymentMode"] = "LocalLoopback",
                ["AiService:BaseUrl"] = "http://localhost:8000",
            };
            foreach (var (k, v) in pairs) d[k] = v;
            return new ConfigurationBuilder().AddInMemoryCollection(d).Build();
        }

        private static AiEgressPolicy Policy(IConfiguration config)
            => new(new FixedContext(new BusinessContext
            {
                CompanyId = Company, EmployeeId = 7, UserId = "u7", Source = BusinessContextSource.Http,
                // Increment 4.3: an approved governance authority, so these tests keep measuring the
                // SECRET rule instead of stopping at the provider gate.
            }), config, NullLogger<AiEgressPolicy>.Instance, AiTestProviderAuthority.Approved());

        private static AiEgressRequest Req() => new()
        {
            Purpose = AiEgressPurpose.CashflowForecast,
            Destination = AiEgressDestinationClass.ApprovedExternalProcessor,
            ProviderScope = AiTestProviderAuthority.TestScope,
            Classification = AiDataClassification.FinancialAggregate,
            DataCompanyId = Company,
            PayloadBytes = 512,
        };

        // ---- §18: a missing secret FAILS CLOSED — no outbound call, not an empty credential ----
        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public async Task A_missing_ai_secret_denies_egress(string? secret)
        {
            var d = await Policy(Config(
                (AiDestinationResolver.DestinationClassKey, "ApprovedExternalProcessor"),
                ("AiService:Secret", secret))).EvaluateAsync(Req());

            Assert.False(d.Allowed);
            Assert.Equal(AiEgressDenyReason.DestinationCredentialMissing, d.Reason);
            Assert.Null(d.Approval);
        }

        // A deployment PLACEHOLDER is not a credential. This is the value the tracked production template
        // ships with, so treating it as real is exactly how an unconfigured production host would start
        // transmitting to an AI endpoint.
        [Theory]
        [InlineData("__SET_ON_SERVER__-rotate-from-the-dev-secret")]
        [InlineData("__set_on_server__-lowercased")]
        [InlineData("prefix-__SET_ON_SERVER__-suffix")]
        public async Task A_placeholder_ai_secret_denies_egress(string secret)
        {
            var d = await Policy(Config(
                (AiDestinationResolver.DestinationClassKey, "ApprovedExternalProcessor"),
                ("AiService:Secret", secret))).EvaluateAsync(Req());

            Assert.False(d.Allowed);
            Assert.Equal(AiEgressDenyReason.DestinationCredentialMissing, d.Reason);
        }

        [Fact]
        public async Task A_real_secret_permits_egress()
        {
            var d = await Policy(Config(
                (AiDestinationResolver.DestinationClassKey, "ApprovedExternalProcessor"),
                ("AiService:Secret", "a-real-looking-configured-value"))).EvaluateAsync(Req());

            Assert.True(d.Allowed, d.Reason?.ToString());
        }

        [Theory]
        [InlineData(null, false)]
        [InlineData("", false)]
        [InlineData("  ", false)]
        [InlineData("__SET_ON_SERVER__", false)]
        [InlineData("real-value", true)]
        public void The_usable_secret_rule_is_explicit(string? secret, bool usable)
            => Assert.Equal(usable, AiEgressPolicy.IsUsableSecret(secret));

        // The denial must not disclose what the expected secret looks like.
        [Fact]
        public async Task The_credential_denial_does_not_leak_the_expected_secret()
        {
            var d = await Policy(Config(
                (AiDestinationResolver.DestinationClassKey, "ApprovedExternalProcessor"),
                ("AiService:Secret", ""))).EvaluateAsync(Req());

            Assert.DoesNotContain("AiService:Secret", d.AppliedPolicy, StringComparison.Ordinal);
            Assert.DoesNotContain("X-AI-Secret", d.AppliedPolicy, StringComparison.Ordinal);
        }

        // ==========================================================================================
        // §19 — REGRESSION GUARD: no usable credential may enter source control.
        // ==========================================================================================
        private static string RepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "CrossBuy.sln"))) dir = dir.Parent;
            Assert.NotNull(dir);
            return dir!.FullName;
        }

        // The TRACKED production template must contain placeholders only. Deliberately narrow — it checks
        // the two keys that are actually credentials, not every string in the file, so ordinary
        // configuration (URLs, timeouts, feature flags) can never trip it.
        [Fact]
        public void The_tracked_production_template_contains_no_usable_credential()
        {
            var path = Path.Combine(RepoRoot(), "CrossBuy", "appsettings.Production.json");
            Assert.True(File.Exists(path), "appsettings.Production.json is the tracked template and must exist.");

            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));

            foreach (var (section, key) in new[] { ("AiService", "Secret"), ("Jwt", "Key") })
            {
                if (!doc.RootElement.TryGetProperty(section, out var s)) continue;
                if (!s.TryGetProperty(key, out var v)) continue;

                var value = v.GetString();
                Assert.False(AiEgressPolicy.IsUsableSecret(value),
                    $"appsettings.Production.json '{section}:{key}' holds a value that would function as a real " +
                    "credential. The tracked template must ship placeholders only.");
            }
        }

        // The local development file must stay OUT of source control. Asserted on .gitignore rather than
        // on the file's contents, because the contents are legitimately a real dev secret — the control
        // is that the file is never committed.
        [Fact]
        public void The_local_appsettings_files_are_excluded_from_source_control()
        {
            var ignore = File.ReadAllText(Path.Combine(RepoRoot(), ".gitignore"));

            foreach (var f in new[] { "appsettings.json", "appsettings.Development.json" })
                Assert.Contains(f, ignore, StringComparison.Ordinal);
        }

        // No credential may be hardcoded in AI source. Checks the AI files specifically, so unrelated
        // code cannot make this test noisy.
        [Fact]
        public void No_ai_source_file_hardcodes_a_credential()
        {
            var root = RepoRoot();
            var files = new List<string> { Path.Combine(root, "CrossBuy", "BL", "AiService.cs") };
            files.AddRange(Directory.GetFiles(Path.Combine(root, "CrossBuy", "BL", "Platform", "Ai"), "*.cs"));

            foreach (var file in files)
            {
                var src = File.ReadAllText(file);
                Assert.DoesNotContain("X-AI-Secret\", \"", src, StringComparison.Ordinal);
                Assert.DoesNotContain("Bearer ", src, StringComparison.Ordinal);
                Assert.DoesNotContain("ApiKey =", src, StringComparison.Ordinal);
                Assert.DoesNotContain("sk-", src, StringComparison.Ordinal);
            }
        }

        // The client must not attach an empty credential. Pinned on the registration source because the
        // defect being guarded was literally the `?? ""` that used to sit there.
        [Fact]
        public void The_http_client_never_attaches_an_empty_credential()
        {
            var program = File.ReadAllText(Path.Combine(RepoRoot(), "CrossBuy", "Program.cs"));

            Assert.DoesNotContain("\"X-AI-Secret\", cfg[\"AiService:Secret\"] ?? \"\"", program, StringComparison.Ordinal);
            Assert.Contains("IsUsableSecret(secret)", program, StringComparison.Ordinal);
        }
    }
}

