using Microsoft.Extensions.Configuration;

namespace CrossBuy.BL.Platform.Ai
{
    // AI Foundation — Increment 4.5, Phase 4. PER-PROVIDER KILL SWITCH.
    //
    // WHAT ALREADY EXISTED: `AiService:EgressEnabled`, checked inside AiEgressPolicy. It is GLOBAL — it
    // stops everything, including the local loopback ML that has nothing to do with any provider. That is
    // the right control for "stop all AI now" and the wrong one for "OpenAI is misbehaving, turn OFF
    // OpenAI and leave the rest running". PRESERVED UNCHANGED; this sits beside it.
    //
    // THREE INDEPENDENT CONDITIONS, ALL REQUIRED:
    //
    //     Global AI egress enabled        (AiService:EgressEnabled — existing, unchanged)
    //          AND
    //     This provider enabled           (this file)
    //          AND
    //     Governance approval             (AiProviderAuthority + AiProviderEvaluator — untouched)
    //
    // The switch is NOT approval and must never be mistaken for it. Enabling a provider here says "we are
    // willing to call it if governance permits"; it says nothing about whether governance permits. A
    // provider can be enabled and still — as OpenAI is today — completely unapproved and unreachable.
    //
    // WHY IT IS NOT INSIDE AiEgressPolicy: the policy is provider-agnostic by design. Its request carries
    // a destination CLASS, not a provider name. Teaching it provider identities would give it a second
    // vocabulary to keep in step with this one.
    public interface IAiProviderSwitchboard
    {
        /// False unless a provider is explicitly enabled. Missing configuration is OFF.
        bool IsEnabled(string providerId);

        /// <summary>
        /// Increment 4.9 — enabled for a SPECIFIC environment. Both the provider-wide switch AND the
        /// environment switch must be on.
        /// </summary>
        /// <remarks>
        /// Defence in depth, not authority: this is still availability, and it can never approve
        /// anything. But a single provider-wide switch would arm Development and Production together,
        /// and "we only turned it on for dev" would not be true of the configuration. Two keys make the
        /// intent explicit and cost one extra line to set.
        /// </remarks>
        bool IsEnabled(string providerId, AiProviderEnvironment environment);

        /// Payload-free explanation for the log and the deny reason.
        string Explain(string providerId);
    }

    public sealed class AiProviderSwitchboard : IAiProviderSwitchboard
    {
        public const string ConfigRoot = "Ai:Providers";

        private readonly IConfiguration _config;
        public AiProviderSwitchboard(IConfiguration config) => _config = config;

        public bool IsEnabled(string providerId)
        {
            if (string.IsNullOrWhiteSpace(providerId)) return false;

            // FAIL CLOSED, and note which way round this is: the DEFAULT is false. `AiService:EgressEnabled`
            // defaults to TRUE because it guards a subsystem that already worked and must not be switched
            // off by an absent key. An external provider has never worked, so the safe default for it is
            // the opposite — absent means off. Reading a missing key as "on" is how an unconfigured host
            // starts making paid calls.
            var raw = _config[$"{ConfigRoot}:{providerId}:Enabled"];
            return bool.TryParse(raw, out var enabled) && enabled;
        }

        public bool IsEnabled(string providerId, AiProviderEnvironment environment)
        {
            // An unstated environment is never enabled. Same rule as everywhere else in this increment:
            // Unknown is not a synonym for Development.
            if (environment == AiProviderEnvironment.Unknown) return false;

            // BOTH switches. The provider-wide key remains the master — turning OpenAI off must turn it
            // off everywhere, without hunting for per-environment keys.
            if (!IsEnabled(providerId)) return false;

            var raw = _config[$"{ConfigRoot}:{providerId}:{environment}:Enabled"];
            return bool.TryParse(raw, out var enabled) && enabled;
        }

        public string Explain(string providerId)
        {
            if (string.IsNullOrWhiteSpace(providerId)) return "provider-switch:unnamed";

            var raw = _config[$"{ConfigRoot}:{providerId}:Enabled"];
            if (raw is null) return $"provider-switch:{providerId}:unconfigured";
            if (!bool.TryParse(raw, out var enabled)) return $"provider-switch:{providerId}:unparseable";
            return enabled ? $"provider-switch:{providerId}:enabled" : $"provider-switch:{providerId}:disabled";
        }
    }
}
