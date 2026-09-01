using CrossBuy.BL.Reporting;
using CrossBuy.Models.Context.Reporting;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CrossBuy.Tests
{
    // Reporting Platform (ADR-037) — SCHEDULING and DELIVERY architecture.
    //
    // The calculator is pure, so every clamping and boundary rule is asserted without a database. The runner is
    // exercised end to end — generate, archive, deliver, advance — precisely because NO hosted service drives it
    // in this slice: these tests are the proof that the architecture is complete without anything firing on its own.
    public class ReportingScheduleTests
    {
        private static readonly DateTime Now = ReportingTestHost.FixedNow;   // Thursday 2026-05-14 10:30

        private static ReportSchedule Schedule(ReportScheduleFrequency frequency, int hour = 6, int minute = 0,
            int? dayOfWeek = null, int? dayOfMonth = null, int? intervalMinutes = null,
            DateTime? lastRunAt = null) => new()
            {
                Id = 1,
                CompanyID = 1,
                ReportCode = TestReportDefinitions.SalesCode,
                Name = "s",
                Frequency = frequency,
                AtHour = hour,
                AtMinute = minute,
                DayOfWeek = dayOfWeek,
                DayOfMonth = dayOfMonth,
                IntervalMinutes = intervalMinutes,
                LastRunAt = lastRunAt,
                TimeZoneId = TimeZoneInfo.Local.Id,
                OwnerEmpId = 7,
                IsActive = true,
            };

        // ================================================================================================
        // 1. NEXT-RUN ARITHMETIC
        // ================================================================================================

        [Fact]
        public void Daily_before_the_hour_fires_today_and_after_it_fires_tomorrow()
        {
            var calculator = new ReportScheduleCalculator();

            // 06:00 has passed at 10:30 → tomorrow.
            Assert.Equal(new DateTime(2026, 5, 15, 6, 0, 0),
                calculator.ComputeNextRun(Schedule(ReportScheduleFrequency.Daily, hour: 6), Now));

            // 18:00 has not → today.
            Assert.Equal(new DateTime(2026, 5, 14, 18, 0, 0),
                calculator.ComputeNextRun(Schedule(ReportScheduleFrequency.Daily, hour: 18), Now));
        }

        [Fact]
        public void Weekly_on_today_after_the_time_moves_a_full_week_not_to_today()
        {
            var calculator = new ReportScheduleCalculator();

            // 2026-05-14 is a Thursday (DayOfWeek 4) and 06:00 has passed.
            var next = calculator.ComputeNextRun(
                Schedule(ReportScheduleFrequency.Weekly, hour: 6, dayOfWeek: 4), Now);

            Assert.Equal(new DateTime(2026, 5, 21, 6, 0, 0), next);
        }

        [Fact]
        public void Weekly_finds_the_next_matching_weekday()
        {
            var calculator = new ReportScheduleCalculator();

            // Sunday (0) from a Thursday → 2026-05-17.
            Assert.Equal(new DateTime(2026, 5, 17, 6, 0, 0),
                calculator.ComputeNextRun(Schedule(ReportScheduleFrequency.Weekly, dayOfWeek: 0), Now));
        }

        [Fact]
        public void Monthly_day_31_is_clamped_to_the_length_of_each_month()
        {
            var calculator = new ReportScheduleCalculator();
            var schedule = Schedule(ReportScheduleFrequency.Monthly, hour: 23, dayOfMonth: 31);

            // Clamping rather than skipping: a month-end report set to the 31st must run EVERY month, not eight
            // times a year.
            Assert.Equal(new DateTime(2026, 5, 31, 23, 0, 0), calculator.ComputeNextRun(schedule, Now));
            Assert.Equal(new DateTime(2026, 6, 30, 23, 0, 0),
                calculator.ComputeNextRun(schedule, new DateTime(2026, 6, 1)));
            Assert.Equal(new DateTime(2026, 2, 28, 23, 0, 0),
                calculator.ComputeNextRun(schedule, new DateTime(2026, 2, 1)));
            Assert.Equal(new DateTime(2028, 2, 29, 23, 0, 0),
                calculator.ComputeNextRun(schedule, new DateTime(2028, 2, 1)));
        }

        [Fact]
        public void Monthly_rolls_to_next_month_when_this_months_slot_has_passed()
        {
            var calculator = new ReportScheduleCalculator();

            var next = calculator.ComputeNextRun(
                Schedule(ReportScheduleFrequency.Monthly, hour: 6, dayOfMonth: 1), Now);

            Assert.Equal(new DateTime(2026, 6, 1, 6, 0, 0), next);
        }

        [Fact]
        public void Interval_is_measured_from_the_last_run_not_from_a_fixed_origin()
        {
            var calculator = new ReportScheduleCalculator();

            var schedule = Schedule(ReportScheduleFrequency.Interval, intervalMinutes: 60,
                lastRunAt: Now.AddMinutes(-10));

            // 50 minutes after the last run. Measuring from an origin would fire immediately on resume after any
            // outage and then again an hour later.
            Assert.Equal(Now.AddMinutes(50), calculator.ComputeNextRun(schedule, Now));
        }

        [Fact]
        public void Interval_never_returns_a_time_in_the_past_after_a_long_outage()
        {
            var calculator = new ReportScheduleCalculator();

            var schedule = Schedule(ReportScheduleFrequency.Interval, intervalMinutes: 30,
                lastRunAt: Now.AddDays(-5));

            var next = calculator.ComputeNextRun(schedule, Now);

            // Otherwise the sweep would fire it over and over trying to catch up on 240 missed occurrences.
            Assert.True(next > Now);
            Assert.Equal(Now.AddMinutes(30), next);
        }

        [Fact]
        public void Hourly_fires_on_the_schedules_minute()
        {
            var calculator = new ReportScheduleCalculator();

            // 10:30 now, minute 15 → 11:15.
            Assert.Equal(new DateTime(2026, 5, 14, 11, 15, 0),
                calculator.ComputeNextRun(Schedule(ReportScheduleFrequency.Hourly, minute: 15), Now));

            // minute 45 → still this hour.
            Assert.Equal(new DateTime(2026, 5, 14, 10, 45, 0),
                calculator.ComputeNextRun(Schedule(ReportScheduleFrequency.Hourly, minute: 45), Now));
        }

        [Fact]
        public void An_inactive_or_deleted_schedule_never_fires_again()
        {
            var calculator = new ReportScheduleCalculator();

            var inactive = Schedule(ReportScheduleFrequency.Daily);
            inactive.IsActive = false;
            Assert.Null(calculator.ComputeNextRun(inactive, Now));

            var deleted = Schedule(ReportScheduleFrequency.Daily);
            deleted.DeletedAt = Now;
            Assert.Null(calculator.ComputeNextRun(deleted, Now));
        }

        [Fact]
        public void An_unknown_time_zone_falls_back_to_server_local_with_a_warning_never_an_exception()
        {
            var calculator = new ReportScheduleCalculator();
            var schedule = Schedule(ReportScheduleFrequency.Daily, hour: 18);
            schedule.TimeZoneId = "Mars/Olympus_Mons";

            var diagnostics = calculator.Validate(schedule);

            Assert.Contains(diagnostics, d => d.Code == "schedule_timezone_unknown"
                                              && d.Severity == ReportDiagnosticSeverity.Warning);

            // A schedule must not become unreadable because a host image lacks a tz database entry.
            Assert.NotNull(calculator.ComputeNextRun(schedule, Now));
        }

        // ================================================================================================
        // 2. VALIDATION
        // ================================================================================================

        [Theory]
        [InlineData(ReportScheduleFrequency.Interval, null, null, null, "schedule_interval_invalid")]
        [InlineData(ReportScheduleFrequency.Weekly, null, 9, null, "schedule_dayofweek_invalid")]
        [InlineData(ReportScheduleFrequency.Monthly, null, null, 0, "schedule_dayofmonth_invalid")]
        public void A_malformed_recurrence_is_rejected_with_a_field_level_reason(
            ReportScheduleFrequency frequency, int? interval, int? dayOfWeek, int? dayOfMonth, string code)
        {
            var calculator = new ReportScheduleCalculator();

            var schedule = Schedule(frequency, dayOfWeek: dayOfWeek, dayOfMonth: dayOfMonth,
                intervalMinutes: interval);

            Assert.Contains(calculator.Validate(schedule), d => d.Code == code);

            // ...and it can never fire, rather than firing wrongly.
            Assert.Null(calculator.ComputeNextRun(schedule, Now));
        }

        [Fact]
        public void An_interval_below_the_floor_is_rejected()
        {
            var calculator = new ReportScheduleCalculator();

            // Below 15 minutes a heavy report can still be running when its next occurrence is due, and the
            // platform would queue work faster than it completes.
            Assert.Contains(
                calculator.Validate(Schedule(ReportScheduleFrequency.Interval, intervalMinutes: 5)),
                d => d.Code == "schedule_interval_invalid");

            Assert.Empty(
                calculator.Validate(Schedule(ReportScheduleFrequency.Interval, intervalMinutes: 15))
                    .Where(d => d.Severity == ReportDiagnosticSeverity.Error));
        }

        // ================================================================================================
        // 3. SAVING A SCHEDULE
        // ================================================================================================

        [Fact]
        public async Task Saving_a_schedule_computes_its_next_run_and_strips_system_parameters()
        {
            using var host = new ReportingTestHost();

            var result = await host.Schedules.SaveAsync(new ReportScheduleInput
            {
                ReportCode = TestReportDefinitions.SalesCode,
                Name = "daily sales",
                Frequency = ReportScheduleFrequency.Daily,
                AtHour = 18,
                Format = ReportOutputFormat.Csv,
                Parameters = new Dictionary<string, string?>
                {
                    ["From"] = "month-start",
                    [ReportSystemParameters.CompanyId] = "99",   // must not be stored
                },
                Recipients = new[]
                {
                    new ReportScheduleRecipientInput { Address = "finance@test.local" },
                },
            }, host.Ctx);

            Assert.True(result.Success);
            Assert.Equal(new DateTime(2026, 5, 14, 18, 0, 0), result.NextRunAt);

            var schedule = await host.Db.ReportSchedules.AsNoTracking().SingleAsync();

            // A schedule that could persist CompanyId would be a stored cross-tenant read waiting for someone to
            // edit the row.
            Assert.DoesNotContain(ReportSystemParameters.CompanyId, schedule.ParametersJson);
            Assert.Contains("month-start", schedule.ParametersJson);
            Assert.Equal(7, schedule.OwnerEmpId);

            Assert.Single(await host.Db.ReportScheduleRecipients.AsNoTracking().ToListAsync());
        }

        [Fact]
        public async Task A_caller_who_cannot_run_the_report_cannot_schedule_it()
        {
            using var host = new ReportingTestHost(companyId: 1, employeeId: 7, roles: "Warehouse");

            var result = await host.Schedules.SaveAsync(new ReportScheduleInput
            {
                ReportCode = TestReportDefinitions.SalesCode, Name = "sneaky",
                Frequency = ReportScheduleFrequency.Daily,
            }, host.Ctx);

            // A schedule is an authorization decision that OUTLIVES the request that made it, so it is checked at
            // save time as well as at run time.
            Assert.False(result.Success);
        }

        [Fact]
        public async Task A_report_that_forbids_scheduling_cannot_be_scheduled()
        {
            using var host = new ReportingTestHost(companyId: 1, employeeId: 7, roles: "Admin");

            // Platform.ReportCatalog declares AllowSchedule = false — a catalogue listing on a cadence is noise.
            var result = await host.Schedules.SaveAsync(new ReportScheduleInput
            {
                ReportCode = PlatformReportCodes.ReportCatalog, Name = "no",
                Frequency = ReportScheduleFrequency.Daily,
            }, host.Ctx);

            Assert.False(result.Success);
            Assert.Contains(result.Diagnostics, d => d.Code == "schedule_not_allowed");
        }

        [Fact]
        public async Task Pausing_and_resuming_recomputes_from_now_instead_of_firing_a_backlog()
        {
            using var host = new ReportingTestHost();

            var saved = await host.Schedules.SaveAsync(new ReportScheduleInput
            {
                ReportCode = TestReportDefinitions.SalesCode, Name = "s",
                Frequency = ReportScheduleFrequency.Daily, AtHour = 18,
            }, host.Ctx);

            Assert.True(await host.Schedules.SetActiveAsync(saved.ScheduleId, false, host.Ctx));
            var paused = await host.Db.ReportSchedules.AsNoTracking().SingleAsync();
            Assert.Null(paused.NextRunAt);

            Assert.True(await host.Schedules.SetActiveAsync(saved.ScheduleId, true, host.Ctx));
            var resumed = await host.Db.ReportSchedules.AsNoTracking().SingleAsync();
            Assert.Equal(new DateTime(2026, 5, 14, 18, 0, 0), resumed.NextRunAt);
        }

        [Fact]
        public async Task Only_due_schedules_are_returned_and_only_for_the_named_company()
        {
            using var host = new ReportingTestHost();

            host.Db.ReportSchedules.AddRange(
                DueSchedule(companyId: 1, next: Now.AddMinutes(-1)),
                DueSchedule(companyId: 1, next: Now.AddHours(1)),      // not yet
                DueSchedule(companyId: 2, next: Now.AddMinutes(-1)));  // another company
            await host.Db.SaveChangesAsync();

            // A future worker iterates companies EXPLICITLY — CLAUDE.md: "every background worker binds an
            // explicit company scope."
            var due = await host.Schedules.GetDueAsync(1, Now);
            Assert.Single(due);
            Assert.Equal(1, due[0].CompanyID);
        }

        // ================================================================================================
        // 4. THE RUNNER, END TO END
        // ================================================================================================

        [Fact]
        public async Task A_scheduled_run_generates_archives_delivers_and_advances_the_schedule()
        {
            using var host = new ReportingTestHost();
            host.SeedEmployee(7, companyId: 1, roles: "Reports");

            var saved = await host.Schedules.SaveAsync(new ReportScheduleInput
            {
                ReportCode = TestReportDefinitions.SalesCode,
                Name = "daily sales",
                Frequency = ReportScheduleFrequency.Daily,
                AtHour = 6,
                Format = ReportOutputFormat.Csv,
                Recipients = new[]
                {
                    new ReportScheduleRecipientInput { Address = "finance@test.local" },
                    new ReportScheduleRecipientInput { Address = "audit@test.local", IsCc = true },
                },
            }, host.Ctx);

            var schedule = await host.Db.ReportSchedules.AsNoTracking().SingleAsync(s => s.Id == saved.ScheduleId);

            var outcome = await host.ScheduleRunner.RunAsync(schedule);

            Assert.Equal(ReportRunStatus.Succeeded, outcome.Status);

            // A scheduled report is ALWAYS archived: it is generated when nobody is watching, so what was sent
            // must be recoverable afterwards.
            Assert.NotNull(outcome.ArchiveEntryId);

            var run = await host.Db.ReportRuns.AsNoTracking().SingleAsync(r => r.Id == outcome.RunId);
            Assert.Equal(ReportRunKind.Scheduled, run.Kind);
            Assert.Equal(6, run.RowCount);

            // Two recipients attempted, zero SENT — because no mail transport is bound.
            Assert.Equal(2, outcome.DeliveriesAttempted);
            Assert.Equal(0, outcome.DeliveriesSent);

            var attempts = await host.Db.ReportDeliveryAttempts.AsNoTracking().ToListAsync();
            Assert.Equal(2, attempts.Count);

            // Skipped, NOT Sent. A report nobody received must never read as delivered.
            Assert.All(attempts, a => Assert.Equal(ReportDeliveryStatus.Skipped, a.Status));
            Assert.All(attempts, a => Assert.Contains("mail transport", a.Detail));

            // The schedule advanced.
            var advanced = await host.Db.ReportSchedules.AsNoTracking().SingleAsync(s => s.Id == saved.ScheduleId);
            Assert.Equal(Now, advanced.LastRunAt);
            Assert.Equal("Succeeded", advanced.LastRunStatus);
            Assert.Equal(new DateTime(2026, 5, 15, 6, 0, 0), advanced.NextRunAt);
        }

        [Fact]
        public async Task A_scheduled_run_ties_its_deliveries_to_the_run_by_correlation_id()
        {
            using var host = new ReportingTestHost();
            host.SeedEmployee(7, companyId: 1, roles: "Reports");

            var saved = await host.Schedules.SaveAsync(new ReportScheduleInput
            {
                ReportCode = TestReportDefinitions.SalesCode, Name = "s",
                Frequency = ReportScheduleFrequency.Daily, Format = ReportOutputFormat.Csv,
                Recipients = new[] { new ReportScheduleRecipientInput { Address = "a@test.local" } },
            }, host.Ctx);

            var schedule = await host.Db.ReportSchedules.AsNoTracking().SingleAsync();
            await host.ScheduleRunner.RunAsync(schedule);

            var run = await host.Db.ReportRuns.AsNoTracking().SingleAsync();
            var attempt = await host.Db.ReportDeliveryAttempts.AsNoTracking().SingleAsync();

            // One scheduled report and the emails it produced are ONE story in the log.
            Assert.NotNull(run.CorrelationId);
            Assert.Equal(run.CorrelationId, attempt.CorrelationId);
        }

        [Fact]
        public async Task A_leaver_stops_their_schedules_and_the_refusal_is_recorded()
        {
            using var host = new ReportingTestHost();
            host.SeedEmployee(7, companyId: 1, active: false);   // owner has left

            var schedule = DueSchedule(companyId: 1, next: Now.AddMinutes(-1), ownerEmpId: 7);
            host.Db.ReportSchedules.Add(schedule);
            await host.Db.SaveChangesAsync();

            var outcome = await host.ScheduleRunner.RunAsync(schedule);

            // Without this check a schedule would keep emailing a former employee's reports.
            Assert.Equal(ReportRunStatus.Denied, outcome.Status);
            Assert.Contains(outcome.Diagnostics, d => d.Code == "schedule_owner_unavailable");
            Assert.Empty(await host.Db.ReportDeliveryAttempts.AsNoTracking().ToListAsync());

            // ...and the schedule STILL advanced, so it does not stay due forever and re-run on every sweep.
            var advanced = await host.Db.ReportSchedules.AsNoTracking().SingleAsync();
            Assert.NotNull(advanced.NextRunAt);
            Assert.True(advanced.NextRunAt > Now);
        }

        [Fact]
        public async Task A_schedule_whose_report_fails_still_advances()
        {
            using var host = new ReportingTestHost();
            host.SeedEmployee(7, companyId: 1, roles: "Reports");

            var schedule = DueSchedule(companyId: 1, next: Now.AddMinutes(-1));
            schedule.Format = "Pdf";   // the PDF engine is unbound in this deployment
            host.Db.ReportSchedules.Add(schedule);
            await host.Db.SaveChangesAsync();

            var outcome = await host.ScheduleRunner.RunAsync(schedule);

            Assert.Equal(ReportRunStatus.Failed, outcome.Status);
            Assert.Equal(0, outcome.DeliveriesAttempted);

            // A schedule that failed and kept its old NextRunAt would be due forever.
            var advanced = await host.Db.ReportSchedules.AsNoTracking().SingleAsync();
            Assert.True(advanced.NextRunAt > Now);
            Assert.Equal("Failed", advanced.LastRunStatus);
        }

        [Fact]
        public async Task A_schedule_with_no_recipients_archives_only_and_reports_that_as_information()
        {
            using var host = new ReportingTestHost();
            host.SeedEmployee(7, companyId: 1, roles: "Reports");

            var schedule = DueSchedule(companyId: 1, next: Now.AddMinutes(-1));
            host.Db.ReportSchedules.Add(schedule);
            await host.Db.SaveChangesAsync();

            var outcome = await host.ScheduleRunner.RunAsync(schedule);

            // Not an error: a schedule may exist purely to ARCHIVE a report on a cadence.
            Assert.Equal(ReportRunStatus.Succeeded, outcome.Status);
            Assert.NotNull(outcome.ArchiveEntryId);
            Assert.Equal(0, outcome.DeliveriesAttempted);
            Assert.Contains(outcome.Diagnostics,
                d => d.Code == "delivery_no_recipients" && d.Severity == ReportDiagnosticSeverity.Info);
        }

        [Fact]
        public async Task RunDue_processes_only_this_companys_due_schedules()
        {
            using var host = new ReportingTestHost();
            host.SeedEmployee(7, companyId: 1, roles: "Reports");

            host.Db.ReportSchedules.AddRange(
                DueSchedule(companyId: 1, next: Now.AddMinutes(-1)),
                DueSchedule(companyId: 2, next: Now.AddMinutes(-1)));
            await host.Db.SaveChangesAsync();

            var outcomes = await host.ScheduleRunner.RunDueAsync(1);

            Assert.Single(outcomes);
        }

        [Fact]
        public async Task A_malformed_email_address_fails_that_recipient_without_stopping_the_others()
        {
            using var host = new ReportingTestHost();
            host.SeedEmployee(7, companyId: 1, roles: "Reports");

            var saved = await host.Schedules.SaveAsync(new ReportScheduleInput
            {
                ReportCode = TestReportDefinitions.SalesCode, Name = "s",
                Frequency = ReportScheduleFrequency.Daily, Format = ReportOutputFormat.Csv,
                Recipients = new[]
                {
                    new ReportScheduleRecipientInput { Address = "not-an-address" },
                    new ReportScheduleRecipientInput { Address = "good@test.local" },
                },
            }, host.Ctx);

            var schedule = await host.Db.ReportSchedules.AsNoTracking().SingleAsync();
            var outcome = await host.ScheduleRunner.RunAsync(schedule);

            var attempts = await host.Db.ReportDeliveryAttempts.AsNoTracking().ToListAsync();

            // One bad mailbox must not cancel the whole distribution list.
            Assert.Equal(2, attempts.Count);
            Assert.Equal(ReportDeliveryStatus.Failed,
                attempts.Single(a => a.Address == "not-an-address").Status);
            Assert.Equal(ReportDeliveryStatus.Skipped,
                attempts.Single(a => a.Address == "good@test.local").Status);
            Assert.Equal(ReportRunStatus.Succeeded, outcome.Status);
        }

        [Fact]
        public void No_hosted_service_drives_scheduling_in_this_slice()
        {
            // A structural assertion of a DELIBERATE omission (ADR-037 §Scheduling): the runner is invocable, and
            // nothing fires on its own. Recorded as a test so that adding a hosted service is a conscious change
            // that breaks this and must be justified — and so the reviewer can see the omission was intentional.
            var hostedServices = typeof(ReportingServiceCollectionExtensions).Assembly
                .GetTypes()
                .Where(t => !t.IsAbstract && t.Namespace == "CrossBuy.BL.Reporting")
                .Where(t => typeof(Microsoft.Extensions.Hosting.IHostedService).IsAssignableFrom(t))
                .ToList();

            Assert.Empty(hostedServices);
        }

        // ------------------------------------------------------------------------------------------------
        private static ReportSchedule DueSchedule(int companyId, DateTime next, int ownerEmpId = 7) => new()
        {
            CompanyID = companyId,
            ReportCode = TestReportDefinitions.SalesCode,
            Name = "due",
            Frequency = ReportScheduleFrequency.Daily,
            AtHour = 6,
            TimeZoneId = TimeZoneInfo.Local.Id,
            Format = "Csv",
            IsActive = true,
            NextRunAt = next,
            OwnerEmpId = ownerEmpId,
            CreatedAt = Now,
        };
    }
}
