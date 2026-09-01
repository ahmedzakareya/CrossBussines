using CrossBuy.BL.Platform;
using CrossBuy.Models.Context.Accounting;
using CrossBuy.Models.Context.Admin;
using CrossBuy.Models.Context.Inventory;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CrossBuy.Tests
{
    // Stage 1 Batch B / B4 — write-side company enforcement.
    //
    // B2 closed the read side. This file is about the half a read filter cannot reach: a filter makes another
    // company's rows invisible, and invisible is not immutable. Every test here would PASS trivially with the
    // filters alone, which is precisely why B4 is a separate batch and not a footnote to B2.
    public class Stage1WriteGuardTests
    {
        private static BusinessContext Admin() => new()
        {
            CompanyId = 1, EmployeeId = 7, UserId = "user-7", Roles = new[] { "PlatformOps" },
            CorrelationId = Guid.NewGuid(),
        };

        // =====================================================================================
        // 1. INSERT INTO ANOTHER COMPANY IS REFUSED
        // =====================================================================================

        // The vector: a company id arriving from a request, a view model or a route, assigned straight onto a new
        // entity. The scope — not the payload — decides where a write lands.
        [Fact]
        public async Task An_insert_into_another_company_is_refused()
        {
            using var host = new PlatformTestHost(companyId: 1);
            host.Db.Customers.Add(new Customer { CompanyID = 2, Name = "another company's customer" });

            var denied = await Assert.ThrowsAsync<CompanyWriteDeniedException>(() => host.Db.SaveChangesAsync());
            Assert.Contains("operates as company 1", denied.Message);
            Assert.Contains("names company 2", denied.Message);

            // Nothing was written — a refusal must not half-apply.
            host.Db.ChangeTracker.Clear();
            using var all = host.AllCompanies();
            Assert.Empty(await all.Customers.AsNoTracking().ToListAsync());
        }

        // The same guard on every pilot entity, not just the one that was convenient to test.
        [Theory]
        [InlineData(nameof(JournalEntry))]
        [InlineData(nameof(SalesInvoice))]
        [InlineData(nameof(PurchaseInvoice))]
        [InlineData(nameof(Customer))]
        [InlineData(nameof(Item))]
        [InlineData(nameof(Warehouse))]
        [InlineData(nameof(Quotation))]
        [InlineData(nameof(Notification))]
        public async Task Every_pilot_entity_refuses_an_insert_into_another_company(string entity)
        {
            using var host = new PlatformTestHost(companyId: 1);
            Add(host, entity, companyId: 2);

            await Assert.ThrowsAsync<CompanyWriteDeniedException>(() => host.Db.SaveChangesAsync());
        }

        // ...and the same insert into the scope's OWN company succeeds. A guard that refuses everything is not a
        // guard, and this is the assertion that would fail if the rule were "refuse any explicit CompanyID".
        [Fact]
        public async Task An_insert_into_the_scopes_own_company_succeeds()
        {
            using var host = new PlatformTestHost(companyId: 1);
            host.Db.Customers.Add(new Customer { CompanyID = 1, Name = "my customer" });
            await host.Db.SaveChangesAsync();

            Assert.Single(await host.Db.Customers.AsNoTracking().ToListAsync());
        }

        // =====================================================================================
        // 2. UPDATE / DELETE OF ANOTHER COMPANY'S ROW IS REFUSED
        // =====================================================================================

        // The attack a read filter cannot see: never query the row at all. Attaching a stub with a known id and
        // flipping the state to Modified issues an UPDATE without a SELECT, so no filter is ever consulted.
        [Fact]
        public async Task Updating_another_companys_row_by_attaching_a_stub_is_refused()
        {
            using var host = new PlatformTestHost(companyId: 1);
            int theirId = await SeedOtherCompanyCustomerAsync(host);

            // No query — this is the point.
            var stub = new Customer { ID = theirId, CompanyID = 2, Name = "renamed by another company" };
            host.Db.Customers.Attach(stub);
            host.Db.Entry(stub).Property(c => c.Name).IsModified = true;

            var denied = await Assert.ThrowsAsync<CompanyWriteDeniedException>(() => host.Db.SaveChangesAsync());
            Assert.Contains("may not modify", denied.Message);

            host.Db.ChangeTracker.Clear();
            using var all = host.AllCompanies();
            Assert.Equal("their customer", (await all.Customers.AsNoTracking().SingleAsync(c => c.ID == theirId)).Name);
        }

        [Fact]
        public async Task Deleting_another_companys_row_is_refused()
        {
            using var host = new PlatformTestHost(companyId: 1);
            int theirId = await SeedOtherCompanyCustomerAsync(host);

            host.Db.Customers.Remove(new Customer { ID = theirId, CompanyID = 2, Name = "their customer" });

            var denied = await Assert.ThrowsAsync<CompanyWriteDeniedException>(() => host.Db.SaveChangesAsync());
            Assert.Contains("may not delete", denied.Message);

            host.Db.ChangeTracker.Clear();
            using var all = host.AllCompanies();
            Assert.NotNull(await all.Customers.AsNoTracking().SingleOrDefaultAsync(c => c.ID == theirId));
        }

        // Claiming ownership by assigning your own company id before saving must NOT work. The guard reads the
        // ORIGINAL value, so a caller cannot "become" the owner on the way to the database.
        [Fact]
        public async Task A_caller_cannot_claim_another_companys_row_by_assigning_its_own_company_id()
        {
            using var host = new PlatformTestHost(companyId: 1);
            int theirId = await SeedOtherCompanyCustomerAsync(host);

            var stub = new Customer { ID = theirId, CompanyID = 2, Name = "their customer" };
            host.Db.Customers.Attach(stub);
            stub.CompanyID = 1;                       // "it's mine now"

            await Assert.ThrowsAsync<CompanyWriteDeniedException>(() => host.Db.SaveChangesAsync());
        }

        // ...and the mirror image: a row that IS mine cannot be pushed into another company.
        [Fact]
        public async Task My_own_row_cannot_be_moved_into_another_company()
        {
            using var host = new PlatformTestHost(companyId: 1);
            var mine = new Customer { CompanyID = 1, Name = "mine" };
            host.Db.Customers.Add(mine);
            await host.Db.SaveChangesAsync();

            mine.CompanyID = 2;
            var denied = await Assert.ThrowsAsync<CompanyWriteDeniedException>(() => host.Db.SaveChangesAsync());
            Assert.Contains("move to another company", denied.Message);
        }

        // =====================================================================================
        // 3. AN UNRESOLVED SCOPE MAY NOT WRITE
        // =====================================================================================

        // The write-side counterpart of B2's fail-closed read. A scope that cannot say who it is may not create
        // business records — and the message says why rather than surfacing an FK error later.
        [Fact]
        public async Task An_unresolved_scope_cannot_write_a_company_scoped_entity()
        {
            using var host = new PlatformTestHost(companyId: null);
            host.Db.Customers.Add(new Customer { CompanyID = 1, Name = "from nowhere" });

            var denied = await Assert.ThrowsAsync<CompanyWriteDeniedException>(() => host.Db.SaveChangesAsync());
            Assert.Contains("no resolved company", denied.Message);
        }

        [Fact]
        public async Task An_unresolved_scope_cannot_write_an_entity_with_no_company_either()
        {
            using var host = new PlatformTestHost(companyId: null);
            host.Db.Customers.Add(new Customer { Name = "no company at all" });

            var denied = await Assert.ThrowsAsync<CompanyWriteDeniedException>(() => host.Db.SaveChangesAsync());
            Assert.Contains("There is no default company", denied.Message);
        }

        // =====================================================================================
        // 4. A MISSING COMPANY IS STAMPED, NOT INVENTED — AND NOT SILENT
        // =====================================================================================

        // A pilot insert that forgot CompanyID would otherwise write CompanyID = 0: an orphan no company can read
        // and no filter can place. It is stamped from the scope (never from a constant) and logged as a Warning, so
        // the omission is fixable at its source rather than becoming invisible.
        [Fact]
        public async Task An_insert_with_no_company_is_stamped_from_the_scope()
        {
            using var host = new PlatformTestHost(companyId: 3);
            host.Db.Customers.Add(new Customer { Name = "forgot the company" });   // CompanyID defaults to 0
            await host.Db.SaveChangesAsync();

            var saved = await host.Db.Customers.AsNoTracking().SingleAsync();
            Assert.Equal(3, saved.CompanyID);        // the SCOPE's company — not 1, and not 0
        }

        // Notification.CompanyID is nullable, so "no company" is reachable without a 0. It is stamped for the same
        // reason: a NULL row is the unattributed case NotificationCompanyPolicy refuses to guess about, and B4 is
        // where the source of those rows is closed rather than tracked.
        [Fact]
        public async Task A_notification_saved_without_a_company_is_stamped_rather_than_left_null()
        {
            using var host = new PlatformTestHost(companyId: 4);
            host.Db.Notifications.Add(new Notification
            {
                RecipientEmployeeID = 9, Type = "purchase_invoice", IsRead = false, CreatedAt = DateTime.UtcNow,
                CompanyID = null,
            });
            await host.Db.SaveChangesAsync();

            using var all = host.AllCompanies();
            Assert.Equal(4, (await all.Notifications.AsNoTracking().SingleAsync()).CompanyID);
        }

        // =====================================================================================
        // 5. THE BYPASS KINDS ARE NOT INTERCHANGEABLE FOR WRITING
        // =====================================================================================

        // ADR-023 recorded this as an OPEN item: "the bypass is read-widening only; IsReadOnly() is advisory today
        // — nothing enforces it. Open item for B4." This is B4 closing it.
        [Fact]
        public async Task A_read_only_monitoring_bypass_cannot_be_used_to_write()
        {
            using var host = new PlatformTestHost(companyId: 1);
            using var _ = host.HostBypass().Begin(
                CompanyBypassKind.PlatformMonitoring, Admin(), "inspecting the event stream");

            host.Db.Customers.Add(new Customer { CompanyID = 2, Name = "written while observing" });

            var denied = await Assert.ThrowsAsync<CompanyWriteDeniedException>(() => host.Db.SaveChangesAsync());
            Assert.Contains("read-only", denied.Message);
        }

        // The anonymous storefront must not be a write path under any circumstances.
        [Fact]
        public async Task The_public_catalogue_scope_cannot_be_used_to_write()
        {
            using var host = new PlatformTestHost(companyId: null);
            using var _ = host.Bypass(host.Holder, new PublicCatalogOptions { StoreCompanyId = 1 })
                .BeginPublicCatalogRead("anonymous storefront");

            host.Db.Items.Add(new Item
            {
                CompanyID = 1, ItemCode = "X", Barcode = "X", Name = "written by a visitor",
                ItemCategoryId = 1, BaseUoMId = 1,
            });

            var denied = await Assert.ThrowsAsync<CompanyWriteDeniedException>(() => host.Db.SaveChangesAsync());
            Assert.Contains("read-only", denied.Message);
        }

        // The dispatcher, by contrast, MUST be able to write across companies: the notification projection writes a
        // Notification for a recipient in the EVENT's company, and the dispatcher's scope has no company of its own.
        // PlatformDispatch is cross-company AND not read-only, which is exactly why the guard tests both.
        [Fact]
        public async Task The_dispatch_bypass_may_write_another_companys_notification()
        {
            using var host = new PlatformTestHost(companyId: 1);
            using (host.HostBypass().BeginPlatformDispatch("notification projection"))
            {
                host.Db.Notifications.Add(new Notification
                {
                    RecipientEmployeeID = 9, CompanyID = 2, Type = "purchase_invoice",
                    IsRead = false, CreatedAt = DateTime.UtcNow,
                });
                await host.Db.SaveChangesAsync();
            }

            using var all = host.AllCompanies();
            Assert.Equal(2, (await all.Notifications.AsNoTracking().SingleAsync()).CompanyID);
        }

        // An authorized administrator may genuinely write across companies — otherwise consolidation and
        // cross-company correction would be impossible. The right is held explicitly, and it is audited.
        [Fact]
        public async Task An_authorized_administrator_may_write_across_companies()
        {
            using var host = new PlatformTestHost(companyId: 1);
            using (host.HostBypass().Begin(
                CompanyBypassKind.CrossCompanyAdministration, Admin(), "cross-company correction"))
            {
                host.Db.Customers.Add(new Customer { CompanyID = 2, Name = "created by an administrator" });
                await host.Db.SaveChangesAsync();
            }

            using var all = host.AllCompanies();
            Assert.Equal(2, (await all.Customers.AsNoTracking().SingleAsync()).CompanyID);
        }

        // =====================================================================================
        // 6. WHAT THE GUARD DELIBERATELY LEAVES ALONE
        // =====================================================================================

        // A non-pilot entity is untouched. The guard and the read filter cover exactly the same twelve, so nothing
        // is half-isolated — a write guard without a read filter would be the most confusing possible state.
        [Fact]
        public async Task A_non_pilot_entity_is_not_guarded()
        {
            using var host = new PlatformTestHost(companyId: 1);

            // ItemCategory carries a CompanyID and is CompanyScopedDirect, but it is NOT in the twelve-entity
            // pilot — so neither the read filter nor this guard touches it. That is the honest state of the pilot:
            // 12 entities guarded, 128 further CompanyScopedDirect entities not yet, and B2/B4 claim only the 12.
            host.Db.ItemCategories.Add(new CrossBuy.Models.Context.Inventory.ItemCategory
            {
                CompanyID = 2, Code = "C2", Name = "تصنيف", NameEn = "category",
            });
            await host.Db.SaveChangesAsync();

            Assert.Single(await host.Db.ItemCategories.AsNoTracking().Where(c => c.CompanyID == 2).ToListAsync());
        }

        [Fact]
        public void The_guard_and_the_read_filter_cover_exactly_the_same_entities()
        {
            using var host = new PlatformTestHost();
            var filtered = host.Db.Model.GetEntityTypes()
                .Where(t => t.GetQueryFilter() != null)
                .Select(t => t.ClrType.Name);

            Assert.All(filtered, name => Assert.True(CompanyQueryFilters.IsPilotEntity(name)));
            Assert.All(CompanyQueryFilters.PilotEntities, name => Assert.True(CompanyQueryFilters.IsPilotEntity(name)));
        }

        // ---- helpers --------------------------------------------------------------------------------------

        // Seeds a company-2 row THROUGH an authorized cross-company write, because the guard being tested would
        // (correctly) refuse the shortcut. The arrangement uses the same door production uses.
        private static async Task<int> SeedOtherCompanyCustomerAsync(PlatformTestHost host)
        {
            using var all = host.AllCompanies();
            var theirs = new Customer { CompanyID = 2, Name = "their customer" };
            all.Customers.Add(theirs);
            await all.SaveChangesAsync();
            return theirs.ID;
        }

        private static void Add(PlatformTestHost host, string entity, int companyId)
        {
            switch (entity)
            {
                case nameof(JournalEntry):
                    host.Db.JournalEntries.Add(new JournalEntry
                    {
                        CompanyID = companyId, EntryNo = "JV-X", EntryDate = new DateTime(2026, 1, 1),
                        FiscalPeriodId = 1, CurrencyId = 1, Status = "Draft",
                    });
                    break;
                case nameof(SalesInvoice):
                    host.Db.SalesInvoices.Add(new SalesInvoice
                    {
                        CompanyID = companyId, InvoiceNo = "SI-X", InvoiceDate = new DateTime(2026, 1, 1),
                        CustomerId = 1, Status = "Draft",
                    });
                    break;
                case nameof(PurchaseInvoice):
                    host.Db.PurchaseInvoices.Add(new PurchaseInvoice
                    {
                        CompanyID = companyId, InvoiceNo = "PV-X", InvoiceDate = new DateTime(2026, 1, 1),
                        VendorId = 1, Status = "Draft",
                    });
                    break;
                case nameof(Customer):
                    host.Db.Customers.Add(new Customer { CompanyID = companyId, Name = "c" });
                    break;
                case nameof(Item):
                    host.Db.Items.Add(new Item
                    {
                        CompanyID = companyId, ItemCode = "I-X", Barcode = "B-X", Name = "item",
                        ItemCategoryId = 1, BaseUoMId = 1,
                    });
                    break;
                case nameof(Warehouse):
                    host.Db.Warehouses.Add(new Warehouse
                    {
                        CompanyID = companyId, Code = "W-X", Name = "مخزن", NameEn = "wh",
                    });
                    break;
                case nameof(Quotation):
                    host.Db.Quotations.Add(new Quotation
                    {
                        CompanyID = companyId, QuoteNo = "Q-X", QuoteDate = new DateTime(2026, 1, 1),
                        CustomerId = 1, Status = "Draft",
                    });
                    break;
                case nameof(Notification):
                    host.Db.Notifications.Add(new Notification
                    {
                        RecipientEmployeeID = 1, CompanyID = companyId, Type = "purchase_invoice",
                        IsRead = false, CreatedAt = DateTime.UtcNow,
                    });
                    break;
                default: throw new InvalidOperationException("Unhandled entity " + entity);
            }
        }
    }
}
