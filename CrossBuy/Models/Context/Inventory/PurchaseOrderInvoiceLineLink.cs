using CrossBuy.Models.Context.Accounting;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.Models.Context.Inventory;

// Forward-only writer provenance. These identities do not value or release a commitment.
public sealed class PurchaseOrderInvoiceLineLink
{
    public int ID { get; set; }
    public int CompanyID { get; set; }
    public int PurchaseOrderId { get; set; }
    public int PurchaseOrderLineId { get; set; }
    public int PurchaseInvoiceId { get; set; }
    public int PurchaseInvoiceLineId { get; set; }
    public int JournalEntryId { get; set; }
    public int JournalEntryLineId { get; set; }
    public DateTime CreatedAt { get; set; }
}

public static class PurchaseOrderInvoiceLineLinkModel
{
    public static void Configure(ModelBuilder builder, CrossDbContext db, bool sqlServer)
    {
        builder.Entity<PurchaseOrderInvoiceLink>().HasAlternateKey(x => new { x.PurchaseOrderId, x.PurchaseInvoiceId, x.CompanyID });
        builder.Entity<PurchaseOrderLine>().HasAlternateKey(x => new { x.ID, x.PurchaseOrderId });
        builder.Entity<PurchaseInvoiceLine>().HasAlternateKey(x => new { x.ID, x.PurchaseInvoiceId });
        builder.Entity<JournalEntry>().HasAlternateKey(x => new { x.ID, x.CompanyID });
        builder.Entity<JournalEntryLine>().HasAlternateKey(x => new { x.ID, x.JournalEntryId });
        builder.Entity<PurchaseOrderInvoiceLineLink>(e =>
        {
            e.ToTable("PurchaseOrderInvoiceLineLinks", t => { if (sqlServer) t.UseSqlOutputClause(false); });
            e.HasKey(x => x.ID);
            e.HasIndex(x => new { x.CompanyID, x.PurchaseOrderLineId }).IsUnique();
            e.HasIndex(x => new { x.CompanyID, x.PurchaseInvoiceLineId }).IsUnique();
            e.HasIndex(x => new { x.CompanyID, x.JournalEntryLineId }).IsUnique();
            e.HasOne<PurchaseOrderInvoiceLink>().WithMany().HasForeignKey(x => new { x.PurchaseOrderId, x.PurchaseInvoiceId, x.CompanyID })
                .HasPrincipalKey(x => new { x.PurchaseOrderId, x.PurchaseInvoiceId, x.CompanyID }).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<PurchaseOrderLine>().WithMany().HasForeignKey(x => new { x.PurchaseOrderLineId, x.PurchaseOrderId })
                .HasPrincipalKey(x => new { x.ID, x.PurchaseOrderId }).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<PurchaseInvoiceLine>().WithMany().HasForeignKey(x => new { x.PurchaseInvoiceLineId, x.PurchaseInvoiceId })
                .HasPrincipalKey(x => new { x.ID, x.PurchaseInvoiceId }).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<JournalEntry>().WithMany().HasForeignKey(x => new { x.JournalEntryId, x.CompanyID })
                .HasPrincipalKey(x => new { x.ID, x.CompanyID }).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<JournalEntryLine>().WithMany().HasForeignKey(x => new { x.JournalEntryLineId, x.JournalEntryId })
                .HasPrincipalKey(x => new { x.ID, x.JournalEntryId }).OnDelete(DeleteBehavior.Restrict);
            e.HasQueryFilter(x => db.CompanyScope.AllowsCrossCompany || x.CompanyID == db.CompanyScope.FilterCompanyId);
        });
    }
}
