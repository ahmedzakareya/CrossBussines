using Microsoft.EntityFrameworkCore;

namespace CrossBuy.Models.Context.Hospital
{
	// ==========================================================================================
	// HOSPITAL — medication routing (unit H21, the prototype's "medroute" screen).
	//
	// A DELIVERY carries one inpatient medication order (H14) from the prescription to the bedside, through six
	// steps, each stamped with who did it and when:
	//   0 prescribed (the order) · 1 verified (pharmacist) · 2 prepared (stock leaves the pharmacy store, FEFO)
	//   · 3 dispatched (tube or porter) · 4 received on the ward (custody moves to nursing)
	//   · 5 administered (the MAR entry, after a wristband scan; the charge lands on the stay's bill).
	// From the ward cabinet the drug is not prepared nor dispatched: those two steps are "not needed", and the
	// cabinet issues it when the ward receives it. A controlled drug goes by porter, with a second signature on
	// receipt and on administration.
	// CrossBuy/deploy/sql/hospital_h21_med_routing.sql builds the table.
	// ==========================================================================================

	public static class HospitalMedPriority
	{
		public const string Routine = "Routine";
		public const string Urgent = "Urgent";
		public const string Stat = "Stat";
		public static readonly IReadOnlyList<string> All = new[] { Routine, Urgent, Stat };
	}

	public static class HospitalMedVia
	{
		public const string Tube = "Tube";
		public const string Porter = "Porter";
		public const string Cabinet = "Cabinet";
		public static readonly IReadOnlyList<string> All = new[] { Tube, Porter, Cabinet };
	}

	public static class HospitalMedStage
	{
		public const byte Prescribed = 0, Verified = 1, Prepared = 2, Dispatched = 3, Received = 4, Administered = 5;
		public const byte Last = Administered;
	}

	public class HospitalMedDelivery
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public string DeliveryNo { get; set; } = "";
		public int MedOrderId { get; set; }
		public int AdmissionId { get; set; }
		public int PatientId { get; set; }
		public string Priority { get; set; } = HospitalMedPriority.Routine;
		public string Via { get; set; } = HospitalMedVia.Tube;
		/// How many units leave the store (the inventory item's unit); 0 for a drug the pharmacy does not stock.
		public decimal Quantity { get; set; }
		/// The last step done (HospitalMedStage).
		public byte Stage { get; set; }
		public DateTime PrescribedAt { get; set; }
		public int? PrescribedByEmployeeId { get; set; }
		public DateTime? VerifiedAt { get; set; }
		public int? VerifiedByEmployeeId { get; set; }
		public DateTime? PreparedAt { get; set; }
		public int? PreparedByEmployeeId { get; set; }
		public DateTime? DispatchedAt { get; set; }
		/// The porter who carried it, or who sent it down the tube.
		public int? DispatchedByEmployeeId { get; set; }
		public DateTime? ReceivedAt { get; set; }
		public int? ReceivedByEmployeeId { get; set; }
		public int? ReceivedWitnessEmployeeId { get; set; }
		public DateTime? AdministeredAt { get; set; }
		public int? AdministeredByEmployeeId { get; set; }
		public int? MarEntryId { get; set; }
		/// What the store issued: its warehouse, the batches (FEFO), the cost per unit.
		public int? WarehouseId { get; set; }
		public string? Batches { get; set; }
		public decimal? UnitCost { get; set; }
		public int? BillLineId { get; set; }
		public DateTime? CancelledAt { get; set; }
		public int? CancelledByEmployeeId { get; set; }
		public string? CancelReason { get; set; }
	}

	public static class HospitalMedRoutingModel
	{
		public static void Configure(ModelBuilder b)
		{
			b.Entity<HospitalMedDelivery>(e =>
			{
				e.ToTable("HospitalMedDeliveries");   // no DbSet: it names its table
				e.Property(x => x.DeliveryNo).HasMaxLength(20);
				e.Property(x => x.Priority).HasMaxLength(10);
				e.Property(x => x.Via).HasMaxLength(10);
				e.Property(x => x.Quantity).HasPrecision(18, 3);
				e.Property(x => x.UnitCost).HasPrecision(18, 4);
				e.Property(x => x.Batches).HasMaxLength(400);
				e.Property(x => x.CancelReason).HasMaxLength(300);
				e.HasIndex(x => new { x.CompanyId, x.DeliveryNo }).IsUnique();
				e.HasIndex(x => new { x.CompanyId, x.Stage });
				e.HasIndex(x => x.MedOrderId);
			});
		}
	}
}
