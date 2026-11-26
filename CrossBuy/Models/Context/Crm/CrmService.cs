using Microsoft.EntityFrameworkCore;

namespace CrossBuy.Models.Context.Crm
{
	// CRM C8 — service: knowledge base, the ticket's public thread, the satisfaction survey. Slice: deploy/sql/crm_c8_service.sql.

	public class CrmKbArticle
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public string Title { get; set; } = "";
		public string? TitleEn { get; set; }
		public string Body { get; set; } = "";
		public string? BodyEn { get; set; }
		public string? Category { get; set; }
		public string? Keywords { get; set; }
		public bool IsPublic { get; set; }
		public string Status { get; set; } = "Draft";      // Draft | Published | Archived
		public int Views { get; set; }
		public int Helpful { get; set; }
		public int NotHelpful { get; set; }
		public int UsedInTickets { get; set; }
		public int? UpdatedByEmployeeId { get; set; }
		public DateTime UpdatedAt { get; set; }
		public DateTime CreatedAt { get; set; }
	}

	/// The part of a ticket the customer sees: our replies and theirs. Internal working stays on the ticket itself.
	public class CrmTicketMessage
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public int TicketId { get; set; }
		public string Direction { get; set; } = "Out";     // In = the customer, Out = our team
		public string Body { get; set; } = "";
		public int? ByEmployeeId { get; set; }
		public int? ByPortalUserId { get; set; }
		public int? ArticleId { get; set; }
		public DateTime At { get; set; }
	}

	public class CrmTicketSurvey
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public int TicketId { get; set; }
		public string TokenHash { get; set; } = "";
		public DateTime SentAt { get; set; }
		public string? SentTo { get; set; }
		public byte? Score { get; set; }
		public string? Comment { get; set; }
		public DateTime? AnsweredAt { get; set; }
	}

	public static class CrmServiceModel
	{
		public static void Configure(ModelBuilder b)
		{
			b.Entity<CrmKbArticle>(e =>
			{
				e.ToTable("CrmKbArticles");
				e.Property(x => x.Title).HasMaxLength(200); e.Property(x => x.TitleEn).HasMaxLength(200);
				e.Property(x => x.Category).HasMaxLength(100); e.Property(x => x.Keywords).HasMaxLength(400);
				e.Property(x => x.Status).HasMaxLength(10).IsUnicode(false);
			});
			b.Entity<CrmTicketMessage>(e =>
			{
				e.ToTable("CrmTicketMessages");
				e.Property(x => x.Direction).HasMaxLength(4).IsUnicode(false); e.Property(x => x.Body).HasMaxLength(4000);
				e.HasIndex(x => new { x.CompanyId, x.TicketId, x.ID });
			});
			b.Entity<CrmTicketSurvey>(e =>
			{
				e.ToTable("CrmTicketSurveys");
				e.Property(x => x.TokenHash).HasMaxLength(64).IsFixedLength().IsUnicode(false);
				e.Property(x => x.SentTo).HasMaxLength(150); e.Property(x => x.Comment).HasMaxLength(1000);
				e.HasIndex(x => x.TicketId).IsUnique(); e.HasIndex(x => x.TokenHash).IsUnique();
			});
		}
	}
}
