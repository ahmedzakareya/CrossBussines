using Microsoft.EntityFrameworkCore;

namespace CrossBuy.Models.Context.Hospital
{
	// ==========================================================================================
	// HOSPITAL H166 — a desk action sent twice is done once. The browser sends a key with each money, booking or send
	// action and keeps it until it gets an answer; the first request with a key does the work and its answer is kept here;
	// a repeat (a retry after a lost connection, a double press) gets that same answer back instead of acting again.
	// ==========================================================================================
	public class HospitalRequestKey
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public string RequestKey { get; set; } = "";
		public string Path { get; set; } = "";
		public int? EmployeeId { get; set; }
		/// Null while the first request is still being handled.
		public int? StatusCode { get; set; }
		public string? Body { get; set; }
		public DateTime CreatedAt { get; set; }
	}

	public static class HospitalRequestKeyModel
	{
		public static void Configure(ModelBuilder b)
		{
			b.Entity<HospitalRequestKey>(e =>
			{
				e.ToTable("HospitalRequestKeys");   // no DbSet: it names its table
				e.Property(x => x.RequestKey).HasMaxLength(64);
				e.Property(x => x.Path).HasMaxLength(200);
				e.HasIndex(x => new { x.CompanyId, x.RequestKey }).IsUnique();
				e.HasIndex(x => x.CreatedAt);
			});
		}
	}
}
