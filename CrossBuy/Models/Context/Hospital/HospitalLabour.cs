using Microsoft.EntityFrameworkCore;

namespace CrossBuy.Models.Context.Hospital
{
	// ==========================================================================================
	// HOSPITAL H86 — THE PARTOGRAPH (labour monitoring; phase 4 maternity, after antenatal care H85 and before the birth
	// register H58). A LABOUR is opened on the mother's stay; each OBSERVATION records the cervix, the head's descent,
	// the contractions, the fetal heart, the liquor and the mother's vital signs at a moment. The alert line (1 cm an hour
	// from the first active-phase dilatation, 4 cm) and the action line (four hours to its right) are computed, as are the
	// warnings; nothing is ticked. A labour closes with how it ended.
	// CrossBuy/deploy/sql/hospital_h86_labour.sql builds the tables.
	// ==========================================================================================
	public static class HospitalLabourStatus
	{
		public const string Active = "Active";
		public const string Closed = "Closed";
	}

	public static class HospitalLabourOutcome
	{
		public const string Vaginal = "Vaginal";
		public const string Assisted = "Assisted";
		public const string Caesarean = "Caesarean";
		public const string Transferred = "Transferred";
		public static readonly IReadOnlyList<string> All = new[] { Vaginal, Assisted, Caesarean, Transferred };
	}

	public static class HospitalLiquor
	{
		/// Membranes intact.
		public const string Intact = "I";
		public const string Clear = "C";
		public const string Meconium = "M";
		public const string Blood = "B";
		public static readonly IReadOnlyList<string> All = new[] { Intact, Clear, Meconium, Blood };
	}

	public class HospitalLabour
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public int AdmissionId { get; set; }
		public int PatientId { get; set; }
		public string Status { get; set; } = HospitalLabourStatus.Active;
		public DateTime StartedAt { get; set; }
		public int? StartedByEmployeeId { get; set; }
		public string? Outcome { get; set; }
		public DateTime? ClosedAt { get; set; }
		public int? ClosedByEmployeeId { get; set; }
		public string? Note { get; set; }
	}

	public class HospitalLabourObservation
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public int LabourId { get; set; }
		public int PatientId { get; set; }
		public DateTime At { get; set; }
		/// Cervical dilatation, cm (0–10).
		public decimal? CervixCm { get; set; }
		/// Head palpable above the brim, in fifths (5 = high, 0 = engaged).
		public int? DescentFifths { get; set; }
		public int? ContractionsPer10 { get; set; }
		/// How long each contraction lasts, in seconds.
		public int? ContractionSeconds { get; set; }
		public int? FetalHeartRate { get; set; }
		public string? Liquor { get; set; }
		public int? BpSystolic { get; set; }
		public int? BpDiastolic { get; set; }
		public int? Pulse { get; set; }
		public decimal? TemperatureC { get; set; }
		public string? Note { get; set; }
		public DateTime CreatedAt { get; set; }
		public int? CreatedByEmployeeId { get; set; }
	}

	public static class HospitalLabourModel
	{
		public static void Configure(ModelBuilder b)
		{
			b.Entity<HospitalLabour>(e =>
			{
				e.ToTable("HospitalLabours");   // no DbSet: each names its table
				e.Property(x => x.Status).HasMaxLength(8);
				e.Property(x => x.Outcome).HasMaxLength(12);
				e.Property(x => x.Note).HasMaxLength(500);
				e.HasIndex(x => new { x.CompanyId, x.AdmissionId });
			});
			b.Entity<HospitalLabourObservation>(e =>
			{
				e.ToTable("HospitalLabourObservations");
				e.Property(x => x.CervixCm).HasPrecision(3, 1);
				e.Property(x => x.Liquor).HasMaxLength(1);
				e.Property(x => x.TemperatureC).HasPrecision(4, 1);
				e.Property(x => x.Note).HasMaxLength(300);
				e.HasIndex(x => new { x.CompanyId, x.LabourId, x.At });
			});
		}
	}
}
