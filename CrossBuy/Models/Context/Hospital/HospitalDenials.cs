using Microsoft.EntityFrameworkCore;

namespace CrossBuy.Models.Context.Hospital
{
	// ==========================================================================================
	// HOSPITAL H240 — the DENIAL WORK-QUEUE. A payer's rejected claim line (H18) becomes a work item: its ROOT CAUSE
	// (suggested from the payer's code and reason, corrected by the biller), who owns it, the appeal deadline, and where
	// it stands (open, working, closed as resubmitted, or not pursued with the reason). The money stays with the claim
	// line and the ledger: nothing here posts — "not pursued" records a decision, the write-off is finance's.
	// deploy/sql/hospital_h240_denials.sql builds the table.
	// ==========================================================================================
	public static class HospitalDenialCause
	{
		public const string Authorization = "Authorization", Eligibility = "Eligibility", Coding = "Coding", Necessity = "Necessity",
			Documentation = "Documentation", Duplicate = "Duplicate", Timely = "Timely", Pricing = "Pricing", Other = "Other";
		public static readonly IReadOnlyList<string> All = new[] { Authorization, Eligibility, Coding, Necessity, Documentation, Duplicate, Timely, Pricing, Other };
	}

	public static class HospitalDenialStatus
	{
		public const string Open = "Open", Working = "Working", Resubmitted = "Resubmitted", NotPursued = "NotPursued";
		public static readonly IReadOnlyList<string> All = new[] { Open, Working, Resubmitted, NotPursued };
		public static bool Active(string s) => s is Open or Working;
	}

	public class HospitalDenialWork
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public int ClaimLineId { get; set; }
		public int PatientId { get; set; }
		public string RootCause { get; set; } = HospitalDenialCause.Other;
		public int? OwnerEmployeeId { get; set; }
		public DateTime DueDate { get; set; }
		public string Status { get; set; } = HospitalDenialStatus.Open;
		public string? Note { get; set; }
		public DateTime CreatedAt { get; set; }
		public DateTime UpdatedAt { get; set; }
		public int? UpdatedByEmployeeId { get; set; }
	}

	public static class HospitalDenialModel
	{
		public static void Configure(ModelBuilder b)
		{
			b.Entity<HospitalDenialWork>(e =>
			{
				e.ToTable("HospitalDenialWork");
				e.Property(x => x.RootCause).HasMaxLength(15); e.Property(x => x.Status).HasMaxLength(12); e.Property(x => x.Note).HasMaxLength(500);
				e.Property(x => x.DueDate).HasColumnType("date");
				e.HasIndex(x => new { x.CompanyId, x.ClaimLineId }).IsUnique();
				e.HasIndex(x => new { x.CompanyId, x.Status, x.DueDate });
				e.HasIndex(x => new { x.CompanyId, x.PatientId });
			});
		}
	}
}
