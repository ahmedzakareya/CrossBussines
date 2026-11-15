using Microsoft.EntityFrameworkCore;

namespace CrossBuy.Models.Context.Hospital
{
	// ==========================================================================================
	// HOSPITAL H135 — what leaves the pharmacy for good. An expired, damaged or recalled batch is either:
	//   · DESTROYED: through the inventory's own write-off (Dr write-off expense / Cr inventory), with a witness who
	//     confirms with their own password;
	//   · or RETURNED to its supplier: through the purchase return, a debit note against the supplier at cost.
	// One row per act: the batch, the store it left, the quantity and the value lost or credited, the document the
	// inventory or payables wrote (WOF-… / SC-… / DN-…), who did it and who witnessed it. A recall whose batch is all gone
	// closes itself on the act.
	// CrossBuy/deploy/sql/hospital_h135_disposals.sql builds the table.
	// ==========================================================================================

	public static class HospitalDisposalKind
	{
		public const string Destroyed = "Destroyed", Returned = "Returned";
		public static readonly IReadOnlyList<string> All = new[] { Destroyed, Returned };
	}

	public static class HospitalDisposalReason
	{
		public const string Expired = "Expired", Recalled = "Recalled", Damaged = "Damaged", NearExpiry = "NearExpiry";
		public static readonly IReadOnlyList<string> All = new[] { Expired, Recalled, Damaged, NearExpiry };
	}

	/// How a batch is destroyed.
	public static class HospitalDestructionMethod
	{
		public const string Incineration = "Incineration", Contractor = "Contractor", Chemical = "Chemical", Other = "Other";
		public static readonly IReadOnlyList<string> All = new[] { Contractor, Incineration, Chemical, Other };
	}

	public class HospitalStockDisposal
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public string DisposalNo { get; set; } = "";
		public string Kind { get; set; } = HospitalDisposalKind.Destroyed;
		public int ItemId { get; set; }
		public string BatchNo { get; set; } = "";
		public DateTime? Expiry { get; set; }
		public int WarehouseId { get; set; }
		public decimal Qty { get; set; }
		/// At cost: what was lost (destroyed) or credited by the supplier (returned).
		public decimal Value { get; set; }
		public string Reason { get; set; } = HospitalDisposalReason.Expired;
		public string? Method { get; set; }
		public int? VendorId { get; set; }
		/// The document the inventory or the payables wrote, and its id.
		public string? DocumentNo { get; set; }
		public int? DocumentId { get; set; }
		public int? RecallId { get; set; }
		public int? WitnessEmployeeId { get; set; }
		public int? DoneByEmployeeId { get; set; }
		public DateTime DoneAt { get; set; }
		public string? Note { get; set; }
	}

	public static class HospitalStockDisposalModel
	{
		public static void Configure(ModelBuilder b)
		{
			b.Entity<HospitalStockDisposal>(e =>
			{
				e.ToTable("HospitalStockDisposals");   // no DbSet: it names its table
				e.Property(x => x.DisposalNo).HasMaxLength(20);
				e.Property(x => x.Kind).HasMaxLength(20);
				e.Property(x => x.BatchNo).HasMaxLength(80);
				e.Property(x => x.Qty).HasPrecision(18, 4);
				e.Property(x => x.Value).HasPrecision(18, 4);
				e.Property(x => x.Reason).HasMaxLength(20);
				e.Property(x => x.Method).HasMaxLength(20);
				e.Property(x => x.DocumentNo).HasMaxLength(40);
				e.Property(x => x.Note).HasMaxLength(400);
				e.HasIndex(x => new { x.CompanyId, x.DisposalNo }).IsUnique();
				e.HasIndex(x => new { x.CompanyId, x.DoneAt });
			});
		}
	}
}
