using CrossBuy.Models.Context.Admin;

namespace CrossBuy.Models.Context.Hr
{
    // ============================================================================================
    // ROSTER / SHIFT MANAGEMENT — who is expected to work, where, when, and under which shift.
    //
    // WHAT DISCOVERY FOUND, AND WHY THIS IS NOT A DUPLICATE.
    //
    // Three existing concepts sit close to this one, and each was checked before a table was added:
    //
    //   · PosShift — NOT a work shift. It is a cash-drawer session on a terminal (TerminalId,
    //     OpeningFloat, CashVariance, VarianceJournalEntryId). Its ShiftType ("Morning"/"Evening") is
    //     a free label on a till, not a schedule. The entity here is deliberately named WorkShift so
    //     the two never read as the same thing in a query or a stack trace.
    //
    //   · AttendancePolicies — the closest call, and worth stating plainly. It ALREADY carries
    //     WorkStartTime, WorkEndTime, WorkHoursPerDay, BreakStartTime/EndTime/DurationMinutes and a
    //     WorkOnSunday..WorkOnSaturday pattern. It is a real working-hours concept and this model does
    //     not replace it. It cannot, however, express a roster: it holds exactly ONE start time, so it
    //     can say "Alice works 08:00–17:00 on weekdays" and can never say "Alice works Morning on
    //     Monday and Night on Thursday". It also has no overnight notion and no CompanyID of its own
    //     (it hangs off Policies, whose tenancy is separately tracked debt).
    //     THE RELATIONSHIP: AttendancePolicies remains the employee's default contractual pattern. A
    //     RosterAssignment is a per-date override of it. Absence of an assignment is not absence of a
    //     schedule — it means the policy still applies.
    //
    //   · AttendanceRecord — authoritative for what ACTUALLY happened (CheckIn, CheckOut,
    //     LateMinutes, WorkedHours). Nothing here writes it or changes it. The two meet on the natural
    //     key (CompanyID, EmployeeID, WorkDate), which both already carry, so planned-vs-actual needs
    //     no foreign key and no change to Attendance at all.
    //
    // WHAT THIS MODEL DELIBERATELY DOES NOT HOLD:
    //   · no recurrence-rule engine (explicitly out of scope for this batch);
    //   · no computed "IsConflicted" column — a conflict is a function of other rows and goes stale
    //     the moment one of them moves. It is detected on demand and returned, never stored;
    //   · no money. Overtime here is a CANDIDATE, never an amount.
    // ============================================================================================

    public static class WorkShiftDefaults
    {
        // A shift window that ends at or before it starts has run past midnight. Stored nowhere: a
        // flag beside StartTime/EndTime is a second answer that can contradict the first. Expressed
        // once, here, so every caller agrees. 08:00→08:00 reads as a full 24 hours, which is the
        // correct reading for a continuous-cover shift.
        public static bool CrossesMidnight(TimeSpan start, TimeSpan end) => end <= start;

        public static int WindowMinutes(TimeSpan start, TimeSpan end)
        {
            var span = end - start;
            if (span <= TimeSpan.Zero) span += TimeSpan.FromDays(1);
            return (int)span.TotalMinutes;
        }
    }

    public static class RosterPeriodStatus
    {
        // Draft is editable. Published is visible to the workforce and only changes with evidence.
        // There is no "Approved" between them: discovery found no approval engine for scheduling, and
        // the brief forbids building one. A period is either a working document or a commitment.
        public const string Draft = "Draft";
        public const string Published = "Published";
        public const string Closed = "Closed";

        public static readonly string[] All = { Draft, Published, Closed };
    }

    public static class RosterAssignmentStatus
    {
        public const string Planned = "Planned";
        public const string Cancelled = "Cancelled";

        public static readonly string[] All = { Planned, Cancelled };

        // A cancelled assignment is kept, never deleted — "I was never scheduled for that" is the
        // dispute this model exists to settle, and a deleted row cannot answer it.
        public static bool CountsAsScheduled(string? status) => status == Planned;
    }

    // ============================================================================================
    // A SHIFT DEFINITION — a reusable named time window, per company.
    // ============================================================================================
    public class WorkShift
    {
        public int ID { get; set; }
        public int CompanyID { get; set; }

        // Stable machine code ("MORNING", "NIGHT") — survives a rename, which is what makes reporting
        // across periods possible.
        public string Code { get; set; } = "";

        public string NameAr { get; set; } = "";
        public string NameEn { get; set; } = "";

        // Local wall-clock times. Not UTC: a shift is "the night shift starts at 22:00" at the site,
        // and storing an instant would make the definition depend on the day it was written.
        public TimeSpan StartTime { get; set; }
        public TimeSpan EndTime { get; set; }

        // Unpaid break deducted from the window. Break RULES (when the break falls, how many) live in
        // AttendancePolicies and are not re-modelled here; a shift needs only the duration to state an
        // expected working figure.
        public int BreakMinutes { get; set; }

        public bool IsActive { get; set; } = true;

        public int? CreatedBy { get; set; }
        public DateTime? CreatedAt { get; set; }
        public int? UpdatedBy { get; set; }
        public DateTime? UpdatedAt { get; set; }

        public bool CrossesMidnight => WorkShiftDefaults.CrossesMidnight(StartTime, EndTime);

        public int WindowMinutes => WorkShiftDefaults.WindowMinutes(StartTime, EndTime);

        // The expected working duration: the window less the break. COMPUTED, not stored, for the
        // same reason CrossesMidnight is — a stored duration silently disagrees with its own start and
        // end the first time somebody edits one of them.
        public int ExpectedWorkMinutes => Math.Max(0, WindowMinutes - Math.Max(0, BreakMinutes));
    }

    // ============================================================================================
    // A ROSTER PERIOD — the unit that gets published.
    // ============================================================================================
    public class RosterPeriod
    {
        public int ID { get; set; }
        public int CompanyID { get; set; }

        public string NameAr { get; set; } = "";
        public string NameEn { get; set; } = "";

        // Inclusive on both ends. A roster is expressed in whole days.
        public DateTime StartDate { get; set; }
        public DateTime EndDate { get; set; }

        // Optional narrowing. A company may roster per branch or company-wide; null means the latter.
        public int? BranchID { get; set; }
        public Branch? Branch { get; set; }

        public string Status { get; set; } = RosterPeriodStatus.Draft;

        public DateTime? PublishedAt { get; set; }
        public int? PublishedBy { get; set; }

        public string? Notes { get; set; }

        public int? CreatedBy { get; set; }
        public DateTime? CreatedAt { get; set; }
        public int? UpdatedBy { get; set; }
        public DateTime? UpdatedAt { get; set; }

        public ICollection<RosterAssignment> Assignments { get; set; } = new List<RosterAssignment>();

        public bool IsPublished => Status == RosterPeriodStatus.Published;
    }

    // ============================================================================================
    // ONE ASSIGNMENT — this employee, this date, this shift.
    // ============================================================================================
    public class RosterAssignment
    {
        public int ID { get; set; }

        // Repeated from the period rather than reached through it: "everyone scheduled in this company
        // next Tuesday" is a real query and a join-only boundary would leave it silently unfiltered
        // the first time somebody omitted the Include.
        public int CompanyID { get; set; }

        public int PeriodID { get; set; }
        public RosterPeriod? Period { get; set; }

        public int EmployeeID { get; set; }
        public Employee? Employee { get; set; }

        // Where the person is expected. Null means "wherever the employee normally is" — the model
        // does not invent a location the business did not state.
        public int? BranchID { get; set; }
        public Branch? Branch { get; set; }

        // The calendar day the shift BEGINS on. An overnight shift belongs to the day it starts, which
        // is how a roster is read aloud ("you're on nights Thursday") and how PlannedStart is derived.
        public DateTime WorkDate { get; set; }

        public int ShiftID { get; set; }
        public WorkShift? Shift { get; set; }

        // MATERIALISED, not derived at read time. The shift definition is provenance; these two are
        // the commitment. Editing "Night" from 22:00 to 23:00 next month must not silently rewrite
        // what a published roster promised somebody last week — the same reasoning that keeps an
        // onboarding plan from being rewritten by a later template edit.
        public DateTime PlannedStart { get; set; }
        public DateTime PlannedEnd { get; set; }

        public string Status { get; set; } = RosterAssignmentStatus.Planned;

        public string? Note { get; set; }

        public int? CreatedBy { get; set; }
        public DateTime? CreatedAt { get; set; }
        public int? UpdatedBy { get; set; }
        public DateTime? UpdatedAt { get; set; }

        public ICollection<RosterAssignmentRevision> Revisions { get; set; }
            = new List<RosterAssignmentRevision>();

        public int PlannedMinutes => (int)(PlannedEnd - PlannedStart).TotalMinutes;
    }

    // ============================================================================================
    // EVIDENCE. Why this table exists rather than three columns on the assignment.
    //
    // The requirement is that published history must not mutate SILENTLY. A RevisedAt/RevisedBy/Reason
    // triple on the assignment satisfies that exactly once: the second change overwrites the first,
    // and the row then testifies only to the most recent edit. A roster is the document people argue
    // about after the fact ("nobody told me the shift moved"), so the second change is precisely the
    // one worth keeping.
    //
    // Only PUBLISHED assignments generate a revision. Editing a draft is not history, it is drafting,
    // and recording every keystroke of it would bury the entries that matter.
    // ============================================================================================
    public class RosterAssignmentRevision
    {
        public int ID { get; set; }
        public int CompanyID { get; set; }

        public int AssignmentID { get; set; }
        public RosterAssignment? Assignment { get; set; }

        // What moved. Free-form field name plus before/after as text: the alternative is a column per
        // trackable field, which turns every future field into a schema change.
        public string ChangedField { get; set; } = "";
        public string? OldValue { get; set; }
        public string? NewValue { get; set; }

        // Required by the service. A change to a published roster without a stated reason is exactly
        // the silent mutation this table exists to prevent.
        public string Reason { get; set; } = "";

        public int? ChangedBy { get; set; }
        public DateTime ChangedAt { get; set; }
    }
}
