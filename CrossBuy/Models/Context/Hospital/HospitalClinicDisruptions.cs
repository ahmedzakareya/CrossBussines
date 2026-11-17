using Microsoft.EntityFrameworkCore;

namespace CrossBuy.Models.Context.Hospital
{
	// ==========================================================================================
	// HOSPITAL H161 — clinic disruptions: the doctor is late or absent, the room changed, a device is down. The desk
	// declares it once; every patient booked in the window is listed with a plan (keep and inform, move to a proposed
	// slot, cancel), the plans are previewed and approved together, the moves are made, each patient is messaged, and
	// those the message could not reach are called — each call recorded — until every one is reached.
	// ==========================================================================================
	public static class HospitalDisruptionKind
	{
		public const string Late = "Late", Absent = "Absent", Room = "Room", Equipment = "Equipment";
		public static readonly IReadOnlyList<string> All = new[] { Late, Absent, Room, Equipment };
	}

	public static class HospitalDisruptionPlan
	{
		public const string Keep = "Keep", Move = "Move", Cancel = "Cancel";
		public static readonly IReadOnlyList<string> All = new[] { Keep, Move, Cancel };
	}

	public static class HospitalDisruptionStatus
	{
		public const string Open = "Open", Applied = "Applied", Closed = "Closed";
	}

	public class HospitalClinicDisruption
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public string DisruptionNo { get; set; } = "";
		public int PractitionerId { get; set; }
		public DateTime Day { get; set; }
		public string Kind { get; set; } = HospitalDisruptionKind.Late;
		public int FromMinute { get; set; }
		public int ToMinute { get; set; }
		public int? DelayMinutes { get; set; }
		public string? Room { get; set; }
		public string? Equipment { get; set; }
		public string? Note { get; set; }
		/// Open (plans not applied yet) → Applied (moves made, messages queued) → Closed (everyone reached or the desk closed it).
		public string Status { get; set; } = HospitalDisruptionStatus.Open;
		public DateTime CreatedAt { get; set; }
		public int? CreatedByEmployeeId { get; set; }
		public DateTime? AppliedAt { get; set; }
		public int? AppliedByEmployeeId { get; set; }
		public DateTime? ClosedAt { get; set; }
		public int? ClosedByEmployeeId { get; set; }
	}

	public class HospitalDisruptionPatient
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public int DisruptionId { get; set; }
		public int AppointmentId { get; set; }
		public int PatientId { get; set; }
		public string Plan { get; set; } = HospitalDisruptionPlan.Keep;
		/// The proposed (and, once applied, the booked) slot for a move.
		public int? NewPractitionerId { get; set; }
		public DateTime? NewDay { get; set; }
		public int? NewMinute { get; set; }
		public int? NewAppointmentId { get; set; }
		public DateTime? AppliedAt { get; set; }
		/// Why the plan could not be applied (the slot was taken meanwhile…): the row stays to be handled.
		public string? ApplyError { get; set; }
		public int? SmsMessageId { get; set; }
		/// null = not known yet; true once the patient is reached (by a call); false after a call that did not reach them.
		public bool? Reached { get; set; }
		public int Calls { get; set; }
		public DateTime? LastCallAt { get; set; }
		public int? LastCallByEmployeeId { get; set; }
		public string? CallNote { get; set; }
	}

	public static class HospitalClinicDisruptionModel
	{
		public static void Configure(ModelBuilder b)
		{
			b.Entity<HospitalClinicDisruption>(e =>
			{
				e.ToTable("HospitalClinicDisruptions");   // no DbSet: it names its table
				e.Property(x => x.DisruptionNo).HasMaxLength(20);
				e.Property(x => x.Day).HasColumnType("date");
				e.Property(x => x.Kind).HasMaxLength(10);
				e.Property(x => x.Room).HasMaxLength(60);
				e.Property(x => x.Equipment).HasMaxLength(120);
				e.Property(x => x.Note).HasMaxLength(300);
				e.Property(x => x.Status).HasMaxLength(10);
				e.HasIndex(x => new { x.CompanyId, x.DisruptionNo }).IsUnique();
				e.HasIndex(x => new { x.CompanyId, x.Day, x.PractitionerId });
			});
			b.Entity<HospitalDisruptionPatient>(e =>
			{
				e.ToTable("HospitalDisruptionPatients");
				e.Property(x => x.Plan).HasColumnName("PlanKind").HasMaxLength(10);   // PLAN is a reserved word in T-SQL
				e.Property(x => x.NewDay).HasColumnType("date");
				e.Property(x => x.ApplyError).HasMaxLength(200);
				e.Property(x => x.CallNote).HasMaxLength(300);
				e.HasIndex(x => new { x.CompanyId, x.DisruptionId, x.AppointmentId }).IsUnique();
				e.HasIndex(x => new { x.CompanyId, x.PatientId });
			});
		}
	}
}
