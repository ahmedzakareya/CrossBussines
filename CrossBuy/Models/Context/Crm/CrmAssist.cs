using Microsoft.EntityFrameworkCore;

namespace CrossBuy.Models.Context.Crm
{
	// CRM C9 — the assistant. Slice: deploy/sql/crm_c9_assist.sql.

	/// A lead's behavioural score and the reasons behind it. Leads.Score (the company's own rule score) is untouched.
	public class CrmLeadInsight
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public int LeadId { get; set; }
		public int Score { get; set; }
		public string Band { get; set; } = "Cold";
		public string ReasonsJson { get; set; } = "[]";
		public DateTime ComputedAt { get; set; }
	}

	public static class CrmAssistModel
	{
		public static void Configure(ModelBuilder b)
		{
			b.Entity<CrmLeadInsight>(e =>
			{
				e.ToTable("CrmLeadInsights");
				e.Property(x => x.Band).HasMaxLength(5).IsUnicode(false); e.Property(x => x.ReasonsJson).HasMaxLength(1000);
				e.HasIndex(x => new { x.CompanyId, x.LeadId }).IsUnique();
			});
		}
	}
}
