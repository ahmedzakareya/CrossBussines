using CrossBuy.BL;
using CrossBuy.BL.Platform;
using CrossBuy.Models.Context.Platform;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CrossBuy.Tests
{
    // ============================================================================================
    // CRM AUTHORITY — the absence of configuration is not authority.
    //
    // WHAT THIS REPLACED. CrmAccessService opened with:
    //
    //     if (!await AnyRoleConfiguredAsync(companyId, ct)) return true;
    //
    // so a company that had never configured CRM allowed EVERY action to EVERY authenticated caller,
    // and "nobody has set this up yet" was indistinguishable from "everybody may do everything". The
    // same absence made VisibleOwnerIdsAsync return null - unrestricted - so a caller whose authority
    // could not be established saw the WIDEST possible set of records rather than the narrowest.
    //
    // Both are now decided by IBootstrapAccessPolicyReader, the platform authority AccountingAccessService
    // already uses: NeverBootstrapOpen is evaluated first, then an explicit, active, in-date policy row
    // for THIS company, THIS scope and THIS action. No row, a stale row, or a row belonging to another
    // company all refuse.
    //
    // These tests are written against the SERVICE rather than a stub of it, because the defect was in
    // exactly the branch a stub would have replaced.
    // ============================================================================================
    public class CrmBootstrapAuthorityTests
    {
        private static CrmAccessService Service(PlatformTestHost host)
            => new(host.Db,
                   new Microsoft.AspNetCore.Http.HttpContextAccessor(),
                   new StubContextAccessor(PlatformTestHost.DefaultContext()),
                   new OrgHierarchy(host.Db, NullLogger<OrgHierarchy>.Instance),
                   new BootstrapAccessPolicyReader(host.Db, NullLogger<BootstrapAccessPolicyReader>.Instance),
                   NullLogger<CrmAccessService>.Instance);

        private static async Task SeedRoleAsync(PlatformTestHost host, int companyId, int employeeId, string role)
        {
            host.Seed.CrmUserRoles.Add(new CrossBuy.Models.Context.Crm.CrmUserRole
            {
                CompanyID = companyId, EmployeeId = employeeId, Role = role,
            });
            await host.Seed.SaveChangesAsync();
        }

        private static async Task SeedPolicyAsync(
            PlatformTestHost host, int companyId, string scope, string action,
            bool active = true, DateTime? expiresAt = null)
        {
            host.Seed.BootstrapAccessPolicies.Add(new BootstrapAccessPolicy
            {
                CompanyID = companyId, Scope = scope, ActionCode = action,
                IsActive = active, ExpiresAt = expiresAt,
                Reason = "test compatibility policy",
            });
            await host.Seed.SaveChangesAsync();
        }

        // ---- the defect itself --------------------------------------------------------------------

        [Fact]
        public async Task A_company_with_no_roles_is_not_universally_open()
        {
            using var host = new PlatformTestHost();
            var crm = Service(host);
            var context = PlatformTestHost.DefaultContext(companyId: 1, employeeId: 7);

            // No CRM roles, and no bootstrap policy. Every action must refuse - this is the exact case
            // that used to return true for all three.
            foreach (var action in new[] { "read", "edit", "manage" })
                Assert.False(await crm.CanAsync(context, action),
                    $"'{action}' was allowed in a company with no roles and no policy");
        }

        [Fact]
        public async Task Read_is_not_unconditionally_true()
        {
            using var host = new PlatformTestHost();

            // The narrowest statement of the defect: read used to be `=> true` on every path.
            Assert.False(await Service(host).CanAsync(PlatformTestHost.DefaultContext(), "read"));
        }

        // ---- the policy is the authority, and only for its own coordinates ------------------------

        [Fact]
        public async Task An_explicit_policy_allows_only_its_own_company_scope_and_action()
        {
            using var host = new PlatformTestHost();
            await SeedPolicyAsync(host, companyId: 1, scope: EntityRegistry.ScopeCrm, action: "read");
            var crm = Service(host);

            // The policy's own coordinates: allowed.
            Assert.True(await crm.CanAsync(PlatformTestHost.DefaultContext(companyId: 1), "read"));

            // A different ACTION in the same company is not covered by it.
            Assert.False(await crm.CanAsync(PlatformTestHost.DefaultContext(companyId: 1), "edit"));

            // A different COMPANY is not covered by it - the property that stops one company's
            // compatibility row becoming every company's.
            Assert.False(await crm.CanAsync(PlatformTestHost.DefaultContext(companyId: 2), "read"));
        }

        [Fact]
        public async Task An_inactive_or_expired_policy_refuses()
        {
            using var host = new PlatformTestHost();
            await SeedPolicyAsync(host, 1, EntityRegistry.ScopeCrm, "read", active: false);
            await SeedPolicyAsync(host, 1, EntityRegistry.ScopeCrm, "edit", expiresAt: DateTime.UtcNow.AddDays(-1));

            var crm = Service(host);
            var context = PlatformTestHost.DefaultContext(companyId: 1);

            Assert.False(await crm.CanAsync(context, "read"));
            Assert.False(await crm.CanAsync(context, "edit"));
        }

        [Fact]
        public async Task Never_bootstrap_open_beats_a_permitting_policy_row()
        {
            using var host = new PlatformTestHost();

            // Crm/manage is on the NeverBootstrapOpen list, so writing a permitting row by hand must not
            // buy the grant. This is the assertion that makes the list load-bearing rather than advisory.
            await SeedPolicyAsync(host, 1, EntityRegistry.ScopeCrm, "manage");

            Assert.False(await Service(host).CanAsync(PlatformTestHost.DefaultContext(companyId: 1), "manage"));
        }

        // ---- configured companies are unchanged ----------------------------------------------------

        [Fact]
        public async Task A_configured_company_still_behaves_exactly_as_before()
        {
            using var host = new PlatformTestHost();
            await SeedRoleAsync(host, companyId: 1, employeeId: 7, role: "SalesManager");
            var crm = Service(host);
            var context = PlatformTestHost.DefaultContext(companyId: 1, employeeId: 7);

            // Roles decide once the company has configured CRM; read stays open WITHIN such a company,
            // which is the Accounting precedent and is not what this change was about.
            Assert.True(await crm.CanAsync(context, "read"));
            Assert.True(await crm.CanAsync(context, "edit"));
            Assert.True(await crm.CanAsync(context, "manage"));
        }

        [Fact]
        public async Task One_companys_roles_do_not_decide_another_companys_access()
        {
            using var host = new PlatformTestHost();

            // Company 1 is fully configured; company 2 has nothing.
            await SeedRoleAsync(host, companyId: 1, employeeId: 7, role: "SalesManager");
            var crm = Service(host);

            Assert.True(await crm.CanAsync(PlatformTestHost.DefaultContext(companyId: 1, employeeId: 7), "manage"));

            // The same employee id in company 2 holds nothing there, and company 2 has no policy either.
            Assert.False(await crm.CanAsync(PlatformTestHost.DefaultContext(companyId: 2, employeeId: 7), "manage"));
            Assert.False(await crm.CanAsync(PlatformTestHost.DefaultContext(companyId: 2, employeeId: 7), "read"));
        }

        // ---- visibility never widens on an unresolved authority ------------------------------------

        [Fact]
        public async Task Owner_visibility_never_widens_when_authority_cannot_be_resolved()
        {
            using var host = new PlatformTestHost();
            var crm = Service(host);

            // Unconfigured company: this used to return null, meaning "unrestricted". It must now be the
            // caller's own records and nothing else.
            var visible = await crm.VisibleOwnerIdsAsync(PlatformTestHost.DefaultContext(companyId: 1, employeeId: 7));

            Assert.NotNull(visible);
            Assert.Equal(new[] { 7 }, visible!.OrderBy(x => x).ToArray());
        }

        [Fact]
        public async Task An_unresolved_employee_sees_nothing_rather_than_everything()
        {
            using var host = new PlatformTestHost();

            var visible = await Service(host)
                .VisibleOwnerIdsAsync(PlatformTestHost.DefaultContext(companyId: 1, employeeId: null));

            Assert.NotNull(visible);
            Assert.Empty(visible!);
        }

        [Fact]
        public async Task A_bootstrap_allow_still_cannot_read_another_employees_records()
        {
            using var host = new PlatformTestHost();
            await SeedPolicyAsync(host, 1, EntityRegistry.ScopeCrm, "read");
            var crm = Service(host);
            var context = PlatformTestHost.DefaultContext(companyId: 1, employeeId: 7);

            // The policy grants the ACTION. It cannot say whose records, so the row-level answer is
            // own-only - a compatibility grant must not become a company-wide read.
            Assert.True(await crm.CanAsync(context, "read",
                new PermissionTarget { CompanyId = 1, OwnerEmployeeId = 7 }));
            Assert.False(await crm.CanAsync(context, "read",
                new PermissionTarget { CompanyId = 1, OwnerEmployeeId = 99 }));
        }

        // ---- the platform contract ------------------------------------------------------------------

        [Fact]
        public void The_service_satisfies_the_platform_module_contract()
        {
            Assert.True(typeof(IModuleAccessService).IsAssignableFrom(typeof(CrmAccessService)));
            Assert.True(typeof(ICrmAccessService).IsAssignableFrom(typeof(CrmAccessService)));
        }

        [Fact]
        public void The_three_registrations_resolve_one_instance()
        {
            var source = File.ReadAllText(Path.Combine(RepoRoot(), "CrossBuy", "Program.cs"));

            // Concrete, module interface, platform interface - the last two THROUGH the concrete one.
            // Registering IModuleAccessService with its own AddScoped<,> would give one request two
            // services, two role reads and two caches that can disagree inside a single call.
            Assert.Contains("AddScoped<CrmAccessService>()", source, StringComparison.Ordinal);
            Assert.Contains("AddScoped<ICrmAccessService>(sp => sp.GetRequiredService<CrmAccessService>())",
                source, StringComparison.Ordinal);
            Assert.Contains("IModuleAccessService>(sp => sp.GetRequiredService<CrmAccessService>())",
                source, StringComparison.Ordinal);
        }

        [Fact]
        public void The_service_no_longer_carries_a_hardcoded_company()
        {
            var source = File.ReadAllText(Path.Combine(RepoRoot(), "CrossBuy", "BL", "CrmAccessService.cs"));
            var code = string.Join("\n", source.Split('\n')
                .Where(l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal)
                         && !l.TrimStart().StartsWith("///", StringComparison.Ordinal)));

            Assert.DoesNotContain("CompanyId = 1", code, StringComparison.Ordinal);
            Assert.DoesNotContain("CompanyID == 1", code, StringComparison.Ordinal);
        }

        private static string RepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "CrossBuy.sln"))) dir = dir.Parent;
            Assert.NotNull(dir);
            return dir!.FullName;
        }
    }
}
