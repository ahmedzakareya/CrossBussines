using CrossBuy.Models.Context;
using CrossBuy.Models.Platform;
using CrossBuy.Models.Context.Platform;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CrossBuy.BL.Platform.Ai
{
    // AI Foundation — the READ-ONLY outbox consumer that feeds the AI subsystem.
    //
    // It writes exactly one table (AiProjections) and reads none of the business tables. It creates,
    // updates, approves, posts, sends and executes NOTHING. It calls no model.
    //
    // THE SECURITY CHAIN, in the order it runs — these are two different questions and they are kept
    // apart deliberately (§13):
    //
    //   1. GRANT GATE      — "may this KIND of fact enter the AI subsystem at all?"  IAiConsumerGrants.
    //                        Default deny. A new event type is refused until someone adds a grant and a
    //                        builder together.
    //   2. TENANCY         — the company comes from the EVENT, never from a caller, and an unresolvable
    //                        company fails closed. There is no default company.
    //   3. PERMISSION GATE — "does this record actually exist inside that company?"
    //                        IPlatformPermissionProvider with a SYSTEM context and PlatformActions.View,
    //                        which is the minimum legitimate right and the ONLY one SystemContextPolicy
    //                        grants. This is a genuinely independent check: the dispatch worker holds a
    //                        PlatformDispatch bypass for the whole batch, so EF's company filters are OFF
    //                        while this consumer runs. Without an explicit ownership check, a malformed or
    //                        tampered event could name a company the entity does not belong to and the
    //                        filters would not catch it.
    //   4. MINIMIZATION    — a registered builder selects named fields. No builder ⇒ no payload ⇒ no row.
    //
    // WHY THE PERMISSION CHECK DOES NOT REPLACE THE GRANT. The permission provider answers a question
    // about an ACTOR and a RECORD. It cannot answer "is this class of business fact appropriate to copy
    // into an AI corpus", which is a data-governance decision with no actor in it. Collapsing them would
    // mean any event a system context may view is AI-ingestible — which is every Internal event in the
    // product, i.e. no boundary at all.
    //
    // WHY IT DOES NOT REPLACE THE PERMISSION CHECK EITHER. The grant is per EVENT TYPE, not per record.
    // It says "Task lifecycle facts are fine"; it cannot say "task 91 really belongs to company 7".
    public sealed class AiProjectionConsumer : IBusinessEventConsumer
    {
        private readonly IAiConsumerGrants _grants;
        private readonly IEnumerable<IAiProjectionBuilder> _builders;
        private readonly IAiProjectionStore _store;
        private readonly IPlatformPermissionProvider _permissions;
        private readonly IAiRetentionPolicyRegistry _retention;
        private readonly CrossDbContext _db;
        private readonly ILogger<AiProjectionConsumer> _log;

        // No IHttpContextAccessor and no session, by construction rather than by discipline: a background
        // consumer has no browser session, and a consumer that could reach one would resolve the wrong
        // company for whichever request happened to be in flight.
        public AiProjectionConsumer(
            IAiConsumerGrants grants,
            IEnumerable<IAiProjectionBuilder> builders,
            IAiProjectionStore store,
            IPlatformPermissionProvider permissions,
            IAiRetentionPolicyRegistry retention,
            CrossDbContext db,
            ILogger<AiProjectionConsumer> log)
        {
            _grants = grants; _builders = builders; _store = store;
            _permissions = permissions; _retention = retention; _db = db; _log = log;
        }

        public string Consumer => BusinessEventConsumers.AiProjection;

        public async Task HandleAsync(BusinessEventEnvelope envelope, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(envelope);

            // ---- 1. grant gate: default deny ----
            var decision = _grants.Evaluate(
                Consumer, envelope.EventType, envelope.Entity.Code, envelope.Visibility);

            if (!decision.IsAllowed)
            {
                // A DENIAL IS A SUCCESSFUL OUTCOME, not a failure. Throwing would mark the dispatch row
                // Failed, retry it to MaxAttempts and leave an operator staring at a red queue that is
                // behaving exactly as designed. Returning marks it Done: the event was considered and
                // correctly not projected. The reason is logged so "why is there no AI data for X?" is
                // answerable.
                _log.LogDebug(
                    "AI projection declined for {EventType} on {Entity}/{Id}: {Reason}",
                    envelope.EventType, envelope.Entity.Code, envelope.Entity.Id, decision.Reason);
                return;
            }

            var grant = decision.Grant!;

            // ---- 2. tenancy, from the event and nowhere else ----
            int companyId = envelope.Context.CompanyId;
            if (companyId <= 0)
                throw new BusinessContextUnresolvedException(
                    $"Event {envelope.EventUid} carries no company; an AI projection cannot be attributed. " +
                    "There is no default company.");

            // ForSystem is the narrowest context that can ask an ownership question with no interactive
            // identity. It is scoped to THIS event's company, so it cannot reach another tenant, and
            // SystemContextPolicy allows it View and nothing else.
            var systemContext = BusinessContext.ForSystem(companyId, envelope.Context.CorrelationId);

            // ---- 3. permission gate: does this record exist inside that company? ----
            var permitted = await _permissions.CanAsync(
                systemContext, envelope.Entity.Code, envelope.Entity.Id, PlatformActions.View, cancellationToken);

            if (!permitted.Allowed)
            {
                // Also a deliberate non-failure: the correct outcome is "no AI data", and retrying cannot
                // change a company-ownership answer.
                _log.LogWarning(
                    "AI projection refused for {EventType} on {Entity}/{Id} in company {Company}: {Reason}",
                    envelope.EventType, envelope.Entity.Code, envelope.Entity.Id, companyId, permitted.Reason);
                return;
            }

            // ---- 4. minimization ----
            var builder = _builders.FirstOrDefault(b =>
                string.Equals(b.ProjectionType, grant.ProjectionType, StringComparison.Ordinal) &&
                b.ProjectionVersion == grant.ProjectionVersion);

            if (builder == null)
            {
                // A grant whose builder is missing is a configuration defect, not a data condition. It
                // THROWS so it surfaces as a failed dispatch row an operator will see, rather than
                // silently ingesting nothing for an event someone believes is being captured.
                throw new InvalidOperationException(
                    $"AI grant for '{grant.EventType}' names projection '{grant.ProjectionType}' v{grant.ProjectionVersion}, " +
                    "but no builder is registered for that shape.");
            }

            var projection = builder.Build(envelope);
            if (projection == null)
            {
                // Malformed/unsupported payload ⇒ fail closed, deterministically, with no row.
                _log.LogWarning(
                    "AI projection builder '{Shape}' could not project {EventType} on {Entity}/{Id}; no projection written.",
                    grant.ProjectionType, envelope.EventType, envelope.Entity.Code, envelope.Entity.Id);
                return;
            }

            // Provenance is resolved here, not by the builder. EventUid is uniquely indexed and is the
            // envelope's link back to the durable log; the numeric EventId is what the projection's
            // foreign key uses.
            long eventId = await _db.BusinessEvents.AsNoTracking()
                .Where(e => e.EventUid == envelope.EventUid)
                .Select(e => e.EventId)
                .FirstOrDefaultAsync(cancellationToken);

            if (eventId <= 0)
                throw new InvalidOperationException(
                    $"Event {envelope.EventUid} has no row in BusinessEvents; an AI projection must be traceable to one.");

            // ---- 5. retention: a projection with no declared policy is NOT PERSISTED ----
            //
            // This is the rule that stops an immortal copy being created by omission. Adding a shape
            // without deciding how long its data may live is a governance gap, and the correct outcome is
            // no data — not data that lives forever because nobody said otherwise.
            var retention = _retention.Find(projection.ProjectionType, projection.ProjectionVersion);
            var expiresAt = _retention.ExpiresAt(projection.ProjectionType, projection.ProjectionVersion, envelope.OccurredAt);
            if (retention == null || expiresAt == null)
            {
                _log.LogWarning(
                    "AI projection refused for {EventType}: shape {Shape} v{Version} has no bounded retention policy, " +
                    "so it is not eligible for persistence.",
                    envelope.EventType, projection.ProjectionType, projection.ProjectionVersion);
                return;
            }

            await _store.UpsertAsync(new AiProjection
            {
                RetentionClass = retention.Class.ToString(),
                ExpiresAtUtc = expiresAt,
                BusinessEventId = eventId,
                EventUid = envelope.EventUid,
                Consumer = Consumer,
                CompanyID = companyId,
                BranchID = envelope.Context.BranchId,
                EntityType = envelope.Entity.Code,
                EntityId = envelope.Entity.Id,
                EventType = envelope.EventType,
                ActorEmployeeId = envelope.Actor.EmployeeId,
                ProjectionType = projection.ProjectionType,
                ProjectionVersion = projection.ProjectionVersion,
                PayloadJson = System.Text.Json.JsonSerializer.Serialize(projection.Payload),
                // Increment 2 — the source event's classification travels with the row, so the READ side
                // can demand the matching View tier per row rather than assuming every projection is
                // Internal because that is all the grant table admits today.
                Visibility = envelope.Visibility,
                OccurredAt = envelope.OccurredAt,
                ProjectedAt = DateTime.UtcNow,
            }, cancellationToken);
        }
    }
}
