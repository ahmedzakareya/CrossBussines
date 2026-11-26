using CrossBuy.Models.Context.Accounting;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.Models.Context.Construction;

// A human-approved estimate, never a posted balance or a replacement for the GL.
public sealed class ConstructionBudgetVersion
{
    public int ID { get; set; }
    public int CompanyID { get; set; }
    public int ProjectId { get; set; }
    public int VersionNo { get; set; }
    public int? PreviousVersionId { get; set; }
    public int CurrencyId { get; set; }
    public string Kind { get; set; } = "Original";
    public string Status { get; set; } = "Draft";
    public bool IsBaseline { get; set; }
    public string Reason { get; set; } = "";
    public int CreatedBy { get; set; }
    public DateTime CreatedAt { get; set; }
    public int? SubmittedBy { get; set; }
    public DateTime? SubmittedAt { get; set; }
    public int? ApprovedBy { get; set; }
    public DateTime? ApprovedAt { get; set; }
    public byte[] RowVersion { get; set; } = Guid.NewGuid().ToByteArray();
}

public sealed class ConstructionBudgetLine
{
    public int ID { get; set; }
    public int CompanyID { get; set; }
    public int ProjectId { get; set; }
    public int BudgetVersionId { get; set; }
    public int WbsNodeId { get; set; }
    public int CostCodeId { get; set; }
    public string CostCategory { get; set; } = "";
    public decimal Amount { get; set; }
    public decimal? Quantity { get; set; }
    public decimal? Rate { get; set; }
    public decimal? PreviousAmount { get; set; }
    public bool IsContingency { get; set; }
    public string ChangeReason { get; set; } = "";
    public byte[] RowVersion { get; set; } = Guid.NewGuid().ToByteArray();
}

public static class ConstructionBudgetVersionModel
{
    public static void Configure(ModelBuilder builder, CrossDbContext db, bool sqlServer)
    {
        builder.Entity<ConstructionBudgetVersion>(e =>
        {
            e.ToTable("ConstructionBudgetVersions", t => { if (sqlServer) t.UseSqlOutputClause(false); });
            e.HasKey(x => x.ID);
            e.HasAlternateKey(x => new { x.ID, x.ProjectId, x.CompanyID });
            e.HasIndex(x => new { x.CompanyID, x.ProjectId, x.VersionNo }).IsUnique();
            e.HasIndex(x => new { x.CompanyID, x.ProjectId }).IsUnique().HasFilter(sqlServer ? "[IsBaseline] = 1" : "\"IsBaseline\" = 1");
            e.HasOne<Project>().WithMany().HasForeignKey(x => new { x.ProjectId, x.CompanyID })
                .HasPrincipalKey(x => new { x.ID, x.CompanyID }).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<ConstructionBudgetVersion>().WithMany().HasForeignKey(x => new { x.PreviousVersionId, x.ProjectId, x.CompanyID })
                .HasPrincipalKey(x => new { x.ID, x.ProjectId, x.CompanyID }).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<Currency>().WithMany().HasForeignKey(x => x.CurrencyId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<ConstructionBudgetPolicy>().WithMany().HasForeignKey(x => x.CompanyID).OnDelete(DeleteBehavior.Restrict);
            e.Property(x => x.Kind).HasMaxLength(20).IsRequired();
            e.Property(x => x.Status).HasMaxLength(20).IsRequired();
            e.Property(x => x.Reason).HasMaxLength(1000).IsRequired();
            e.HasQueryFilter(x => db.CompanyScope.AllowsCrossCompany || x.CompanyID == db.CompanyScope.FilterCompanyId);
            var version = e.Property(x => x.RowVersion);
            if (sqlServer) version.IsRowVersion(); else version.IsConcurrencyToken().ValueGeneratedNever();
        });
        builder.Entity<ConstructionBudgetLine>(e =>
        {
            e.ToTable("ConstructionBudgetLines", t => { if (sqlServer) t.UseSqlOutputClause(false); });
            e.HasKey(x => x.ID);
            e.HasIndex(x => new { x.BudgetVersionId, x.WbsNodeId, x.CostCodeId }).IsUnique();
            e.HasOne<ConstructionBudgetVersion>().WithMany().HasForeignKey(x => new { x.BudgetVersionId, x.ProjectId, x.CompanyID })
                .HasPrincipalKey(x => new { x.ID, x.ProjectId, x.CompanyID }).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<WbsNode>().WithMany().HasForeignKey(x => new { x.WbsNodeId, x.ProjectId, x.CompanyID })
                .HasPrincipalKey(x => new { x.ID, x.ProjectId, x.CompanyID }).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<ConstructionCostCode>().WithMany().HasForeignKey(x => new { x.CostCodeId, x.CompanyID, x.CostCategory })
                .HasPrincipalKey(x => new { x.ID, x.CompanyID, x.Category }).OnDelete(DeleteBehavior.Restrict);
            e.Property(x => x.CostCategory).HasMaxLength(30).IsRequired();
            e.Property(x => x.ChangeReason).HasMaxLength(1000).IsRequired();
            e.Property(x => x.Amount).HasPrecision(28, 8);
            e.Property(x => x.PreviousAmount).HasPrecision(28, 8);
            e.Property(x => x.Quantity).HasPrecision(28, 8);
            e.Property(x => x.Rate).HasPrecision(28, 8);
            e.HasQueryFilter(x => db.CompanyScope.AllowsCrossCompany || x.CompanyID == db.CompanyScope.FilterCompanyId);
            var version = e.Property(x => x.RowVersion);
            if (sqlServer) version.IsRowVersion(); else version.IsConcurrencyToken().ValueGeneratedNever();
        });
    }
}
