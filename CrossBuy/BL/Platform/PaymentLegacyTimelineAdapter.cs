using CrossBuy.Models.Context;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL.Platform
{
    // The vendor-payment twin of ReceiptLegacyTimelineAdapter (سند دفع). Same lifecycle, same sources.
    public class PaymentLegacyTimelineAdapter : ILegacyTimelineAdapter
    {
        private readonly CrossDbContext _db;
        private readonly IEntityRegistry _registry;

        public PaymentLegacyTimelineAdapter(CrossDbContext db, IEntityRegistry registry)
        { _db = db; _registry = registry; }

        public string EntityCode => EntityRegistry.Payment;

        public async Task<IReadOnlyList<TimelineItemViewModel>> GetAsync(
            int entityId, BusinessContext context, CancellationToken cancellationToken = default)
        {
            var doc = await _db.Payments.AsNoTracking()
                .Where(p => p.ID == entityId && p.CompanyID == context.CompanyId)
                .Select(p => new { p.ID, p.PaymentNo, p.CreatedAt, p.PaymentDate, p.Amount, p.Method, p.VendorId })
                .FirstOrDefaultAsync(cancellationToken);
            if (doc == null) return Array.Empty<TimelineItemViewModel>();

            string? vendorName = null;
            if (doc.VendorId != null)
                vendorName = await _db.Vendors.AsNoTracking()
                    .Where(v => v.ID == doc.VendorId && v.CompanyID == context.CompanyId)
                    .Select(v => v.Name).FirstOrDefaultAsync(cancellationToken);

            var allocations = await (from a in _db.PaymentAllocations.AsNoTracking()
                                     join i in _db.PurchaseInvoices.AsNoTracking() on a.PurchaseInvoiceId equals i.ID
                                     where a.CompanyID == context.CompanyId && a.PaymentId == entityId
                                     select new MoneyDocLegacyTimeline.Allocation
                                     {
                                         DocId = i.ID,
                                         DocNo = i.InvoiceNo ?? ("#" + i.ID),
                                         Amount = a.ForeignAmount,
                                     }).ToListAsync(cancellationToken);

            return await MoneyDocLegacyTimeline.BuildAsync(
                _db, _registry, context,
                entityCode: EntityRegistry.Payment,
                sourceType: "Payment",
                entityId: entityId,
                reference: doc.PaymentNo ?? ("#" + doc.ID),
                recordedAt: doc.CreatedAt ?? doc.PaymentDate,
                amount: doc.Amount,
                methodText: MoneyMethodNames.Arabic(doc.Method),
                partyName: vendorName,
                partyPrefixAr: "للمورد", partyPrefixEn: "to",
                allocations: allocations,
                createdType: PaymentEvents.Created,
                allocatedType: PaymentEvents.Allocated,
                updatedType: PaymentEvents.Updated,
                reversedType: PaymentEvents.Reversed,
                titleCreatedAr: "تسجيل سند دفع", titleCreatedEn: "Payment recorded",
                titleAllocatedAr: "تخصيص على فاتورة", titleAllocatedEn: "Allocated to an invoice",
                titleUpdatedAr: "تعديل سند دفع", titleUpdatedEn: "Payment updated",
                icon: "ki-outline ki-dollar", color: "danger",
                cancellationToken);
        }
    }
}
