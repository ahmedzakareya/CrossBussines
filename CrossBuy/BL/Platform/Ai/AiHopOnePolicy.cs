using CrossBuy.Models.Platform;
using Microsoft.Extensions.Configuration;

namespace CrossBuy.BL.Platform.Ai
{
    // AI Foundation — Increment 4. HOP 1: CrossBuy -> the Python AI service.
    //
    // Increment 3.1 established that "Internal" is only true when the service is actually reachable at a
    // loopback address. That was a check on ONE field. This turns the whole hop into a DECLARED
    // DEPLOYMENT MODE whose invariants are validated together, because the dangerous combinations are
    // relationships between fields rather than any single bad value:
    //
    //     Internal + remote host        -> a remote processor labelled internal
    //     Remote   + plaintext HTTP     -> business data over the wire in clear
    //     Any mode + missing credential -> an unauthenticated call that still leaves the estate
    //
    // Each of those is individually plausible as a configuration typo, and individually invisible without
    // a check that looks at the fields together.
    public enum AiDeploymentMode
    {
        // Zero is Unknown so an unset or misspelled configuration is refused rather than inheriting the
        // first real mode's meaning. Same rule as every other enum in the AI contracts.
        Unknown = 0,

        // The Python service runs on this host. HTTP is acceptable because the traffic never reaches a
        // network interface; the shared secret is still required, so a second local process cannot call it.
        LocalLoopback,

        // The Python service runs elsewhere. HTTPS is mandatory, certificate validation is the platform
        // default (never overridden), and the destination can never be Internal.
        RemoteSecure,
    }

    public sealed class AiHopOneValidation
    {
        public required bool Ok { get; init; }
        public required AiDeploymentMode Mode { get; init; }

        // Safe to log and to surface to an operator: names the rule, never the URL (which may carry a
        // host name that is itself sensitive) and never the secret.
        public string? Reason { get; init; }

        public static AiHopOneValidation Valid(AiDeploymentMode mode) => new() { Ok = true, Mode = mode };
        public static AiHopOneValidation Invalid(AiDeploymentMode mode, string reason)
            => new() { Ok = false, Mode = mode, Reason = reason };
    }

    public static class AiHopOnePolicy
    {
        public const string DeploymentModeKey = "AiService:DeploymentMode";

        /// The mode this host declares. Unset or unrecognised is Unknown, which is refused — a deployment
        /// must SAY what it is rather than having it inferred from a URL that may be wrong.
        public static AiDeploymentMode ResolveMode(IConfiguration config)
        {
            var configured = config[DeploymentModeKey];
            if (string.IsNullOrWhiteSpace(configured)) return AiDeploymentMode.Unknown;

            return Enum.TryParse<AiDeploymentMode>(configured, ignoreCase: true, out var parsed)
                ? parsed
                : AiDeploymentMode.Unknown;
        }

        /// Validates the hop as a whole. Every rule here is a RELATIONSHIP between configuration values,
        /// which is precisely what a per-field check cannot express.
        public static AiHopOneValidation Validate(IConfiguration config)
        {
            var mode = ResolveMode(config);
            var baseUrl = config[AiDestinationResolver.BaseUrlKey];
            var declaredClass = config[AiDestinationResolver.DestinationClassKey];

            if (mode == AiDeploymentMode.Unknown)
                return AiHopOneValidation.Invalid(mode,
                    $"'{DeploymentModeKey}' is not set to a supported mode " +
                    $"({nameof(AiDeploymentMode.LocalLoopback)} | {nameof(AiDeploymentMode.RemoteSecure)}). " +
                    "An undeclared deployment cannot be validated, so AI egress is refused.");

            if (string.IsNullOrWhiteSpace(baseUrl))
                return AiHopOneValidation.Invalid(mode, $"'{AiDestinationResolver.BaseUrlKey}' is not configured.");

            if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri))
                return AiHopOneValidation.Invalid(mode,
                    $"'{AiDestinationResolver.BaseUrlKey}' is not an absolute URL. A URL that cannot be parsed " +
                    "cannot be shown to be local or secure, so it is refused.");

            switch (mode)
            {
                case AiDeploymentMode.LocalLoopback:
                    // The whole justification for permitting plaintext is that the traffic never leaves the
                    // host. If the host is not loopback, that justification is gone.
                    if (!uri.IsLoopback)
                        return AiHopOneValidation.Invalid(mode,
                            $"{nameof(AiDeploymentMode.LocalLoopback)} requires a loopback host " +
                            "(localhost / 127.0.0.1 / ::1). The configured host is not loopback, so this is a " +
                            "REMOTE deployment and must use " + nameof(AiDeploymentMode.RemoteSecure) + " over HTTPS.");
                    break;

                case AiDeploymentMode.RemoteSecure:
                    // No auto-upgrade: a caller that wrote http:// meant http://, and silently rewriting it
                    // would hide a real misconfiguration behind a working request.
                    if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
                        return AiHopOneValidation.Invalid(mode,
                            $"{nameof(AiDeploymentMode.RemoteSecure)} requires HTTPS. Plaintext HTTP to a remote " +
                            "host would send business data in clear; the URL is NOT upgraded automatically.");

                    // A remote host is not internal, whatever the label says. Refusing the COMBINATION is
                    // clearer than silently downgrading the class, because the operator's intent is wrong.
                    if (string.Equals(declaredClass, nameof(AiEgressDestinationClass.Internal), StringComparison.OrdinalIgnoreCase))
                        return AiHopOneValidation.Invalid(mode,
                            $"'{AiDestinationResolver.DestinationClassKey}' declares Internal, but the deployment " +
                            "mode is remote. A processor on another host is not internal.");
                    break;
            }

            // Applies to BOTH modes. Loopback narrows who can reach the service; it does not authenticate
            // the caller, and any local process could otherwise call it.
            if (!AiEgressPolicy.IsUsableSecret(config["AiService:Secret"]))
                return AiHopOneValidation.Invalid(mode,
                    "No usable 'AiService:Secret' is configured (missing, blank, or still the deployment " +
                    "placeholder). The AI service authenticates its caller with that shared secret, so egress " +
                    "is refused before any network call.");

            return AiHopOneValidation.Valid(mode);
        }
    }

    // ---------------------------------------------------------------------------------------------
    // PROVIDER FACTS — the contract a FUTURE external provider must satisfy. No provider is connected,
    // and nothing here names one.
    //
    // Every field defaults to Unknown, and Unknown blocks approval. That is the whole design: a provider
    // becomes approvable by someone SUPPLYING facts, never by the absence of an objection.
    // ---------------------------------------------------------------------------------------------
    public enum AiProviderFact
    {
        Unknown = 0,
        Yes,
        No,
        NotApplicable,
    }

    public sealed class AiProviderProfile
    {
        public string? ProviderName { get; init; }
        public string? AccountOrProject { get; init; }
        public string? Region { get; init; }

        // The facts that gate approval. Deliberately phrased so that "Yes" is the SAFE answer in each
        // case — a reader cannot approve by skimming and seeing a column of Yes without them meaning it.
        public AiProviderFact TrainingOnCustomerDataDisabled { get; init; } = AiProviderFact.Unknown;
        public AiProviderFact PromptAndOutputRetentionBounded { get; init; } = AiProviderFact.Unknown;
        public AiProviderFact DataResidencyCommitted { get; init; } = AiProviderFact.Unknown;
        public AiProviderFact EncryptionInTransit { get; init; } = AiProviderFact.Unknown;
        public AiProviderFact DeletionControlsAvailable { get; init; } = AiProviderFact.Unknown;
        public AiProviderFact DataProcessingAgreementInPlace { get; init; } = AiProviderFact.Unknown;

        // An explicit human decision, recorded separately from the facts. Facts alone never approve:
        // somebody has to accept the residual risk, and that acceptance is auditable.
        public bool OwnerApproved { get; init; }

        public static readonly string[] MandatoryFactNames =
        {
            nameof(TrainingOnCustomerDataDisabled),
            nameof(PromptAndOutputRetentionBounded),
            nameof(DataResidencyCommitted),
            nameof(EncryptionInTransit),
            nameof(DeletionControlsAvailable),
            nameof(DataProcessingAgreementInPlace),
        };

        /// Facts still missing. Non-empty ⇒ the provider may NOT be an ApprovedExternalProcessor.
        public IReadOnlyList<string> MissingMandatoryFacts()
        {
            var missing = new List<string>();
            void Check(AiProviderFact f, string name) { if (f == AiProviderFact.Unknown) missing.Add(name); }

            Check(TrainingOnCustomerDataDisabled, nameof(TrainingOnCustomerDataDisabled));
            Check(PromptAndOutputRetentionBounded, nameof(PromptAndOutputRetentionBounded));
            Check(DataResidencyCommitted, nameof(DataResidencyCommitted));
            Check(EncryptionInTransit, nameof(EncryptionInTransit));
            Check(DeletionControlsAvailable, nameof(DeletionControlsAvailable));
            Check(DataProcessingAgreementInPlace, nameof(DataProcessingAgreementInPlace));
            return missing;
        }

        /// The decision. NOT a boolean — "not yet decided" and "decided no" are different answers and an
        /// operator must be able to tell them apart.
        public AiProviderDecision Decide()
        {
            if (string.IsNullOrWhiteSpace(ProviderName))
                return AiProviderDecision.OwnerDecisionRequired("No provider is named.");

            var missing = MissingMandatoryFacts();
            if (missing.Count > 0)
                return AiProviderDecision.OwnerDecisionRequired(
                    "Mandatory provider facts are still unknown: " + string.Join(", ", missing));

            // A fact answered the UNSAFE way is a refusal, not a missing answer.
            if (TrainingOnCustomerDataDisabled == AiProviderFact.No)
                return AiProviderDecision.Denied("The provider trains on customer data.");
            if (EncryptionInTransit == AiProviderFact.No)
                return AiProviderDecision.Denied("The provider does not encrypt data in transit.");

            if (!OwnerApproved)
                return AiProviderDecision.OwnerDecisionRequired(
                    "All mandatory facts are known, but no owner approval has been recorded.");

            return AiProviderDecision.Approved();
        }
    }

    public sealed class AiProviderDecision
    {
        public required AiEgressDestinationClass Class { get; init; }
        public required string Reason { get; init; }
        public bool IsApproved => Class == AiEgressDestinationClass.ApprovedExternalProcessor;

        public static AiProviderDecision Approved() => new()
        {
            Class = AiEgressDestinationClass.ApprovedExternalProcessor,
            Reason = "All mandatory provider facts are known and an owner approval is recorded.",
        };

        // Both non-approval outcomes map to a destination class that DENIES, so a caller that ignores the
        // reason still fails closed.
        public static AiProviderDecision OwnerDecisionRequired(string reason) => new()
        {
            Class = AiEgressDestinationClass.UnapprovedExternal,
            Reason = "OWNER DECISION REQUIRED — " + reason,
        };

        public static AiProviderDecision Denied(string reason) => new()
        {
            Class = AiEgressDestinationClass.UnapprovedExternal,
            Reason = "DENIED — " + reason,
        };
    }
}
