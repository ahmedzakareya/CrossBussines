using Microsoft.EntityFrameworkCore;

namespace CrossBuy.Models.Context.Hospital
{
	public static class HospitalTeleStatus
	{
		public const string Scheduled = "Scheduled";
		public const string Live = "Live";
		public const string Ended = "Ended";
		public static readonly IReadOnlyList<string> All = new[] { Scheduled, Live, Ended };
	}

	/// The side a signal comes from: the doctor ("D") or the patient ("P").
	public static class HospitalTeleSide
	{
		public const string Doctor = "D";
		public const string Patient = "P";
	}

	public static class HospitalTeleSignalKind
	{
		public const string Hello = "hello";
		public const string Offer = "offer";
		public const string Answer = "answer";
		public const string Ice = "ice";
		public const string Bye = "bye";
		public static readonly IReadOnlyList<string> All = new[] { Hello, Offer, Answer, Ice, Bye };
	}

	// HOSPITAL H214 — a video visit: a booked appointment held over video between the doctor (in the hospital) and the
	// patient (in the portal). Who joined when, and when it ended; the call itself is peer to peer and never recorded.
	public class HospitalTeleVisit
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public int AppointmentId { get; set; }
		public int PatientId { get; set; }
		public int PractitionerId { get; set; }
		public string Status { get; set; } = HospitalTeleStatus.Scheduled;
		public DateTime CreatedAt { get; set; }
		public int? CreatedByEmployeeId { get; set; }
		public DateTime? DoctorJoinedAt { get; set; }
		public DateTime? PatientJoinedAt { get; set; }
		public DateTime? EndedAt { get; set; }
		public int? EndedByEmployeeId { get; set; }
	}

	/// The call's set-up messages (WebRTC offer / answer / network candidates), passed between the two sides.
	public class HospitalTeleSignal
	{
		public long ID { get; set; }
		public int CompanyId { get; set; }
		public int VisitId { get; set; }
		public string FromSide { get; set; } = HospitalTeleSide.Doctor;
		public string Kind { get; set; } = HospitalTeleSignalKind.Hello;
		public string? Payload { get; set; }
		public DateTime At { get; set; }
	}

	public static class HospitalTeleModel
	{
		public static void Configure(ModelBuilder b)
		{
			b.Entity<HospitalTeleVisit>(e =>
			{
				e.ToTable("HospitalTeleVisits");
				e.Property(x => x.Status).HasMaxLength(10);
				e.HasIndex(x => new { x.CompanyId, x.AppointmentId }).IsUnique();
				e.HasIndex(x => new { x.CompanyId, x.PatientId });
			});
			b.Entity<HospitalTeleSignal>(e =>
			{
				e.ToTable("HospitalTeleSignals");
				e.Property(x => x.FromSide).HasMaxLength(1);
				e.Property(x => x.Kind).HasMaxLength(10);
				e.Property(x => x.Payload).HasMaxLength(20000);
				e.HasIndex(x => new { x.CompanyId, x.VisitId, x.ID });
			});
		}
	}
}
