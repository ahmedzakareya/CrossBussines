using CrossBuy.BL;
using CrossBuy.BL.Platform;

// MODULE SURFACE — each module publishes its own permission vocabulary to the Platform Kernel.
//
// These are the inverted dependency. PlatformGrantWriter used to hold
//
//     [EntityRegistry.ScopeTasks] = TasksRoles.All            and
//     EntityRegistry.ScopeTasks   => TasksActions.Manage
//
// inside kernel source, which is why a kernel-only build could not resolve TasksRoles, HrRoles,
// ProjectsRoles or CommunicationRoles. The lists are not copied here: each vocabulary PROJECTS the
// module's own published constants, so the module remains the single source of truth and a rename in
// the module still flows through. A second hand-maintained copy would be the thing that goes stale and
// then rejects a role the module genuinely honours.
namespace CrossBuy.BL.ModulePermissions
{
    public sealed class HrPermissionVocabulary : IPlatformPermissionVocabulary
    {
        public string Scope => EntityRegistry.ScopeHr;
        public IReadOnlyList<string> Roles => HrRoles.All;

        // Named by the module, not guessed by the kernel: HR also publishes `attendance-manage`, and a
        // "contains manage" heuristic would pick it, which is not HR's administrative right.
        public string? ManageAction => HrActions.OrganizationManage;
    }

    public sealed class ProjectsPermissionVocabulary : IPlatformPermissionVocabulary
    {
        public string Scope => EntityRegistry.ScopeProjects;
        public IReadOnlyList<string> Roles => ProjectsRoles.All;
        public string? ManageAction => ProjectsActions.Manage;
    }

    public sealed class TasksPermissionVocabulary : IPlatformPermissionVocabulary
    {
        public string Scope => EntityRegistry.ScopeTasks;
        public IReadOnlyList<string> Roles => TasksRoles.All;
        public string? ManageAction => TasksActions.Manage;
    }

    public sealed class CommunicationPermissionVocabulary : IPlatformPermissionVocabulary
    {
        public string Scope => EntityRegistry.ScopeCommunication;
        public IReadOnlyList<string> Roles => CommunicationRoles.All;
        public string? ManageAction => CommunicationActions.ManageGroup;
    }
}
