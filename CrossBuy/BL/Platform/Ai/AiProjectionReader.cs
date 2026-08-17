using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Platform;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CrossBuy.BL.Platform.Ai
{
    // AI Foundation — Increment 2. The SECURE READ SIDE.
    //
    // Increment 1 asked "may this EVENT enter the AI subsystem?" — once, at ingestion. This asks a
    // different question on EVERY read: "may THIS USER retrieve THIS PROJECTION NOW?"
    //
    // Those cannot be the same decision, because everything the first one depended on can change
    // afterwards: the task moves company, the user loses module access, the entity is deleted, the grant
    // is revoked, the shape is retired. A projection created in the past authorises nobody in the future,
    // so nothing here consults the stored row's history as evidence of permission.
    //
    // THE CHAIN, in order, per candidate row:
    //
    //   1. AUTHENTICATED USER  — IBusinessContextAccessor.GetCurrentAsync(). Throws when unresolved.
    //                            NEVER BusinessContext.ForSystem: the ingestion consumer legitimately
    //                            runs as a system context, and reusing that here would mean "a machine
    //                            may read it" silently became "any caller may read it".
    //   2. COMPANY             — from that context. The request contract has NO company field, so there
    //                            is nothing to spoof and nothing to validate.
    //   3. REVOCATION          — RevokedAt IS NULL, applied in SQL so revoked rows are never materialised.
    //   4. GRANT STILL ENABLED — fail-closed: a disabled grant stops reads, not just new writes.
    //   5. VERSION             — a closed (type, version) registry. No "latest" fallback.
    //   6. ENTITY STILL EXISTS — IEntityRegistry.ResolveAsync under the USER's context. A deleted record,
    //                            or one that has moved to another company, resolves to nothing.
    //   7. USER PERMISSION     — IPlatformPermissionProvider.CanAsync under the USER's context, for the
    //                            action the row's CLASSIFICATION demands.
    //   8. PAYLOAD             — parsed and re-selected against the shape's declared field list.
    //
    // Steps 6 and 7 are the ones that make a historical projection safe. They are deliberately not
    // collapsed: "the record is gone" and "you may not see it" are different facts, and the audit
    // counters must be able to distinguish them.
    public interface IAiProjectionReader
    {
        Task<AiRetrievalResult> RetrieveAsync(AiRetrievalRequest request, CancellationToken cancellationToken = default);
    }

    public sealed class AiProjectionReader : IAiProjectionReader
    {
        private readonly CrossDbContext _db;
        private readonly IBusinessContextAccessor _context;
        private readonly IEntityRegistry _registry;
        private readonly IPlatformPermissionProvider _permissions;
        private readonly IAiConsumerGrants _grants;
        private readonly IAiProjectionShapeRegistry _shapes;
        private readonly ILogger<AiProjectionReader> _log;

        public AiProjectionReader(
            CrossDbContext db,
            IBusinessContextAccessor context,
            IEntityRegistry registry,
            IPlatformPermissionProvider permissions,
            IAiConsumerGrants grants,
            IAiProjectionShapeRegistry shapes,
            ILogger<AiProjectionReader> log)
        {
            _db = db; _context = context; _registry = registry;
            _permissions = permissions; _grants = grants; _shapes = shapes; _log = log;
        }

        public async Task<AiRetrievalResult> RetrieveAsync(
            AiRetrievalRequest request, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);

            // ---- request validation: an unknown shape is an ERROR, not an empty page ----
            // Returning nothing would make a typo indistinguishable from "you may see none of these",
            // and would let a caller probe for which shapes exist by watching for a different response.
            if (string.IsNullOrWhiteSpace(request.ProjectionType) || !_shapes.IsKnownType(request.ProjectionType))
                throw new AiRetrievalRequestException(
                    $"Projection type '{request.ProjectionType}' is not a supported AI projection shape.");

            if (request.FromUtc.HasValue && request.ToUtc.HasValue)
            {
                if (request.ToUtc.Value < request.FromUtc.Value)
                    throw new AiRetrievalRequestException("The retrieval window ends before it begins.");
                if ((request.ToUtc.Value - request.FromUtc.Value).TotalDays > AiRetrievalLimits.MaxWindowDays)
                    throw new AiRetrievalRequestException(
                        $"The retrieval window exceeds {AiRetrievalLimits.MaxWindowDays} days. Page by time instead.");
            }

            // ---- 1. the authenticated user. Throws BusinessContextUnresolvedException when absent ----
            var user = await _context.GetCurrentAsync(cancellationToken);

            // A worker or system context must not retrieve on a user's behalf. Ingestion may run as
            // System; retrieval may not, and this is the line that says so.
            if (user.Source is BusinessContextSource.System or BusinessContextSource.Worker)
                throw new AiRetrievalNotPermittedException(
                    "AI retrieval requires an authenticated user context; a system or worker context cannot retrieve " +
                    "projections on a user's behalf.");

            if (user.CompanyId <= 0)
                throw new BusinessContextUnresolvedException(
                    "No company could be resolved for this request. There is no default company.");

            int limit = AiRetrievalLimits.Clamp(request.Limit);

            // ---- 2/3. company + revocation, in SQL. The company comes from `user`, never the request ----
            var query = _db.Set<AiProjection>().AsNoTracking()
                .Where(p => p.CompanyID == user.CompanyId)
                .Where(p => p.RevokedAt == null)
                .Where(p => p.ProjectionType == request.ProjectionType);

            if (!string.IsNullOrWhiteSpace(request.EntityType))
                query = query.Where(p => p.EntityType == request.EntityType);

            if (request.EntityIds is { Count: > 0 })
            {
                // Bounded: an id list is a filter, not a bulk-export channel.
                var ids = request.EntityIds.Distinct().Take(AiRetrievalLimits.MaxLimit).ToArray();
                query = query.Where(p => ids.Contains(p.EntityId));
            }

            if (request.FromUtc.HasValue) query = query.Where(p => p.OccurredAt >= request.FromUtc.Value);
            if (request.ToUtc.HasValue) query = query.Where(p => p.OccurredAt <= request.ToUtc.Value);

            // Deterministic ordering: newest first, then Id to break ties. Without the tie-break, two rows
            // sharing an OccurredAt could swap between pages and a caller would silently miss one.
            var candidates = await query
                .OrderByDescending(p => p.OccurredAt).ThenByDescending(p => p.Id)
                .Take(Math.Min(AiRetrievalLimits.MaxCandidateScan, limit * 4 + 1))
                .ToListAsync(cancellationToken);

            var items = new List<AiRetrievedProjection>(limit);
            var denied = new Dictionary<AiRetrievalDenialReason, int>();
            void Deny(AiRetrievalDenialReason reason)
                => denied[reason] = denied.TryGetValue(reason, out var n) ? n + 1 : 1;

            int considered = 0;
            bool truncated = false;

            foreach (var row in candidates)
            {
                if (items.Count >= limit) { truncated = true; break; }
                considered++;

                // ---- 4. is the grant still enabled? FAIL CLOSED ----
                //
                // Increment 1 made a disabled grant stop new projections. Leaving already-stored rows
                // readable would mean "revoke AI access to this event type" did not actually revoke
                // anything a caller could still see — an ambiguous security state. Disabling the grant
                // therefore ends retrieval immediately; physical revocation is a separate, auditable act.
                var grant = _grants.Evaluate(
                    BusinessEventConsumers.AiProjection, row.EventType, row.EntityType, row.Visibility);
                if (!grant.IsAllowed) { Deny(AiRetrievalDenialReason.GrantDisabled); continue; }

                // ---- 5. version compatibility, explicit only ----
                var shape = _shapes.Find(row.ProjectionType, row.ProjectionVersion);
                if (shape == null) { Deny(AiRetrievalDenialReason.UnsupportedVersion); continue; }

                // ---- 5b. RETENTION, enforced AT READ TIME (Increment 3) ----
                //
                // Read-time enforcement is mandatory and is not an optimisation of cleanup. A background
                // sweep runs on a schedule; between the expiry instant and the next sweep, the row is
                // still in the table. If retrievability depended on the sweep, expired data would remain
                // readable for exactly as long as the sweep was late — which is the failure mode the
                // whole retention idea exists to prevent.
                //
                // Missing or unparseable retention metadata FAILS CLOSED. A NULL ExpiresAtUtc is
                // "unknown", never "never expires": reading it as the latter is how a legacy row becomes
                // an immortal copy.
                if (row.ExpiresAtUtc == null || string.IsNullOrWhiteSpace(row.RetentionClass)
                    || !Enum.TryParse<AiRetentionClass>(row.RetentionClass, out var storedClass)
                    || storedClass == AiRetentionClass.Unknown)
                {
                    Deny(AiRetrievalDenialReason.RetentionUnknown);
                    continue;
                }

                if (row.ExpiresAtUtc.Value <= DateTime.UtcNow)
                {
                    // Distinct from Revoked on purpose: expiry is a clock, revocation is an event, and an
                    // operator asking "why is this gone?" needs to be able to tell them apart.
                    Deny(AiRetrievalDenialReason.Expired);
                    continue;
                }

                // ---- 6. does the entity still exist, in THIS user's company? ----
                //
                // Resolved under the USER's context, so a record that has been deleted, or has moved to
                // another company since the projection was written, resolves to nothing. This is what
                // stops a historical projection outliving its source.
                var resolved = await _registry.ResolveAsync(row.EntityType, row.EntityId, user, cancellationToken);
                if (!resolved.Found) { Deny(AiRetrievalDenialReason.EntityNoLongerResolvable); continue; }

                // ---- 7. may THIS user view it, at THIS classification? ----
                string action = RequiredAction(row.Visibility);
                if (action.Length == 0) { Deny(AiRetrievalDenialReason.ClassificationTooHigh); continue; }

                var decision = await _permissions.CanAsync(
                    user, row.EntityType, row.EntityId, action, cancellationToken);
                if (!decision.Allowed)
                {
                    // Counted by CLASSIFICATION when the row demanded an elevated tier, so the audit can
                    // distinguish "you may not see this entity at all" from "you may see the entity but
                    // not facts at this sensitivity".
                    Deny(action == PlatformActions.View
                        ? AiRetrievalDenialReason.PermissionDenied
                        : AiRetrievalDenialReason.ClassificationTooHigh);
                    continue;
                }

                // ---- 8. payload: untrusted stored data until proven otherwise ----
                var payload = AiPayloadReader.TryRead(row.PayloadJson, shape);
                if (payload == null) { Deny(AiRetrievalDenialReason.MalformedPayload); continue; }

                items.Add(new AiRetrievedProjection
                {
                    ProjectionId = row.Id,
                    ProjectionType = row.ProjectionType,
                    ProjectionVersion = row.ProjectionVersion,
                    EntityType = row.EntityType,
                    EntityId = row.EntityId,
                    OccurredAt = row.OccurredAt,
                    ApprovedPayload = payload,
                    // Note what is NOT copied: CompanyID, BranchID, BusinessEventId, EventUid,
                    // ActorEmployeeId, Consumer, PayloadJson. Authorization metadata and provenance stay
                    // on this side of the boundary.
                });
            }

            if (!truncated && candidates.Count > considered) truncated = true;

            // ---- read-side audit (§21): who, which company, what, how much, what was withheld ----
            // Counts and reasons only — never payload contents, and never the ids of denied rows, which
            // would disclose the existence of records the caller may not see.
            _log.LogInformation(
                "AI retrieval: user={UserId} employee={EmployeeId} company={Company} shape={Shape} " +
                "considered={Considered} returned={Returned} denied={Denied} reasons=[{Reasons}] " +
                "truncated={Truncated} correlation={Correlation}",
                user.UserId, user.EmployeeId, user.CompanyId, request.ProjectionType,
                considered, items.Count, denied.Values.Sum(),
                string.Join(",", denied.Select(kv => $"{kv.Key}:{kv.Value}")),
                truncated, user.CorrelationId);

            return new AiRetrievalResult
            {
                Items = items,
                Considered = considered,
                Returned = items.Count,
                Denied = denied,
                Truncated = truncated,
            };
        }

        // Classification → the platform action a caller must hold. This REUSES the existing visibility
        // vocabulary and the existing three View tiers rather than inventing an AI taxonomy: the tiers
        // already mean exactly this, and a second scale would drift from the first.
        //
        // System is machine bookkeeping — dispatcher and integrity facts. It is never surfaced to an end
        // user through any product surface, so it is refused here outright rather than mapped to a tier
        // somebody might later grant. An empty string means "no action can satisfy this".
        private static string RequiredAction(string visibility) => visibility switch
        {
            BusinessEventVisibility.Internal => PlatformActions.View,
            BusinessEventVisibility.Confidential => PlatformActions.ViewConfidential,
            BusinessEventVisibility.Restricted => PlatformActions.ViewRestricted,
            _ => "",     // System, or anything unrecognised ⇒ refuse
        };
    }
}
