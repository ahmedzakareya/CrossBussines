using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Hr;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL.Hr
{
    // ============================================================================================
    // THE CANONICAL ATTENDANCE BASELINE — what an employee was actually expected to work.
    //
    // THE DEFECT THIS CLOSES. AttendanceService computed lateness as
    //
    //      checkIn.TimeOfDay - (policy.WorkStartTime + grace)
    //
    // which asks "how far past 08:00 is this clock reading" and has two independent faults. It
    // consults the POLICY even when a published roster says something different, and it compares
    // TIMES OF DAY rather than instants. An employee rostered 22:00–06:00 who arrives exactly on
    // time was recorded as fourteen hours late, and their 06:00 check-out was recorded as eleven
    // hours early. Both numbers then reached the monthly summary as fact.
    //
    // ONE RESOLVER, AND THE REASON IT IS ONE. The brief's rule — "do not duplicate this decision in
    // controllers/reports/payroll later" — is the whole design. The moment a second place decides
    // what someone was supposed to work, the screen and the report begin to disagree and there is no
    // way to say which is right. So the baseline is resolved here, the arithmetic is done in
    // AttendanceMath, and every consumer (recording, planned-vs-actual, reporting, and later payroll)
    // calls the same two.
    //
    // PRIORITY, AND WHY THE POLICY IS NOT DEMOTED. A published roster assignment wins; otherwise the
    // attendance policy answers. Roster is an explicit override, not a replacement — an employee with
    // no roster is not an error and must keep working exactly as before. That is why Source is on the
    // result: a caller can tell which authority answered without guessing.
    //
    // MATERIALISED INSTANTS ONLY. The resolver reads RosterAssignment.PlannedStart/PlannedEnd and
    // NEVER WorkShift.StartTime/EndTime. Those columns were materialised at assignment time precisely
    // so that editing "Night shift" to start at 23:00 next month cannot rewrite what last month's
    // attendance meant. Reading the shift definition here would undo that guarantee silently, which
    // is why there is no navigation to it in this file at all.
    // ============================================================================================

    public static class AttendanceBaselineSource
    {
        /// A published roster assignment defined the planned window.
        public const string Roster = "Roster";

        /// No published roster for the date; the employee's attendance policy answered.
        public const string Policy = "Policy";

        /// Neither exists. Not an error — an employee with no policy and no roster simply has no
        /// planned window, and everything derived from one stays null rather than becoming zero.
        /// Zero would read as "expected to work no time", which is a different and false claim.
        public const string None = "None";
    }

    public sealed class AttendanceBaseline
    {
        public required string Source { get; init; }

        // Real instants, not times of day. For an overnight shift PlannedEnd falls on the NEXT
        // calendar day, which is what makes every comparison downstream a simple subtraction.
        public DateTime? PlannedStart { get; init; }
        public DateTime? PlannedEnd { get; init; }

        public int BreakMinutes { get; init; }

        // GRACE ALWAYS COMES FROM THE POLICY, even when the roster supplied the window. A shift
        // definition says when work starts; it does not say how forgiving the employer is about
        // arriving a few minutes after that. Those are separate policies and conflating them would
        // silently drop an employer's grace period the day somebody was first rostered.
        public int GraceMinutes { get; init; }

        public int? RosterAssignmentId { get; init; }
        public int? WorkShiftId { get; init; }

        /// The window less the break. Null when there is no window at all.
        public int? ExpectedMinutes =>
            PlannedStart.HasValue && PlannedEnd.HasValue
                ? Math.Max(0, (int)(PlannedEnd.Value - PlannedStart.Value).TotalMinutes - Math.Max(0, BreakMinutes))
                : null;

        public bool HasPlan => PlannedStart.HasValue && PlannedEnd.HasValue;

        public static AttendanceBaseline Nothing() => new() { Source = AttendanceBaselineSource.None };
    }

    public interface IAttendanceBaselineResolver
    {
        Task<AttendanceBaseline> ResolveAsync(int companyId, int employeeId, DateTime workDate,
            CancellationToken ct = default);

        /// The same answer for many (employee, date) pairs in one pass. Reporting needs a month of
        /// rows and resolving them one at a time would be a query per row — so this exists to keep
        /// the canonical path the FAST path. If re-deriving the baseline were expensive, somebody
        /// would eventually inline a cheaper formula somewhere, and the duplication this file exists
        /// to prevent would come back through the performance door.
        Task<IReadOnlyDictionary<(int EmployeeId, DateTime WorkDate), AttendanceBaseline>> ResolveManyAsync(
            int companyId, IReadOnlyCollection<int> employeeIds, DateTime from, DateTime to,
            CancellationToken ct = default);
    }

    public sealed class AttendanceBaselineResolver : IAttendanceBaselineResolver
    {
        private readonly CrossDbContext _db;

        public AttendanceBaselineResolver(CrossDbContext db) { _db = db; }

        public async Task<AttendanceBaseline> ResolveAsync(int companyId, int employeeId,
            DateTime workDate, CancellationToken ct = default)
        {
            if (companyId <= 0 || employeeId <= 0) return AttendanceBaseline.Nothing();

            var policy = await PolicyForAsync(employeeId, ct);
            int grace = policy?.AllowedGraceMinutes ?? 0;

            var assignment = await PublishedAssignmentQuery(companyId, workDate.Date, workDate.Date)
                .Where(a => a.EmployeeID == employeeId)
                .OrderBy(a => a.PlannedStart)
                .Select(a => new { a.ID, a.ShiftID, a.PlannedStart, a.PlannedEnd, a.Shift!.BreakMinutes })
                .FirstOrDefaultAsync(ct);

            if (assignment != null)
            {
                return new AttendanceBaseline
                {
                    Source = AttendanceBaselineSource.Roster,
                    PlannedStart = assignment.PlannedStart,
                    PlannedEnd = assignment.PlannedEnd,
                    BreakMinutes = assignment.BreakMinutes,
                    GraceMinutes = grace,
                    RosterAssignmentId = assignment.ID,
                    WorkShiftId = assignment.ShiftID,
                };
            }

            return FromPolicy(policy, workDate);
        }

        public async Task<IReadOnlyDictionary<(int, DateTime), AttendanceBaseline>> ResolveManyAsync(
            int companyId, IReadOnlyCollection<int> employeeIds, DateTime from, DateTime to,
            CancellationToken ct = default)
        {
            var result = new Dictionary<(int, DateTime), AttendanceBaseline>();
            if (companyId <= 0 || employeeIds.Count == 0) return result;

            var policies = await PoliciesForAsync(employeeIds, ct);

            var assignments = await PublishedAssignmentQuery(companyId, from.Date, to.Date)
                .Where(a => employeeIds.Contains(a.EmployeeID))
                .Select(a => new
                {
                    a.ID, a.EmployeeID, a.WorkDate, a.ShiftID,
                    a.PlannedStart, a.PlannedEnd, a.Shift!.BreakMinutes,
                })
                .ToListAsync(ct);

            foreach (var a in assignments)
            {
                var key = (a.EmployeeID, a.WorkDate.Date);

                // Two published assignments on one date is a roster conflict the roster screen already
                // reports. Here the EARLIER one wins deterministically rather than whichever the
                // database happened to return first — an arbitrary pick would make the same day's
                // lateness change between two runs with no edit in between.
                if (result.TryGetValue(key, out var existing)
                    && existing.PlannedStart <= a.PlannedStart) continue;

                policies.TryGetValue(a.EmployeeID, out var p);
                result[key] = new AttendanceBaseline
                {
                    Source = AttendanceBaselineSource.Roster,
                    PlannedStart = a.PlannedStart,
                    PlannedEnd = a.PlannedEnd,
                    BreakMinutes = a.BreakMinutes,
                    GraceMinutes = p?.AllowedGraceMinutes ?? 0,
                    RosterAssignmentId = a.ID,
                    WorkShiftId = a.ShiftID,
                };
            }

            // Policy fallback fills every remaining (employee, date) in the window.
            for (var d = from.Date; d <= to.Date; d = d.AddDays(1))
            {
                foreach (var employeeId in employeeIds)
                {
                    var key = (employeeId, d);
                    if (result.ContainsKey(key)) continue;
                    policies.TryGetValue(employeeId, out var p);
                    result[key] = FromPolicy(p, d);
                }
            }

            return result;
        }

        // PUBLISHED, PLANNED, AND THIS COMPANY'S. Each predicate earns its place:
        //   · CompanyID       — the tenant boundary; without it a foreign roster could set the
        //                       baseline for this company's attendance;
        //   · Status Planned  — a cancelled assignment is not a plan anybody was held to;
        //   · Period Published— a DRAFT roster is a working document. Letting a draft drive real
        //                       attendance would mean an unfinished plan silently changed what people
        //                       were judged against, which is exactly what publishing exists to gate.
        private IQueryable<RosterAssignment> PublishedAssignmentQuery(int companyId, DateTime from, DateTime to) =>
            _db.Set<RosterAssignment>().AsNoTracking()
                .Where(a => a.CompanyID == companyId
                         && a.WorkDate >= from && a.WorkDate <= to
                         && a.Status == RosterAssignmentStatus.Planned
                         && a.Period!.Status == RosterPeriodStatus.Published);

        // The policy's weekly pattern turned into instants for ONE date. WorkEndTime at or before
        // WorkStartTime means the policy itself describes an overnight pattern, so the end rolls to
        // the next day — the same rule a roster shift uses, stated once in WorkShiftDefaults.
        private static AttendanceBaseline FromPolicy(Models.Context.Admin.AttendancePolicies? policy, DateTime workDate)
        {
            if (policy == null) return AttendanceBaseline.Nothing();

            var start = workDate.Date + policy.WorkStartTime;
            var end = workDate.Date + policy.WorkEndTime;
            if (WorkShiftDefaults.CrossesMidnight(policy.WorkStartTime, policy.WorkEndTime))
                end = end.AddDays(1);

            return new AttendanceBaseline
            {
                Source = AttendanceBaselineSource.Policy,
                PlannedStart = start,
                PlannedEnd = end,
                BreakMinutes = policy.BreakDurationMinutes ?? 0,
                GraceMinutes = policy.AllowedGraceMinutes,
            };
        }

        // Mirrors AttendanceService.PolicyForAsync deliberately rather than calling it: the resolver
        // is what AttendanceService will depend on, so depending back would be a cycle. Two lines of
        // duplicated LOOKUP is not the duplication the brief forbids — that is about duplicating the
        // baseline DECISION, which lives only here.
        private async Task<Models.Context.Admin.AttendancePolicies?> PolicyForAsync(int employeeId, CancellationToken ct)
        {
            var assignment = await _db.PolicyAssignments.AsNoTracking()
                .FirstOrDefaultAsync(p => p.EmployeeID == employeeId, ct);
            if (assignment == null) return null;

            return await _db.AttendancePolicies.AsNoTracking()
                .FirstOrDefaultAsync(x => x.LeavePolicyTypeID == assignment.LeavePolicyTypeID, ct);
        }

        private async Task<Dictionary<int, Models.Context.Admin.AttendancePolicies>> PoliciesForAsync(
            IReadOnlyCollection<int> employeeIds, CancellationToken ct)
        {
            var assignments = await _db.PolicyAssignments.AsNoTracking()
                .Where(p => employeeIds.Contains(p.EmployeeID))
                .Select(p => new { p.EmployeeID, p.LeavePolicyTypeID })
                .ToListAsync(ct);
            if (assignments.Count == 0) return new Dictionary<int, Models.Context.Admin.AttendancePolicies>();

            var typeIds = assignments.Select(a => a.LeavePolicyTypeID).Distinct().ToList();
            var policies = await _db.AttendancePolicies.AsNoTracking()
                .Where(x => typeIds.Contains(x.LeavePolicyTypeID))
                .ToListAsync(ct);

            var byType = policies.GroupBy(p => p.LeavePolicyTypeID)
                .ToDictionary(g => g.Key, g => g.First());

            var result = new Dictionary<int, Models.Context.Admin.AttendancePolicies>();
            foreach (var a in assignments)
            {
                if (result.ContainsKey(a.EmployeeID)) continue;
                if (byType.TryGetValue(a.LeavePolicyTypeID, out var p)) result[a.EmployeeID] = p;
            }
            return result;
        }
    }
}
