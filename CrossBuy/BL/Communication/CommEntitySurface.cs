using CrossBuy.BL.Platform;
using CrossBuy.Models.Communication;
using CrossBuy.Models.Platform;
using Microsoft.Extensions.Options;

namespace CrossBuy.BL.Communication
{
    // =============================================================================================
    // Communication Platform (ADR-031) — THE COLLABORATION SURFACE.
    //
    // This is the answer to the architecture requirement: "Every business entity must support Timeline,
    // Comments, Mentions, Attachments, Activity History without duplicating code."
    //
    // The mechanism is a single question asked in one place: MAY this entity carry this capability? Every
    // write path in the platform asks it first, so onboarding Accounting, Inventory, CRM, HR, Tasks, Projects,
    // Manufacturing and Support comments (modules 36-43) is a CONFIGURATION change plus a registry code — not
    // eight services, eight tables, or eight controllers.
    //
    // THE THREE-WAY DECISION, and why it is not just "read the registry flag":
    //
    //   1. DENY LIST wins outright. A deployment that discovers a leak (a confidential note printed on a
    //      customer-facing document) must be able to pull one entity out of the surface without a code change
    //      and without waiting for a release.
    //   2. REGISTRY FLAG (SupportsComments / SupportsFiles / SupportsFollowers / SupportsTimeline) is the
    //      authoritative "wired today" signal, owned by the platform kernel.
    //   3. ALLOW LIST is ADDITIVE on top of the flag.
    //
    // Why (3) exists at all — the honest reason: EntityRegistry is kernel code owned by another work stream,
    // and CLAUDE.md names it and its neighbours architectural invariants requiring owner coordination. Today
    // exactly THREE codes carry SupportsComments (SalesInvoice, PurchaseInvoice, Quotation) and ONE carries
    // SupportsFollowers (none). Flipping eight flags from this work stream would be the uncoordinated
    // cross-team edit that rule forbids. So the allow list lets a deployment onboard an entity now, and
    // GetOnboardingGap() reports exactly which flags the kernel owner still needs to flip — the divergence is
    // reported, never hidden.
    //
    // The registry is still the gate on EXISTENCE: an unregistered code is refused by every path, allow list
    // or not. Free-text entity types are what ADR-002 exists to prevent.
    // =============================================================================================
    public interface ICommEntitySurface
    {
        // Capability names, as they appear in a CommEntityNotSupportedException.
        // Kept as constants rather than an enum so the exception message and the option list read the same.
        Task<CommSurfaceDecision> EvaluateAsync(CommEntityRef? entity, string capability);

        // Throws CommEntityNotSupportedException when the capability is unavailable. The normal entry point:
        // a service that calls this cannot forget to check the return value.
        Task<EntityDefinition> RequireAsync(CommEntityRef? entity, string capability);

        // Non-throwing probe for read paths that legitimately answer "nothing here" — e.g. the timeline
        // aggregator asking whether the kernel source applies at all.
        Task<bool> SupportsAsync(CommEntityRef? entity, string capability);

        // Every code this deployment carries comments on, with its registry state. Read by the platform's
        // own diagnostics and by CommunicationSurfaceTests.
        IReadOnlyList<CommSurfaceReport> Describe();

        // The onboarding gap: codes enabled by CONFIGURATION whose registry flag is still false. This is the
        // list the kernel owner must act on, and it is published rather than tracked in someone's head.
        IReadOnlyList<CommSurfaceReport> GetOnboardingGap();
    }

    public static class CommCapabilities
    {
        public const string Comments = "Comments";
        public const string Mentions = "Mentions";
        public const string Attachments = "Attachments";
        public const string Followers = "Followers";
        public const string Timeline = "Timeline";

        public static readonly IReadOnlyList<string> All =
            new[] { Comments, Mentions, Attachments, Followers, Timeline };

        public static bool IsValid(string? value) =>
            value != null && All.Contains(value, StringComparer.Ordinal);
    }

    public sealed class CommSurfaceDecision
    {
        public required bool Allowed { get; init; }
        public required string Reason { get; init; }
        public EntityDefinition? Definition { get; init; }

        // True when the capability is allowed by CONFIGURATION while the registry flag is still false.
        // Surfaced so a caller can log it once rather than the platform silently diverging from the kernel.
        public bool GrantedByConfiguration { get; init; }

        public static CommSurfaceDecision Deny(string reason) =>
            new() { Allowed = false, Reason = reason };

        public static CommSurfaceDecision Allow(EntityDefinition definition, string reason, bool byConfiguration) =>
            new() { Allowed = true, Reason = reason, Definition = definition, GrantedByConfiguration = byConfiguration };
    }

    public sealed class CommSurfaceReport
    {
        public required string EntityCode { get; init; }
        public required string Module { get; init; }
        public required bool RegistrySupportsComments { get; init; }
        public required bool RegistrySupportsFiles { get; init; }
        public required bool RegistrySupportsFollowers { get; init; }
        public required bool RegistrySupportsTimeline { get; init; }
        public required bool EnabledByConfiguration { get; init; }
        public required bool BlockedByConfiguration { get; init; }
        public required IReadOnlyList<string> EffectiveCapabilities { get; init; }
    }

    public sealed class CommEntitySurface : ICommEntitySurface
    {
        private readonly IEntityRegistry _registry;
        private readonly CommunicationPlatformOptions _options;

        public CommEntitySurface(IEntityRegistry registry, IOptions<CommunicationPlatformOptions> options)
        {
            _registry = registry ?? throw new ArgumentNullException(nameof(registry));
            _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        }

        public Task<CommSurfaceDecision> EvaluateAsync(CommEntityRef? entity, string capability)
            => Task.FromResult(Evaluate(entity, capability));

        // Synchronous core. The interface is async because a future surface may consult the database (a
        // per-company onboarding table), and changing the signature later would touch every caller.
        private CommSurfaceDecision Evaluate(CommEntityRef? entity, string capability)
        {
            if (!CommCapabilities.IsValid(capability))
                return CommSurfaceDecision.Deny($"'{capability}' is not a communication capability.");

            if (entity == null || !entity.IsWellFormed)
                return CommSurfaceDecision.Deny("A well-formed entity reference (registered code + positive id) is required.");

            // ---- gate 1: EXISTENCE. An unregistered code is refused whatever the configuration says.
            // This is the ADR-002 rule and the allow list does not override it: configuration may onboard a
            // registered entity, never invent one.
            if (!_registry.TryGetDefinition(entity.EntityCode, out var definition) || definition == null)
                return CommSurfaceDecision.Deny(
                    $"'{entity.EntityCode}' is not registered in IEntityRegistry.");

            // ---- gate 2: DENY LIST. Wins over everything, including the registry flag.
            if (Contains(_options.BlockedEntityCodes, definition.Code))
                return CommSurfaceDecision.Deny(
                    $"'{definition.Code}' is in CommunicationPlatform:BlockedEntityCodes.");

            // ---- gate 3: capability.
            bool registryFlag = RegistryFlag(definition, capability);
            bool configured = Contains(_options.EnabledEntityCodes, definition.Code);

            if (registryFlag)
                return CommSurfaceDecision.Allow(definition, "IEntityRegistry declares the capability.", byConfiguration: false);

            if (configured)
                return CommSurfaceDecision.Allow(
                    definition,
                    "Enabled by CommunicationPlatform:EnabledEntityCodes; the registry flag is still false.",
                    byConfiguration: true);

            return CommSurfaceDecision.Deny(
                $"neither IEntityRegistry.Supports{Flag(capability)} nor CommunicationPlatform:EnabledEntityCodes covers it");
        }

        public async Task<EntityDefinition> RequireAsync(CommEntityRef? entity, string capability)
        {
            var decision = await EvaluateAsync(entity, capability);
            if (!decision.Allowed)
                throw new CommEntityNotSupportedException(entity?.EntityCode, capability, decision.Reason);
            return decision.Definition!;
        }

        public async Task<bool> SupportsAsync(CommEntityRef? entity, string capability)
            => (await EvaluateAsync(entity, capability)).Allowed;

        public IReadOnlyList<CommSurfaceReport> Describe()
        {
            var reports = new List<CommSurfaceReport>();
            foreach (var d in _registry.GetDefinitions())
            {
                bool blocked = Contains(_options.BlockedEntityCodes, d.Code);
                bool enabled = Contains(_options.EnabledEntityCodes, d.Code);

                var effective = new List<string>();
                foreach (var capability in CommCapabilities.All)
                {
                    if (blocked) continue;
                    if (RegistryFlag(d, capability) || enabled) effective.Add(capability);
                }

                reports.Add(new CommSurfaceReport
                {
                    EntityCode = d.Code,
                    Module = d.Module,
                    RegistrySupportsComments = d.SupportsComments,
                    RegistrySupportsFiles = d.SupportsFiles,
                    RegistrySupportsFollowers = d.SupportsFollowers,
                    RegistrySupportsTimeline = d.SupportsTimeline,
                    EnabledByConfiguration = enabled,
                    BlockedByConfiguration = blocked,
                    EffectiveCapabilities = effective,
                });
            }
            return reports;
        }

        public IReadOnlyList<CommSurfaceReport> GetOnboardingGap()
            => Describe()
                .Where(r => r.EnabledByConfiguration && !r.BlockedByConfiguration && !r.RegistrySupportsComments)
                .ToList();

        // Which registry flag backs which capability.
        //
        // Mentions maps to SupportsComments deliberately: a mention only exists inside a comment, so an
        // entity that carries comments carries mentions by construction. Giving mentions their own flag would
        // create a state — comments on, mentions off — that no code path could produce.
        //
        // Attachments maps to SupportsFiles because that flag is exactly the kernel's statement about whether
        // the entity has a file surface at all.
        private static bool RegistryFlag(EntityDefinition d, string capability) => capability switch
        {
            CommCapabilities.Comments => d.SupportsComments,
            CommCapabilities.Mentions => d.SupportsComments,
            CommCapabilities.Attachments => d.SupportsFiles,
            CommCapabilities.Followers => d.SupportsFollowers,
            CommCapabilities.Timeline => d.SupportsTimeline,
            _ => false,
        };

        private static string Flag(string capability) => capability switch
        {
            CommCapabilities.Comments or CommCapabilities.Mentions => "Comments",
            CommCapabilities.Attachments => "Files",
            CommCapabilities.Followers => "Followers",
            CommCapabilities.Timeline => "Timeline",
            _ => capability,
        };

        // Ordinal-ignore-case on purpose: a code is PascalCase by contract, but an operator typing it into
        // appsettings.json will get the casing wrong sooner or later, and silently ignoring their entry is
        // worse than accepting it. The value STORED is always the registry's canonical casing.
        private static bool Contains(List<string>? list, string code)
            => list != null && list.Any(c => string.Equals(c?.Trim(), code, StringComparison.OrdinalIgnoreCase));
    }
}