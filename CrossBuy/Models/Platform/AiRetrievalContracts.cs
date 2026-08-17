using System.Text.Json.Serialization;

namespace CrossBuy.Models.Platform
{
    // AI Foundation — Increment 2. The READ-side contracts.
    //
    // Increment 1 answered "may this EVENT enter the AI subsystem?". These contracts serve a different
    // question: "may THIS USER retrieve THIS PROJECTION NOW?" — and the answer is recomputed on every
    // read. A projection having been created in the past authorises nobody in the future.
    //
    // WHAT IS DELIBERATELY ABSENT FROM THE REQUEST
    //
    //   * CompanyId. Not "validated" — ABSENT. There is no field for a caller to populate, so there is
    //     nothing to spoof, nothing to forget to validate, and no coercion path. The company comes from
    //     the authenticated BusinessContext and only from there.
    //   * Any predicate, expression, SQL fragment, JSON path or table name. The reader exposes methods,
    //     never IQueryable or DbSet, so a caller cannot describe its own query.
    //   * Any "include revoked" or "as system" escape hatch.

    public sealed class AiRetrievalRequest
    {
        // The projection SHAPE. Required, and matched against a closed registry of supported
        // (type, version) pairs — an unknown shape is refused rather than returning nothing, so a typo
        // is a visible error and not a silently empty corpus.
        public required string ProjectionType { get; init; }

        // Optional narrowing to one entity kind, and optionally to specific records. Both are FILTERS,
        // never authorization: every returned row is authorized individually regardless of what was
        // asked for.
        public string? EntityType { get; init; }
        public IReadOnlyCollection<int>? EntityIds { get; init; }

        // Optional occurred-at window. Bounded by AiRetrievalLimits.
        public DateTime? FromUtc { get; init; }
        public DateTime? ToUtc { get; init; }

        // Requested page size. Clamped — never trusted. See AiRetrievalLimits.Clamp.
        public int? Limit { get; init; }
    }

    // Central, testable limits. "Give me the entire company corpus" is the request this exists to
    // refuse, and the numbers are small on purpose: this is an authorization boundary, not a reporting
    // engine, and every returned row costs an entity resolution plus a permission evaluation.
    public static class AiRetrievalLimits
    {
        public const int DefaultLimit = 50;
        public const int MaxLimit = 200;

        // The widest occurred-at window a single call may span. A caller wanting more must page by time
        // deliberately rather than by omitting the parameter.
        public const int MaxWindowDays = 366;

        // A stored payload larger than this is treated as corrupt and refused. Business events cap
        // payloads at 64 KB on write and a minimized projection is far smaller, so anything approaching
        // this ceiling did not come from a builder.
        public const int MaxPayloadBytes = 16 * 1024;

        // The number of CANDIDATE rows the reader will consider before authorization. Bounded so a
        // company whose corpus is mostly unauthorized cannot turn one call into an unbounded scan.
        public const int MaxCandidateScan = 1000;

        public static int Clamp(int? requested)
            => requested is null or <= 0 ? DefaultLimit : Math.Min(requested.Value, MaxLimit);
    }

    // ---------------------------------------------------------------------------------------------
    // The SAFE result. Note what is NOT here: no CompanyId, no BranchId, no BusinessEventId, no
    // EventUid, no actor, no consumer name, no raw JSON.
    //
    // Those are AUTHORIZATION METADATA and PROVENANCE. They are essential to deciding whether a caller
    // may have the row, and they belong in the audit record — but handing them onward would let a future
    // model layer treat them as content, and an internal event id or actor id in a corpus is exactly the
    // kind of incidental identifier that leaks. The caller already knows which company it is.
    // ---------------------------------------------------------------------------------------------
    public sealed class AiRetrievedProjection
    {
        [JsonPropertyName("projectionId")] public required long ProjectionId { get; init; }
        [JsonPropertyName("projectionType")] public required string ProjectionType { get; init; }
        [JsonPropertyName("projectionVersion")] public required int ProjectionVersion { get; init; }

        [JsonPropertyName("entityType")] public required string EntityType { get; init; }
        [JsonPropertyName("entityId")] public required int EntityId { get; init; }

        [JsonPropertyName("occurredAt")] public required DateTime OccurredAt { get; init; }

        // The approved fields, already parsed and re-selected against the shape's declared field list.
        // NOT the stored string: a caller never receives raw JSON from this contract.
        [JsonPropertyName("payload")] public required IReadOnlyDictionary<string, object?> ApprovedPayload { get; init; }
    }

    // Why a row was not returned. Counted rather than itemised: telling a caller "row 4192 exists but
    // you may not see it" discloses the row's existence, which is the thing being withheld.
    public enum AiRetrievalDenialReason
    {
        EntityNoLongerResolvable,
        PermissionDenied,
        Revoked,

        // Increment 3 — the CLOCK, distinct from Revoked (the EVENT). Counted separately so an operator
        // can tell "this data was withdrawn" from "this data aged out", which are different questions.
        Expired,

        // Retention metadata missing or unrecognised. Fails closed: never read as "keeps forever".
        RetentionUnknown,

        GrantDisabled,
        UnsupportedVersion,
        MalformedPayload,
        ClassificationTooHigh,
    }

    public sealed class AiRetrievalResult
    {
        public required IReadOnlyList<AiRetrievedProjection> Items { get; init; }

        // Audit counters (§21). Enough to answer "what was withheld and why" without naming rows.
        public required int Considered { get; init; }
        public required int Returned { get; init; }
        public required IReadOnlyDictionary<AiRetrievalDenialReason, int> Denied { get; init; }

        // True when the limit truncated the result, so a caller can tell "no more data" from
        // "more data, ask again" instead of guessing from a full page.
        public required bool Truncated { get; init; }

        public int DeniedTotal => Denied.Values.Sum();
    }

    // Raised when the REQUEST itself is unacceptable — an unknown shape, an unsupported version, an
    // over-wide window. Distinct from an empty result: "that shape does not exist" and "you may see
    // nothing of that shape" are different answers and a caller must be able to tell them apart.
    public sealed class AiRetrievalRequestException : Exception
    {
        public AiRetrievalRequestException(string message) : base(message) { }
    }

    // Raised when the CALLING CONTEXT may not retrieve at all — as opposed to a per-row denial, which is
    // counted rather than thrown.
    //
    // Deliberately NOT PlatformAccessDeniedException: that type requires an entity type, an entity id and
    // an action, because it describes being refused a specific record. "A system context may not retrieve
    // on a user's behalf" is about the caller, not about any record, and passing a fabricated entity id
    // to satisfy the constructor would put a lie in the audit trail.
    public sealed class AiRetrievalNotPermittedException : Exception
    {
        public AiRetrievalNotPermittedException(string message) : base(message) { }
    }
}
