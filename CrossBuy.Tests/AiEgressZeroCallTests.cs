using CrossBuy.BL;
using CrossBuy.BL.Platform;
using CrossBuy.BL.Platform.Ai;
using CrossBuy.Models.Context.Accounting;
using CrossBuy.Models.Platform;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CrossBuy.Tests
{
    // AI Foundation Increment 3 — E9/E10: a DENY must produce ZERO outbound calls, and an ALLOW must
    // produce exactly the expected one.
    //
    // "Zero network calls" is the property that actually matters on an egress boundary: a policy that
    // denies after the request has been sent has denied nothing.
    public class AiEgressZeroCallTests
    {
        private const int CompanyA = 1;
        private const int CompanyB = 65;

        // Counts calls and records the approval each one carried. Nothing is sent anywhere.
        private sealed class CountingAiService : IAiService
        {
            public List<(string Path, AiEgressApproval Approval)> Calls { get; } = new();

            public Task<AiProxyResult> EchoAsync(AiEgressApproval approval, string message, string tier, CancellationToken ct = default)
                => PostAsync(approval, "/diag/echo", new { message, tier }, ct);

            public Task<AiProxyResult> PostAsync(AiEgressApproval approval, string path, object payload, CancellationToken ct = default)
            {
                Assert.NotNull(approval);                 // the type system should already guarantee this
                Calls.Add((path, approval));
                return Task.FromResult(new AiProxyResult(200, "{}"));
            }
        }

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

        private static IConfiguration Config(params (string, string)[] pairs)
            => new ConfigurationBuilder()
                .AddInMemoryCollection(pairs.Select(p => new KeyValuePair<string, string?>(p.Item1, p.Item2)).Concat(new[] { new KeyValuePair<string, string?>("AiService:Secret", "test-configured-secret"), new KeyValuePair<string, string?>("AiService:BaseUrl", "http://localhost:8000"), new KeyValuePair<string, string?>("AiService:DeploymentMode", "LocalLoopback") }).GroupBy(k => k.Key).Select(g => g.First()))
                .Build();

        // The destination that permits financial aggregates, so a denial in these tests is attributable
        // to the thing under test rather than to the default-refusing destination.
        private static IConfiguration ApprovedDestination(params (string, string)[] extra)
            => Config(new[]
                {
                    (AiDestinationResolver.DestinationClassKey, "ApprovedExternalProcessor"),
                    // Increment 4.9 — AiInsightsService now states WHICH account context it is asking
                    // about, and that scope comes from configuration. Without these the request carries
                    // an incomplete scope and every case here would deny for that reason instead of the
                    // one it exists to test.
                    ("OpenAi:OrganizationId", AiTestProviderAuthority.TestOrganizationId),
                    ("OpenAi:ProjectId", AiTestProviderAuthority.TestProjectId),
                    ("OpenAi:Environment", "Development"),
                }
                .Concat(extra).ToArray());

        private static (AiInsightsService Service, CountingAiService Ai) Build(
            PlatformTestHost host, BusinessContext? user, IConfiguration config)
        {
            var ai = new CountingAiService();
            // Increment 4.3: an APPROVED governance authority, for the same reason ApprovedDestination
            // exists above — a denial in these tests must be attributable to the thing under test, not to
            // the provider gate that now (correctly) refuses everything by default.
            // Increment 4.9 — approved for the OpenAI-provider-id scope, because that is the scope
            // AiInsightsService derives from OpenAiOptions. The generic fixture scope carries a different
            // provider id and correctly would not cover it.
            var authority = AiTestProviderAuthority.ApprovedFor(AiTestProviderAuthority.OpenAiTestScope);
            var policy = new AiEgressPolicy(new FixedContext(user), config, NullLogger<AiEgressPolicy>.Instance, authority);
            return (new AiInsightsService(host.Db, ai, policy, config, authority), ai);
        }

        private static async Task SeedAsync(CrossBuy.Models.Context.CrossDbContext db, int company)
        {
            db.Accounts.Add(new Account { ID = 900 + company, CompanyID = company, Code = "110101", Name = "Cash", IsPostable = true });
            db.Customers.Add(new Customer { ID = 910 + company, CompanyID = company, Name = "Cust" });
            await db.SaveChangesAsync();
        }

        // ==========================================================================================
        // E10 — ALLOW produces exactly one call, carrying the approval
        // ==========================================================================================
        [Fact]
        public async Task E10_an_allowed_cashflow_forecast_makes_exactly_one_approved_call()
        {
            using var host = new PlatformTestHost(CompanyA);
            await SeedAsync(host.Db, CompanyA);

            var (svc, ai) = Build(host, User(CompanyA), ApprovedDestination());
            await svc.ForecastCashflowAsync(CompanyA, 90);

            var call = Assert.Single(ai.Calls);
            Assert.Equal("/forecast/cashflow", call.Path);
            Assert.Equal(AiEgressPurpose.CashflowForecast, call.Approval.Purpose);
            Assert.Equal(AiDataClassification.FinancialAggregate, call.Approval.Classification);
            Assert.Equal(CompanyA, call.Approval.CompanyId);
        }

        [Fact]
        public async Task An_allowed_inventory_analysis_makes_exactly_one_approved_call()
        {
            using var host = new PlatformTestHost(CompanyA);
            await SeedAsync(host.Db, CompanyA);

            var (svc, ai) = Build(host, User(CompanyA), ApprovedDestination());
            await svc.AnalyzeInventoryAsync(CompanyA, 90);

            var call = Assert.Single(ai.Calls);
            Assert.Equal("/inventory/analyze", call.Path);
            Assert.Equal(AiEgressPurpose.InventoryAnalysis, call.Approval.Purpose);
        }

        // ==========================================================================================
        // E9 — every denial path produces ZERO calls
        // ==========================================================================================

        // The destination is unapproved (the DEFAULT, unconfigured state). Nothing may leave.
        [Fact]
        public async Task E9_an_unapproved_destination_produces_zero_calls()
        {
            using var host = new PlatformTestHost(CompanyA);
            await SeedAsync(host.Db, CompanyA);

            var (svc, ai) = Build(host, User(CompanyA), Config());   // nothing configured
            var r1 = await svc.ForecastCashflowAsync(CompanyA, 90);
            var r2 = await svc.AnalyzeInventoryAsync(CompanyA, 90);
            var r3 = await svc.ScanJournalAnomaliesAsync(CompanyA);

            Assert.Empty(ai.Calls);
            foreach (var r in new[] { r1, r2, r3 })
            {
                Assert.Equal(403, r.Status);
                Assert.Contains("ai_egress_denied", r.Json, StringComparison.Ordinal);
            }
        }

        // THE JOURNAL ANOMALY DECISION, proven rather than described.
        //
        // Its payload carries JournalEntry.Description — user-authored free text. Under the matrix, free
        // text is refused at ANY external destination, including an approved one. So this feature FAILS
        // CLOSED at the external processor, and its classification was NOT softened to keep it working.
        [Fact]
        public async Task The_journal_anomaly_scan_is_refused_at_an_external_destination_because_it_carries_free_text()
        {
            using var host = new PlatformTestHost(CompanyA);
            await SeedAsync(host.Db, CompanyA);

            var (svc, ai) = Build(host, User(CompanyA), ApprovedDestination());
            var result = await svc.ScanJournalAnomaliesAsync(CompanyA);

            Assert.Empty(ai.Calls);
            Assert.Equal(403, result.Status);
            Assert.Contains(AiEgressDenyReason.ClassificationNotPermittedAtDestination.ToString(),
                result.Json, StringComparison.Ordinal);
        }

        // ...but the same payload IS permitted to a genuinely internal destination, which is what makes
        // the classification a real distinction rather than a blanket ban.
        [Fact]
        public async Task The_journal_anomaly_scan_is_permitted_to_a_genuinely_internal_destination()
        {
            using var host = new PlatformTestHost(CompanyA);
            await SeedAsync(host.Db, CompanyA);

            var (svc, ai) = Build(host, User(CompanyA),
                Config((AiDestinationResolver.DestinationClassKey, "Internal")));
            await svc.ScanJournalAnomaliesAsync(CompanyA);

            var call = Assert.Single(ai.Calls);
            Assert.Equal(AiDataClassification.FreeTextBusinessContent, call.Approval.Classification);
        }

        // Cross-company: the service is asked for company A while the caller is company B.
        [Fact]
        public async Task E9_a_company_mismatch_produces_zero_calls()
        {
            using var host = new PlatformTestHost(CompanyB);
            await SeedAsync(host.Db, CompanyB);

            var (svc, ai) = Build(host, User(CompanyB), ApprovedDestination());
            var result = await svc.ForecastCashflowAsync(CompanyA, 90);      // asking for someone else's

            Assert.Empty(ai.Calls);
            Assert.Equal(403, result.Status);
            Assert.Contains(AiEgressDenyReason.CompanyMismatch.ToString(), result.Json, StringComparison.Ordinal);
        }

        [Fact]
        public async Task E9_an_unresolved_company_produces_zero_calls()
        {
            using var host = new PlatformTestHost(CompanyA);
            await SeedAsync(host.Db, CompanyA);

            var (svc, ai) = Build(host, user: null, ApprovedDestination());
            var result = await svc.ForecastCashflowAsync(CompanyA, 90);

            Assert.Empty(ai.Calls);
            Assert.Equal(403, result.Status);
        }

        // An invalid company never even reaches the policy — the service's own guard throws first, and
        // still nothing is sent.
        [Theory]
        [InlineData(0)]
        [InlineData(-1)]
        public async Task E9_an_invalid_company_throws_and_produces_zero_calls(int companyId)
        {
            using var host = new PlatformTestHost(CompanyA);
            var (svc, ai) = Build(host, User(CompanyA), ApprovedDestination());

            await Assert.ThrowsAnyAsync<ArgumentException>(() => svc.ForecastCashflowAsync(companyId, 90));
            Assert.Empty(ai.Calls);
        }

        [Fact]
        public async Task E9_a_disabled_policy_produces_zero_calls()
        {
            using var host = new PlatformTestHost(CompanyA);
            await SeedAsync(host.Db, CompanyA);

            var (svc, ai) = Build(host, User(CompanyA), ApprovedDestination(("AiService:EgressEnabled", "false")));
            await svc.ForecastCashflowAsync(CompanyA, 90);

            Assert.Empty(ai.Calls);
        }

        // The AiService itself refuses a payload larger than its approval describes. Without this, the
        // size limit would be advisory: a caller could obtain a small approval and send anything.
        [Fact]
        public async Task A_payload_larger_than_its_approval_is_refused_by_the_client()
        {
            var approval = await MintApprovalAsync(bytes: 8);
            var client = new AiService(new HttpClient { BaseAddress = new Uri("http://localhost:1") });

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                client.PostAsync(approval, "/anything", new { padding = new string('x', 4096) }));
        }

        private static async Task<AiEgressApproval> MintApprovalAsync(int bytes)
        {
            // Config() supplies the secret and loopback base URL a real deployment supplies — Increment 3.1
            // made both mandatory, so an empty configuration would now be denied for the wrong reason and
            // this test would stop measuring the size rule it exists for.
            var policy = new AiEgressPolicy(
                new FixedContext(User(CompanyA)),
                Config(),
                NullLogger<AiEgressPolicy>.Instance,
                // ...and Increment 4.3 made an approved provider mandatory for the external class, so the
                // fixture supplies that too. Same principle: supply what a real approved deployment
                // supplies rather than relax the rule to keep an old test green.
                AiTestProviderAuthority.Approved());

            var d = await policy.EvaluateAsync(new AiEgressRequest
            {
                Purpose = AiEgressPurpose.CashflowForecast,
                Destination = AiEgressDestinationClass.Internal,
                Classification = AiDataClassification.FinancialAggregate,
                DataCompanyId = CompanyA,
                PayloadBytes = bytes,
            });

            Assert.True(d.Allowed);
            return d.Approval!;
        }
    }
}


