using Microsoft.EntityFrameworkCore;

namespace CrossBuy.Models.Context.Hospital
{
	// ==========================================================================================
	// HOSPITAL H138 — the unit-dose cart fill (the leading systems' "fill list"). For one ward and the next 24 hours, every
	// active medication order of every admitted patient becomes:
	//   · its scheduled doses in the window (an as-needed order gets a standard supply);
	//   · the units each dose takes, from the written dose and the formulary's strength;
	//   · one medication delivery (H21), reviewed and prepared at once. The stock leaves the pharmacy store FEFO, and the
	//     dose is charged when it is given (MAR), returned if it never is (H131).
	// A medicine the ward's cabinet stocks (it has a par) is taken from the cabinet, not filled. An order with a delivery
	// already on its way is not filled twice.
	// This table keeps one row per fill: the ward, the window, what was sent and what was skipped.
	// CrossBuy/deploy/sql/hospital_h138_cart_fill.sql builds it.
	// ==========================================================================================

	public class HospitalCartFill
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public string FillNo { get; set; } = "";
		public int WardId { get; set; }
		public DateTime WindowStart { get; set; }
		public DateTime WindowEnd { get; set; }
		public int Patients { get; set; }
		public int Deliveries { get; set; }
		public decimal Units { get; set; }
		public int Skipped { get; set; }
		public DateTime DoneAt { get; set; }
		public int? DoneByEmployeeId { get; set; }
	}

	public static class HospitalCartFillModel
	{
		public static void Configure(ModelBuilder b)
		{
			b.Entity<HospitalCartFill>(e =>
			{
				e.ToTable("HospitalCartFills");   // no DbSet: it names its table
				e.Property(x => x.FillNo).HasMaxLength(20);
				e.Property(x => x.Units).HasPrecision(18, 4);
				e.HasIndex(x => new { x.CompanyId, x.FillNo }).IsUnique();
				e.HasIndex(x => new { x.CompanyId, x.WardId, x.DoneAt });
			});
		}
	}
}
