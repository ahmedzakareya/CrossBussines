using System;
using System.Linq;
using System.Threading.Tasks;
using CrossBuy.BL.Portal;
using CrossBuy.Models.Context.Portal;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CrossBuy.Tests.Portal
{
    // =============================================================================================
    // "PLEASE GET IN TOUCH" — the first row an OUTSIDER creates in this product.
    //
    // Every other portal test is about what a customer may READ or DECIDE. This one is about what a
    // customer may CREATE, which is a different risk: the row lands in front of staff, and the
    // questions that matter are whose name is on it, what it is allowed to point at, and whether one
    // customer can fill a queue or reach into another tenant's.
    //
    // The sharpest of these is the optional quotation reference. A caller may send any integer, so
    // the assertions below insist that a foreign id is REFUSED rather than saved-without-the-link:
    // storing the row anyway would turn a probe into a successful write, and answering differently
    // for "exists but not yours" than for "does not exist" would make the endpoint an oracle for
    // discovering quotation ids.
    // =============================================================================================
    public class PortalContactRequestTests
    {
        private static string[] Asker => PortalCapabilities.Default
            .Concat(new[] { PortalCapabilities.RequestContact }).ToArray();

        private static PortalDataService Service(PortalHost host)
            => new(host.Platform.Seed, host.Contexts);

        private static IQueryable<PortalContactRequest> Rows(PortalHost host)
            => host.Platform.Seed.Set<PortalContactRequest>().AsNoTracking();

        // ---- identity and capability ------------------------------------------------------------

        [Fact]
        public async Task An_unauthenticated_caller_creates_nothing()
        {
            using var host = new PortalHost();
            host.ActAsNobody();

            var result = await Service(host).RequestContactAsync(null, "call me");

            Assert.False(result.Ok);
            Assert.Equal(PortalDecisionResult.NotFound, result.Code);
            Assert.Empty(await Rows(host).ToListAsync());
        }

        [Fact]
        public async Task The_read_grant_alone_does_not_carry_the_right_to_raise_work()
        {
            // Requesting contact puts a row in a staff queue. That is not something every portal
            // account should be able to do just by existing, which is why it is its own capability and
            // why it is deliberately outside the read-only default.
            using var host = new PortalHost();
            host.ActAs(PortalHost.AcmeId, PortalHost.CompanyA);   // PortalCapabilities.Default

            var result = await Service(host).RequestContactAsync(null, "call me");

            Assert.False(result.Ok);
            Assert.Equal(PortalDecisionResult.NotPermitted, result.Code);
            Assert.Empty(await Rows(host).ToListAsync());
        }

        // ---- the successful write, and whose name is on it ---------------------------------------

        [Fact]
        public async Task A_general_request_records_the_company_customer_and_portal_user_from_the_identity()
        {
            using var host = new PortalHost();
            host.ActAs(PortalHost.AcmeId, PortalHost.CompanyA, Asker);

            var result = await Service(host).RequestContactAsync(null, "  Please call me back.  ");

            Assert.True(result.Ok);
            Assert.Equal(PortalContactRequestStatus.Requested, result.Status);

            var row = Assert.Single(await Rows(host).ToListAsync());
            Assert.Equal(PortalHost.CompanyA, row.CompanyID);
            Assert.Equal(PortalHost.AcmeId, row.CustomerId);
            Assert.Equal(1, row.PortalUserId);             // the stub identity's portal user
            Assert.Null(row.QuotationId);
            Assert.Equal("Please call me back.", row.Message);   // trimmed, and otherwise their words
            Assert.Equal(PortalContactRequestStatus.Requested, row.Status);
            Assert.Null(row.HandledAt);
            Assert.Null(row.HandledByEmployeeId);
            Assert.True(row.RequestedAt > DateTime.UtcNow.AddMinutes(-5));
        }

        [Fact]
        public async Task A_request_about_the_customers_own_quotation_keeps_the_reference()
        {
            using var host = new PortalHost();
            var id = host.SeedQuotation(PortalHost.AcmeId, PortalHost.CompanyA);
            host.ActAs(PortalHost.AcmeId, PortalHost.CompanyA, Asker);

            Assert.True((await Service(host).RequestContactAsync(id, null)).Ok);

            var row = Assert.Single(await Rows(host).ToListAsync());
            Assert.Equal(id, row.QuotationId);
            Assert.Null(row.Message);
        }

        // ---- the reference is an identifier, never an authority ----------------------------------

        [Fact]
        public async Task Another_customers_quotation_is_refused_and_nothing_is_written()
        {
            // NOT "saved without the link". Writing the row anyway would turn a probe into a
            // successful write and tell the caller nothing was wrong with the id they guessed.
            using var host = new PortalHost();
            var foreign = host.SeedQuotation(PortalHost.BetaId, PortalHost.CompanyA);
            host.ActAs(PortalHost.AcmeId, PortalHost.CompanyA, Asker);

            var result = await Service(host).RequestContactAsync(foreign, "about this one");

            Assert.False(result.Ok);
            Assert.Equal(PortalDecisionResult.NotFound, result.Code);
            Assert.Empty(await Rows(host).ToListAsync());
        }

        [Fact]
        public async Task Another_companys_quotation_is_refused_exactly_as_an_absent_one_is()
        {
            using var host = new PortalHost();
            var otherCompany = host.SeedQuotation(PortalHost.OtherCoCustomerId, PortalHost.CompanyB);
            host.ActAs(PortalHost.AcmeId, PortalHost.CompanyA, Asker);

            var foreign = await Service(host).RequestContactAsync(otherCompany, null);
            var absent = await Service(host).RequestContactAsync(999_999, null);

            // IDENTICAL answers. Any difference between them is an oracle.
            Assert.Equal(absent.Code, foreign.Code);
            Assert.Equal(PortalDecisionResult.NotFound, foreign.Code);
            Assert.Empty(await Rows(host).ToListAsync());
        }

        [Fact]
        public async Task A_draft_quotation_cannot_even_be_named()
        {
            // A draft is not this customer's business, so naming one must not confirm it exists.
            using var host = new PortalHost();
            var draft = host.SeedQuotation(PortalHost.AcmeId, PortalHost.CompanyA, status: "Draft");
            host.ActAs(PortalHost.AcmeId, PortalHost.CompanyA, Asker);

            var result = await Service(host).RequestContactAsync(draft, null);

            Assert.False(result.Ok);
            Assert.Equal(PortalDecisionResult.NotFound, result.Code);
            Assert.Empty(await Rows(host).ToListAsync());
        }

        [Fact]
        public async Task A_customer_id_that_collides_across_companies_grants_nothing()
        {
            // The same numeric customer id in another company is a different business. An identity for
            // company B naming company B's own quotation is fine; the point is that being customer 300
            // in one tenant says nothing about customer 300 in the other.
            using var host = new PortalHost();
            var theirs = host.SeedQuotation(PortalHost.OtherCoCustomerId, PortalHost.CompanyB);
            host.ActAs(PortalHost.OtherCoCustomerId, PortalHost.CompanyA, Asker);   // same id, wrong company

            var result = await Service(host).RequestContactAsync(theirs, null);

            Assert.False(result.Ok);
            Assert.Equal(PortalDecisionResult.NotFound, result.Code);
            Assert.Empty(await Rows(host).ToListAsync());
        }

        // ---- duplicates, and what "the same request" means ---------------------------------------

        [Fact]
        public async Task Asking_twice_while_the_first_is_still_open_is_refused_rather_than_doubled()
        {
            using var host = new PortalHost();
            var id = host.SeedQuotation(PortalHost.AcmeId, PortalHost.CompanyA);
            host.ActAs(PortalHost.AcmeId, PortalHost.CompanyA, Asker);
            var service = Service(host);

            Assert.True((await service.RequestContactAsync(id, "first")).Ok);
            var again = await service.RequestContactAsync(id, "second");

            Assert.False(again.Ok);
            Assert.Equal(PortalDecisionResult.AlreadyRequested, again.Code);
            Assert.Single(await Rows(host).ToListAsync());
        }

        [Fact]
        public async Task Once_staff_have_handled_it_a_new_request_is_a_genuinely_new_one()
        {
            // The duplicate rule is about an OPEN request, not about the subject forever. A customer
            // whose first question was answered in March may legitimately have another in June.
            using var host = new PortalHost();
            var id = host.SeedQuotation(PortalHost.AcmeId, PortalHost.CompanyA);
            host.ActAs(PortalHost.AcmeId, PortalHost.CompanyA, Asker);
            var service = Service(host);

            Assert.True((await service.RequestContactAsync(id, "first")).Ok);

            var open = await host.Platform.Seed.Set<PortalContactRequest>().SingleAsync();
            open.Status = PortalContactRequestStatus.Handled;
            open.HandledAt = DateTime.UtcNow;
            open.HandledByEmployeeId = 42;
            await host.Platform.Seed.SaveChangesAsync();

            Assert.True((await service.RequestContactAsync(id, "second")).Ok);
            Assert.Equal(2, await Rows(host).CountAsync());
        }

        [Fact]
        public async Task A_general_request_and_a_quotation_request_are_different_subjects()
        {
            // "Call me about quotation 5" and "call me" are not the same ask, so the open-request rule
            // must not collapse them into one.
            using var host = new PortalHost();
            var id = host.SeedQuotation(PortalHost.AcmeId, PortalHost.CompanyA);
            host.ActAs(PortalHost.AcmeId, PortalHost.CompanyA, Asker);
            var service = Service(host);

            Assert.True((await service.RequestContactAsync(id, null)).Ok);
            Assert.True((await service.RequestContactAsync(null, null)).Ok);

            Assert.Equal(2, await Rows(host).CountAsync());
        }

        [Fact]
        public async Task Another_customers_open_request_does_not_block_mine()
        {
            // The duplicate predicate carries company AND customer. Without the customer it would let
            // one customer suppress another's request; without the company, one tenant could suppress
            // another's.
            using var host = new PortalHost();
            host.ActAs(PortalHost.BetaId, PortalHost.CompanyA, Asker);
            Assert.True((await Service(host).RequestContactAsync(null, "beta first")).Ok);

            host.ActAs(PortalHost.AcmeId, PortalHost.CompanyA, Asker);
            Assert.True((await Service(host).RequestContactAsync(null, "acme too")).Ok);

            Assert.Equal(2, await Rows(host).CountAsync());
        }

        // ---- the free text -----------------------------------------------------------------------

        [Fact]
        public async Task The_message_is_bounded_by_the_service_and_not_left_to_the_column()
        {
            // A service that lets the database enforce a length is a service that THROWS where it
            // should refuse - and on a customer-facing endpoint a throw is a 500 for a long sentence.
            using var host = new PortalHost();
            host.ActAs(PortalHost.AcmeId, PortalHost.CompanyA, Asker);

            var huge = new string('x', PortalContactRequestModel.MaxMessageLength + 500);
            Assert.True((await Service(host).RequestContactAsync(null, huge)).Ok);

            var row = Assert.Single(await Rows(host).ToListAsync());
            Assert.Equal(PortalContactRequestModel.MaxMessageLength, row.Message!.Length);
        }

        [Fact]
        public async Task Whitespace_only_is_stored_as_no_message_rather_than_as_a_blank_one()
        {
            using var host = new PortalHost();
            host.ActAs(PortalHost.AcmeId, PortalHost.CompanyA, Asker);

            Assert.True((await Service(host).RequestContactAsync(null, "   ")).Ok);
            Assert.Null(Assert.Single(await Rows(host).ToListAsync()).Message);
        }

        // ---- what the customer may NOT do --------------------------------------------------------

        [Fact]
        public void The_portal_service_offers_no_way_to_mark_a_request_handled()
        {
            // HANDLED is a STAFF transition. The asymmetry - customer raises, staff close - is the
            // whole workflow, and the portal authority simply has no member that could close one.
            var members = typeof(IPortalDataService)
                .GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
                .Select(m => m.Name)
                .ToList();

            Assert.DoesNotContain(members, m => m.Contains("Handle", StringComparison.Ordinal));
            Assert.DoesNotContain(members, m => m.Contains("Close", StringComparison.Ordinal));
            Assert.Contains("RequestContactAsync", members);
        }

        [Fact]
        public void The_status_vocabulary_is_exactly_two_and_validates()
        {
            Assert.True(PortalContactRequestStatus.IsValid(PortalContactRequestStatus.Requested));
            Assert.True(PortalContactRequestStatus.IsValid(PortalContactRequestStatus.Handled));
            Assert.False(PortalContactRequestStatus.IsValid("InProgress"));
            Assert.False(PortalContactRequestStatus.IsValid(null));
        }

        [Fact]
        public void Request_contact_is_a_known_capability_and_is_not_granted_by_default()
        {
            Assert.True(PortalCapabilities.IsKnown(PortalCapabilities.RequestContact));
            Assert.DoesNotContain(PortalCapabilities.RequestContact, PortalCapabilities.Default);
        }
    }
}
