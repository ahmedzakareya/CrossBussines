using CrossBuy.Models.Platform;

namespace CrossBuy.BL.Platform.Ai
{
    // AI Foundation — Increment 3. Retention: how long AI-derived data may exist.
    //
    // WHY THIS EXISTS SEPARATELY FROM REVOCATION. They answer different questions and must not be made
    // to depend on each other:
    //
    //   REVOCATION — "this data is no longer ALLOWED" (the entity was deleted, the grant was disabled,
    //                the shape was retired, the tenant left). It is an EVENT, and it is immediate.
    //   EXPIRY     — "this data has exceeded its approved LIFETIME". It is a CLOCK, and it happens with
    //                nobody doing anything.
    //
    // If expiry were implemented as "revoke on a schedule", data would stay readable until the sweep ran.
    // If revocation were implemented as "expire now", the audit trail would lose the reason. So both are
    // enforced independently, and BOTH are enforced AT READ TIME — a background cleanup is an
    // optimisation for storage, never the thing that makes data unreadable.
    public enum AiRetentionClass
    {
        // Zero is the refused value: an unclassified projection is not eligible for persistence or
        // retrieval, rather than inheriting whichever policy happens to be first.
        Unknown = 0,

        // Operational signal whose usefulness decays quickly. Bounded by days.
        ShortLived,

        // Tied to the lifetime of the business record it describes. Bounded, but generously — the
        // analytic value of "how long do tasks take" spans a business cycle, not a week.
        BusinessRecordBound,

        // Lives until something revokes it. Reserved and DELIBERATELY UNUSED today: it is what an
        // immortal second copy looks like, and nothing in this increment is allowed to be one.
        RevocationBound,
    }

    public sealed class AiRetentionPolicy
    {
        public required string ProjectionType { get; init; }
        public required int ProjectionVersion { get; init; }
        public required AiRetentionClass Class { get; init; }

        // Null only for RevocationBound. Every other class MUST bound its lifetime.
        public required int? RetainDays { get; init; }

        // Why this number. Recorded on the policy itself so a reviewer does not have to reconstruct the
        // reasoning from a commit message.
        public required string Rationale { get; init; }
    }

    public interface IAiRetentionPolicyRegistry
    {
        // Null when the (type, version) pair has no declared policy. Null means NOT ELIGIBLE — the caller
        // must fail closed, never assume a default.
        AiRetentionPolicy? Find(string projectionType, int version);

        // The expiry instant for a projection, or null when no policy applies (⇒ not eligible).
        DateTime? ExpiresAt(string projectionType, int version, DateTime occurredAtUtc);

        IReadOnlyList<AiRetentionPolicy> All { get; }
    }

    public sealed class AiRetentionPolicyRegistry : IAiRetentionPolicyRegistry
    {
        // Every retrievable shape MUST appear here. A test asserts the shape registry and this registry
        // describe exactly the same set, so adding a projection without deciding its retention fails the
        // build rather than creating an immortal copy by omission.
        private static readonly AiRetentionPolicy[] Policies =
        {
            new()
            {
                ProjectionType = AiConsumerGrants.TaskLifecycleProjection,
                ProjectionVersion = AiConsumerGrants.TaskLifecycleVersion,
                Class = AiRetentionClass.BusinessRecordBound,
                RetainDays = 730,
                Rationale =
                    "Task lifecycle facts support throughput, ageing and overdue analytics, which are compared " +
                    "year over year — so one year is too short to answer the question the shape exists for. Two " +
                    "years covers two full comparison cycles and then stops; it is bounded because a task's " +
                    "operational signal has no value once the business has moved two years past it.",
            },
            new()
            {
                ProjectionType = AiConsumerGrants.CalendarSchedulingProjection,
                ProjectionVersion = AiConsumerGrants.CalendarSchedulingVersion,
                Class = AiRetentionClass.ShortLived,
                RetainDays = 400,
                Rationale =
                    "Scheduling load, reschedule and cancellation rates are current-behaviour signals: the useful " +
                    "window is 'this year versus last year', so slightly over one year. Shorter than the task " +
                    "shape ON PURPOSE — calendar data is closer to people's working patterns, and the shortest " +
                    "period that answers the question is the right one to keep.",
            },
        };

        public IReadOnlyList<AiRetentionPolicy> All => Policies;

        public AiRetentionPolicy? Find(string projectionType, int version)
            => string.IsNullOrWhiteSpace(projectionType)
                ? null
                : Array.Find(Policies, p =>
                    string.Equals(p.ProjectionType, projectionType, StringComparison.Ordinal)
                    && p.ProjectionVersion == version);

        public DateTime? ExpiresAt(string projectionType, int version, DateTime occurredAtUtc)
        {
            var policy = Find(projectionType, version);
            if (policy == null) return null;                       // no policy ⇒ not eligible

            // RevocationBound is the only class permitted to have no expiry, and nothing uses it today.
            if (policy.Class == AiRetentionClass.RevocationBound) return null;

            // A class other than RevocationBound with no RetainDays is a malformed policy. Returning null
            // here would read as "never expires" — the exact failure this must not have — so the registry
            // is asserted well-formed by test and this branch cannot be reached silently.
            if (policy.RetainDays is not > 0) return null;

            return occurredAtUtc.AddDays(policy.RetainDays.Value);
        }

        // Exposed for the well-formedness test: no policy may be unbounded unless it is RevocationBound.
        public static bool IsWellFormed(AiRetentionPolicy p)
            => p.Class != AiRetentionClass.Unknown
               && (p.Class == AiRetentionClass.RevocationBound || p.RetainDays is > 0)
               && !string.IsNullOrWhiteSpace(p.Rationale);
    }

    // ---------------------------------------------------------------------------------------------
    // FUTURE RAG / DERIVED-COPY CONTRACT — DOCUMENTED, DELIBERATELY NOT IMPLEMENTED.
    //
    // There is no vector store, no embedding and no chunking in this increment. This type exists so the
    // requirement is written down in code, next to the thing it constrains, rather than in a document
    // that the eventual implementer may not read.
    //
    // THE PROBLEM IT PREVENTS. An embedding is a SECOND COPY of a projection. Revoking the projection
    // does nothing to it. If a chunk is stored with only its text and a vector, there is no way to
    // answer "which chunks came from company 7's task 91?", and the revocation guarantees proven in
    // Increment 2 quietly stop being true the day RAG ships.
    //
    // THE CONTRACT. Every derived copy MUST persist enough lineage to be located by any of the
    // revocation operations that already exist:
    //
    //     CompanyId          — so RevokeCompanyAsync can reach it
    //     EntityType         — so RevokeEntityAsync can reach it
    //     EntityId           — ditto, and never on its own: entity ids repeat across companies
    //     ProjectionType     — so RevokeProjectionTypeAsync can reach it
    //     ProjectionVersion  — so a retired version can be rebuilt without touching current data
    //     SourceProjectionId — the AiProjections row it was derived from
    //     BusinessEventId    — the original fact, so provenance survives a projection rebuild
    //     ExpiresAtUtc       — copied from the source, so a derived copy can NEVER outlive its origin
    //
    // AND: a derived copy must be created inside the same company scope as its source, and must be
    // refused if the source is already revoked or expired. Deriving from data that is no longer
    // retrievable is how a revoked fact comes back.
    // ---------------------------------------------------------------------------------------------
    public static class AiDerivedCopyLineageContract
    {
        public static readonly IReadOnlyList<string> RequiredLineageFields = new[]
        {
            "CompanyId", "EntityType", "EntityId",
            "ProjectionType", "ProjectionVersion",
            "SourceProjectionId", "BusinessEventId", "ExpiresAtUtc",
        };

        // The revocation operations a derived store must be reachable by. Mirrors
        // IAiProjectionRevocationService so the two cannot drift without a test noticing.
        public static readonly IReadOnlyList<string> MustBeReachableBy = new[]
        {
            nameof(IAiProjectionRevocationService.RevokeEntityAsync),
            nameof(IAiProjectionRevocationService.RevokeCompanyAsync),
            nameof(IAiProjectionRevocationService.RevokeProjectionTypeAsync),
        };
    }
}
