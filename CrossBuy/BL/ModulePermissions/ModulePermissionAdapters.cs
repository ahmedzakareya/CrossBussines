using CrossBuy.BL;
using CrossBuy.BL.Platform;
using CrossBuy.Models.Platform;

// MODULE SURFACE — the per-module translation layers, moved OUT of the Platform Kernel.
//
// These classes were physically inside BL/Platform/PlatformPermissionProvider.cs. They are module code:
// each one maps the three canonical platform actions onto the action strings its own module already
// understands, and CalendarPermissionAdapter names CalendarActions directly. Keeping them in a kernel
// file made the kernel un-committable on its own — a kernel-only build against HEAD failed on
// CalendarActions, among others.
//
// The code below is MOVED VERBATIM. Behaviour is deliberately untouched: same base class, same scopes,
// same action mappings, same deny reasons. The only change is which file, namespace and assembly-layer
// they live in, so the kernel no longer names a module and the module keeps owning its own vocabulary.
namespace CrossBuy.BL.ModulePermissions
{
    // AccountingAccessService: read | post | pay | manage | currency-override
    public class AccountingPermissionAdapter : ModulePermissionAdapterBase
    {
        public AccountingPermissionAdapter(IEnumerable<IModuleAccessService> modules) : base(modules) { }

        public override string Scope => EntityRegistry.ScopeAccounting;
        protected override string ModuleScope => EntityRegistry.ScopeAccounting;
        protected override string ReasonPrefix => "accounting";

        protected override string? MapAction(string platformAction) => platformAction switch
        {
            PlatformActions.View => "read",              // any authenticated user in the company may view
            PlatformActions.ViewConfidential => "post",  // Accountant / ChiefAccountant
            PlatformActions.ViewRestricted => "manage",  // ChiefAccountant only
            _ => null,
        };
    }

    // InventoryAccessService: read | doc | purchase | manage
    public class InventoryPermissionAdapter : ModulePermissionAdapterBase
    {
        public InventoryPermissionAdapter(IEnumerable<IModuleAccessService> modules) : base(modules) { }

        public override string Scope => EntityRegistry.ScopeInventory;
        protected override string ModuleScope => EntityRegistry.ScopeInventory;
        protected override string ReasonPrefix => "inventory";

        protected override string? MapAction(string platformAction) => platformAction switch
        {
            PlatformActions.View => "read",
            PlatformActions.ViewConfidential => "doc",    // InventoryManager / WarehouseKeeper
            PlatformActions.ViewRestricted => "manage",   // InventoryManager only
            _ => null,
        };
    }

    // Manufacturing (slice 2). There is NO manufacturing RBAC service in this codebase: the work-order
    // screens live in InventoryController and are governed by inventory roles via InvPermAttribute.
    //
    // This adapter therefore DELEGATES to the inventory service rather than inventing a policy:
    //   * View maps to inventory "read", which is the module's own decision that any authenticated user in
    //     the company may look — not a default this adapter granted;
    //   * the elevated tiers map to "doc" and "manage", which ARE real gates (InventoryManager /
    //     WarehouseKeeper), so a Confidential or Restricted manufacturing event is genuinely restricted.
    // The scope is separate from Inventory so manufacturing policy can diverge later — a real
    // ManufacturingUserRole table would only change this class.
    public class ManufacturingPermissionAdapter : ModulePermissionAdapterBase
    {
        public ManufacturingPermissionAdapter(IEnumerable<IModuleAccessService> modules) : base(modules) { }

        public override string Scope => EntityRegistry.ScopeManufacturing;
        // Points at INVENTORY on purpose — manufacturing has no access service of its own.
        protected override string ModuleScope => EntityRegistry.ScopeInventory;
        protected override string ReasonPrefix => "manufacturing(inventory)";

        protected override string? MapAction(string platformAction) => platformAction switch
        {
            PlatformActions.View => "read",
            PlatformActions.ViewConfidential => "doc",     // InventoryManager / WarehouseKeeper
            PlatformActions.ViewRestricted => "manage",    // InventoryManager only
            _ => null,
        };
    }

    // CrmAccessService: read | edit | manage
    public class CrmPermissionAdapter : ModulePermissionAdapterBase
    {
        public CrmPermissionAdapter(IEnumerable<IModuleAccessService> modules) : base(modules) { }

        public override string Scope => EntityRegistry.ScopeCrm;
        protected override string ModuleScope => EntityRegistry.ScopeCrm;
        protected override string ReasonPrefix => "crm";

        protected override string? MapAction(string platformAction) => platformAction switch
        {
            PlatformActions.View => "read",
            PlatformActions.ViewConfidential => "edit",
            PlatformActions.ViewRestricted => "manage",
            _ => null,
        };
    }

    // PosAccessService was ALREADY session-free (its predicates take the role list, and resolution takes a
    // user id), so this adapter changes only in that it now routes through the module's canonical CanAsync.
    // The POS vocabulary is predicates, not read/post/pay/manage, so the mapping names them as they are.
    public class PosPermissionAdapter : ModulePermissionAdapterBase
    {
        public PosPermissionAdapter(IEnumerable<IModuleAccessService> modules) : base(modules) { }

        public override string Scope => EntityRegistry.ScopePos;
        protected override string ModuleScope => EntityRegistry.ScopePos;
        protected override string ReasonPrefix => "pos";

        protected override string? MapAction(string platformAction) => platformAction switch
        {
            PlatformActions.View => "view",                  // holding any POS role is enough to view
            PlatformActions.ViewConfidential => "sell",      // cashier / manager
            PlatformActions.ViewRestricted => "manage",      // manager only
            _ => null,
        };
    }

    // ============================================================================================
    // Stage 1 Batch C — the four adapters for the modules that had none.
    //
    // Each overrides BuildTarget so a record-level rule is actually reachable through the platform provider.
    // That override is the ONLY thing they add over the base; the forwarding logic lives once, in the base.
    // ============================================================================================

    // HrAccessService: read | employee-view | employee-manage | attendance-manage | leave-manage |
    //                  leave-approve | payroll-view | payroll-manage | organization-manage |
    //                  performance-manage | confidential-view
    public class HrPermissionAdapter : ModulePermissionAdapterBase
    {
        public HrPermissionAdapter(IEnumerable<IModuleAccessService> modules) : base(modules) { }

        public override string Scope => EntityRegistry.ScopeHr;
        protected override string ModuleScope => EntityRegistry.ScopeHr;
        protected override string ReasonPrefix => "hr";

        protected override string? MapAction(string platformAction) => platformAction switch
        {
            PlatformActions.View => "read",
            PlatformActions.ViewConfidential => "employee-view",
            PlatformActions.ViewRestricted => "confidential-view",
            _ => null,
        };

        // An HR check about an `Employee` record is about that employee — so the entity id becomes the
        // SUBJECT, which is what self-access and the direct-report rule compare against. For any other entity
        // type the target carries only the company, so the module still applies its company gate.
        protected override PermissionTarget? BuildTarget(PermissionCheckRequest request)
            => string.Equals(request.EntityType, EntityRegistry.Employee, StringComparison.Ordinal)
                ? new PermissionTarget
                {
                    EntityType = request.EntityType, EntityId = request.EntityId,
                    SubjectEmployeeId = request.EntityId, CompanyId = request.Context.CompanyId,
                }
                : new PermissionTarget
                {
                    EntityType = request.EntityType, EntityId = request.EntityId,
                    CompanyId = request.Context.CompanyId,
                };
    }

    // ProjectsAccessService: read | create | edit | manage | budget-view | budget-manage | billing | close
    public class ProjectsPermissionAdapter : ModulePermissionAdapterBase
    {
        public ProjectsPermissionAdapter(IEnumerable<IModuleAccessService> modules) : base(modules) { }

        public override string Scope => EntityRegistry.ScopeProjects;
        protected override string ModuleScope => EntityRegistry.ScopeProjects;
        protected override string ReasonPrefix => "projects";

        protected override string? MapAction(string platformAction) => platformAction switch
        {
            PlatformActions.View => "read",
            // Confidential maps to budget-view because a project's MONEY is the confidential tier here — it is
            // the one thing membership deliberately does not confer.
            PlatformActions.ViewConfidential => "budget-view",
            PlatformActions.ViewRestricted => "manage",
            _ => null,
        };

        protected override PermissionTarget? BuildTarget(PermissionCheckRequest request)
            => string.Equals(request.EntityType, EntityRegistry.Project, StringComparison.Ordinal)
                ? new PermissionTarget
                {
                    EntityType = request.EntityType, EntityId = request.EntityId,
                    ProjectId = request.EntityId, CompanyId = request.Context.CompanyId,
                }
                : new PermissionTarget
                {
                    EntityType = request.EntityType, EntityId = request.EntityId,
                    CompanyId = request.Context.CompanyId,
                };
    }

    // TasksAccessService: read | create | edit | assign | reassign | complete | reopen | manage
    // Calendar. Added with CalendarAccessService — before it, CalendarEvent fell to
    // DefaultPermissionAdapter, which denies everything except View because no module policy existed.
    public class CalendarPermissionAdapter : ModulePermissionAdapterBase
    {
        public CalendarPermissionAdapter(IEnumerable<IModuleAccessService> modules) : base(modules) { }

        public override string Scope => EntityRegistry.ScopeCalendar;
        protected override string ModuleScope => EntityRegistry.ScopeCalendar;
        protected override string ReasonPrefix => "calendar";

        protected override string? MapAction(string platformAction) => platformAction switch
        {
            PlatformActions.View => CalendarActions.Read,
            PlatformActions.ViewConfidential => CalendarActions.Edit,
            PlatformActions.ViewRestricted => CalendarActions.Manage,
            _ => null,
        };

        // A calendar check NAMES the event, so the record rule (owner / attendee / company-scope /
        // manager) can run. Without this the platform would ask a module-level question and the
        // per-event rule would never be reached.
        protected override PermissionTarget? BuildTarget(PermissionCheckRequest request)
            => new PermissionTarget
            {
                EntityType = request.EntityType, EntityId = request.EntityId,
                CompanyId = request.Context.CompanyId,
            };
    }

    public class TasksPermissionAdapter : ModulePermissionAdapterBase
    {
        public TasksPermissionAdapter(IEnumerable<IModuleAccessService> modules) : base(modules) { }

        public override string Scope => EntityRegistry.ScopeTasks;
        protected override string ModuleScope => EntityRegistry.ScopeTasks;
        protected override string ReasonPrefix => "tasks";

        protected override string? MapAction(string platformAction) => platformAction switch
        {
            PlatformActions.View => "read",
            PlatformActions.ViewConfidential => "edit",
            PlatformActions.ViewRestricted => "manage",
            _ => null,
        };

        // A task check names the task, so the record rule (assignee / creator / manager / linked entity) can
        // run. EntityRegistry has no TaskItem code yet, so this keys on the id being present with the Tasks
        // scope rather than on a registry constant that does not exist — stated because it is the one place a
        // canonical code would have been preferable.
        protected override PermissionTarget? BuildTarget(PermissionCheckRequest request)
            => new PermissionTarget
            {
                EntityType = request.EntityType, EntityId = request.EntityId,
                TaskId = request.EntityId, CompanyId = request.Context.CompanyId,
            };
    }

    // CommunicationAccessService: read | send | create-group | manage-group | announcement-send | outbox-manage
    public class CommunicationPermissionAdapter : ModulePermissionAdapterBase
    {
        public CommunicationPermissionAdapter(IEnumerable<IModuleAccessService> modules) : base(modules) { }

        public override string Scope => EntityRegistry.ScopeCommunication;
        protected override string ModuleScope => EntityRegistry.ScopeCommunication;
        protected override string ReasonPrefix => "communication";

        protected override string? MapAction(string platformAction) => platformAction switch
        {
            PlatformActions.View => "read",
            PlatformActions.ViewConfidential => "send",
            PlatformActions.ViewRestricted => "manage-group",
            _ => null,
        };

        protected override PermissionTarget? BuildTarget(PermissionCheckRequest request)
            => new PermissionTarget
            {
                EntityType = request.EntityType, EntityId = request.EntityId,
                ConversationId = request.EntityId, CompanyId = request.Context.CompanyId,
            };
    }
}
