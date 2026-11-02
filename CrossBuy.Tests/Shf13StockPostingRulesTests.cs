using System.Text.RegularExpressions;
using CrossBuy.BL;
using Xunit;

namespace CrossBuy.Tests
{
    // ============================================================================================
    // SHF-13 — THE STOCK POSTING RULES, owner-authorized code-only correction (2026-11-02).
    //
    // NOT ONE TEST IN THIS FILE TOUCHES A DATABASE OF ANY KIND. The rules under test are pure
    // functions on StockService, and the two source checks read files from the repository.
    //
    // WHAT WAS WRONG, measured on CrossBuyDB2: the counter-account switch had no arm for
    // SalesInvoiceReversal, SalesInvoiceEdit or the dev seed's "Opening", so all three fell to the
    // category's adjustment account — 520103 Depreciation Expense on all 28 mapped categories. POS
    // reversals credited depreciation instead of reversing cost of goods (4,097.30), invoice edits
    // likewise (444.00), and dev opening stock credited it 69,550.00.
    // ============================================================================================
    public class Shf13StockPostingRulesTests
    {
        private const int Inv = 7, Counter = 25;

        [Theory]
        [InlineData("SalesInvoiceReversal")]
        [InlineData("SalesInvoiceEdit")]
        [InlineData("SalesReturn")]
        [InlineData("SalesInvoice")]
        [InlineData("Issue")]
        public void Sales_movements_book_against_cost_of_goods(string sourceType)
            => Assert.Equal(StockCounterSide.Cogs, StockService.CounterSideFor(sourceType));

        [Fact]
        public void Dev_opening_stock_books_against_opening_balance_equity_not_the_adjustment_account()
            => Assert.Equal(StockCounterSide.OpeningEquity, StockService.CounterSideFor("Opening"));

        [Theory]
        [InlineData("Receipt")]
        [InlineData("PurchaseInvoice")]
        [InlineData("PurchaseReturn")]
        public void Purchase_movements_book_against_grni(string sourceType)
            => Assert.Equal(StockCounterSide.Grni, StockService.CounterSideFor(sourceType));

        [Theory]
        [InlineData("Adjustment")]
        [InlineData("Transfer")]
        [InlineData("HospitalDispense")]   // carries its own CounterAccountOverride; the side only matters without one
        [InlineData(null)]
        public void Counts_and_unnamed_types_keep_the_category_adjustment_account(string? sourceType)
            => Assert.Equal(StockCounterSide.Adjustment, StockService.CounterSideFor(sourceType));

        // DEBIT/CREDIT DIRECTION, per transaction type, checked against the owner's mapping.
        [Theory]
        [InlineData("Opening", 1)]                // Dr Inventory / Cr 3301
        [InlineData("SalesInvoiceReversal", 1)]   // Dr Inventory / Cr COGS — undoes the sale's Dr COGS / Cr Inventory
        [InlineData("SalesInvoiceEdit", 1)]       // Dr Inventory / Cr COGS — the edited-away quantity comes back
        [InlineData("SalesReturn", 1)]
        [InlineData("Receipt", 1)]                // Dr Inventory / Cr GRNI
        public void Stock_coming_in_debits_inventory_and_credits_the_counter(string sourceType, short direction)
        {
            Assert.NotEqual(StockCounterSide.Adjustment, StockService.CounterSideFor(sourceType));
            Assert.Equal((Inv, Counter), StockService.GlSidesFor(direction, Inv, Counter));
        }

        [Theory]
        [InlineData("SalesInvoice", StockCounterSide.Cogs)]    // Dr COGS / Cr Inventory
        [InlineData("Issue", StockCounterSide.Cogs)]
        [InlineData("PurchaseReturn", StockCounterSide.Grni)]  // Dr GRNI / Cr Inventory
        [InlineData("Adjustment", StockCounterSide.Adjustment)] // count shortage: Dr adjustment / Cr Inventory
        public void Stock_going_out_debits_the_counter_and_credits_inventory(string sourceType, StockCounterSide side)
        {
            Assert.Equal(side, StockService.CounterSideFor(sourceType));
            Assert.Equal((Counter, Inv), StockService.GlSidesFor(-1, Inv, Counter));
        }

        // D-41a — a FEFO split must not lose the caller's counter account or project.
        [Fact]
        public void A_fefo_share_carries_the_callers_counter_account_and_project()
        {
            var req = new MovementRequest
            {
                Date = new DateTime(2026, 11, 2), ItemId = 5, WarehouseId = 3, Direction = -1, Qty = 10,
                SourceType = "HospitalDispense", SourceId = 41, SourceLineId = 42, BinLocationId = 9, PostToGl = true,
                CounterAccountOverride = 25, ProjectId = 77, Notes = "dispense"
            };

            var share = StockService.FefoShare(req, itemId: 5, baseUoMId: 2, qtyBase: 4m, batchNo: "B-1");

            Assert.Equal(25, share.CounterAccountOverride);
            Assert.Equal(77, share.ProjectId);
            Assert.Equal((short)-1, share.Direction);
            Assert.Equal(4m, share.Qty);
            Assert.Equal(2, share.UoMId);
            Assert.Equal("B-1", share.BatchNo);
            Assert.Equal(("HospitalDispense", 41, 42), (share.SourceType, share.SourceId, share.SourceLineId));
            Assert.Equal((3, 9, true), (share.WarehouseId, share.BinLocationId, share.PostToGl));
            Assert.Equal("dispense [FEFO B-1]", share.Notes);
        }

        [Fact]
        public void A_fefo_share_without_an_override_still_uses_the_category_default()
        {
            var share = StockService.FefoShare(new MovementRequest { SourceType = "Issue" }, 5, 2, 1m, "B-2");
            Assert.Null(share.CounterAccountOverride);
            Assert.Null(share.ProjectId);
        }

        [Fact]
        public void The_fefo_split_builds_its_shares_through_FefoShare()
            => Assert.Contains("var creq = FefoShare(req, hdr.ID, hdr.BaseUoMId, a.qtyBase, a.batchNo);", Source("CrossBuy", "BL", "StockService.cs"));

        // D-41b — the default location rule is preserved exactly: rack, else section, never a non-positive id.
        [Theory]
        [InlineData(12, 4, 12)]
        [InlineData(null, 4, 4)]
        [InlineData(null, null, null)]
        [InlineData(0, null, null)]
        [InlineData(null, -1, null)]
        public void The_default_bin_is_the_rack_then_the_section(int? rack, int? section, int? expected)
            => Assert.Equal(expected, StockService.DefaultBinFor(rack, section));

        // The dev count seed must not choose 520103 Depreciation Expense as the adjustment account. The extractor is
        // tested against the OLD line too, so a regex that stopped matching cannot pass this vacuously.
        [Fact]
        public void The_count_seed_does_not_book_adjustments_to_depreciation()
        {
            const string oldLine = "int accAdj = await _db.Accounts.Where(a => a.CompanyID == company && a.Code == \"520103\").Select(a => a.ID).FirstOrDefaultAsync();";
            Assert.Equal("520103", SeedAdjustmentCode(oldLine));

            var code = SeedAdjustmentCode(Source("CrossBuy", "Controllers", "Api", "DevSeedController.cs"));
            Assert.NotNull(code);
            Assert.NotEqual("520103", code);
        }

        private static string? SeedAdjustmentCode(string source)
        {
            var m = Regex.Match(source, "int accAdj = await _db\\.Accounts\\.Where\\([^;]*a\\.Code == \"(\\d+)\"");
            return m.Success ? m.Groups[1].Value : null;
        }

        private static string Source(params string[] parts)
            => File.ReadAllText(Path.Combine(new[] { RepoRoot() }.Concat(parts).ToArray()));

        private static string RepoRoot()
        {
            var fromEnvironment = Environment.GetEnvironmentVariable("CROSSBUY_REPO_ROOT");
            if (!string.IsNullOrWhiteSpace(fromEnvironment) && File.Exists(Path.Combine(fromEnvironment, "CrossBuy.sln")))
                return fromEnvironment;

            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "CrossBuy.sln")))
                dir = dir.Parent;
            return dir?.FullName ?? throw new InvalidOperationException("CrossBuy.sln not found above " + AppContext.BaseDirectory);
        }
    }
}
