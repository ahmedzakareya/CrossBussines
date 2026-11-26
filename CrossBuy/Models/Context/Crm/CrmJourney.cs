using Microsoft.EntityFrameworkCore;

namespace CrossBuy.Models.Context.Crm
{
	// CRM C7 — dynamic segments and marketing journeys. Slice: deploy/sql/crm_c7_journeys.sql.

	public class CrmSegment
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public string Name { get; set; } = "";
		public string? NameEn { get; set; }
		public bool MatchAll { get; set; } = true;
		public string RulesJson { get; set; } = "[]";
		public int MemberCount { get; set; }
		public DateTime? RefreshedAt { get; set; }
		public int? CreatedByEmployeeId { get; set; }
		public DateTime CreatedAt { get; set; }
	}

	public class CrmSegmentMember
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public int SegmentId { get; set; }
		public int ContactId { get; set; }
		public DateTime EnteredAt { get; set; }
		public DateTime? LeftAt { get; set; }
	}

	public class CrmJourney
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public string Name { get; set; } = "";
		public string? NameEn { get; set; }
		public int SegmentId { get; set; }
		public string Status { get; set; } = "Draft";      // Draft | Active | Paused
		public bool IncludeExisting { get; set; }
		public bool ExitOnLeave { get; set; } = true;
		public string NodesJson { get; set; } = "[]";
		public string? PublicOrigin { get; set; }
		public DateTime? ActivatedAt { get; set; }
		public int? CreatedByEmployeeId { get; set; }
		public DateTime CreatedAt { get; set; }
	}

	public class CrmJourneyMember
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public int JourneyId { get; set; }
		public int ContactId { get; set; }
		public int? AccountId { get; set; }
		public DateTime EnteredAt { get; set; }
		public string Status { get; set; } = "Active";     // Active | Finished | Exited
		public string? ExitReason { get; set; }            // OptOut | LeftSegment | Removed | Manual
		public string? NodeId { get; set; }
		public DateTime? NextAt { get; set; }
		public DateTime? FinishedAt { get; set; }
	}

	public class CrmJourneyEvent
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public int MemberId { get; set; }
		public string? NodeId { get; set; }
		public DateTime At { get; set; }
		public string Outcome { get; set; } = "Entered";
		public string? Detail { get; set; }
		public int? RefId { get; set; }
	}

	public static class CrmJourneyModel
	{
		public static void Configure(ModelBuilder b)
		{
			b.Entity<CrmSegment>(e => { e.ToTable("CrmSegments"); e.Property(x => x.Name).HasMaxLength(150); e.Property(x => x.NameEn).HasMaxLength(150); e.Property(x => x.RulesJson).HasMaxLength(4000); });
			b.Entity<CrmSegmentMember>(e => { e.ToTable("CrmSegmentMembers"); e.HasIndex(x => new { x.SegmentId, x.ContactId }); });
			b.Entity<CrmJourney>(e =>
			{
				e.ToTable("CrmJourneys");
				e.Property(x => x.Name).HasMaxLength(150); e.Property(x => x.NameEn).HasMaxLength(150);
				e.Property(x => x.Status).HasMaxLength(10).IsUnicode(false); e.Property(x => x.PublicOrigin).HasMaxLength(200).IsUnicode(false);
			});
			b.Entity<CrmJourneyMember>(e =>
			{
				e.ToTable("CrmJourneyMembers");
				e.Property(x => x.Status).HasMaxLength(10).IsUnicode(false); e.Property(x => x.ExitReason).HasMaxLength(12).IsUnicode(false);
				e.Property(x => x.NodeId).HasMaxLength(12).IsUnicode(false);
				e.HasIndex(x => new { x.JourneyId, x.ContactId }).IsUnique();
			});
			b.Entity<CrmJourneyEvent>(e =>
			{
				e.ToTable("CrmJourneyEvents");
				e.Property(x => x.NodeId).HasMaxLength(12).IsUnicode(false); e.Property(x => x.Outcome).HasMaxLength(10).IsUnicode(false);
				e.Property(x => x.Detail).HasMaxLength(300);
				e.HasIndex(x => new { x.MemberId, x.ID });
			});
		}
	}
}
