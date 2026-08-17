using CrossBuy.Models.Context;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL.Platform
{
    // Platform Kernel slice 2 (ADR-005) — reconstructs pre-kernel Customer history at read time.
    //
    // HISTORICAL SOURCES FOUND, and why each one is usable:
    //   1. Customer.CreatedAt — the only timestamp on the row. Gives the "customer added" fact.
    //   2. Crm.Activity — a REAL polymorphic activity log that already supports this entity two ways:
    //      Activity.CustomerId (a direct FK) and Activity.EntityType/EntityId, whose documented values
    //      include "Customer". Both are read so an activity linked either way is picked up. This is the only
    //      pilot entity with genuine per-event pre-kernel history.
    //
    // NOT used: DocComment. Comments are authored discussion, not derived history, and the comments widget
    // renders them itself on the screens where it is wired — surfacing them here would duplicate that.
    // Customer has no status-history table, and nothing else records customer changes.
    //
    // Nothing is written. See ADR-005 for why merge-at-read beat a backfill.
    public class CustomerLegacyTimelineAdapter : ILegacyTimelineAdapter
    {
        private readonly CrossDbContext _db;
        private readonly IEntityRegistry _registry;

        public CustomerLegacyTimelineAdapter(CrossDbContext db, IEntityRegistry registry)
        { _db = db; _registry = registry; }

        public string EntityCode => EntityRegistry.Customer;

        public async Task<IReadOnlyList<TimelineItemViewModel>> GetAsync(int entityId, BusinessContext context, CancellationToken cancellationToken = default)
        {
            var customer = await _db.Customers.AsNoTracking()
                .Where(c => c.ID == entityId && c.CompanyID == context.CompanyId)
                .Select(c => new { c.ID, c.Name, c.Segment, c.CreatedAt, c.IsActive })
                .FirstOrDefaultAsync(cancellationToken);
            if (customer == null) return Array.Empty<TimelineItemViewModel>();

            var url = _registry.BuildUrl(EntityRegistry.Customer, entityId);
            var items = new List<TimelineItemViewModel>();

            // ---- 1. the customer was added. CreatedAt is nullable on older rows; without it there is no
            // honest timestamp to show, so the item is skipped rather than dated to "now".
            if (customer.CreatedAt.HasValue)
            {
                string segmentAr = string.IsNullOrWhiteSpace(customer.Segment) ? "" : $" — التصنيف: {customer.Segment}";
                string segmentEn = string.IsNullOrWhiteSpace(customer.Segment) ? "" : $" — segment: {customer.Segment}";
                items.Add(LegacyTimelineSupport.Item(
                    EntityRegistry.Customer, entityId, CustomerEvents.Created, customer.CreatedAt.Value,
                    titleAr: "إضافة عميل", titleEn: "Customer added",
                    descriptionAr: $"أُضيف العميل «{customer.Name}»{segmentAr}{LegacyTimelineSupport.HistoricalAr}",
                    descriptionEn: $"Customer «{customer.Name}» was added{segmentEn}{LegacyTimelineSupport.HistoricalEn}",
                    icon: "ki-outline ki-profile-circle", color: "success", url: url,
                    payloadVersion: CustomerEventPayload.Version));
            }

            // ---- 2. CRM activities against this customer, by either linkage.
            var activities = await _db.Activities.AsNoTracking()
                .Where(a => a.CompanyID == context.CompanyId
                            && (a.CustomerId == entityId
                                || (a.EntityType == EntityRegistry.Customer && a.EntityId == entityId)))
                .OrderBy(a => a.ID)
                .Select(a => new { a.ID, a.Type, a.Subject, a.SubjectEn, a.Done, a.CreatedAt, a.DueDate, a.OwnerEmployeeId })
                .ToListAsync(cancellationToken);

            // Owner names are resolved here, not by the projection: the projection only names actors for real
            // BusinessEvent rows, so a legacy item that carries an owner id would otherwise render an id with
            // no name.
            var ownerIds = activities.Where(a => a.OwnerEmployeeId.HasValue).Select(a => a.OwnerEmployeeId!.Value).Distinct().ToList();
            var ownerNames = new Dictionary<int, string>();
            if (ownerIds.Count > 0)
            {
                bool isArabic = System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ar";
                ownerNames = (await _db.Employee.AsNoTracking()
                        .Where(e => ownerIds.Contains(e.ID))
                        .Select(e => new { e.ID, e.FullName, e.FullNameEn })
                        .ToListAsync(cancellationToken))
                    .ToDictionary(e => e.ID, e => (isArabic ? (e.FullName ?? e.FullNameEn) : (e.FullNameEn ?? e.FullName)) ?? ("#" + e.ID));
            }

            foreach (var activity in activities)
            {
                var when = activity.CreatedAt ?? activity.DueDate;
                if (when == null) continue;   // undateable row — never invent a position on the timeline

                var subjectAr = string.IsNullOrWhiteSpace(activity.Subject) ? "-" : activity.Subject;
                var subjectEn = string.IsNullOrWhiteSpace(activity.SubjectEn) ? subjectAr : activity.SubjectEn!;
                var (kindAr, kindEn, icon) = ActivityKind(activity.Type);
                var stateAr = activity.Done ? " — مكتمل" : "";
                var stateEn = activity.Done ? " — done" : "";

                items.Add(LegacyTimelineSupport.Item(
                    EntityRegistry.Customer, entityId, CustomerEvents.Updated, when.Value,
                    titleAr: kindAr, titleEn: kindEn,
                    descriptionAr: $"{subjectAr}{stateAr}{LegacyTimelineSupport.HistoricalAr}",
                    descriptionEn: $"{subjectEn}{stateEn}{LegacyTimelineSupport.HistoricalEn}",
                    icon: icon, color: "info", url: url,
                    payloadVersion: CustomerEventPayload.Version,
                    // The activity id keeps each row's identity distinct and stable across reads.
                    uidDiscriminator: "activity:" + activity.ID,
                    // Crm.Activity DOES record an owner, so unlike every other legacy source this one can
                    // name a real actor without fabricating anything.
                    actorEmployeeId: activity.OwnerEmployeeId,
                    actorDisplayName: activity.OwnerEmployeeId.HasValue
                        ? ownerNames.GetValueOrDefault(activity.OwnerEmployeeId.Value)
                        : null));
            }

            return items;
        }

        private static (string ar, string en, string icon) ActivityKind(string? type) => type switch
        {
            "Call" => ("مكالمة", "Call", "ki-outline ki-phone"),
            "Meeting" => ("اجتماع", "Meeting", "ki-outline ki-calendar-tick"),
            "Email" => ("بريد", "Email", "ki-outline ki-sms"),
            _ => ("مهمة", "Task", "ki-outline ki-check-square"),
        };
    }
}
