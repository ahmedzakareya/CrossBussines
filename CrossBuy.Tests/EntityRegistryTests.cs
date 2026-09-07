using CrossBuy.BL;
using CrossBuy.BL.Platform;
using Xunit;

namespace CrossBuy.Tests
{
    public class EntityRegistryTests
    {
        // ---- Test 1: the registry rejects an unknown EntityType ----
        [Fact]
        public void GetDefinition_rejects_an_unknown_entity_code()
        {
            using var host = new PlatformTestHost();
            var registry = host.Registry();

            Assert.Throws<EntityCodeNotRegisteredException>(() => registry.GetDefinition("NotARealEntity"));
            Assert.False(registry.IsValid("NotARealEntity"));
            Assert.False(registry.IsValid(null));
            Assert.False(registry.IsValid(""));

            // Codes are ORDINAL: a differently-cased string is a different (invalid) code, which is what
            // stops "salesinvoice" and "SalesInvoice" ever becoming two vocabularies again.
            Assert.False(registry.IsValid("salesinvoice"));
            Assert.True(registry.IsValid(EntityRegistry.SalesInvoice));
        }

        [Fact]
        public void Every_definition_has_a_permission_scope_and_a_usable_url_contract()
        {
            using var host = new PlatformTestHost();
            var registry = host.Registry();

            foreach (var definition in registry.GetDefinitions())
            {
                Assert.False(string.IsNullOrWhiteSpace(definition.PermissionScope));
                Assert.False(string.IsNullOrWhiteSpace(definition.Icon));
                Assert.False(string.IsNullOrWhiteSpace(definition.Color));

                var url = registry.BuildUrl(definition.Code, 42);
                if (definition.RouteTemplate == null)
                {
                    Assert.Null(url);
                }
                else if (definition.RouteTemplate.Contains("{id}"))
                {
                    Assert.Contains("42", url);
                    Assert.DoesNotContain("{id}", url);
                }
                else
                {
                    // List-only screen: returned unchanged, never with a bogus id appended.
                    Assert.Equal(definition.RouteTemplate, url);
                }
            }
        }

        // ---- Test 2: the TaskLinkResolver compatibility wrapper still behaves as before promotion ----
        [Fact]
        public async Task TaskLinkResolver_preserves_its_pre_kernel_behaviour()
        {
            using var host = new PlatformTestHost();
            var registry = host.Registry();
            ITaskLinkResolver resolver = new TaskLinkResolver(registry);

            // The picker listed exactly these seven types, in this order. "Supplier" was searchable but
            // never listed, so it must still be absent.
            var keys = resolver.Types().Select(t => t.Key).ToList();
            Assert.Equal(
                new[] { "SalesInvoice", "Customer", "ManufWorkOrder", "PosOrder", "Employee", "Project", "Item" },
                keys);
            Assert.DoesNotContain(EntityRegistry.Supplier, keys);
            Assert.All(resolver.Types(), t =>
            {
                Assert.False(string.IsNullOrWhiteSpace(t.LabelAr));
                Assert.False(string.IsNullOrWhiteSpace(t.LabelEn));
                Assert.False(string.IsNullOrWhiteSpace(t.Icon));
            });

            // Supplier: searchable, but not resolvable through the picker contract.
            host.Db.Vendors.Add(new CrossBuy.Models.Context.Accounting.Vendor { CompanyID = 1, Name = "مورّد الاختبار" });
            host.Db.Customers.Add(new CrossBuy.Models.Context.Accounting.Customer { CompanyID = 1, Name = "عميل الاختبار" });
            await host.Db.SaveChangesAsync();
            var vendorId = host.Db.Vendors.Single().ID;
            var customerId = host.Db.Customers.Single().ID;

            var suppliers = await resolver.SearchAsync(1, EntityRegistry.Supplier, null);
            Assert.Single(suppliers);
            Assert.Null(await resolver.ResolveAsync(1, EntityRegistry.Supplier, vendorId));

            // TM-9 party lookup keeps returning the plain name, and null when the row is absent.
            Assert.Equal("مورّد الاختبار", await resolver.PartyNameAsync(1, "Supplier", vendorId));
            Assert.Equal("عميل الاختبار", await resolver.PartyNameAsync(1, "Customer", customerId));
            Assert.Null(await resolver.PartyNameAsync(1, "Customer", 99999));
            Assert.Null(await resolver.PartyNameAsync(1, "Employee", customerId));   // unsupported party type

            // Unknown / empty / non-positive inputs stay null-or-empty instead of throwing.
            Assert.Empty(await resolver.SearchAsync(1, "NotARealEntity", "x"));
            Assert.Null(await resolver.ResolveAsync(1, "NotARealEntity", 1));
            Assert.Null(await resolver.ResolveAsync(1, null, 1));
            Assert.Null(await resolver.ResolveAsync(1, EntityRegistry.SalesInvoice, 0));

            // A record that no longer exists degrades to "#id" with no link — never a crash, never a
            // navigable URL to a missing row.
            var missing = await resolver.ResolveAsync(1, EntityRegistry.SalesInvoice, 4242);
            Assert.NotNull(missing);
            Assert.Equal("#4242", missing!.Label);
            Assert.Null(missing.Url);
        }

        [Fact]
        public async Task Registry_search_and_resolve_are_company_scoped()
        {
            using var host = new PlatformTestHost();
            // B4: another company's row is ARRANGED through the authorized cross-company context — the write guard
            // (correctly) refuses a company-1 scope creating a company-2 row, and that refusal has its own test.
            host.Seed.Customers.Add(new CrossBuy.Models.Context.Accounting.Customer { CompanyID = 1, Name = "Company one customer" });
            host.Seed.Customers.Add(new CrossBuy.Models.Context.Accounting.Customer { CompanyID = 2, Name = "Company two customer" });
            await host.Seed.SaveChangesAsync();
            // B2: host.Db is filtered to company 1, so the company-2 row it just wrote is no longer visible
            // through it. The id is read through an authorized cross-company context — the same door production
            // uses — because the point of the test is what the REGISTRY does with that id, not who can see it.
            using var all = host.AllCompanies();
            var otherCompanyId = all.Customers.Single(c => c.CompanyID == 2).ID;

            var registry = host.Registry();
            var rows = await registry.SearchAsync(EntityRegistry.Customer, null, PlatformTestHost.DefaultContext(companyId: 1));
            Assert.Single(rows);
            Assert.Equal("Company one customer", rows[0].Label);

            var crossCompany = await registry.ResolveAsync(EntityRegistry.Customer, otherCompanyId, PlatformTestHost.DefaultContext(companyId: 1));
            Assert.False(crossCompany.Found);
            Assert.Null(crossCompany.Url);
        }

        // ---- Test 15: SalesInvoice event names follow the canonical convention ----
        [Fact]
        public void Event_type_names_must_be_EntityCode_dot_PascalCaseAction()
        {
            foreach (var eventType in new[] { SalesInvoiceEvents.Created, SalesInvoiceEvents.Updated, SalesInvoiceEvents.Cancelled })
            {
                Assert.True(BusinessEventTypes.TryValidate(eventType, EntityRegistry.SalesInvoice, out var error), error);
                Assert.StartsWith(EntityRegistry.SalesInvoice + ".", eventType);
            }

            Assert.Equal("SalesInvoice.Created", SalesInvoiceEvents.Created);
            Assert.Equal("Created", BusinessEventTypes.ActionOf(SalesInvoiceEvents.Created));

            // The three non-canonical styles that existed in the codebase before the kernel.
            Assert.False(BusinessEventTypes.TryValidate("InvoiceCreated", EntityRegistry.SalesInvoice, out _));
            Assert.False(BusinessEventTypes.TryValidate("sales_invoice_created", EntityRegistry.SalesInvoice, out _));
            Assert.False(BusinessEventTypes.TryValidate("SalesInvoiceCreated", EntityRegistry.SalesInvoice, out _));

            // Other malformed shapes.
            Assert.False(BusinessEventTypes.TryValidate("SalesInvoice.", EntityRegistry.SalesInvoice, out _));
            Assert.False(BusinessEventTypes.TryValidate(".Created", EntityRegistry.SalesInvoice, out _));
            Assert.False(BusinessEventTypes.TryValidate("SalesInvoice.created", EntityRegistry.SalesInvoice, out _));
            Assert.False(BusinessEventTypes.TryValidate("SalesInvoice.Created.Again", EntityRegistry.SalesInvoice, out _));
            Assert.False(BusinessEventTypes.TryValidate("", EntityRegistry.SalesInvoice, out _));
            Assert.False(BusinessEventTypes.TryValidate(null, EntityRegistry.SalesInvoice, out _));

            // A canonical name recorded against the WRONG entity is still rejected.
            Assert.False(BusinessEventTypes.TryValidate("SalesInvoice.Created", EntityRegistry.Customer, out _));
        }
    }
}