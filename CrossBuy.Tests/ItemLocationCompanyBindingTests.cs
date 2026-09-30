using System.Globalization;
using System.Reflection;
using CrossBuy.BL;
using CrossBuy.BL.Platform;
using CrossBuy.Controllers;
using CrossBuy.Models;
using CrossBuy.Models.Context.Inventory;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using Xunit;

namespace CrossBuy.Tests
{
    // =================================================================================================
    // OPS3 P0 — the company binding of the ItemWarehouseSetting write paths.
    //
    // ItemWarehouseSetting carries no CompanyID, is not a company-filtered pilot entity, and is not in
    // DeliberatelyUnfiltered either. A row pairing this company's warehouse with another company's item
    // is therefore indistinguishable from a legitimate one once written, and nothing downstream can
    // catch it. The binding has to be proven before the write, and for EVERY id in the payload.
    //
    // Two actions reach that table, and they were open in different ways:
    //
    //   SaveItemLocations   — the section lookup stood upstream of the write, so scoping
    //                         WarehouseService.GetBinLocationsAsync closed the WAREHOUSE half. The ITEM
    //                         half stayed open: a foreign item with a valid own-company section wrote.
    //   SaveReorderSettings — no section lookup at all, so nothing stood between the request and the
    //                         write, and the values written are the reorder thresholds that decide when
    //                         another tenant's low-stock work is raised or suppressed.
    //
    // Assertions are identity-based. A symmetric row-count fixture passes with the predicate inverted,
    // which is exactly the mistake these tests exist to catch.
    // =================================================================================================
    public class ItemLocationCompanyBindingTests
    {
        private const int Ours = 1;
        private const int Theirs = 7;

        // ---- fixtures ---------------------------------------------------------------------------
        private static Warehouse Wh(int id, int companyId, string code) => new()
        { ID = id, CompanyID = companyId, Code = code, Name = code };

        private static BinLocation Section(int id, int warehouseId, string code) => new()
        { ID = id, WarehouseId = warehouseId, Code = code, Name = code, LocationType = "Section", IsActive = true };

        private static Item It(int id, int companyId, string code) => new()
        { ID = id, CompanyID = companyId, ItemCode = code, Barcode = code, Name = code, IsActive = true };

        /// Seeds through the UNFILTERED context: a filtered seed could not create the foreign rows
        /// these tests exist to exclude.
        private static void Seed(PlatformTestHost host, Action<CrossBuy.Models.Context.CrossDbContext> seed)
        {
            seed(host.Seed);
            host.Seed.SaveChanges();
        }

        private static IWarehouseService ServiceFor(PlatformTestHost host, int companyId)
            => new WarehouseService(host.Request(companyId).Db);

        /// A request that resolved NOBODY. The seeded data is company 1's and is perfectly valid;
        /// the only thing wrong is that this caller has no company, which must be enough to refuse.
        private static InventoryController UnresolvedControllerFor(PlatformTestHost host)
        {
            var db = host.Request(Ours).Db;
            var controller = new InventoryController(
                null!, new WarehouseService(db), null!, db, null!,
                null!, null!, null!, null!, null!,
                null!, null!, null!, null!, null!,
                null!, null!, null!, null!,
                new StubCompanyResolver(Ours, resolves: false), null!, new StubLocalizer());
            var http = new DefaultHttpContext();
            controller.ControllerContext = new ControllerContext { HttpContext = http };
            controller.TempData = new TempDataDictionary(http, new StubTempDataProvider());
            return controller;
        }

        private static InventoryController ControllerFor(PlatformTestHost host, int companyId)
            => ControllerOver(host.Request(companyId).Db, companyId);

        /// The controller over a GIVEN context, with the resolver naming `companyId`. Used directly only
        /// by PART 4, to put the controller on a context whose ambient filter does NOT already hide the
        /// other tenant — the only arrangement in which the explicit CompanyID predicates are the sole guard.
        private static InventoryController ControllerOver(CrossBuy.Models.Context.CrossDbContext db, int companyId)
        {
            var controller = new InventoryController(
                null!, new WarehouseService(db), null!, db, null!,
                null!, null!, null!, null!, null!,
                null!, null!, null!, null!, null!,
                null!, null!, null!, null!,
                new StubCompanyResolver(companyId), null!, new StubLocalizer());

            var http = new DefaultHttpContext();
            controller.ControllerContext = new ControllerContext { HttpContext = http };
            controller.TempData = new TempDataDictionary(http, new StubTempDataProvider());
            return controller;
        }

        /// Reads through the UNFILTERED context so a cross-tenant write cannot hide behind a filter.
        private static List<ItemWarehouseSetting> AllSettings(PlatformTestHost host)
            => host.Seed.ItemWarehouseSettings.AsNoTracking().ToList();

        private static string LocRows(params (int itemId, int sectionId)[] rows)
            => "[" + string.Join(",", rows.Select(r =>
                $"{{\"ItemId\":{r.itemId},\"SectionId\":{r.sectionId},\"RackId\":null}}")) + "]";

        private static string ReorderRows(params (int itemId, decimal rop)[] rows)
            => "[" + string.Join(",", rows.Select(r =>
                $"{{\"ItemId\":{r.itemId},\"ReorderPoint\":{r.rop.ToString(CultureInfo.InvariantCulture)}," +
                $"\"MinQty\":1,\"MaxQty\":99}}")) + "]";

        private static string? Refusal(InventoryController c)
            => c.TempData["InvErr"] as string;

        /// Everything the caller can observe of a POST: which page it lands on, with which route values,
        /// and the message it carries. Two refusals are externally equivalent only if all of it matches.
        private static string Observed(IActionResult result, InventoryController c)
        {
            var r = Assert.IsType<RedirectToActionResult>(result);
            var route = string.Join(";", (r.RouteValues ?? new()).OrderBy(kv => kv.Key)
                .Select(kv => $"{kv.Key}={kv.Value}"));
            return $"{r.ActionName}|{r.ControllerName}|{route}|{Refusal(c)}|{c.TempData["InvMsg"]}";
        }

        // =========================================================================================
        // PART 1 — the warehouse half, at the read that gates it (WarehouseService).
        // =========================================================================================

        [Fact]
        public async Task A_foreign_warehouses_sections_are_not_visible_to_this_company()
        {
            using var host = new PlatformTestHost(Ours);
            Seed(host, db =>
            {
                db.Warehouses.Add(Wh(300, Theirs, "THEIR-WH"));
                db.BinLocations.Add(Section(3001, 300, "THEIR-SEC"));
            });

            var bins = await ServiceFor(host, Ours).GetBinLocationsAsync(300);

            Assert.DoesNotContain(bins, b => b.ID == 3001);
            Assert.Empty(bins);
        }

        [Fact]
        public async Task With_no_visible_section_the_foreign_write_cannot_be_reached()
        {
            using var host = new PlatformTestHost(Ours);
            Seed(host, db =>
            {
                db.Warehouses.Add(Wh(301, Theirs, "THEIR-WH"));
                db.BinLocations.Add(Section(3011, 301, "THEIR-SEC"));
            });

            var bins = (await ServiceFor(host, Ours).GetBinLocationsAsync(301)).ToDictionary(b => b.ID, b => b);

            Assert.False(bins.TryGetValue(3011, out _));
        }

        [Fact]
        public async Task Our_own_warehouse_still_returns_its_sections()
        {
            using var host = new PlatformTestHost(Ours);
            Seed(host, db =>
            {
                db.Warehouses.Add(Wh(100, Ours, "OUR-WH"));
                db.BinLocations.Add(Section(1001, 100, "OUR-SEC"));
                db.BinLocations.Add(Section(1002, 100, "OUR-SEC-2"));
            });

            var bins = await ServiceFor(host, Ours).GetBinLocationsAsync(100);

            Assert.Equal(2, bins.Count);
            Assert.Contains(bins, b => b.ID == 1001);
            Assert.Contains(bins, b => b.ID == 1002);
        }

        [Fact]
        public async Task Two_tenants_warehouses_do_not_leak_into_each_other()
        {
            using var host = new PlatformTestHost(Ours);
            Seed(host, db =>
            {
                db.Warehouses.Add(Wh(110, Ours, "OUR-WH"));
                db.Warehouses.Add(Wh(310, Theirs, "THEIR-WH"));
                db.BinLocations.Add(Section(1101, 110, "OUR-SEC"));
                db.BinLocations.Add(Section(3101, 310, "THEIR-SEC"));
            });

            var ours = await ServiceFor(host, Ours).GetBinLocationsAsync(110);
            var theirsFromUs = await ServiceFor(host, Ours).GetBinLocationsAsync(310);

            Assert.Single(ours);
            Assert.Equal(1101, ours[0].ID);
            Assert.Empty(theirsFromUs);
        }

        [Fact]
        public async Task The_scope_follows_the_caller_and_is_not_pinned_to_one_company()
        {
            using var host = new PlatformTestHost(Ours);
            Seed(host, db =>
            {
                db.Warehouses.Add(Wh(120, Ours, "OUR-WH"));
                db.Warehouses.Add(Wh(320, Theirs, "THEIR-WH"));
                db.BinLocations.Add(Section(1201, 120, "OUR-SEC"));
                db.BinLocations.Add(Section(3201, 320, "THEIR-SEC"));
            });

            var asThem = ServiceFor(host, Theirs);

            var theirOwn = await asThem.GetBinLocationsAsync(320);
            var oursFromThem = await asThem.GetBinLocationsAsync(120);

            Assert.Single(theirOwn);
            Assert.Equal(3201, theirOwn[0].ID);
            Assert.Empty(oursFromThem);
        }

        [Fact]
        public async Task An_unknown_warehouse_returns_nothing_rather_than_everything()
        {
            using var host = new PlatformTestHost(Ours);
            Seed(host, db =>
            {
                db.Warehouses.Add(Wh(130, Ours, "OUR-WH"));
                db.BinLocations.Add(Section(1301, 130, "OUR-SEC"));
            });

            Assert.Empty(await ServiceFor(host, Ours).GetBinLocationsAsync(999999));
        }

        // =========================================================================================
        // PART 2 — SaveItemLocations, through the real action.
        // =========================================================================================

        private static void SeedBothTenants(PlatformTestHost host)
        {
            Seed(host, db =>
            {
                db.Warehouses.Add(Wh(10, Ours, "OUR-WH"));
                db.BinLocations.Add(Section(11, 10, "OUR-SEC"));
                db.Items.Add(It(20, Ours, "OUR-ITEM"));
                db.Items.Add(It(21, Ours, "OUR-ITEM-2"));

                db.Warehouses.Add(Wh(70, Theirs, "THEIR-WH"));
                db.BinLocations.Add(Section(71, 70, "THEIR-SEC"));
                db.Items.Add(It(80, Theirs, "THEIR-ITEM"));
            });
        }

        [Fact]
        public async Task Locations_own_warehouse_and_own_item_is_written()
        {
            using var host = new PlatformTestHost(Ours);
            SeedBothTenants(host);

            await ControllerFor(host, Ours).SaveItemLocations(10, LocRows((20, 11)));

            var rows = AllSettings(host);
            Assert.Single(rows);
            Assert.Equal(20, rows[0].ItemId);
            Assert.Equal(10, rows[0].WarehouseId);
            Assert.Equal(11, rows[0].DefaultSectionId);
        }

        [Fact]
        public async Task Locations_foreign_warehouse_is_refused_and_writes_nothing()
        {
            using var host = new PlatformTestHost(Ours);
            SeedBothTenants(host);

            var c = ControllerFor(host, Ours);
            await c.SaveItemLocations(70, LocRows((80, 71)));

            Assert.Empty(AllSettings(host));
            Assert.NotNull(Refusal(c));
        }

        [Fact]
        public async Task Locations_foreign_item_in_our_own_warehouse_is_refused()
        {
            using var host = new PlatformTestHost(Ours);
            SeedBothTenants(host);

            // our warehouse, our section, THEIR item — the half the warehouse scoping cannot see
            await ControllerFor(host, Ours).SaveItemLocations(10, LocRows((80, 11)));

            Assert.Empty(AllSettings(host));
        }

        [Fact]
        public async Task Locations_unknown_item_is_refused()
        {
            using var host = new PlatformTestHost(Ours);
            SeedBothTenants(host);

            await ControllerFor(host, Ours).SaveItemLocations(10, LocRows((999999, 11)));

            Assert.Empty(AllSettings(host));
        }

        [Fact]
        public async Task Locations_mixed_own_and_foreign_items_write_zero_rows()
        {
            using var host = new PlatformTestHost(Ours);
            SeedBothTenants(host);

            // two of ours FIRST, so a valid-prefix implementation would already have written them
            await ControllerFor(host, Ours).SaveItemLocations(10, LocRows((20, 11), (21, 11), (80, 11)));

            Assert.Empty(AllSettings(host));
        }

        [Fact]
        public async Task Locations_mixed_own_and_unknown_items_write_zero_rows()
        {
            using var host = new PlatformTestHost(Ours);
            SeedBothTenants(host);

            await ControllerFor(host, Ours).SaveItemLocations(10, LocRows((20, 11), (999999, 11)));

            Assert.Empty(AllSettings(host));
        }

        [Fact]
        public async Task Locations_foreign_and_unknown_items_are_indistinguishable()
        {
            using var host = new PlatformTestHost(Ours);
            SeedBothTenants(host);

            var foreignCall = ControllerFor(host, Ours);
            var foreignResult = await foreignCall.SaveItemLocations(10, LocRows((80, 11)));

            var unknownCall = ControllerFor(host, Ours);
            var unknownResult = await unknownCall.SaveItemLocations(10, LocRows((999999, 11)));

            // same answer for "exists elsewhere" and "does not exist" — no existence oracle
            Assert.Equal(Refusal(foreignCall), Refusal(unknownCall));
            Assert.NotNull(Refusal(foreignCall));
            Assert.Equal(Observed(unknownResult, unknownCall), Observed(foreignResult, foreignCall));
        }

        [Fact]
        public async Task Locations_scope_follows_the_caller_not_company_one()
        {
            using var host = new PlatformTestHost(Ours);
            SeedBothTenants(host);

            // acting as company 7: its own pair is accepted, company 1's is not
            await ControllerFor(host, Theirs).SaveItemLocations(70, LocRows((80, 71)));
            var afterTheirOwn = AllSettings(host);
            Assert.Single(afterTheirOwn);
            Assert.Equal(80, afterTheirOwn[0].ItemId);

            await ControllerFor(host, Theirs).SaveItemLocations(10, LocRows((20, 11)));
            Assert.Single(AllSettings(host));          // unchanged — company 1's pair was refused
            Assert.DoesNotContain(AllSettings(host), s => s.ItemId == 20);
        }

        // =========================================================================================
        // PART 3 - SaveReorderSettings, through the real action.
        //
        // WHAT THIS ACTION CAN AND CANNOT BE HELD TO, measured rather than assumed.
        //
        // InvReportRow declares ItemId, ReorderPoint, MinQty and MaxQty as public FIELDS, and the
        // action deserialises with only PropertyNameCaseInsensitive. System.Text.Json does not bind
        // fields without IncludeFields, so every row arrives with ItemId = 0 and is dropped by the
        // pre-existing `if (r.ItemId <= 0) continue;`. The action persists nothing, for anybody. That
        // deserialiser is byte-identical to HEAD; this slice neither introduced it nor may repair it,
        // because the SHF-26 concern authorises tenant binding and not reorder behaviour
        // (backlog: OPS-REORDER-BINDING-01).
        //
        // TWO CONSEQUENCES, both stated plainly rather than papered over:
        //
        //   1. "no rows were written" proves nothing on this action - it is true of every payload,
        //      authorised or not. So these tests assert the REFUSAL SIGNAL, which does discriminate.
        //
        //   2. The item half of the gate cannot fire here today, because the gate reads the same
        //      r.ItemId the write reads. THAT COUPLING IS THE SAFETY PROPERTY: gate and write consume
        //      one field on one set of objects, so there is no state in which the write is live and
        //      the gate is blind. Fix the binding and both activate in the same commit.
        //
        // The warehouse half DOES bind today - warehouseId is an action parameter, not payload - and
        // is asserted below. The full item matrix is proved on SaveItemLocations in PART 2, whose DTO
        // uses properties and therefore binds.
        // =========================================================================================

        [Fact]
        public async Task Reorder_an_authorised_warehouse_is_not_refused()
        {
            using var host = new PlatformTestHost(Ours);
            SeedBothTenants(host);

            var c = ControllerFor(host, Ours);
            await c.SaveReorderSettings(10, ReorderRows((20, 50m)));

            Assert.Null(Refusal(c));
        }

        [Fact]
        public async Task Reorder_a_foreign_warehouse_is_refused()
        {
            using var host = new PlatformTestHost(Ours);
            SeedBothTenants(host);

            var c = ControllerFor(host, Ours);
            await c.SaveReorderSettings(70, ReorderRows((80, 50m)));

            Assert.NotNull(Refusal(c));
            Assert.Empty(AllSettings(host));
        }

        [Fact]
        public async Task Reorder_an_unknown_warehouse_is_refused()
        {
            using var host = new PlatformTestHost(Ours);
            SeedBothTenants(host);

            var c = ControllerFor(host, Ours);
            await c.SaveReorderSettings(999999, ReorderRows((20, 50m)));

            Assert.NotNull(Refusal(c));
        }

        [Fact]
        public async Task Reorder_foreign_and_unknown_warehouses_are_indistinguishable()
        {
            using var host = new PlatformTestHost(Ours);
            SeedBothTenants(host);

            var foreignCall = ControllerFor(host, Ours);
            var foreignResult = await foreignCall.SaveReorderSettings(70, ReorderRows((20, 50m)));

            var unknownCall = ControllerFor(host, Ours);
            var unknownResult = await unknownCall.SaveReorderSettings(999999, ReorderRows((20, 50m)));

            Assert.Equal(Refusal(foreignCall), Refusal(unknownCall));
            Assert.NotNull(Refusal(foreignCall));

            // the route value is the caller's own input echoed back, so equality holds only modulo it
            var f = Assert.IsType<RedirectToActionResult>(foreignResult);
            var u = Assert.IsType<RedirectToActionResult>(unknownResult);
            Assert.Equal(u.ActionName, f.ActionName);
            Assert.Equal(u.ControllerName, f.ControllerName);
        }

        // An existing threshold owned by another tenant survives, AND the attempt is refused. The
        // refusal is what makes this non-vacuous: survival alone would be true of any payload.
        [Fact]
        public async Task Reorder_another_company_cannot_touch_existing_thresholds()
        {
            using var host = new PlatformTestHost(Ours);
            SeedBothTenants(host);
            Seed(host, db => db.ItemWarehouseSettings.Add(
                new ItemWarehouseSetting { ItemId = 80, WarehouseId = 70, ReorderPoint = 500m }));

            var c = ControllerFor(host, Ours);
            await c.SaveReorderSettings(70, ReorderRows((80, 1m)));

            Assert.NotNull(Refusal(c));
            var row = Assert.Single(AllSettings(host));
            Assert.Equal(80, row.ItemId);
            Assert.Equal(500m, row.ReorderPoint);
        }

        [Fact]
        public async Task Reorder_scope_follows_the_caller_not_company_one()
        {
            using var host = new PlatformTestHost(Ours);
            SeedBothTenants(host);

            var asThem = ControllerFor(host, Theirs);
            await asThem.SaveReorderSettings(70, ReorderRows((80, 50m)));
            Assert.Null(Refusal(asThem));

            var asThemOnUs = ControllerFor(host, Theirs);
            await asThemOnUs.SaveReorderSettings(10, ReorderRows((20, 50m)));
            Assert.NotNull(Refusal(asThemOnUs));
        }

        // Pins the pre-existing binding defect itself, so a later reader cannot mistake the silence
        // above for a fault in the gate. A record, not a repair - see OPS-REORDER-BINDING-01.
        [Fact]
        public void Reorder_payload_fields_do_not_bind_today_which_is_why_nothing_persists()
        {
            var rows = System.Text.Json.JsonSerializer.Deserialize<List<InvReportRow>>(
                "[{\"ItemId\":20,\"ReorderPoint\":50}]",
                new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;

            Assert.Single(rows);
            Assert.Equal(0, rows[0].ItemId);          // a FIELD, so it never binds
            Assert.Equal(0m, rows[0].ReorderPoint);
        }

        // =========================================================================================
        // PART 4 — properties the reference matrix left implicit, added in the ORIGINAL recovery.
        // =========================================================================================

        // EVERY distinct id is checked, and Distinct is load-bearing: a repeated own id must not be
        // counted twice against a single matching row, and must not dilute a foreign id either.
        //
        // Only the GATE is asserted here. What the pre-existing write loop then does with a repeated id
        // is its own behaviour and not this slice's: its lookup reads the database, not the rows it has
        // just added, so it currently inserts one row per occurrence. That is recorded as a separate
        // backlog item rather than pinned, so fixing it later does not break a tenant-binding test.
        [Fact]
        public async Task Locations_repeated_own_item_is_not_refused()
        {
            using var host = new PlatformTestHost(Ours);
            SeedBothTenants(host);

            var c = ControllerFor(host, Ours);
            await c.SaveItemLocations(10, LocRows((20, 11), (20, 11)));

            Assert.Null(Refusal(c));
            var rows = AllSettings(host);
            Assert.NotEmpty(rows);
            Assert.All(rows, s => { Assert.Equal(20, s.ItemId); Assert.Equal(10, s.WarehouseId); });
        }

        [Fact]
        public async Task Locations_foreign_item_after_repeated_own_items_writes_zero_rows()
        {
            using var host = new PlatformTestHost(Ours);
            SeedBothTenants(host);

            var c = ControllerFor(host, Ours);
            await c.SaveItemLocations(10, LocRows((20, 11), (20, 11), (21, 11), (80, 11)));

            Assert.NotNull(Refusal(c));
            Assert.Empty(AllSettings(host));
        }

        // Non-positive ids keep their pre-existing meaning (the row is skipped) and cannot be used to
        // smuggle a foreign id past the gate or to skip the warehouse half of it.
        [Fact]
        public async Task Locations_non_positive_ids_keep_their_skip_semantics()
        {
            using var host = new PlatformTestHost(Ours);
            SeedBothTenants(host);

            var c = ControllerFor(host, Ours);
            await c.SaveItemLocations(10, LocRows((0, 11), (-3, 11), (20, 11)));

            Assert.Null(Refusal(c));
            var row = Assert.Single(AllSettings(host));
            Assert.Equal(20, row.ItemId);
        }

        [Fact]
        public async Task Locations_non_positive_ids_do_not_carry_a_foreign_item()
        {
            using var host = new PlatformTestHost(Ours);
            SeedBothTenants(host);

            var c = ControllerFor(host, Ours);
            await c.SaveItemLocations(10, LocRows((0, 11), (-3, 11), (80, 11)));

            Assert.NotNull(Refusal(c));
            Assert.Empty(AllSettings(host));
        }

        [Fact]
        public async Task Locations_payload_with_no_positive_id_still_checks_the_warehouse()
        {
            using var host = new PlatformTestHost(Ours);
            SeedBothTenants(host);

            var foreign = ControllerFor(host, Ours);
            await foreign.SaveItemLocations(70, LocRows((0, 71)));
            Assert.NotNull(Refusal(foreign));

            var own = ControllerFor(host, Ours);
            await own.SaveItemLocations(10, LocRows((0, 11)));
            Assert.Null(Refusal(own));

            Assert.Empty(AllSettings(host));
        }

        // THE EXPLICIT PREDICATES, isolated. Under a request-scoped context the Item/Warehouse query
        // filter already hides the other tenant, so it and the explicit `CompanyID == scope.CompanyId`
        // predicates are redundant there and neither can be proven alone. A legitimate cross-company
        // administrative scope switches the filter off (and WarehouseService honours that bypass by
        // design), leaving the resolved-company predicates as the only guard. The resolver still names
        // OUR company, so the binding must still refuse the other tenant's ids.
        [Fact]
        public async Task Under_a_cross_company_scope_the_resolved_company_still_binds_the_item()
        {
            using var host = new PlatformTestHost(Ours);
            SeedBothTenants(host);

            var own = ControllerOver(host.AllCompanies(), Ours);
            await own.SaveItemLocations(10, LocRows((20, 11)));
            Assert.Null(Refusal(own));
            Assert.Single(AllSettings(host), s => s.ItemId == 20);

            var foreign = ControllerOver(host.AllCompanies(), Ours);
            await foreign.SaveItemLocations(10, LocRows((80, 11)));
            Assert.NotNull(Refusal(foreign));
            Assert.DoesNotContain(AllSettings(host), s => s.ItemId == 80);
        }

        [Fact]
        public async Task Under_a_cross_company_scope_the_resolved_company_still_binds_the_warehouse()
        {
            using var host = new PlatformTestHost(Ours);
            SeedBothTenants(host);

            // their warehouse, their (now visible) section, OUR item: only the warehouse predicate stands
            var locations = ControllerOver(host.AllCompanies(), Ours);
            await locations.SaveItemLocations(70, LocRows((20, 71)));
            Assert.NotNull(Refusal(locations));
            Assert.Empty(AllSettings(host));

            var reorder = ControllerOver(host.AllCompanies(), Ours);
            await reorder.SaveReorderSettings(70, ReorderRows((20, 50m)));
            Assert.NotNull(Refusal(reorder));
        }

        // The tenant gate is ADDED to the existing contract, not substituted for it: both write actions
        // keep POST-only, anti-forgery and the Inventory "manage" permission exactly as at HEAD.
        [Theory]
        [InlineData(nameof(InventoryController.SaveItemLocations))]
        [InlineData(nameof(InventoryController.SaveReorderSettings))]
        public void Write_actions_keep_post_antiforgery_and_manage_permission(string actionName)
        {
            var method = Assert.Single(typeof(InventoryController).GetMethods(), m => m.Name == actionName);

            Assert.NotNull(method.GetCustomAttribute<HttpPostAttribute>());
            Assert.NotNull(method.GetCustomAttribute<ValidateAntiForgeryTokenAttribute>());
            Assert.Null(method.GetCustomAttribute<HttpGetAttribute>());

            var perm = Assert.Single(method.GetCustomAttributes<InvPermAttribute>());
            var action = typeof(InvPermAttribute).GetField("_action", BindingFlags.Instance | BindingFlags.NonPublic)!;
            Assert.Equal("manage", action.GetValue(perm));
        }

        // =========================================================================================
        // COMPANY RESOLUTION - the invariant the authoritative suite did not reach.
        //
        // Measured: CompanyResolution.Unresolved and .Mismatch both leave CompanyId at 0, so the
        // company predicates already match nothing on a failed resolution and the explicit
        // `if (!scope.Ok) return false;` is defence-in-depth over them. That is exactly why it has
        // to be tested directly: the guard protects against a FUTURE resolution that fails while
        // carrying a non-zero company, and nothing else in the suite would notice it disappearing.
        //
        // The payload below is company 1's own, entirely valid data. The only defect is the caller.
        // =========================================================================================

        [Fact]
        public async Task Locations_an_unresolved_company_is_refused_even_for_valid_company_one_data()
        {
            using var host = new PlatformTestHost(Ours);
            SeedBothTenants(host);

            var c = UnresolvedControllerFor(host);
            await c.SaveItemLocations(10, LocRows((20, 11)));

            Assert.NotNull(Refusal(c));
            Assert.Empty(AllSettings(host));   // no company-1 fallback let it through
        }

        [Fact]
        public async Task Reorder_an_unresolved_company_is_refused_even_for_valid_company_one_data()
        {
            using var host = new PlatformTestHost(Ours);
            SeedBothTenants(host);

            var c = UnresolvedControllerFor(host);
            await c.SaveReorderSettings(10, ReorderRows((20, 50m)));

            Assert.NotNull(Refusal(c));
            Assert.Empty(AllSettings(host));
        }

        // ---- stubs -------------------------------------------------------------------------------
        private sealed class StubCompanyResolver : IRequestCompanyResolver
        {
            private readonly int _companyId;
            private readonly bool _resolves;
            public StubCompanyResolver(int companyId, bool resolves = true)
            { _companyId = companyId; _resolves = resolves; }

            public Task<CompanyResolution> ResolveAsync(
                int? requestSuppliedCompanyId = null, CancellationToken cancellationToken = default)
                => Task.FromResult(_resolves
                    ? CompanyResolution.Resolved(_companyId, null, null)
                    : CompanyResolution.Unresolved("test: no company on this request"));
        }

        private sealed class StubLocalizer : IStringLocalizer<CrossBuy.SharedResources>
        {
            public LocalizedString this[string name] => new(name, name, resourceNotFound: false);
            public LocalizedString this[string name, params object[] arguments]
                => new(name, string.Format(CultureInfo.InvariantCulture, name, arguments), resourceNotFound: false);
            public IEnumerable<LocalizedString> GetAllStrings(bool includeParentCultures)
                => Array.Empty<LocalizedString>();
        }

        private sealed class StubTempDataProvider : ITempDataProvider
        {
            public IDictionary<string, object?> LoadTempData(HttpContext context) => new Dictionary<string, object?>();
            public void SaveTempData(HttpContext context, IDictionary<string, object?> values) { }
        }
    }
}
