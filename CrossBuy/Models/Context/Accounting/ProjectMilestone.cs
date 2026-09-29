using System.ComponentModel.DataAnnotations.Schema;

namespace CrossBuy.Models.Context.Accounting
{
    // =============================================================================================
    // PROJECTS-2 — WHAT the project must deliver, and WHEN.
    //
    // THE BOUNDARY THIS MODEL EXISTS TO HOLD. A milestone is a COMMITMENT: a named outcome with a
    // target date and a state. A task is WORK: an assignee, hours, escalation, hold, approval. They
    // answer different questions and the repository already has a good answer to the second one, so
    // nothing here carries an assignee, a due-date policy, an SLA, an escalation level, a hold flag
    // or an approval vocabulary. A milestone that grew those fields would be a second task engine
    // with none of TaskItem's machinery behind it.
    //
    // HOW WORK ATTACHES, when it does: through the link TaskItem already publishes -
    // EntityType/EntityId - by an explicit human action, never automatically. PROJECTS-2 does not
    // implement that action; see the seam note in ProjectMilestoneService.
    //
    // Lives in the Accounting namespace because `Project` does (Dimensions.cs), following
    // ProjectMember's precedent rather than introducing a Projects namespace for two classes.
    // =============================================================================================
    public class ProjectMilestone
    {
        public int ID { get; set; }

        // Denormalised from Project on purpose, and for exactly ProjectMember's reason: every access
        // check filters by company FIRST, and joining to Projects to learn the company would make the
        // company predicate depend on the table being authorized. The same-company invariant is
        // enforced by the script's CHECK and re-checked in the service.
        public int CompanyID { get; set; }

        public int ProjectId { get; set; }

        // Arabic primary with an English twin, which is the convention Project, BoqItem and every
        // other row in this tree already follow - Name/NameEn, Description/DescriptionEn.
        public string Name { get; set; } = "";
        public string? NameEn { get; set; }
        public string? Description { get; set; }
        public string? DescriptionEn { get; set; }

        // REQUIRED, and that is the definition rather than a constraint: a commitment with no date is
        // not a milestone, it is a wish. Deliverables may be undated; milestones may not.
        public DateTime TargetDate { get; set; }

        // ProjectMilestoneStatuses value. Mirrors CK_ProjectMilestones_Status.
        public string Status { get; set; } = ProjectMilestoneStatuses.Planned;

        // Author-chosen order within the project. Milestones are a SEQUENCE a human reads, and date
        // order is not always that sequence - two milestones can share a target date.
        public int SortOrder { get; set; }

        // Set when the milestone reaches Completed and cleared when it is reopened, so the row can
        // answer "when was this done" without reading an audit trail.
        public DateTime? CompletedAt { get; set; }
        public int? CompletedBy { get; set; }

        public int? CreatedBy { get; set; }
        public DateTime CreatedAt { get; set; }
        public int? UpdatedBy { get; set; }
        public DateTime? UpdatedAt { get; set; }

        // DERIVED, NEVER STORED - the same decision TaskItem records for its own Overdue ("Overdue is
        // DERIVED, never stored"). A stored flag would be wrong every midnight until something wrote
        // to it, and it would need a job to stay true. Cancelled is not overdue: nobody is waiting.
        [NotMapped]
        public bool IsOverdue => ProjectMilestoneStatuses.IsOpen(Status) && TargetDate.Date < DateTime.Today;
    }

    // =============================================================================================
    // A concrete thing handed over. Optionally grouped under a milestone.
    //
    // WHY MilestoneId IS NULLABLE, decided on evidence rather than taste: nothing in the repository
    // requires every deliverable to sit under a milestone, and BoqItem sets the local precedent for
    // an optional parent (ParentId null = a top-level item). A project that tracks deliverables but
    // not milestones is a real shape, and forcing a placeholder milestone to express it would put
    // fictitious commitments in the very list the owner reads to see what was promised.
    // =============================================================================================
    public class ProjectDeliverable
    {
        public int ID { get; set; }

        // Same denormalisation, same reason as above.
        public int CompanyID { get; set; }

        public int ProjectId { get; set; }

        // Null = a project-level deliverable. When set, the service proves the milestone belongs to
        // THIS project and THIS company before the row is written - a request cannot carry a
        // deliverable into another project's milestone by supplying its id.
        public int? MilestoneId { get; set; }

        public string Name { get; set; } = "";
        public string? NameEn { get; set; }
        public string? Description { get; set; }
        public string? DescriptionEn { get; set; }

        // OPTIONAL, unlike a milestone's TargetDate: a deliverable is frequently dated BY the
        // milestone it sits under, and a nullable column says "this one has no date of its own"
        // rather than inventing one that would then drift from its parent's.
        public DateTime? DueDate { get; set; }

        // ProjectDeliverableStatuses value. Mirrors CK_ProjectDeliverables_Status.
        public string Status { get; set; } = ProjectDeliverableStatuses.Pending;

        public int SortOrder { get; set; }

        public DateTime? CompletedAt { get; set; }
        public int? CompletedBy { get; set; }

        public int? CreatedBy { get; set; }
        public DateTime CreatedAt { get; set; }
        public int? UpdatedBy { get; set; }
        public DateTime? UpdatedAt { get; set; }

        [NotMapped]
        public bool IsOverdue => ProjectDeliverableStatuses.IsOpen(Status)
                                 && DueDate.HasValue && DueDate.Value.Date < DateTime.Today;
    }

    // =============================================================================================
    // THE MILESTONE STATES. Four, mirroring the vocabulary Project.Status already uses for the whole
    // project (Draft / Active / OnHold / Completed / Cancelled) rather than inventing a parallel one.
    //
    // OnHold IS DELIBERATELY ABSENT. Hold is a TASK concept in this repository, with its own
    // requested/approved lifecycle that TaskLifecycle owns; copying the word here would imply that
    // machinery exists for milestones, and it does not.
    // =============================================================================================
    public static class ProjectMilestoneStatuses
    {
        public const string Planned = "Planned";
        public const string InProgress = "InProgress";
        public const string Completed = "Completed";
        public const string Cancelled = "Cancelled";

        public static readonly IReadOnlyList<string> All = new[] { Planned, InProgress, Completed, Cancelled };

        public static bool IsKnown(string? s) => s != null && All.Contains(s, StringComparer.Ordinal);

        /// Still awaiting delivery. Drives IsOverdue and the "open milestones" count.
        public static bool IsOpen(string? s) => s == Planned || s == InProgress;

        // The legal moves. REOPEN IS EXPLICIT (Completed -> InProgress) because this repository already
        // treats reopening as a first-class act rather than an undo - ProjectCloseoutService has
        // ReopenAsync, and ProgressBilling has Returned. Cancelled is terminal: reinstating a cancelled
        // commitment is creating a new one, and saying so keeps the history honest.
        private static readonly Dictionary<string, string[]> LegalNext = new(StringComparer.Ordinal)
        {
            [Planned] = new[] { InProgress, Completed, Cancelled },
            [InProgress] = new[] { Completed, Cancelled, Planned },
            [Completed] = new[] { InProgress },
            [Cancelled] = Array.Empty<string>(),
        };

        public static bool CanMove(string? from, string to) =>
            from != null && IsKnown(to) && LegalNext.TryGetValue(from, out var next)
            && Array.IndexOf(next, to) >= 0;
    }

    // =============================================================================================
    // THE DELIVERABLE STATES, and the one that is NOT here.
    //
    // ACCEPTANCE IS NOT MODELLED, and that is measured rather than chosen: a search of the model and
    // the business layer for a customer-acceptance authority - accepted-by, client approval, customer
    // sign-off - returns nothing. The nearest thing the repository has is ProgressBilling's INTERNAL
    // approval, which is a company approving its own certificate, not a customer accepting work.
    //
    // So Delivered means "we handed it over", which is a fact this company can state. An Accepted
    // state would be a fact about the CUSTOMER that nothing in the system is entitled to record, and
    // a status nobody can legitimately set is worse than a status that does not exist. Customer
    // acceptance is recorded as future work, not implemented here.
    // =============================================================================================
    public static class ProjectDeliverableStatuses
    {
        public const string Pending = "Pending";
        public const string InProgress = "InProgress";
        public const string Delivered = "Delivered";
        public const string Cancelled = "Cancelled";

        public static readonly IReadOnlyList<string> All = new[] { Pending, InProgress, Delivered, Cancelled };

        public static bool IsKnown(string? s) => s != null && All.Contains(s, StringComparer.Ordinal);

        public static bool IsOpen(string? s) => s == Pending || s == InProgress;

        private static readonly Dictionary<string, string[]> LegalNext = new(StringComparer.Ordinal)
        {
            [Pending] = new[] { InProgress, Delivered, Cancelled },
            [InProgress] = new[] { Delivered, Cancelled, Pending },
            [Delivered] = new[] { InProgress },
            [Cancelled] = Array.Empty<string>(),
        };

        public static bool CanMove(string? from, string to) =>
            from != null && IsKnown(to) && LegalNext.TryGetValue(from, out var next)
            && Array.IndexOf(next, to) >= 0;
    }
}
