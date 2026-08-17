using CrossBuy.Models.Platform;

namespace CrossBuy.BL.Platform.Ai
{
    // AI Foundation — Increment 4.5. THE PROVIDER-INDEPENDENT EXTERNAL BOUNDARY.
    //
    // One interface, so a second provider is a second adapter rather than a second architecture. It sits
    // BELOW the governance pipeline and knows nothing about approval: an implementation cannot decide it
    // is approved, because the only way to obtain the AiEgressApproval its method demands is for
    // AiEgressPolicy to have minted one, and that type's constructor is internal to the assembly.
    //
    // That is the whole safety property, and it is a COMPILE-TIME one: an adapter cannot be called without
    // an approval, and an approval cannot be forged. No amount of adapter code can bypass the boundary,
    // because the boundary is in the signature.

    /// The request. Carries a typed, already-minimised payload — never an EF entity, never free text
    /// assembled ad hoc at the call site.
    public sealed class AiProviderRequest
    {
        public required AiEgressApproval Approval { get; init; }

        /// The system instruction. A CONSTANT chosen by CrossBuy, never anything a user typed.
        public required string SystemPrompt { get; init; }

        /// The already-classified, already-minimised payload, serialised by the caller.
        public required string PayloadJson { get; init; }

        /// Ceiling on generated tokens. Cost control, and a bound on what has to be parsed.
        public required int MaxOutputTokens { get; init; }

        /// Correlates this call with the business request across log, audit and provider.
        public required string CorrelationId { get; init; }
    }

    public enum AiProviderOutcome
    {
        // Zero = nothing happened. A default-constructed result must not read as success.
        NotAttempted = 0,
        Success,
        Refused,          // stopped locally before any network activity
        ProviderError,    // the provider answered with an error
        Timeout,
        Cancelled,
        InvalidResponse,  // answered, but the body could not be trusted
    }

    /// Payload-free apart from `Content`, which the caller must treat as untrusted.
    public sealed class AiProviderResult
    {
        public required AiProviderOutcome Outcome { get; init; }

        /// The model's text. UNTRUSTED EXTERNAL INPUT. Null unless Outcome == Success.
        public string? Content { get; init; }

        public AiProviderUsage? Usage { get; init; }

        /// A machine-readable CATEGORY, never a provider message — those can quote the request back.
        public required string Reason { get; init; }

        public TimeSpan Duration { get; init; }
        public bool Succeeded => Outcome == AiProviderOutcome.Success;

        public static AiProviderResult Refuse(string reason)
            => new() { Outcome = AiProviderOutcome.Refused, Reason = reason };
    }

    public interface IAiExternalProvider
    {
        /// Stable identifier used by the switchboard, the rate limiter, the usage guard and the audit.
        string ProviderId { get; }

        Task<AiProviderResult> SendAsync(AiProviderRequest request, CancellationToken ct = default);
    }

    // ---------------------------------------------------------------------------------------------
    // AUDIT — Increment 4.5, Phase 9.
    //
    // The existing AI audit is ILogger only: correct in content (payload-free) and ephemeral in nature.
    // Before any paid production use there must be a durable record of what was sent where, and by whom.
    //
    // The INTERFACE lands now; the durable implementation does not. See the report and
    // deploy/sql/platform_ai_egress_audit.sql — the table is designed and scripted but NOT APPLIED, and
    // no DbSet is registered, because applying schema was explicitly out of scope for this phase.
    // ---------------------------------------------------------------------------------------------
    public sealed class AiEgressAuditRecord
    {
        public required int CompanyId { get; init; }
        public required string ProviderId { get; init; }
        public required AiEgressPurpose Feature { get; init; }
        public required AiDataClassification Classification { get; init; }
        public required AiEgressDestinationClass Destination { get; init; }

        /// The governance verdict — allowed, or the machine-readable deny reason.
        public required string GovernanceDecision { get; init; }

        /// The approval's own AppliedPolicy string. Identity of the decision, never the payload.
        public string? ApprovalReference { get; init; }

        public string? Model { get; init; }
        public required string CorrelationId { get; init; }
        public required DateTime OccurredAtUtc { get; init; }
        public TimeSpan Duration { get; init; }
        public required AiProviderOutcome Outcome { get; init; }
        public int RequestBytes { get; init; }
        public int InputTokens { get; init; }
        public int OutputTokens { get; init; }
        public decimal? EstimatedCost { get; init; }
        public string? Currency { get; init; }

        /// A CATEGORY such as "provider-error:429". Never a provider message, never a prompt.
        public string? FailureCategory { get; init; }
    }

    public interface IAiEgressAuditSink
    {
        Task RecordAsync(AiEgressAuditRecord record, CancellationToken ct = default);
    }
}
