using CrossBuy.BL.Platform;
using CrossBuy.Models.Context;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CrossBuy.BL
{
    // Stage 1 Batch C — the FIRST authorization the HR module has ever had.
    //
    // Before this, `AdminController` (+3 partials) and `PeopleController` carried ZERO authorization: no
    // permission attribute, no role check, no in-body gate. Their only guard was the global session
    // middleware, i.e. authentication. Any signed-in employee could reach employee administration, salary
    // policies, payroll paths and appraisals. That is the gap this service closes — see ADR-027.
    //
    // ROLES: read through IPlatformRoleDirectory from the ONE shared PlatformRoleAssignments table
    // (Scope = 'Hr'). There is no HrUserRoles table and there never will be — see ADR-026.
    //
    // BOOTSTRAP-OPEN: until a company assigns its first Hr role, the module answers as it does today. That
    // keeps a live install working on the day the code deploys; it is compatibility, not the destination.
    public interface IHrAccessService
    {
        // Canonical, session-free. Prefer this everywhere.
        Task<bool> CanAsync(BusinessContext context, string action, PermissionTarget? target = null,
            CancellationToken cancellationToken = default);

        // The set-shaped companion: which employees' records may this caller see?
        Task<AccessScope> ResolveEmployeeScopeAsync(
            BusinessContext context, string action, CancellationToken cancellationToken = default);
    }

    // The HR vocabulary. Eleven actions, each backed by a real operation found in the 47 mutating actions of
    // AdminController + PeopleController (analysis §3). Nothing speculative.
    public static class HrActions
    {
        public const string Read = "read";                              // HR screens exist; lists and lookups
        public const string EmployeeView = "employee-view";             // an individual employee record
        public const string EmployeeManage = "employee-manage";         // create/edit employees, contracts, documents
        public const string AttendanceManage = "attendance-manage";     // attendance records, policies, holidays
        public const string LeaveManage = "leave-manage";               // leave types/policies/encashment/provision
        public const string LeaveApprove = "leave-approve";             // act on an approval step
        public const string PayrollView = "payroll-view";               // payslips, payroll preview
        public const string PayrollManage = "payroll-manage";           // salary policies, payroll runs
        public const string OrganizationManage = "organization-manage"; // hierarchicals, job titles, org units
        public const string PerformanceManage = "performance-manage";   // appraisals, training, recruitment
        public const string ConfidentialView = "confidential-view";     // salary/disciplinary/performance detail

        public static readonly IReadOnlyCollection<string> All = new[]
        {
            Read, EmployeeView, EmployeeManage, AttendanceManage, LeaveManage, LeaveApprove,
            PayrollView, PayrollManage, OrganizationManage, PerformanceManage, ConfidentialView,
        };
    }

    // The role names this module recognises in PlatformRoleAssignments (Scope = 'Hr').
    //
    // These are NEW names — no HR role existed anywhere in the codebase or the data (analysis §1.1), so there
    // was nothing to preserve. They are deliberately few: four roles that map onto the operations that
    // actually exist, rather than an org chart of titles nobody has assigned.
    public static class HrRoles
    {
        public const string HrManager = "HrManager";           // everything except payroll money
        public const string HrOfficer = "HrOfficer";           // day-to-day employee/attendance/leave admin
        public const string PayrollOfficer = "PayrollOfficer"; // payroll and salary policy
        public const string HrViewer = "HrViewer";             // read-only across the company's HR data

        public static readonly IReadOnlyList<string> All = new[] { HrManager, HrOfficer, PayrollOfficer, HrViewer };
    }

    public sealed class HrAccessService : ModuleAccessServiceBase, IHrAccessService
    {
        private readonly CrossDbContext _db;
        private readonly IOrgHierarchy _org;
        private readonly ILogger<HrAccessService> _log;

        public HrAccessService(
            CrossDbContext db, IPlatformRoleDirectory roles, IOrgHierarchy org, ILogger<HrAccessService> log)
            : base(roles, log)
        { _db = db; _org = org; _log = log; }

        public override string Scope => EntityRegistry.ScopeHr;
        public override IReadOnlyCollection<string> Actions => HrActions.All;

        protected override async Task<bool> EvaluateAsync(
            BusinessContext context, string action, PermissionTarget? target,
            IReadOnlyList<RoleGrant> grants, bool bootstrapOpen, CancellationToken cancellationToken)
        {
            int me = context.EmployeeId!.Value;

            // ---- SELF-ACCESS, evaluated before roles and independent of them ----
            //
            // An employee may read their own non-confidential record even with no HR role at all. That is
            // self-service, and it is why `employee-view` is separated from `confidential-view`: the
            // confidential tier is NOT self-served, because "it is my own salary" is not the same claim as
            // "I may see salary data", and a disciplinary record about someone is not theirs to read.
            bool aboutMe = target?.SubjectEmployeeId is int subject && subject == me;
            if (aboutMe && (action == HrActions.Read || action == HrActions.EmployeeView))
                return true;

            // Confidential/payroll about oneself still requires the explicit permission. Stated as its own
            // branch so the rule is visible rather than implied by falling through.
            if (aboutMe && (action == HrActions.ConfidentialView
                         || action == HrActions.PayrollView
                         || action == HrActions.PerformanceManage))
            {
                // fall through to the role rules below — no self-exemption
            }

            // ---- BOOTSTRAP-OPEN ----
            // Compatibility while a company has configured no HR role. It deliberately does NOT extend to the
            // confidential tier or to payroll: those were unreachable-by-design data before this batch, and
            // opening them by default would be a new exposure created by the very batch meant to close one.
            if (bootstrapOpen)
            {
                if (action == HrActions.ConfidentialView || action == HrActions.PayrollManage)
                {
                    _log.LogInformation(
                        "Hr: '{Action}' denied under bootstrap-open for company {Company} — the confidential and " +
                        "payroll-manage tiers are never bootstrap-open.", action, context.CompanyId);
                    return false;
                }
                return await SubjectIsInScopeAsync(context, target, allowTeam: true, cancellationToken);
            }

            // ---- ROLE RULES ----
            bool hrManager = Holds(grants, HrRoles.HrManager);
            bool hrOfficer = Holds(grants, HrRoles.HrOfficer);
            bool payroll = Holds(grants, HrRoles.PayrollOfficer);
            bool viewer = Holds(grants, HrRoles.HrViewer);
            bool anyHr = hrManager || hrOfficer || payroll || viewer;

            bool allowedByRole = action switch
            {
                HrActions.Read => anyHr,
                HrActions.EmployeeView => anyHr,
                HrActions.EmployeeManage => hrManager || hrOfficer,
                HrActions.AttendanceManage => hrManager || hrOfficer,
                HrActions.LeaveManage => hrManager || hrOfficer,
                // Approval is NOT a role grant — it is the approver chain (below). A role never substitutes
                // for being the actual approver, because approving out of turn is the thing the chain exists
                // to prevent.
                HrActions.LeaveApprove => false,
                HrActions.PayrollView => hrManager || payroll,
                HrActions.PayrollManage => payroll,              // NOT HrManager: money is a separate right
                HrActions.OrganizationManage => hrManager,
                HrActions.PerformanceManage => hrManager,
                HrActions.ConfidentialView => hrManager || payroll,
                _ => false,
            };

            // ---- LEAVE APPROVAL: the one record-level rule that already existed ----
            //
            // LeaveWorkflowService computes the approver chain (direct manager → unit head) from Hierarchical.
            // This service does not re-derive it: the caller must be a manager of the SUBJECT, which is the
            // same relationship the chain is built from, company-intersected.
            if (action == HrActions.LeaveApprove)
            {
                if (target?.SubjectEmployeeId is not int leaveSubject) return false;
                if (leaveSubject == me) return false;   // nobody approves their own leave
                var team = await _org.DirectAndIndirectReportsAsync(context.CompanyId, me, cancellationToken);
                return team.Contains(leaveSubject);
            }

            if (!allowedByRole) return false;

            // ---- the subject must be in scope, even for a role holder ----
            return await SubjectIsInScopeAsync(context, target, allowTeam: true, cancellationToken);
        }

        // An HR role is company-wide by design (an HR officer administers their company's employees), so the
        // check here is that the SUBJECT belongs to the caller's company — never that a posted id looked
        // plausible. A manager with no HR role reaches their own reports through the team path.
        private async Task<bool> SubjectIsInScopeAsync(
            BusinessContext context, PermissionTarget? target, bool allowTeam, CancellationToken cancellationToken)
        {
            if (target?.SubjectEmployeeId is not int subject) return true;   // not about a specific employee
            if (subject == context.EmployeeId!.Value) return true;

            // The authoritative company of the subject comes from the Employee ROW, never from the request.
            var row = await _db.Employee.AsNoTracking()
                .Where(e => e.ID == subject)
                .Select(e => new { e.EmpCompanyID, e.IsActive })
                .FirstOrDefaultAsync(cancellationToken);

            // A subject that does not exist, or belongs to another company, is refused — and the two answer
            // identically, so a caller cannot enumerate employees by probing ids.
            if (row == null || row.EmpCompanyID != context.CompanyId) return false;

            // An INACTIVE employee is still administrable (a leaver's record must remain correctable), so
            // being inactive does not deny — it is recorded explicitly rather than left to chance, and the
            // behaviour is asserted by a test so it cannot drift into an accidental deny.
            if (!row.IsActive)
                _log.LogDebug("Hr: subject employee {Subject} is inactive; access is unchanged by that.", subject);

            return true;
        }

        // The set-shaped answer: whose employee records may this caller see?
        public async Task<AccessScope> ResolveEmployeeScopeAsync(
            BusinessContext context, string action, CancellationToken cancellationToken = default)
        {
            if (context == null || context.CompanyId <= 0 || context.EmployeeId is not > 0) return AccessScope.None();
            if (!HrActions.All.Contains(action, StringComparer.Ordinal)) return AccessScope.None();

            // Any HR role is company-wide, so the breadth is Company. Otherwise a caller sees themself plus
            // their reports — the same rule CanAsync applies one record at a time.
            var grants = await RoleDirectory.RolesAsync(context, Scope, cancellationToken);
            if (grants.Count > 0) return AccessScope.Company(context.CompanyId);

            bool bootstrapOpen = !await RoleDirectory
                .AnyConfiguredAsync(context.CompanyId, Scope, cancellationToken);
            if (bootstrapOpen) return AccessScope.Company(context.CompanyId);

            var team = await _org.DirectAndIndirectReportsAsync(context.CompanyId, context.EmployeeId.Value, cancellationToken);
            return team.Count > 1 ? AccessScope.Team(context.CompanyId, team) : AccessScope.Own(context.CompanyId);
        }
    }
}
