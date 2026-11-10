using Microsoft.EntityFrameworkCore;

namespace CrossBuy.Models.Context.Hospital
{
	// ==========================================================================================
	// HOSPITAL — consignment implants (unit H38), as the approved prototype draws them (#/requisitions, "Consignment
	// implants"): the supplier's implants kept in the hospital, by serial. They are not the hospital's stock and not in
	// its books until used; used on a case, an implant is tied to the patient, charged to the bill, and a draft purchase
	// order for it goes to the supplier. CrossBuy/deploy/sql/hospital_h38_consignment.sql builds the table.
	// ==========================================================================================

	public static class HospitalConsignmentStatus
	{
		public const string InStore = "InStore";
		public const string Used = "Used";
		public const string Returned = "Returned";
		public static readonly IReadOnlyList<string> All = new[] { InStore, Used, Returned };
	}

	public class HospitalConsignment
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		/// The supplier who owns it until it is used (a purchasing vendor).
		public int VendorId { get; set; }
		/// The catalogue item it is (an inventory item: what the purchase order and the bill name).
		public int ItemId { get; set; }
		public string SerialNo { get; set; } = "";
		public string? Lot { get; set; }
		public DateTime? ExpiryDate { get; set; }
		/// The price agreed with the supplier — what the purchase order will carry.
		public decimal UnitPrice { get; set; }
		public string Status { get; set; } = HospitalConsignmentStatus.InStore;
		public DateTime ReceivedAt { get; set; }
		public int? ReceivedByEmployeeId { get; set; }
		public DateTime? UsedAt { get; set; }
		public int? UsedByEmployeeId { get; set; }
		public int? PatientId { get; set; }
		public int? SurgeryId { get; set; }
		public int? PurchaseOrderId { get; set; }
		public int? BillLineId { get; set; }
		public DateTime? ReturnedAt { get; set; }
		public string? Note { get; set; }
	}

	public static class HospitalConsignmentModel
	{
		public static void Configure(ModelBuilder b)
		{
			b.Entity<HospitalConsignment>(e =>
			{
				e.ToTable("HospitalConsignments");   // no DbSet: it names its table
				e.Property(x => x.SerialNo).HasMaxLength(60);
				e.Property(x => x.Lot).HasMaxLength(60);
				e.Property(x => x.UnitPrice).HasPrecision(18, 3);
				e.Property(x => x.Status).HasMaxLength(15);
				e.Property(x => x.Note).HasMaxLength(300);
				e.HasIndex(x => new { x.CompanyId, x.ItemId, x.SerialNo }).IsUnique();   // a serial once per item
				e.HasIndex(x => new { x.CompanyId, x.Status });
			});
		}
	}
}
