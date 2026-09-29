using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using CrossBuy.BL.Portal;
using CrossBuy.Models.Context.Inventory;
using CrossBuy.Models.Context.Portal;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CrossBuy.Tests.Portal
{
    // =============================================================================================
    // THE CUSTOMER DECISION — the portal's first mutation.
    //
    // Reading a quotation and answering one are different acts, and this file is about the second.
    // The claim under test is that a customer can move a quotation they were SENT to Accepted or
    // Rejected, and can do nothing else at all: not to a draft, not to a lapsed offer, not to one
    // already answered, and never to another customer's - not even one bearing the same id in the
    // same company, which is the case a portal scoped only by tenant gets wrong.
    //
    // The transition is delegated to ISellingService, so the stub here RECORDS what the portal asked
    // for. That is the sharper assertion: it proves the portal passed the company it resolved from
    // the identity and a target status its own matrix approved, rather than anything a caller sent.
    // =============================================================================================

    /// Records every transition the portal requests, and applies it, so a follow-up read sees the
    /// state a real SellingService would have left behind.
    internal sealed class RecordingSelling : CrossBuy.BL.ISellingService
    {
        private readonly CrossBuy.Models.Context.CrossDbContext _db;
        public RecordingSelling(CrossBuy.Models.Context.CrossDbContext db) => _db = db;

        public readonly List<(int CompanyId, int Id, string Status)> Transitions = new();
        public bool RefuseNext;

        /// Whether the portal asked for the CUSTOMER-DECISION overload. Recorded rather than ignored:
        /// it is the flag that decides whether a Quotation.*ByCustomer event is produced at all, so a
        /// portal that stopped passing it would silently stop recording and every other assertion here
        /// would still pass.
        public readonly List<bool> CustomerDecisionFlags = new();

        public async Task<(bool ok, string? error)> SetQuotationStatusAsync(
            int companyId, int id, string status, bool customerDecision = false)
        {
            Transitions.Add((companyId, id, status));
            CustomerDecisionFlags.Add(customerDecision);
            if (RefuseNext) return (false, "refused");

            var q = await _db.Quotations.FirstOrDefaultAsync(x => x.ID == id && x.CompanyID == companyId);
            if (q == null) return (false, "not found");
            q.Status = status;
            await _db.SaveChangesAsync();
            return (true, null);
        }

        // THE REST OF THE MODULE SURFACE THROWS, deliberately. SetQuotationStatusAsync is the only
        // member the portal may ever reach: every other one creates orders, deliveries or invoices,
        // and a portal that reached them would be transacting on the customer's behalf rather than
        // recording their answer. A future call to any of these fails loudly in a test instead of
        // quietly working.
        public Task<List<SalesOrder>> GetSalesOrdersAsync(int companyId) => throw new NotSupportedException();
        public Task<SalesOrder?> GetSalesOrderAsync(int companyId, int id) => throw new NotSupportedException();
        public Task<(bool ok, string? error, SalesOrder? so)> CreateSalesOrderAsync(
            int companyId, int customerId, int? warehouseId, DateTime date, DateTime? expected, string? notes,
            List<CrossBuy.BL.SoLineInput> lines, string? userId, int? currencyId = null,
            decimal? exchangeRate = null, int? projectId = null) => throw new NotSupportedException();
        public Task<List<DeliveryNote>> GetDeliveriesAsync(int companyId) => throw new NotSupportedException();
        public Task<DeliveryNote?> GetDeliveryAsync(int companyId, int id) => throw new NotSupportedException();
        public Task<(bool ok, string? error, DeliveryNote? dn)> CreateDeliveryAsync(
            int companyId, int? customerId, int warehouseId, int? soId, DateTime date, string? notes,
            List<CrossBuy.BL.DeliveryLineInput> lines, string? userId) => throw new NotSupportedException();
        public Task<(bool ok, string? error, int? invoiceId)> ConvertToInvoiceAsync(
            int companyId, int soId, string? userId) => throw new NotSupportedException();
        public Task<List<Quotation>> GetQuotationsAsync(int companyId) => throw new NotSupportedException();
        public Task<Quotation?> GetQuotationAsync(int companyId, int id) => throw new NotSupportedException();
        public Task<(bool ok, string? error, Quotation? q)> CreateQuotationAsync(
            int companyId, int customerId, int? warehouseId, DateTime date, DateTime? validUntil, string? notes,
            List<CrossBuy.BL.SoLineInput> lines, string? userId, int? currencyId = null, decimal? exchangeRate = null)
            => throw new NotSupportedException();
        public Task<(bool ok, string? error, int? salesOrderId)> ConvertQuotationToOrderAsync(
            int companyId, int id, string? userId) => throw new NotSupportedException();
    }

    public class PortalQuotationDecisionTests
    {
        private const string Respond = PortalCapabilities.RespondToQuotations;

        /// The full capability set a responding customer holds: the read grant plus the answer right.
        private static string[] Responder =>
            PortalCapabilities.Default.Concat(new[] { Respond }).ToArray();

        private static (PortalDataService Service, RecordingSelling Selling) Decider(PortalHost host)
        {
            var selling = new RecordingSelling(host.Platform.Seed);
            return (new PortalDataService(host.Platform.Seed, host.Contexts, selling), selling);
        }

        private static async Task<string> StatusOfAsync(PortalHost host, int id)
            => (await host.Platform.Seed.Quotations.AsNoTracking().SingleAsync(q => q.ID == id)).Status;

        // ---- identity and capability ------------------------------------------------------------

        [Fact]
        public async Task An_unauthenticated_caller_decides_nothing()
        {
            using var host = new PortalHost();
            var id = host.SeedQuotation(PortalHost.AcmeId, PortalHost.CompanyA);
            host.ActAsNobody();

            var (service, selling) = Decider(host);
            var result = await service.RespondToQuotationAsync(id, accept: true);

            Assert.False(result.Ok);
            Assert.Equal(PortalDecisionResult.NotFound, result.Code);
            Assert.Empty(selling.Transitions);
            Assert.Equal("Sent", await StatusOfAsync(host, id));
        }

        [Fact]
        public async Task The_read_grant_alone_does_not_carry_the_right_to_answer()
        {
            // The default grant is deliberately read-only: seeing an offer and accepting it are
            // different rights, and accepting one has contractual meaning.
            using var host = new PortalHost();
            var id = host.SeedQuotation(PortalHost.AcmeId, PortalHost.CompanyA);
            host.ActAs(PortalHost.AcmeId, PortalHost.CompanyA);   // PortalCapabilities.Default

            var (service, selling) = Decider(host);
            var result = await service.RespondToQuotationAsync(id, accept: true);

            Assert.False(result.Ok);
            Assert.Equal(PortalDecisionResult.NotPermitted, result.Code);
            Assert.Empty(selling.Transitions);
            Assert.Equal("Sent", await StatusOfAsync(host, id));
        }

        // ---- the permitted transitions ----------------------------------------------------------

        [Fact]
        public async Task The_portal_always_asks_for_the_customer_decision_overload()
        {
            // Without this flag the module transitions the status and records NO event, which is the
            // silent-drop this whole seam exists to remove. Nothing else in this file would notice.
            using var host = new PortalHost();
            var id = host.SeedQuotation(PortalHost.AcmeId, PortalHost.CompanyA);
            host.ActAs(PortalHost.AcmeId, PortalHost.CompanyA, Responder);

            var (service, selling) = Decider(host);
            Assert.True((await service.RespondToQuotationAsync(id, accept: true)).Ok);

            Assert.True(Assert.Single(selling.CustomerDecisionFlags));
        }

        [Fact]
        public async Task A_sent_quotation_is_accepted_and_the_module_performs_the_transition()
        {
            using var host = new PortalHost();
            var id = host.SeedQuotation(PortalHost.AcmeId, PortalHost.CompanyA);
            host.ActAs(PortalHost.AcmeId, PortalHost.CompanyA, Responder);

            var (service, selling) = Decider(host);
            var result = await service.RespondToQuotationAsync(id, accept: true);

            Assert.True(result.Ok, result.Code);
            Assert.Equal("Accepted", result.Status);
            Assert.Equal("Accepted", await StatusOfAsync(host, id));

            // THE COMPANY CAME FROM THE IDENTITY. Nothing in the call carried one, and the module was
            // asked for exactly the status the portal's own matrix approved.
            var asked = Assert.Single(selling.Transitions);
            Assert.Equal((PortalHost.CompanyA, id, "Accepted"), asked);
        }

        [Fact]
        public async Task A_sent_quotation_is_rejected_through_the_same_path()
        {
            using var host = new PortalHost();
            var id = host.SeedQuotation(PortalHost.AcmeId, PortalHost.CompanyA);
            host.ActAs(PortalHost.AcmeId, PortalHost.CompanyA, Responder);

            var (service, selling) = Decider(host);
            var result = await service.RespondToQuotationAsync(id, accept: false);

            Assert.True(result.Ok, result.Code);
            Assert.Equal("Rejected", await StatusOfAsync(host, id));
            Assert.Equal((PortalHost.CompanyA, id, "Rejected"), Assert.Single(selling.Transitions));
        }

        // ---- idempotency and conflict -----------------------------------------------------------

        [Fact]
        public async Task The_same_answer_twice_succeeds_twice_and_writes_once()
        {
            // A double-clicked Accept must not be an error the customer has to interpret, and must not
            // reach the module a second time.
            using var host = new PortalHost();
            var id = host.SeedQuotation(PortalHost.AcmeId, PortalHost.CompanyA);
            host.ActAs(PortalHost.AcmeId, PortalHost.CompanyA, Responder);

            var (service, selling) = Decider(host);
            Assert.True((await service.RespondToQuotationAsync(id, accept: true)).Ok);
            var again = await service.RespondToQuotationAsync(id, accept: true);

            Assert.True(again.Ok, again.Code);
            Assert.Equal("Accepted", again.Status);
            Assert.Single(selling.Transitions);          // the second call wrote nothing
            Assert.Equal("Accepted", await StatusOfAsync(host, id));
        }

        [Fact]
        public async Task A_different_answer_after_deciding_is_refused_rather_than_overwritten()
        {
            // The first answer already had contractual meaning. This is the asymmetry that makes the
            // idempotency above safe: same answer yes, changed mind no.
            using var host = new PortalHost();
            var id = host.SeedQuotation(PortalHost.AcmeId, PortalHost.CompanyA);
            host.ActAs(PortalHost.AcmeId, PortalHost.CompanyA, Responder);

            var (service, selling) = Decider(host);
            Assert.True((await service.RespondToQuotationAsync(id, accept: true)).Ok);
            var reversal = await service.RespondToQuotationAsync(id, accept: false);

            Assert.False(reversal.Ok);
            Assert.Equal(PortalDecisionResult.AlreadyDecided, reversal.Code);
            Assert.Single(selling.Transitions);
            Assert.Equal("Accepted", await StatusOfAsync(host, id));
        }

        [Fact]
        public async Task A_rejection_is_just_as_final_as_an_acceptance()
        {
            // THE OTHER DIRECTION, and it was missing. The test above only walked Accepted -> reject,
            // so a matrix that had forgotten "Rejected" in its terminal list would still have passed:
            // the Accepted arm caught every case the suite exercised. Mutation PORTAL-M6 removed
            // exactly that clause and survived, which is how the hole was found rather than guessed at.
            //
            // It matters commercially, not just symmetrically. A customer who declined an offer and
            // then watched the price move must not be able to go back and take the old one; the
            // decline ended that offer, and re-opening it is a staff act.
            using var host = new PortalHost();
            var id = host.SeedQuotation(PortalHost.AcmeId, PortalHost.CompanyA);
            host.ActAs(PortalHost.AcmeId, PortalHost.CompanyA, Responder);

            var (service, selling) = Decider(host);
            Assert.True((await service.RespondToQuotationAsync(id, accept: false)).Ok);
            var reversal = await service.RespondToQuotationAsync(id, accept: true);

            Assert.False(reversal.Ok);
            Assert.Equal(PortalDecisionResult.AlreadyDecided, reversal.Code);
            Assert.Single(selling.Transitions);
            Assert.Equal("Rejected", await StatusOfAsync(host, id));
        }

        // ---- the states a customer may not touch ------------------------------------------------

        [Theory]
        [InlineData("Draft", PortalDecisionResult.NotFound)]      // invisible: a distinct refusal would confirm it exists
        [InlineData("Converted", PortalDecisionResult.AlreadyDecided)]
        [InlineData("Expired", PortalDecisionResult.Expired)]
        public async Task A_quotation_in_a_closed_state_is_refused_and_never_reaches_the_module(
            string status, string expectedCode)
        {
            using var host = new PortalHost();
            var id = host.SeedQuotation(PortalHost.AcmeId, PortalHost.CompanyA, status: status);
            host.ActAs(PortalHost.AcmeId, PortalHost.CompanyA, Responder);

            var (service, selling) = Decider(host);
            var result = await service.RespondToQuotationAsync(id, accept: true);

            Assert.False(result.Ok);
            Assert.Equal(expectedCode, result.Code);
            Assert.Empty(selling.Transitions);
            Assert.Equal(status, await StatusOfAsync(host, id));
        }

        [Fact]
        public async Task An_offer_whose_validity_has_lapsed_is_refused_even_while_it_still_reads_Sent()
        {
            // Expiry is a DATE, not only a status. A sweep may not have run; accepting here would form
            // an agreement on lapsed terms.
            using var host = new PortalHost();
            var id = host.SeedQuotation(PortalHost.AcmeId, PortalHost.CompanyA);
            var stale = await host.Platform.Seed.Quotations.SingleAsync(q => q.ID == id);
            stale.ValidUntil = DateTime.UtcNow.Date.AddDays(-1);
            await host.Platform.Seed.SaveChangesAsync();

            host.ActAs(PortalHost.AcmeId, PortalHost.CompanyA, Responder);
            var (service, selling) = Decider(host);
            var result = await service.RespondToQuotationAsync(id, accept: true);

            Assert.False(result.Ok);
            Assert.Equal(PortalDecisionResult.Expired, result.Code);
            Assert.Empty(selling.Transitions);
            Assert.Equal("Sent", await StatusOfAsync(host, id));
        }

        // ---- ownership --------------------------------------------------------------------------

        [Fact]
        public async Task Another_customer_in_the_same_company_cannot_answer_the_offer()
        {
            // The case a tenant-only scope gets wrong, and the reason CustomerId is a predicate rather
            // than a comparison after loading.
            using var host = new PortalHost();
            var id = host.SeedQuotation(PortalHost.BetaId, PortalHost.CompanyA);
            host.ActAs(PortalHost.AcmeId, PortalHost.CompanyA, Responder);

            var (service, selling) = Decider(host);
            var result = await service.RespondToQuotationAsync(id, accept: true);

            Assert.False(result.Ok);
            Assert.Equal(PortalDecisionResult.NotFound, result.Code);   // identical to absent
            Assert.Empty(selling.Transitions);
            Assert.Equal("Sent", await StatusOfAsync(host, id));
        }

        [Fact]
        public async Task Another_companys_quotation_is_refused_exactly_as_an_absent_one_is()
        {
            using var host = new PortalHost();
            var foreign = host.SeedQuotation(PortalHost.OtherCoCustomerId, PortalHost.CompanyB);
            host.ActAs(PortalHost.AcmeId, PortalHost.CompanyA, Responder);

            var (service, selling) = Decider(host);
            var onForeign = await service.RespondToQuotationAsync(foreign, accept: true);
            var onAbsent = await service.RespondToQuotationAsync(999_999, accept: true);

            // Byte-identical refusals: the id cannot be probed for existence.
            Assert.Equal(onAbsent.Ok, onForeign.Ok);
            Assert.Equal(onAbsent.Code, onForeign.Code);
            Assert.Empty(selling.Transitions);
            Assert.Equal("Sent", await StatusOfAsync(host, foreign));
        }

        [Fact]
        public async Task A_customer_id_that_collides_across_companies_grants_nothing()
        {
            // Same numeric customer id, different company. A scope that compared only the number would
            // hand this one over.
            using var host = new PortalHost();
            host.Platform.Seed.Customers.Add(new CrossBuy.Models.Context.Accounting.Customer
            { ID = 777, CompanyID = PortalHost.CompanyB, Name = "Collider", ControlAccountId = 1 });
            await host.Platform.Seed.SaveChangesAsync();
            var theirs = host.SeedQuotation(777, PortalHost.CompanyB);

            // The caller is customer 777 of company A - the same number, the other tenant.
            host.Platform.Seed.Customers.Add(new CrossBuy.Models.Context.Accounting.Customer
            { ID = 778, CompanyID = PortalHost.CompanyA, Name = "Mine", ControlAccountId = 1 });
            await host.Platform.Seed.SaveChangesAsync();
            host.ActAs(777, PortalHost.CompanyA, Responder);

            var (service, selling) = Decider(host);
            var result = await service.RespondToQuotationAsync(theirs, accept: true);

            Assert.False(result.Ok);
            Assert.Equal(PortalDecisionResult.NotFound, result.Code);
            Assert.Empty(selling.Transitions);
        }

        // ---- the module's own refusal -----------------------------------------------------------

        [Fact]
        public async Task A_module_refusal_is_reported_rather_than_reinterpreted_as_success()
        {
            // The portal decides WHETHER to ask. The module decides whether the transition happens, and
            // if it says no the portal must not claim otherwise.
            using var host = new PortalHost();
            var id = host.SeedQuotation(PortalHost.AcmeId, PortalHost.CompanyA);
            host.ActAs(PortalHost.AcmeId, PortalHost.CompanyA, Responder);

            var (service, selling) = Decider(host);
            selling.RefuseNext = true;
            var result = await service.RespondToQuotationAsync(id, accept: true);

            Assert.False(result.Ok);
            Assert.Equal("Sent", await StatusOfAsync(host, id));
        }

        [Fact]
        public async Task A_host_without_the_selling_module_refuses_honestly_instead_of_writing_the_status()
        {
            // The portal never writes the transition itself. Without the module it has nothing to
            // delegate to, and says so.
            using var host = new PortalHost();
            var id = host.SeedQuotation(PortalHost.AcmeId, PortalHost.CompanyA);
            host.ActAs(PortalHost.AcmeId, PortalHost.CompanyA, Responder);

            var service = new PortalDataService(host.Platform.Seed, host.Contexts);   // no selling
            var result = await service.RespondToQuotationAsync(id, accept: true);

            Assert.False(result.Ok);
            Assert.Equal(PortalDecisionResult.Unavailable, result.Code);
            Assert.Equal("Sent", await StatusOfAsync(host, id));
        }

        // ---- structural: the request cannot be the authority -------------------------------------

        private static string RepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "CrossBuy.sln"))) dir = dir.Parent;
            Assert.NotNull(dir);
            return dir!.FullName;
        }

        private static string CodeOnly(string source)
        {
            var withoutBlocks = Regex.Replace(source, @"/\*.*?\*/", "", RegexOptions.Singleline);
            var sb = new System.Text.StringBuilder(withoutBlocks.Length);
            foreach (var line in withoutBlocks.Split('\n'))
            {
                int slash = line.IndexOf("//", StringComparison.Ordinal);
                sb.Append(slash >= 0 ? line[..slash] : line).Append('\n');
            }
            return sb.ToString();
        }

        [Fact]
        public void The_decision_takes_no_company_or_customer_from_its_caller()
        {
            // THE STRONGEST FORM OF "a request parameter is not authority": there is no parameter to
            // pass one through. A behavioural test can only show that a supplied value was ignored;
            // this shows the door does not exist. Comments are stripped, because the header beside this
            // method discusses the very identifiers it forbids.
            var code = CodeOnly(File.ReadAllText(Path.Combine(
                RepoRoot(), "CrossBuy", "BL", "Portal", "PortalDataService.cs")));

            Assert.Matches(new Regex(
                @"Task<PortalDecisionResult>\s+RespondToQuotationAsync\(\s*int\s+quotationId,\s*bool\s+accept,\s*CancellationToken"),
                code);

            // and the company handed to the module is the resolved one, never a parameter.
            // The company handed to the module is the RESOLVED one, never a parameter - and the
            // customer-decision flag is passed, which is what makes the transition a recorded fact.
            Assert.Contains("SetQuotationStatusAsync(", code, StringComparison.Ordinal);
            Assert.Contains("ctx.CompanyId, quotationId, target, customerDecision: true", code, StringComparison.Ordinal);
        }

        [Fact]
        public void Both_decision_endpoints_are_POST_with_anti_forgery()
        {
            var code = CodeOnly(File.ReadAllText(Path.Combine(
                RepoRoot(), "CrossBuy", "Controllers", "ClientPortalController.cs")));

            foreach (var action in new[] { "AcceptQuotation", "RejectQuotation" })
            {
                int at = code.IndexOf(action, StringComparison.Ordinal);
                Assert.True(at > 0, action + " is missing");

                // The two attributes immediately above the action, in the 260 characters before it.
                var attributes = code[Math.Max(0, at - 260)..at];
                Assert.Contains("HttpPost", attributes, StringComparison.Ordinal);
                Assert.Contains("ValidateAntiForgeryToken", attributes, StringComparison.Ordinal);
            }

            // No GET may reach the decision: a link, a prefetch or a foreign page would be enough.
            Assert.DoesNotMatch(new Regex(@"HttpGet\([^)]*Accept"), code);
            Assert.DoesNotMatch(new Regex(@"HttpGet\([^)]*Reject"), code);
        }
    }
}
