using Microsoft.EntityFrameworkCore;

namespace CrossBuy.Models.Context.Hospital
{
	// ==========================================================================================
	// HOSPITAL H115 — REFERENCE RANGES BY WHO THE PATIENT IS. A concept's own normal / critical limits are one range
	// for everybody; a haemoglobin, a heart rate or a TSH is not. Each extra range says who it is for — a sex, an age
	// band (in days, so a newborn and a toddler differ), and/or a pregnancy (any, a trimester, or not pregnant) — and the
	// most specific range that fits the patient replaces the concept's own limits for that reading. None fits: the
	// concept's own range, exactly as before.
	// CrossBuy/deploy/sql/hospital_h115_concept_ranges.sql builds the table.
	// ==========================================================================================
	public static class HospitalRangePregnancy
	{
		public const string NotPregnant = "No";
		public const string Pregnant = "Any";
		public const string T1 = "T1";
		public const string T2 = "T2";
		public const string T3 = "T3";
		public static readonly IReadOnlyList<string> All = new[] { NotPregnant, Pregnant, T1, T2, T3 };
	}

	public class HospitalConceptRange
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public int ConceptId { get; set; }
		/// M / F, or null = either.
		public string? Sex { get; set; }
		/// The age band in days, from (inclusive) to (exclusive); null = open-ended.
		public int? AgeFromDays { get; set; }
		public int? AgeToDays { get; set; }
		/// HospitalRangePregnancy, or null = whether pregnant or not.
		public string? Pregnancy { get; set; }
		public decimal? NormalLow { get; set; }
		public decimal? NormalHigh { get; set; }
		public decimal? CriticalLow { get; set; }
		public decimal? CriticalHigh { get; set; }
		/// A pair's second number (a diastolic): null keeps the concept's own.
		public decimal? NormalHigh2 { get; set; }
		public decimal? CriticalHigh2 { get; set; }
		/// Where the range comes from (a guideline, the laboratory's own validation).
		public string? Source { get; set; }
		public DateTime UpdatedAt { get; set; }
		public int? UpdatedByEmployeeId { get; set; }
	}

	public static class HospitalConceptRangeModel
	{
		public static void Configure(ModelBuilder b)
		{
			b.Entity<HospitalConceptRange>(e =>
			{
				e.ToTable("HospitalConceptRanges");
				e.Property(x => x.Sex).HasMaxLength(1);
				e.Property(x => x.Pregnancy).HasMaxLength(3);
				foreach (var p in new[] { "NormalLow", "NormalHigh", "CriticalLow", "CriticalHigh", "NormalHigh2", "CriticalHigh2" }) e.Property<decimal?>(p).HasPrecision(18, 4);
				e.Property(x => x.Source).HasMaxLength(200);
				e.HasIndex(x => new { x.CompanyId, x.ConceptId });
			});
		}
	}
}
