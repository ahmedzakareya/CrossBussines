using Microsoft.EntityFrameworkCore;

namespace CrossBuy.Models.Context.Hospital
{
	// ==========================================================================================
	// HOSPITAL H72 — MEDICATION RECONCILIATION (phase 1 "patient safety" of the 2026-11-09 gap research; JCI MMU and the
	// accreditation bodies require it at admission and discharge). On admission the patient's home medicines are listed
	// (the best possible medication history: what, how much, how often, when last taken, who said so) and a doctor decides
	// for each — continue, change, hold or stop — then signs the reconciliation complete (or records that the patient
	// takes nothing at home). On discharge every home medicine and every medicine still running in hospital gets a
	// decision for home — continue, change, stop — and a medicine new for home can be added; the signed list is what the
	// patient goes home with and can be written into the discharge summary.
	// CrossBuy/deploy/sql/hospital_h72_med_recon.sql builds the tables.
	// ==========================================================================================
	public static class HospitalMedReconStage
	{
		public const string Admission = "Admission";
		public const string Discharge = "Discharge";
		public static readonly IReadOnlyList<string> All = new[] { Admission, Discharge };
	}

	public static class HospitalMedSource
	{
		public const string Patient = "Patient";
		public const string Family = "Family";
		public const string Records = "Records";
		public const string Pharmacy = "Pharmacy";
		public const string Other = "Other";
		public static readonly IReadOnlyList<string> All = new[] { Patient, Family, Records, Pharmacy, Other };
	}

	/// What a line is: a medicine the patient takes at home, one given in hospital, or one new for home at discharge.
	public static class HospitalMedLineKind
	{
		public const string Home = "Home";
		public const string Inpatient = "Inpatient";
		public const string New = "New";
	}

	public static class HospitalMedDecision
	{
		public const string Continue = "Continue";
		public const string Change = "Change";
		public const string Hold = "Hold";
		public const string Stop = "Stop";
		/// Admission: continue, change, hold, stop. Discharge: continue, change, stop.
		public static IReadOnlyList<string> For(string stage) => stage == HospitalMedReconStage.Admission ? new[] { Continue, Change, Hold, Stop } : new[] { Continue, Change, Stop };
	}

	public class HospitalMedReconLine
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public int AdmissionId { get; set; }
		public int PatientId { get; set; }
		public string Kind { get; set; } = HospitalMedLineKind.Home;
		/// For an Inpatient line: the hospital order it stands for.
		public int? MedOrderId { get; set; }
		public string Drug { get; set; } = "";
		public string? Dose { get; set; }
		public string? Route { get; set; }
		/// "twice a day", "08:00 and 20:00" — as told.
		public string? Frequency { get; set; }
		/// Home lines: when the patient last took it, as told ("this morning").
		public string? LastTaken { get; set; }
		/// Home lines: who the history came from.
		public string? Source { get; set; }
		public string? AdmitDecision { get; set; }
		public string? AdmitReason { get; set; }
		public string? DischargeDecision { get; set; }
		public string? DischargeNote { get; set; }
		public DateTime CreatedAt { get; set; }
		public int? CreatedByEmployeeId { get; set; }
		public DateTime? DecidedAt { get; set; }
		public int? DecidedByEmployeeId { get; set; }
	}

	/// A stage signed complete — once per admission and stage.
	public class HospitalMedRecon
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public int AdmissionId { get; set; }
		public int PatientId { get; set; }
		public string Stage { get; set; } = HospitalMedReconStage.Admission;
		/// Admission only: the patient takes no medicines at home.
		public bool NoHomeMeds { get; set; }
		public DateTime CompletedAt { get; set; }
		public int? CompletedByEmployeeId { get; set; }
	}

	public static class HospitalMedReconModel
	{
		public static void Configure(ModelBuilder b)
		{
			b.Entity<HospitalMedReconLine>(e =>
			{
				e.ToTable("HospitalMedReconLines");   // no DbSet: each names its table
				e.Property(x => x.Kind).HasMaxLength(10);
				e.Property(x => x.Drug).HasMaxLength(200);
				e.Property(x => x.Dose).HasMaxLength(60);
				e.Property(x => x.Route).HasMaxLength(10);
				e.Property(x => x.Frequency).HasMaxLength(100);
				e.Property(x => x.LastTaken).HasMaxLength(60);
				e.Property(x => x.Source).HasMaxLength(10);
				e.Property(x => x.AdmitDecision).HasMaxLength(10);
				e.Property(x => x.AdmitReason).HasMaxLength(300);
				e.Property(x => x.DischargeDecision).HasMaxLength(10);
				e.Property(x => x.DischargeNote).HasMaxLength(300);
				e.HasIndex(x => new { x.CompanyId, x.AdmissionId });
			});
			b.Entity<HospitalMedRecon>(e =>
			{
				e.ToTable("HospitalMedRecons");
				e.Property(x => x.Stage).HasMaxLength(10);
				e.HasIndex(x => new { x.CompanyId, x.AdmissionId, x.Stage }).IsUnique();
			});
		}
	}
}
