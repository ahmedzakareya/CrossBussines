using Microsoft.EntityFrameworkCore;

namespace CrossBuy.Models.Context.Hospital
{
	// ==========================================================================================
	// HOSPITAL H145 — partial dispensing. When the store holds less than the prescription asks, the pharmacist gives what
	// there is and the rest is OWED to the patient (status Partial); dispensing again later gives what is owed. Each time a
	// line leaves the store is one FILL: how much, at what cost, from which batches, on which bill line. The line keeps the
	// running total (DispensedQty) the screens read; the fills keep the history a return (H146) needs to credit exactly what
	// was charged.
	// CrossBuy/deploy/sql/hospital_h145_partial_dispense.sql builds it and widens the status rule.
	// ==========================================================================================

	public class HospitalDispenseFill
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public int DispenseId { get; set; }
		public int LineId { get; set; }
		public decimal Quantity { get; set; }
		public decimal? UnitCost { get; set; }
		public decimal? UnitPrice { get; set; }
		public string? Batches { get; set; }
		public int? BillLineId { get; set; }
		public DateTime At { get; set; }
		public int? ByEmployeeId { get; set; }
	}

	public static class HospitalDispenseFillModel
	{
		public static void Configure(ModelBuilder b)
		{
			b.Entity<HospitalDispenseFill>(e =>
			{
				e.ToTable("HospitalDispenseFills");   // no DbSet: it names its table
				e.Property(x => x.Quantity).HasPrecision(18, 4);
				e.Property(x => x.UnitCost).HasPrecision(18, 4);
				e.Property(x => x.UnitPrice).HasPrecision(18, 4);
				e.Property(x => x.Batches).HasMaxLength(400);
				e.HasIndex(x => new { x.CompanyId, x.DispenseId });
			});
		}
	}
}
