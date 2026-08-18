namespace CrossBuy.Models.Context.Accounting
{
    // Stage 1 Batch C — who is on a project.
    //
    // A BUSINESS RELATIONSHIP, not a permission: it has its own lifecycle (JoinedAt/LeftAt), its own
    // reporting meaning (who worked on this, at what allocation), and it belongs to the project rather than
    // to the module. That is why it is a module-specific table and not a row in PlatformRoleAssignments —
    // full reasoning in deploy/sql/project_members.sql and ADR-026.
    //
    // It exists because `Project` carries no manager, owner or member column, and no employee-to-project
    // relationship existed anywhere in the model (Batch C analysis §1.3). Without it, record-level project
    // access was not derivable from stored data.
    //
    // Lives in the Accounting namespace because `Project` does (Models/Context/Accounting/Dimensions.cs) —
    // following the file's own convention rather than introducing a Projects namespace for one class.
    public class ProjectMember
    {
        public int ID { get; set; }

        // Denormalised from Project on purpose: every access check filters by company FIRST, and joining to
        // Projects to learn the company would make the company predicate depend on the table being
        // authorized. The same-company invariant is enforced in ProjectsAccessService and by the script's CHECK.
        public int CompanyID { get; set; }

        public int ProjectId { get; set; }
        public int EmployeeId { get; set; }

        // ProjectMemberRoles value. Three, each with a distinct documented access level.
        public string RoleOnProject { get; set; } = ProjectMemberRoles.Member;

        // Optional planning figure, 0..100 (bounded by CHECK).
        public decimal? AllocationPct { get; set; }

        public DateTime JoinedAt { get; set; }
        public DateTime? LeftAt { get; set; }

        // Revocation without losing history. An inactive OR ended membership grants nothing.
        public bool IsActive { get; set; } = true;

        public int? CreatedBy { get; set; }
        public DateTime CreatedAt { get; set; }
        public int? UpdatedBy { get; set; }
        public DateTime? UpdatedAt { get; set; }
    }

    // The three supported roles. Mirrors CK_ProjectMembers_RoleOnProject.
    //
    // What NONE of them grants — enforced in ProjectsAccessService, restated here because a reader of the
    // model is exactly who would assume otherwise: budget-view, budget-manage, billing, company-wide project
    // administration, or any Accounting permission.
    public static class ProjectMemberRoles
    {
        // Full record access to THIS project, and may manage its membership. Not an administrator of other
        // projects — "manager of project 47" is not "manage" on the Projects module.
        public const string Manager = "Manager";

        // Record access to this project. No membership management, no financial access.
        public const string Member = "Member";

        // Read-only record access to this project.
        public const string Observer = "Observer";

        public static readonly IReadOnlyList<string> All = new[] { Manager, Member, Observer };

        public static bool IsKnown(string? role) => role != null && All.Contains(role, StringComparer.Ordinal);
    }
}
