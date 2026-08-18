using CrossBuy.BL.Platform;
using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Accounting;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CrossBuy.BL
{
    // Stage 1 Batch C — the first authorization the Projects module has ever had.
    //
    // `ProjectController` carried ZERO authorization on its 30 mutating actions. Record-level access was also
    // not derivable: `Project` has no manager, owner or member column, and no employee-to-project relationship
    // existed anywhere in the model. `ProjectMembers` (Batch C) is that missing relationship. See ADR-028.
    public interface IProjectsAccessService
    {
        Task<bool> CanAsync(BusinessContext context, string action, PermissionTarget? target = null,
            CancellationToken cancellationToken = default);

        // Which projects may this caller act on? Company for a role holder, an explicit id set for a member.
        Task<AccessScope> ResolveProjectScopeAsync(
            BusinessContext context, string action, CancellationToken cancellationToken = default);

        // The project ids an ordinary member is attached to — the set a consumer turns into one predicate.
        Task<IReadOnlySet<int>> MemberProjectIdsAsync(
            BusinessContext context, CancellationToken cancellationToken = default);
    }

    // Eight actions. `member-manage` and `approve` from the brief's example list are ABSENT: there was no
    // membership to manage before this batch and there is no project approval anywhere in the code. Membership
    // management is expressed by `manage` plus the Manager role on the project itself.
    public static class ProjectsActions
    {
        public const string Read = "read";
        public const string Create = "create";
        public const string Edit = "edit";
        public const string Manage = "manage";              // company-wide project administration
        public const string BudgetView = "budget-view";
        public const string BudgetManage = "budget-manage";
        public const string Billing = "billing";            // progress billing / invoicing a project
        public const string Close = "close";

        public static readonly IReadOnlyCollection<string> All = new[]
        { Read, Create, Edit, Manage, BudgetView, BudgetManage, Billing, Close };
    }

    // Module roles (PlatformRoleAssignments, Scope = 'Projects'). Distinct from ProjectMemberRoles, which is a
    // per-project business relationship — the two answer different questions and must not be conflated.
    public static class ProjectsRoles
    {
        public const string ProjectsAdministrator = "ProjectsAdministrator"; // every project in the company
        public const string ProjectsFinance = "ProjectsFinance";             // budgets and billing
        public const string ProjectsViewer = "ProjectsViewer";               // read-only, company-wide

        public static readonly IReadOnlyList<string> All =
            new[] { ProjectsAdministrator, ProjectsFinance, ProjectsViewer };
    }

    public sealed class ProjectsAccessService : ModuleAccessServiceBase, IProjectsAccessService
    {
        private readonly CrossDbContext _db;
        private readonly IModuleAccessService _accounting;
        private readonly ILogger<ProjectsAccessService> _log;

        // Accounting is injected as the CONCRETE AccountingAccessService, not as IEnumerable<IModuleAccessService>.
        //
        // WHY, and it is not a style choice: this service is ITSELF registered as IModuleAccessService. Asking for
        // IEnumerable<IModuleAccessService> would therefore ask DI to build a collection that contains the very
        // object being constructed — a CIRCULAR DEPENDENCY that fails at container-build time and takes the whole
        // application down at startup:
        //
        //   A circular dependency was detected for the service of type 'IModuleAccessService'
        //
        // The concrete type is registered (Program.cs `AddScoped<AccountingAccessService>()`) and implements
        // IModuleAccessService, so this reaches the same CONTEXT-AWARE decision with no cycle. It is still not the
        // legacy `IAccountingAccessService.CanAsync(string)` — that one reads the HTTP session, and a canonical
        // permission check must never do that.
        //
        // Caught by Stage1DiWiringTests.The_batch_c_container_builds_with_scope_validation.
        public ProjectsAccessService(
            CrossDbContext db, IPlatformRoleDirectory roles, AccountingAccessService accounting,
            ILogger<ProjectsAccessService> log) : base(roles, log)
        {
            _db = db; _log = log; _accounting = accounting;
        }

        public override string Scope => EntityRegistry.ScopeProjects;
        public override IReadOnlyCollection<string> Actions => ProjectsActions.All;

        protected override async Task<bool> EvaluateAsync(
            BusinessContext context, string action, PermissionTarget? target,
            IReadOnlyList<RoleGrant> grants, bool bootstrapOpen, CancellationToken cancellationToken)
        {
            bool admin = Holds(grants, ProjectsRoles.ProjectsAdministrator);
            bool finance = Holds(grants, ProjectsRoles.ProjectsFinance);
            bool viewer = Holds(grants, ProjectsRoles.ProjectsViewer);

            // ---- the financial tier is governed SEPARATELY, and delegates to Accounting ----
            //
            // Billing a project posts to the GL. Being an administrator of projects is not a right over the
            // ledger, so `billing` requires the ACCOUNTING module's own decision as well — it cooperates with
            // AccountingAccessService rather than substituting for it. This is the rule that stops project
            // administration becoming a back door into accounting.
            if (action == ProjectsActions.Billing)
            {
                bool accountingSaysYes = await _accounting.CanAsync(context, "post", null, cancellationToken);
                if (!accountingSaysYes)
                {
                    _log.LogInformation(
                        "Projects: 'billing' denied for employee {Employee} — the accounting module refused 'post'. " +
                        "Project billing posts to the GL, so it requires the accounting right as well.",
                        context.EmployeeId);
                    return false;
                }
                // ...and a projects-side right on top of it.
                if (!(admin || finance || bootstrapOpen)) return false;
                return await ProjectIsInScopeAsync(context, target, requireMembership: false, cancellationToken);
            }

            // ---- budget: never granted by membership ----
            // A Member sees the project, not its money. Stated first because it is the rule most likely to be
            // "helpfully" relaxed later.
            if (action is ProjectsActions.BudgetView or ProjectsActions.BudgetManage)
            {
                bool budgetOk = action == ProjectsActions.BudgetView
                    ? (admin || finance || (bootstrapOpen && true))
                    : (admin || finance);
                if (!budgetOk) return false;
                return await ProjectIsInScopeAsync(context, target, requireMembership: false, cancellationToken);
            }

            // ---- bootstrap-open: the non-financial actions behave as they do today ----
            if (bootstrapOpen)
                return await ProjectIsInScopeAsync(context, target, requireMembership: false, cancellationToken);

            // ---- role-driven company-wide access ----
            bool companyWide = action switch
            {
                ProjectsActions.Read => admin || finance || viewer,
                ProjectsActions.Create => admin,
                ProjectsActions.Edit => admin,
                ProjectsActions.Manage => admin,
                ProjectsActions.Close => admin,
                _ => false,
            };
            if (companyWide)
                return await ProjectIsInScopeAsync(context, target, requireMembership: false, cancellationToken);

            // ---- record-level: membership on THIS project ----
            //
            // No module role, so the caller may only reach a project they are actually on. `create` is absent
            // from this path deliberately: creating a project is not a per-project right, so a non-role holder
            // cannot create one at all.
            if (action is ProjectsActions.Read or ProjectsActions.Edit or ProjectsActions.Manage or ProjectsActions.Close)
            {
                // THE PROJECT'S OWN COMPANY IS VERIFIED FIRST, before the membership row is trusted.
                //
                // A ProjectMembers row carries a denormalised CompanyID, and a wrong or tampered one would
                // otherwise be enough: a row saying (CompanyID = 1, ProjectId = <a company-2 project>) would
                // grant a company-1 employee access to company 2's project, because MembershipAsync matches on
                // the ROW's company rather than the project's. Caught by
                // BatchCAccessServiceTests.A_project_in_another_company_is_refused_even_with_a_membership_row,
                // which failed against the first version of this method.
                if (!await ProjectIsInScopeAsync(context, target, requireMembership: false, cancellationToken))
                    return false;

                var membership = await MembershipAsync(context, target, cancellationToken);
                if (membership == null) return false;

                return action switch
                {
                    // Observer, Member and Manager may all READ the project they are on.
                    ProjectsActions.Read => true,
                    // Editing needs Member or Manager — an Observer is read-only by name and by rule.
                    ProjectsActions.Edit => membership is ProjectMemberRoles.Member or ProjectMemberRoles.Manager,
                    // `manage` on a project the caller manages means managing THAT project (its membership),
                    // never company-wide project administration.
                    ProjectsActions.Manage => membership == ProjectMemberRoles.Manager,
                    // Closing a project is an administrative act even for its manager: it freezes billing and
                    // costs, so it stays with the module role.
                    ProjectsActions.Close => false,
                    _ => false,
                };
            }

            return false;
        }

        // The membership role on the target project, or null when there is none that grants anything.
        // Requires: same company, active, and not ended.
        private async Task<string?> MembershipAsync(
            BusinessContext context, PermissionTarget? target, CancellationToken cancellationToken)
        {
            if (target?.ProjectId is not int projectId || projectId <= 0) return null;

            var now = DateTime.UtcNow;
            return await _db.ProjectMembers.AsNoTracking()
                .Where(m => m.ProjectId == projectId
                         && m.EmployeeId == context.EmployeeId!.Value
                         && m.CompanyID == context.CompanyId          // company FIRST, from the context
                         && m.IsActive
                         && (m.LeftAt == null || m.LeftAt > now))     // an ended membership grants nothing
                .Select(m => m.RoleOnProject)
                .FirstOrDefaultAsync(cancellationToken);
        }

        // The project named by the target must belong to the caller's company. Its company comes from the
        // PROJECT ROW, never from the request — a posted ProjectID is only ever a lookup key.
        private async Task<bool> ProjectIsInScopeAsync(
            BusinessContext context, PermissionTarget? target, bool requireMembership,
            CancellationToken cancellationToken)
        {
            if (target?.ProjectId is not int projectId || projectId <= 0) return true;   // not project-specific

            var projectCompany = await _db.Projects.AsNoTracking()
                .Where(p => p.ID == projectId)
                .Select(p => (int?)p.CompanyID)
                .FirstOrDefaultAsync(cancellationToken);

            // A project that does not exist and one belonging to another company answer identically, so ids
            // cannot be enumerated by probing.
            if (projectCompany == null || projectCompany.Value != context.CompanyId) return false;

            if (!requireMembership) return true;
            return await MembershipAsync(context, target, cancellationToken) != null;
        }

        public async Task<IReadOnlySet<int>> MemberProjectIdsAsync(
            BusinessContext context, CancellationToken cancellationToken = default)
        {
            if (context?.EmployeeId is not > 0 || context.CompanyId <= 0) return new HashSet<int>();
            var now = DateTime.UtcNow;
            var ids = await _db.ProjectMembers.AsNoTracking()
                .Where(m => m.EmployeeId == context.EmployeeId.Value
                         && m.CompanyID == context.CompanyId
                         && m.IsActive
                         && (m.LeftAt == null || m.LeftAt > now))
                .Select(m => m.ProjectId)
                .ToListAsync(cancellationToken);
            return ids.ToHashSet();
        }

        public async Task<AccessScope> ResolveProjectScopeAsync(
            BusinessContext context, string action, CancellationToken cancellationToken = default)
        {
            if (context == null || context.CompanyId <= 0 || context.EmployeeId is not > 0) return AccessScope.None();
            if (!ProjectsActions.All.Contains(action, StringComparer.Ordinal)) return AccessScope.None();

            var grants = await RoleDirectory.RolesAsync(context, Scope, cancellationToken);
            bool bootstrapOpen = grants.Count == 0
                && !await RoleDirectory.AnyConfiguredAsync(context.CompanyId, Scope, cancellationToken);

            if (bootstrapOpen || grants.Count > 0) return AccessScope.Company(context.CompanyId);

            // A non-role holder sees exactly the projects they are a member of. Expressed as Own with the
            // project ids carried in PrincipalIds, because for Projects the "principal" of a record IS the
            // project the caller is attached to — the consumer filters `Projects.ID IN (…)`.
            var ids = await MemberProjectIdsAsync(context, cancellationToken);
            return ids.Count == 0
                ? AccessScope.None()
                : AccessScope.Team(context.CompanyId, ids.ToList());
        }
    }
}
