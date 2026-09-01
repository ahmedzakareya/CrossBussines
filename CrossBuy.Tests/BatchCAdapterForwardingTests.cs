using CrossBuy.BL.Platform;
using CrossBuy.BL.ModulePermissions;
using CrossBuy.Models.Platform;
using Xunit;

namespace CrossBuy.Tests
{
    // Stage 1 Batch C — the additive PermissionTarget forwarding in ModulePermissionAdapterBase.
    //
    // THE DEFECT: the base passed `target: null` unconditionally, so a module's record-level rule could never
    // be reached through IPlatformPermissionProvider. A module could implement per-record access and the
    // platform would still ask it a module-level question.
    //
    // THE RISK OF FIXING IT: the base is shared by five WORKING adapters (Accounting, Inventory, CRM, POS,
    // Manufacturing). If the change altered what they pass, five modules' authorization would shift at once.
    // So the fix is a virtual `BuildTarget` returning null by default, and this file is the proof — one
    // regression test per existing adapter, plus the positive case for each new one.
    public class BatchCAdapterForwardingTests
    {
        // Records exactly what the adapter handed to the module: the action, and the target (or null).
        private sealed class RecordingModule : IModuleAccessService
        {
            public RecordingModule(string scope) { Scope = scope; }
            public string Scope { get; }
            public IReadOnlyCollection<string> Actions { get; } = new[]
            {
                // Every action name the five existing adapters and the four new ones can map to, so the module
                // never refuses for the wrong reason.
                "read", "post", "pay", "manage", "currency-override", "doc", "purchase", "view", "sell",
                "edit", "employee-view", "confidential-view", "budget-view", "send", "manage-group",
            };

            public string? LastAction { get; private set; }
            public PermissionTarget? LastTarget { get; private set; }
            public bool TargetWasSupplied { get; private set; }

            public Task<bool> CanAsync(BusinessContext context, string action, PermissionTarget? target = null,
                CancellationToken cancellationToken = default)
            {
                LastAction = action;
                LastTarget = target;
                TargetWasSupplied = target != null;
                return Task.FromResult(true);
            }

            public Task<IReadOnlyList<string>> RolesAsync(BusinessContext context, CancellationToken ct = default)
                => Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
        }

        private static PermissionCheckRequest Request(string entityType, int entityId, string action = PlatformActions.View)
            => new()
            {
                Context = PlatformTestHost.DefaultContext(companyId: 1),
                EntityType = entityType,
                EntityId = entityId,
                Action = action,
            };

        // ============================================================================================
        // 1. THE FIVE EXISTING ADAPTERS — behaviour must be IDENTICAL (target stays null)
        // ============================================================================================

        public static IEnumerable<object[]> ExistingAdapters()
        {
            yield return new object[] { EntityRegistry.ScopeAccounting, "accounting" };
            yield return new object[] { EntityRegistry.ScopeInventory, "inventory" };
            yield return new object[] { EntityRegistry.ScopeCrm, "crm" };
            yield return new object[] { EntityRegistry.ScopePos, "pos" };
            yield return new object[] { EntityRegistry.ScopeManufacturing, "manufacturing" };
        }

        private static IModulePermissionAdapter AdapterFor(string scope, IEnumerable<IModuleAccessService> modules)
            => scope switch
            {
                var s when s == EntityRegistry.ScopeAccounting => new AccountingPermissionAdapter(modules),
                var s when s == EntityRegistry.ScopeInventory => new InventoryPermissionAdapter(modules),
                var s when s == EntityRegistry.ScopeCrm => new CrmPermissionAdapter(modules),
                var s when s == EntityRegistry.ScopePos => new PosPermissionAdapter(modules),
                var s when s == EntityRegistry.ScopeManufacturing => new ManufacturingPermissionAdapter(modules),
                _ => throw new InvalidOperationException(scope),
            };

        // The regression the instruction asks for, per adapter: an existing adapter must still pass NULL.
        [Theory]
        [MemberData(nameof(ExistingAdapters))]
        public async Task An_existing_adapter_still_passes_a_null_target(string scope, string reasonPrefix)
        {
            // Manufacturing delegates to Inventory, so its module must be registered under the inventory scope.
            string moduleScope = scope == EntityRegistry.ScopeManufacturing ? EntityRegistry.ScopeInventory : scope;
            var module = new RecordingModule(moduleScope);
            var adapter = AdapterFor(scope, new[] { (IModuleAccessService)module });

            var decision = await adapter.CanAsync(Request(EntityRegistry.SalesInvoice, 5));

            Assert.True(decision.Allowed);
            Assert.False(module.TargetWasSupplied);   // ← the regression guard
            Assert.Null(module.LastTarget);
            Assert.Contains(reasonPrefix, decision.Reason);
        }

        // ...and the action mapping is untouched too, so "identical behaviour" covers what they ask as well as
        // what they pass.
        [Theory]
        [InlineData(EntityRegistry.ScopeAccounting, PlatformActions.View, "read")]
        [InlineData(EntityRegistry.ScopeAccounting, PlatformActions.ViewConfidential, "post")]
        [InlineData(EntityRegistry.ScopeAccounting, PlatformActions.ViewRestricted, "manage")]
        [InlineData(EntityRegistry.ScopeInventory, PlatformActions.View, "read")]
        [InlineData(EntityRegistry.ScopeInventory, PlatformActions.ViewConfidential, "doc")]
        [InlineData(EntityRegistry.ScopeInventory, PlatformActions.ViewRestricted, "manage")]
        [InlineData(EntityRegistry.ScopePos, PlatformActions.View, "view")]
        [InlineData(EntityRegistry.ScopePos, PlatformActions.ViewConfidential, "sell")]
        [InlineData(EntityRegistry.ScopePos, PlatformActions.ViewRestricted, "manage")]
        public async Task An_existing_adapters_action_mapping_is_unchanged(string scope, string platformAction, string expected)
        {
            var module = new RecordingModule(scope);
            var adapter = AdapterFor(scope, new[] { (IModuleAccessService)module });

            await adapter.CanAsync(Request(EntityRegistry.SalesInvoice, 5, platformAction));

            Assert.Equal(expected, module.LastAction);
        }

        // An unknown platform action still denies before the module is consulted at all.
        [Theory]
        [MemberData(nameof(ExistingAdapters))]
        public async Task An_unknown_platform_action_still_denies_without_asking_the_module(string scope, string _)
        {
            string moduleScope = scope == EntityRegistry.ScopeManufacturing ? EntityRegistry.ScopeInventory : scope;
            var module = new RecordingModule(moduleScope);
            var adapter = AdapterFor(scope, new[] { (IModuleAccessService)module });

            var decision = await adapter.CanAsync(Request(EntityRegistry.SalesInvoice, 5, "NotAnAction"));

            Assert.False(decision.Allowed);
            Assert.Null(module.LastAction);   // never asked
        }

        // ============================================================================================
        // 2. THE FOUR NEW ADAPTERS — a non-null target must ARRIVE UNCHANGED
        // ============================================================================================

        [Fact]
        public async Task The_hr_adapter_forwards_the_employee_as_the_subject()
        {
            var module = new RecordingModule(EntityRegistry.ScopeHr);
            var adapter = new HrPermissionAdapter(new[] { (IModuleAccessService)module });

            var decision = await adapter.CanAsync(Request(EntityRegistry.Employee, 42));

            Assert.True(decision.Allowed);
            Assert.True(module.TargetWasSupplied);
            // The employee the check is ABOUT becomes the subject — which is what self-access and the
            // direct-report rule compare against.
            Assert.Equal(42, module.LastTarget!.SubjectEmployeeId);
            Assert.Equal(42, module.LastTarget!.EntityId);
            Assert.Equal(EntityRegistry.Employee, module.LastTarget!.EntityType);
            Assert.Equal(1, module.LastTarget!.CompanyId);
        }

        // For a non-Employee entity the HR adapter still carries the company, so the module's company gate runs.
        [Fact]
        public async Task The_hr_adapter_carries_the_company_even_for_a_non_employee_entity()
        {
            var module = new RecordingModule(EntityRegistry.ScopeHr);
            var adapter = new HrPermissionAdapter(new[] { (IModuleAccessService)module });

            await adapter.CanAsync(Request(EntityRegistry.SalesInvoice, 7));

            Assert.True(module.TargetWasSupplied);
            Assert.Null(module.LastTarget!.SubjectEmployeeId);
            Assert.Equal(1, module.LastTarget!.CompanyId);
        }

        [Fact]
        public async Task The_projects_adapter_forwards_the_project_id()
        {
            var module = new RecordingModule(EntityRegistry.ScopeProjects);
            var adapter = new ProjectsPermissionAdapter(new[] { (IModuleAccessService)module });

            await adapter.CanAsync(Request(EntityRegistry.Project, 99));

            Assert.Equal(99, module.LastTarget!.ProjectId);
            Assert.Equal(1, module.LastTarget!.CompanyId);
        }

        [Fact]
        public async Task The_tasks_adapter_forwards_the_task_id()
        {
            var module = new RecordingModule(EntityRegistry.ScopeTasks);
            var adapter = new TasksPermissionAdapter(new[] { (IModuleAccessService)module });

            await adapter.CanAsync(Request("TaskItem", 13));

            Assert.Equal(13, module.LastTarget!.TaskId);
            Assert.Equal(1, module.LastTarget!.CompanyId);
        }

        [Fact]
        public async Task The_communication_adapter_forwards_the_conversation_id()
        {
            var module = new RecordingModule(EntityRegistry.ScopeCommunication);
            var adapter = new CommunicationPermissionAdapter(new[] { (IModuleAccessService)module });

            await adapter.CanAsync(Request("Conversation", 8));

            Assert.Equal(8, module.LastTarget!.ConversationId);
            Assert.Equal(1, module.LastTarget!.CompanyId);
        }

        // The new adapters' visibility mappings, pinned — so a later "small" change to a mapping is a failing
        // test rather than a silent broadening of what Confidential or Restricted means.
        [Theory]
        [InlineData(EntityRegistry.ScopeHr, PlatformActions.View, "read")]
        [InlineData(EntityRegistry.ScopeHr, PlatformActions.ViewConfidential, "employee-view")]
        [InlineData(EntityRegistry.ScopeHr, PlatformActions.ViewRestricted, "confidential-view")]
        [InlineData(EntityRegistry.ScopeProjects, PlatformActions.View, "read")]
        [InlineData(EntityRegistry.ScopeProjects, PlatformActions.ViewConfidential, "budget-view")]
        [InlineData(EntityRegistry.ScopeProjects, PlatformActions.ViewRestricted, "manage")]
        [InlineData(EntityRegistry.ScopeTasks, PlatformActions.View, "read")]
        [InlineData(EntityRegistry.ScopeTasks, PlatformActions.ViewConfidential, "edit")]
        [InlineData(EntityRegistry.ScopeTasks, PlatformActions.ViewRestricted, "manage")]
        [InlineData(EntityRegistry.ScopeCommunication, PlatformActions.View, "read")]
        [InlineData(EntityRegistry.ScopeCommunication, PlatformActions.ViewConfidential, "send")]
        [InlineData(EntityRegistry.ScopeCommunication, PlatformActions.ViewRestricted, "manage-group")]
        public async Task The_new_adapters_map_visibility_to_the_documented_action(string scope, string platformAction, string expected)
        {
            var module = new RecordingModule(scope);
            IModulePermissionAdapter adapter = scope switch
            {
                var s when s == EntityRegistry.ScopeHr => new HrPermissionAdapter(new[] { (IModuleAccessService)module }),
                var s when s == EntityRegistry.ScopeProjects => new ProjectsPermissionAdapter(new[] { (IModuleAccessService)module }),
                var s when s == EntityRegistry.ScopeTasks => new TasksPermissionAdapter(new[] { (IModuleAccessService)module }),
                _ => new CommunicationPermissionAdapter(new[] { (IModuleAccessService)module }),
            };

            await adapter.CanAsync(Request(EntityRegistry.Employee, 1, platformAction));

            Assert.Equal(expected, module.LastAction);
        }

        // A missing module DENIES rather than allowing — the same behaviour the base always had, re-asserted
        // because the forwarding change touched that method.
        [Fact]
        public async Task An_adapter_with_no_registered_module_denies()
        {
            var adapter = new HrPermissionAdapter(Array.Empty<IModuleAccessService>());

            var decision = await adapter.CanAsync(Request(EntityRegistry.Employee, 1));

            Assert.False(decision.Allowed);
            Assert.Contains("No IModuleAccessService is registered", decision.Reason);
        }

        // ============================================================================================
        // 3. A MALFORMED TARGET DENIES — it must not degrade to a module-level check
        // ============================================================================================

        // An adapter whose BuildTarget throws must DENY. Falling back to `null` would silently turn a
        // record-level question into a broader one — the failure mode most likely to look like it worked.
        private sealed class ThrowingTargetAdapter : ModulePermissionAdapterBase
        {
            public ThrowingTargetAdapter(IEnumerable<IModuleAccessService> modules) : base(modules) { }
            public override string Scope => EntityRegistry.ScopeHr;
            protected override string ModuleScope => EntityRegistry.ScopeHr;
            protected override string ReasonPrefix => "hr";
            protected override string? MapAction(string platformAction) => "read";
            protected override PermissionTarget? BuildTarget(PermissionCheckRequest request)
                => throw new InvalidOperationException("malformed target");
        }

        [Fact]
        public async Task A_malformed_target_denies_and_the_module_is_never_asked()
        {
            var module = new RecordingModule(EntityRegistry.ScopeHr);
            var adapter = new ThrowingTargetAdapter(new[] { (IModuleAccessService)module });

            var decision = await adapter.CanAsync(Request(EntityRegistry.Employee, 1));

            Assert.False(decision.Allowed);
            Assert.Contains("record-level target could not be built", decision.Reason);
            Assert.Null(module.LastAction);   // the module was never consulted
        }

        // ============================================================================================
        // 4. STARTUP VALIDATION
        // ============================================================================================

        // The validator takes an IServiceScopeFactory because it is a SINGLETON hosted service and the module
        // access services are SCOPED — injecting them directly made the DI container refuse to build. The rule
        // itself is exercised through ValidateAsync so it needs no container.
        private static PermissionScopeStartupValidator Validator()
            => new(new ThrowingScopeFactory(),
                Microsoft.Extensions.Logging.Abstractions.NullLogger<PermissionScopeStartupValidator>.Instance);

        // Never used by ValidateAsync; present only to satisfy the constructor. If a future change makes
        // ValidateAsync reach for a scope, these tests fail loudly instead of silently resolving nothing.
        private sealed class ThrowingScopeFactory : Microsoft.Extensions.DependencyInjection.IServiceScopeFactory
        {
            public Microsoft.Extensions.DependencyInjection.IServiceScope CreateScope()
                => throw new InvalidOperationException(
                    "ValidateAsync must not create a scope — the module list is passed in.");
        }

        [Fact]
        public async Task An_unknown_module_scope_fails_startup()
        {
            var validator = Validator();

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                () => validator.ValidateAsync(new[] { (IModuleAccessService)new RecordingModule("NotARealScope") }));
            Assert.Contains("not in EntityRegistry.PermissionScopes", ex.Message);
        }

        // Two modules claiming one scope would make `FirstOrDefault(m => m.Scope == …)` resolve to whichever
        // was registered first — one module's policy silently answering for the other.
        [Fact]
        public async Task Two_modules_claiming_the_same_scope_fails_startup()
        {
            var validator = Validator();

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => validator.ValidateAsync(new[]
            {
                (IModuleAccessService)new RecordingModule(EntityRegistry.ScopeHr),
                new RecordingModule(EntityRegistry.ScopeHr),
            }));
            Assert.Contains("claim the same scope", ex.Message);
        }

        [Fact]
        public async Task The_real_scope_set_passes_startup_validation()
        {
            await Validator().ValidateAsync(
                EntityRegistry.PermissionScopes.Select(s => (IModuleAccessService)new RecordingModule(s)).ToList());
            Assert.Contains(EntityRegistry.ScopeHr, EntityRegistry.PermissionScopes);
            Assert.Contains(EntityRegistry.ScopeProjects, EntityRegistry.PermissionScopes);
            Assert.Contains(EntityRegistry.ScopeTasks, EntityRegistry.PermissionScopes);
            Assert.Contains(EntityRegistry.ScopeCommunication, EntityRegistry.PermissionScopes);
            // ScopeNone is deliberately NOT a permission scope: a grant against it would be one nobody evaluates.
            Assert.DoesNotContain(EntityRegistry.ScopeNone, EntityRegistry.PermissionScopes);
        }
    }
}
