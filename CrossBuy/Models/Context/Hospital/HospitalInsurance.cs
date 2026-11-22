using Microsoft.EntityFrameworkCore;

namespace CrossBuy.Models.Context.Hospital
{
	// ==========================================================================================
	// HOSPITAL INSURANCE & CLAIMS (unit H11).
	//
	// A PAYER is a contract — an insurer, a corporate account or a government programme — with its co-pay
	// (a percentage of the covered amount, capped per bill), its price list (H7) and the service categories
	// that need the payer's prior approval. A patient is covered through a POLICY with that payer (member
	// number, class, validity); eligibility is the policy AND the contract being valid on the day.
	// A PRE-AUTHORISATION records the payer's answer for one service before it is given. A CLAIM is one
	// closed bill of an insured patient, split into what the payer owes and the patient's share; claims are
	// grouped into a monthly BATCH per payer, submitted, and the payer's remittance accepts or rejects each
	// LINE (with its code and reason); a rejected line can be resubmitted with a justification.
	// CrossBuy/deploy/sql/hospital_h11_insurance.sql builds the tables (applied to CrossBuyDB2 2026-11-03).
	// ==========================================================================================

	public static class HospitalPayerKind
	{
		public const string Insurer = "Insurer";
		public const string Corporate = "Corporate";
		public const string Government = "Government";
		public static readonly IReadOnlyList<string> All = new[] { Insurer, Corporate, Government };
	}

	public static class HospitalPreauthStatus
	{
		public const string Pending = "Pending";
		public const string Approved = "Approved";
		public const string Rejected = "Rejected";
		public static readonly IReadOnlyList<string> All = new[] { Pending, Approved, Rejected };
	}

	public static class HospitalClaimBatchStatus
	{
		public const string Draft = "Draft";
		public const string Submitted = "Submitted";
		public const string Partial = "Partial";
		public const string Paid = "Paid";
		public const string Rejected = "Rejected";
		public static readonly IReadOnlyList<string> All = new[] { Draft, Submitted, Partial, Paid, Rejected };
	}

	public static class HospitalClaimLineStatus
	{
		public const string Pending = "Pending";
		public const string Accepted = "Accepted";
		public const string Rejected = "Rejected";
		/// A rejected line sent again in a later batch, with its justification.
		public const string Resubmitted = "Resubmitted";
		public static readonly IReadOnlyList<string> All = new[] { Pending, Accepted, Rejected, Resubmitted };
	}

	public class HospitalPayer
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public string Code { get; set; } = "";
		public string Name { get; set; } = "";
		public string NameEn { get; set; } = "";
		public string Kind { get; set; } = HospitalPayerKind.Insurer;
		/// The member classes the contract knows (e.g. "VIP · A · B").
		public string? Classes { get; set; }
		/// The contract's discount, in percent — taken off the main list's prices when the contract has no list
		/// of its own (a payer's own list has its discount built in, so it is not applied twice).
		public decimal DiscountPct { get; set; }
		/// The patient's share of the covered amount, in percent.
		public decimal CopayPct { get; set; }
		/// The most a patient pays of a covered bill (0 = no cap), in the bill's currency.
		public decimal CopayCap { get; set; }
		/// The H7 price list the contract's patients are billed on (null = the main list).
		public int? PriceListId { get; set; }
		/// Comma-separated service categories that need the payer's prior approval (e.g. "Radiology,Procedure").
		public string? PreauthCategories { get; set; }
		public DateTime ValidFrom { get; set; }
		public DateTime? ValidTo { get; set; }
		public bool IsActive { get; set; } = true;
	}

	public class HospitalPatientPolicy
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public int PatientId { get; set; }
		public int PayerId { get; set; }
		public string PolicyNo { get; set; } = "";
		public string? MemberClass { get; set; }
		public DateTime ValidFrom { get; set; }
		public DateTime? ValidTo { get; set; }
		public bool IsActive { get; set; } = true;
		public DateTime CreatedAt { get; set; }
		public int? CreatedByEmployeeId { get; set; }
	}

	public class HospitalPreauth
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public string RequestNo { get; set; } = "";
		public int PatientId { get; set; }
		public int PayerId { get; set; }
		public int PolicyId { get; set; }
		public int ServiceId { get; set; }
		public decimal Amount { get; set; }
		public string Status { get; set; } = HospitalPreauthStatus.Pending;
		public string? ApprovalRef { get; set; }
		public string? Reason { get; set; }
		public DateTime RequestedAt { get; set; }
		public int? RequestedByEmployeeId { get; set; }
		public DateTime? DecidedAt { get; set; }
		public int? DecidedByEmployeeId { get; set; }
	}

	public class HospitalClaimBatch
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public string BatchNo { get; set; } = "";
		public int PayerId { get; set; }
		/// The month the batch claims, "yyyy-MM".
		public string Period { get; set; } = "";
		public string Status { get; set; } = HospitalClaimBatchStatus.Draft;
		public DateTime CreatedAt { get; set; }
		public int? CreatedByEmployeeId { get; set; }
		public DateTime? SubmittedAt { get; set; }
		public int? SubmittedByEmployeeId { get; set; }
		public DateTime? SettledAt { get; set; }
	}

	public class HospitalClaim
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public int BatchId { get; set; }
		public int BillId { get; set; }
		public int PatientId { get; set; }
		public int PolicyId { get; set; }
		public decimal Gross { get; set; }
		public decimal PatientShare { get; set; }
		public decimal InsurerShare { get; set; }
	}

	public class HospitalClaimLine
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public int ClaimId { get; set; }
		public int? BillLineId { get; set; }
		public string Code { get; set; } = "";
		public string Description { get; set; } = "";
		public string DescriptionEn { get; set; } = "";
		public string Category { get; set; } = "";
		public decimal Amount { get; set; }
		/// What the payer is asked to pay for this line.
		public decimal InsurerAmount { get; set; }
		public int? PreauthId { get; set; }
		/// The line needed prior approval and none was approved — it goes out flagged.
		public bool MissingPreauth { get; set; }
		public string Status { get; set; } = HospitalClaimLineStatus.Pending;
		public decimal? AcceptedAmount { get; set; }
		public string? RejectCode { get; set; }
		public string? RejectReason { get; set; }
		public DateTime? DecidedAt { get; set; }
		public string? Justification { get; set; }
		/// On a resubmitted line: the line it resubmits.
		public int? ResubmitsLineId { get; set; }
		/// H82: a recoupment — after accepting (and often paying) the line, the payer takes part or all of it back after an
		/// audit. Once per line; never more than was accepted.
		public decimal? RecoupedAmount { get; set; }
		public DateTime? RecoupedAt { get; set; }
		public string? RecoupReason { get; set; }
		public int? RecoupedByEmployeeId { get; set; }
	}

	public static class HospitalInsuranceModel
	{
		public static void Configure(ModelBuilder b)
		{
			HospitalClaimExchangeModel.Configure(b);   // H200: the batch's electronic files
			HospitalClaimSheetModel.Configure(b);   // H210: a payer's claim sheet (the TPA's spreadsheet)
			HospitalDenialModel.Configure(b);   // H240: the denial work-queue
			HospitalPayerGatewayModel.Configure(b);   // H242: payer gateways and FHIR eligibility / pre-authorisation messages
			b.Entity<HospitalPayer>(e =>
			{
				e.ToTable("HospitalPayers");   // no DbSet: without this EF would look for dbo.HospitalPayer
				e.Property(x => x.CopayPct).HasPrecision(9, 4);
				e.Property(x => x.CopayCap).HasPrecision(18, 4);
				e.HasIndex(x => new { x.CompanyId, x.Code }).IsUnique();
			});
			b.Entity<HospitalPatientPolicy>(e =>
			{
				e.ToTable("HospitalPatientPolicies");   // no DbSet: without this EF would look for dbo.HospitalPatientPolicy
				e.HasOne<HospitalPayer>().WithMany().HasForeignKey(x => x.PayerId).OnDelete(DeleteBehavior.NoAction);
				e.HasIndex(x => new { x.CompanyId, x.PatientId });
				e.HasIndex(x => new { x.CompanyId, x.PayerId, x.PolicyNo }).IsUnique();
			});
			b.Entity<HospitalPreauth>(e =>
			{
				e.ToTable("HospitalPreauths");   // no DbSet: without this EF would look for dbo.HospitalPreauth
				e.Property(x => x.Amount).HasPrecision(18, 4);
				e.HasIndex(x => new { x.CompanyId, x.RequestNo }).IsUnique();
				e.HasIndex(x => new { x.CompanyId, x.PatientId, x.ServiceId });
				// one waiting request per patient and service: a twin sent at the same moment is refused
				e.HasIndex(x => new { x.CompanyId, x.PatientId, x.ServiceId }, "UX_HospitalPreauths_Pending").IsUnique().HasFilter("[Status] = 'Pending'");
			});
			b.Entity<HospitalClaimBatch>(e =>
			{
				e.ToTable("HospitalClaimBatches");   // no DbSet: without this EF would look for dbo.HospitalClaimBatch
				e.HasOne<HospitalPayer>().WithMany().HasForeignKey(x => x.PayerId).OnDelete(DeleteBehavior.NoAction);
				e.HasIndex(x => new { x.CompanyId, x.BatchNo }).IsUnique();
				// one draft per payer and month, so two builds at the same moment meet in the same batch
				e.HasIndex(x => new { x.CompanyId, x.PayerId, x.Period }, "UX_HospitalClaimBatches_Draft").IsUnique().HasFilter("[Status] = 'Draft'");
			});
			b.Entity<HospitalClaim>(e =>
			{
				e.ToTable("HospitalClaims");   // no DbSet: without this EF would look for dbo.HospitalClaim
				foreach (var p in new[] { nameof(HospitalClaim.Gross), nameof(HospitalClaim.PatientShare), nameof(HospitalClaim.InsurerShare) }) e.Property(p).HasPrecision(18, 4);
				e.HasOne<HospitalClaimBatch>().WithMany().HasForeignKey(x => x.BatchId).OnDelete(DeleteBehavior.NoAction);
				// a bill is claimed once per batch; a resubmitted line brings it into a later batch again
				e.HasIndex(x => new { x.CompanyId, x.BatchId, x.BillId }).IsUnique();
				e.HasIndex(x => new { x.CompanyId, x.BillId });
			});
			b.Entity<HospitalClaimLine>(e =>
			{
				e.ToTable("HospitalClaimLines");   // no DbSet: without this EF would look for dbo.HospitalClaimLine
				foreach (var p in new[] { nameof(HospitalClaimLine.Amount), nameof(HospitalClaimLine.InsurerAmount), nameof(HospitalClaimLine.AcceptedAmount) }) e.Property(p).HasPrecision(18, 4);
				e.Property(x => x.RecoupedAmount).HasPrecision(18, 4);   // H82
				e.Property(x => x.RecoupReason).HasMaxLength(300);
				e.HasOne<HospitalClaim>().WithMany().HasForeignKey(x => x.ClaimId).OnDelete(DeleteBehavior.NoAction);
				e.HasIndex(x => new { x.CompanyId, x.ClaimId });
				// a rejected line is resubmitted once
				e.HasIndex(x => x.ResubmitsLineId, "UX_HospitalClaimLines_Resubmits").IsUnique().HasFilter("[ResubmitsLineId] IS NOT NULL");
			});
		}
	}
}
