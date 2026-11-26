using CrossBuy.Models.Context.Accounting;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.Models.Context.Construction;

// Append-only admission/release facts; no duplicate financial ledger and no mutable balance.
public sealed class ConstructionCommitmentReservation
{
    public int ID { get; set; }
    public int CompanyID { get; set; }
    public int ProjectId { get; set; }
    public int PurchaseOrderId { get; set; }
    public int AllocationId { get; set; }
    public int BudgetVersionId { get; set; }
    public int CurrencyId { get; set; }
    public decimal NetDocumentAmount { get; set; }
    public Guid RequestId { get; set; }
    public string RequestHash { get; set; } = "";
    public string SourceVersion { get; set; } = "";
    public string PolicyMode { get; set; } = "";
    public string DecisionCode { get; set; } = "";
    public string Reason { get; set; } = "";
    public int CreatedBy { get; set; }
    public DateTime CreatedAt { get; set; }
}

public sealed class ConstructionCommitmentRelease
{
    public int ID { get; set; }
    public int CompanyID { get; set; }
    public int ProjectId { get; set; }
    public int ReservationId { get; set; }
    public Guid RequestId { get; set; }
    public string RequestHash { get; set; } = "";
    public string Reason { get; set; } = "";
    public int CreatedBy { get; set; }
    public DateTime CreatedAt { get; set; }
}

public static class ConstructionCommitmentReservationModel
{
    public static void Configure(ModelBuilder builder, CrossDbContext db, bool sqlServer)
    {
        builder.Entity<ConstructionCommitmentReservation>(e =>
        {
            e.ToTable("ConstructionCommitmentReservations", t => { if (sqlServer) t.UseSqlOutputClause(false); });
            e.HasKey(x => x.ID); e.HasAlternateKey(x => new { x.ID, x.ProjectId, x.CompanyID });
            e.HasIndex(x => new { x.CompanyID, x.RequestId }).IsUnique();
            // Amendments/reopening need their own future workflow; never silently re-admit a processed PO.
            e.HasIndex(x => new { x.CompanyID, x.PurchaseOrderId }).IsUnique();
            e.HasOne<ConstructionOrderAllocation>().WithMany().HasForeignKey(x => new { x.AllocationId, x.PurchaseOrderId, x.ProjectId, x.CompanyID })
                .HasPrincipalKey(x => new { x.ID, x.PurchaseOrderId, x.ProjectId, x.CompanyID }).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<ConstructionBudgetVersion>().WithMany().HasForeignKey(x => new { x.BudgetVersionId, x.ProjectId, x.CompanyID })
                .HasPrincipalKey(x => new { x.ID, x.ProjectId, x.CompanyID }).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<Currency>().WithMany().HasForeignKey(x => x.CurrencyId).OnDelete(DeleteBehavior.Restrict);
            e.Property(x => x.NetDocumentAmount).HasPrecision(28, 8);
            e.Property(x => x.RequestHash).HasMaxLength(64).IsRequired(); e.Property(x => x.SourceVersion).HasMaxLength(64).IsRequired();
            e.Property(x => x.PolicyMode).HasMaxLength(30).IsRequired(); e.Property(x => x.DecisionCode).HasMaxLength(40).IsRequired();
            e.Property(x => x.Reason).HasMaxLength(1000).IsRequired();
            e.HasQueryFilter(x => db.CompanyScope.AllowsCrossCompany || x.CompanyID == db.CompanyScope.FilterCompanyId);
        });
        builder.Entity<ConstructionCommitmentRelease>(e =>
        {
            e.ToTable("ConstructionCommitmentReleases", t => { if (sqlServer) t.UseSqlOutputClause(false); });
            e.HasKey(x => x.ID); e.HasIndex(x => new { x.CompanyID, x.RequestId }).IsUnique();
            e.HasIndex(x => x.ReservationId).IsUnique();
            e.HasOne<ConstructionCommitmentReservation>().WithMany().HasForeignKey(x => new { x.ReservationId, x.ProjectId, x.CompanyID })
                .HasPrincipalKey(x => new { x.ID, x.ProjectId, x.CompanyID }).OnDelete(DeleteBehavior.Restrict);
            e.Property(x => x.RequestHash).HasMaxLength(64).IsRequired(); e.Property(x => x.Reason).HasMaxLength(1000).IsRequired();
            e.HasQueryFilter(x => db.CompanyScope.AllowsCrossCompany || x.CompanyID == db.CompanyScope.FilterCompanyId);
        });
    }
}
