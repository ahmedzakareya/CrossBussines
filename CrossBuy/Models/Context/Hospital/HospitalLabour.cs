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
	// H175 — the WHO LABOUR CARE GUIDE (2020) replaces the partograph (register rule "labour", version 2). Active labour
	// from 5 cm; no alert/action lines but a time limit at each centimetre; the guide's seven sections — the labour's
	// characteristics, supportive care, the baby, the woman, labour progress, medication, shared decision-making — each
	// item with its own alert threshold. CrossBuy/deploy/sql/hospital_h175_labour_care_guide.sql.
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
		/// Meconium-stained (light or moderate: M+, M++).
		public const string Meconium = "M";
		/// H175: thick meconium (M+++) — the guide's alert.
		public const string ThickMeconium = "T";
		public const string Blood = "B";
		public static readonly IReadOnlyList<string> All = new[] { Intact, Clear, Meconium, ThickMeconium, Blood };
	}

	/// H175: the Labour Care Guide's coded items.
	public static class HospitalLcg
	{
		public static readonly IReadOnlyList<string> Onsets = new[] { "Spontaneous", "Induced" };
		/// Posture: supine (SP, the alert) or mobile (MO).
		public static readonly IReadOnlyList<string> Postures = new[] { "SP", "MO" };
		/// FHR deceleration: none, early, late (the alert), variable.
		public static readonly IReadOnlyList<string> Decelerations = new[] { "N", "E", "L", "V" };
		/// Fetal position: occiput anterior, posterior, transverse (OP and OT alert).
		public static readonly IReadOnlyList<string> Positions = new[] { "OA", "OP", "OT" };
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
		// H175: the guide's first section — the labour's characteristics
		public int? Parity { get; set; }
		public string? Onset { get; set; }
		public DateTime? MembranesRupturedAt { get; set; }
		public string? RiskFactors { get; set; }
		/// When active labour was diagnosed (≥ 5 cm); empty → the first observation at 5 cm or more.
		public DateTime? ActiveLabourAt { get; set; }
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
		// H175: the guide's other sections
		public bool? Companion { get; set; }
		public bool? PainRelief { get; set; }
		public bool? OralFluid { get; set; }
		public string? Posture { get; set; }
		public string? Deceleration { get; set; }
		public string? FetalPosition { get; set; }
		/// Caput and moulding: 0, +, ++, +++ (0–3).
		public int? Caput { get; set; }
		public int? Moulding { get; set; }
		/// Urine protein and acetone: 0 to ++++ (0–4).
		public int? UrineProtein { get; set; }
		public int? UrineAcetone { get; set; }
		public string? Oxytocin { get; set; }
		public string? Medicine { get; set; }
		public bool? IvFluids { get; set; }
		/// Shared decision-making: the assessment and the plan.
		public string? Assessment { get; set; }
		public string? Plan { get; set; }
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
				e.Property(x => x.Onset).HasMaxLength(12);   // H175
				e.Property(x => x.RiskFactors).HasMaxLength(300);
				e.HasIndex(x => new { x.CompanyId, x.AdmissionId });
			});
			b.Entity<HospitalLabourObservation>(e =>
			{
				e.ToTable("HospitalLabourObservations");
				e.Property(x => x.CervixCm).HasPrecision(3, 1);
				e.Property(x => x.Liquor).HasMaxLength(1);
				e.Property(x => x.TemperatureC).HasPrecision(4, 1);
				e.Property(x => x.Note).HasMaxLength(300);
				e.Property(x => x.Posture).HasMaxLength(2);   // H175
				e.Property(x => x.Deceleration).HasMaxLength(1);
				e.Property(x => x.FetalPosition).HasMaxLength(2);
				e.Property(x => x.Oxytocin).HasMaxLength(40);
				e.Property(x => x.Medicine).HasMaxLength(200);
				e.Property(x => x.Assessment).HasMaxLength(300);
				e.Property(x => x.Plan).HasMaxLength(300).HasColumnName("CarePlan");   // "Plan" is a reserved word
				e.HasIndex(x => new { x.CompanyId, x.LabourId, x.At });
			});
		}
	}
}
