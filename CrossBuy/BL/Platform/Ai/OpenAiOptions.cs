using Microsoft.Extensions.Configuration;

namespace CrossBuy.BL.Platform.Ai
{
    // AI Foundation — Increment 4.5, Phase 6. OPENAI CONFIGURATION.
    //
    // CONFIGURATION IS NOT APPROVAL. Every value here is a technical fact — where to send, which model,
    // how long to wait. None of it can make OpenAI an approved processor; that requires a governance
    // record with eleven evidenced facts and three signatures, and OpenAI has neither. A fully and
    // correctly configured OpenAI is still refused by AiEgressPolicy today, and that is intended.
    //
    // THE KEY IS NEVER STORED. It is read from the environment at use time and held only for the lifetime
    // of the request that needs it. It is not written to appsettings, not to the database, not to a log,
    // not to an audit record, not to an exception message, and never reaches Razor or the browser.
    public sealed class OpenAiOptions
    {
        public const string ProviderId = "OpenAI";

        /// The environment variable the operator has already set. Read via IConfiguration, which
        /// CreateBuilder wires to the environment by default.
        public const string ApiKeyEnvironmentVariable = "OPENAI_API_KEY";

        /// Sectioned alternative, matching the house convention (`AiService:Secret` → `AiService__Secret`).
        /// Checked FIRST so a deployment can scope the key per environment; the flat variable remains
        /// supported because it is the OpenAI SDK's own convention and what developers already have set.
        public const string ApiKeyConfigurationKey = "OpenAi:ApiKey";

        public const string ConfigRoot = "OpenAi";

        public string BaseUrl { get; init; } = "https://api.openai.com/v1";
        public string Model { get; init; } = "gpt-4o-mini";
        public int TimeoutSeconds { get; init; } = 60;
        public int MaxOutputTokens { get; init; } = 800;

        /// Mirrors the switchboard. Present so the whole provider block reads in one place; the
        /// switchboard remains the enforcement point.
        public bool Enabled { get; init; }

        // ---- Increment 4.9: WHICH ACCOUNT CONTEXT THIS DEPLOYMENT INTENDS TO CALL ----
        //
        // Identifiers, never credentials. Stating them is NOT approval — the governance record decides
        // that, and it currently approves nobody. These exist so a deployment can say WHICH project it
        // is aiming at, and so the adapter can refuse an approval minted for a different one.
        //
        // Environment is a TYPED value parsed from configuration, and an unrecognised or absent value is
        // Unknown, which fails closed everywhere. It is never inferred from ASPNETCORE_ENVIRONMENT: the
        // deployment's own idea of "development" must not silently become a governance claim.
        public string? OrganizationId { get; init; }
        public string? ProjectId { get; init; }
        public AiProviderEnvironment Environment { get; init; } = AiProviderEnvironment.Unknown;

        public AiProviderScope Scope => new(ProviderId, OrganizationId, ProjectId, Environment);

        public static OpenAiOptions FromConfiguration(IConfiguration config)
        {
            ArgumentNullException.ThrowIfNull(config);

            var defaults = new OpenAiOptions();
            return new OpenAiOptions
            {
                BaseUrl = Nonblank(config[$"{ConfigRoot}:BaseUrl"]) ?? defaults.BaseUrl,
                Model = Nonblank(config[$"{ConfigRoot}:Model"]) ?? defaults.Model,
                TimeoutSeconds = PositiveInt(config[$"{ConfigRoot}:TimeoutSeconds"]) ?? defaults.TimeoutSeconds,
                MaxOutputTokens = PositiveInt(config[$"{ConfigRoot}:MaxOutputTokens"]) ?? defaults.MaxOutputTokens,
                Enabled = bool.TryParse(config[$"{AiProviderSwitchboard.ConfigRoot}:{ProviderId}:Enabled"], out var e) && e,

                OrganizationId = Nonblank(config[$"{ConfigRoot}:OrganizationId"]),
                ProjectId = Nonblank(config[$"{ConfigRoot}:ProjectId"]),
                Environment = Enum.TryParse<AiProviderEnvironment>(
                        config[$"{ConfigRoot}:Environment"], ignoreCase: true, out var env)
                    ? env
                    : AiProviderEnvironment.Unknown,
            };
        }

        private static string? Nonblank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;
        private static int? PositiveInt(string? s) => int.TryParse(s, out var v) && v > 0 ? v : null;

        /// <summary>
        /// Reads the key. Returns null when absent or unusable — never throws, and never reveals it.
        /// </summary>
        /// <remarks>
        /// Reuses <see cref="AiEgressPolicy.IsUsableSecret"/> rather than writing a second check, so the
        /// placeholder rule that stops an unconfigured production host from authenticating with the
        /// template's own text applies here identically.
        /// </remarks>
        public static string? ReadApiKey(IConfiguration config)
        {
            ArgumentNullException.ThrowIfNull(config);

            var sectioned = config[ApiKeyConfigurationKey];
            if (AiEgressPolicy.IsUsableSecret(sectioned)) return sectioned;

            var flat = config[ApiKeyEnvironmentVariable];
            return AiEgressPolicy.IsUsableSecret(flat) ? flat : null;
        }

        /// True when a usable key exists. **Presence only** — never the value, the length or a prefix.
        public static bool HasApiKey(IConfiguration config) => ReadApiKey(config) is not null;

        /// <summary>
        /// A sanitized readiness report, safe to log and safe to show an operator.
        /// </summary>
        /// <remarks>
        /// Deliberately NOT a startup exception. OpenAI is optional and unapproved; taking an ERP offline
        /// because an optional, currently-forbidden provider lacks a key would be a self-inflicted outage.
        /// This mirrors the hop-1 startup block: report loudly, keep serving, stay denied.
        /// </remarks>
        public static string Diagnose(IConfiguration config)
        {
            var o = FromConfiguration(config);
            var key = HasApiKey(config) ? "present" : "MISSING";

            // Increment 4.6 — pricing readiness is reported alongside the rest, because "unpriced" is a
            // state an operator needs to know about BEFORE a bill arrives, not after. It is a warning,
            // never an error: an unpriced model still runs under the token ceiling, and inventing a
            // price to silence this line would be far worse than the line itself.
            var priced = IsPriced(config, o.Model) ? "configured" : "NOT CONFIGURED (cost will report UNKNOWN, never zero)";

            return $"[AI] OpenAI provider configuration: enabled={o.Enabled}; apiKey={key}; " +
                   $"model={o.Model}; baseUrl={o.BaseUrl}; timeout={o.TimeoutSeconds}s; " +
                   $"maxOutputTokens={o.MaxOutputTokens}; pricing={priced}. " +
                   "NOTE: configuration does not approve a provider — external egress remains subject to " +
                   "the governance record, which currently approves nobody.";
        }

        /// <summary>True when a usable price exists for (OpenAI, model).</summary>
        /// <remarks>
        /// Deliberately checks that the values PARSE, not merely that the keys exist. The shipped
        /// production template carries `__SET_FROM_CONTRACT__` placeholders precisely so that an
        /// unfilled deployment reads as unpriced rather than as a silent zero.
        /// </remarks>
        public static bool IsPriced(IConfiguration config, string model)
        {
            ArgumentNullException.ThrowIfNull(config);
            if (string.IsNullOrWhiteSpace(model)) return false;

            var root = $"Ai:Pricing:{ProviderId}:{model}";
            return decimal.TryParse(config[$"{root}:InputPerMillion"], out _)
                   && decimal.TryParse(config[$"{root}:OutputPerMillion"], out _)
                   && !string.IsNullOrWhiteSpace(config[$"{root}:Currency"]);
        }
    }
}
