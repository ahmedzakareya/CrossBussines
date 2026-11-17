using Microsoft.EntityFrameworkCore;

namespace CrossBuy.Models.Context.Hospital
{
	// ==========================================================================================
	// HOSPITAL H165 — the desk's shift handover: what the leaving employee still holds (queue items taken, check-in gaps
	// owned) passes to a named colleague in one act, with a note and the cash as the closed cashier shift left it
	// (expected, counted, the difference). The receiver acknowledges it; until then the desk shows it to them.
	// ==========================================================================================
	public class HospitalDeskHandover
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public string HandoverNo { get; set; } = "";
		public int FromEmployeeId { get; set; }
		public int ToEmployeeId { get; set; }
		public DateTime At { get; set; }
		public string? Note { get; set; }
		/// What moved: queue items, check-in gaps; and how many of them were overdue.
		public int QueueItems { get; set; }
		public int Gaps { get; set; }
		public int Overdue { get; set; }
		/// The handoffs the leaving employee sent that were still open (they stay theirs on record; the note names them).
		public int OpenHandoffs { get; set; }
		/// The cash, as the last cashier shift closed today left it (null when there was no shift).
		public int? ShiftId { get; set; }
		public decimal? CashExpected { get; set; }
		public decimal? CashCounted { get; set; }
		public decimal? CashVariance { get; set; }
		public DateTime? AcknowledgedAt { get; set; }
	}

	public static class HospitalDeskHandoverModel
	{
		public static void Configure(ModelBuilder b)
		{
			b.Entity<HospitalDeskHandover>(e =>
			{
				e.ToTable("HospitalDeskHandovers");   // no DbSet: it names its table
				e.Property(x => x.HandoverNo).HasMaxLength(20);
				e.Property(x => x.Note).HasMaxLength(500);
				e.Property(x => x.CashExpected).HasColumnType("decimal(18,3)");
				e.Property(x => x.CashCounted).HasColumnType("decimal(18,3)");
				e.Property(x => x.CashVariance).HasColumnType("decimal(18,3)");
				e.HasIndex(x => new { x.CompanyId, x.HandoverNo }).IsUnique();
				e.HasIndex(x => new { x.CompanyId, x.ToEmployeeId, x.AcknowledgedAt });
			});
		}
	}
}
