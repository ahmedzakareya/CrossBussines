using CrossBuy.Models.Context.Inventory;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.Models.Context.Construction;

// Immutable request snapshot. Rejection stays in its canonical InventoryApproval, never erases the request.
public sealed class ConstructionPurchaseApprovalRequest
{
    public int ID { get; set; }
    public int CompanyID { get; set; }
    public int ProjectId { get; set; }
    public int PurchaseOrderId { get; set; }
    public int DraftId { get; set; }
    public int AllocationId { get; set; }
    public int ApprovalId { get; set; }
    public int? ExceptionId { get; set; }
    public Guid RequestId { get; set; }
    public string RequestHash { get; set; } = "";
    public string SourceVersion { get; set; } = "";
    public string ContentHash { get; set; } = "";
    public string PayloadJson { get; set; } = "";
    public string Reason { get; set; } = "";
    public int CreatedBy { get; set; }
    public DateTime CreatedAt { get; set; }
}

// One admitted transition per source. Neither its replay nor a prior reservation grants new admission.
public sealed class ConstructionPurchaseAdmission
{
    public int ID { get; set; }
    public int CompanyID { get; set; }
    public int ProjectId { get; set; }
    public int PurchaseOrderId { get; set; }
    public int ApprovalRequestId { get; set; }
    public int ReservationId { get; set; }
    public Guid RequestId { get; set; }
    public string RequestHash { get; set; } = "";
    public string BeforeVersion { get; set; } = "";
    public string ApprovedVersion { get; set; } = "";
    public string ContentHash { get; set; } = "";
    public string Reason { get; set; } = "";
    public int CreatedBy { get; set; }
    public DateTime CreatedAt { get; set; }
}

public static class ConstructionPurchaseApprovalModel
{
    public static void Configure(ModelBuilder builder, CrossDbContext db, bool sqlServer)
    {
        builder.Entity<InventoryApproval>().HasAlternateKey(x => new { x.ID, x.CompanyID });
        builder.Entity<InventoryApproval>().Property(x => x.Amount).HasPrecision(28, 8);
        builder.Entity<ConstructionPurchaseDraft>().HasAlternateKey(x => new { x.ID, x.PurchaseOrderId, x.ProjectId, x.CompanyID });
        if (sqlServer)
        {
            builder.Entity<InventoryApproval>().ToTable("InventoryApprovals", t => t.UseSqlOutputClause(false));
            builder.Entity<PurchaseOrderLine>().ToTable("PurchaseOrderLines", t => t.UseSqlOutputClause(false));
        }
        builder.Entity<ConstructionPurchaseApprovalRequest>(e =>
        {
            e.ToTable("ConstructionPurchaseApprovalRequests", t => { if (sqlServer) t.UseSqlOutputClause(false); });
            e.HasKey(x => x.ID);
            e.HasAlternateKey(x => new { x.ID, x.PurchaseOrderId, x.ProjectId, x.CompanyID });
            e.HasIndex(x => new { x.CompanyID, x.RequestId }).IsUnique();
            e.HasIndex(x => new { x.CompanyID, x.ApprovalId }).IsUnique();
            e.HasOne<ConstructionPurchaseDraft>().WithMany().HasForeignKey(x => new { x.DraftId, x.PurchaseOrderId, x.ProjectId, x.CompanyID })
                .HasPrincipalKey(x => new { x.ID, x.PurchaseOrderId, x.ProjectId, x.CompanyID }).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<ConstructionOrderAllocation>().WithMany().HasForeignKey(x => new { x.AllocationId, x.PurchaseOrderId, x.ProjectId, x.CompanyID })
                .HasPrincipalKey(x => new { x.ID, x.PurchaseOrderId, x.ProjectId, x.CompanyID }).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<InventoryApproval>().WithMany().HasForeignKey(x => new { x.ApprovalId, x.CompanyID })
                .HasPrincipalKey(x => new { x.ID, x.CompanyID }).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<ConstructionBudgetExceptionRequest>().WithMany().HasForeignKey(x => new { x.ExceptionId, x.ProjectId, x.CompanyID })
                .HasPrincipalKey(x => new { x.ID, x.ProjectId, x.CompanyID }).OnDelete(DeleteBehavior.Restrict);
            e.Property(x => x.RequestHash).HasMaxLength(64).IsRequired();
            e.Property(x => x.SourceVersion).HasMaxLength(64).IsRequired();
            e.Property(x => x.ContentHash).HasMaxLength(64).IsRequired();
            e.Property(x => x.Reason).HasMaxLength(1000).IsRequired();
            e.HasQueryFilter(x => db.CompanyScope.AllowsCrossCompany || x.CompanyID == db.CompanyScope.FilterCompanyId);
        });
        builder.Entity<ConstructionPurchaseAdmission>(e =>
        {
            e.ToTable("ConstructionPurchaseAdmissions", t => { if (sqlServer) t.UseSqlOutputClause(false); });
            e.HasKey(x => x.ID);
            e.HasAlternateKey(x => new { x.ID, x.PurchaseOrderId, x.ProjectId, x.CompanyID });
            e.HasIndex(x => new { x.CompanyID, x.RequestId }).IsUnique();
            e.HasIndex(x => new { x.CompanyID, x.PurchaseOrderId }).IsUnique();
            e.HasIndex(x => x.ApprovalRequestId).IsUnique();
            e.HasOne<ConstructionPurchaseApprovalRequest>().WithMany().HasForeignKey(x => new { x.ApprovalRequestId, x.PurchaseOrderId, x.ProjectId, x.CompanyID })
                .HasPrincipalKey(x => new { x.ID, x.PurchaseOrderId, x.ProjectId, x.CompanyID }).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<ConstructionCommitmentReservation>().WithMany().HasForeignKey(x => new { x.ReservationId, x.ProjectId, x.CompanyID })
                .HasPrincipalKey(x => new { x.ID, x.ProjectId, x.CompanyID }).OnDelete(DeleteBehavior.Restrict);
            e.Property(x => x.RequestHash).HasMaxLength(64).IsRequired();
            e.Property(x => x.BeforeVersion).HasMaxLength(64).IsRequired();
            e.Property(x => x.ApprovedVersion).HasMaxLength(64).IsRequired();
            e.Property(x => x.ContentHash).HasMaxLength(64).IsRequired();
            e.Property(x => x.Reason).HasMaxLength(1000).IsRequired();
            e.HasQueryFilter(x => db.CompanyScope.AllowsCrossCompany || x.CompanyID == db.CompanyScope.FilterCompanyId);
        });
    }
}
