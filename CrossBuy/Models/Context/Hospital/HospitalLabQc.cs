using Microsoft.EntityFrameworkCore;

namespace CrossBuy.Models.Context.Hospital
{
	// ==========================================================================================
	// HOSPITAL H232 — LABORATORY QUALITY CONTROL. A control MATERIAL is a lot of a known level for one analyte, with its
	// target mean and standard deviation. Each QC RUN records the value measured on the analyser; the Westgard rules
	// judge it against the material's mean and SD and the material's previous runs. A rejected run puts the analyte's
	// test OUT OF CONTROL: no patient result for that test may be entered until a later run of that material is accepted.
	// deploy/sql/hospital_h232_lab_qc.sql builds the tables.
	// ==========================================================================================
	public static class HospitalQcStatus
	{
		public const string Accepted = "Accepted";
		/// 1-2s only: in control, look closer.
		public const string Warning = "Warning";
		public const string Rejected = "Rejected";
		public static readonly IReadOnlyList<string> All = new[] { Accepted, Warning, Rejected };
	}

	public class HospitalLabQcMaterial
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public int AnalyteId { get; set; }
		public string Lot { get; set; } = "";
		/// 1 = low, 2 = normal, 3 = high.
		public int Level { get; set; } = 2;
		public decimal Mean { get; set; }
		public decimal Sd { get; set; }
		public DateTime? ExpiresOn { get; set; }
		public bool IsActive { get; set; } = true;
		public DateTime CreatedAt { get; set; }
		public int? CreatedByEmployeeId { get; set; }
	}

	public class HospitalLabQcRun
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public int MaterialId { get; set; }
		public decimal Value { get; set; }
		public DateTime At { get; set; }
		public int? ByEmployeeId { get; set; }
		public string Status { get; set; } = HospitalQcStatus.Accepted;
		/// The rules this run broke, comma-joined (e.g. "1-3s,R-4s"); empty when none.
		public string? Violations { get; set; }
		/// What was done about a rejected run (recalibrated, new reagent...).
		public string? Action { get; set; }
	}

	public static class HospitalLabQcModel
	{
		public static void Configure(ModelBuilder b)
		{
			b.Entity<HospitalLabQcMaterial>(e =>
			{
				e.ToTable("HospitalLabQcMaterials");
				e.Property(x => x.Lot).HasMaxLength(40);
				e.Property(x => x.Mean).HasPrecision(18, 4); e.Property(x => x.Sd).HasPrecision(18, 4);
				e.HasIndex(x => new { x.CompanyId, x.AnalyteId, x.Lot, x.Level }).IsUnique();
			});
			b.Entity<HospitalLabQcRun>(e =>
			{
				e.ToTable("HospitalLabQcRuns");
				e.Property(x => x.Value).HasPrecision(18, 4);
				e.Property(x => x.Status).HasMaxLength(10); e.Property(x => x.Violations).HasMaxLength(60); e.Property(x => x.Action).HasMaxLength(300);
				e.HasIndex(x => new { x.CompanyId, x.MaterialId, x.At });
			});
		}
	}
}
