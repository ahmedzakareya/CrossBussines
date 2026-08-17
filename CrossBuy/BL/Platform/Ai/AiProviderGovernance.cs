using CrossBuy.Models.Platform;

namespace CrossBuy.BL.Platform.Ai
{
    // AI Foundation — Increment 4.2. The PROVIDER DECISION model.
    //
    // This is governance, not integration. Nothing here calls a provider, names a selected provider, or
    // carries a credential. It exists so that when the owner supplies the facts, approval is a recorded
    // decision against a checkable policy rather than someone flipping a flag.
    //
    // THE RULE THE WHOLE FILE ENFORCES: facts alone never approve, and approval alone never approves.
    // Both are required, and either one missing yields a state that DENIES.

    // How a fact is known. The distinction matters: "the code does this" and "someone said so" are not
    // the same quality of evidence, and a contract term is different again.
    public enum AiEvidenceLevel
    {
        // Zero = nothing is known. Never treat as satisfied.
        Unknown = 0,

        // Demonstrable from source in this repository (e.g. HTTPS enforced by AiHopOnePolicy).
        CodeProven,

        // Set in server-owned deployment configuration (e.g. a processing region).
        ConfigProven,

        // A named person has attested it. Weaker than a contract; still a real, auditable answer.
        OwnerAttested,

        // Written into a contract, DPA or enterprise agreement.
        ContractProven,
    }

    // Who can authoritatively answer a fact. Legal conclusions are never assigned to code.
    public enum AiFactOwner
    {
        Unknown = 0,
        Engineering,
        Security,
        Infrastructure,
        DataProtectionLegal,
        Procurement,
        BusinessOwner,
    }

    // ---------------------------------------------------------------------------------------------
    // Increment 4.9 — PROVIDER SCOPE. The environment and account an approval is bound to.
    //
    // WHY THIS EXISTS. Increment 4.8 proved approval was PROVIDER-GLOBAL: the model had no
    // OrganizationId, no ProjectId and no Environment, so "approve this provider for Development" could
    // only be recorded as "approve this provider". Production would have become reachable the moment the
    // record was filled — same code, same switch, same credential resolution — and nothing could have
    // proven the two apart.
    //
    // NO PROVIDER IS NAMED IN THIS FILE, and a test enforces it: a provider name in the governance model
    // reads as a pre-approval nobody signed.
    //
    // A STRONGLY TYPED ENVIRONMENT, NOT A STRING. A free-text environment invites "dev", "Dev", "DEV",
    // "development" and a typo that silently matches nothing — or worse, matches the wrong thing.
    // ---------------------------------------------------------------------------------------------
    public enum AiProviderEnvironment
    {
        // Zero is the refusing value, as everywhere else in this subsystem. An unstated environment is
        // never assumed to be Development: assuming the safer-sounding option is how a production call
        // gets authorised by a development record.
        Unknown = 0,
        Development,
        Production,
    }

    /// <summary>
    /// The account context an approval covers, or that a request is asking for.
    /// </summary>
    /// <remarks>
    /// <para>Identifiers, never credentials. An organisation id and a project id are public-ish names
    /// that identify WHERE a call goes; an API key authorises it. Nothing secret belongs here, and a
    /// test asserts the type carries no key-shaped property.</para>
    /// <para>Comparison is ORDINAL and exact. Case-insensitive or trimmed-fuzzy matching would mean
    /// "proj_dev " and "PROJ_DEV" could satisfy an approval written for neither.</para>
    /// </remarks>
    public sealed record AiProviderScope(
        string ProviderId,
        string? OrganizationId,   // the provider-side account, e.g. an organisation identifier
        string? ProjectId,        // the specific project within it
        AiProviderEnvironment Environment)
    {
        /// Bounded so an identifier cannot become a payload smuggled through the governance record.
        public const int MaxIdentifierLength = 128;

        /// <summary>Every part present, non-blank and within bounds. Incomplete scope can never approve.</summary>
        public bool IsComplete =>
            Ok(ProviderId) && Ok(OrganizationId) && Ok(ProjectId) && Environment != AiProviderEnvironment.Unknown;

        private static bool Ok(string? s)
            => !string.IsNullOrWhiteSpace(s) && s.Trim().Length == s.Length && s.Length <= MaxIdentifierLength;

        /// <summary>
        /// True only when every part matches exactly. There is no wildcard, no inheritance and no
        /// "Production covers Development" — an approval covers the one scope it was written for.
        /// </summary>
        public bool Covers(AiProviderScope requested)
        {
            ArgumentNullException.ThrowIfNull(requested);

            // Incomplete on either side denies. A blank cannot match a blank into an approval.
            if (!IsComplete || !requested.IsComplete) return false;

            return string.Equals(ProviderId, requested.ProviderId, StringComparison.Ordinal)
                && string.Equals(OrganizationId, requested.OrganizationId, StringComparison.Ordinal)
                && string.Equals(ProjectId, requested.ProjectId, StringComparison.Ordinal)
                && Environment == requested.Environment;
        }

        /// Payload-free, safe to log: identifiers only.
        public override string ToString() => $"{ProviderId}/{OrganizationId}/{ProjectId}/{Environment}";
    }

    /// Well-known <see cref="AiProviderEvidence.Fact"/> keys that the evaluator itself looks for.
    ///
    /// Most evidence is documentary — recorded for the auditor, not read by code. These keys are the
    /// exception: the evaluator requires a matching entry at a minimum strength before it will treat the
    /// corresponding provider fact as established.
    public static class AiProviderEvidenceKeys
    {
        /// Evidence that ZERO DATA RETENTION IS GRANTED FOR THE EVALUATED ACCOUNT / PROJECT.
        ///
        /// Not "the provider offers ZDR" — that is a capability of the product and is worth nothing to a
        /// customer who has not been granted it. This key means a named CrossBuy organisation/project has
        /// it enabled. See <see cref="AiProviderEvaluator.ZeroRetentionEvidenceIsSufficient"/>.
        public const string ZeroRetentionGranted = "ZeroRetentionGranted";
    }

    /// One recorded fact about a provider: what it says, who says it, and how strongly.
    public sealed class AiProviderEvidence
    {
        public required string Fact { get; init; }
        public required AiEvidenceLevel Level { get; init; }
        public required AiFactOwner Owner { get; init; }

        // Free text supplied by the owner (a contract clause reference, a console screenshot id).
        // NEVER a credential — this is a governance record, not configuration.
        public string? Reference { get; init; }

        // ---- Increment 4.9: WHICH ACCOUNT CONTEXT THIS EVIDENCE IS ABOUT ----
        //
        // A ZDR confirmation is granted to a NAMED organisation and project. Before this field existed,
        // evidence said only "ZDR granted" — so a grant obtained for the development project would have
        // satisfied a production candidate, which is exactly the confusion the whole increment exists to
        // remove. Null means the evidence is not scope-bound and therefore cannot satisfy a
        // scope-sensitive fact.
        public AiProviderScope? Scope { get; init; }

        public bool IsKnown => Level != AiEvidenceLevel.Unknown;
    }

    // ---------------------------------------------------------------------------------------------
    // The OWNER'S POLICY — what CrossBuy requires of any external processor.
    //
    // Deliberately separate from the candidate: the requirement is the business's, the candidate is the
    // provider's. Comparing them is the decision. Every field defaults to the STRICTEST reading, so an
    // unfilled policy cannot approve anything.
    // ---------------------------------------------------------------------------------------------
    public sealed class AiProviderRequirement
    {
        /// The processing boundary the business will accept. Empty = not yet decided = nothing qualifies.
        /// This code does NOT choose a region; the owner does.
        public IReadOnlyList<string> AcceptableResidencyRegions { get; init; } = Array.Empty<string>();

        /// The longest provider-side prompt/output retention the business will accept.
        /// Null = not yet decided.
        public TimeSpan? MaximumProviderRetention { get; init; }

        /// True when the business requires the provider to retain nothing at all.
        public bool ZeroRetentionRequired { get; init; }

        /// May CrossBuy business data be used to train provider models? Null = UNDECIDED, which blocks.
        /// There is no default: inferring this answer is exactly what must not happen.
        public bool? TrainingOnBusinessDataAllowed { get; init; }

        /// Whether a signed DPA / enterprise agreement is mandatory.
        public bool ContractRequired { get; init; } = true;

        public bool IsComplete =>
            AcceptableResidencyRegions.Count > 0
            && (ZeroRetentionRequired || MaximumProviderRetention.HasValue)
            && TrainingOnBusinessDataAllowed.HasValue;
    }

    // ---------------------------------------------------------------------------------------------
    // The OWNER'S DECISION — deliberately NOT a configuration value.
    //
    // §7: a developer must not be able to write `Approved=true` in appsettings and thereby approve a
    // processor. So approval is modelled as an ARTIFACT with named approvers and dates, supplied to the
    // evaluator by a caller, and the evaluator never reads IConfiguration — asserted by test.
    //
    // Three separate approvals, because in a real organisation these are three different people with
    // three different responsibilities, and collapsing them into one boolean loses that.
    // ---------------------------------------------------------------------------------------------
    public sealed class AiProviderOwnerApproval
    {
        public bool SecurityApproved { get; init; }
        public bool DataProtectionApproved { get; init; }
        public bool BusinessOwnerApproved { get; init; }

        public string? ApprovedBy { get; init; }
        public DateTime? ApprovedAtUtc { get; init; }

        /// <summary>Increment 4.9 — when the approval BEGINS.</summary>
        /// <remarks>
        /// The mirror of the expiry hole closed in Increment 4.2. An approval with no start is valid
        /// retroactively for all time, which makes "we approved this on the 14th" unprovable and lets a
        /// record signed today cover a call made last month. Absent = NOT approved, exactly as an absent
        /// expiry is.
        /// </remarks>
        public DateTime? ValidFromUtc { get; init; }

        /// Approval is never permanent. An absent expiry is treated as NOT approved rather than as
        /// "forever" — the same fail-closed reading retention uses for a null ExpiresAtUtc.
        public DateTime? ExpiresAtUtc { get; init; }

        /// Set when approval has been explicitly withdrawn (incident, contract change, owner decision).
        public bool Revoked { get; init; }
        public string? RevokedReason { get; init; }

        public bool AllApproverssigned => SecurityApproved && DataProtectionApproved && BusinessOwnerApproved;
    }

    /// The provider being assessed. No SDK, no endpoint, no credential — identity and claims only.
    public sealed class AiProviderCandidate
    {
        public required string ProviderId { get; init; }
        public required string ProviderName { get; init; }

        // ---- Increment 4.9: THE ACCOUNT CONTEXT THIS CANDIDATE IS ABOUT ----
        //
        // Not decoration. Before these existed the record described only a provider; it now describes a
        // specific organisation, a specific project and a specific environment. That is what makes
        // "approved for Development" a statement the runtime can check rather than a note in a document.
        //
        // Null / Unknown blocks. There is no default environment, and Development is emphatically not
        // the default — assuming the less-dangerous-sounding value is how a production request gets
        // served by a development approval.
        //
        // No provider is named anywhere in this file, deliberately and by test: a provider name in the
        // governance model reads as a pre-approval nobody signed.
        public string? OrganizationId { get; init; }
        public string? ProjectId { get; init; }
        public AiProviderEnvironment Environment { get; init; } = AiProviderEnvironment.Unknown;

        /// The scope this candidate is approved FOR, assembled from the fields above.
        public AiProviderScope Scope => new(ProviderId, OrganizationId, ProjectId, Environment);

        public string? LegalEntity { get; init; }
        public string? AccountOwner { get; init; }
        public string? CommercialTier { get; init; }

        /// Where the provider states processing occurs. Compared against the owner's accepted regions.
        public string? ResidencyRegion { get; init; }

        /// What the provider retains. Null = unknown = blocks.
        public TimeSpan? ProviderRetention { get; init; }

        // -----------------------------------------------------------------------------------------
        // Increment 4.4 — ZERO DATA RETENTION, GRANTED FOR THIS ACCOUNT / PROJECT.
        //
        // WHY THIS IS A SEPARATE FACT AND NOT `ProviderRetention == 0`. The two answer different
        // questions, and conflating them was the defect this increment closes:
        //
        //     ProviderRetention      = "how long does this provider retain, per its documentation?"
        //     ZeroRetentionGranted   = "has zero retention been GRANTED to a named CrossBuy
        //                               organisation / production project, and can we show it?"
        //
        // At every candidate evaluated so far, ZDR is an opt-in arrangement requiring the provider's
        // prior approval. So a documented duration — even a documented zero — is a statement about the
        // product, not about us. Before this field existed, the only way to express "we have ZDR" was to
        // write `ProviderRetention = 0`, which is indistinguishable from quoting a web page.
        //
        // THREE STATES, ALL MEANINGFUL:
        //     Unknown  — nobody has established it. Blocks: assessment incomplete.
        //     No       — the provider will not or cannot grant it. REJECTED: a known policy mismatch.
        //     Yes      — granted, and it must be backed by evidence at OwnerAttested or better.
        //
        // Only consulted when the owner's policy sets ZeroRetentionRequired. A policy that accepts
        // bounded retention is unaffected by this field.
        // -----------------------------------------------------------------------------------------
        public AiProviderFact ZeroRetentionGranted { get; init; } = AiProviderFact.Unknown;

        /// Whether the provider trains on customer data. Null = unknown = blocks.
        public bool? TrainsOnCustomerData { get; init; }

        public AiProviderFact SubprocessorPositionAccepted { get; init; } = AiProviderFact.Unknown;
        public AiProviderFact EncryptionInTransit { get; init; } = AiProviderFact.Unknown;
        public AiProviderFact DeletionControlsAvailable { get; init; } = AiProviderFact.Unknown;
        public AiProviderFact ContractInPlace { get; init; } = AiProviderFact.Unknown;

        public IReadOnlyList<AiProviderEvidence> Evidence { get; init; } = Array.Empty<AiProviderEvidence>();

        /// Explicit rejection by the owner, independent of the facts.
        public bool ExplicitlyRejected { get; init; }
        public string? RejectionReason { get; init; }
    }

    // §8 — the state machine. Explicit states, never a boolean.
    public enum AiProviderState
    {
        // Nothing is known. The starting point, and the state an empty candidate stays in.
        Unknown = 0,

        // Facts are being gathered — some known, some not.
        UnderAssessment,

        // Everything technical is satisfied; a human decision is outstanding.
        OwnerDecisionRequired,

        // Facts satisfy policy AND all three approvals are recorded and unexpired.
        ApprovedExternalProcessor,

        // The owner said no.
        Rejected,

        // Was approved, but the approval has expired or been revoked. Distinct from Rejected: it records
        // that approval once existed, which matters for an audit of what was sent and when.
        Suspended,
    }

    public sealed class AiProviderAssessment
    {
        public required AiProviderState State { get; init; }
        public required string Reason { get; init; }
        public required IReadOnlyList<string> MissingFacts { get; init; }

        /// Only ApprovedExternalProcessor yields the approved destination class. Every other state maps
        /// to UnapprovedExternal, so a caller that ignores the state still fails closed.
        public AiEgressDestinationClass DestinationClass =>
            State == AiProviderState.ApprovedExternalProcessor
                ? AiEgressDestinationClass.ApprovedExternalProcessor
                : AiEgressDestinationClass.UnapprovedExternal;

        public bool IsApproved => State == AiProviderState.ApprovedExternalProcessor;
    }

    // ---------------------------------------------------------------------------------------------
    // The evaluator. A pure function of (candidate, requirement, approval, now).
    //
    // It reads NO configuration, NO environment and NO request — so there is no surface through which a
    // deployment could approve a provider by editing a file.
    // ---------------------------------------------------------------------------------------------
    public static class AiProviderEvaluator
    {
        public static AiProviderAssessment Evaluate(
            AiProviderCandidate? candidate,
            AiProviderRequirement requirement,
            AiProviderOwnerApproval? approval,
            DateTime nowUtc)
        {
            if (candidate == null || string.IsNullOrWhiteSpace(candidate.ProviderName))
                return State(AiProviderState.Unknown, "No provider candidate has been supplied.", Array.Empty<string>());

            // An explicit "no" is final and is not reconsidered against the facts — the owner has decided.
            if (candidate.ExplicitlyRejected)
                return State(AiProviderState.Rejected,
                    "The owner explicitly rejected this provider" +
                    (string.IsNullOrWhiteSpace(candidate.RejectionReason) ? "." : $": {candidate.RejectionReason}"),
                    Array.Empty<string>());

            // ---- which mandatory facts are still unknown ----
            var missing = MissingFacts(candidate, requirement);

            // ---- facts that are KNOWN but do not satisfy policy ----
            // Checked before the "unknown" branch: a provider that demonstrably breaches policy is a
            // REJECTION, not something waiting on more information.
            var breach = PolicyBreach(candidate, requirement);
            if (breach != null)
                return State(AiProviderState.Rejected, "DOES NOT SATISFY POLICY — " + breach, missing);

            if (missing.Count > 0)
                // Unknown can never jump to Approved — the state machine has no such edge.
                return State(
                    approval is { AllApproverssigned: true } ? AiProviderState.OwnerDecisionRequired : AiProviderState.UnderAssessment,
                    "Mandatory provider facts are still unknown: " + string.Join(", ", missing),
                    missing);

            // ---- every fact satisfies policy; now the human decision ----
            if (approval == null || !approval.AllApproverssigned)
                return State(AiProviderState.OwnerDecisionRequired,
                    "All mandatory facts satisfy policy, but Security, Data Protection and Business Owner " +
                    "approvals are not all recorded.", missing);

            if (approval.Revoked)
                return State(AiProviderState.Suspended,
                    "Approval was revoked" + (string.IsNullOrWhiteSpace(approval.RevokedReason) ? "." : $": {approval.RevokedReason}"),
                    missing);

            // ---- Increment 4.9: THE VALIDITY WINDOW, both ends ----
            //
            // EXPIRY IS CHECKED FIRST, and the order is deliberate rather than incidental. It is the older
            // and the more consequential of the two rules — an approval that never lapses is the one that
            // outlives the contract under it — so when both ends are absent that is the reason worth
            // reporting. Both return the same state, so only the explanation differs.
            //
            // A missing expiry is NOT "forever". An approval nobody has to re-confirm is how a provider
            // stays approved years after the contract it rested on lapsed.
            if (approval.ExpiresAtUtc == null)
                return State(AiProviderState.OwnerDecisionRequired,
                    "Approval carries no review/expiry date. An approval that never expires is never re-examined.",
                    missing);

            // A missing START is the mirror of that, and just as wrong: an approval with no beginning is
            // valid retroactively for all time, so "we approved this on the 14th" becomes unprovable and a
            // record signed today silently covers a call made last month.
            if (approval.ValidFromUtc == null)
                return State(AiProviderState.OwnerDecisionRequired,
                    "Approval carries no start date. An approval with no beginning cannot be audited against " +
                    "the calls it was supposed to authorise.",
                    missing);

            // A window that ends before (or when) it starts authorises nothing and is almost certainly a
            // transcription error. Rejected rather than silently treated as expired, because the record
            // itself is wrong and needs re-issuing, not re-confirming.
            if (approval.ValidFromUtc.Value >= approval.ExpiresAtUtc.Value)
                return State(AiProviderState.Rejected,
                    "Approval validity window is invalid: it does not start before it ends.", missing);

            if (nowUtc < approval.ValidFromUtc.Value)
                return State(AiProviderState.OwnerDecisionRequired,
                    "Approval has been recorded but is not yet in force.", missing);

            if (approval.ExpiresAtUtc.Value <= nowUtc)
                return State(AiProviderState.Suspended,
                    "Approval expired and must be re-confirmed before external processing resumes.", missing);

            return State(AiProviderState.ApprovedExternalProcessor,
                "All mandatory facts satisfy policy and all three approvals are recorded and current.", missing);
        }

        /// The mandatory-fact list, in one place so the report and the code cannot drift.
        public static IReadOnlyList<string> MissingFacts(AiProviderCandidate c, AiProviderRequirement r)
        {
            var missing = new List<string>();

            void Need(bool known, string name) { if (!known) missing.Add(name); }

            // ---- Increment 4.9: the account context comes FIRST ----
            //
            // Before any other fact matters, the record must say WHICH organisation, WHICH project and
            // WHICH environment it describes. A complete set of facts about an unnamed account is not an
            // approval of anything.
            Need(!string.IsNullOrWhiteSpace(c.OrganizationId), "OrganizationId");
            Need(!string.IsNullOrWhiteSpace(c.ProjectId), "ProjectId");
            Need(c.Environment != AiProviderEnvironment.Unknown, "Environment");

            Need(!string.IsNullOrWhiteSpace(c.LegalEntity), "LegalEntity");
            Need(!string.IsNullOrWhiteSpace(c.AccountOwner), "AccountOwner");
            Need(!string.IsNullOrWhiteSpace(c.CommercialTier), "CommercialTier");
            Need(c.TrainsOnCustomerData.HasValue, "TrainsOnCustomerData");
            // A documented duration is only mandatory when the policy accepts one. Under a ZDR policy the
            // duration is redundant — the GRANT is the fact that matters, and it is required below.
            Need(c.ProviderRetention.HasValue || r.ZeroRetentionRequired, "ProviderRetention");

            // ---- Increment 4.4: the ZDR grant, and the evidence behind it ----
            //
            // Only when the owner's policy demands zero retention. Otherwise this field is not a gate at
            // all, and a provider policy built on MaximumProviderRetention is unaffected.
            if (r.ZeroRetentionRequired)
            {
                Need(c.ZeroRetentionGranted != AiProviderFact.Unknown, "ZeroRetentionGranted");

                // A claim of "granted" is not self-supporting. It needs a reference someone can check,
                // recorded by a named owner — see ZeroRetentionEvidenceIsSufficient for why a public
                // documentation link cannot satisfy this.
                if (c.ZeroRetentionGranted == AiProviderFact.Yes)
                    Need(ZeroRetentionEvidenceIsSufficient(c), "ZeroRetentionGrantedEvidence");
            }
            Need(!string.IsNullOrWhiteSpace(c.ResidencyRegion), "ResidencyRegion");
            Need(c.SubprocessorPositionAccepted != AiProviderFact.Unknown, "SubprocessorPositionAccepted");
            Need(c.EncryptionInTransit != AiProviderFact.Unknown, "EncryptionInTransit");
            Need(c.DeletionControlsAvailable != AiProviderFact.Unknown, "DeletionControlsAvailable");
            Need(c.ContractInPlace != AiProviderFact.Unknown, "ContractInPlace");

            // The owner's own policy must be complete too. A candidate cannot be measured against a
            // requirement that has not been decided.
            if (!r.IsComplete) missing.Add("OwnerRequirementPolicy");

            return missing;
        }

        /// <summary>
        /// True when the candidate carries evidence that ZDR is granted FOR THIS ACCOUNT / PROJECT, at a
        /// strength that a public web page cannot reach.
        /// </summary>
        /// <remarks>
        /// Only <see cref="AiEvidenceLevel.OwnerAttested"/> and <see cref="AiEvidenceLevel.ContractProven"/>
        /// qualify, and the two weaker levels are excluded on purpose rather than by an accident of enum
        /// ordering:
        ///
        ///   * <c>CodeProven</c> — this repository cannot demonstrate what another company granted us.
        ///   * <c>ConfigProven</c> — a deployment setting is where a link to the provider's ZDR page would
        ///     land, and "the provider offers ZDR" is precisely the claim that must not pass.
        ///
        /// What does qualify: written provider confirmation, an account-manager statement, a support
        /// ticket reference, console evidence, or a contract term. A named person stands behind each.
        ///
        /// The comparison is written as an explicit set, not <c>&gt;= OwnerAttested</c>, so reordering the
        /// enum later cannot quietly admit a weaker level.
        /// </remarks>
        public static bool ZeroRetentionEvidenceIsSufficient(AiProviderCandidate candidate)
            => candidate.Evidence.Any(e =>
                string.Equals(e.Fact, AiProviderEvidenceKeys.ZeroRetentionGranted, StringComparison.Ordinal)
                && e.Level is AiEvidenceLevel.OwnerAttested or AiEvidenceLevel.ContractProven
                // Increment 4.9 — AND IT MUST BE ABOUT THIS ACCOUNT.
                //
                // ZDR is granted per organisation or project. Without this, a grant obtained for the
                // development project would satisfy a production candidate: the evidence would be real,
                // correctly levelled, and about the wrong thing. Null scope on the evidence denies —
                // unscoped evidence cannot establish a scoped fact.
                && e.Scope is not null
                && e.Scope.Covers(candidate.Scope));

        /// A KNOWN fact that breaches policy. Returns null when nothing known is disqualifying.
        private static string? PolicyBreach(AiProviderCandidate c, AiProviderRequirement r)
        {
            if (c.TrainsOnCustomerData == true && r.TrainingOnBusinessDataAllowed == false)
                return "the provider trains on customer data and the business policy forbids it.";

            if (c.EncryptionInTransit == AiProviderFact.No)
                return "the provider does not encrypt data in transit.";

            if (r.ContractRequired && c.ContractInPlace == AiProviderFact.No)
                return "a DPA / enterprise agreement is required and none is in place.";

            if (!string.IsNullOrWhiteSpace(c.ResidencyRegion)
                && r.AcceptableResidencyRegions.Count > 0
                && !r.AcceptableResidencyRegions.Contains(c.ResidencyRegion, StringComparer.OrdinalIgnoreCase))
                return $"the provider processes in a region the business has not accepted.";

            // Increment 4.4 — an explicit refusal is a REJECTION, not an incomplete assessment. The
            // provider has answered; the answer does not satisfy the policy, and no further evidence
            // changes that. Checked before the retention-duration rule below because it is the more
            // specific and more final of the two.
            if (r.ZeroRetentionRequired && c.ZeroRetentionGranted == AiProviderFact.No)
                return "zero data retention is required and the provider has not granted it for this account.";

            if (r.ZeroRetentionRequired && c.ProviderRetention is { } zr && zr > TimeSpan.Zero)
                return "zero provider-side retention is required and the provider retains data.";

            if (r.MaximumProviderRetention is { } max && c.ProviderRetention is { } actual && actual > max)
                return "provider-side retention exceeds the maximum the business accepts.";

            return null;
        }

        private static AiProviderAssessment State(AiProviderState state, string reason, IReadOnlyList<string> missing)
            => new() { State = state, Reason = reason, MissingFacts = missing };
    }
}
