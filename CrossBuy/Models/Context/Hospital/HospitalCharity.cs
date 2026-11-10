using Microsoft.EntityFrameworkCore;

namespace CrossBuy.Models.Context.Hospital
{
	// ==========================================================================================
	// HOSPITAL H63 — CHARITY CASES (the prototype's "charity cases, patient statement"). A part of what a patient owes
	// on a CLOSED bill is waived by a charity FUND (the hospital's own charity fund, zakat, an outside donor…): the
	// billing desk requests it, the hospital administrator approves or refuses it (never the same person). Approving
	// posts the waiver — debit the fund's expense account, credit the patients' receivable — and the bill's amount due
	// falls by it everywhere (the bill, the cashier, the portal). A waived amount was never collected, so it never
	// counts toward a doctor's fee. Never edited or deleted: a request is approved or refused, once.
	// CrossBuy/deploy/sql/hospital_h63_charity.sql builds the tables.
	// ==========================================================================================
	public static class HospitalCharityStatus
	{
		public const string Requested = "Requested";
		public const string Approved = "Approved";
		public const string Refused = "Refused";
		public static readonly IReadOnlyList<string> All = new[] { Requested, Approved, Refused };
	}

	public class HospitalCharityFund
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public string Code { get; set; } = "";
		public string Name { get; set; } = "";
		public string NameEn { get; set; } = "";
		/// The expense account a waiver from this fund is debited to.
		public int? AccountId { get; set; }
		public bool IsActive { get; set; } = true;
		public DateTime UpdatedAt { get; set; }
		public int? UpdatedByEmployeeId { get; set; }
	}

	public class HospitalCharityGrant
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public string GrantNo { get; set; } = "";
		public int BillId { get; set; }
		public int PatientId { get; set; }
		public int FundId { get; set; }
		public decimal Amount { get; set; }
		public string Reason { get; set; } = "";
		public string Status { get; set; } = HospitalCharityStatus.Requested;
		public DateTime RequestedAt { get; set; }
		public int RequestedByEmployeeId { get; set; }
		public DateTime? DecidedAt { get; set; }
		public int? DecidedByEmployeeId { get; set; }
		public string? DecisionNote { get; set; }
		public int? JournalEntryId { get; set; }
	}

	public static class HospitalCharityModel
	{
		public static void Configure(ModelBuilder b)
		{
			b.Entity<HospitalCharityFund>(e =>
			{
				e.ToTable("HospitalCharityFunds");   // no DbSet: each names its table
				e.Property(x => x.Code).HasMaxLength(20);
				e.Property(x => x.Name).HasMaxLength(150);
				e.Property(x => x.NameEn).HasMaxLength(150);
				e.HasIndex(x => new { x.CompanyId, x.Code }).IsUnique();
			});
			b.Entity<HospitalCharityGrant>(e =>
			{
				e.ToTable("HospitalCharityGrants");
				e.Property(x => x.GrantNo).HasMaxLength(20);
				e.Property(x => x.Amount).HasColumnType("decimal(18,4)");
				e.Property(x => x.Reason).HasMaxLength(500);
				e.Property(x => x.Status).HasMaxLength(10);
				e.Property(x => x.DecisionNote).HasMaxLength(300);
				e.HasIndex(x => new { x.CompanyId, x.GrantNo }).IsUnique();
				e.HasIndex(x => new { x.CompanyId, x.BillId });
				e.HasIndex(x => new { x.CompanyId, x.PatientId });
			});
		}
	}
}
