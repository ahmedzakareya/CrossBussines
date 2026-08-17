using CrossBuy.Models.Context;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL.Platform
{
    // Platform Kernel slice 2 (ADR-005) — reconstructs pre-kernel Purchase Invoice history at read time.
    //
    // HISTORICAL SOURCES FOUND:
    //   1. PurchaseInvoice.CreatedAt (falling back to InvoiceDate) + InvoiceNo + GrandTotal — the recording fact.
    //   2. JournalEntries where SourceType='PurchaseInvoice' — index 0 is the original posting (already
    //      covered by item 1); each subsequent entry is a re-post produced by EditPurchaseInvoiceAsync.
    //      Reversal entries carry SourceType='Reversal', and edit-time stock reversals use
    //      SourceType='PurchaseInvoiceEdit' on stock movements, so neither can double-count here.
    //
    // NOT used: DocComment (authored discussion, rendered by the comments widget already on this screen),
    // PurchaseInvoiceLine (no per-line history), Notification (delivery records, not business history).
    // There is no status-history table for purchase invoices.
    //
    // This mirrors SalesInvoiceLegacyTimelineAdapter deliberately: the two documents have the same lifecycle
    // shape in this codebase, so keeping the reconstruction identical keeps their timelines comparable.
    public class PurchaseInvoiceLegacyTimelineAdapter : ILegacyTimelineAdapter
    {
        private readonly CrossDbContext _db;
        private readonly IEntityRegistry _registry;

        public PurchaseInvoiceLegacyTimelineAdapter(CrossDbContext db, IEntityRegistry registry)
        { _db = db; _registry = registry; }

        public string EntityCode => EntityRegistry.PurchaseInvoice;

        public async Task<IReadOnlyList<TimelineItemViewModel>> GetAsync(int entityId, BusinessContext context, CancellationToken cancellationToken = default)
        {
            var invoice = await _db.PurchaseInvoices.AsNoTracking()
                .Where(i => i.ID == entityId && i.CompanyID == context.CompanyId)
                .Select(i => new { i.ID, i.InvoiceNo, i.CreatedAt, i.InvoiceDate, i.GrandTotal, i.VendorId })
                .FirstOrDefaultAsync(cancellationToken);
            if (invoice == null) return Array.Empty<TimelineItemViewModel>();

            var supplierName = await _db.Vendors.AsNoTracking()
                .Where(v => v.ID == invoice.VendorId && v.CompanyID == context.CompanyId)
                .Select(v => v.Name).FirstOrDefaultAsync(cancellationToken);

            var url = _registry.BuildUrl(EntityRegistry.PurchaseInvoice, entityId);
            var reference = invoice.InvoiceNo ?? ("#" + invoice.ID);
            var supplierAr = string.IsNullOrWhiteSpace(supplierName) ? "" : $" من المورد {supplierName}";
            var supplierEn = string.IsNullOrWhiteSpace(supplierName) ? "" : $" from {supplierName}";
            var items = new List<TimelineItemViewModel>();

            // ---- 1. the invoice was recorded.
            items.Add(LegacyTimelineSupport.Item(
                EntityRegistry.PurchaseInvoice, entityId, PurchaseInvoiceEvents.Created,
                invoice.CreatedAt ?? invoice.InvoiceDate,
                titleAr: "تسجيل فاتورة مشتريات", titleEn: "Purchase invoice recorded",
                descriptionAr: $"سُجّلت الفاتورة {reference}{supplierAr} بإجمالي {invoice.GrandTotal:N2}{LegacyTimelineSupport.HistoricalAr}",
                descriptionEn: $"Invoice {reference}{supplierEn} was recorded totalling {invoice.GrandTotal:N2}{LegacyTimelineSupport.HistoricalEn}",
                icon: "ki-outline ki-bill", color: "success", url: url,
                payloadVersion: PurchaseInvoiceEventPayload.Version));

            // ---- 2. one item per re-post (edit).
            var postings = await _db.JournalEntries.AsNoTracking()
                .Where(e => e.CompanyID == context.CompanyId && e.SourceType == "PurchaseInvoice" && e.SourceId == entityId)
                .OrderBy(e => e.ID)
                .Select(e => new { e.ID, e.EntryNo, e.CreatedAt, e.EntryDate })
                .ToListAsync(cancellationToken);

            for (int i = 1; i < postings.Count; i++)
            {
                var posting = postings[i];
                items.Add(LegacyTimelineSupport.Item(
                    EntityRegistry.PurchaseInvoice, entityId, PurchaseInvoiceEvents.Updated,
                    posting.CreatedAt ?? posting.EntryDate,
                    titleAr: "تعديل فاتورة مشتريات", titleEn: "Purchase invoice updated",
                    descriptionAr: $"أُعيد ترحيل الفاتورة بالقيد {posting.EntryNo}{LegacyTimelineSupport.HistoricalAr}",
                    descriptionEn: $"Invoice was re-posted under entry {posting.EntryNo}{LegacyTimelineSupport.HistoricalEn}",
                    icon: "ki-outline ki-pencil", color: "warning", url: url,
                    payloadVersion: PurchaseInvoiceEventPayload.Version,
                    uidDiscriminator: posting.ID.ToString()));
            }

            return items;
        }
    }
}
