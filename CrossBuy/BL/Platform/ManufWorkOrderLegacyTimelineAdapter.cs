using CrossBuy.Models.Context;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL.Platform
{
    // Platform Kernel slice 2 (ADR-005) — reconstructs pre-kernel Work Order history at read time.
    //
    // This is the RICHEST legacy source of the three pilots, and the only one that yields genuine per-status
    // history, because ManufWorkOrder already stamps its own lifecycle timestamps on the row:
    //   CreatedAt   -> ManufWorkOrder.Created
    //   ReleasedAt  -> ManufWorkOrder.Released
    //   CompletedAt -> ManufWorkOrder.Completed
    //   ClosedAt    -> only emitted when it differs from CompletedAt (see below)
    // plus JournalEntries where SourceType='WorkOrder' for the GL-bearing steps.
    //
    // WHAT CANNOT BE RECONSTRUCTED, stated plainly:
    //   * Cancellation has NO timestamp. Status='Cancelled' is the only trace, so a cancelled order can be
    //     shown as cancelled but not dated — and a legacy item with no honest date is not emitted at all.
    //     Cancelled work orders therefore show their earlier history and then simply stop.
    //   * Partial production (ProducePartialAsync) leaves only the running ProducedQty total, not a per-step
    //     history, so individual partial productions cannot be recovered.
    //   * Header edits before the kernel are not recorded anywhere.
    //
    // NOT used: ManufWorkOrderComponent (planned/issued quantities, not history), ManufWorkOrderLabor (cost
    // lines, and they carry employee cost data that does not belong in an Internal-visibility row).
    public class ManufWorkOrderLegacyTimelineAdapter : ILegacyTimelineAdapter
    {
        private readonly CrossDbContext _db;
        private readonly IEntityRegistry _registry;

        public ManufWorkOrderLegacyTimelineAdapter(CrossDbContext db, IEntityRegistry registry)
        { _db = db; _registry = registry; }

        public string EntityCode => EntityRegistry.ManufWorkOrder;

        public async Task<IReadOnlyList<TimelineItemViewModel>> GetAsync(int entityId, BusinessContext context, CancellationToken cancellationToken = default)
        {
            var wo = await _db.ManufWorkOrders.AsNoTracking()
                .Where(w => w.ID == entityId && w.CompanyID == context.CompanyId)
                .Select(w => new
                {
                    w.ID, w.WoNo, w.ItemId, w.Qty, w.ProducedQty, w.Status,
                    w.CreatedAt, w.ReleasedAt, w.CompletedAt, w.ClosedAt, w.PlannedStart, w.PlannedEnd,
                })
                .FirstOrDefaultAsync(cancellationToken);
            if (wo == null) return Array.Empty<TimelineItemViewModel>();

            var itemName = await _db.Items.AsNoTracking()
                .Where(i => i.ID == wo.ItemId && i.CompanyID == context.CompanyId)
                .Select(i => (i.ItemCode ?? "") + " — " + i.Name)
                .FirstOrDefaultAsync(cancellationToken);

            var url = _registry.BuildUrl(EntityRegistry.ManufWorkOrder, entityId);
            var reference = wo.WoNo ?? ("#" + wo.ID);
            var itemAr = string.IsNullOrWhiteSpace(itemName) ? "" : $" — الصنف {itemName}";
            var itemEn = string.IsNullOrWhiteSpace(itemName) ? "" : $" — item {itemName}";
            var items = new List<TimelineItemViewModel>();

            // ---- created
            if (wo.CreatedAt.HasValue)
                items.Add(LegacyTimelineSupport.Item(
                    EntityRegistry.ManufWorkOrder, entityId, ManufWorkOrderEvents.Created, wo.CreatedAt.Value,
                    titleAr: "إنشاء أمر تشغيل", titleEn: "Work order created",
                    descriptionAr: $"أُنشئ أمر التشغيل {reference}{itemAr} بكمية مخطّطة {wo.Qty:N2}{LegacyTimelineSupport.HistoricalAr}",
                    descriptionEn: $"Work order {reference} was created{itemEn} for a planned quantity of {wo.Qty:N2}{LegacyTimelineSupport.HistoricalEn}",
                    icon: "ki-outline ki-gear", color: "primary", url: url,
                    payloadVersion: ManufWorkOrderEventPayload.Version));

            // ---- released
            if (wo.ReleasedAt.HasValue)
                items.Add(LegacyTimelineSupport.Item(
                    EntityRegistry.ManufWorkOrder, entityId, ManufWorkOrderEvents.Released, wo.ReleasedAt.Value,
                    titleAr: "إصدار أمر تشغيل", titleEn: "Work order released",
                    descriptionAr: $"صدر أمر التشغيل {reference} للإنتاج{LegacyTimelineSupport.HistoricalAr}",
                    descriptionEn: $"Work order {reference} was released to production{LegacyTimelineSupport.HistoricalEn}",
                    icon: "ki-outline ki-rocket", color: "info", url: url,
                    payloadVersion: ManufWorkOrderEventPayload.Version));

            // ---- completed
            if (wo.CompletedAt.HasValue)
                items.Add(LegacyTimelineSupport.Item(
                    EntityRegistry.ManufWorkOrder, entityId, ManufWorkOrderEvents.Completed, wo.CompletedAt.Value,
                    titleAr: "اكتمال أمر تشغيل", titleEn: "Work order completed",
                    descriptionAr: $"اكتمل أمر التشغيل {reference} بكمية منتجة {wo.ProducedQty:N2} من {wo.Qty:N2}{LegacyTimelineSupport.HistoricalAr}",
                    descriptionEn: $"Work order {reference} completed with {wo.ProducedQty:N2} of {wo.Qty:N2} produced{LegacyTimelineSupport.HistoricalEn}",
                    icon: "ki-outline ki-check-circle", color: "success", url: url,
                    payloadVersion: ManufWorkOrderEventPayload.Version));

            // ---- closed, only when it is a DISTINCT fact. CompleteWorkOrderAsync stamps CompletedAt and
            // ClosedAt together, so emitting both would show the same moment twice.
            if (wo.ClosedAt.HasValue && wo.ClosedAt != wo.CompletedAt)
                items.Add(LegacyTimelineSupport.Item(
                    EntityRegistry.ManufWorkOrder, entityId, ManufWorkOrderEvents.Completed, wo.ClosedAt.Value,
                    titleAr: "إغلاق أمر تشغيل", titleEn: "Work order closed",
                    descriptionAr: $"أُغلق أمر التشغيل {reference}{LegacyTimelineSupport.HistoricalAr}",
                    descriptionEn: $"Work order {reference} was closed{LegacyTimelineSupport.HistoricalEn}",
                    icon: "ki-outline ki-lock", color: "dark", url: url,
                    payloadVersion: ManufWorkOrderEventPayload.Version,
                    uidDiscriminator: "closed"));

            // ---- GL-bearing steps that the row's own timestamps do not cover.
            var postings = await _db.JournalEntries.AsNoTracking()
                .Where(e => e.CompanyID == context.CompanyId && e.SourceType == "WorkOrder" && e.SourceId == entityId)
                .OrderBy(e => e.ID)
                .Select(e => new { e.ID, e.EntryNo, e.CreatedAt, e.EntryDate })
                .ToListAsync(cancellationToken);

            foreach (var posting in postings)
            {
                var when = posting.CreatedAt ?? posting.EntryDate;
                // Skip a posting that lands on a moment already described by a status stamp above — the
                // release/complete entries are posted inside those same transactions.
                if (when == wo.ReleasedAt || when == wo.CompletedAt || when == wo.ClosedAt) continue;

                items.Add(LegacyTimelineSupport.Item(
                    EntityRegistry.ManufWorkOrder, entityId, ManufWorkOrderEvents.Produced, when,
                    titleAr: "حركة تصنيع", titleEn: "Manufacturing posting",
                    descriptionAr: $"قيد تصنيع {posting.EntryNo} على أمر التشغيل {reference}{LegacyTimelineSupport.HistoricalAr}",
                    descriptionEn: $"Manufacturing entry {posting.EntryNo} on work order {reference}{LegacyTimelineSupport.HistoricalEn}",
                    icon: "ki-outline ki-chart-line-up", color: "info", url: url,
                    payloadVersion: ManufWorkOrderEventPayload.Version,
                    uidDiscriminator: "je:" + posting.ID));
            }

            return items;
        }
    }
}
