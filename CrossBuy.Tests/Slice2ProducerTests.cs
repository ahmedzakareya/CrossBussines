using CrossBuy.BL;
using CrossBuy.BL.Platform;
using CrossBuy.Models.Context.Inventory;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CrossBuy.Tests
{
    // Platform Kernel slice 2 — event producers for Customer, Purchase Invoice and Work Order.
    //
    // These exercise IBusinessEventService directly with each entity's real payload, plus the transaction
    // rules, rather than standing up ReceivableService / PayableService / ManufService with their full
    // dependency graphs (currency, journals, stock, chart of accounts, seeded GL accounts). The producers'
    // own placement — inside the ScopedTx, before CommitAsync — is verified by the transaction tests below
    // and by the compile-time fact that RecordAsync refuses to run without an ambient transaction.
    public class Slice2ProducerTests
    {
        // ---- Test 6: Customer.Created is recorded atomically ----
        [Fact]
        public async Task Customer_Created_is_recorded_atomically_with_a_safe_payload()
        {
            using var host = new PlatformTestHost();
            var events = host.Events();

            long eventId;
            await using (var tx = await ScopedTx.BeginOrJoinAsync(host.Db))
            {
                eventId = await events.RecordAsync(new BusinessEventRecord
                {
                    EntityCode = EntityRegistry.Customer,
                    EntityId = 11,
                    EventType = CustomerEvents.Created,
                    PayloadVersion = CustomerEventPayload.Version,
                    Visibility = BusinessEventVisibility.Internal,
                    DedupKey = "Customer.Created:11",
                    Payload = new CustomerEventPayload
                    {
                        CustomerCode = "TX-999", CustomerName = "عميل الاختبار",
                        CustomerType = "Wholesale", InitialStatus = "Active", NewStatus = "Active",
                    },
                });
                await tx.CommitAsync();
            }

            using var verify = host.NewContext();
            var stored = await verify.BusinessEvents.SingleAsync();
            Assert.Equal(EntityRegistry.Customer, stored.EntityType);
            Assert.Equal(CustomerEvents.Created, stored.EventType);
            Assert.Equal(BusinessEventVisibility.Internal, stored.Visibility);

            // The payload carries identity and classification only — never private contact data.
            Assert.Contains("TX-999", stored.Payload);
            Assert.DoesNotContain("phone", stored.Payload!, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("email", stored.Payload!, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("address", stored.Payload!, StringComparison.OrdinalIgnoreCase);

            // One dispatch row per registered consumer — both of them, in the same transaction.
            var dispatches = await verify.BusinessEventDispatches.Where(d => d.EventId == eventId).ToListAsync();
            Assert.Equal(BusinessEventConsumers.Registered.Length, dispatches.Count);
            Assert.Contains(dispatches, d => d.Consumer == BusinessEventConsumers.TimelineProjection);
            Assert.Contains(dispatches, d => d.Consumer == BusinessEventConsumers.NotificationProjection);
        }

        // ---- Test 7: Customer.Updated carries safe changedFields ----
        [Fact]
        public async Task Customer_Updated_carries_field_names_and_the_status_flip_only()
        {
            using var host = new PlatformTestHost();
            var events = host.Events();

            await using var tx = await ScopedTx.BeginOrJoinAsync(host.Db);
            var eventId = await events.RecordAsync(new BusinessEventRecord
            {
                EntityCode = EntityRegistry.Customer,
                EntityId = 11,
                EventType = CustomerEvents.Updated,
                PayloadVersion = CustomerEventPayload.Version,
                Visibility = BusinessEventVisibility.Internal,
                Payload = new CustomerEventPayload
                {
                    CustomerCode = "TX-999", CustomerName = "عميل الاختبار",
                    // Phone/Email appear as FIELD NAMES only — proving the summary can report that private
                    // fields changed without carrying their values.
                    ChangedFields = new[] { "Phone", "Email", "IsActive" },
                    OldStatus = "Active", NewStatus = "Inactive",
                },
            });

            var stored = await host.Db.BusinessEvents.SingleAsync(e => e.EventId == eventId);
            var presentation = TimelineEventPresenter.Present(stored.EventType, stored.PayloadVersion, stored.Payload);

            Assert.Equal("Customer updated", presentation.TitleEn);
            Assert.Contains("Active", presentation.DescriptionEn);
            Assert.Contains("Inactive", presentation.DescriptionEn);
            // Field names are localised for display, and no value ever appears.
            Assert.Contains("Phone", presentation.DescriptionEn);

            await tx.RollbackAsync();
        }

        // ---- Test 8: a customer rollback removes its BusinessEvent ----
        [Fact]
        public async Task Customer_event_is_rolled_back_with_its_transaction()
        {
            using var host = new PlatformTestHost();
            var events = host.Events();

            await using (var tx = await ScopedTx.BeginOrJoinAsync(host.Db))
            {
                host.Db.Customers.Add(new CrossBuy.Models.Context.Accounting.Customer { CompanyID = 1, Name = "Rolled back" });
                await host.Db.SaveChangesAsync();
                var customerId = host.Db.Customers.Single().ID;

                await events.RecordAsync(new BusinessEventRecord
                {
                    EntityCode = EntityRegistry.Customer, EntityId = customerId,
                    EventType = CustomerEvents.Created, Visibility = BusinessEventVisibility.Internal,
                    PayloadVersion = CustomerEventPayload.Version,
                    Payload = new CustomerEventPayload { CustomerName = "Rolled back" },
                });
                await tx.RollbackAsync();
            }

            using var verify = host.NewContext();
            Assert.Equal(0, await verify.Customers.CountAsync());        // the customer did not stand...
            Assert.Equal(0, await verify.BusinessEvents.CountAsync());   // ...and neither did its event
            Assert.Equal(0, await verify.BusinessEventDispatches.CountAsync());
        }

        // ---- Tests 9 + 11: purchase invoice transitions and rollback ----
        [Fact]
        public async Task PurchaseInvoice_produces_Created_and_Updated_with_the_documented_payload()
        {
            using var host = new PlatformTestHost();
            var events = host.Events();

            await using var tx = await ScopedTx.BeginOrJoinAsync(host.Db);

            await events.RecordAsync(new BusinessEventRecord
            {
                EntityCode = EntityRegistry.PurchaseInvoice, EntityId = 21,
                EventType = PurchaseInvoiceEvents.Created,
                PayloadVersion = PurchaseInvoiceEventPayload.Version,
                Visibility = BusinessEventVisibility.Internal,
                DedupKey = "PurchaseInvoice.Created:21",
                Payload = new PurchaseInvoiceEventPayload
                {
                    InvoiceNumber = "PV-2026-00021", SupplierId = 3, SupplierName = "مورّد الاختبار",
                    InvoiceDate = new DateTime(2026, 3, 1), NewStatus = "Posted", TotalAfter = 1500m,
                },
            });

            await events.RecordAsync(new BusinessEventRecord
            {
                EntityCode = EntityRegistry.PurchaseInvoice, EntityId = 21,
                EventType = PurchaseInvoiceEvents.Updated,
                PayloadVersion = PurchaseInvoiceEventPayload.Version,
                Visibility = BusinessEventVisibility.Internal,
                Payload = new PurchaseInvoiceEventPayload
                {
                    InvoiceNumber = "PV-2026-00021", SupplierId = 3, SupplierName = "مورّد الاختبار",
                    ChangedFields = new[] { "Lines", "GrandTotal" },
                    OldStatus = "Posted", NewStatus = "Posted",
                    TotalBefore = 1500m, TotalAfter = 1750m,
                },
            });

            var stored = await host.Db.BusinessEvents.OrderBy(e => e.EventId).ToListAsync();
            Assert.Equal(2, stored.Count);

            var created = TimelineEventPresenter.Present(stored[0].EventType, stored[0].PayloadVersion, stored[0].Payload);
            Assert.Equal("Purchase invoice recorded", created.TitleEn);
            Assert.Contains("PV-2026-00021", created.DescriptionEn);
            Assert.Contains("1,500.00", created.DescriptionEn);

            var updated = TimelineEventPresenter.Present(stored[1].EventType, stored[1].PayloadVersion, stored[1].Payload);
            Assert.Equal("Purchase invoice updated", updated.TitleEn);
            Assert.Contains("1,500.00", updated.DescriptionEn);
            Assert.Contains("1,750.00", updated.DescriptionEn);

            // No invoice line, supplier record or GL entry leaked into the payload.
            Assert.All(stored, e =>
            {
                Assert.DoesNotContain("unitPrice", e.Payload!, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("journal", e.Payload!, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("taxRegNo", e.Payload!, StringComparison.OrdinalIgnoreCase);
            });

            await tx.RollbackAsync();
        }

        // ---- Test 10: no event for a transition that does not exist ----
        [Fact]
        public async Task Events_for_non_existent_transitions_cannot_be_recorded()
        {
            using var host = new PlatformTestHost();
            var events = host.Events();

            await using var tx = await ScopedTx.BeginOrJoinAsync(host.Db);

            // Purchase invoices have no approval and no separate posting step; work orders have no "Started".
            // Those names are not declared anywhere, and the naming validator rejects them if invented ad hoc
            // only when they are malformed — so the real guarantee is that no PRODUCER exists. What IS
            // enforced here is that a canonical-looking name for the WRONG entity is refused.
            await Assert.ThrowsAsync<BusinessEventContractException>(() => events.RecordAsync(new BusinessEventRecord
            {
                EntityCode = EntityRegistry.PurchaseInvoice, EntityId = 21,
                EventType = "SalesInvoice.Created",   // canonical shape, wrong entity
                Visibility = BusinessEventVisibility.Internal,
            }));

            await Assert.ThrowsAsync<BusinessEventContractException>(() => events.RecordAsync(new BusinessEventRecord
            {
                EntityCode = EntityRegistry.ManufWorkOrder, EntityId = 31,
                EventType = "ManufWorkOrderStarted",   // non-canonical
                Visibility = BusinessEventVisibility.Internal,
            }));

            Assert.Equal(0, await host.Db.BusinessEvents.CountAsync());
            await tx.RollbackAsync();
        }

        // ---- Test 12: work-order status transitions create canonical events ----
        [Fact]
        public async Task WorkOrder_lifecycle_transitions_produce_canonical_events_in_order()
        {
            using var host = new PlatformTestHost();
            var events = host.Events();

            var lifecycle = new (string eventType, string? oldStatus, string newStatus, decimal? before, decimal? after)[]
            {
                (ManufWorkOrderEvents.Created,   null,       "Draft",     null, 0m),
                (ManufWorkOrderEvents.Released,  "Draft",    "Released",  0m,   0m),
                (ManufWorkOrderEvents.Produced,  "Released", "Released",  0m,   4m),
                (ManufWorkOrderEvents.Completed, "Released", "Completed", 4m,   10m),
            };

            await using var tx = await ScopedTx.BeginOrJoinAsync(host.Db);
            foreach (var step in lifecycle)
            {
                await events.RecordAsync(new BusinessEventRecord
                {
                    EntityCode = EntityRegistry.ManufWorkOrder, EntityId = 31,
                    EventType = step.eventType,
                    PayloadVersion = ManufWorkOrderEventPayload.Version,
                    Visibility = BusinessEventVisibility.Internal,
                    Payload = new ManufWorkOrderEventPayload
                    {
                        WorkOrderNumber = "WO-00031", ItemId = 5, ItemName = "FG-1 — منتج تام",
                        PlannedQuantity = 10m,
                        CompletedQuantityBefore = step.before, CompletedQuantityAfter = step.after,
                        OldStatus = step.oldStatus, NewStatus = step.newStatus,
                    },
                });
            }

            var stored = await host.Db.BusinessEvents.OrderBy(e => e.EventId).ToListAsync();
            Assert.Equal(lifecycle.Length, stored.Count);
            Assert.Equal(lifecycle.Select(s => s.eventType), stored.Select(e => e.EventType));

            // Every step renders, and the produced step shows real progress.
            var produced = stored.Single(e => e.EventType == ManufWorkOrderEvents.Produced);
            var producedText = TimelineEventPresenter.Present(produced.EventType, produced.PayloadVersion, produced.Payload);
            Assert.Equal("Partial production", producedText.TitleEn);
            Assert.Contains("0.00", producedText.DescriptionEn);
            Assert.Contains("4.00", producedText.DescriptionEn);

            var completed = stored.Single(e => e.EventType == ManufWorkOrderEvents.Completed);
            var completedText = TimelineEventPresenter.Present(completed.EventType, completed.PayloadVersion, completed.Payload);
            Assert.Equal("Work order completed", completedText.TitleEn);

            // No BOM, routing or labour data in any work-order payload.
            Assert.All(stored, e =>
            {
                Assert.DoesNotContain("component", e.Payload!, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("routing", e.Payload!, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("labor", e.Payload!, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("employee", e.Payload!, StringComparison.OrdinalIgnoreCase);
            });

            await tx.RollbackAsync();
        }

        // ---- Test 13: a rejected transition records nothing ----
        [Fact]
        public async Task A_rejected_work_order_transition_records_no_event()
        {
            using var host = new PlatformTestHost();

            // SetStatusAsync's guards run BEFORE any event is recorded, so a rejected transition leaves the
            // log untouched. Reproduced here through the real service, whose dependencies for this path are
            // just the DbContext and the event service.
            var manuf = new ManufService(host.Db, null!, null!, host.Events(), null!);

            host.Db.ManufWorkOrders.Add(new ManufWorkOrder
            {
                CompanyID = 1, ItemId = 5, Qty = 10m, WarehouseId = 1, WoNo = "WO-00031",
                Status = "Completed",   // a completed order refuses every further transition
            });
            await host.Db.SaveChangesAsync();
            var id = host.Db.ManufWorkOrders.Single().ID;

            var (ok, error) = await manuf.SetStatusAsync(1, id, "Released");
            Assert.False(ok);
            Assert.NotNull(error);
            Assert.Equal(0, await host.Db.BusinessEvents.CountAsync());

            // An unsupported status is rejected too — "Started" is not a state this domain has.
            var (ok2, _) = await manuf.SetStatusAsync(1, id, "Started");
            Assert.False(ok2);
            Assert.Equal(0, await host.Db.BusinessEvents.CountAsync());
        }

        [Fact]
        public async Task A_real_work_order_transition_through_the_service_records_one_event()
        {
            using var host = new PlatformTestHost();
            var manuf = new ManufService(host.Db, null!, null!, host.Events(), null!);

            host.Db.ManufWorkOrders.Add(new ManufWorkOrder
            {
                CompanyID = 1, ItemId = 5, Qty = 10m, WarehouseId = 1, WoNo = "WO-00032", Status = "Draft",
            });
            await host.Db.SaveChangesAsync();
            var id = host.Db.ManufWorkOrders.Single().ID;

            var (ok, error) = await manuf.SetStatusAsync(1, id, "Released");
            Assert.True(ok, error);

            using var verify = host.NewContext();
            var stored = await verify.BusinessEvents.SingleAsync();
            Assert.Equal(ManufWorkOrderEvents.Released, stored.EventType);
            Assert.Equal(EntityRegistry.ManufWorkOrder, stored.EntityType);
            Assert.Equal($"ManufWorkOrder.Released:{id}", stored.DedupKey);
            Assert.Contains("\"oldStatus\":\"Draft\"", stored.Payload);
            Assert.Contains("\"newStatus\":\"Released\"", stored.Payload);

            // The service opened its own ScopedTx and committed it, so the row is durable.
            Assert.Equal("Released", (await verify.ManufWorkOrders.SingleAsync()).Status);
        }

        // ---- Test 14: payload stays within the size limit ----
        [Fact]
        public async Task Slice2_payloads_stay_far_inside_the_size_limit()
        {
            using var host = new PlatformTestHost();
            var events = host.Events();

            await using var tx = await ScopedTx.BeginOrJoinAsync(host.Db);

            // A realistic worst case: every optional field populated and a long changed-field list.
            await events.RecordAsync(new BusinessEventRecord
            {
                EntityCode = EntityRegistry.ManufWorkOrder, EntityId = 31,
                EventType = ManufWorkOrderEvents.Updated,
                PayloadVersion = ManufWorkOrderEventPayload.Version,
                Visibility = BusinessEventVisibility.Internal,
                Payload = new ManufWorkOrderEventPayload
                {
                    WorkOrderNumber = "WO-00031", ItemId = 5,
                    ItemName = new string('م', 200),
                    PlannedQuantity = 10m, CompletedQuantityBefore = 0m, CompletedQuantityAfter = 10m,
                    OldStatus = "Released", NewStatus = "Completed",
                    PlannedStartDate = new DateTime(2026, 1, 1), PlannedEndDate = new DateTime(2026, 2, 1),
                    ChangedFields = Enumerable.Range(0, 40).Select(i => "Field" + i).ToArray(),
                },
            });

            var stored = await host.Db.BusinessEvents.SingleAsync();
            var bytes = System.Text.Encoding.UTF8.GetByteCount(stored.Payload!);
            Assert.True(bytes < BusinessEventService.MaxPayloadBytes,
                $"worst-case work-order payload was {bytes} bytes");
            // A summary payload should be a small fraction of the cap, not merely under it.
            Assert.True(bytes < BusinessEventService.MaxPayloadBytes / 10,
                $"payload grew to {bytes} bytes — check nothing entity-shaped crept in");

            await tx.RollbackAsync();
        }
    }
}