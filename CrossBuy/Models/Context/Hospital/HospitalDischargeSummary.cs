using Microsoft.EntityFrameworkCore;

namespace CrossBuy.Models.Context.Hospital
{
	// ==========================================================================================
	// HOSPITAL H55 — the DISCHARGE SUMMARY of a stay (the prototype's "medical records: discharge summary, medical
	// reports"). One per admission. The stay's facts are READ, never stored twice: the patient, the dates, the ward and
	// bed, the reason for admission, the operations done during the stay. The doctor writes what only a doctor can:
	// the final diagnosis, the course in hospital, the condition at discharge, the medicines to take home and the
	// follow-up. A signed summary is final. It prints only through Report Studio (Hospital.DischargeSummary).
	// CrossBuy/deploy/sql/hospital_h55_discharge_summary.sql builds the table.
	// ==========================================================================================
	public static class HospitalDischargeCondition
	{
		public const string Improved = "Improved";
		public const string Stable = "Stable";
		public const string Unchanged = "Unchanged";
		public const string Referred = "Referred";
		public const string AgainstAdvice = "AgainstAdvice";
		public const string Deceased = "Deceased";
		public static readonly IReadOnlyList<string> All = new[] { Improved, Stable, Unchanged, Referred, AgainstAdvice, Deceased };
	}

	public static class HospitalDischargeSummaryStatus
	{
		public const string Draft = "Draft";
		public const string Signed = "Signed";
	}

	public class HospitalDischargeSummary
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public int AdmissionId { get; set; }
		public int PatientId { get; set; }
		public string? FinalDiagnosis { get; set; }
		public string? Course { get; set; }
		public string? Procedures { get; set; }
		public string? Condition { get; set; }
		public string? Medications { get; set; }
		public string? Instructions { get; set; }
		public DateTime? FollowUpOn { get; set; }
		public string? FollowUpWith { get; set; }
		public string Status { get; set; } = HospitalDischargeSummaryStatus.Draft;
		public DateTime CreatedAt { get; set; }
		public int? CreatedByEmployeeId { get; set; }
		public DateTime? UpdatedAt { get; set; }
		public int? UpdatedByEmployeeId { get; set; }
		public DateTime? SignedAt { get; set; }
		public int? SignedByEmployeeId { get; set; }
	}

	public static class HospitalDischargeSummaryModel
	{
		public static void Configure(ModelBuilder b)
		{
			b.Entity<HospitalDischargeSummary>(e =>
			{
				e.ToTable("HospitalDischargeSummaries");   // no DbSet: it names its table
				e.Property(x => x.FinalDiagnosis).HasMaxLength(1000);
				e.Property(x => x.Course).HasMaxLength(4000);
				e.Property(x => x.Procedures).HasMaxLength(2000);
				e.Property(x => x.Condition).HasMaxLength(20);
				e.Property(x => x.Medications).HasMaxLength(2000);
				e.Property(x => x.Instructions).HasMaxLength(2000);
				e.Property(x => x.FollowUpOn).HasColumnType("date");
				e.Property(x => x.FollowUpWith).HasMaxLength(200);
				e.Property(x => x.Status).HasMaxLength(10);
				e.HasIndex(x => x.AdmissionId).IsUnique();
				e.HasIndex(x => new { x.CompanyId, x.PatientId });
			});
		}
	}
}
