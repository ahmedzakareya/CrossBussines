using Microsoft.EntityFrameworkCore;

namespace CrossBuy.Models.Context.Hospital
{
	// ==========================================================================================
	// HOSPITAL H252 — the NEONATAL UNIT's daily record of each admitted newborn (one row per baby and day): weight,
	// length and head circumference, the day's enteral feeds (volume and kind), parenteral nutrition, phototherapy and
	// the serum bilirubin. The baby's birth (H-birth register) gives the gestation and birth weight the board works from.
	// deploy/sql/hospital_h252_nicu.sql builds the table.
	// ==========================================================================================
	public static class HospitalNicuFeed
	{
		public const string Breast = "Breast", Expressed = "Expressed", Donor = "Donor", Formula = "Formula", Mixed = "Mixed", Nil = "Nil";
		public static readonly IReadOnlyList<string> All = new[] { Breast, Expressed, Donor, Formula, Mixed, Nil };
	}

	public class HospitalNicuDay
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public int AdmissionId { get; set; }
		public int PatientId { get; set; }
		public DateTime Day { get; set; }
		public int? WeightGrams { get; set; }
		public decimal? LengthCm { get; set; }
		public decimal? HeadCm { get; set; }
		/// Enteral feeds over the day, mL.
		public int? FeedsMl { get; set; }
		public string? FeedType { get; set; }
		public bool Tpn { get; set; }
		public bool Phototherapy { get; set; }
		/// Total serum bilirubin, µmol/L.
		public int? Bilirubin { get; set; }
		public DateTime RecordedAt { get; set; }
		public int? RecordedByEmployeeId { get; set; }
	}

	public static class HospitalNicuModel
	{
		public static void Configure(ModelBuilder b)
		{
			b.Entity<HospitalNicuDay>(e =>
			{
				e.ToTable("HospitalNicuDays");
				e.Property(x => x.Day).HasColumnType("date"); e.Property(x => x.FeedType).HasMaxLength(10);
				e.Property(x => x.LengthCm).HasPrecision(4, 1); e.Property(x => x.HeadCm).HasPrecision(4, 1);
				e.HasIndex(x => new { x.CompanyId, x.AdmissionId, x.Day }).IsUnique();
				e.HasIndex(x => new { x.CompanyId, x.PatientId });
			});
		}
	}
}
