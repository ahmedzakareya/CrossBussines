using CrossBuy.Models.Context.Accounting;
using CrossBuy.Models.Context.Inventory;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.Models.Context.Construction;

// Forward-only quantity provenance, not a second monetary ledger. The future invoice writer must
// insert header + slices and seal in ONE owned transaction. Readers must refuse unsealed history.
public sealed class ConstructionPartialInvoice
{
    public int ID { get; set; }
    public int CompanyID { get; set; }
    public int ProjectId { get; set; }
    public int PurchaseOrderId { get; set; }
    public int PurchaseInvoiceId { get; set; }
    public int JournalEntryId { get; set; }
    public Guid RequestId { get; set; }
    public string RequestHash { get; set; } = "";
    public string SourceVersion { get; set; } = "";
    public int CreatedBy { get; set; }
    public DateTime CreatedAt { get; set; }
    public bool IsSealed { get; set; }
}

public sealed class ConstructionPartialInvoiceSlice
{
    public int ID { get; set; }
    public int HeaderId { get; set; }
    public int CompanyID { get; set; }
    public int ProjectId { get; set; }
    public int PurchaseOrderId { get; set; }
    public int PurchaseInvoiceId { get; set; }
    public int JournalEntryId { get; set; }
    public int PurchaseOrderLineId { get; set; }
    public int GoodsReceiptId { get; set; }
    public int GoodsReceiptLineId { get; set; }
    public int PurchaseInvoiceLineId { get; set; }
    public int JournalEntryLineId { get; set; }
    public decimal Quantity { get; set; }
}

public static class ConstructionPartialInvoiceModel
{
    public static void Configure(ModelBuilder builder, CrossDbContext db, bool sqlServer)
    {
        builder.Entity<Project>().HasAlternateKey(x => new { x.ID, x.CompanyID });
        builder.Entity<ConstructionPartialInvoice>(e =>
        {
            e.ToTable("ConstructionPartialInvoices", t => { if (sqlServer) t.UseSqlOutputClause(false); });
            e.HasKey(x => x.ID);
            e.HasAlternateKey(x => new { x.ID, x.CompanyID, x.ProjectId, x.PurchaseOrderId, x.PurchaseInvoiceId, x.JournalEntryId });
            e.HasIndex(x => new { x.CompanyID, x.RequestId }).IsUnique();
            e.HasIndex(x => new { x.CompanyID, x.PurchaseInvoiceId }).IsUnique();
            e.HasIndex(x => new { x.CompanyID, x.PurchaseOrderId });
            e.Property(x => x.RequestHash).HasMaxLength(64).IsRequired();
            e.Property(x => x.SourceVersion).HasMaxLength(64).IsRequired();
            e.HasOne<Project>().WithMany().HasForeignKey(x => new { x.ProjectId, x.CompanyID })
                .HasPrincipalKey(x => new { x.ID, x.CompanyID }).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<PurchaseOrder>().WithMany().HasForeignKey(x => new { x.PurchaseOrderId, x.CompanyID })
                .HasPrincipalKey(x => new { x.ID, x.CompanyID }).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<PurchaseInvoice>().WithMany().HasForeignKey(x => new { x.PurchaseInvoiceId, x.CompanyID })
                .HasPrincipalKey(x => new { x.ID, x.CompanyID }).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<JournalEntry>().WithMany().HasForeignKey(x => new { x.JournalEntryId, x.CompanyID })
                .HasPrincipalKey(x => new { x.ID, x.CompanyID }).OnDelete(DeleteBehavior.Restrict);
            e.HasQueryFilter(x => db.CompanyScope.AllowsCrossCompany || x.CompanyID == db.CompanyScope.FilterCompanyId);
        });
        builder.Entity<ConstructionPartialInvoiceSlice>(e =>
        {
            e.ToTable("ConstructionPartialInvoiceSlices", t => { if (sqlServer) t.UseSqlOutputClause(false); });
            e.HasKey(x => x.ID);
            e.HasIndex(x => new { x.HeaderId, x.PurchaseInvoiceLineId, x.GoodsReceiptLineId }).IsUnique();
            e.HasIndex(x => new { x.CompanyID, x.GoodsReceiptLineId });
            e.HasIndex(x => new { x.CompanyID, x.PurchaseOrderLineId });
            e.HasIndex(x => new { x.CompanyID, x.PurchaseInvoiceLineId });
            e.Property(x => x.Quantity).HasPrecision(28, 8);
            e.HasOne<ConstructionPartialInvoice>().WithMany()
                .HasForeignKey(x => new { x.HeaderId, x.CompanyID, x.ProjectId, x.PurchaseOrderId, x.PurchaseInvoiceId, x.JournalEntryId })
                .HasPrincipalKey(x => new { x.ID, x.CompanyID, x.ProjectId, x.PurchaseOrderId, x.PurchaseInvoiceId, x.JournalEntryId })
                .OnDelete(DeleteBehavior.Restrict);
            e.HasOne<PurchaseOrderLine>().WithMany().HasForeignKey(x => new { x.PurchaseOrderLineId, x.PurchaseOrderId })
                .HasPrincipalKey(x => new { x.ID, x.PurchaseOrderId }).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<PurchaseInvoiceLine>().WithMany().HasForeignKey(x => new { x.PurchaseInvoiceLineId, x.PurchaseInvoiceId })
                .HasPrincipalKey(x => new { x.ID, x.PurchaseInvoiceId }).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<JournalEntryLine>().WithMany().HasForeignKey(x => new { x.JournalEntryLineId, x.JournalEntryId })
                .HasPrincipalKey(x => new { x.ID, x.JournalEntryId }).OnDelete(DeleteBehavior.Restrict);
            // Do not put nullable PO links in receipt alternate keys: that would make non-PO receipts required.
            // The authored INSERT trigger proves receipt company/order/line/item/unit ownership.
            e.HasOne<GoodsReceipt>().WithMany().HasForeignKey(x => x.GoodsReceiptId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<GoodsReceiptLine>().WithMany().HasForeignKey(x => x.GoodsReceiptLineId).OnDelete(DeleteBehavior.Restrict);
            e.HasQueryFilter(x => db.CompanyScope.AllowsCrossCompany || x.CompanyID == db.CompanyScope.FilterCompanyId);
        });
    }
}
