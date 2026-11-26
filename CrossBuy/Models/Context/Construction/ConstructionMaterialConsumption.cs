using CrossBuy.Models.Context.Accounting;
using CrossBuy.Models.Context.Inventory;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.Models.Context.Construction;

// Immutable analytical provenance. This row neither posts stock/GL nor releases any reservation.
public sealed class ConstructionMaterialConsumption
{
    public int ID { get; set; }
    public int CompanyID { get; set; }
    public int ProjectId { get; set; }
    public int PurchaseOrderId { get; set; }
    public int PurchaseOrderLineId { get; set; }
    public int GoodsReceiptId { get; set; }
    public int GoodsReceiptLineId { get; set; }
    public int ReceiptMovementId { get; set; }
    public int IssueMovementId { get; set; }
    public int IssueJournalEntryId { get; set; }
    public int ExpenseJournalLineId { get; set; }
    public int ActualAllocationId { get; set; }
    public int ReservationId { get; set; }
    public int FunctionalCurrencyId { get; set; }
    public int FunctionalCurrencyDecimals { get; set; }
    public decimal QuantityBase { get; set; }
    public decimal NetBaseCost { get; set; }
    public string SourceVersion { get; set; } = "";
    public Guid RequestId { get; set; }
    public string RequestHash { get; set; } = "";
    public string Reason { get; set; } = "";
    public int CreatedBy { get; set; }
    public DateTime CreatedAt { get; set; }
}

public static class ConstructionMaterialConsumptionModel
{
    public static void Configure(ModelBuilder builder, CrossDbContext db, bool sqlServer)
    {
        builder.Entity<ConstructionMaterialConsumption>(e =>
        {
            e.ToTable("ConstructionMaterialConsumptions", t => { if (sqlServer) t.UseSqlOutputClause(false); });
            e.HasKey(x => x.ID);
            e.HasIndex(x => new { x.CompanyID, x.RequestId }).IsUnique();
            e.HasIndex(x => new { x.CompanyID, x.IssueMovementId }).IsUnique();
            e.HasIndex(x => new { x.CompanyID, x.ExpenseJournalLineId }).IsUnique();
            e.HasIndex(x => new { x.CompanyID, x.GoodsReceiptLineId });
            e.HasOne<PurchaseOrder>().WithMany().HasForeignKey(x => new { x.PurchaseOrderId, x.CompanyID })
                .HasPrincipalKey(x => new { x.ID, x.CompanyID }).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<PurchaseOrderLine>().WithMany().HasForeignKey(x => new { x.PurchaseOrderLineId, x.PurchaseOrderId })
                .HasPrincipalKey(x => new { x.ID, x.PurchaseOrderId }).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<GoodsReceipt>().WithMany().HasForeignKey(x => x.GoodsReceiptId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<GoodsReceiptLine>().WithMany().HasForeignKey(x => x.GoodsReceiptLineId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<StockMovement>().WithMany().HasForeignKey(x => x.ReceiptMovementId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<StockMovement>().WithMany().HasForeignKey(x => x.IssueMovementId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<JournalEntry>().WithMany().HasForeignKey(x => new { x.IssueJournalEntryId, x.CompanyID })
                .HasPrincipalKey(x => new { x.ID, x.CompanyID }).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<JournalEntryLine>().WithMany().HasForeignKey(x => new { x.ExpenseJournalLineId, x.IssueJournalEntryId })
                .HasPrincipalKey(x => new { x.ID, x.JournalEntryId }).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<ConstructionActualCostAllocation>().WithMany().HasForeignKey(x => new { x.ActualAllocationId, x.ProjectId, x.CompanyID })
                .HasPrincipalKey(x => new { x.ID, x.ProjectId, x.CompanyID }).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<ConstructionCommitmentReservation>().WithMany().HasForeignKey(x => new { x.ReservationId, x.ProjectId, x.CompanyID })
                .HasPrincipalKey(x => new { x.ID, x.ProjectId, x.CompanyID }).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<Currency>().WithMany().HasForeignKey(x => x.FunctionalCurrencyId).OnDelete(DeleteBehavior.Restrict);
            e.Property(x => x.QuantityBase).HasPrecision(28, 8); e.Property(x => x.NetBaseCost).HasPrecision(28, 8);
            e.Property(x => x.SourceVersion).HasMaxLength(64).IsRequired(); e.Property(x => x.RequestHash).HasMaxLength(64).IsRequired();
            e.Property(x => x.Reason).HasMaxLength(1000).IsRequired();
            e.HasQueryFilter(x => db.CompanyScope.AllowsCrossCompany || x.CompanyID == db.CompanyScope.FilterCompanyId);
        });
    }
}
