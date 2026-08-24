using CrossBuy.BL.Platform;
using CrossBuy.Models.Platform;
using Microsoft.Extensions.Logging.Abstractions;
using System.Reflection;
using System.Text.Json.Nodes;
using Xunit;

namespace CrossBuy.Tests
{
    // ============================================================================================
    // TIMELINE PRESENTATION COVERAGE — the standing guard behind the 136 terminal dispatch rows.
    //
    // WHAT THE 136 ACTUALLY WERE. On CrossBuyDev, BusinessEventDispatch holds exactly 136
    // TimelineProjection rows at Status = Failed, every one at Attempts = 5 = MaxAttempts, between
    // 2026-08-03 and 2026-08-12. They are NOT one defect. They are three, and only one is still open:
    //
    //   A1  10 rows  JournalEntry.Reversed  "entity code is no longer registered"
    //   A2  67 rows  JournalEntry.Reversed  "SupportsTimeline is false"
    //   B   59 rows  5 x Task.*             "has no timeline presentation registered"
    //
    // A1 and A2 are the SAME entity at two points in its history: JournalEntry was unregistered when the
    // first ten failed, and registered-but-timeline-disabled by the time the rest did.
    //
    // FAMILY B IS CLOSED, and the database says so rather than a code reading: Task.BecameOverdue now has
    // 71 Done against 5 Failed, and its newest event (2026-08-20) dispatched successfully while the
    // failures all predate 2026-08-12. The presentations landed in between - see
    // TaskCalendarTimelinePresentationTests, which fixed it. What was missing afterwards was a guard that
    // KEEPS it closed, which is what this file is.
    //
    // FAMILY A IS OPEN AND DELIBERATE, which is why it is pinned here rather than "fixed". JournalEntry
    // carries SupportsTimeline = false on purpose (Slice3JournalReversalTests asserts it: there is no
    // journal-entry timeline screen), while JournalEntryService still records JournalEntry.Reversed. The
    // consumer's own message names the only two exits - "either enable the capability or stop producing
    // the event" - and both files belong to other tabs. So this file proves the gap is NOT presentation
    // coverage, and leaves the design decision where it belongs.
    //
    // WHY A GUARD AND NOT MORE PRESENTERS. Every event type these 136 rows name already presents today.
    // Adding presenters would be writing code for a problem that is already solved; the thing that was
    // actually missing is a test that fails the moment a new event type is declared on a timeline-enabled
    // entity without a presentation - i.e. before it reaches a dispatch row on somebody's database.
    // ============================================================================================
    public class TimelinePresentationCoverageTests
    {
        // The exact six event types behind the 136 rows, with the failure family each one produced.
        public static TheoryData<string> HistoricallyFailingEventTypes() => new()
        {
            TaskEvents.Created,          // B - 18 rows
            TaskEvents.Assigned,         // B - 18 rows
            TaskEvents.StatusChanged,    // B - 14 rows
            TaskEvents.BecameOverdue,    // B -  5 rows
            TaskEvents.Completed,        // B -  4 rows
            JournalEntryEvents.Reversed, // A - 77 rows
        };

        private const string NoPresentation = "has no timeline presentation registered";

        // ----------------------------------------------------------------------------------------
        // 1. PRESENTATION COVERAGE — the strict path, which is the one the consumer takes.
        // ----------------------------------------------------------------------------------------

        [Theory]
        [MemberData(nameof(HistoricallyFailingEventTypes))]
        public void Every_event_type_behind_the_136_rows_now_has_a_strict_presentation(string eventType)
        {
            // A null payload is deliberate: TryPayload treats "no payload" as valid, so this isolates
            // "does the presenter KNOW this event type" from "is this particular payload well formed".
            // A test that supplied a good payload would pass for a different reason than the one claimed.
            var ok = TimelineEventPresenter.TryPresent(eventType, 1, null, out var presentation, out var error);

            Assert.True(ok, $"{eventType}: {error}");
            Assert.NotNull(presentation);
            Assert.False(string.IsNullOrWhiteSpace(presentation!.TitleAr));
            Assert.False(string.IsNullOrWhiteSpace(presentation.TitleEn));
            Assert.False(string.IsNullOrWhiteSpace(presentation.Icon));
            Assert.False(string.IsNullOrWhiteSpace(presentation.Color));
        }

        [Fact]
        public void JournalEntry_Reversed_is_blocked_by_the_capability_flag_and_NOT_by_presentation()
        {
            // The whole point of family A. If this ever starts failing because the presentation went
            // missing, the diagnosis in the file header is wrong and the 77 rows have a second cause.
            Assert.True(TimelineEventPresenter.TryPresent(
                JournalEntryEvents.Reversed, 1, null, out _, out var error), error);

            using var host = new PlatformTestHost();
            var definition = host.Registry().GetDefinition(EntityRegistry.JournalEntry);

            // Deliberate, and asserted elsewhere too (Slice3JournalReversalTests): no journal-entry
            // timeline screen exists. This is the ONLY reason those 77 rows are still terminal.
            Assert.False(definition.SupportsTimeline);
        }

        // ----------------------------------------------------------------------------------------
        // 2. THE STANDING GUARD — what was missing when the 512 rows piled up.
        // ----------------------------------------------------------------------------------------

        [Fact]
        public void Every_declared_event_type_on_a_timeline_enabled_entity_has_a_presentation()
        {
            using var host = new PlatformTestHost();
            var registry = host.Registry();

            var missing = new List<string>();
            var checkedTypes = 0;

            foreach (var eventType in DeclaredEventTypes())
            {
                // "Entity.Action" - the frozen vocabulary BusinessEventTypes.Build produces.
                var entityCode = eventType.Split('.')[0];
                if (!registry.TryGetDefinition(entityCode, out var definition)) continue;

                // An entity with no timeline cannot strand a dispatch row on presentation: the consumer
                // rejects it earlier, on the capability. That is family A, tested above, not this guard.
                if (!definition!.SupportsTimeline) continue;

                checkedTypes++;
                if (!TimelineEventPresenter.TryPresent(eventType, 1, null, out _, out var error)
                    && error != null && error.Contains(NoPresentation, StringComparison.Ordinal))
                    missing.Add(eventType);
            }

            // Guard the guard: a reflection sweep that silently matched nothing would pass forever.
            Assert.True(checkedTypes >= 20, $"only {checkedTypes} event types were swept - the sweep is broken, not the coverage");
            Assert.True(missing.Count == 0,
                "these event types are declared on timeline-enabled entities but have no strict timeline " +
                "presentation, so every one of them will strand a dispatch row at Attempts = MaxAttempts:" +
                Environment.NewLine + "  " + string.Join(Environment.NewLine + "  ", missing));
        }

        // Every "<Entity>Events" constant holder in the kernel. Reflection rather than a hand-kept list,
        // because a hand-kept list is exactly what would not be updated when the next family is added.
        private static IEnumerable<string> DeclaredEventTypes()
        {
            var holders = typeof(BusinessEventTypes).Assembly.GetTypes()
                .Where(t => t.IsAbstract && t.IsSealed          // C# static class
                         && t.Namespace == typeof(BusinessEventTypes).Namespace
                         && t.Name.EndsWith("Events", StringComparison.Ordinal));

            foreach (var holder in holders)
                foreach (var field in holder.GetFields(BindingFlags.Public | BindingFlags.Static))
                    if (field.IsLiteral && field.FieldType == typeof(string)
                        && field.GetRawConstantValue() is string value && value.Contains('.'))
                        yield return value;
        }

        // ----------------------------------------------------------------------------------------
        // 3. THE CONSUMER END TO END — presentation coverage only matters if dispatch actually passes.
        // ----------------------------------------------------------------------------------------

        [Theory]
        [InlineData(nameof(TaskEvents.Created))]
        [InlineData(nameof(TaskEvents.Assigned))]
        [InlineData(nameof(TaskEvents.StatusChanged))]
        [InlineData(nameof(TaskEvents.BecameOverdue))]
        [InlineData(nameof(TaskEvents.Completed))]
        public async Task The_consumer_now_accepts_every_Task_event_type_that_used_to_strand(string constantName)
        {
            var eventType = (string)typeof(TaskEvents)
                .GetField(constantName, BindingFlags.Public | BindingFlags.Static)!
                .GetRawConstantValue()!;

            using var host = new PlatformTestHost();
            var consumer = Consumer(host);

            // The real consumer, the real registry, the real strict presenter. This is the exact call the
            // dispatch worker makes, so a pass here is the same pass a dispatch row would record.
            await consumer.HandleAsync(Envelope(EntityRegistry.Task, eventType));
        }

        [Fact]
        public async Task The_consumer_still_refuses_JournalEntry_Reversed_and_names_the_capability()
        {
            using var host = new PlatformTestHost();
            var consumer = Consumer(host);

            var ex = await Assert.ThrowsAsync<BusinessEventContractException>(() =>
                consumer.HandleAsync(Envelope(EntityRegistry.JournalEntry, JournalEntryEvents.Reversed)));

            // The REASON is the assertion. If this ever changes to a presentation complaint, family A has
            // regressed into family B and the recovery plan for those 77 rows no longer applies.
            Assert.Contains("SupportsTimeline is false", ex.Message, StringComparison.Ordinal);
            Assert.DoesNotContain(NoPresentation, ex.Message, StringComparison.Ordinal);
        }

        // ----------------------------------------------------------------------------------------
        // 4. FAILING SAFELY — the guard must keep rejecting what it was built to reject.
        // ----------------------------------------------------------------------------------------

        [Fact]
        public async Task An_unknown_event_type_on_a_timeline_entity_still_fails_loudly()
        {
            using var host = new PlatformTestHost();

            await Assert.ThrowsAsync<BusinessEventContractException>(() =>
                Consumer(host).HandleAsync(Envelope(EntityRegistry.Task, "Task.NeverDeclared")));
        }

        [Fact]
        public async Task A_payload_from_a_newer_build_fails_rather_than_rendering_half_of_it()
        {
            using var host = new PlatformTestHost();

            var envelope = Envelope(EntityRegistry.Task, TaskEvents.Created, payloadVersion: 999);
            var ex = await Assert.ThrowsAsync<BusinessEventContractException>(() =>
                Consumer(host).HandleAsync(envelope));

            Assert.Contains("999", ex.Message, StringComparison.Ordinal);
        }

        [Fact]
        public async Task A_payload_whose_shape_contradicts_its_version_fails()
        {
            using var host = new PlatformTestHost();

            // Well-formed JSON, wrong shape: `title` is a declared string on TaskEventPayload and here it
            // arrives as an object. It has to be a REAL property - an unknown member is ignored by design,
            // so a made-up field would prove nothing and would pass for the wrong reason.
            var envelope = Envelope(EntityRegistry.Task, TaskEvents.Created,
                payload: JsonNode.Parse("""{"title":{"nested":true}}"""));

            await Assert.ThrowsAsync<BusinessEventContractException>(() =>
                Consumer(host).HandleAsync(envelope));
        }

        [Fact]
        public async Task A_visibility_outside_the_frozen_vocabulary_fails()
        {
            using var host = new PlatformTestHost();

            var envelope = Envelope(EntityRegistry.Task, TaskEvents.Created, visibility: "PubliclyVisible");
            var ex = await Assert.ThrowsAsync<BusinessEventContractException>(() =>
                Consumer(host).HandleAsync(envelope));

            Assert.Contains("frozen vocabulary", ex.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void The_consumer_reports_the_timeline_projection_name_the_dispatch_rows_carry()
        {
            using var host = new PlatformTestHost();

            // The 136 rows are keyed by this string. A rename would orphan them rather than fix them.
            Assert.Equal(BusinessEventConsumers.TimelineProjection, Consumer(host).Consumer);
            Assert.Equal("TimelineProjection", Consumer(host).Consumer);
        }

        // ---- fixtures ---------------------------------------------------------------------------

        private static TimelineProjectionConsumer Consumer(PlatformTestHost host)
            => new(host.Registry(), NullLogger<TimelineProjectionConsumer>.Instance);

        private static BusinessEventEnvelope Envelope(
            string entityCode, string eventType, int payloadVersion = 1,
            string? visibility = null, JsonNode? payload = null)
            => new()
            {
                EventUid = Guid.NewGuid(),
                EventType = eventType,
                Entity = new BusinessEventEntity { Code = entityCode, Id = 7 },
                Actor = new BusinessEventActor { EmployeeId = 7 },
                Context = new BusinessEventContext { CompanyId = 1, BranchId = null },
                PayloadVersion = payloadVersion,
                Visibility = visibility ?? BusinessEventVisibility.Internal,
                OccurredAt = DateTime.UtcNow,
                Payload = payload,
            };
    }
}
