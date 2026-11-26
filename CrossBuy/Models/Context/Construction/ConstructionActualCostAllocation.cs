using CrossBuy.Models.Context.Accounting;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.Models.Context.Construction;

// Analytical classification of one authoritative expense GL line, not a second posting writer.
public sealed class ConstructionActualCostAllocation
{
    public int ID { get; set; }
    public int CompanyID { get; set; }
    public int ProjectId { get; set; }
    public int JournalEntryId { get; set; }
    public int JournalEntryLineId { get; set; }
    public int FunctionalCurrencyId { get; set; }
    public int FunctionalCurrencyDecimals { get; set; }
    public decimal NetBaseAmount { get; set; }
    public Guid RequestId { get; set; }
    public string RequestHash { get; set; } = "";
    public string SourceVersion { get; set; } = "";
    public string Reason { get; set; } = "";
    public int CreatedBy { get; set; }
    public DateTime CreatedAt { get; set; }
    public bool IsSealed { get; set; }
}
public sealed class ConstructionActualCostAllocationLine
{
    public int ID { get; set; }
    public int CompanyID { get; set; }
    public int ProjectId { get; set; }
    public int AllocationId { get; set; }
    public int WbsNodeId { get; set; }
    public int CostCodeId { get; set; }
    public string CostCategory { get; set; } = "";
    public decimal NetBaseAmount { get; set; }
}

public static class ConstructionActualCostAllocationModel
{
    public static void Configure(ModelBuilder builder, CrossDbContext db, bool sqlServer)
    {
        builder.Entity<JournalEntry>().HasAlternateKey(x => new { x.ID, x.CompanyID });
        if (sqlServer) builder.Entity<JournalEntry>().ToTable("JournalEntries", t => t.UseSqlOutputClause(false));
        builder.Entity<JournalEntry>().HasOne<Currency>().WithMany().HasForeignKey(x => x.FunctionalCurrencyId).OnDelete(DeleteBehavior.Restrict);
        builder.Entity<JournalEntryLine>().HasAlternateKey(x => new { x.ID, x.JournalEntryId });
        builder.Entity<ConstructionActualCostAllocation>(e =>
        {
            e.ToTable("ConstructionActualCostAllocations", t => { if (sqlServer) t.UseSqlOutputClause(false); });
            e.HasKey(x => x.ID); e.HasAlternateKey(x => new { x.ID, x.ProjectId, x.CompanyID });
            e.HasIndex(x => new { x.CompanyID, x.RequestId }).IsUnique();
            e.HasIndex(x => new { x.CompanyID, x.JournalEntryLineId }).IsUnique();
            e.HasOne<JournalEntry>().WithMany().HasForeignKey(x => new { x.JournalEntryId, x.CompanyID })
                .HasPrincipalKey(x => new { x.ID, x.CompanyID }).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<JournalEntryLine>().WithMany().HasForeignKey(x => new { x.JournalEntryLineId, x.JournalEntryId })
                .HasPrincipalKey(x => new { x.ID, x.JournalEntryId }).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<Project>().WithMany().HasForeignKey(x => new { x.ProjectId, x.CompanyID })
                .HasPrincipalKey(x => new { x.ID, x.CompanyID }).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<Currency>().WithMany().HasForeignKey(x => x.FunctionalCurrencyId).OnDelete(DeleteBehavior.Restrict);
            e.Property(x => x.NetBaseAmount).HasPrecision(28, 8);
            e.Property(x => x.RequestHash).HasMaxLength(64).IsRequired(); e.Property(x => x.SourceVersion).HasMaxLength(64).IsRequired();
            e.Property(x => x.Reason).HasMaxLength(1000).IsRequired();
            e.HasQueryFilter(x => db.CompanyScope.AllowsCrossCompany || x.CompanyID == db.CompanyScope.FilterCompanyId);
        });
        builder.Entity<ConstructionActualCostAllocationLine>(e =>
        {
            e.ToTable("ConstructionActualCostAllocationLines", t => { if (sqlServer) t.UseSqlOutputClause(false); });
            e.HasKey(x => x.ID); e.HasIndex(x => new { x.AllocationId, x.WbsNodeId, x.CostCodeId }).IsUnique();
            e.HasOne<ConstructionActualCostAllocation>().WithMany().HasForeignKey(x => new { x.AllocationId, x.ProjectId, x.CompanyID })
                .HasPrincipalKey(x => new { x.ID, x.ProjectId, x.CompanyID }).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<WbsNode>().WithMany().HasForeignKey(x => new { x.WbsNodeId, x.ProjectId, x.CompanyID })
                .HasPrincipalKey(x => new { x.ID, x.ProjectId, x.CompanyID }).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<ConstructionCostCode>().WithMany().HasForeignKey(x => new { x.CostCodeId, x.CompanyID, x.CostCategory })
                .HasPrincipalKey(x => new { x.ID, x.CompanyID, x.Category }).OnDelete(DeleteBehavior.Restrict);
            e.Property(x => x.CostCategory).HasMaxLength(30).IsRequired(); e.Property(x => x.NetBaseAmount).HasPrecision(28, 8);
            e.HasQueryFilter(x => db.CompanyScope.AllowsCrossCompany || x.CompanyID == db.CompanyScope.FilterCompanyId);
        });
    }
}
