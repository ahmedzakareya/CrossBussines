using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace CrossBuy.Models.Platform
{
    // Platform Kernel (PKS-001 / ADR-001, ADR-004) — the immutable BusinessEvent contracts.

    // ---------------------------------------------------------------------------------------------
    // Visibility — FROZEN vocabulary. Mirrored by the CK_BusinessEvents_Visibility check constraint in
    // deploy/sql/platform_business_events.sql. Never accept a value outside this set (ADR-004).
    // ---------------------------------------------------------------------------------------------
    public static class BusinessEventVisibility
    {
        // Any user who may VIEW the entity may read the event. The default for ordinary document facts.
        public const string Internal = "Internal";

        // Requires elevated module rights (accounting: post) — e.g. margin, cost, credit decisions.
        public const string Confidential = "Confidential";

        // Readable only by the actor or a module manager — e.g. personal / disciplinary facts.
        public const string Restricted = "Restricted";

        // Machine facts (integrity runs, dispatcher bookkeeping). Managers only; never surfaced to end users.
        public const string System = "System";

        private static readonly HashSet<string> All = new(StringComparer.Ordinal) { Internal, Confidential, Restricted, System };

        public static bool IsValid(string? value) => value != null && All.Contains(value);
        public static IReadOnlyCollection<string> Values => All;
    }

    // ---------------------------------------------------------------------------------------------
    // Consumers — FROZEN vocabulary of the outbox fan-out targets. Only consumers that are ACTUALLY
    // implemented may appear in Registered: a dispatch row is created per registered consumer inside the
    // business transaction, so registering a consumer with no implementation would accumulate dead rows.
    // ---------------------------------------------------------------------------------------------
    public static class BusinessEventConsumers
    {
        public const string TimelineProjection = "TimelineProjection";

        // Slice 2 — the second real consumer. Added here only because it is actually implemented.
        public const string NotificationProjection = "NotificationProjection";

        // AI Foundation Increment 1 — the third real consumer. READ-ONLY: it writes only AiProjections and
        // holds no business write rights (see AiProjectionConsumer). Registering it here starts creating a
        // dispatch row per NEW event; existing rows are untouched, so nothing is back-filled into AI.
        //
        // Note that being registered grants it NOTHING: every event it receives is refused unless
        // IAiConsumerGrants holds an explicit grant for that event type. Registration decides what it is
        // OFFERED; the grant decides what it may KEEP.
        public const string AiProjection = "AiProjection";

        // Implemented so far. Search / Workflow / Integrations are added here as each one lands
        // (PKS-001 "Per-consumer dispatch state"). A name in this list with no IBusinessEventConsumer
        // registered in DI accumulates dispatch rows nothing drains — the worker logs an error if that happens.
        public static readonly string[] Registered = { TimelineProjection, NotificationProjection, AiProjection };

        public static bool IsRegistered(string? consumer) =>
            consumer != null && Registered.Contains(consumer, StringComparer.Ordinal);
    }

    // ---------------------------------------------------------------------------------------------
    // Dispatch status — FROZEN. Mirrored by CK_BusinessEventDispatch_Status.
    // Claimed exists so a worker can take a row ATOMICALLY (ADR-003): it is a work-in-progress marker,
    // reclaimed after BusinessEventDispatchOptions.StaleClaimMinutes if the worker died holding it.
    // ---------------------------------------------------------------------------------------------
    public static class BusinessEventDispatchStatus
    {
        public const string Pending = "Pending";
        public const string Claimed = "Claimed";
        public const string Done = "Done";
        public const string Failed = "Failed";
    }

    // ---------------------------------------------------------------------------------------------
    // The caller-supplied fact. Producers fill Entity/EventType/Payload; the service fills identity and
    // context. Company/Branch/Actor overrides are honoured ONLY for a system BusinessContext.
    // ---------------------------------------------------------------------------------------------
    public sealed class BusinessEventRecord
    {
        // Canonical entity code from IEntityRegistry. Validated — unknown codes throw.
        public required string EntityCode { get; init; }
        public required int EntityId { get; init; }

        // "<EntityCode>.<Action>" — e.g. "SalesInvoice.Created". Validated against EntityCode.
        public required string EventType { get; init; }

        // Anything JSON-serializable, or null. Serialized once; capped at MaxPayloadBytes.
        // NEVER put files, binary, secrets, passwords, tokens, connection strings or unrestricted
        // employee data here (PKS-001 payload rules).
        public object? Payload { get; init; }

        // Schema version of Payload ONLY. Not an entity concurrency token (PKS-001 "Payload versioning").
        public int PayloadVersion { get; init; } = 1;

        public string Visibility { get; init; } = BusinessEventVisibility.Internal;

        // Idempotency key, scoped per company. A second RecordAsync with the same key is a no-op that
        // returns the ORIGINAL EventId. Same pattern as TaskAutoLogs.RuleKey.
        public string? DedupKey { get; init; }

        // Explicit correlation, when a producer is continuing an operation that started elsewhere.
        // Omitted (the normal case) means the BusinessContext's per-scope correlation id is used.
        public Guid? CorrelationId { get; init; }

        // ---- trusted overrides: applied only when BusinessContext.IsSystem ----
        public int? CompanyIdOverride { get; init; }
        public int? BranchIdOverride { get; init; }
        public int? ActorEmployeeIdOverride { get; init; }

        // Defaults to DateTime.UtcNow at record time when omitted.
        public DateTime? OccurredAt { get; init; }
    }

    // ---------------------------------------------------------------------------------------------
    // The serialized envelope handed to consumers. Property names are the wire contract (PKS-001).
    // ---------------------------------------------------------------------------------------------
    public sealed class BusinessEventEnvelope
    {
        [JsonPropertyName("eventUid")] public required Guid EventUid { get; init; }
        [JsonPropertyName("eventType")] public required string EventType { get; init; }
        [JsonPropertyName("entity")] public required BusinessEventEntity Entity { get; init; }
        [JsonPropertyName("actor")] public required BusinessEventActor Actor { get; init; }
        [JsonPropertyName("context")] public required BusinessEventContext Context { get; init; }
        [JsonPropertyName("payloadVersion")] public required int PayloadVersion { get; init; }
        [JsonPropertyName("visibility")] public required string Visibility { get; init; }
        [JsonPropertyName("occurredAt")] public required DateTime OccurredAt { get; init; }
        [JsonPropertyName("payload")] public JsonNode? Payload { get; init; }
    }

    public sealed class BusinessEventEntity
    {
        [JsonPropertyName("code")] public required string Code { get; init; }
        [JsonPropertyName("id")] public required int Id { get; init; }
    }

    public sealed class BusinessEventActor
    {
        [JsonPropertyName("employeeId")] public int? EmployeeId { get; init; }
    }

    public sealed class BusinessEventContext
    {
        [JsonPropertyName("companyId")] public required int CompanyId { get; init; }
        [JsonPropertyName("branchId")] public int? BranchId { get; init; }
        [JsonPropertyName("correlationId")] public Guid? CorrelationId { get; init; }
    }

    // ---------------------------------------------------------------------------------------------
    // Failures. These are DELIBERATELY exceptions and never swallowed: a BusinessEvent that cannot be
    // written must fail the business transaction that produced it (ADR-001 rule 3).
    // ---------------------------------------------------------------------------------------------
    public sealed class BusinessEventPayloadTooLargeException : InvalidOperationException
    {
        public BusinessEventPayloadTooLargeException(string eventType, int actualBytes, int maxBytes)
            : base($"BusinessEvent '{eventType}' payload is {actualBytes} bytes which exceeds the {maxBytes}-byte limit. " +
                   "Store the large content in its own table or file store and reference it from the payload.")
        { ActualBytes = actualBytes; MaxBytes = maxBytes; }

        public int ActualBytes { get; }
        public int MaxBytes { get; }
    }

    public sealed class BusinessEventContractException : InvalidOperationException
    {
        public BusinessEventContractException(string message) : base(message) { }
    }
}