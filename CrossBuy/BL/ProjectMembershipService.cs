using CrossBuy.BL.Platform;
using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Accounting;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL
{
    // WHO IS ON A PROJECT — the writer for dbo.ProjectMembers.
    //
    // WHY THIS EXISTS. The table, the entity, the EF mapping, the CHECK constraints and a correct record-level
    // access service all shipped in Batch C. What never shipped was a way to put a row in it: before this file
    // the whole codebase contained exactly two reads of ProjectMembers and ZERO writes, so a membership could
    // only be created by hand-written SQL.
    //
    // That was not merely inconvenient. ProjectsAccessService.ResolveProjectScopeAsync grants company-wide
    // access only while the company is bootstrap-open; the moment its FIRST Projects role is configured, every
    // non-role-holder falls through to membership — which was permanently empty — and receives
    // AccessScope.None(). Turning governance on would therefore have locked a company out of its own projects.
    // Membership had to become writable in the same batch that repaired the controller's authorization, and
    // that is the only reason this service is in this pass.
    //
    // IT IS NOT A SECOND AUTHORIZATION SYSTEM. Every method asks IProjectsAccessService and nothing else. The
    // rules about who may manage membership live there, beside the rules about who may read a project; this
    // file only refuses to act when that service says no. The one thing it adds is the COMPANY INTERSECTION —
    // the project and the employee must BOTH belong to the caller's company — because a writer must not lean
    // on a reader's guard to keep a foreign row out of the table.
    public sealed record ProjectMemberRow(
        int Id,
        int EmployeeId,
        string EmployeeName,
        string RoleOnProject,
        decimal? AllocationPct,
        DateTime JoinedAt,
        DateTime? LeftAt,
        bool IsActive);

    public interface IProjectMembershipService
    {
        Task<IReadOnlyList<ProjectMemberRow>> ListAsync(
            BusinessContext context, int projectId, CancellationToken cancellationToken = default);

        Task<(bool ok, string? error, int id)> AddAsync(
            BusinessContext context, int projectId, int employeeId, string role, decimal? allocationPct,
            CancellationToken cancellationToken = default);

        Task<(bool ok, string? error)> UpdateAsync(
            BusinessContext context, int projectId, int membershipId, string role, decimal? allocationPct,
            CancellationToken cancellationToken = default);

        Task<(bool ok, string? error)> EndAsync(
            BusinessContext context, int projectId, int membershipId,
            CancellationToken cancellationToken = default);
    }

    public sealed class ProjectMembershipService : IProjectMembershipService
    {
        // ONE refusal message for every reason a caller may not proceed: no context, no right, a project in
        // another company, a project that does not exist. They must be indistinguishable, or the screen
        // becomes a probe for which project ids exist in other companies.
        public const string Refused = "You do not have permission to perform this action";

        private readonly CrossDbContext _db;
        private readonly IProjectsAccessService _access;

        public ProjectMembershipService(CrossDbContext db, IProjectsAccessService access)
        {
            _db = db;
            _access = access;
        }

        public async Task<IReadOnlyList<ProjectMemberRow>> ListAsync(
            BusinessContext context, int projectId, CancellationToken cancellationToken = default)
        {
            // Reading the team needs only the right to read the project: an Observer may see who else is on it.
            if (!await AllowedAsync(context, projectId, ProjectsActions.Read, cancellationToken))
                return Array.Empty<ProjectMemberRow>();

            // History is included — an ended membership is a fact about who worked on this project, and the
            // screen shows it as ended rather than hiding it. Active first, then most recent departures.
            return await (from m in _db.ProjectMembers.AsNoTracking()
                          join e in _db.Employee.AsNoTracking() on m.EmployeeId equals e.ID
                          where m.ProjectId == projectId && m.CompanyID == context.CompanyId
                          orderby m.IsActive descending, m.JoinedAt descending, m.ID descending
                          select new ProjectMemberRow(
                              m.ID, m.EmployeeId, e.FullName ?? "", m.RoleOnProject,
                              m.AllocationPct, m.JoinedAt, m.LeftAt, m.IsActive))
                         .ToListAsync(cancellationToken);
        }

        public async Task<(bool ok, string? error, int id)> AddAsync(
            BusinessContext context, int projectId, int employeeId, string role, decimal? allocationPct,
            CancellationToken cancellationToken = default)
        {
            if (!await AllowedAsync(context, projectId, ProjectsActions.Manage, cancellationToken))
                return (false, Refused, 0);

            if (!ProjectMemberRoles.IsKnown(role))
                return (false, "Unknown project role", 0);
            if (allocationPct is < 0 or > 100)
                return (false, "Allocation must be between 0 and 100", 0);
            if (employeeId <= 0)
                return (false, "Select an employee", 0);

            // THE COMPANY INTERSECTION, and the reason it is not left to the CHECK constraints: the schema
            // bounds the columns but has no constraint tying a member's company to the project's, so nothing
            // in the database would stop a company-2 employee being written onto a company-1 project. The
            // employee's company comes from the EMPLOYEE ROW, never from the request.
            var employeeCompany = await _db.Employee.AsNoTracking()
                .Where(e => e.ID == employeeId)
                .Select(e => (int?)e.EmpCompanyID)
                .FirstOrDefaultAsync(cancellationToken);

            // A foreign employee and a non-existent one answer identically, for the same reason as projects.
            if (employeeCompany == null || employeeCompany.Value != context.CompanyId)
                return (false, Refused, 0);

            // Mirrors UX_ProjectMembers_ActiveMembership so the user reads a sentence instead of a unique-index
            // violation. The index stays the authority — this check only makes the common case civil.
            var alreadyOn = await _db.ProjectMembers.AsNoTracking().AnyAsync(
                m => m.ProjectId == projectId && m.EmployeeId == employeeId
                  && m.CompanyID == context.CompanyId && m.IsActive, cancellationToken);
            if (alreadyOn)
                return (false, "This employee is already on the project", 0);

            var now = DateTime.UtcNow;
            var row = new ProjectMember
            {
                CompanyID = context.CompanyId,
                ProjectId = projectId,
                EmployeeId = employeeId,
                RoleOnProject = role,
                AllocationPct = allocationPct,
                JoinedAt = now,
                IsActive = true,
                CreatedBy = context.EmployeeId,
                CreatedAt = now,
            };
            _db.ProjectMembers.Add(row);
            await _db.SaveChangesAsync(cancellationToken);
            return (true, null, row.ID);
        }

        public async Task<(bool ok, string? error)> UpdateAsync(
            BusinessContext context, int projectId, int membershipId, string role, decimal? allocationPct,
            CancellationToken cancellationToken = default)
        {
            if (!await AllowedAsync(context, projectId, ProjectsActions.Manage, cancellationToken))
                return (false, Refused);

            if (!ProjectMemberRoles.IsKnown(role))
                return (false, "Unknown project role");
            if (allocationPct is < 0 or > 100)
                return (false, "Allocation must be between 0 and 100");

            var row = await LoadForWriteAsync(context, projectId, membershipId, cancellationToken);
            if (row == null) return (false, Refused);

            // An ended membership is history. Re-roling it would silently rewrite the record of what somebody
            // did on this project; rejoining creates a NEW row, which is exactly what the filtered unique
            // index was designed to allow.
            if (!row.IsActive || row.LeftAt != null)
                return (false, "This membership has ended and cannot be changed");

            row.RoleOnProject = role;
            row.AllocationPct = allocationPct;
            row.UpdatedBy = context.EmployeeId;
            row.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync(cancellationToken);
            return (true, null);
        }

        public async Task<(bool ok, string? error)> EndAsync(
            BusinessContext context, int projectId, int membershipId,
            CancellationToken cancellationToken = default)
        {
            if (!await AllowedAsync(context, projectId, ProjectsActions.Manage, cancellationToken))
                return (false, Refused);

            var row = await LoadForWriteAsync(context, projectId, membershipId, cancellationToken);
            if (row == null) return (false, Refused);
            if (!row.IsActive && row.LeftAt != null) return (true, null);   // already ended — idempotent

            // REVERSED, NEVER DELETED — the convention this project follows everywhere, and the reason both
            // FKs are ON DELETE NO ACTION. Both columns are set: IsActive releases the filtered unique index
            // so the person may rejoin later, and LeftAt is what the access service actually reads.
            row.IsActive = false;
            row.LeftAt = DateTime.UtcNow;
            row.UpdatedBy = context.EmployeeId;
            row.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync(cancellationToken);
            return (true, null);
        }

        // Delegates to ProjectsAccessService — the ONLY place a projects rule is evaluated. The target carries
        // the project id, so that service resolves the project's own company from the project row.
        private async Task<bool> AllowedAsync(
            BusinessContext context, int projectId, string action, CancellationToken cancellationToken)
        {
            if (context == null || context.CompanyId <= 0 || context.EmployeeId is not > 0) return false;
            if (projectId <= 0) return false;
            return await _access.CanAsync(context, action, PermissionTarget.ForProject(projectId), cancellationToken);
        }

        // A membership is only writable through the project it belongs to: the row must match BOTH the project
        // named in the authorized request AND the caller's company. Without the ProjectId predicate, a caller
        // authorized on project 1 could pass a membership id belonging to project 2.
        private Task<ProjectMember?> LoadForWriteAsync(
            BusinessContext context, int projectId, int membershipId, CancellationToken cancellationToken)
            => _db.ProjectMembers.FirstOrDefaultAsync(
                m => m.ID == membershipId && m.ProjectId == projectId && m.CompanyID == context.CompanyId,
                cancellationToken);
    }
}
