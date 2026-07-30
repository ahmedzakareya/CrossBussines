using System.ComponentModel.DataAnnotations.Schema;

namespace CrossBuy.Models.Context.Admin
{
    public class AttendancePolicies : BaseEntity
    {
        public int ID { get; set; }
        public int LeavePolicyTypeID { get; set; }

        public int AllowedGraceMinutes { get; set; }
        public int WarningThresholdCount { get; set; }

        [Column(TypeName = "decimal(18,2)")]
        public decimal DeductionRatePerOccurrence { get; set; }

        public int UnexcusedAbsenceThreshold { get; set; }

        public TimeSpan WorkStartTime { get; set; }
        public TimeSpan WorkEndTime { get; set; }

        [Column(TypeName = "decimal(4,2)")]
        public decimal WorkHoursPerDay { get; set; }

        public TimeSpan? BreakStartTime { get; set; }
        public TimeSpan? BreakEndTime { get; set; }
        public int? BreakDurationMinutes { get; set; }

        public bool WorkOnSunday { get; set; }
        public bool WorkOnMonday { get; set; }
        public bool WorkOnTuesday { get; set; }
        public bool WorkOnWednesday { get; set; }
        public bool WorkOnThursday { get; set; }
        public bool WorkOnFriday { get; set; }
        public bool WorkOnSaturday { get; set; }

        public int WorkDaysPerWeek =>
            (WorkOnSunday ? 1 : 0) +
            (WorkOnMonday ? 1 : 0) +
            (WorkOnTuesday ? 1 : 0) +
            (WorkOnWednesday ? 1 : 0) +
            (WorkOnThursday ? 1 : 0) +
            (WorkOnFriday ? 1 : 0) +
            (WorkOnSaturday ? 1 : 0);

        // Permission / Short Leave Settings
        public bool AllowPermissions { get; set; }

        public int? MaxPermissionRequestsPerDay { get; set; }
        public int? MaxPermissionRequestsPerWeek { get; set; }
        public int? MaxPermissionRequestsPerMonth { get; set; }

        public int? MaxPermissionMinutesPerRequest { get; set; }
        public int? MaxPermissionMinutesPerDay { get; set; }
        public int? MaxPermissionMinutesPerWeek { get; set; }
        public int? MaxPermissionMinutesPerMonth { get; set; }

        public bool RequirePermissionApproval { get; set; }

        public bool RejectPermissionIfExceeded { get; set; }
        public bool DeductPermissionIfExceeded { get; set; }

        public bool AllowLateArrivalPermission { get; set; }
        public bool AllowEarlyLeavePermission { get; set; }
        public bool AllowDuringWorkPermission { get; set; }

        public bool LinkPermissionWithFingerprint { get; set; }

        [Column(TypeName = "decimal(18,2)")]
        public decimal? PermissionDeductionRatePerMinute { get; set; }

        [ForeignKey(nameof(LeavePolicyTypeID))]
        public Policies policies { get; set; }
    }
}
