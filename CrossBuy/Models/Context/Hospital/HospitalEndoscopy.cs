using Microsoft.EntityFrameworkCore;

namespace CrossBuy.Models.Context.Hospital
{
	public static class HospitalEndoKind
	{
		public const string Gastroscopy = "Gastroscopy";
		public const string Colonoscopy = "Colonoscopy";
		public const string Bronchoscopy = "Bronchoscopy";
		public const string Ercp = "ERCP";
		public static readonly IReadOnlyList<string> All = new[] { Gastroscopy, Colonoscopy, Bronchoscopy, Ercp };
	}

	public static class HospitalEndoSedation
	{
		public const string None = "None";
		public const string Moderate = "Moderate";
		public const string Deep = "Deep";
		public static readonly IReadOnlyList<string> All = new[] { None, Moderate, Deep };
	}

	public static class HospitalEndoStatus
	{
		public const string Started = "Started";
		public const string Completed = "Completed";
		public static readonly IReadOnlyList<string> All = new[] { Started, Completed };
	}

	// HOSPITAL H217 — a flexible endoscope, tracked by serial number.
	public class HospitalScope
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public string Serial { get; set; } = "";
		public string Kind { get; set; } = HospitalEndoKind.Gastroscopy;
		public string? Model { get; set; }
		public bool IsActive { get; set; } = true;
		public DateTime CreatedAt { get; set; }
	}

	// HOSPITAL H217 — one reprocessing of a scope after use: leak test, manual clean, high-level disinfection (agent and
	// minutes), and the result. Only a passed cycle after the scope's last use clears it for the next patient.
	public class HospitalScopeCycle
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public int ScopeId { get; set; }
		public bool LeakTestPassed { get; set; }
		public bool ManualClean { get; set; }
		public string Agent { get; set; } = "";
		public int Minutes { get; set; }
		public bool Passed { get; set; }
		public string? Note { get; set; }
		public DateTime At { get; set; }
		public int? ByEmployeeId { get; set; }
	}

	// HOSPITAL H217 — an endoscopic procedure: the scope and the cycle that cleared it, the indication and sedation, and
	// on completion the findings, interventions, complications, recommendations, the colonoscopy quality figures and the
	// biopsies (which open a pathology case).
	public class HospitalEndoscopy
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public string ProcNo { get; set; } = "";
		public int PatientId { get; set; }
		public string Kind { get; set; } = HospitalEndoKind.Gastroscopy;
		public int ScopeId { get; set; }
		public int CycleId { get; set; }
		public string Indication { get; set; } = "";
		public string Sedation { get; set; } = HospitalEndoSedation.None;
		public string Status { get; set; } = HospitalEndoStatus.Started;
		public DateTime StartedAt { get; set; }
		public int? EndoscopistEmployeeId { get; set; }
		public string? Findings { get; set; }
		public string? Interventions { get; set; }
		public string? Complications { get; set; }
		public string? Recommendations { get; set; }
		public bool? CecumReached { get; set; }
		public int? WithdrawalMinutes { get; set; }
		public int? BowelPrepScore { get; set; }
		public int Biopsies { get; set; }
		public int? PathCaseId { get; set; }
		public DateTime? CompletedAt { get; set; }
	}

	public static class HospitalEndoscopyModel
	{
		public static void Configure(ModelBuilder b)
		{
			b.Entity<HospitalScope>(e =>
			{
				e.ToTable("HospitalScopes");
				e.Property(x => x.Serial).HasMaxLength(40);
				e.Property(x => x.Kind).HasMaxLength(15);
				e.Property(x => x.Model).HasMaxLength(100);
				e.HasIndex(x => new { x.CompanyId, x.Serial }).IsUnique();
			});
			b.Entity<HospitalScopeCycle>(e =>
			{
				e.ToTable("HospitalScopeCycles");
				e.Property(x => x.Agent).HasMaxLength(60);
				e.Property(x => x.Note).HasMaxLength(300);
				e.HasIndex(x => new { x.CompanyId, x.ScopeId, x.At });
			});
			b.Entity<HospitalEndoscopy>(e =>
			{
				e.ToTable("HospitalEndoscopies");
				e.Property(x => x.ProcNo).HasMaxLength(20);
				e.Property(x => x.Kind).HasMaxLength(15);
				e.Property(x => x.Indication).HasMaxLength(500);
				e.Property(x => x.Sedation).HasMaxLength(10);
				e.Property(x => x.Status).HasMaxLength(10).IsConcurrencyToken();
				e.Property(x => x.Findings).HasMaxLength(4000);
				e.Property(x => x.Interventions).HasMaxLength(1000);
				e.Property(x => x.Complications).HasMaxLength(500);
				e.Property(x => x.Recommendations).HasMaxLength(1000);
				e.HasIndex(x => new { x.CompanyId, x.ProcNo }).IsUnique();
				e.HasIndex(x => new { x.CompanyId, x.ScopeId, x.StartedAt });
				e.HasIndex(x => new { x.CompanyId, x.PatientId });
			});
		}
	}
}
