using System.Text.Json;
using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Reporting;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL.Reporting
{
    // ============================================================================================
    // Reporting Platform (ADR-037) — SCHEDULING ARCHITECTURE.
    //
    // WHAT SHIPS HERE: the schedule model, a next-run CALCULATOR (pure, timezone-aware, unit-tested), a schedule
    // service with validation, and a RUNNER that executes one due schedule end-to-end.
    //
    // WHAT DOES NOT SHIP HERE: a hosted service. That omission is a decision, recorded so it is not read as an
    // oversight:
    //
    //   * ADR-013 constrains this deployment to a single worker process. Adding a second background loop is a
    //     platform decision with an owner, not a side effect of a reporting slice.
    //   * A hosted service is a SINGLETON and may never inject a scoped service (CLAUDE.md — Batch C's own
    //     PermissionScopeStartupValidator broke exactly this and stopped the app from starting). The runner below
    //     is SCOPED, so the eventual worker must take IServiceScopeFactory and create a scope per due schedule.
    //     That worker belongs in the same change that adds it to Stage1DiWiringTests, which builds the real graph
    //     with ValidateOnBuild + ValidateScopes.
    //   * Turning on an unattended process that generates and emails documents before the permission and delivery
    //     layers have been reviewed would be exactly the wrong order.
    //
    // So RunDueAsync is callable — by a test, by an operator action, and later by one small hosted service — and
    // the architecture is provably complete without anything firing on its own.
    // ============================================================================================

    // Pure next-run arithmetic. No database, no DI, one static-ish job — which is why every clamping and
    // timezone rule below is directly testable.
    public interface IReportScheduleCalculator
    {
        // null = the schedule can never fire again (inactive, or malformed beyond repair).
        //
        // `after` is a LOCAL server time. The result is also local, so the caller compares it against the same
        // clock everything else in the platform reads.
        DateTime? ComputeNextRun(ReportSchedule schedule, DateTime after);

        // Validation is separate from calculation so a save can reject a bad schedule with field-level reasons
        // instead of silently storing one that never fires.
        IReadOnlyList<ReportDiagnostic> Validate(ReportSchedule schedule);
    }

    public class ReportScheduleCalculator : IReportScheduleCalculator
    {
        // 15 minutes is the floor for Interval. Below that a heavy report can still be running when its next
        // occurrence is due, and the platform would queue work faster than it completes.
        public const int MinIntervalMinutes = 15;

        public IReadOnlyList<ReportDiagnostic> Validate(ReportSchedule schedule)
        {
            var diagnostics = new List<ReportDiagnostic>();

            if (schedule.AtHour is < 0 or > 23)
                diagnostics.Add(ReportDiagnostic.Error("schedule_hour_invalid",
                    "The hour must be between 0 and 23.", nameof(schedule.AtHour)));

            if (schedule.AtMinute is < 0 or > 59)
                diagnostics.Add(ReportDiagnostic.Error("schedule_minute_invalid",
                    "The minute must be between 0 and 59.", nameof(schedule.AtMinute)));

            switch (schedule.Frequency)
            {
                case ReportScheduleFrequency.Interval:
                    if (schedule.IntervalMinutes is not >= MinIntervalMinutes)
                        diagnostics.Add(ReportDiagnostic.Error("schedule_interval_invalid",
                            $"An interval schedule needs at least {MinIntervalMinutes} minutes.",
                            nameof(schedule.IntervalMinutes)));
                    break;

                case ReportScheduleFrequency.Weekly:
                    if (schedule.DayOfWeek is not (>= 0 and <= 6))
                        diagnostics.Add(ReportDiagnostic.Error("schedule_dayofweek_invalid",
                            "A weekly schedule needs a day of week between 0 (Sunday) and 6.",
                            nameof(schedule.DayOfWeek)));
                    break;

                case ReportScheduleFrequency.Monthly:
                    if (schedule.DayOfMonth is not (>= 1 and <= 31))
                        diagnostics.Add(ReportDiagnostic.Error("schedule_dayofmonth_invalid",
                            "A monthly schedule needs a day of month between 1 and 31.",
                            nameof(schedule.DayOfMonth)));
                    break;
            }

            if (schedule.OwnerEmpId <= 0)
                diagnostics.Add(ReportDiagnostic.Error("schedule_owner_required",
                    "A schedule must name the employee whose rights it runs with.", nameof(schedule.OwnerEmpId)));

            if (!string.IsNullOrWhiteSpace(schedule.TimeZoneId) && !TryFindTimeZone(schedule.TimeZoneId, out _))
                diagnostics.Add(ReportDiagnostic.Warning("schedule_timezone_unknown",
                    $"Time zone '{schedule.TimeZoneId}' is not known on this host; server local time will be " +
                    "used instead.", nameof(schedule.TimeZoneId)));

            return diagnostics;
        }

        public DateTime? ComputeNextRun(ReportSchedule schedule, DateTime after)
        {
            if (!schedule.IsActive || schedule.DeletedAt != null) return null;
            if (Validate(schedule).Any(d => d.Severity == ReportDiagnosticSeverity.Error)) return null;

            var zone = ResolveZone(schedule.TimeZoneId);

            // Everything below is computed in the SCHEDULE'S OWN zone, then converted back. Computing in server
            // local time and adding an offset at the end gets daylight-saving transitions wrong — a 09:00 report
            // would arrive at 08:00 for half the year.
            var afterLocalToZone = TimeZoneInfo.ConvertTime(
                DateTime.SpecifyKind(after, DateTimeKind.Unspecified),
                TimeZoneInfo.Local, zone);

            DateTime nextInZone;

            switch (schedule.Frequency)
            {
                case ReportScheduleFrequency.Interval:
                {
                    var minutes = schedule.IntervalMinutes!.Value;

                    // Measured from the LAST RUN, not from a fixed origin: "every 30 minutes" means 30 minutes
                    // after it last completed. Measuring from an origin would fire immediately on resume after any
                    // outage and then again 30 minutes later.
                    var lastInZone = schedule.LastRunAt.HasValue
                        ? TimeZoneInfo.ConvertTime(
                            DateTime.SpecifyKind(schedule.LastRunAt.Value, DateTimeKind.Unspecified),
                            TimeZoneInfo.Local, zone)
                        : afterLocalToZone;

                    nextInZone = lastInZone.AddMinutes(minutes);
                    if (nextInZone <= afterLocalToZone) nextInZone = afterLocalToZone.AddMinutes(minutes);
                    break;
                }

                case ReportScheduleFrequency.Hourly:
                {
                    // On the schedule's minute, every hour.
                    var candidate = new DateTime(afterLocalToZone.Year, afterLocalToZone.Month,
                        afterLocalToZone.Day, afterLocalToZone.Hour, schedule.AtMinute, 0);
                    nextInZone = candidate > afterLocalToZone ? candidate : candidate.AddHours(1);
                    break;
                }

                case ReportScheduleFrequency.Daily:
                {
                    var candidate = AtTime(afterLocalToZone.Date, schedule);
                    nextInZone = candidate > afterLocalToZone ? candidate : candidate.AddDays(1);
                    break;
                }

                case ReportScheduleFrequency.Weekly:
                {
                    var wanted = schedule.DayOfWeek!.Value;
                    var days = (wanted - (int)afterLocalToZone.DayOfWeek + 7) % 7;
                    var candidate = AtTime(afterLocalToZone.Date.AddDays(days), schedule);

                    // days == 0 means "today is the day"; if the time has passed, it is next week, not today.
                    if (candidate <= afterLocalToZone) candidate = candidate.AddDays(7);
                    nextInZone = candidate;
                    break;
                }

                case ReportScheduleFrequency.Monthly:
                {
                    var candidate = MonthlyCandidate(afterLocalToZone.Year, afterLocalToZone.Month, schedule);
                    if (candidate <= afterLocalToZone)
                    {
                        var next = afterLocalToZone.AddMonths(1);
                        candidate = MonthlyCandidate(next.Year, next.Month, schedule);
                    }
                    nextInZone = candidate;
                    break;
                }

                default:
                    return null;
            }

            return TimeZoneInfo.ConvertTime(
                DateTime.SpecifyKind(nextInZone, DateTimeKind.Unspecified), zone, TimeZoneInfo.Local);
        }

        private static DateTime AtTime(DateTime date, ReportSchedule schedule) =>
            new(date.Year, date.Month, date.Day, schedule.AtHour, schedule.AtMinute, 0);

        // "The 31st" in a 30-day month is the 30th, and in February the 28th (or 29th). Clamping rather than
        // skipping: a month-end report configured for the 31st must run every month, not eight times a year.
        private static DateTime MonthlyCandidate(int year, int month, ReportSchedule schedule)
        {
            var day = Math.Min(schedule.DayOfMonth!.Value, DateTime.DaysInMonth(year, month));
            return new DateTime(year, month, day, schedule.AtHour, schedule.AtMinute, 0);
        }

        private static TimeZoneInfo ResolveZone(string? id) =>
            TryFindTimeZone(id, out var zone) ? zone! : TimeZoneInfo.Local;

        private static bool TryFindTimeZone(string? id, out TimeZoneInfo? zone)
        {
            zone = null;
            if (string.IsNullOrWhiteSpace(id)) return false;
            try
            {
                zone = TimeZoneInfo.FindSystemTimeZoneById(id);
                return true;
            }
            catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
            {
                // An unknown id falls back to server local time WITH A WARNING from Validate — never an
                // exception. A schedule must not become unreadable because a host image lacks a tz database entry.
                return false;
            }
        }
    }

    // ============================================================================================
    // Building the identity a scheduled run executes as.
    // ============================================================================================
    public interface IReportSchedulePrincipalFactory
    {
        // The context the run authorizes against. MUST be the schedule OWNER's rights — never an
        // "administrative" identity, or a schedule would become a way to read reports its creator cannot.
        Task<BusinessContext?> CreateAsync(int companyId, int ownerEmployeeId, Guid correlationId,
            CancellationToken cancellationToken = default);
    }

    // Builds the owner's context by reading the existing identity tables directly.
    //
    // READ-ONLY, and it decides nothing: it loads the employee's user account and that account's role names, then
    // hands them to the SAME IReportPermissionEvaluator an interactive request goes through. No authorization
    // service is called, extended or modified — the evaluator makes the decision either way.
    //
    // A LEAVER STOPS THEIR SCHEDULES: an inactive employee resolves to null, the run is recorded Denied, and the
    // report is not produced. Without that check, a schedule would keep emailing a former employee's reports.
    public class IdentityReportSchedulePrincipalFactory : IReportSchedulePrincipalFactory
    {
        private readonly CrossDbContext _db;

        public IdentityReportSchedulePrincipalFactory(CrossDbContext db) { _db = db; }

        public async Task<BusinessContext?> CreateAsync(int companyId, int ownerEmployeeId, Guid correlationId,
            CancellationToken cancellationToken = default)
        {
            if (companyId <= 0 || ownerEmployeeId <= 0) return null;

            var employee = await _db.Employee.AsNoTracking()
                .Where(e => e.ID == ownerEmployeeId && e.EmpCompanyID == companyId && e.IsActive)
                .Select(e => new { e.ID, e.UserId, e.BranchID })
                .FirstOrDefaultAsync(cancellationToken);

            if (employee == null) return null;

            var roles = string.IsNullOrEmpty(employee.UserId)
                ? new List<string>()
                : await _db.UserRoles.AsNoTracking()
                    .Where(ur => ur.UserId == employee.UserId)
                    .Join(_db.Roles.AsNoTracking(), ur => ur.RoleId, r => r.Id, (_, r) => r.Name!)
                    .Where(name => name != null)
                    .ToListAsync(cancellationToken);

            return new BusinessContext
            {
                CompanyId = companyId,
                EmployeeId = employee.ID,
                BranchId = employee.BranchID,
                UserId = employee.UserId ?? "",
                Roles = roles,
                CorrelationId = correlationId,

                // Worker, not System: a worker context is authorized like any other caller. System would grant the
                // narrow SystemContextPolicy action list, which is not what a user's scheduled report should hold.
                Source = BusinessContextSource.Worker,
            };
        }
    }

    // ============================================================================================
    // The schedule service
    // ============================================================================================
    public sealed class ReportScheduleInput
    {
        public int Id { get; init; }
        public required string ReportCode { get; init; }
        public int? TemplateId { get; init; }
        public required string Name { get; init; }
        public string? NameEn { get; init; }
        public ReportScheduleFrequency Frequency { get; init; } = ReportScheduleFrequency.Daily;
        public int? IntervalMinutes { get; init; }
        public int? DayOfWeek { get; init; }
        public int? DayOfMonth { get; init; }
        public int AtHour { get; init; }
        public int AtMinute { get; init; }
        public string? TimeZoneId { get; init; }
        public ReportOutputFormat Format { get; init; } = ReportOutputFormat.Pdf;
        public IReadOnlyDictionary<string, string?> Parameters { get; init; } = new Dictionary<string, string?>();
        public bool IsActive { get; init; } = true;
        public IReadOnlyList<ReportScheduleRecipientInput> Recipients { get; init; } =
            Array.Empty<ReportScheduleRecipientInput>();
    }

    public sealed class ReportScheduleRecipientInput
    {
        public string ChannelKey { get; init; } = ReportDeliveryChannels.Email;
        public required string Address { get; init; }
        public int? EmployeeId { get; init; }
        public bool IsCc { get; init; }
    }

    public sealed class ReportScheduleSaveResult
    {
        public bool Success { get; init; }
        public int ScheduleId { get; init; }
        public DateTime? NextRunAt { get; init; }
        public IReadOnlyList<ReportDiagnostic> Diagnostics { get; init; } = Array.Empty<ReportDiagnostic>();

        public static ReportScheduleSaveResult Fail(params ReportDiagnostic[] diagnostics) =>
            new() { Success = false, Diagnostics = diagnostics };
    }

    public sealed class ReportScheduleRunOutcome
    {
        public required int ScheduleId { get; init; }
        public ReportRunStatus Status { get; init; }
        public long? RunId { get; init; }
        public long? ArchiveEntryId { get; init; }
        public int DeliveriesAttempted { get; init; }
        public int DeliveriesSent { get; init; }
        public DateTime? NextRunAt { get; init; }
        public IReadOnlyList<ReportDiagnostic> Diagnostics { get; init; } = Array.Empty<ReportDiagnostic>();
    }

    public interface IReportScheduleService
    {
        Task<IReadOnlyList<ReportSchedule>> ListAsync(BusinessContext context,
            CancellationToken cancellationToken = default);

        Task<ReportScheduleSaveResult> SaveAsync(ReportScheduleInput input, BusinessContext context,
            CancellationToken cancellationToken = default);

        Task<bool> SetActiveAsync(int scheduleId, bool active, BusinessContext context,
            CancellationToken cancellationToken = default);

        Task<bool> DeleteAsync(int scheduleId, BusinessContext context,
            CancellationToken cancellationToken = default);

        // Schedules whose NextRunAt has passed. Company-scoped like everything else — a future worker iterates
        // companies explicitly (CLAUDE.md: "every background worker binds an explicit company scope").
        Task<IReadOnlyList<ReportSchedule>> GetDueAsync(int companyId, DateTime asOf,
            CancellationToken cancellationToken = default);
    }

    public interface IReportScheduleRunner
    {
        // Runs ONE schedule end to end: build the owner's context, generate, archive, deliver, advance NextRunAt.
        // Never throws for a business failure — the outcome carries the diagnostics and the schedule is still
        // advanced, so one broken report cannot wedge a schedule into firing forever.
        Task<ReportScheduleRunOutcome> RunAsync(ReportSchedule schedule,
            CancellationToken cancellationToken = default);

        Task<IReadOnlyList<ReportScheduleRunOutcome>> RunDueAsync(int companyId,
            CancellationToken cancellationToken = default);
    }

    public class ReportScheduleService : IReportScheduleService
    {
        private readonly CrossDbContext _db;
        private readonly IReportCatalog _catalog;
        private readonly IReportAuthorizationService _authorization;
        private readonly IReportScheduleCalculator _calculator;
        private readonly IReportClock _clock;

        public ReportScheduleService(CrossDbContext db, IReportCatalog catalog,
            IReportAuthorizationService authorization, IReportScheduleCalculator calculator, IReportClock clock)
        {
            _db = db;
            _catalog = catalog;
            _authorization = authorization;
            _calculator = calculator;
            _clock = clock;
        }

        public async Task<IReadOnlyList<ReportSchedule>> ListAsync(BusinessContext context,
            CancellationToken cancellationToken = default)
        {
            if (context.CompanyId <= 0) return Array.Empty<ReportSchedule>();

            var isAdmin = await _authorization.IsAdministratorAsync(context, cancellationToken);

            var q = _db.ReportSchedules.AsNoTracking()
                .Where(s => s.CompanyID == context.CompanyId && s.DeletedAt == null);

            // Your own schedules unless you administer reporting. A schedule reveals both a recipient list and a
            // parameter set, so it is not a shared listing by default.
            if (!isAdmin) q = q.Where(s => s.OwnerEmpId == context.EmployeeId);

            return await q.OrderBy(s => s.Name).ToListAsync(cancellationToken);
        }

        public async Task<ReportScheduleSaveResult> SaveAsync(ReportScheduleInput input, BusinessContext context,
            CancellationToken cancellationToken = default)
        {
            if (context.CompanyId <= 0)
                return ReportScheduleSaveResult.Fail(ReportDiagnostic.Error(
                    ReportAuthorizationService.CodeCompanyUnresolved, "No company is resolved."));

            if (context.EmployeeId is not > 0)
                return ReportScheduleSaveResult.Fail(ReportDiagnostic.Error("schedule_owner_required",
                    "A schedule requires a resolved employee."));

            var definition = _catalog.GetDefinition(input.ReportCode);

            if (!definition.Capabilities.AllowSchedule)
                return ReportScheduleSaveResult.Fail(ReportDiagnostic.Error("schedule_not_allowed",
                    $"Report '{definition.Code}' may not be scheduled."));

            // The creator must be able to RUN the report NOW. Checked at save time as well as at run time,
            // because a schedule is an authorization decision that outlives the request that made it.
            var decision = await _authorization.AuthorizeReportAsync(definition, ReportAccessLevel.Run, context,
                cancellationToken);
            if (!decision.Allowed)
                return ReportScheduleSaveResult.Fail(ReportDiagnostic.Error(
                    decision.ReasonCode ?? "schedule_denied",
                    decision.Reason ?? "You may not schedule that report."));

            if (!definition.Capabilities.SupportsFormat(input.Format))
                return ReportScheduleSaveResult.Fail(ReportDiagnostic.Error("schedule_format_not_allowed",
                    $"Report '{definition.Code}' cannot be produced as {input.Format}."));

            var now = _clock.LocalNow;
            ReportSchedule schedule;

            if (input.Id > 0)
            {
                schedule = await _db.ReportSchedules
                               .FirstOrDefaultAsync(s => s.Id == input.Id && s.CompanyID == context.CompanyId
                                                         && s.DeletedAt == null, cancellationToken)
                           ?? throw new ReportingException($"Report schedule {input.Id} was not found.");

                var isAdmin = await _authorization.IsAdministratorAsync(context, cancellationToken);
                if (schedule.OwnerEmpId != context.EmployeeId && !isAdmin)
                    return ReportScheduleSaveResult.Fail(ReportDiagnostic.Error("schedule_denied",
                        "You may only change your own schedules."));

                schedule.updatedBy = context.EmployeeId;
                schedule.UpdatedAt = now;
            }
            else
            {
                schedule = new ReportSchedule
                {
                    CompanyID = context.CompanyId,
                    OwnerEmpId = context.EmployeeId!.Value,
                    CreatedBy = context.EmployeeId,
                    CreatedAt = now,
                };
                _db.ReportSchedules.Add(schedule);
            }

            schedule.ReportCode = definition.Code;
            schedule.TemplateId = input.TemplateId;
            schedule.Name = input.Name.Trim();
            schedule.NameEn = input.NameEn?.Trim();
            schedule.Frequency = input.Frequency;
            schedule.IntervalMinutes = input.IntervalMinutes;
            schedule.DayOfWeek = input.DayOfWeek;
            schedule.DayOfMonth = input.DayOfMonth;
            schedule.AtHour = input.AtHour;
            schedule.AtMinute = input.AtMinute;
            schedule.TimeZoneId = string.IsNullOrWhiteSpace(input.TimeZoneId)
                ? TimeZoneInfo.Local.Id
                : input.TimeZoneId;
            schedule.Format = input.Format.ToString();
            schedule.IsActive = input.IsActive;

            // System-supplied keys are stripped before storing. A schedule that could persist CompanyId would be a
            // stored cross-tenant read waiting for someone to edit the row.
            var parameters = input.Parameters
                .Where(kv => !ReportSystemParameters.IsSystemKey(kv.Key))
                .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
            schedule.ParametersJson = JsonSerializer.Serialize(parameters);

            var diagnostics = _calculator.Validate(schedule).ToList();
            if (diagnostics.Any(d => d.Severity == ReportDiagnosticSeverity.Error))
                return new ReportScheduleSaveResult { Success = false, Diagnostics = diagnostics };

            schedule.NextRunAt = _calculator.ComputeNextRun(schedule, now);

            await _db.SaveChangesAsync(cancellationToken);

            // Recipients are replaced wholesale rather than diffed: the list is short, and a diff would have to
            // decide what an "unchanged" recipient is when only its Cc flag moved.
            var existing = _db.ReportScheduleRecipients.Where(r => r.ScheduleId == schedule.Id);
            _db.ReportScheduleRecipients.RemoveRange(existing);

            foreach (var recipient in input.Recipients.Where(r => !string.IsNullOrWhiteSpace(r.Address)))
                _db.ReportScheduleRecipients.Add(new ReportScheduleRecipient
                {
                    CompanyID = context.CompanyId,
                    ScheduleId = schedule.Id,
                    ChannelKey = string.IsNullOrWhiteSpace(recipient.ChannelKey)
                        ? ReportDeliveryChannels.Email
                        : recipient.ChannelKey,
                    Address = recipient.Address.Trim(),
                    EmployeeId = recipient.EmployeeId,
                    IsCc = recipient.IsCc,
                    CreatedBy = context.EmployeeId,
                    CreatedAt = now,
                });

            await _db.SaveChangesAsync(cancellationToken);

            return new ReportScheduleSaveResult
            {
                Success = true,
                ScheduleId = schedule.Id,
                NextRunAt = schedule.NextRunAt,
                Diagnostics = diagnostics,
            };
        }

        public async Task<bool> SetActiveAsync(int scheduleId, bool active, BusinessContext context,
            CancellationToken cancellationToken = default)
        {
            var schedule = await LoadOwnAsync(scheduleId, context, cancellationToken);
            if (schedule == null) return false;

            schedule.IsActive = active;

            // Reactivating recomputes from NOW, so a schedule paused for a month does not fire a backlog the
            // moment it is switched on.
            schedule.NextRunAt = active ? _calculator.ComputeNextRun(schedule, _clock.LocalNow) : null;
            schedule.updatedBy = context.EmployeeId;
            schedule.UpdatedAt = _clock.LocalNow;
            await _db.SaveChangesAsync(cancellationToken);
            return true;
        }

        public async Task<bool> DeleteAsync(int scheduleId, BusinessContext context,
            CancellationToken cancellationToken = default)
        {
            var schedule = await LoadOwnAsync(scheduleId, context, cancellationToken);
            if (schedule == null) return false;

            schedule.DeletedAt = _clock.LocalNow;
            schedule.IsActive = false;
            schedule.NextRunAt = null;
            schedule.updatedBy = context.EmployeeId;
            schedule.UpdatedAt = _clock.LocalNow;
            await _db.SaveChangesAsync(cancellationToken);
            return true;
        }

        public async Task<IReadOnlyList<ReportSchedule>> GetDueAsync(int companyId, DateTime asOf,
            CancellationToken cancellationToken = default)
        {
            if (companyId <= 0) return Array.Empty<ReportSchedule>();

            return await _db.ReportSchedules.AsNoTracking()
                .Where(s => s.CompanyID == companyId && s.DeletedAt == null && s.IsActive
                            && s.NextRunAt != null && s.NextRunAt <= asOf)
                .OrderBy(s => s.NextRunAt)
                .ToListAsync(cancellationToken);
        }

        private async Task<ReportSchedule?> LoadOwnAsync(int scheduleId, BusinessContext context,
            CancellationToken cancellationToken)
        {
            if (context.CompanyId <= 0) return null;

            var schedule = await _db.ReportSchedules
                .FirstOrDefaultAsync(s => s.Id == scheduleId && s.CompanyID == context.CompanyId
                                          && s.DeletedAt == null, cancellationToken);
            if (schedule == null) return null;

            if (schedule.OwnerEmpId == context.EmployeeId) return schedule;
            return await _authorization.IsAdministratorAsync(context, cancellationToken) ? schedule : null;
        }
    }

    // ============================================================================================
    // The runner
    // ============================================================================================
    public class ReportScheduleRunner : IReportScheduleRunner
    {
        private readonly CrossDbContext _db;
        private readonly IReportEngine _engine;
        private readonly IReportScheduleService _schedules;
        private readonly IReportScheduleCalculator _calculator;
        private readonly IReportSchedulePrincipalFactory _principals;
        private readonly IReportDeliveryService _delivery;
        private readonly IReportClock _clock;
        private readonly ILogger<ReportScheduleRunner> _logger;

        public ReportScheduleRunner(CrossDbContext db, IReportEngine engine, IReportScheduleService schedules,
            IReportScheduleCalculator calculator, IReportSchedulePrincipalFactory principals,
            IReportDeliveryService delivery, IReportClock clock, ILogger<ReportScheduleRunner> logger)
        {
            _db = db;
            _engine = engine;
            _schedules = schedules;
            _calculator = calculator;
            _principals = principals;
            _delivery = delivery;
            _clock = clock;
            _logger = logger;
        }

        public async Task<IReadOnlyList<ReportScheduleRunOutcome>> RunDueAsync(int companyId,
            CancellationToken cancellationToken = default)
        {
            var due = await _schedules.GetDueAsync(companyId, _clock.LocalNow, cancellationToken);
            var outcomes = new List<ReportScheduleRunOutcome>(due.Count);

            foreach (var schedule in due)
            {
                cancellationToken.ThrowIfCancellationRequested();
                outcomes.Add(await RunAsync(schedule, cancellationToken));
            }
            return outcomes;
        }

        public async Task<ReportScheduleRunOutcome> RunAsync(ReportSchedule schedule,
            CancellationToken cancellationToken = default)
        {
            var correlationId = Guid.NewGuid();
            var diagnostics = new List<ReportDiagnostic>();

            // NextRunAt is advanced in a finally-equivalent position at the end of EVERY path below, including the
            // failure paths. A schedule that failed and kept its old NextRunAt would be due forever and would
            // re-run on every sweep.
            var context = await _principals.CreateAsync(schedule.CompanyID, schedule.OwnerEmpId, correlationId,
                cancellationToken);

            if (context == null)
            {
                diagnostics.Add(ReportDiagnostic.Error("schedule_owner_unavailable",
                    $"Schedule {schedule.Id} could not resolve its owner (employee {schedule.OwnerEmpId}); the " +
                    "employee may be inactive or have no company. Nothing was generated or delivered."));

                _logger.LogWarning(
                    "Report schedule {ScheduleId} skipped: owner {OwnerEmpId} could not be resolved.",
                    schedule.Id, schedule.OwnerEmpId);

                return await FinishAsync(schedule, ReportRunStatus.Denied, null, null, 0, 0, diagnostics,
                    cancellationToken);
            }

            ReportFormats.TryParse(schedule.Format, out var format);

            var parameters = DeserializeParameters(schedule.ParametersJson);

            var request = new ReportRequest
            {
                ReportCode = schedule.ReportCode,
                TemplateId = schedule.TemplateId,
                Format = format,
                Kind = ReportRunKind.Scheduled,
                Parameters = parameters,

                // A scheduled report is always archived: it is generated when nobody is watching, so the artifact
                // that was sent must be recoverable afterwards. This is also what gives a delivery attempt
                // something to reference.
                Archive = true,
                CorrelationId = correlationId,
            };

            ReportResult result;
            try
            {
                result = await _engine.GenerateAsync(request, context, cancellationToken);
            }
            catch (Exception ex)
            {
                // A programming error (unregistered report, missing data source, unbound PDF engine) must not take
                // the sweep down with it — the next schedule still deserves to run.
                _logger.LogError(ex, "Report schedule {ScheduleId} ({ReportCode}) failed to generate.",
                    schedule.Id, schedule.ReportCode);
                diagnostics.Add(ReportDiagnostic.Error("schedule_generate_failed", ex.Message));
                return await FinishAsync(schedule, ReportRunStatus.Failed, null, null, 0, 0, diagnostics,
                    cancellationToken);
            }

            diagnostics.AddRange(result.Diagnostics);

            if (!result.IsSuccess || result.Artifact == null)
                return await FinishAsync(schedule, result.Status, result.Run?.RunId, null, 0, 0, diagnostics,
                    cancellationToken);

            var deliveries = await _delivery.DeliverScheduleAsync(schedule, result, context, correlationId,
                cancellationToken);
            diagnostics.AddRange(deliveries.Diagnostics);

            return await FinishAsync(schedule, ReportRunStatus.Succeeded, result.Run?.RunId,
                result.Run?.ArchiveEntryId, deliveries.Attempted, deliveries.Sent, diagnostics, cancellationToken);
        }

        private async Task<ReportScheduleRunOutcome> FinishAsync(ReportSchedule schedule, ReportRunStatus status,
            long? runId, long? archiveEntryId, int attempted, int sent, List<ReportDiagnostic> diagnostics,
            CancellationToken cancellationToken)
        {
            var now = _clock.LocalNow;

            var tracked = await _db.ReportSchedules
                .FirstOrDefaultAsync(s => s.Id == schedule.Id, cancellationToken);

            DateTime? next = null;
            if (tracked != null)
            {
                tracked.LastRunAt = now;
                tracked.LastRunStatus = status.ToString();
                next = _calculator.ComputeNextRun(tracked, now);
                tracked.NextRunAt = next;
                tracked.UpdatedAt = now;
                await _db.SaveChangesAsync(cancellationToken);
            }

            return new ReportScheduleRunOutcome
            {
                ScheduleId = schedule.Id,
                Status = status,
                RunId = runId,
                ArchiveEntryId = archiveEntryId,
                DeliveriesAttempted = attempted,
                DeliveriesSent = sent,
                NextRunAt = next,
                Diagnostics = diagnostics,
            };
        }

        private static IReadOnlyDictionary<string, string?> DeserializeParameters(string? json)
        {
            if (string.IsNullOrWhiteSpace(json)) return new Dictionary<string, string?>();
            try
            {
                return JsonSerializer.Deserialize<Dictionary<string, string?>>(json)
                       ?? new Dictionary<string, string?>();
            }
            catch (JsonException)
            {
                // A corrupt blob runs the report with NO parameters, which for a report with required parameters
                // fails loudly at binding. That is the right outcome: better a recorded parameter_required failure
                // than a report silently produced for the wrong period.
                return new Dictionary<string, string?>();
            }
        }
    }
}