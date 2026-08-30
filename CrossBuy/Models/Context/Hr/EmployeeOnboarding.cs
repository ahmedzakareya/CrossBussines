using CrossBuy.Models.Context.Admin;

namespace CrossBuy.Models.Context.Hr
{
    // ============================================================================================
    // EMPLOYEE ONBOARDING — the smallest model that answers one operational question:
    //
    //      "What remains before this employee is operationally ready?"
    //
    // Four tables, and the restraint is the design. It would have been easy to build a workflow
    // engine here — states, transitions, rules, conditions — and the brief warns against exactly
    // that. What HR needs is a list of things that must happen, who owns each one, when it is due,
    // and whether it is done. Anything beyond that is a second product nobody asked for.
    //
    // WHAT THIS DELIBERATELY DOES NOT HOLD:
    //
    //   · no document columns. A passport is not a column on an onboarding row — it is a
    //     PlatformDocument of a PlatformDocumentType, owned by the Central Document platform. An item
    //     names a REQUIRED TYPE and the platform answers whether a valid one exists. Putting
    //     PassportNumber here would fork the document model on day one.
    //   · no IsOverdue flag. Overdue is (due date is past) AND (not finished). A stored flag is a
    //     second answer that goes stale the moment midnight passes, and then two parts of the screen
    //     disagree. It is computed.
    //   · no percentage column. Same reason: it is a function of the items.
    //   · no actor names, only ids. Names change; the audit trail must not.
    // ============================================================================================

    public static class OnboardingStatus
    {
        public const string NotStarted = "NotStarted";
        public const string InProgress = "InProgress";
        public const string Completed = "Completed";
        public const string Cancelled = "Cancelled";

        public static readonly string[] All = { NotStarted, InProgress, Completed, Cancelled };
    }

    public static class OnboardingItemStatus
    {
        public const string Pending = "Pending";
        public const string InProgress = "InProgress";
        public const string Completed = "Completed";

        // WAIVED IS NOT "OPTIONAL". An optional item may simply never be done and nothing is owed.
        // A waiver is a decision a named person took, for a stated reason, at a recorded moment — it
        // is how a MANDATORY requirement is legitimately skipped, and it is the only such way.
        // Treating "optional" as "already waived" would erase the difference between a requirement
        // nobody needed and a requirement somebody excused.
        public const string Waived = "Waived";

        public static readonly string[] All = { Pending, InProgress, Completed, Waived };

        // The two states that stop an item from blocking readiness. Named once so "is this settled?"
        // has a single definition rather than an `|| item.Status == ...` repeated at five call sites.
        public static bool IsSettled(string? status) =>
            status == Completed || status == Waived;
    }

    // WHO OWES THE NEXT ACTION. Four kinds, not a role engine.
    //
    // Hr and Manager are RESOLVED, not stored: an item owned by "Manager" belongs to whoever manages
    // that employee today, through the org hierarchy the platform already computes. Storing a manager
    // id would freeze it, and the person who left in March would still own the item in June.
    // SpecificEmployee exists for the case the other three cannot express.
    public static class OnboardingResponsibility
    {
        public const string Hr = "Hr";
        public const string Manager = "Manager";
        public const string Employee = "Employee";
        public const string SpecificEmployee = "SpecificEmployee";

        public static readonly string[] All = { Hr, Manager, Employee, SpecificEmployee };
    }

    // ============================================================================================
    // THE PLAN — one per employee, per company.
    // ============================================================================================
    public class EmployeeOnboarding
    {
        public int ID { get; set; }

        // Both, and not one derived from the other. The company is the tenant boundary every query
        // filters on; deriving it through the employee join on every read would make the boundary a
        // property of the join rather than of the row. EmployeeService writes it from the employee's
        // own row, so the two cannot disagree.
        public int CompanyID { get; set; }
        public int EmployeeID { get; set; }
        public Employee? Employee { get; set; }

        // Which template produced this plan, when one did. Kept for provenance — "why does this
        // employee have these twelve items" is a real question — and deliberately NOT a live link:
        // editing the template later must not silently rewrite an in-flight plan.
        public int? TemplateID { get; set; }

        public string Status { get; set; } = OnboardingStatus.NotStarted;

        public DateTime? StartedAt { get; set; }

        // What HR promised. Overdue at the PLAN level is measured against this; overdue at the ITEM
        // level is measured against the item's own due date. They are different questions.
        public DateTime? TargetCompletionDate { get; set; }

        public DateTime? CompletedAt { get; set; }
        public int? CompletedBy { get; set; }

        public string? Notes { get; set; }

        public int? CreatedBy { get; set; }
        public DateTime? CreatedAt { get; set; }
        public int? UpdatedBy { get; set; }
        public DateTime? UpdatedAt { get; set; }

        public ICollection<EmployeeOnboardingItem> Items { get; set; } = new List<EmployeeOnboardingItem>();
    }

    // ============================================================================================
    // ONE REQUIREMENT.
    // ============================================================================================
    public class EmployeeOnboardingItem
    {
        public int ID { get; set; }

        // CompanyID is repeated here rather than reached through the parent. It is the column the
        // tenant predicate uses on a direct item query — and there are such queries, because "every
        // overdue item in this company" is a screen. A join-only boundary would make that query
        // silently unfiltered the first time somebody forgot the Include.
        public int CompanyID { get; set; }

        public int OnboardingID { get; set; }
        public EmployeeOnboarding? Onboarding { get; set; }

        // A stable machine key carried down from the template — "collect-civil-id", "issue-laptop".
        // It survives a title being reworded, which is what makes reporting across employees possible
        // and what lets a later batch recognise an item it already created.
        public string ItemKey { get; set; } = "";

        public string TitleAr { get; set; } = "";
        public string TitleEn { get; set; } = "";
        public string? DescriptionAr { get; set; }
        public string? DescriptionEn { get; set; }

        public string Responsibility { get; set; } = OnboardingResponsibility.Hr;

        // Set only when Responsibility is SpecificEmployee. The service refuses a value that is not an
        // employee of this company, so an id here has already been proved to belong.
        public int? ResponsibleEmployeeID { get; set; }

        public bool IsMandatory { get; set; } = true;
        public DateTime? DueDate { get; set; }
        public int SortOrder { get; set; }

        public string Status { get; set; } = OnboardingItemStatus.Pending;

        // ---- the document requirement ------------------------------------------------------------
        //
        // A TYPE ID, never a document id and never a file name. The item says "a valid Civil ID must
        // exist for this employee"; the Central Document platform answers whether one does. Storing a
        // document id would let the item go stale the moment the document was replaced — and
        // replacement is the normal case for an expiring document.
        public long? RequiredDocumentTypeID { get; set; }

        // ---- audit -------------------------------------------------------------------------------
        public DateTime? CompletedAt { get; set; }
        public int? CompletedBy { get; set; }

        // A waiver is three facts or it is not a waiver: who, why, when. The service refuses one with
        // an empty reason, so this is never null while Status is Waived.
        public DateTime? WaivedAt { get; set; }
        public int? WaivedBy { get; set; }
        public string? WaiverReason { get; set; }

        public string? Notes { get; set; }

        public int? CreatedBy { get; set; }
        public DateTime? CreatedAt { get; set; }
        public int? UpdatedBy { get; set; }
        public DateTime? UpdatedAt { get; set; }
    }

    // ============================================================================================
    // TEMPLATES — company-level, and only company-level.
    //
    // The brief allows one additional applicability dimension "if actual product evidence strongly
    // justifies it". It does not. Employee carries EmploymentType, DepartmentID, JobTitleID and
    // BranchID, so four dimensions are AVAILABLE — availability is not evidence. Nobody has yet run
    // onboarding once, so there is no observed case of two templates diverging by department. Adding
    // a matching dimension now would mean inventing the matching rules, the precedence between them
    // and the "no template matched" behaviour, all against zero usage.
    //
    // One template per company, chosen explicitly when a plan is created. When a second dimension is
    // genuinely needed it can be added without moving any data, because the plan already records
    // WHICH template produced it.
    // ============================================================================================
    public class OnboardingTemplate
    {
        public int ID { get; set; }
        public int CompanyID { get; set; }

        public string NameAr { get; set; } = "";
        public string NameEn { get; set; } = "";
        public bool IsActive { get; set; } = true;

        // The company's default, used when a plan is created without naming a template. At most one
        // per company — enforced by a filtered unique index rather than by application code, because
        // "at most one" checked in C# is a race.
        public bool IsDefault { get; set; }

        public int? DefaultDurationDays { get; set; }

        public int? CreatedBy { get; set; }
        public DateTime? CreatedAt { get; set; }
        public int? UpdatedBy { get; set; }
        public DateTime? UpdatedAt { get; set; }

        public ICollection<OnboardingTemplateItem> Items { get; set; } = new List<OnboardingTemplateItem>();
    }

    public class OnboardingTemplateItem
    {
        public int ID { get; set; }
        public int CompanyID { get; set; }
        public int TemplateID { get; set; }
        public OnboardingTemplate? Template { get; set; }

        public string ItemKey { get; set; } = "";
        public string TitleAr { get; set; } = "";
        public string TitleEn { get; set; } = "";
        public string? DescriptionAr { get; set; }
        public string? DescriptionEn { get; set; }

        public string Responsibility { get; set; } = OnboardingResponsibility.Hr;
        public bool IsMandatory { get; set; } = true;

        // Days from the employee's joining date, not an absolute date — a template outlives any one
        // hire. The plan turns it into a real date once, at creation, so a later template edit cannot
        // move a due date somebody is already working to.
        public int? DueOffsetDays { get; set; }

        public int SortOrder { get; set; }
        public long? RequiredDocumentTypeID { get; set; }

        public int? CreatedBy { get; set; }
        public DateTime? CreatedAt { get; set; }
        public int? UpdatedBy { get; set; }
        public DateTime? UpdatedAt { get; set; }
    }
}
