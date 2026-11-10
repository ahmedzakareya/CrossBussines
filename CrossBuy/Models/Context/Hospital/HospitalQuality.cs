using Microsoft.EntityFrameworkCore;

namespace CrossBuy.Models.Context.Hospital
{
	// ==========================================================================================
	// HOSPITAL H81 — QUALITY INDICATORS (phase 2 of the 2026-11-09 gap research; JCI QPS, CBAHI, the national indicator
	// sets). Every indicator is computed for a month from the hospital's own records — stays, deaths, incidents, infection
	// cases, reconciliations, consultations, chart sign-offs, surveys — never typed. Its target is a SETTING the hospital
	// can change (the owner's rule: who decides and what counts is configuration, not code); until set, a published
	// benchmark applies. CrossBuy/deploy/sql/hospital_h81_quality_targets.sql builds the targets table.
	// ==========================================================================================
	public static class HospitalQualityIndicator
	{
		public const string Alos = "Alos";
		public const string Occupancy = "Occupancy";
		public const string Readmission30 = "Readmission30";
		public const string Mortality = "Mortality";
		public const string Hai = "Hai";
		public const string Falls = "Falls";
		public const string MedErrors = "MedErrors";
		public const string MedRec24h = "MedRec24h";
		public const string ConsultOnTime = "ConsultOnTime";
		public const string ChartOnTime = "ChartOnTime";
		public const string Satisfaction = "Satisfaction";
		public const string Nps = "Nps";

		/// Code, unit ("days", "%", "per1000", "of5", "score"), whether lower is better, and the default target.
		public static readonly IReadOnlyList<(string Code, string Unit, bool LowerBetter, decimal Default)> All = new[]
		{
			(Alos, "days", true, 5.0m),
			(Occupancy, "%", false, 75m),
			(Readmission30, "%", true, 10m),
			(Mortality, "%", true, 2m),
			(Hai, "per1000", true, 2m),
			(Falls, "per1000", true, 1m),
			(MedErrors, "per1000", true, 1m),
			(MedRec24h, "%", false, 90m),
			(ConsultOnTime, "%", false, 90m),
			(ChartOnTime, "%", false, 90m),
			(Satisfaction, "of5", false, 4.0m),
			(Nps, "score", false, 30m),
		};

		public static bool Known(string code) => All.Any(i => i.Code == code);
	}

	/// The hospital's own target for one indicator (absent: the default applies).
	public class HospitalQualityTarget
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public string Indicator { get; set; } = "";
		public decimal Target { get; set; }
		public DateTime UpdatedAt { get; set; }
		public int? UpdatedByEmployeeId { get; set; }
	}

	public static class HospitalQualityModel
	{
		public static void Configure(ModelBuilder b)
		{
			b.Entity<HospitalQualityTarget>(e =>
			{
				e.ToTable("HospitalQualityTargets");   // no DbSet: it names its table
				e.Property(x => x.Indicator).HasMaxLength(20);
				e.Property(x => x.Target).HasColumnType("decimal(10,2)");
				e.HasIndex(x => new { x.CompanyId, x.Indicator }).IsUnique();
			});
		}
	}
}
