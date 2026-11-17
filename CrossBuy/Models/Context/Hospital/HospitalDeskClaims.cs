using Microsoft.EntityFrameworkCore;

namespace CrossBuy.Models.Context.Hospital
{
	// ==========================================================================================
	// HOSPITAL H164 — who owns an item of the desk's queues (a portal request, a waitlist offer, a self-intake form, a
	// pre-authorisation, an unidentified patient, a pair of look-alike files). One row per item: taken by someone, or
	// released back to nobody. The item itself stays its own module's; this only says whose it is.
	// ==========================================================================================
	public class HospitalDeskClaim
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public string Kind { get; set; } = "";
		public string ItemKey { get; set; } = "";
		/// Null once released: the item is nobody's again.
		public int? OwnerEmployeeId { get; set; }
		public DateTime ClaimedAt { get; set; }
		public DateTime? ReleasedAt { get; set; }
	}

	public static class HospitalDeskClaimModel
	{
		public static void Configure(ModelBuilder b)
		{
			b.Entity<HospitalDeskClaim>(e =>
			{
				e.ToTable("HospitalDeskClaims");   // no DbSet: it names its table
				e.Property(x => x.Kind).HasMaxLength(20);
				e.Property(x => x.ItemKey).HasMaxLength(40);
				e.HasIndex(x => new { x.CompanyId, x.Kind, x.ItemKey }).IsUnique();
				e.HasIndex(x => new { x.CompanyId, x.OwnerEmployeeId });
			});
		}
	}
}
