using Microsoft.EntityFrameworkCore;

namespace CrossBuy.Models.Context.Hospital
{
	// ==========================================================================================
	// HOSPITAL H66 — EQUIPMENT WORK ORDERS (the prototype's "equipment work orders"). Anyone reports a broken device: the
	// asset (from Accounting's fixed-asset register) or a description when it is not registered, where it is, what is
	// wrong, and how urgent (a device a patient needs now is Urgent). The administrator assigns a technician; the
	// technician starts it and closes it with what was done. Closing a registered asset's order logs the repair in
	// Accounting's maintenance records (and posts its cost when a pay-from account is given) — the one maintenance
	// history, never a second one. Open → Assigned → InProgress → Done; Open/Assigned → Cancelled (with why).
	// CrossBuy/deploy/sql/hospital_h66_work_orders.sql builds the table.
	// ==========================================================================================
	public static class HospitalWorkOrderStatus
	{
		public const string Open = "Open";
		public const string Assigned = "Assigned";
		public const string InProgress = "InProgress";
		public const string Done = "Done";
		public const string Cancelled = "Cancelled";
		public static readonly IReadOnlyList<string> All = new[] { Open, Assigned, InProgress, Done, Cancelled };
		public static bool Active(string s) => s is Open or Assigned or InProgress;
	}

	public static class HospitalWorkOrderPriority
	{
		public const string Urgent = "Urgent";
		public const string High = "High";
		public const string Normal = "Normal";
		public static readonly IReadOnlyList<string> All = new[] { Urgent, High, Normal };
	}

	public class HospitalWorkOrder
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public string OrderNo { get; set; } = "";
		/// The device from the fixed-asset register (null = described in Equipment).
		public int? AssetId { get; set; }
		public string? Equipment { get; set; }
		public int? RoomId { get; set; }
		public string Problem { get; set; } = "";
		public string Priority { get; set; } = HospitalWorkOrderPriority.Normal;
		public string Status { get; set; } = HospitalWorkOrderStatus.Open;
		public DateTime ReportedAt { get; set; }
		public int ReportedByEmployeeId { get; set; }
		public int? TechnicianEmployeeId { get; set; }
		public DateTime? AssignedAt { get; set; }
		public DateTime? StartedAt { get; set; }
		public DateTime? DoneAt { get; set; }
		public string? Resolution { get; set; }
		public decimal? Cost { get; set; }
		public string? Vendor { get; set; }
		public string? CancelReason { get; set; }
		/// Whether the repair was logged in Accounting's maintenance records.
		public bool LoggedToAsset { get; set; }
	}

	public static class HospitalWorkOrderModel
	{
		public static void Configure(ModelBuilder b)
		{
			b.Entity<HospitalWorkOrder>(e =>
			{
				e.ToTable("HospitalWorkOrders");   // no DbSet: it names its table
				e.Property(x => x.OrderNo).HasMaxLength(20);
				e.Property(x => x.Equipment).HasMaxLength(200);
				e.Property(x => x.Problem).HasMaxLength(1000);
				e.Property(x => x.Priority).HasMaxLength(10);
				e.Property(x => x.Status).HasMaxLength(12);
				e.Property(x => x.Resolution).HasMaxLength(1000);
				e.Property(x => x.Cost).HasColumnType("decimal(18,4)");
				e.Property(x => x.Vendor).HasMaxLength(200);
				e.Property(x => x.CancelReason).HasMaxLength(300);
				e.HasIndex(x => new { x.CompanyId, x.OrderNo }).IsUnique();
				e.HasIndex(x => new { x.CompanyId, x.Status });
				e.HasIndex(x => new { x.CompanyId, x.AssetId });
			});
		}
	}
}
