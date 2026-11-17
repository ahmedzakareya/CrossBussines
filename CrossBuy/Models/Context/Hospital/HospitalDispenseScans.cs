using Microsoft.EntityFrameworkCore;

namespace CrossBuy.Models.Context.Hospital
{
	// ==========================================================================================
	// HOSPITAL H150 — a pack scanned at the pharmacy counter (GS1 DataMatrix: GTIN, expiry, batch, serial). One row per
	// pack. A scan is checked before it is kept (the right medicine for this prescription, not expired, not recalled, a
	// batch the store holds, a serial never scanned before); dispensing then issues the scanned packs FROM THEIR OWN BATCH,
	// so the batch on record is the one in the patient's hands. FillId = the fill that took it (null = scanned, not yet given).
	// CrossBuy/deploy/sql/hospital_h150_dispense_scans.sql builds it.
	// ==========================================================================================
	public class HospitalDispenseScan
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public int DispenseId { get; set; }
		public int LineId { get; set; }
		public int ItemId { get; set; }
		public string Gtin { get; set; } = "";
		public string? BatchNo { get; set; }
		public DateTime? Expiry { get; set; }
		public string? Serial { get; set; }
		public int? FillId { get; set; }
		public DateTime At { get; set; }
		public int? ByEmployeeId { get; set; }
	}

	public static class HospitalDispenseScanModel
	{
		public static void Configure(ModelBuilder b)
		{
			b.Entity<HospitalDispenseScan>(e =>
			{
				e.ToTable("HospitalDispenseScans");   // no DbSet: it names its table
				e.Property(x => x.Gtin).HasMaxLength(14);
				e.Property(x => x.BatchNo).HasMaxLength(20);
				e.Property(x => x.Serial).HasMaxLength(20);
				e.HasIndex(x => new { x.CompanyId, x.DispenseId });
				e.HasIndex(x => new { x.CompanyId, x.Gtin, x.Serial });
			});
		}
	}
}
