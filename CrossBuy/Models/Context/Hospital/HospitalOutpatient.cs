using Microsoft.EntityFrameworkCore;

namespace CrossBuy.Models.Context.Hospital
{
	// ==========================================================================================
	// HOSPITAL OUTPATIENT — departments, practitioners, appointments (unit H4).
	//
	// A practitioner is one clinical record per doctor: optionally the employee they are (their
	// photo, their payroll), the days and hours they see patients, their clinic room and their
	// professional licence. An appointment is a patient with a practitioner on a DATE at a MINUTE
	// OF THE DAY — clinic wall-clock time, never an instant — so a booking cannot shift with a time
	// zone; arrival, start and finish are instants (UTC) because they are measured.
	//
	// COMPANY ISOLATION IS EXPLICIT, as in every hospital file: HospitalOutpatientService names the
	// caller's company in every query. deploy/sql/hospital_h4_outpatient.sql builds the tables.
	// ==========================================================================================

	public static class HospitalEngagements
	{
		public const string Permanent = "Permanent";
		public const string PartTime = "PartTime";
		public const string Visiting = "Visiting";
		public static readonly IReadOnlyList<string> All = new[] { Permanent, PartTime, Visiting };
		public static bool IsKnown(string? s) => s != null && All.Contains(s, StringComparer.Ordinal);
	}

	public static class HospitalPayChannels
	{
		public const string Payroll = "Payroll";
		public const string Vendor = "Vendor";
		public static readonly IReadOnlyList<string> All = new[] { Payroll, Vendor };
		public static bool IsKnown(string? s) => s != null && All.Contains(s, StringComparer.Ordinal);
	}

	/// What a practitioner may do; later units read it (a surgeon in theatre, an anaesthetist).
	public static class HospitalPrivileges
	{
		public static readonly IReadOnlyList<string> All = new[] { "opd", "ip", "proc", "surg", "er", "anes" };
		public static bool IsKnown(string? s) => s != null && All.Contains(s, StringComparer.Ordinal);
	}

	public static class HospitalVisitTypes
	{
		public const string New = "New";
		public const string FollowUp = "FollowUp";
		public const string Procedure = "Procedure";
		public const string Video = "Video";
		public static readonly IReadOnlyList<string> All = new[] { New, FollowUp, Procedure, Video };
		public static bool IsKnown(string? s) => s != null && All.Contains(s, StringComparer.Ordinal);
	}

	public static class HospitalAppointmentStatus
	{
		public const string Booked = "Booked";
		public const string Arrived = "Arrived";
		public const string InRoom = "InRoom";
		public const string Done = "Done";
		public const string NoShow = "NoShow";
		public const string Cancelled = "Cancelled";
		public static readonly IReadOnlyList<string> All = new[] { Booked, Arrived, InRoom, Done, NoShow, Cancelled };

		/// The only moves the desk and the doctor may make. Anything else is refused.
		public static bool CanMove(string from, string to) => (from, to) switch
		{
			(Booked, Arrived) or (Booked, NoShow) or (Booked, Cancelled) => true,
			(Arrived, InRoom) or (Arrived, Cancelled) => true,
			(InRoom, Done) => true,
			_ => false,
		};
	}

	public class HospitalDepartment
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public string Code { get; set; } = "";
		public string Name { get; set; } = "";
		public string? NameEn { get; set; }
		public int SortOrder { get; set; }
		public bool IsActive { get; set; } = true;
		public DateTime CreatedAt { get; set; }
		public int? CreatedByEmployeeId { get; set; }
	}

	public class HospitalPractitioner
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public int? EmployeeId { get; set; }
		public string Name { get; set; } = "";
		public string NameEn { get; set; } = "";
		public string? Title { get; set; }
		public string? TitleEn { get; set; }
		public int? DepartmentId { get; set; }
		public string Engagement { get; set; } = HospitalEngagements.Permanent;
		public string PayChannel { get; set; } = HospitalPayChannels.Payroll;
		public string? LicenseNumber { get; set; }
		public DateTime? LicenseExpiry { get; set; }
		public string? Privileges { get; set; }
		/// Comma-separated DayOfWeek numbers (0 = Sunday … 6 = Saturday).
		public string? WorkDays { get; set; }
		public int SessionStartMinute { get; set; } = 9 * 60;
		public int SessionEndMinute { get; set; } = 14 * 60;
		public int SlotMinutes { get; set; } = 15;
		public int? ClinicRoomId { get; set; }
		/// H6: the catalogue service a visit to this doctor is billed as (the consultation fee).
		public int? ConsultationServiceId { get; set; }
		public bool IsActive { get; set; } = true;
		public DateTime CreatedAt { get; set; }
		public int? CreatedByEmployeeId { get; set; }
		public DateTime? UpdatedAt { get; set; }
		public int? UpdatedByEmployeeId { get; set; }

		public IEnumerable<DayOfWeek> Days() => (WorkDays ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries)
			.Select(s => int.TryParse(s, out var d) && d is >= 0 and <= 6 ? (DayOfWeek?)d : null).Where(d => d != null).Select(d => d!.Value);
	}

	public class HospitalAppointment
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public int PatientId { get; set; }
		public int PractitionerId { get; set; }
		/// The clinic day; only the date part is meaningful.
		public DateTime AppointmentDate { get; set; }
		public int StartMinute { get; set; }
		public int DurationMinutes { get; set; }
		public string VisitType { get; set; } = HospitalVisitTypes.New;
		public string Status { get; set; } = HospitalAppointmentStatus.Booked;
		public int? QueueNumber { get; set; }
		public string? Note { get; set; }
		public DateTime? ArrivedAt { get; set; }
		public DateTime? StartedAt { get; set; }
		public DateTime? FinishedAt { get; set; }
		public DateTime CreatedAt { get; set; }
		public int? CreatedByEmployeeId { get; set; }
		public DateTime? UpdatedAt { get; set; }
		public int? UpdatedByEmployeeId { get; set; }
	}

	public static class HospitalOutpatientModel
	{
		public static void Configure(ModelBuilder b)
		{
			b.Entity<HospitalDepartment>(e => e.HasIndex(x => new { x.CompanyId, x.Code }).IsUnique());
			b.Entity<HospitalPractitioner>(e => e.HasIndex(x => new { x.CompanyId, x.DepartmentId }));
			b.Entity<HospitalAppointment>(e =>
			{
				e.HasIndex(x => new { x.CompanyId, x.PractitionerId, x.AppointmentDate, x.StartMinute }).IsUnique()
					.HasFilter("[Status] <> 'Cancelled'").HasDatabaseName("UX_HospitalAppointments_OneLivePerSlot");
				// H83: a queue ticket is one per hospital day — the database refuses a duplicate from any writer
				// (deploy/sql/hospital_h83_queue_ticket_unique.sql); MoveAsync's applock only orders its own callers
				e.HasIndex(x => new { x.CompanyId, x.AppointmentDate, x.QueueNumber }).IsUnique()
					.HasFilter("[QueueNumber] IS NOT NULL").HasDatabaseName("UX_HospitalAppointments_OneTicketPerDay");
				e.HasIndex(x => new { x.CompanyId, x.AppointmentDate });
				e.HasIndex(x => new { x.CompanyId, x.PatientId, x.AppointmentDate });
			});
		}
	}
}
