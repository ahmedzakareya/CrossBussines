using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Accounting;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL.Reporting
{
    // ============================================================================================
    // PROJECTS REPORTING SUBJECT PACK — FIRST WAVE (SHF-179).
    //
    // Two subjects on the EXISTING reporting platform. No second engine, no Projects designer, no
    // Projects Reports Center, no saved-report store of its own: these are ReportDatasetDefinition
    // values and IReportDataSource implementations, registered exactly the way Accounting, Inventory,
    // CRM, HR and Work already are, so they reach Report Studio, widgets, scheduling, export and
    // archiving with no new execution path anywhere.
    //
    //   projects.projects     the project register — code, name, status, activity window, location
    //   projects.milestones   the dated COMMITMENTS on those projects, and whether they are overdue
    //
    // WHY TWO AND NOT SIX. A subject earns its place by being a different GRAIN, not a different
    // question. "Projects by status", "active projects by location" and "projects started this
    // quarter" are all one grain — one row per project — and the platform's own Group By, filters and
    // measures answer all three from projects.projects. "How many milestones does each project have"
    // is likewise projects.milestones grouped by ProjectId, which is precisely what grouping is for.
    // Adding a dataset per question would multiply the places a company predicate has to be right,
    // and that predicate is the only thing about this file that is hard.
    //
    // ── WHAT IS DELIBERATELY ABSENT, AND WHY ─────────────────────────────────────────────────────
    //
    // MONEY. Budget, ContractValue, AdvancePercent and RetentionPercent are columns on Project and
    //   are exposed by NO field below. This is not squeamishness: the Projects module publishes
    //   `budget-view` and `budget-manage` as rights SEPARATE from `read`, which is the business
    //   saying in code that reading a project is not reading its money. Every field here is reachable
    //   with `projects.reports.view`, which maps to `read` — so publishing a budget column would hand
    //   contract values to every holder of a read grant and silently retire a right the module
    //   deliberately split out. A budget subject is a real future subject; it needs its own
    //   permission key, its own map entry in ProjectsReportPermissionAdapter and its own fields.
    //
    // COMPLETION PERCENTAGE, HEALTH, RISK, BUDGET VARIANCE, SLA. None of these exist as a business
    //   rule anywhere in this repository. Each would be a number invented in a reporting file,
    //   printed on a management screen and believed. Reporting is not where a business rule is born.
    //   Note in particular that "percent complete" has a real owner elsewhere — ProjectProgress and
    //   progress billing measure executed quantity against a BOQ — and a second, quietly different
    //   definition computed from milestone counts would disagree with the money.
    //
    // DELIVERABLES. ProjectDeliverable is a real entity and is NOT a subject here, by instruction and
    //   by grain: its parent is an OPTIONAL milestone, so a deliverable subject has to answer
    //   "visible through which ancestor" before it can answer anything else. That is a design
    //   question, not a wave-one omission.
    //
    // PROJECT MANAGER. There is no manager COLUMN on Project, and ProjectMembers cannot supply one:
    //   its unique index UX_ProjectMembers_ActiveMembership is on (ProjectId, EmployeeId), not on the
    //   role, so two active Managers on one project is a legal state. A singular "Manager" column
    //   would therefore have to pick one of them arbitrarily and would read as authoritative.
    //
    // CUSTOMER, ACTIVITY TYPE, COST CENTRE. All three are ids on Project whose NAMES live in other
    //   subject areas — Customer in receivables, CostCenter in accounting. Naming them means joining
    //   a source this pack does not own; publishing the bare integers instead would be a column
    //   nobody can read. Both are refused.
    // ============================================================================================

    // ==============================================================================================
    // RECONCILED ONTO THE FINAL LINE (RECONCILIATION-02). Transplanted from the programme line by
    // PATH, not by merge, and UNCHANGED - there was nothing in it to remove.
    //
    //   * It never defined ProjectsReportPermissions; it only REFERENCES the class, which the final
    //     line owns in WorkProjectsReportAuthority.cs beside the authority that maps it. So the
    //     reference below resolves to the authoritative definition with no edit and no duplicate.
    //
    //   * The donor also carried ProjectsReportPermissionAdapter.cs, which extended the obsolete
    //     ModuleActionReportPermissionAdapter. That file is NOT created here. projects.reports.view is
    //     answered by ProjectsReportModuleAuthority through IReportModuleAuthority +
    //     ReportModulePermissionAdapter, mapping to the same ProjectsActions.Read - and claiming the
    //     BROADER prefix "projects.", so an unmapped key under it is refused rather than delegated.
    //
    //   * ProjectsReportScope is verbatim and is the row-scope provider. The permission key
    //     authorizes NO rows on its own: Team carries PROJECT ids, and milestones reach rows only
    //     through the visible-project set.
    // ==============================================================================================
    public static class ProjectsDatasetCodes
    {
        // Lowercase dotted, matching every other pack on this platform. The data source Key is the
        // SAME string: ReportDataSourceRegistry resolves by DataSourceKey, and a dataset whose code
        // and source key drift apart is a subject that exists in the Studio and cannot be run.
        public const string Projects = "projects.projects";
        public const string Milestones = "projects.milestones";
    }

    // --------------------------------------------------------------------------------------------
    // SCOPE — WHICH PROJECTS, APPLIED BEFORE ANYTHING IS COUNTED.
    //
    // ProjectsReportPermissionAdapter answers whether this caller may open a Projects report at all.
    // It does NOT answer which rows: that is a BREADTH, and ProjectsAccessService.ResolveProjectScopeAsync
    // is the one place in the product that decides it. This type is the translation from that breadth
    // into a predicate, and nothing more — it re-derives no membership, reads no role name and makes
    // no policy decision of its own.
    //
    // WHY IT IS NOT ProjectListScope. The identical rule already exists as
    // CrossBuy.Controllers.ProjectListScope, and it is deliberately NOT called from here: it lives in
    // the MVC layer and BL must not take a dependency on Controllers. Duplication across a layer
    // boundary is a drift risk, so it is answered with a test rather than a comment —
    // ProjectsReportingTests drives BOTH implementations over the same AccessScope shapes and asserts
    // they agree, so the day someone loosens one, the other one's suite fails.
    //
    // ORDER IS THE WHOLE POINT. Company first, breadth second, and BOTH before Count, OrderBy, Take
    // or any aggregate. A filter applied after an aggregate has already published the number it was
    // supposed to withhold — a count is a leak just as surely as a row is.
    // --------------------------------------------------------------------------------------------
    public static class ProjectsReportScope
    {
        /// <param name="OnlyProjectIds">Null means NOT NARROWED — every project in the company. Never
        /// an empty set: a caller with nothing to see is REFUSED instead, because "narrow to nothing"
        /// and "do not narrow" are one line apart in code and opposite in effect.</param>
        public readonly record struct Decision(bool Allowed, IReadOnlyCollection<int>? OnlyProjectIds);

        public static Decision From(AccessScope? scope)
        {
            if (scope is null) return new Decision(false, null);

            switch (scope.Breadth)
            {
                // Company-wide project authority: every project in THIS company. CrossCompany is
                // treated the same here and is narrowed to the context's company anyway — see Apply.
                case AccessBreadth.Company:
                case AccessBreadth.CrossCompany:
                    return new Decision(true, null);

                // An ordinary member. ResolveProjectScopeAsync expresses this as Team with the PROJECT
                // ids carried in PrincipalIds, because for Projects the "principal" of a record is the
                // project the caller is attached to.
                case AccessBreadth.Team:
                    var ids = scope.PrincipalIds;
                    return ids is null || ids.Count == 0
                        ? new Decision(false, null)
                        : new Decision(true, ids);

                // None, Own and Branch. Own and Branch are breadths this resolver does not produce,
                // and they are REFUSED rather than guessed at — a breadth nobody designed this subject
                // for must not fall through to publishing the whole company.
                default:
                    return new Decision(false, null);
            }
        }

        /// Narrow a project query to what this scope may see. Returns NULL when the scope can see
        /// nothing at all, which callers turn into an empty result — never into an unfiltered one.
        public static IQueryable<Project>? Apply(IQueryable<Project> rows, AccessScope? scope, BusinessContext? context)
        {
            if (rows is null || context is null || context.CompanyId <= 0) return null;

            var decision = From(scope);
            if (!decision.Allowed) return null;

            // THE COMPANY PREDICATE IS UNCONDITIONAL, INCLUDING FOR CrossCompany BREADTH.
            //
            // A report run is scoped to ONE tenant — the one on the resolved BusinessContext — and
            // ReportDataQuery states that contract explicitly ("a data source MUST filter on
            // Context.CompanyId"). Cross-company breadth widens WHICH PROJECTS inside that tenant a
            // caller may see; it does not turn a report into a cross-tenant report. There is no path
            // through this method that returns rows without this line having run.
            //
            // This is also the ONLY tenancy defence these two tables have. Project and
            // ProjectMilestone are NOT among the entities CompanyQueryFilters covers, so there is no
            // global filter underneath to catch a mistake here — which is exactly why the isolation
            // tests over this file are non-vacuous without any cross-company bypass.
            rows = rows.Where(p => p.CompanyID == context.CompanyId);

            if (decision.OnlyProjectIds is { } only)
            {
                // Materialised before the expression so EF translates it to an IN list rather than
                // capturing the collection reference.
                var ids = only.ToList();
                rows = rows.Where(p => ids.Contains(p.ID));
            }

            return rows;
        }
    }

    public static class ProjectsDatasets
    {
        internal const int MaxRows = 20000;

        public static IEnumerable<ReportDatasetDefinition> All()
        {
            yield return Projects();
            yield return Milestones();
        }

        // ---- projects.projects -----------------------------------------------------------------
        public static ReportDatasetDefinition Projects() => new()
        {
            DatasetCode = ProjectsDatasetCodes.Projects,
            Module = "Projects",
            TitleAr = "سجل المشاريع",
            TitleEn = "Project register",
            DescriptionAr = "المشاريع كما هي: الكود والاسم والحالة وفترة التنفيذ والموقع. جمّع حسب الحالة "
                          + "أو الموقع للحصول على أعداد المشاريع. المشاريع الظاهرة هي ما تسمح به صلاحية "
                          + "المستخدم على وحدة المشاريع نفسها، لا قاعدة ثانية مكتوبة هنا. لا يتضمن هذا "
                          + "الموضوع أي قيمة مالية: الموازنة وقيمة العقد والمحتجز تحكمها صلاحية مستقلة.",
            DescriptionEn = "Projects as they stand: code, name, status, activity window and location. Group by "
                          + "status or location for project counts. Which projects appear is decided by the "
                          + "caller's authority in the Projects module itself, not by a second rule written "
                          + "here. Carries no monetary value: budget, contract value and retention are governed "
                          + "by a separate right.",
            DataSourceKey = ProjectsDatasetCodes.Projects,
            RequiredPermissionKey = ProjectsReportPermissions.View,
            MaxRows = MaxRows,
            RowCapPolicy = ReportRowCapPolicy.TruncateAndDeclare,
            AvailableInStudio = true,
            AvailableAsWidget = true,

            Fields = new[]
            {
                new ReportDatasetField
                {
                    Key = "ProjectId", TitleAr = "رقم المشروع", TitleEn = "Project no.",
                    Type = ReportFieldType.Integer, VisibleByDefault = false, WidthMm = 20,
                    SupportedOperators = new[] { ReportFilterOperator.Equals, ReportFilterOperator.In },
                    SupportedAggregates = new[] { ReportAggregate.Count, ReportAggregate.CountDistinct },
                },
                new ReportDatasetField
                {
                    Key = "Code", TitleAr = "الكود", TitleEn = "Code",
                    Groupable = true, WidthMm = 26,
                    SupportedOperators = new[] { ReportFilterOperator.Equals, ReportFilterOperator.Contains, ReportFilterOperator.StartsWith },
                    SupportedAggregates = new[] { ReportAggregate.Count, ReportAggregate.CountDistinct },
                },
                new ReportDatasetField
                {
                    // ARABIC PRIMARY WITH AN ENGLISH TWIN, which is how this row is stored and how every
                    // other bilingual field in the product is published. The two are separate COLUMNS
                    // rather than one value picked by culture, so an Arabic report and an English report
                    // are the same report with a different column visible — not two different datasets.
                    Key = "Name", TitleAr = "اسم المشروع", TitleEn = "Project name",
                    Groupable = true, WidthMm = 60,
                    SupportedOperators = new[] { ReportFilterOperator.Equals, ReportFilterOperator.Contains, ReportFilterOperator.StartsWith },
                    SupportedAggregates = new[] { ReportAggregate.CountDistinct },
                },
                new ReportDatasetField
                {
                    Key = "NameEn", TitleAr = "الاسم بالإنجليزية", TitleEn = "Name (English)",
                    VisibleByDefault = false, WidthMm = 60,
                    SupportedOperators = new[] { ReportFilterOperator.Equals, ReportFilterOperator.Contains, ReportFilterOperator.StartsWith },
                    SupportedAggregates = new[] { ReportAggregate.CountDistinct },
                },
                new ReportDatasetField
                {
                    // AS STORED. There is no ProjectStatuses vocabulary class in this repository — the
                    // column is a free string the screens fill with Draft / Active / OnHold / Completed
                    // / Cancelled — so this publishes the stored value and normalises nothing. Mapping
                    // it to a tidier set here would invent a vocabulary the product does not have and
                    // would hide rows that carry something else.
                    Key = "Status", TitleAr = "الحالة", TitleEn = "Status",
                    Groupable = true, WidthMm = 26,
                    SupportedOperators = new[] { ReportFilterOperator.Equals, ReportFilterOperator.In, ReportFilterOperator.IsNull, ReportFilterOperator.IsNotNull },
                    SupportedAggregates = new[] { ReportAggregate.Count },
                },
                new ReportDatasetField
                {
                    // A SEPARATE FACT FROM Status, and deliberately published alongside it: IsActive is
                    // the flag the lookups and pickers filter on, Status is what the contracting screens
                    // write. They can disagree, and a report that showed only one would quietly hide it.
                    Key = "IsActive", TitleAr = "نشط", TitleEn = "Active",
                    Type = ReportFieldType.Boolean, Groupable = true, WidthMm = 18,
                    SupportedOperators = new[] { ReportFilterOperator.Equals },
                    SupportedAggregates = new[] { ReportAggregate.Count },
                },
                new ReportDatasetField
                {
                    Key = "StartDate", TitleAr = "تاريخ البدء", TitleEn = "Start date",
                    Type = ReportFieldType.Date, Format = "yyyy-MM-dd", Groupable = true, WidthMm = 26,
                    SupportedOperators = new[] { ReportFilterOperator.Between, ReportFilterOperator.GreaterOrEqual, ReportFilterOperator.LessOrEqual, ReportFilterOperator.IsNull, ReportFilterOperator.IsNotNull },
                    SupportedAggregates = new[] { ReportAggregate.Min, ReportAggregate.Max, ReportAggregate.Count },
                },
                new ReportDatasetField
                {
                    Key = "EndDate", TitleAr = "تاريخ الانتهاء", TitleEn = "End date",
                    Type = ReportFieldType.Date, Format = "yyyy-MM-dd", Groupable = true, WidthMm = 26,
                    SupportedOperators = new[] { ReportFilterOperator.Between, ReportFilterOperator.GreaterOrEqual, ReportFilterOperator.LessOrEqual, ReportFilterOperator.IsNull, ReportFilterOperator.IsNotNull },
                    SupportedAggregates = new[] { ReportAggregate.Min, ReportAggregate.Max, ReportAggregate.Count },
                },
                new ReportDatasetField
                {
                    Key = "Location", TitleAr = "الموقع", TitleEn = "Location",
                    Groupable = true, WidthMm = 40,
                    SupportedOperators = new[] { ReportFilterOperator.Equals, ReportFilterOperator.Contains, ReportFilterOperator.IsNull, ReportFilterOperator.IsNotNull },
                    SupportedAggregates = new[] { ReportAggregate.CountDistinct },
                },
                new ReportDatasetField
                {
                    Key = "CreatedAt", TitleAr = "تاريخ الإنشاء", TitleEn = "Created",
                    Type = ReportFieldType.DateTime, Format = "yyyy-MM-dd HH:mm", Groupable = true, WidthMm = 32,
                    SupportedOperators = new[] { ReportFilterOperator.Between, ReportFilterOperator.GreaterOrEqual, ReportFilterOperator.LessOrEqual, ReportFilterOperator.IsNull, ReportFilterOperator.IsNotNull },
                    SupportedAggregates = new[] { ReportAggregate.Min, ReportAggregate.Max, ReportAggregate.Count },
                },
            },

            Parameters = new[]
            {
                // BOTH BOUND StartDate, and both are optional. A project whose StartDate is NULL is in
                // NO date window — stated here rather than discovered, because "undated projects
                // vanished when I set a date" is otherwise a support call. Leave the parameters empty
                // and every visible project is returned, undated ones included.
                new ReportParameterDescriptor
                {
                    Key = "FromDate", TitleAr = "من تاريخ البدء", TitleEn = "Start date from",
                    Type = ReportFieldType.Date, Required = false,
                },
                new ReportParameterDescriptor
                {
                    Key = "ToDate", TitleAr = "إلى تاريخ البدء", TitleEn = "Start date to",
                    Type = ReportFieldType.Date, Required = false,
                },
            },
        };

        // ---- projects.milestones ---------------------------------------------------------------
        public static ReportDatasetDefinition Milestones() => new()
        {
            DatasetCode = ProjectsDatasetCodes.Milestones,
            Module = "Projects",
            TitleAr = "التزامات المشاريع (المراحل)",
            TitleEn = "Project milestones",
            DescriptionAr = "المراحل كما التُزم بها: المشروع والاسم والتاريخ المستهدف والحالة وتاريخ الإنجاز، "
                          + "والمتأخر منها محسوب بقاعدة المرحلة المعتمدة نفسها وليس بقاعدة ثانية مكتوبة هنا. "
                          + "جمّع حسب المشروع أو الحالة لمعرفة عدد الالتزامات المفتوحة. المرحلة تظهر فقط إذا "
                          + "كان مشروعها ظاهرًا للمستخدم.",
            DescriptionEn = "Milestones as they were committed: project, name, target date, status and completion "
                          + "date. Overdue is the canonical milestone rule, not a second copy. Group by project "
                          + "or status for open-commitment counts. A milestone is visible only when its parent "
                          + "project is.",
            DataSourceKey = ProjectsDatasetCodes.Milestones,
            RequiredPermissionKey = ProjectsReportPermissions.View,
            MaxRows = MaxRows,
            RowCapPolicy = ReportRowCapPolicy.TruncateAndDeclare,
            AvailableInStudio = true,
            AvailableAsWidget = true,

            Fields = new[]
            {
                new ReportDatasetField
                {
                    Key = "MilestoneId", TitleAr = "رقم المرحلة", TitleEn = "Milestone no.",
                    Type = ReportFieldType.Integer, VisibleByDefault = false, WidthMm = 20,
                    SupportedOperators = new[] { ReportFilterOperator.Equals, ReportFilterOperator.In },
                    SupportedAggregates = new[] { ReportAggregate.Count, ReportAggregate.CountDistinct },
                },
                new ReportDatasetField
                {
                    Key = "ProjectId", TitleAr = "رقم المشروع", TitleEn = "Project no.",
                    Type = ReportFieldType.Integer, Groupable = true, VisibleByDefault = false, WidthMm = 20,
                    SupportedOperators = new[] { ReportFilterOperator.Equals, ReportFilterOperator.In },
                    SupportedAggregates = new[] { ReportAggregate.CountDistinct },
                },
                new ReportDatasetField
                {
                    Key = "ProjectCode", TitleAr = "كود المشروع", TitleEn = "Project code",
                    Groupable = true, WidthMm = 26,
                    SupportedOperators = new[] { ReportFilterOperator.Equals, ReportFilterOperator.Contains, ReportFilterOperator.StartsWith },
                    SupportedAggregates = new[] { ReportAggregate.CountDistinct },
                },
                new ReportDatasetField
                {
                    Key = "ProjectName", TitleAr = "المشروع", TitleEn = "Project",
                    Groupable = true, WidthMm = 50,
                    SupportedOperators = new[] { ReportFilterOperator.Equals, ReportFilterOperator.Contains },
                    SupportedAggregates = new[] { ReportAggregate.CountDistinct },
                },
                new ReportDatasetField
                {
                    Key = "Name", TitleAr = "المرحلة", TitleEn = "Milestone",
                    WidthMm = 60,
                    SupportedOperators = new[] { ReportFilterOperator.Equals, ReportFilterOperator.Contains, ReportFilterOperator.StartsWith },
                    SupportedAggregates = new[] { ReportAggregate.Count },
                },
                new ReportDatasetField
                {
                    Key = "NameEn", TitleAr = "المرحلة بالإنجليزية", TitleEn = "Milestone (English)",
                    VisibleByDefault = false, WidthMm = 60,
                    SupportedOperators = new[] { ReportFilterOperator.Equals, ReportFilterOperator.Contains, ReportFilterOperator.StartsWith },
                    SupportedAggregates = new[] { ReportAggregate.Count },
                },
                new ReportDatasetField
                {
                    // ProjectMilestoneStatuses: Planned / InProgress / Completed / Cancelled. A real
                    // vocabulary this time, owned by the model and re-stated nowhere.
                    Key = "Status", TitleAr = "الحالة", TitleEn = "Status",
                    Groupable = true, WidthMm = 24,
                    SupportedOperators = new[] { ReportFilterOperator.Equals, ReportFilterOperator.In },
                    SupportedAggregates = new[] { ReportAggregate.Count },
                },
                new ReportDatasetField
                {
                    // DERIVED, NEVER STORED, and ASKED rather than restated: ProjectMilestone.IsOverdue
                    // is the only definition of this in the product. See the data source for why the
                    // entity is materialised instead of the expression being copied into a projection.
                    //
                    // Cancelled is NOT overdue, and Completed is not either — nobody is waiting on
                    // either one. That is the model's rule, inherited whole.
                    Key = "IsOverdue", TitleAr = "متأخرة", TitleEn = "Overdue",
                    Type = ReportFieldType.Boolean, Groupable = true, WidthMm = 20,
                    SupportedOperators = new[] { ReportFilterOperator.Equals },
                    SupportedAggregates = new[] { ReportAggregate.Count },
                },
                new ReportDatasetField
                {
                    // "Still awaiting delivery" — Planned or InProgress. Published because it is the
                    // count the owner actually asks for, and because deriving it in the Studio would
                    // mean a user hand-writing a status list that the model already owns.
                    Key = "IsOpen", TitleAr = "مفتوحة", TitleEn = "Open",
                    Type = ReportFieldType.Boolean, Groupable = true, WidthMm = 18,
                    SupportedOperators = new[] { ReportFilterOperator.Equals },
                    SupportedAggregates = new[] { ReportAggregate.Count },
                },
                new ReportDatasetField
                {
                    Key = "TargetDate", TitleAr = "التاريخ المستهدف", TitleEn = "Target date",
                    Type = ReportFieldType.Date, Format = "yyyy-MM-dd", Groupable = true, WidthMm = 28,
                    SupportedOperators = new[] { ReportFilterOperator.Between, ReportFilterOperator.GreaterOrEqual, ReportFilterOperator.LessOrEqual },
                    SupportedAggregates = new[] { ReportAggregate.Min, ReportAggregate.Max, ReportAggregate.Count },
                },
                new ReportDatasetField
                {
                    Key = "CompletedAt", TitleAr = "تاريخ الإنجاز", TitleEn = "Completed",
                    Type = ReportFieldType.DateTime, Format = "yyyy-MM-dd HH:mm", Groupable = true, WidthMm = 32,
                    SupportedOperators = new[] { ReportFilterOperator.Between, ReportFilterOperator.IsNull, ReportFilterOperator.IsNotNull },
                    SupportedAggregates = new[] { ReportAggregate.Min, ReportAggregate.Max, ReportAggregate.Count },
                },
                new ReportDatasetField
                {
                    // The author-chosen sequence within the project. Hidden by default — it is how the
                    // rows are ORDERED, rarely something a reader wants a column of — but published so
                    // a layout can show a numbered plan.
                    Key = "SortOrder", TitleAr = "الترتيب", TitleEn = "Order",
                    Type = ReportFieldType.Integer, VisibleByDefault = false, WidthMm = 18,
                    SupportedOperators = new[] { ReportFilterOperator.Equals, ReportFilterOperator.In },
                    SupportedAggregates = new[] { ReportAggregate.Min, ReportAggregate.Max },
                },
                new ReportDatasetField
                {
                    Key = "CreatedAt", TitleAr = "تاريخ الإنشاء", TitleEn = "Created",
                    Type = ReportFieldType.DateTime, Format = "yyyy-MM-dd HH:mm", Groupable = true,
                    VisibleByDefault = false, WidthMm = 32,
                    SupportedOperators = new[] { ReportFilterOperator.Between, ReportFilterOperator.GreaterOrEqual, ReportFilterOperator.LessOrEqual },
                    SupportedAggregates = new[] { ReportAggregate.Min, ReportAggregate.Max, ReportAggregate.Count },
                },
            },

            Parameters = new[]
            {
                // Both bound TargetDate, which is REQUIRED on a milestone — a commitment with no date
                // is not a milestone. So unlike the project register there is no undated row to lose.
                new ReportParameterDescriptor
                {
                    Key = "FromDate", TitleAr = "من التاريخ المستهدف", TitleEn = "Target date from",
                    Type = ReportFieldType.Date, Required = false,
                },
                new ReportParameterDescriptor
                {
                    Key = "ToDate", TitleAr = "إلى التاريخ المستهدف", TitleEn = "Target date to",
                    Type = ReportFieldType.Date, Required = false,
                },
            },
        };
    }

    // ============================================================================================
    // projects.projects — THE PROJECT REGISTER.
    //
    // The order of operations below is the security contract, and it is written top to bottom on
    // purpose so a reviewer can read it as a sequence:
    //
    //   1. FAIL CLOSED on an unresolved tenant. No company means NO ROWS — never every company's.
    //   2. ASK THE MODULE for the breadth, on the CURRENT context, so a saved template carries no
    //      authority from the run that created it or from the person who saved it.
    //   3. NARROW by company, then by the allowed project ids.
    //   4. ONLY THEN count, order, cap and project.
    //
    // Steps 3 and 4 are not interchangeable. Count runs over the narrowed query, so the total this
    // source declares is the caller's total — not the company's total with the caller's page.
    // ============================================================================================
    public sealed class ProjectsDataSource : IReportDataSource
    {
        private readonly CrossDbContext _db;
        private readonly IProjectsAccessService _projects;

        public ProjectsDataSource(CrossDbContext db, IProjectsAccessService projects)
        {
            _db = db; _projects = projects;
        }

        public string Key => ProjectsDatasetCodes.Projects;

        public async Task<ReportDataSet> FetchAsync(ReportDataQuery query, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(query);
            var context = query.Context;
            var columns = query.RequestedColumns.Count > 0 ? query.RequestedColumns : query.Definition.Columns;
            var builder = new ReportDataSetBuilder(columns);

            // 1 — FAIL CLOSED. An unresolved company is the one case where "return nothing" and
            // "return everything" are a single missing predicate apart.
            if (context is null || context.CompanyId <= 0) return builder.Build(totalRowCount: 0);

            // 2 — THE MODULE DECIDES THE BREADTH, asked with ProjectsActions.Read on this context.
            var scope = await _projects.ResolveProjectScopeAsync(context, ProjectsActions.Read, cancellationToken);

            // 3 — COMPANY, THEN THE ALLOWED IDS. Null means the scope can see nothing at all.
            var rows = ProjectsReportScope.Apply(_db.Projects.AsNoTracking(), scope, context);
            if (rows is null) return builder.Build(totalRowCount: 0);

            var from = query.Parameters.GetDate("FromDate");
            var to = query.Parameters.GetDate("ToDate");
            if (from is not null) rows = rows.Where(p => p.StartDate != null && p.StartDate >= from.Value.Date);
            if (to is not null) rows = rows.Where(p => p.StartDate != null && p.StartDate < to.Value.Date.AddDays(1));

            // 4 — AND ONLY NOW. The count is of the NARROWED set, so it is this caller's total.
            int total = await rows.CountAsync(cancellationToken);
            var cap = query.MaxRows > 0 ? query.MaxRows : ProjectsDatasets.MaxRows;

            // A DETERMINISTIC ORDER BEFORE Take, with ID as the tiebreaker. Without one the cap would
            // return an arbitrary page and two runs of the same report could disagree.
            var page = await rows
                .OrderBy(p => p.Code).ThenBy(p => p.ID)
                .Take(cap)
                .Select(p => new
                {
                    p.ID, p.Code, p.Name, p.NameEn, p.Status, p.IsActive,
                    p.StartDate, p.EndDate, p.Location, p.CreatedAt,
                })
                .ToListAsync(cancellationToken);

            foreach (var p in page)
            {
                // KEYED, not positional. The platform may hand a SUBSET of the definition's columns in
                // RequestedColumns, and a positional AddRow would then write every value into the wrong
                // column — silently, and with the right row count. The keyed overload ignores unknown
                // keys and leaves missing ones null, which is the correct behaviour in both directions.
                builder.AddRow(new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["ProjectId"] = p.ID,
                    ["Code"] = p.Code,
                    ["Name"] = p.Name,
                    ["NameEn"] = p.NameEn,
                    ["Status"] = p.Status,
                    ["IsActive"] = p.IsActive,
                    ["StartDate"] = p.StartDate,
                    ["EndDate"] = p.EndDate,
                    ["Location"] = p.Location,
                    ["CreatedAt"] = p.CreatedAt,
                });
            }

            // TRUNCATION IS DECLARED, not swallowed: RowCapPolicy is TruncateAndDeclare, and the engine
            // turns this flag into a warning on the run summary. A report that silently stopped at the
            // cap would read as a complete answer.
            return builder.Build(truncated: total > page.Count, totalRowCount: total);
        }
    }

    // ============================================================================================
    // projects.milestones — THE DATED COMMITMENTS.
    //
    // VISIBILITY IS INHERITED FROM THE PARENT PROJECT, and it is inherited by a SEMI-JOIN against the
    // already-scoped project query rather than by re-applying the rule to the milestone table.
    //
    // That choice is deliberate and it is stronger than the obvious alternative. ProjectMilestone
    // carries a denormalised CompanyID, so filtering `m.CompanyID == context.CompanyId` looks like
    // enough — but it would make milestone tenancy depend on a column being right, rather than on the
    // project actually being visible. A milestone whose CompanyID says 1 while its project belongs to
    // company 2 would pass that test and leak. Reaching the rows only through the scoped project query
    // means a milestone is visible if and only if its project is, which is the rule as stated.
    //
    // The direct company predicate is kept as well, one line above. It is defence in depth and it is
    // NOT the load-bearing one — said plainly here so a later edit does not remove the semi-join on
    // the grounds that "the company is already filtered".
    // ============================================================================================
    public sealed class ProjectMilestonesDataSource : IReportDataSource
    {
        private readonly CrossDbContext _db;
        private readonly IProjectsAccessService _projects;

        public ProjectMilestonesDataSource(CrossDbContext db, IProjectsAccessService projects)
        {
            _db = db; _projects = projects;
        }

        public string Key => ProjectsDatasetCodes.Milestones;

        public async Task<ReportDataSet> FetchAsync(ReportDataQuery query, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(query);
            var context = query.Context;
            var columns = query.RequestedColumns.Count > 0 ? query.RequestedColumns : query.Definition.Columns;
            var builder = new ReportDataSetBuilder(columns);

            // DEFENCE IN DEPTH, AND MEASURED AS SUCH. Weakening this one guard alone changes no
            // observable behaviour - ProjectsReportScope.Apply refuses the same condition one layer
            // down, and the milestone company predicate below refuses it a second time. That was
            // established by mutation rather than assumed, and it is written here so the next reader
            // does not delete a guard on the grounds that nothing fails when they do. Removing ALL
            // THREE together does leak an unresolved tenant's rows, which is the point of having them.
            if (context is null || context.CompanyId <= 0) return builder.Build(totalRowCount: 0);

            // THE SAME QUESTION, TO THE SAME AUTHORITY. A milestone subject does not get a milestone
            // permission: there is no such right in the Projects module, and inventing one here would
            // create a population of users nobody granted anything to.
            var scope = await _projects.ResolveProjectScopeAsync(context, ProjectsActions.Read, cancellationToken);

            var visibleProjects = ProjectsReportScope.Apply(_db.Projects.AsNoTracking(), scope, context);
            if (visibleProjects is null) return builder.Build(totalRowCount: 0);

            var rows = _db.ProjectMilestones.AsNoTracking()
                // Defence in depth — see the header. NOT the load-bearing predicate.
                .Where(m => m.CompanyID == context.CompanyId)
                // THE LOAD-BEARING ONE: visible if and only if the parent project is visible. Translates
                // to EXISTS over the scoped project query, so the company predicate and the allowed-id
                // predicate both reach the milestone row through it.
                .Where(m => visibleProjects.Any(p => p.ID == m.ProjectId));

            var from = query.Parameters.GetDate("FromDate");
            var to = query.Parameters.GetDate("ToDate");
            if (from is not null) rows = rows.Where(m => m.TargetDate >= from.Value.Date);
            if (to is not null) rows = rows.Where(m => m.TargetDate < to.Value.Date.AddDays(1));

            int total = await rows.CountAsync(cancellationToken);
            var cap = query.MaxRows > 0 ? query.MaxRows : ProjectsDatasets.MaxRows;

            // THE ENTITY IS MATERIALISED, NOT PROJECTED TO AN ANONYMOUS SHAPE, and that is the whole
            // reason IsOverdue can be ASKED instead of restated. It is a [NotMapped] property, so it
            // cannot be translated to SQL; the alternatives were to copy its expression into a
            // projection — a second definition of "overdue" in the product, which is exactly what this
            // pack refuses — or to build a partial instance and read the property off that, which
            // reads correctly today and returns a default the moment the rule depends on a third
            // field. Materialising the row costs a few columns and cannot go quietly wrong.
            //
            // AFTER Take, ALWAYS. The cap is applied in the database; IsOverdue is evaluated on the
            // page that came back.
            var page = await rows
                .OrderBy(m => m.ProjectId).ThenBy(m => m.SortOrder).ThenBy(m => m.TargetDate).ThenBy(m => m.ID)
                .Take(cap)
                .ToListAsync(cancellationToken);

            // ONE ROUND TRIP FOR THE WHOLE PAGE, through the ALREADY-SCOPED project query — never a
            // fresh _db.Projects. Reading the table raw here would work, because these ids came out of
            // the scoped query a moment ago, but it would break the invariant that this file reaches
            // Projects only through ProjectsReportScope — and that invariant is what a future edit
            // gets measured against, not the reasoning that made one exception safe today.
            var pageProjectIds = page.Select(m => m.ProjectId).Distinct().ToList();
            var projects = pageProjectIds.Count == 0
                ? new Dictionary<int, (string Code, string Name)>()
                : (await visibleProjects
                        .Where(p => pageProjectIds.Contains(p.ID))
                        .Select(p => new { p.ID, p.Code, p.Name })
                        .ToListAsync(cancellationToken))
                    .ToDictionary(p => p.ID, p => (p.Code, p.Name));

            foreach (var m in page)
            {
                projects.TryGetValue(m.ProjectId, out var project);

                builder.AddRow(new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["MilestoneId"] = m.ID,
                    ["ProjectId"] = m.ProjectId,
                    ["ProjectCode"] = project.Code,
                    ["ProjectName"] = project.Name,
                    ["Name"] = m.Name,
                    ["NameEn"] = m.NameEn,
                    ["Status"] = m.Status,
                    // ASKED, NOT RESTATED — both of them.
                    ["IsOverdue"] = m.IsOverdue,
                    ["IsOpen"] = ProjectMilestoneStatuses.IsOpen(m.Status),
                    ["TargetDate"] = m.TargetDate,
                    ["CompletedAt"] = m.CompletedAt,
                    ["SortOrder"] = m.SortOrder,
                    ["CreatedAt"] = m.CreatedAt,
                });
            }

            return builder.Build(truncated: total > page.Count, totalRowCount: total);
        }
    }
}
