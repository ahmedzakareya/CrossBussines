using CrossBuy.BL.Platform;
using CrossBuy.Models.Context.Accounting;
using CrossBuy.Models.Context.Admin;
using CrossBuy.Models.Context.Crm;
using CrossBuy.Models.Context.Inventory;
using CrossBuy.Models.Context.Platform;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace CrossBuy.Tests
{
    // Stage 1 Batch B / B2 — the pilot global query filters.
    //
    // The FIRST test in this file is the one that decides whether the design is sound at all, and it is first
    // deliberately. EF Core caches the model per (context type, provider). A query filter that closes over a
    // per-instance object rather than reading it through the executing DbContext would be compiled ONCE against
    // the FIRST context's holder — and every later request would then be filtered to the first request's
    // company. That failure is silent, it is cross-tenant, and it would look like the filters were working.
    public class Stage1QueryFilterTests
    {
        // =====================================================================================
        // 0. THE MODEL-CACHE TRAP
        // =====================================================================================

        // Two contexts over the SAME database and the same options shape — therefore the same cached model —
        // holding DIFFERENT scopes. Each must see its own company.
        [Fact]
        public async Task Two_contexts_sharing_the_cached_model_are_filtered_by_their_own_scope()
        {
            using var host = new PlatformTestHost(companyId: 1);
            await SeedCustomersAsync(host);

            var scopeTwo = new CompanyScopeHolder();
            scopeTwo.Set(2, null);

            // host.Db was created FIRST, so it is the instance the model would have been built from.
            var seenByOne = await host.Db.Customers.AsNoTracking().Select(c => c.CompanyID).ToListAsync();

            using var contextTwo = host.NewContext(scopeTwo);
            var seenByTwo = await contextTwo.Customers.AsNoTracking().Select(c => c.CompanyID).ToListAsync();

            Assert.All(seenByOne, c => Assert.Equal(1, c));
            Assert.NotEmpty(seenByOne);

            // If the filter captured the first holder, this would return company 1's rows.
            Assert.All(seenByTwo, c => Assert.Equal(2, c));
            Assert.NotEmpty(seenByTwo);
        }

        // The same trap in the other direction: a context created BEFORE another company's scope is even
        // resolved must still honour the resolution that happens afterwards. Batch A's factory (and the
        // storefront's pin) both resolve the company AFTER the DbContext exists, so a filter that read the
        // holder once at construction time would be wrong for every request.
        [Fact]
        public async Task A_scope_resolved_after_the_context_was_created_still_filters_correctly()
        {
            using var host = new PlatformTestHost(companyId: null);   // nothing resolved yet
            await SeedCustomersAsync(host);

            // An unresolved scope reads nothing — fail closed.
            Assert.Empty(await host.Db.Customers.AsNoTracking().ToListAsync());

            // ...and the moment a company is resolved, the SAME context instance is filtered to it.
            host.Holder.Set(2, null);
            var seen = await host.Db.Customers.AsNoTracking().Select(c => c.CompanyID).ToListAsync();
            Assert.NotEmpty(seen);
            Assert.All(seen, c => Assert.Equal(2, c));
        }

        // =====================================================================================
        // 1. THE TWELVE ARE FILTERED
        // =====================================================================================

        // Every pilot entity, one company's rows in and another's out, through the real model. Written as one
        // test per entity via a Theory so a failure names the entity rather than the batch.
        [Theory]
        [InlineData(nameof(JournalEntry))]
        [InlineData(nameof(SalesInvoice))]
        [InlineData(nameof(PurchaseInvoice))]
        [InlineData(nameof(Customer))]
        [InlineData(nameof(Item))]
        [InlineData(nameof(Warehouse))]
        [InlineData(nameof(Quotation))]
        [InlineData(nameof(Lead))]
        [InlineData(nameof(Opportunity))]
        [InlineData(nameof(CrmAccount))]
        [InlineData(nameof(BusinessEvent))]
        [InlineData(nameof(Notification))]
        public async Task Each_pilot_entity_reads_only_the_scopes_company(string entity)
        {
            using var host = new PlatformTestHost(companyId: 1);
            await SeedOneRowPerCompanyAsync(host, entity);

            var mine = await CountAsync(host.Db, entity);
            Assert.Equal(1, mine);          // company 1's row only — company 2's is filtered out

            var scopeTwo = new CompanyScopeHolder();
            scopeTwo.Set(2, null);
            using var contextTwo = host.NewContext(scopeTwo);
            Assert.Equal(1, await CountAsync(contextTwo, entity));

            // ...and the two are genuinely different rows: unfiltered, there are two.
            using (host.HostBypass().Begin(CompanyBypassKind.CrossCompanyAdministration, AdminContext(), "verify"))
            {
                Assert.Equal(2, await CountAsync(host.Db, entity));
            }
        }

        // The filter must be declared on the EF model itself, not merely produce the right answer for the
        // queries this file happens to write. Asserted against the model so a removed filter fails here even if
        // no other test reads that entity.
        [Fact]
        public void All_twelve_pilot_entities_carry_a_query_filter_on_the_model()
        {
            using var host = new PlatformTestHost();
            var model = host.Db.Model;

            var filtered = model.GetEntityTypes()
                .Where(t => t.GetQueryFilter() != null)
                .Select(t => t.ClrType.Name)
                .ToHashSet(StringComparer.Ordinal);

            foreach (var entity in CompanyQueryFilters.PilotEntities)
                Assert.Contains(entity, filtered);

            Assert.Equal(CompanyQueryFilters.PilotEntities.Length, filtered.Count);
        }

        // The other half of the same claim, and the one that matters for honesty: nothing OUTSIDE the pilot
        // acquired a filter. B1 excluded each of these for a stated reason, and a filter appearing on one of
        // them would break authorization, the stock lock, or the dispatch queue.
        [Fact]
        public void No_entity_outside_the_pilot_is_filtered()
        {
            using var host = new PlatformTestHost();
            var filtered = host.Db.Model.GetEntityTypes()
                .Where(t => t.GetQueryFilter() != null)
                .Select(t => t.ClrType.Name)
                .ToHashSet(StringComparer.Ordinal);

            foreach (var (entity, reason) in CompanyQueryFilters.DeliberatelyUnfiltered)
                Assert.False(filtered.Contains(entity), $"{entity} must NOT be filtered: {reason}");
        }

        // =====================================================================================
        // 2. THE UNRESOLVED SCOPE FAILS CLOSED
        // =====================================================================================

        // A request that could not establish who it is reads NOTHING from the pilot entities, rather than
        // everything. This is the read-side counterpart of Batch A deleting the company-1 fallback.
        [Fact]
        public async Task An_unresolved_scope_reads_nothing_rather_than_everything()
        {
            using var host = new PlatformTestHost(companyId: null);
            await SeedCustomersAsync(host);
            host.Seed.Items.Add(NewItem(1));
            host.Seed.JournalEntries.Add(NewJournalEntry(1));
            await host.Seed.SaveChangesAsync();

            Assert.False(host.Holder.IsResolved);
            Assert.Empty(await host.Db.Customers.AsNoTracking().ToListAsync());
            Assert.Empty(await host.Db.Items.AsNoTracking().ToListAsync());
            Assert.Empty(await host.Db.JournalEntries.AsNoTracking().ToListAsync());
        }

        // A FILTER IS A READ CONTROL. It is stated as a test because "the filter protects the data" would
        // otherwise be read as covering writes — it does not, and nothing in EF makes it.
        //
        // Written during B2, this test asserted that the cross-company INSERT succeeded, and it documented the gap
        // as B4's. B4 has since closed it, so the assertion is now the refusal — deliberately kept here, next to
        // the filters, so nobody reads B2 as having secured the write path. The depth is in Stage1WriteGuardTests.
        [Fact]
        public async Task A_filter_alone_does_not_stop_a_cross_company_write_so_B4_guards_it()
        {
            using var host = new PlatformTestHost(companyId: 1);

            // Nothing in the FILTER objects to this: no query runs on the write path, so the predicate is never
            // consulted. What refuses it is B4's SaveChanges guard.
            host.Db.Customers.Add(new Customer { CompanyID = 2, Name = "another company's customer" });

            await Assert.ThrowsAsync<CompanyWriteDeniedException>(() => host.Db.SaveChangesAsync());

            // ...and the row was never written, checked from a context that could see it if it had been.
            host.Db.ChangeTracker.Clear();
            using var all = host.AllCompanies();
            Assert.Empty(await all.Customers.AsNoTracking().Where(c => c.CompanyID == 2).ToListAsync());
        }

        // =====================================================================================
        // 3. THE STOREFRONT READS ONE COMPANY THROUGH THE SAME FILTER
        // =====================================================================================

        // The public catalogue is not an exception to the filter — it is a caller of it. PublicCompanyRead pins
        // the scope and leaves the filter fully in force, so the anonymous storefront reads exactly the
        // configured company's items and cannot see another's.
        [Fact]
        public async Task The_anonymous_storefront_reads_exactly_the_configured_company()
        {
            using var host = new PlatformTestHost(companyId: null);   // anonymous: nothing resolved
            host.Seed.Items.AddRange(NewItem(1), NewItem(2), NewItem(3));
            await host.Seed.SaveChangesAsync();

            Assert.Empty(await host.Db.Items.AsNoTracking().ToListAsync());   // no scope, no catalogue

            var storefront = host.Bypass(host.Holder, new PublicCatalogOptions { StoreCompanyId = 2 });
            using (storefront.BeginPublicCatalogRead("anonymous storefront"))
            {
                var items = await host.Db.Items.AsNoTracking().Select(i => i.CompanyID).ToListAsync();
                Assert.Equal(new[] { 2 }, items);          // ONE company, and it is the configured one
                Assert.False(host.Holder.AllowsCrossCompany);
            }
        }

        // =====================================================================================
        // 4. THE FOUR BYPASSED FLOWS STILL WORK WITH THE FILTERS ON
        // =====================================================================================

        // B3 proved the bypass grants the right; this proves the flow now works THROUGH a filtered context —
        // which is the claim B3 explicitly could not make because no filter existed yet.
        [Fact]
        public async Task The_dispatch_worker_flow_loads_every_companys_event_through_a_filtered_context()
        {
            using var host = new PlatformTestHost(companyId: 1);
            var events = new[]
            {
                await AddEventAsync(host, companyId: 1),
                await AddEventAsync(host, companyId: 2),
                await AddEventAsync(host, companyId: 3),
            };

            // Without the bypass the worker's by-id load returns null for companies 2 and 3 — it would mark two
            // healthy rows Failed. This is the Critical finding B1 raised, now demonstrable.
            foreach (var id in events.Skip(1))
                Assert.Null(await host.Db.BusinessEvents.AsNoTracking().FirstOrDefaultAsync(e => e.EventId == id));

            using (host.HostBypass().BeginPlatformDispatch("outbox pass"))
            {
                foreach (var id in events)
                    Assert.NotNull(await host.Db.BusinessEvents.AsNoTracking().FirstOrDefaultAsync(e => e.EventId == id));
            }
        }

        // The dedup guard, through a filtered context. Without the bypass the existing row is invisible and the
        // consumer would send a DUPLICATE notification; with it, the duplicate is prevented.
        [Fact]
        public async Task The_notification_dedup_guard_sees_another_companys_row_only_under_the_bypass()
        {
            using var host = new PlatformTestHost(companyId: 1);
            const int recipient = 501;
            const string dedupKey = "evt:abc:501";
            host.Seed.Notifications.Add(new Notification
            {
                RecipientEmployeeID = recipient, CompanyID = 2, Type = "purchase_invoice",
                DedupKey = dedupKey, IsRead = true, CreatedAt = DateTime.UtcNow,
            });
            await host.Seed.SaveChangesAsync();

            Task<bool> AlreadySentAsync() => host.Db.Notifications.AsNoTracking()
                .AnyAsync(n => n.RecipientEmployeeID == recipient && n.DedupKey == dedupKey);

            Assert.False(await AlreadySentAsync());        // filtered out -> the duplicate B1 predicted
            using (host.HostBypass().BeginPlatformDispatch("notification projection dedup"))
            {
                Assert.True(await AlreadySentAsync());     // ...prevented by the bypass
            }
        }

        // The queue itself is NOT filtered, so a company-1 scope still claims every company's work. If
        // BusinessEventDispatch had been filtered, one company's scope would stall every other company's fan-out.
        [Fact]
        public async Task The_dispatch_queue_is_not_filtered_so_one_scope_claims_every_companys_work()
        {
            using var host = new PlatformTestHost(companyId: 1);
            await AddEventAsync(host, companyId: 1, withDispatch: true);
            await AddEventAsync(host, companyId: 2, withDispatch: true);
            await AddEventAsync(host, companyId: 3, withDispatch: true);

            // No bypass at all: the QUEUE is readable because it carries no filter.
            var pending = await host.Db.BusinessEventDispatches.AsNoTracking().CountAsync();
            Assert.Equal(3, pending);
        }

        // =====================================================================================
        // 5. THE FILTER CANNOT BE WIDENED BY THE REQUEST
        // =====================================================================================

        // A hand-written `.Where(x => x.CompanyID == 2)` inside a company-1 scope returns NOTHING rather than
        // company 2's rows. This is the shape of the AccountingApiController tampering vector — `?companyId=2`
        // reaching a query predicate — and the filter now composes with it as an AND.
        [Fact]
        public async Task A_request_supplied_company_id_cannot_widen_a_scoped_read()
        {
            using var host = new PlatformTestHost(companyId: 1);
            await SeedCustomersAsync(host);

            int tamperedCompanyId = 2;
            var rows = await host.Db.Customers.AsNoTracking()
                .Where(c => c.CompanyID == tamperedCompanyId)
                .ToListAsync();

            Assert.Empty(rows);
        }

        // Include() of an unfiltered child through a filtered parent: the parent filter still applies, so the
        // child cannot be reached by navigating from another company's header. Child entities are
        // CompanyScopedIndirect by design (B1 §2.4) — isolated BY their parent, and this is that claim tested.
        [Fact]
        public async Task An_indirect_child_is_isolated_by_its_filtered_parent()
        {
            using var host = new PlatformTestHost(companyId: 1);
            var mine = NewSalesInvoice(1);
            var theirs = NewSalesInvoice(2);
            host.Seed.SalesInvoices.AddRange(mine, theirs);
            await host.Seed.SaveChangesAsync();

            host.Seed.SalesInvoiceLines.AddRange(
                new SalesInvoiceLine { SalesInvoiceId = mine.ID, LineNo = 1, ItemId = 1, Qty = 1, UnitPrice = 10, LineTotal = 10, RevenueAccountId = 1 },
                new SalesInvoiceLine { SalesInvoiceId = theirs.ID, LineNo = 1, ItemId = 1, Qty = 1, UnitPrice = 10, LineTotal = 10, RevenueAccountId = 1 });
            await host.Seed.SaveChangesAsync();

            var headers = await host.Db.SalesInvoices.AsNoTracking().Include(i => i.Lines).ToListAsync();
            Assert.Single(headers);
            Assert.Equal(1, headers[0].CompanyID);

            // The child table itself is NOT filtered — stated rather than implied. Reaching a line requires
            // knowing its id directly; it is not reachable by navigation from another company's header.
            Assert.Equal(2, await host.Db.SalesInvoiceLines.AsNoTracking().CountAsync());
        }

        // ---- helpers --------------------------------------------------------------------------------------

        private static BusinessContext AdminContext() => new()
        {
            CompanyId = 1, EmployeeId = 7, UserId = "user-7", Roles = new[] { "PlatformOps" },
            CorrelationId = Guid.NewGuid(),
        };

        private static async Task SeedCustomersAsync(PlatformTestHost host)
        {
            // B4: seeding spans companies, so it uses the authorized arrangement context. Every ASSERTION below
            // still reads through the filtered host.Db — the arrangement is what changed, not what is proven.
            host.Seed.Customers.AddRange(
                new Customer { CompanyID = 1, Name = "c1" },
                new Customer { CompanyID = 2, Name = "c2" });
            await host.Seed.SaveChangesAsync();
        }

        private static Item NewItem(int companyId) => new()
        {
            CompanyID = companyId, ItemCode = "I-" + companyId + "-" + Guid.NewGuid().ToString("N")[..6],
            Barcode = "B-" + companyId + "-" + Guid.NewGuid().ToString("N")[..6],
            Name = "صنف", NameEn = "item", ItemCategoryId = 1, BaseUoMId = 1,
        };

        private static JournalEntry NewJournalEntry(int companyId) => new()
        {
            CompanyID = companyId, EntryNo = "JV-" + companyId + "-" + Guid.NewGuid().ToString("N")[..6],
            EntryDate = new DateTime(2026, 1, 1), FiscalPeriodId = 1, CurrencyId = 1,
            Description = "-", Status = "Posted",
        };

        private static SalesInvoice NewSalesInvoice(int companyId) => new()
        {
            CompanyID = companyId, InvoiceNo = "SI-" + companyId + "-" + Guid.NewGuid().ToString("N")[..6],
            InvoiceDate = new DateTime(2026, 1, 1), Status = "Posted", CustomerId = 1,
        };

        private static async Task<long> AddEventAsync(PlatformTestHost host, int companyId, bool withDispatch = false)
        {
            var ev = new BusinessEvent
            {
                EventUid = Guid.NewGuid(), CompanyID = companyId,
                EntityType = EntityRegistry.PurchaseInvoice, EntityId = 1,
                EventType = PurchaseInvoiceEvents.Created, Visibility = BusinessEventVisibility.Internal,
                PayloadVersion = PurchaseInvoiceEventPayload.Version, CreatedAt = DateTime.UtcNow,
                Payload = "{\"invoiceNumber\":\"PV-1\",\"totalAfter\":1.0}",
            };
            host.Seed.BusinessEvents.Add(ev);
            await host.Seed.SaveChangesAsync();

            if (withDispatch)
            {
                host.Seed.BusinessEventDispatches.Add(new BusinessEventDispatch
                {
                    EventId = ev.EventId, Consumer = BusinessEventConsumers.NotificationProjection,
                    Status = BusinessEventDispatchStatus.Pending, Attempts = 0,
                });
                await host.Seed.SaveChangesAsync();
            }
            return ev.EventId;
        }

        // One row for company 1 and one for company 2, for whichever pilot entity the Theory names.
        private static async Task SeedOneRowPerCompanyAsync(PlatformTestHost host, string entity)
        {
            foreach (var companyId in new[] { 1, 2 })
            {
                switch (entity)
                {
                    case nameof(JournalEntry): host.Seed.JournalEntries.Add(NewJournalEntry(companyId)); break;
                    case nameof(SalesInvoice): host.Seed.SalesInvoices.Add(NewSalesInvoice(companyId)); break;
                    case nameof(PurchaseInvoice):
                        host.Seed.PurchaseInvoices.Add(new PurchaseInvoice
                        {
                            CompanyID = companyId, InvoiceNo = "PV-" + companyId, InvoiceDate = new DateTime(2026, 1, 1),
                            Status = "Posted", VendorId = 1,
                        });
                        break;
                    case nameof(Customer): host.Seed.Customers.Add(new Customer { CompanyID = companyId, Name = "c" + companyId }); break;
                    case nameof(Item): host.Seed.Items.Add(NewItem(companyId)); break;
                    case nameof(Warehouse):
                        host.Seed.Warehouses.Add(new Warehouse
                        {
                            CompanyID = companyId, Code = "W" + companyId, Name = "مخزن", NameEn = "wh",
                        });
                        break;
                    case nameof(Quotation):
                        host.Seed.Quotations.Add(new Quotation
                        {
                            CompanyID = companyId, QuoteNo = "Q-" + companyId, QuoteDate = new DateTime(2026, 1, 1),
                            CustomerId = 1, Status = "Draft",
                        });
                        break;
                    case nameof(Lead):
                        host.Seed.Leads.Add(new Lead { CompanyID = companyId, Name = "l" + companyId, Status = "New" });
                        break;
                    case nameof(Opportunity):
                        host.Seed.Opportunities.Add(new Opportunity
                        {
                            CompanyID = companyId, Title = "o" + companyId, Stage = "Qualification",
                        });
                        break;
                    case nameof(CrmAccount):
                        host.Seed.CrmAccounts.Add(new CrmAccount { CompanyID = companyId, Name = "a" + companyId });
                        break;
                    case nameof(BusinessEvent): await AddEventAsync(host, companyId); continue;
                    case nameof(Notification):
                        host.Seed.Notifications.Add(new Notification
                        {
                            RecipientEmployeeID = companyId, CompanyID = companyId, Type = "purchase_invoice",
                            IsRead = false, CreatedAt = DateTime.UtcNow,
                        });
                        break;
                    default: throw new InvalidOperationException("Unhandled pilot entity " + entity);
                }
            }
            await host.Seed.SaveChangesAsync();
        }

        private static Task<int> CountAsync(CrossBuy.Models.Context.CrossDbContext db, string entity) => entity switch
        {
            nameof(JournalEntry) => db.JournalEntries.AsNoTracking().CountAsync(),
            nameof(SalesInvoice) => db.SalesInvoices.AsNoTracking().CountAsync(),
            nameof(PurchaseInvoice) => db.PurchaseInvoices.AsNoTracking().CountAsync(),
            nameof(Customer) => db.Customers.AsNoTracking().CountAsync(),
            nameof(Item) => db.Items.AsNoTracking().CountAsync(),
            nameof(Warehouse) => db.Warehouses.AsNoTracking().CountAsync(),
            nameof(Quotation) => db.Quotations.AsNoTracking().CountAsync(),
            nameof(Lead) => db.Leads.AsNoTracking().CountAsync(),
            nameof(Opportunity) => db.Opportunities.AsNoTracking().CountAsync(),
            nameof(CrmAccount) => db.CrmAccounts.AsNoTracking().CountAsync(),
            nameof(BusinessEvent) => db.BusinessEvents.AsNoTracking().CountAsync(),
            nameof(Notification) => db.Notifications.AsNoTracking().CountAsync(),
            _ => throw new InvalidOperationException("Unhandled pilot entity " + entity),
        };
    }
}
