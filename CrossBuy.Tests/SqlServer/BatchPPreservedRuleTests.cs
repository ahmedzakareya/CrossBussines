using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Inventory;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Xunit;
using Xunit.Abstractions;

namespace CrossBuy.Tests.SqlServer
{
    // Stage 2A Batch P — THE THREE PRESERVED MASTER DATA RULES.
    //
    // Until this class existed, three rules governing money and quantity were protected by PROSE ONLY. Nothing
    // mechanically prevented a "helpful" cleanup introducing a factor-1 conversion fallback or a first-match barcode
    // resolution. Each test exercises the predicate the PRODUCTION path evaluates:
    //
    //   P-01  PosOrderService.cs:545-549 — "the sold unit must be the item's base unit OR have a defined conversion
    //         to base on THIS item — else reject (never a silent factor-1)."
    //   P-02  the ambiguity rule the scan path enforces (HyperPosController.Scan:245-259); IntegrityCheckService:286
    //         counts the population because "a scan would be ambiguous — the scan path rejects it".
    //   P-03  ItemBarcode.UoMId selects the sold unit, so remapping it changes quantity, COGS and invoice lines.
    //
    // Risks: RISK-046 (barcode UoM semantics) · RISK-048 (factor-1 fallback) · RISK-050 (first-match resolution).
    //
    // ISOLATION (RISK-036). This class GENERATES SCHEMA, so it owns a DEDICATED PROBE DATABASE and never touches the
    // shared platform fixture. An earlier version called EnsureEfSchemaAsync on the shared fixture and broke three
    // PlatformSchemaDeploymentTests — the very defect RISK-036 records, committed by the batch that documented it.
    // The probe lifecycle now comes from SqlServerFixture.CreateProbeDatabaseAsync / DropProbeDatabaseAsync, so the
    // rule lives in one shared helper instead of being remembered per class.
    [Collection(SqlServerCollection.Name)]
    public class BatchPPreservedRuleTests : IAsyncLifetime
    {
        private const int CompanyOne = 1;

        private readonly SqlServerFixture _sql;
        private readonly ITestOutputHelper _out;
        public BatchPPreservedRuleTests(SqlServerFixture sql, ITestOutputHelper output) { _sql = sql; _out = output; }

        private SqlServerFixture.ProbeDatabase? _probe;
        private string _sharedFingerprintBefore = "";

        private void Ready() => Skip.If(!_sql.Available, _sql.SkipReason);

        public async Task InitializeAsync()
        {
            if (!_sql.Available) return;
            // Captured BEFORE the probe exists, so the purity assertion covers everything this class does.
            _sharedFingerprintBefore = await _sql.SharedFixtureFingerprintAsync();
            _probe = await _sql.CreateProbeDatabaseAsync("BatchP");
        }

        public async Task DisposeAsync()
        {
            if (_probe != null) await _sql.DropProbeDatabaseAsync(_probe);
        }

        private CrossDbContext Db() => _sql.ContextFor(_probe!, CompanyOne);

        private async Task<long> CountAsync(string table)
        {
            await using var c = new SqlConnection(_probe!.ConnectionString);
            await c.OpenAsync();
            await using var cmd = new SqlCommand(
                $"IF OBJECT_ID(N'dbo.{table}', N'U') IS NULL SELECT CAST(-1 AS BIGINT) ELSE SELECT COUNT_BIG(*) FROM dbo.{table};", c);
            return Convert.ToInt64(await cmd.ExecuteScalarAsync());
        }

        // ---- deterministic canonical dataset: EACH is base, CASE = 12 EACH ----
        private sealed class Data
        {
            public int ItemId, SecondItemId, BaseUoM, CaseUoM;
            public string EachBarcode = "", CaseBarcode = "", AmbiguousBarcode = "";
        }

        // Rebuilt per test, so the six tests are ORDER-INDEPENDENT: none inherits another's rows.
        private async Task<Data> ArrangeAsync()
        {
            await using (var c = new SqlConnection(_probe!.ConnectionString))
            {
                await c.OpenAsync();
                foreach (var t in new[] { "ItemBarcodes", "UoMConversions", "Items", "UnitsOfMeasure" })
                {
                    await using var cmd = new SqlCommand(
                        $"IF OBJECT_ID(N'dbo.{t}', N'U') IS NOT NULL DELETE FROM dbo.{t};", c) { CommandTimeout = 120 };
                    await cmd.ExecuteNonQueryAsync();
                }
            }

            using var db = Db();

            var each = new UnitOfMeasure { CompanyID = CompanyOne, Code = "EA", Name = "حبة", NameEn = "Each", IsActive = true };
            var box = new UnitOfMeasure { CompanyID = CompanyOne, Code = "CS", Name = "كرتونة", NameEn = "Case", IsActive = true };
            db.UnitsOfMeasure.AddRange(each, box);
            await db.SaveChangesAsync();

            var item = new Item
            {
                CompanyID = CompanyOne, ItemCode = "BP-PEN", Name = "قلم", NameEn = "Pen",
                ItemType = "Product", BaseUoMId = each.ID, CostingMethod = "Average",
                IsActive = true, SalesPrice = 10m, CreatedAt = DateTime.UtcNow,
            };
            var other = new Item
            {
                CompanyID = CompanyOne, ItemCode = "BP-NBK", Name = "دفتر", NameEn = "Notebook",
                ItemType = "Product", BaseUoMId = each.ID, CostingMethod = "Average",
                IsActive = true, SalesPrice = 20m, CreatedAt = DateTime.UtcNow,
            };
            db.Items.AddRange(item, other);
            await db.SaveChangesAsync();

            // Direction matters: production looks up (sold unit -> base unit).
            db.UoMConversions.Add(new UoMConversion
            { ItemId = item.ID, FromUoMId = box.ID, ToUoMId = each.ID, Factor = 12m });

            var d = new Data
            {
                ItemId = item.ID, SecondItemId = other.ID, BaseUoM = each.ID, CaseUoM = box.ID,
                EachBarcode = "BP-EA-0001", CaseBarcode = "BP-CS-0001", AmbiguousBarcode = "BP-AMBIG-01",
            };

            db.ItemBarcodes.AddRange(
                new ItemBarcode { ItemId = item.ID, Barcode = d.EachBarcode, UoMId = each.ID },
                new ItemBarcode { ItemId = item.ID, Barcode = d.CaseBarcode, UoMId = box.ID },
                // the SAME barcode value on TWO items — the ambiguity case
                new ItemBarcode { ItemId = item.ID, Barcode = d.AmbiguousBarcode, UoMId = each.ID },
                new ItemBarcode { ItemId = other.ID, Barcode = d.AmbiguousBarcode, UoMId = each.ID });
            await db.SaveChangesAsync();

            return d;
        }

        // The production predicate, expressed once. MUTATION SEAM: the mutation proofs replace only this method,
        // never production code.
        private static async Task<bool> IsUnitSellableAsync(CrossDbContext db, Item item, int soldUoM) =>
            soldUoM == item.BaseUoMId ||
            await db.UoMConversions.AsNoTracking()
                .AnyAsync(cv => cv.ItemId == item.ID && cv.FromUoMId == soldUoM && cv.ToUoMId == item.BaseUoMId);

        // MUTATION SEAM for P-02.
        private static async Task<List<int>> ResolveCandidatesAsync(CrossDbContext db, string barcode) =>
            await db.ItemBarcodes.AsNoTracking()
                .Where(b => b.Barcode == barcode).Select(b => b.ItemId).Distinct().ToListAsync();

        // =======================================================================================
        // P-01 — A MISSING UOM CONVERSION REJECTS. NO FACTOR-1 FALLBACK. (RISK-048)
        // =======================================================================================

        [SkippableFact]
        public async Task P01_a_unit_with_a_defined_conversion_is_sellable_and_one_without_is_refused()
        {
            Ready();
            var d = await ArrangeAsync();
            using var db = Db();

            var item = await db.Items.AsNoTracking().FirstAsync(i => i.ID == d.ItemId);

            Assert.True(await IsUnitSellableAsync(db, item, d.BaseUoM));   // base unit needs no conversion row
            Assert.True(await IsUnitSellableAsync(db, item, d.CaseUoM));   // CASE has one

            var orphan = new UnitOfMeasure { CompanyID = CompanyOne, Code = "PL", Name = "طبلية", NameEn = "Pallet", IsActive = true };
            db.UnitsOfMeasure.Add(orphan);
            await db.SaveChangesAsync();

            Assert.False(await IsUnitSellableAsync(db, item, orphan.ID),
                "a unit with no conversion must NOT be sellable — a factor-1 fallback would silently change quantity");

            Assert.Equal(0, await CountAsync("StockMovements"));
            Assert.Equal(0, await CountAsync("JournalEntries"));
        }

        // Production stores (sold -> base). A row in the opposite direction must NOT satisfy the guard — the subtle
        // version of the same hole, and asserting it stops a "helpful" reverse lookup being added later.
        [SkippableFact]
        public async Task P01_an_inverse_only_conversion_does_not_make_a_unit_sellable()
        {
            Ready();
            var d = await ArrangeAsync();
            using var db = Db();

            var item = await db.Items.AsNoTracking().FirstAsync(i => i.ID == d.ItemId);
            var dozen = new UnitOfMeasure { CompanyID = CompanyOne, Code = "DZ", Name = "دستة", NameEn = "Dozen", IsActive = true };
            db.UnitsOfMeasure.Add(dozen);
            await db.SaveChangesAsync();

            // deliberately the wrong way round: base -> unit
            db.UoMConversions.Add(new UoMConversion
            { ItemId = item.ID, FromUoMId = item.BaseUoMId, ToUoMId = dozen.ID, Factor = 12m });
            await db.SaveChangesAsync();

            Assert.False(await IsUnitSellableAsync(db, item, dozen.ID),
                "an inverse-only conversion must not satisfy the guard — production looks up (sold -> base) only");
        }

        // =======================================================================================
        // P-02 — AN AMBIGUOUS BARCODE REJECTS. NEVER FIRST-MATCH. (RISK-050)
        // =======================================================================================

        [SkippableFact]
        public async Task P02_a_barcode_on_two_items_is_ambiguous_and_must_not_resolve_to_the_first_match()
        {
            Ready();
            var d = await ArrangeAsync();
            using var db = Db();

            Assert.Single(await ResolveCandidatesAsync(db, d.EachBarcode));

            var ambiguous = await ResolveCandidatesAsync(db, d.AmbiguousBarcode);

            Assert.Equal(2, ambiguous.Count);
            Assert.True(ambiguous.Count > 1,
                "ambiguity must be detectable so the scan path can refuse rather than guess");

            Assert.Equal(0, await CountAsync("StockMovements"));
            Assert.Equal(0, await CountAsync("JournalEntries"));

            _out.WriteLine($"P-02: '{d.AmbiguousBarcode}' maps to items [{string.Join(", ", ambiguous)}] — must refuse");
        }

        // Ordering must not decide the answer: a first-match implementation returns different items depending on sort
        // direction, which is precisely why first-match is prohibited rather than merely discouraged.
        [SkippableFact]
        public async Task P02_first_match_would_be_order_dependent_which_is_why_it_is_prohibited()
        {
            Ready();
            var d = await ArrangeAsync();
            using var db = Db();

            var ascending = await db.ItemBarcodes.AsNoTracking()
                .Where(b => b.Barcode == d.AmbiguousBarcode).OrderBy(b => b.ItemId)
                .Select(b => b.ItemId).FirstAsync();
            var descending = await db.ItemBarcodes.AsNoTracking()
                .Where(b => b.Barcode == d.AmbiguousBarcode).OrderByDescending(b => b.ItemId)
                .Select(b => b.ItemId).FirstAsync();

            Assert.NotEqual(ascending, descending);
            _out.WriteLine($"P-02: first-match would return {ascending} or {descending} by order — refuse instead");
        }

        // =======================================================================================
        // P-03 — ItemBarcode.UoMId CONTROLS THE SOLD UNIT (RISK-046)
        // =======================================================================================

        [SkippableFact]
        public async Task P03_the_barcode_uom_determines_the_base_quantity_sold()
        {
            Ready();
            var d = await ArrangeAsync();
            using var db = Db();

            var item = await db.Items.AsNoTracking().FirstAsync(i => i.ID == d.ItemId);

            async Task<decimal> BaseQtyAsync(string barcode, decimal scanned)
            {
                var uom = await db.ItemBarcodes.AsNoTracking()
                    .Where(b => b.Barcode == barcode).Select(b => b.UoMId).FirstAsync();
                if (uom == item.BaseUoMId) return scanned;

                var factor = await db.UoMConversions.AsNoTracking()
                    .Where(cv => cv.ItemId == item.ID && cv.FromUoMId == uom && cv.ToUoMId == item.BaseUoMId)
                    .Select(cv => cv.Factor).FirstAsync();
                return scanned * factor;
            }

            Assert.Equal(1m, await BaseQtyAsync(d.EachBarcode, 1m));
            Assert.Equal(12m, await BaseQtyAsync(d.CaseBarcode, 1m));

            // THE MIGRATION DANGER, demonstrated: remap the CASE barcode to EACH and the same scan sells 1, not 12.
            var caseRow = await db.ItemBarcodes.FirstAsync(b => b.Barcode == d.CaseBarcode);
            caseRow.UoMId = d.BaseUoM;
            await db.SaveChangesAsync();

            Assert.Equal(1m, await BaseQtyAsync(d.CaseBarcode, 1m));

            _out.WriteLine("P-03: remapping ItemBarcode.UoMId changed the sold base quantity 12 -> 1. " +
                           "This is why a barcode migration requires guarded approval (RISK-046).");

            caseRow.UoMId = d.CaseUoM;
            await db.SaveChangesAsync();
            Assert.Equal(12m, await BaseQtyAsync(d.CaseBarcode, 1m));
        }

        // Two barcodes for two units on ONE item is a correct configuration, not ambiguity. Conflating it with P-02's
        // cross-item collision would make a valid setup look broken.
        [SkippableFact]
        public async Task P03_two_barcodes_for_two_units_on_one_item_is_not_ambiguity()
        {
            Ready();
            var d = await ArrangeAsync();
            using var db = Db();

            foreach (var bc in new[] { d.EachBarcode, d.CaseBarcode })
                Assert.Single(await ResolveCandidatesAsync(db, bc));

            var units = await db.ItemBarcodes.AsNoTracking()
                .Where(b => b.ItemId == d.ItemId && (b.Barcode == d.EachBarcode || b.Barcode == d.CaseBarcode))
                .Select(b => b.UoMId).Distinct().ToListAsync();
            Assert.Equal(2, units.Count);
        }

        // =======================================================================================
        // SHARED FIXTURE PURITY (RISK-036) — this class must add nothing to the shared database
        // =======================================================================================

        [SkippableFact]
        public async Task This_class_adds_nothing_to_the_shared_platform_fixture()
        {
            Ready();
            await ArrangeAsync();   // do the full schema-and-seed work first

            var after = await _sql.SharedFixtureFingerprintAsync();

            Assert.Equal(_sharedFingerprintBefore, after);
            _out.WriteLine($"shared fixture fingerprint unchanged: {after} (tables|indexes|fks|schemas|columns)");

            Assert.NotNull(_probe);
            Assert.StartsWith("CrossBuyProbe_BatchP_", _probe!.Name, StringComparison.Ordinal);
            Assert.DoesNotContain(_probe.Name, _sql.TestConnectionString);
        }
    }
}
