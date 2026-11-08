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

        // ==========================================================================================
        // THE REGISTRY'S OWN FAILING PATH
        //
        // Found by TAB-1 on 2026-11-08, and pinned here from OUTSIDE on purpose. BuildUrl used to call
        // GetDefinition, which raises EntityCodeNotRegisteredException, so a single retired entity code
        // took down whichever caller had not guarded separately - and five of the eight call sites had
        // not (CommNotificationService, NotificationProjectionConsumer, LegacyReturnTimeline,
        // MoneyDocLegacyTimeline and the legacy timeline adapters). Every one of those holds HISTORY:
        // notifications, mentions and timeline rows outlive the vocabulary they were written against,
        // so this was reached by ordinary ageing, not by a bug.
        //
        // WHY THE TEST LIVES HERE AND NOT AT A CALL SITE: the guard is now INSIDE BuildUrl, where it is
        // invisible to anyone reading a caller. The next person who "simplifies" it back to
        // GetDefinition will see no local reason not to. This is the fact that tells them.
        // ==========================================================================================

        [Fact]
        public void An_unregistered_code_returns_null_rather_than_throwing()
        {
            // The declared contract was always "null when the type has no screen". An unregistered type
            // has no screen, so null is the stated behaviour finally being true - not a new leniency.
            var registry = Registry();

            Assert.Null(registry.BuildUrl("RetiredThingNobodyRegistered", 7));
            Assert.Null(registry.BuildUrl("", 7));
        }

        [Fact]
        public void One_unknown_code_costs_one_link_and_not_the_batch()
        {
            // THE DISTINCTION THE FIX DEFENDS. A throw does not cost the row its link, it costs the
            // READER the whole panel: one retired code in a page of thirty notifications and the caller
            // catches, degrades and shows nothing. Resolving a mixed batch must yield a dead link for
            // the unknown code and a working one for every neighbour.
            var registry = Registry();

            var codes = new[] { "SalesInvoice", "RetiredThingNobodyRegistered", "Task" };
            var urls = codes.Select(c => registry.BuildUrl(c, 4120)).ToArray();

            Assert.Equal("/Accounting/SalesInvoiceDetail?id=4120", urls[0]);
            Assert.Null(urls[1]);
            Assert.Equal("/Tasks/Detail/4120", urls[2]);
        }
    }
}
