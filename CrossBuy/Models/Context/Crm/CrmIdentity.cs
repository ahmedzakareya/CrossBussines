using Microsoft.EntityFrameworkCore;

namespace CrossBuy.Models.Context.Crm
{
	// CRM C1 — one customer record. Slice: deploy/sql/crm_c1_identity.sql.

	/// One consent decision for one person on one channel. APPEND-ONLY: a change is a new row, never an
	/// edit, so the record can always answer "when, how and by whom was this given or withdrawn" — the
	/// question a data-protection complaint actually asks. The latest row per (subject, channel) is the
	/// state; no row at all means "never asked", which is NOT consent.
	public class CrmConsent
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public string SubjectType { get; set; } = "Contact";   // Contact | Lead
		public int SubjectId { get; set; }
		public string Channel { get; set; } = "Email";         // WhatsApp | Email | Sms | Call
		public string State { get; set; } = "OptIn";           // OptIn | OptOut
		public string Source { get; set; } = "Verbal";         // Verbal | Written | WebForm | Unsubscribe | Import | Merge
		public string? Note { get; set; }
		public DateTime RecordedAt { get; set; }
		public int? RecordedByEmployeeId { get; set; }
	}

	/// A record merged into another. The row is the redirect (open the merged id → land on the survivor)
	/// and the audit (what moved, who decided).
	public class CrmMerge
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public string EntityType { get; set; } = "Account";    // Account | Contact
		public int SurvivorId { get; set; }
		public int MergedId { get; set; }
		public string MergedName { get; set; } = "";
		public string MovedJson { get; set; } = "{}";
		public DateTime MergedAt { get; set; }
		public int? MergedByEmployeeId { get; set; }
	}

	/// A pair a person ruled "not the same record". Stored low id first so the pair has one spelling.
	public class CrmDuplicateDismissal
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public string EntityType { get; set; } = "Account";
		public int LowId { get; set; }
		public int HighId { get; set; }
		public DateTime DismissedAt { get; set; }
		public int? DismissedByEmployeeId { get; set; }
	}

	public static class CrmConsentVocabulary
	{
		public static readonly string[] Channels = { "WhatsApp", "Email", "Sms", "Call" };
		public static readonly string[] States = { "OptIn", "OptOut" };
		/// Sources a member of staff may record by hand. "Unsubscribe" and "Merge" are written by the system only.
		public static readonly string[] StaffSources = { "Verbal", "Written", "WebForm", "Import" };
	}

	public static class CrmIdentityModel
	{
		public static void Configure(ModelBuilder b)
		{
			b.Entity<CrmConsent>(e =>
			{
				e.ToTable("CrmConsents");   // no DbSet: each names its table
				e.Property(x => x.SubjectType).HasMaxLength(10);
				e.Property(x => x.Channel).HasMaxLength(10);
				e.Property(x => x.State).HasMaxLength(8);
				e.Property(x => x.Source).HasMaxLength(16);
				e.Property(x => x.Note).HasMaxLength(300);
				e.HasIndex(x => new { x.CompanyId, x.SubjectType, x.SubjectId, x.Channel, x.RecordedAt });
			});
			b.Entity<CrmMerge>(e =>
			{
				e.ToTable("CrmMerges");
				e.Property(x => x.EntityType).HasMaxLength(10);
				e.Property(x => x.MergedName).HasMaxLength(200);
				e.Property(x => x.MovedJson).HasMaxLength(2000);
				e.HasIndex(x => new { x.CompanyId, x.EntityType, x.MergedId }).IsUnique();
			});
			b.Entity<CrmDuplicateDismissal>(e =>
			{
				e.ToTable("CrmDuplicateDismissals");
				e.Property(x => x.EntityType).HasMaxLength(10);
				e.HasIndex(x => new { x.CompanyId, x.EntityType, x.LowId, x.HighId }).IsUnique();
			});
		}
	}
}
