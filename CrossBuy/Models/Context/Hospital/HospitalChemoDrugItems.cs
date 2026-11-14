using Microsoft.EntityFrameworkCore;

namespace CrossBuy.Models.Context.Hospital
{
	// ==========================================================================================
	// HOSPITAL H120 — the pharmacy's link from a chemotherapy drug to its stock item. A regimen names a drug and a dose
	// (Doxorubicin 60 mg/m²); the pharmacy stocks a product (the 50 mg vial). This is the pharmacy's own table, apart from
	// the regimens — a regimen in use never changes, but the pharmacy may link or relink its stock at any time.
	// CrossBuy/deploy/sql/hospital_h120_chemo_pharmacy.sql builds it.
	// ==========================================================================================
	public class HospitalChemoDrugItem
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		/// The drug as the regimens write it, folded (trimmed, single spaces, upper case) so "doxorubicin" is "DOXORUBICIN".
		public string DrugKey { get; set; } = "";
		public int ItemId { get; set; }
		/// How many mg one unit of the item holds (a vial, an ampoule, a tablet).
		public decimal MgPerUnit { get; set; }
		public DateTime UpdatedAt { get; set; }
		public int? UpdatedByEmployeeId { get; set; }
	}

	public static class HospitalChemoDrugItemModel
	{
		public static void Configure(ModelBuilder b)
		{
			b.Entity<HospitalChemoDrugItem>(e =>
			{
				e.ToTable("HospitalChemoDrugItems");
				e.Property(x => x.DrugKey).HasMaxLength(100);
				e.Property(x => x.MgPerUnit).HasPrecision(18, 4);
				e.HasIndex(x => new { x.CompanyId, x.DrugKey }).IsUnique();
			});
		}
	}
}
