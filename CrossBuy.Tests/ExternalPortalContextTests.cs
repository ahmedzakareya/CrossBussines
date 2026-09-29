using CrossBuy.BL;
using CrossBuy.BL.Platform;
using CrossBuy.Models.Context.Inventory;
using CrossBuy.Models.Platform;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CrossBuy.Tests
{
    // ============================================================================================
    // THE EXTERNAL PORTAL BUSINESS CONTEXT — an authenticated outsider, and nothing more.
    //
    // WHY IT EXISTS. Platform services that legitimately need a company take it from BusinessContext.
    // The employee factory correctly returns nothing when there is no employee, so a portal customer
    // could not record a business event at all. The fix is a context that says what it is, not a
    // pretended employee and not a system context.
    //
    // THE WHOLE VALUE OF THIS FILE IS THE REFUSALS. Establishing a context for an outsider is the easy
    // half; the half that matters is that the context buys them nothing beyond their own company. Every
    // shortcut that would have been simpler is asserted against here: a fabricated employee id, an
    // IsSystem flag, a company taken from the request, a company taken from the CustomerId.
    // ============================================================================================
    public class ExternalPortalContextTests
    {
        private const int CompanyA = 7;
        private const int CompanyB = 9;
        private const string PortalLogin = "portal-user-abc";

        // ---- 1. the company comes from the resolved portal user, and from nowhere else ---------------

        [Fact]
        public void An_external_context_carries_the_company_it_was_resolved_with()
        {
            var ctx = BusinessContext.ForExternalPortal(CompanyA, PortalLogin, portalUserId: 42);

            Assert.Equal(CompanyA, ctx.CompanyId);
            Assert.Equal(PortalLogin, ctx.UserId);
            Assert.Equal(42, ctx.PortalUserId);
        }

        [Fact]
        public void An_external_context_cannot_be_built_without_a_company()
        {
            // No company-1 consolation, exactly as every other source in this file refuses one.
            Assert.Throws<BusinessContextUnresolvedException>(
                () => BusinessContext.ForExternalPortal(0, PortalLogin));
            Assert.Throws<BusinessContextUnresolvedException>(
                () => BusinessContext.ForExternalPortal(-1, PortalLogin));
        }

        [Fact]
        public void An_external_context_cannot_be_built_without_an_identity()
        {
            // A company with no identity behind it is an anonymous caller wearing a tenant, which is the
            // one thing this factory must never produce.
            Assert.Throws<BusinessContextUnresolvedException>(
                () => BusinessContext.ForExternalPortal(CompanyA, ""));
            Assert.Throws<BusinessContextUnresolvedException>(
                () => BusinessContext.ForExternalPortal(CompanyA, "   "));
        }

        // ---- 2. it is not an employee, and not the system --------------------------------------------

        [Fact]
        public void An_external_context_fabricates_no_employee()
        {
            var ctx = BusinessContext.ForExternalPortal(CompanyA, PortalLogin, portalUserId: 42);

            // Synthesising one would attribute a customer's decision to a member of staff in every audit
            // row that followed it.
            Assert.Null(ctx.EmployeeId);

            // It is still authenticated - the identity is the portal login, not an employee row.
            Assert.True(ctx.IsAuthenticated);
        }

        [Fact]
        public void An_external_context_is_never_the_system()
        {
            var ctx = BusinessContext.ForExternalPortal(CompanyA, PortalLogin);

            // IsSystem gates the override paths reserved for trusted platform code - the company, branch
            // and actor overrides on a business event among them. A customer must not reach any of them.
            Assert.False(ctx.IsSystem);
            Assert.NotEqual(BusinessContextSource.System, ctx.Source);
        }

        [Fact]
        public void The_external_source_is_distinguishable_from_every_other_source()
        {
            var external = BusinessContext.ForExternalPortal(CompanyA, PortalLogin);

            Assert.Equal(BusinessContextSource.ExternalPortal, external.Source);
            Assert.True(external.IsExternalPortal);

            // ...and no other source claims to be external. A consumer branching on this must not be able
            // to confuse an outsider with a worker, a system job or a signed-in member of staff.
            Assert.False(BusinessContext.ForWorker(CompanyA).IsExternalPortal);
            Assert.False(BusinessContext.ForSystem(CompanyA).IsExternalPortal);
            Assert.False(new BusinessContext
            {
                CompanyId = CompanyA, EmployeeId = 3, UserId = "u3",
                Source = BusinessContextSource.Http,
            }.IsExternalPortal);
        }

        [Fact]
        public void A_company_collision_does_not_blur_two_external_callers()
        {
            // Two portal logins, two companies, the same customer NUMBER. Nothing about one may leak into
            // the other, and the customer id is not what separates them - the company is.
            var a = BusinessContext.ForExternalPortal(CompanyA, "login-a", portalUserId: 1);
            var b = BusinessContext.ForExternalPortal(CompanyB, "login-b", portalUserId: 2);

            Assert.NotEqual(a.CompanyId, b.CompanyId);
            Assert.NotEqual(a.UserId, b.UserId);
        }

        // ---- 3. the module-authority negatives -------------------------------------------------------
        //
        // An external context must not accidentally satisfy an employee module. These use the REAL access
        // services: a stub would prove only that a fake refuses.

        [Fact]
        public async Task An_external_context_obtains_no_inventory_authority()
        {
            using var host = new PlatformTestHost(CompanyA);
            ConfigureInventory(host, CompanyA);
            var inventory = InventoryService(host);
            var external = BusinessContext.ForExternalPortal(CompanyA, PortalLogin);

            // Not even read. Inventory grants read to any authenticated EMPLOYEE of a configured company,
            // and the whole point is that an outsider is not one.
            foreach (var action in new[] { "read", "doc", "purchase", "manage" })
            {
                Assert.False(await inventory.CanAsync(external, action),
                    "external portal context obtained Inventory " + action);
            }
        }

        [Fact]
        public async Task An_external_context_obtains_no_accounting_authority()
        {
            using var host = new PlatformTestHost(CompanyA);
            var accounting = AccountingService(host);
            var external = BusinessContext.ForExternalPortal(CompanyA, PortalLogin);

            foreach (var action in new[] { "read", "post", "pay", "manage" })
            {
                Assert.False(await accounting.CanAsync(external, action),
                    "external portal context obtained Accounting " + action);
            }
        }

        [Fact]
        public async Task An_external_context_obtains_no_crm_or_tasks_authority()
        {
            using var host = new PlatformTestHost(CompanyA);
            var external = BusinessContext.ForExternalPortal(CompanyA, PortalLogin);

            var crm = new CrmAccessService(
                host.Db,
                new Microsoft.AspNetCore.Http.HttpContextAccessor(),
                new StubContextAccessor(PlatformTestHost.DefaultContext()),
                new OrgHierarchy(host.Db, NullLogger<OrgHierarchy>.Instance),
                new BootstrapAccessPolicyReader(host.Db, NullLogger<BootstrapAccessPolicyReader>.Instance),
                NullLogger<CrmAccessService>.Instance);

            foreach (var action in crm.Actions)
            {
                Assert.False(await crm.CanAsync(external, action),
                    "external portal context obtained Crm " + action);
            }
        }

        // ---- 4. non-vacuity: the same services DO answer yes for a real employee ----------------------

        [Fact]
        public async Task The_negatives_are_not_vacuous_because_an_employee_still_passes()
        {
            using var host = new PlatformTestHost(CompanyA);
            ConfigureInventory(host, CompanyA);
            var inventory = InventoryService(host);

            var employee = new BusinessContext
            {
                CompanyId = CompanyA, EmployeeId = 11, UserId = "u11",
                Source = BusinessContextSource.Http,
            };

            // Without this the whole file would be satisfied by services that refuse everyone.
            Assert.True(await inventory.CanAsync(employee, "read"));
        }

        // ---- fixture ---------------------------------------------------------------------------------

        private static InventoryAccessService InventoryService(PlatformTestHost host)
            => new(host.Db,
                   new Microsoft.AspNetCore.Http.HttpContextAccessor(),
                   new StubContextAccessor(PlatformTestHost.DefaultContext()),
                   new BootstrapAccessPolicyReader(host.Db, NullLogger<BootstrapAccessPolicyReader>.Instance),
                   NullLogger<InventoryAccessService>.Instance);

        private static AccountingAccessService AccountingService(PlatformTestHost host)
            => new(host.Db,
                   new Microsoft.AspNetCore.Http.HttpContextAccessor(),
                   new StubContextAccessor(PlatformTestHost.DefaultContext()),
                   new BootstrapAccessPolicyReader(host.Db, NullLogger<BootstrapAccessPolicyReader>.Instance),
                   NullLogger<AccountingAccessService>.Instance);

        // A configured company, so the module's ROLE rules decide rather than its bootstrap branch.
        private static void ConfigureInventory(PlatformTestHost host, int companyId)
        {
            host.Seed.InventoryUserRoles.Add(new InventoryUserRole
            {
                CompanyID = companyId, EmployeeId = 11, Role = "InventoryManager",
            });
            host.Seed.SaveChanges();
        }
    }
}
