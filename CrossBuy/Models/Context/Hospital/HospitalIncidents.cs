using Microsoft.EntityFrameworkCore;

namespace CrossBuy.Models.Context.Hospital
{
	// ==========================================================================================
	// HOSPITAL — incident reports & patient safety (unit H45), as the approved prototype draws it (#/incidents): the
	// month's figures, "reporting is for learning, not blame", the reports and a one-minute form. Any staff member
	// reports — anonymously if they wish, and then the report keeps NO trace of who sent it. A moderate or severe harm
	// opens a root-cause analysis at once, due in 45 days; quality management reviews, analyses and closes (an analysis
	// closes only with its root cause and its actions). CrossBuy/deploy/sql/hospital_h45_incidents.sql.
	// ==========================================================================================

	public static class HospitalIncidentKind
	{
		public const string MedicationError = "MedicationError";
		public const string Fall = "Fall";
		public const string WrongPatient = "WrongPatient";
		public const string EquipmentFailure = "EquipmentFailure";
		public const string Infection = "Infection";
		public const string Other = "Other";
		public static readonly IReadOnlyList<string> All = new[] { MedicationError, Fall, WrongPatient, EquipmentFailure, Infection, Other };
	}

	public static class HospitalIncidentHarm
	{
		public const string NearMiss = "NearMiss";
		public const string NoHarm = "NoHarm";
		public const string Mild = "Mild";
		public const string Moderate = "Moderate";
		public const string Severe = "Severe";
		public static readonly IReadOnlyList<string> All = new[] { NearMiss, NoHarm, Mild, Moderate, Severe };
	}

	public static class HospitalIncidentStatus
	{
		public const string New = "New";
		public const string Review = "Review";
		public const string Rca = "Rca";
		public const string Closed = "Closed";
		public static readonly IReadOnlyList<string> All = new[] { New, Review, Rca, Closed };
	}

	/// Where it happened, when it is not a ward.
	public static class HospitalIncidentPlace
	{
		public const string Ward = "Ward";
		public static readonly IReadOnlyList<string> All = new[] { Ward, "Emergency", "Theatre", "Laboratory", "Imaging", "Pharmacy", "Clinics", "Other" };
	}

	public class HospitalIncident
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public string IncidentNo { get; set; } = "";
		public string Kind { get; set; } = HospitalIncidentKind.Other;
		public string Harm { get; set; } = HospitalIncidentHarm.NoHarm;
		public string Status { get; set; } = HospitalIncidentStatus.New;
		public string Place { get; set; } = HospitalIncidentPlace.Ward;
		public int? WardId { get; set; }
		public int? PatientId { get; set; }
		public string Description { get; set; } = "";
		public DateTime ReportedAt { get; set; }
		/// Null when the report was sent anonymously — nothing else records the sender either.
		public int? ReporterEmployeeId { get; set; }
		public bool Anonymous { get; set; }
		public DateTime? RcaDueOn { get; set; }
		public string? RootCause { get; set; }
		public string? Actions { get; set; }
		public string? Outcome { get; set; }
		public DateTime? ReviewedAt { get; set; }
		public int? ReviewedByEmployeeId { get; set; }
		public DateTime? ClosedAt { get; set; }
		public int? ClosedByEmployeeId { get; set; }
	}

	/// H54: a task of the task module opened from an incident's root-cause analysis (the task itself is the module's).
	public class HospitalIncidentTask
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public int IncidentId { get; set; }
		public int TaskId { get; set; }
		public DateTime CreatedAt { get; set; }
		public int? CreatedByEmployeeId { get; set; }
	}

	public static class HospitalIncidentsModel
	{
		public static void Configure(ModelBuilder b)
		{
			b.Entity<HospitalIncident>(e =>
			{
				e.ToTable("HospitalIncidents");   // no DbSet: it names its table
				e.Property(x => x.IncidentNo).HasMaxLength(20);
				e.Property(x => x.Kind).HasMaxLength(20);
				e.Property(x => x.Harm).HasMaxLength(10);
				e.Property(x => x.Status).HasMaxLength(10);
				e.Property(x => x.Place).HasMaxLength(20);
				e.Property(x => x.Description).HasMaxLength(2000);
				e.Property(x => x.RcaDueOn).HasColumnType("date");
				e.Property(x => x.RootCause).HasMaxLength(2000);
				e.Property(x => x.Actions).HasMaxLength(2000);
				e.Property(x => x.Outcome).HasMaxLength(1000);
				e.HasIndex(x => new { x.CompanyId, x.IncidentNo }).IsUnique();
			});
			b.Entity<HospitalIncidentTask>(e =>
			{
				e.ToTable("HospitalIncidentTasks");   // H54 (hospital_h54_incident_followups.sql)
				e.HasIndex(x => x.TaskId).IsUnique();
				e.HasIndex(x => x.IncidentId);
			});
		}
	}
}
