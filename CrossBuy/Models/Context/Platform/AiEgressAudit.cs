namespace CrossBuy.Models.Context.Platform
{
    // AI Foundation — Increment 4.6. The persisted external-AI egress audit row.
    //
    // Sits beside AiProjection because it belongs to the same kernel, and follows the same rule as
    // that entity: every column exists to answer a question someone will actually ask.
    //
    // WHAT IT ANSWERS
    //   "Which tenant sent what kind of data, to which provider, when, and what did it cost?"
    //   "Which attempts were refused, and by which control?"
    //
    // WHAT IT CANNOT ANSWER, BY DESIGN
    //   "What did we actually say to the model?" — there is no column for it and there never will be.
    //   The classification matrix exists to control what leaves the estate; storing the payload here
    //   would recreate, inside CrossBuy, the retention the owner forbade at the provider.
    //
    // APPEND-ONLY. Nothing updates a row after insert.
    public class AiEgressAudit
    {
        public long Id { get; set; }

        // ---- tenancy: copied from the APPROVAL, which the policy verified against the authenticated
        // context. Never from a caller, never defaulted. The database rejects 0 as well. ----
        public int CompanyID { get; set; }

        public string ProviderId { get; set; } = "";

        // The AiEgressPurpose. "Feature" rather than "Purpose" because that is the word the rest of the
        // AI subsystem already uses for the same idea (AiRateLimitKey.Feature, AiUsageKey.Feature).
        // Storing it twice under two names would invite the two from drifting.
        public string Feature { get; set; } = "";

        // NULL when the attempt was refused before a model was ever chosen.
        public string? Model { get; set; }

        public string Classification { get; set; } = "";
        public string DestinationClass { get; set; } = "";

        // The governance verdict, and the identity of the decision — AiEgressApproval.AppliedPolicy on
        // an allowed call, or the deny code on a refused one. An identity and a machine code; never a
        // payload, and safe to read in full.
        public string GovernanceDecision { get; set; } = "";
        public string? ApprovalReference { get; set; }

        public string CorrelationId { get; set; } = "";

        public DateTime OccurredAtUtc { get; set; }
        public int DurationMs { get; set; }

        // Both, deliberately: Success is the cheap filter ("how many failed today" must not parse a
        // string), Outcome is the detail ("how did it fail" must not be lost to a bit).
        public bool Success { get; set; }
        public string Outcome { get; set; } = "";

        // A CATEGORY such as "provider-error:429" or "rate:exceeded:30/60s". NEVER a provider message:
        // an error body frequently quotes the offending request back.
        public string? FailureCategory { get; set; }

        public int InputTokens { get; set; }
        public int OutputTokens { get; set; }

        // Computed and persisted in the database; never assigned from code. A total that can disagree
        // with its own parts is a reporting bug waiting to happen.
        public int TotalTokens { get; private set; }

        public int RequestBytes { get; set; }

        // NULL means UNPRICED, never free. A model with no configured price must not accumulate as
        // 0.000000 beside real token counts — that reads as "this cost nothing", which is a different
        // and wrong claim. Currency travels with the amount or neither is stored.
        public decimal? EstimatedCost { get; set; }
        public string? CostCurrency { get; set; }

        public DateTime CreatedAtUtc { get; set; }
    }
}
