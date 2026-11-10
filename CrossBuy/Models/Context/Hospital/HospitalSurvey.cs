using Microsoft.EntityFrameworkCore;

namespace CrossBuy.Models.Context.Hospital
{
	// ==========================================================================================
	// HOSPITAL H65 — PATIENT SATISFACTION SURVEYS (the prototype's "satisfaction surveys"). The administrator keeps the
	// questions per kind of care (a clinic visit, a stay, the emergency department), each answered 1–5. A survey is taken
	// in the hospital, on a tablet, for ONE visit or ONE stay (never twice): the questions, the "would you recommend us"
	// score (0–10, the NPS), a comment, and whether the patient answers anonymously. A low survey opens a follow-up the
	// administrator closes with what was done. (Patients are written to by email only, and the portal keeps its design:
	// a link by email waits for a designed page.)
	// CrossBuy/deploy/sql/hospital_h65_surveys.sql builds the tables.
	// ==========================================================================================
	public static class HospitalSurveyKind
	{
		public const string Visit = "Visit";
		public const string Stay = "Stay";
		public const string Emergency = "Emergency";
		public static readonly IReadOnlyList<string> All = new[] { Visit, Stay, Emergency };
	}

	public static class HospitalSurveyFollowUp
	{
		public const string None = "None";
		public const string Open = "Open";
		public const string Done = "Done";
	}

	public class HospitalSurveyQuestion
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public string Code { get; set; } = "";
		public string Kind { get; set; } = HospitalSurveyKind.Visit;
		public string Text { get; set; } = "";
		public string TextEn { get; set; } = "";
		public int SortOrder { get; set; }
		public bool IsActive { get; set; } = true;
	}

	public class HospitalSurveyResponse
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public string ResponseNo { get; set; } = "";
		public string Kind { get; set; } = HospitalSurveyKind.Visit;
		public int PatientId { get; set; }
		public int? EncounterId { get; set; }
		public int? AdmissionId { get; set; }
		public int? ErVisitId { get; set; }
		/// "How likely are you to recommend us?" 0–10.
		public int Recommend { get; set; }
		/// The average of the 1–5 answers, kept with the response.
		public decimal Average { get; set; }
		public string? Comment { get; set; }
		public bool Anonymous { get; set; }
		public DateTime At { get; set; }
		public int CollectedByEmployeeId { get; set; }
		public string FollowUp { get; set; } = HospitalSurveyFollowUp.None;
		public DateTime? FollowedUpAt { get; set; }
		public int? FollowedUpByEmployeeId { get; set; }
		public string? FollowUpNote { get; set; }
	}

	public class HospitalSurveyAnswer
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public int ResponseId { get; set; }
		public int QuestionId { get; set; }
		public int Score { get; set; }
	}

	public static class HospitalSurveyModel
	{
		public static void Configure(ModelBuilder b)
		{
			b.Entity<HospitalSurveyQuestion>(e =>
			{
				e.ToTable("HospitalSurveyQuestions");   // no DbSet: each names its table
				e.Property(x => x.Code).HasMaxLength(20);
				e.Property(x => x.Kind).HasMaxLength(10);
				e.Property(x => x.Text).HasMaxLength(300);
				e.Property(x => x.TextEn).HasMaxLength(300);
				e.HasIndex(x => new { x.CompanyId, x.Code }).IsUnique();
			});
			b.Entity<HospitalSurveyResponse>(e =>
			{
				e.ToTable("HospitalSurveyResponses");
				e.Property(x => x.ResponseNo).HasMaxLength(20);
				e.Property(x => x.Kind).HasMaxLength(10);
				e.Property(x => x.Average).HasColumnType("decimal(4,2)");
				e.Property(x => x.Comment).HasMaxLength(1000);
				e.Property(x => x.FollowUp).HasMaxLength(6);
				e.Property(x => x.FollowUpNote).HasMaxLength(500);
				e.HasIndex(x => new { x.CompanyId, x.ResponseNo }).IsUnique();
				e.HasIndex(x => new { x.CompanyId, x.PatientId });
				e.HasIndex(x => new { x.CompanyId, x.At });
			});
			b.Entity<HospitalSurveyAnswer>(e =>
			{
				e.ToTable("HospitalSurveyAnswers");
				e.HasIndex(x => new { x.ResponseId, x.QuestionId }).IsUnique();
			});
		}
	}
}
