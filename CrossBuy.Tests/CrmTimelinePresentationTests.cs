using System.Text.Json;
using CrossBuy.BL.Platform;
using CrossBuy.Models.Context.Platform;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CrossBuy.Tests
{
    // =============================================================================================
    // CRM WAVE 1 — TIMELINE CLOSURE.
    //
    // WHY THIS SUITE EXISTS AS ONE INVARIANT AND NOT AS FOURTEEN ISOLATED CHECKS.
    //
    // An entity has a timeline when TWO things are true at once: EntityRegistry says
    // SupportsTimeline, and TimelineEventPresenter can render the events that entity publishes.
    // Either one alone is a defect, and they are opposite defects:
    //
    //   flag without presenter  → TimelineProjectionConsumer is STRICT, so every CRM event becomes a
    //                             dispatch row that throws and retries to its attempt cap. That is the
    //                             512-row failure the Tasks and Calendar note in the presenter records.
    //   presenter without flag  → BusinessEventService.IsEligible never creates the dispatch row, so
    //                             the arms are unreachable and the timeline stays empty.
    //
    // So the pairing itself is the thing under test (C1/C2), not just the fourteen arms.
    //
    // COMPANIES ARE 41 AND 77, never 1 — the value every historical default-company bug produced.
    // =============================================================================================
    public class CrmTimelinePresentationTests
    {
        private const int CoA = 41;
        private const int CoB = 77;
        private const int Emp = 5;

        // The full declared CRM vocabulary: five Lead facts, seven Opportunity facts, two account facts.
        // Written out rather than reflected, so ADDING an event without teaching the presenter fails here
        // instead of passing a loop that discovers nothing.
        public static readonly (string Entity, string Type)[] AllCrmEvents =
        {
            (EntityRegistry.Lead, LeadEvents.Created),
            (EntityRegistry.Lead, LeadEvents.Updated),
            (EntityRegistry.Lead, LeadEvents.Converted),
            (EntityRegistry.Lead, LeadEvents.FollowUpScheduled),
            (EntityRegistry.Lead, LeadEvents.FollowUpCompleted),
            (EntityRegistry.Opportunity, OpportunityEvents.Created),
            (EntityRegistry.Opportunity, OpportunityEvents.Updated),
            (EntityRegistry.Opportunity, OpportunityEvents.StageChanged),
            (EntityRegistry.Opportunity, OpportunityEvents.Won),
            (EntityRegistry.Opportunity, OpportunityEvents.Lost),
            (EntityRegistry.Opportunity, OpportunityEvents.FollowUpScheduled),
            (EntityRegistry.Opportunity, OpportunityEvents.FollowUpCompleted),
            (EntityRegistry.CrmAccount, CrmAccountEvents.Created),
            (EntityRegistry.CrmAccount, CrmAccountEvents.Updated),
        };

        public static TheoryData<string, string> EveryCrmEvent()
        {
            var data = new TheoryData<string, string>();
            foreach (var (entity, type) in AllCrmEvents) data.Add(entity, type);
            return data;
        }

        /// A realistic v1 payload for whichever payload type the event uses. Values are the ones a
        /// producer actually sends; nothing here is a field the payload does not declare.
        private static string PayloadFor(string eventType)
        {
            if (eventType.StartsWith("Lead.FollowUp", StringComparison.Ordinal)
                || eventType.StartsWith("Opportunity.FollowUp", StringComparison.Ordinal))
                return JsonSerializer.Serialize(new CrmFollowUpEventPayload
                {
                    ActivityId = 9, Subject = "اتصال بالعميل", ActivityType = "Task",
                    OwnerEmployeeId = Emp, DueDate = new DateTime(2026, 9, 30, 0, 0, 0, DateTimeKind.Utc),
                    EntityCode = EntityRegistry.Lead, EntityId = 1, EntityName = "شركة النور",
                });

            if (eventType.StartsWith("Lead.", StringComparison.Ordinal))
                return JsonSerializer.Serialize(new LeadEventPayload
                {
                    LeadName = "شركة النور", CompanyName = "مجموعة الأمل", OwnerEmployeeId = Emp,
                    Status = "Qualified", PreviousStatus = "New", Source = "Website",
                    EstimatedValue = 25000m, CrmAccountId = 7,
                    ChangedFields = new[] { "Status", "OwnerEmployeeId" },
                });

            if (eventType.StartsWith("Opportunity.", StringComparison.Ordinal))
                return JsonSerializer.Serialize(new OpportunityEventPayload
                {
                    OpportunityTitle = "توريد مكاتب", CrmAccountId = 7, OwnerEmployeeId = Emp,
                    StageId = 3, StageName = "Negotiation", PreviousStageId = 2, PreviousStageName = "Proposal",
                    Amount = 48000m, WinLossReason = "السعر",
                    ChangedFields = new[] { "Amount", "ExpectedCloseDate" },
                });

            return JsonSerializer.Serialize(new CrmAccountEventPayload
            {
                AccountName = "مجموعة الأمل", OwnerEmployeeId = Emp, Industry = "مقاولات",
                Segment = "VIP", ConvertedFromLeadId = 4,
                ChangedFields = new[] { "Industry", "OwnerEmployeeId" },
            });
        }

        // ---- C1 / C2: the pairing invariant --------------------------------------------------------

        [Theory]
        [MemberData(nameof(EveryCrmEvent))]
        public void C1_every_declared_CRM_event_has_a_presenter_arm(string entity, string eventType)
        {
            bool ok = TimelineEventPresenter.TryPresent(eventType, 1, PayloadFor(eventType), out var view, out var error);

            Assert.True(ok, $"{eventType}: {error}");
            Assert.False(string.IsNullOrWhiteSpace(view!.TitleAr), $"{eventType} has no Arabic title");
            Assert.False(string.IsNullOrWhiteSpace(view.TitleEn), $"{eventType} has no English title");
            Assert.False(string.IsNullOrWhiteSpace(view.DescriptionAr), $"{eventType} has no Arabic description");
            Assert.False(string.IsNullOrWhiteSpace(view.DescriptionEn), $"{eventType} has no English description");
            Assert.False(string.IsNullOrWhiteSpace(view.Icon));
            Assert.False(string.IsNullOrWhiteSpace(view.Color));

            // The arm must belong to the event it was reached by: a switch that fell through to a
            // neighbour would still produce text, and only the entity prefix catches that.
            Assert.StartsWith(entity, eventType, StringComparison.Ordinal);
        }

        [Fact]
        public void C2_the_capability_flag_and_the_presenter_agree_for_every_CRM_entity()
        {
            using var host = new PlatformTestHost(CoA);
            var registry = host.Registry();

            foreach (var code in new[] { EntityRegistry.Lead, EntityRegistry.Opportunity, EntityRegistry.CrmAccount })
                Assert.True(registry.GetDefinition(code).SupportsTimeline,
                    $"{code} declares no timeline, but its events are presentable - the flag must follow.");

            // And the other direction, which is the one that actually rots: an entity that claims a
            // timeline must be able to render every event family it declares. Flipping a flag on without
            // the arms, or adding an arm-less event later, fails right here.
            foreach (var (entity, type) in AllCrmEvents)
            {
                Assert.True(registry.GetDefinition(entity).SupportsTimeline);
                Assert.True(TimelineEventPresenter.TryPresent(type, 1, PayloadFor(type), out _, out var error),
                    $"{entity} claims a timeline but {type} cannot be presented: {error}");
            }
        }

        [Fact]
        public void C3_the_presentation_says_what_actually_happened()
        {
            // Spot checks on the facts a reader would notice being wrong. Each value comes from the
            // payload; none of it is fetched, because the presenter has no database.
            var stage = Present(OpportunityEvents.StageChanged);
            Assert.Contains("Proposal", stage.DescriptionEn, StringComparison.Ordinal);
            Assert.Contains("Negotiation", stage.DescriptionEn, StringComparison.Ordinal);

            var won = Present(OpportunityEvents.Won);
            Assert.Equal("Opportunity won", won.TitleEn);
            Assert.Contains("48,000.00", won.DescriptionEn, StringComparison.Ordinal);

            var lost = Present(OpportunityEvents.Lost);
            Assert.Equal("Opportunity lost", lost.TitleEn);
            Assert.NotEqual(won.Color, lost.Color);          // won and lost must not read alike

            var converted = Present(LeadEvents.Converted);
            Assert.Contains("converted", converted.DescriptionEn, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("#7", converted.DescriptionEn, StringComparison.Ordinal);

            var scheduled = Present(LeadEvents.FollowUpScheduled);
            Assert.Contains("2026-09-30", scheduled.DescriptionEn, StringComparison.Ordinal);
            Assert.Equal("Follow-up completed", Present(OpportunityEvents.FollowUpCompleted).TitleEn);

            var account = Present(CrmAccountEvents.Created);
            Assert.Contains("مجموعة الأمل", account.DescriptionAr!, StringComparison.Ordinal);
        }

        [Fact]
        public void C4_a_payload_that_omits_a_field_omits_it_rather_than_inventing_one()
        {
            // An event recorded before a field existed, or by a path that does not know it, must still
            // render a true sentence. Nothing may be filled in from anywhere.
            bool ok = TimelineEventPresenter.TryPresent(
                OpportunityEvents.Won, 1,
                JsonSerializer.Serialize(new OpportunityEventPayload { OpportunityTitle = "توريد مكاتب" }),
                out var view, out _);

            Assert.True(ok);
            Assert.DoesNotContain("48,000", view!.DescriptionEn!, StringComparison.Ordinal);
            Assert.DoesNotContain("reason", view.DescriptionEn!, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("توريد مكاتب", view.DescriptionEn!, StringComparison.Ordinal);
        }

        // ---- C5 / C6: the failure contract ---------------------------------------------------------

        [Theory]
        [InlineData("Lead.Deleted")]
        [InlineData("Lead.StatusChanged")]      // deliberately never declared - see BusinessEventTypes
        [InlineData("Opportunity.Reopened")]    // the open product decision, not an event
        [InlineData("CrmAccount.Merged")]
        [InlineData("CrmContact.Created")]      // CrmContact is not a registered entity at all
        public void C5_an_undeclared_CRM_event_is_refused_and_not_dressed_up(string eventType)
        {
            bool ok = TimelineEventPresenter.TryPresent(eventType, 1, "{}", out var view, out var error);

            Assert.False(ok, $"{eventType} must not be presentable - unknown stays unknown.");
            Assert.Null(view);
            Assert.Contains("no timeline presentation registered", error!, StringComparison.Ordinal);

            // The LENIENT read path may still render the canonical action so a document screen does not
            // break, but it must not manufacture a CRM sentence: no description at all.
            var lenient = TimelineEventPresenter.Present(eventType, 1, "{}");
            Assert.Null(lenient.DescriptionEn);
            Assert.Null(lenient.DescriptionAr);
        }

        [Theory]
        [MemberData(nameof(EveryCrmEvent))]
        public void C6_a_payload_version_this_build_does_not_understand_is_refused(string entity, string eventType)
        {
            _ = entity;
            bool ok = TimelineEventPresenter.TryPresent(eventType, 2, PayloadFor(eventType), out var view, out var error);

            // CRM payloads are all v1. A v2 body is from a newer build, and rendering it with v1 rules
            // would show a reader fields that mean something else. The consumer must see the failure.
            Assert.False(ok, $"{eventType} presented a v2 payload it cannot understand.");
            Assert.Null(view);
            Assert.Contains("payload version 2", error!, StringComparison.Ordinal);
        }

        // ---- C7..C9: end to end through the strict consumer ----------------------------------------

        private static TimelineEventPresenter.Presentation Present(string eventType)
        {
            Assert.True(TimelineEventPresenter.TryPresent(eventType, 1, PayloadFor(eventType), out var v, out var e), e);
            return v!;
        }

        private static TimelineProjectionConsumer Consumer(PlatformTestHost host, CrossBuy.Models.Context.CrossDbContext db)
            => new(host.Registry(db), NullLogger<TimelineProjectionConsumer>.Instance);

        private static ITimelineProjectionService Projection(PlatformTestHost host, CrossBuy.Models.Context.CrossDbContext db)
            => new TimelineProjectionService(db, host.Registry(db),
                new StubPermissionProvider(PlatformActions.View), Array.Empty<ILegacyTimelineAdapter>());

        private static async Task<long> RecordAsync(
            PlatformTestHost host, PlatformTestHost.RequestScope req, int companyId,
            string entityCode, int entityId, string eventType, string? dedupKey = null)
        {
            var events = host.Events(req.Db, PlatformTestHost.DefaultContext(companyId, Emp));
            await using var tx = await req.Db.Database.BeginTransactionAsync();
            long id = await events.RecordAsync(new BusinessEventRecord
            {
                EntityCode = entityCode, EntityId = entityId, EventType = eventType,
                Payload = JsonSerializer.Deserialize<JsonElement>(PayloadFor(eventType)),
                DedupKey = dedupKey,
            });
            await tx.CommitAsync();
            return id;
        }

        [Theory]
        [InlineData(EntityRegistry.Lead, "Lead.Created")]
        [InlineData(EntityRegistry.Lead, "Lead.Converted")]
        [InlineData(EntityRegistry.Lead, "Lead.FollowUpScheduled")]
        [InlineData(EntityRegistry.Opportunity, "Opportunity.StageChanged")]
        [InlineData(EntityRegistry.Opportunity, "Opportunity.Won")]
        [InlineData(EntityRegistry.Opportunity, "Opportunity.Lost")]
        [InlineData(EntityRegistry.Opportunity, "Opportunity.FollowUpCompleted")]
        [InlineData(EntityRegistry.CrmAccount, "CrmAccount.Created")]
        public async Task C7_a_recorded_CRM_event_reaches_the_strict_consumer_and_becomes_one_timeline_row(
            string entityCode, string eventType)
        {
            using var host = new PlatformTestHost(CoA);
            var req = host.Request(CoA);

            await RecordAsync(host, req, CoA, entityCode, entityId: 12, eventType);

            using var read = host.NewContext(req.Holder);
            var stored = await read.BusinessEvents.SingleAsync();

            // The dispatch row exists at all only because SupportsTimeline is true — IsEligible is
            // capability-based, so this assertion dies the moment the flag is flipped back.
            var consumers = await read.BusinessEventDispatches.Select(d => d.Consumer).ToListAsync();
            Assert.Contains(BusinessEventConsumers.TimelineProjection, consumers);

            // And the consumer is STRICT: a missing presenter arm throws right here rather than
            // producing a blank row, which is the whole reason the flag waited for the presenter.
            var envelope = host.Events(read).BuildEnvelope(stored);
            await Consumer(host, read).HandleAsync(envelope);

            var items = await Projection(host, read).GetAsync(entityCode, 12, PlatformTestHost.DefaultContext(CoA, Emp));
            var row = Assert.Single(items);
            Assert.False(string.IsNullOrWhiteSpace(row.TitleEn));
            Assert.False(string.IsNullOrWhiteSpace(row.TitleAr));
        }

        [Fact]
        public async Task C8_replaying_the_same_CRM_event_leaves_one_timeline_row()
        {
            using var host = new PlatformTestHost(CoA);
            var req = host.Request(CoA);
            const string key = "crm:lead:12:created";

            long first = await RecordAsync(host, req, CoA, EntityRegistry.Lead, 12, LeadEvents.Created, key);
            long again = await RecordAsync(host, req, CoA, EntityRegistry.Lead, 12, LeadEvents.Created, key);

            // Idempotency is the platform's, not a CRM invention: a second RecordAsync with the same
            // dedup key is a no-op that returns the ORIGINAL id, so there is only ever one event to
            // project — and therefore only ever one row, however many times delivery is retried.
            Assert.Equal(first, again);

            using var read = host.NewContext(req.Holder);
            Assert.Equal(1, await read.BusinessEvents.CountAsync());

            var stored = await read.BusinessEvents.SingleAsync();
            var envelope = host.Events(read).BuildEnvelope(stored);
            var consumer = Consumer(host, read);
            await consumer.HandleAsync(envelope);
            await consumer.HandleAsync(envelope);          // redelivery, which at-least-once guarantees

            var items = await Projection(host, read).GetAsync(EntityRegistry.Lead, 12, PlatformTestHost.DefaultContext(CoA, Emp));
            Assert.Single(items);
        }

        [Fact]
        public async Task C9_one_companys_CRM_timeline_is_invisible_to_the_other()
        {
            using var host = new PlatformTestHost(CoA);

            var a = host.Request(CoA);
            var b = host.Request(CoB);
            await RecordAsync(host, a, CoA, EntityRegistry.Lead, 12, LeadEvents.Created);
            await RecordAsync(host, b, CoB, EntityRegistry.Lead, 12, LeadEvents.Updated);

            // Both events exist, on the SAME entity id, in different companies — read unfiltered to prove
            // the arrangement is real before asserting that a scoped read cannot see across it.
            using (var all = host.AllCompanies())
                Assert.Equal(2, await all.BusinessEvents.CountAsync());

            using var readA = host.NewContext(a.Holder);
            var seenByA = await Projection(host, readA).GetAsync(EntityRegistry.Lead, 12, PlatformTestHost.DefaultContext(CoA, Emp));
            Assert.Single(seenByA);
            Assert.Equal("Lead created", seenByA[0].TitleEn);

            using var readB = host.NewContext(b.Holder);
            var seenByB = await Projection(host, readB).GetAsync(EntityRegistry.Lead, 12, PlatformTestHost.DefaultContext(CoB, Emp));
            Assert.Single(seenByB);
            Assert.NotEqual("Lead created", seenByB[0].TitleEn);
        }
    }
}
