using Microsoft.EntityFrameworkCore;

namespace CrossBuy.Models.Context.Hospital
{
	public static class HospitalReferralKind
	{
		/// To a department (and optionally a doctor) of this hospital — a consult request.
		public const string Internal = "Internal";
		/// To another facility.
		public const string External = "External";
		public static readonly IReadOnlyList<string> All = new[] { Internal, External };
	}

	public static class HospitalReferralUrgency
	{
		public const string Routine = "Routine";
		public const string Urgent = "Urgent";
		public const string Emergency = "Emergency";
		public static readonly IReadOnlyList<string> All = new[] { Routine, Urgent, Emergency };
	}

	public static class HospitalReferralStatus
	{
		public const string Sent = "Sent";
		public const string Accepted = "Accepted";
		public const string Declined = "Declined";
		public const string Completed = "Completed";
		public const string Cancelled = "Cancelled";
		public static readonly IReadOnlyList<string> All = new[] { Sent, Accepted, Declined, Completed, Cancelled };
		public static bool Open(string s) => s is Sent or Accepted;
	}

	// HOSPITAL H203 — a referral: a doctor asks another department of the hospital (a consult) or another facility to see
	// the patient, with the question and the clinical summary; it is answered (the consultant's reply, or the outside
	// facility's letter), declined with a reason, or cancelled by the one who asked — and it has a time it is due by.
	public class HospitalReferral
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public string ReferralNo { get; set; } = "";
		public int PatientId { get; set; }
		public int? EncounterId { get; set; }
		public string Kind { get; set; } = HospitalReferralKind.Internal;
		public int? FromPractitionerId { get; set; }
		public int? FromDepartmentId { get; set; }
		public int? ToDepartmentId { get; set; }
		public int? ToPractitionerId { get; set; }
		public string? ToFacility { get; set; }
		public string? ToSpecialty { get; set; }
		public string Urgency { get; set; } = HospitalReferralUrgency.Routine;
		public string Reason { get; set; } = "";
		public string? Summary { get; set; }
		public string Status { get; set; } = HospitalReferralStatus.Sent;
		public DateTime DueAt { get; set; }
		public DateTime CreatedAt { get; set; }
		public int? CreatedByEmployeeId { get; set; }
		public DateTime? AcceptedAt { get; set; }
		public int? AcceptedByEmployeeId { get; set; }
		/// The answer: the consultant's reply, or what the outside facility wrote back.
		public string? Reply { get; set; }
		/// Why it was declined or cancelled.
		public string? CloseReason { get; set; }
		public DateTime? ClosedAt { get; set; }
		public int? ClosedByEmployeeId { get; set; }
	}

	public static class HospitalReferralModel
	{
		public static void Configure(ModelBuilder b)
		{
			b.Entity<HospitalReferral>(e =>
			{
				e.ToTable("HospitalReferrals");   // no DbSet: it names its table
				e.Property(x => x.ReferralNo).HasMaxLength(20);
				e.Property(x => x.Kind).HasMaxLength(10);
				e.Property(x => x.ToFacility).HasMaxLength(200);
				e.Property(x => x.ToSpecialty).HasMaxLength(100);
				e.Property(x => x.Urgency).HasMaxLength(10);
				e.Property(x => x.Reason).HasMaxLength(1000);
				e.Property(x => x.Summary).HasMaxLength(2000);
				e.Property(x => x.Status).HasMaxLength(12);
				e.Property(x => x.Reply).HasMaxLength(2000);
				e.Property(x => x.CloseReason).HasMaxLength(500);
				e.HasIndex(x => new { x.CompanyId, x.ReferralNo }).IsUnique();
				e.HasIndex(x => new { x.CompanyId, x.Status, x.DueAt });
				e.HasIndex(x => new { x.CompanyId, x.PatientId });
			});
		}
	}
}
