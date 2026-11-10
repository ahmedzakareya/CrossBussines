using Microsoft.EntityFrameworkCore;

namespace CrossBuy.Models.Context.Hospital
{
	// ==========================================================================================
	// HOSPITAL — insurance accounting (unit H20, the owner's rulings D2 and G1 on H18).
	//
	// D2: what a payer owes sits in its OWN receivable: the payer's override, else the company's payer receivable.
	//     Closing an insured bill is REFUSED while neither is mapped — never the patients' account, never a
	//     hard-coded number.
	// G1: the payer's answers post, each once (keyed on the claim line and the event, or the batch and the
	//     remittance reference), carrying the bill, batch and original journal:
	//       · accepted in full: nothing (the receivable stands);
	//       · accepted short (contractual adjustment): Dr claim adjustments / Cr payer receivable;
	//       · rejected: Dr claim rejections / Cr payer receivable;
	//       · a rejected line accepted on resubmission: Dr payer receivable / Cr claim rejections (the recovery);
	//       · a remittance, full or partial: Dr bank / Cr payer receivable.
	//     Every event REFUSES while its account is unmapped, and the answer is then not recorded at all.
	// The mappings are tables of their own (no column added to an existing table), so a database without them
	// reads as "not mapped" and stays closed — nothing else stops working.
	// CrossBuy/deploy/sql/hospital_h20_claim_accounting.sql builds the tables (authored, NOT applied).
	// ==========================================================================================

	public static class HospitalClaimEvent
	{
		public const string Adjustment = "Adjustment";
		public const string Rejection = "Rejection";
		public const string Recovery = "Recovery";
		public const string Remittance = "Remittance";
		public static readonly IReadOnlyList<string> All = new[] { Adjustment, Rejection, Recovery, Remittance };
	}

	/// The company's insurance accounts (one row per company).
	public class HospitalInsuranceAccounts
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public int? PayerReceivableAccountId { get; set; }
		public int? ClaimRejectionAccountId { get; set; }
		public int? ClaimAdjustmentAccountId { get; set; }
	}

	/// A payer's own receivable account, overriding the company's.
	public class HospitalPayerAccount
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public int PayerId { get; set; }
		public int ReceivableAccountId { get; set; }
	}

	/// One posted answer of a payer: what posted, for which line or batch, and the journal it made.
	public class HospitalClaimPosting
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public int BatchId { get; set; }
		/// The claim line answered (null for a remittance, which is the batch's).
		public int? ClaimLineId { get; set; }
		public string Event { get; set; } = HospitalClaimEvent.Adjustment;
		/// The remittance's own reference (the payer's payment advice); null for a line's event.
		public string? Reference { get; set; }
		public decimal Amount { get; set; }
		public int? BillId { get; set; }
		/// The bill's closing journal the payer's receivable came from.
		public int? OriginJournalEntryId { get; set; }
		public int JournalEntryId { get; set; }
		public DateTime PostedAt { get; set; }
		public int? PostedByEmployeeId { get; set; }
	}

	public static class HospitalClaimAccountingModel
	{
		public static void Configure(ModelBuilder b)
		{
			b.Entity<HospitalInsuranceAccounts>(e =>
			{
				e.ToTable("HospitalInsuranceAccounts");   // no DbSet: each names its table
				e.HasIndex(x => x.CompanyId).IsUnique();
			});
			b.Entity<HospitalPayerAccount>(e =>
			{
				e.ToTable("HospitalPayerAccounts");
				e.HasIndex(x => new { x.CompanyId, x.PayerId }).IsUnique();
			});
			b.Entity<HospitalClaimPosting>(e =>
			{
				e.ToTable("HospitalClaimPostings");
				e.Property(x => x.Amount).HasPrecision(18, 3);
				e.Property(x => x.Event).HasMaxLength(20);
				e.Property(x => x.Reference).HasMaxLength(60);
				// each answer posts once: a line's event, a batch's remittance reference
				e.HasIndex(x => new { x.ClaimLineId, x.Event }, "UX_HospitalClaimPostings_LineEvent").IsUnique().HasFilter("[ClaimLineId] IS NOT NULL");
				e.HasIndex(x => new { x.BatchId, x.Reference }, "UX_HospitalClaimPostings_Remittance").IsUnique().HasFilter("[Reference] IS NOT NULL");
				e.HasIndex(x => new { x.CompanyId, x.BatchId });
			});
		}
	}
}
