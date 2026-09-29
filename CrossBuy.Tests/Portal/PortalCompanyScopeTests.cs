using System;
using System.Linq;
using System.Threading.Tasks;
using CrossBuy.BL.Platform;
using CrossBuy.BL.Portal;
using CrossBuy.Models.Context.Portal;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CrossBuy.Tests.Portal
{
    /// The one piece of plumbing these tests need: an authenticated principal. Nothing else about a
    /// portal request matters to identity resolution, which is the point of the design.
    internal static class PortalScopeProbe
    {
        /// A REAL BusinessContextFactory over this host's holder and context. Real, not a stub: the
        /// whole point of the seam is that ForExternalPortal publishes the company to the holder the
        /// query filters read AND binds the context the event recorder resolves, and a stub would
        /// assert neither.
        public static CrossBuy.BL.Platform.IBusinessContextFactory Contexts(
            PlatformTestHost host, string userId)
            => new CrossBuy.BL.Platform.BusinessContextFactory(
                Http(userId), host.Db, host.Holder,
                Microsoft.Extensions.Logging.Abstractions.NullLogger<CrossBuy.BL.Platform.BusinessContextFactory>.Instance);

        public static Microsoft.AspNetCore.Http.IHttpContextAccessor Http(string userId)
        {
            var ctx = new Microsoft.AspNetCore.Http.DefaultHttpContext
            {
                User = new System.Security.Claims.ClaimsPrincipal(
                    new System.Security.Claims.ClaimsIdentity(
                        new[] { new System.Security.Claims.Claim(
                            System.Security.Claims.ClaimTypes.NameIdentifier, userId) },
                        "TestPortal")),
            };
            return new Microsoft.AspNetCore.Http.HttpContextAccessor { HttpContext = ctx };
        }
    }

    // =============================================================================================
    // THE CONTEXT A PORTAL REQUEST REALLY GETS.
    //
    // Every other portal test hands PortalDataService the CROSS-COMPANY context on purpose, so that
    // the service's own two predicates are what is under test rather than an ambient filter. That is
    // the right call for an isolation test and it has one consequence nobody had measured: not one of
    // them runs against the context production actually builds.
    //
    // Production builds a FILTERED one. CrossDbContext reads ICompanyScopeHolder, and the holder is
    // populated as a side effect of resolving a BusinessContext - which BusinessContextFactory does by
    // finding an EMPLOYEE row for the signed-in user. A portal customer has no Employee row, so
    // resolution returns null at `if (employeeId == null) return null`, the holder is never Set, and
    // FilterCompanyId stays 0. Quotation, SalesInvoice and Customer are all pilot-filtered entities.
    //
    // These tests state that fact as behaviour rather than as an argument about code. PlatformTestHost
    // already models it exactly: `new PlatformTestHost(companyId: null)` is documented as "a request
    // that resolved NOBODY", and `Platform.Db` is the filtered context that scope produces.
    // =============================================================================================
    public class PortalCompanyScopeTests
    {
        /// The service as production wires it: the REQUEST context, not the seeding one.
        private static PortalDataService AsRequest(PortalHost host)
            => new(host.Platform.Db, host.Contexts);

        [Fact]
        public async Task A_portal_request_resolves_no_company_scope_so_every_read_returns_nothing()
        {
            // PRODUCT DEFECT, not a test artefact. The customer is real, the quotation is theirs, the
            // capability is held, and the portal still shows an empty screen - which is exactly the
            // failure mode the scope holder documents: "Company-scoped entities will return no rows for
            // this request."
            using var host = new PortalHost();
            host.SeedQuotation(PortalHost.AcmeId, PortalHost.CompanyA);
            host.SeedInvoice(PortalHost.AcmeId, PortalHost.CompanyA);
            host.ActAs(PortalHost.AcmeId, PortalHost.CompanyA);

            // The scope really is unresolved - the precondition, asserted so this cannot pass for some
            // other reason later.
            Assert.False(host.Platform.Holder.IsResolved);
            Assert.Equal(0, host.Platform.Holder.FilterCompanyId);

            var service = AsRequest(host);

            Assert.Empty(await service.QuotationsAsync());
            Assert.Empty(await service.InvoicesAsync());

            // And the home screen cannot even name the customer, so it renders the no-access refusal to
            // somebody who has perfectly good access.
            Assert.Null(await service.HomeAsync());
        }

        [Fact]
        public async Task The_same_reads_succeed_the_moment_the_scope_is_pinned_to_the_portal_company()
        {
            // THE CONTROL, and the shape of the fix. Nothing about the portal's own logic changes here:
            // the same service, the same identity, the same rows. The only difference is that the scope
            // holder has been told which company this request operates as - the one thing
            // BusinessContextFactory does for an employee and cannot do for a customer.
            //
            // Note what is NOT being asked for: no cross-company bypass. The holder is PINNED, so the
            // query filter stays fully in force and the portal's own CompanyID/CustomerId predicates
            // still run on top of it. This is the storefront's arrangement, which CompanyQueryFilters
            // describes for Item: "it is not an exception to the filter, it is a caller of it."
            using var host = new PortalHost();
            var id = host.SeedQuotation(PortalHost.AcmeId, PortalHost.CompanyA);
            host.ActAs(PortalHost.AcmeId, PortalHost.CompanyA);

            host.Platform.Holder.Set(PortalHost.CompanyA, null);

            var listed = await AsRequest(host).QuotationsAsync();
            Assert.Equal(id, Assert.Single(listed).Id);
        }

        [Fact]
        public async Task A_pinned_scope_is_not_a_bypass_and_still_hides_the_other_company()
        {
            // The pin must not become a skeleton key. With the scope pinned to company A, a portal
            // identity that somehow named company B reads nothing at all - the ambient filter refuses
            // it before the portal's own predicate is even reached.
            using var host = new PortalHost();
            host.SeedQuotation(PortalHost.OtherCoCustomerId, PortalHost.CompanyB);
            host.Platform.Holder.Set(PortalHost.CompanyA, null);

            host.ActAs(PortalHost.OtherCoCustomerId, PortalHost.CompanyB);

            Assert.Empty(await AsRequest(host).QuotationsAsync());
            Assert.False(host.Platform.Holder.AllowsCrossCompany);
        }

        [Fact]
        public async Task The_real_accessor_pins_the_scope_it_resolved_so_the_filtered_reads_work()
        {
            // THE FIX, end to end and through the REAL accessor rather than the stub the other files
            // use. A PortalUser row exists, nothing else in the request resolved a company, and after
            // the accessor answers, the filtered context reads the customer's own quotation.
            using var host = new PortalHost();
            var id = host.SeedQuotation(PortalHost.AcmeId, PortalHost.CompanyA);
            host.Platform.Seed.Set<PortalUser>().Add(new PortalUser
            {
                CompanyID = PortalHost.CompanyA, CustomerId = PortalHost.AcmeId,
                UserId = "portal-acme", ContactName = "Acme contact", IsActive = true,
            });
            await host.Platform.Seed.SaveChangesAsync();

            var accessor = new PortalContextAccessor(
                host.Platform.Db, PortalScopeProbe.Http("portal-acme"),
                PortalScopeProbe.Contexts(host.Platform, "portal-acme"), host.Platform.Holder);

            var resolved = await accessor.TryGetCurrentAsync();

            Assert.NotNull(resolved);
            Assert.Equal(PortalHost.CompanyA, resolved!.CompanyId);
            Assert.True(host.Platform.Holder.IsResolved);
            Assert.Equal(PortalHost.CompanyA, host.Platform.Holder.FilterCompanyId);
            Assert.False(host.Platform.Holder.AllowsCrossCompany);

            // NON-VACUOUS: the pin is worth nothing unless the filtered read now succeeds.
            var listed = await new PortalDataService(host.Platform.Db, accessor).QuotationsAsync();
            Assert.Equal(id, Assert.Single(listed).Id);
        }

        [Fact]
        public async Task An_identity_that_disagrees_with_an_already_pinned_company_resolves_to_nothing()
        {
            // The holder refuses a second tenant in one scope by throwing; the accessor turns that into
            // NO IDENTITY rather than letting the exception escape or, far worse, proceeding with a
            // context whose company the filter disagrees with.
            using var host = new PortalHost();
            host.Platform.Holder.Set(PortalHost.CompanyB, null);
            host.Platform.Seed.Set<PortalUser>().Add(new PortalUser
            {
                CompanyID = PortalHost.CompanyA, CustomerId = PortalHost.AcmeId,
                UserId = "portal-acme", ContactName = "Acme contact", IsActive = true,
            });
            await host.Platform.Seed.SaveChangesAsync();

            var accessor = new PortalContextAccessor(
                host.Platform.Db, PortalScopeProbe.Http("portal-acme"),
                PortalScopeProbe.Contexts(host.Platform, "portal-acme"), host.Platform.Holder);

            Assert.Null(await accessor.TryGetCurrentAsync());
        }

        [Fact]
        public void The_scope_holder_refuses_a_second_company_in_one_request()
        {
            // Why pinning is safe to do from the portal: the holder itself enforces that one scope is
            // one tenant. A portal pin cannot quietly co-exist with a different company resolved by
            // anything else in the same request - it throws instead.
            var holder = new CompanyScopeHolder();
            holder.Set(PortalHost.CompanyA, null);

            holder.Set(PortalHost.CompanyA, null);          // same company: idempotent
            Assert.Throws<InvalidOperationException>(() => holder.Set(PortalHost.CompanyB, null));
        }
    }
}
