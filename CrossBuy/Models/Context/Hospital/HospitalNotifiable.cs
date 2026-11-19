using Microsoft.EntityFrameworkCore;

namespace CrossBuy.Models.Context.Hospital
{
	/// How fast the Ministry must hear of a case: group A by the same day (24 hours), group B within the week.
	public static class HospitalNotifiableUrgency
	{
		public const string Immediate = "Immediate";
		public const string Weekly = "Weekly";
		public static readonly IReadOnlyList<string> All = new[] { Immediate, Weekly };
	}

	public static class HospitalDiseaseReportStatus
	{
		/// Found on a signed visit; the Ministry has not been told yet.
		public const string Due = "Due";
		/// Reported to the Ministry, with its reference.
		public const string Reported = "Reported";
		/// Not a case after all (the diagnosis was revised, or it was already reported elsewhere), with the reason.
		public const string Dismissed = "Dismissed";
		public static readonly IReadOnlyList<string> All = new[] { Due, Reported, Dismissed };
	}

	// HOSPITAL H209 — a disease the Ministry of Health must be told of, matched by its ICD-10 code prefix ("A00" covers
	// A00.0, A00.1 …). The list is the hospital's own (seeded with the Ministry's, editable by an administrator).
	public class HospitalNotifiableDisease
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public string CodePrefix { get; set; } = "";
		public string Name { get; set; } = "";
		public string? NameEn { get; set; }
		public string Urgency { get; set; } = HospitalNotifiableUrgency.Immediate;
		public bool IsActive { get; set; } = true;
		public DateTime CreatedAt { get; set; }
	}

	// HOSPITAL H209 — one case to report: a notifiable diagnosis on a signed visit, the time it is due by, and how it
	// closed (the Ministry's reference, or why it was not a case). One per visit and disease.
	public class HospitalDiseaseReport
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public string ReportNo { get; set; } = "";
		public int PatientId { get; set; }
		public int EncounterId { get; set; }
		public int DiseaseId { get; set; }
		/// The diagnosis as the doctor recorded it (a later catalogue change never rewrites the case).
		public string Code { get; set; } = "";
		public string DiagnosisName { get; set; } = "";
		public string? DiagnosisNameEn { get; set; }
		public int? PractitionerId { get; set; }
		public DateTime DiagnosedAt { get; set; }
		public DateTime DueAt { get; set; }
		public string Status { get; set; } = HospitalDiseaseReportStatus.Due;
		public string? MohRef { get; set; }
		public string? DismissReason { get; set; }
		public DateTime? ClosedAt { get; set; }
		public int? ClosedByEmployeeId { get; set; }
		public DateTime CreatedAt { get; set; }
	}

	public static class HospitalNotifiableModel
	{
		public static void Configure(ModelBuilder b)
		{
			b.Entity<HospitalNotifiableDisease>(e =>
			{
				e.ToTable("HospitalNotifiableDiseases");
				e.Property(x => x.CodePrefix).HasMaxLength(10);
				e.Property(x => x.Name).HasMaxLength(200);
				e.Property(x => x.NameEn).HasMaxLength(200);
				e.Property(x => x.Urgency).HasMaxLength(10);
				e.HasIndex(x => new { x.CompanyId, x.CodePrefix }).IsUnique();
			});
			b.Entity<HospitalDiseaseReport>(e =>
			{
				e.ToTable("HospitalDiseaseReports");
				e.Property(x => x.ReportNo).HasMaxLength(20);
				e.Property(x => x.Code).HasMaxLength(20);
				e.Property(x => x.DiagnosisName).HasMaxLength(300);
				e.Property(x => x.DiagnosisNameEn).HasMaxLength(300);
				e.Property(x => x.Status).HasMaxLength(10);
				e.Property(x => x.MohRef).HasMaxLength(60);
				e.Property(x => x.DismissReason).HasMaxLength(500);
				e.HasIndex(x => new { x.CompanyId, x.ReportNo }).IsUnique();
				e.HasIndex(x => new { x.CompanyId, x.EncounterId, x.DiseaseId }).IsUnique();
				e.HasIndex(x => new { x.CompanyId, x.Status, x.DueAt });
				e.HasIndex(x => new { x.CompanyId, x.PatientId });
			});
		}
	}
}
