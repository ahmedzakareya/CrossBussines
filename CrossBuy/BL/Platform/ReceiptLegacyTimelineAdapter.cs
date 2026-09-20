using CrossBuy.Models.Context;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL.Platform
{
    // Read-time history for a customer receipt (سند قبض).
    //
    // Unlike the returns, a receipt was not merely unread — it had NO ENTITY FAMILY AT ALL until now, so
    // nothing could address it: no timeline, no conversation, no record picker entry. Registering the family
    // is what makes this adapter reachable, and the registry's ResolveAsync case is what makes it permitted
    // (a registered code with no case there is refused for every action, before any adapter is consulted).
    public class ReceiptLegacyTimelineAdapter : ILegacyTimelineAdapter
    {
        private readonly CrossDbContext _db;
        private readonly IEntityRegistry _registry;

        public ReceiptLegacyTimelineAdapter(CrossDbContext db, IEntityRegistry registry)
        { _db = db; _registry = registry; }

        public string EntityCode => EntityRegistry.Receipt;

        public async Task<IReadOnlyList<TimelineItemViewModel>> GetAsync(
            int entityId, BusinessContext context, CancellationToken cancellationToken = default)
        {
            var doc = await _db.Receipts.AsNoTracking()
                .Where(r => r.ID == entityId && r.CompanyID == context.CompanyId)
                .Select(r => new { r.ID, r.ReceiptNo, r.CreatedAt, r.ReceiptDate, r.Amount, r.Method, r.CustomerId })
                .FirstOrDefaultAsync(cancellationToken);
            if (doc == null) return Array.Empty<TimelineItemViewModel>();

            string? customerName = null;
            if (doc.CustomerId != null)
                customerName = await _db.Customers.AsNoTracking()
                    .Where(c => c.ID == doc.CustomerId && c.CompanyID == context.CompanyId)
                    .Select(c => c.Name).FirstOrDefaultAsync(cancellationToken);

            var allocations = await (from a in _db.ReceiptAllocations.AsNoTracking()
                                     join i in _db.SalesInvoices.AsNoTracking() on a.SalesInvoiceId equals i.ID
                                     where a.CompanyID == context.CompanyId && a.ReceiptId == entityId
                                     select new MoneyDocLegacyTimeline.Allocation
                                     {
                                         DocId = i.ID,
                                         DocNo = i.InvoiceNo ?? ("#" + i.ID),
                                         Amount = a.ForeignAmount,
                                     }).ToListAsync(cancellationToken);

            return await MoneyDocLegacyTimeline.BuildAsync(
                _db, _registry, context,
                entityCode: EntityRegistry.Receipt,
                sourceType: "Receipt",
                entityId: entityId,
                reference: doc.ReceiptNo ?? ("#" + doc.ID),
                recordedAt: doc.CreatedAt ?? doc.ReceiptDate,
                amount: doc.Amount,
                methodText: MoneyMethodNames.Arabic(doc.Method),
                partyName: customerName,
                partyPrefixAr: "من العميل", partyPrefixEn: "from",
                allocations: allocations,
                createdType: ReceiptEvents.Created,
                allocatedType: ReceiptEvents.Allocated,
                updatedType: ReceiptEvents.Updated,
                reversedType: ReceiptEvents.Reversed,
                titleCreatedAr: "تسجيل سند قبض", titleCreatedEn: "Receipt recorded",
                titleAllocatedAr: "تخصيص على فاتورة", titleAllocatedEn: "Allocated to an invoice",
                titleUpdatedAr: "تعديل سند قبض", titleUpdatedEn: "Receipt updated",
                icon: "ki-outline ki-wallet", color: "success",
                cancellationToken);
        }
    }
}
