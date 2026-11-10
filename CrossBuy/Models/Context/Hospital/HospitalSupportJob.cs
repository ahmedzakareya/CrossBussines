using Microsoft.EntityFrameworkCore;

namespace CrossBuy.Models.Context.Hospital
{
	// ==========================================================================================
	// HOSPITAL H67 — HOUSEKEEPING, PORTERS AND LAUNDRY (the prototype's "housekeeping, porters, laundry"), one desk:
	// a cleaning job (a room or a bed), a porter job (a patient moved from one place to another), a laundry pickup (bags
	// from a place). Open → Taken (a member of staff takes it) → Done (with a note), or Cancelled (with why). A bed that
	// a discharge or a transfer leaves in "Cleaning" opens its own terminal-cleaning job, and closing that job turns the
	// bed back to Vacant — the beds board and the desk are one flow.
	// CrossBuy/deploy/sql/hospital_h67_support_desk.sql builds the table.
	// ==========================================================================================
	public static class HospitalSupportKind
	{
		public const string Cleaning = "Cleaning";
		public const string Porter = "Porter";
		public const string Laundry = "Laundry";
		public static readonly IReadOnlyList<string> All = new[] { Cleaning, Porter, Laundry };
	}

	public static class HospitalSupportStatus
	{
		public const string Open = "Open";
		public const string Taken = "Taken";
		public const string Done = "Done";
		public const string Cancelled = "Cancelled";
		public static readonly IReadOnlyList<string> All = new[] { Open, Taken, Done, Cancelled };
		public static bool Active(string s) => s is Open or Taken;
	}

	public static class HospitalSupportSource
	{
		public const string Manual = "Manual";
		/// Opened by a bed left in Cleaning (a discharge or a transfer).
		public const string BedTurnover = "BedTurnover";
	}

	public class HospitalSupportJob
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public string JobNo { get; set; } = "";
		public string Kind { get; set; } = HospitalSupportKind.Cleaning;
		public string Source { get; set; } = HospitalSupportSource.Manual;
		public int? RoomId { get; set; }
		public int? BedId { get; set; }
		/// The patient a porter moves.
		public int? PatientId { get; set; }
		public string? FromPlace { get; set; }
		public string? ToPlace { get; set; }
		public int? Bags { get; set; }
		public string? Details { get; set; }
		public bool Urgent { get; set; }
		public string Status { get; set; } = HospitalSupportStatus.Open;
		public DateTime RequestedAt { get; set; }
		public int? RequestedByEmployeeId { get; set; }
		public int? TakenByEmployeeId { get; set; }
		public DateTime? TakenAt { get; set; }
		public DateTime? DoneAt { get; set; }
		public string? DoneNote { get; set; }
		public string? CancelReason { get; set; }
	}

	public static class HospitalSupportJobModel
	{
		public static void Configure(ModelBuilder b)
		{
			b.Entity<HospitalSupportJob>(e =>
			{
				e.ToTable("HospitalSupportJobs");   // no DbSet: it names its table
				e.Property(x => x.JobNo).HasMaxLength(20);
				e.Property(x => x.Kind).HasMaxLength(10);
				e.Property(x => x.Source).HasMaxLength(12);
				e.Property(x => x.FromPlace).HasMaxLength(150);
				e.Property(x => x.ToPlace).HasMaxLength(150);
				e.Property(x => x.Details).HasMaxLength(500);
				e.Property(x => x.Status).HasMaxLength(10);
				e.Property(x => x.DoneNote).HasMaxLength(300);
				e.Property(x => x.CancelReason).HasMaxLength(300);
				e.HasIndex(x => new { x.CompanyId, x.JobNo }).IsUnique();
				e.HasIndex(x => new { x.CompanyId, x.Status, x.Kind });
				e.HasIndex(x => new { x.CompanyId, x.BedId });
			});
		}
	}
}
