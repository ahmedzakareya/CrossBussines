using CrossBuy.Models;

namespace CrossBuy.Models.Context.Reporting
{
    // How often a schedule fires.
    //
    // Deliberately an enum of business recurrences rather than a cron string. A cron field looks more powerful
    // but it is unvalidatable at save time, untranslatable in a UI, and it invites "* * * * *" — a report
    // engine firing every minute. These five cover every recurrence a finance/ops report actually needs, and
    // each one is checkable when the row is written.
    public enum ReportScheduleFrequency
    {
        // Every IntervalMinutes minutes from LastRunAt (or from creation). IntervalMinutes >= 15 is enforced.
        Interval = 0,

        Hourly = 1,
        Daily = 2,
        Weekly = 3,     // on DayOfWeek
        Monthly = 4,    // on DayOfMonth, clamped to the month's last day (31 → 28/29/30 as applicable)
    }

    // A standing instruction to generate a report and hand it to delivery channels.
    //
    // ARCHITECTURE NOTE (ADR-037 §Scheduling): this slice ships the schedule MODEL, the next-run CALCULATOR and
    // the RUNNER interface, but registers NO hosted service. Two reasons, both load-bearing:
    //   * ADR-013 constrains this deployment to a single worker process; adding a second background loop is a
    //     platform-level decision, not a reporting one.
    //   * A scheduler that runs before the archive, delivery and permission layers have been reviewed would be
    //     an unattended process generating and emailing documents. The runner is invocable on demand (and
    //     covered by tests) so the architecture is provably complete without turning that on.
    public class ReportSchedule : BaseEntity
    {
        public int Id { get; set; }
        public int CompanyID { get; set; }

        public string ReportCode { get; set; } = "";
        public int? TemplateId { get; set; }

        public string Name { get; set; } = "";
        public string? NameEn { get; set; }

        public ReportScheduleFrequency Frequency { get; set; }
        public int? IntervalMinutes { get; set; }    // Interval only
        public int? DayOfWeek { get; set; }          // Weekly only, 0 = Sunday (matches System.DayOfWeek)
        public int? DayOfMonth { get; set; }         // Monthly only, 1..31 (clamped when the month is shorter)
        public int AtHour { get; set; }              // 0..23, local to TimeZoneId
        public int AtMinute { get; set; }            // 0..59

        // IANA or Windows id. Stored per schedule because a group's month-end close is local, not server-local.
        public string TimeZoneId { get; set; } = "";

        public string Format { get; set; } = "Pdf";   // output-format name, as on ReportRun
        public string? ParametersJson { get; set; }   // the frozen parameter set the schedule runs with

        public bool IsActive { get; set; } = true;

        public DateTime? LastRunAt { get; set; }
        public string? LastRunStatus { get; set; }
        public DateTime? NextRunAt { get; set; }      // recomputed on every save and after every run

        // Whose rights the scheduled run executes with. A schedule must name an employee: an unattended run
        // with no principal would have to either skip authorization or invent a company-wide identity, and both
        // are how a scheduler becomes a data-exfiltration path.
        public int OwnerEmpId { get; set; }

        public DateTime? DeletedAt { get; set; }
    }

    // Where one schedule's output goes. Several rows per schedule.
    public class ReportScheduleRecipient : BaseEntity
    {
        public int Id { get; set; }
        public int CompanyID { get; set; }
        public int ScheduleId { get; set; }

        // Delivery channel key, e.g. "Email". Resolved through IReportDeliveryChannel — an unknown key is a
        // configuration error surfaced at save time, not a silently skipped recipient.
        public string ChannelKey { get; set; } = "Email";

        public string Address { get; set; } = "";     // channel-specific (an email address for Email)
        public int? EmployeeId { get; set; }          // set when the recipient is an employee, for audit
        public bool IsCc { get; set; }
    }

    public enum ReportDeliveryStatus
    {
        Pending = 0,
        Sent = 1,
        Failed = 2,

        // The channel was reached but deliberately did nothing — the default state today, because no mail
        // transport is bound (see NullReportMailSender). Skipped is NOT Sent: a report nobody received must
        // never read as delivered in the audit trail.
        Skipped = 3,
    }

    // One attempt to hand one artifact to one address. Append-only.
    public class ReportDeliveryAttempt : BaseEntity
    {
        public long Id { get; set; }
        public int CompanyID { get; set; }

        public int? ScheduleId { get; set; }
        public long? RunId { get; set; }
        public long? ArchiveEntryId { get; set; }

        public string ChannelKey { get; set; } = "";
        public string Address { get; set; } = "";
        public ReportDeliveryStatus Status { get; set; }
        public string? Detail { get; set; }           // provider message / skip reason
        public DateTime AttemptedAt { get; set; }
        public Guid? CorrelationId { get; set; }
    }
}