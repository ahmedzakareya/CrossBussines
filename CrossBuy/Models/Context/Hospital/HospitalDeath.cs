using Microsoft.EntityFrameworkCore;

namespace CrossBuy.Models.Context.Hospital
{
	// ==========================================================================================
	// HOSPITAL H59 — DEATHS: the medical certificate of cause of death in the WHO international form. Part I is the chain
	// from the immediate cause (a) through what it was due to (b, c) down to the underlying cause (d); Part II the other
	// significant conditions. Certified by a doctor, numbered (DT-000001), final (a wrong one is voided with a reason).
	// Certifying marks the patient's file deceased (HospitalPatient.DeceasedAt). Prints only through Report Studio
	// (Hospital.DeathCertificate). CrossBuy/deploy/sql/hospital_h59_deaths.sql builds the table.
	// ==========================================================================================
	public static class HospitalDeathPlace
	{
		public const string Ward = "Ward", Icu = "Icu", Er = "Er", Theatre = "Theatre", OnArrival = "OnArrival", Other = "Other";
		public static readonly IReadOnlyList<string> All = new[] { Ward, Icu, Er, Theatre, OnArrival, Other };
	}

	public static class HospitalDeathManner
	{
		public const string Natural = "Natural", Accident = "Accident", Suicide = "Suicide", Homicide = "Homicide", Pending = "Pending", Undetermined = "Undetermined";
		public static readonly IReadOnlyList<string> All = new[] { Natural, Accident, Suicide, Homicide, Pending, Undetermined };
	}

	public class HospitalDeath
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public string DeathNo { get; set; } = "";
		public int PatientId { get; set; }
		public int? AdmissionId { get; set; }
		public DateTime DiedAt { get; set; }
		public string Place { get; set; } = HospitalDeathPlace.Ward;
		public string ImmediateCause { get; set; } = "";
		public string? DueTo1 { get; set; }
		public string? DueTo2 { get; set; }
		public string? UnderlyingCause { get; set; }
		public string? OtherConditions { get; set; }
		public string Manner { get; set; } = HospitalDeathManner.Natural;
		public bool AutopsyRequested { get; set; }
		public int? PractitionerId { get; set; }
		public DateTime CertifiedAt { get; set; }
		public int CertifiedByEmployeeId { get; set; }
		public DateTime? VoidedAt { get; set; }
		public int? VoidedByEmployeeId { get; set; }
		public string? VoidReason { get; set; }
	}

	public static class HospitalDeathModel
	{
		public static void Configure(ModelBuilder b)
		{
			b.Entity<HospitalDeath>(e =>
			{
				e.ToTable("HospitalDeaths");   // no DbSet: it names its table
				e.Property(x => x.DeathNo).HasMaxLength(20);
				e.Property(x => x.Place).HasMaxLength(12);
				e.Property(x => x.ImmediateCause).HasMaxLength(300);
				e.Property(x => x.DueTo1).HasMaxLength(300);
				e.Property(x => x.DueTo2).HasMaxLength(300);
				e.Property(x => x.UnderlyingCause).HasMaxLength(300);
				e.Property(x => x.OtherConditions).HasMaxLength(500);
				e.Property(x => x.Manner).HasMaxLength(14);
				e.Property(x => x.VoidReason).HasMaxLength(300);
				e.HasIndex(x => new { x.CompanyId, x.DeathNo }).IsUnique();
				e.HasIndex(x => new { x.CompanyId, x.PatientId });   // the "one live certificate" filter is the slice's
			});
		}
	}
}
