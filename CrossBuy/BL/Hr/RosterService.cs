using CrossBuy.BL.Platform;
using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Admin;
using CrossBuy.Models.Context.Hr;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL.Hr
{
    // ============================================================================================
    // ROSTER SERVICE — the authority for who is expected to work.
    //
    // AUTHORIZATION USES THE EXISTING VOCABULARY. No new HrAction was invented, and that was checked
    // rather than assumed:
    //
    //   · MANAGING a roster gates on HrActions.AttendanceManage, which already means "attendance
    //     records, policies, holidays". Rostering is the same administrative domain — a schedule is
    //     the plan the attendance record is measured against. Inventing "roster-manage" would have
    //     required a change to HrActions, which lives in a TAB-1-owned file, and would have forced
    //     somebody to decide a fresh action's bootstrap posture. Reusing AttendanceManage gives roster
    //     EXACTLY the posture attendance administration already has, and widens nothing.
    //
    //   · READING one's OWN roster gates on HrActions.EmployeeView with the caller as subject.
    //     HrAccessService answers that self-only, above the role rules, so an employee with no HR role
    //     sees their own schedule and nobody else's. Reading a colleague's requires a real HR role,
    //     which is the brief's requirement and is inherited rather than re-implemented here.
    //
    //   · A bootstrap note, stated rather than left implicit: attendance-manage IS bootstrap-open (only
    //     payroll-manage and confidential-view are permanently excluded). So on an install with no HR
    //     roles configured, roster management is as open as attendance administration already is. That
    //     is inherited posture, not new exposure — but it is the kind of thing that should be written
    //     down rather than discovered later.
    //
    // COMPANY COMES FROM BusinessContext AND NOWHERE ELSE. No method here takes a companyId, so there
    // is no parameter a caller could pass the wrong value to, and no fallback to company 1 to write.
    // ============================================================================================

    public static class RosterConflictKind
    {
        public const string OverlappingAssignment = "overlapping_assignment";
        public const string ApprovedLeave = "approved_leave";
        public const string EmployeeCompanyMismatch = "employee_company_mismatch";
        public const string BranchCompanyMismatch = "branch_company_mismatch";
        public const string InactiveEmployee = "inactive_employee";
        public const string InvalidTimeRange = "invalid_time_range";
        public const string DuplicateAssignment = "duplicate_assignment";
    }

    // A conflict carries the IDs of everything involved, not a sentence. The screen needs to link to
    // the other assignment and the leave request; a formatted string would force it to parse prose
    // back into ids, and a localized string could not be parsed at all.
    public sealed class RosterConflict
    {
        public required string Kind { get; init; }
        public int EmployeeID { get; init; }
        public DateTime WorkDate { get; init; }

        public int? AssignmentID { get; init; }
        public int? ConflictingAssignmentID { get; init; }
        public int? LeaveRequestID { get; init; }
        public int? BranchID { get; init; }

        // Blocking conflicts stop a publish. Advisory ones do not — but they are still returned, and
        // nothing here resolves anything silently.
        public bool IsBlocking => Kind != RosterConflictKind.DuplicateAssignment
                                  || ConflictingAssignmentID != null;
    }

    public sealed record RosterOutcome(bool Ok, string? Error = null,
        IReadOnlyList<RosterConflict>? Conflicts = null, RosterPeriod? Period = null,
        RosterAssignment? Assignment = null)
    {
        // One refusal for "not yours", "does not exist" and "not allowed". Distinguishing them would
        // let a caller map the company's employees by watching which ids answer differently.
        public static RosterOutcome Denied() => new(false, "not_authorized");
        public static RosterOutcome Fail(string code) => new(false, code);
        public static RosterOutcome Blocked(IReadOnlyList<RosterConflict> c) =>
            new(false, "conflicts_detected", c);
    }

    // What the screen renders: the period, its assignments, and every conflict currently detected.
    public sealed class RosterView
    {
        public required RosterPeriod Period { get; init; }
        public required IReadOnlyList<RosterAssignment> Assignments { get; init; }
        public required IReadOnlyList<RosterConflict> Conflicts { get; init; }

        public int ScheduledCount =>
            Assignments.Count(a => RosterAssignmentStatus.CountsAsScheduled(a.Status));

        public int ScheduledMinutes => Assignments
            .Where(a => RosterAssignmentStatus.CountsAsScheduled(a.Status))
            .Sum(a => a.PlannedMinutes);

        public int BlockingConflictCount => Conflicts.Count(c => c.IsBlocking);

        public IReadOnlyList<RosterConflict> ConflictsFor(int assignmentId) =>
            Conflicts.Where(c => c.AssignmentID == assignmentId
                              || c.ConflictingAssignmentID == assignmentId).ToList();
    }

    // Planned vs actual, for one employee on one date. Computed on demand by joining the roster to
    // AttendanceRecord on (CompanyID, EmployeeID, WorkDate) — the natural key both already carry.
    // NOTHING here is written back to Attendance, and no figure becomes money.
    public sealed class PlannedVsActual
    {
        public int EmployeeID { get; init; }
        public DateTime WorkDate { get; init; }

        public DateTime? PlannedStart { get; init; }
        public DateTime? PlannedEnd { get; init; }
        public DateTime? ActualIn { get; init; }
        public DateTime? ActualOut { get; init; }

        public int PlannedMinutes =>
            PlannedStart.HasValue && PlannedEnd.HasValue
                ? (int)(PlannedEnd.Value - PlannedStart.Value).TotalMinutes : 0;

        public int ActualMinutes =>
            ActualIn.HasValue && ActualOut.HasValue
                ? (int)(ActualOut.Value - ActualIn.Value).TotalMinutes : 0;

        // Scheduled but never showed up. Requires a plan — an employee who was not rostered cannot be
        // absent from a shift that was never promised.
        public bool IsAbsent => PlannedStart.HasValue && ActualIn == null;

        public int LateMinutes =>
            PlannedStart.HasValue && ActualIn.HasValue && ActualIn.Value > PlannedStart.Value
                ? (int)(ActualIn.Value - PlannedStart.Value).TotalMinutes : 0;

        public int EarlyDepartureMinutes =>
            PlannedEnd.HasValue && ActualOut.HasValue && ActualOut.Value < PlannedEnd.Value
                ? (int)(PlannedEnd.Value - ActualOut.Value).TotalMinutes : 0;

        // A CANDIDATE, deliberately not an entitlement and never an amount. Whether minutes past the
        // planned end are payable is a payroll policy question, and payroll is out of scope here.
        public int OvertimeCandidateMinutes =>
            PlannedEnd.HasValue && ActualOut.HasValue && ActualOut.Value > PlannedEnd.Value
                ? (int)(ActualOut.Value - PlannedEnd.Value).TotalMinutes : 0;
    }

    public interface IRosterClock { DateTime Now { get; } DateTime Today { get; } }

    public sealed class SystemRosterClock : IRosterClock
    {
        public DateTime Now => DateTime.Now;
        public DateTime Today => DateTime.Today;
    }

    public interface IRosterService
    {
        Task<RosterView?> GetPeriodAsync(int periodId, CancellationToken ct = default);
        Task<IReadOnlyList<RosterAssignment>> MyScheduleAsync(DateTime from, DateTime to, CancellationToken ct = default);
        Task<IReadOnlyList<RosterAssignment>?> EmployeeScheduleAsync(int employeeId, DateTime from, DateTime to, CancellationToken ct = default);
        Task<RosterOutcome> CreatePeriodAsync(string nameAr, string nameEn, DateTime start, DateTime end, int? branchId, CancellationToken ct = default);
        Task<RosterOutcome> AssignAsync(int periodId, int employeeId, DateTime workDate, int shiftId, int? branchId, string? note, string? reason, CancellationToken ct = default);
        Task<RosterOutcome> CancelAssignmentAsync(int assignmentId, string? reason, CancellationToken ct = default);
        Task<RosterOutcome> PublishAsync(int periodId, CancellationToken ct = default);
        Task<IReadOnlyList<PlannedVsActual>> PlannedVsActualAsync(DateTime from, DateTime to, int? employeeId, CancellationToken ct = default);
    }

    public sealed class RosterService : IRosterService
    {
        private readonly CrossDbContext _db;
        private readonly IBusinessContextAccessor _contexts;
        private readonly IHrAccessService _hr;
        private readonly IRosterClock _clock;

        public RosterService(CrossDbContext db, IBusinessContextAccessor contexts,
            IHrAccessService hr, IRosterClock clock)
        {
            _db = db;
            _contexts = contexts;
            _hr = hr;
            _clock = clock;
        }

        private DbSet<WorkShift> Shifts => _db.Set<WorkShift>();
        private DbSet<RosterPeriod> Periods => _db.Set<RosterPeriod>();
        private DbSet<RosterAssignment> Assignments => _db.Set<RosterAssignment>();
        private DbSet<RosterAssignmentRevision> Revisions => _db.Set<RosterAssignmentRevision>();

        // ----------------------------------------------------------------------------------------
        // GATES
        // ----------------------------------------------------------------------------------------

        // May the caller administer the roster for their own company? No subject, no company argument.
        private async Task<BusinessContext?> ManageGateAsync(CancellationToken ct)
        {
            var ctx = await _contexts.TryGetCurrentAsync(ct);
            if (ctx is not { CompanyId: > 0 }) return null;
            return await _hr.CanAsync(ctx, HrActions.AttendanceManage, null, ct) ? ctx : null;
        }

        // May the caller read THIS employee's schedule? Self-only for an ordinary employee; company-
        // wide for an HR role. Both answers come from HrAccessService, not from a rule re-stated here.
        private async Task<BusinessContext?> ReadGateAsync(int employeeId, CancellationToken ct)
        {
            var ctx = await _contexts.TryGetCurrentAsync(ct);
            if (ctx is not { CompanyId: > 0 }) return null;

            // The employee must belong to the caller's company before anything else is asked. Without
            // this, a foreign employee id would reach CanAsync, and a caller with a company-wide HR
            // role would be answered about somebody else's tenant.
            var owning = await _db.Employee.AsNoTracking()
                .Where(e => e.ID == employeeId)
                .Select(e => (int?)e.EmpCompanyID).FirstOrDefaultAsync(ct);
            if (owning is null || owning.Value != ctx.CompanyId) return null;

            var target = PermissionTarget.ForSubjectEmployee(employeeId, ctx.CompanyId);
            return await _hr.CanAsync(ctx, HrActions.EmployeeView, target, ct) ? ctx : null;
        }

        // ----------------------------------------------------------------------------------------
        // READS
        // ----------------------------------------------------------------------------------------

        public async Task<RosterView?> GetPeriodAsync(int periodId, CancellationToken ct = default)
        {
            var ctx = await _contexts.TryGetCurrentAsync(ct);
            if (ctx is not { CompanyId: > 0 }) return null;

            var period = await Periods.AsNoTracking()
                .FirstOrDefaultAsync(p => p.ID == periodId && p.CompanyID == ctx.CompanyId, ct);
            if (period == null) return null;

            // A DRAFT period is an internal working document. Seeing it requires management authority;
            // a published one is readable by anyone who may read HR lists for the company. Without
            // this split, an employee could read next month's unfinished draft as if it were a promise.
            bool mayManage = await _hr.CanAsync(ctx, HrActions.AttendanceManage, null, ct);
            if (!mayManage)
            {
                if (!period.IsPublished) return null;
                if (!await _hr.CanAsync(ctx, HrActions.Read, null, ct)) return null;
            }

            var assignments = await Assignments.AsNoTracking()
                .Include(a => a.Shift)
                .Include(a => a.Employee)
                .Where(a => a.CompanyID == ctx.CompanyId && a.PeriodID == periodId)
                .OrderBy(a => a.WorkDate).ThenBy(a => a.PlannedStart)
                .ToListAsync(ct);

            var conflicts = await DetectAsync(ctx, period, assignments, ct);

            return new RosterView { Period = period, Assignments = assignments, Conflicts = conflicts };
        }

        // Self-service. The caller's own employee identity comes from BusinessContext — there is no
        // parameter for it, so no request can name a different subject.
        public async Task<IReadOnlyList<RosterAssignment>> MyScheduleAsync(
            DateTime from, DateTime to, CancellationToken ct = default)
        {
            var ctx = await _contexts.TryGetCurrentAsync(ct);
            if (ctx is not { CompanyId: > 0 } || ctx.EmployeeId is not int me)
                return Array.Empty<RosterAssignment>();

            return await PublishedScheduleQuery(ctx.CompanyId, me, from, to).ToListAsync(ct);
        }

        public async Task<IReadOnlyList<RosterAssignment>?> EmployeeScheduleAsync(
            int employeeId, DateTime from, DateTime to, CancellationToken ct = default)
        {
            var ctx = await ReadGateAsync(employeeId, ct);
            if (ctx == null) return null;

            return await PublishedScheduleQuery(ctx.CompanyId, employeeId, from, to).ToListAsync(ct);
        }

        // PUBLISHED ONLY, for every schedule read that is not the management screen. A draft is not a
        // schedule anybody is entitled to plan their life around.
        private IQueryable<RosterAssignment> PublishedScheduleQuery(int companyId, int employeeId, DateTime from, DateTime to) =>
            Assignments.AsNoTracking()
                .Include(a => a.Shift)
                .Include(a => a.Period)
                .Where(a => a.CompanyID == companyId
                         && a.EmployeeID == employeeId
                         && a.WorkDate >= from.Date && a.WorkDate <= to.Date
                         && a.Status == RosterAssignmentStatus.Planned
                         && a.Period!.Status == RosterPeriodStatus.Published)
                .OrderBy(a => a.WorkDate).ThenBy(a => a.PlannedStart);

        // ----------------------------------------------------------------------------------------
        // CONFLICT DETECTION — returns facts, resolves nothing.
        // ----------------------------------------------------------------------------------------

        private async Task<IReadOnlyList<RosterConflict>> DetectAsync(
            BusinessContext ctx, RosterPeriod period, IReadOnlyList<RosterAssignment> assignments,
            CancellationToken ct)
        {
            var live = assignments
                .Where(a => RosterAssignmentStatus.CountsAsScheduled(a.Status))
                .ToList();
            if (live.Count == 0) return Array.Empty<RosterConflict>();

            var found = new List<RosterConflict>();
            var employeeIds = live.Select(a => a.EmployeeID).Distinct().ToList();

            // --- employee state: company membership and active flag, in one read ---
            var employees = await _db.Employee.AsNoTracking()
                .Where(e => employeeIds.Contains(e.ID))
                .Select(e => new { e.ID, e.EmpCompanyID, e.IsActive })
                .ToDictionaryAsync(e => e.ID, ct);

            // --- branch tenancy, in one read ---
            var branchIds = live.Where(a => a.BranchID.HasValue).Select(a => a.BranchID!.Value)
                .Distinct().ToList();
            var branches = branchIds.Count == 0
                ? new Dictionary<int, int>()
                : await _db.Branches.AsNoTracking()
                    .Where(b => branchIds.Contains(b.ID))
                    .ToDictionaryAsync(b => b.ID, b => b.CompanyID, ct);

            foreach (var a in live)
            {
                if (!employees.TryGetValue(a.EmployeeID, out var emp)
                    || emp.EmpCompanyID != ctx.CompanyId)
                {
                    found.Add(new RosterConflict
                    {
                        Kind = RosterConflictKind.EmployeeCompanyMismatch,
                        EmployeeID = a.EmployeeID, WorkDate = a.WorkDate, AssignmentID = a.ID,
                    });
                }
                else if (!emp.IsActive)
                {
                    found.Add(new RosterConflict
                    {
                        Kind = RosterConflictKind.InactiveEmployee,
                        EmployeeID = a.EmployeeID, WorkDate = a.WorkDate, AssignmentID = a.ID,
                    });
                }

                if (a.BranchID is int br
                    && (!branches.TryGetValue(br, out var branchCompany) || branchCompany != ctx.CompanyId))
                {
                    found.Add(new RosterConflict
                    {
                        Kind = RosterConflictKind.BranchCompanyMismatch,
                        EmployeeID = a.EmployeeID, WorkDate = a.WorkDate,
                        AssignmentID = a.ID, BranchID = br,
                    });
                }

                // An end at or before the start survived materialisation — the window is unusable.
                if (a.PlannedEnd <= a.PlannedStart)
                {
                    found.Add(new RosterConflict
                    {
                        Kind = RosterConflictKind.InvalidTimeRange,
                        EmployeeID = a.EmployeeID, WorkDate = a.WorkDate, AssignmentID = a.ID,
                    });
                }
            }

            // --- OVERLAP, including across period boundaries -------------------------------------
            //
            // Compared on PlannedStart/PlannedEnd, not on WorkDate. A night shift starting Thursday
            // 22:00 and ending Friday 06:00 overlaps a Friday 05:00 morning shift, and a date-only
            // comparison would miss it entirely — which is the whole reason overnight shifts are
            // modelled with real instants rather than a flag.
            var windowStart = live.Min(a => a.PlannedStart).AddDays(-1);
            var windowEnd = live.Max(a => a.PlannedEnd).AddDays(1);

            var neighbours = await Assignments.AsNoTracking()
                .Where(a => a.CompanyID == ctx.CompanyId
                         && employeeIds.Contains(a.EmployeeID)
                         && a.Status == RosterAssignmentStatus.Planned
                         && a.PlannedStart < windowEnd && a.PlannedEnd > windowStart)
                .Select(a => new { a.ID, a.EmployeeID, a.PeriodID, a.WorkDate, a.ShiftID, a.PlannedStart, a.PlannedEnd })
                .ToListAsync(ct);

            var seenPair = new HashSet<(int, int)>();
            foreach (var a in live)
            {
                foreach (var b in neighbours)
                {
                    if (b.ID == a.ID || b.EmployeeID != a.EmployeeID) continue;

                    bool overlaps = a.PlannedStart < b.PlannedEnd && b.PlannedStart < a.PlannedEnd;
                    if (!overlaps) continue;

                    // Report each pair once, whichever side is examined first.
                    var key = a.ID < b.ID ? (a.ID, b.ID) : (b.ID, a.ID);
                    if (!seenPair.Add(key)) continue;

                    // The same employee, date and shift twice is a duplicate rather than a clash — a
                    // different message and a different fix, so it is a different kind.
                    bool duplicate = b.WorkDate == a.WorkDate && b.ShiftID == a.ShiftID;

                    found.Add(new RosterConflict
                    {
                        Kind = duplicate
                            ? RosterConflictKind.DuplicateAssignment
                            : RosterConflictKind.OverlappingAssignment,
                        EmployeeID = a.EmployeeID, WorkDate = a.WorkDate,
                        AssignmentID = a.ID, ConflictingAssignmentID = b.ID,
                    });
                }
            }

            // --- APPROVED LEAVE ------------------------------------------------------------------
            //
            // Read-only. Leave balances, entitlement and the approval chain stay entirely with HR
            // Leave; this asks one question of it — "is this person approved to be away that day".
            // Status == 1 is approved (0 pending, 2 rejected).
            //
            // LeaveRequest carries no CompanyID, so tenancy is enforced through the employee ids,
            // which were themselves confirmed to belong to this company above.
            var minDate = live.Min(a => a.WorkDate).Date;
            var maxDate = live.Max(a => a.WorkDate).Date;

            var approvedLeave = await _db.LeaveRequests.AsNoTracking()
                .Where(l => employeeIds.Contains(l.EmployeeID)
                         && l.Status == 1
                         && l.StartDate.Date <= maxDate && l.EndDate.Date >= minDate)
                .Select(l => new { l.ID, l.EmployeeID, l.StartDate, l.EndDate })
                .ToListAsync(ct);

            foreach (var a in live)
            {
                foreach (var l in approvedLeave)
                {
                    if (l.EmployeeID != a.EmployeeID) continue;
                    if (a.WorkDate.Date < l.StartDate.Date || a.WorkDate.Date > l.EndDate.Date) continue;

                    found.Add(new RosterConflict
                    {
                        Kind = RosterConflictKind.ApprovedLeave,
                        EmployeeID = a.EmployeeID, WorkDate = a.WorkDate,
                        AssignmentID = a.ID, LeaveRequestID = l.ID,
                    });
                }
            }

            return found;
        }

        // ----------------------------------------------------------------------------------------
        // WRITES
        // ----------------------------------------------------------------------------------------

        public async Task<RosterOutcome> CreatePeriodAsync(string nameAr, string nameEn,
            DateTime start, DateTime end, int? branchId, CancellationToken ct = default)
        {
            var ctx = await ManageGateAsync(ct);
            if (ctx == null) return RosterOutcome.Denied();

            if (end.Date < start.Date) return RosterOutcome.Fail("invalid_period_range");

            // A branch from another tenant is refused at the door rather than becoming a conflict
            // later: a period is a container, and letting it point outside the company would make
            // every assignment inside it suspect.
            if (branchId is int br)
            {
                var owns = await _db.Branches.AsNoTracking()
                    .AnyAsync(b => b.ID == br && b.CompanyID == ctx.CompanyId, ct);
                if (!owns) return RosterOutcome.Fail("branch_not_in_company");
            }

            var period = new RosterPeriod
            {
                CompanyID = ctx.CompanyId,
                NameAr = nameAr, NameEn = nameEn,
                StartDate = start.Date, EndDate = end.Date,
                BranchID = branchId,
                Status = RosterPeriodStatus.Draft,
                CreatedBy = ctx.EmployeeId, CreatedAt = _clock.Now,
            };
            Periods.Add(period);
            await _db.SaveChangesAsync(ct);

            return new RosterOutcome(true, Period: period);
        }

        public async Task<RosterOutcome> AssignAsync(int periodId, int employeeId, DateTime workDate,
            int shiftId, int? branchId, string? note, string? reason, CancellationToken ct = default)
        {
            var ctx = await ManageGateAsync(ct);
            if (ctx == null) return RosterOutcome.Denied();

            var period = await Periods
                .FirstOrDefaultAsync(p => p.ID == periodId && p.CompanyID == ctx.CompanyId, ct);
            if (period == null) return RosterOutcome.Denied();
            if (period.Status == RosterPeriodStatus.Closed) return RosterOutcome.Fail("period_closed");

            // Adding to a PUBLISHED period is legitimate — rosters change — but it is a change to a
            // commitment, so it requires a stated reason and leaves a revision behind.
            if (period.IsPublished && string.IsNullOrWhiteSpace(reason))
                return RosterOutcome.Fail("reason_required_for_published_change");

            if (workDate.Date < period.StartDate.Date || workDate.Date > period.EndDate.Date)
                return RosterOutcome.Fail("date_outside_period");

            var shift = await Shifts.AsNoTracking()
                .FirstOrDefaultAsync(s => s.ID == shiftId && s.CompanyID == ctx.CompanyId, ct);
            if (shift == null) return RosterOutcome.Fail("shift_not_in_company");
            if (!shift.IsActive) return RosterOutcome.Fail("shift_inactive");

            // The employee is verified as this company's BEFORE a row is written. Company mismatch is
            // also a detected conflict — that path exists for rows whose employee MOVED after being
            // rostered, which detection catches and creation cannot.
            var employee = await _db.Employee.AsNoTracking()
                .Where(e => e.ID == employeeId)
                .Select(e => new { e.ID, e.EmpCompanyID, e.IsActive })
                .FirstOrDefaultAsync(ct);
            if (employee == null || employee.EmpCompanyID != ctx.CompanyId)
                return RosterOutcome.Denied();

            if (branchId is int br2)
            {
                var owns = await _db.Branches.AsNoTracking()
                    .AnyAsync(b => b.ID == br2 && b.CompanyID == ctx.CompanyId, ct);
                if (!owns) return RosterOutcome.Fail("branch_not_in_company");
            }

            var (plannedStart, plannedEnd) = Materialise(workDate, shift);

            var assignment = new RosterAssignment
            {
                CompanyID = ctx.CompanyId,
                PeriodID = period.ID,
                EmployeeID = employeeId,
                BranchID = branchId ?? period.BranchID,
                WorkDate = workDate.Date,
                ShiftID = shift.ID,
                PlannedStart = plannedStart,
                PlannedEnd = plannedEnd,
                Status = RosterAssignmentStatus.Planned,
                Note = note,
                CreatedBy = ctx.EmployeeId, CreatedAt = _clock.Now,
            };
            Assignments.Add(assignment);
            await _db.SaveChangesAsync(ct);

            if (period.IsPublished)
                await RecordRevisionAsync(ctx, assignment, "Assignment", null, "Planned", reason!, ct);

            return new RosterOutcome(true, Assignment: assignment);
        }

        public async Task<RosterOutcome> CancelAssignmentAsync(int assignmentId, string? reason,
            CancellationToken ct = default)
        {
            var ctx = await ManageGateAsync(ct);
            if (ctx == null) return RosterOutcome.Denied();

            var assignment = await Assignments
                .Include(a => a.Period)
                .FirstOrDefaultAsync(a => a.ID == assignmentId && a.CompanyID == ctx.CompanyId, ct);
            if (assignment == null) return RosterOutcome.Denied();

            bool published = assignment.Period?.Status == RosterPeriodStatus.Published;
            if (published && string.IsNullOrWhiteSpace(reason))
                return RosterOutcome.Fail("reason_required_for_published_change");

            if (assignment.Status == RosterAssignmentStatus.Cancelled)
                return new RosterOutcome(true, Assignment: assignment);

            var old = assignment.Status;
            assignment.Status = RosterAssignmentStatus.Cancelled;
            assignment.UpdatedBy = ctx.EmployeeId;
            assignment.UpdatedAt = _clock.Now;
            await _db.SaveChangesAsync(ct);

            if (published)
                await RecordRevisionAsync(ctx, assignment, "Status", old,
                    RosterAssignmentStatus.Cancelled, reason!, ct);

            return new RosterOutcome(true, Assignment: assignment);
        }

        public async Task<RosterOutcome> PublishAsync(int periodId, CancellationToken ct = default)
        {
            var ctx = await ManageGateAsync(ct);
            if (ctx == null) return RosterOutcome.Denied();

            var period = await Periods
                .FirstOrDefaultAsync(p => p.ID == periodId && p.CompanyID == ctx.CompanyId, ct);
            if (period == null) return RosterOutcome.Denied();
            if (period.IsPublished) return new RosterOutcome(true, Period: period);
            if (period.Status == RosterPeriodStatus.Closed) return RosterOutcome.Fail("period_closed");

            var assignments = await Assignments.AsNoTracking()
                .Where(a => a.CompanyID == ctx.CompanyId && a.PeriodID == periodId)
                .ToListAsync(ct);

            // PUBLISHING IS THE GATE, AND IT REFUSES RATHER THAN REPAIRS. A blocking conflict is
            // returned with every id needed to fix it; nothing is dropped, reassigned or silently
            // "resolved". The alternative — publishing a roster that puts someone on shift during
            // approved leave — is precisely the failure this batch exists to prevent.
            var conflicts = await DetectAsync(ctx, period, assignments, ct);
            var blocking = conflicts.Where(c => c.IsBlocking).ToList();
            if (blocking.Count > 0) return RosterOutcome.Blocked(conflicts);

            period.Status = RosterPeriodStatus.Published;
            period.PublishedAt = _clock.Now;
            period.PublishedBy = ctx.EmployeeId;
            period.UpdatedBy = ctx.EmployeeId;
            period.UpdatedAt = _clock.Now;
            await _db.SaveChangesAsync(ct);

            return new RosterOutcome(true, Period: period, Conflicts: conflicts);
        }

        // ----------------------------------------------------------------------------------------
        // PLANNED VS ACTUAL — a read across two models, writing neither.
        // ----------------------------------------------------------------------------------------
        public async Task<IReadOnlyList<PlannedVsActual>> PlannedVsActualAsync(
            DateTime from, DateTime to, int? employeeId, CancellationToken ct = default)
        {
            var ctx = employeeId is int one
                ? await ReadGateAsync(one, ct)
                : await ManageGateAsync(ct);
            if (ctx == null) return Array.Empty<PlannedVsActual>();

            var planned = await Assignments.AsNoTracking()
                .Where(a => a.CompanyID == ctx.CompanyId
                         && a.WorkDate >= from.Date && a.WorkDate <= to.Date
                         && a.Status == RosterAssignmentStatus.Planned
                         && a.Period!.Status == RosterPeriodStatus.Published
                         && (employeeId == null || a.EmployeeID == employeeId))
                .Select(a => new { a.EmployeeID, a.WorkDate, a.PlannedStart, a.PlannedEnd })
                .ToListAsync(ct);

            // The join key is (CompanyID, EmployeeID, WorkDate) — already present on both sides, which
            // is why this needed no foreign key and no change to AttendanceRecord.
            var actual = await _db.AttendanceRecords.AsNoTracking()
                .Where(r => r.CompanyID == ctx.CompanyId
                         && r.WorkDate >= from.Date && r.WorkDate <= to.Date
                         && (employeeId == null || r.EmployeeID == employeeId))
                .Select(r => new { r.EmployeeID, r.WorkDate, r.CheckIn, r.CheckOut })
                .ToListAsync(ct);

            var actualBy = actual
                .GroupBy(r => (r.EmployeeID, r.WorkDate.Date))
                .ToDictionary(g => g.Key, g => g.First());

            var rows = new List<PlannedVsActual>();
            foreach (var p in planned)
            {
                actualBy.TryGetValue((p.EmployeeID, p.WorkDate.Date), out var act);
                rows.Add(new PlannedVsActual
                {
                    EmployeeID = p.EmployeeID, WorkDate = p.WorkDate,
                    PlannedStart = p.PlannedStart, PlannedEnd = p.PlannedEnd,
                    ActualIn = act?.CheckIn, ActualOut = act?.CheckOut,
                });
            }

            // Attendance with no plan is included with a null plan rather than dropped: an unrostered
            // day somebody worked is a real finding, and silently omitting it would make coverage
            // reporting look tidier than the operation actually is.
            foreach (var kv in actualBy)
            {
                if (planned.Any(p => p.EmployeeID == kv.Key.Item1 && p.WorkDate.Date == kv.Key.Item2))
                    continue;
                rows.Add(new PlannedVsActual
                {
                    EmployeeID = kv.Key.Item1, WorkDate = kv.Key.Item2,
                    ActualIn = kv.Value.CheckIn, ActualOut = kv.Value.CheckOut,
                });
            }

            return rows.OrderBy(r => r.WorkDate).ThenBy(r => r.EmployeeID).ToList();
        }

        // ----------------------------------------------------------------------------------------

        // Turns a date plus a shift definition into two real instants. An overnight shift lands its
        // end on the NEXT day, which is what makes overlap detection work on instants rather than on
        // a date plus a flag every caller would have to remember to honour.
        private static (DateTime start, DateTime end) Materialise(DateTime workDate, WorkShift shift)
        {
            var start = workDate.Date + shift.StartTime;
            var end = workDate.Date + shift.EndTime;
            if (shift.CrossesMidnight) end = end.AddDays(1);
            return (start, end);
        }

        private async Task RecordRevisionAsync(BusinessContext ctx, RosterAssignment assignment,
            string field, string? oldValue, string? newValue, string reason, CancellationToken ct)
        {
            Revisions.Add(new RosterAssignmentRevision
            {
                CompanyID = ctx.CompanyId,
                AssignmentID = assignment.ID,
                ChangedField = field,
                OldValue = oldValue,
                NewValue = newValue,
                Reason = reason.Trim(),
                ChangedBy = ctx.EmployeeId,
                ChangedAt = _clock.Now,
            });
            await _db.SaveChangesAsync(ct);
        }
    }
}
