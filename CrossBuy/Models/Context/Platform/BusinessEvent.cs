namespace CrossBuy.Models.Context.Platform
{
    // Platform Kernel (ADR-001) — the append-only business event log. ONE row per durable business fact.
    //
    // Written INSIDE the business transaction (ScopedTx) that produced the fact: the event and the fact
    // commit or roll back together. Never written after CommitAsync, never wrapped in a swallowing
    // try/catch — that is what separates it from Notification (best-effort, after-commit).
    //
    // Not a BaseEntity: CreatedAt/actor/company are first-class columns here with stricter semantics
    // than BaseEntity's nullable audit stamps, and the row is immutable once written.
    public class BusinessEvent
    {
        public long EventId { get; set; }

        // Stable public identity, safe to expose in URLs and to consumers. Assigned by the service.
        public Guid EventUid { get; set; }

        public int CompanyID { get; set; }
        public int? BranchID { get; set; }

        // Canonical EntityDefinition.Code — validated by IEntityRegistry before insert. Never free text.
        public string EntityType { get; set; } = "";
        public int EntityId { get; set; }

        // "<EntityType>.<Action>" — validated against EntityType before insert.
        public string EventType { get; set; } = "";

        // Employee who caused the fact. null = system.
        public int? ActorEmployeeId { get; set; }

        // JSON, capped at BusinessEventService.MaxPayloadBytes. Never files/binary/secrets/tokens.
        public string? Payload { get; set; }

        // Schema version of Payload only — NOT an entity concurrency token.
        public int PayloadVersion { get; set; } = 1;

        public Guid? CorrelationId { get; set; }

        // Idempotency key, unique per company where not null.
        public string? DedupKey { get; set; }

        // BusinessEventVisibility value. Enforced by CK_BusinessEvents_Visibility.
        public string Visibility { get; set; } = "";

        public DateTime CreatedAt { get; set; }

        // Set when EVERY registered consumer reached Done. Convenience/reporting only — a dispatcher must
        // never use this (or EventId) to decide what to process; per-consumer state lives in the dispatch
        // table (ADR-003).
        public DateTime? CompletedAt { get; set; }
    }

    // Platform Kernel (ADR-003) — per-consumer dispatch state. One row per (event, consumer).
    //
    // A single DispatchedAt flag on the event cannot express six independent consumers: if search
    // indexing fails while notifications succeeded there is no way to retry only search. This table is
    // the outbox work queue, and it is claimed by STATUS, never by an EventId high-water cursor —
    // identity values are assigned at INSERT but become visible at COMMIT, so a cursor silently skips
    // any event whose transaction committed after a later-numbered one.
    public class BusinessEventDispatch
    {
        public long ID { get; set; }
        public long EventId { get; set; }

        // BusinessEventConsumers value.
        public string Consumer { get; set; } = "";

        // BusinessEventDispatchStatus value. Enforced by CK_BusinessEventDispatch_Status.
        public string Status { get; set; } = "";

        public int Attempts { get; set; }

        // Truncated to 400 chars by the store — the column is nvarchar(400).
        public string? Error { get; set; }

        public DateTime? UpdatedAt { get; set; }
    }
}