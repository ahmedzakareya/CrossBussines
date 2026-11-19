using Microsoft.EntityFrameworkCore;

namespace CrossBuy.Models.Context.Hospital
{
	// ==========================================================================================
	// HOSPITAL H71 — WARD OBSERVATIONS, NEWS2 AND NURSING RISK ASSESSMENTS (gap research 2026-11-09, phase 1 "patient
	// safety", owner: «موافق ابدا بالتطبيق» — "agreed, start building"). Every inpatient's vital signs as the National Early Warning Score 2
	// (Royal College of Physicians, 2017) reads them — respiration, SpO₂ on scale 1 or 2, air or oxygen, systolic
	// pressure, pulse, consciousness (ACVPU), temperature — plus pain. The score and its clinical risk are worked out on
	// save and kept with the row (what the nurse saw then), and a risk that calls for a response opens an urgent nursing
	// task. The falls risk (Morse Fall Scale) and the pressure-injury risk (Braden Scale) are scored from their items and
	// kept with them. The ICU keeps its own hourly flowsheet (H40); this is the ward's.
	// CrossBuy/deploy/sql/hospital_h71_observations.sql builds the tables.
	// ==========================================================================================
	public static class HospitalConsciousness
	{
		public const string Alert = "A";
		/// New confusion (scores like V, P and U).
		public const string Confused = "C";
		public const string Voice = "V";
		public const string Pain = "P";
		public const string Unresponsive = "U";
		public static readonly IReadOnlyList<string> All = new[] { Alert, Confused, Voice, Pain, Unresponsive };
	}

	public static class HospitalNews2Risk
	{
		public const string Low = "Low";
		/// A single parameter scoring 3 while the total is under 5.
		public const string LowSingle = "LowSingle";
		public const string Medium = "Medium";
		public const string High = "High";
		public static readonly IReadOnlyList<string> All = new[] { Low, LowSingle, Medium, High };
	}

	public static class HospitalRiskKind
	{
		public const string Morse = "Morse";
		public const string Braden = "Braden";
		public static readonly IReadOnlyList<string> All = new[] { Morse, Braden };
	}

	public class HospitalObservation
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public int AdmissionId { get; set; }
		public int PatientId { get; set; }
		/// When the values were taken (UTC).
		public DateTime TakenAt { get; set; }
		public int? RespRate { get; set; }
		public int? SpO2 { get; set; }
		/// SpO₂ scale 2 (a target of 88–92%, hypercapnic respiratory failure) — chosen by a clinician.
		public bool SpO2Scale2 { get; set; }
		public bool OnOxygen { get; set; }
		public int? SystolicBp { get; set; }
		public int? DiastolicBp { get; set; }
		public int? HeartRate { get; set; }
		/// A, C, V, P or U.
		public string? Consciousness { get; set; }
		public decimal? TempC { get; set; }
		/// 0–10.
		public int? PainScore { get; set; }
		/// The NEWS2 total — null when a parameter was not taken (an incomplete set is not scored).
		public int? News2 { get; set; }
		public string? News2Risk { get; set; }
		public string? Note { get; set; }
		public int? RecordedByEmployeeId { get; set; }
		public DateTime RecordedAt { get; set; }
	}

	public class HospitalRiskAssessment
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public int AdmissionId { get; set; }
		public int PatientId { get; set; }
		public string Kind { get; set; } = HospitalRiskKind.Morse;
		/// The item scores in the scale's order, comma-separated (Morse 6 items, Braden 6).
		public string Items { get; set; } = "";
		public int Score { get; set; }
		public string Level { get; set; } = "";
		public string? Note { get; set; }
		public DateTime AssessedAt { get; set; }
		public int? AssessedByEmployeeId { get; set; }
	}

	public static class HospitalObservationModel
	{
		public static void Configure(ModelBuilder b)
		{
			HospitalDeviceVitalModel.Configure(b);   // H201: the bedside monitors' readings, waiting for a nurse
			HospitalSurveillanceModel.Configure(b);   // H212: sepsis and VTE alerts the record raised by itself
			b.Entity<HospitalObservation>(e =>
			{
				e.ToTable("HospitalObservations");   // no DbSet: each names its table
				e.Property(x => x.Consciousness).HasMaxLength(1);
				e.Property(x => x.TempC).HasPrecision(4, 1);
				e.Property(x => x.News2Risk).HasMaxLength(10);
				e.Property(x => x.Note).HasMaxLength(300);
				e.HasIndex(x => new { x.CompanyId, x.AdmissionId, x.TakenAt });
			});
			b.Entity<HospitalRiskAssessment>(e =>
			{
				e.ToTable("HospitalRiskAssessments");
				e.Property(x => x.Kind).HasMaxLength(10);
				e.Property(x => x.Items).HasMaxLength(40);
				e.Property(x => x.Level).HasMaxLength(10);
				e.Property(x => x.Note).HasMaxLength(300);
				e.HasIndex(x => new { x.CompanyId, x.AdmissionId, x.Kind, x.AssessedAt });
			});
		}
	}
}
