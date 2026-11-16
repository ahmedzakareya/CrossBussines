using Microsoft.EntityFrameworkCore;

namespace CrossBuy.Models.Context.Hospital
{
	// ==========================================================================================
	// HOSPITAL H137 — counting the controlled drugs, the way the diversion guidelines ask (ASHP 2022).
	//   · A BLIND COUNT: the counter enters what is on the shelf without seeing what the inventory expects. A second person
	//     witnesses it with their own password. The expected balance is taken at that moment and kept beside the count.
	//   · A difference opens a DISCREPANCY. It must be closed within the shift (12 hours), and an open one past that is
	//     overdue. It closes with a reason:
	//       - MISCOUNT: a recount found the expected quantity;
	//       - DOCUMENTATION: a movement was done but not recorded, and the note says which;
	//       - LOSS: the units are gone, written off through the inventory with a witness (WOF-…).
	// CrossBuy/deploy/sql/hospital_h137_controlled_counts.sql builds the table.
	// ==========================================================================================

	public static class HospitalCountStatus
	{
		public const string Matched = "Matched", Open = "Open", Resolved = "Resolved";
		public static readonly IReadOnlyList<string> All = new[] { Open, Matched, Resolved };
	}

	public static class HospitalCountResolution
	{
		public const string Miscount = "Miscount", Documentation = "Documentation", Loss = "Loss";
		public static readonly IReadOnlyList<string> All = new[] { Miscount, Documentation, Loss };
	}

	public class HospitalControlledCount
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public string CountNo { get; set; } = "";
		public int WarehouseId { get; set; }
		public int ItemId { get; set; }
		/// What the inventory held when the count was entered (never shown to the counter), what was counted, and the gap.
		public decimal Expected { get; set; }
		public decimal Counted { get; set; }
		public decimal Difference { get; set; }
		public string Status { get; set; } = HospitalCountStatus.Matched;
		public DateTime CountedAt { get; set; }
		public int? CountedByEmployeeId { get; set; }
		public int? WitnessEmployeeId { get; set; }
		public string? Resolution { get; set; }
		public string? ResolutionNote { get; set; }
		public decimal? Recounted { get; set; }
		public DateTime? ResolvedAt { get; set; }
		public int? ResolvedByEmployeeId { get; set; }
		public int? ResolutionWitnessEmployeeId { get; set; }
		/// The inventory's write-off when the gap was a loss.
		public string? DocumentNo { get; set; }
	}

	public static class HospitalControlledCountModel
	{
		public static void Configure(ModelBuilder b)
		{
			b.Entity<HospitalControlledCount>(e =>
			{
				e.ToTable("HospitalControlledCounts");   // no DbSet: it names its table
				e.Property(x => x.CountNo).HasMaxLength(20);
				foreach (var p in new[] { nameof(HospitalControlledCount.Expected), nameof(HospitalControlledCount.Counted), nameof(HospitalControlledCount.Difference), nameof(HospitalControlledCount.Recounted) })
					e.Property(p).HasPrecision(18, 4);
				e.Property(x => x.Status).HasMaxLength(10);
				e.Property(x => x.Resolution).HasMaxLength(20);
				e.Property(x => x.ResolutionNote).HasMaxLength(400);
				e.Property(x => x.DocumentNo).HasMaxLength(40);
				e.HasIndex(x => new { x.CompanyId, x.CountNo }).IsUnique();
				e.HasIndex(x => new { x.CompanyId, x.WarehouseId, x.ItemId, x.CountedAt });
				e.HasIndex(x => new { x.CompanyId, x.Status });
			});
		}
	}
}
