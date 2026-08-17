using System.Text.Json.Serialization;

namespace CrossBuy.Models.Platform
{
    // AI Foundation — Increment 1. The contract AI is allowed to see, and nothing else.
    //
    // WHAT THIS BOUNDARY IS FOR. Everything upstream of it (BusinessEvents, the outbox, per-consumer
    // dispatch) already exists and is proven. What did not exist was an answer to the question "which
    // business facts is the AI subsystem entitled to, and in what shape?". Registering a consumer is not
    // that answer: a registered consumer receives a dispatch row for EVERY event, which would make every
    // future module AI-visible the moment someone raised an event. This file is the explicit answer.
    //
    // NOTHING HERE CALLS A MODEL. There is no provider, no prompt, no embedding, no vector store and no
    // retrieval. This increment builds the security and projection boundary only.

    // The persisted row itself lives with the other kernel ENTITIES, in
    // CrossBuy.Models.Context.Platform.AiProjection — the same separation BusinessEvent uses. What follows
    // here are the CONTRACTS: the shape a builder produces and the grant that authorises it.

    // ---------------------------------------------------------------------------------------------
    // The in-memory envelope a builder fills. Kept separate from the entity so a builder cannot set
    // provenance columns — the consumer owns those.
    // ---------------------------------------------------------------------------------------------
    public sealed class AiProjectionEnvelope
    {
        [JsonPropertyName("projectionType")] public required string ProjectionType { get; init; }
        [JsonPropertyName("projectionVersion")] public required int ProjectionVersion { get; init; }

        // Field-by-field, explicitly selected. See AiProjectionBuilders for what is deliberately absent.
        [JsonPropertyName("payload")] public required IReadOnlyDictionary<string, object?> Payload { get; init; }
    }

    // ---------------------------------------------------------------------------------------------
    // The grant. DEFAULT DENY: an event type absent from the table is refused, so a new module becomes
    // AI-visible only when someone deliberately adds a grant AND writes a builder for it.
    // ---------------------------------------------------------------------------------------------
    public sealed class AiConsumerGrant
    {
        public required string Consumer { get; init; }
        public required string EventType { get; init; }
        public required string EntityType { get; init; }

        // The projection shape this grant authorises. A grant without a registered builder is a
        // configuration error and is refused at startup rather than silently projecting nothing.
        public required string ProjectionType { get; init; }
        public required int ProjectionVersion { get; init; }

        // The HIGHEST event visibility this grant admits. A grant for an Internal fact must not quietly
        // start admitting Confidential ones if a producer later raises the same event type at a higher
        // classification — that is a realistic drift, and it would be invisible without this field.
        public required string MaxVisibility { get; init; }

        // Operational kill switch: revoke AI ingestion of one event type without a schema change and
        // without unregistering the consumer (which would strand its dispatch rows).
        public bool IsEnabled { get; init; } = true;
    }

    // The outcome of the grant gate. A record rather than a bool, because "denied, and why" is what an
    // operator needs and what the dispatch row records.
    public sealed class AiGrantDecision
    {
        public required bool IsAllowed { get; init; }
        public required string Reason { get; init; }
        public AiConsumerGrant? Grant { get; init; }

        public static AiGrantDecision Allow(AiConsumerGrant grant)
            => new() { IsAllowed = true, Reason = $"granted:{grant.EventType}->{grant.ProjectionType}", Grant = grant };

        public static AiGrantDecision Deny(string reason) => new() { IsAllowed = false, Reason = reason };
    }
}
