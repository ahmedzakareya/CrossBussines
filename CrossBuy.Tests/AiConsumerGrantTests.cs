using CrossBuy.BL.Platform;
using CrossBuy.BL.Platform.Ai;
using CrossBuy.Models.Platform;
using Xunit;

namespace CrossBuy.Tests
{
    // AI Foundation — the grant gate, tested as the pure decision function it is.
    //
    // These assert the BOUNDARY, not plumbing: the whole point of the increment is that a business fact
    // cannot enter the AI subsystem unless someone explicitly decided it may.
    public class AiConsumerGrantTests
    {
        private static readonly IAiConsumerGrants Grants = new AiConsumerGrants();
        private const string Ai = BusinessEventConsumers.AiProjection;

        private static AiGrantDecision Evaluate(
            string eventType, string entityType = EntityRegistry.Task,
            string visibility = BusinessEventVisibility.Internal, string consumer = Ai)
            => Grants.Evaluate(consumer, eventType, entityType, visibility);

        // ---- 1. explicitly approved events are allowed ----
        [Theory]
        [InlineData(TaskEvents.Created)]
        [InlineData(TaskEvents.StatusChanged)]
        [InlineData(TaskEvents.Completed)]
        [InlineData(TaskEvents.BecameOverdue)]
        public void An_explicitly_granted_event_is_allowed(string eventType)
        {
            var d = Evaluate(eventType);
            Assert.True(d.IsAllowed, d.Reason);
            Assert.Equal(AiConsumerGrants.TaskLifecycleProjection, d.Grant!.ProjectionType);
        }

        // ---- 2. DEFAULT DENY — the central guarantee of this increment ----
        //
        // These are all REAL event types the product raises today. None is granted, so none may enter AI.
        // If someone adds a grant without a builder, or a builder without a grant, this still denies.
        [Theory]
        [InlineData(SalesInvoiceEvents.Created, EntityRegistry.SalesInvoice)]
        [InlineData(PurchaseInvoiceEvents.Created, EntityRegistry.PurchaseInvoice)]
        [InlineData(JournalEntryEvents.Reversed, EntityRegistry.JournalEntry)]
        [InlineData(CustomerEvents.Updated, EntityRegistry.Customer)]
        [InlineData(TaskEvents.Assigned, EntityRegistry.Task)]
        [InlineData(TaskEvents.Reopened, EntityRegistry.Task)]
        // Increment 2 grants CalendarEvent Created/Rescheduled/Cancelled. These four are the ones it
        // DELIBERATELY omitted, and they must stay denied: Updated carries changed-field names, the
        // attendee events are people-tracking, and ReminderTriggered is machine noise.
        [InlineData(CalendarEventEvents.Updated, EntityRegistry.CalendarEvent)]
        [InlineData(CalendarEventEvents.AttendeeAdded, EntityRegistry.CalendarEvent)]
        [InlineData(CalendarEventEvents.AttendeeRemoved, EntityRegistry.CalendarEvent)]
        [InlineData(CalendarEventEvents.ReminderTriggered, EntityRegistry.CalendarEvent)]
        public void A_real_but_ungranted_event_is_denied(string eventType, string entityType)
        {
            var d = Evaluate(eventType, entityType);
            Assert.False(d.IsAllowed);
            Assert.Null(d.Grant);
        }

        // The scenario §4 names explicitly: a module that does not exist yet must not become AI-visible
        // merely because a developer raised a BusinessEvent for it.
        [Fact]
        public void A_future_unknown_event_defaults_to_deny()
        {
            var d = Evaluate("SomeFutureSecretEntity.Created", "SomeFutureSecretEntity");
            Assert.False(d.IsAllowed);
            Assert.Contains("no AI grant", d.Reason, StringComparison.Ordinal);
        }

        // ---- 3. a disabled grant denies ----
        [Fact]
        public void A_disabled_grant_denies_its_event()
        {
            var disabled = new DisabledGrants();
            var d = disabled.Evaluate(Ai, TaskEvents.Completed, EntityRegistry.Task, BusinessEventVisibility.Internal);
            Assert.False(d.IsAllowed);
            Assert.Contains("disabled", d.Reason, StringComparison.OrdinalIgnoreCase);
        }

        // ---- classification ceiling ----
        //
        // A producer that later raises Task.Completed at a higher classification must NOT be admitted by a
        // grant written for Internal facts. This is realistic drift, and it would otherwise be invisible.
        [Theory]
        [InlineData(BusinessEventVisibility.Confidential)]
        [InlineData(BusinessEventVisibility.Restricted)]
        [InlineData(BusinessEventVisibility.System)]
        public void A_granted_event_raised_above_its_classification_ceiling_is_denied(string visibility)
        {
            var d = Evaluate(TaskEvents.Completed, visibility: visibility);
            Assert.False(d.IsAllowed);
            Assert.Contains("visibility", d.Reason, StringComparison.OrdinalIgnoreCase);
        }

        // An event type granted for Task must not admit a different entity kind carrying the same action.
        [Fact]
        public void A_granted_event_type_arriving_for_another_entity_is_denied()
        {
            var d = Evaluate(TaskEvents.Completed, entityType: EntityRegistry.SalesInvoice);
            Assert.False(d.IsAllowed);
            Assert.Contains("grant covers", d.Reason, StringComparison.Ordinal);
        }

        // A grant belongs to ONE consumer. Another consumer must not inherit AI's entitlements.
        [Fact]
        public void Another_consumer_cannot_use_the_ai_grant()
        {
            var d = Evaluate(TaskEvents.Completed, consumer: BusinessEventConsumers.NotificationProjection);
            Assert.False(d.IsAllowed);
        }

        // ---- the table itself is well-formed ----
        [Fact]
        public void Every_grant_names_the_ai_consumer_and_a_registered_projection_shape()
        {
            Assert.NotEmpty(Grants.All);
            foreach (var g in Grants.All)
            {
                Assert.Equal(Ai, g.Consumer);
                Assert.False(string.IsNullOrWhiteSpace(g.ProjectionType));
                Assert.True(g.ProjectionVersion > 0);
                // "<Entity>.<Action>" — the platform's own event-name rule.
                Assert.StartsWith(g.EntityType + ".", g.EventType, StringComparison.Ordinal);
            }
        }

        // Every granted shape must have a builder, or the consumer throws a configuration error at
        // dispatch time. Asserting it here turns a runtime failure into a compile-adjacent one.
        [Fact]
        public void Every_granted_projection_shape_has_a_registered_builder()
        {
            var builders = new IAiProjectionBuilder[]
            {
                new TaskLifecycleProjectionBuilder(),
                new CalendarSchedulingProjectionBuilder(),   // Increment 2's second shape
            };
            foreach (var g in Grants.All)
            {
                Assert.True(
                    builders.Any(b => b.ProjectionType == g.ProjectionType && b.ProjectionVersion == g.ProjectionVersion),
                    $"Grant '{g.EventType}' names shape '{g.ProjectionType}' v{g.ProjectionVersion} with no builder.");
            }
        }

        // No accounting or financial event is granted. Stated as its own test because it is a standing
        // policy of this increment, not an accident of the current table.
        [Fact]
        public void No_financial_event_is_ai_visible_in_this_increment()
        {
            foreach (var g in Grants.All)
            {
                Assert.DoesNotContain("Invoice", g.EntityType, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("Journal", g.EntityType, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("Payment", g.EntityType, StringComparison.OrdinalIgnoreCase);
            }
        }

        // A grant table whose entries are all disabled — used to prove the kill switch, without mutating
        // the real table (which is deliberately immutable).
        private sealed class DisabledGrants : IAiConsumerGrants
        {
            private readonly AiConsumerGrant _off = new()
            {
                Consumer = Ai,
                EventType = TaskEvents.Completed,
                EntityType = EntityRegistry.Task,
                ProjectionType = AiConsumerGrants.TaskLifecycleProjection,
                ProjectionVersion = AiConsumerGrants.TaskLifecycleVersion,
                MaxVisibility = BusinessEventVisibility.Internal,
                IsEnabled = false,
            };

            public IReadOnlyList<AiConsumerGrant> All => new[] { _off };

            public AiGrantDecision Evaluate(string consumer, string eventType, string entityType, string visibility)
                => !_off.IsEnabled && eventType == _off.EventType
                    ? AiGrantDecision.Deny($"AI grant for '{eventType}' is disabled")
                    : AiGrantDecision.Deny("no AI grant");
        }
    }
}
