namespace CrossBuy.Models.Platform
{
    // AI Foundation — Increment 3. The ONE boundary that decides whether data may leave CrossBuy for
    // AI processing.
    //
    // WHY A TOKEN AND NOT A CONVENTION. Increments 1 and 2 could rely on a single consumer and a single
    // reader honouring their gates. Egress cannot: any service with IAiService injected can post anything
    // to the external processor, and a rule that says "remember to ask the policy first" is one new
    // caller away from being false. So the approval is a TYPE — AiEgressApproval — that only
    // IAiEgressPolicy can mint, and IAiService REQUIRES one. A caller that skips the policy does not fail
    // a review; it fails to compile.

    // WHY the data is leaving. A generic "external AI is enabled" switch is exactly the control this
    // replaces: it answers whether, never why, so it cannot distinguish an approved analysis from an
    // arbitrary export. The vocabulary is deliberately small and derived from features that EXIST today.
    public enum AiEgressPurpose
    {
        // Unset is first so `default` is the refused value rather than an accidental grant.
        Unknown = 0,

        JournalAnomalyDetection,
        CashflowForecast,
        InventoryAnalysis,

        // The /api/ai/diag connectivity probe. Its own category because it forwards CALLER-SUPPLIED TEXT,
        // which is a different risk from a computed business payload and must be governable separately.
        ConnectivityDiagnostic,
    }

    // WHERE it is going. Ownership of the code is not the question — hosting and onward transmission are.
    public enum AiEgressDestinationClass
    {
        Unknown = 0,

        // Runs inside the trust boundary and does not forward anywhere else.
        Internal,

        // A processor that has been explicitly assessed and approved, including what it does with the
        // request afterwards.
        ApprovedExternalProcessor,

        // Anything else, including a processor that relays to a third party without an assessment.
        UnapprovedExternal,
    }

    // WHAT is leaving. Reuses the platform's existing classification words rather than inventing a
    // parallel scale — BusinessEventVisibility already means exactly these things, and two scales would
    // drift the first time one was extended.
    public enum AiDataClassification
    {
        Unknown = 0,

        // Aggregates and structural facts: counts, quantities, dates, ids, statuses.
        OperationalMetadata,

        // Money, balances, positions. Sensitive, but structured and bounded.
        FinancialAggregate,

        // Free text written by a user. The category that can contain anything — a customer name, a
        // person, a complaint, a case reference — and the one a corpus can never un-see.
        FreeTextBusinessContent,

        // Personal data about an identifiable person.
        PersonalData,

        // ---- Increment 4.10 — CONTENT MANUFACTURED BY A TEST, ABOUT NOBODY ----
        //
        // Added LAST so every existing numeric value is unchanged; the audit table stores the NAME, not
        // the number, so nothing already written is reinterpreted either.
        //
        // WHY A CLASSIFICATION AND NOT A FLAG. The audit row records what left, and "a synthetic smoke
        // test" and "a real cashflow aggregate" must not read identically six months later when someone
        // asks what CrossBuy has ever sent to a provider. A boolean beside the classification would be a
        // second scale saying the same kind of thing, which is exactly what this enum's own comment warns
        // against.
        //
        // THIS IS NOT A BYPASS, AND THE DISTINCTION IS THE WHOLE POINT. It says nothing about whether a
        // provider is approved, whether ZDR exists or whether an owner signed anything — an external send
        // of synthetic content is refused by the governance record exactly as any other external send is.
        // It answers only "what was in the payload", never "may it go".
        //
        // AND IT IS NOT SELF-ASSERTED. Labelling a payload synthetic does not make it so: any payload
        // carrying this classification must also PASS AiSyntheticPayload.IsSynthetic, which is enforced at
        // the adapter before a socket is opened. Without that, this member would be a way to relabel real
        // free text and walk it past the matrix — a hole, not a control.
        SyntheticTestData,
    }

    public enum AiEgressDenyReason
    {
        UnknownPurpose,
        UnknownDestination,
        UnknownClassification,
        ClassificationNotPermittedAtDestination,
        PayloadTooLarge,
        CompanyUnresolved,
        CompanyMismatch,
        DestinationNotApproved,
        PolicyDisabled,

        // The destination requires an authenticating shared secret and none is configured — or the
        // configured value is still a deployment placeholder. Sending an empty or placeholder credential
        // is a fail-OPEN: the request leaves the estate and is merely rejected at the far end, by which
        // point the data has already been transmitted.
        DestinationCredentialMissing,

        // Increment 4 — the hop-1 deployment itself is not in a supported, validated shape: an undeclared
        // mode, a remote host over plaintext, a "local" mode pointed at a remote host, or a remote host
        // labelled Internal. These are RELATIONSHIPS between configuration values, so they cannot be
        // caught by validating any one field.
        HopOneDeploymentInvalid,

        // Increment 4.3 (BLOCKER-1) — the request claimed an APPROVED external processor, but the
        // governance record approves nobody. Distinct from DestinationNotApproved, which means the
        // destination declared itself unapproved: this one means the destination CLAIMED approval and
        // the claim was not backed by an owner decision. Keeping them apart matters operationally —
        // the first is a configuration state, the second is an authority failure.
        ProviderNotApproved,

        // Increment 4.9 — an external request that did not state a complete provider scope: no
        // organisation, no project, or no environment. Distinct from ProviderNotApproved, which means a
        // stated scope was checked and is not approved. This one means the question was never asked
        // properly, and it must not be answered by assuming Development.
        ProviderScopeMissing,
    }

    // The request. Carries NO business payload — only its SIZE and its classification. A policy that saw
    // the payload would invite logging it, and the decision does not need it.
    public sealed class AiEgressRequest
    {
        public required AiEgressPurpose Purpose { get; init; }
        public required AiEgressDestinationClass Destination { get; init; }
        public required AiDataClassification Classification { get; init; }

        // The company the DATA belongs to. Validated against the authenticated context — never trusted on
        // its own, and never used as the answer.
        public required int DataCompanyId { get; init; }

        public required int PayloadBytes { get; init; }

        // Optional provenance for projection-derived egress. Absent for the legacy insight payloads,
        // which are computed directly from business tables.
        public string? ProjectionType { get; init; }
        public int? ProjectionVersion { get; init; }

        // ---- Increment 4.9: WHICH EXTERNAL ACCOUNT CONTEXT THIS REQUEST IS FOR ----
        //
        // Required for ApprovedExternalProcessor, meaningless for Internal — a loopback ML call has no
        // organisation or project, and demanding one would couple local ML to a provider decision it does
        // not use. Null on an external request DENIES; it is never defaulted to Development.
        //
        // Typed as object to keep Models.Platform free of a dependency on BL.Platform.Ai. The policy
        // casts it to AiProviderScope; anything else is treated as absent. Not elegant, and the
        // alternative — moving the scope type into Models — would drag the whole governance model with
        // it, because the scope is only meaningful alongside the evaluator that consumes it.
        public object? ProviderScope { get; init; }
    }

    public sealed class AiEgressDecision
    {
        public required bool Allowed { get; init; }
        public AiEgressDenyReason? Reason { get; init; }

        // Which rule decided, for the log line. Never contains payload.
        public required string AppliedPolicy { get; init; }

        // Present only when Allowed. This is the object IAiService demands.
        public AiEgressApproval? Approval { get; init; }
    }

    // The capability token. Its constructor is internal to the assembly and it is only ever constructed
    // by AiEgressPolicy, so possessing one is proof that the policy allowed this exact egress.
    //
    // It carries the decision's identity, not the payload, so it is safe to log in full.
    public sealed class AiEgressApproval
    {
        internal AiEgressApproval(AiEgressPurpose purpose, AiEgressDestinationClass destination,
            AiDataClassification classification, int companyId, int payloadBytes, string appliedPolicy,
            object? providerScope = null)
        {
            Purpose = purpose; Destination = destination; Classification = classification;
            CompanyId = companyId; PayloadBytes = payloadBytes; AppliedPolicy = appliedPolicy;
            ProviderScope = providerScope;
        }

        public AiEgressPurpose Purpose { get; }
        public AiEgressDestinationClass Destination { get; }
        public AiDataClassification Classification { get; }
        public int CompanyId { get; }
        public int PayloadBytes { get; }
        public string AppliedPolicy { get; }

        // Increment 4.9 — the scope this approval was minted FOR. The adapter compares it against the
        // scope it is about to call, so a Development approval cannot be replayed against Production:
        // the token would be authentic and the destination wrong, which is the failure mode a capability
        // token exists to prevent.
        public object? ProviderScope { get; }
    }

    public static class AiEgressLimits
    {
        // The ceiling on a single outbound AI payload.
        //
        // Chosen from the feature, not picked round: the largest legacy payload is the inventory analysis,
        // roughly 200 bytes per item, so 2 MB is ~10,000 items — far beyond any real catalogue this
        // analysis is run over, and far below "the entire company database". Denial rather than silent
        // truncation, because a partial financial or inventory analysis that LOOKS complete is worse than
        // no analysis: the caller cannot tell it was cut.
        public const int MaxPayloadBytes = 2 * 1024 * 1024;

        // The ceiling on a single INBOUND AI response (Increment 4.1).
        //
        // Deliberately NOT the same number as the request limit. The request carries a whole dataset —
        // every posted journal entry, every item — so 2 MB is proportionate there. A response carries
        // FINDINGS about that dataset, which are an order of magnitude smaller:
        //
        //   /anomaly/journal    a scored subset of the entries submitted
        //   /forecast/cashflow  a dated series over the requested horizon (<= 366 points)
        //   /inventory/analyze  one classification row per item submitted
        //   /diag/echo          a short reply plus token counts (bytes, not kilobytes)
        //
        // 512 KB is comfortably above any legitimate response — the largest realistic case, a
        // classification row per item for a ten-thousand-item catalogue, is well under it — while
        // preventing an unbounded body from being buffered into the ERP's memory. Copying the 2 MB
        // request figure would have been convenient and four times looser than the evidence supports.
        public const int MaxResponseBytes = 512 * 1024;
    }
}
