using CrossBuy.BL;
using CrossBuy.BL.Platform;
using CrossBuy.Models.Context.Platform;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CrossBuy.Tests
{
    // Stage 0 (Slice-003) — JournalEntry.Reversed.
    //
    // Reversal is the most audit-relevant operation in the system and until Stage 0 it left no durable trace
    // beyond the two entry rows. These tests pin the contract (naming, payload hygiene, visibility, atomicity)
    // rather than re-testing JournalEntryService's GL mathematics, which is covered by inv-test-integrity.
    public class Slice3JournalReversalTests
    {
        private static BusinessEventRecord Reversed(int entryId = 501, int companyId = 1) => new()
        {
            EntityCode = EntityRegistry.JournalEntry,
            EntityId = entryId,
            EventType = JournalEntryEvents.Reversed,
            PayloadVersion = JournalEntryEventPayload.Version,
            Visibility = BusinessEventVisibility.Confidential,
            DedupKey = $"JournalEntry.Reversed:{entryId}",
            CompanyIdOverride = companyId,
            Payload = new JournalEntryEventPayload
            {
                OriginalJournalEntryId = entryId,
                OriginalJournalNumber = "JV-2026-000501",
                ReversingJournalEntryId = entryId + 1,
                ReversingJournalNumber = "JV-2026-000502",
                OriginalSourceType = "SalesInvoice",
                OriginalSourceId = 42,
                ReversalReason = "تعديل فاتورة SV-2026-00042",
                OriginalAmount = 1500m,
                ReversedAt = new DateTime(2026, 8, 3, 10, 0, 0, DateTimeKind.Utc),
            },
        };

        // ---- Test 24: canonical naming enforced ----
        [Fact]
        public void The_event_name_is_canonical_and_bound_to_the_JournalEntry_entity()
        {
            Assert.Equal("JournalEntry.Reversed", JournalEntryEvents.Reversed);
            Assert.True(BusinessEventTypes.TryValidate(JournalEntryEvents.Reversed, EntityRegistry.JournalEntry, out var error), error);

            // Recorded against the wrong entity is still rejected.
            Assert.False(BusinessEventTypes.TryValidate(JournalEntryEvents.Reversed, EntityRegistry.SalesInvoice, out _));

            // Transitions the domain does NOT have must not be declared. JournalEntryService writes entries
            // already posted, so there is no separate Created/Posted transition to name.
            var declared = typeof(JournalEntryEvents).GetFields().Select(f => (string)f.GetRawConstantValue()!).ToArray();
            Assert.Equal(new[] { "JournalEntry.Reversed" }, declared);
        }

        [Fact]
        public void JournalEntry_is_registered_with_an_accounting_scope_and_no_timeline_screen()
        {
            using var host = new PlatformTestHost();
            var definition = host.Registry().GetDefinition(EntityRegistry.JournalEntry);

            Assert.Equal(EntityRegistry.ScopeAccounting, definition.PermissionScope);
            Assert.Equal("Accounting", definition.Module);
            Assert.True(definition.SupportsSearch);
            // Deliberately false: there is no journal-entry timeline screen, and enabling the flag without one
            // would make ITimelineProjectionService answer for a screen that does not exist.
            Assert.False(definition.SupportsTimeline);
            Assert.False(definition.SupportsComments);
            Assert.False(definition.ListedInRecordPicker);
            Assert.Equal("/Accounting/JournalEntry?id=7", host.Registry().BuildUrl(EntityRegistry.JournalEntry, 7));
        }

        // ---- Test 21: recorded atomically ----
        [Fact]
        public async Task The_reversal_event_is_recorded_inside_the_transaction_with_both_consumers()
        {
            using var host = new PlatformTestHost();
            var events = host.Events();

            long eventId;
            await using (var tx = await ScopedTx.BeginOrJoinAsync(host.Db))
            {
                eventId = await events.RecordAsync(Reversed());
                await tx.CommitAsync();
            }

            using var verify = host.NewContext();
            var stored = await verify.BusinessEvents.SingleAsync();
            Assert.Equal(EntityRegistry.JournalEntry, stored.EntityType);
            Assert.Equal(JournalEntryEvents.Reversed, stored.EventType);
            Assert.Equal(BusinessEventVisibility.Confidential, stored.Visibility);
            Assert.Equal("JournalEntry.Reversed:501", stored.DedupKey);

            // Every consumer that can USE this event gets a row, in the same transaction.
            //
            // This assertion used to be Registered.Length - every registered consumer, unconditionally.
            // That is the rule that produced the 77 terminal TimelineProjection failures sitting on
            // CrossBuyDev, all of them this very event type: JournalEntry declares SupportsTimeline =
            // false (asserted above), so the timeline row was created, retried five times and died,
            // every single time an entry was reversed. BusinessEventService now skips a consumer that
            // provably cannot use the event, so the count is the ELIGIBLE set rather than the registered
            // one.
            var consumers = await verify.BusinessEventDispatches.AsNoTracking()
                .Where(d => d.EventId == eventId).Select(d => d.Consumer).OrderBy(c => c).ToListAsync();

            Assert.DoesNotContain(BusinessEventConsumers.TimelineProjection, consumers);
            Assert.Equal(
                BusinessEventConsumers.Registered
                    .Where(c => !string.Equals(c, BusinessEventConsumers.TimelineProjection, StringComparison.Ordinal))
                    .OrderBy(c => c).ToList(),
                consumers);

            // The reversal itself is untouched - suppressing projection work never suppresses the fact.
            Assert.NotEmpty(consumers);
        }

        // ---- Test 22: rollback removes the event ----
        [Fact]
        public async Task A_rolled_back_reversal_leaves_no_event_behind()
        {
            using var host = new PlatformTestHost();
            var events = host.Events();

            await using (var tx = await ScopedTx.BeginOrJoinAsync(host.Db))
            {
                await events.RecordAsync(Reversed());
                Assert.Equal(1, await host.Db.BusinessEvents.CountAsync());
                await tx.RollbackAsync();
            }

            using var verify = host.NewContext();
            Assert.Equal(0, await verify.BusinessEvents.CountAsync());
            Assert.Equal(0, await verify.BusinessEventDispatches.CountAsync());
        }

        [Fact]
        public async Task Reversing_the_same_entry_twice_cannot_produce_two_events()
        {
            using var host = new PlatformTestHost();
            var events = host.Events();

            await using (var tx = await ScopedTx.BeginOrJoinAsync(host.Db))
            {
                var first = await events.RecordAsync(Reversed());
                var second = await events.RecordAsync(Reversed());
                Assert.Equal(first, second);   // the dedup key returns the original id
                await tx.CommitAsync();
            }
            Assert.Equal(1, await host.NewContext().BusinessEvents.CountAsync());
        }

        // ---- Test 23: the payload is safe ----
        [Fact]
        public async Task The_payload_carries_identity_and_a_total_but_never_the_entry_composition()
        {
            using var host = new PlatformTestHost();
            var events = host.Events();

            await using var tx = await ScopedTx.BeginOrJoinAsync(host.Db);
            var eventId = await events.RecordAsync(Reversed());
            var stored = await host.Db.BusinessEvents.SingleAsync(e => e.EventId == eventId);
            var payload = stored.Payload!;

            // Present: both entry identities, the source linkage, the reason and the single total.
            Assert.Contains("JV-2026-000501", payload);
            Assert.Contains("JV-2026-000502", payload);
            Assert.Contains("SalesInvoice", payload);
            Assert.Contains("1500", payload);

            // Absent by contract: line-level composition, accounts, cost centres, employees.
            foreach (var forbidden in new[] { "accountId", "debit", "credit", "costCenter", "employee", "lines", "password", "token" })
                Assert.DoesNotContain(forbidden, payload, StringComparison.OrdinalIgnoreCase);

            // Well inside the payload cap.
            Assert.True(System.Text.Encoding.UTF8.GetByteCount(payload) < BusinessEventService.MaxPayloadBytes / 20);
            await tx.RollbackAsync();
        }

        [Fact]
        public async Task The_reversal_renders_a_bilingual_timeline_row_naming_both_entries()
        {
            using var host = new PlatformTestHost();
            var events = host.Events();

            await using var tx = await ScopedTx.BeginOrJoinAsync(host.Db);
            var eventId = await events.RecordAsync(Reversed());
            var stored = await host.Db.BusinessEvents.SingleAsync(e => e.EventId == eventId);

            // Strict presentation must succeed — otherwise TimelineProjectionConsumer would fail its dispatch row.
            Assert.True(TimelineEventPresenter.TryPresent(stored.EventType, stored.PayloadVersion, stored.Payload, out var p, out var err), err);
            Assert.Equal("Journal entry reversed", p!.TitleEn);
            Assert.NotEqual(p.TitleAr, p.TitleEn);
            Assert.Contains("JV-2026-000501", p.DescriptionEn);
            Assert.Contains("JV-2026-000502", p.DescriptionEn);
            Assert.Contains("JV-2026-000501", p.DescriptionAr);
            await tx.RollbackAsync();
        }

        // ---- Test 25: company isolation ----
        [Fact]
        public async Task Reversal_events_are_company_isolated()
        {
            using var host = new PlatformTestHost();

            // B4: two companies' reversal events are written on ONE context in ONE ambient transaction, under an
            // explicit audited cross-company lease rather than by evading the write guard.
            using var crossCompany = host.HostBypass().Begin(
                CrossBuy.Models.Platform.CompanyBypassKind.CrossCompanyAdministration,
                PlatformTestHost.AdminContext(), "recording two companies' reversal events in one transaction");

            await using (var tx = await ScopedTx.BeginOrJoinAsync(host.Db))
            {
                await host.Events(host.Db, PlatformTestHost.DefaultContext(companyId: 1)).RecordAsync(Reversed(501));
                await host.Events(host.Db, PlatformTestHost.DefaultContext(companyId: 2)).RecordAsync(Reversed(601));
                await tx.CommitAsync();
            }

            // B2: BusinessEvent is filtered, and this verification deliberately reads BOTH companies, so it
            // goes through an authorized cross-company context. A company-1 read would see only its own row.
            using var verify = host.AllCompanies();
            Assert.Equal(1, await verify.BusinessEvents.CountAsync(e => e.CompanyID == 1));
            Assert.Equal(1, await verify.BusinessEvents.CountAsync(e => e.CompanyID == 2));

            // The same dedup key in another company is a different event — isolation holds through dedup too.
            await using (var tx = await ScopedTx.BeginOrJoinAsync(host.Db))
            {
                await host.Events(host.Db, PlatformTestHost.DefaultContext(companyId: 2)).RecordAsync(Reversed(501));
                await tx.CommitAsync();
            }
            using var verifyAll = host.AllCompanies();
            Assert.Equal(3, await verifyAll.BusinessEvents.CountAsync());
        }

        [Fact]
        public async Task Confidential_reversal_events_are_hidden_from_a_plain_viewer()
        {
            using var host = new PlatformTestHost();
            const int entityId = 501;

            host.Db.BusinessEvents.Add(new BusinessEvent
            {
                EventUid = Guid.NewGuid(), CompanyID = 1,
                EntityType = EntityRegistry.SalesInvoice, EntityId = entityId,
                EventType = SalesInvoiceEvents.Created,
                Visibility = BusinessEventVisibility.Internal, PayloadVersion = 1, CreatedAt = DateTime.UtcNow,
            });
            host.Db.BusinessEvents.Add(new BusinessEvent
            {
                EventUid = Guid.NewGuid(), CompanyID = 1,
                EntityType = EntityRegistry.SalesInvoice, EntityId = entityId,
                EventType = SalesInvoiceEvents.Updated,
                Visibility = BusinessEventVisibility.Confidential, PayloadVersion = 1, CreatedAt = DateTime.UtcNow,
            });
            await host.Db.SaveChangesAsync();

            var registry = host.Registry();
            var adapters = Array.Empty<ILegacyTimelineAdapter>();

            // A viewer with only View sees the Internal row; the Confidential one is filtered out. This is the
            // tier JournalEntry.Reversed uses, so the visibility choice is enforceable rather than decorative.
            var viewer = new TimelineProjectionService(host.Db, registry, new StubPermissionProvider(PlatformActions.View), adapters);
            Assert.Single(await viewer.GetAsync(EntityRegistry.SalesInvoice, entityId, PlatformTestHost.DefaultContext()));

            var accountant = new TimelineProjectionService(host.Db, registry,
                new StubPermissionProvider(PlatformActions.View, PlatformActions.ViewConfidential), adapters);
            Assert.Equal(2, (await accountant.GetAsync(EntityRegistry.SalesInvoice, entityId, PlatformTestHost.DefaultContext())).Count);
        }
    }
}
