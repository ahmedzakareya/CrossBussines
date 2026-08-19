using CrossBuy.BL.Platform;
using CrossBuy.Models.Context;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL.Reporting
{
    // ============================================================================================
    // Reporting Platform (ADR-037 §Dataset) — R1 ACTIVATION: the BUSINESS EVENTS dataset.
    //
    // The first real dataset on the platform, and it is deliberately over `BusinessEvents` — the platform
    // kernel's own append-only fact log — rather than over Accounting, Inventory or CRM. Three reasons:
    //
    //   1. It is the highest-value read in the product for an auditor: "what happened, to what, by whom, when",
    //      across every onboarded entity, in one place.
    //   2. It touches no module's tables, so activation does not couple reporting to a module's schema.
    //   3. It exercises the ENTIRE dataset layer for real — sensitivity tiers, calculated fields, drill-through,
    //      row caps, operators — against a table with genuine confidential rows.
    //
    // ────────────────────────────────────────────────────────────────────────────────────────────
    // THE SECURITY PROBLEM THIS FILE EXISTS TO SOLVE
    //
    // `BusinessEvents` rows carry a VISIBILITY (ADR-004): Internal | Confidential | Restricted | System. The
    // kernel's own reader, ITimelineProjectionService, applies FOUR filters before returning a row — company,
    // branch, the caller's module permission ON THAT ENTITY, and per-row visibility with an own-actor exception.
    //
    // A report over the same table could trivially become a way around all four. So it does not read the table
    // naively; it reproduces three of the four filters exactly, and replaces the fourth with something stricter.
    //
    //   company     — identical: CompanyID == context.CompanyId, on the row.
    //   branch      — identical, including the "only when BOTH sides have a branch" rule.
    //   visibility  — identical tiering, identical own-actor exception (see §Visibility below).
    //   per-entity  — CANNOT be reproduced, and is replaced by something HARDER.
    //                 ┌──────────────────────────────────────────────────────────────────────────┐
    //                 │ The kernel asks "may this caller View THIS record?" per row. A report     │
    //                 │ crossing 50 000 rows and 11 entity types cannot: that is 50 000           │
    //                 │ permission evaluations, and the answer varies per row.                    │
    //                 │                                                                            │
    //                 │ So the whole report is gated behind its own permission key, which is NOT  │
    //                 │ mapped to any ordinary module role. An auditor holds it; a salesperson    │
    //                 │ does not. That is a STRICTER gate than the kernel's per-entity View —      │
    //                 │ anyone who passes it could already open the records — but it is a         │
    //                 │ DIFFERENT gate, and the difference is recorded here rather than left for  │
    //                 │ somebody to infer.                                                        │
    //                 └──────────────────────────────────────────────────────────────────────────┘
    //
    // The consequence to keep in mind when mapping the permission: holding
    // `reporting.businessevents.view` lets a caller see WHICH records changed and WHEN across the company,
    // without the per-record View check. Map it to audit/administration roles, never to an ordinary module role.
    // ============================================================================================
    public static class BusinessEventsReportCodes
    {
        public const string DatasetCode = "Platform.BusinessEvents.Log";
        public const string DataSourceKey = "Platform.BusinessEvents.Log";
        public const string ReportCode = "Platform.BusinessEventLog";

        public const string CategoryKey = "platform.reporting";
    }

    // Three keys, one per visibility tier, so the tiers can be granted independently.
    //
    // They are NOT mapped by default. An unmapped key is DENIED by the fail-closed evaluator, so activating this
    // dataset without a deliberate mapping grants nobody anything — which is the intended starting state.
    public static class BusinessEventsReportPermissions
    {
        // The report itself, and its Internal-visibility rows.
        public const string View = "reporting.businessevents.view";

        // Additionally reveals Confidential rows and the Payload column.
        public const string Confidential = "reporting.businessevents.confidential";

        // Additionally reveals Restricted and System rows.
        public const string Restricted = "reporting.businessevents.restricted";
    }

    // The DELIVERY vocabulary (R3 Phase 4). A projection of BusinessEventDispatch.Status onto the question a
    // person actually asks — "did it land?" — plus the two states the raw status column cannot express:
    // an event with NO consumers at all, and the "not yet delivered" union an operator sweeps for.
    public static class DeliveryStates
    {
        public const string Any = "";
        public const string Failed = "Failed";
        public const string Pending = "Pending";
        public const string Done = "Done";

        // Failed OR Pending. The single most useful filter on this screen: "what is stuck".
        public const string Undelivered = "Undelivered";

        // No dispatch row exists. NOT the same as delivered, and not the same as pending — it means no consumer
        // subscribes to this event type at all, which is a configuration fact rather than a failure.
        public const string None = "None";

        // WORST-FIRST. One Failed consumer among four Done ones makes the event Failed: an event is only as
        // delivered as its least delivered consumer. Ranking by recency or by majority is what lets a single
        // broken consumer hide behind its healthy peers.
        public static string Worst(IEnumerable<string> statuses)
        {
            string? best = null;
            foreach (var status in statuses)
            {
                if (status == BusinessEventDispatchStatus.Failed) return Failed;
                if (status == BusinessEventDispatchStatus.Pending || status == BusinessEventDispatchStatus.Claimed)
                    best = Pending;
                else if (best == null) best = Done;
            }
            return best ?? None;
        }
    }

    // ============================================================================================
    // THE DATASET
    // ============================================================================================
    public static class BusinessEventsDataset
    {
        // 25 000 rows. High enough for a month of a busy company, low enough that an unfiltered request does not
        // try to materialise the largest table in the database — BusinessEventService's own description of it.
        public const int MaxRows = 25_000;

        public static ReportDatasetDefinition Definition() => new()
        {
            DatasetCode = BusinessEventsReportCodes.DatasetCode,
            // 1.2 — R3 Phase 4 added the four delivery fields and five investigative parameters. MINOR, because
            // every addition is additive: no existing field changed type, meaning or sensitivity, so a saved
            // layout built against 1.1 still resolves every column it names.
            Version = new ReportDatasetVersion { Major = 1, Minor = 2 },
            Module = "Platform",
            TitleAr = "سجل أحداث الأعمال",
            TitleEn = "Business event log",
            DescriptionAr = "كل حقيقة عمل دائمة سجّلها النظام: ما الذي تغيّر، وعلى أي سجل، ومن قام به، ومتى.",
            DescriptionEn = "Every durable business fact the platform recorded: what changed, on which record, "
                          + "by whom, and when.",
            DataSourceKey = BusinessEventsReportCodes.DataSourceKey,
            RequiredPermissionKey = BusinessEventsReportPermissions.View,

            MaxRows = MaxRows,

            // TRUNCATE, not fail. This is an investigative log, not a financial statement: a partial answer that
            // SAYS it is partial is useful ("narrow the date range"), whereas failing a 25 001-row query outright
            // just makes the tool unusable on a busy month. The opposite choice is correct for a trial balance,
            // which is why the policy is per dataset rather than global.
            RowCapPolicy = ReportRowCapPolicy.TruncateAndDeclare,

            // Any field above Normal is gated, and the Payload column is Confidential — so a CSV of this dataset
            // can carry payload text. Exporting it therefore needs the export right as well.
            ExportPolicy = ReportDatasetExportPolicy.RequirePermissionForDataExport,

            // Available to the future Studio, but NOT as a dashboard widget: a widget refreshes unattended on a
            // shared screen, and an audit log is the last thing that should sit on one.
            AvailableInStudio = true,
            AvailableAsWidget = false,

            Fields = new[]
            {
                new ReportDatasetField
                {
                    Key = "OccurredAt", TitleAr = "التاريخ والوقت", TitleEn = "Occurred at",
                    Type = ReportFieldType.DateTime, Format = "yyyy-MM-dd HH:mm",
                    Groupable = true, WidthMm = 34,
                    SupportedAggregates = new[] { ReportAggregate.Min, ReportAggregate.Max, ReportAggregate.Count },
                },
                new ReportDatasetField
                {
                    Key = "EventType", TitleAr = "نوع الحدث", TitleEn = "Event type",
                    Groupable = true, WidthMm = 42,
                    SupportedAggregates = new[] { ReportAggregate.Count, ReportAggregate.CountDistinct },
                },
                new ReportDatasetField
                {
                    Key = "EntityType", TitleAr = "نوع السجل", TitleEn = "Record type",
                    Groupable = true, WidthMm = 30,
                    SupportedAggregates = new[] { ReportAggregate.CountDistinct },
                },
                new ReportDatasetField
                {
                    // EntityRef so a renderer can deep-link the cell through IEntityRegistry.BuildUrl, and so a
                    // parameter over it becomes a record picker. The registry code lives in EntityType, which is
                    // why LookupEntityCode here is the generic one — the real code is per row.
                    Key = "EntityId", TitleAr = "رقم السجل", TitleEn = "Record",
                    Type = ReportFieldType.Integer, WidthMm = 22,
                    DrillThroughKey = "record",
                },
                new ReportDatasetField
                {
                    Key = "ActorName", TitleAr = "المستخدم", TitleEn = "Actor",
                    Groupable = true, WidthMm = 40,
                    SupportedAggregates = new[] { ReportAggregate.CountDistinct },
                },
                new ReportDatasetField
                {
                    Key = "Visibility", TitleAr = "مستوى الظهور", TitleEn = "Visibility",
                    Groupable = true, WidthMm = 26,
                    SupportedAggregates = new[] { ReportAggregate.Count },
                },
                new ReportDatasetField
                {
                    Key = "BranchId", TitleAr = "الفرع", TitleEn = "Branch",
                    Type = ReportFieldType.Integer, Groupable = true, WidthMm = 20,
                    VisibleByDefault = false,
                },
                new ReportDatasetField
                {
                    // The correlation id is what joins one operation across the kernel log and the communication
                    // audit log, so it earns a place — hidden by default because it is only useful once an
                    // investigation has narrowed to a single operation.
                    Key = "CorrelationId", TitleAr = "معرّف الارتباط", TitleEn = "Correlation id",
                    WidthMm = 56, VisibleByDefault = false,
                    SupportedOperators = new[] { ReportFilterOperator.Equals, ReportFilterOperator.IsNull,
                                                 ReportFilterOperator.IsNotNull },
                },
                // ---- DELIVERY (R3 Phase 4) ----------------------------------------------------------------
                //
                // An event's FAN-OUT state, derived from BusinessEventDispatch — one row per (event, consumer).
                // It answers the question the audit columns cannot: "the fact was recorded, but did anything
                // downstream ever act on it?" A notification that never arrived looks identical to one that was
                // never raised unless this is on the page.
                //
                // WORST-FIRST, not last-writer. An event with four Done consumers and one Failed is FAILED —
                // reporting it as Done because Done is the majority (or the newest UpdatedAt) is how a broken
                // consumer stays invisible. See DeliveryStates.Worst.
                new ReportDatasetField
                {
                    Key = "DeliveryState", TitleAr = "حالة التسليم", TitleEn = "Delivery",
                    Groupable = true, WidthMm = 24, VisibleByDefault = false,
                    SupportedAggregates = new[] { ReportAggregate.Count },
                },
                new ReportDatasetField
                {
                    Key = "FailedConsumers", TitleAr = "المستهلكات المتعثرة", TitleEn = "Failed consumers",
                    WidthMm = 46, VisibleByDefault = false,
                    Filterable = false,   // a joined list is not a filterable value; filter on DeliveryState
                    Sortable = false,
                },
                new ReportDatasetField
                {
                    Key = "DeliveryAttempts", TitleAr = "المحاولات", TitleEn = "Attempts",
                    Type = ReportFieldType.Integer, WidthMm = 18, VisibleByDefault = false,
                    SupportedAggregates = new[] { ReportAggregate.Max, ReportAggregate.Sum },
                },
                new ReportDatasetField
                {
                    // CONFIDENTIAL, and the reason is not obvious: a dispatch error is operational text, but it
                    // is an EXCEPTION MESSAGE, and exception messages quote the data that broke them — a
                    // constraint violation names the value, a serializer failure quotes the payload fragment.
                    // Treating it as ordinary operational metadata would leak, through the diagnostics column,
                    // exactly what the Payload gate exists to withhold.
                    Key = "DeliveryError", TitleAr = "سبب التعثر", TitleEn = "Failure reason",
                    WidthMm = 70, VisibleByDefault = false,
                    Sensitivity = ReportFieldSensitivity.Confidential,
                    RequiredPermissionKey = BusinessEventsReportPermissions.Confidential,
                    Filterable = false, Sortable = false,
                },

                new ReportDatasetField
                {
                    // CONFIDENTIAL. A payload is a summary by contract (PKS-001), but a summary of a sales
                    // invoice still carries its total. Gating it separately means an auditor can read the SHAPE
                    // of activity — who touched what, when — without reading the amounts.
                    Key = "Payload", TitleAr = "التفاصيل", TitleEn = "Payload",
                    WidthMm = 80, VisibleByDefault = false,
                    Sensitivity = ReportFieldSensitivity.Confidential,
                    RequiredPermissionKey = BusinessEventsReportPermissions.Confidential,
                    Filterable = false,   // free-text search over a JSON blob is a table scan, not a filter
                    Sortable = false,
                },
                new ReportDatasetField
                {
                    // The stable public identity. Safe to expose (the kernel's own comment says so), and it is
                    // what an operator quotes when reporting a specific event.
                    Key = "EventUid", TitleAr = "معرّف الحدث", TitleEn = "Event id",
                    WidthMm = 56, VisibleByDefault = false,
                    SupportedOperators = new[] { ReportFilterOperator.Equals },
                },
                new ReportDatasetField
                {
                    // NEVER. The dedup key is an idempotency token; it has no business meaning on a page, and
                    // exposing it would let a reader infer a producer's keying scheme.
                    Key = "DedupKey", TitleAr = "-", TitleEn = "-",
                    Sensitivity = ReportFieldSensitivity.Never,
                    Filterable = false, Sortable = false, VisibleByDefault = false,
                },
                new ReportDatasetField
                {
                    // A CALCULATED field: the action half of "<EntityCode>.<Action>". Not filterable or sortable
                    // — the source never produces it and the shaper computes it after filtering (the rule
                    // ReportDatasetValidator enforces).
                    Key = "Action", TitleAr = "الإجراء", TitleEn = "Action",
                    Expression = "SUBSTRING([EventType], LENGTH([EntityType]) + 2, 80)",
                    DependsOn = new[] { "EventType", "EntityType" },
                    Filterable = false, Sortable = false,
                    Groupable = true, WidthMm = 30,
                    VisibleByDefault = false,
                },
            },

            Parameters = new[]
            {
                new ReportParameterDescriptor
                {
                    Key = "From", TitleAr = "من تاريخ", TitleEn = "From",
                    Type = ReportFieldType.Date, Required = true, DefaultValue = "month-start",
                    HelpTextAr = "بداية الفترة (شاملة).", HelpTextEn = "Start of the period (inclusive).",
                },
                new ReportParameterDescriptor
                {
                    Key = "To", TitleAr = "إلى تاريخ", TitleEn = "To",
                    Type = ReportFieldType.Date, Required = true, DefaultValue = "today",
                    HelpTextAr = "نهاية الفترة (شاملة).", HelpTextEn = "End of the period (inclusive).",
                },
                new ReportParameterDescriptor
                {
                    Key = "EntityType", TitleAr = "نوع السجل", TitleEn = "Record type",
                    AllowMultiple = true,
                    HelpTextAr = "اتركه فارغًا لكل الأنواع.", HelpTextEn = "Leave empty for every type.",
                },
                new ReportParameterDescriptor
                {
                    Key = "EntityId", TitleAr = "رقم السجل", TitleEn = "Record id",
                    Type = ReportFieldType.Integer,
                    HelpTextAr = "لعرض تاريخ سجل واحد.", HelpTextEn = "To see one record's history.",
                },
                new ReportParameterDescriptor
                {
                    Key = "ActorEmployeeId", TitleAr = "المستخدم", TitleEn = "Actor",
                    Type = ReportFieldType.EntityRef, LookupEntityCode = "Employee",
                },

                // ---- R3 Phase 4 — the pilot's investigative filters -----------------------------------------
                new ReportParameterDescriptor
                {
                    Key = "EventType", TitleAr = "نوع الحدث", TitleEn = "Event type",
                    AllowMultiple = true,
                    HelpTextAr = "مثال: SalesInvoice.Created. اتركه فارغًا للكل.",
                    HelpTextEn = "e.g. SalesInvoice.Created. Leave empty for every type.",
                },
                new ReportParameterDescriptor
                {
                    // The join key across logs. Exact match only — a LIKE over a GUID column is a table scan
                    // that buys nothing, since a correlation id is copied, never typed from memory.
                    Key = "CorrelationId", TitleAr = "معرّف الارتباط", TitleEn = "Correlation id",
                    HelpTextAr = "لتتبّع عملية واحدة عبر السجلات.",
                    HelpTextEn = "Follow one operation across logs.",
                },
                new ReportParameterDescriptor
                {
                    // "Branch where permitted" — and permitted is decided by the CONTEXT, not by this parameter.
                    //
                    // A caller pinned to a branch cannot use it to widen: the data source ignores the value
                    // whenever context.BranchId is set, so the parameter can only ever NARROW what the caller
                    // could already read. A head-office reader (no pinned branch) uses it to focus on one branch.
                    // Presented as an ordinary filter because a control that silently does nothing for half the
                    // users is worse than one that visibly narrows for the other half.
                    //
                    // NAMED FilterBranchId, NOT BranchId. "BranchId" is a reserved system parameter: the binder
                    // fills it from the resolved BusinessContext unconditionally (EnsureSystemParameter) and
                    // discards anything the caller sends. Declaring a user parameter under that key would put a
                    // scope value and a filter value in one slot — the filter would be silently overwritten by
                    // the context, and the input would appear to do nothing. Distinct key, distinct meaning.
                    Key = "FilterBranchId", TitleAr = "الفرع", TitleEn = "Branch",
                    Type = ReportFieldType.Integer,
                    HelpTextAr = "يُتجاهل إذا كان حسابك مقيّدًا بفرع.",
                    HelpTextEn = "Ignored when your account is already pinned to a branch.",
                },
                new ReportParameterDescriptor
                {
                    // The operational lens: "show me only what never landed".
                    Key = "DeliveryState", TitleAr = "حالة التسليم", TitleEn = "Delivery state",
                    Options = new[]
                    {
                        new ReportParameterOption { Value = DeliveryStates.Any,        LabelAr = "الكل",           LabelEn = "Any" },
                        new ReportParameterOption { Value = DeliveryStates.Failed,     LabelAr = "متعثّر",          LabelEn = "Failed" },
                        new ReportParameterOption { Value = DeliveryStates.Pending,    LabelAr = "قيد الانتظار",    LabelEn = "Pending" },
                        new ReportParameterOption { Value = DeliveryStates.Done,       LabelAr = "مكتمل",          LabelEn = "Delivered" },
                        new ReportParameterOption { Value = DeliveryStates.Undelivered,LabelAr = "لم يُسلَّم بعد",   LabelEn = "Not yet delivered" },
                        new ReportParameterOption { Value = DeliveryStates.None,       LabelAr = "بلا مستهلكات",    LabelEn = "No consumers" },
                    },
                    DefaultValue = DeliveryStates.Any,
                    HelpTextAr = "«لم يُسلَّم بعد» = متعثّر أو قيد الانتظار.",
                    HelpTextEn = "\"Not yet delivered\" means Failed or Pending.",
                },
                new ReportParameterDescriptor
                {
                    Key = "Consumer", TitleAr = "المستهلك", TitleEn = "Consumer",
                    HelpTextAr = "يقيّد حالة التسليم على مستهلك واحد.",
                    HelpTextEn = "Narrows the delivery-state filter to one consumer.",
                },

                new ReportParameterDescriptor
                {
                    Key = ReportSystemParameters.CompanyId, TitleAr = "الشركة", TitleEn = "Company",
                    Type = ReportFieldType.Integer, SystemSupplied = true,
                },
            },

            DrillDowns = new[]
            {
                new ReportDatasetDrillDown
                {
                    Key = "by-record-type", TitleAr = "حسب نوع السجل", TitleEn = "By record type",
                    Levels = new[] { "EntityType", "EventType", "ActorName" },
                },
                new ReportDatasetDrillDown
                {
                    Key = "by-actor", TitleAr = "حسب المستخدم", TitleEn = "By actor",
                    Levels = new[] { "ActorName", "EntityType" },
                },
            },

            DrillThroughTargets = new[]
            {
                new ReportDatasetDrillTarget
                {
                    Key = "record", TitleAr = "فتح السجل", TitleEn = "Open the record",

                    // Points at THIS report, filtered to the one record — the useful drill-through for an audit
                    // log. Opening the business record itself is a UI deep link through IEntityRegistry, not a
                    // report-to-report navigation.
                    TargetReportCode = BusinessEventsReportCodes.ReportCode,
                    ParameterMap = new Dictionary<string, string>
                    {
                        ["EntityType"] = "EntityType",
                        ["EntityId"] = "EntityId",
                    },
                },
            },
        };

        // The report over the dataset. A thin definition: the dataset already declares the shape.
        public static ReportDefinition Report()
        {
            var dataset = Definition();
            return new ReportDefinition
            {
                Code = BusinessEventsReportCodes.ReportCode,
                Module = "Platform",
                TitleAr = "سجل أحداث الأعمال",
                TitleEn = "Business event log",
                DescriptionAr = dataset.DescriptionAr,
                DescriptionEn = dataset.DescriptionEn,
                DataSourceKey = BusinessEventsReportCodes.DataSourceKey,
                PermissionKey = BusinessEventsReportPermissions.View,
                CategoryKey = BusinessEventsReportCodes.CategoryKey,
                Tags = new[] { "audit", "platform" },

                // Projected from the dataset, so the two can never disagree about a column's type, format or
                // internal-ness. This is what ToColumn() exists for.
                Columns = dataset.Fields.Select(f => f.ToColumn()).ToList(),
                Parameters = dataset.Parameters,

                DefaultSorts = new[] { ReportSort.By("OccurredAt", descending: true) },

                DefaultFormat = ReportOutputFormat.Html,
                Capabilities = new ReportCapabilities
                {
                    MaxRows = MaxRows,
                    PreviewRows = 100,
                    AllowSchedule = true,
                    AllowArchive = true,
                    AllowShare = true,
                },
                Icon = "ki-outline ki-time",
                Color = "info",
                SortOrder = 30,

                // 2 — the delivery columns arrived in R3. Bumped even though the change is additive, because the
                // version's job is to EXPLAIN AN ARCHIVED ARTIFACT: a PDF produced last month legitimately has no
                // Delivery column, and "it ran against definition v1" is the answer to why.
                DefinitionVersion = 2,
            };
        }
    }

    // A definition provider so the report joins the catalog through the same seam the platform's own two use.
    public sealed class BusinessEventsReportDefinitionProvider : IReportDefinitionProvider
    {
        public string ProviderName => "Platform.BusinessEvents";

        public IEnumerable<ReportDefinition> GetDefinitions()
        {
            yield return BusinessEventsDataset.Report();
        }
    }

    // ============================================================================================
    // THE DATA SOURCE
    //
    // Read-only, AsNoTracking, and it reproduces the kernel's own filters. See the file header for which four
    // filters exist and why one of them is replaced rather than copied.
    // ============================================================================================
    public sealed class BusinessEventsReportDataSource : IReportDataSource
    {
        private readonly CrossDbContext _db;
        private readonly IReportPermissionEvaluator _permissions;

        public BusinessEventsReportDataSource(CrossDbContext db, IReportPermissionEvaluator permissions)
        {
            _db = db;
            _permissions = permissions;
        }

        public string Key => BusinessEventsReportCodes.DataSourceKey;

        public async Task<ReportDataSet> FetchAsync(ReportDataQuery query, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(query);
            var context = query.Context;

            var columns = query.RequestedColumns.Count > 0 ? query.RequestedColumns : query.Definition.Columns;
            var builder = new ReportDataSetBuilder(columns);

            // FAIL CLOSED on an unresolved tenant. The platform rule is that an unresolved company scope reads no
            // company-scoped data and writes none — an event log is emphatically company-scoped data.
            if (context.CompanyId <= 0) return builder.Build(totalRowCount: 0);

            // ---- the visibility tiers this caller may read ------------------------------------------------
            //
            // TWO values, and the split is load-bearing. `allowed` is what the SQL filter may pass — and it
            // CONTAINS Restricted even for an ordinary caller, purely so their OWN restricted rows are fetched.
            // `mayReadAnyRestricted` is the separate MANAGER grant. Deriving the flag from the set instead
            // (allowed.Contains(Restricted)) is the exact conflation TimelineProjectionService warns about, and
            // it silently shows every viewer every restricted event. It was written that way here first, and
            // A_plain_auditor_sees_internal_only caught it.
            var (allowed, mayReadAnyRestricted) = await ResolveVisibilitiesAsync(context, cancellationToken);

            // Holding neither elevated key still leaves Internal, so the report is useful to a plain auditor.
            // An empty set would mean the caller failed the report's own permission, which the engine already
            // refused before reaching here — so this is defensive, not a real path.
            if (allowed.Count == 0) return builder.Build(totalRowCount: 0);

            int? me = context.EmployeeId;

            // ---- the query ---------------------------------------------------------------------------------
            var rows = _db.BusinessEvents.AsNoTracking()
                .Where(e => e.CompanyID == context.CompanyId);

            // BRANCH: only when BOTH sides carry one. Filtering unconditionally would hide branch-stamped history
            // from a head-office reader AND hide unstamped events from a branch user — the exact two-way mistake
            // TimelineProjectionService documents.
            if (context.BranchId.HasValue)
            {
                int branchId = context.BranchId.Value;
                rows = rows.Where(e => e.BranchID == null || e.BranchID == branchId);
            }
            else
            {
                // THE BRANCH PARAMETER IS ONLY REACHABLE HERE — inside the else. A caller already pinned to a
                // branch by their context never gets to supply one, so the parameter can narrow but can never
                // widen. Putting it outside the else would turn a read-only filter into a scope override, which
                // is the whole class of bug the company parameter's absence avoids on the controller.
                var requestedBranch = query.Parameters.GetInt("FilterBranchId");
                if (requestedBranch is > 0) rows = rows.Where(e => e.BranchID == requestedBranch.Value);
            }

            // VISIBILITY, in SQL. Restricted is included for an ordinary caller so their OWN rows are fetched;
            // the row-level pass below drops everybody else's — the two-step the kernel's timeline uses, and
            // collapsing it would show every viewer every restricted event.
            var allowedList = allowed.ToList();
            rows = rows.Where(e => allowedList.Contains(e.Visibility));

            // ---- parameters ---------------------------------------------------------------------------------
            var from = query.Parameters.GetDate("From");
            var to = query.Parameters.GetDate("To");
            if (from.HasValue) rows = rows.Where(e => e.CreatedAt >= from.Value.Date);

            // INCLUSIVE upper bound. Every date range a user types ("1 Jan to 31 Jan") means both endpoints, and
            // a half-open range silently drops the last day — the same reasoning ReportFilterOperator.Between
            // records for filters.
            if (to.HasValue) rows = rows.Where(e => e.CreatedAt < to.Value.Date.AddDays(1));

            // AllowMultiple, so the binder produced a list. Nulls and blanks are dropped rather than turned into
            // a match on "" — an empty entry in a multi-select is the user not choosing, not a filter for blank.
            var entityTypes = query.Parameters.GetList("EntityType")
                .Select(v => v as string)
                .Where(v => !string.IsNullOrWhiteSpace(v))
                .Select(v => v!)
                .ToList();
            if (entityTypes.Count > 0) rows = rows.Where(e => entityTypes.Contains(e.EntityType));

            var entityId = query.Parameters.GetInt("EntityId");
            if (entityId is > 0) rows = rows.Where(e => e.EntityId == entityId.Value);

            var actorId = query.Parameters.GetInt("ActorEmployeeId");
            if (actorId is > 0) rows = rows.Where(e => e.ActorEmployeeId == actorId.Value);

            var eventTypes = query.Parameters.GetList("EventType")
                .Select(v => v as string)
                .Where(v => !string.IsNullOrWhiteSpace(v))
                .Select(v => v!)
                .ToList();
            if (eventTypes.Count > 0) rows = rows.Where(e => eventTypes.Contains(e.EventType));

            // CorrelationId is a Guid column. An unparseable value returns NOTHING rather than being ignored:
            // a filter the user can see in the box but which silently did not apply is how somebody concludes
            // "there is no activity for this operation" from a typo.
            var correlationText = query.Parameters.GetString("CorrelationId");
            if (!string.IsNullOrWhiteSpace(correlationText))
            {
                if (!Guid.TryParse(correlationText.Trim(), out var correlationId))
                    return builder.Build(totalRowCount: 0);
                rows = rows.Where(e => e.CorrelationId == correlationId);
            }

            // ---- delivery state, in SQL ---------------------------------------------------------------------
            //
            // Applied BEFORE the cap, not after. Filtering delivery in memory would cap first and filter second,
            // so "show me the failures" over a busy month would search only the newest 25 000 events and report
            // "no failures" for a queue that is visibly stuck.
            var deliveryState = query.Parameters.GetString("DeliveryState");
            var consumer = query.Parameters.GetString("Consumer");
            bool hasConsumer = !string.IsNullOrWhiteSpace(consumer);

            if (!string.IsNullOrWhiteSpace(deliveryState) && deliveryState != DeliveryStates.Any)
            {
                var dispatches = _db.BusinessEventDispatches.AsNoTracking();
                if (hasConsumer) dispatches = dispatches.Where(d => d.Consumer == consumer);

                rows = deliveryState switch
                {
                    DeliveryStates.Failed =>
                        rows.Where(e => dispatches.Any(d => d.EventId == e.EventId
                                                            && d.Status == BusinessEventDispatchStatus.Failed)),

                    DeliveryStates.Pending =>
                        rows.Where(e => dispatches.Any(d => d.EventId == e.EventId
                                                            && (d.Status == BusinessEventDispatchStatus.Pending
                                                                || d.Status == BusinessEventDispatchStatus.Claimed))),

                    // "Not yet delivered" = anything still owed. One predicate rather than two so the common
                    // operator sweep is a single query.
                    DeliveryStates.Undelivered =>
                        rows.Where(e => dispatches.Any(d => d.EventId == e.EventId
                                                            && d.Status != BusinessEventDispatchStatus.Done)),

                    // Done means EVERY consumer is done — worst-first, in SQL. `All(Done)` over an empty set is
                    // true, so the Any() guard is what keeps a no-consumer event out of "delivered".
                    DeliveryStates.Done =>
                        rows.Where(e => dispatches.Any(d => d.EventId == e.EventId)
                                        && !dispatches.Any(d => d.EventId == e.EventId
                                                                && d.Status != BusinessEventDispatchStatus.Done)),

                    DeliveryStates.None =>
                        rows.Where(e => !dispatches.Any(d => d.EventId == e.EventId)),

                    // An unrecognised value is treated as "Any" — the parameter engine already validated it
                    // against the declared options, so reaching here means a system-supplied path, not a user.
                    _ => rows,
                };
            }
            else if (hasConsumer)
            {
                // A consumer with no state means "events this consumer is involved in at all".
                rows = rows.Where(e => _db.BusinessEventDispatches
                    .Any(d => d.EventId == e.EventId && d.Consumer == consumer));
            }

            // ---- fetch ----------------------------------------------------------------------------------------
            //
            // Newest first, and +1 over the cap so truncation is DETECTED rather than guessed. A source that
            // fetched exactly the cap could not tell "exactly N rows exist" from "more than N exist", and would
            // have to either declare truncation it is unsure of or stay silent about truncation that happened.
            int cap = query.MaxRows > 0 ? query.MaxRows : BusinessEventsDataset.MaxRows;

            var fetched = await rows
                .OrderByDescending(e => e.CreatedAt).ThenByDescending(e => e.EventId)
                .Take(cap + 1)
                .Select(e => new
                {
                    e.EventId, e.EventUid, e.EntityType, e.EntityId, e.EventType,
                    e.ActorEmployeeId, e.Payload, e.Visibility, e.CreatedAt, e.BranchID, e.CorrelationId,
                })
                .ToListAsync(cancellationToken);

            bool truncated = fetched.Count > cap;
            if (truncated) fetched = fetched.Take(cap).ToList();

            // THE ROW-LEVEL OWN-ACTOR PASS. The SQL filter deliberately let Restricted through so the caller's own
            // rows would be fetched; this drops everybody else's. Identical to the kernel's timeline.
            if (!mayReadAnyRestricted)
                fetched = fetched
                    .Where(e => e.Visibility != BusinessEventVisibility.Restricted || e.ActorEmployeeId == me)
                    .ToList();

            // ---- actor names, resolved ONCE for the page, not per row -----------------------------------------
            var actorIds = fetched.Where(e => e.ActorEmployeeId.HasValue)
                .Select(e => e.ActorEmployeeId!.Value).Distinct().ToList();

            var actors = actorIds.Count == 0
                ? new Dictionary<int, string>()
                : (await _db.Employee.AsNoTracking()
                        .Where(x => actorIds.Contains(x.ID))
                        .Select(x => new { x.ID, x.FullName, x.FullNameEn })
                        .ToListAsync(cancellationToken))
                    .ToDictionary(x => x.ID, x => x.FullNameEn ?? x.FullName ?? ("#" + x.ID));

            bool arabic = query.Culture.TwoLetterISOLanguageName == "ar";

            // ---- delivery, resolved ONCE for the page ------------------------------------------------------
            //
            // One query for the whole page rather than one per row. BusinessEventDispatch carries no CompanyID
            // of its own, so it is reached ONLY through event ids that the company-filtered query above already
            // returned — the isolation is inherited from the parent set rather than re-asserted here, which is
            // why this must never become a standalone dispatch query.
            var eventIds = fetched.Select(e => e.EventId).ToList();

            var dispatchRows = eventIds.Count == 0
                ? new List<DispatchFact>()
                : await _db.BusinessEventDispatches.AsNoTracking()
                    .Where(d => eventIds.Contains(d.EventId))
                    .Select(d => new DispatchFact
                    {
                        EventId = d.EventId, Consumer = d.Consumer, Status = d.Status,
                        Attempts = d.Attempts, Error = d.Error,
                    })
                    .ToListAsync(cancellationToken);

            var delivery = dispatchRows.GroupBy(d => d.EventId).ToDictionary(g => g.Key, g => g.ToList());

            foreach (var e in fetched)
            {
                var dispatched = delivery.GetValueOrDefault(e.EventId) ?? new List<DispatchFact>();
                var failures = dispatched.Where(d => d.Status == BusinessEventDispatchStatus.Failed).ToList();

                builder.AddRow(new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["OccurredAt"] = e.CreatedAt,
                    ["EventType"] = e.EventType,
                    ["EntityType"] = e.EntityType,
                    ["EntityId"] = e.EntityId,

                    // A null actor is SYSTEM, not blank. A blank cell reads as missing data; "System" is the fact.
                    ["ActorName"] = e.ActorEmployeeId.HasValue
                        ? actors.GetValueOrDefault(e.ActorEmployeeId.Value, "#" + e.ActorEmployeeId.Value)
                        : (arabic ? "النظام" : "System"),

                    ["Visibility"] = e.Visibility,
                    ["BranchId"] = e.BranchID,
                    ["CorrelationId"] = e.CorrelationId?.ToString(),
                    ["Payload"] = e.Payload,
                    ["EventUid"] = e.EventUid.ToString(),

                    ["DeliveryState"] = DeliveryStates.Worst(dispatched.Select(d => d.Status)),
                    ["FailedConsumers"] = failures.Count == 0
                        ? null
                        : string.Join(", ", failures.Select(d => d.Consumer).Distinct()),
                    ["DeliveryAttempts"] = dispatched.Count == 0 ? null : dispatched.Max(d => d.Attempts),

                    // Only the FAILING consumers' errors. A Done row's stale Error text (from an attempt that
                    // later succeeded) on a delivered event reads as a live failure.
                    ["DeliveryError"] = failures.Count == 0
                        ? null
                        : string.Join(" | ", failures.Where(d => !string.IsNullOrWhiteSpace(d.Error))
                                                     .Select(d => d.Consumer + ": " + d.Error)),


                    // DedupKey is Never-sensitivity, so it is not projected at all. Emitting it and relying on the
                    // renderer to drop it would put it in memory and one bug away from an export.
                    ["DedupKey"] = null,
                });
            }

            // TotalRowCount is left NULL when truncated. The honest answer is "unknown" — computing the true total
            // needs a second COUNT over the same predicate, which on the largest table in the database is a real
            // cost for a number nobody acts on. ReportDataSet documents null as the honest unknown.
            return builder.Build(
                truncated: truncated,
                totalRowCount: truncated ? null : fetched.Count,
                appliedFilters: Array.Empty<ReportFilter>(),
                appliedSorts: new[] { ReportSort.By("OccurredAt", descending: true) });
        }

        // The tiers this caller may read, mirroring TimelineProjectionService.ResolveVisibilitiesAsync — including
        // its System-rides-with-Restricted rule, and its own-actor allowance for Restricted.
        private async Task<(HashSet<string> Allowed, bool MayReadAnyRestricted)> ResolveVisibilitiesAsync(
            BusinessContext context, CancellationToken cancellationToken)
        {
            var allowed = new HashSet<string>(StringComparer.Ordinal) { BusinessEventVisibility.Internal };

            if (await _permissions.HasPermissionAsync(
                    BusinessEventsReportPermissions.Confidential, context, cancellationToken))
                allowed.Add(BusinessEventVisibility.Confidential);

            if (await _permissions.HasPermissionAsync(
                    BusinessEventsReportPermissions.Restricted, context, cancellationToken))
            {
                allowed.Add(BusinessEventVisibility.Restricted);

                // System events are machine bookkeeping: the kernel gates them at the same level as Restricted and
                // never shows them below it.
                allowed.Add(BusinessEventVisibility.System);
                return (allowed, true);
            }

            // Own-actor exception: fetch Restricted so the caller's OWN rows come back, then drop the rest
            // row-by-row in the caller. Only meaningful for a resolved employee.
            if (context.EmployeeId is > 0) allowed.Add(BusinessEventVisibility.Restricted);

            return (allowed, false);
        }

        // A named projection rather than an anonymous type: it crosses a method boundary and is grouped, and an
        // anonymous type there costs a second EF materialisation shape for no readability.
        private sealed class DispatchFact
        {
            public long EventId { get; init; }
            public string Consumer { get; init; } = "";
            public string Status { get; init; } = "";
            public int Attempts { get; init; }
            public string? Error { get; init; }
        }
    }
}
