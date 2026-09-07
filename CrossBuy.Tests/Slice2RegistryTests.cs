using CrossBuy.BL;
using CrossBuy.BL.Platform;
using CrossBuy.Models.Context.Accounting;
using CrossBuy.Models.Context.Inventory;
using Xunit;

namespace CrossBuy.Tests
{
    // Platform Kernel slice 2 — registry expansion.
    public class Slice2RegistryTests
    {
        public static IEnumerable<object[]> PilotCodes => new List<object[]>
        {
            new object[] { EntityRegistry.Customer },
            new object[] { EntityRegistry.PurchaseInvoice },
            new object[] { EntityRegistry.ManufWorkOrder },
        };

        // ---- Tests 1-3: each pilot definition is valid and timeline-enabled ----
        [Theory]
        [MemberData(nameof(PilotCodes))]
        public void Pilot_definitions_are_complete_and_timeline_enabled(string code)
        {
            using var host = new PlatformTestHost();
            var definition = host.Registry().GetDefinition(code);

            Assert.Equal(code, definition.Code);
            Assert.False(string.IsNullOrWhiteSpace(definition.DisplayNameAr));
            Assert.False(string.IsNullOrWhiteSpace(definition.DisplayNameEn));
            Assert.NotEqual(definition.DisplayNameAr, definition.DisplayNameEn);
            Assert.False(string.IsNullOrWhiteSpace(definition.Module));
            Assert.False(string.IsNullOrWhiteSpace(definition.Icon));
            Assert.False(string.IsNullOrWhiteSpace(definition.Color));
            Assert.False(string.IsNullOrWhiteSpace(definition.PermissionScope));

            // The whole point of the slice: all three carry a timeline.
            Assert.True(definition.SupportsTimeline);
            Assert.True(definition.SupportsSearch);

            // Explicit non-goals stay off.
            Assert.False(definition.SupportsFiles);
            Assert.False(definition.SupportsFollowers);
        }

        [Fact]
        public void Pilot_entities_are_not_duplicated_and_scopes_are_the_real_module()
        {
            using var host = new PlatformTestHost();
            var registry = host.Registry();

            // Customer and ManufWorkOrder already existed in slice 1 — they were upgraded, not re-added.
            Assert.Equal(1, registry.GetDefinitions().Count(d => d.Code == EntityRegistry.Customer));
            Assert.Equal(1, registry.GetDefinitions().Count(d => d.Code == EntityRegistry.ManufWorkOrder));
            Assert.Equal(1, registry.GetDefinitions().Count(d => d.Code == EntityRegistry.PurchaseInvoice));

            // Customer is the ACCOUNTING customer master (screens under /Accounting, SaveCustomer gated by
            // AccPerm) — not a CRM entity. Authorizing it by CRM roles would block accounting users from a
            // page they can already open.
            Assert.Equal(EntityRegistry.ScopeAccounting, registry.GetDefinition(EntityRegistry.Customer).PermissionScope);
            Assert.Equal(EntityRegistry.ScopeAccounting, registry.GetDefinition(EntityRegistry.PurchaseInvoice).PermissionScope);
            // Manufacturing gets its own scope so its policy can diverge later; the adapter delegates to inventory.
            Assert.Equal(EntityRegistry.ScopeManufacturing, registry.GetDefinition(EntityRegistry.ManufWorkOrder).PermissionScope);
        }

        // ---- Test 4: BuildUrl returns a usable route for every pilot ----
        [Theory]
        [MemberData(nameof(PilotCodes))]
        public void BuildUrl_returns_a_real_per_record_route(string code)
        {
            using var host = new PlatformTestHost();
            var url = host.Registry().BuildUrl(code, 123);

            Assert.NotNull(url);
            Assert.StartsWith("/", url);
            Assert.Contains("123", url);          // per-record, not a list screen
            Assert.DoesNotContain("{id}", url);
        }

        [Fact]
        public void Pilot_routes_point_at_the_real_existing_detail_pages()
        {
            using var host = new PlatformTestHost();
            var registry = host.Registry();

            Assert.Equal("/Accounting/CustomerStatement?id=7", registry.BuildUrl(EntityRegistry.Customer, 7));
            Assert.Equal("/Accounting/PurchaseInvoiceDetail?id=7", registry.BuildUrl(EntityRegistry.PurchaseInvoice, 7));
            Assert.Equal("/Inventory/WorkOrderDetails?id=7", registry.BuildUrl(EntityRegistry.ManufWorkOrder, 7));
        }

        [Fact]
        public async Task Search_and_resolve_work_for_the_new_PurchaseInvoice_code()
        {
            using var host = new PlatformTestHost();
            host.Db.PurchaseInvoices.Add(new PurchaseInvoice
            {
                CompanyID = 1, VendorId = 1, InvoiceNo = "PV-2026-00009",
                InvoiceDate = new DateTime(2026, 3, 1), Status = "Posted", GrandTotal = 500m,
            });
            await host.Db.SaveChangesAsync();
            var id = host.Db.PurchaseInvoices.Single().ID;

            var registry = host.Registry();
            var context = PlatformTestHost.DefaultContext();

            var found = await registry.SearchAsync(EntityRegistry.PurchaseInvoice, "PV-2026", context);
            Assert.Equal("PV-2026-00009", Assert.Single(found).Label);

            var resolved = await registry.ResolveAsync(EntityRegistry.PurchaseInvoice, id, context);
            Assert.True(resolved.Found);
            Assert.Equal("PV-2026-00009", resolved.Label);
            Assert.Equal($"/Accounting/PurchaseInvoiceDetail?id={id}", resolved.Url);

            // Non-matching term returns nothing rather than everything.
            Assert.Empty(await registry.SearchAsync(EntityRegistry.PurchaseInvoice, "NOPE", context));
        }

        // ---- Test 5: cross-company resolve is rejected for every pilot ----
        [Fact]
        public async Task Cross_company_resolve_is_rejected_for_every_pilot_entity()
        {
            using var host = new PlatformTestHost();

            // B4: arranged through the authorized cross-company context — see EntityRegistryTests.
            host.Seed.Customers.Add(new Customer { CompanyID = 2, Name = "Company two customer" });
            host.Seed.PurchaseInvoices.Add(new PurchaseInvoice
            {
                CompanyID = 2, VendorId = 1, InvoiceNo = "PV-OTHER", InvoiceDate = new DateTime(2026, 3, 1),
                Status = "Posted", GrandTotal = 10m,
            });
            host.Seed.ManufWorkOrders.Add(new ManufWorkOrder
            {
                CompanyID = 2, ItemId = 1, Qty = 5m, WarehouseId = 1, WoNo = "WO-OTHER", Status = "Draft",
            });
            await host.Seed.SaveChangesAsync();

            var registry = host.Registry();
            var companyOne = PlatformTestHost.DefaultContext(companyId: 1);

            // B2: Customer and PurchaseInvoice are pilot entities, so host.Db (company 1) can no longer see the
            // company-2 rows it seeded. The ids come from an authorized cross-company read; what is under test is
            // that ResolveAsync REFUSES them for a company-1 caller.
            using var all = host.AllCompanies();
            foreach (var (code, id) in new[]
            {
                (EntityRegistry.Customer, all.Customers.Single().ID),
                (EntityRegistry.PurchaseInvoice, all.PurchaseInvoices.Single().ID),
                (EntityRegistry.ManufWorkOrder, all.ManufWorkOrders.Single().ID),
            })
            {
                var resolved = await registry.ResolveAsync(code, id, companyOne);
                Assert.False(resolved.Found);
                Assert.Null(resolved.Url);                  // never navigate to another company's record
                Assert.Equal("#" + id, resolved.Label);

                Assert.Empty(await registry.SearchAsync(code, null, companyOne));
            }
        }

        // ---- Unknown entity rejection still holds after the expansion ----
        [Fact]
        public void Unknown_codes_are_still_rejected_and_no_alias_became_canonical()
        {
            using var host = new PlatformTestHost();
            var registry = host.Registry();

            Assert.Throws<EntityCodeNotRegisteredException>(() => registry.GetDefinition("PurchaseInvoices"));
            Assert.False(registry.IsValid("purchase_invoice"));   // the NotificationTypes catalog key is NOT a code
            Assert.False(registry.IsValid("purchaseinvoice"));
            Assert.False(registry.IsValid("WorkOrder"));          // the JE SourceType value is NOT a code

            // No legacy alias needed: stored data uses the canonical PascalCase codes only. DocComment rows
            // written before the kernel used "SalesInvoice"/"PurchaseInvoice", which already match.
            Assert.True(registry.IsValid(EntityRegistry.PurchaseInvoice));
        }

        // ---- Slice 1 compatibility: the record picker must be untouched by the expansion ----
        [Fact]
        public void The_record_picker_did_not_change_when_PurchaseInvoice_was_added()
        {
            using var host = new PlatformTestHost();
            ITaskLinkResolver resolver = new TaskLinkResolver(host.Registry());

            // PurchaseInvoice is registered but was never a TM-2 picker type, so it must stay out.
            Assert.Equal(
                new[] { "SalesInvoice", "Customer", "ManufWorkOrder", "PosOrder", "Employee", "Project", "Item" },
                resolver.Types().Select(t => t.Key).ToArray());
        }

        // ---- Canonical event names for all three pilots ----
        [Fact]
        public void Slice2_event_names_follow_the_canonical_convention()
        {
            var cases = new (string code, string[] events)[]
            {
                (EntityRegistry.Customer, new[] { CustomerEvents.Created, CustomerEvents.Updated }),
                (EntityRegistry.PurchaseInvoice, new[] { PurchaseInvoiceEvents.Created, PurchaseInvoiceEvents.Updated, PurchaseInvoiceEvents.Cancelled }),
                (EntityRegistry.ManufWorkOrder, new[]
                {
                    ManufWorkOrderEvents.Created, ManufWorkOrderEvents.Updated, ManufWorkOrderEvents.Released,
                    ManufWorkOrderEvents.Produced, ManufWorkOrderEvents.Completed, ManufWorkOrderEvents.Cancelled,
                }),
            };

            foreach (var (code, events) in cases)
                foreach (var eventType in events)
                {
                    Assert.True(BusinessEventTypes.TryValidate(eventType, code, out var error), error);
                    Assert.StartsWith(code + ".", eventType);
                    // Recorded against the wrong entity is still rejected.
                    Assert.False(BusinessEventTypes.TryValidate(eventType, EntityRegistry.SalesInvoice, out _));
                }

            // Transitions that do NOT exist in the code are not declared anywhere.
            var declared = typeof(PurchaseInvoiceEvents).GetFields().Select(f => (string)f.GetRawConstantValue()!).ToArray();
            Assert.DoesNotContain("PurchaseInvoice.Approved", declared);
            Assert.DoesNotContain("PurchaseInvoice.Posted", declared);

            var workOrderDeclared = typeof(ManufWorkOrderEvents).GetFields().Select(f => (string)f.GetRawConstantValue()!).ToArray();
            Assert.DoesNotContain("ManufWorkOrder.Started", workOrderDeclared);

            var customerDeclared = typeof(CustomerEvents).GetFields().Select(f => (string)f.GetRawConstantValue()!).ToArray();
            Assert.DoesNotContain("Customer.StatusChanged", customerDeclared);
        }
    }
}