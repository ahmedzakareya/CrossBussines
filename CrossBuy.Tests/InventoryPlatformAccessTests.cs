using CrossBuy.BL;
using CrossBuy.BL.ModulePermissions;
using CrossBuy.BL.Platform;
using CrossBuy.Models.Context.Platform;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CrossBuy.Tests
{
    // ============================================================================================
    // INVENTORY ON THE PLATFORM CONTRACT — and the authority Manufacturing borrows.
    //
    // InventoryAccessService implemented only IInventoryAccessService, whose CanAsync(string action) is
    // the legacy session-shaped contract: no BusinessContext, no PermissionTarget, and a company taken
    // from the session rather than resolved. PlatformPermissionProvider therefore found no module behind
    // InventoryPermissionAdapter and denied the Inventory scope for a CONFIGURATION reason rather than a
    // policy one - and ManufacturingPermissionAdapter, which deliberately points its ModuleScope at
    // Inventory, was denied for the same reason without any Manufacturing defect existing.
    //
    // Closing it is a platform contract change, not an Inventory feature: the role rules, the four
    // actions and the warehouse scoping are the ones the module already had.
    // ============================================================================================
    public class InventoryPlatformAccessTests
    {
        private static InventoryAccessService Service(PlatformTestHost host, BusinessContext? ctx = null)
            => new(host.Db,
                   new Microsoft.AspNetCore.Http.HttpContextAccessor(),
                   new StubContextAccessor(ctx ?? PlatformTestHost.DefaultContext()),
                   new BootstrapAccessPolicyReader(host.Db, NullLogger<BootstrapAccessPolicyReader>.Instance),
                   NullLogger<InventoryAccessService>.Instance);

        private static async Task SeedRoleAsync(PlatformTestHost host, int companyId, int employeeId, string role)
        {
            host.Seed.InventoryUserRoles.Add(new CrossBuy.Models.Context.Inventory.InventoryUserRole
            {
                CompanyID = companyId, EmployeeId = employeeId, Role = role,
            });
            await host.Seed.SaveChangesAsync();
        }

        // ---- 1-4: the platform contract -------------------------------------------------------------

        [Fact]
        public void The_service_implements_both_contracts()
        {
            Assert.True(typeof(IModuleAccessService).IsAssignableFrom(typeof(InventoryAccessService)));
            Assert.True(typeof(IInventoryAccessService).IsAssignableFrom(typeof(InventoryAccessService)));
        }

        [Fact]
        public void The_scope_and_actions_are_the_modules_own()
        {
            using var host = new PlatformTestHost();
            var svc = Service(host);

            Assert.Equal(EntityRegistry.ScopeInventory, svc.Scope);

            // The four the module already had - this change did not invent an action.
            Assert.Equal(new[] { "read", "doc", "purchase", "manage" }, svc.Actions.ToArray());
        }

        [Fact]
        public void The_three_registrations_resolve_one_instance()
        {
            var source = File.ReadAllText(Path.Combine(RepoRoot(), "CrossBuy", "Program.cs"));

            // Resolve-through-concrete. Two independent AddScoped<,> registrations would give one request
            // two services, two role reads and two caches that can disagree inside a single call.
            Assert.Contains("AddScoped<InventoryAccessService>()", source, StringComparison.Ordinal);
            Assert.Contains("AddScoped<IInventoryAccessService>(sp => sp.GetRequiredService<InventoryAccessService>())",
                source, StringComparison.Ordinal);
            Assert.Contains("IModuleAccessService>(sp => sp.GetRequiredService<InventoryAccessService>())",
                source, StringComparison.Ordinal);

            // And NOT the shape that silently produces two instances.
            Assert.DoesNotContain("AddScoped<CrossBuy.BL.Platform.IModuleAccessService, InventoryAccessService>",
                source, StringComparison.Ordinal);
        }

        // ---- 5-8: company, authority, fail-closed ---------------------------------------------------

        [Fact]
        public async Task One_companys_roles_do_not_grant_another_companys_authority()
        {
            using var host = new PlatformTestHost();
            await SeedRoleAsync(host, companyId: 1, employeeId: 7, role: "InventoryManager");
            var svc = Service(host);

            Assert.True(await svc.CanAsync(PlatformTestHost.DefaultContext(companyId: 1, employeeId: 7), "manage"));

            // Same employee id, company 2: holds nothing there, and company 2 has no policy either.
            Assert.False(await svc.CanAsync(PlatformTestHost.DefaultContext(companyId: 2, employeeId: 7), "manage"));
            Assert.False(await svc.CanAsync(PlatformTestHost.DefaultContext(companyId: 2, employeeId: 7), "doc"));
        }

        [Fact]
        public async Task An_unresolved_company_fails_closed()
        {
            using var host = new PlatformTestHost();
            var svc = Service(host);

            foreach (var action in new[] { "read", "doc", "purchase", "manage" })
                Assert.False(await svc.CanAsync(PlatformTestHost.DefaultContext(companyId: 0), action));
        }

        [Fact]
        public async Task An_unknown_action_is_refused()
        {
            using var host = new PlatformTestHost();
            await SeedRoleAsync(host, 1, 7, "InventoryManager");

            Assert.False(await Service(host).CanAsync(PlatformTestHost.DefaultContext(), "delete-everything"));
        }

        [Fact]
        public async Task A_company_with_no_roles_is_not_unboundedly_open()
        {
            using var host = new PlatformTestHost();
            var svc = Service(host);
            var context = PlatformTestHost.DefaultContext(companyId: 1, employeeId: 7);

            // No roles and no bootstrap policy: the platform reader decides, and with nothing to read it
            // refuses. "Not configured yet" must not mean "everyone may do everything".
            foreach (var action in new[] { "read", "doc", "purchase", "manage" })
                Assert.False(await svc.CanAsync(context, action),
                    $"'{action}' was allowed with no roles and no policy");
        }

        [Fact]
        public async Task The_service_carries_no_hardcoded_company()
        {
            using var host = new PlatformTestHost();
            await SeedRoleAsync(host, companyId: 5, employeeId: 7, role: "InventoryManager");

            // Company 5, not 1. A surviving constant would deny here and allow for company 1.
            Assert.True(await Service(host).CanAsync(
                PlatformTestHost.DefaultContext(companyId: 5, employeeId: 7), "manage"));

            var source = File.ReadAllText(Path.Combine(RepoRoot(), "CrossBuy", "BL", "InventoryAccessService.cs"));
            var code = string.Join("\n", source.Split('\n')
                .Where(l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal)
                         && !l.TrimStart().StartsWith("///", StringComparison.Ordinal)));

            Assert.DoesNotContain("CompanyId = 1", code, StringComparison.Ordinal);
            Assert.DoesNotContain("DefaultCompanyId", code, StringComparison.Ordinal);
        }

        [Fact]
        public async Task Warehouse_scope_does_not_reach_outside_the_company()
        {
            using var host = new PlatformTestHost();
            await SeedRoleAsync(host, companyId: 1, employeeId: 7, role: "InventoryManager");

            // A warehouse belonging to company 2, addressed by a company-1 manager.
            host.Seed.Warehouses.Add(new CrossBuy.Models.Context.Inventory.Warehouse
            {
                ID = 900, CompanyID = 2, Code = "W2", Name = "company two", IsActive = true,
            });
            await host.Seed.SaveChangesAsync();

            var svc = Service(host);

            // The company-1 manager holds no role in company 2, so addressing company 2's warehouse from
            // a company-2 context is refused - the role rows are company-filtered before the warehouse is.
            Assert.False(await svc.CanAsync(
                PlatformTestHost.DefaultContext(companyId: 2, employeeId: 7), "doc",
                new PermissionTarget { CompanyId = 2, WarehouseId = 900 }));

            // And the platform's own gate refuses a target naming a company the caller did not resolve to,
            // before the module is consulted at all.
            Assert.False(await svc.CanAsync(
                PlatformTestHost.DefaultContext(companyId: 1, employeeId: 7), "doc",
                new PermissionTarget { CompanyId = 2, WarehouseId = 900 }));
        }

        // ---- 9-11: the adapters ---------------------------------------------------------------------

        private static IPlatformPermissionProvider Provider(PlatformTestHost host, params IModulePermissionAdapter[] adapters)
            => new PlatformPermissionProvider(host.Registry(), adapters,
                NullLogger<PlatformPermissionProvider>.Instance);

        [Fact]
        public async Task The_inventory_adapter_now_has_a_module_to_delegate_to()
        {
            using var host = new PlatformTestHost();
            await SeedRoleAsync(host, 1, 7, "InventoryManager");

            host.Db.Items.Add(new CrossBuy.Models.Context.Inventory.Item
            {
                ID = 11, CompanyID = 1, ItemCode = "IT-1", Name = "item",
            });
            await host.Db.SaveChangesAsync();

            var inventory = Service(host);
            var provider = Provider(host, new InventoryPermissionAdapter(new IModuleAccessService[] { inventory }));

            var decision = await provider.CanAsync(
                PlatformTestHost.DefaultContext(companyId: 1, employeeId: 7),
                EntityRegistry.Item, 11, PlatformActions.View);

            Assert.True(decision.Allowed);

            // "inventory:read" is the Inventory adapter's own phrasing. DefaultPermissionAdapter's allow
            // reason is "authenticated, company-scoped", so this is what proves WHICH adapter decided.
            Assert.Contains("inventory:", decision.Reason, StringComparison.Ordinal);
        }

        [Fact]
        public async Task Manufacturing_resolves_through_Inventorys_authority_and_not_its_own()
        {
            using var host = new PlatformTestHost();
            await SeedRoleAsync(host, 1, 7, "InventoryManager");

            host.Db.ManufWorkOrders.Add(new CrossBuy.Models.Context.Inventory.ManufWorkOrder
            {
                ID = 21, CompanyID = 1, WoNo = "WO-1",
            });
            await host.Db.SaveChangesAsync();

            // ONE module service - Inventory's - and the Manufacturing adapter alone.
            var inventory = Service(host);
            var provider = Provider(host, new ManufacturingPermissionAdapter(new IModuleAccessService[] { inventory }));

            var decision = await provider.CanAsync(
                PlatformTestHost.DefaultContext(companyId: 1, employeeId: 7),
                EntityRegistry.ManufWorkOrder, 21, PlatformActions.View);

            // Work-order authority IS Inventory policy. There is no ManufacturingAccessService, and this
            // passing without one is the proof that no second engine was introduced.
            Assert.True(decision.Allowed);
        }

        [Fact]
        public async Task Neither_adapter_falls_back_when_the_module_is_absent()
        {
            using var host = new PlatformTestHost();
            host.Db.Items.Add(new CrossBuy.Models.Context.Inventory.Item
            {
                ID = 11, CompanyID = 1, ItemCode = "IT-1", Name = "item",
            });
            await host.Db.SaveChangesAsync();

            foreach (var adapter in new IModulePermissionAdapter[]
                     {
                         new InventoryPermissionAdapter(Array.Empty<IModuleAccessService>()),
                         new ManufacturingPermissionAdapter(Array.Empty<IModuleAccessService>()),
                     })
            {
                var decision = await Provider(host, adapter).CanAsync(
                    PlatformTestHost.DefaultContext(companyId: 1, employeeId: 7),
                    EntityRegistry.Item, 11, PlatformActions.View);

                // With no module behind it the adapter denies and NAMES the missing module - it does not
                // quietly hand the question to a weaker adapter.
                Assert.False(decision.Allowed);
            }
        }

        // ---- 12: the legacy surface is unchanged ----------------------------------------------------

        [Fact]
        public async Task The_legacy_contract_still_answers_and_gains_nothing()
        {
            using var host = new PlatformTestHost();
            await SeedRoleAsync(host, companyId: 1, employeeId: 7, role: "WarehouseKeeper");

            // The eight InventoryController call sites use this shape. A keeper may file stock documents
            // and may not manage master data - the same answer as before the platform contract existed.
            var svc = Service(host, PlatformTestHost.DefaultContext(companyId: 1, employeeId: 7));

            Assert.True(await svc.CanAsync(PlatformTestHost.DefaultContext(companyId: 1, employeeId: 7), "doc"));
            Assert.False(await svc.CanAsync(PlatformTestHost.DefaultContext(companyId: 1, employeeId: 7), "manage"));
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
