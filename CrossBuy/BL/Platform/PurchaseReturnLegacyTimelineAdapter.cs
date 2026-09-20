using CrossBuy.Models.Context;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL.Platform
{
    // The debit-note twin of SalesReturnLegacyTimelineAdapter. Same lifecycle shape, same sources, so the two
    // timelines stay comparable — the reason the two invoice adapters were also kept identical.
    public class PurchaseReturnLegacyTimelineAdapter : ILegacyTimelineAdapter
    {
        private readonly CrossDbContext _db;
        private readonly IEntityRegistry _registry;

        public PurchaseReturnLegacyTimelineAdapter(CrossDbContext db, IEntityRegistry registry)
        { _db = db; _registry = registry; }

        public string EntityCode => EntityRegistry.PurchaseReturn;

        public async Task<IReadOnlyList<TimelineItemViewModel>> GetAsync(
            int entityId, BusinessContext context, CancellationToken cancellationToken = default)
        {
            var ret = await _db.PurchaseReturns.AsNoTracking()
                .Where(r => r.ID == entityId && r.CompanyID == context.CompanyId)
                .Select(r => new { r.ID, r.ReturnNo, r.CreatedAt, r.ReturnDate, r.GrandTotal, r.VendorId, r.OriginalInvoiceId })
                .FirstOrDefaultAsync(cancellationToken);
            if (ret == null) return Array.Empty<TimelineItemViewModel>();

            var vendorName = await _db.Vendors.AsNoTracking()
                .Where(v => v.ID == ret.VendorId && v.CompanyID == context.CompanyId)
                .Select(v => v.Name).FirstOrDefaultAsync(cancellationToken);

            string? againstNo = null;
            if (ret.OriginalInvoiceId != null)
                againstNo = await _db.PurchaseInvoices.AsNoTracking()
                    .Where(i => i.ID == ret.OriginalInvoiceId && i.CompanyID == context.CompanyId)
                    .Select(i => i.InvoiceNo).FirstOrDefaultAsync(cancellationToken);

            return await LegacyReturnTimeline.BuildAsync(
                _db, _registry, context,
                entityCode: EntityRegistry.PurchaseReturn,
                sourceType: "PurchaseReturn",
                entityId: entityId,
                reference: ret.ReturnNo ?? ("#" + ret.ID),
                recordedAt: ret.CreatedAt ?? ret.ReturnDate,
                total: ret.GrandTotal,
                partyName: vendorName,
                partyPrefixAr: "للمورد", partyPrefixEn: "to",
                againstNo: againstNo,
                createdType: PurchaseReturnEvents.Created,
                updatedType: PurchaseReturnEvents.Updated,
                reversedType: PurchaseReturnEvents.Reversed,
                titleCreatedAr: "تسجيل مرتجع مشتريات", titleCreatedEn: "Purchase return recorded",
                titleUpdatedAr: "تعديل مرتجع مشتريات", titleUpdatedEn: "Purchase return updated",
                icon: "ki-outline ki-arrow-circle-right",
                cancellationToken);
        }
    }
}
