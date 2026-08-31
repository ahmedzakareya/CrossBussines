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
    // ROSTER / SHIFT MANAGEMENT — behaviour, against a real database and the real access service.
    //
    // Only the clock is stubbed. HrAccessService is the real one, LeaveRequest and AttendanceRecord
    // rows are real rows, and every assertion is about what the service RETURNS rather than which
    // method it called.
    //
    // THE FIXTURE HAS TWO COMPANIES AND THE NEIGHBOUR IS NOT EMPTY, for the reason the onboarding
    // fixture learned the hard way: in a single-company fixture every row is in scope, so a missing
    // tenant predicate looks exactly like a correct one and an isolation test passes vacuously.
    //
    // BRANCHES AND JOB TITLES ARE SEEDED even where a test does not mention them. Employee.JobTitle is
    // a REQUIRED reference, so any projection through it becomes an INNER JOIN — an unseeded lookup
    // row silently joins every employee away and turns a real assertion into a green vacuum. That has
    // already happened once in this codebase; the seed below is the fix, not decoration.
    // ============================================================================================
    public class RosterTests
    {
        private const int CompanyA = 41;
        private const int CompanyB = 77;

        private const int Alice = 501;    // company A, ordinary employee — no HR role at all
        private const int Bob = 502;      // company A, ordinary employee
        private const int Officer = 510;  // company A, HrOfficer — may manage the roster
        private const int Mallory = 901;  // company B

        private const int BranchA = 11;   // company A
        private const int BranchB = 22;   // company B — the wrong-tenant branch

        private static readonly DateTime Today = new(2026, 6, 15);

        private sealed class FixedClock : IRosterClock
        {
            public DateTime Now => Today.AddHours(9);
            public DateTime Today => RosterTests.Today;
        }

        // ---- fixture ---------------------------------------------------------------------------

        private static PlatformTestHost Seed()
        {
            var host = new PlatformTestHost();
            var db = host.Db;

            db.JobTitles.Add(new JobTitle { ID = 1, Title = "Ops", TitleAr = "تشغيل", Description = "" });
            db.Branches.AddRange(
                Site(BranchA, CompanyA, "Main"), Site(BranchB, CompanyB, "Neighbour"));
            db.Employee.AddRange(
                Person(Alice, CompanyA), Person(Bob, CompanyA),
                Person(Officer, CompanyA), Person(Mallory, CompanyB));
            db.SaveChanges();
            return host;
        }

        private static Branch Site(int id, int companyId, string name) => new()
        {
            ID = id, CompanyID = companyId, Name = name, NameAr = name,
            Location = name, CountryID = 1, PhoneNumber = "0", Email = name + "@x.local",
            Description = "",
        };

        private static Employee Person(int id, int companyId, bool active = true) => new()
        {
            ID = id, EmpCompanyID = companyId, IsActive = active,
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
        // refusal below pass without exercising anything.
        private static BusinessContext Ctx(int companyId, int? employeeId) => new()
        {
            CompanyId = companyId, EmployeeId = employeeId,
            UserId = "u" + employeeId, Source = BusinessContextSource.Http,
        };

        private static RosterService Svc(PlatformTestHost host, BusinessContext? ctx)
        {
            var hr = new HrAccessService(host.Db,
                new PlatformRoleDirectory(host.Db, NullLogger<PlatformRoleDirectory>.Instance),
                new OrgHierarchy(host.Db, NullLogger<OrgHierarchy>.Instance),
                new BootstrapAccessPolicyReader(host.Db, NullLogger<BootstrapAccessPolicyReader>.Instance),
                NullLogger<HrAccessService>.Instance);

            var accessor = ctx == null ? StubContextAccessor.Unresolved() : new StubContextAccessor(ctx);
            // HR-B3: the roster service now consults the canonical attendance baseline resolver for
            // planned-vs-actual, so it takes one. The REAL resolver, not a stub — these tests assert
            // on the numbers it produces, and a stub would only prove the stub.
            return new RosterService(host.Db, accessor, hr, new FixedClock(),
                new AttendanceBaselineResolver(host.Db));
        }

        private static int AddShift(PlatformTestHost host, int companyId, string code,
            TimeSpan start, TimeSpan end, int breakMinutes = 0, bool active = true)
        {
            var s = new WorkShift
            {
                CompanyID = companyId, Code = code, NameAr = code, NameEn = code,
                StartTime = start, EndTime = end, BreakMinutes = breakMinutes, IsActive = active,
            };
            host.Db.Set<WorkShift>().Add(s);
            host.Db.SaveChanges();
            return s.ID;
        }

        private static void ApproveLeave(PlatformTestHost host, int employeeId, DateTime from, DateTime to)
        {
            host.Db.LeaveRequests.Add(new LeaveRequest
            {
                EmployeeID = employeeId, LeaveTypeID = 1,
                StartDate = from, EndDate = to, Days = (to - from).Days + 1,
                Status = 1,   // 1 = approved (0 pending, 2 rejected)
            });
            host.Db.SaveChanges();
        }

        // A period with management authority already in place — the starting point for most tests.
        private static async Task<(RosterService svc, int periodId, int shiftId)> DraftAsync(
            PlatformTestHost host, TimeSpan? start = null, TimeSpan? end = null)
        {
            GiveHrRole(host, CompanyA, Officer, HrRoles.HrOfficer);
            var shiftId = AddShift(host, CompanyA, "MORNING",
                start ?? TimeSpan.FromHours(8), end ?? TimeSpan.FromHours(16));
            var svc = Svc(host, Ctx(CompanyA, Officer));
            var period = (await svc.CreatePeriodAsync("فترة", "Period",
                Today, Today.AddDays(13), null)).Period!;
            return (svc, period.ID, shiftId);
        }

        // =========================================================================================
        // §13 — SHIFT DEFINITION AND OVERNIGHT
        // =========================================================================================

        [Fact]
        public void An_overnight_shift_is_recognised_from_its_own_times()
        {
            var night = new WorkShift { StartTime = TimeSpan.FromHours(22), EndTime = TimeSpan.FromHours(6) };
            var day = new WorkShift { StartTime = TimeSpan.FromHours(8), EndTime = TimeSpan.FromHours(16) };

            Assert.True(night.CrossesMidnight);
            Assert.False(day.CrossesMidnight);

            // Eight hours, arrived at by wrapping rather than by a stored duration that could disagree.
            Assert.Equal(8 * 60, night.WindowMinutes);
            Assert.Equal(8 * 60, day.WindowMinutes);
        }

        [Fact]
        public void A_full_day_shift_reads_as_twenty_four_hours_not_zero()
        {
            // 08:00 → 08:00 is continuous cover. Naive subtraction gives zero, which would silently
            // schedule nobody for a whole day.
            var round = new WorkShift { StartTime = TimeSpan.FromHours(8), EndTime = TimeSpan.FromHours(8) };
            Assert.True(round.CrossesMidnight);
            Assert.Equal(24 * 60, round.WindowMinutes);
        }

        [Fact]
        public void Expected_working_duration_is_the_window_less_the_break()
        {
            var s = new WorkShift
            {
                StartTime = TimeSpan.FromHours(9), EndTime = TimeSpan.FromHours(18),
                BreakMinutes = 60,
            };
            Assert.Equal(9 * 60, s.WindowMinutes);
            Assert.Equal(8 * 60, s.ExpectedWorkMinutes);
        }

        [Fact]
        public async Task An_overnight_assignment_ends_on_the_following_day()
        {
            var host = Seed();
            var (svc, periodId, _) = await DraftAsync(host);
            var night = AddShift(host, CompanyA, "NIGHT", TimeSpan.FromHours(22), TimeSpan.FromHours(6));

            var result = await svc.AssignAsync(periodId, Alice, Today, night, null, null, null);

            Assert.True(result.Ok);
            var a = result.Assignment!;
            Assert.Equal(Today.AddHours(22), a.PlannedStart);
            Assert.Equal(Today.AddDays(1).AddHours(6), a.PlannedEnd);   // NEXT day
            Assert.Equal(8 * 60, a.PlannedMinutes);
        }

        // =========================================================================================
        // §13 — AUTHORITY
        // =========================================================================================

        [Fact]
        public async Task An_unresolved_context_cannot_create_a_period()
        {
            var host = Seed();
            var svc = Svc(host, null);   // no BusinessContext at all

            var result = await svc.CreatePeriodAsync("x", "x", Today, Today.AddDays(6), null);

            // Fails CLOSED. There is no company to fall back to, and none is invented.
            Assert.False(result.Ok);
            Assert.Empty(host.Db.Set<RosterPeriod>().ToList());
        }

        [Fact]
        public async Task An_employee_with_no_hr_role_cannot_create_or_publish()
        {
            var host = Seed();
            var (officerSvc, periodId, shiftId) = await DraftAsync(host);
            await officerSvc.AssignAsync(periodId, Alice, Today, shiftId, null, null, null);

            var asAlice = Svc(host, Ctx(CompanyA, Alice));

            Assert.False((await asAlice.CreatePeriodAsync("x", "x", Today, Today.AddDays(6), null)).Ok);
            Assert.False((await asAlice.AssignAsync(periodId, Bob, Today, shiftId, null, null, null)).Ok);
            Assert.False((await asAlice.PublishAsync(periodId)).Ok);

            // And nothing moved.
            Assert.Equal(RosterPeriodStatus.Draft,
                host.Db.Set<RosterPeriod>().Single().Status);
        }

        [Fact]
        public async Task An_hr_officer_may_create_assign_and_publish()
        {
            var host = Seed();
            var (svc, periodId, shiftId) = await DraftAsync(host);

            Assert.True((await svc.AssignAsync(periodId, Alice, Today, shiftId, null, null, null)).Ok);
            var published = await svc.PublishAsync(periodId);

            Assert.True(published.Ok);
            Assert.Equal(RosterPeriodStatus.Published, published.Period!.Status);
            Assert.Equal(Officer, published.Period.PublishedBy);   // actor from the server, not the request
        }

        // =========================================================================================
        // §13 — COMPANY / BRANCH ISOLATION
        // =========================================================================================

        [Fact]
        public async Task A_period_from_another_company_is_not_readable()
        {
            var host = Seed();
            var (_, periodId, _) = await DraftAsync(host);

            // Mallory has a full HR role — in HER OWN company. Authority is not portable.
            GiveHrRole(host, CompanyB, Mallory, HrRoles.HrManager);

            Assert.Null(await Svc(host, Ctx(CompanyB, Mallory)).GetPeriodAsync(periodId));
        }

        [Fact]
        public async Task An_employee_of_another_company_cannot_be_assigned()
        {
            var host = Seed();
            var (svc, periodId, shiftId) = await DraftAsync(host);

            var result = await svc.AssignAsync(periodId, Mallory, Today, shiftId, null, null, null);

            Assert.False(result.Ok);
            Assert.Empty(host.Db.Set<RosterAssignment>().ToList());
        }

        [Fact]
        public async Task A_branch_belonging_to_another_company_is_refused()
        {
            var host = Seed();
            var (svc, periodId, shiftId) = await DraftAsync(host);

            var result = await svc.AssignAsync(periodId, Alice, Today, shiftId, BranchB, null, null);

            Assert.False(result.Ok);
            Assert.Equal("branch_not_in_company", result.Error);
            Assert.Empty(host.Db.Set<RosterAssignment>().ToList());
        }

        [Fact]
        public async Task A_shift_belonging_to_another_company_is_refused()
        {
            var host = Seed();
            var (svc, periodId, _) = await DraftAsync(host);
            var foreignShift = AddShift(host, CompanyB, "THEIRS", TimeSpan.FromHours(8), TimeSpan.FromHours(16));

            var result = await svc.AssignAsync(periodId, Alice, Today, foreignShift, null, null, null);

            Assert.False(result.Ok);
            Assert.Equal("shift_not_in_company", result.Error);
        }

        // =========================================================================================
        // §13 — SELF-SERVICE
        // =========================================================================================

        [Fact]
        public async Task An_employee_sees_their_own_published_schedule()
        {
            var host = Seed();
            var (svc, periodId, shiftId) = await DraftAsync(host);
            await svc.AssignAsync(periodId, Alice, Today, shiftId, null, null, null);
            await svc.PublishAsync(periodId);

            // Alice has no HR role whatsoever.
            var mine = await Svc(host, Ctx(CompanyA, Alice)).MyScheduleAsync(Today, Today.AddDays(6));

            Assert.Single(mine);
            Assert.Equal(Alice, mine[0].EmployeeID);
        }

        [Fact]
        public async Task An_employee_cannot_read_a_colleagues_schedule()
        {
            var host = Seed();
            var (svc, periodId, shiftId) = await DraftAsync(host);
            await svc.AssignAsync(periodId, Bob, Today, shiftId, null, null, null);
            await svc.PublishAsync(periodId);

            var asAlice = Svc(host, Ctx(CompanyA, Alice));

            // Bob's schedule, asked for by Alice.
            Assert.Null(await asAlice.EmployeeScheduleAsync(Bob, Today, Today.AddDays(6)));

            // And her own view does not quietly include him.
            Assert.Empty(await asAlice.MyScheduleAsync(Today, Today.AddDays(6)));
        }

        [Fact]
        public async Task An_employee_does_not_see_a_draft_roster()
        {
            var host = Seed();
            var (svc, periodId, shiftId) = await DraftAsync(host);
            await svc.AssignAsync(periodId, Alice, Today, shiftId, null, null, null);
            // deliberately NOT published

            var asAlice = Svc(host, Ctx(CompanyA, Alice));

            // A draft is a working document, not a promise. Neither route exposes it.
            Assert.Empty(await asAlice.MyScheduleAsync(Today, Today.AddDays(6)));
            Assert.Null(await asAlice.GetPeriodAsync(periodId));

            // Publishing it changes exactly that, which is what makes the assertion above meaningful.
            await svc.PublishAsync(periodId);
            Assert.Single(await asAlice.MyScheduleAsync(Today, Today.AddDays(6)));
        }

        // =========================================================================================
        // §13 — CONFLICTS
        // =========================================================================================

        [Fact]
        public async Task Two_overlapping_shifts_for_one_employee_are_detected_and_block_publishing()
        {
            var host = Seed();
            var (svc, periodId, morning) = await DraftAsync(host);
            var overlapping = AddShift(host, CompanyA, "MID", TimeSpan.FromHours(12), TimeSpan.FromHours(20));

            await svc.AssignAsync(periodId, Alice, Today, morning, null, null, null);      // 08–16
            await svc.AssignAsync(periodId, Alice, Today, overlapping, null, null, null);  // 12–20

            var view = await svc.GetPeriodAsync(periodId);
            var clash = Assert.Single(view!.Conflicts.Where(c => c.Kind == RosterConflictKind.OverlappingAssignment));
            Assert.Equal(Alice, clash.EmployeeID);
            Assert.NotNull(clash.ConflictingAssignmentID);   // actionable: both ids are carried

            // The conflict is not resolved silently — it stops the publish.
            var publish = await svc.PublishAsync(periodId);
            Assert.False(publish.Ok);
            Assert.Equal("conflicts_detected", publish.Error);
            Assert.Equal(RosterPeriodStatus.Draft, host.Db.Set<RosterPeriod>().Single().Status);
        }

        [Fact]
        public async Task An_overnight_shift_clashing_with_the_next_mornings_shift_is_detected()
        {
            var host = Seed();
            var (svc, periodId, _) = await DraftAsync(host);
            var night = AddShift(host, CompanyA, "NIGHT", TimeSpan.FromHours(22), TimeSpan.FromHours(6));
            var early = AddShift(host, CompanyA, "EARLY", TimeSpan.FromHours(5), TimeSpan.FromHours(13));

            await svc.AssignAsync(periodId, Alice, Today, night, null, null, null);            // Mon 22:00 → Tue 06:00
            await svc.AssignAsync(periodId, Alice, Today.AddDays(1), early, null, null, null); // Tue 05:00 → Tue 13:00

            // The two rows carry DIFFERENT WorkDates, so a date-only comparison would call this fine.
            // Comparing instants is what catches it.
            var view = await svc.GetPeriodAsync(periodId);
            Assert.Contains(view!.Conflicts, c => c.Kind == RosterConflictKind.OverlappingAssignment);
        }

        [Fact]
        public async Task Two_shifts_on_the_same_day_that_do_not_overlap_are_not_a_conflict()
        {
            var host = Seed();
            var (svc, periodId, morning) = await DraftAsync(host);            // 08–16
            var evening = AddShift(host, CompanyA, "EVE", TimeSpan.FromHours(16), TimeSpan.FromHours(23));

            await svc.AssignAsync(periodId, Alice, Today, morning, null, null, null);
            await svc.AssignAsync(periodId, Alice, Today, evening, null, null, null);

            // Touching at 16:00 is back-to-back, not an overlap. A >= comparison would wrongly flag it.
            var view = await svc.GetPeriodAsync(periodId);
            Assert.Empty(view!.Conflicts);
            Assert.True((await svc.PublishAsync(periodId)).Ok);
        }

        [Fact]
        public async Task An_assignment_during_approved_leave_is_detected()
        {
            var host = Seed();
            var (svc, periodId, shiftId) = await DraftAsync(host);
            ApproveLeave(host, Alice, Today, Today.AddDays(2));

            await svc.AssignAsync(periodId, Alice, Today.AddDays(1), shiftId, null, null, null);

            var view = await svc.GetPeriodAsync(periodId);
            var conflict = Assert.Single(view!.Conflicts);
            Assert.Equal(RosterConflictKind.ApprovedLeave, conflict.Kind);
            Assert.NotNull(conflict.LeaveRequestID);    // actionable: links to the request

            Assert.False((await svc.PublishAsync(periodId)).Ok);
        }

        [Fact]
        public async Task Pending_and_rejected_leave_do_not_block_a_roster()
        {
            var host = Seed();
            var (svc, periodId, shiftId) = await DraftAsync(host);

            // 0 = pending, 2 = rejected. Neither is permission to be away, so neither is a conflict —
            // and treating "requested" as "approved" would let anyone clear their own roster.
            host.Db.LeaveRequests.AddRange(
                new LeaveRequest { EmployeeID = Alice, LeaveTypeID = 1, StartDate = Today, EndDate = Today, Days = 1, Status = 0 },
                new LeaveRequest { EmployeeID = Alice, LeaveTypeID = 1, StartDate = Today, EndDate = Today, Days = 1, Status = 2 });
            host.Db.SaveChanges();

            await svc.AssignAsync(periodId, Alice, Today, shiftId, null, null, null);

            var view = await svc.GetPeriodAsync(periodId);
            Assert.Empty(view!.Conflicts);
            Assert.True((await svc.PublishAsync(periodId)).Ok);
        }

        [Fact]
        public async Task An_employee_deactivated_after_being_rostered_is_flagged()
        {
            var host = Seed();
            var (svc, periodId, shiftId) = await DraftAsync(host);
            await svc.AssignAsync(periodId, Alice, Today, shiftId, null, null, null);

            // She was active when rostered; she is not now. Creation cannot catch this — only
            // detection can, which is why both paths exist.
            var alice = host.Db.Employee.Single(e => e.ID == Alice);
            alice.IsActive = false;
            host.Db.SaveChanges();

            var view = await svc.GetPeriodAsync(periodId);
            Assert.Contains(view!.Conflicts, c => c.Kind == RosterConflictKind.InactiveEmployee);
            Assert.False((await svc.PublishAsync(periodId)).Ok);
        }

        [Fact]
        public async Task The_same_employee_date_and_shift_twice_is_reported_as_a_duplicate()
        {
            var host = Seed();
            var (svc, periodId, shiftId) = await DraftAsync(host);

            await svc.AssignAsync(periodId, Alice, Today, shiftId, null, null, null);
            await svc.AssignAsync(periodId, Alice, Today, shiftId, null, null, null);

            var view = await svc.GetPeriodAsync(periodId);
            var conflict = Assert.Single(view!.Conflicts);

            // A duplicate and a clash need different fixes, so they are different kinds — even though
            // a duplicate trivially overlaps itself.
            Assert.Equal(RosterConflictKind.DuplicateAssignment, conflict.Kind);
        }

        [Fact]
        public async Task A_cancelled_assignment_stops_conflicting()
        {
            var host = Seed();
            var (svc, periodId, morning) = await DraftAsync(host);
            var overlapping = AddShift(host, CompanyA, "MID", TimeSpan.FromHours(12), TimeSpan.FromHours(20));

            var first = (await svc.AssignAsync(periodId, Alice, Today, morning, null, null, null)).Assignment!;
            await svc.AssignAsync(periodId, Alice, Today, overlapping, null, null, null);
            Assert.NotEmpty((await svc.GetPeriodAsync(periodId))!.Conflicts);

            await svc.CancelAssignmentAsync(first.ID, null);

            // Cancelled, not deleted — the row survives to answer "was I ever scheduled for that".
            Assert.Empty((await svc.GetPeriodAsync(periodId))!.Conflicts);
            Assert.Equal(2, host.Db.Set<RosterAssignment>().Count());
        }

        // =========================================================================================
        // §13 — PUBLISHED HISTORY MUST NOT MUTATE SILENTLY
        // =========================================================================================

        [Fact]
        public async Task Changing_a_published_roster_without_a_reason_is_refused()
        {
            var host = Seed();
            var (svc, periodId, shiftId) = await DraftAsync(host);
            var a = (await svc.AssignAsync(periodId, Alice, Today, shiftId, null, null, null)).Assignment!;
            await svc.PublishAsync(periodId);

            var noReason = await svc.CancelAssignmentAsync(a.ID, null);
            Assert.False(noReason.Ok);
            Assert.Equal("reason_required_for_published_change", noReason.Error);

            // Unchanged, and no revision invented.
            Assert.Equal(RosterAssignmentStatus.Planned,
                host.Db.Set<RosterAssignment>().Single().Status);
            Assert.Empty(host.Db.Set<RosterAssignmentRevision>().ToList());
        }

        [Fact]
        public async Task Changing_a_published_roster_with_a_reason_leaves_evidence()
        {
            var host = Seed();
            var (svc, periodId, shiftId) = await DraftAsync(host);
            var a = (await svc.AssignAsync(periodId, Alice, Today, shiftId, null, null, null)).Assignment!;
            await svc.PublishAsync(periodId);

            Assert.True((await svc.CancelAssignmentAsync(a.ID, "شهادة مرضية")).Ok);

            var revision = Assert.Single(host.Db.Set<RosterAssignmentRevision>().ToList());
            Assert.Equal("Status", revision.ChangedField);
            Assert.Equal(RosterAssignmentStatus.Planned, revision.OldValue);
            Assert.Equal(RosterAssignmentStatus.Cancelled, revision.NewValue);
            Assert.Equal("شهادة مرضية", revision.Reason);
            Assert.Equal(Officer, revision.ChangedBy);   // server identity, not a posted actor id
        }

        [Fact]
        public async Task Editing_a_draft_leaves_no_revision_noise()
        {
            var host = Seed();
            var (svc, periodId, shiftId) = await DraftAsync(host);
            var a = (await svc.AssignAsync(periodId, Alice, Today, shiftId, null, null, null)).Assignment!;

            // Still a draft. Drafting is not history; recording it would bury the entries that matter.
            Assert.True((await svc.CancelAssignmentAsync(a.ID, null)).Ok);
            Assert.Empty(host.Db.Set<RosterAssignmentRevision>().ToList());
        }

        // =========================================================================================
        // §13 — ATTENDANCE RELATIONSHIP (planned vs actual, writing nothing)
        // =========================================================================================

        [Fact]
        public async Task Planned_versus_actual_derives_lateness_and_overtime_from_the_roster()
        {
            var host = Seed();
            var (svc, periodId, shiftId) = await DraftAsync(host);   // 08:00–16:00
            await svc.AssignAsync(periodId, Alice, Today, shiftId, null, null, null);
            await svc.PublishAsync(periodId);

            host.Db.AttendanceRecords.Add(new AttendanceRecord
            {
                CompanyID = CompanyA, EmployeeID = Alice, WorkDate = Today,
                CheckIn = Today.AddHours(8).AddMinutes(25),      // 25 minutes late
                CheckOut = Today.AddHours(17),                   // an hour past the planned end
            });
            host.Db.SaveChanges();

            var rows = await svc.PlannedVsActualAsync(Today, Today, Alice);
            var row = Assert.Single(rows);

            Assert.Equal(25, row.LateMinutes);
            Assert.Equal(60, row.OvertimeCandidateMinutes);
            Assert.Equal(0, row.EarlyDepartureMinutes);
            Assert.False(row.IsAbsent);
            Assert.Equal(8 * 60, row.PlannedMinutes);

            // Nothing was written back to attendance, and no figure became money.
            Assert.Single(host.Db.AttendanceRecords.ToList());
            Assert.Equal(0, host.Db.AttendanceRecords.Single().LateMinutes);
        }

        [Fact]
        public async Task A_scheduled_employee_who_never_checked_in_is_absent()
        {
            var host = Seed();
            var (svc, periodId, shiftId) = await DraftAsync(host);
            await svc.AssignAsync(periodId, Alice, Today, shiftId, null, null, null);
            await svc.PublishAsync(periodId);

            var row = Assert.Single(await svc.PlannedVsActualAsync(Today, Today, Alice));
            Assert.True(row.IsAbsent);
        }

        [Fact]
        public async Task Attendance_on_a_day_nobody_was_rostered_is_reported_not_dropped()
        {
            var host = Seed();
            var (svc, periodId, shiftId) = await DraftAsync(host);
            await svc.AssignAsync(periodId, Alice, Today, shiftId, null, null, null);
            await svc.PublishAsync(periodId);

            host.Db.AttendanceRecords.Add(new AttendanceRecord
            {
                CompanyID = CompanyA, EmployeeID = Bob, WorkDate = Today,
                CheckIn = Today.AddHours(9), CheckOut = Today.AddHours(17),
            });
            host.Db.SaveChanges();

            var rows = await svc.PlannedVsActualAsync(Today, Today, null);

            // Bob worked unrostered. Dropping him would make coverage look tidier than it is.
            var bob = Assert.Single(rows.Where(r => r.EmployeeID == Bob));
            Assert.Null(bob.PlannedStart);
            Assert.False(bob.IsAbsent);          // no plan means no promise to break
            Assert.Equal(8 * 60, bob.ActualMinutes);
        }

        [Fact]
        public async Task Planned_versus_actual_is_company_scoped()
        {
            var host = Seed();
            var (svc, periodId, shiftId) = await DraftAsync(host);
            await svc.AssignAsync(periodId, Alice, Today, shiftId, null, null, null);
            await svc.PublishAsync(periodId);

            // The neighbour's attendance exists and must not appear.
            host.Db.AttendanceRecords.Add(new AttendanceRecord
            {
                CompanyID = CompanyB, EmployeeID = Mallory, WorkDate = Today,
                CheckIn = Today.AddHours(9), CheckOut = Today.AddHours(17),
            });
            host.Db.SaveChanges();

            var rows = await svc.PlannedVsActualAsync(Today, Today, null);
            Assert.DoesNotContain(rows, r => r.EmployeeID == Mallory);
        }

        // =========================================================================================
        // §13 — NO COMPANY-1 FALLBACK ANYWHERE
        // =========================================================================================

        [Fact]
        public async Task Nothing_is_ever_written_against_company_one_by_default()
        {
            var host = Seed();
            var (svc, periodId, shiftId) = await DraftAsync(host);
            await svc.AssignAsync(periodId, Alice, Today, shiftId, null, null, null);
            await svc.PublishAsync(periodId);

            // Company A is 41. If any path had fallen back to a default tenant, a 1 would appear here.
            Assert.All(host.Db.Set<RosterPeriod>().ToList(), p => Assert.Equal(CompanyA, p.CompanyID));
            Assert.All(host.Db.Set<RosterAssignment>().ToList(), a => Assert.Equal(CompanyA, a.CompanyID));
            Assert.Empty(host.Db.Set<RosterPeriod>().Where(p => p.CompanyID == 1).ToList());
            Assert.Empty(host.Db.Set<RosterAssignment>().Where(a => a.CompanyID == 1).ToList());
        }

        [Fact]
        public async Task A_date_outside_the_period_is_refused()
        {
            var host = Seed();
            var (svc, periodId, shiftId) = await DraftAsync(host);   // Today .. Today+13

            var result = await svc.AssignAsync(periodId, Alice, Today.AddDays(30), shiftId, null, null, null);

            Assert.False(result.Ok);
            Assert.Equal("date_outside_period", result.Error);
        }

        [Fact]
        public async Task An_inactive_shift_cannot_be_assigned()
        {
            var host = Seed();
            var (svc, periodId, _) = await DraftAsync(host);
            var retired = AddShift(host, CompanyA, "OLD", TimeSpan.FromHours(8), TimeSpan.FromHours(16), active: false);

            var result = await svc.AssignAsync(periodId, Alice, Today, retired, null, null, null);

            Assert.False(result.Ok);
            Assert.Equal("shift_inactive", result.Error);
        }
    }
}
