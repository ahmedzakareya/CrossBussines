using CrossBuy.Models.Platform;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace CrossBuy.BL.Platform.Ai
{
    // AI Foundation — Increment 3. The ONE canonical AI egress boundary.
    //
    // Every decision is a pure function of the request plus server-owned configuration. Nothing here
    // reads a header, a query string or a body: the caller states WHAT it wants to send and WHY, and the
    // policy decides. The company on the request is checked against the authenticated context and a
    // mismatch is REFUSED — never coerced, which is the same rule IRequestCompanyResolver applies.
    public interface IAiEgressPolicy
    {
        // The company MUST come from the trusted context, not from the request. It is passed separately
        // for exactly that reason: the two values are compared, and disagreement denies.
        Task<AiEgressDecision> EvaluateAsync(
            AiEgressRequest request, CancellationToken cancellationToken = default);
    }

    public sealed class AiEgressPolicy : IAiEgressPolicy
    {
        private readonly IBusinessContextAccessor _context;
        private readonly IConfiguration _config;
        private readonly ILogger<AiEgressPolicy> _log;
        private readonly IAiProviderAuthority _providerAuthority;

        // The authority is REQUIRED, with no default. A defaulted parameter would have let a future call
        // site silently construct a policy with no governance at all — the same shape of gap BLOCKER-1
        // was. Making it explicit costs a few test constructions and buys a compile error instead.
        public AiEgressPolicy(
            IBusinessContextAccessor context, IConfiguration config, ILogger<AiEgressPolicy> log,
            IAiProviderAuthority providerAuthority)
        {
            _context = context;
            _config = config;
            _log = log;
            _providerAuthority = providerAuthority ?? throw new ArgumentNullException(nameof(providerAuthority));
        }

        // ------------------------------------------------------------------------------------------
        // THE MATRIX. Classification × Destination, with purpose gating on top.
        //
        // Read it as: "what may this class of data be sent to a destination of this kind?"
        //
        //                                  Internal   ApprovedExternal   UnapprovedExternal
        //   OperationalMetadata               yes            yes                 NO
        //   FinancialAggregate                yes            yes                 NO
        //   FreeTextBusinessContent           yes            NO                  NO
        //   PersonalData                      NO             NO                  NO
        //   SyntheticTestData                 yes            yes                 NO
        //
        // FreeTextBusinessContent is refused at an EXTERNAL processor even an approved one. That is the
        // deliberate consequence of the JournalEntry.Description finding: free text can contain a
        // customer name, a person, or a case reference, and an approved processor is approved for a
        // PURPOSE — it is not a blanket licence to receive unbounded prose. Sending it requires an
        // explicit, separately-assessed decision, not a policy that quietly permits it.
        //
        // PersonalData is refused everywhere, including Internal, because no current AI purpose needs it.
        // A future purpose that does must be added here deliberately, which is the point.
        //
        // SyntheticTestData (Increment 4.10) sits in the same column as an aggregate, and adding a row to
        // this matrix deserves an explicit justification rather than a shrug:
        //
        //   * It widens WHAT MAY BE DESCRIBED, not WHO MAY RECEIVE IT. Reaching the external column still
        //     requires a complete provider scope and an approved governance record — neither of which
        //     exists — so this row changes nothing about today's answer, which is DENY.
        //   * The label is not self-asserted. A payload carrying it must also pass
        //     AiSyntheticPayload.IsSynthetic at the adapter, an exact-match allowlist over a fixed shape.
        //     Relabelling real free text as synthetic therefore fails at the content check, which is the
        //     defence that makes the row safe rather than the row itself.
        //   * The alternative was to send synthetic content as FinancialAggregate. That would have worked
        //     and would have left the audit trail unable to distinguish a connectivity check from a real
        //     financial send — a worse outcome for the only question this data will ever be asked.
        // ------------------------------------------------------------------------------------------
        private static bool Permitted(AiDataClassification classification, AiEgressDestinationClass destination)
            => destination switch
            {
                AiEgressDestinationClass.Internal => classification is
                    AiDataClassification.OperationalMetadata or
                    AiDataClassification.FinancialAggregate or
                    AiDataClassification.FreeTextBusinessContent or
                    AiDataClassification.SyntheticTestData,

                AiEgressDestinationClass.ApprovedExternalProcessor => classification is
                    AiDataClassification.OperationalMetadata or
                    AiDataClassification.FinancialAggregate or
                    AiDataClassification.SyntheticTestData,

                // UnapprovedExternal and Unknown: nothing, ever.
                _ => false,
            };

        public async Task<AiEgressDecision> EvaluateAsync(
            AiEgressRequest request, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);

            // ---- unknowns fail closed, before anything else ----
            // Each enum's zero value is Unknown, so a default-constructed or partially-populated request
            // is refused rather than inheriting the first real member's meaning.
            if (request.Purpose == AiEgressPurpose.Unknown)
                return Deny(request, AiEgressDenyReason.UnknownPurpose, "purpose:unknown");

            if (request.Destination == AiEgressDestinationClass.Unknown)
                return Deny(request, AiEgressDenyReason.UnknownDestination, "destination:unknown");

            if (request.Classification == AiDataClassification.Unknown)
                return Deny(request, AiEgressDenyReason.UnknownClassification, "classification:unknown");

            // ---- the destination must be one this deployment has approved ----
            if (request.Destination == AiEgressDestinationClass.UnapprovedExternal)
                return Deny(request, AiEgressDenyReason.DestinationNotApproved, "destination:unapproved");

            // ---- INCREMENT 4.3: the claim of an APPROVED external processor is re-derived here ----
            //
            // The destination class arrives ON THE REQUEST, which means the caller chose it. Hardening
            // AiDestinationResolver alone would therefore have secured only the callers that use it —
            // and the boundary's whole purpose is that it does not depend on its callers behaving.
            //
            // So the one canonical boundary asks the governance record itself. Configuration cannot
            // answer this question; IAiProviderAuthority reads no configuration, no request and no
            // environment, and the shipped record is empty.
            //
            // Internal is deliberately NOT gated. Loopback ML never involved a provider, and making a
            // working internal capability depend on an external approval nobody has yet made would take
            // it offline to govern something it does not use.
            if (request.Destination == AiEgressDestinationClass.ApprovedExternalProcessor)
            {
                // Increment 4.9 — the request must NAME the account context it wants. An external call
                // with no organisation, project or environment is refused here rather than defaulted:
                // reading an unstated environment as Development would let every unscoped caller inherit
                // whatever a development record allows.
                if (request.ProviderScope is not AiProviderScope scope || !scope.IsComplete)
                {
                    _log.LogWarning(
                        "AI egress refused: an external processor was requested without a complete provider scope. " +
                        "purpose={Purpose} classification={Classification}",
                        request.Purpose, request.Classification);
                    return Deny(request, AiEgressDenyReason.ProviderScopeMissing, "provider-scope:missing");
                }

                var assessment = _providerAuthority.Assess(DateTime.UtcNow, scope);
                if (!assessment.IsApproved)
                {
                    // The STATE is logged, not the reason string: states are a closed enum safe to emit,
                    // whereas the reason can name a candidate that has not been publicly announced.
                    _log.LogWarning(
                        "AI egress refused: an approved external processor was claimed but none is approved. state={State}",
                        assessment.State);
                    return Deny(request, AiEgressDenyReason.ProviderNotApproved, $"provider:{assessment.State}");
                }
            }

            // ---- size, before any authentication work: a refusal here costs nothing ----
            if (request.PayloadBytes < 0 || request.PayloadBytes > AiEgressLimits.MaxPayloadBytes)
                return Deny(request, AiEgressDenyReason.PayloadTooLarge,
                    $"size:{request.PayloadBytes}>{AiEgressLimits.MaxPayloadBytes}");

            // ---- the classification/destination matrix ----
            if (!Permitted(request.Classification, request.Destination))
                return Deny(request, AiEgressDenyReason.ClassificationNotPermittedAtDestination,
                    $"matrix:{request.Classification}@{request.Destination}");

            // ---- tenancy: the authenticated context is the authority ----
            var context = await _context.TryGetCurrentAsync(cancellationToken);
            if (context == null || context.CompanyId <= 0)
                return Deny(request, AiEgressDenyReason.CompanyUnresolved, "company:unresolved");

            // A request naming a company other than the caller's is REFUSED, not corrected. This is the
            // structural defence against a client-supplied company id reaching an outbound call: even if
            // a controller were to pass one through, the policy compares it against the resolved context
            // and denies. Coercing it would hide the attempt.
            if (request.DataCompanyId != context.CompanyId)
                return Deny(request, AiEgressDenyReason.CompanyMismatch,
                    $"company:{request.DataCompanyId}!={context.CompanyId}");

            // ---- the destination's credential must actually exist ----
            //
            // Checked BEFORE the structural hop-1 validation, and deliberately so: hop-1 validation also
            // requires a secret, but "your credential is missing" is a more actionable message than "your
            // deployment shape is invalid". Ordering the specific rule first means the operator is sent to
            // the right place; the hop-1 check keeps its own secret rule as a backstop for any future
            // caller that reaches it another way.
            //
            // FAIL CLOSED ON A MISSING SECRET. The HTTP client used to send `cfg["AiService:Secret"] ?? ""`,
            // so a host with no secret configured still TRANSMITTED the payload and was merely rejected at
            // the far end — the data had already left the estate by then. A missing credential now stops
            // the request here, before the network.
            //
            // A deployment PLACEHOLDER counts as missing. `__SET_ON_SERVER__…` is what the tracked
            // production template ships, and treating it as a real secret is precisely how an
            // unconfigured production host would start talking to an AI endpoint.
            if (!IsUsableSecret(_config["AiService:Secret"]))
                return Deny(request, AiEgressDenyReason.DestinationCredentialMissing, "credential:missing");

            // ---- HOP 1 must be in a supported, validated deployment shape ----
            //
            // These are RELATIONSHIPS between configuration values — an undeclared mode, a "local"
            // deployment pointed at a remote host, a remote host over plaintext, a remote host labelled
            // Internal — so no per-field check can catch them. Each is a plausible configuration typo.
            var hopOne = AiHopOnePolicy.Validate(_config);
            if (!hopOne.Ok)
            {
                _log.LogError(
                    "AI egress refused: hop-1 deployment is not valid. mode={Mode} reason={Reason}",
                    hopOne.Mode, hopOne.Reason);
                return Deny(request, AiEgressDenyReason.HopOneDeploymentInvalid, $"hop1:{hopOne.Mode}:invalid");
            }

            // ---- master switch, server-owned ----
            // Read from IConfiguration (appsettings / environment / user-secrets), never from anything a
            // caller can influence. Absent means ENABLED, so this cannot silently disable a working
            // deployment; setting it to false is a deliberate act.
            if (_config.GetValue("AiService:EgressEnabled", true) == false)
                return Deny(request, AiEgressDenyReason.PolicyDisabled, "policy:disabled");

            string applied = $"allow:{request.Purpose}@{request.Destination}:{request.Classification}";

            // Safe by construction: purpose, destination, classification, company, size, decision. No
            // payload, no free text, no account or customer detail.
            _log.LogInformation(
                "AI egress ALLOW purpose={Purpose} destination={Destination} classification={Classification} " +
                "company={Company} employee={Employee} bytes={Bytes} shape={Shape} v{Version} correlation={Correlation}",
                request.Purpose, request.Destination, request.Classification,
                context.CompanyId, context.EmployeeId, request.PayloadBytes,
                request.ProjectionType, request.ProjectionVersion, context.CorrelationId);

            return new AiEgressDecision
            {
                Allowed = true,
                AppliedPolicy = applied,
                // Increment 4.9 — the approval carries the scope it was minted for, so the adapter can
                // refuse a token that is authentic but aimed at the wrong project or environment.
                Approval = new AiEgressApproval(
                    request.Purpose, request.Destination, request.Classification,
                    context.CompanyId, request.PayloadBytes, applied, request.ProviderScope),
            };
        }

        // The deployment placeholder the tracked production template ships with. Recognised so an
        // unconfigured host fails closed instead of authenticating with the template's own text.
        public const string SecretPlaceholderMarker = "__SET_ON_SERVER__";

        /// True only for a secret that could actually authenticate. Public so a guard test can assert the
        /// rule directly rather than inferring it from a denial.
        public static bool IsUsableSecret(string? secret)
            => !string.IsNullOrWhiteSpace(secret)
               && !secret.Contains(SecretPlaceholderMarker, StringComparison.OrdinalIgnoreCase);

        private AiEgressDecision Deny(AiEgressRequest request, AiEgressDenyReason reason, string applied)
        {
            // Denials are logged at Warning: an outbound AI call that was refused is something an
            // operator should be able to see without turning on debug logging. Still no payload.
            _log.LogWarning(
                "AI egress DENY reason={Reason} purpose={Purpose} destination={Destination} " +
                "classification={Classification} bytes={Bytes} policy={Policy}",
                reason, request.Purpose, request.Destination, request.Classification,
                request.PayloadBytes, applied);

            return new AiEgressDecision { Allowed = false, Reason = reason, AppliedPolicy = applied };
        }
    }

    // Where the configured AI destination sits on the trust scale.
    //
    // THIS IS A DELIBERATELY CONSERVATIVE READING. The service is CrossBuy's own Python process, which
    // invites calling it "Internal" — but the code's own contracts describe the path as
    // ".NET -> Python -> Claude" (IAiService.EchoAsync, AiController.Diag), i.e. it RELAYS to a third-party
    // LLM. A destination that forwards to someone else is not internal, whoever wrote it.
    //
    // So the class is derived from configuration and from that relay evidence, and the default is the
    // refusing one. Approving it is an owner decision backed by an assessment of what the relayed-to
    // provider does with the request — not something this code may assume.
    public static class AiDestinationResolver
    {
        // Config keys are server-owned. `AiService:DestinationClass` must be set explicitly to approve.
        public const string DestinationClassKey = "AiService:DestinationClass";
        public const string BaseUrlKey = "AiService:BaseUrl";

        // ------------------------------------------------------------------------------------------
        // ASSESSED FROM THE PYTHON SERVICE'S OWN SOURCE (crossbuy_ai, read this increment), which
        // corrects an over-broad statement in the previous report. The service does NOT relay everything
        // to Claude:
        //
        //   app/routers/anomaly.py   -> app.ml.anomaly      (local ML, no anthropic import)
        //   app/routers/forecast.py  -> app.ml.forecast     (local ML, no anthropic import)
        //   app/routers/inventory.py -> app.ml.inventory    (local ML, no anthropic import)
        //   app/routers/diag.py      -> app.clients.anthropic.complete   <-- the ONLY relay
        //
        // So the three BUSINESS payloads terminate at a local process, while the DIAGNOSTIC echo — which
        // forwards caller-supplied text — reaches a third party. One destination class per deployment
        // cannot express that, and treating the whole service as one thing is wrong in either direction:
        // too strict blocks working local ML, too lax sends business data to a relay.
        // ------------------------------------------------------------------------------------------
        // ------------------------------------------------------------------------------------------
        // INCREMENT 4.3 — CONFIGURATION STATES AN INTENT; GOVERNANCE GRANTS THE AUTHORITY.
        //
        // These two overloads supply NO governance authority, so they can never return
        // ApprovedExternalProcessor. That is not a limitation to work around — it is the fix. A caller
        // that has not consulted the owner-approval record has not established that an external
        // processor is approved, and the safe reading of "I didn't ask" is "not approved".
        //
        // Internal and UnapprovedExternal are unaffected: local loopback ML never needed a provider
        // decision and must not acquire a dependency on one.
        // ------------------------------------------------------------------------------------------
        public static AiEgressDestinationClass Resolve(IConfiguration config)
            => Resolve(config, AiEgressPurpose.Unknown, AiProviderState.Unknown);

        public static AiEgressDestinationClass Resolve(IConfiguration config, AiEgressPurpose purpose)
            => Resolve(config, purpose, AiProviderState.Unknown);

        /// The authoritative overload. <paramref name="providerState"/> comes from
        /// <see cref="IAiProviderAuthority"/> — never from configuration.
        public static AiEgressDestinationClass Resolve(
            IConfiguration config, AiEgressPurpose purpose, AiProviderState providerState)
        {
            var configured = config[DestinationClassKey];

            // Unset ⇒ UnapprovedExternal, NOT Internal. UNCHANGED from the previous increment: the safe
            // default for an unassessed destination is the one that refuses.
            if (string.IsNullOrWhiteSpace(configured))
                return AiEgressDestinationClass.UnapprovedExternal;

            if (!Enum.TryParse<AiEgressDestinationClass>(configured, ignoreCase: true, out var parsed))
                return AiEgressDestinationClass.Unknown;    // a typo must not become a grant

            // ---- evidence-based corrections that a deployment may not configure away ----

            // 1. The diagnostic route RELAYS to Anthropic. It can never be Internal, whatever the config
            //    says, because "Internal" means "does not forward anywhere else" and this one does.
            //
            //    ASSIGNED, NOT RETURNED (Increment 4.3): this rule reclassifies the destination as
            //    external, and everything external must still pass the governance gate below. Returning
            //    here would have let the relay route out through the one door the gate does not watch.
            if (purpose == AiEgressPurpose.ConnectivityDiagnostic && parsed == AiEgressDestinationClass.Internal)
                parsed = AiEgressDestinationClass.ApprovedExternalProcessor;

            // 2. "Internal" is only true if the service is actually reachable at a LOOPBACK address. A
            //    deployment that labels a remote host Internal is mislabelling it — possibly by copying a
            //    development config — and the label must not be taken at face value.
            if (parsed == AiEgressDestinationClass.Internal && !IsLoopback(config[BaseUrlKey]))
                return AiEgressDestinationClass.UnapprovedExternal;

            // 3. THE GOVERNANCE GATE (Increment 4.3). Applied LAST, so it also catches the class that
            //    rule 1 just derived for the diagnostic relay. Configuration may ask for an approved
            //    external processor; only the owner-approval record can answer yes.
            //
            //    Every state except ApprovedExternalProcessor denies — including Suspended and Rejected,
            //    which describe an approval that once existed or was refused. Both are "not approved now",
            //    and treating them as anything else would let a lapsed approval keep working.
            if (parsed == AiEgressDestinationClass.ApprovedExternalProcessor
                && providerState != AiProviderState.ApprovedExternalProcessor)
                return AiEgressDestinationClass.UnapprovedExternal;

            return parsed;
        }

        /// True only for a loopback host. Anything unparseable is NOT loopback — an unreadable URL is not
        /// evidence of locality.
        public static bool IsLoopback(string? baseUrl)
        {
            if (string.IsNullOrWhiteSpace(baseUrl)) return false;
            if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri)) return false;
            return uri.IsLoopback;
        }
    }
}
