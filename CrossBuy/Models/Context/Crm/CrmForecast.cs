using Microsoft.EntityFrameworkCore;

namespace CrossBuy.Models.Context.Crm
{
	// CRM C5 — quotas and forecasting. Slice: deploy/sql/crm_c5_forecast.sql.

	/// One person's target for one month. A quarter's target is the sum of its months, never stored twice.
	public class CrmQuota
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public int EmployeeId { get; set; }
		public string Period { get; set; } = "";        // yyyy-MM
		public decimal Amount { get; set; }
		public int? UpdatedByEmployeeId { get; set; }
		public DateTime UpdatedAt { get; set; }
	}

	/// The forecast category someone chose for a deal. No row = the category its probability implies.
	public class CrmDealForecast
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public int OpportunityId { get; set; }
		public string Category { get; set; } = "Pipeline";   // Pipeline | BestCase | Commit | Omitted
		public int? SetByEmployeeId { get; set; }
		public DateTime SetAt { get; set; }
	}

	/// A forecast as it was SUBMITTED: the numbers are a snapshot, so a later change to a deal never rewrites what was
	/// promised. Every submission is a new row; the history is the list.
	public class CrmForecastSubmission
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public int EmployeeId { get; set; }
		public string Period { get; set; } = "";        // yyyy-MM or yyyy-Qn
		public decimal Quota { get; set; }
		public decimal Won { get; set; }
		public decimal CommitAmount { get; set; }
		public decimal BestCase { get; set; }
		public decimal Pipeline { get; set; }
		public string? Note { get; set; }
		public DateTime SubmittedAt { get; set; }
		public string Status { get; set; } = "Submitted";   // Submitted | Approved | Returned | Superseded
		public int? ReviewedByEmployeeId { get; set; }
		public DateTime? ReviewedAt { get; set; }
		public string? ReviewNote { get; set; }
		public decimal? AdjustedCommit { get; set; }
	}

	public static class CrmForecastModel
	{
		public static void Configure(ModelBuilder b)
		{
			b.Entity<CrmQuota>(e =>
			{
				e.ToTable("CrmQuotas");
				e.Property(x => x.Period).HasMaxLength(7).IsFixedLength().IsUnicode(false);
				e.Property(x => x.Amount).HasPrecision(18, 2);
				e.HasIndex(x => new { x.CompanyId, x.EmployeeId, x.Period }).IsUnique();
			});
			b.Entity<CrmDealForecast>(e =>
			{
				e.ToTable("CrmDealForecasts");
				e.Property(x => x.Category).HasMaxLength(12).IsUnicode(false);
				e.HasIndex(x => new { x.CompanyId, x.OpportunityId }).IsUnique();
			});
			b.Entity<CrmForecastSubmission>(e =>
			{
				e.ToTable("CrmForecastSubmissions");
				e.Property(x => x.Period).HasMaxLength(7).IsUnicode(false);
				e.Property(x => x.Status).HasMaxLength(10).IsUnicode(false);
				foreach (var p in new[] { nameof(CrmForecastSubmission.Quota), nameof(CrmForecastSubmission.Won), nameof(CrmForecastSubmission.CommitAmount),
					nameof(CrmForecastSubmission.BestCase), nameof(CrmForecastSubmission.Pipeline), nameof(CrmForecastSubmission.AdjustedCommit) })
					e.Property(p).HasPrecision(18, 2);
				e.Property(x => x.Note).HasMaxLength(500);
				e.Property(x => x.ReviewNote).HasMaxLength(500);
				// one undecided submission per person+period: the slice's filtered unique index (a filter here breaks SQLite hosts)
				e.HasIndex(x => new { x.CompanyId, x.EmployeeId, x.Period, x.ID });
			});
		}
	}
}
