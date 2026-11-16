using Microsoft.EntityFrameworkCore;

namespace CrossBuy.Models.Context.Hospital
{
	// ==========================================================================================
	// HOSPITAL H146 — a patient brings dispensed medicine back. The pharmacist inspects it; what can be sold again goes back
	// on the shelf and is taken off the patient's bill:
	//   · the stock returns to the store it left, movement by movement (newest first) with its own batch, expiry and unit
	//     cost, the cost-of-goods account as the counter — so the value comes back exactly;
	//   · the bill's pharmacy lines lose the returned quantity (newest fill first). Only an OPEN bill: a closed bill has
	//     posted its revenue and needs a credit note;
	//   · if the patient had already paid more than the bill now asks, the excess becomes a CREDIT on the patient's deposit
	//     balance (H143: kept for the next bill or refunded), through the returning person's open cashier shift.
	// One row per return; what a line can still return = dispensed − returned.
	// CrossBuy/deploy/sql/hospital_h146_dispense_returns.sql builds it.
	// ==========================================================================================

	public class HospitalDispenseReturn
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public int DispenseId { get; set; }
		public int LineId { get; set; }
		public decimal Quantity { get; set; }
		public string Reason { get; set; } = "";
		/// What came off the bill (quantity × the fill's price), before any insurer share.
		public decimal Credited { get; set; }
		/// The deposit the overpaid excess went to, when there was one.
		public int? DepositId { get; set; }
		public DateTime At { get; set; }
		public int? ByEmployeeId { get; set; }
	}

	public static class HospitalDispenseReturnModel
	{
		public static void Configure(ModelBuilder b)
		{
			b.Entity<HospitalDispenseReturn>(e =>
			{
				e.ToTable("HospitalDispenseReturns");   // no DbSet: it names its table
				e.Property(x => x.Quantity).HasPrecision(18, 4);
				e.Property(x => x.Credited).HasPrecision(18, 4);
				e.Property(x => x.Reason).HasMaxLength(300);
				e.HasIndex(x => new { x.CompanyId, x.DispenseId });
			});
		}
	}
}
