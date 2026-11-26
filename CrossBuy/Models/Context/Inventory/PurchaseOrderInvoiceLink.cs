using CrossBuy.Models.Context.Accounting;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.Models.Context.Inventory;

// Durable conversion provenance, not an amount, realization balance or second financial ledger.
public sealed class PurchaseOrderInvoiceLink
{
    public int ID { get; set; }
    public int CompanyID { get; set; }
    public int PurchaseOrderId { get; set; }
    public int PurchaseInvoiceId { get; set; }
    public DateTime CreatedAt { get; set; }
    public string? CreatedByUserId { get; set; }
    public byte[] RowVersion { get; set; } = Guid.NewGuid().ToByteArray();
}

public static class PurchaseOrderInvoiceLinkModel
{
    public static void Configure(ModelBuilder builder, CrossDbContext db, bool sqlServer)
    {
        builder.Entity<PurchaseOrder>().HasAlternateKey(x => new { x.ID, x.CompanyID });
        builder.Entity<PurchaseInvoice>().HasAlternateKey(x => new { x.ID, x.CompanyID });
        builder.Entity<PurchaseOrderInvoiceLink>(e =>
        {
            e.ToTable("PurchaseOrderInvoiceLinks", t => { if (sqlServer) t.UseSqlOutputClause(false); });
            e.HasKey(x => x.ID);
            e.HasIndex(x => new { x.CompanyID, x.PurchaseOrderId }).IsUnique();
            e.HasIndex(x => new { x.CompanyID, x.PurchaseInvoiceId }).IsUnique();
            e.HasOne<PurchaseOrder>().WithMany().HasForeignKey(x => new { x.PurchaseOrderId, x.CompanyID })
                .HasPrincipalKey(x => new { x.ID, x.CompanyID }).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<PurchaseInvoice>().WithMany().HasForeignKey(x => new { x.PurchaseInvoiceId, x.CompanyID })
                .HasPrincipalKey(x => new { x.ID, x.CompanyID }).OnDelete(DeleteBehavior.Restrict);
            e.Property(x => x.CreatedByUserId).HasMaxLength(450);
            e.HasQueryFilter(x => db.CompanyScope.AllowsCrossCompany || x.CompanyID == db.CompanyScope.FilterCompanyId);
            var version = e.Property(x => x.RowVersion);
            if (sqlServer) version.IsRowVersion(); else version.IsConcurrencyToken().ValueGeneratedNever();
        });
    }
}
