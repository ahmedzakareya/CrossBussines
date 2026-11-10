using Microsoft.EntityFrameworkCore;

namespace CrossBuy.Models.Context.Hospital
{
	// ==========================================================================================
	// HOSPITAL H70 — SMS TO PATIENTS (the prototype's "SMS"), built before a provider is chosen (owner 2026-11-09). Every
	// message the hospital would send is written to an outbox — a booking confirmed, the reminder the day before, a missed
	// appointment, lab results ready, or one typed by staff — from an editable template per kind. With no provider the
	// messages wait as Queued; once a provider is connected (one place: CrossBuy.BL.Comm.ISmsSender) the same outbox is
	// sent. A message that could not be addressed is kept as Skipped with why (no phone, the patient asked for no
	// messages), so the screen says who was never reached instead of quietly sending nothing. One message per event:
	// DedupKey is unique per company (an appointment's reminder is written once however often the sweep runs).
	// CrossBuy/deploy/sql/hospital_h70_sms.sql builds the tables.
	// ==========================================================================================
	public static class HospitalSmsKind
	{
		public const string Booked = "Booked";
		public const string Reminder = "Reminder";
		public const string NoShow = "NoShow";
		public const string LabReady = "LabReady";
		public const string Custom = "Custom";
		public static readonly IReadOnlyList<string> All = new[] { Booked, Reminder, NoShow, LabReady, Custom };
		/// The kinds written by an event, each with its template.
		public static readonly IReadOnlyList<string> Templated = new[] { Booked, Reminder, NoShow, LabReady };
	}

	public static class HospitalSmsStatus
	{
		public const string Queued = "Queued";
		public const string Sent = "Sent";
		public const string Failed = "Failed";
		/// Never addressable (no phone, or the patient asked for no messages) — kept so the gap is visible.
		public const string Skipped = "Skipped";
		public const string Cancelled = "Cancelled";
		public static readonly IReadOnlyList<string> All = new[] { Queued, Sent, Failed, Skipped, Cancelled };
	}

	public class HospitalSmsMessage
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public string MessageNo { get; set; } = "";
		public int? PatientId { get; set; }
		public string? Phone { get; set; }
		public string Kind { get; set; } = HospitalSmsKind.Custom;
		/// "ar" or "en": the template language the body was written in.
		public string Language { get; set; } = "ar";
		public string Body { get; set; } = "";
		public string Status { get; set; } = HospitalSmsStatus.Queued;
		/// What the message is about ("Appointment", "LabOrder"), and its id.
		public string? SourceType { get; set; }
		public int? SourceId { get; set; }
		/// One message per event (e.g. "appt:12:reminder"); a typed message carries its own number.
		public string DedupKey { get; set; } = "";
		public DateTime QueuedAt { get; set; }
		public int? QueuedByEmployeeId { get; set; }
		public int Attempts { get; set; }
		public DateTime? SentAt { get; set; }
		public string? ProviderRef { get; set; }
		/// Why it was skipped or failed.
		public string? Error { get; set; }
		public DateTime? CancelledAt { get; set; }
		public int? CancelledByEmployeeId { get; set; }
	}

	/// The words of one kind of message, in Arabic and English, with {patient} {doctor} {date} {time} {test} {hospital}.
	public class HospitalSmsTemplate
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public string Kind { get; set; } = "";
		public string BodyAr { get; set; } = "";
		public string BodyEn { get; set; } = "";
		public bool IsActive { get; set; } = true;
		public DateTime UpdatedAt { get; set; }
		public int? UpdatedByEmployeeId { get; set; }
	}

	/// A patient who asked for no messages. Lifting it deletes the row (the audit is the messages' own Skipped trail).
	public class HospitalSmsOptOut
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public int PatientId { get; set; }
		public string? Reason { get; set; }
		public DateTime At { get; set; }
		public int? ByEmployeeId { get; set; }
	}

	public static class HospitalSmsModel
	{
		public static void Configure(ModelBuilder b)
		{
			b.Entity<HospitalSmsMessage>(e =>
			{
				e.ToTable("HospitalSmsMessages");   // no DbSet: it names its table
				e.Property(x => x.MessageNo).HasMaxLength(20);
				e.Property(x => x.Phone).HasMaxLength(30);
				e.Property(x => x.Kind).HasMaxLength(10);
				e.Property(x => x.Language).HasMaxLength(2);
				e.Property(x => x.Body).HasMaxLength(640);
				e.Property(x => x.Status).HasMaxLength(10);
				e.Property(x => x.SourceType).HasMaxLength(20);
				e.Property(x => x.DedupKey).HasMaxLength(60);
				e.Property(x => x.ProviderRef).HasMaxLength(100);
				e.Property(x => x.Error).HasMaxLength(300);
				e.HasIndex(x => new { x.CompanyId, x.MessageNo }).IsUnique();
				e.HasIndex(x => new { x.CompanyId, x.DedupKey }).IsUnique();
				e.HasIndex(x => new { x.CompanyId, x.Status });
				e.HasIndex(x => new { x.CompanyId, x.PatientId });
			});
			b.Entity<HospitalSmsTemplate>(e =>
			{
				e.ToTable("HospitalSmsTemplates");
				e.Property(x => x.Kind).HasMaxLength(10);
				e.Property(x => x.BodyAr).HasMaxLength(480);
				e.Property(x => x.BodyEn).HasMaxLength(480);
				e.HasIndex(x => new { x.CompanyId, x.Kind }).IsUnique();
			});
			b.Entity<HospitalSmsOptOut>(e =>
			{
				e.ToTable("HospitalSmsOptOuts");
				e.Property(x => x.Reason).HasMaxLength(200);
				e.HasIndex(x => new { x.CompanyId, x.PatientId }).IsUnique();
			});
		}
	}
}
