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

    // The HR vocabulary. Twelve actions, each backed by a real operation found in the 47 mutating actions of
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

        // The one MUTATION an employee may perform with no HR role at all, and only ever about
        // THEMSELVES: submitting and managing their own employee-service requests — leave requests,
        // employee requests, acknowledging their own appraisal.
        //
        // It exists because four live self-service mutations derive the actor from authenticated identity
        // and constrain the subject correctly, but had no creditable mutation authority to check. Reusing
        // an administrative action to authorize them would have been the real defect: leave-manage or
        // employee-manage would credit a self-service POST with authority over the whole company's HR
        // records. One reusable capability for the self-service class, not one action per endpoint.
        //
        // AUTHORITY, NOT REACH. Holding this NEVER means "may raise a request for anyone" — see
        // EvaluateAsync, where it is answered self-only, above both the bootstrap branch and the role
        // rules, so no role and no bootstrap state can widen it.
        public const string EmployeeRequest = "employee-request";

        public static readonly IReadOnlyCollection<string> All = new[]
        {
            Read, EmployeeView, EmployeeManage, AttendanceManage, LeaveManage, LeaveApprove,
            PayrollView, PayrollManage, OrganizationManage, PerformanceManage, ConfidentialView,
            EmployeeRequest,
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

        // The platform’s bootstrap classification, consulted instead of a second copy of it here. HR used to
        // name its own never-open actions inline; NeverBootstrapOpen already carries ("Hr","payroll-manage")
        // and ("Hr","confidential-view") with the note "listed so a refactor cannot widen it", so the list was
        // duplicated in two places that could drift apart. The table is now the only copy.
        private readonly CrossBuy.BL.Platform.IBootstrapAccessPolicyReader _policies;

        public HrAccessService(
            CrossDbContext db, IPlatformRoleDirectory roles, IOrgHierarchy org,
            CrossBuy.BL.Platform.IBootstrapAccessPolicyReader policies, ILogger<HrAccessService> log)
            : base(roles, log)
        { _db = db; _org = org; _policies = policies; _log = log; }

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

            // ---- SELF-SERVICE MUTATION, decided ABOVE bootstrap and ABOVE the role rules ----
            //
            // employee-request answers the same on every install: yes about yourself, no about anyone
            // else. The placement is the security property, not a style choice.
            //
            // Below the bootstrap branch it would inherit SubjectIsInScopeAsync(allowTeam: true), so a
            // company that had simply never configured an HR role would let a manager raise requests for
            // their whole team — bootstrap compatibility SILENTLY WIDER than the configured answer, which
            // is backwards. Below the role rules it would let HrManager or HrOfficer raise a request for
            // anyone in the company, which is the exact reading this capability must never carry.
            //
            // So there is no bootstrap variant of this action and no role that grants it. A caller must
            // name themselves as the subject: a null target is a refusal, because a self-service mutation
            // that does not say whose record it is has not established self-service at all.
            if (action == HrActions.EmployeeRequest)
                return aboutMe;

            // ---- BOOTSTRAP-OPEN ----
            if (bootstrapOpen)
            {
                // Compatibility while a company has configured no HR role — and NOT for every action. Which
                // actions bootstrap may never satisfy is the platform’s classification, not this file’s: the
                // pair that used to be hardcoded here is already in NeverBootstrapOpen, and asking the reader
                // means adding a sensitive HR action to that table closes it here with no edit to this file.
                if (await _policies.IsNeverBootstrapOpenAsync(Scope, action, cancellationToken))
                {
                    _log.LogInformation(
                        "Hr: '{Action}' denied under bootstrap-open for company {Company} — the platform classifies it as never-bootstrap-open.", action, context.CompanyId);
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

            // employee-request is Own on EVERY install, before roles and before bootstrap are consulted.
            // CanAsync answers it self-only, so any wider breadth here would be the two APIs disagreeing
            // again — the defect closed in 8003212, where the record-shaped answer refused what the
            // set-shaped answer handed back company-wide.
            if (action == HrActions.EmployeeRequest) return AccessScope.Own(context.CompanyId);

            // Any HR role is company-wide, so the breadth is Company. Otherwise a caller sees themself plus
            // their reports — the same rule CanAsync applies one record at a time.
            var grants = await RoleDirectory.RolesAsync(context, Scope, cancellationToken);
            if (grants.Count > 0) return AccessScope.Company(context.CompanyId);

            // BOOTSTRAP-OPEN, AND THE SAME CLASSIFICATION CanAsync APPLIES. This is the half that was wrong:
            // the record-shaped answer refused confidential-view and payroll-manage under bootstrap, while
            // this set-shaped answer handed back COMPANY scope for them. One API said "not for you" and the
            // other said "everyone in the company", for the same caller and the same action — and a caller
            // that asks for the scope first would have read salary and disciplinary data company-wide on a
            // tenant that had simply never configured an HR role.
            bool bootstrapOpen = !await RoleDirectory
                .AnyConfiguredAsync(context.CompanyId, Scope, cancellationToken);
            if (bootstrapOpen)
            {
                if (await _policies.IsNeverBootstrapOpenAsync(Scope, action, cancellationToken))
                    return AccessScope.None();
                return AccessScope.Company(context.CompanyId);
            }

            var team = await _org.DirectAndIndirectReportsAsync(context.CompanyId, context.EmployeeId.Value, cancellationToken);
            return team.Count > 1 ? AccessScope.Team(context.CompanyId, team) : AccessScope.Own(context.CompanyId);
        }
    }
}
