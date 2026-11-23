using Microsoft.EntityFrameworkCore;

namespace CrossBuy.Models.Context.Hospital
{
	// ==========================================================================================
	// HOSPITAL H250 — PATIENT QUESTIONNAIRES. The clinic sends a validated instrument (PHQ-9, GAD-7, AUDIT-C) to a
	// patient; the patient (or the family account that holds them) answers on the portal; the score and severity are
	// computed, and a PHQ-9 answer of thoughts of self-harm is flagged for the clinic at once.
	// deploy/sql/hospital_h250_questionnaires.sql builds the table.
	// ==========================================================================================
	public static class HospitalQuestionnaireStatus
	{
		public const string Pending = "Pending", Completed = "Completed", Cancelled = "Cancelled";
	}

	public class HospitalQuestionnaire
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public int PatientId { get; set; }
		public string Instrument { get; set; } = "";
		public string Status { get; set; } = HospitalQuestionnaireStatus.Pending;
		public DateTime SentAt { get; set; }
		public int? SentByEmployeeId { get; set; }
		public DateTime? DueDate { get; set; }
		/// The answers, item by item (0..3), comma-separated.
		public string? Answers { get; set; }
		public int? Score { get; set; }
		public string? Severity { get; set; }
		public bool Urgent { get; set; }
		public DateTime? CompletedAt { get; set; }
		public int? CompletedByAccountId { get; set; }
		public DateTime? ReviewedAt { get; set; }
		public int? ReviewedByEmployeeId { get; set; }
	}

	public static class HospitalQuestionnaireModel
	{
		public static void Configure(ModelBuilder b)
		{
			b.Entity<HospitalQuestionnaire>(e =>
			{
				e.ToTable("HospitalQuestionnaires");
				e.Property(x => x.Instrument).HasMaxLength(10); e.Property(x => x.Status).HasMaxLength(10); e.Property(x => x.Answers).HasMaxLength(100); e.Property(x => x.Severity).HasMaxLength(20);
				e.Property(x => x.DueDate).HasColumnType("date");
				e.HasIndex(x => new { x.CompanyId, x.PatientId, x.Status });
				e.HasIndex(x => new { x.CompanyId, x.Status, x.Urgent });
			});
		}
	}
}
