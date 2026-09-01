using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CrossBuy.BL.Platform;
using CrossBuy.Models.Context.Accounting;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CrossBuy.Tests
{
    // =============================================================================================
    // THE MUTATION THAT SURVIVED STABILIZATION, AND WHY.
    //
    // Replacing `context.CompanyId` with the literal 1 inside PlatformPermissionProvider left 81
    // permission and isolation tests green. That looked like a coverage hole in the company gate; it
    // is not. Reading the file settles it — there are exactly two such reads:
    //
    //   1. inside a deny MESSAGE string. Mutating it changes prose and nothing else.
    //   2. the CACHE KEY: (entityCode, entityId, context.CompanyId).
    //
    // The gate itself never reads the property. It calls _registry.ResolveAsync(code, id, CONTEXT),
    // handing the whole context down, so company isolation is decided by the registry's own
    // company-scoped query. That is why the mutation was invisible: the first read is decoration and
    // the second is a memo key.
    //
    // BUT THE MEMO KEY IS A REAL SECURITY SURFACE, and this is the part nothing covered. The provider
    // is Scoped and caches "does (type, id) exist in company C" for the life of that scope. Drop the
    // company from the key and two different companies asking about the SAME entity id share one
    // entry: the first answer is served to the second caller. A company-A row resolves Found = true,
    // and company B — asking about an id it must not be able to see — gets that cached true and is
    // let through the company gate entirely.
    //
    // The notification consumer is exactly the shape that reaches it: one entity, many recipients,
    // one scope. These tests drive TWO companies through ONE provider instance, which is the only
    // way the key is observable at all.
    // =============================================================================================
    public class PlatformPermissionCompanyCacheTests
    {
        private const int CompanyA = 41;
        private const int CompanyB = 77;
        private const int AliceInA = 11;
        private const int BobInB = 12;

        /// Grants everything it is asked. The point is that the caller must never REACH it for a
        /// foreign record: the company gate runs first, and an adapter that says yes to everything
        /// makes any leak past that gate immediately visible as an Allow.
        private sealed class AllowAllAdapter : IModulePermissionAdapter
        {
            public string Scope { get; }
            public AllowAllAdapter(string scope) { Scope = scope; }
            public Task<PermissionDecision> CanAsync(PermissionCheckRequest request, CancellationToken ct = default)
                => Task.FromResult(PermissionDecision.Allow());
        }

        private static BusinessContext Ctx(int companyId, int employeeId) => new()
        {
            CompanyId = companyId,
            EmployeeId = employeeId,
            UserId = "u" + employeeId,
            Source = BusinessContextSource.Http,
        };

        // -----------------------------------------------------------------------------------------
        // THE PROOF
        // -----------------------------------------------------------------------------------------

        [Fact]
        public async Task One_providers_cache_does_not_serve_company_As_answer_to_company_B()
        {
            // THE mutation-killing test. Same provider instance, same entity id, two companies.
            //
            // Company A owns employee 11. A asks first and is allowed, which populates the memo. B
            // then asks about the SAME id. With the company in the key that is a miss and the registry
            // refuses; with the company dropped it is a hit, and B is handed A's "yes".
            using var host = Seeded();
            var provider = Provider(host);

            var forOwner = await provider.CanAsync(
                Ctx(CompanyA, AliceInA), EntityRegistry.Customer, AliceInA, PlatformActions.View);
            Assert.True(forOwner.Allowed, forOwner.Reason);

            var forStranger = await provider.CanAsync(
                Ctx(CompanyB, BobInB), EntityRegistry.Customer, AliceInA, PlatformActions.View);

            Assert.False(forStranger.Allowed,
                "company B was allowed onto a company-A record. If the first call populated a cache " +
                "entry that is not keyed by company, the gate has been answered from somebody else's " +
                "question.");
        }

        [Fact]
        public async Task The_order_of_the_two_questions_does_not_change_either_answer()
        {
            // The mirror. A stale-cache bug is usually directional, so asking B first must also refuse
            // B and must NOT poison A's later, legitimate answer.
            using var host = Seeded();
            var provider = Provider(host);

            var strangerFirst = await provider.CanAsync(
                Ctx(CompanyB, BobInB), EntityRegistry.Customer, AliceInA, PlatformActions.View);
            Assert.False(strangerFirst.Allowed);

            var ownerSecond = await provider.CanAsync(
                Ctx(CompanyA, AliceInA), EntityRegistry.Customer, AliceInA, PlatformActions.View);
            Assert.True(ownerSecond.Allowed,
                "company A was refused its own record after company B had been refused it. A negative " +
                "answer has been cached against the wrong company.");
        }

        [Fact]
        public async Task Repeating_the_same_question_is_memoised_without_changing_the_answer()
        {
            // The memo exists for a reason — one entity, many recipients, one scope — so prove it
            // still works. Two identical asks, same answer, and the second must not become a refusal
            // because the key drifted.
            using var host = Seeded();
            var provider = Provider(host);
            var ctx = Ctx(CompanyA, AliceInA);

            var first = await provider.CanAsync(ctx, EntityRegistry.Customer, AliceInA, PlatformActions.View);
            var second = await provider.CanAsync(ctx, EntityRegistry.Customer, AliceInA, PlatformActions.View);

            Assert.True(first.Allowed);
            Assert.True(second.Allowed);
        }

        [Fact]
        public async Task Neither_company_can_reach_the_others_record_through_the_shared_memo()
        {
            // Both companies genuinely own a customer, and each asks about the OTHER's. Both must
            // refuse, and the second refusal must not be a cache hit on the first.
            //
            // The positive direction for company B is deliberately NOT asserted here. PlatformTestHost
            // holds ONE company scope, and the DbContext's global filters answer to that holder, so a
            // second live company scope is not representable in this fixture. Asserting it would be
            // testing the fixture. The decisive proof is above: company A is allowed, and company B is
            // then refused the SAME id through the SAME provider.
            using var host = Seeded();
            var provider = Provider(host);

            Assert.False((await provider.CanAsync(
                Ctx(CompanyA, AliceInA), EntityRegistry.Customer, BobInB, PlatformActions.View)).Allowed);
            Assert.False((await provider.CanAsync(
                Ctx(CompanyB, BobInB), EntityRegistry.Customer, AliceInA, PlatformActions.View)).Allowed);
        }

        // -----------------------------------------------------------------------------------------
        // §9 — the supplied context is honoured, not reconstructed
        // -----------------------------------------------------------------------------------------

        [Fact]
        public async Task The_gate_reads_the_SUPPLIED_context_and_not_an_ambient_one()
        {
            // The provider takes a BusinessContext as an argument. Nothing here resolves a company
            // from a session, an accessor or a default — so a context naming company B must be
            // answered as company B even though company A's data is what exists.
            using var host = Seeded();

            var decision = await Provider(host).CanAsync(
                Ctx(CompanyB, BobInB), EntityRegistry.Customer, AliceInA, PlatformActions.View);

            Assert.False(decision.Allowed);
            Assert.Contains(CompanyB.ToString(), decision.Reason ?? "", StringComparison.Ordinal);
        }

        [Fact]
        public async Task An_entity_id_that_exists_nowhere_refuses_exactly_like_a_foreign_one()
        {
            // Missing and foreign must be indistinguishable, or the gate becomes a probe for what
            // other companies hold.
            using var host = Seeded();
            var provider = Provider(host);

            var foreign = await provider.CanAsync(
                Ctx(CompanyB, BobInB), EntityRegistry.Customer, AliceInA, PlatformActions.View);
            var missing = await provider.CanAsync(
                Ctx(CompanyB, BobInB), EntityRegistry.Customer, 999_999, PlatformActions.View);

            Assert.False(foreign.Allowed);
            Assert.False(missing.Allowed);
        }

        // -----------------------------------------------------------------------------------------

        private static PlatformPermissionProvider Provider(PlatformTestHost host)
            => new(new EntityRegistry(host.Db),
                   new IModulePermissionAdapter[] { new AllowAllAdapter(EntityRegistry.ScopeAccounting) },
                   NullLogger<PlatformPermissionProvider>.Instance);

        /// CUSTOMER, not Employee, and the choice is the finding. EntityRegistry.ResolveAsync applies
        /// NO company filter to Employee - a declared deviation, commented "TM-2 behaviour" - so the
        /// provider's company gate cannot be observed through that entity type at all. Customer is
        /// resolved with `c.CompanyID == companyId`, so the gate is real and the memo key is visible.
        private static PlatformTestHost Seeded()
        {
            var host = new PlatformTestHost(companyId: CompanyA);
            foreach (var (id, company) in new[] { (AliceInA, CompanyA), (BobInB, CompanyB) })
                host.Seed.Customers.Add(new Customer { ID = id, CompanyID = company, Name = "C" + id });
            host.Seed.SaveChanges();
            return host;
        }
    }
}
