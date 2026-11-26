using Microsoft.EntityFrameworkCore;

namespace CrossBuy.Models.Context.Crm
{
	// CRM C3 — the deal workspace. Slice: deploy/sql/crm_c3_deal.sql.

	/// What a deal must have before it may ENTER a stage. Checked on every way into a stage (the board and the form).
	public class CrmStageRequirement
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public int StageId { get; set; }
		public string Requirement { get; set; } = "";   // see CrmDealVocabulary.Requirements
		public int? FieldId { get; set; }
	}

	/// A person on a deal and the part they play. The roles are what a buying committee is made of.
	public class CrmOpportunityContact
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public int OpportunityId { get; set; }
		public int ContactId { get; set; }
		public string Role { get; set; } = "Influencer";
		public DateTime AddedAt { get; set; }
		public int? AddedByEmployeeId { get; set; }
	}

	/// The largest LINE discount a CRM role may give without asking. A role with no row may give none.
	public class CrmDiscountLimit
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public string Role { get; set; } = "SalesRep";
		public decimal MaxPercent { get; set; }
	}

	/// A request to give more than one's limit. It covers exactly the lines it was asked for (LinesFingerprint):
	/// change a quantity, a price or a discount and the approval no longer applies.
	public class CrmDiscountApproval
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public int OpportunityId { get; set; }
		public decimal RequestedPercent { get; set; }
		public string LinesFingerprint { get; set; } = "";
		public decimal Amount { get; set; }
		public string Reason { get; set; } = "";
		public int RequestedByEmployeeId { get; set; }
		public DateTime RequestedAt { get; set; }
		public string Status { get; set; } = "Pending";   // Pending | Approved | Rejected | Withdrawn
		public int? DecidedByEmployeeId { get; set; }
		public DateTime? DecidedAt { get; set; }
		public string? DecisionNote { get; set; }
	}

	public static class CrmDealVocabulary
	{
		public static readonly string[] Requirements = { "Amount", "CloseDate", "Products", "DecisionMaker", "Approver", "NextStep", "Discount", "Quotation", "CustomField" };
		public static readonly string[] Roles = { "DecisionMaker", "Approver", "Influencer", "Champion", "User" };
		public static readonly string[] CrmRoles = { "SalesRep", "SalesManager", "Marketing", "CrmViewer" };
	}

	public static class CrmDealModel
	{
		public static void Configure(ModelBuilder b)
		{
			b.Entity<CrmStageRequirement>(e =>
			{
				e.ToTable("CrmStageRequirements");   // no DbSet: each names its table
				e.Property(x => x.Requirement).HasMaxLength(24);
				e.HasIndex(x => new { x.CompanyId, x.StageId, x.Requirement, x.FieldId }).IsUnique();
			});
			b.Entity<CrmOpportunityContact>(e =>
			{
				e.ToTable("CrmOpportunityContacts");
				e.Property(x => x.Role).HasMaxLength(16);
				e.HasIndex(x => new { x.CompanyId, x.OpportunityId, x.ContactId }).IsUnique();
			});
			b.Entity<CrmDiscountLimit>(e =>
			{
				e.ToTable("CrmDiscountLimits");
				e.Property(x => x.Role).HasMaxLength(16);
				e.Property(x => x.MaxPercent).HasPrecision(5, 2);
				e.HasIndex(x => new { x.CompanyId, x.Role }).IsUnique();
			});
			b.Entity<CrmDiscountApproval>(e =>
			{
				e.ToTable("CrmDiscountApprovals");
				e.Property(x => x.RequestedPercent).HasPrecision(5, 2);
				e.Property(x => x.Amount).HasPrecision(18, 4);
				e.Property(x => x.LinesFingerprint).HasMaxLength(64);
				e.Property(x => x.Reason).HasMaxLength(500);
				e.Property(x => x.Status).HasMaxLength(10);
				e.Property(x => x.DecisionNote).HasMaxLength(500);
				// one open request per deal: the slice's filtered unique index (a filter here breaks SQLite hosts)
				e.HasIndex(x => new { x.CompanyId, x.OpportunityId, x.Status });
			});
		}
	}
}
