using Microsoft.EntityFrameworkCore;

namespace CrossBuy.Models.Context.Hospital
{
	// ==========================================================================================
	// HOSPITAL H91 — RECEPTION SELF-INTAKE. The arriving patient fills in their own details on a reception tablet; the
	// desk then links the intake to the file the patient already has, or opens a new one. (Research: Epic Welcome,
	// Phreesia, Clearwave, NHS kiosks, SEHA Abu Dhabi — see memory hospital-self-intake-research.)
	//   * The kiosk only COLLECTS: it never shows an existing record or says "we found you". It answers with a ticket
	//     number and the steps that follow.
	//   * The danger signs come FIRST. Any "yes" ends the self-service there ("go to the desk now") and raises an alert
	//     the desk must acknowledge. The kiosk does not triage — a nurse does.
	//   * An intake is PROVISIONAL until the desk reconciles it, against the identity document, with two identifiers:
	//     linked to an existing file, a new file opened, or rejected. An intake nobody reconciles expires at day's end.
	// CrossBuy/deploy/sql/hospital_h91_self_intake.sql builds the table.
	// ==========================================================================================
	public static class HospitalIntakeStatus
	{
		public const string Submitted = "Submitted";
		public const string Linked = "Linked";
		public const string NewFile = "NewFile";
		public const string Rejected = "Rejected";
		public const string Expired = "Expired";
		public static readonly IReadOnlyList<string> All = new[] { Submitted, Linked, NewFile, Rejected, Expired };
	}

	public static class HospitalIntakeRedFlag
	{
		public static readonly IReadOnlyList<string> All = new[] { "ChestPain", "Breathing", "Stroke", "Bleeding", "Fainting", "Allergic", "SelfHarm", "PregnancyBleeding" };
	}

	public static class HospitalIntakeInfection
	{
		public static readonly IReadOnlyList<string> All = new[] { "Fever", "Cough", "Rash", "Diarrhoea", "Travel" };
	}

	public static class HospitalIntakeVisit
	{
		public const string Appointment = "Appointment";
		public const string WalkIn = "WalkIn";
		public const string Services = "Services";
		public static readonly IReadOnlyList<string> All = new[] { Appointment, WalkIn, Services };
	}

	public static class HospitalIntakeIdType
	{
		public static readonly IReadOnlyList<string> All = new[] { "NationalId", "Residency", "Passport", "None" };
	}

	public class HospitalSelfIntake
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public string IntakeNo { get; set; } = "";
		/// The short number the patient is called by, per day ("R-007").
		public string TicketNo { get; set; } = "";
		public DateTime TicketDate { get; set; }
		public string Status { get; set; } = HospitalIntakeStatus.Submitted;
		public bool Urgent { get; set; }
		/// Comma-separated HospitalIntakeRedFlag codes answered "yes".
		public string? RedFlags { get; set; }
		public DateTime? AlertAckAt { get; set; }
		public int? AlertAckByEmployeeId { get; set; }
		public string Language { get; set; } = "ar";
		public string VisitType { get; set; } = HospitalIntakeVisit.WalkIn;
		public string? IdType { get; set; }
		public string? IdNumber { get; set; }
		public string? Name { get; set; }
		public string? NameEn { get; set; }
		public DateTime? BirthDate { get; set; }
		public string? Sex { get; set; }
		public string? Phone { get; set; }
		public string? Reason { get; set; }
		public string? Allergies { get; set; }
		public string? Medications { get; set; }
		public bool? Pregnant { get; set; }
		/// Comma-separated HospitalIntakeInfection codes.
		public string? Infection { get; set; }
		public string? EmergencyName { get; set; }
		public string? EmergencyPhone { get; set; }
		public string? ConsentVersion { get; set; }
		public DateTime? ConsentAt { get; set; }
		public DateTime SubmittedAt { get; set; }
		public DateTime ExpiresAt { get; set; }
		public DateTime? ReconciledAt { get; set; }
		public int? ReconciledByEmployeeId { get; set; }
		public int? PatientId { get; set; }
		public decimal? MatchScore { get; set; }
		public string? MatchMethod { get; set; }
		public string? DecisionNote { get; set; }
	}

	public static class HospitalSelfIntakeModel
	{
		public static void Configure(ModelBuilder b)
		{
			b.Entity<HospitalSelfIntake>(e =>
			{
				e.ToTable("HospitalSelfIntakes");   // no DbSet: it names its table
				e.Property(x => x.IntakeNo).HasMaxLength(20);
				e.Property(x => x.TicketNo).HasMaxLength(10);
				e.Property(x => x.TicketDate).HasColumnType("date");
				e.Property(x => x.Status).HasMaxLength(10);
				e.Property(x => x.RedFlags).HasMaxLength(150);
				e.Property(x => x.Language).HasMaxLength(5);
				e.Property(x => x.VisitType).HasMaxLength(12);
				e.Property(x => x.IdType).HasMaxLength(12);
				e.Property(x => x.IdNumber).HasMaxLength(30);
				e.Property(x => x.Name).HasMaxLength(200);
				e.Property(x => x.NameEn).HasMaxLength(200);
				e.Property(x => x.BirthDate).HasColumnType("date");
				e.Property(x => x.Sex).HasMaxLength(1);
				e.Property(x => x.Phone).HasMaxLength(20);
				e.Property(x => x.Reason).HasMaxLength(300);
				e.Property(x => x.Allergies).HasMaxLength(300);
				e.Property(x => x.Medications).HasMaxLength(300);
				e.Property(x => x.Infection).HasMaxLength(60);
				e.Property(x => x.EmergencyName).HasMaxLength(100);
				e.Property(x => x.EmergencyPhone).HasMaxLength(20);
				e.Property(x => x.ConsentVersion).HasMaxLength(10);
				e.Property(x => x.MatchScore).HasPrecision(5, 1);
				e.Property(x => x.MatchMethod).HasMaxLength(13);
				e.Property(x => x.DecisionNote).HasMaxLength(300);
				e.HasIndex(x => new { x.CompanyId, x.IntakeNo }).IsUnique();
				e.HasIndex(x => new { x.CompanyId, x.TicketDate, x.TicketNo }).IsUnique();
				e.HasIndex(x => new { x.CompanyId, x.Status });
			});
		}
	}
}
