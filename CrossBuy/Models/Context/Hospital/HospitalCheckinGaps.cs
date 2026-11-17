using Microsoft.EntityFrameworkCore;

namespace CrossBuy.Models.Context.Hospital
{
	// ==========================================================================================
	// HOSPITAL H159 — check-in gaps: a check-in step the desk could not finish at the counter (no ID, the payer not decided,
	// no mobile, no photo, the consent unsigned, money owed, a document missing) becomes a follow-up with an OWNER (or
	// unassigned, for whoever takes it), a REASON and a DUE DATE. It closes itself once the step is done on the file, or is
	// closed / waived by hand with a word; one not closed by its date is overdue.
	// ==========================================================================================
	public static class HospitalCheckinItem
	{
		public const string Identity = "identity", Payer = "payer", Phone = "phone", Photo = "photo", Consent = "consent", Balance = "balance", Document = "document";
		public static readonly IReadOnlyList<string> All = new[] { Identity, Payer, Phone, Photo, Consent, Balance, Document };
		/// Steps the file itself proves done (the others are closed by hand).
		public static readonly IReadOnlyList<string> SelfClosing = new[] { Identity, Phone, Photo, Consent };
	}

	public static class HospitalCheckinGapStatus
	{
		public const string Open = "Open", Done = "Done", Waived = "Waived";
		public static readonly IReadOnlyList<string> All = new[] { Open, Done, Waived };
	}

	public class HospitalCheckinGap
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public int PatientId { get; set; }
		public string Item { get; set; } = HospitalCheckinItem.Identity;
		public string Reason { get; set; } = "";
		/// Null = unassigned: anyone at the desk may take it.
		public int? OwnerEmployeeId { get; set; }
		public DateTime DueDate { get; set; }
		public string Status { get; set; } = HospitalCheckinGapStatus.Open;
		public DateTime OpenedAt { get; set; }
		public int? OpenedByEmployeeId { get; set; }
		public DateTime? ClosedAt { get; set; }
		public int? ClosedByEmployeeId { get; set; }
		/// "auto" when the file proved the step done; otherwise the closer's word.
		public string? CloseNote { get; set; }
	}

	public static class HospitalCheckinGapModel
	{
		public static void Configure(ModelBuilder b)
		{
			b.Entity<HospitalCheckinGap>(e =>
			{
				e.ToTable("HospitalCheckinGaps");   // no DbSet: it names its table
				e.Property(x => x.Item).HasMaxLength(20);
				e.Property(x => x.Reason).HasMaxLength(300);
				e.Property(x => x.Status).HasMaxLength(10);
				e.Property(x => x.CloseNote).HasMaxLength(300);
				e.Property(x => x.DueDate).HasColumnType("date");
				e.HasIndex(x => new { x.CompanyId, x.Status, x.DueDate });
				e.HasIndex(x => new { x.CompanyId, x.PatientId });
			});
		}
	}
}
