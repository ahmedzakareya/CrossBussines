using Microsoft.EntityFrameworkCore;

namespace CrossBuy.Models.Context.Hospital
{
	// ==========================================================================================
	// HOSPITAL H241 — DRG GROUPING. The groups are the facility's own table, entered by the administrator from the
	// payer's or the ministry's published grouper (no licensed grouper is bundled): each group names the ICD-10 prefixes
	// of its principal diagnosis, whether it is surgical (an operation done in the stay), an optional age band, its
	// relative WEIGHT and mean length of stay. A discharged stay is coded with its principal diagnosis; the grouper picks
	// the group (the longest matching prefix, surgical before medical), the coder may override, and the weight is frozen
	// on the assignment so a later table change never rewrites a closed case-mix.
	// deploy/sql/hospital_h241_drg.sql builds the tables.
	// ==========================================================================================
	public class HospitalDrg
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public string Code { get; set; } = "";
		public string Name { get; set; } = "";
		public string? NameEn { get; set; }
		/// ICD-10 prefixes of the principal diagnosis, comma-separated ("I21,I22").
		public string DxPrefixes { get; set; } = "";
		/// True: only a stay with an operation; false: only one without; null: either.
		public bool? Surgical { get; set; }
		public int? MinAge { get; set; }
		public int? MaxAge { get; set; }
		public decimal Weight { get; set; }
		public decimal? MeanLos { get; set; }
		public bool IsActive { get; set; } = true;
		public DateTime UpdatedAt { get; set; }
	}

	public class HospitalDrgAssignment
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public int AdmissionId { get; set; }
		public int PatientId { get; set; }
		public string PrincipalDx { get; set; } = "";
		public bool Surgical { get; set; }
		/// Null when no group matched (ungroupable).
		public int? DrgId { get; set; }
		public decimal Weight { get; set; }
		public int LosDays { get; set; }
		/// The coder chose the group rather than the grouper.
		public bool Manual { get; set; }
		public DateTime AssignedAt { get; set; }
		public int? AssignedByEmployeeId { get; set; }
	}

	public static class HospitalDrgModel
	{
		public static void Configure(ModelBuilder b)
		{
			b.Entity<HospitalDrg>(e =>
			{
				e.ToTable("HospitalDrgs");
				e.Property(x => x.Code).HasMaxLength(20); e.Property(x => x.Name).HasMaxLength(200); e.Property(x => x.NameEn).HasMaxLength(200);
				e.Property(x => x.DxPrefixes).HasMaxLength(500); e.Property(x => x.Weight).HasPrecision(8, 4); e.Property(x => x.MeanLos).HasPrecision(5, 1);
				e.HasIndex(x => new { x.CompanyId, x.Code }).IsUnique();
			});
			b.Entity<HospitalDrgAssignment>(e =>
			{
				e.ToTable("HospitalDrgAssignments");
				e.Property(x => x.PrincipalDx).HasMaxLength(10); e.Property(x => x.Weight).HasPrecision(8, 4);
				e.HasIndex(x => new { x.CompanyId, x.AdmissionId }).IsUnique();
				e.HasIndex(x => new { x.CompanyId, x.PatientId });
			});
		}
	}
}
