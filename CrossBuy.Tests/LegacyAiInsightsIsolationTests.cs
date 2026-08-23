using System.Text.Json;
using CrossBuy.BL;
using CrossBuy.BL.Platform;
using CrossBuy.BL.Platform.Ai;
using CrossBuy.Models.Context.Accounting;
using CrossBuy.Models.Context.Inventory;
using CrossBuy.Models.Platform;
using Microsoft.Extensions.Configuration;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CrossBuy.Tests
{
    // LEGACY AI PATH — company isolation for AiInsightsService.
    //
    // This is the service that feeds the external Python AI service. Its three methods take a companyId,
    // and until this increment AccountingController passed the literal 1 to all three. Six of the tables
    // they read carry NO Stage-1 global company filter, so for those the method argument was the ONLY
    // company control — and a seventh, ItemWarehouseSettings, had no company predicate at all AND has no
    // company column to write one against.
    //
    // Every test below drives the service DIRECTLY with a company argument, under a DbContext scoped to a
    // DIFFERENT company. That combination is the exploit shape: it proves the isolation of the queries
    // themselves rather than of the controller that calls them, which is where the defect actually lived.
    public class LegacyAiInsightsIsolationTests
    {
        private const int CompanyOne = 1;
        private const int CompanyOther = 65;

        // Captures everything that would leave .NET for the Python service. Nothing is sent anywhere.
        //
        // Increment 3 changed IAiService to REQUIRE an AiEgressApproval — the token only IAiEgressPolicy
        // can mint — so this double takes one too. That signature change is the hardening: a fake that
        // could still be called without an approval would no longer represent the real client.
        private sealed class CapturingAiService : IAiService
        {
            public List<(string Path, string Json)> Calls { get; } = new();

            public Task<AiProxyResult> EchoAsync(AiEgressApproval approval, string message, string tier, CancellationToken ct = default)
                => PostAsync(approval, "/diag/echo", new { message, tier }, ct);

            public Task<AiProxyResult> PostAsync(AiEgressApproval approval, string path, object payload, CancellationToken ct = default)
            {
                Assert.NotNull(approval);
                Calls.Add((path, JsonSerializer.Serialize(payload)));
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

        // Seeds the SAME logical records in two companies, with ids chosen so a cross-company read is
        // unambiguous: company 1 uses the 100-range, company 65 the 200-range.
        private static async Task SeedTwoCompaniesAsync(CrossBuy.Models.Context.CrossDbContext db)
        {
            db.Accounts.AddRange(
                new Account { ID = 101, CompanyID = CompanyOne, Code = "110101", Name = "Cash C1", IsPostable = true },
                new Account { ID = 201, CompanyID = CompanyOther, Code = "110101", Name = "Cash C65", IsPostable = true });

            db.Vendors.AddRange(
                new Vendor { ID = 102, CompanyID = CompanyOne, Name = "Vendor C1" },
                new Vendor { ID = 202, CompanyID = CompanyOther, Name = "Vendor C65" });

            db.Customers.AddRange(
                new Customer { ID = 103, CompanyID = CompanyOne, Name = "Customer C1" },
                new Customer { ID = 203, CompanyID = CompanyOther, Name = "Customer C65" });

            db.Receipts.AddRange(
                new Receipt { ID = 104, CompanyID = CompanyOne, CustomerId = 103, Amount = 500m, Status = "Posted" },
                new Receipt { ID = 204, CompanyID = CompanyOther, CustomerId = 203, Amount = 900m, Status = "Posted" });

            db.Payments.AddRange(
                new Payment { ID = 105, CompanyID = CompanyOne, VendorId = 102, Amount = 700m, Status = "Posted" },
                new Payment { ID = 205, CompanyID = CompanyOther, VendorId = 202, Amount = 300m, Status = "Posted" });

            db.Items.AddRange(
                new Item { ID = 106, CompanyID = CompanyOne, ItemCode = "ITEM-C1", Name = "Item C1", IsActive = true },
                new Item { ID = 206, CompanyID = CompanyOther, ItemCode = "ITEM-C65", Name = "Item C65", IsActive = true });

            db.StockBalances.AddRange(
                new StockBalance { ID = 107, CompanyID = CompanyOne, ItemId = 106, WarehouseId = 1, QtyOnHand = 11m, TotalValue = 1100m },
                new StockBalance { ID = 207, CompanyID = CompanyOther, ItemId = 206, WarehouseId = 2, QtyOnHand = 22m, TotalValue = 2200m });

            db.StockMovements.AddRange(
                new StockMovement { ID = 108, CompanyID = CompanyOne, ItemId = 106, WarehouseId = 1, Direction = -1, QtyBase = 5m, MovementDate = DateTime.Today.AddDays(-3) },
                new StockMovement { ID = 208, CompanyID = CompanyOther, ItemId = 206, WarehouseId = 2, Direction = -1, QtyBase = 6m, MovementDate = DateTime.Today.AddDays(-3) });

            db.ItemWarehouseSettings.AddRange(
                new ItemWarehouseSetting { ID = 109, ItemId = 106, WarehouseId = 1, ReorderPoint = 111m, MaxQty = 999m },
                new ItemWarehouseSetting { ID = 209, ItemId = 206, WarehouseId = 2, ReorderPoint = 222m, MaxQty = 888m });

            await db.SaveChangesAsync();
        }

        // `actingCompany` is the AUTHENTICATED caller's company — the one the egress policy trusts.
        // Destination defaults to Internal so these query-isolation tests can still observe the payload;
        // the classification/destination matrix has its own dedicated suite.
        private static (AiInsightsService Service, CapturingAiService Ai) Build(
            CrossBuy.Models.Context.CrossDbContext db, int actingCompany, string destination = "Internal")
        {
            var ai = new CapturingAiService();
            var config = new Microsoft.Extensions.Configuration.ConfigurationBuilder()
                .AddInMemoryCollection(new[]
                {
                    new KeyValuePair<string, string?>(AiDestinationResolver.DestinationClassKey, destination), new KeyValuePair<string, string?>("AiService:Secret", "test-configured-secret"), new KeyValuePair<string, string?>("AiService:BaseUrl", "http://localhost:8000"), new KeyValuePair<string, string?>("AiService:DeploymentMode", "LocalLoopback"),
                })
                .Build();

            var user = new BusinessContext
            {
                CompanyId = actingCompany, EmployeeId = 7, UserId = "u7",
                Source = BusinessContextSource.Http,
            };

            // Increment 4.3: an approved governance authority, so these tests keep measuring COMPANY
            // ISOLATION — the thing they exist for — rather than stopping at the new provider gate.
            var authority = AiTestProviderAuthority.Approved();

            var policy = new AiEgressPolicy(new FixedContext(user), config,
                Microsoft.Extensions.Logging.Abstractions.NullLogger<AiEgressPolicy>.Instance, authority);

            return (new AiInsightsService(db, ai, policy, config, authority), ai);
        }

        // ==========================================================================================
        // §10 — QUERY-LEVEL isolation. Each method must return ONLY the company it was given.
        // ==========================================================================================

        // The exploit shape, stated directly: the DbContext is scoped to company 65 (as it would be for a
        // signed-in company-65 user), and the service is asked for COMPANY 1 — exactly what the controller
        // did with its hardcoded constant. No company-1 value may be read or egress.
        [Fact]
        public async Task A_company_65_scope_asking_for_company_1_inventory_gets_no_company_1_data()
        {
            using var host = new PlatformTestHost(CompanyOther);
            using (var seed = host.AllCompanies()) await SeedTwoCompaniesAsync(seed);

            // Increment 3 strengthened this outcome. The egress policy now refuses BEFORE any data is
            // gathered, because the authenticated caller is company 65 while the request names company 1.
            // "Zero outbound calls" is strictly stronger than the previous assertion, which could only
            // say the payload happened to contain no company-1 markers.
            var (svc, ai) = Build(host.Db, actingCompany: CompanyOther);
            var result = await svc.AnalyzeInventoryAsync(CompanyOne, 90);

            Assert.Empty(ai.Calls);
            Assert.Equal(403, result.Status);
            Assert.Contains(nameof(CrossBuy.Models.Platform.AiEgressDenyReason.CompanyMismatch),
                result.Json, StringComparison.Ordinal);
        }

        [Fact]
        public async Task A_company_65_scope_asking_for_company_1_cashflow_gets_no_company_1_data()
        {
            using var host = new PlatformTestHost(CompanyOther);
            using (var seed = host.AllCompanies()) await SeedTwoCompaniesAsync(seed);

            var (svc, ai) = Build(host.Db, actingCompany: CompanyOther);
            var result = await svc.ForecastCashflowAsync(CompanyOne, 90);

            Assert.Empty(ai.Calls);
            Assert.Equal(403, result.Status);
        }

        // The positive direction: the correct company still gets its own data. A fix that simply returned
        // nothing for everyone would pass the isolation tests and break the product.
        [Fact]
        public async Task A_company_65_scope_asking_for_company_65_inventory_gets_its_own_data()
        {
            using var host = new PlatformTestHost(CompanyOther);
            using (var seed = host.AllCompanies()) await SeedTwoCompaniesAsync(seed);

            var (svc, ai) = Build(host.Db, actingCompany: CompanyOther);
            await svc.AnalyzeInventoryAsync(CompanyOther, 90);

            var sent = Assert.Single(ai.Calls).Json;
            Assert.Contains("ITEM-C65", sent, StringComparison.Ordinal);
            Assert.Contains("222", sent, StringComparison.Ordinal);      // its own reorder point
            AssertNoCompanyOneMarkers(sent);
        }

        [Fact]
        public async Task A_company_one_scope_asking_for_company_one_still_works()
        {
            using var host = new PlatformTestHost(CompanyOne);
            using (var seed = host.AllCompanies()) await SeedTwoCompaniesAsync(seed);

            var (svc, ai) = Build(host.Db, actingCompany: CompanyOne);
            await svc.AnalyzeInventoryAsync(CompanyOne, 90);

            var sent = Assert.Single(ai.Calls).Json;
            Assert.Contains("ITEM-C1", sent, StringComparison.Ordinal);
            Assert.Contains("111", sent, StringComparison.Ordinal);      // its own reorder point
            Assert.DoesNotContain("ITEM-C65", sent, StringComparison.Ordinal);
        }

        // §9-E — overlapping ids must not cause confusion. ItemWarehouseSetting has NO company column, so
        // it can only be scoped THROUGH its item; this is the test that proves that indirection works.
        [Fact]
        public async Task Item_warehouse_settings_are_scoped_through_their_item_not_read_across_companies()
        {
            using var host = new PlatformTestHost(CompanyOther);
            using (var seed = host.AllCompanies()) await SeedTwoCompaniesAsync(seed);

            var (svc, ai) = Build(host.Db, actingCompany: CompanyOther);
            await svc.AnalyzeInventoryAsync(CompanyOther, 90);

            var sent = Assert.Single(ai.Calls).Json;
            Assert.Contains("222", sent, StringComparison.Ordinal);      // company 65's reorder point
            Assert.DoesNotContain("111", sent, StringComparison.Ordinal); // company 1's must be absent
            Assert.DoesNotContain("999", sent, StringComparison.Ordinal); // company 1's max qty
        }

        [Fact]
        public async Task The_journal_anomaly_scan_is_company_scoped()
        {
            using var host = new PlatformTestHost(CompanyOther);
            using (var seed = host.AllCompanies())
            {
                await SeedTwoCompaniesAsync(seed);
                seed.JournalEntries.AddRange(
                    new JournalEntry { ID = 110, CompanyID = CompanyOne, EntryNo = "JE-C1-0001", Status = "Posted", Description = "company one entry", EntryDate = DateTime.Today },
                    new JournalEntry { ID = 210, CompanyID = CompanyOther, EntryNo = "JE-C65-0001", Status = "Posted", Description = "company sixtyfive entry", EntryDate = DateTime.Today });
                await seed.SaveChangesAsync();
            }

            var (svc, ai) = Build(host.Db, actingCompany: CompanyOther);
            var result = await svc.ScanJournalAnomaliesAsync(CompanyOne);

            Assert.Empty(ai.Calls);
            Assert.Equal(403, result.Status);
        }

        // §22.4 — a company must never receive another's rows, whichever direction it is asked in.
        [Theory]
        [InlineData(CompanyOne, CompanyOther)]
        [InlineData(CompanyOther, CompanyOne)]
        public async Task No_scope_can_obtain_another_companys_insight_data(int scopeCompany, int requestedCompany)
        {
            using var host = new PlatformTestHost(scopeCompany);
            using (var seed = host.AllCompanies()) await SeedTwoCompaniesAsync(seed);

            var (svc, ai) = Build(host.Db, actingCompany: scopeCompany);
            await svc.AnalyzeInventoryAsync(requestedCompany, 90);
            await svc.ForecastCashflowAsync(requestedCompany, 90);

            string other = requestedCompany == CompanyOne ? "ITEM-C1" : "ITEM-C65";
            if (scopeCompany != requestedCompany)
                foreach (var call in ai.Calls)
                    Assert.DoesNotContain(other, call.Json, StringComparison.Ordinal);
        }

        // ==========================================================================================
        // §4 — the service contract must not permit a silent company substitution.
        // ==========================================================================================
        [Theory]
        [InlineData(0)]
        [InlineData(-1)]
        public async Task An_invalid_company_is_refused_and_nothing_is_sent(int companyId)
        {
            using var host = new PlatformTestHost(CompanyOther);
            using (var seed = host.AllCompanies()) await SeedTwoCompaniesAsync(seed);

            var (svc, ai) = Build(host.Db, actingCompany: CompanyOther);

            await Assert.ThrowsAnyAsync<ArgumentException>(() => svc.AnalyzeInventoryAsync(companyId, 90));
            await Assert.ThrowsAnyAsync<ArgumentException>(() => svc.ForecastCashflowAsync(companyId, 90));
            await Assert.ThrowsAnyAsync<ArgumentException>(() => svc.ScanJournalAnomaliesAsync(companyId));

            // §8 — ZERO EGRESS on refusal. This is the assertion that matters most on this path.
            Assert.Empty(ai.Calls);
        }

        // Company-1 markers, in one place so every isolation test checks the same set. Values are chosen
        // to be unique to company 1's seed rows.
        private static void AssertNoCompanyOneMarkers(string json)
        {
            foreach (var marker in new[] { "ITEM-C1", "Item C1", "Vendor C1", "Customer C1", "Cash C1" })
                Assert.DoesNotContain(marker, json, StringComparison.Ordinal);
        }
    }
}


