using CrossBuy.Models.Context;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL.Platform
{
    // Reconstructs Sales Return history at read time, the same way the invoice adapters do (ADR-005).
    //
    // WHY THIS EXISTS: EntityRegistry has marked SalesReturn SupportsTimeline = true since it was onboarded,
    // with the note "the panel renders 'no activity yet' until ReceivableService/PayableService publish return
    // events". Those producers were never written, so the panel said "no activity yet" on EVERY return —
    // including returns that had been recorded, posted and reversed. The history was not missing; nothing was
    // reading it. Measured before this file existed: 0 rows in BusinessEvents for SalesReturn or
    // PurchaseReturn, against 149 for SalesInvoice and 99 for PurchaseInvoice.
    //
    // HISTORICAL SOURCES USED — every fact below is already recorded somewhere, and nothing is invented:
    //   1. SalesReturn.CreatedAt (falling back to ReturnDate) + ReturnNo + GrandTotal + OriginalInvoiceId.
    //   2. JournalEntries where SourceType='SalesReturn' — index 0 IS the original posting and is already
    //      described by item 1; each later entry is a re-post.
    //   3. JournalEntry.ReversedByEntryId — the one transition that changes a posted entry, and the one the
    //      invoice adapters do not show. A reader who quotes a reversed figure without knowing it was
    //      reversed is the reason it is here.
    //
    // NOT used: DocComment (authored discussion — the conversation panel renders it), SalesReturnLine (no
    // per-line history), Notification (delivery records, not business history).
    public class SalesReturnLegacyTimelineAdapter : ILegacyTimelineAdapter
    {
        private readonly CrossDbContext _db;
        private readonly IEntityRegistry _registry;

        public SalesReturnLegacyTimelineAdapter(CrossDbContext db, IEntityRegistry registry)
        { _db = db; _registry = registry; }

        public string EntityCode => EntityRegistry.SalesReturn;

        public async Task<IReadOnlyList<TimelineItemViewModel>> GetAsync(
            int entityId, BusinessContext context, CancellationToken cancellationToken = default)
        {
            var ret = await _db.SalesReturns.AsNoTracking()
                .Where(r => r.ID == entityId && r.CompanyID == context.CompanyId)
                .Select(r => new { r.ID, r.ReturnNo, r.CreatedAt, r.ReturnDate, r.GrandTotal, r.CustomerId, r.OriginalInvoiceId })
                .FirstOrDefaultAsync(cancellationToken);
            if (ret == null) return Array.Empty<TimelineItemViewModel>();

            var customerName = await _db.Customers.AsNoTracking()
                .Where(c => c.ID == ret.CustomerId && c.CompanyID == context.CompanyId)
                .Select(c => c.Name).FirstOrDefaultAsync(cancellationToken);

            // The invoice it came back against, by NUMBER — an id in a history line tells a reader nothing.
            string? againstNo = null;
            if (ret.OriginalInvoiceId != null)
                againstNo = await _db.SalesInvoices.AsNoTracking()
                    .Where(i => i.ID == ret.OriginalInvoiceId && i.CompanyID == context.CompanyId)
                    .Select(i => i.InvoiceNo).FirstOrDefaultAsync(cancellationToken);

            return await LegacyReturnTimeline.BuildAsync(
                _db, _registry, context,
                entityCode: EntityRegistry.SalesReturn,
                sourceType: "SalesReturn",
                entityId: entityId,
                reference: ret.ReturnNo ?? ("#" + ret.ID),
                recordedAt: ret.CreatedAt ?? ret.ReturnDate,
                total: ret.GrandTotal,
                partyName: customerName,
                partyPrefixAr: "للعميل", partyPrefixEn: "from",
                againstNo: againstNo,
                createdType: SalesReturnEvents.Created,
                updatedType: SalesReturnEvents.Updated,
                reversedType: SalesReturnEvents.Reversed,
                titleCreatedAr: "تسجيل مرتجع مبيعات", titleCreatedEn: "Sales return recorded",
                titleUpdatedAr: "تعديل مرتجع مبيعات", titleUpdatedEn: "Sales return updated",
                icon: "ki-outline ki-arrow-circle-left",
                cancellationToken);
        }
    }
}
