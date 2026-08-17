namespace CrossBuy.Models.Context.Platform
{
    // AI Foundation — Increment 1. The persisted projection row.
    //
    // This is the ONLY table the future AI/RAG layer reads; it never touches a business table. It lives
    // beside BusinessEvent because it is an entity of the same kernel — the CONTRACTS it is built from
    // (AiProjectionEnvelope, AiConsumerGrant) live in CrossBuy.Models.Platform with the other kernel
    // contracts, which is the separation this project already uses.
    //
    // Every column below exists to answer one of two questions: "what may AI see?" and "why does this
    // data exist in the AI subsystem?".
    public class AiProjection
    {
        public long Id { get; set; }

        // ---- provenance: the exact business fact this came from ----
        public long BusinessEventId { get; set; }
        public Guid EventUid { get; set; }

        // Which subsystem produced it. Present so a second consumer could never be mistaken for AI, and
        // so a revocation sweep can target one subsystem.
        public string Consumer { get; set; } = "";

        // ---- tenancy: copied from the EVENT's context, never from a caller and never defaulted ----
        public int CompanyID { get; set; }
        public int? BranchID { get; set; }

        public string EntityType { get; set; } = "";
        public int EntityId { get; set; }
        public string EventType { get; set; } = "";

        // Who caused the fact. NULL means a system/worker origin — a real answer, not a gap.
        public int? ActorEmployeeId { get; set; }

        // The projection SHAPE, not the event type: several event types may share one shape, and a shape
        // may be re-versioned without renaming the event.
        public string ProjectionType { get; set; } = "";
        public int ProjectionVersion { get; set; } = 1;

        // Minimized JSON, built field-by-field by a registered builder — never a serialized EF entity.
        public string PayloadJson { get; set; } = "";

        // Increment 2 — the SOURCE EVENT's classification, copied at projection time.
        //
        // The write side already refuses anything above a grant's ceiling, so today every row is
        // Internal. It is stored anyway because the READ side must enforce classification per row: a
        // user who may View an Internal entity must not receive a Confidential projection about it, and
        // that decision cannot be made from the grant table (which describes what MAY be ingested, not
        // what a given row IS).
        public string Visibility { get; set; } = "";

        // When the business fact happened, and when we projected it. Both, because a rebuild changes the
        // second and must not change the first.
        public DateTime OccurredAt { get; set; }
        public DateTime ProjectedAt { get; set; }

        // ---- Increment 2 — revocation TOMBSTONE ----
        //
        // Set means: this projection is no longer retrievable, and PayloadJson has been emptied. The row
        // itself survives so audit can still answer "this data existed and was revoked, by whom, when
        // and why" — a question a hard delete destroys. Retrievability ends on RevokedAt alone, so it
        // does not depend on the payload having been successfully cleared.
        public DateTime? RevokedAt { get; set; }
        public string? RevokedBy { get; set; }
        public string? RevocationReason { get; set; }

        public bool IsRevoked => RevokedAt.HasValue;

        // ---- Increment 3 — RETENTION ----
        //
        // The class this row was written under, and the instant it stops being retrievable. Both are
        // NULLABLE, and NULL means UNKNOWN — which the reader treats as NOT ELIGIBLE, never as "keeps
        // forever". A legacy row written before retention existed therefore fails closed rather than
        // being served under a lifetime nobody approved.
        //
        // ExpiresAtUtc is STORED, not recomputed at read time: if a shape's retention policy is later
        // shortened or lengthened, an existing row must keep the lifetime it was persisted under.
        // Recomputing would silently extend data that was written under a shorter promise.
        public string? RetentionClass { get; set; }
        public DateTime? ExpiresAtUtc { get; set; }
    }
}
