using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CrossBuy.BL.Portal;
using CrossBuy.Models.Context.Accounting;
using CrossBuy.Models.Context.Inventory;
using CrossBuy.Models.Context.Portal;
using CrossBuy.Tests.Communication;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CrossBuy.Tests.Portal
{
    // =============================================================================================
    // CLIENT PORTAL — the external boundary.
    //
    // The question every test here asks is the same one: can an outsider see something that is not
    // theirs? Two customers of ONE company is the case that matters most, because a portal that only
    // scopes by tenant looks correct in every single-customer demo and leaks on the day the second
    // customer signs in.
    //
    // These run the REAL PortalDataService over a real DbContext. Only the identity accessor is
    // doubled, and only so a test can be somebody — it returns whatever a real PortalUser row would
    // have produced, and null when there is no row, which is the production behaviour.
    // =============================================================================================

    internal sealed class StubPortalContext : IPortalContextAccessor
    {
        public PortalContext? Current;
        public Task<PortalContext?> TryGetCurrentAsync(CancellationToken ct = default)
            => Task.FromResult(Current);
    }

    internal sealed class PortalHost : IDisposable
    {
        public readonly PlatformTestHost Platform = new(companyId: null);
        public readonly StubPortalContext Contexts = new();

        public const int CompanyA = 1, CompanyB = 2;
        public const int AcmeId = 100, BetaId = 200, OtherCoCustomerId = 300;

        public PortalHost()
        {
            // Two customers in ONE company, plus a same-numbered customer in another company. The
            // second is what catches "scoped by tenant" mistakes; the third catches "scoped by id".
            AddCustomer(AcmeId, CompanyA, "Acme");
            AddCustomer(BetaId, CompanyA, "Beta");
            AddCustomer(OtherCoCustomerId, CompanyB, "OtherCo");
            Platform.Seed.SaveChanges();
            ActAs(AcmeId, CompanyA);
        }

        private void AddCustomer(int id, int companyId, string name)
            => Platform.Seed.Customers.Add(new Customer { ID = id, CompanyID = companyId, Name = name, ControlAccountId = 1 });

        public void ActAs(int customerId, int companyId, params string[] capabilities)
            => Contexts.Current = new PortalContext(companyId, customerId, 1,
                capabilities.Length > 0 ? capabilities : PortalCapabilities.Default);

        public void ActAsNobody() => Contexts.Current = null;

        /// Deliberately the CROSS-COMPANY context. The portal's own two predicates are what is
        /// under test, so handing the service a context that already filters by company would let
        /// every isolation test pass whether or not the service scoped anything at all.
        public PortalDataService Service() => new(Platform.Seed, Contexts);

        public int SeedQuotation(int customerId, int companyId, string status = "Sent", decimal total = 100m, string? no = null)
        {
            var q = new Quotation
            {
                CompanyID = companyId, CustomerId = customerId, Status = status, GrandTotal = total,
                QuoteNo = no ?? ("Q-" + customerId + "-" + status), QuoteDate = DateTime.UtcNow.AddDays(-1),
                ValidUntil = DateTime.UtcNow.AddDays(30),
            };
            Platform.Seed.Quotations.Add(q); Platform.Seed.SaveChanges(); return q.ID;
        }

        public int SeedInvoice(int customerId, int companyId, string status = "Posted", decimal total = 500m)
        {
            var i = new SalesInvoice
            {
                CompanyID = companyId, CustomerId = customerId, Status = status, GrandTotal = total,
                InvoiceNo = "INV-" + customerId, InvoiceDate = DateTime.UtcNow.AddDays(-5),
            };
            Platform.Seed.SalesInvoices.Add(i); Platform.Seed.SaveChanges(); return i.ID;
        }

        public int SeedProject(int? customerId, int companyId, string status = "Active", string name = "Site works")
        {
            var p = new Project
            {
                CompanyID = companyId, CustomerId = customerId, Code = "P" + (customerId ?? 0),
                Name = name, NameEn = name, Status = status, IsActive = true,
                Budget = 999_999m, ContractValue = 888_888m, RetentionPercent = 5m,
            };
            Platform.Seed.Projects.Add(p); Platform.Seed.SaveChanges(); return p.ID;
        }

        public void Dispose() => Platform.Dispose();
    }

    public class ClientPortalSecurityTests
    {
        // ---- the case that matters most: same company, different customer --------------------
        [Fact]
        public async Task Customer_A_never_sees_customer_B_even_though_they_share_a_company()
        {
            using var h = new PortalHost();
            h.SeedQuotation(PortalHost.AcmeId, PortalHost.CompanyA, no: "ACME-Q");
            h.SeedQuotation(PortalHost.BetaId, PortalHost.CompanyA, no: "BETA-Q");
            h.SeedInvoice(PortalHost.AcmeId, PortalHost.CompanyA, total: 500m);
            h.SeedInvoice(PortalHost.BetaId, PortalHost.CompanyA, total: 900m);
            h.SeedProject(PortalHost.AcmeId, PortalHost.CompanyA, name: "Acme site");
            h.SeedProject(PortalHost.BetaId, PortalHost.CompanyA, name: "Beta site");

            var quotations = await h.Service().QuotationsAsync();
            var invoices = await h.Service().InvoicesAsync();
            var projects = await h.Service().ProjectsAsync();

            Assert.Equal("ACME-Q", Assert.Single(quotations).Number);
            Assert.Equal(500m, Assert.Single(invoices).Total);
            Assert.Equal("Acme site", Assert.Single(projects).Name);
        }

        [Fact]
        public async Task Same_company_does_not_imply_same_customer_in_the_home_totals()
        {
            // The outstanding figure is the one a client will read as "what I owe". Summing the
            // company's receivables here would be the most damaging possible leak.
            using var h = new PortalHost();
            h.SeedInvoice(PortalHost.AcmeId, PortalHost.CompanyA, total: 500m);
            h.SeedInvoice(PortalHost.BetaId, PortalHost.CompanyA, total: 900m);

            var home = await h.Service().HomeAsync();

            Assert.NotNull(home);
            Assert.Equal("Acme", home!.CustomerName);
            Assert.Equal(1, home.OutstandingInvoices);
            Assert.Equal(500m, home.OutstandingAmount);
        }

        [Fact]
        public async Task The_same_customer_id_in_another_company_is_a_different_business()
        {
            using var h = new PortalHost();
            h.SeedQuotation(PortalHost.AcmeId, PortalHost.CompanyB, no: "OTHERCO-Q");   // same id, other tenant

            var listed = await h.Service().QuotationsAsync();

            Assert.Empty(listed);
        }

        [Fact]
        public async Task A_portal_identity_for_another_company_sees_nothing_of_this_one()
        {
            using var h = new PortalHost();
            h.SeedQuotation(PortalHost.AcmeId, PortalHost.CompanyA);
            h.SeedInvoice(PortalHost.AcmeId, PortalHost.CompanyA);

            h.ActAs(PortalHost.OtherCoCustomerId, PortalHost.CompanyB);

            Assert.Empty(await h.Service().QuotationsAsync());
            Assert.Empty(await h.Service().InvoicesAsync());
            Assert.Empty(await h.Service().ProjectsAsync());
        }

        // ---- fails closed ----------------------------------------------------------------------
        [Fact]
        public async Task An_unresolved_portal_identity_fails_closed_everywhere()
        {
            using var h = new PortalHost();
            h.SeedQuotation(PortalHost.AcmeId, PortalHost.CompanyA);
            h.SeedInvoice(PortalHost.AcmeId, PortalHost.CompanyA);
            h.SeedProject(PortalHost.AcmeId, PortalHost.CompanyA);

            h.ActAsNobody();

            Assert.Null(await h.Service().HomeAsync());
            Assert.Empty(await h.Service().QuotationsAsync());
            Assert.Empty(await h.Service().InvoicesAsync());
            Assert.Empty(await h.Service().ProjectsAsync());
        }

        [Fact]
        public async Task A_link_pointing_at_a_customer_that_does_not_exist_is_not_a_session()
        {
            using var h = new PortalHost();
            h.ActAs(customerId: 999999, companyId: PortalHost.CompanyA);

            Assert.Null(await h.Service().HomeAsync());
        }

        [Fact]
        public async Task A_capability_the_login_does_not_hold_returns_nothing()
        {
            // Capability is granted, never inferred. A contact allowed to see invoices must not see
            // quotations merely because both live behind the same login.
            using var h = new PortalHost();
            h.SeedQuotation(PortalHost.AcmeId, PortalHost.CompanyA);
            h.SeedInvoice(PortalHost.AcmeId, PortalHost.CompanyA);

            h.ActAs(PortalHost.AcmeId, PortalHost.CompanyA, PortalCapabilities.ViewInvoices);

            Assert.Empty(await h.Service().QuotationsAsync());
            Assert.Single(await h.Service().InvoicesAsync());
        }

        // ---- internal state stays internal -------------------------------------------------------
        [Fact]
        public async Task A_draft_quotation_is_not_the_customers_business_yet()
        {
            using var h = new PortalHost();
            h.SeedQuotation(PortalHost.AcmeId, PortalHost.CompanyA, status: "Draft", no: "DRAFT-Q");
            h.SeedQuotation(PortalHost.AcmeId, PortalHost.CompanyA, status: "Sent", no: "SENT-Q");

            var listed = await h.Service().QuotationsAsync();

            Assert.Equal("SENT-Q", Assert.Single(listed).Number);
        }

        [Fact]
        public async Task Only_posted_invoices_are_shown_so_a_client_is_never_told_they_owe_a_draft()
        {
            using var h = new PortalHost();
            h.SeedInvoice(PortalHost.AcmeId, PortalHost.CompanyA, status: "Draft", total: 111m);
            h.SeedInvoice(PortalHost.AcmeId, PortalHost.CompanyA, status: "Cancelled", total: 222m);
            h.SeedInvoice(PortalHost.AcmeId, PortalHost.CompanyA, status: "Posted", total: 333m);

            var listed = await h.Service().InvoicesAsync();

            Assert.Equal(333m, Assert.Single(listed).Total);
        }

        [Fact]
        public async Task An_internal_project_with_no_customer_is_never_somebody_elses()
        {
            using var h = new PortalHost();
            h.SeedProject(customerId: null, companyId: PortalHost.CompanyA, name: "Internal R&D");
            h.SeedProject(PortalHost.AcmeId, PortalHost.CompanyA, name: "Acme site");

            var listed = await h.Service().ProjectsAsync();

            Assert.Equal("Acme site", Assert.Single(listed).Name);
        }

        [Fact]
        public void The_portal_DTOs_carry_no_internal_field()
        {
            // Hand-written projections, asserted by shape. If somebody later adds Budget to the
            // project DTO to make a screen easier, this fails before the leak ships.
            var project = typeof(PortalProject).GetProperties().Select(p => p.Name).ToList();
            foreach (var internalOnly in new[] { "Budget", "ContractValue", "AdvancePercent", "RetentionPercent", "CostCenterId", "ActivityTypeId" })
                Assert.DoesNotContain(internalOnly, project);

            var invoice = typeof(PortalInvoice).GetProperties().Select(p => p.Name).ToList();
            foreach (var internalOnly in new[] { "JournalEntryId", "SubTotalBase", "TaxTotalBase", "ExchangeRate", "ControlAccountId", "EtaStatus" })
                Assert.DoesNotContain(internalOnly, invoice);

            var quotation = typeof(PortalQuotation).GetProperties().Select(p => p.Name).ToList();
            foreach (var internalOnly in new[] { "Notes", "CreatedBy", "WarehouseId", "SalesOrderId", "ExchangeRate" })
                Assert.DoesNotContain(internalOnly, quotation);
        }

        [Fact]
        public async Task Settlement_never_sums_another_customers_receipts()
        {
            using var h = new PortalHost();
            var mine = h.SeedInvoice(PortalHost.AcmeId, PortalHost.CompanyA, total: 500m);
            var theirs = h.SeedInvoice(PortalHost.BetaId, PortalHost.CompanyA, total: 900m);

            h.Platform.Seed.ReceiptAllocations.AddRange(
                new ReceiptAllocation { CompanyID = PortalHost.CompanyA, SalesInvoiceId = mine, ForeignAmount = 200m, ReceiptId = 1 },
                new ReceiptAllocation { CompanyID = PortalHost.CompanyA, SalesInvoiceId = theirs, ForeignAmount = 400m, ReceiptId = 2 });
            await h.Platform.Seed.SaveChangesAsync();

            var invoice = Assert.Single(await h.Service().InvoicesAsync());

            Assert.Equal(200m, invoice.Paid);
            Assert.Equal(300m, invoice.Outstanding);
        }

        // ---- the employee boundary ----------------------------------------------------------------
        [Fact]
        public void The_portal_context_cannot_become_an_employee_context()
        {
            // Structural, not a policy check. PortalContext has no EmployeeId and no route to a
            // BusinessContext, so there is nothing for an internal access service to accept — those
            // services resolve through BusinessContextFactory, which needs an Employee row a portal
            // user does not have.
            var portal = typeof(PortalContext).GetProperties().Select(p => p.Name).ToList();
            Assert.DoesNotContain("EmployeeId", portal);
            Assert.Contains("CustomerId", portal);

            Assert.False(typeof(CrossBuy.Models.Platform.BusinessContext)
                .IsAssignableFrom(typeof(PortalContext)));
        }

        [Fact]
        public void The_portal_registration_hands_the_container_no_internal_authority()
        {
            var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
            PortalRegistration.AddClientPortal(services);

            var registered = services.Select(d => d.ServiceType.Name).ToList();
            Assert.Contains("IPortalContextAccessor", registered);
            Assert.Contains("IPortalDataService", registered);
            // No access service, no business context, no employee anything.
            Assert.DoesNotContain(registered, n => n.Contains("AccessService", StringComparison.Ordinal));
            Assert.DoesNotContain(registered, n => n.Contains("BusinessContext", StringComparison.Ordinal));
            Assert.DoesNotContain(registered, n => n.Contains("Employee", StringComparison.Ordinal));
        }

        [Fact]
        public void A_new_portal_login_gets_the_read_only_default_and_not_the_response_capability()
        {
            // Accepting a quotation is a contractual act. It is named in the vocabulary so the read
            // side could be built against real words, and it is deliberately NOT in the default grant.
            Assert.DoesNotContain(PortalCapabilities.RespondToQuotations, PortalCapabilities.Default);
            Assert.Contains(PortalCapabilities.ViewInvoices, PortalCapabilities.Default);
        }
    }
}
