using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Tasks;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL.Reporting
{
    // ============================================================================================
    // WORK REPORTING — the task register and the approval backlog.
    //
    // TWO SUBJECTS. work.tasks is the work as it stands; work.approvals is the work waiting on a
    // decision. work.calendar is excluded by the commissioning wave; work.hours and work.escalations
    // are outside its scope. None of the three is declared here, so nothing can register a subject
    // this file does not publish.
    //
    // ---- THE READ PATH IS THE AUTHORITATIVE ONE, AND THAT IS THE POINT ------------------------
    //
    // Every query starts from ITaskReadAuthorizer. TaskReadAuthorizer exists because a generic task
    // list once read `TaskItems.Where(t => t.CompanyId == companyId)` and called it a scope — and
    // company is a TENANCY boundary, not an authorization one. A report repeating that would hand any
    // signed-in employee every title, assignee and linked-record id in the company.
    //
    //   layer 1  task scope          breadth: own, team, or a genuinely company-wide role
    //   layer 2  source eligibility  INTERSECTED onto layer 1, so it can only REMOVE rows
    //   layer 3  source entrance     UNIONed for work a module admits the caller to without breadth,
    //                                and re-restricted by layer 2, so it is never a way around it
    //
    // Concretely: a generic task viewer with company breadth still cannot read protected approval
    // work, because layer 2 asks the owning module; and a billing PREPARER cannot reach their own
    // submitted billing, because the billing provider answers with the approvals pending FOR THIS
    // CALLER and a preparer is not their own approver.
    //
    // PROJECT-LINKED WORK IS NARROWED SEPARATELY, and must be: there is no ITaskSourceVisibility
    // provider for Project, so layer 2 does not cover it. ResolveProjectScopeAsync is asked before any
    // count, exactly as the committed pack did.
    //
    // AUTHORIZATION COMES FIRST — before the approval predicate, the date window, the count, the
    // ordering, the page and the aggregates — so the list, its total and its groups describe one set.
    //
    // THE APPROVAL VOCABULARY IS ASKED, NEVER COPIED. TaskApprovalProjection decides which
    // orchestration rules are approvals across its five flows, so Inventory and ProjectBilling work
    // stay eligible without being named here.
    // ============================================================================================
    public static class WorkDatasetCodes
    {
        public const string Tasks = "work.tasks";
        public const string Approvals = "work.approvals";
    }

    public static class WorkReportingDatasets
    {
        internal const int MaxRows = 20000;

        public static IEnumerable<ReportDatasetDefinition> All()
        {
            yield return Tasks();
            yield return Approvals();
        }

        // ---- work.tasks ------------------------------------------------------------------------
        public static ReportDatasetDefinition Tasks() => new()
        {
            DatasetCode = WorkDatasetCodes.Tasks,
            Module = "Work",
            TitleAr = "سجل المهام",
            TitleEn = "Task register",
            DescriptionAr = "المهام كما هي: الحالة والأولوية والمكلَّف والإدارة والمشروع المرتبط وتواريخ "
                          + "الإنشاء والاستحقاق والإنجاز. جمّع حسب الحالة أو المكلَّف أو الإدارة للحصول على "
                          + "أعداد المهام، والمتأخر منها محسوب بقاعدة دورة حياة المهمة المعتمدة وليس بقاعدة "
                          + "ثانية مكتوبة هنا.",
            DescriptionEn = "Tasks as they stand: status, priority, assignee, department, linked project and "
                          + "the created, due and completed dates. Group by status, assignee or department for "
                          + "task counts. Overdue is the canonical task-lifecycle rule, not a second copy.",
            DataSourceKey = WorkDatasetCodes.Tasks,
            RequiredPermissionKey = WorkReportPermissions.View,
            MaxRows = MaxRows,
            RowCapPolicy = ReportRowCapPolicy.TruncateAndDeclare,
            AvailableInStudio = true,
            AvailableAsWidget = true,

            Fields = new[]
            {
                new ReportDatasetField
                {
                    Key = "TaskId", TitleAr = "رقم المهمة", TitleEn = "Task no.",
                    Type = ReportFieldType.Integer, VisibleByDefault = false, WidthMm = 20,
                    SupportedOperators = new[] { ReportFilterOperator.Equals, ReportFilterOperator.In },
                    SupportedAggregates = new[] { ReportAggregate.Count, ReportAggregate.CountDistinct },
                },
                new ReportDatasetField
                {
                    Key = "Title", TitleAr = "المهمة", TitleEn = "Task", WidthMm = 60,
                    SupportedOperators = new[] { ReportFilterOperator.Contains, ReportFilterOperator.StartsWith },
                    SupportedAggregates = new[] { ReportAggregate.Count },
                },
                new ReportDatasetField
                {
                    Key = "Status", TitleAr = "الحالة", TitleEn = "Status",
                    Groupable = true, WidthMm = 24,
                    SupportedOperators = new[] { ReportFilterOperator.Equals, ReportFilterOperator.In },
                    SupportedAggregates = new[] { ReportAggregate.Count },
                },
                new ReportDatasetField
                {
                    // DERIVED, never stored. TaskStatuses.IsOverdue is the only definition of this in the
                    // product and this column asks it rather than restating "due < now and not Done".
                    Key = "IsOverdue", TitleAr = "متأخرة", TitleEn = "Overdue",
                    Type = ReportFieldType.Boolean, Groupable = true, WidthMm = 20,
                    SupportedOperators = new[] { ReportFilterOperator.Equals },
                    SupportedAggregates = new[] { ReportAggregate.Count },
                },
                new ReportDatasetField
                {
                    // ASKED OF TaskApprovalProjection, the one vocabulary that decides which orchestration
                    // rules are approvals. Reporting holds no rule-key prefix of its own, so the five
                    // flows cannot drift apart between a screen and a report.
                    Key = "IsWaitingApproval", TitleAr = "بانتظار الموافقة", TitleEn = "Waiting approval",
                    Type = ReportFieldType.Boolean, Groupable = true, WidthMm = 24,
                    SupportedOperators = new[] { ReportFilterOperator.Equals },
                    SupportedAggregates = new[] { ReportAggregate.Count },
                },
                new ReportDatasetField
                {
                    // APPROVED, NOT MERELY REQUESTED. TaskLifecycle draws that line: a pending request
                    // pauses nothing and the SLA keeps running, so counting it as held would report a
                    // stopped clock that is still ticking.
                    Key = "IsOnHold", TitleAr = "معلّقة", TitleEn = "On hold",
                    Type = ReportFieldType.Boolean, Groupable = true, WidthMm = 20,
                    SupportedOperators = new[] { ReportFilterOperator.Equals },
                    SupportedAggregates = new[] { ReportAggregate.Count },
                },
                new ReportDatasetField
                {
                    Key = "Priority", TitleAr = "الأولوية", TitleEn = "Priority",
                    Groupable = true, WidthMm = 24,
                    SupportedOperators = new[] { ReportFilterOperator.Equals, ReportFilterOperator.In },
                    SupportedAggregates = new[] { ReportAggregate.Count },
                },
                new ReportDatasetField
                {
                    Key = "AssigneeName", TitleAr = "المكلَّف", TitleEn = "Assignee",
                    Groupable = true, WidthMm = 46,
                    SupportedOperators = new[] { ReportFilterOperator.Equals, ReportFilterOperator.Contains },
                    SupportedAggregates = new[] { ReportAggregate.CountDistinct },
                },
                new ReportDatasetField
                {
                    Key = "AssigneeEmployeeId", TitleAr = "رقم المكلَّف", TitleEn = "Assignee no.",
                    Type = ReportFieldType.Integer, VisibleByDefault = false, WidthMm = 20,
                    SupportedOperators = new[] { ReportFilterOperator.Equals, ReportFilterOperator.In },
                },
                new ReportDatasetField
                {
                    // Read through the assignee, who IS company-scoped. The department NODE itself carries
                    // no company column, so it is never the anchor - see the source.
                    Key = "DepartmentName", TitleAr = "الإدارة", TitleEn = "Department",
                    Groupable = true, WidthMm = 40,
                    SupportedOperators = new[] { ReportFilterOperator.Equals, ReportFilterOperator.Contains },
                    SupportedAggregates = new[] { ReportAggregate.CountDistinct },
                },
                new ReportDatasetField
                {
                    // The live model links work to a project by discriminator, not by a foreign key:
                    // EntityType == "Project" AND EntityId == the project id. Stated plainly so nobody
                    // reading a layout thinks a ProjectId column exists on the task.
                    Key = "ProjectId", TitleAr = "رقم المشروع", TitleEn = "Project no.",
                    Type = ReportFieldType.Integer, Groupable = true, WidthMm = 22,
                    SupportedOperators = new[] { ReportFilterOperator.Equals, ReportFilterOperator.In, ReportFilterOperator.IsNotNull },
                    SupportedAggregates = new[] { ReportAggregate.CountDistinct },
                },
                new ReportDatasetField
                {
                    Key = "ProjectName", TitleAr = "المشروع", TitleEn = "Project",
                    Groupable = true, WidthMm = 46,
                    SupportedOperators = new[] { ReportFilterOperator.Equals, ReportFilterOperator.Contains },
                    SupportedAggregates = new[] { ReportAggregate.CountDistinct },
                },
                new ReportDatasetField
                {
                    Key = "EntityType", TitleAr = "نوع السجل المرتبط", TitleEn = "Linked record type",
                    Groupable = true, VisibleByDefault = false, WidthMm = 30,
                    SupportedOperators = new[] { ReportFilterOperator.Equals, ReportFilterOperator.In },
                    SupportedAggregates = new[] { ReportAggregate.Count },
                },
                new ReportDatasetField
                {
                    Key = "EntityId", TitleAr = "رقم السجل المرتبط", TitleEn = "Linked record no.",
                    Type = ReportFieldType.Integer, VisibleByDefault = false, WidthMm = 22,
                    SupportedOperators = new[] { ReportFilterOperator.Equals, ReportFilterOperator.In },
                },
                new ReportDatasetField
                {
                    Key = "CreatedAt", TitleAr = "تاريخ الإنشاء", TitleEn = "Created",
                    Type = ReportFieldType.DateTime, Format = "yyyy-MM-dd HH:mm", Groupable = true, WidthMm = 32,
                    SupportedOperators = new[] { ReportFilterOperator.Between, ReportFilterOperator.GreaterOrEqual, ReportFilterOperator.LessOrEqual },
                    SupportedAggregates = new[] { ReportAggregate.Min, ReportAggregate.Max, ReportAggregate.Count },
                },
                new ReportDatasetField
                {
                    Key = "DueDate", TitleAr = "تاريخ الاستحقاق", TitleEn = "Due",
                    Type = ReportFieldType.DateTime, Format = "yyyy-MM-dd HH:mm", Groupable = true, WidthMm = 32,
                    SupportedOperators = new[] { ReportFilterOperator.Between, ReportFilterOperator.GreaterOrEqual, ReportFilterOperator.LessOrEqual, ReportFilterOperator.IsNull, ReportFilterOperator.IsNotNull },
                    SupportedAggregates = new[] { ReportAggregate.Min, ReportAggregate.Max },
                },
                new ReportDatasetField
                {
                    Key = "CompletedAt", TitleAr = "تاريخ الإنجاز", TitleEn = "Completed",
                    Type = ReportFieldType.DateTime, Format = "yyyy-MM-dd HH:mm", Groupable = true, WidthMm = 32,
                    SupportedOperators = new[] { ReportFilterOperator.Between, ReportFilterOperator.IsNull, ReportFilterOperator.IsNotNull },
                    SupportedAggregates = new[] { ReportAggregate.Min, ReportAggregate.Max, ReportAggregate.Count },
                },
            },

            Parameters = new[]
            {
                new ReportParameterDescriptor
                {
                    Key = "FromDate", TitleAr = "من تاريخ", TitleEn = "From date",
                    Type = ReportFieldType.Date, Required = false,
                },
                new ReportParameterDescriptor
                {
                    Key = "ToDate", TitleAr = "إلى تاريخ", TitleEn = "To date",
                    Type = ReportFieldType.Date, Required = false,
                },
            },
        };

        // ---- work.hours ------------------------------------------------------------------------
        // ---- work.approvals --------------------------------------------------------------------
        public static ReportDatasetDefinition Approvals() => new()
        {
            DatasetCode = WorkDatasetCodes.Approvals,
            Module = "Work",
            TitleAr = "الموافقات المعلّقة",
            TitleEn = "Pending approvals",
            DescriptionAr = "العمل المفتوح الذي ينتظر قراراً من صاحب صلاحية، عبر مسارات الموافقة الخمسة "
                          + "التي ينتجها التنسيق فعلاً. ليست صندوق موافقات شخصياً: النطاق هو صلاحية القراءة "
                          + "في الشركة. يختفي السطر من تلقاء نفسه حين تُتخذ الموافقة، لأن دورة حياة المهمة "
                          + "هي ما ينهيه - لا عمود حالة جديد.",
            DescriptionEn = "Open work waiting on somebody with authority to decide, across the five approval "
                          + "flows the orchestration actually produces. This is NOT a personal inbox: the scope "
                          + "is the ordinary Work read authority for the company. A row disappears on its own "
                          + "when the decision lands, because the task lifecycle ends it - there is no new "
                          + "status column behind this.",
            DataSourceKey = WorkDatasetCodes.Approvals,
            RequiredPermissionKey = WorkReportPermissions.View,
            MaxRows = MaxRows,
            RowCapPolicy = ReportRowCapPolicy.TruncateAndDeclare,
            AvailableInStudio = true,
            AvailableAsWidget = true,

            Fields = new[]
            {
                new ReportDatasetField
                {
                    Key = "TaskId", TitleAr = "رقم المهمة", TitleEn = "Task no.",
                    Type = ReportFieldType.Integer, Groupable = true, WidthMm = 20,
                    SupportedOperators = new[] { ReportFilterOperator.Equals, ReportFilterOperator.In },
                    SupportedAggregates = new[] { ReportAggregate.CountDistinct },
                },
                new ReportDatasetField
                {
                    Key = "Title", TitleAr = "العمل", TitleEn = "Work", WidthMm = 66,
                    SupportedOperators = new[] { ReportFilterOperator.Contains },
                },
                new ReportDatasetField
                {
                    // THE FLOW, FROM THE ONE VOCABULARY. The value is the orchestration rule-s own name,
                    // a stable technical code rather than a caption, because it is grouped on, filtered
                    // on and saved into templates that must survive a language change.
                    Key = "ApprovalFlow", TitleAr = "مسار الموافقة", TitleEn = "Approval flow",
                    Groupable = true, WidthMm = 34,
                    SupportedOperators = new[] { ReportFilterOperator.Equals, ReportFilterOperator.In },
                    SupportedAggregates = new[] { ReportAggregate.CountDistinct, ReportAggregate.Count },
                },
                new ReportDatasetField
                {
                    Key = "Status", TitleAr = "الحالة", TitleEn = "Status",
                    Groupable = true, WidthMm = 24,
                    SupportedOperators = new[] { ReportFilterOperator.Equals, ReportFilterOperator.In },
                    SupportedAggregates = new[] { ReportAggregate.Count },
                },
                new ReportDatasetField
                {
                    Key = "Priority", TitleAr = "الأولوية", TitleEn = "Priority",
                    Groupable = true, WidthMm = 24,
                    SupportedOperators = new[] { ReportFilterOperator.Equals, ReportFilterOperator.In },
                    SupportedAggregates = new[] { ReportAggregate.Count },
                },
                new ReportDatasetField
                {
                    // WHEN THE APPROVAL WORK OPENED - TaskItem.CreatedAt. Deliberately NOT called the
                    // business document-s creation time: the document may be older than the approval step
                    // it is now waiting on, and a resubmission opens a NEW step for the same document.
                    Key = "SubmittedAt", TitleAr = "تاريخ الطلب", TitleEn = "Opened",
                    Type = ReportFieldType.DateTime, Format = "yyyy-MM-dd HH:mm", Groupable = true, WidthMm = 32,
                    SupportedOperators = new[] { ReportFilterOperator.Between, ReportFilterOperator.GreaterOrEqual, ReportFilterOperator.LessOrEqual },
                    SupportedAggregates = new[] { ReportAggregate.Min, ReportAggregate.Max, ReportAggregate.Count },
                },
                new ReportDatasetField
                {
                    // ELAPSED AGE, NOT LATENESS. There is no approval SLA in this product - the approval
                    // projections carry DueDatePolicy.None - so this says how long the work has been open
                    // and claims nothing about whether that is too long.
                    Key = "AgeDays", TitleAr = "عمر الطلب (يوم)", TitleEn = "Age (days)",
                    Type = ReportFieldType.Integer, Align = ReportAlign.End, WidthMm = 24,
                    SupportedOperators = new[] { ReportFilterOperator.GreaterOrEqual, ReportFilterOperator.LessOrEqual },
                    SupportedAggregates = new[] { ReportAggregate.Max, ReportAggregate.Average, ReportAggregate.Count },
                },
                new ReportDatasetField
                {
                    // THE CURRENT ASSIGNEE OF THE APPROVAL WORK, and named for exactly that. It is NOT
                    // called the approver: only two of the five flows name a person on the event (Leave
                    // and EmployeeRequest write the domain-s resolved approver), while Inventory,
                    // ProjectBilling and DocumentVerification deliberately project UNASSIGNED and say so
                    // in their own headers. Calling this column Approver would assert an authority three
                    // flows out of five do not carry.
                    Key = "AssigneeName", TitleAr = "المُسنَد إليه", TitleEn = "Assigned to",
                    Groupable = true, WidthMm = 44,
                    SupportedOperators = new[] { ReportFilterOperator.Equals, ReportFilterOperator.Contains },
                    SupportedAggregates = new[] { ReportAggregate.CountDistinct },
                },
                new ReportDatasetField
                {
                    Key = "AssigneeEmployeeId", TitleAr = "رقم المُسنَد إليه", TitleEn = "Assignee no.",
                    Type = ReportFieldType.Integer, VisibleByDefault = false, WidthMm = 20,
                    SupportedOperators = new[] { ReportFilterOperator.Equals, ReportFilterOperator.In },
                },
                new ReportDatasetField
                {
                    // THE SOURCE RECORD, as registry codes and ids. No module URL is composed here: a
                    // dataset that knew how to link would be a second place routing lives.
                    Key = "EntityType", TitleAr = "نوع السجل", TitleEn = "Record type",
                    Groupable = true, WidthMm = 30,
                    SupportedOperators = new[] { ReportFilterOperator.Equals, ReportFilterOperator.In },
                    SupportedAggregates = new[] { ReportAggregate.Count },
                },
                new ReportDatasetField
                {
                    Key = "EntityId", TitleAr = "رقم السجل", TitleEn = "Record no.",
                    Type = ReportFieldType.Integer, VisibleByDefault = false, WidthMm = 20,
                    SupportedOperators = new[] { ReportFilterOperator.Equals, ReportFilterOperator.In },
                },
            },

            Parameters = new[]
            {
                new ReportParameterDescriptor
                {
                    Key = "FromDate", TitleAr = "من تاريخ", TitleEn = "From date",
                    Type = ReportFieldType.Date, Required = false,
                },
                new ReportParameterDescriptor
                {
                    Key = "ToDate", TitleAr = "إلى تاريخ", TitleEn = "To date",
                    Type = ReportFieldType.Date, Required = false,
                },
            },
        };
    }

    // ============================================================================================
    // SOURCES. Read-only, AsNoTracking, company predicate first, scope before aggregation.
    // ============================================================================================


    // ============================================================================================
    // THE SHARED AUTHORIZED ENTRY. One method, so the two subjects cannot drift apart about what the
    // caller may read, and one place to look when that question is asked.
    //
    // A NULL AUTHORIZATION IS A REFUSAL, NOT AN ABSENCE OF LIMITS. ForListAsync returns null when no
    // context resolves or the scope comes back empty; the only safe reading of null is "no rows".
    // ============================================================================================
    internal static class WorkAuthorizedTasks
    {
        public static async Task<IQueryable<TaskItem>?> QueryAsync(
            CrossDbContext db, ITaskReadAuthorizer authorizer, BusinessContext context,
            CancellationToken cancellationToken)
        {
            if (context.CompanyId <= 0) return null;

            var authorization = await authorizer.ForListAsync(cancellationToken);
            if (authorization is null) return null;

            return await authorizer.ApplyAsync(db.TaskItems.AsNoTracking(), authorization, cancellationToken);
        }
    }

    public sealed class WorkTasksDataSource : IReportDataSource
    {
        private readonly CrossDbContext _db;
        private readonly ITaskReadAuthorizer _authorizer;
        private readonly IProjectsAccessService _projects;

        public WorkTasksDataSource(CrossDbContext db, ITaskReadAuthorizer authorizer,
            IProjectsAccessService projects)
        {
            _db = db; _authorizer = authorizer; _projects = projects;
        }

        public string Key => WorkDatasetCodes.Tasks;

        public async Task<ReportDataSet> FetchAsync(ReportDataQuery query, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(query);
            var context = query.Context;
            var columns = query.RequestedColumns.Count > 0 ? query.RequestedColumns : query.Definition.Columns;
            var builder = new ReportDataSetBuilder(columns);

            // AUTHORIZATION FIRST, AND FROM THE AUTHORITATIVE PATH. Not a company predicate: company
            // is tenancy, not authority. Scope INTERSECTED with source-record eligibility, UNIONed with
            // the source entrance branch — composed by ITaskReadAuthorizer, never re-implemented here.
            // A null answer is a REFUSAL (no context, or a scope that resolves to nothing), so it
            // becomes an empty result rather than an unfiltered one.
            var rows = await WorkAuthorizedTasks.QueryAsync(_db, _authorizer, context, cancellationToken);
            if (rows is null) return builder.Build(totalRowCount: 0);

            var from = query.Parameters.GetDate("FromDate");
            var to = query.Parameters.GetDate("ToDate");
            if (from is not null) rows = rows.Where(t => t.CreatedAt >= from.Value.Date);
            if (to is not null) rows = rows.Where(t => t.CreatedAt < to.Value.Date.AddDays(1));

            // PROJECT VISIBILITY, BEFORE AGGREGATION. The visible set is resolved by the project module;
            // a task linked to a project this caller cannot see is dropped from the ROW SET, so it cannot
            // reach a count, a group, a name, a sum, or a non-empty bucket. Filtering after aggregating
            // would already have published the number.
            // WHICH PROJECTS, DECIDED BY THE PROJECTS MODULE - not by a membership assumption made here.
            //
            // THE DEFECT THIS REPLACES was fail-closed UNDER-reporting rather than a leak: the previous
            // call asked MemberProjectIdsAsync, which answers "projects this person is ATTACHED to". A
            // ProjectsAdministrator holding company-wide breadth is attached to nothing in particular, and
            // a bootstrap-open company has no membership rows at all, so both silently lost every
            // project-linked task from their own reports.
            //
            // ResolveProjectScopeAsync is the module-s own answer and already separates the three cases.
            // Reporting reads the breadth; it does not re-derive who may see a project.
            var projectScope = await _projects.ResolveProjectScopeAsync(context, ProjectsActions.Read, cancellationToken);
            switch (projectScope.Breadth)
            {
                case AccessBreadth.Company:
                case AccessBreadth.CrossCompany:
                    // Company breadth over projects: project-linked work is visible, still inside whatever
                    // the WORK scope above already allowed. This widens nothing else - an ordinary task the
                    // caller could not see does not become visible because they administer projects.
                    break;

                case AccessBreadth.Team:
                case AccessBreadth.Own:
                {
                    // An explicit id set; PrincipalIds carries the project ids for this shape.
                    var projectIds = (projectScope.PrincipalIds ?? Array.Empty<int>()).ToList();
                    rows = projectIds.Count == 0
                        ? rows.Where(t => t.EntityType != "Project")
                        : rows.Where(t => t.EntityType != "Project"
                                       || (t.EntityId != null && projectIds.Contains(t.EntityId.Value)));
                    break;
                }

                default:
                    // No project authority at all: no project-linked work. Ordinary tasks are untouched,
                    // because project scope decides project rows and nothing else.
                    rows = rows.Where(t => t.EntityType != "Project");
                    break;
            }

            int total = await rows.CountAsync(cancellationToken);

            var page = await rows
                .OrderByDescending(t => t.CreatedAt)
                .Take(query.MaxRows > 0 ? query.MaxRows : WorkReportingDatasets.MaxRows)
                .Select(t => new
                {
                    t.ID,
                    t.Title,
                    t.TitleEn,
                    t.Status,
                    t.Priority,
                    t.AssigneeEmployeeId,
                    t.DueDate,
                    t.CreatedAt,
                    t.CompletedAt,
                    t.EntityType,
                    t.EntityId,
                    // The hold fact travels with the row: ON HOLD is derived from this one timestamp, so
                    // reading it here costs nothing and asks nobody a second question.
                    t.HoldApprovedAtUtc,
                })
                .ToListAsync(cancellationToken);

            // Names resolved through COMPANY-SCOPED employees. The department node itself carries no
            // company column, so it is never the anchor: we reach it only from an employee already
            // confined to this tenant, and a department id belonging to another company can therefore
            // never put a name on this report.
            // ---- THE TWO SET-SHAPED AUTHORITIES, ASKED ONCE FOR THE WHOLE PAGE --------------------
            //
            // Both are resolved AFTER the page is materialised and are scoped to the ids on it, so the
            // cost is one round trip each regardless of how many rows came back. A per-row question here
            // would be the N+1 this whole file is written to avoid, and it would ask the same service
            // twenty thousand times at the row cap.
            var pageIds = page.Select(t => t.ID).ToList();


            // WAITING APPROVAL: the SAME predicate the Tasks screen uses, narrowed to this page. Not a
            // copy of it - the vocabulary of approval rules lives in TaskApprovalProjection and nowhere
            // else, which is what keeps a report and a screen from disagreeing about the word.
            //
            // IT RUNS OVER THE ALREADY-AUTHORIZED QUERY, narrowed to this page - never over the
            // DbSet directly. Reading the table raw here would work, because the page ids came out of the
            // authorized query a moment ago, but it would break the invariant that the task table is only
            // ever reached through ITaskReadAuthorizer - and that is what a future edit will be
            // measured against, not the reasoning that made one exception safe today.
            var waitingIds = pageIds.Count == 0
                ? new HashSet<int>()
                : (await CrossBuy.BL.TasksCalendar.TaskApprovalProjection
                        .WaitingApproval(rows.Where(t => pageIds.Contains(t.ID)), _db, context.CompanyId)
                        .Select(t => t.ID)
                        .ToListAsync(cancellationToken))
                    .ToHashSet();

            var empIds = page.Select(p => p.AssigneeEmployeeId).Where(i => i > 0).Distinct().ToList();
            var people = await _db.Employee.AsNoTracking()
                .Where(e => e.EmpCompanyID == context.CompanyId && empIds.Contains(e.ID))
                .Select(e => new { e.ID, e.FullName, e.FullNameEn, e.DepartmentID })
                .ToListAsync(cancellationToken);
            var byEmp = people.ToDictionary(p => p.ID);

            var deptIds = people.Where(p => p.DepartmentID is > 0).Select(p => p.DepartmentID!.Value).Distinct().ToList();
            var deptNames = await _db.Hierarchicals.AsNoTracking()
                .Where(h => deptIds.Contains(h.H_ID))
                .Select(h => new { h.H_ID, h.H_Name, h.H_NameEn })
                .ToDictionaryAsync(h => h.H_ID, cancellationToken);

            var linkedProjectIds = page.Where(p => p.EntityType == "Project" && p.EntityId != null)
                .Select(p => p.EntityId!.Value).Distinct().ToList();
            var projectNames = await _db.Projects.AsNoTracking()
                .Where(p => p.CompanyID == context.CompanyId && linkedProjectIds.Contains(p.ID))
                .Select(p => new { p.ID, p.Name })
                .ToDictionaryAsync(p => p.ID, cancellationToken);

            var now = DateTime.UtcNow;
            foreach (var t in page)
            {
                byEmp.TryGetValue(t.AssigneeEmployeeId, out var emp);
                string? deptName = null;
                if (emp?.DepartmentID is > 0 && deptNames.TryGetValue(emp.DepartmentID.Value, out var d))
                    deptName = d.H_Name ?? d.H_NameEn;

                int? projectId = t.EntityType == "Project" ? t.EntityId : null;
                string? projectName = projectId is not null && projectNames.TryGetValue(projectId.Value, out var pr)
                    ? pr.Name : null;

                builder.AddRow(
                    t.ID,
                    t.Title,
                    t.Status,
                    // ASKED, NOT RESTATED. One definition of overdue in the product.
                    TaskStatuses.IsOverdue(t.DueDate, t.Status, now),
                    waitingIds.Contains(t.ID),
                    // ON HOLD IS THE APPROVAL, NOT THE REQUEST - TaskLifecycle draws that line and this
                    // asks it rather than testing the column directly.
                    TaskStatuses.IsOnHold(t.HoldApprovedAtUtc),
                    t.Priority,
                    emp is null ? null : (emp.FullName ?? emp.FullNameEn),
                    t.AssigneeEmployeeId,
                    deptName,
                    projectId,
                    projectName,
                    t.EntityType,
                    t.EntityId,
                    t.CreatedAt,
                    t.DueDate,
                    t.CompletedAt);
            }

            return builder.Build(totalRowCount: total);
        }
    }

    public sealed class WorkApprovalsDataSource : IReportDataSource
    {
        private readonly CrossDbContext _db;
        private readonly ITaskReadAuthorizer _authorizer;
        private readonly IProjectsAccessService _projects;

        public WorkApprovalsDataSource(CrossDbContext db, ITaskReadAuthorizer authorizer,
            IProjectsAccessService projects)
        {
            _db = db; _authorizer = authorizer; _projects = projects;
        }

        public string Key => WorkDatasetCodes.Approvals;

        public async Task<ReportDataSet> FetchAsync(ReportDataQuery query, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(query);
            var context = query.Context;
            var columns = query.RequestedColumns.Count > 0 ? query.RequestedColumns : query.Definition.Columns;
            var builder = new ReportDataSetBuilder(columns);

            // AUTHORIZATION FIRST, AND FROM THE AUTHORITATIVE PATH. Not a company predicate: company
            // is tenancy, not authority. Scope INTERSECTED with source-record eligibility, UNIONed with
            // the source entrance branch — composed by ITaskReadAuthorizer, never re-implemented here.
            // A null answer is a REFUSAL (no context, or a scope that resolves to nothing), so it
            // becomes an empty result rather than an unfiltered one.
            var rows = await WorkAuthorizedTasks.QueryAsync(_db, _authorizer, context, cancellationToken);
            if (rows is null) return builder.Build(totalRowCount: 0);

            // THE APPROVAL PREDICATE, FROM THE ONE VOCABULARY. Five flows, decided in
            // TaskApprovalProjection; this file holds no rule-key prefix of its own.
            rows = CrossBuy.BL.TasksCalendar.TaskApprovalProjection.WaitingApproval(rows, _db, context.CompanyId);

            // PROJECT SCOPE, the same authority work.tasks now uses. An approval task linked to a project
            // this caller may not see is dropped from the ROW SET, before any count.
            var projectScope = await _projects.ResolveProjectScopeAsync(context, ProjectsActions.Read, cancellationToken);
            switch (projectScope.Breadth)
            {
                case AccessBreadth.Company:
                case AccessBreadth.CrossCompany:
                    break;

                case AccessBreadth.Team:
                case AccessBreadth.Own:
                {
                    var projectIds = (projectScope.PrincipalIds ?? Array.Empty<int>()).ToList();
                    rows = projectIds.Count == 0
                        ? rows.Where(t => t.EntityType != "Project")
                        : rows.Where(t => t.EntityType != "Project"
                                       || (t.EntityId != null && projectIds.Contains(t.EntityId.Value)));
                    break;
                }

                default:
                    rows = rows.Where(t => t.EntityType != "Project");
                    break;
            }

            // The window is over when the approval WORK opened, which is what this dataset is about.
            var from = query.Parameters.GetDate("FromDate");
            var to = query.Parameters.GetDate("ToDate");
            if (from is not null) rows = rows.Where(t => t.CreatedAt >= from.Value.Date);
            if (to is not null) rows = rows.Where(t => t.CreatedAt < to.Value.Date.AddDays(1));

            int total = await rows.CountAsync(cancellationToken);

            var page = await rows
                .OrderByDescending(t => t.CreatedAt)
                .Take(query.MaxRows > 0 ? query.MaxRows : WorkReportingDatasets.MaxRows)
                .Select(t => new
                {
                    t.ID,
                    t.Title,
                    t.TitleEn,
                    t.Status,
                    t.Priority,
                    t.AssigneeEmployeeId,
                    t.CreatedAt,
                    t.EntityType,
                    t.EntityId,
                })
                .ToListAsync(cancellationToken);

            var pageIds = page.Select(t => t.ID).ToList();

            // THE FLOW PER ROW, IN ONE ROUND TRIP, from the vocabulary authority. A per-row classification
            // would be twenty thousand queries at the cap.
            var flows = await CrossBuy.BL.TasksCalendar.TaskApprovalProjection
                .FlowsForTasksAsync(_db, context.CompanyId, pageIds, cancellationToken);

            // Names through COMPANY-SCOPED employees, set-based, exactly as the other sources resolve them.
            var empIds = page.Select(p => p.AssigneeEmployeeId).Where(i => i > 0).Distinct().ToList();
            var people = empIds.Count == 0
                ? new Dictionary<int, string?>()
                : (await _db.Employee.AsNoTracking()
                        .Where(e => e.EmpCompanyID == context.CompanyId && empIds.Contains(e.ID))
                        .Select(e => new { e.ID, e.FullName, e.FullNameEn })
                        .ToListAsync(cancellationToken))
                    .ToDictionary(e => e.ID, e => e.FullName ?? e.FullNameEn);

            var now = DateTime.UtcNow;
            foreach (var t in page)
            {
                flows.TryGetValue(t.ID, out var flow);
                people.TryGetValue(t.AssigneeEmployeeId, out var assigneeName);

                // ELAPSED AGE, computed here rather than in SQL: a clock in the predicate would make the
                // query non-deterministic for the database-s own caching, and this is presentation arithmetic
                // over a page that has already been narrowed.
                int ageDays = (int)Math.Max(0, Math.Floor((now - t.CreatedAt).TotalDays));

                builder.AddRow(
                    t.ID,
                    CrossBuy.BL.DisplayName.Of(t.Title, t.TitleEn),
                    flow,
                    t.Status,
                    t.Priority,
                    t.CreatedAt,
                    ageDays,
                    assigneeName,
                    t.AssigneeEmployeeId > 0 ? t.AssigneeEmployeeId : (int?)null,
                    t.EntityType,
                    t.EntityId);
            }

            return builder.Build(totalRowCount: total);
        }
    }

}
