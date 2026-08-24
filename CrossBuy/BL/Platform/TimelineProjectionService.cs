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

        // Cross-entity variant: "what changed recently in my company", for a dashboard feed. The same four
        // filters apply, with one deliberate difference. GetAsync THROWS when the caller may not view the
        // entity, because it was asked about one named record and silence would be a lie. A feed is asked about
        // the company, so an entity the caller may not see is SKIPPED - otherwise one unreadable record would
        // blank the whole panel, and the panel would become a probe for what exists.
        //
        // sinceUtc is a mandatory lookback and is clamped server-side to MaxRecentLookbackDays; take is clamped
        // to MaxTake. Ordering is CreatedAt DESC, EventId DESC.
        Task<IReadOnlyList<TimelineItemViewModel>> GetRecentForContextAsync(
            BusinessContext context, DateTime sinceUtc, int take, CancellationToken cancellationToken = default);
    }

    public class TimelineProjectionService : ITimelineProjectionService
    {
        public const int MaxTake = 200;

        // The recent feed is a dashboard read, not an archive: a caller may ask for a NARROWER window, never a
        // wider one. This number is the kernel's own. Workspace states its agenda lookback separately and on
        // purpose (WorkspaceService.AgendaOverdueLookbackDays) - a constant shared between a module and the
        // kernel is a coupling that quietly retunes one screen when the other is tuned.
        public const int MaxRecentLookbackDays = 30;

        // Hard ceiling on rows the recent feed will EXAMINE, independent of take. Authorization is resolved per
        // distinct entity, so an otherwise reasonable window over a busy company fans out into unbounded
        // permission work. The scan already stops as soon as take rows are accepted; this bounds the
        // pathological case where nearly everything in the window is invisible to this caller.
        public const int MaxRecentCandidates = 1000;

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

        // ---- PKS-001 recent feed --------------------------------------------------------------------------
        //
        // SOURCE: BusinessEvents, projected on read. There is no persisted timeline read model in this
        // architecture - the per-record contract above projects the same table - and adding a second
        // persistence model for one dashboard panel would fork the vocabulary the kernel exists to keep single.
        //
        // LEGACY ADAPTERS ARE DELIBERATELY NOT MERGED HERE. ILegacyTimelineAdapter.GetAsync is keyed by ONE
        // entity id and offers no time-ordered cross-entity query, so a company-wide merge would have to
        // enumerate every record of every adapted type and then probe each one for its first kernel event to
        // apply the dedup rule - an N x all-entity scan to fill one panel. The recent feed is therefore
        // platform Business Events only; per-record history stays complete through GetAsync.
        public async Task<IReadOnlyList<TimelineItemViewModel>> GetRecentForContextAsync(
            BusinessContext context, DateTime sinceUtc, int take, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);

            // Fail closed. An unresolved company is not "every company" - it is no company.
            if (context.CompanyId <= 0) return Array.Empty<TimelineItemViewModel>();

            take = Math.Clamp(take, 1, MaxTake);

            var since = sinceUtc.Kind == DateTimeKind.Local ? sinceUtc.ToUniversalTime() : sinceUtc;
            var floor = DateTime.UtcNow.AddDays(-MaxRecentLookbackDays);
            if (since < floor) since = floor;   // clamp only: a caller may narrow the window, never widen it.

            var query = _db.BusinessEvents.AsNoTracking()
                .Where(e => e.CompanyID == context.CompanyId && e.CreatedAt >= since);
            query = ApplyBranchFilter(query, context);

            var candidates = await query
                .OrderByDescending(e => e.CreatedAt).ThenByDescending(e => e.EventId)
                .Take(MaxRecentCandidates)
                .Select(e => new RecentRow(e.EventUid, e.EntityType, e.EntityId, e.EventType,
                                           e.ActorEmployeeId, e.Payload, e.PayloadVersion, e.Visibility, e.CreatedAt))
                .ToListAsync(cancellationToken);

            // One gate per DISTINCT entity, cached. A null gate means "this caller gets nothing from this
            // record" and short-circuits every later row for it, so a busy entity costs one permission pass.
            var gates = new Dictionary<(string EntityType, int EntityId), RecentGate?>();
            Exception? firstGateFault = null;
            int gatesResolved = 0, gatesFaulted = 0;

            var accepted = new List<(RecentRow Row, RecentGate Gate)>(take);
            foreach (var r in candidates)
            {
                if (accepted.Count == take) break;   // already in final order; stop paying for more.

                var key = (r.EntityType, r.EntityId);
                if (!gates.TryGetValue(key, out var gate))
                {
                    try
                    {
                        gate = await ResolveRecentGateAsync(r.EntityType, r.EntityId, context, cancellationToken);
                        gatesResolved++;
                    }
                    catch (OperationCanceledException)
                    {
                        throw;   // cancellation belongs to the caller and is never "one bad row".
                    }
                    catch (Exception ex)
                    {
                        // Isolation: one module adapter throwing on one record must not blank the feed. Whether
                        // this was a bad row or a broken subsystem is decided after the loop, not here.
                        firstGateFault ??= ex;
                        gatesFaulted++;
                        gate = null;
                    }
                    gates[key] = gate;
                }
                if (gate == null) continue;

                if (!gate.Visibilities.Contains(r.Visibility)) continue;

                // Same own-actor rule as GetAsync, for the same reason: Restricted sits in the allowed set for
                // an ordinary user ONLY so their own rows survive the SQL filter and the rest drop here.
                if (r.Visibility == BusinessEventVisibility.Restricted
                    && !gate.MayReadAnyRestricted
                    && r.ActorEmployeeId != context.EmployeeId)
                    continue;

                accepted.Add((r, gate));
            }

            // Nothing resolved and something faulted: the failure is systemic, not a bad row. Returning an empty
            // list here would render as a calm "no recent activity" on top of a broken subsystem.
            if (gatesResolved == 0 && gatesFaulted > 0) throw firstGateFault!;

            if (accepted.Count == 0) return Array.Empty<TimelineItemViewModel>();

            var actorNames = await ResolveActorNamesAsync(accepted.Select(a => a.Row.ActorEmployeeId), cancellationToken);

            // No re-sort. candidates arrived CreatedAt DESC, EventId DESC and this loop preserves it. The
            // per-record merge above re-sorts on CreatedAt alone because legacy items carry no EventId; doing
            // that here would discard the tiebreak that makes the feed deterministic across equal timestamps.
            var items = new List<TimelineItemViewModel>(accepted.Count);
            foreach (var (r, gate) in accepted)
            {
                var presentation = TimelineEventPresenter.Present(r.EventType, r.PayloadVersion, r.Payload);
                items.Add(new TimelineItemViewModel
                {
                    EventUid = r.EventUid, EntityType = r.EntityType, EntityId = r.EntityId,
                    EventType = r.EventType,
                    TitleAr = presentation.TitleAr, TitleEn = presentation.TitleEn,
                    DescriptionAr = presentation.DescriptionAr, DescriptionEn = presentation.DescriptionEn,
                    ActorEmployeeId = r.ActorEmployeeId,
                    ActorDisplayName = r.ActorEmployeeId.HasValue ? actorNames.GetValueOrDefault(r.ActorEmployeeId.Value) : null,
                    Icon = presentation.Icon, Color = presentation.Color,
                    CreatedAt = r.CreatedAt, PayloadVersion = r.PayloadVersion,
                    Visibility = r.Visibility, Url = gate.Url,
                    Source = TimelineItemSource.Event,
                });
            }
            return items;
        }

        // The per-entity half of the feed's authorization, reusing exactly what GetAsync uses. Returns null for
        // "this caller sees nothing here", which covers every reason a candidate is dropped without being an
        // error: an entity code no longer registered, a type with no timeline, no View grant, or a record that
        // no longer exists in this company.
        private async Task<RecentGate?> ResolveRecentGateAsync(
            string entityCode, int entityId, BusinessContext context, CancellationToken cancellationToken)
        {
            // GetAsync throws on an unregistered code because a caller naming one is a programming error. A feed
            // reads whatever history already holds, so a code left behind by a removed module is data, not a
            // defect - drop the row and keep the panel.
            if (!_registry.TryGetDefinition(entityCode, out var definition) || definition == null) return null;
            if (!definition.SupportsTimeline) return null;
            if (entityId <= 0) return null;

            var view = await _permissions.CanAsync(context, definition.Code, entityId, PlatformActions.View, cancellationToken);
            if (!view.Allowed) return null;   // SKIP, never throw - see the interface comment.

            // Existence AND company ownership in one call: Found is false for a deleted record and for one that
            // belongs to another company. A row whose entity cannot be resolved has no honest navigation target,
            // so it must not appear at all.
            var resolved = await _registry.ResolveAsync(definition.Code, entityId, context, cancellationToken);
            if (!resolved.Found) return null;

            var (visibilities, mayReadAnyRestricted) =
                await ResolveVisibilitiesAsync(context, definition.Code, entityId, cancellationToken);

            return new RecentGate(visibilities, mayReadAnyRestricted,
                                  resolved.Url ?? _registry.BuildUrl(definition.Code, entityId));
        }

        // Named rather than anonymous so an accepted row can be carried alongside its gate into the second pass,
        // where actor names are resolved for the whole page in one query.
        private sealed record RecentRow(
            Guid EventUid, string EntityType, int EntityId, string EventType,
            int? ActorEmployeeId, string? Payload, int PayloadVersion, string Visibility, DateTime CreatedAt);

        private sealed record RecentGate(HashSet<string> Visibilities, bool MayReadAnyRestricted, string? Url);

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