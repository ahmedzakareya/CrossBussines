using CrossBuy.BL.ModulePermissions;
using CrossBuy.BL.Platform;
using CrossBuy.Models.Context.Platform;
using CrossBuy.Models.Context.Tasks;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CrossBuy.Tests
{
    // ============================================================================================
    // MODULE PERMISSION ADAPTER REGISTRATION.
    //
    // WHAT WAS BROKEN. PlatformPermissionProvider picks an adapter by scope and denies when it finds
    // none. Program.cs registered exactly ONE IModulePermissionAdapter - DefaultPermissionAdapter,
    // whose scope is "None" - so every entity whose PermissionScope was anything else got:
    //
    //     Deny("No permission adapter is registered for scope 'Tasks'.")
    //
    // That is 11 of the 14 registered entities, including all six that support a timeline. The kernel
    // was refusing to ASK the module rather than the module refusing to grant, and because it failed
    // closed it looked like correct authorization from every angle except the empty screen: the
    // governed recent feed returned nothing for every caller no matter what they were entitled to see.
    //
    // The ten adapters existed and were correct the whole time. They were simply never registered.
    //
    // WHAT THESE TESTS HOLD. That the registration exists and is unambiguous, and - more importantly -
    // that registering it did not turn the platform into a permissive one. Fail-closed has to survive
    // in both directions: an unknown scope still denies, and an adapter whose module is missing still
    // denies rather than falling back to the weakest adapter that happens to be registered.
    // ============================================================================================
    public class ModulePermissionAdapterCompositionTests
    {
        // Scope -> adapter, as Program.cs must register them. Kept as data because the two assertions
        // that matter (all present, each exactly once) are both about this set.
        public static readonly (string Scope, Type Adapter)[] Expected =
        {
            (EntityRegistry.ScopeAccounting,    typeof(AccountingPermissionAdapter)),
            (EntityRegistry.ScopeInventory,     typeof(InventoryPermissionAdapter)),
            (EntityRegistry.ScopeManufacturing, typeof(ManufacturingPermissionAdapter)),
            (EntityRegistry.ScopeCrm,           typeof(CrmPermissionAdapter)),
            (EntityRegistry.ScopePos,           typeof(PosPermissionAdapter)),
            (EntityRegistry.ScopeHr,            typeof(HrPermissionAdapter)),
            (EntityRegistry.ScopeProjects,      typeof(ProjectsPermissionAdapter)),
            (EntityRegistry.ScopeCalendar,      typeof(CalendarPermissionAdapter)),
            (EntityRegistry.ScopeTasks,         typeof(TasksPermissionAdapter)),
            (EntityRegistry.ScopeCommunication, typeof(CommunicationPermissionAdapter)),
        };

        private static string ProgramSource() =>
            File.ReadAllText(Path.Combine(RepoRoot(), "CrossBuy", "Program.cs"));

        private static string RepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "CrossBuy.sln"))) dir = dir.Parent;
            Assert.NotNull(dir);
            return dir!.FullName;
        }

        // ---- registration -----------------------------------------------------------------------

        [Fact]
        public void Every_committed_module_adapter_is_registered()
        {
            var source = ProgramSource();

            var missing = Expected
                .Select(e => e.Adapter.Name)
                .Where(name => !source.Contains("IModulePermissionAdapter, CrossBuy.BL.ModulePermissions." + name + ">",
                                                StringComparison.Ordinal))
                .ToList();

            Assert.True(missing.Count == 0,
                "these adapters exist in BL/ModulePermissions but are not registered, so their scopes " +
                "will be denied for a configuration reason rather than a policy one: " + string.Join(", ", missing));
        }

        [Fact]
        public void Each_adapter_is_registered_exactly_once()
        {
            var source = ProgramSource();

            foreach (var (_, adapter) in Expected)
            {
                var needle = "IModulePermissionAdapter, CrossBuy.BL.ModulePermissions." + adapter.Name + ">";
                var count = source.Split(needle).Length - 1;

                // Two registrations of one adapter would put two entries in the collection.
                // FirstOrDefault would still pick one, so this never breaks visibly - it just makes
                // "which adapter answered" depend on registration order.
                Assert.True(count == 1, $"{adapter.Name} is registered {count} times, expected exactly 1");
            }
        }

        [Fact]
        public void The_default_adapter_survives_because_scope_None_still_needs_one()
        {
            // Employee, Project and PlatformRoleAssignment carry ScopeNone. Removing this while adding
            // the ten would swap one set of silently-denied entities for another.
            Assert.Contains("IModulePermissionAdapter, CrossBuy.BL.Platform.DefaultPermissionAdapter>",
                ProgramSource(), StringComparison.Ordinal);
        }

        [Fact]
        public void No_two_adapters_claim_the_same_scope()
        {
            // The provider resolves with FirstOrDefault, so a duplicated scope is decided by registration
            // order rather than by intent - the kind of thing that is invisible until two tabs disagree.
            var adapters = Adapters(new StubModule(EntityRegistry.ScopeTasks, allow: true));
            var duplicates = adapters.GroupBy(a => a.Scope).Where(g => g.Count() > 1).Select(g => g.Key).ToList();

            Assert.Empty(duplicates);
            Assert.Equal(Expected.Length, adapters.Count);
        }

        [Fact]
        public void Every_registry_scope_that_has_an_adapter_is_covered()
        {
            using var host = new PlatformTestHost();
            var scopes = host.Registry().GetDefinitions().Select(d => d.PermissionScope).Distinct().ToList();

            var covered = Expected.Select(e => e.Scope).Append(EntityRegistry.ScopeNone).ToHashSet(StringComparer.Ordinal);
            var uncovered = scopes.Where(s => !covered.Contains(s)).ToList();

            Assert.True(uncovered.Count == 0,
                "registry entities use these scopes but no adapter claims them: " + string.Join(", ", uncovered));
        }

        // ---- fail closed ------------------------------------------------------------------------

        [Fact]
        public async Task An_entity_whose_scope_has_no_adapter_is_still_denied()
        {
            using var host = new PlatformTestHost();

            // The provider with a DELIBERATELY empty adapter set: this is the pre-fix world, and it must
            // still deny. Registering ten adapters must not have introduced a permissive fallback.
            var provider = new PlatformPermissionProvider(
                host.Registry(), Array.Empty<IModulePermissionAdapter>(),
                NullLogger<PlatformPermissionProvider>.Instance);

            await SeedTaskAsync(host, taskId: 1, companyId: 1);

            var decision = await provider.CanAsync(
                PlatformTestHost.DefaultContext(), EntityRegistry.Task, 1, PlatformActions.View);

            Assert.False(decision.Allowed);
            Assert.Contains("No permission adapter is registered", decision.Reason, StringComparison.Ordinal);
        }

        [Fact]
        public async Task An_adapter_whose_module_is_not_registered_denies_and_names_the_module()
        {
            using var host = new PlatformTestHost();
            await SeedTaskAsync(host, taskId: 1, companyId: 1);

            // The Tasks adapter present, but no IModuleAccessService behind it. Four scopes are in exactly
            // this state today (Inventory, Manufacturing, Crm, Communication): their access services exist
            // but are not registered as IModuleAccessService. The answer must still be deny.
            var provider = new PlatformPermissionProvider(
                host.Registry(),
                new IModulePermissionAdapter[] { new TasksPermissionAdapter(Array.Empty<IModuleAccessService>()) },
                NullLogger<PlatformPermissionProvider>.Instance);

            var decision = await provider.CanAsync(
                PlatformTestHost.DefaultContext(), EntityRegistry.Task, 1, PlatformActions.View);

            Assert.False(decision.Allowed);
            Assert.Contains("No IModuleAccessService is registered", decision.Reason, StringComparison.Ordinal);
        }

        // ---- the Tasks decision now reaches the module ------------------------------------------

        [Fact]
        public async Task The_Tasks_adapter_is_found_and_the_module_decides()
        {
            using var host = new PlatformTestHost();
            await SeedTaskAsync(host, taskId: 1, companyId: 1);
            var context = PlatformTestHost.DefaultContext();

            // Allowed and denied come from the MODULE, not from the platform. The adapter's whole job is
            // to stop deciding and start asking - so the two cases differ only in what the module says.
            var allowed = await Provider(host, new StubModule(EntityRegistry.ScopeTasks, allow: true))
                .CanAsync(context, EntityRegistry.Task, 1, PlatformActions.View);
            var denied = await Provider(host, new StubModule(EntityRegistry.ScopeTasks, allow: false))
                .CanAsync(context, EntityRegistry.Task, 1, PlatformActions.View);

            Assert.True(allowed.Allowed);
            Assert.Contains("tasks:read", allowed.Reason, StringComparison.Ordinal);

            Assert.False(denied.Allowed);
            Assert.Contains("tasks:read", denied.Reason, StringComparison.Ordinal);
        }

        [Fact]
        public async Task Company_isolation_still_runs_before_the_module_is_asked()
        {
            using var host = new PlatformTestHost();
            // The task exists, but in company 2. A module that says yes to everything must not be able to
            // reach it: the provider checks company BEFORE delegating, and that ordering is the contract.
            await SeedTaskAsync(host.Seed, taskId: 1, companyId: 2);

            var module = new StubModule(EntityRegistry.ScopeTasks, allow: true);
            var decision = await Provider(host, module)
                .CanAsync(PlatformTestHost.DefaultContext(companyId: 1), EntityRegistry.Task, 1, PlatformActions.View);

            Assert.False(decision.Allowed);
            Assert.False(module.WasAsked);   // never delegated - the tenancy gate answered first
        }

        [Fact]
        public async Task The_platform_actions_map_onto_the_modules_own_vocabulary()
        {
            using var host = new PlatformTestHost();
            await SeedTaskAsync(host, taskId: 1, companyId: 1);

            var module = new StubModule(EntityRegistry.ScopeTasks, allow: true);
            var provider = Provider(host, module);
            var context = PlatformTestHost.DefaultContext();

            // View/ViewConfidential/ViewRestricted are the kernel's three words. The module never learns
            // them; it hears read/edit/manage, which is what the adapter exists to translate.
            await provider.CanAsync(context, EntityRegistry.Task, 1, PlatformActions.View);
            await provider.CanAsync(context, EntityRegistry.Task, 1, PlatformActions.ViewConfidential);
            await provider.CanAsync(context, EntityRegistry.Task, 1, PlatformActions.ViewRestricted);

            Assert.Equal(new[] { "read", "edit", "manage" }, module.Asked);
        }

        // ---- the read path this was blocking ----------------------------------------------------

        [Fact]
        public async Task The_recent_feed_returns_task_activity_once_the_adapter_is_registered()
        {
            using var host = new PlatformTestHost();
            await SeedTaskAsync(host, taskId: 1, companyId: 1);
            await SeedTaskAsync(host, taskId: 2, companyId: 1);
            await AddTaskEventAsync(host.Db, companyId: 1, taskId: 1, minutesAgo: 2);
            await AddTaskEventAsync(host.Db, companyId: 1, taskId: 2, minutesAgo: 1);

            var context = PlatformTestHost.DefaultContext();

            // Before: no adapter -> the feed is empty for a caller who is genuinely entitled.
            var before = await Feed(host, Array.Empty<IModulePermissionAdapter>())
                .GetRecentForContextAsync(context, DateTime.UtcNow.AddDays(-1), 50);
            Assert.Empty(before);

            // After: the adapter is found, the module allows, and the rows arrive.
            var after = await Feed(host, Adapters(new StubModule(EntityRegistry.ScopeTasks, allow: true)))
                .GetRecentForContextAsync(context, DateTime.UtcNow.AddDays(-1), 50);

            Assert.Equal(2, after.Count);
            Assert.All(after, i => Assert.Equal(EntityRegistry.Task, i.EntityType));
            Assert.All(after, i => Assert.False(string.IsNullOrWhiteSpace(i.Url)));
        }

        [Fact]
        public async Task A_task_the_module_denies_is_excluded_from_the_feed_row_by_row()
        {
            using var host = new PlatformTestHost();
            await SeedTaskAsync(host, taskId: 1, companyId: 1);
            await SeedTaskAsync(host, taskId: 2, companyId: 1);
            await AddTaskEventAsync(host.Db, companyId: 1, taskId: 1, minutesAgo: 2);
            await AddTaskEventAsync(host.Db, companyId: 1, taskId: 2, minutesAgo: 1);

            // The module allows task 2 and refuses task 1. Registering adapters must not turn per-record
            // policy into a per-module one.
            var module = new StubModule(EntityRegistry.ScopeTasks, allow: true) { DeniedTaskId = 1 };

            var items = await Feed(host, Adapters(module))
                .GetRecentForContextAsync(PlatformTestHost.DefaultContext(), DateTime.UtcNow.AddDays(-1), 50);

            Assert.Single(items);
            Assert.Equal(2, items[0].EntityId);
        }

        [Fact]
        public async Task Another_companys_task_activity_stays_out_of_the_feed()
        {
            using var host = new PlatformTestHost();
            await SeedTaskAsync(host, taskId: 1, companyId: 1);
            await SeedTaskAsync(host.Seed, taskId: 2, companyId: 2);
            await AddTaskEventAsync(host.Db, companyId: 1, taskId: 1, minutesAgo: 2);
            await AddTaskEventAsync(host.Seed, companyId: 2, taskId: 2, minutesAgo: 1);

            var items = await Feed(host, Adapters(new StubModule(EntityRegistry.ScopeTasks, allow: true)))
                .GetRecentForContextAsync(PlatformTestHost.DefaultContext(companyId: 1), DateTime.UtcNow.AddDays(-1), 50);

            Assert.Single(items);
            Assert.Equal(1, items[0].EntityId);
        }

        [Fact]
        public async Task Restricted_and_system_rows_are_unchanged_by_the_registration()
        {
            using var host = new PlatformTestHost();
            await SeedTaskAsync(host, taskId: 1, companyId: 1);

            await AddTaskEventAsync(host.Db, 1, 1, minutesAgo: 4);
            await AddTaskEventAsync(host.Db, 1, 1, minutesAgo: 3, visibility: BusinessEventVisibility.Confidential);
            await AddTaskEventAsync(host.Db, 1, 1, minutesAgo: 2, visibility: BusinessEventVisibility.Restricted, actor: 99);
            await AddTaskEventAsync(host.Db, 1, 1, minutesAgo: 1, visibility: BusinessEventVisibility.System);

            // The module grants `read` only, which the adapter maps back to View alone - so the elevated
            // tiers stay hidden exactly as before. The adapter cannot widen visibility.
            var items = await Feed(host, Adapters(new StubModule(EntityRegistry.ScopeTasks, allow: true, readOnly: true)))
                .GetRecentForContextAsync(PlatformTestHost.DefaultContext(employeeId: 7), DateTime.UtcNow.AddDays(-1), 50);

            Assert.Single(items);
            Assert.Equal(BusinessEventVisibility.Internal, items[0].Visibility);

            // And still no raw payload anywhere on the contract.
            Assert.Empty(typeof(TimelineItemViewModel).GetProperties()
                .Where(p => p.Name.Contains("Payload") && p.PropertyType == typeof(string)));
        }

        // ---- fixtures ---------------------------------------------------------------------------

        private static List<IModulePermissionAdapter> Adapters(IModuleAccessService module)
        {
            var modules = new[] { module };
            return new List<IModulePermissionAdapter>
            {
                new AccountingPermissionAdapter(modules), new InventoryPermissionAdapter(modules),
                new ManufacturingPermissionAdapter(modules), new CrmPermissionAdapter(modules),
                new PosPermissionAdapter(modules), new HrPermissionAdapter(modules),
                new ProjectsPermissionAdapter(modules), new CalendarPermissionAdapter(modules),
                new TasksPermissionAdapter(modules), new CommunicationPermissionAdapter(modules),
            };
        }

        private static IPlatformPermissionProvider Provider(PlatformTestHost host, IModuleAccessService module)
            => new PlatformPermissionProvider(host.Registry(), Adapters(module),
                NullLogger<PlatformPermissionProvider>.Instance);

        private static ITimelineProjectionService Feed(PlatformTestHost host, IEnumerable<IModulePermissionAdapter> adapters)
        {
            var registry = host.Registry();
            var permissions = new PlatformPermissionProvider(registry, adapters,
                NullLogger<PlatformPermissionProvider>.Instance);
            return new TimelineProjectionService(host.Db, registry, permissions, Array.Empty<ILegacyTimelineAdapter>());
        }

        private static Task SeedTaskAsync(PlatformTestHost host, int taskId, int companyId)
            => SeedTaskAsync(host.Db, taskId, companyId);

        private static async Task SeedTaskAsync(
            CrossBuy.Models.Context.CrossDbContext db, int taskId, int companyId)
        {
            db.TaskItems.Add(new TaskItem
            {
                ID = taskId, CompanyId = companyId, Title = "مهمة " + taskId,
                Status = "New", CreatedAt = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        private static async Task AddTaskEventAsync(
            CrossBuy.Models.Context.CrossDbContext db, int companyId, int taskId, int minutesAgo,
            string? visibility = null, int? actor = null)
        {
            db.BusinessEvents.Add(new BusinessEvent
            {
                EventUid = Guid.NewGuid(), CompanyID = companyId, BranchID = null,
                EntityType = EntityRegistry.Task, EntityId = taskId,
                EventType = TaskEvents.BecameOverdue,
                ActorEmployeeId = actor ?? 7,
                Visibility = visibility ?? BusinessEventVisibility.Internal,
                PayloadVersion = 1, CreatedAt = DateTime.UtcNow.AddMinutes(-minutesAgo),
                Payload = "{\"title\":\"مهمة\"}",
            });
            await db.SaveChangesAsync();
        }

        // Stands in for the module's access service. It records what it was ASKED, because the point of
        // an adapter is delegation - a test that only checked the answer could not tell a real
        // delegation from a platform-side shortcut that happened to agree.
        private sealed class StubModule : IModuleAccessService
        {
            private readonly bool _allow;
            private readonly bool _readOnly;

            public StubModule(string scope, bool allow, bool readOnly = false)
            { Scope = scope; _allow = allow; _readOnly = readOnly; }

            public string Scope { get; }
            public IReadOnlyCollection<string> Actions { get; } = new[] { "read", "edit", "manage" };

            public List<string> Asked { get; } = new();
            public bool WasAsked => Asked.Count > 0;
            public int? DeniedTaskId { get; init; }

            public Task<bool> CanAsync(
                BusinessContext context, string action, PermissionTarget? target, CancellationToken cancellationToken = default)
            {
                Asked.Add(action);
                if (_readOnly && action != "read") return Task.FromResult(false);
                if (DeniedTaskId != null && target?.TaskId == DeniedTaskId) return Task.FromResult(false);
                return Task.FromResult(_allow);
            }

            // Not part of the adapter path - the adapters call CanAsync only - but the contract requires it.
            public Task<IReadOnlyList<string>> RolesAsync(
                BusinessContext context, CancellationToken cancellationToken = default)
                => Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
        }
    }
}
