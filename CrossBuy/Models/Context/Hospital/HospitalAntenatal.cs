using Microsoft.EntityFrameworkCore;

namespace CrossBuy.Models.Context.Hospital
{
	// ==========================================================================================
	// HOSPITAL H85 — ANTENATAL CARE (phase 4: maternity, after immunizations). The birth itself is H58's; this is the
	// pregnancy before it.
	//   * A PREGNANCY is opened on the mother's file: her last menstrual period (or the ultrasound's due date, which wins),
	//     gravida / para / abortions, and the risks known at booking. The due date and the gestational age are computed.
	//     It closes as delivered (pointing at the H58 birth when there is one) or as a loss.
	//   * A VISIT records what is measured at each antenatal check: blood pressure, weight, fundal height, the baby's heart
	//     rate, protein in the urine, and when to come back. The warnings (hypertension, suspected pre-eclampsia, a heart
	//     rate out of range, a fundal height that does not fit the dates) are computed, never ticked.
	// CrossBuy/deploy/sql/hospital_h85_antenatal.sql builds the tables.
	// ==========================================================================================
	public static class HospitalPregnancyStatus
	{
		public const string Active = "Active";
		public const string Delivered = "Delivered";
		public const string Loss = "Loss";
		public static readonly IReadOnlyList<string> All = new[] { Active, Delivered, Loss };
	}

	public static class HospitalUrineProtein
	{
		public const string Negative = "Neg";
		public const string Trace = "Trace";
		public const string Plus1 = "1+";
		public const string Plus2 = "2+";
		public const string Plus3 = "3+";
		public static readonly IReadOnlyList<string> All = new[] { Negative, Trace, Plus1, Plus2, Plus3 };
		/// Significant proteinuria on a dipstick: 1+ or more.
		public static bool Significant(string? p) => p is Plus1 or Plus2 or Plus3;
	}

	public class HospitalPregnancy
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public string PregnancyNo { get; set; } = "";
		public int PatientId { get; set; }
		/// The first day of the last menstrual period.
		public DateTime? Lmp { get; set; }
		/// The due date from an ultrasound; when set it wins over the LMP's.
		public DateTime? UltrasoundEdd { get; set; }
		public int Gravida { get; set; }
		public int Para { get; set; }
		public int Abortions { get; set; }
		public string? Risks { get; set; }
		public string Status { get; set; } = HospitalPregnancyStatus.Active;
		/// H58's birth, when the pregnancy ended in one registered here.
		public int? BirthId { get; set; }
		public DateTime? EndedAt { get; set; }
		public string? EndNote { get; set; }
		public DateTime CreatedAt { get; set; }
		public int? CreatedByEmployeeId { get; set; }
	}

	public class HospitalAntenatalVisit
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public int PregnancyId { get; set; }
		public int PatientId { get; set; }
		public DateTime VisitDate { get; set; }
		public int? BpSystolic { get; set; }
		public int? BpDiastolic { get; set; }
		public decimal? WeightKg { get; set; }
		public decimal? FundalHeightCm { get; set; }
		public int? FetalHeartRate { get; set; }
		public string? UrineProtein { get; set; }
		public string? Note { get; set; }
		public DateTime? NextVisitOn { get; set; }
		public DateTime CreatedAt { get; set; }
		public int? CreatedByEmployeeId { get; set; }
	}

	public static class HospitalAntenatalModel
	{
		public static void Configure(ModelBuilder b)
		{
			b.Entity<HospitalPregnancy>(e =>
			{
				e.ToTable("HospitalPregnancies");   // no DbSet: each names its table
				e.Property(x => x.PregnancyNo).HasMaxLength(20);
				e.Property(x => x.Lmp).HasColumnType("date");
				e.Property(x => x.UltrasoundEdd).HasColumnType("date");
				e.Property(x => x.Risks).HasMaxLength(500);
				e.Property(x => x.Status).HasMaxLength(10);
				e.Property(x => x.EndNote).HasMaxLength(300);
				e.HasIndex(x => new { x.CompanyId, x.PregnancyNo }).IsUnique();
				e.HasIndex(x => new { x.CompanyId, x.PatientId });
			});
			b.Entity<HospitalAntenatalVisit>(e =>
			{
				e.ToTable("HospitalAntenatalVisits");
				e.Property(x => x.VisitDate).HasColumnType("date");
				e.Property(x => x.WeightKg).HasPrecision(5, 1);
				e.Property(x => x.FundalHeightCm).HasPrecision(4, 1);
				e.Property(x => x.UrineProtein).HasMaxLength(5);
				e.Property(x => x.Note).HasMaxLength(500);
				e.Property(x => x.NextVisitOn).HasColumnType("date");
				e.HasIndex(x => new { x.CompanyId, x.PregnancyId });
			});
		}
	}
}
