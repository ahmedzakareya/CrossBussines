using Microsoft.EntityFrameworkCore;

namespace CrossBuy.Models.Context.Hospital
{
	// HOSPITAL H254 — the trail of ambient documentation: who recorded which visit, when, and the words the speech
	// service heard (the recording itself is never kept). deploy/sql/hospital_h254_scribe.sql builds the table.
	public class HospitalScribeLog
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public int EncounterId { get; set; }
		public int PatientId { get; set; }
		public string Transcript { get; set; } = "";
		public string? Language { get; set; }
		public DateTime At { get; set; }
		public int? ByEmployeeId { get; set; }
	}

	public static class HospitalScribeModel
	{
		public static void Configure(ModelBuilder b)
		{
			b.Entity<HospitalScribeLog>(e =>
			{
				e.ToTable("HospitalScribeLogs");
				e.Property(x => x.Language).HasMaxLength(10);
				e.HasIndex(x => new { x.CompanyId, x.EncounterId });
				e.HasIndex(x => new { x.CompanyId, x.PatientId });
			});
		}
	}
}
