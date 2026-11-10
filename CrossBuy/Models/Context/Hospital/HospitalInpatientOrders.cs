using Microsoft.EntityFrameworkCore;

namespace CrossBuy.Models.Context.Hospital
{
	// ==========================================================================================
	// HOSPITAL H75 — LAB TESTS AND IMAGING FOR INPATIENTS (found missing while building order sets, 2026-11-09: until
	// now a lab or radiology order was born only from a signed clinic or emergency visit, so a patient in a bed could not
	// be sent for a blood test). A doctor orders from the nursing station (or through an order set): the order goes to
	// the lab's or radiology's own worklist exactly as a visit's does, and its service is charged to the stay's bill.
	// While nothing has happened to it (not collected, not scanned) it can be cancelled with why: the order leaves the
	// worklist, its charge leaves the open bill, and this record keeps what was ordered and why it was cancelled.
	// CrossBuy/deploy/sql/hospital_h75_inpatient_orders.sql builds the table (and lets order sets hold these orders).
	// ==========================================================================================
	public static class HospitalInpatientOrderKind
	{
		public const string Lab = "Lab";
		public const string Imaging = "Rad";
		public static readonly IReadOnlyList<string> All = new[] { Lab, Imaging };
	}

	public static class HospitalInpatientOrderStatus
	{
		public const string Active = "Active";
		public const string Cancelled = "Cancelled";
	}

	public class HospitalInpatientOrder
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public int AdmissionId { get; set; }
		public int PatientId { get; set; }
		public string Kind { get; set; } = HospitalInpatientOrderKind.Lab;
		public int ServiceId { get; set; }
		/// The lab order or the radiology order it created (null once cancelled — the order itself is removed).
		public int? LabOrderId { get; set; }
		public int? RadOrderId { get; set; }
		/// The accession it carried (kept after a cancel, so the record still names it).
		public string AccessionNo { get; set; } = "";
		public string Code { get; set; } = "";
		public string Name { get; set; } = "";
		public string NameEn { get; set; } = "";
		/// The stay's bill line that charged it.
		public int? BillLineId { get; set; }
		public string Priority { get; set; } = HospitalLabPriority.Routine;
		public string? Note { get; set; }
		public string Status { get; set; } = HospitalInpatientOrderStatus.Active;
		public DateTime OrderedAt { get; set; }
		public int? OrderedByEmployeeId { get; set; }
		public DateTime? CancelledAt { get; set; }
		public int? CancelledByEmployeeId { get; set; }
		public string? CancelReason { get; set; }
	}

	public static class HospitalInpatientOrderModel
	{
		public static void Configure(ModelBuilder b)
		{
			b.Entity<HospitalInpatientOrder>(e =>
			{
				e.ToTable("HospitalInpatientOrders");   // no DbSet: it names its table
				e.Property(x => x.Kind).HasMaxLength(5);
				e.Property(x => x.AccessionNo).HasMaxLength(30);
				e.Property(x => x.Code).HasMaxLength(40);
				e.Property(x => x.Name).HasMaxLength(200);
				e.Property(x => x.NameEn).HasMaxLength(200);
				e.Property(x => x.Priority).HasMaxLength(10);
				e.Property(x => x.Note).HasMaxLength(300);
				e.Property(x => x.Status).HasMaxLength(10);
				e.Property(x => x.CancelReason).HasMaxLength(300);
				e.HasIndex(x => new { x.CompanyId, x.AdmissionId });
			});
		}
	}
}
