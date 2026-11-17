using Microsoft.EntityFrameworkCore;

namespace CrossBuy.Models.Context.Hospital
{
	// ==========================================================================================
	// HOSPITAL H160 — checkout at the desk after the consultation. A visit whose consultation is finished stays "awaiting
	// desk completion" until the desk has gone through what the visit left for it — the money owed on its bill, the
	// follow-up the doctor advised (booked or not), a referral, tests and scans not done yet, medicines for the pharmacy, a
	// sick-leave paper — and closed it. What was still open at that moment is kept on the row, and closing with something
	// unresolved needs the desk's word.
	// ==========================================================================================
	public class HospitalDeskCheckout
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public int AppointmentId { get; set; }
		public int PatientId { get; set; }
		public int? EncounterId { get; set; }
		public DateTime CompletedAt { get; set; }
		public int? CompletedByEmployeeId { get; set; }
		/// What the visit's bill still owed when the desk closed it.
		public decimal OwedAtClose { get; set; }
		public int? FollowUpDays { get; set; }
		public bool FollowUpBooked { get; set; }
		/// Tests, scans and medicines still waiting when the desk closed it.
		public int OpenOrders { get; set; }
		public string? Note { get; set; }
	}

	public static class HospitalDeskCheckoutModel
	{
		public static void Configure(ModelBuilder b)
		{
			b.Entity<HospitalDeskCheckout>(e =>
			{
				e.ToTable("HospitalDeskCheckouts");   // no DbSet: it names its table
				e.Property(x => x.OwedAtClose).HasColumnType("decimal(18,3)");
				e.Property(x => x.Note).HasMaxLength(300);
				e.HasIndex(x => new { x.CompanyId, x.AppointmentId }).IsUnique();
				e.HasIndex(x => new { x.CompanyId, x.PatientId });
			});
		}
	}
}
