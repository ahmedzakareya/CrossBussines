using Microsoft.EntityFrameworkCore;
using CrossBuy.Models.Context.Inventory;

namespace CrossBuy.Models.Context.Construction;

// Consent to use a specifically named destination after a budget rebase. This is evidence only;
// it is not an inventory grant and cannot authorize the purchase or a stock movement by itself.
public sealed class ConstructionRebasedWarehouseConsentRequest
{
    public int ID { get; set; }
    public int CompanyID { get; set; }
    public int ProjectId { get; set; }
    public int PurchaseOrderId { get; set; }
    public int ExposureRebaseId { get; set; }
    public int DestinationSiteId { get; set; }
    public int SourceWarehouseId { get; set; }
    public string SourceVersion { get; set; } = "";
    public string SourceFactsVersion { get; set; } = "";
    public string MappingVersion { get; set; } = "";
    public byte[] PolicyToken { get; set; } = Array.Empty<byte>();
    public Guid RequestId { get; set; }
    public string RequestHash { get; set; } = "";
    public string Reason { get; set; } = "";
    public int CreatedBy { get; set; }
    public DateTime CreatedAt { get; set; }
}

public sealed class ConstructionRebasedWarehouseConsentDecision
{
    public int ID { get; set; }
    public int CompanyID { get; set; }
    public int ProjectId { get; set; }
    public int RequestRowId { get; set; }
    public string Outcome { get; set; } = "";
    public string SourceFactsVersion { get; set; } = "";
    public Guid DecisionId { get; set; }
    public string RequestHash { get; set; } = "";
    public string Reason { get; set; } = "";
    public int CreatedBy { get; set; }
    public DateTime CreatedAt { get; set; }
}

// Binds an independently approved destination consent to one exact canonical rebased purchase request.
public sealed class ConstructionRebasedWarehouseConsentBinding
{
    public int ID { get; set; }
    public int CompanyID { get; set; }
    public int ProjectId { get; set; }
    public int PurchaseOrderId { get; set; }
    public int ExposureRebaseId { get; set; }
    public int RebaseRequestId { get; set; }
    public int ApprovalRequestId { get; set; }
    public int ConsentRequestRowId { get; set; }
    public int ConsentDecisionRowId { get; set; }
    public int DestinationSiteId { get; set; }
    public int SourceWarehouseId { get; set; }
    public string SourceFactsVersion { get; set; } = "";
    public string MappingVersion { get; set; } = "";
    public byte[] PolicyToken { get; set; } = Array.Empty<byte>();
    public string ReviewVersion { get; set; } = "";
    public int CreatedBy { get; set; }
    public DateTime CreatedAt { get; set; }
}

// Immutable proof that the canonical purchase admission consumed the bound consent.
public sealed class ConstructionRebasedWarehouseConsentApplication
{
    public int ID { get; set; }
    public int CompanyID { get; set; }
    public int ProjectId { get; set; }
    public int PurchaseOrderId { get; set; }
    public int BindingId { get; set; }
    public int AdmissionId { get; set; }
    public Guid RequestId { get; set; }
    public string ReviewVersion { get; set; } = "";
    public int CreatedBy { get; set; }
    public DateTime CreatedAt { get; set; }
}

public static class ConstructionRebasedWarehouseConsentModel
{
    public static void Configure(ModelBuilder builder, CrossDbContext db, bool sqlServer)
    {
        builder.Entity<ConstructionRebasedWarehouseConsentRequest>(e =>
        {
            e.ToTable("ConstructionRebasedWarehouseConsentRequests", t => { if (sqlServer) t.UseSqlOutputClause(false); });
            e.HasKey(x => x.ID);
            e.HasAlternateKey(x => new { x.ID, x.ProjectId, x.CompanyID });
            e.HasIndex(x => new { x.CompanyID, x.RequestId }).IsUnique();
            e.HasIndex(x => new { x.CompanyID, x.ExposureRebaseId, x.DestinationSiteId, x.SourceFactsVersion, x.CreatedBy }).IsUnique();
            e.HasOne<ConstructionReservationExposureRebase>().WithMany()
                .HasForeignKey(x => new { x.ExposureRebaseId, x.ProjectId, x.CompanyID })
                .HasPrincipalKey(x => new { x.ID, x.ProjectId, x.CompanyID }).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<PurchaseOrder>().WithMany()
                .HasForeignKey(x => new { x.PurchaseOrderId, x.CompanyID })
                .HasPrincipalKey(x => new { x.ID, x.CompanyID }).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<ConstructionSite>().WithMany()
                .HasForeignKey(x => new { x.DestinationSiteId, x.ProjectId, x.CompanyID })
                .HasPrincipalKey(x => new { x.ID, x.ProjectId, x.CompanyID }).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<Warehouse>().WithMany()
                .HasForeignKey(x => new { x.SourceWarehouseId, x.CompanyID })
                .HasPrincipalKey(x => new { x.ID, x.CompanyID }).OnDelete(DeleteBehavior.Restrict);
            e.Property(x => x.SourceVersion).HasMaxLength(64).IsRequired();
            e.Property(x => x.SourceFactsVersion).HasMaxLength(64).IsRequired();
            e.Property(x => x.MappingVersion).HasMaxLength(64).IsRequired();
            e.Property(x => x.PolicyToken).HasMaxLength(16).IsRequired();
            e.Property(x => x.RequestHash).HasMaxLength(64).IsRequired();
            e.Property(x => x.Reason).HasMaxLength(1000).IsRequired();
            e.HasQueryFilter(x => db.CompanyScope.AllowsCrossCompany || x.CompanyID == db.CompanyScope.FilterCompanyId);
        });

        builder.Entity<ConstructionRebasedWarehouseConsentDecision>(e =>
        {
            e.ToTable("ConstructionRebasedWarehouseConsentDecisions", t => { if (sqlServer) t.UseSqlOutputClause(false); });
            e.HasKey(x => x.ID);
            e.HasAlternateKey(x => new { x.ID, x.ProjectId, x.CompanyID });
            e.HasIndex(x => new { x.CompanyID, x.DecisionId }).IsUnique();
            e.HasIndex(x => new { x.CompanyID, x.RequestRowId }).IsUnique();
            e.HasOne<ConstructionRebasedWarehouseConsentRequest>().WithMany()
                .HasForeignKey(x => new { x.RequestRowId, x.ProjectId, x.CompanyID })
                .HasPrincipalKey(x => new { x.ID, x.ProjectId, x.CompanyID }).OnDelete(DeleteBehavior.Restrict);
            e.Property(x => x.Outcome).HasMaxLength(16).IsRequired();
            e.Property(x => x.SourceFactsVersion).HasMaxLength(64).IsRequired();
            e.Property(x => x.RequestHash).HasMaxLength(64).IsRequired();
            e.Property(x => x.Reason).HasMaxLength(1000).IsRequired();
            e.HasQueryFilter(x => db.CompanyScope.AllowsCrossCompany || x.CompanyID == db.CompanyScope.FilterCompanyId);
        });

        builder.Entity<ConstructionRebasedWarehouseConsentBinding>(e =>
        {
            e.ToTable("ConstructionRebasedWarehouseConsentBindings", t => { if (sqlServer) t.UseSqlOutputClause(false); });
            e.HasKey(x => x.ID);
            e.HasAlternateKey(x => new { x.ID, x.PurchaseOrderId, x.ProjectId, x.CompanyID });
            e.HasIndex(x => x.ApprovalRequestId).IsUnique();
            e.HasIndex(x => new { x.CompanyID, x.ConsentDecisionRowId }).IsUnique();
            e.HasOne<ConstructionPurchaseApprovalRequest>().WithMany()
                .HasForeignKey(x => new { x.ApprovalRequestId, x.PurchaseOrderId, x.ProjectId, x.CompanyID })
                .HasPrincipalKey(x => new { x.ID, x.PurchaseOrderId, x.ProjectId, x.CompanyID }).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<ConstructionPurchaseRebaseRequest>().WithMany()
                .HasForeignKey(x => new { x.RebaseRequestId, x.PurchaseOrderId, x.ProjectId, x.CompanyID })
                .HasPrincipalKey(x => new { x.ID, x.PurchaseOrderId, x.ProjectId, x.CompanyID }).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<ConstructionRebasedWarehouseConsentRequest>().WithMany()
                .HasForeignKey(x => new { x.ConsentRequestRowId, x.ProjectId, x.CompanyID })
                .HasPrincipalKey(x => new { x.ID, x.ProjectId, x.CompanyID }).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<ConstructionRebasedWarehouseConsentDecision>().WithMany()
                .HasForeignKey(x => new { x.ConsentDecisionRowId, x.ProjectId, x.CompanyID })
                .HasPrincipalKey(x => new { x.ID, x.ProjectId, x.CompanyID }).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<ConstructionReservationExposureRebase>().WithMany()
                .HasForeignKey(x => new { x.ExposureRebaseId, x.ProjectId, x.CompanyID })
                .HasPrincipalKey(x => new { x.ID, x.ProjectId, x.CompanyID }).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<ConstructionSite>().WithMany()
                .HasForeignKey(x => new { x.DestinationSiteId, x.ProjectId, x.CompanyID })
                .HasPrincipalKey(x => new { x.ID, x.ProjectId, x.CompanyID }).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<Warehouse>().WithMany()
                .HasForeignKey(x => new { x.SourceWarehouseId, x.CompanyID })
                .HasPrincipalKey(x => new { x.ID, x.CompanyID }).OnDelete(DeleteBehavior.Restrict);
            e.Property(x => x.SourceFactsVersion).HasMaxLength(64).IsRequired();
            e.Property(x => x.MappingVersion).HasMaxLength(64).IsRequired();
            e.Property(x => x.PolicyToken).HasMaxLength(16).IsRequired();
            e.Property(x => x.ReviewVersion).HasMaxLength(64).IsRequired();
            e.HasQueryFilter(x => db.CompanyScope.AllowsCrossCompany || x.CompanyID == db.CompanyScope.FilterCompanyId);
        });

        builder.Entity<ConstructionRebasedWarehouseConsentApplication>(e =>
        {
            e.ToTable("ConstructionRebasedWarehouseConsentApplications", t => { if (sqlServer) t.UseSqlOutputClause(false); });
            e.HasKey(x => x.ID);
            e.HasIndex(x => new { x.CompanyID, x.RequestId }).IsUnique();
            e.HasIndex(x => x.BindingId).IsUnique();
            e.HasIndex(x => x.AdmissionId).IsUnique();
            e.HasOne<ConstructionRebasedWarehouseConsentBinding>().WithMany()
                .HasForeignKey(x => new { x.BindingId, x.PurchaseOrderId, x.ProjectId, x.CompanyID })
                .HasPrincipalKey(x => new { x.ID, x.PurchaseOrderId, x.ProjectId, x.CompanyID }).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<ConstructionPurchaseAdmission>().WithMany()
                .HasForeignKey(x => new { x.AdmissionId, x.PurchaseOrderId, x.ProjectId, x.CompanyID })
                .HasPrincipalKey(x => new { x.ID, x.PurchaseOrderId, x.ProjectId, x.CompanyID }).OnDelete(DeleteBehavior.Restrict);
            e.Property(x => x.ReviewVersion).HasMaxLength(64).IsRequired();
            e.HasQueryFilter(x => db.CompanyScope.AllowsCrossCompany || x.CompanyID == db.CompanyScope.FilterCompanyId);
        });
    }
}
