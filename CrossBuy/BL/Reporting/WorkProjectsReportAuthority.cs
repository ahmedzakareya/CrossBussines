using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CrossBuy.BL.Platform;
using CrossBuy.Models.Platform;

namespace CrossBuy.BL.Reporting
{
    // ==================================================================================================
    // WORK AND PROJECTS REPORTING AUTHORITY (ADR-037).
    //
    // WHAT THIS FILE IS, AND WHAT IT IS EMPHATICALLY NOT. It is a MAP: two reporting permission keys,
    // each resolving to one action a module already publishes, asked of that module on the caller's own
    // BusinessContext. It defines no role, no population, no new capability and no row rule. A reporting
    // key is a PROJECTION of module authority; the moment Reporting starts deciding who may read Work or
    // Projects data, Reporting has become the business authority, which is the precise inversion ADR-037
    // exists to prevent.
    //
    // WHY NO NEW MODULE ACTION WAS ADDED. Accounting needed a narrower `report-read` because its module
    // read tier is "any authenticated user in this company" - as a reporting grant that would publish the
    // ledger to everyone. Tasks and Projects are not shaped that way: `read` is ALREADY a gated action on
    // both, and both ALREADY resolve to a breadth (Own / Team / Company) that a data source applies per
    // row. So the module's existing answer is the correct one, and inventing a reporting action would have
    // meant defining a new population of users - a business decision this wave has no authority to make.
    //
    // THE KEYS ARE CANONICAL, NOT INVENTED. `module.reports.view` is the established vocabulary:
    // accounting.reports.view, crm.reports.view, inventory.reports.view, roster.reports.view and
    // admin.orgstructure.reports.view all precede these two.
    //
    // ---- WHAT THIS AUTHORITY DELIBERATELY DOES NOT COVER ---------------------------------------------
    //
    // CALENDAR. `work.reports.view` governs the TASK-grounded Work subjects only - the task register,
    // logged hours, escalation facts and approval state, all of which are rows of, or semi-joins onto,
    // TaskItem. It does NOT govern CalendarEvent. Calendar is a SEPARATE module with its own scope
    // (EntityRegistry.ScopeCalendar) and its own roles (CalendarAdministrator, CalendarSupervisor), and
    // its read rule is not the Tasks one: a TasksViewer holds company-wide TASK read and NO calendar right
    // at all, so gating calendar rows on Tasks read would hand that person every colleague's
    // Personal-scope event - something CalendarAccessService refuses them today. A calendar reporting
    // subject therefore needs its own key and its own module authority. Absent by decision, not by
    // oversight.
    //
    // MONEY. Projects publishes `budget-view` as a SEPARATE right because contract value, budget and
    // retention are not readable by everyone who may read a project. No key here maps to it, and an
    // unmapped key inside a claimed namespace is REFUSED (see below), so a budget subject cannot quietly
    // ride in on `projects.reports.view`.
    // ==================================================================================================

    public static class WorkReportPermissions
    {
        // ONE TIER. A second tier separates a commercially sensitive disclosure from an operational one
        // (accounting's margin, inventory's cost). The task-grounded Work subjects expose operational
        // facts only, so a second tier would separate nothing. A commercial Work field - BillRate,
        // IsBillable - would need its own key AND its own module action, not room left over in this one.
        public const string View = "work.reports.view";

        // The namespace this authority claims. EVERYTHING under it is answered here or refused here.
        public const string Prefix = "work.";
    }

    public static class ProjectsReportPermissions
    {
        public const string View = "projects.reports.view";

        public const string Prefix = "projects.";
    }

    // --------------------------------------------------------------------------------------------------
    // The shape both authorities share: resolve the key to an action, refuse an unrecognised one, verify
    // the module is the module we meant and that it really publishes the action, then let the module
    // decide on the caller's own context.
    //
    // THE TARGET IS NULL, AND THAT IS THE QUESTION BEING ASKED. "May you open this subject at all" is a
    // module-level question, so no record is named. The consequence is deliberate and fail-closed: a
    // caller with no module role is refused HERE even where a breadth would have shown them their own
    // rows. Widening this by inventing a target would be Reporting deciding reach.
    // --------------------------------------------------------------------------------------------------

    public sealed class WorkReportModuleAuthority : IReportModuleAuthority
    {
        private readonly IModuleAccessService _tasks;

        public WorkReportModuleAuthority(IModuleAccessService tasks)
        {
            _tasks = tasks ?? throw new ArgumentNullException(nameof(tasks));
        }

        public string KeyPrefix => WorkReportPermissions.Prefix;

        public Task<bool?> EvaluateAsync(string permissionKey, BusinessContext context,
            CancellationToken cancellationToken = default)
        {
            // Explicit, never derived from the key's text. `work.reports.view` resolving to an action by
            // string surgery is how a rename becomes a silent grant.
            var action = permissionKey switch
            {
                WorkReportPermissions.View => TasksActions.Read,
                _ => null,
            };

            // Inside our namespace but not a key we issued - including a future `work.reports.calendar`
            // that nobody has mapped. null, which the adapter reads as a refusal rather than a
            // fall-through to the role map.
            if (action is null) return Task.FromResult<bool?>(null);

            return Decide(_tasks, EntityRegistry.ScopeTasks, action, context, cancellationToken);
        }

        // PUBLIC so the two guards below can be asserted directly. Both are unobservable through
        // EvaluateAsync under a correct registration - which is exactly why they need their own test.
        public static async Task<bool?> Decide(
            IModuleAccessService module, string expectedScope, string action,
            BusinessContext context, CancellationToken cancellationToken)
        {
            // THE MODULE MUST BE THE MODULE WE MEANT. IModuleAccessService has nine implementations and a
            // mis-wired registration would otherwise ask the wrong one and be believed. Refusing on a
            // scope mismatch makes that a denial rather than a silent cross-module grant.
            if (!string.Equals(module.Scope, expectedScope, StringComparison.OrdinalIgnoreCase))
                return false;

            // AND IT SHOULD PUBLISH THE ACTION - but be honest about what this line is worth.
            //
            // IT IS REDUNDANT, AND MEASURED TO BE SO. Deleting it kills no test, and neither does deleting
            // it TOGETHER WITH ModuleAccessServiceBase gate 2, because every module's own EvaluateAsync
            // switch also defaults to false. Three layers refuse an unpublished action, so this one is
            // defence in depth against a FOURTH case that does not exist today: an IReportModuleAuthority
            // pointed at some future IModuleAccessService that is not ModuleAccessServiceBase-derived.
            // Kept deliberately and labelled as redundant, because a comment claiming that a dead line is
            // load-bearing is worse than the dead line.
            if (!module.Actions.Contains(action, StringComparer.OrdinalIgnoreCase))
                return false;

            // The context is passed through untouched and no target is named. CanAsync re-checks the
            // company itself and denies an unresolved one, so the tenant is decided by the module, from
            // the caller's own scope - never from a request, and never defaulted.
            return await module.CanAsync(context, action, null, cancellationToken);
        }
    }

    public sealed class ProjectsReportModuleAuthority : IReportModuleAuthority
    {
        private readonly IModuleAccessService _projects;

        public ProjectsReportModuleAuthority(IModuleAccessService projects)
        {
            _projects = projects ?? throw new ArgumentNullException(nameof(projects));
        }

        public string KeyPrefix => ProjectsReportPermissions.Prefix;

        public Task<bool?> EvaluateAsync(string permissionKey, BusinessContext context,
            CancellationToken cancellationToken = default)
        {
            // `budget-view` is NOT mapped - see the header. One entry, exhaustive by intent.
            var action = permissionKey switch
            {
                ProjectsReportPermissions.View => ProjectsActions.Read,
                _ => null,
            };

            if (action is null) return Task.FromResult<bool?>(null);

            return WorkReportModuleAuthority.Decide(
                _projects, EntityRegistry.ScopeProjects, action, context, cancellationToken);
        }
    }
}
