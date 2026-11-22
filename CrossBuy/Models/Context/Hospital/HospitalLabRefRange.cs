using Microsoft.EntityFrameworkCore;

namespace CrossBuy.Models.Context.Hospital
{
	// ==========================================================================================
	// HOSPITAL H233 — a reference range of an analyte for a sex and an age band (in days from birth, the end exclusive;
	// no end = for life). The most specific one that fits the patient is used when a result is entered
	// (HospitalLabAutoRules.Limits); the analyte's own limits remain the fallback.
	// deploy/sql/hospital_h233_lab_ranges_autoverify.sql builds it.
	// ==========================================================================================
	public class HospitalLabRefRange
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public int AnalyteId { get; set; }
		/// M, F, or null for both.
		public string? Sex { get; set; }
		public int AgeFromDays { get; set; }
		public int? AgeToDays { get; set; }
		public decimal? RefLow { get; set; }
		public decimal? RefHigh { get; set; }
		public decimal? CriticalLow { get; set; }
		public decimal? CriticalHigh { get; set; }
	}

	public static class HospitalLabRefRangeModel
	{
		public static void Configure(ModelBuilder b)
		{
			b.Entity<HospitalLabRefRange>(e =>
			{
				e.ToTable("HospitalLabRefRanges");
				e.Property(x => x.Sex).HasMaxLength(1);
				e.Property(x => x.RefLow).HasPrecision(18, 4); e.Property(x => x.RefHigh).HasPrecision(18, 4);
				e.Property(x => x.CriticalLow).HasPrecision(18, 4); e.Property(x => x.CriticalHigh).HasPrecision(18, 4);
				e.HasIndex(x => new { x.CompanyId, x.AnalyteId });
			});
		}
	}
}
