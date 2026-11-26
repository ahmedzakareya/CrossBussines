using Microsoft.EntityFrameworkCore;

namespace CrossBuy.Models.Context.Crm
{
	// CRM C2 — lead capture and assignment. Slice: deploy/sql/crm_c2_capture.sql.

	/// A sales territory. Each criterion is a comma list; an empty one means "any". A lead joins the FIRST active
	/// territory (lowest Priority) whose every non-empty criterion it meets.
	public class CrmTerritory
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public string Name { get; set; } = "";
		public string? NameEn { get; set; }
		public string? Areas { get; set; }
		public string? Segments { get; set; }
		public string? Sources { get; set; }
		public int Priority { get; set; } = 100;
		public bool IsActive { get; set; } = true;
		public DateTime CreatedAt { get; set; }
	}

	public class CrmTerritoryMember
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public int TerritoryId { get; set; }
		public int EmployeeId { get; set; }
		/// The round-robin: the member who waited longest (never assigned first) takes the next lead.
		public DateTime? LastAssignedAt { get; set; }
	}

	/// A public lead form. The visitor's request names only PublicKey; the company comes from this row.
	public class CrmWebForm
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public string PublicKey { get; set; } = "";
		public string Name { get; set; } = "";
		public string Title { get; set; } = "";
		public string Source { get; set; } = "Website";
		public string? Segment { get; set; }
		public bool AskCompany { get; set; } = true;
		public bool AskEmail { get; set; } = true;
		public bool AskArea { get; set; }
		public string? AreaOptions { get; set; }
		public string? NeedOptions { get; set; }
		public string ConsentText { get; set; } = "";
		public string ThankYou { get; set; } = "";
		public string? AllowedSites { get; set; }
		public bool IsActive { get; set; } = true;
		public DateTime CreatedAt { get; set; }
		public int? CreatedByEmployeeId { get; set; }
	}

	/// One visitor submission, exactly as received. It becomes a lead (or is linked to an existing account) only
	/// inside the company's own scope — never from the anonymous request itself.
	public class CrmWebSubmission
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public int FormId { get; set; }
		public DateTime ReceivedAt { get; set; }
		public string Name { get; set; } = "";
		public string? Company { get; set; }
		public string? Phone { get; set; }
		public string? Email { get; set; }
		public string? Area { get; set; }
		public string? Need { get; set; }
		public string? Message { get; set; }
		public bool ConsentGiven { get; set; }
		public string? ConsentText { get; set; }
		public string? PageUrl { get; set; }
		public string? VisitorKey { get; set; }
		public string Status { get; set; } = "Pending";   // Pending | Converted | Linked | Spam | Failed
		public int? LeadId { get; set; }
		public int? AccountId { get; set; }
		public int? OwnerEmployeeId { get; set; }
		public string? AssignReason { get; set; }
		public DateTime? ProcessedAt { get; set; }
		public string? Error { get; set; }
	}

	public static class CrmCaptureModel
	{
		public static void Configure(ModelBuilder b)
		{
			b.Entity<CrmTerritory>(e =>
			{
				e.ToTable("CrmTerritories");   // no DbSet: each names its table
				e.Property(x => x.Name).HasMaxLength(100);
				e.Property(x => x.NameEn).HasMaxLength(100);
				e.Property(x => x.Areas).HasMaxLength(400);
				e.Property(x => x.Segments).HasMaxLength(200);
				e.Property(x => x.Sources).HasMaxLength(200);
				e.HasIndex(x => new { x.CompanyId, x.IsActive, x.Priority });
			});
			b.Entity<CrmTerritoryMember>(e =>
			{
				e.ToTable("CrmTerritoryMembers");
				e.HasIndex(x => new { x.TerritoryId, x.EmployeeId }).IsUnique();
			});
			b.Entity<CrmWebForm>(e =>
			{
				e.ToTable("CrmWebForms");
				e.Property(x => x.PublicKey).HasMaxLength(32);
				e.Property(x => x.Name).HasMaxLength(100);
				e.Property(x => x.Title).HasMaxLength(150);
				e.Property(x => x.Source).HasMaxLength(60);
				e.Property(x => x.Segment).HasMaxLength(60);
				e.Property(x => x.AreaOptions).HasMaxLength(400);
				e.Property(x => x.NeedOptions).HasMaxLength(400);
				e.Property(x => x.ConsentText).HasMaxLength(300);
				e.Property(x => x.ThankYou).HasMaxLength(300);
				e.Property(x => x.AllowedSites).HasMaxLength(400);
				e.HasIndex(x => x.PublicKey).IsUnique();
				e.HasIndex(x => x.CompanyId);
			});
			b.Entity<CrmWebSubmission>(e =>
			{
				e.ToTable("CrmWebSubmissions");
				e.Property(x => x.Name).HasMaxLength(150);
				e.Property(x => x.Company).HasMaxLength(150);
				e.Property(x => x.Phone).HasMaxLength(40);
				e.Property(x => x.Email).HasMaxLength(150);
				e.Property(x => x.Area).HasMaxLength(60);
				e.Property(x => x.Need).HasMaxLength(100);
				e.Property(x => x.Message).HasMaxLength(1000);
				e.Property(x => x.ConsentText).HasMaxLength(300);
				e.Property(x => x.PageUrl).HasMaxLength(400);
				e.Property(x => x.VisitorKey).HasMaxLength(64);
				e.Property(x => x.Status).HasMaxLength(10);
				e.Property(x => x.AssignReason).HasMaxLength(200);
				e.Property(x => x.Error).HasMaxLength(300);
				e.HasIndex(x => new { x.CompanyId, x.Status, x.ReceivedAt });
				e.HasIndex(x => new { x.FormId, x.ReceivedAt });
			});
		}
	}
}
