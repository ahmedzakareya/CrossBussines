using Microsoft.EntityFrameworkCore;

namespace CrossBuy.Models.Context.Hospital
{
	// HOSPITAL H199 — the imaging archive (PACS) the company's machines send their pictures to. One row per company:
	// the address of its web viewer (a template naming the study by its accession number) and the name the order
	// messages are addressed to. Empty = not connected (the order screen says so).
	public class HospitalPacsSettings
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public string? ViewerUrl { get; set; }
		public string? ReceivingApp { get; set; }
		public DateTime UpdatedAt { get; set; }
		public int? UpdatedByEmployeeId { get; set; }
	}

	public static class HospitalPacsModel
	{
		public static void Configure(ModelBuilder b)
		{
			b.Entity<HospitalPacsSettings>(e =>
			{
				e.ToTable("HospitalPacsSettings");   // no DbSet: it names its table
				e.Property(x => x.ViewerUrl).HasMaxLength(500);
				e.Property(x => x.ReceivingApp).HasMaxLength(60);
				e.HasIndex(x => x.CompanyId).IsUnique();
			});
		}
	}
}
