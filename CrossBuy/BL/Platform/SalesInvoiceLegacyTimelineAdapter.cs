using System.Security.Cryptography;
using System.Text;
using CrossBuy.Models.Context;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL.Platform
{
    // Platform Kernel (PKS-001 "Legacy timeline compatibility") — the isolated legacy adapter.
    //
    // THE PROBLEM: BusinessEvents starts empty. Every invoice that already exists has no events, so the
    // moment the projection goes live those invoices would show a blank timeline — a visible regression on
    // historical data, and it would make "the event log is the history of business state" untrue for
    // everything recorded before the kernel.
    //
    // THE CHOSEN STRATEGY is the merge option, not a backfill: this adapter RECONSTRUCTS timeline items at
    // read time from data the system already stores durably, and the projection merges them under the real
    // events. Nothing is written, so there is no migration to reverse, no risk of double-counting a
    // half-finished backfill, and no global rewrite of CRM Activity / DocComment / Notification (explicitly
    // out of scope). The cost is that legacy items are coarser than real events — which is honest, because
    // the finer detail was never recorded.
    //
    // DEDUPLICATION is owned by the projection: legacy items are dropped once real events cover the same
    // ground (see TimelineProjectionService). This adapter just reports what it can derive.
    //
    // Everything legacy lives in this one class so it can be deleted whole once history no longer matters.
    public interface ILegacyTimelineAdapter
    {
        // Entity code this adapter reconstructs history for.
        string EntityCode { get; }

        Task<IReadOnlyList<TimelineItemViewModel>> GetAsync(int entityId, BusinessContext context, CancellationToken cancellationToken = default);
    }

    public class SalesInvoiceLegacyTimelineAdapter : ILegacyTimelineAdapter
    {
        private readonly CrossDbContext _db;
        private readonly IEntityRegistry _registry;

        public SalesInvoiceLegacyTimelineAdapter(CrossDbContext db, IEntityRegistry registry)
        { _db = db; _registry = registry; }

        public string EntityCode => EntityRegistry.SalesInvoice;

        public async Task<IReadOnlyList<TimelineItemViewModel>> GetAsync(int entityId, BusinessContext context, CancellationToken cancellationToken = default)
        {
            var invoice = await _db.SalesInvoices.AsNoTracking()
                .Where(i => i.ID == entityId && i.CompanyID == context.CompanyId)
                .Select(i => new { i.ID, i.InvoiceNo, i.CreatedAt, i.InvoiceDate, i.GrandTotal, i.Status })
                .FirstOrDefaultAsync(cancellationToken);
            if (invoice == null) return Array.Empty<TimelineItemViewModel>();

            var url = _registry.BuildUrl(EntityRegistry.SalesInvoice, entityId);
            var items = new List<TimelineItemViewModel>();

            // ---- the issue itself. CreatedAt is nullable on older rows; InvoiceDate is the honest fallback.
            items.Add(Build(
                entityId, SalesInvoiceEvents.Created,
                invoice.CreatedAt ?? invoice.InvoiceDate,
                titleAr: "إصدار فاتورة المبيعات", titleEn: "Sales invoice issued",
                descriptionAr: $"صدرت الفاتورة {invoice.InvoiceNo ?? ("#" + invoice.ID)} بإجمالي {invoice.GrandTotal:N2} (سجل تاريخي)",
                descriptionEn: $"Invoice {invoice.InvoiceNo ?? ("#" + invoice.ID)} was issued totalling {invoice.GrandTotal:N2} (historical record)",
                icon: "ki-outline ki-bill", color: "success", url: url));

            // ---- edits. EditSalesInvoiceAsync reverses the old GL entry and posts a NEW one against the
            // same invoice, so the count of SourceType='SalesInvoice' entries is 1 (the original) plus one
            // per edit. Reversal entries carry SourceType='Reversal', so they cannot double-count here.
            var postings = await _db.JournalEntries.AsNoTracking()
                .Where(e => e.CompanyID == context.CompanyId && e.SourceType == "SalesInvoice" && e.SourceId == entityId)
                .OrderBy(e => e.ID)
                .Select(e => new { e.ID, e.EntryNo, e.CreatedAt, e.EntryDate })
                .ToListAsync(cancellationToken);

            for (int i = 1; i < postings.Count; i++)   // index 0 is the original posting, already covered above
            {
                var posting = postings[i];
                items.Add(Build(
                    entityId, SalesInvoiceEvents.Updated,
                    posting.CreatedAt ?? posting.EntryDate,
                    titleAr: "تعديل فاتورة المبيعات", titleEn: "Sales invoice updated",
                    descriptionAr: $"أُعيد ترحيل الفاتورة بالقيد {posting.EntryNo} (سجل تاريخي)",
                    descriptionEn: $"Invoice was re-posted under entry {posting.EntryNo} (historical record)",
                    icon: "ki-outline ki-pencil", color: "warning", url: url,
                    uidDiscriminator: posting.ID.ToString()));
            }

            return items;
        }

        private static TimelineItemViewModel Build(
            int entityId, string eventType, DateTime createdAt,
            string titleAr, string titleEn, string descriptionAr, string descriptionEn,
            string icon, string color, string? url, string? uidDiscriminator = null)
            => new()
            {
                // Deterministic so the same historical fact keeps the same identity across page loads —
                // a random Guid here would make the UI treat every refresh as new rows.
                EventUid = DeterministicUid(EntityRegistry.SalesInvoice, entityId, eventType, uidDiscriminator),
                EntityType = EntityRegistry.SalesInvoice,
                EntityId = entityId,
                EventType = eventType,
                TitleAr = titleAr, TitleEn = titleEn,
                DescriptionAr = descriptionAr, DescriptionEn = descriptionEn,
                // Pre-kernel data records no actor for these facts. Claiming one would be a fabrication.
                ActorEmployeeId = null, ActorDisplayName = null,
                Icon = icon, Color = color,
                CreatedAt = createdAt,
                PayloadVersion = SalesInvoiceEventPayload.Version,
                Visibility = BusinessEventVisibility.Internal,
                Url = url,
                Source = TimelineItemSource.Legacy,
            };

        // Stable name -> Guid. MD5 is used purely to derive a deterministic identifier, never for security.
        private static Guid DeterministicUid(string entityCode, int entityId, string eventType, string? discriminator)
        {
            var key = $"legacy:{entityCode}:{entityId}:{eventType}:{discriminator ?? ""}";
            return new Guid(MD5.HashData(Encoding.UTF8.GetBytes(key)));
        }
    }
}