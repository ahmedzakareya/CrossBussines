using System;
using System.Linq;
using System.Threading.Tasks;
using CrossBuy.BL;
using CrossBuy.BL.Platform;
using CrossBuy.BL.Portal;
using CrossBuy.Models.Context.Accounting;
using CrossBuy.Models.Context.Inventory;
using CrossBuy.Models.Context.Platform;
using CrossBuy.Models.Context.Portal;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CrossBuy.Tests.Portal
{
    // =============================================================================================
    // THE CUSTOMER'S DECISION, RECORDED — end to end, through every real type.
    //
    // THIS FILE PREVIOUSLY PROVED THE OPPOSITE. It documented why the producer could NOT be written:
    // BusinessEventService.RecordAsync resolves its company through IBusinessContextAccessor, which
    // knew only how to rebuild a context from an EMPLOYEE row, so a portal request threw instead of
    // recording. IBusinessContextFactory.ForExternalPortal closes that seam, and these tests are the
    // evidence that it is closed rather than merely present.
    //
    // NOTHING HERE IS STUBBED THAT MATTERS. Real PortalContextAccessor, real BusinessContextFactory,
    // real BusinessEventService, real SellingService, real EF filters over PlatformTestHost's filtered
    // context with an UNRESOLVED scope — which is exactly what a portal request is, and what every
    // other portal test deliberately avoids in order to test the service's own predicates.
    //
    // The unused SellingService collaborators are null on purpose: SetQuotationStatusAsync touches
    // none of them, and passing elaborate fakes would suggest this path depends on stock, receivables,
    // notifications or currency when it does not.
    // =============================================================================================
    public class PortalQuotationEventTests
    {
        private const int CompanyA = 1, CompanyB = 2;
        private const int AcmeId = 100, BetaId = 200;
        private const string PortalLogin = "u-portal-acme";

        private sealed class Wired : IDisposable
        {
            public PlatformTestHost Host = null!;
            public PortalDataService Portal = null!;
            public IBusinessContextAccessor Contexts = null!;
            public PortalContextAccessor PortalContexts = null!;
            public void Dispose() => Host.Dispose();

            public IQueryable<BusinessEvent> Events =>
                Host.Seed.BusinessEvents.AsNoTracking();
        }

        /// The whole portal request, assembled the way Program.cs assembles it.
        ///
        /// `signedInAs` is SEPARATE from the seeded login on purpose. An earlier version of this helper
        /// used one value for both, so the "unauthenticated caller" test signed in as a user it had just
        /// provisioned and passed while proving the opposite of its name.
        private static Wired Wire(string userId = PortalLogin, int portalCompany = CompanyA,
                                  int portalCustomer = AcmeId, string? signedInAs = null)
        {
            var principal = signedInAs ?? userId;
            var host = new PlatformTestHost(companyId: null);   // "a request that resolved NOBODY"

            host.Seed.Customers.AddRange(
                new Customer { ID = AcmeId, CompanyID = CompanyA, Name = "Acme", ControlAccountId = 1 },
                new Customer { ID = BetaId, CompanyID = CompanyA, Name = "Beta", ControlAccountId = 1 });
            host.Seed.Set<PortalUser>().Add(new PortalUser
            {
                CompanyID = portalCompany, CustomerId = portalCustomer, UserId = userId,
                ContactName = "Acme contact", IsActive = true,
                Capabilities = string.Join(",", PortalCapabilities.Default
                    .Concat(new[] { PortalCapabilities.RespondToQuotations })),
            });
            host.Seed.SaveChanges();

            var factory = new BusinessContextFactory(
                PortalScopeProbe.Http(principal), host.Db, host.Holder,
                NullLogger<BusinessContextFactory>.Instance);
            var contexts = new BusinessContextAccessor(factory);
            var portalContexts = new PortalContextAccessor(
                host.Db, PortalScopeProbe.Http(principal), factory, host.Holder);
            var events = new BusinessEventService(
                host.Db, new EntityRegistry(host.Db), contexts, NullLogger<BusinessEventService>.Instance);
            var selling = new SellingService(host.Db, null!, null!, null!, null!, null!, events);

            return new Wired
            {
                Host = host,
                Portal = new PortalDataService(host.Db, portalContexts, selling),
                Contexts = contexts,
                PortalContexts = portalContexts,
            };
        }

        private static int SeedQuotation(PlatformTestHost host, int customerId, int companyId,
                                         string status = "Sent")
        {
            var q = new Quotation
            {
                CompanyID = companyId, CustomerId = customerId, Status = status, GrandTotal = 1000m,
                QuoteNo = "Q-" + customerId + "-" + status,
                QuoteDate = DateTime.UtcNow.AddDays(-2), ValidUntil = DateTime.UtcNow.AddDays(20),
            };
            host.Seed.Quotations.Add(q);
            host.Seed.SaveChanges();
            return q.ID;
        }

        // ---- 1. the two events, exactly once each ------------------------------------------------

        [Fact]
        public async Task Accepting_records_exactly_one_AcceptedByCustomer_event()
        {
            using var w = Wire();
            var id = SeedQuotation(w.Host, AcmeId, CompanyA);

            Assert.True((await w.Portal.RespondToQuotationAsync(id, accept: true)).Ok);

            var e = Assert.Single(await w.Events.Where(x => x.EntityId == id).ToListAsync());
            Assert.Equal(QuotationEvents.AcceptedByCustomer, e.EventType);
            Assert.Equal(EntityRegistry.Quotation, e.EntityType);
            Assert.Equal("Accepted", (await w.Host.Seed.Quotations.AsNoTracking().SingleAsync(q => q.ID == id)).Status);
        }

        [Fact]
        public async Task Rejecting_records_exactly_one_RejectedByCustomer_event()
        {
            using var w = Wire();
            var id = SeedQuotation(w.Host, AcmeId, CompanyA);

            Assert.True((await w.Portal.RespondToQuotationAsync(id, accept: false)).Ok);

            var e = Assert.Single(await w.Events.Where(x => x.EntityId == id).ToListAsync());
            Assert.Equal(QuotationEvents.RejectedByCustomer, e.EventType);
        }

        // ---- 2. the event's tenancy and provenance -----------------------------------------------

        [Fact]
        public async Task The_events_company_is_the_portal_users_company_and_the_context_is_external()
        {
            // THE CENTRE OF THE WHOLE SEAM. The company on the event comes from the ACTIVE PortalUser
            // row, resolved from the authenticated principal — not from a route, a form, a CustomerId
            // or the companyId argument the module was called with.
            using var w = Wire();
            var id = SeedQuotation(w.Host, AcmeId, CompanyA);

            Assert.True((await w.Portal.RespondToQuotationAsync(id, accept: true)).Ok);

            var e = Assert.Single(await w.Events.Where(x => x.EntityId == id).ToListAsync());
            Assert.Equal(CompanyA, e.CompanyID);

            var ctx = await w.Contexts.TryGetCurrentAsync();
            Assert.NotNull(ctx);
            Assert.Equal(BusinessContextSource.ExternalPortal, ctx!.Source);
            Assert.False(ctx.IsSystem);          // the escalation this seam exists to make impossible
            Assert.Null(ctx.EmployeeId);         // a customer is not staff
            Assert.Equal(CompanyA, ctx.CompanyId);
            Assert.Equal(PortalLogin, ctx.UserId);

            // And no actor employee is stamped on the event, because there is no employee.
            Assert.Null(e.ActorEmployeeId);
        }

        // ---- 3. idempotency and terminal conflicts -----------------------------------------------

        [Fact]
        public async Task The_same_decision_twice_records_one_event_total()
        {
            using var w = Wire();
            var id = SeedQuotation(w.Host, AcmeId, CompanyA);

            Assert.True((await w.Portal.RespondToQuotationAsync(id, accept: true)).Ok);
            Assert.True((await w.Portal.RespondToQuotationAsync(id, accept: true)).Ok);   // idempotent

            Assert.Single(await w.Events.Where(x => x.EntityId == id).ToListAsync());
        }

        [Fact]
        public async Task The_same_rejection_twice_records_one_event_total()
        {
            using var w = Wire();
            var id = SeedQuotation(w.Host, AcmeId, CompanyA);

            Assert.True((await w.Portal.RespondToQuotationAsync(id, accept: false)).Ok);
            Assert.True((await w.Portal.RespondToQuotationAsync(id, accept: false)).Ok);

            Assert.Single(await w.Events.Where(x => x.EntityId == id).ToListAsync());
        }

        [Theory]
        [InlineData(true, false)]    // accept, then try to reject
        [InlineData(false, true)]    // reject, then try to accept
        public async Task A_conflicting_second_decision_is_refused_and_records_no_second_event(
            bool first, bool second)
        {
            using var w = Wire();
            var id = SeedQuotation(w.Host, AcmeId, CompanyA);

            Assert.True((await w.Portal.RespondToQuotationAsync(id, accept: first)).Ok);
            var reversal = await w.Portal.RespondToQuotationAsync(id, accept: second);

            Assert.False(reversal.Ok);
            Assert.Equal(PortalDecisionResult.AlreadyDecided, reversal.Code);
            Assert.Single(await w.Events.Where(x => x.EntityId == id).ToListAsync());
        }

        // ---- 4. everything that must record NOTHING ----------------------------------------------

        [Fact]
        public async Task Another_customers_quotation_records_nothing()
        {
            using var w = Wire();
            var foreign = SeedQuotation(w.Host, BetaId, CompanyA);

            var result = await w.Portal.RespondToQuotationAsync(foreign, accept: true);

            Assert.False(result.Ok);
            Assert.Equal(PortalDecisionResult.NotFound, result.Code);
            Assert.Empty(await w.Events.ToListAsync());
            Assert.Equal("Sent", (await w.Host.Seed.Quotations.AsNoTracking().SingleAsync(q => q.ID == foreign)).Status);
        }

        [Fact]
        public async Task Another_companys_quotation_records_nothing()
        {
            using var w = Wire();
            var otherCompany = SeedQuotation(w.Host, AcmeId, CompanyB);

            Assert.False((await w.Portal.RespondToQuotationAsync(otherCompany, accept: true)).Ok);
            Assert.Empty(await w.Events.ToListAsync());
        }

        [Theory]
        [InlineData("Draft")]
        [InlineData("Expired")]
        [InlineData("Converted")]
        public async Task A_quotation_in_a_state_the_customer_cannot_answer_records_nothing(string status)
        {
            using var w = Wire();
            var id = SeedQuotation(w.Host, AcmeId, CompanyA, status);

            Assert.False((await w.Portal.RespondToQuotationAsync(id, accept: true)).Ok);
            Assert.Empty(await w.Events.ToListAsync());
            Assert.Equal(status, (await w.Host.Seed.Quotations.AsNoTracking().SingleAsync(q => q.ID == id)).Status);
        }

        [Fact]
        public async Task An_unauthenticated_caller_records_nothing()
        {
            // A real portal user EXISTS; the caller simply is not them.
            using var w = Wire(signedInAs: "nobody-has-this-login");
            var id = SeedQuotation(w.Host, AcmeId, CompanyA);

            Assert.False((await w.Portal.RespondToQuotationAsync(id, accept: true)).Ok);
            Assert.Empty(await w.Events.ToListAsync());
        }

        // ---- 5. staff are not customers -----------------------------------------------------------

        [Fact]
        public async Task Staff_moving_the_same_status_record_no_customer_decision_event()
        {
            // A quotation can be accepted by staff on the customer's behalf. That is a DIFFERENT fact,
            // and an event that could not tell the two apart would be useless to anyone auditing who
            // decided — which is why the authorized names carry ByCustomer and why the module records
            // nothing unless the caller says the customer decided.
            // Its OWN host, resolved as company 1: a staff request is a staff scope, and reusing the
            // portal's deliberately-unresolved one would have the query filters hide the row and the
            // test would pass for the wrong reason.
            using var host = new PlatformTestHost(companyId: CompanyA);
            var id = SeedQuotation(host, AcmeId, CompanyA);

            var events = new BusinessEventService(
                host.Db, new EntityRegistry(host.Db),
                new StubContextAccessor(PlatformTestHost.DefaultContext(CompanyA)),
                NullLogger<BusinessEventService>.Instance);
            var staffSelling = new SellingService(host.Db, null!, null!, null!, null!, null!, events);

            Assert.True((await staffSelling.SetQuotationStatusAsync(CompanyA, id, "Accepted")).ok);

            Assert.Equal("Accepted", (await host.Seed.Quotations.AsNoTracking().SingleAsync(q => q.ID == id)).Status);
            Assert.Empty(await host.Seed.BusinessEvents.AsNoTracking().ToListAsync());
        }

        [Fact]
        public async Task A_customer_decision_with_no_recorder_refuses_rather_than_transitioning_silently()
        {
            // The asymmetry that matters: moving the state while recording nothing is precisely what
            // the outbox exists to prevent, so the module refuses instead of half-doing it.
            using var host = new PlatformTestHost(companyId: CompanyA);
            var id = SeedQuotation(host, AcmeId, CompanyA);

            var recorderless = new SellingService(host.Db, null!, null!, null!, null!, null!, events: null);
            var (ok, error) = await recorderless.SetQuotationStatusAsync(CompanyA, id, "Accepted", customerDecision: true);

            Assert.False(ok);
            Assert.NotNull(error);
            Assert.Equal("Sent", (await host.Seed.Quotations.AsNoTracking().SingleAsync(q => q.ID == id)).Status);
            Assert.Empty(await host.Seed.BusinessEvents.AsNoTracking().ToListAsync());

            // NON-VACUITY: the same call WITH a recorder succeeds, so the refusal above is about the
            // missing recorder and not about the row, the state or the scope.
            var withRecorder = new SellingService(host.Db, null!, null!, null!, null!, null!,
                new BusinessEventService(host.Db, new EntityRegistry(host.Db),
                    new StubContextAccessor(PlatformTestHost.DefaultContext(CompanyA)),
                    NullLogger<BusinessEventService>.Instance));
            Assert.True((await withRecorder.SetQuotationStatusAsync(CompanyA, id, "Accepted", customerDecision: true)).ok);
            Assert.Single(await host.Seed.BusinessEvents.AsNoTracking().ToListAsync());
        }

        // ---- 6. the vocabulary ---------------------------------------------------------------------

        [Fact]
        public void Only_the_two_authorized_quotation_actions_are_declared_and_both_validate()
        {
            BusinessEventTypes.Validate(QuotationEvents.AcceptedByCustomer, EntityRegistry.Quotation);
            BusinessEventTypes.Validate(QuotationEvents.RejectedByCustomer, EntityRegistry.Quotation);

            var declared = typeof(QuotationEvents)
                .GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
                .Select(f => (string)f.GetValue(null)!)
                .ToArray();
            Assert.Equal(2, declared.Length);

            // The flat spelling a brief might reach for is refused by the platform's own validator.
            Assert.False(BusinessEventTypes.TryValidate(
                "QuotationAcceptedByCustomer", EntityRegistry.Quotation, out _));
        }
    }
}
