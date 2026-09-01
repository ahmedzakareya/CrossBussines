using CrossBuy.BL;
using CrossBuy.BL.Platform;
using CrossBuy.Models.Context.Accounting;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CrossBuy.Tests
{
    public class BusinessEventServiceTests
    {
        private static BusinessEventRecord Created(int invoiceId = 5, string? dedupKey = null, object? payload = null) => new()
        {
            EntityCode = EntityRegistry.SalesInvoice,
            EntityId = invoiceId,
            EventType = SalesInvoiceEvents.Created,
            Visibility = BusinessEventVisibility.Internal,
            DedupKey = dedupKey,
            Payload = payload ?? new SalesInvoiceEventPayload { ReferenceNumber = "SV-2026-00005", TotalAfter = 100m },
        };

        // ---- Test 3: the event is rolled back when the business transaction rolls back ----
        [Fact]
        public async Task Event_is_rolled_back_with_the_business_transaction()
        {
            using var host = new PlatformTestHost();
            var events = host.Events();

            await using (var tx = await ScopedTx.BeginOrJoinAsync(host.Db))
            {
                var eventId = await events.RecordAsync(Created());
                Assert.True(eventId > 0);
                // Visible inside the transaction...
                Assert.Equal(1, await host.Db.BusinessEvents.CountAsync());
                await tx.RollbackAsync();
            }

            // ...and gone after the rollback. Read through a FRESH context so the assertion cannot be
            // satisfied by EF's identity map.
            using var verify = host.NewContext();
            Assert.Equal(0, await verify.BusinessEvents.CountAsync());
            Assert.Equal(0, await verify.BusinessEventDispatches.CountAsync());
        }

        [Fact]
        public async Task Event_and_its_dispatch_rows_survive_a_commit_together()
        {
            using var host = new PlatformTestHost();
            var events = host.Events();

            await using (var tx = await ScopedTx.BeginOrJoinAsync(host.Db))
            {
                await events.RecordAsync(Created());
                await tx.CommitAsync();
            }

            using var verify = host.NewContext();
            var stored = await verify.BusinessEvents.SingleAsync();
            Assert.Equal(EntityRegistry.SalesInvoice, stored.EntityType);
            Assert.Equal(SalesInvoiceEvents.Created, stored.EventType);
            Assert.Equal(BusinessEventVisibility.Internal, stored.Visibility);
            Assert.Equal(1, stored.CompanyID);
            Assert.Equal(7, stored.ActorEmployeeId);            // from the BusinessContext, not the caller
            Assert.NotEqual(Guid.Empty, stored.EventUid);
            Assert.NotNull(stored.CorrelationId);
            Assert.Null(stored.CompletedAt);                     // no consumer has run yet

            // Exactly one dispatch row per REGISTERED consumer.
            var dispatches = await verify.BusinessEventDispatches.ToListAsync();
            Assert.Equal(BusinessEventConsumers.Registered.Length, dispatches.Count);
            Assert.All(dispatches, d => Assert.Equal(BusinessEventDispatchStatus.Pending, d.Status));
            Assert.Contains(dispatches, d => d.Consumer == BusinessEventConsumers.TimelineProjection);
        }

        // ---- Test 4: a failed event insert fails the business transaction ----
        [Fact]
        public async Task A_contract_failure_propagates_and_takes_the_business_data_with_it()
        {
            using var host = new PlatformTestHost();
            var events = host.Events();

            await using (var tx = await ScopedTx.BeginOrJoinAsync(host.Db))
            {
                host.Db.SalesInvoices.Add(new SalesInvoice
                {
                    CompanyID = 1, CustomerId = 1, InvoiceDate = new DateTime(2026, 1, 1),
                    InvoiceNo = "SV-2026-00001", Status = "Posted", GrandTotal = 100m,
                });
                await host.Db.SaveChangesAsync();

                // The event is NOT wrapped in a swallowing try/catch: a bad contract throws out to the caller.
                await Assert.ThrowsAsync<BusinessEventContractException>(() => events.RecordAsync(new BusinessEventRecord
                {
                    EntityCode = EntityRegistry.SalesInvoice,
                    EntityId = 1,
                    EventType = "SalesInvoiceCreated",   // not canonical
                    Visibility = BusinessEventVisibility.Internal,
                }));

                await tx.RollbackAsync();
            }

            using var verify = host.NewContext();
            Assert.Equal(0, await verify.SalesInvoices.CountAsync());   // the sale did not stand
            Assert.Equal(0, await verify.BusinessEvents.CountAsync());
        }

        [Fact]
        public async Task Recording_outside_a_transaction_is_refused()
        {
            using var host = new PlatformTestHost();
            var events = host.Events();

            // Rule 1/2 of ADR-001 made mechanical: with no ambient ScopedTx there is nothing to bind the
            // event to, so it is rejected rather than written as an orphan that could outlive a rollback.
            var ex = await Assert.ThrowsAsync<BusinessEventContractException>(() => events.RecordAsync(Created()));
            Assert.Contains("ambient transaction", ex.Message);
            Assert.Equal(0, await host.Db.BusinessEvents.CountAsync());
        }

        [Fact]
        public async Task Unknown_entity_codes_and_bad_visibility_are_refused()
        {
            using var host = new PlatformTestHost();
            var events = host.Events();

            await using var tx = await ScopedTx.BeginOrJoinAsync(host.Db);

            await Assert.ThrowsAsync<EntityCodeNotRegisteredException>(() => events.RecordAsync(new BusinessEventRecord
            {
                EntityCode = "NotARealEntity", EntityId = 1,
                EventType = "NotARealEntity.Created", Visibility = BusinessEventVisibility.Internal,
            }));

            await Assert.ThrowsAsync<BusinessEventContractException>(() => events.RecordAsync(new BusinessEventRecord
            {
                EntityCode = EntityRegistry.SalesInvoice, EntityId = 1,
                EventType = SalesInvoiceEvents.Created, Visibility = "Public",   // outside the frozen vocabulary
            }));

            await Assert.ThrowsAsync<BusinessEventContractException>(() => events.RecordAsync(new BusinessEventRecord
            {
                EntityCode = EntityRegistry.SalesInvoice, EntityId = 0,           // not a real record
                EventType = SalesInvoiceEvents.Created, Visibility = BusinessEventVisibility.Internal,
            }));

            await tx.RollbackAsync();
        }

        // ---- Test 5: a duplicate DedupKey does not create a duplicate event ----
        [Fact]
        public async Task Duplicate_dedup_key_returns_the_original_event_and_writes_nothing()
        {
            using var host = new PlatformTestHost();
            var events = host.Events();

            await using (var tx = await ScopedTx.BeginOrJoinAsync(host.Db))
            {
                var first = await events.RecordAsync(Created(dedupKey: "SalesInvoice.Created:5"));
                var second = await events.RecordAsync(Created(dedupKey: "SalesInvoice.Created:5"));

                Assert.Equal(first, second);
                await tx.CommitAsync();
            }

            using var verify = host.NewContext();
            Assert.Equal(1, await verify.BusinessEvents.CountAsync());
            Assert.Equal(BusinessEventConsumers.Registered.Length, await verify.BusinessEventDispatches.CountAsync());
        }

        [Fact]
        public async Task The_same_dedup_key_in_another_company_is_a_different_event()
        {
            using var host = new PlatformTestHost();

            // B4: both companies' events are written on ONE context inside ONE ambient transaction, which is what
            // the dedup index is being tested against — so the write happens under an explicit, audited
            // cross-company administration lease rather than by evading the guard.
            using var crossCompany = host.HostBypass().Begin(
                CrossBuy.Models.Platform.CompanyBypassKind.CrossCompanyAdministration,
                PlatformTestHost.AdminContext(), "recording two companies' events in one transaction");

            await using (var tx = await ScopedTx.BeginOrJoinAsync(host.Db))
            {
                await host.Events(host.Db, PlatformTestHost.DefaultContext(companyId: 1))
                    .RecordAsync(Created(dedupKey: "SalesInvoice.Created:5"));
                await host.Events(host.Db, PlatformTestHost.DefaultContext(companyId: 2))
                    .RecordAsync(Created(dedupKey: "SalesInvoice.Created:5"));
                await tx.CommitAsync();
            }

            // Dedup is scoped per company (UX_BusinessEvents_DedupKey is on CompanyID + DedupKey), so one
            // company can never suppress another company's event.
            //
            // B2: the verification read spans BOTH companies, so it needs an authorized cross-company context —
            // a company-1 context would see 1 of the 2 and the test would read as a dedup failure.
            using var verify = host.AllCompanies();
            Assert.Equal(2, await verify.BusinessEvents.CountAsync());
        }

        // ---- Test 14: the payload size limit is enforced ----
        [Fact]
        public async Task An_oversized_payload_is_rejected_with_the_limit_in_the_message()
        {
            using var host = new PlatformTestHost();
            var events = host.Events();

            await using var tx = await ScopedTx.BeginOrJoinAsync(host.Db);

            var oversized = new SalesInvoiceEventPayload
            {
                ReferenceNumber = new string('x', BusinessEventService.MaxPayloadBytes + 1024),
            };

            var ex = await Assert.ThrowsAsync<BusinessEventPayloadTooLargeException>(
                () => events.RecordAsync(Created(payload: oversized)));

            Assert.Equal(BusinessEventService.MaxPayloadBytes, ex.MaxBytes);
            Assert.True(ex.ActualBytes > ex.MaxBytes);
            Assert.Equal(0, await host.Db.BusinessEvents.CountAsync());

            // A payload just under the limit is accepted, so the boundary is a real limit and not a
            // conservative guess that rejects usable payloads.
            var justUnder = new SalesInvoiceEventPayload { ReferenceNumber = new string('x', 60 * 1024) };
            Assert.True(await events.RecordAsync(Created(payload: justUnder)) > 0);

            await tx.RollbackAsync();
        }

        [Fact]
        public async Task A_payload_supplied_as_a_non_json_string_is_rejected()
        {
            using var host = new PlatformTestHost();
            var events = host.Events();

            await using var tx = await ScopedTx.BeginOrJoinAsync(host.Db);

            // Caught here rather than at COMMIT time by the SQL Server ISJSON check constraint — which would
            // surface the failure inside someone else's financial transaction.
            await Assert.ThrowsAsync<BusinessEventContractException>(
                () => events.RecordAsync(Created(payload: "this is not json")));

            // Valid JSON supplied as a string is stored verbatim.
            var eventId = await events.RecordAsync(Created(payload: "{\"referenceNumber\":\"SV-1\"}"));
            var stored = await host.Db.BusinessEvents.SingleAsync(e => e.EventId == eventId);
            Assert.Equal("{\"referenceNumber\":\"SV-1\"}", stored.Payload);

            await tx.RollbackAsync();
        }

        [Fact]
        public async Task The_envelope_carries_the_documented_wire_contract()
        {
            using var host = new PlatformTestHost();
            var events = host.Events();

            long eventId;
            await using (var tx = await ScopedTx.BeginOrJoinAsync(host.Db))
            {
                eventId = await events.RecordAsync(Created());
                await tx.CommitAsync();
            }

            var stored = await host.Db.BusinessEvents.SingleAsync(e => e.EventId == eventId);
            var envelope = events.BuildEnvelope(stored);

            Assert.Equal(stored.EventUid, envelope.EventUid);
            Assert.Equal(SalesInvoiceEvents.Created, envelope.EventType);
            Assert.Equal(EntityRegistry.SalesInvoice, envelope.Entity.Code);
            Assert.Equal(5, envelope.Entity.Id);
            Assert.Equal(7, envelope.Actor.EmployeeId);
            Assert.Equal(1, envelope.Context.CompanyId);
            Assert.NotNull(envelope.Context.CorrelationId);
            Assert.Equal(SalesInvoiceEventPayload.Version, envelope.PayloadVersion);
            Assert.Equal(BusinessEventVisibility.Internal, envelope.Visibility);
            Assert.Equal(stored.CreatedAt, envelope.OccurredAt);
            Assert.NotNull(envelope.Payload);
            Assert.Equal("SV-2026-00005", envelope.Payload!["referenceNumber"]!.GetValue<string>());
        }
    }
}