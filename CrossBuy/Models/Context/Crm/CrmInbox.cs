using Microsoft.EntityFrameworkCore;

namespace CrossBuy.Models.Context.Crm
{
	// CRM C4 — the unified inbox. Slice: deploy/sql/crm_c4_inbox.sql.

	/// A connected channel. PublicKey is the only thing an outside request (a chat visitor, Meta's webhook) names; the
	/// company comes from this row. Secrets live in SecretProtected, encrypted with the application's data-protection key.
	public class CrmChannel
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public string Kind { get; set; } = "WebChat";   // WebChat | WhatsApp | Email
		public string Name { get; set; } = "";
		public string PublicKey { get; set; } = "";
		public string? ConfigJson { get; set; }
		public string? SecretProtected { get; set; }
		public string? VerifyToken { get; set; }
		public bool IsActive { get; set; } = true;
		public DateTime CreatedAt { get; set; }
	}

	/// One conversation with one outside party on one channel. Linked = it has been matched to the CRM (contact, lead,
	/// account) inside the company's own scope — an anonymous request never does that matching.
	public class CrmConversation
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public int ChannelId { get; set; }
		public string ExternalKey { get; set; } = "";
		public string? DisplayName { get; set; }
		public string? Phone { get; set; }
		public string? Email { get; set; }
		public int? ContactId { get; set; }
		public int? LeadId { get; set; }
		public int? AccountId { get; set; }
		public int? OpportunityId { get; set; }
		public int? TicketId { get; set; }
		public int? OwnerEmployeeId { get; set; }
		public string Status { get; set; } = "Open";   // Open | Pending | Closed
		public bool Linked { get; set; }
		public int UnreadCount { get; set; }
		public DateTime? LastInboundAt { get; set; }
		public DateTime LastMessageAt { get; set; }
		public string? LastPreview { get; set; }
		public DateTime CreatedAt { get; set; }
	}

	public class CrmConversationMessage
	{
		public long ID { get; set; }
		public int CompanyId { get; set; }
		public int ConversationId { get; set; }
		public string Direction { get; set; } = "In";        // In | Out | Note | System
		public string Body { get; set; } = "";
		public int? SentByEmployeeId { get; set; }
		public string? ExternalId { get; set; }
		public string Delivery { get; set; } = "Received";   // Received | Queued | Sent | Delivered | Read | Failed | Internal
		public string? Error { get; set; }
		public DateTime At { get; set; }
	}

	public static class CrmInboxModel
	{
		public static void Configure(ModelBuilder b)
		{
			b.Entity<CrmChannel>(e =>
			{
				e.ToTable("CrmChannels");   // no DbSet: each names its table
				e.Property(x => x.Kind).HasMaxLength(10);
				e.Property(x => x.Name).HasMaxLength(100);
				e.Property(x => x.PublicKey).HasMaxLength(32);
				e.Property(x => x.ConfigJson).HasMaxLength(2000);
				e.Property(x => x.VerifyToken).HasMaxLength(64);
				e.HasIndex(x => x.PublicKey).IsUnique();
				e.HasIndex(x => new { x.CompanyId, x.Kind });
			});
			b.Entity<CrmConversation>(e =>
			{
				e.ToTable("CrmConversations");
				e.Property(x => x.ExternalKey).HasMaxLength(150);
				e.Property(x => x.DisplayName).HasMaxLength(150);
				e.Property(x => x.Phone).HasMaxLength(40);
				e.Property(x => x.Email).HasMaxLength(150);
				e.Property(x => x.Status).HasMaxLength(8);
				e.Property(x => x.LastPreview).HasMaxLength(200);
				e.HasIndex(x => new { x.ChannelId, x.ExternalKey }).IsUnique();
				e.HasIndex(x => new { x.CompanyId, x.Status, x.LastMessageAt });
			});
			b.Entity<CrmConversationMessage>(e =>
			{
				e.ToTable("CrmConversationMessages");
				e.Property(x => x.Direction).HasMaxLength(6);
				e.Property(x => x.Body).HasMaxLength(4000);
				e.Property(x => x.ExternalId).HasMaxLength(128);
				e.Property(x => x.Delivery).HasMaxLength(10);
				e.Property(x => x.Error).HasMaxLength(300);
				// (ConversationId, ExternalId) unique where not null: the slice's filtered index (a filter here breaks SQLite hosts)
				e.HasIndex(x => new { x.ConversationId, x.ID });
			});
		}
	}
}
