using Microsoft.EntityFrameworkCore;

namespace CrossBuy.Models.Context.Hospital
{
	// ==========================================================================================
	// HOSPITAL PHARMACY (unit H10) — dispensing from the inventory.
	//
	// A signed visit's prescription becomes one DISPENSE (RX-000001) on the pharmacy queue, its lines copied
	// from the prescription. A line names an inventory ITEM (a medicine or consumable the doctor picked) or is
	// free text the hospital does not stock (the patient buys it outside). The pharmacist reviews it
	// (Verified), then dispenses: every stocked line leaves the pharmacy store through the inventory's one
	// stock writer (StockService — nearest expiry first, cost to the ledger) and becomes a charge on the
	// visit's bill at the patient's price. The pharmacy store is the warehouse named in the settings.
	// deploy/sql/hospital_h10_pharmacy.sql builds the tables.
	// ==========================================================================================

	public static class HospitalDispenseStatus
	{
		public const string New = "New";
		public const string Verified = "Verified";
		public const string Dispensed = "Dispensed";
		public static readonly IReadOnlyList<string> All = new[] { New, Verified, Dispensed };
	}

	public static class HospitalDispenseSource
	{
		public const string Outpatient = "Outpatient";
		public const string Inpatient = "Inpatient";
		public const string Emergency = "Emergency";
	}

	/// The company's pharmacy: which warehouse it dispenses from, and how near an expiry counts as "near".
	public class HospitalPharmacySettings
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public int? WarehouseId { get; set; }
		public int NearExpiryDays { get; set; } = 60;
	}

	public class HospitalDispense
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public string RxNo { get; set; } = "";
		public int PatientId { get; set; }
		public int? EncounterId { get; set; }
		public string Source { get; set; } = HospitalDispenseSource.Outpatient;
		public int? PrescribedByEmployeeId { get; set; }
		public int? PractitionerId { get; set; }
		public string Status { get; set; } = HospitalDispenseStatus.New;
		public DateTime PrescribedAt { get; set; }
		public DateTime? VerifiedAt { get; set; }
		public int? VerifiedByEmployeeId { get; set; }
		public string? PharmacistNote { get; set; }
		public DateTime? DispensedAt { get; set; }
		public int? DispensedByEmployeeId { get; set; }
		public int? WarehouseId { get; set; }
		public int? BillId { get; set; }
	}

	public class HospitalDispenseLine
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public int DispenseId { get; set; }
		public int? PrescriptionLineId { get; set; }
		/// The inventory item, or null for a drug the hospital does not stock (free text).
		public int? ItemId { get; set; }
		public string Drug { get; set; } = "";
		public string? Instructions { get; set; }
		public decimal Quantity { get; set; }
		/// Filled on dispensing: what left the store, at what price, from which batches.
		public decimal? DispensedQty { get; set; }
		public decimal? UnitPrice { get; set; }
		public decimal? UnitCost { get; set; }
		public string? Batches { get; set; }
		public DateTime? NearestExpiry { get; set; }
		public int? BillLineId { get; set; }
		public int SortOrder { get; set; }
	}

	public static class HospitalPharmacyModel
	{
		public static void Configure(ModelBuilder b)
		{
			b.Entity<HospitalPharmacySettings>(e => e.HasIndex(x => x.CompanyId).IsUnique());
			b.Entity<HospitalDispense>(e =>
			{
				e.HasIndex(x => new { x.CompanyId, x.RxNo }).IsUnique();
				e.HasIndex(x => new { x.CompanyId, x.EncounterId }).IsUnique().HasFilter("[EncounterId] IS NOT NULL");
				e.HasIndex(x => new { x.CompanyId, x.Status });
				e.HasIndex(x => new { x.CompanyId, x.PatientId });
			});
			b.Entity<HospitalDispenseLine>(e =>
			{
				foreach (var p in new[] { nameof(HospitalDispenseLine.Quantity), nameof(HospitalDispenseLine.DispensedQty), nameof(HospitalDispenseLine.UnitPrice), nameof(HospitalDispenseLine.UnitCost) })
					e.Property(p).HasPrecision(18, 4);
				e.HasOne<HospitalDispense>().WithMany().HasForeignKey(x => x.DispenseId).OnDelete(DeleteBehavior.NoAction);
				e.HasIndex(x => new { x.CompanyId, x.DispenseId });
			});
		}
	}
}
