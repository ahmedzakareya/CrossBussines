using Microsoft.EntityFrameworkCore;

namespace CrossBuy.Models.Context.Hospital
{
	// ==========================================================================================
	// HOSPITAL — intensive care (unit H40), as the approved prototype draws it (#/icu): the patient's banner, the unit's
	// figures (beds occupied, ventilated, on vasopressors, SOFA against yesterday) and the HOURLY FLOWSHEET — heart
	// rate, mean arterial pressure, SpO₂, respiratory rate, temperature, urine output. A nurse enters an hour until the
	// bedside monitors are connected; the day's assessment (SOFA, ventilator, vasopressors) is one row per admission and
	// day. Who is in intensive care is read from the beds (H3: an open stay in an `Icu` room), never stored twice.
	// CrossBuy/deploy/sql/hospital_h40_icu.sql.
	// ==========================================================================================

	public class HospitalIcuObservation
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public int AdmissionId { get; set; }
		public int PatientId { get; set; }
		/// The hour the values belong to (UTC, on the hour).
		public DateTime HourAt { get; set; }
		public int? HeartRate { get; set; }
		/// Mean arterial pressure, mmHg.
		public int? Map { get; set; }
		public int? SpO2 { get; set; }
		public int? RespRate { get; set; }
		public decimal? TempC { get; set; }
		/// Urine output over the hour, ml.
		public int? UrineMl { get; set; }
		public int? RecordedByEmployeeId { get; set; }
		public DateTime RecordedAt { get; set; }
	}

	public class HospitalIcuAssessment
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public int AdmissionId { get; set; }
		public int PatientId { get; set; }
		/// The ward-clock day assessed.
		public DateTime Day { get; set; }
		public int? Sofa { get; set; }
		public bool Ventilated { get; set; }
		public bool Vasopressors { get; set; }
		public int? RecordedByEmployeeId { get; set; }
		public DateTime RecordedAt { get; set; }
	}

	public static class HospitalIcuModel
	{
		public static void Configure(ModelBuilder b)
		{
			HospitalIcuCareModel.Configure(b);   // H238
			b.Entity<HospitalIcuObservation>(e =>
			{
				e.ToTable("HospitalIcuObservations");   // no DbSet: each names its table
				e.Property(x => x.TempC).HasPrecision(4, 1);
				e.HasIndex(x => new { x.AdmissionId, x.HourAt }).IsUnique();
			});
			b.Entity<HospitalIcuAssessment>(e =>
			{
				e.ToTable("HospitalIcuAssessments");
				e.Property(x => x.Day).HasColumnType("date");
				e.HasIndex(x => new { x.AdmissionId, x.Day }).IsUnique();
			});
		}
	}
}
