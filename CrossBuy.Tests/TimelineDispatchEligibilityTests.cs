using CrossBuy.BL;
using CrossBuy.BL.Platform;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CrossBuy.Tests
{
    // ============================================================================================
    // TIMELINE DISPATCH ELIGIBILITY — the fan-out no longer creates work that cannot succeed.
    //
    // WHAT WAS WRONG. BusinessEventService created one dispatch row per REGISTERED consumer,
    // unconditionally. TimelineProjectionConsumer then rejected any entity whose definition says
    // SupportsTimeline = false, because such an event "would never be shown". Both halves were
    // individually correct and together they manufactured guaranteed failures: the row retried under
    // the backoff policy, ended terminal at Attempts = MaxAttempts, and stayed in the operator's
    // failure list describing a defect that did not exist. CrossBuyDev holds 77 of exactly those, all
    // JournalEntry.Reversed, all "SupportsTimeline is false".
    //
    // THE RULE IS ABOUT CAPABILITY, NOT ABOUT JournalEntry. It reads SupportsTimeline off the
    // definition the service has ALREADY resolved, so no entity is named anywhere and an entity that
    // gains or loses a timeline changes its own dispatch behaviour by changing its own definition.
    // These tests are written the same way: they assert through the registry's capability rather than
    // through a hardcoded expectation about one module.
    //
    // WHAT IT DOES NOT DO. It does not suppress the BusinessEvent - the reversal is still recorded,
    // still auditable, still available to reporting and AI. It does not touch the consumer's strict
    // guard, which must keep rejecting an unsupported entity that reaches it anyway through replay or
    // through a definition changed after recording. And it does not change any other consumer:
    // eligibility is default-yes, so a new consumer cannot silently inherit a rule written for this one.
    // ============================================================================================
    public class TimelineDispatchEligibilityTests
    {
        private static BusinessEventRecord JournalReversal(int entryId = 7) => new()
        {
            EntityCode = EntityRegistry.JournalEntry,
            EntityId = entryId,
            EventType = JournalEntryEvents.Reversed,
            Visibility = BusinessEventVisibility.Confidential,
            Payload = new JournalEntryEventPayload { OriginalJournalEntryId = entryId, ReversingJournalEntryId = entryId + 1 },
        };

        private static BusinessEventRecord InvoiceCreated(int invoiceId = 5) => new()
        {
            EntityCode = EntityRegistry.SalesInvoice,
            EntityId = invoiceId,
            EventType = SalesInvoiceEvents.Created,
            Visibility = BusinessEventVisibility.Internal,
            Payload = new SalesInvoiceEventPayload { ReferenceNumber = "SV-2026-00005", TotalAfter = 100m },
        };

        private static BusinessEventRecord TaskCreated(int taskId = 3) => new()
        {
            EntityCode = EntityRegistry.Task,
            EntityId = taskId,
            EventType = TaskEvents.Created,
            Visibility = BusinessEventVisibility.Internal,
            Payload = new TaskEventPayload { Title = "مهمة" },
        };

        private static async Task<long> RecordAsync(PlatformTestHost host, BusinessEventRecord record)
        {
            long id;
            await using (var tx = await ScopedTx.BeginOrJoinAsync(host.Db))
            {
                id = await host.Events().RecordAsync(record);
                await tx.CommitAsync();
            }
            return id;
        }

        private static async Task<List<string>> ConsumersFor(PlatformTestHost host, long eventId)
        {
            using var verify = host.NewContext();
            return await verify.BusinessEventDispatches.AsNoTracking()
                .Where(d => d.EventId == eventId)
                .Select(d => d.Consumer)
                .OrderBy(c => c)
                .ToListAsync();
        }

        // ---- A + F: a timeline-enabled entity is untouched ---------------------------------------

        [Fact]
        public async Task An_entity_that_supports_a_timeline_still_gets_its_timeline_dispatch()
        {
            using var host = new PlatformTestHost();

            // Asserted through the registry, not through a belief about SalesInvoice: if this entity
            // ever loses its timeline the test says so instead of failing somewhere confusing.
            Assert.True(host.Registry().GetDefinition(EntityRegistry.SalesInvoice).SupportsTimeline);

            var consumers = await ConsumersFor(host, await RecordAsync(host, InvoiceCreated()));

            Assert.Contains(BusinessEventConsumers.TimelineProjection, consumers);
            Assert.Equal(BusinessEventConsumers.Registered.OrderBy(c => c).ToList(), consumers);
        }

        [Fact]
        public async Task A_second_timeline_enabled_entity_behaves_the_same()
        {
            using var host = new PlatformTestHost();
            Assert.True(host.Registry().GetDefinition(EntityRegistry.Task).SupportsTimeline);

            var consumers = await ConsumersFor(host, await RecordAsync(host, TaskCreated()));

            Assert.Contains(BusinessEventConsumers.TimelineProjection, consumers);
            Assert.Equal(BusinessEventConsumers.Registered.Length, consumers.Count);
        }

        // ---- B + C: an entity with no timeline ---------------------------------------------------

        [Fact]
        public async Task An_entity_with_no_timeline_gets_no_timeline_dispatch()
        {
            using var host = new PlatformTestHost();
            Assert.False(host.Registry().GetDefinition(EntityRegistry.JournalEntry).SupportsTimeline);

            var consumers = await ConsumersFor(host, await RecordAsync(host, JournalReversal()));

            // The row that used to be created, retried five times and left terminal.
            Assert.DoesNotContain(BusinessEventConsumers.TimelineProjection, consumers);
        }

        [Fact]
        public async Task Every_other_consumer_still_receives_its_dispatch_row()
        {
            using var host = new PlatformTestHost();
            var consumers = await ConsumersFor(host, await RecordAsync(host, JournalReversal()));

            // Suppression is scoped to the ONE consumer that cannot use the event. Everything else the
            // platform registers must still be there, named from the registered set rather than from a
            // list here, so a consumer added later is covered without editing this test.
            var expected = BusinessEventConsumers.Registered
                .Where(c => !string.Equals(c, BusinessEventConsumers.TimelineProjection, StringComparison.Ordinal))
                .OrderBy(c => c)
                .ToList();

            Assert.Equal(expected, consumers);
            Assert.NotEmpty(consumers);   // guards against "passed because nothing was created at all"
        }

        // ---- D: the business fact survives -------------------------------------------------------

        [Fact]
        public async Task The_business_event_itself_is_still_recorded()
        {
            using var host = new PlatformTestHost();
            var eventId = await RecordAsync(host, JournalReversal(entryId: 42));

            using var verify = host.NewContext();
            var stored = await verify.BusinessEvents.AsNoTracking().SingleAsync(e => e.EventId == eventId);

            // The reversal is a business fact. Option B removes projection work, never the audit record -
            // reporting, AI and any future consumer still see it.
            Assert.Equal(EntityRegistry.JournalEntry, stored.EntityType);
            Assert.Equal(JournalEntryEvents.Reversed, stored.EventType);
            Assert.Equal(42, stored.EntityId);
            Assert.Equal(BusinessEventVisibility.Confidential, stored.Visibility);
            Assert.False(string.IsNullOrWhiteSpace(stored.Payload));
        }

        // ---- E: no duplicates --------------------------------------------------------------------

        [Fact]
        public async Task No_duplicate_dispatch_rows_are_created_for_either_kind_of_entity()
        {
            using var host = new PlatformTestHost();

            var withTimeline = await RecordAsync(host, InvoiceCreated());
            var without = await RecordAsync(host, JournalReversal());

            using var verify = host.NewContext();
            var duplicates = await verify.BusinessEventDispatches.AsNoTracking()
                .GroupBy(d => new { d.EventId, d.Consumer })
                .Where(g => g.Count() > 1)
                .CountAsync();

            Assert.Equal(0, duplicates);

            // And the counts are exactly what the rule predicts: full fan-out minus one for the entity
            // with no timeline.
            Assert.Equal(BusinessEventConsumers.Registered.Length,
                await verify.BusinessEventDispatches.CountAsync(d => d.EventId == withTimeline));
            Assert.Equal(BusinessEventConsumers.Registered.Length - 1,
                await verify.BusinessEventDispatches.CountAsync(d => d.EventId == without));
        }

        // ---- the consumer keeps its own guard ----------------------------------------------------

        [Fact]
        public void The_consumer_still_refuses_an_unsupported_entity_that_reaches_it_anyway()
        {
            using var host = new PlatformTestHost();

            // Option B stops such work being CREATED. It must not make the consumer lenient about work
            // that arrives regardless - through a replay, or a definition changed after recording. Those
            // are different jobs and both are needed.
            var consumer = new TimelineProjectionConsumer(
                host.Registry(), Microsoft.Extensions.Logging.Abstractions.NullLogger<TimelineProjectionConsumer>.Instance);

            var envelope = new BusinessEventEnvelope
            {
                EventUid = Guid.NewGuid(),
                EventType = JournalEntryEvents.Reversed,
                Entity = new BusinessEventEntity { Code = EntityRegistry.JournalEntry, Id = 7 },
                Actor = new BusinessEventActor { EmployeeId = 7 },
                Context = new BusinessEventContext { CompanyId = 1 },
                PayloadVersion = 1,
                Visibility = BusinessEventVisibility.Confidential,
                OccurredAt = DateTime.UtcNow,
            };

            var ex = Assert.ThrowsAsync<BusinessEventContractException>(
                () => consumer.HandleAsync(envelope)).GetAwaiter().GetResult();

            Assert.Contains("SupportsTimeline is false", ex.Message, StringComparison.Ordinal);
        }

        // ---- the rule is capability-based, not entity-based ---------------------------------------

        [Fact]
        public void The_eligibility_rule_names_no_entity()
        {
            var source = File.ReadAllText(Path.Combine(RepoRoot(), "CrossBuy", "BL", "Platform", "BusinessEventService.cs"));

            // Strip comments: the explanation is allowed to say WHY JournalEntry exposed this, but the
            // code must not decide anything by entity name.
            var code = string.Join("\n", source.Split('\n')
                .Where(l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal)));

            Assert.DoesNotContain("JournalEntry", code, StringComparison.Ordinal);
            Assert.Contains("SupportsTimeline", code, StringComparison.Ordinal);
        }

        private static string RepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "CrossBuy.sln"))) dir = dir.Parent;
            Assert.NotNull(dir);
            return dir!.FullName;
        }
    }
}
