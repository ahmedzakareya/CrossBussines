using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using CrossBuy.BL.Platform;
using Xunit;

namespace CrossBuy.Tests
{
    // =============================================================================================
    // STAGE 2 - VOCABULARY CONVERGENCE, PROVED RATHER THAN ASSERTED.
    //
    // Stage 2 imported entity codes and business-event type strings from the programme line so both
    // histories mean the same thing by the same identifier. Those identifiers are PERSISTED: an event
    // type is written into BusinessEvents and an entity code is stored beside it, so a value that
    // drifts does not fail a build - it silently stops matching rows that already exist.
    //
    // WHAT THESE TESTS ARE FOR, AND WHAT THEY ARE NOT. They do not test that any feature works: no
    // producer, consumer or registration arrived in Stage 2. They test that the VOCABULARY is stable,
    // complete and unambiguous, which is the only thing Stage 2 actually claims.
    //
    // THE VALUES ARE WRITTEN OUT LONGHAND ON PURPOSE. A test that asserts
    // `EntityRegistry.Lead == EntityRegistry.Lead` proves nothing; one that asserts it equals the
    // literal "Lead" is the only shape that catches a rename, which is the failure being guarded.
    // =============================================================================================
    public class Stage2VocabularyConvergenceTests
    {
        private static IReadOnlyDictionary<string, string> Constants(Type type) =>
            type.GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy)
                .Where(f => f.IsLiteral && !f.IsInitOnly && f.FieldType == typeof(string))
                .ToDictionary(f => f.Name, f => (string)f.GetRawConstantValue()!, StringComparer.Ordinal);

        private static IEnumerable<Type> EventFamilies() =>
            typeof(BusinessEventTypes).Assembly.GetTypes()
                .Where(t => t.IsClass && t.IsAbstract && t.IsSealed          // static class
                            && t.Namespace == typeof(BusinessEventTypes).Namespace
                            && t.Name.EndsWith("Events", StringComparison.Ordinal));

        // -----------------------------------------------------------------------------------------
        // 1 + 2. UNIQUENESS
        // -----------------------------------------------------------------------------------------

        [Fact]
        public void No_two_entity_codes_share_a_value()
        {
            var codes = Constants(typeof(EntityRegistry))
                .Where(p => !p.Key.StartsWith("Scope", StringComparison.Ordinal))
                .ToList();

            var collisions = codes.GroupBy(p => p.Value, StringComparer.Ordinal)
                                  .Where(g => g.Count() > 1)
                                  .Select(g => g.Key + " <- " + string.Join(", ", g.Select(x => x.Key)))
                                  .ToList();

            Assert.True(collisions.Count == 0,
                "Two entity codes resolve to one string, so a stored code is ambiguous: "
                + string.Join(" | ", collisions));
        }

        [Fact]
        public void No_two_business_event_types_share_a_value()
        {
            var all = new List<(string Owner, string Name, string Value)>();
            foreach (var family in EventFamilies())
                foreach (var pair in Constants(family))
                    all.Add((family.Name, pair.Key, pair.Value));

            var collisions = all.GroupBy(x => x.Value, StringComparer.Ordinal)
                                .Where(g => g.Count() > 1)
                                .Select(g => g.Key + " <- " + string.Join(", ", g.Select(x => x.Owner + "." + x.Name)))
                                .ToList();

            // An aliased event type is the failure this stage exists to prevent: two names for one
            // stored string means a consumer subscribing to one silently receives the other's rows.
            Assert.True(collisions.Count == 0,
                "Two event constants resolve to one string: " + string.Join(" | ", collisions));
        }

        [Fact]
        public void Every_event_type_is_prefixed_by_a_registered_entity_code()
        {
            var registered = new HashSet<string>(
                Constants(typeof(EntityRegistry))
                    .Where(p => !p.Key.StartsWith("Scope", StringComparison.Ordinal))
                    .Select(p => p.Value), StringComparer.Ordinal);

            var orphans = new List<string>();
            foreach (var family in EventFamilies())
                foreach (var pair in Constants(family))
                {
                    var prefix = pair.Value.Split('.')[0];
                    // RecordAsync resolves the entity through the registry and THROWS on an unknown
                    // code, so an event whose prefix is unregistered can never be emitted at all.
                    if (!registered.Contains(prefix)) orphans.Add(family.Name + "." + pair.Key + " -> " + pair.Value);
                }

            Assert.True(orphans.Count == 0,
                "These event types name an entity code the registry does not declare: "
                + string.Join(" | ", orphans));
        }

        // -----------------------------------------------------------------------------------------
        // 3. THE TARGET'S OWN VOCABULARY SURVIVED THE IMPORT
        // -----------------------------------------------------------------------------------------

        [Theory]
        // The four families the programme line had DROPPED. Convergence took the union rather than the
        // programme file, so these must still be here - a wholesale file replacement would delete them
        // and every Payment/Receipt/return event already stored would stop matching its own constant.
        [InlineData("Payment", "Payment")]
        [InlineData("Receipt", "Receipt")]
        [InlineData("PlatformEvent", "PlatformEvent")]
        [InlineData("SalesInvoice", "SalesInvoice")]
        [InlineData("Task", "Task")]
        [InlineData("CalendarEvent", "CalendarEvent")]
        public void An_entity_code_this_line_already_owned_keeps_its_exact_value(string name, string expected)
        {
            var codes = Constants(typeof(EntityRegistry));
            Assert.True(codes.ContainsKey(name), $"EntityRegistry.{name} was removed by convergence.");
            Assert.Equal(expected, codes[name]);
        }

        [Fact]
        public void The_event_families_this_line_already_owned_survived_the_import()
        {
            Assert.Equal("Payment.Allocated", PaymentEvents.Allocated);
            Assert.Equal("Payment.Reversed", PaymentEvents.Reversed);
            Assert.Equal("Receipt.Allocated", ReceiptEvents.Allocated);
            Assert.Equal("SalesReturn.Reversed", SalesReturnEvents.Reversed);
            Assert.Equal("PurchaseReturn.Reversed", PurchaseReturnEvents.Reversed);
            Assert.Equal("CalendarEvent.Created", CalendarEventEvents.Created);
        }

        // -----------------------------------------------------------------------------------------
        // 4 + 5. THE IMPORTED IDENTITIES CARRY THE PROGRAMME'S EXACT VALUES
        // -----------------------------------------------------------------------------------------

        [Theory]
        [InlineData("CrmAccount", "CrmAccount")]
        [InlineData("Lead", "Lead")]
        [InlineData("Opportunity", "Opportunity")]
        [InlineData("EmployeeRequest", "EmployeeRequest")]
        [InlineData("LeaveRequest", "LeaveRequest")]
        [InlineData("InventoryApproval", "InventoryApproval")]
        [InlineData("ProjectMilestone", "ProjectMilestone")]
        public void An_imported_entity_code_keeps_the_value_the_programme_line_persisted(string name, string expected)
        {
            var codes = Constants(typeof(EntityRegistry));
            Assert.True(codes.ContainsKey(name), $"EntityRegistry.{name} was not imported by Stage 2.");
            Assert.Equal(expected, codes[name]);
        }

        [Fact]
        public void The_project_milestone_vocabulary_resolves_exactly_and_not_approximately()
        {
            // TAB-6 depends on this pair specifically: the orchestration rule keys on the entity code
            // and the events carry the prefix, so a drift in either makes correlated work unfindable.
            Assert.Equal("ProjectMilestone", EntityRegistry.ProjectMilestone);
            Assert.NotEqual(EntityRegistry.Project, EntityRegistry.ProjectMilestone);

            Assert.True(BusinessEventTypes.TryValidate("ProjectMilestone.Created",
                EntityRegistry.ProjectMilestone, out _));
            Assert.True(BusinessEventTypes.TryValidate("ProjectMilestone.StatusChanged",
                EntityRegistry.ProjectMilestone, out _));
            Assert.True(BusinessEventTypes.TryValidate("ProjectMilestone.Deleted",
                EntityRegistry.ProjectMilestone, out _));
        }

        [Fact]
        public void The_task_hold_vocabulary_resolves_exactly()
        {
            Assert.Equal("Task.HoldRequested", TaskEvents.HoldRequested);
            Assert.Equal("Task.HoldApproved", TaskEvents.HoldApproved);
            Assert.Equal("Task.HoldRejected", TaskEvents.HoldRejected);
            Assert.Equal("Task.HoldResumed", TaskEvents.HoldResumed);

            foreach (var value in new[] { TaskEvents.HoldRequested, TaskEvents.HoldApproved,
                                          TaskEvents.HoldRejected, TaskEvents.HoldResumed })
                Assert.True(BusinessEventTypes.TryValidate(value, EntityRegistry.Task, out _));
        }

        // -----------------------------------------------------------------------------------------
        // 6. UNKNOWN VOCABULARY DOES NOT SILENTLY BECOME KNOWN VOCABULARY
        // -----------------------------------------------------------------------------------------

        [Theory]
        [InlineData("projectmilestone.Created")]   // casing
        [InlineData("ProjectMilestones.Created")]  // plural
        [InlineData("Project.MilestoneCreated")]   // the prefix the platform explicitly refuses
        [InlineData("ProjectMilestone")]           // no action
        public void A_near_miss_event_type_is_refused_rather_than_mapped(string candidate)
        {
            Assert.False(BusinessEventTypes.TryValidate(candidate, EntityRegistry.ProjectMilestone, out _));
        }

        [Fact]
        public void An_imported_code_is_not_yet_a_registered_definition()
        {
            // STAGE BOUNDARY, ASSERTED RATHER THAN DESCRIBED. Stage 2 imported identity only; the
            // EntityDefinition objects were deferred because three of them are timeline-enabled and
            // TimelineEventPresenter here has no arm for them. If a later stage registers one, this
            // test fails and points at the decision - which is the intent, not an obstacle.
            using var host = new PlatformTestHost();
            var registry = host.Registry();

            Assert.False(registry.IsValid(EntityRegistry.ProjectMilestone));
            Assert.False(registry.IsValid(EntityRegistry.Lead));
            Assert.False(registry.IsValid(EntityRegistry.CrmAccount));
        }

        // -----------------------------------------------------------------------------------------
        // 7. THE VALUES SURVIVE A ROUND TRIP THROUGH A STORED PAYLOAD
        // -----------------------------------------------------------------------------------------

        [Fact]
        public void An_event_type_survives_json_round_tripping_byte_for_byte()
        {
            var sample = new[]
            {
                TaskEvents.HoldRequested, PaymentEvents.Allocated,
                "ProjectMilestone.Created", LeadEvents.Converted, OpportunityEvents.Won,
            };

            var json = JsonSerializer.Serialize(sample);
            var back = JsonSerializer.Deserialize<string[]>(json)!;

            Assert.Equal(sample, back);
            // No escaping, no casing policy and no culture may touch a persisted identifier.
            foreach (var value in sample) Assert.Contains("\"" + value + "\"", json, StringComparison.Ordinal);
        }

        // -----------------------------------------------------------------------------------------
        // 8 + 9 + 10. THE VOCABULARY IS INERT
        // -----------------------------------------------------------------------------------------

        [Fact]
        public void Reading_the_vocabulary_needs_no_container_and_no_company()
        {
            // Every value is a compile-time constant, so touching them resolves no service, opens no
            // DbContext and requires no BusinessContext. If any of that were needed, Stage 2 would have
            // introduced runtime behaviour rather than vocabulary.
            var values = new[]
            {
                EntityRegistry.ProjectMilestone, EntityRegistry.InventoryApproval,
                TaskEvents.HoldResumed, InventoryApprovalEvents.Requested,
                LeaveRequestEvents.Submitted, EmployeeRequestEvents.Approved,
                QuotationEvents.AcceptedByCustomer,
            };

            Assert.All(values, v => Assert.False(string.IsNullOrWhiteSpace(v)));
            Assert.Equal(values.Length, values.Distinct(StringComparer.Ordinal).Count());
        }

        [Fact]
        public void Stage_two_introduced_no_company_fallback_and_no_portal_source()
        {
            // The two boundaries Stage 2 was told to hold. BusinessContextSource must still carry no
            // ExternalPortal member: that primitive belongs to the Portal stage, and importing it here
            // would have activated external-portal behaviour as a side effect of a vocabulary wave.
            var sources = Enum.GetNames(typeof(CrossBuy.Models.Platform.BusinessContextSource));
            Assert.DoesNotContain("ExternalPortal", sources);

            // And no imported identifier is a disguised company literal.
            var codes = Constants(typeof(EntityRegistry)).Values;
            Assert.DoesNotContain("1", codes);
        }
    }
}
