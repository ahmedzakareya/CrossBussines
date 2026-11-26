using CrossBuy.Models.Context.Accounting;
using CrossBuy.Models.Context.Inventory;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.Models.Context.Construction;

// Creation receipt only. Neither this fact nor its replay authorizes purchase, receipt or posting.
public sealed class ConstructionPurchaseDraft
{
    public int ID { get; set; }
    public int CompanyID { get; set; }
    public int ProjectId { get; set; }
    public int PurchaseOrderId { get; set; }
    public int BudgetVersionId { get; set; }
    public int CurrencyId { get; set; }
    public int CurrencyDecimals { get; set; }
    public Guid RequestId { get; set; }
    public string RequestHash { get; set; } = "";
    public string InitialSourceVersion { get; set; } = "";
    public string Reason { get; set; } = "";
    public int CreatedBy { get; set; }
    public DateTime CreatedAt { get; set; }
}

public static class ConstructionPurchaseDraftModel
{
    public static void Configure(ModelBuilder builder, CrossDbContext db, bool sqlServer)
    {
        if (sqlServer) builder.Entity<PurchaseOrder>().ToTable("PurchaseOrders", t => t.UseSqlOutputClause(false));
        builder.Entity<ConstructionPurchaseDraft>(e =>
        {
            e.ToTable("ConstructionPurchaseDrafts", t => { if (sqlServer) t.UseSqlOutputClause(false); });
            e.HasKey(x => x.ID);
            e.HasIndex(x => new { x.CompanyID, x.RequestId }).IsUnique();
            e.HasIndex(x => new { x.CompanyID, x.PurchaseOrderId }).IsUnique();
            e.HasOne<PurchaseOrder>().WithMany().HasForeignKey(x => new { x.PurchaseOrderId, x.CompanyID })
                .HasPrincipalKey(x => new { x.ID, x.CompanyID }).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<Project>().WithMany().HasForeignKey(x => new { x.ProjectId, x.CompanyID })
                .HasPrincipalKey(x => new { x.ID, x.CompanyID }).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<ConstructionBudgetVersion>().WithMany().HasForeignKey(x => new { x.BudgetVersionId, x.ProjectId, x.CompanyID })
                .HasPrincipalKey(x => new { x.ID, x.ProjectId, x.CompanyID }).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<Currency>().WithMany().HasForeignKey(x => x.CurrencyId).OnDelete(DeleteBehavior.Restrict);
            e.Property(x => x.RequestHash).HasMaxLength(64).IsRequired();
            e.Property(x => x.InitialSourceVersion).HasMaxLength(64).IsRequired();
            e.Property(x => x.Reason).HasMaxLength(1000).IsRequired();
            e.HasQueryFilter(x => db.CompanyScope.AllowsCrossCompany || x.CompanyID == db.CompanyScope.FilterCompanyId);
        });
    }
}
