namespace CrossBuy.BL.Hr
{
    // ============================================================================================
    // THE ONE ATTENDANCE FORMULA.
    //
    // Pure and static on purpose: no database, no clock, no context. Everything it needs is an
    // argument, so the same inputs always produce the same numbers whether the caller is recording
    // attendance, drawing the planned-vs-actual screen, running a report, or — later — feeding
    // payroll. That is the entire point of the brief's "do not leave one calculation in
    // AttendanceService and another in Reporting": there is nowhere else for the arithmetic to live.
    //
    // WHAT CHANGED FROM THE OLD ARITHMETIC. Every comparison is now between INSTANTS. The previous
    // code subtracted times of day, which silently assumed the shift began and ended on the same
    // calendar date — true for 08:00–17:00 and false for every night shift ever worked.
    // ============================================================================================

    public sealed class AttendanceComputation
    {
        public required string Source { get; init; }

        public DateTime? PlannedStart { get; init; }
        public DateTime? PlannedEnd { get; init; }
        public int? ExpectedMinutes { get; init; }

        public DateTime? ActualIn { get; init; }

        /// The check-out AS COMPARED — see NormaliseCheckOut. May be a day later than the value the
        /// caller supplied, and that is the corrected reading rather than a mutation of their data.
        public DateTime? ActualOut { get; init; }

        public int WorkedMinutes { get; init; }
        public int LateMinutes { get; init; }
        public int EarlyLeaveMinutes { get; init; }
        public int OvertimeCandidateMinutes { get; init; }

        public bool IsAbsent { get; init; }
        public bool OnApprovedLeave { get; init; }
        public bool IsHoliday { get; init; }
        public bool IsRestDay { get; init; }

        /// The status string AttendanceRecord already stores. Unchanged vocabulary —
        /// Present | Late | Absent | Holiday | RestDay | Leave — because other screens read it.
        public required string Status { get; init; }

        public decimal WorkedHours => Math.Round(WorkedMinutes / 60m, 2);

        // ---- the future-payroll contract (§15) -------------------------------------------------
        //
        // Payroll must be able to read workforce FACTS without reinterpreting a roster or re-deriving
        // a baseline. These four properties plus OnApprovedLeave/IsAbsent are that contract. None of
        // them is money, and none of them decides whether anything is payable — that judgement is
        // payroll's, and putting it here would make this class a payroll engine by accident.

        /// Worked minutes up to what was planned. Minutes beyond the plan are NOT here; they are
        /// overtime candidates, counted separately so nobody can accidentally pay them twice.
        public int RegularMinutes => ExpectedMinutes is int expected
            ? Math.Min(WorkedMinutes, expected)
            : WorkedMinutes;

        /// Explicitly a CANDIDATE. Whether these minutes are payable, and at what multiplier, is a
        /// payroll policy question this batch deliberately does not answer.
        public int OvertimeCandidate => OvertimeCandidateMinutes;
    }

    public static class AttendanceMath
    {
        /// <summary>
        /// A check-out that reads EARLIER than its check-in belongs to the following day.
        ///
        /// This is the cross-midnight rule the brief states outright: a 06:05 check-out must not
        /// appear before a 22:00 check-in. Callers hand attendance a work DATE plus two clock
        /// readings, and a device that stamps 06:05 against the shift's own date produces a
        /// check-out eight hours before the check-in — which the old code turned into negative
        /// worked hours and a colossal "early leave".
        ///
        /// Only a STRICTLY earlier check-out is rolled. Equal readings stay equal and mean zero,
        /// because treating them as a 24-hour shift would invent a full day of work out of what is
        /// far more likely a duplicate stamp.
        /// </summary>
        public static DateTime? NormaliseCheckOut(DateTime? checkIn, DateTime? checkOut)
        {
            if (checkIn is not DateTime start || checkOut is not DateTime end) return checkOut;
            return end < start ? end.AddDays(1) : end;
        }

        public static AttendanceComputation Compute(
            AttendanceBaseline baseline,
            DateTime? checkIn,
            DateTime? checkOut,
            bool isHoliday = false,
            bool isRestDay = false,
            bool onApprovedLeave = false)
        {
            ArgumentNullException.ThrowIfNull(baseline);

            var actualOut = NormaliseCheckOut(checkIn, checkOut);

            int worked = 0;
            if (checkIn is DateTime inAt && actualOut is DateTime outAt && outAt > inAt)
                worked = (int)Math.Round((outAt - inAt).TotalMinutes);

            int late = 0, early = 0, overtime = 0;

            // Lateness and its neighbours only mean anything against a plan. With no baseline the
            // figures stay ZERO rather than being invented from a default working day — a zero here
            // says "not measured", and a fabricated 09:00 start would say something false.
            if (baseline.HasPlan)
            {
                var plannedStart = baseline.PlannedStart!.Value;
                var plannedEnd = baseline.PlannedEnd!.Value;

                if (checkIn is DateTime arrival)
                {
                    // GRACE IS PRESERVED EXACTLY AS THE POLICY DEFINED IT. The brief says keep the
                    // grace rules unless they are shown wrong, and they are not wrong — they were
                    // being applied to the wrong baseline, which is a different fault.
                    var forgivenUntil = plannedStart.AddMinutes(Math.Max(0, baseline.GraceMinutes));
                    if (arrival > forgivenUntil)
                        late = (int)Math.Round((arrival - forgivenUntil).TotalMinutes);
                }

                if (actualOut is DateTime departure)
                {
                    if (departure < plannedEnd)
                        early = (int)Math.Round((plannedEnd - departure).TotalMinutes);
                    else if (departure > plannedEnd)
                        overtime = (int)Math.Round((departure - plannedEnd).TotalMinutes);
                }
            }

            // ---- status ------------------------------------------------------------------------
            //
            // Order matters and is unchanged from the behaviour already in AttendanceService: a
            // holiday outranks a rest day, which outranks approved leave, which outranks absence.
            // Approved leave sitting ABOVE absence is what stops an authorised day off being
            // reported as an unexplained no-show.
            string status;
            bool absent = false;
            if (isHoliday) status = "Holiday";
            else if (isRestDay) status = "RestDay";
            else if (onApprovedLeave) status = "Leave";
            else if (checkIn == null)
            {
                // ABSENCE REQUIRES A PLAN. Somebody who was never expected has not failed to appear,
                // and calling that absence would fill the report with people who were never rostered.
                absent = baseline.HasPlan;
                status = absent ? "Absent" : "Present";
            }
            else status = late > 0 ? "Late" : "Present";

            return new AttendanceComputation
            {
                Source = baseline.Source,
                PlannedStart = baseline.PlannedStart,
                PlannedEnd = baseline.PlannedEnd,
                ExpectedMinutes = baseline.ExpectedMinutes,
                ActualIn = checkIn,
                ActualOut = actualOut,
                WorkedMinutes = worked,
                LateMinutes = late,
                EarlyLeaveMinutes = early,
                OvertimeCandidateMinutes = overtime,
                IsAbsent = absent,
                OnApprovedLeave = onApprovedLeave,
                IsHoliday = isHoliday,
                IsRestDay = isRestDay,
                Status = status,
            };
        }
    }
}
