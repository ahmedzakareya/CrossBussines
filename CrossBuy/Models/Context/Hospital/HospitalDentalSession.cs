using Microsoft.EntityFrameworkCore;

namespace CrossBuy.Models.Context.Hospital
{
	// ==========================================================================================
	// HOSPITAL H179 — the dental treatment session record (from the dental review: "a signed treatment-session record").
	// The dentist signs one record of what was done at a sitting: the plan's work completed in it (each line points at its
	// session), the local anaesthetic (drug, amount, site), the materials and their lots, complications and the post-
	// operative instructions given. Signed when written; never edited — an addendum (H177) corrects it.
	// deploy/sql/hospital_h179_dental_sessions.sql (also links a procedure consent to a plan phase).
	// ==========================================================================================
	public class HospitalDentalSession
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public string SessionNo { get; set; } = "";
		public int PatientId { get; set; }
		public int? PractitionerId { get; set; }
		public DateTime SessionAt { get; set; }
		public string? Anaesthetic { get; set; }
		public string? AnaestheticAmount { get; set; }
		public string? AnaestheticSite { get; set; }
		public string? Materials { get; set; }
		public string? Complications { get; set; }
		public string? PostOpInstructions { get; set; }
		public string? Note { get; set; }
		public DateTime SignedAt { get; set; }
		public int SignedByEmployeeId { get; set; }
	}

	public static class HospitalDentalSessionModel
	{
		public static void Configure(ModelBuilder b)
		{
			b.Entity<HospitalDentalSession>(e =>
			{
				e.ToTable("HospitalDentalSessions");   // no DbSet: it names its table
				e.Property(x => x.SessionNo).HasMaxLength(20);
				e.Property(x => x.Anaesthetic).HasMaxLength(100);
				e.Property(x => x.AnaestheticAmount).HasMaxLength(40);
				e.Property(x => x.AnaestheticSite).HasMaxLength(100);
				e.Property(x => x.Materials).HasMaxLength(500);
				e.Property(x => x.Complications).HasMaxLength(500);
				e.Property(x => x.PostOpInstructions).HasMaxLength(500);
				e.Property(x => x.Note).HasMaxLength(1000);
				e.HasIndex(x => new { x.CompanyId, x.SessionNo }).IsUnique();
				e.HasIndex(x => new { x.CompanyId, x.PatientId, x.SessionAt });
			});
		}
	}
}
