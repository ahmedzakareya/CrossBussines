using Microsoft.EntityFrameworkCore;

namespace CrossBuy.Models.Context.Construction;

// Immutable bridge to the canonical pending purchase approval. Not a purchase admission.
public sealed class ConstructionPurchaseRebaseRequest
{
    public int ID { get; set; }
    public int CompanyID { get; set; }
    public int ProjectId { get; set; }
    public int PurchaseOrderId { get; set; }
    public int ApprovalRequestId { get; set; }
    public int ExposureRebaseId { get; set; }
    public string ReviewVersion { get; set; } = "";
    public int CreatedBy { get; set; }
    public DateTime CreatedAt { get; set; }
}

public static class ConstructionPurchaseRebaseRequestModel
{
    public static void Configure(ModelBuilder builder, CrossDbContext db, bool sqlServer)
    {
        builder.Entity<ConstructionReservationExposureRebase>().HasAlternateKey(x => new { x.ID, x.ProjectId, x.CompanyID });
        builder.Entity<ConstructionPurchaseRebaseRequest>(e =>
        {
            e.ToTable("ConstructionPurchaseRebaseRequests", t => { if (sqlServer) t.UseSqlOutputClause(false); });
            e.HasKey(x => x.ID);
            e.HasIndex(x => x.ApprovalRequestId).IsUnique();
            e.HasOne<ConstructionPurchaseApprovalRequest>().WithMany()
                .HasForeignKey(x => new { x.ApprovalRequestId, x.PurchaseOrderId, x.ProjectId, x.CompanyID })
                .HasPrincipalKey(x => new { x.ID, x.PurchaseOrderId, x.ProjectId, x.CompanyID }).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<ConstructionReservationExposureRebase>().WithMany()
                .HasForeignKey(x => new { x.ExposureRebaseId, x.ProjectId, x.CompanyID })
                .HasPrincipalKey(x => new { x.ID, x.ProjectId, x.CompanyID }).OnDelete(DeleteBehavior.Restrict);
            e.Property(x => x.ReviewVersion).HasMaxLength(64).IsRequired();
            e.HasQueryFilter(x => db.CompanyScope.AllowsCrossCompany || x.CompanyID == db.CompanyScope.FilterCompanyId);
        });
    }
}
