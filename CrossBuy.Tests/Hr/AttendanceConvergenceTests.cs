using CrossBuy.BL;
using CrossBuy.BL.Hr;
using CrossBuy.BL.Platform;
using CrossBuy.Models.Context.Admin;
using CrossBuy.Models.Context.Hr;
using CrossBuy.Models.Context.Platform;
using CrossBuy.Models.Platform;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CrossBuy.Tests.Hr
{
    // ============================================================================================
    // ROSTER ↔ ATTENDANCE CONVERGENCE.
    //
    // THE DEFECT UNDER TEST, stated once. Attendance computed lateness as
    //     checkIn.TimeOfDay - (policy.WorkStartTime + grace)
    // which consulted the POLICY even when a published roster said otherwise, and compared TIMES OF
    // DAY rather than instants. An employee correctly rostered 22:00-06:00 was recorded as fourteen
    // hours late and eleven hours early.
    //
    // The fixture has two companies and the neighbour is not empty, because in a single-company
    // fixture every row is in scope and a missing tenant predicate looks exactly like a correct one.
    // JobTitle and Branch rows are seeded even where unmentioned: Employee.JobTitle is a REQUIRED
    // reference, so any projection through it is an INNER JOIN and an unseeded lookup row silently
    // joins every employee away, turning a real assertion into a green vacuum.
    // ============================================================================================
    public class AttendanceConvergenceTests
    {
        private const int CompanyA = 41;
        private const int CompanyB = 77;

        private const int Alice = 501;    // company A, ordinary employee, no HR role
        private const int Bob = 502;      // company A, ordinary employee
        private const int Officer = 510;  // company A, HrOfficer
        private const int Mallory = 901;  // company B

        private const int BranchA = 11;
        private const int PolicyType = 7;

        private static readonly DateTime Day = new(2026, 9, 1);   // a Tuesday

        private sealed class FixedClock : IRosterClock
        {
            public DateTime Now => Day.AddHours(9);
            public DateTime Today => Day;
        }

        // ---- fixture ---------------------------------------------------------------------------

        private static PlatformTestHost Seed(bool withPolicy = true, int graceMinutes = 0)
        {
            var host = new PlatformTestHost();
            var db = host.Db;

            db.JobTitles.Add(new JobTitle { ID = 1, Title = "Ops", TitleAr = "تشغيل", Description = "" });
            db.Branches.Add(new Branch
            {
                ID = BranchA, CompanyID = CompanyA, Name = "Main", NameAr = "الرئيسي",
                Location = "Main", CountryID = 1, PhoneNumber = "0", Email = "b@x.local", Description = "",
            });
            db.Employee.AddRange(
                Person(Alice, CompanyA), Person(Bob, CompanyA),
                Person(Officer, CompanyA), Person(Mallory, CompanyB));
            db.SaveChanges();

            if (withPolicy)
            {
                // The standard 08:00–17:00 policy every test measures the roster against.
                db.AttendancePolicies.Add(new AttendancePolicies
                {
                    LeavePolicyTypeID = PolicyType,
                    WorkStartTime = TimeSpan.FromHours(8),
                    WorkEndTime = TimeSpan.FromHours(17),
                    AllowedGraceMinutes = graceMinutes,
                    WorkHoursPerDay = 8,
                    WorkOnSunday = true, WorkOnMonday = true, WorkOnTuesday = true,
                    WorkOnWednesday = true, WorkOnThursday = true,
                    WorkOnFriday = false, WorkOnSaturday = false,
                });
                foreach (var id in new[] { Alice, Bob, Officer, Mallory })
                    db.PolicyAssignments.Add(new PolicyAssignments { EmployeeID = id, LeavePolicyTypeID = PolicyType });
                db.SaveChanges();
            }
            return host;
        }

        private static Employee Person(int id, int companyId) => new()
        {
            ID = id, EmpCompanyID = companyId, IsActive = true,
            FullName = "E" + id, FullNameEn = "E" + id, FirstName = "E", LastName = id.ToString(),
            Email = id + "@x.local", PhoneNumber = "09" + id, Address = "", Gender = "F",
            MaritalStatus = "Single", ProfileImage = "", UserId = "u" + id, JobTitleID = 1,
            DateOfJoining = new DateTime(2026, 1, 1),
        };

        private static void GiveHrRole(PlatformTestHost host, int companyId, int employeeId, string role)
        {
            host.Db.Set<PlatformRoleAssignment>().Add(new()
            {
                CompanyID = companyId, Scope = EntityRegistry.ScopeHr,
                PrincipalType = PlatformPrincipalTypes.Employee, PrincipalId = employeeId,
                Role = role, IsActive = true,
            });
            host.Db.SaveChanges();
        }

        // Http, not Worker: ModuleAccessServiceBase refuses a Worker outright, which would make every
        // refusal pass without exercising anything.
        private static BusinessContext Ctx(int companyId, int? employeeId) => new()
        {
            CompanyId = companyId, EmployeeId = employeeId,
            UserId = "u" + employeeId, Source = BusinessContextSource.Http,
        };

        private static AttendanceBaselineResolver Resolver(PlatformTestHost host)
            => new(host.Db);

        private static RosterService Roster(PlatformTestHost host, BusinessContext? ctx)
        {
            var hr = new HrAccessService(host.Db,
                new PlatformRoleDirectory(host.Db, NullLogger<PlatformRoleDirectory>.Instance),
                new OrgHierarchy(host.Db, NullLogger<OrgHierarchy>.Instance),
                new BootstrapAccessPolicyReader(host.Db, NullLogger<BootstrapAccessPolicyReader>.Instance),
                NullLogger<HrAccessService>.Instance);

            var accessor = ctx == null ? StubContextAccessor.Unresolved() : new StubContextAccessor(ctx);
            return new RosterService(host.Db, accessor, hr, new FixedClock(), Resolver(host));
        }

        /// Publishes a roster putting <paramref name="employeeId"/> on the given window for Day.
        private static async Task<(int periodId, int shiftId, int assignmentId)> PublishShiftAsync(
            PlatformTestHost host, int employeeId, TimeSpan start, TimeSpan end, int breakMinutes = 0)
        {
            GiveHrRole(host, CompanyA, Officer, HrRoles.HrOfficer);

            var shift = new WorkShift
            {
                CompanyID = CompanyA, Code = "S" + start.Hours, NameAr = "مناوبة", NameEn = "Shift",
                StartTime = start, EndTime = end, BreakMinutes = breakMinutes, IsActive = true,
            };
            host.Db.Set<WorkShift>().Add(shift);
            host.Db.SaveChanges();

            var svc = Roster(host, Ctx(CompanyA, Officer));
            var period = (await svc.CreatePeriodAsync("ف", "P", Day, Day.AddDays(6), null)).Period!;
            var assignment = (await svc.AssignAsync(period.ID, employeeId, Day, shift.ID, null, null, null)).Assignment!;
            var published = await svc.PublishAsync(period.ID);
            Assert.True(published.Ok);

            return (period.ID, shift.ID, assignment.ID);
        }

        // =========================================================================================
        // §17 — BASELINE RESOLUTION
        // =========================================================================================

        [Fact]
        public async Task With_no_roster_the_attendance_policy_defines_the_baseline()
        {
            var host = Seed();

            var baseline = await Resolver(host).ResolveAsync(CompanyA, Alice, Day);

            // Roster is an override, not a replacement. An employee nobody rostered keeps working
            // exactly as before, which is the whole of §5.
            Assert.Equal(AttendanceBaselineSource.Policy, baseline.Source);
            Assert.Equal(Day.AddHours(8), baseline.PlannedStart);
            Assert.Equal(Day.AddHours(17), baseline.PlannedEnd);
            Assert.Null(baseline.RosterAssignmentId);
        }

        [Fact]
        public async Task A_published_roster_overrides_the_policy()
        {
            var host = Seed();
            var (_, shiftId, assignmentId) = await PublishShiftAsync(host, Alice, TimeSpan.FromHours(22), TimeSpan.FromHours(6));

            var baseline = await Resolver(host).ResolveAsync(CompanyA, Alice, Day);

            Assert.Equal(AttendanceBaselineSource.Roster, baseline.Source);
            Assert.Equal(Day.AddHours(22), baseline.PlannedStart);
            Assert.Equal(Day.AddDays(1).AddHours(6), baseline.PlannedEnd);   // NEXT day
            Assert.Equal(assignmentId, baseline.RosterAssignmentId);
            Assert.Equal(shiftId, baseline.WorkShiftId);
        }

        [Fact]
        public async Task A_draft_roster_is_ignored_and_the_policy_still_answers()
        {
            var host = Seed();
            GiveHrRole(host, CompanyA, Officer, HrRoles.HrOfficer);

            var shift = new WorkShift
            {
                CompanyID = CompanyA, Code = "N", NameAr = "ليل", NameEn = "Night",
                StartTime = TimeSpan.FromHours(22), EndTime = TimeSpan.FromHours(6), IsActive = true,
            };
            host.Db.Set<WorkShift>().Add(shift);
            host.Db.SaveChanges();

            var svc = Roster(host, Ctx(CompanyA, Officer));
            var period = (await svc.CreatePeriodAsync("ف", "P", Day, Day.AddDays(6), null)).Period!;
            await svc.AssignAsync(period.ID, Alice, Day, shift.ID, null, null, null);
            // deliberately NOT published

            var baseline = await Resolver(host).ResolveAsync(CompanyA, Alice, Day);

            // A draft is a working document. Letting one drive real attendance would mean an
            // unfinished plan silently changed what somebody was judged against.
            Assert.Equal(AttendanceBaselineSource.Policy, baseline.Source);
        }

        [Fact]
        public async Task A_cancelled_assignment_falls_back_to_the_policy()
        {
            var host = Seed();
            var (_, _, assignmentId) = await PublishShiftAsync(host, Alice, TimeSpan.FromHours(22), TimeSpan.FromHours(6));

            var svc = Roster(host, Ctx(CompanyA, Officer));
            Assert.True((await svc.CancelAssignmentAsync(assignmentId, "غير مطلوب")).Ok);

            var baseline = await Resolver(host).ResolveAsync(CompanyA, Alice, Day);
            Assert.Equal(AttendanceBaselineSource.Policy, baseline.Source);
        }

        [Fact]
        public async Task Another_companys_roster_cannot_define_this_companys_baseline()
        {
            var host = Seed();
            await PublishShiftAsync(host, Alice, TimeSpan.FromHours(22), TimeSpan.FromHours(6));

            // Same employee id, asked for under the neighbour's company.
            var baseline = await Resolver(host).ResolveAsync(CompanyB, Alice, Day);

            Assert.NotEqual(AttendanceBaselineSource.Roster, baseline.Source);
            Assert.Null(baseline.RosterAssignmentId);
        }

        [Fact]
        public async Task An_unresolved_company_or_employee_yields_no_baseline()
        {
            var host = Seed();
            Assert.Equal(AttendanceBaselineSource.None, (await Resolver(host).ResolveAsync(0, Alice, Day)).Source);
            Assert.Equal(AttendanceBaselineSource.None, (await Resolver(host).ResolveAsync(CompanyA, 0, Day)).Source);
        }

        [Fact]
        public async Task Nothing_falls_back_to_company_one()
        {
            var host = Seed();
            await PublishShiftAsync(host, Alice, TimeSpan.FromHours(22), TimeSpan.FromHours(6));

            // Company A is 41. A fallback to a default tenant would surface as a company-1 answer.
            var baseline = await Resolver(host).ResolveAsync(1, Alice, Day);
            Assert.Equal(AttendanceBaselineSource.Policy, baseline.Source);   // policy is not tenant-scoped
            Assert.Null(baseline.RosterAssignmentId);                          // but the ROSTER is
        }

        // =========================================================================================
        // §17 — HISTORICAL SEMANTICS (§9)
        // =========================================================================================

        [Fact]
        public async Task Editing_the_shift_definition_later_does_not_alter_a_past_assignments_baseline()
        {
            var host = Seed();
            var (_, shiftId, _) = await PublishShiftAsync(host, Alice, TimeSpan.FromHours(22), TimeSpan.FromHours(6));

            var before = await Resolver(host).ResolveAsync(CompanyA, Alice, Day);
            Assert.Equal(Day.AddHours(22), before.PlannedStart);

            // The business moves the night shift an hour later, for the future.
            var shift = host.Db.Set<WorkShift>().Single(s => s.ID == shiftId);
            shift.StartTime = TimeSpan.FromHours(23);
            shift.EndTime = TimeSpan.FromHours(7);
            host.Db.SaveChanges();

            var after = await Resolver(host).ResolveAsync(CompanyA, Alice, Day);

            // Last month's attendance must not change meaning because a definition was edited today.
            // The resolver reads the assignment's MATERIALISED instants and never the shift's current
            // times — this test is what stops somebody "simplifying" it into a join to WorkShift.
            Assert.Equal(Day.AddHours(22), after.PlannedStart);
            Assert.Equal(Day.AddDays(1).AddHours(6), after.PlannedEnd);
        }

        // =========================================================================================
        // §17 — THE ARITHMETIC
        // =========================================================================================

        [Fact]
        public void A_night_shift_worked_on_time_is_neither_late_nor_early()
        {
            // The headline defect, reduced to its arithmetic. Under the old TimeOfDay subtraction
            // this employee was 14 hours late and 11 hours early.
            var baseline = new AttendanceBaseline
            {
                Source = AttendanceBaselineSource.Roster,
                PlannedStart = Day.AddHours(22),
                PlannedEnd = Day.AddDays(1).AddHours(6),
            };

            var c = AttendanceMath.Compute(baseline, Day.AddHours(22), Day.AddDays(1).AddHours(6));

            Assert.Equal(0, c.LateMinutes);
            Assert.Equal(0, c.EarlyLeaveMinutes);
            Assert.Equal(0, c.OvertimeCandidateMinutes);
            Assert.Equal(8 * 60, c.WorkedMinutes);
            Assert.Equal("Present", c.Status);
        }

        [Fact]
        public void A_checkout_stamped_before_its_checkin_is_read_as_the_next_morning()
        {
            // §3 verbatim: a check-out at 06:05 must not appear before a 22:00 check-in. A device
            // that stamps the shift's own date produces exactly this, and the old code turned it into
            // negative worked hours.
            var baseline = new AttendanceBaseline
            {
                Source = AttendanceBaselineSource.Roster,
                PlannedStart = Day.AddHours(22),
                PlannedEnd = Day.AddDays(1).AddHours(6),
            };

            var c = AttendanceMath.Compute(baseline, Day.AddHours(22), Day.AddHours(6).AddMinutes(5));

            Assert.Equal(Day.AddDays(1).AddHours(6).AddMinutes(5), c.ActualOut);
            Assert.Equal(8 * 60 + 5, c.WorkedMinutes);
            Assert.Equal(5, c.OvertimeCandidateMinutes);
            Assert.Equal(0, c.EarlyLeaveMinutes);
        }

        [Fact]
        public void An_identical_checkin_and_checkout_is_zero_rather_than_a_full_day()
        {
            var baseline = new AttendanceBaseline
            {
                Source = AttendanceBaselineSource.Policy,
                PlannedStart = Day.AddHours(8), PlannedEnd = Day.AddHours(17),
            };

            // Far more likely a duplicate stamp than a 24-hour shift, so rolling it a day would
            // invent a full day of work.
            var c = AttendanceMath.Compute(baseline, Day.AddHours(8), Day.AddHours(8));
            Assert.Equal(0, c.WorkedMinutes);
        }

        [Fact]
        public void The_grace_period_is_preserved_and_applied_to_the_roster_baseline()
        {
            var baseline = new AttendanceBaseline
            {
                Source = AttendanceBaselineSource.Roster,
                PlannedStart = Day.AddHours(22),
                PlannedEnd = Day.AddDays(1).AddHours(6),
                GraceMinutes = 15,
            };

            // Inside grace.
            Assert.Equal(0, AttendanceMath.Compute(baseline, Day.AddHours(22).AddMinutes(10), null).LateMinutes);
            // Exactly at the edge — still forgiven.
            Assert.Equal(0, AttendanceMath.Compute(baseline, Day.AddHours(22).AddMinutes(15), null).LateMinutes);
            // Past it: counted from the END of grace, which is how the policy always behaved.
            Assert.Equal(5, AttendanceMath.Compute(baseline, Day.AddHours(22).AddMinutes(20), null).LateMinutes);
        }

        [Fact]
        public void Early_departure_and_overtime_are_measured_against_the_planned_end()
        {
            var baseline = new AttendanceBaseline
            {
                Source = AttendanceBaselineSource.Roster,
                PlannedStart = Day.AddHours(22), PlannedEnd = Day.AddDays(1).AddHours(6),
            };

            var early = AttendanceMath.Compute(baseline, Day.AddHours(22), Day.AddDays(1).AddHours(5).AddMinutes(30));
            Assert.Equal(30, early.EarlyLeaveMinutes);
            Assert.Equal(0, early.OvertimeCandidateMinutes);

            var over = AttendanceMath.Compute(baseline, Day.AddHours(22), Day.AddDays(1).AddHours(6).AddMinutes(14));
            Assert.Equal(0, over.EarlyLeaveMinutes);
            Assert.Equal(14, over.OvertimeCandidateMinutes);
        }

        [Fact]
        public void Expected_minutes_deduct_the_break_and_regular_minutes_never_exceed_them()
        {
            var baseline = new AttendanceBaseline
            {
                Source = AttendanceBaselineSource.Roster,
                PlannedStart = Day.AddHours(9), PlannedEnd = Day.AddHours(18),
                BreakMinutes = 60,
            };

            var c = AttendanceMath.Compute(baseline, Day.AddHours(9), Day.AddHours(19));

            Assert.Equal(8 * 60, c.ExpectedMinutes);
            Assert.Equal(10 * 60, c.WorkedMinutes);
            Assert.Equal(60, c.OvertimeCandidateMinutes);

            // §15: regular and overtime never double-count the same minute.
            Assert.Equal(8 * 60, c.RegularMinutes);
        }

        [Fact]
        public void With_no_baseline_at_all_the_derived_figures_stay_zero_rather_than_invented()
        {
            var c = AttendanceMath.Compute(AttendanceBaseline.Nothing(), Day.AddHours(9), Day.AddHours(17));

            Assert.Equal(0, c.LateMinutes);
            Assert.Equal(0, c.EarlyLeaveMinutes);
            Assert.Equal(0, c.OvertimeCandidateMinutes);
            Assert.Equal(8 * 60, c.WorkedMinutes);   // the clock still tells the truth
            Assert.False(c.IsAbsent);                // never expected, so cannot have failed to appear
        }

        // =========================================================================================
        // §17 — LEAVE AND ABSENCE (§6)
        // =========================================================================================

        [Fact]
        public void Approved_leave_outranks_absence()
        {
            var baseline = new AttendanceBaseline
            {
                Source = AttendanceBaselineSource.Roster,
                PlannedStart = Day.AddHours(8), PlannedEnd = Day.AddHours(17),
            };

            var absent = AttendanceMath.Compute(baseline, null, null);
            Assert.True(absent.IsAbsent);
            Assert.Equal("Absent", absent.Status);

            var onLeave = AttendanceMath.Compute(baseline, null, null, onApprovedLeave: true);
            Assert.False(onLeave.IsAbsent);           // authorised, therefore not unexplained
            Assert.True(onLeave.OnApprovedLeave);
            Assert.Equal("Leave", onLeave.Status);
        }

        [Fact]
        public async Task Planned_versus_actual_does_not_report_approved_leave_as_absence()
        {
            var host = Seed();
            await PublishShiftAsync(host, Alice, TimeSpan.FromHours(8), TimeSpan.FromHours(17));

            host.Db.LeaveRequests.Add(new LeaveRequest
            {
                EmployeeID = Alice, LeaveTypeID = 1,
                StartDate = Day, EndDate = Day, Days = 1, Status = 1,   // approved
            });
            host.Db.SaveChanges();

            var rows = await Roster(host, Ctx(CompanyA, Officer)).PlannedVsActualAsync(Day, Day, Alice);
            var row = Assert.Single(rows);

            Assert.False(row.IsAbsent);
            Assert.True(row.OnApprovedLeave);
        }

        [Fact]
        public async Task Pending_leave_does_not_excuse_an_absence()
        {
            var host = Seed();
            await PublishShiftAsync(host, Alice, TimeSpan.FromHours(8), TimeSpan.FromHours(17));

            host.Db.LeaveRequests.Add(new LeaveRequest
            {
                EmployeeID = Alice, LeaveTypeID = 1,
                StartDate = Day, EndDate = Day, Days = 1, Status = 0,   // pending, not approved
            });
            host.Db.SaveChanges();

            var row = Assert.Single(await Roster(host, Ctx(CompanyA, Officer)).PlannedVsActualAsync(Day, Day, Alice));

            // Requesting leave is not permission to be away — otherwise anybody could clear their own
            // absence record by filing a request.
            Assert.True(row.IsAbsent);
            Assert.False(row.OnApprovedLeave);
        }

        // =========================================================================================
        // §17 — THE RECORDING PATH USES THE SAME AUTHORITY (§4, §8)
        // =========================================================================================

        // The REAL HolidayService, not a stub. It reads the same database, and a fixture with no
        // OfficialHoliday rows is an honest "no holidays" rather than a hand-written promise of one.
        private static AttendanceService Attendance(PlatformTestHost host)
            => new(host.Db, new HolidayService(host.Db), Resolver(host));

        [Fact]
        public async Task Recording_a_night_shift_no_longer_reports_a_massively_late_arrival()
        {
            var host = Seed();
            await PublishShiftAsync(host, Alice, TimeSpan.FromHours(22), TimeSpan.FromHours(6));

            // Exactly the brief's example: policy says 08:00-17:00, roster says 22:00-06:00, the
            // employee arrives at 22:00.
            var (ok, _, rec) = await Attendance(host).RecordAsync(
                CompanyA, Alice, Day, Day.AddHours(22), Day.AddDays(1).AddHours(6), "Web", null, "u");

            Assert.True(ok);
            Assert.Equal(0, rec!.LateMinutes);          // was 14 hours before this batch
            Assert.Equal(0, rec.EarlyLeaveMinutes);     // was 11 hours before this batch
            Assert.Equal(0, rec.OvertimeMinutes);
            Assert.Equal(8m, rec.WorkedHours);
            Assert.Equal("Present", rec.Status);
        }

        [Fact]
        public async Task Recording_without_a_roster_still_measures_against_the_policy()
        {
            var host = Seed(graceMinutes: 10);

            var (ok, _, rec) = await Attendance(host).RecordAsync(
                CompanyA, Alice, Day, Day.AddHours(8).AddMinutes(25), Day.AddHours(17), "Web", null, "u");

            Assert.True(ok);
            Assert.Equal(AttendanceBaselineSource.Policy,
                (await Resolver(host).ResolveAsync(CompanyA, Alice, Day)).Source);
            Assert.Equal(15, rec!.LateMinutes);         // 25 late, 10 forgiven — grace preserved
            Assert.Equal("Late", rec.Status);
        }

        [Fact]
        public async Task The_recorded_numbers_and_the_reported_numbers_agree()
        {
            var host = Seed(graceMinutes: 10);
            await PublishShiftAsync(host, Alice, TimeSpan.FromHours(22), TimeSpan.FromHours(6));

            var (_, _, rec) = await Attendance(host).RecordAsync(
                CompanyA, Alice, Day,
                Day.AddHours(22).AddMinutes(30),           // 30 late, 10 forgiven -> 20
                Day.AddDays(1).AddHours(6).AddMinutes(14), // 14 past the planned end
                "Web", null, "u");

            var row = Assert.Single(
                await Roster(host, Ctx(CompanyA, Officer)).PlannedVsActualAsync(Day, Day, Alice));

            // THIS IS THE CONVERGENCE. Recording and reporting are two callers of one formula, so
            // they cannot disagree — and if somebody reintroduces a second formula anywhere, this is
            // the test that fails.
            Assert.Equal(rec!.LateMinutes, row.LateMinutes);
            Assert.Equal(rec.EarlyLeaveMinutes, row.EarlyDepartureMinutes);
            Assert.Equal(rec.OvertimeMinutes, row.OvertimeCandidateMinutes);
            Assert.Equal(rec.WorkedHours, Math.Round(row.ActualMinutes / 60m, 2));

            Assert.Equal(20, row.LateMinutes);
            Assert.Equal(14, row.OvertimeCandidateMinutes);
            Assert.Equal(AttendanceBaselineSource.Roster, row.BaselineSource);
        }

        // =========================================================================================
        // §17 — AUTHORIZATION (§10)
        // =========================================================================================

        [Fact]
        public async Task An_employee_sees_their_own_planned_versus_actual()
        {
            var host = Seed();
            await PublishShiftAsync(host, Alice, TimeSpan.FromHours(8), TimeSpan.FromHours(17));

            // Alice holds no HR role at all.
            var rows = await Roster(host, Ctx(CompanyA, Alice)).PlannedVsActualAsync(Day, Day, Alice);

            Assert.Single(rows);
            Assert.Equal(Alice, rows[0].EmployeeID);
        }

        [Fact]
        public async Task An_employee_cannot_read_a_colleagues_planned_versus_actual()
        {
            var host = Seed();
            await PublishShiftAsync(host, Bob, TimeSpan.FromHours(8), TimeSpan.FromHours(17));

            var asAlice = Roster(host, Ctx(CompanyA, Alice));

            // Bob's row, asked for by Alice.
            Assert.Empty(await asAlice.PlannedVsActualAsync(Day, Day, Bob));

            // And the company-wide route needs management authority, which she does not have.
            Assert.Empty(await asAlice.PlannedVsActualAsync(Day, Day, null));
        }

        [Fact]
        public async Task Planned_versus_actual_never_crosses_a_company_boundary()
        {
            var host = Seed();
            await PublishShiftAsync(host, Alice, TimeSpan.FromHours(8), TimeSpan.FromHours(17));

            host.Db.AttendanceRecords.Add(new AttendanceRecord
            {
                CompanyID = CompanyB, EmployeeID = Mallory, WorkDate = Day,
                CheckIn = Day.AddHours(9), CheckOut = Day.AddHours(17),
            });
            host.Db.SaveChanges();

            var rows = await Roster(host, Ctx(CompanyA, Officer)).PlannedVsActualAsync(Day, Day, null);

            Assert.DoesNotContain(rows, r => r.EmployeeID == Mallory);
            Assert.All(rows, r => Assert.NotEqual(Mallory, r.EmployeeID));
        }

        [Fact]
        public async Task An_unresolved_context_reads_nothing()
        {
            var host = Seed();
            await PublishShiftAsync(host, Alice, TimeSpan.FromHours(8), TimeSpan.FromHours(17));

            Assert.Empty(await Roster(host, null).PlannedVsActualAsync(Day, Day, null));
        }

        // =========================================================================================
        // §17 — UNROSTERED WORK STILL SURFACES
        // =========================================================================================

        [Fact]
        public async Task Someone_who_worked_without_a_roster_is_measured_against_their_policy()
        {
            var host = Seed(graceMinutes: 0);
            await PublishShiftAsync(host, Alice, TimeSpan.FromHours(8), TimeSpan.FromHours(17));

            host.Db.AttendanceRecords.Add(new AttendanceRecord
            {
                CompanyID = CompanyA, EmployeeID = Bob, WorkDate = Day,
                CheckIn = Day.AddHours(8).AddMinutes(20), CheckOut = Day.AddHours(17),
            });
            host.Db.SaveChanges();

            var rows = await Roster(host, Ctx(CompanyA, Officer)).PlannedVsActualAsync(Day, Day, null);
            var bob = Assert.Single(rows.Where(r => r.EmployeeID == Bob));

            // Bob was not rostered, but he HAS a policy — so he is not invisible and not unmeasured.
            Assert.Equal(AttendanceBaselineSource.Policy, bob.BaselineSource);
            Assert.Equal(20, bob.LateMinutes);
        }

        // =========================================================================================
        // §G — WHAT THE PLANNED-VS-ACTUAL PANEL RENDERS FOR A NIGHT SHIFT.
        //
        // The view prints act.ActualIn / act.ActualOut and compares their DATES to decide whether to
        // show the "+1" marker. These assertions are about exactly those values, because the screen
        // has no arithmetic of its own — if the row is right, the panel is right.
        // =========================================================================================

        [Fact]
        public async Task The_panel_row_for_a_night_shift_reads_forwards_in_time()
        {
            var host = Seed(graceMinutes: 5);
            await PublishShiftAsync(host, Alice, TimeSpan.FromHours(22), TimeSpan.FromHours(6));

            // The device stamps the check-out against the SHIFT'S OWN DATE — the common real-world
            // case, and the one that used to render as a check-out eight hours before the check-in.
            host.Db.AttendanceRecords.Add(new AttendanceRecord
            {
                CompanyID = CompanyA, EmployeeID = Alice, WorkDate = Day,
                CheckIn = Day.AddHours(22).AddMinutes(3),
                CheckOut = Day.AddHours(6).AddMinutes(14),
            });
            host.Db.SaveChanges();

            var row = Assert.Single(
                await Roster(host, Ctx(CompanyA, Officer)).PlannedVsActualAsync(Day, Day, Alice));

            // What the panel prints: 22:03 – 06:14, and 06:14 belongs to the NEXT day.
            Assert.Equal("22:03", row.ActualIn!.Value.ToString("HH:mm"));
            Assert.Equal("06:14", row.ActualOut!.Value.ToString("HH:mm"));
            Assert.True(row.ActualOut > row.ActualIn, "the panel would render time running backwards");

            // Which is what makes the view's "+1" marker fire.
            Assert.NotEqual(row.ActualIn.Value.Date, row.ActualOut.Value.Date);

            // And the figures beside it: 3 minutes late is inside a 5-minute grace, so nothing is
            // flagged; 14 minutes past the planned end is an overtime CANDIDATE.
            Assert.Equal(0, row.LateMinutes);
            Assert.Equal(0, row.EarlyDepartureMinutes);
            Assert.Equal(14, row.OvertimeCandidateMinutes);
            Assert.False(row.IsAbsent);

            // The tooltip's answer to "measured against what".
            Assert.Equal(AttendanceBaselineSource.Roster, row.BaselineSource);
        }

        [Fact]
        public async Task The_panel_names_the_policy_when_no_roster_judged_the_day()
        {
            var host = Seed(graceMinutes: 0);

            host.Db.AttendanceRecords.Add(new AttendanceRecord
            {
                CompanyID = CompanyA, EmployeeID = Alice, WorkDate = Day,
                CheckIn = Day.AddHours(8).AddMinutes(12), CheckOut = Day.AddHours(17),
            });
            host.Db.SaveChanges();

            var row = Assert.Single(
                await Roster(host, Ctx(CompanyA, Alice)).PlannedVsActualAsync(Day, Day, Alice));

            // The tooltip must say "standard working hours" rather than "published roster", and the
            // only thing that decides which is this value.
            Assert.Equal(AttendanceBaselineSource.Policy, row.BaselineSource);
            Assert.Equal(12, row.LateMinutes);
        }
    }
}
