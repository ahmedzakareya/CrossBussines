using Microsoft.EntityFrameworkCore;

namespace CrossBuy.Models.Context.Hospital
{
	// ==========================================================================================
	// HOSPITAL — the blood bank (unit H41), as the approved prototype draws it (#/bloodbank): the units on the shelf per
	// blood group (low below the safety level), and the transfusion requests with their cross-match. A UNIT is one bag,
	// received by its number with its group, product and expiry; it is available, reserved for a request, issued, or
	// discarded. A REQUEST is the doctor's (patient, product, how many units); the laboratory cross-matches it and, when
	// compatible, the units are reserved — compatible by group, nearest expiry first — and then issued.
	// CrossBuy/deploy/sql/hospital_h41_blood_bank.sql.
	// ==========================================================================================

	public static class HospitalBloodProduct
	{
		public const string WholeBlood = "WholeBlood";
		public const string PackedCells = "PackedCells";
		public const string Plasma = "Plasma";
		public const string Platelets = "Platelets";
		public static readonly IReadOnlyList<string> All = new[] { WholeBlood, PackedCells, Plasma, Platelets };
	}

	public static class HospitalBloodUnitStatus
	{
		public const string Available = "Available";
		public const string Reserved = "Reserved";
		public const string Issued = "Issued";
		public const string Discarded = "Discarded";
		public static readonly IReadOnlyList<string> All = new[] { Available, Reserved, Issued, Discarded };
	}

	public static class HospitalTransfusionStatus
	{
		public const string Requested = "Requested";
		public const string CrossMatching = "CrossMatching";
		public const string Compatible = "Compatible";
		public const string Incompatible = "Incompatible";
		public const string Issued = "Issued";
		public const string Cancelled = "Cancelled";
		public static readonly IReadOnlyList<string> All = new[] { Requested, CrossMatching, Compatible, Incompatible, Issued, Cancelled };
	}

	public class HospitalBloodUnit
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		/// The bag's number, as printed on it.
		public string UnitNo { get; set; } = "";
		public string BloodGroup { get; set; } = "";
		public string Product { get; set; } = HospitalBloodProduct.PackedCells;
		public DateTime ExpiresOn { get; set; }
		public string Status { get; set; } = HospitalBloodUnitStatus.Available;
		/// The request it is reserved for or was issued to.
		public int? RequestId { get; set; }
		public string? Note { get; set; }
		public DateTime ReceivedAt { get; set; }
		public int? ReceivedByEmployeeId { get; set; }
		public DateTime? IssuedAt { get; set; }
		public DateTime? DiscardedAt { get; set; }
	}

	public class HospitalTransfusionRequest
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public int PatientId { get; set; }
		public string Product { get; set; } = HospitalBloodProduct.PackedCells;
		public int Units { get; set; }
		public string Status { get; set; } = HospitalTransfusionStatus.Requested;
		public string? Note { get; set; }
		/// Why the cross-match failed or the request was cancelled.
		public string? Reason { get; set; }
		public DateTime RequestedAt { get; set; }
		public int? RequestedByEmployeeId { get; set; }
		public DateTime? MatchedAt { get; set; }
		public int? MatchedByEmployeeId { get; set; }
		public DateTime? IssuedAt { get; set; }
		public int? IssuedByEmployeeId { get; set; }
		/// H51: the bill line the issue was charged as (null = not charged: no service named for the product).
		public int? BillLineId { get; set; }
	}

	/// H51: the catalogue service a blood product is charged as (one per company and product; the billing settings).
	public class HospitalBloodProductService
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public string Product { get; set; } = HospitalBloodProduct.PackedCells;
		public int ServiceId { get; set; }
		public DateTime UpdatedAt { get; set; }
		public int? UpdatedByEmployeeId { get; set; }
	}

	public static class HospitalBloodBankModel
	{
		public static void Configure(ModelBuilder b)
		{
			b.Entity<HospitalBloodUnit>(e =>
			{
				e.ToTable("HospitalBloodUnits");   // no DbSet: each names its table
				e.Property(x => x.UnitNo).HasMaxLength(40);
				e.Property(x => x.BloodGroup).HasMaxLength(3);
				e.Property(x => x.Product).HasMaxLength(20);
				e.Property(x => x.Status).HasMaxLength(20);
				e.Property(x => x.Note).HasMaxLength(300);
				e.Property(x => x.ExpiresOn).HasColumnType("date");
				e.HasIndex(x => new { x.CompanyId, x.UnitNo }).IsUnique();
			});
			b.Entity<HospitalTransfusionRequest>(e =>
			{
				e.ToTable("HospitalTransfusionRequests");
				e.Property(x => x.Product).HasMaxLength(20);
				e.Property(x => x.Status).HasMaxLength(20);
				e.Property(x => x.Note).HasMaxLength(500);
				e.Property(x => x.Reason).HasMaxLength(300);
			});
			b.Entity<HospitalBloodProductService>(e =>
			{
				e.ToTable("HospitalBloodProductServices");   // H51 (hospital_h51_blood_billing.sql)
				e.Property(x => x.Product).HasMaxLength(20);
				e.HasIndex(x => new { x.CompanyId, x.Product }).IsUnique();
			});
		}
	}
}
