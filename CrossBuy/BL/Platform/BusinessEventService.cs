using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Platform;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CrossBuy.BL.Platform
{
    // Platform Kernel (ADR-001) — records durable business facts. NOTHING ELSE.
    //
    // It does not notify, index, run AI or execute workflow: those are consumers that read the outbox
    // after the transaction commits. Keeping RecordAsync free of side effects is what makes it safe to
    // call inside a financial transaction.
    public interface IBusinessEventService
    {
        // Returns the EventId of the persisted event, or of the EXISTING event when DedupKey matches one.
        Task<long> RecordAsync(BusinessEventRecord record, CancellationToken cancellationToken = default);

        // The wire envelope for a persisted event, handed to consumers.
        BusinessEventEnvelope BuildEnvelope(BusinessEvent stored);
    }

    public class BusinessEventService : IBusinessEventService
    {
        // 64 KB of UTF-8. The event log is destined to be the largest table in the database, so a payload
        // is a SUMMARY, never a document: no entity graphs, files, binary, secrets, passwords, tokens,
        // connection strings, or unrestricted employee data (PKS-001 payload rules).
        public const int MaxPayloadBytes = 64 * 1024;

        private static readonly JsonSerializerOptions PayloadJson = new(JsonSerializerDefaults.Web);

        private readonly CrossDbContext _db;
        private readonly IEntityRegistry _registry;
        private readonly IBusinessContextAccessor _contextAccessor;
        private readonly ILogger<BusinessEventService> _log;

        public BusinessEventService(
            CrossDbContext db,
            IEntityRegistry registry,
            IBusinessContextAccessor contextAccessor,
            ILogger<BusinessEventService> log)
        { _db = db; _registry = registry; _contextAccessor = contextAccessor; _log = log; }

        public async Task<long> RecordAsync(BusinessEventRecord record, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(record);

            // ---- 1. contract validation. These throw: a malformed event is a bug, and letting it through
            //         would put an unparseable row in the permanent audit log.
            var definition = _registry.GetDefinition(record.EntityCode);   // unknown code -> throws
            BusinessEventTypes.Validate(record.EventType, definition.Code);

            if (record.EntityId <= 0)
                throw new BusinessEventContractException($"Event '{record.EventType}' requires a positive EntityId.");
            if (!BusinessEventVisibility.IsValid(record.Visibility))
                throw new BusinessEventContractException(
                    $"Visibility '{record.Visibility}' is not one of {string.Join(" | ", BusinessEventVisibility.Values)}.");
            if (record.PayloadVersion < 1)
                throw new BusinessEventContractException("PayloadVersion is the payload schema version and starts at 1.");

            // ---- 2. context. Company/branch/actor come from BusinessContext; the explicit overrides are
            //         honoured only for trusted system code (a hosted service acting for a company).
            var context = await _contextAccessor.GetCurrentAsync(cancellationToken);
            int companyId = context.IsSystem && record.CompanyIdOverride is > 0
                ? record.CompanyIdOverride!.Value
                : context.CompanyId;
            int? branchId = context.IsSystem && record.BranchIdOverride.HasValue
                ? record.BranchIdOverride
                : context.BranchId;
            int? actorEmployeeId = context.IsSystem && record.ActorEmployeeIdOverride.HasValue
                ? record.ActorEmployeeIdOverride
                : context.EmployeeId;

            // ---- 3. payload
            string? payloadJson = SerializePayload(record);

            // ---- 4. the transaction rule (ADR-001). The event MUST land inside the caller's business
            //         transaction. This service never opens or commits one — it enrols in the ambient
            //         ScopedTx so the fact and its event share one fate. No ambient transaction means the
            //         producer would be writing an event that survives a rolled-back business operation,
            //         which is exactly the failure mode the kernel exists to prevent, so it is refused.
            if (_db.Database.CurrentTransaction == null)
                throw new BusinessEventContractException(
                    $"Event '{record.EventType}' was recorded with no ambient transaction. RecordAsync must be called " +
                    "inside the business ScopedTx, BEFORE CommitAsync — never after it.");

            // ---- 5. idempotency. A duplicate key returns the original id and writes nothing.
            if (!string.IsNullOrWhiteSpace(record.DedupKey))
            {
                var existing = await _db.BusinessEvents.AsNoTracking()
                    .Where(e => e.CompanyID == companyId && e.DedupKey == record.DedupKey)
                    .Select(e => e.EventId)
                    .FirstOrDefaultAsync(cancellationToken);
                if (existing != 0)
                {
                    _log.LogDebug("BusinessEvent {EventType} deduplicated on key {DedupKey} -> event {EventId}",
                        record.EventType, record.DedupKey, existing);
                    return existing;
                }
            }

            var stored = new BusinessEvent
            {
                EventUid = Guid.NewGuid(),
                CompanyID = companyId,
                BranchID = branchId,
                EntityType = definition.Code,
                EntityId = record.EntityId,
                EventType = record.EventType,
                ActorEmployeeId = actorEmployeeId,
                Payload = payloadJson,
                PayloadVersion = record.PayloadVersion,
                CorrelationId = record.CorrelationId ?? context.CorrelationId,
                DedupKey = string.IsNullOrWhiteSpace(record.DedupKey) ? null : record.DedupKey,
                Visibility = record.Visibility,
                CreatedAt = record.OccurredAt ?? DateTime.UtcNow,
                CompletedAt = null,
            };

            _db.BusinessEvents.Add(stored);

            // ---- 6. outbox rows, same transaction. One per REGISTERED consumer: the fan-out targets are
            //         decided at record time so a consumer can never miss an event that was written while
            //         it was offline.
            try
            {
                await _db.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateException) when (!string.IsNullOrWhiteSpace(record.DedupKey))
            {
                // Lost a race on UX_BusinessEvents_DedupKey: another writer inserted the same key first.
                // Detach our row and return theirs — still exactly one event for the key.
                _db.Entry(stored).State = EntityState.Detached;
                var winner = await _db.BusinessEvents.AsNoTracking()
                    .Where(e => e.CompanyID == companyId && e.DedupKey == record.DedupKey)
                    .Select(e => e.EventId)
                    .FirstOrDefaultAsync(cancellationToken);
                if (winner != 0) return winner;
                throw;   // not the dedup index — the business transaction must fail
            }

            foreach (var consumer in BusinessEventConsumers.Registered)
            {
                if (!IsEligible(consumer, definition)) continue;

                _db.BusinessEventDispatches.Add(new BusinessEventDispatch
                {
                    EventId = stored.EventId,
                    Consumer = consumer,
                    Status = BusinessEventDispatchStatus.Pending,
                    Attempts = 0,
                    UpdatedAt = DateTime.UtcNow,
                });
            }
            await _db.SaveChangesAsync(cancellationToken);

            return stored.EventId;
        }

        // ---- consumer eligibility -----------------------------------------------------------------
        //
        // Fan-out is decided here, at record time, so the question "will any consumer ever be able to do
        // something with this row" has to be answered here too. Creating work that is guaranteed to fail
        // is not a harmless extra row: it retries under the backoff policy, ends terminal at
        // Attempts = MaxAttempts, and sits in the operator's failure list forever describing a defect
        // that does not exist. CrossBuyDev carries 77 of exactly those.
        //
        // TIMELINE PROJECTION is eligible only for an entity that declares a timeline. The registry
        // already answered that when it resolved `definition`, which is why this takes the definition
        // rather than looking the entity up again - a second lookup would be a second source of truth for
        // one capability, and the two would eventually disagree.
        //
        // This is CAPABILITY-BASED and deliberately names no entity. JournalEntry is the case that
        // exposed it, but the rule is about SupportsTimeline, so an entity that gains or loses a timeline
        // changes its own dispatch behaviour by changing its own definition and nothing here moves.
        //
        // TimelineProjectionConsumer keeps its strict guard. It still rejects an unsupported entity if one
        // ever reaches it - through replay, a definition changed after recording, or a row written by an
        // older build. This suppresses work that should never be created; it does not make the consumer
        // lenient about work that arrives anyway, and those are different jobs.
        //
        // EVERY OTHER CONSUMER IS UNAFFECTED, by default rather than by listing them: an unknown consumer
        // name is eligible. Notification and AI projections do not depend on a timeline screen, and a new
        // consumer must not silently inherit a rule written for this one.
        private static bool IsEligible(string consumer, EntityDefinition definition)
            => !string.Equals(consumer, BusinessEventConsumers.TimelineProjection, StringComparison.Ordinal)
               || definition.SupportsTimeline;

        public BusinessEventEnvelope BuildEnvelope(BusinessEvent stored)
        {
            ArgumentNullException.ThrowIfNull(stored);
            JsonNode? payload = null;
            if (!string.IsNullOrWhiteSpace(stored.Payload))
            {
                // A stored payload that will not parse is data corruption, not a runtime condition to hide:
                // surface it as null payload plus a warning so the consumer still sees the event.
                try { payload = JsonNode.Parse(stored.Payload); }
                catch (JsonException ex)
                {
                    _log.LogWarning(ex, "BusinessEvent {EventId} has an unparseable payload; delivering envelope without it", stored.EventId);
                }
            }

            return new BusinessEventEnvelope
            {
                EventUid = stored.EventUid,
                EventType = stored.EventType,
                Entity = new BusinessEventEntity { Code = stored.EntityType, Id = stored.EntityId },
                Actor = new BusinessEventActor { EmployeeId = stored.ActorEmployeeId },
                Context = new BusinessEventContext
                {
                    CompanyId = stored.CompanyID,
                    BranchId = stored.BranchID,
                    CorrelationId = stored.CorrelationId,
                },
                PayloadVersion = stored.PayloadVersion,
                Visibility = stored.Visibility,
                OccurredAt = stored.CreatedAt,
                Payload = payload,
            };
        }

        private static string? SerializePayload(BusinessEventRecord record)
        {
            if (record.Payload == null) return null;

            var json = record.Payload as string ?? JsonSerializer.Serialize(record.Payload, PayloadJson);
            if (record.Payload is string raw)
            {
                // A caller-supplied string must already be JSON — the column carries a JSON check
                // constraint on SQL Server 2016+, so an unvalidated string would fail at COMMIT time
                // (i.e. inside someone else's financial transaction) instead of here.
                try { JsonNode.Parse(raw); }
                catch (JsonException ex)
                {
                    throw new BusinessEventContractException(
                        $"Event '{record.EventType}' payload was supplied as a string that is not valid JSON: {ex.Message}");
                }
                json = raw;
            }

            int bytes = Encoding.UTF8.GetByteCount(json);
            if (bytes > MaxPayloadBytes)
                throw new BusinessEventPayloadTooLargeException(record.EventType, bytes, MaxPayloadBytes);

            return json;
        }
    }
}