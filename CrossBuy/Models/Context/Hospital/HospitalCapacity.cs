using Microsoft.EntityFrameworkCore;

namespace CrossBuy.Models.Context.Hospital
{
	// ==========================================================================================
	// HOSPITAL H239 — the CAPACITY CENTRE's one stored fact: the team's DISCHARGE PLAN for an admission — the expected
	// day, how sure (likely / possible), and what stands in the way (barriers). Everything else on the centre is read:
	// beds and their status (H3), who lies where (bed stays), emergency patients at disposition, the admissions history
	// that gives the expected arrivals and the length of stay when no plan is set.
	// deploy/sql/hospital_h239_capacity.sql builds the table.
	// ==========================================================================================
	public static class HospitalDischargeConfidence
	{
		public const string Likely = "Likely", Possible = "Possible";
		public static readonly IReadOnlyList<string> All = new[] { Likely, Possible };
	}

	public static class HospitalDischargeBarrier
	{
		public const string Results = "Results", Consult = "Consult", Pharmacy = "Pharmacy", Transport = "Transport", Family = "Family", Insurance = "Insurance", Placement = "Placement";
		public static readonly IReadOnlyList<string> All = new[] { Results, Consult, Pharmacy, Transport, Family, Insurance, Placement };
	}

	public class HospitalDischargePlan
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public int AdmissionId { get; set; }
		public int PatientId { get; set; }
		/// The ward-clock day the team expects the discharge.
		public DateTime ExpectedDate { get; set; }
		public string Confidence { get; set; } = HospitalDischargeConfidence.Likely;
		/// Barrier codes, comma-separated.
		public string? Barriers { get; set; }
		public string? Note { get; set; }
		public DateTime UpdatedAt { get; set; }
		public int? UpdatedByEmployeeId { get; set; }
	}

	public static class HospitalCapacityModel
	{
		public static void Configure(ModelBuilder b)
		{
			b.Entity<HospitalDischargePlan>(e =>
			{
				e.ToTable("HospitalDischargePlans");
				e.Property(x => x.ExpectedDate).HasColumnType("date");
				e.Property(x => x.Confidence).HasMaxLength(10); e.Property(x => x.Barriers).HasMaxLength(200); e.Property(x => x.Note).HasMaxLength(300);
				e.HasIndex(x => new { x.CompanyId, x.AdmissionId }).IsUnique();
				e.HasIndex(x => new { x.CompanyId, x.PatientId });
			});
		}
	}
}
