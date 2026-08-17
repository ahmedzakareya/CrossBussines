using CrossBuy.Models.Platform;

namespace CrossBuy.BL.Platform
{
    // Platform Kernel (ADR-002) — the single source of truth for business object types.
    //
    // Promoted from the TM-2 ITaskLinkResolver, which already owned 80% of this job (bilingual label,
    // icon, deep link, record search) for its own picker. Everything that needs to key off an entity type
    // — BusinessEvents, timeline, comments, and later relations / files / followers / search / AI — resolves
    // through this ONE registry so the code vocabulary can never fork again.
    //
    // Before the kernel there were three disagreeing vocabularies: TaskLinkResolver keys (PascalCase),
    // DocComment.EntityType (unconstrained free text) and NotificationTypes strings (snake_case).
    // The registry freezes the PascalCase set; new platform code must not accept free-text entity types.
    public interface IEntityRegistry
    {
        // Every registered definition, in stable display order.
        IReadOnlyList<EntityDefinition> GetDefinitions();

        // The definition for a canonical code. Throws EntityCodeNotRegisteredException when unknown —
        // an unregistered code is a programming error, not a runtime condition to tolerate.
        EntityDefinition GetDefinition(string entityCode);

        bool TryGetDefinition(string? entityCode, out EntityDefinition? definition);

        bool IsValid(string? entityCode);

        // Company-scoped record search powering pickers. Returns at most SearchTake rows.
        Task<List<EntitySearchResult>> SearchAsync(string entityCode, string? query, BusinessContext context, CancellationToken cancellationToken = default);

        // Resolves (code, id) to a label + deep link. Found = false when the row does not exist in the
        // caller's company; the result still carries a safe "#id" label so callers never crash on a
        // deleted record.
        Task<EntityResolveResult> ResolveAsync(string entityCode, int entityId, BusinessContext context, CancellationToken cancellationToken = default);

        // Deep link for a record, or null when the type has no screen. A RouteTemplate without "{id}"
        // is a list-only screen and is returned unchanged.
        string? BuildUrl(string entityCode, int entityId);
    }

    // Thrown when platform code is handed an entity code the registry does not know.
    public sealed class EntityCodeNotRegisteredException : InvalidOperationException
    {
        public EntityCodeNotRegisteredException(string? entityCode)
            : base($"Entity code '{entityCode ?? "(null)"}' is not registered in IEntityRegistry. " +
                   "Platform code must use a canonical registered code — free-text entity types are not allowed.")
        { EntityCode = entityCode; }

        public string? EntityCode { get; }
    }
}