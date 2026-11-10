using Microsoft.EntityFrameworkCore;

namespace CrossBuy.Models.Context.Hospital
{
	// ==========================================================================================
	// HOSPITAL — the pictures of an imaging order (unit H31): the radiographer (or the radiologist) attaches the
	// exam's images to the order, until its report is signed. They show on the order, in the dentist's chart (its
	// "X-rays & photos" tab) and, once the order is released to the portal (H27), to the patient.
	//
	// The bytes are NOT under wwwroot: they live in the platform's private document storage (IDocumentStorage,
	// App_Data/documents), named by an opaque StorageKey that never leaves the server. Every read goes through an
	// endpoint that asks first — `read` for staff; for a patient, their own (or family) order, released.
	// CrossBuy/deploy/sql/hospital_h31_rad_images.sql builds the table.
	// ==========================================================================================

	public class HospitalRadImage
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public int RadOrderId { get; set; }
		/// H31's own storage key (32 hex), for a picture attached before H35; null once it is on the platform.
		public string? StorageKey { get; set; }
		/// H35: the central document platform's document holding this picture (on the HospitalRadOrder record).
		public long? DocumentId { get; set; }
		public string FileName { get; set; } = "";
		public string ContentType { get; set; } = "image/jpeg";
		public int SizeBytes { get; set; }
		public string? Caption { get; set; }
		public DateTime UploadedAt { get; set; }
		public int? UploadedByEmployeeId { get; set; }
	}

	public static class HospitalRadImagesModel
	{
		public static void Configure(ModelBuilder b)
		{
			b.Entity<HospitalRadImage>(e =>
			{
				e.ToTable("HospitalRadImages");   // no DbSet: it names its table
				e.Property(x => x.StorageKey).HasMaxLength(32);
				e.Property(x => x.FileName).HasMaxLength(200);
				e.Property(x => x.ContentType).HasMaxLength(60);
				e.Property(x => x.Caption).HasMaxLength(200);
				e.HasIndex(x => new { x.CompanyId, x.RadOrderId });
			});
		}
	}
}
