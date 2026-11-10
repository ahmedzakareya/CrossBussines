using Microsoft.EntityFrameworkCore;

namespace CrossBuy.Models.Context.Hospital
{
	// ==========================================================================================
	// HOSPITAL H77 — INPATIENT CONSULTATION REQUESTS (phase 2 of the 2026-11-09 gap research; every large HIS has the
	// "consult order": the attending asks another specialty for an opinion and the answer is written back on the chart).
	// The attending writes the question, the specialty (and, if they want a particular doctor, that doctor) and how urgent
	// it is; the urgency sets when an answer is due (emergency 1 hour, urgent 4, routine 24). A doctor of that specialty
	// accepts it and answers: findings, recommendations, whether they will follow the patient. Overdue is computed, never
	// stored. CrossBuy/deploy/sql/hospital_h77_consult_requests.sql builds the table.
	// ==========================================================================================
	public static class HospitalConsultUrgency
	{
		public const string Emergency = "Emergency";
		public const string Urgent = "Urgent";
		public const string Routine = "Routine";
		public static readonly IReadOnlyList<string> All = new[] { Emergency, Urgent, Routine };

		/// How long the specialty has to answer.
		public static TimeSpan Window(string urgency) => urgency switch
		{
			Emergency => TimeSpan.FromHours(1),
			Urgent => TimeSpan.FromHours(4),
			_ => TimeSpan.FromHours(24),
		};
	}

	public static class HospitalConsultRequestStatus
	{
		public const string Requested = "Requested";
		public const string Accepted = "Accepted";
		public const string Answered = "Answered";
		public const string Cancelled = "Cancelled";
		public static readonly IReadOnlyList<string> All = new[] { Requested, Accepted, Answered, Cancelled };
		public static bool Open(string s) => s is Requested or Accepted;
	}

	public class HospitalConsultRequest
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public string RequestNo { get; set; } = "";
		public int AdmissionId { get; set; }
		public int PatientId { get; set; }
		/// The specialty asked.
		public int DepartmentId { get; set; }
		/// A particular doctor of it, when the attending wants one.
		public int? PractitionerId { get; set; }
		public string Urgency { get; set; } = HospitalConsultUrgency.Routine;
		public string Question { get; set; } = "";
		public string Status { get; set; } = HospitalConsultRequestStatus.Requested;
		public DateTime RequestedAt { get; set; }
		public int? RequestedByEmployeeId { get; set; }
		public DateTime DueAt { get; set; }
		public DateTime? AcceptedAt { get; set; }
		public int? AcceptedByEmployeeId { get; set; }
		public DateTime? AnsweredAt { get; set; }
		public int? AnsweredByEmployeeId { get; set; }
		public string? Findings { get; set; }
		public string? Recommendations { get; set; }
		/// The consultant will keep seeing the patient.
		public bool FollowUp { get; set; }
		public DateTime? CancelledAt { get; set; }
		public int? CancelledByEmployeeId { get; set; }
		public string? CancelReason { get; set; }
	}

	public static class HospitalConsultRequestModel
	{
		public static void Configure(ModelBuilder b)
		{
			b.Entity<HospitalConsultRequest>(e =>
			{
				e.ToTable("HospitalConsultRequests");   // no DbSet: it names its table
				e.Property(x => x.RequestNo).HasMaxLength(20);
				e.Property(x => x.Urgency).HasMaxLength(10);
				e.Property(x => x.Question).HasMaxLength(1000);
				e.Property(x => x.Status).HasMaxLength(10);
				e.Property(x => x.Findings).HasMaxLength(2000);
				e.Property(x => x.Recommendations).HasMaxLength(2000);
				e.Property(x => x.CancelReason).HasMaxLength(300);
				e.HasIndex(x => new { x.CompanyId, x.RequestNo }).IsUnique();
				e.HasIndex(x => new { x.CompanyId, x.AdmissionId });
				e.HasIndex(x => new { x.CompanyId, x.DepartmentId, x.Status });
			});
		}
	}
}
