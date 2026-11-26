using CrossBuy.Models.Context.Inventory;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.Models.Context.Construction;

// Immutable analytical allocation revisions. These are not spending reservations or GL balances.
public sealed class ConstructionOrderAllocation
{
    public int ID { get; set; }
    public int CompanyID { get; set; }
    public int ProjectId { get; set; }
    public int PurchaseOrderId { get; set; }
    public int BudgetVersionId { get; set; }
    public int RevisionNo { get; set; }
    public Guid RequestId { get; set; }
    public string RequestHash { get; set; } = "";
    public string SourceVersion { get; set; } = "";
    public string Reason { get; set; } = "";
    public int CreatedBy { get; set; }
    public DateTime CreatedAt { get; set; }
    public bool IsSealed { get; set; }
}

public sealed class ConstructionOrderAllocationLine
{
    public int ID { get; set; }
    public int CompanyID { get; set; }
    public int ProjectId { get; set; }
    public int AllocationId { get; set; }
    public int PurchaseOrderId { get; set; }
    public int SourceLineId { get; set; }
    public int WbsNodeId { get; set; }
    public int CostCodeId { get; set; }
    public string CostCategory { get; set; } = "";
    public decimal NetDocumentAmount { get; set; }
}

public static class ConstructionOrderAllocationModel
{
    public static void Configure(ModelBuilder builder, CrossDbContext db, bool sqlServer)
    {
        builder.Entity<PurchaseOrderLine>().HasAlternateKey(x => new { x.ID, x.PurchaseOrderId });
        builder.Entity<ConstructionOrderAllocation>(e =>
        {
            e.ToTable("ConstructionOrderAllocations", t => { if (sqlServer) t.UseSqlOutputClause(false); });
            e.HasKey(x => x.ID);
            e.HasAlternateKey(x => new { x.ID, x.PurchaseOrderId, x.ProjectId, x.CompanyID });
            e.HasIndex(x => new { x.CompanyID, x.RequestId }).IsUnique();
            e.HasIndex(x => new { x.CompanyID, x.PurchaseOrderId, x.RevisionNo }).IsUnique();
            e.HasOne<PurchaseOrder>().WithMany().HasForeignKey(x => new { x.PurchaseOrderId, x.CompanyID })
                .HasPrincipalKey(x => new { x.ID, x.CompanyID }).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<ConstructionBudgetVersion>().WithMany().HasForeignKey(x => new { x.BudgetVersionId, x.ProjectId, x.CompanyID })
                .HasPrincipalKey(x => new { x.ID, x.ProjectId, x.CompanyID }).OnDelete(DeleteBehavior.Restrict);
            e.Property(x => x.RequestHash).HasMaxLength(64).IsRequired();
            e.Property(x => x.SourceVersion).HasMaxLength(64).IsRequired();
            e.Property(x => x.Reason).HasMaxLength(1000).IsRequired();
            e.HasQueryFilter(x => db.CompanyScope.AllowsCrossCompany || x.CompanyID == db.CompanyScope.FilterCompanyId);
        });
        builder.Entity<ConstructionOrderAllocationLine>(e =>
        {
            e.ToTable("ConstructionOrderAllocationLines", t => { if (sqlServer) t.UseSqlOutputClause(false); });
            e.HasKey(x => x.ID);
            e.HasIndex(x => new { x.AllocationId, x.SourceLineId, x.WbsNodeId, x.CostCodeId }).IsUnique();
            e.HasOne<ConstructionOrderAllocation>().WithMany()
                .HasForeignKey(x => new { x.AllocationId, x.PurchaseOrderId, x.ProjectId, x.CompanyID })
                .HasPrincipalKey(x => new { x.ID, x.PurchaseOrderId, x.ProjectId, x.CompanyID }).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<PurchaseOrderLine>().WithMany().HasForeignKey(x => new { x.SourceLineId, x.PurchaseOrderId })
                .HasPrincipalKey(x => new { x.ID, x.PurchaseOrderId }).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<WbsNode>().WithMany().HasForeignKey(x => new { x.WbsNodeId, x.ProjectId, x.CompanyID })
                .HasPrincipalKey(x => new { x.ID, x.ProjectId, x.CompanyID }).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<ConstructionCostCode>().WithMany().HasForeignKey(x => new { x.CostCodeId, x.CompanyID, x.CostCategory })
                .HasPrincipalKey(x => new { x.ID, x.CompanyID, x.Category }).OnDelete(DeleteBehavior.Restrict);
            e.Property(x => x.CostCategory).HasMaxLength(30).IsRequired();
            e.Property(x => x.NetDocumentAmount).HasPrecision(28, 8);
            e.HasQueryFilter(x => db.CompanyScope.AllowsCrossCompany || x.CompanyID == db.CompanyScope.FilterCompanyId);
        });
    }
}
