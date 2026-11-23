using Microsoft.EntityFrameworkCore;

namespace CrossBuy.Models.Context.Hospital
{
	// ==========================================================================================
	// HOSPITAL CONSULTATION — encounters, diagnoses, prescriptions, the code catalogue (unit H5).
	//
	// An encounter is the doctor's record of ONE appointment: the SOAP note, the vitals, the
	// diagnoses (one primary), the prescription, the follow-up and the referral. Draft while it is
	// written, Signed once closed — and a signed encounter is never edited again (the service
	// refuses; the database records who signed and when).
	//
	// Diagnoses are copied onto the encounter (code + both names) at the moment of signing, so a
	// later change to the catalogue never rewrites what the doctor recorded.
	// deploy/sql/hospital_h5_consultation.sql builds the tables.
	// ==========================================================================================

	public static class HospitalEncounterStatus
	{
		public const string Draft = "Draft";
		public const string Signed = "Signed";
	}

	public class HospitalDiagnosisCode
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public string Code { get; set; } = "";
		public string Name { get; set; } = "";
		public string NameEn { get; set; } = "";
		public bool IsActive { get; set; } = true;
		public DateTime CreatedAt { get; set; }
	}

	public class HospitalEncounter
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public int AppointmentId { get; set; }
		public int PatientId { get; set; }
		public int PractitionerId { get; set; }
		public string Status { get; set; } = HospitalEncounterStatus.Draft;
		public string? ChiefComplaint { get; set; }
		public string? History { get; set; }
		public string? Examination { get; set; }
		public string? PlanNote { get; set; }
		public int? BpSystolic { get; set; }
		public int? BpDiastolic { get; set; }
		public int? HeartRate { get; set; }
		public decimal? TemperatureC { get; set; }
		public int? SpO2 { get; set; }
		public decimal? WeightKg { get; set; }
		public decimal? HeightCm { get; set; }
		public int? FollowUpDays { get; set; }
		public int? ReferToDepartmentId { get; set; }
		public DateTime StartedAt { get; set; }
		public DateTime? SignedAt { get; set; }
		public int? SignedByEmployeeId { get; set; }
		public DateTime CreatedAt { get; set; }
		public int? CreatedByEmployeeId { get; set; }
		public DateTime? UpdatedAt { get; set; }
		public int? UpdatedByEmployeeId { get; set; }

		public bool HasVitals => BpSystolic != null || HeartRate != null || TemperatureC != null || SpO2 != null || WeightKg != null;

		/// Body-mass index from weight and height, when both are known.
		public decimal? Bmi => WeightKg is decimal w && HeightCm is decimal h && h > 0 ? Math.Round(w / (h / 100m * (h / 100m)), 1) : null;
	}

	public class HospitalEncounterDiagnosis
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public int EncounterId { get; set; }
		public string Code { get; set; } = "";
		public string Name { get; set; } = "";
		public string NameEn { get; set; } = "";
		public bool IsPrimary { get; set; }
		public int SortOrder { get; set; }
	}

	public class HospitalPrescriptionLine
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public int EncounterId { get; set; }
		public string Drug { get; set; } = "";
		/// H10: the inventory item picked from the pharmacy's stock; null for a drug the hospital does not stock.
		public int? ItemId { get; set; }
		public string? Instructions { get; set; }
		/// H33: the English twins (a stocked drug takes its item's English name).
		public string? DrugEn { get; set; }
		public string? InstructionsEn { get; set; }
		public int Quantity { get; set; } = 1;
		public int SortOrder { get; set; }
	}

	public static class HospitalConsultationModel
	{
		public static void Configure(ModelBuilder b)
		{
			HospitalScribeModel.Configure(b);   // H254: ambient documentation trail
			b.Entity<HospitalDiagnosisCode>(e => e.HasIndex(x => new { x.CompanyId, x.Code }).IsUnique());
			b.Entity<HospitalEncounter>(e =>
			{
				e.Property(x => x.TemperatureC).HasPrecision(4, 1);
				e.Property(x => x.WeightKg).HasPrecision(5, 1);
				e.Property(x => x.HeightCm).HasPrecision(5, 1);
				e.Ignore(x => x.HasVitals);
				e.Ignore(x => x.Bmi);
				e.HasIndex(x => new { x.CompanyId, x.AppointmentId }).IsUnique();
				e.HasIndex(x => new { x.CompanyId, x.PatientId, x.StartedAt });
			});
			b.Entity<HospitalEncounterDiagnosis>(e =>
			{
				e.HasOne<HospitalEncounter>().WithMany().HasForeignKey(x => x.EncounterId).OnDelete(DeleteBehavior.NoAction);
				e.HasIndex(x => new { x.CompanyId, x.EncounterId });
			});
			b.Entity<HospitalPrescriptionLine>(e =>
			{
				e.HasOne<HospitalEncounter>().WithMany().HasForeignKey(x => x.EncounterId).OnDelete(DeleteBehavior.NoAction);
				e.HasIndex(x => new { x.CompanyId, x.EncounterId });
			});
		}
	}
}
