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

	/// H184: work marked done by mistake, taken back. The line returns to planned; this row keeps what it was (when and by
	/// whom it was marked done, its session and its bill line), why it was taken back, and the bill's reversal — the
	/// open bill's line removed, or a credit note on a closed bill.
	public class HospitalDentalCorrection
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public int PatientId { get; set; }
		public int PlanId { get; set; }
		public int LineId { get; set; }
		public int ServiceId { get; set; }
		public int? Tooth { get; set; }
		public decimal Fee { get; set; }
		public DateTime? CompletedAt { get; set; }
		public int? CompletedByEmployeeId { get; set; }
		public int? SessionId { get; set; }
		public int? BillLineId { get; set; }
		/// What the bill gave back: the open bill's line removed (its amount), or the credit note issued on the closed bill.
		public decimal? Reversed { get; set; }
		public int? CreditNoteId { get; set; }
		public string Reason { get; set; } = "";
		public DateTime At { get; set; }
		public int ByEmployeeId { get; set; }
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
			b.Entity<HospitalDentalCorrection>(e =>
			{
				e.ToTable("HospitalDentalCorrections");   // H184
				e.Property(x => x.Fee).HasColumnType("decimal(18,4)");
				e.Property(x => x.Reversed).HasColumnType("decimal(18,4)");
				e.Property(x => x.Reason).HasMaxLength(300);
				e.HasIndex(x => new { x.CompanyId, x.PlanId });
			});
		}
	}
}
