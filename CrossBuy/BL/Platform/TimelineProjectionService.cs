using CrossBuy.Models.Context;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL.Platform
{
    // Platform Kernel (PKS-001) — the ONLY way a screen reads a business object's timeline.
    //
    // The UI never touches BusinessEvents. Every read goes through here so that the four filters below are
    // impossible to forget:
    //   1. company isolation   — CompanyID must equal the caller's company;
    //   2. branch isolation    — applied only where both sides carry a branch (see BranchFilter);
    //   3. user permission     — View on the entity, via IPlatformPermissionProvider (module RBAC);
    //   4. event visibility    — per-row, against what the caller is allowed to see (ADR-004).
    //
    // The contract returns a PROJECTION, not events, so introducing a persisted projection table later
    // changes nothing above this interface.
    public interface ITimelineProjectionService
    {
        // Throws PlatformAccessDeniedException when the caller may not view the entity at all.
        // Returns the merged, filtered, newest-first timeline otherwise.
        Task<IReadOnlyList<TimelineItemViewModel>> GetAsync(
            string entityCode, int entityId, BusinessContext context, int take = 100, CancellationToken cancellationToken = default);
    }

    public class TimelineProjectionService : ITimelineProjectionService
    {
        public const int MaxTake = 200;

        private readonly CrossDbContext _db;
        private readonly IEntityRegistry _registry;
        private readonly IPlatformPermissionProvider _permissions;
        private readonly IEnumerable<ILegacyTimelineAdapter> _legacyAdapters;

        public TimelineProjectionService(
            CrossDbContext db,
            IEntityRegistry registry,
            IPlatformPermissionProvider permissions,
            IEnumerable<ILegacyTimelineAdapter> legacyAdapters)
        { _db = db; _registry = registry; _permissions = permissions; _legacyAdapters = legacyAdapters; }

        public async Task<IReadOnlyList<TimelineItemViewModel>> GetAsync(
            string entityCode, int entityId, BusinessContext context, int take = 100, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);

            var definition = _registry.GetDefinition(entityCode);   // unknown code -> throws
            if (!definition.SupportsTimeline)
                throw new InvalidOperationException(
                    $"Entity '{definition.Code}' does not support a timeline. Enable SupportsTimeline in IEntityRegistry " +
                    "when the entity is onboarded — asking for one now is a programming error, not a user condition.");
            if (entityId <= 0) return Array.Empty<TimelineItemViewModel>();
            take = Math.Clamp(take, 1, MaxTake);

            // ---- filter 3: may the caller see this record at all?
            var view = await _permissions.CanAsync(context, definition.Code, entityId, PlatformActions.View, cancellationToken);
            if (!view.Allowed)
                throw new PlatformAccessDeniedException(definition.Code, entityId, PlatformActions.View, view.Reason);

            // ---- filter 4 setup: which visibilities may this caller read?
            var (allowedVisibilities, mayReadAnyRestricted) =
                await ResolveVisibilitiesAsync(context, definition.Code, entityId, cancellationToken);

            // ---- filters 1 + 2 + 4: the event query
            var query = _db.BusinessEvents.AsNoTracking()
                .Where(e => e.CompanyID == context.CompanyId
                            && e.EntityType == definition.Code
                            && e.EntityId == entityId
                            && allowedVisibilities.Contains(e.Visibility));

            query = ApplyBranchFilter(query, context);

            var rows = await query
                .OrderByDescending(e => e.CreatedAt).ThenByDescending(e => e.EventId)
                .Take(take)
                .Select(e => new
                {
                    e.EventUid, e.EntityType, e.EntityId, e.EventType, e.ActorEmployeeId,
                    e.Payload, e.PayloadVersion, e.Visibility, e.CreatedAt,
                })
                .ToListAsync(cancellationToken);

            var url = _registry.BuildUrl(definition.Code, entityId);
            var actorNames = await ResolveActorNamesAsync(rows.Select(r => r.ActorEmployeeId), cancellationToken);

            var items = new List<TimelineItemViewModel>(rows.Count);
            foreach (var r in rows)
            {
                // Row-level half of the own-actor exception. `mayReadAnyRestricted` is the MANAGER grant, not
                // "Restricted is in the allowed set" — the allowed set also contains Restricted for the
                // own-actor case, so testing membership here would let every viewer read every restricted row.
                if (r.Visibility == BusinessEventVisibility.Restricted
                    && !mayReadAnyRestricted
                    && r.ActorEmployeeId != context.EmployeeId)
                    continue;

                var presentation = TimelineEventPresenter.Present(r.EventType, r.PayloadVersion, r.Payload);
                items.Add(new TimelineItemViewModel
                {
                    EventUid = r.EventUid,
                    EntityType = r.EntityType,
                    EntityId = r.EntityId,
                    EventType = r.EventType,
                    TitleAr = presentation.TitleAr,
                    TitleEn = presentation.TitleEn,
                    DescriptionAr = presentation.DescriptionAr,
                    DescriptionEn = presentation.DescriptionEn,
                    ActorEmployeeId = r.ActorEmployeeId,
                    ActorDisplayName = r.ActorEmployeeId.HasValue
                        ? actorNames.GetValueOrDefault(r.ActorEmployeeId.Value)
                        : null,
                    Icon = presentation.Icon,
                    Color = presentation.Color,
                    CreatedAt = r.CreatedAt,
                    PayloadVersion = r.PayloadVersion,
                    Visibility = r.Visibility,
                    Url = url,
                    Source = TimelineItemSource.Event,
                });
            }

            // ---- legacy merge (PKS-001 legacy compatibility)
            items.AddRange(await LegacyItemsAsync(definition.Code, entityId, context, items, cancellationToken));

            return items
                .OrderByDescending(i => i.CreatedAt)
                .Take(take)
                .ToList();
        }

        // Branch isolation is applied only when BOTH sides have a branch. A caller with no branch (an
        // accountant at head office) must not lose branch-stamped history, and an event with no branch
        // (SalesInvoice carries no BranchID column at all) must not disappear for a branch user. Filtering
        // unconditionally would silently hide rows in both directions.
        private static IQueryable<Models.Context.Platform.BusinessEvent> ApplyBranchFilter(
            IQueryable<Models.Context.Platform.BusinessEvent> query, BusinessContext context)
        {
            if (context.BranchId == null) return query;
            int branchId = context.BranchId.Value;
            return query.Where(e => e.BranchID == null || e.BranchID == branchId);
        }

        // ADR-004 vocabulary -> what this caller may read. Internal is implied by the View grant that
        // already passed; the elevated tiers are separate module checks.
        //
        // Returns TWO things, and the distinction matters: `visibilities` is what the SQL filter may let
        // through, while `mayReadAnyRestricted` is the manager grant. Restricted appears in `visibilities`
        // for an ordinary user too — but only so their OWN restricted rows can be fetched and the rest
        // dropped row-by-row afterwards. Collapsing the two into one value silently grants every viewer
        // every restricted event.
        private async Task<(HashSet<string> visibilities, bool mayReadAnyRestricted)> ResolveVisibilitiesAsync(
            BusinessContext context, string entityCode, int entityId, CancellationToken cancellationToken)
        {
            var allowed = new HashSet<string>(StringComparer.Ordinal) { BusinessEventVisibility.Internal };

            var confidential = await _permissions.CanAsync(context, entityCode, entityId, PlatformActions.ViewConfidential, cancellationToken);
            if (confidential.Allowed) allowed.Add(BusinessEventVisibility.Confidential);

            var restricted = await _permissions.CanAsync(context, entityCode, entityId, PlatformActions.ViewRestricted, cancellationToken);
            if (restricted.Allowed)
            {
                allowed.Add(BusinessEventVisibility.Restricted);
                // System events are machine bookkeeping: same gate as Restricted, never shown to anyone below it.
                allowed.Add(BusinessEventVisibility.System);
                return (allowed, true);
            }

            if (context.EmployeeId.HasValue)
            {
                // Own-actor exception: fetch Restricted rows, then keep only this employee's (see the caller).
                allowed.Add(BusinessEventVisibility.Restricted);
            }
            return (allowed, false);
        }

        private async Task<Dictionary<int, string>> ResolveActorNamesAsync(IEnumerable<int?> actorIds, CancellationToken cancellationToken)
        {
            var ids = actorIds.Where(id => id.HasValue).Select(id => id!.Value).Distinct().ToList();
            if (ids.Count == 0) return new Dictionary<int, string>();

            bool isArabic = System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ar";
            var rows = await _db.Employee.AsNoTracking()
                .Where(e => ids.Contains(e.ID))
                .Select(e => new { e.ID, e.FullName, e.FullNameEn })
                .ToListAsync(cancellationToken);

            return rows.ToDictionary(
                r => r.ID,
                r => (isArabic ? (r.FullName ?? r.FullNameEn) : (r.FullNameEn ?? r.FullName)) ?? ("#" + r.ID));
        }

        // DEDUPLICATION RULE: the kernel is authoritative from the moment it recorded its first event for
        // this record. Legacy items at or after that moment are dropped, and a legacy item whose event type
        // already appears among the real events is dropped outright. Anything strictly older is history the
        // kernel never saw, so it is kept.
        private async Task<List<TimelineItemViewModel>> LegacyItemsAsync(
            string entityCode, int entityId, BusinessContext context,
            List<TimelineItemViewModel> realItems, CancellationToken cancellationToken)
        {
            var adapter = _legacyAdapters.FirstOrDefault(a => string.Equals(a.EntityCode, entityCode, StringComparison.Ordinal));
            if (adapter == null) return new List<TimelineItemViewModel>();

            var legacy = await adapter.GetAsync(entityId, context, cancellationToken);
            if (legacy.Count == 0) return new List<TimelineItemViewModel>();

            // The earliest real event for this record, regardless of the page window above.
            var firstRealAt = await _db.BusinessEvents.AsNoTracking()
                .Where(e => e.CompanyID == context.CompanyId && e.EntityType == entityCode && e.EntityId == entityId)
                .OrderBy(e => e.CreatedAt)
                .Select(e => (DateTime?)e.CreatedAt)
                .FirstOrDefaultAsync(cancellationToken);

            var realEventTypes = new HashSet<string>(realItems.Select(i => i.EventType), StringComparer.Ordinal);

            return legacy
                .Where(l => !realEventTypes.Contains(l.EventType))
                .Where(l => firstRealAt == null || l.CreatedAt < firstRealAt.Value)
                .ToList();
        }
    }
}