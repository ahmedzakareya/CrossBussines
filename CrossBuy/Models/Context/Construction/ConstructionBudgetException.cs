using CrossBuy.Models.Context.Accounting;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.Models.Context.Construction;

// Immutable workflow facts. Approval does not change the approved budget or write a financial ledger.
public sealed class ConstructionBudgetExceptionRequest
{
    public int ID { get; set; }
    public int CompanyID { get; set; }
    public int ProjectId { get; set; }
    public int PurchaseOrderId { get; set; }
    public int AllocationId { get; set; }
    public int BudgetVersionId { get; set; }
    public int CurrencyId { get; set; }
    public string SourceVersion { get; set; } = "";
    public string AdmissionVersion { get; set; } = "";
    public decimal ProposedAmount { get; set; }
    public decimal Overrun { get; set; }
    public Guid RequestId { get; set; }
    public string RequestHash { get; set; } = "";
    public string Reason { get; set; } = "";
    public int CreatedBy { get; set; }
    public DateTime CreatedAt { get; set; }
}

public sealed class ConstructionBudgetExceptionDecision
{
    public int ID { get; set; }
    public int CompanyID { get; set; }
    public int ProjectId { get; set; }
    public int ExceptionRequestId { get; set; }
    public string Outcome { get; set; } = "";
    public Guid RequestId { get; set; }
    public string RequestHash { get; set; } = "";
    public string Reason { get; set; } = "";
    public int CreatedBy { get; set; }
    public DateTime CreatedAt { get; set; }
}

public sealed class ConstructionBudgetExceptionConsumption
{
    public int ID { get; set; }
    public int CompanyID { get; set; }
    public int ProjectId { get; set; }
    public int ExceptionRequestId { get; set; }
    public int ReservationId { get; set; }
    public DateTime CreatedAt { get; set; }
}

public static class ConstructionBudgetExceptionModel
{
    public static void Configure(ModelBuilder builder, CrossDbContext db, bool sqlServer)
    {
        builder.Entity<ConstructionBudgetExceptionRequest>(e =>
        {
            e.ToTable("ConstructionBudgetExceptionRequests", t => { if (sqlServer) t.UseSqlOutputClause(false); });
            e.HasKey(x => x.ID); e.HasAlternateKey(x => new { x.ID, x.ProjectId, x.CompanyID });
            e.HasIndex(x => new { x.CompanyID, x.RequestId }).IsUnique();
            e.HasOne<ConstructionOrderAllocation>().WithMany().HasForeignKey(x => new { x.AllocationId, x.PurchaseOrderId, x.ProjectId, x.CompanyID })
                .HasPrincipalKey(x => new { x.ID, x.PurchaseOrderId, x.ProjectId, x.CompanyID }).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<ConstructionBudgetVersion>().WithMany().HasForeignKey(x => new { x.BudgetVersionId, x.ProjectId, x.CompanyID })
                .HasPrincipalKey(x => new { x.ID, x.ProjectId, x.CompanyID }).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<Currency>().WithMany().HasForeignKey(x => x.CurrencyId).OnDelete(DeleteBehavior.Restrict);
            e.Property(x => x.SourceVersion).HasMaxLength(64).IsRequired();
            e.Property(x => x.AdmissionVersion).HasMaxLength(64).IsRequired();
            e.Property(x => x.RequestHash).HasMaxLength(64).IsRequired();
            e.Property(x => x.Reason).HasMaxLength(1000).IsRequired();
            e.Property(x => x.ProposedAmount).HasPrecision(28, 8); e.Property(x => x.Overrun).HasPrecision(28, 8);
            e.HasQueryFilter(x => db.CompanyScope.AllowsCrossCompany || x.CompanyID == db.CompanyScope.FilterCompanyId);
        });
        builder.Entity<ConstructionBudgetExceptionDecision>(e =>
        {
            e.ToTable("ConstructionBudgetExceptionDecisions", t => { if (sqlServer) t.UseSqlOutputClause(false); });
            e.HasKey(x => x.ID); e.HasIndex(x => new { x.CompanyID, x.RequestId }).IsUnique();
            e.HasIndex(x => x.ExceptionRequestId).IsUnique();
            e.HasOne<ConstructionBudgetExceptionRequest>().WithMany().HasForeignKey(x => new { x.ExceptionRequestId, x.ProjectId, x.CompanyID })
                .HasPrincipalKey(x => new { x.ID, x.ProjectId, x.CompanyID }).OnDelete(DeleteBehavior.Restrict);
            e.Property(x => x.Outcome).HasMaxLength(20).IsRequired(); e.Property(x => x.RequestHash).HasMaxLength(64).IsRequired();
            e.Property(x => x.Reason).HasMaxLength(1000).IsRequired();
            e.HasQueryFilter(x => db.CompanyScope.AllowsCrossCompany || x.CompanyID == db.CompanyScope.FilterCompanyId);
        });
        builder.Entity<ConstructionBudgetExceptionConsumption>(e =>
        {
            e.ToTable("ConstructionBudgetExceptionConsumptions", t => { if (sqlServer) t.UseSqlOutputClause(false); });
            e.HasKey(x => x.ID); e.HasIndex(x => x.ExceptionRequestId).IsUnique(); e.HasIndex(x => x.ReservationId).IsUnique();
            e.HasOne<ConstructionBudgetExceptionRequest>().WithMany().HasForeignKey(x => new { x.ExceptionRequestId, x.ProjectId, x.CompanyID })
                .HasPrincipalKey(x => new { x.ID, x.ProjectId, x.CompanyID }).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<ConstructionCommitmentReservation>().WithMany().HasForeignKey(x => new { x.ReservationId, x.ProjectId, x.CompanyID })
                .HasPrincipalKey(x => new { x.ID, x.ProjectId, x.CompanyID }).OnDelete(DeleteBehavior.Restrict);
            e.HasQueryFilter(x => db.CompanyScope.AllowsCrossCompany || x.CompanyID == db.CompanyScope.FilterCompanyId);
        });
    }
}
