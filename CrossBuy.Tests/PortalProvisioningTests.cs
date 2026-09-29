using System;
using System.Linq;
using System.Threading.Tasks;
using CrossBuy.BL.Platform;
using CrossBuy.BL.Portal;
using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Accounting;
using CrossBuy.Models.Context.Portal;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CrossBuy.Tests
{
    // =============================================================================================
    // GIVING A CUSTOMER A WAY IN — the contract for the thing that did not exist.
    //
    // Nothing in this application created a PortalUser row before now. The whole Client Portal was
    // reachable only by somebody running an INSERT against production by hand, which is the
    // product-enablement defect this closes.
    //
    // MOST OF THESE TESTS ARE REFUSALS, and that is the right proportion for a service whose job is
    // handing an outsider a key. The one that matters most is the one about the company: there is no
    // companyId parameter anywhere in this API, so the test for it has to be written as "a caller can
    // only ever provision into their own tenant", which is asserted by provisioning as company 1 and
    // finding company 2's customer unreachable.
    //
    // A SUCCESSFUL PROVISION SITS BESIDE EVERY REFUSAL. Without it, a service that refused
    // unconditionally would satisfy the whole file.
    // =============================================================================================
    public class PortalProvisioningTests
    {
        private const int CompanyA = 1, CompanyB = 2;
        private const int AcmeId = 100, BetaId = 200, OtherCoId = 300;
        private const int StaffEmployeeId = 55;

        /// A host whose FILTERED context operates as `companyId`, which is what a signed-in staff
        /// request looks like.
        private static PlatformTestHost Staff(int companyId = CompanyA)
        {
            var host = new PlatformTestHost(companyId);
            host.Seed.Customers.AddRange(
                new Customer { ID = AcmeId, CompanyID = CompanyA, Name = "Acme", ControlAccountId = 1 },
                new Customer { ID = BetaId, CompanyID = CompanyA, Name = "Beta", ControlAccountId = 1 },
                new Customer { ID = OtherCoId, CompanyID = CompanyB, Name = "OtherCo", ControlAccountId = 1 });
            host.Seed.SaveChanges();
            return host;
        }

        /// The service as a signed-in employee of `companyId` gets it.
        ///
        /// EACH COMPANY GETS ITS OWN REQUEST SCOPE, and that is not fixture tidiness - it is what
        /// production is. One DI scope holds one company: the holder the query filters read and the
        /// BusinessContext the service authorizes against are the SAME request's, so they cannot
        /// disagree. An earlier version of this helper handed company 2's context a context filtered
        /// to company 1, and the two tests that exercise the second tenant failed - correctly, because
        /// that combination cannot occur and the filter was right to hide the rows.
        private static IPortalProvisioningService AsStaff(PlatformTestHost host, int companyId = CompanyA)
            => new PortalProvisioningService(
                companyId == CompanyA ? host.Db : host.Request(companyId).Db,
                new StubContextAccessor(
                    new BusinessContext { CompanyId = companyId, EmployeeId = StaffEmployeeId }));

        /// The service as an EXTERNAL caller gets it: no resolvable business context at all.
        private static IPortalProvisioningService AsOutsider(PlatformTestHost host)
            => new PortalProvisioningService(host.Db, new StubContextAccessor(null));

        private static IQueryable<PortalUser> Links(PlatformTestHost host)
            => host.Seed.Set<PortalUser>().AsNoTracking();

        // ---- 1. authority ------------------------------------------------------------------------

        [Fact]
        public async Task An_authorized_staff_member_can_provision_and_the_row_records_who_did_it()
        {
            // THE NON-VACUITY PROOF. Every refusal below is worthless without this.
            using var host = Staff();

            var result = await AsStaff(host).ProvisionAsync(AcmeId, "Dana Halim", "dana@acme.example");

            Assert.True(result.Ok);
            var row = Assert.Single(await Links(host).ToListAsync());
            Assert.Equal(result.PortalUserId, row.Id);
            Assert.Equal(CompanyA, row.CompanyID);
            Assert.Equal(AcmeId, row.CustomerId);
            Assert.Equal("Dana Halim", row.ContactName);
            Assert.True(row.IsActive);
            Assert.Equal(StaffEmployeeId, row.CreatedByEmployeeId);
            Assert.True(row.CreatedAt > DateTime.UtcNow.AddMinutes(-5));
        }

        [Fact]
        public async Task An_unresolved_caller_provisions_nothing()
        {
            // This is what an EXTERNAL caller is. A portal user has no Employee row, so
            // BusinessContextFactory produces no context for them, so they arrive here as nobody -
            // refused before a single argument is read. There is no "is this a portal user" branch to
            // forget, which is the point.
            using var host = Staff();

            var result = await AsOutsider(host).ProvisionAsync(AcmeId, "Self Service");

            Assert.False(result.Ok);
            Assert.Equal(PortalProvisioningResult.NotPermitted, result.Code);
            Assert.Empty(await Links(host).ToListAsync());
        }

        [Fact]
        public void The_api_offers_no_way_to_name_the_company()
        {
            // THE STRUCTURAL ASSERTION. A caller who could name the company would be provisioning into
            // a tenant they do not belong to, so the parameter simply does not exist - and this test
            // fails the moment somebody adds it for convenience.
            var parameters = typeof(IPortalProvisioningService)
                .GetMethods()
                .SelectMany(m => m.GetParameters())
                .Select(p => p.Name!)
                .ToList();

            Assert.DoesNotContain(parameters, n => n.Contains("company", StringComparison.OrdinalIgnoreCase));
            Assert.Contains("customerId", parameters);   // the scan is looking in the right place
        }

        // ---- 2. the target -----------------------------------------------------------------------

        [Fact]
        public async Task A_customer_in_another_company_is_unreachable()
        {
            // The caller is company 1 staff; OtherCo belongs to company 2. The same refusal as a
            // customer that does not exist at all, because telling them apart would confirm that
            // another tenant has a customer with that id.
            using var host = Staff();

            var foreign = await AsStaff(host).ProvisionAsync(OtherCoId, "Wrong Tenant");
            var absent = await AsStaff(host).ProvisionAsync(999_999, "No Such");

            Assert.Equal(PortalProvisioningResult.NoSuchCustomer, foreign.Code);
            Assert.Equal(foreign.Code, absent.Code);
            Assert.Empty(await Links(host).ToListAsync());
        }

        [Fact]
        public async Task The_same_customer_id_reaches_a_different_business_in_a_different_company()
        {
            // Customer 300 exists - in company 2. Staff of company 2 CAN provision it, which proves the
            // refusal above was about the tenant and not about the id being unusable.
            using var host = Staff();

            var mine = await AsStaff(host, CompanyB).ProvisionAsync(OtherCoId, "OtherCo Contact");

            Assert.True(mine.Ok);
            Assert.Equal(CompanyB, Assert.Single(await Links(host).ToListAsync()).CompanyID);
        }

        [Fact]
        public async Task The_company_check_is_the_services_own_and_not_the_ambient_query_filter()
        {
            // WHY THIS EXISTS, and it is not belt-and-braces for its own sake.
            //
            // A_customer_in_another_company_is_unreachable passes even with the service's company
            // predicate DELETED, because the context it runs on is already filtered to the caller's
            // company - the ambient filter hides the row before the service looks. Mutation PORTAL2-M7
            // removed `&& c.CompanyID == companyId` and SURVIVED the whole suite, which is how that
            // was found rather than guessed.
            //
            // A filter is a second line, not the line. If provisioning ever runs on a context that is
            // not company-filtered - a background job, a maintenance path, a future cross-company
            // admin screen - the service's own predicate is the only thing standing between a caller
            // and another tenant's customer. So this test hands it the CROSS-COMPANY context on
            // purpose: only the service's own check can produce the refusal here.
            using var host = Staff();

            var unfiltered = new PortalProvisioningService(host.Seed, new StubContextAccessor(
                new BusinessContext { CompanyId = CompanyA, EmployeeId = StaffEmployeeId }));

            var result = await unfiltered.ProvisionAsync(OtherCoId, "Wrong Tenant");

            Assert.False(result.Ok);
            Assert.Equal(PortalProvisioningResult.NoSuchCustomer, result.Code);
            Assert.Empty(await Links(host).ToListAsync());

            // NON-VACUOUS: the same unfiltered service DOES provision a customer of its own company,
            // so the refusal above was about the tenant and not about the context being unusable.
            var mine = await unfiltered.ProvisionAsync(AcmeId, "Right Tenant");
            Assert.True(mine.Ok);
            Assert.Equal(CompanyA, Assert.Single(await Links(host).ToListAsync()).CompanyID);
        }

        [Fact]
        public async Task A_missing_contact_name_is_refused_because_the_row_is_about_a_person()
        {
            using var host = Staff();

            Assert.Equal(PortalProvisioningResult.InvalidArgument,
                (await AsStaff(host).ProvisionAsync(AcmeId, "   ")).Code);
            Assert.Equal(PortalProvisioningResult.InvalidArgument,
                (await AsStaff(host).ProvisionAsync(0, "Nameless Customer")).Code);
            Assert.Empty(await Links(host).ToListAsync());
        }

        // ---- 3. collisions -----------------------------------------------------------------------

        [Fact]
        public async Task One_login_cannot_speak_for_two_customers()
        {
            // The database enforces this too (UX_PortalUsers_UserId is unique across the whole table),
            // and it is global rather than per-company on purpose: an Identity account linked twice
            // would make the tenant boundary depend on which row the query plan returned first.
            using var host = Staff();
            var service = AsStaff(host);

            Assert.True((await service.ProvisionAsync(AcmeId, "Dana", userId: "u-dana")).Ok);
            var second = await service.ProvisionAsync(BetaId, "Dana Again", userId: "u-dana");

            Assert.False(second.Ok);
            Assert.Equal(PortalProvisioningResult.AlreadyLinked, second.Code);
            Assert.Single(await Links(host).ToListAsync());
        }

        [Fact]
        public async Task The_same_login_cannot_be_linked_across_companies_either()
        {
            using var host = Staff();

            Assert.True((await AsStaff(host, CompanyA).ProvisionAsync(AcmeId, "Dana", userId: "u-dana")).Ok);
            var crossTenant = await AsStaff(host, CompanyB).ProvisionAsync(OtherCoId, "Dana", userId: "u-dana");

            Assert.Equal(PortalProvisioningResult.AlreadyLinked, crossTenant.Code);
            Assert.Single(await Links(host).ToListAsync());
        }

        [Fact]
        public async Task Provisioning_the_same_contact_twice_is_prevented_rather_than_doubled()
        {
            // The ordinary double-click. Two active links for one person on one account is not a
            // narrower kind of duplicate - it is two credentials where somebody intended one.
            using var host = Staff();
            var service = AsStaff(host);

            Assert.True((await service.ProvisionAsync(AcmeId, "Dana Halim")).Ok);
            var again = await service.ProvisionAsync(AcmeId, " Dana Halim ");

            Assert.Equal(PortalProvisioningResult.AlreadyLinked, again.Code);
            Assert.Single(await Links(host).ToListAsync());
        }

        // ---- 4. capabilities ---------------------------------------------------------------------

        [Fact]
        public async Task The_default_grant_is_the_three_read_capabilities_and_nothing_else()
        {
            using var host = Staff();

            var result = await AsStaff(host).ProvisionAsync(AcmeId, "Dana");

            var row = await Links(host).SingleAsync(p => p.Id == result.PortalUserId);
            var granted = row.Capabilities!.Split(',');

            Assert.Equal(3, granted.Length);
            Assert.Contains(PortalCapabilities.ViewQuotations, granted);
            Assert.Contains(PortalCapabilities.ViewProjects, granted);
            Assert.Contains(PortalCapabilities.ViewInvoices, granted);

            // The one WRITE a portal login can hold is deliberately outside the default: accepting a
            // quotation is a contractual act, and somebody decides per contact that this person speaks
            // for the customer.
            Assert.DoesNotContain(PortalCapabilities.RespondToQuotations, granted);
            Assert.DoesNotContain(PortalCapabilities.RequestContact, granted);
            Assert.DoesNotContain(PortalCapabilities.ViewDocuments, granted);
        }

        [Fact]
        public async Task An_unknown_capability_refuses_the_whole_call_rather_than_being_dropped()
        {
            // Dropping it silently would provision a NARROWER account than the person doing it
            // believed they were creating, and they would have no way to tell.
            using var host = Staff();

            var result = await AsStaff(host).ProvisionAsync(
                AcmeId, "Dana",
                capabilities: new[] { PortalCapabilities.ViewQuotations, "portal-everything" });

            Assert.False(result.Ok);
            Assert.Equal(PortalProvisioningResult.UnknownCapability, result.Code);
            Assert.Empty(await Links(host).ToListAsync());
        }

        [Fact]
        public async Task An_explicit_grant_is_stored_exactly_as_given()
        {
            using var host = Staff();

            var result = await AsStaff(host).ProvisionAsync(
                AcmeId, "Dana",
                capabilities: new[] { PortalCapabilities.ViewQuotations, PortalCapabilities.RespondToQuotations });

            var granted = (await Links(host).SingleAsync(p => p.Id == result.PortalUserId)).Capabilities!.Split(',');
            Assert.Equal(2, granted.Length);
            Assert.Contains(PortalCapabilities.RespondToQuotations, granted);
            Assert.DoesNotContain(PortalCapabilities.ViewInvoices, granted);
        }

        [Fact]
        public async Task Clearing_a_grant_leaves_nothing_rather_than_falling_back_to_the_default()
        {
            // THE TRAP THIS CLOSES. The resolver reads a NULL capability column as "nobody configured
            // this row" and answers with the read-only default. If clearing a grant wrote null - or an
            // empty string, back when the resolver could not tell the two apart - it would hand the
            // three read capabilities straight back to an account whose access had just been removed.
            using var host = Staff();
            var service = AsStaff(host);
            var created = await service.ProvisionAsync(AcmeId, "Dana");

            Assert.True((await service.SetCapabilitiesAsync(created.PortalUserId, Array.Empty<string>())).Ok);

            var row = await Links(host).SingleAsync(p => p.Id == created.PortalUserId);
            Assert.Equal("", row.Capabilities);
            Assert.NotNull(row.Capabilities);

            // And the resolver agrees: this login may do nothing.
            Assert.Empty(PortalCapabilitiesProbe.Resolve(row.Capabilities));
            Assert.Equal(3, PortalCapabilitiesProbe.Resolve(null).Count);
        }

        [Fact]
        public async Task Capabilities_cannot_be_changed_on_another_tenants_portal_user()
        {
            using var host = Staff();
            var created = await AsStaff(host, CompanyA).ProvisionAsync(AcmeId, "Dana");

            var byOtherTenant = await AsStaff(host, CompanyB)
                .SetCapabilitiesAsync(created.PortalUserId, new[] { PortalCapabilities.RespondToQuotations });

            Assert.Equal(PortalProvisioningResult.NoSuchPortalUser, byOtherTenant.Code);
        }

        // ---- 5. activation -----------------------------------------------------------------------

        [Fact]
        public async Task Deactivation_revokes_access_without_losing_the_row()
        {
            using var host = Staff();
            var service = AsStaff(host);
            var created = await service.ProvisionAsync(AcmeId, "Dana", userId: "u-dana");

            Assert.True((await service.DeactivateAsync(created.PortalUserId)).Ok);

            var row = await Links(host).SingleAsync(p => p.Id == created.PortalUserId);
            Assert.False(row.IsActive);
            Assert.Equal(AcmeId, row.CustomerId);       // the audit trail is intact
            Assert.Equal("u-dana", row.UserId);
        }

        [Fact]
        public async Task An_inactive_link_resolves_to_no_portal_identity_at_all()
        {
            // The whole point of deactivation, asserted through the REAL resolver rather than by
            // reading the flag back. A row that still resolved would be stale access.
            using var host = Staff();
            var service = AsStaff(host);
            var created = await service.ProvisionAsync(AcmeId, "Dana", userId: "u-dana");
            await service.DeactivateAsync(created.PortalUserId);

            var accessor = new PortalContextAccessor(host.Db, ProvisioningProbe.Http("u-dana"), ProvisioningProbe.Contexts(host, "u-dana"));
            Assert.Null(await accessor.TryGetCurrentAsync());
        }

        [Fact]
        public async Task Reactivation_restores_access_to_the_same_customer_and_the_same_grant()
        {
            // Reactivation must not silently re-decide anything. Restoring somebody's access is not the
            // moment to widen or narrow what they may do.
            using var host = Staff();
            var service = AsStaff(host);
            var created = await service.ProvisionAsync(
                AcmeId, "Dana", userId: "u-dana",
                capabilities: new[] { PortalCapabilities.ViewQuotations, PortalCapabilities.RespondToQuotations });

            await service.DeactivateAsync(created.PortalUserId);
            Assert.True((await service.ReactivateAsync(created.PortalUserId)).Ok);

            var row = await Links(host).SingleAsync(p => p.Id == created.PortalUserId);
            Assert.True(row.IsActive);
            Assert.Equal(CompanyA, row.CompanyID);
            Assert.Equal(AcmeId, row.CustomerId);
            Assert.Equal(
                PortalCapabilities.ViewQuotations + "," + PortalCapabilities.RespondToQuotations,
                row.Capabilities);
        }

        [Fact]
        public async Task One_tenants_staff_cannot_deactivate_another_tenants_portal_login()
        {
            using var host = Staff();
            var created = await AsStaff(host, CompanyA).ProvisionAsync(AcmeId, "Dana");

            var attempt = await AsStaff(host, CompanyB).DeactivateAsync(created.PortalUserId);

            Assert.False(attempt.Ok);
            Assert.Equal(PortalProvisioningResult.NoSuchPortalUser, attempt.Code);
            Assert.True((await Links(host).SingleAsync(p => p.Id == created.PortalUserId)).IsActive);
        }

        [Fact]
        public async Task An_outsider_can_neither_deactivate_nor_regrant()
        {
            using var host = Staff();
            var created = await AsStaff(host).ProvisionAsync(AcmeId, "Dana");
            var outsider = AsOutsider(host);

            Assert.Equal(PortalProvisioningResult.NotPermitted,
                (await outsider.DeactivateAsync(created.PortalUserId)).Code);
            Assert.Equal(PortalProvisioningResult.NotPermitted,
                (await outsider.SetCapabilitiesAsync(
                    created.PortalUserId, new[] { PortalCapabilities.RespondToQuotations })).Code);

            Assert.True((await Links(host).SingleAsync(p => p.Id == created.PortalUserId)).IsActive);
        }
    }

    /// Reaches the resolver's private capability parsing the only honest way: through the resolver.
    internal static class PortalCapabilitiesProbe
    {
        public static System.Collections.Generic.IReadOnlyCollection<string> Resolve(string? stored)
        {
            using var host = new PlatformTestHost(1);
            host.Seed.Set<PortalUser>().Add(new PortalUser
            {
                CompanyID = 1, CustomerId = 100, UserId = "probe",
                ContactName = "Probe", IsActive = true, Capabilities = stored,
            });
            host.Seed.SaveChanges();

            var accessor = new PortalContextAccessor(host.Db, ProvisioningProbe.Http("probe"), ProvisioningProbe.Contexts(host, "probe"));
            return accessor.TryGetCurrentAsync().GetAwaiter().GetResult()!.Capabilities;
        }
    }

    internal static class ProvisioningProbe
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
}
