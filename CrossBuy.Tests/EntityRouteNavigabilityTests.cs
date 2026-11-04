using CrossBuy.BL.Platform;
using CrossBuy.Models.Platform;
using Xunit;

namespace CrossBuy.Tests
{
    // =================================================================================================
    // NAVIGABILITY OF A REGISTERED ENTITY — can a row the platform emits actually be opened?
    //
    // EntityRegistry.BuildUrl returns NULL for an entity whose RouteTemplate is null. That is correct
    // behaviour and the registry is right to do it: a destination that cannot be addressed honestly is
    // better left absent than guessed at. Two entities are in that position today and BOTH give their
    // reason in the registry itself:
    //
    //   ProjectBilling - "Its current screen also requires BOTH project id and billing id, but this
    //                     resolver only has the billing id; do not emit a URL that omits the project
    //                     context."  BuildUrl substitutes a single {id}; it structurally cannot address
    //                     a child document that needs its parent's id too.
    //   CalendarEvent  - "Calendar has no event-detail route yet. A generic calendar list URL is not a
    //                     deep link to this event, so leave the destination absent until the owning
    //                     module supplies one."
    //
    // THE RISK THIS SUITE EXISTS FOR is downstream, not here. An entity that SupportsTimeline produces
    // rows on navigable surfaces - the Workspace activity feed, attention list and metric tiles. A
    // consumer that renders a null destination as a link anyway ships a control that looks clickable
    // and goes nowhere. So the set of "timeline-enabled but unaddressable" entities must stay SMALL,
    // CLOSED and DELIBERATE: adding a third is a decision somebody makes on purpose, not a side effect
    // of registering an entity.
    //
    // DATABASE-FREE BY CONSTRUCTION: Definitions is a static list and GetDefinitions/BuildUrl never
    // dereference the CrossDbContext, so the registry is constructed with a null context on purpose.
    // If that ever stops being true this suite will NullReference rather than quietly open a database.
    // =================================================================================================
    public class EntityRouteNavigabilityTests
    {
        private static EntityRegistry Registry() => new EntityRegistry(null!);

        private static IReadOnlyList<EntityDefinition> Definitions() => Registry().GetDefinitions();

        // ==========================================================================================
        // The closed set
        // ==========================================================================================

        [Fact]
        public void Timeline_entities_without_a_route_are_a_closed_documented_set()
        {
            // EXACT equality, so this fails in BOTH directions. Register a third timeline entity with
            // no route and it fails: say why in the registry and add it here. Give ProjectBilling or
            // CalendarEvent a real route and it ALSO fails: delete the exemption rather than leave a
            // list that quietly describes a world that moved on. A one-directional assertion here
            // would rot into a comment.
            var expected = new[] { EntityRegistry.CalendarEvent, EntityRegistry.ProjectBilling };

            var actual = Definitions()
                .Where(d => d.SupportsTimeline && string.IsNullOrEmpty(d.RouteTemplate))
                .Select(d => d.Code)
                .OrderBy(c => c, StringComparer.Ordinal)
                .ToArray();

            Assert.Equal(expected, actual);
        }

        // ==========================================================================================
        // What the registry hands a consumer
        // ==========================================================================================

        [Fact]
        public void BuildUrl_returns_null_for_every_routeless_entity()
        {
            // The source of the dead link. Pinning it is what makes "the consumer must handle null" a
            // contract rather than an observation - a consumer may not assume a destination exists.
            var registry = Registry();

            foreach (var def in Definitions().Where(d => string.IsNullOrEmpty(d.RouteTemplate)))
            {
                Assert.Null(registry.BuildUrl(def.Code, 42));
            }
        }

        [Fact]
        public void A_templated_entity_builds_a_url_carrying_the_id()
        {
            var registry = Registry();

            foreach (var def in Definitions().Where(d =>
                         !string.IsNullOrEmpty(d.RouteTemplate) && d.RouteTemplate!.Contains("{id}")))
            {
                var url = registry.BuildUrl(def.Code, 42);

                Assert.NotNull(url);
                Assert.Contains("42", url!, StringComparison.Ordinal);
                Assert.DoesNotContain("{id}", url!, StringComparison.Ordinal);
            }
        }

        [Fact]
        public void A_list_only_template_is_returned_unchanged()
        {
            // A template with no {id} is a screen that has no per-record view - the registry returns it
            // as-is rather than inventing a query string the screen would ignore.
            var registry = Registry();

            foreach (var def in Definitions().Where(d =>
                         !string.IsNullOrEmpty(d.RouteTemplate) && !d.RouteTemplate!.Contains("{id}")))
            {
                Assert.Equal(def.RouteTemplate, registry.BuildUrl(def.Code, 42));
            }
        }

        // ==========================================================================================
        // The shape of the escape hatch nobody should take
        // ==========================================================================================

        [Fact]
        public void No_entity_is_routed_at_the_workspace_dashboard()
        {
            // The tempting "fix" for an unaddressable entity is to point it at a page that definitely
            // exists. That converts a visibly missing destination into an invisible wrong one: the user
            // clicks a progress billing and lands on their own dashboard with no error. Absent beats
            // misleading, so no entity may be routed at a generic landing page.
            foreach (var def in Definitions().Where(d => !string.IsNullOrEmpty(d.RouteTemplate)))
            {
                Assert.False(
                    def.RouteTemplate!.StartsWith("/Workspace", StringComparison.OrdinalIgnoreCase),
                    $"{def.Code} is routed at the Workspace dashboard, which is not a record destination.");
            }
        }
    }
}
