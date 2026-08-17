namespace CrossBuy.BL.Platform.Ai
{
    // AI Foundation — Increment 4.3, extended by 4.9. THE RUNTIME SOURCE OF PROVIDER GOVERNANCE AUTHORITY.
    //
    // WHY THIS TYPE EXISTS (BLOCKER-1). AiProviderEvaluator modelled the entire owner-approval decision —
    // mandatory facts, three separate signatures, expiry, revocation — and nothing in the running program
    // ever called it. The runtime derived its destination class from one settings line:
    //
    //     AiService:DestinationClass = "ApprovedExternalProcessor"
    //
    // and that was the whole approval. Proven, not argued: a temporary test asserted the insecure
    // behaviour against the unmodified tree and passed on all three counts.
    //
    // WHAT INCREMENT 4.9 ADDED. Assess now REQUIRES the caller to state which account context it is
    // asking about. Increment 4.8 proved approval was provider-global: with no organisation, project or
    // environment on the record, "approve OpenAI for Development" could only be recorded as "approve
    // OpenAI", and Production would have become reachable the instant the record was filled.
    //
    // THE SEPARATION THIS ENFORCES.
    //
    //   Configuration describes TECHNICAL FACTS   — endpoint, mode, URL, region, deployment name.
    //   The CALLER states the REQUESTED SCOPE     — which org, which project, which environment.
    //   This type supplies GOVERNANCE AUTHORITY   — whether THAT scope is approved.
    //
    // Configuration may still say WHERE to send. It may not say WHETHER, and it may not say FOR WHICH
    // ENVIRONMENT. Those were the same field once; they are now three separate inputs, and only one of
    // them is reachable from a settings file.
    public interface IAiProviderAuthority
    {
        /// <summary>The governance assessment for a SPECIFIC requested scope.</summary>
        /// <param name="nowUtc">
        /// A parameter rather than a read of the clock, so the validity window is testable and this type
        /// has no ambient dependency of any kind.
        /// </param>
        /// <param name="requested">
        /// Which provider, organisation, project and environment the caller is asking to use. Required:
        /// an unstated scope is refused rather than defaulted. See <see cref="AiProviderScope"/>.
        /// </param>
        AiProviderAssessment Assess(DateTime nowUtc, AiProviderScope requested);
    }

    /// <summary>
    /// The shipped governance record. It is EMPTY, and that is its current correct content.
    /// </summary>
    /// <remarks>
    /// To populate it, ALL of the following must be true and must land in the same reviewed change:
    ///
    ///   1. docs/ai/provider-owner-decision.md §11/§13 is filled in — all eight owner decisions recorded.
    ///   2. All three approvals are recorded with named approvers and dates.
    ///   3. Both ends of the validity window exist. The evaluator refuses an approval missing either.
    ///   4. The candidate's mandatory facts are evidenced, not assumed — INCLUDING its organisation,
    ///      project and environment, and ZDR evidence bound to that same scope.
    ///
    /// The evaluator re-checks every one of those at runtime, so a partially-filled record here does not
    /// approve anything — it produces UnderAssessment or OwnerDecisionRequired, both of which deny.
    ///
    /// A record approved for Development will NOT satisfy a Production request. That is now a property
    /// of the code rather than of a document.
    /// </remarks>
    public sealed class AiProviderAuthority : IAiProviderAuthority
    {
        // ---- THE GOVERNANCE RECORD ----
        //
        // Null candidate, empty requirement, null approval. Evaluate() maps this to Unknown, which maps to
        // UnapprovedExternal, which denies. Nothing outside this file can change these three values.
        private static readonly AiProviderCandidate? RecordedCandidate = null;
        private static readonly AiProviderRequirement RecordedRequirement = new();
        private static readonly AiProviderOwnerApproval? RecordedApproval = null;

        public AiProviderAssessment Assess(DateTime nowUtc, AiProviderScope requested)
        {
            ArgumentNullException.ThrowIfNull(requested);

            // ---- 1. THE REQUEST MUST NAME A COMPLETE SCOPE ----
            //
            // Refused, never defaulted. Reading an unstated environment as Development would be the
            // single most dangerous convenience in this file: every unscoped caller would silently
            // acquire whatever the development record allows.
            if (!requested.IsComplete)
                return Deny($"The requested provider scope is incomplete ({requested}). " +
                            "Organisation, project and environment must all be stated.");

            var assessment = AiProviderEvaluator.Evaluate(
                RecordedCandidate, RecordedRequirement, RecordedApproval, nowUtc);

            // ---- 2. AND THE RECORD MUST COVER THAT EXACT SCOPE ----
            //
            // Checked AFTER the record's own validity, so the reported reason is the most specific true
            // one: "no candidate" is more useful than "scope mismatch" when there is no candidate at all.
            if (!assessment.IsApproved) return assessment;

            var approvedScope = RecordedCandidate!.Scope;
            if (!approvedScope.Covers(requested))
                return Deny($"The recorded approval covers {approvedScope} and does not cover the " +
                            $"requested {requested}. Approval is bound to one scope; there is no wildcard " +
                            "and no inheritance between environments.");

            return assessment;
        }

        /// Denials from this type are ALWAYS UnderAssessment, never Rejected: a scope that is not covered
        /// is not a judgement about the provider, it is an absence of authority for this request.
        private static AiProviderAssessment Deny(string reason) => new()
        {
            State = AiProviderState.UnderAssessment,
            Reason = reason,
            MissingFacts = Array.Empty<string>(),
        };
    }
}
