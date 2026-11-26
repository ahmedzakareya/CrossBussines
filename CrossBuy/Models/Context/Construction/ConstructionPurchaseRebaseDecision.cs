using Microsoft.EntityFrameworkCore;

namespace CrossBuy.Models.Context.Construction;

// Independently reviewed grant, consumed by the canonical admission in the same transaction.
public sealed class ConstructionPurchaseRebaseDecision
{
    public int ID { get; set; }
    public int CompanyID { get; set; }
    public int ProjectId { get; set; }
    public int PurchaseOrderId { get; set; }
    public int RebaseRequestId { get; set; }
    public int ReservationId { get; set; }
    public int TargetBudgetVersionId { get; set; }
    public string RequestVersion { get; set; } = "";
    public string SourceVersion { get; set; } = "";
    public string ReviewVersion { get; set; } = "";
    public Guid RequestId { get; set; }
    public string RequestHash { get; set; } = "";
    public string Reason { get; set; } = "";
    public int CreatedBy { get; set; }
    public DateTime CreatedAt { get; set; }
}

public static class ConstructionPurchaseRebaseDecisionModel
{
    public static void Configure(ModelBuilder builder, CrossDbContext db, bool sqlServer)
    {
        builder.Entity<ConstructionPurchaseRebaseRequest>().HasAlternateKey(x => new { x.ID, x.PurchaseOrderId, x.ProjectId, x.CompanyID });
        builder.Entity<ConstructionPurchaseRebaseDecision>(e =>
        {
            e.ToTable("ConstructionPurchaseRebaseDecisions", t => { if (sqlServer) t.UseSqlOutputClause(false); });
            e.HasKey(x => x.ID);
            e.HasIndex(x => new { x.CompanyID, x.RequestId }).IsUnique();
            e.HasIndex(x => x.RebaseRequestId).IsUnique();
            e.HasIndex(x => new { x.CompanyID, x.PurchaseOrderId }).IsUnique();
            e.HasOne<ConstructionPurchaseRebaseRequest>().WithMany()
                .HasForeignKey(x => new { x.RebaseRequestId, x.PurchaseOrderId, x.ProjectId, x.CompanyID })
                .HasPrincipalKey(x => new { x.ID, x.PurchaseOrderId, x.ProjectId, x.CompanyID }).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<ConstructionCommitmentReservation>().WithMany()
                .HasForeignKey(x => new { x.ReservationId, x.ProjectId, x.CompanyID })
                .HasPrincipalKey(x => new { x.ID, x.ProjectId, x.CompanyID }).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<ConstructionBudgetVersion>().WithMany()
                .HasForeignKey(x => new { x.TargetBudgetVersionId, x.ProjectId, x.CompanyID })
                .HasPrincipalKey(x => new { x.ID, x.ProjectId, x.CompanyID }).OnDelete(DeleteBehavior.Restrict);
            e.Property(x => x.RequestVersion).HasMaxLength(64).IsRequired();
            e.Property(x => x.SourceVersion).HasMaxLength(64).IsRequired();
            e.Property(x => x.ReviewVersion).HasMaxLength(64).IsRequired();
            e.Property(x => x.RequestHash).HasMaxLength(64).IsRequired();
            e.Property(x => x.Reason).HasMaxLength(1000).IsRequired();
            e.HasQueryFilter(x => db.CompanyScope.AllowsCrossCompany || x.CompanyID == db.CompanyScope.FilterCompanyId);
        });
    }
}
