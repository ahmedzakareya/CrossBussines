using Microsoft.EntityFrameworkCore;

namespace CrossBuy.Models.Context.Hospital
{
	// ==========================================================================================
	// HOSPITAL H246 — ACUITY-BASED STAFFING. Each shift the nurse classifies every inpatient by the care they need
	// (the factors that drive nursing time); the level sets the hours of care, the hours set the nurses a ward needs,
	// and the charge nurse's planned staff is set against it. Who is on which ward is read from the beds (H3).
	// deploy/sql/hospital_h246_acuity.sql builds the tables.
	// ==========================================================================================
	public class HospitalAcuityScore
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public int AdmissionId { get; set; }
		public int PatientId { get; set; }
		/// The ward-clock day and the shift classified.
		public DateTime Day { get; set; }
		public string Shift { get; set; } = HospitalShift.Day;
		/// Factor codes, comma-separated.
		public string Factors { get; set; } = "";
		public int Level { get; set; }
		public DateTime At { get; set; }
		public int? ByEmployeeId { get; set; }
	}

	public class HospitalWardStaffing
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public int WardId { get; set; }
		public DateTime Day { get; set; }
		public string Shift { get; set; } = HospitalShift.Day;
		public int NursesPlanned { get; set; }
		public DateTime UpdatedAt { get; set; }
		public int? UpdatedByEmployeeId { get; set; }
	}

	public static class HospitalAcuityModel
	{
		public static void Configure(ModelBuilder b)
		{
			b.Entity<HospitalAcuityScore>(e =>
			{
				e.ToTable("HospitalAcuityScores");
				e.Property(x => x.Day).HasColumnType("date"); e.Property(x => x.Shift).HasMaxLength(10); e.Property(x => x.Factors).HasMaxLength(200);
				e.HasIndex(x => new { x.CompanyId, x.AdmissionId, x.Day, x.Shift }).IsUnique();
				e.HasIndex(x => new { x.CompanyId, x.PatientId });
			});
			b.Entity<HospitalWardStaffing>(e =>
			{
				e.ToTable("HospitalWardStaffing");
				e.Property(x => x.Day).HasColumnType("date"); e.Property(x => x.Shift).HasMaxLength(10);
				e.HasIndex(x => new { x.CompanyId, x.WardId, x.Day, x.Shift }).IsUnique();
			});
		}
	}
}
