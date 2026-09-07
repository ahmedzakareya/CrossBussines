using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Hr;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL.Reporting
{
    // ============================================================================================
    // HR ROSTER — reporting shape, on the EXISTING reporting engine.
    //
    // No second engine, no second saved-report store, no new execution path. These are two
    // IReportDatasetDefinition values and two IReportDataSource implementations, registered exactly
    // the way Accounting, Inventory and CRM already are, so they arrive in Report Studio, in widgets
    // and in the scheduled-report path for free.
    //
    // TWO DATASETS COVER FIVE OF THE SIX SHAPES THE BRIEF ASKS FOR:
    //
    //   roster.schedule           scheduled hours by employee/team, and roster coverage
    //   roster.planned-vs-actual  planned vs actual attendance, absence, overtime candidates
    //
    // THE SIXTH — CONFLICTS — IS DELIBERATELY NOT A DATASET, and that is a design decision rather
    // than an omission. A conflict is not a stored fact; it is the answer RosterService computes by
    // comparing an assignment against every other live assignment and against approved leave.
    // Re-expressing that as a report query would be a SECOND implementation of the same rule, in a
    // different language, with no test holding the two together — and the day they disagreed, the
    // screen and the report would each insist the other was wrong. Conflicts stay on the one
    // authority that owns them and are surfaced through the roster screen. The handoff note in the
    // final report says what a conflicts dataset would need before it could exist safely.
    //
    // NO MONEY ANYWHERE. Overtime is reported in MINUTES and named a candidate, because whether those
    // minutes are payable is a payroll policy question and payroll is out of scope for this batch.
    // ============================================================================================

    public static class RosterDatasetCodes
    {
        public const string Schedule = "roster.schedule";
        public const string PlannedVsActual = "roster.planned-vs-actual";
    }

    public static class RosterReportPermissions
    {
        // The module's reporting key. Mapped in Program.cs; unmapped means denied, for everyone.
        //
        // ONE TIER, not two. Accounting and Inventory split a second tier off because cost and margin
        // are commercially sensitive in a way a document total is not. A roster has no such split: a
        // schedule and the hours worked against it are the same sensitivity, and inventing a tier that
        // separated nothing would be ceremony. If payroll money ever reaches this data it will need
        // its own tier, and that is a payroll decision, not a rostering one.
        public const string View = "roster.reports.view";
    }

    public static class HrRosterDatasets
    {
        internal const int MaxRows = 20000;

        // ---- roster.schedule -------------------------------------------------------------------
        public static ReportDatasetDefinition Schedule() => new()
        {
            DatasetCode = RosterDatasetCodes.Schedule,
            Module = "Hr",
            TitleAr = "جدول المناوبات",
            TitleEn = "Roster schedule",
            DescriptionAr = "المناوبات المنشورة: الموظف والفرع والتاريخ والمناوبة والساعات المخطط لها. "
                          + "جمّع حسب الموظف أو الفرع أو التاريخ للحصول على الساعات المجدولة وتغطية المناوبات.",
            DescriptionEn = "Published shift assignments: employee, branch, date, shift and planned hours. "
                          + "Group by employee, branch or date to get scheduled hours and shift coverage.",
            DataSourceKey = RosterDatasetCodes.Schedule,
            RequiredPermissionKey = RosterReportPermissions.View,
            MaxRows = MaxRows,
            RowCapPolicy = ReportRowCapPolicy.TruncateAndDeclare,
            AvailableInStudio = true,
            AvailableAsWidget = true,

            Fields = new[]
            {
                new ReportDatasetField
                {
                    Key = "WorkDate", TitleAr = "التاريخ", TitleEn = "Date",
                    Type = ReportFieldType.Date, Format = "yyyy-MM-dd", Groupable = true, WidthMm = 26,
                    SupportedAggregates = new[] { ReportAggregate.Min, ReportAggregate.Max, ReportAggregate.Count },
                },
                new ReportDatasetField
                {
                    Key = "EmployeeName", TitleAr = "الموظف", TitleEn = "Employee",
                    Groupable = true, WidthMm = 50,
                    SupportedAggregates = new[] { ReportAggregate.CountDistinct },
                },
                new ReportDatasetField
                {
                    Key = "EmployeeId", TitleAr = "رقم الموظف", TitleEn = "Employee id",
                    Type = ReportFieldType.Integer, VisibleByDefault = false, WidthMm = 20,
                },
                new ReportDatasetField
                {
                    Key = "BranchName", TitleAr = "الفرع", TitleEn = "Branch",
                    Groupable = true, WidthMm = 40,
                    SupportedAggregates = new[] { ReportAggregate.CountDistinct },
                },
                new ReportDatasetField
                {
                    Key = "ShiftCode", TitleAr = "رمز المناوبة", TitleEn = "Shift code",
                    Groupable = true, WidthMm = 26, SupportedAggregates = new[] { ReportAggregate.Count },
                },
                new ReportDatasetField
                {
                    Key = "ShiftName", TitleAr = "المناوبة", TitleEn = "Shift", Groupable = true, WidthMm = 36,
                },
                new ReportDatasetField
                {
                    Key = "PlannedStart", TitleAr = "بداية مخططة", TitleEn = "Planned start",
                    Type = ReportFieldType.DateTime, Format = "yyyy-MM-dd HH:mm", WidthMm = 34,
                },
                new ReportDatasetField
                {
                    Key = "PlannedEnd", TitleAr = "نهاية مخططة", TitleEn = "Planned end",
                    Type = ReportFieldType.DateTime, Format = "yyyy-MM-dd HH:mm", WidthMm = 34,
                },
                new ReportDatasetField
                {
                    // Hours, not minutes, because this is the field people SUM — and a total in
                    // minutes is a number nobody can read as a working figure.
                    Key = "PlannedHours", TitleAr = "الساعات المخططة", TitleEn = "Planned hours",
                    Type = ReportFieldType.Decimal, Format = "0.00", WidthMm = 26,
                    SupportedAggregates = new[] { ReportAggregate.Sum, ReportAggregate.Average, ReportAggregate.Count },
                },
                new ReportDatasetField
                {
                    Key = "IsOvernight", TitleAr = "مناوبة ليلية", TitleEn = "Overnight",
                    Type = ReportFieldType.Boolean, Groupable = true, WidthMm = 22,
                },
                new ReportDatasetField
                {
                    Key = "PeriodName", TitleAr = "الفترة", TitleEn = "Period", Groupable = true, WidthMm = 36,
                },
            },
        };

        // ---- roster.planned-vs-actual ----------------------------------------------------------
        public static ReportDatasetDefinition PlannedVsActual() => new()
        {
            DatasetCode = RosterDatasetCodes.PlannedVsActual,
            Module = "Hr",
            TitleAr = "المخطط مقابل الفعلي",
            TitleEn = "Planned vs actual",
            DescriptionAr = "مقارنة المناوبة المخطط لها بالحضور الفعلي: التأخير والانصراف المبكر والغياب "
                          + "والدقائق الإضافية المرشحة. لا يتحول أي رقم هنا إلى مبلغ.",
            DescriptionEn = "The planned shift against actual attendance: late arrival, early departure, "
                          + "absence and overtime-candidate minutes. No figure here becomes money.",
            DataSourceKey = RosterDatasetCodes.PlannedVsActual,
            RequiredPermissionKey = RosterReportPermissions.View,
            MaxRows = MaxRows,
            RowCapPolicy = ReportRowCapPolicy.TruncateAndDeclare,
            AvailableInStudio = true,
            AvailableAsWidget = true,

            Fields = new[]
            {
                new ReportDatasetField
                {
                    Key = "WorkDate", TitleAr = "التاريخ", TitleEn = "Date",
                    Type = ReportFieldType.Date, Format = "yyyy-MM-dd", Groupable = true, WidthMm = 26,
                    SupportedAggregates = new[] { ReportAggregate.Min, ReportAggregate.Max, ReportAggregate.Count },
                },
                new ReportDatasetField
                {
                    Key = "EmployeeName", TitleAr = "الموظف", TitleEn = "Employee",
                    Groupable = true, WidthMm = 50,
                    SupportedAggregates = new[] { ReportAggregate.CountDistinct },
                },
                new ReportDatasetField
                {
                    Key = "EmployeeId", TitleAr = "رقم الموظف", TitleEn = "Employee id",
                    Type = ReportFieldType.Integer, VisibleByDefault = false, WidthMm = 20,
                },
                new ReportDatasetField
                {
                    // WHICH AUTHORITY defined the planned window — Roster, Policy or None. Without it
                    // "why is this person 40 minutes late" has no answer anybody can check.
                    Key = "BaselineSource", TitleAr = "مصدر الخطة", TitleEn = "Baseline source",
                    Groupable = true, WidthMm = 26,
                    SupportedAggregates = new[] { ReportAggregate.Count },
                },
                new ReportDatasetField
                {
                    Key = "PlannedHours", TitleAr = "الساعات المخططة", TitleEn = "Planned hours",
                    Type = ReportFieldType.Decimal, Format = "0.00", WidthMm = 26,
                    SupportedAggregates = new[] { ReportAggregate.Sum, ReportAggregate.Average },
                },
                new ReportDatasetField
                {
                    Key = "ActualHours", TitleAr = "الساعات الفعلية", TitleEn = "Actual hours",
                    Type = ReportFieldType.Decimal, Format = "0.00", WidthMm = 26,
                    SupportedAggregates = new[] { ReportAggregate.Sum, ReportAggregate.Average },
                },
                new ReportDatasetField
                {
                    Key = "LateMinutes", TitleAr = "دقائق التأخير", TitleEn = "Late minutes",
                    Type = ReportFieldType.Integer, WidthMm = 24,
                    SupportedAggregates = new[] { ReportAggregate.Sum, ReportAggregate.Average, ReportAggregate.Max },
                },
                new ReportDatasetField
                {
                    Key = "EarlyDepartureMinutes", TitleAr = "دقائق الانصراف المبكر", TitleEn = "Early departure minutes",
                    Type = ReportFieldType.Integer, WidthMm = 28,
                    SupportedAggregates = new[] { ReportAggregate.Sum, ReportAggregate.Max },
                },
                new ReportDatasetField
                {
                    // NAMED A CANDIDATE, and it stays a candidate. Whether these minutes are payable
                    // is a payroll policy question this dataset deliberately does not answer.
                    Key = "OvertimeCandidateMinutes", TitleAr = "دقائق إضافية مرشحة", TitleEn = "Overtime candidate minutes",
                    Type = ReportFieldType.Integer, WidthMm = 30,
                    SupportedAggregates = new[] { ReportAggregate.Sum, ReportAggregate.Max },
                },
                new ReportDatasetField
                {
                    Key = "IsAbsent", TitleAr = "غياب", TitleEn = "Absent",
                    Type = ReportFieldType.Boolean, Groupable = true, WidthMm = 20,
                },
                new ReportDatasetField
                {
                    // The reason an absence is NOT unexplained. Reported beside IsAbsent rather than
                    // folded into it, so "away with permission" and "did not appear" stay distinct.
                    Key = "OnApprovedLeave", TitleAr = "إجازة معتمدة", TitleEn = "Approved leave",
                    Type = ReportFieldType.Boolean, Groupable = true, WidthMm = 24,
                },
                new ReportDatasetField
                {
                    // Worked without being rostered. Reported rather than dropped: an unrostered day
                    // somebody worked is a real finding, and hiding it would make coverage look
                    // tidier than the operation actually is.
                    Key = "IsUnrostered", TitleAr = "بدون جدولة", TitleEn = "Unrostered",
                    Type = ReportFieldType.Boolean, Groupable = true, WidthMm = 22,
                },
            },
        };

        public static IEnumerable<ReportDatasetDefinition> All()
        {
            yield return Schedule();
            yield return PlannedVsActual();
        }
    }

    // ============================================================================================
    // DATA SOURCES.
    //
    // Both fail closed on an unresolved tenant, both filter on CompanyId in SQL before any cap is
    // applied, and both read PUBLISHED periods only — a draft is a working document, and a report
    // that quietly included one would present an unfinished plan as a commitment.
    // ============================================================================================

    public sealed class RosterScheduleDataSource : IReportDataSource
    {
        private readonly CrossDbContext _db;
        public RosterScheduleDataSource(CrossDbContext db) { _db = db; }

        public string Key => RosterDatasetCodes.Schedule;

        public async Task<ReportDataSet> FetchAsync(ReportDataQuery query, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(query);
            var context = query.Context;
            var columns = query.RequestedColumns.Count > 0 ? query.RequestedColumns : query.Definition.Columns;
            var builder = new ReportDataSetBuilder(columns);

            // FAIL CLOSED on an unresolved tenant. No company means no rows, never every company's.
            if (context.CompanyId <= 0) return builder.Build(totalRowCount: 0);

            var from = query.Parameters.GetDate("FromDate");
            var to = query.Parameters.GetDate("ToDate");
            var employeeId = query.Parameters.GetInt("EmployeeId");
            var branchId = query.Parameters.GetInt("BranchId");

            var rows = _db.Set<RosterAssignment>().AsNoTracking()
                .Where(a => a.CompanyID == context.CompanyId
                         && a.Status == RosterAssignmentStatus.Planned
                         && a.Period!.Status == RosterPeriodStatus.Published);

            // Pushed into SQL, never applied after the cap: capping first and filtering second would
            // search the wrong slice and report an empty week for a company with a full roster.
            if (from is not null) rows = rows.Where(a => a.WorkDate >= from.Value.Date);
            if (to is not null) rows = rows.Where(a => a.WorkDate <= to.Value.Date);
            if (employeeId is > 0) rows = rows.Where(a => a.EmployeeID == employeeId.Value);
            if (branchId is > 0) rows = rows.Where(a => a.BranchID == branchId.Value);

            int total = await rows.CountAsync(cancellationToken);

            var page = await rows
                .OrderBy(a => a.WorkDate).ThenBy(a => a.PlannedStart)
                .Take(query.MaxRows > 0 ? query.MaxRows : HrRosterDatasets.MaxRows)
                .Select(a => new
                {
                    a.WorkDate,
                    EmployeeName = a.Employee!.FullName,
                    EmployeeNameEn = a.Employee.FullNameEn,
                    a.EmployeeID,
                    BranchName = a.Branch == null ? null : a.Branch.Name,
                    ShiftCode = a.Shift!.Code,
                    ShiftNameAr = a.Shift.NameAr,
                    ShiftNameEn = a.Shift.NameEn,
                    a.PlannedStart,
                    a.PlannedEnd,
                    PeriodNameAr = a.Period!.NameAr,
                    PeriodNameEn = a.Period.NameEn,
                })
                .ToListAsync(cancellationToken);

            bool isAr = System.Globalization.CultureInfo.CurrentUICulture
                .TwoLetterISOLanguageName == "ar";

            foreach (var r in page)
            {
                var minutes = (int)(r.PlannedEnd - r.PlannedStart).TotalMinutes;
                builder.AddRow(
                    r.WorkDate,
                    (isAr ? r.EmployeeName : DisplayName.Or(r.EmployeeNameEn, r.EmployeeName)) ?? r.EmployeeName,
                    r.EmployeeID,
                    r.BranchName,
                    r.ShiftCode,
                    (isAr ? r.ShiftNameAr : DisplayName.Or(r.ShiftNameEn, r.ShiftNameAr)),
                    r.PlannedStart,
                    r.PlannedEnd,
                    Math.Round(minutes / 60m, 2),
                    r.PlannedStart.Date != r.PlannedEnd.Date,
                    (isAr ? r.PeriodNameAr : DisplayName.Or(r.PeriodNameEn, r.PeriodNameAr)));
            }

            return builder.Build(totalRowCount: total);
        }
    }

    public sealed class RosterPlannedVsActualDataSource : IReportDataSource
    {
        private readonly CrossDbContext _db;
        private readonly CrossBuy.BL.Hr.IAttendanceBaselineResolver _baselines;
        public RosterPlannedVsActualDataSource(CrossDbContext db,
            CrossBuy.BL.Hr.IAttendanceBaselineResolver baselines)
        { _db = db; _baselines = baselines; }

        public string Key => RosterDatasetCodes.PlannedVsActual;

        public async Task<ReportDataSet> FetchAsync(ReportDataQuery query, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(query);
            var context = query.Context;
            var columns = query.RequestedColumns.Count > 0 ? query.RequestedColumns : query.Definition.Columns;
            var builder = new ReportDataSetBuilder(columns);

            if (context.CompanyId <= 0) return builder.Build(totalRowCount: 0);

            var from = query.Parameters.GetDate("FromDate") ?? DateTime.Today.AddDays(-30);
            var to = query.Parameters.GetDate("ToDate") ?? DateTime.Today;
            var employeeId = query.Parameters.GetInt("EmployeeId");

            // HR-B3: NO FORMULA LIVES HERE ANY MORE.
            //
            // This data source previously re-derived lateness, early leave, overtime and absence from
            // planned and actual instants — a second implementation of arithmetic AttendanceService
            // also had, and the two had already drifted: this one ignored the employer's grace period
            // and never consulted the attendance policy when no roster existed. A report that
            // disagrees with the screen it reports on is worse than no report.
            //
            // It now asks IAttendanceBaselineResolver what the employee was expected to work and
            // AttendanceMath what that means against the clock — the same two the recording path and
            // the roster screen use. The rows below are a projection, not a calculation.
            var employeeIds = await EmployeesInScopeAsync(context.CompanyId, from, to, employeeId, cancellationToken);
            if (employeeIds.Count == 0) return builder.Build(totalRowCount: 0);

            var baselines = await _baselines.ResolveManyAsync(
                context.CompanyId, employeeIds, from, to, cancellationToken);

            var actual = await _db.AttendanceRecords.AsNoTracking()
                .Where(r => r.CompanyID == context.CompanyId
                         && r.WorkDate >= from.Date && r.WorkDate <= to.Date
                         && employeeIds.Contains(r.EmployeeID))
                .Select(r => new { r.EmployeeID, r.WorkDate, r.CheckIn, r.CheckOut })
                .ToListAsync(cancellationToken);

            var actualBy = actual
                .GroupBy(r => (r.EmployeeID, r.WorkDate.Date))
                .ToDictionary(g => g.Key, g => g.First());

            // Approved leave, read from HR Leave and nowhere else (Status == 1 is approved).
            var leave = await _db.LeaveRequests.AsNoTracking()
                .Where(l => employeeIds.Contains(l.EmployeeID)
                         && l.Status == 1
                         && l.StartDate.Date <= to.Date && l.EndDate.Date >= from.Date)
                .Select(l => new { l.EmployeeID, l.StartDate, l.EndDate })
                .ToListAsync(cancellationToken);

            bool isAr = System.Globalization.CultureInfo.CurrentUICulture
                .TwoLetterISOLanguageName == "ar";

            var names = await _db.Employee.AsNoTracking()
                .Where(e => e.EmpCompanyID == context.CompanyId && employeeIds.Contains(e.ID))
                .Select(e => new { e.ID, e.FullName, e.FullNameEn })
                .ToDictionaryAsync(e => e.ID, cancellationToken);

            var emitted = new List<object?[]>();

            foreach (var kv in baselines)
            {
                var (empId, day) = kv.Key;
                var baseline = kv.Value;
                actualBy.TryGetValue((empId, day), out var act);

                // Neither a plan nor a clock-in is not a row — otherwise every employee's every
                // weekend fills the report.
                if (!baseline.HasPlan && act == null) continue;

                bool onLeave = leave.Any(l => l.EmployeeID == empId
                                           && l.StartDate.Date <= day && l.EndDate.Date >= day);

                var c = CrossBuy.BL.Hr.AttendanceMath.Compute(
                    baseline, act?.CheckIn, act?.CheckOut, onApprovedLeave: onLeave);

                names.TryGetValue(empId, out var who);
                emitted.Add(new object?[]
                {
                    day,
                    (isAr ? who?.FullName : DisplayName.Or(who?.FullNameEn, who?.FullName)) ?? who?.FullName,
                    empId,
                    c.Source,
                    Math.Round((c.ExpectedMinutes ?? 0) / 60m, 2),
                    Math.Round(c.WorkedMinutes / 60m, 2),
                    c.LateMinutes,
                    c.EarlyLeaveMinutes,
                    c.OvertimeCandidateMinutes,
                    c.IsAbsent,
                    c.OnApprovedLeave,
                    baseline.Source == CrossBuy.BL.Hr.AttendanceBaselineSource.Roster ? false : act != null && !baseline.HasPlan,
                });
            }

            int cap = query.MaxRows > 0 ? query.MaxRows : HrRosterDatasets.MaxRows;
            int total = emitted.Count;

            foreach (var row in emitted
                .OrderBy(r => (DateTime)r[0]!).ThenBy(r => (int)r[2]!)
                .Take(cap))
            {
                builder.AddRow(row);
            }

            return builder.Build(totalRowCount: total);
        }

        // Everyone rostered in the window, plus everyone with attendance in it. The union is what
        // makes both an absence and an unrostered day visible.
        private async Task<List<int>> EmployeesInScopeAsync(int companyId, DateTime from, DateTime to,
            int? employeeId, CancellationToken ct)
        {
            var rostered = await _db.Set<RosterAssignment>().AsNoTracking()
                .Where(a => a.CompanyID == companyId
                         && a.Status == RosterAssignmentStatus.Planned
                         && a.Period!.Status == RosterPeriodStatus.Published
                         && a.WorkDate >= from.Date && a.WorkDate <= to.Date
                         && (employeeId == null || a.EmployeeID == employeeId.Value))
                .Select(a => a.EmployeeID).Distinct().ToListAsync(ct);

            var attended = await _db.AttendanceRecords.AsNoTracking()
                .Where(r => r.CompanyID == companyId
                         && r.WorkDate >= from.Date && r.WorkDate <= to.Date
                         && (employeeId == null || r.EmployeeID == employeeId.Value))
                .Select(r => r.EmployeeID).Distinct().ToListAsync(ct);

            return rostered.Union(attended).ToList();
        }
    }
}
