using Microsoft.EntityFrameworkCore;

namespace CrossBuy.Models.Context.Hospital
{
	// ==========================================================================================
	// HOSPITAL H64 — RESTRICTED FILES and BREAK-GLASS access (the prototype's "break-glass access"). The administrator
	// restricts a patient's file (a public figure, a member of staff, a sensitive case) with the reason. A restricted
	// file opens for the administrator, for whoever holds "view confidential", and for the patient's care team (the
	// attending doctor of an open stay, the doctor of a visit in the last 30 days). Anyone else meets a gate: in an
	// emergency they write why, and the file opens to them for 60 minutes — recorded, and reviewed afterwards by the
	// administrator (justified or not). A break-glass is never deleted; a restriction is lifted, not erased.
	// CrossBuy/deploy/sql/hospital_h64_break_glass.sql builds the tables.
	// ==========================================================================================
	public static class HospitalBreakGlassOutcome
	{
		public const string Justified = "Justified";
		public const string Unjustified = "Unjustified";
		public static readonly IReadOnlyList<string> All = new[] { Justified, Unjustified };
	}

	public class HospitalRestrictedFile
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public int PatientId { get; set; }
		public string Reason { get; set; } = "";
		public DateTime RestrictedAt { get; set; }
		public int RestrictedByEmployeeId { get; set; }
		public DateTime? LiftedAt { get; set; }
		public int? LiftedByEmployeeId { get; set; }
	}

	public class HospitalBreakGlass
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public int PatientId { get; set; }
		public int EmployeeId { get; set; }
		public string Reason { get; set; } = "";
		public DateTime OpenedAt { get; set; }
		public DateTime ExpiresAt { get; set; }
		public DateTime? ReviewedAt { get; set; }
		public int? ReviewedByEmployeeId { get; set; }
		public string? Outcome { get; set; }
		public string? ReviewNote { get; set; }
	}

	public static class HospitalRestrictedAccessModel
	{
		public static void Configure(ModelBuilder b)
		{
			HospitalPrivacyModel.Configure(b);   // H255: behavioural privacy alerts
			b.Entity<HospitalRestrictedFile>(e =>
			{
				e.ToTable("HospitalRestrictedFiles");   // no DbSet: each names its table
				e.Property(x => x.Reason).HasMaxLength(300);
				e.HasIndex(x => new { x.CompanyId, x.PatientId });
			});
			b.Entity<HospitalBreakGlass>(e =>
			{
				e.ToTable("HospitalBreakGlass");
				e.Property(x => x.Reason).HasMaxLength(500);
				e.Property(x => x.Outcome).HasMaxLength(12);
				e.Property(x => x.ReviewNote).HasMaxLength(300);
				e.HasIndex(x => new { x.CompanyId, x.PatientId, x.EmployeeId });
				e.HasIndex(x => new { x.CompanyId, x.ReviewedAt });
			});
		}
	}
}
