using Microsoft.Extensions.Configuration;
using CrossBuy.Models.Platform;

namespace CrossBuy.BL.Platform.Ai
{
    // AI Foundation — Increment 4.10. THE DEVELOPMENT READINESS REPORT.
    //
    // WHAT QUESTION THIS ANSWERS, AND THE THREE IT REFUSES TO CONFLATE WITH IT.
    //
    //     CONFIGURED      — this deployment knows WHICH account it would call.
    //     ENABLED         — the operator is WILLING to call it.
    //     APPROVED        — the owner has DECIDED it may be called.
    //     CALL PERMITTED  — all of the above, and every remaining gate, are simultaneously satisfied.
    //
    // Collapsing those into one boolean is the failure this whole programme has been unwinding since
    // BLOCKER-1, where a configuration value alone produced approved-processor authority. A single
    // `bool isReady` here would rebuild that in a new place: it would be true whenever the *easy* half
    // was done, and the easy half is configuration. So this type reports FOUR independent answers and
    // never derives one from another — CallPermitted is an AND over every gate, and it is the only one
    // that means "go".
    //
    // WHY IT IS A PURE REPORT AND NOT A GATE. Nothing consults this class before sending. AiEgressPolicy
    // and OpenAiProviderAdapter each make their own decision from the same underlying sources, and this
    // type merely asks them what they would say. If it disagreed with them, it would be the one that is
    // wrong, and no send would be affected — a report that could authorise something would be a second
    // authority, and there is exactly one.
    //
    // IT READS NO CREDENTIAL VALUE. The API key is reported as present or absent and in no other way:
    // no prefix, no length, no masked form, no hash. `HasApiKey` returns a bool and this type stores a
    // bool; the string never enters a field, a log line or a report row.
    public static class OpenAiDevelopmentReadiness
    {
        /// <summary>One gate's answer, with the reason attached to it rather than to a summary.</summary>
        /// <remarks>
        /// The reason travels WITH the verdict because a readiness report is read by someone who has just
        /// been told "no" and needs to know which of fifteen things to go and do. A list of bools with a
        /// single trailing message loses that.
        /// </remarks>
        public sealed record Gate(string Name, bool Satisfied, string Detail);

        public sealed class Report
        {
            public required AiProviderScope RequestedScope { get; init; }

            // ---- the four answers, deliberately separate ----
            public required bool Configured { get; init; }
            public required bool Enabled { get; init; }
            public required bool Approved { get; init; }
            public required bool CallPermitted { get; init; }

            public required AiProviderState AuthorityState { get; init; }
            public required IReadOnlyList<Gate> Gates { get; init; }

            /// <summary>The FIRST unsatisfied gate — the thing to fix next.</summary>
            public Gate? FirstBlocker => Gates.FirstOrDefault(g => !g.Satisfied);

            /// <summary>Payload-free, credential-free, safe to log and safe to show an operator.</summary>
            public string Describe()
            {
                var lines = Gates.Select(g => $"  [{(g.Satisfied ? "OK " : "NO ")}] {g.Name}: {g.Detail}");
                // The ENVIRONMENT comes from the scope, not from the type's name. Labelling a Production
                // scope "Development readiness" would be the one misreading this whole increment exists
                // to prevent, printed at the top of the report that exists to prevent it.
                return
                    $"[AI] OpenAI {RequestedScope.Environment} readiness for {RequestedScope}\n" +
                    $"  CONFIGURED={Configured}  ENABLED={Enabled}  APPROVED={Approved}  " +
                    $"CALL-PERMITTED={CallPermitted}  (authority state: {AuthorityState})\n" +
                    string.Join('\n', lines) + '\n' +
                    (CallPermitted
                        ? "  => A call is permitted by every gate above."
                        : $"  => BLOCKED: {FirstBlocker!.Name} — {FirstBlocker.Detail}");
            }
        }

        /// <param name="authority">
        /// Injected rather than constructed, so a test can ask "what would this report say if the record
        /// DID approve?" without the report having a way to approve anything itself.
        /// </param>
        public static Report Evaluate(
            IConfiguration config,
            IAiProviderAuthority authority,
            IAiProviderSwitchboard switchboard,
            DateTime nowUtc)
        {
            ArgumentNullException.ThrowIfNull(config);
            ArgumentNullException.ThrowIfNull(authority);
            ArgumentNullException.ThrowIfNull(switchboard);

            var options = OpenAiOptions.FromConfiguration(config);
            var scope = options.Scope;
            var assessment = authority.Assess(nowUtc, scope);

            var hasKey = OpenAiOptions.HasApiKey(config);
            var enabled = switchboard.IsEnabled(OpenAiOptions.ProviderId, scope.Environment);
            var priced = OpenAiOptions.IsPriced(config, options.Model);

            // ---- CONFIGURED: does this deployment know which account it would call? ----
            var configured = scope.IsComplete;

            var gates = new List<Gate>
            {
                new("ProviderScopeComplete", scope.IsComplete,
                    scope.IsComplete
                        ? $"organisation, project and environment are all stated ({scope})"
                        : $"incomplete ({scope}) — an unstated part is never assumed, least of all the environment"),

                new("OrganizationIdConfigured", !string.IsNullOrWhiteSpace(options.OrganizationId),
                    string.IsNullOrWhiteSpace(options.OrganizationId)
                        ? $"'{OpenAiOptions.ConfigRoot}:OrganizationId' is empty"
                        : "set"),

                new("ProjectIdConfigured", !string.IsNullOrWhiteSpace(options.ProjectId),
                    string.IsNullOrWhiteSpace(options.ProjectId)
                        ? $"'{OpenAiOptions.ConfigRoot}:ProjectId' is empty"
                        : "set"),

                new("EnvironmentStated", scope.Environment != AiProviderEnvironment.Unknown,
                    scope.Environment == AiProviderEnvironment.Unknown
                        ? "Unknown — and Unknown is never read as Development"
                        : $"{scope.Environment} (stated explicitly, never derived from ASPNETCORE_ENVIRONMENT)"),

                // PRESENCE ONLY. There is no branch below this that inspects the value.
                new("ApiKeyPresent", hasKey,
                    hasKey ? "present" : "absent or still a placeholder"),

                new("ProviderSwitchEnabled", enabled,
                    enabled
                        ? $"provider-wide AND {scope.Environment} switches are both on"
                        : $"off — availability, not authority ({switchboard.Explain(OpenAiOptions.ProviderId)})"),

                new("ProviderApproved", assessment.IsApproved,
                    assessment.IsApproved
                        ? "the governance record approves this exact scope"
                        : $"{assessment.State} — {assessment.Reason}"),

                // ---- DERIVED FROM THE RECORD, never asserted ----
                //
                // Writing `false` here would be correct today and a LIE the day the record is populated,
                // and a readiness report that goes stale silently is worse than none. Both gates below
                // read the assessment the real evaluator just produced.
                new("ZeroRetentionEvidence", ZdrSatisfied(assessment),
                    ZdrSatisfied(assessment)
                        ? "a scoped zero-retention grant is recorded and sufficient for this scope"
                        : "UNKNOWN — no scoped zero-retention grant is recorded for this organisation and " +
                          "project. Owner Decision 6A requires one before ANY external processing."),

                // The three signatures are reported SEPARATELY because they are three different people
                // with three different responsibilities — but the assessment exposes them only in
                // aggregate, since the evaluator reaches ApprovedExternalProcessor if and only if all
                // three are present, unexpired and in force. Saying so is more honest than inventing a
                // per-signature verdict the record cannot support.
                new("SecurityApproval", assessment.IsApproved, SignatureDetail(assessment)),
                new("LegalDataProtectionApproval", assessment.IsApproved, SignatureDetail(assessment)),
                new("BusinessOwnerApproval", assessment.IsApproved, SignatureDetail(assessment)),

                // A warning-shaped gate rather than an error-shaped one, and it still blocks a call: an
                // unpriced model is UNPRICED, not free, and a spend nobody can attribute is exactly what
                // an approval process exists to prevent.
                new("PricingConfigured", priced,
                    priced ? $"a price is configured for model '{options.Model}'"
                           : $"no usable price for model '{options.Model}' — cost would report UNKNOWN, never zero"),

                new("RateLimitConfigured", true,
                    "a limiter is always in force; an absent or unparseable setting falls back to the " +
                    "code default and is never read as unlimited"),

                // STRUCTURAL, and the wording is careful for a reason. This code can see that the audit
                // path is wired — every send goes through IAiEgressAuditSink — and it CANNOT see whether
                // the AiEgressAudits table exists on whatever catalogue this host is pointed at. Claiming
                // the latter would be asserting a fact about a database nobody here has queried, which is
                // precisely the kind of unverified confidence this report is meant to replace.
                new("DurableAuditReady", true,
                    "the audit path is wired and every send is recorded before it is attempted. NOTE: " +
                    "this does not verify that the AiEgressAudits table exists on the target catalogue — " +
                    "deploy platform_ai_egress_audit.sql there first"),

                new("SyntheticPayloadGuardReady", true,
                    $"AiSyntheticPayload enforces an exact-match allowlist; a payload classified " +
                    $"{nameof(AiDataClassification.SyntheticTestData)} that is not the canonical synthetic " +
                    "payload is refused at the adapter"),
            };

            // ---- APPROVED and CALL PERMITTED are computed, never assumed ----
            //
            // Note what CallPermitted does NOT do: it does not shortcut when Approved is false, and it
            // does not stop at the first blocker. Every gate is evaluated so the report lists everything
            // outstanding — being told one blocker at a time, fifteen times, is not a readiness report.
            var approved = assessment.IsApproved;
            var callPermitted = gates.All(g => g.Satisfied);

            return new Report
            {
                RequestedScope = scope,
                Configured = configured,
                Enabled = enabled,
                Approved = approved,
                CallPermitted = callPermitted,
                AuthorityState = assessment.State,
                Gates = gates,
            };
        }

        /// <summary>Whether the record carries a sufficient, scope-matched zero-retention grant.</summary>
        /// <remarks>
        /// The `State != Unknown` guard is doing real work. `MissingFacts` is EMPTY when there is no
        /// candidate at all, so a bare "the fact is not in the missing list" test would read an empty
        /// record as satisfying the owner's hardest gate — the exact fail-open this programme exists to
        /// remove. No candidate means nothing is established, including this.
        /// </remarks>
        private static bool ZdrSatisfied(AiProviderAssessment assessment)
            => assessment.State != AiProviderState.Unknown
               && !assessment.MissingFacts.Contains(AiProviderEvidenceKeys.ZeroRetentionGranted)
               && !assessment.MissingFacts.Contains(AiProviderEvidenceKeys.ZeroRetentionGranted + "Evidence");

        private static string SignatureDetail(AiProviderAssessment assessment)
            => assessment.IsApproved
                ? "recorded, in force, and covering this scope — all three signatures are required for " +
                  "the approved state and the record is in it"
                : $"not recorded. The governance record reports {assessment.State}, which is reached " +
                  "whenever any of the three is missing, expired, revoked or not yet in force.";
    }
}
