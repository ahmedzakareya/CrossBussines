namespace CrossBuy.Models.Platform
{
    // Platform Kernel (PKS-001 / ADR-002) — the immutable description of ONE business object type.
    // The registry that owns these definitions is the SINGLE source of truth for entity codes: no new
    // platform code may accept a free-text EntityType. Promoted from the TM-2 TaskLinkResolver type list.
    public sealed class EntityDefinition
    {
        // Canonical PascalCase code. This is the value persisted in BusinessEvents.EntityType,
        // DocComment.EntityType and every future platform side-table. Frozen vocabulary.
        public required string Code { get; init; }

        public required string DisplayNameAr { get; init; }
        public required string DisplayNameEn { get; init; }

        // Owning functional module (Accounting | Inventory | Manufacturing | Pos | Hr | Projects | Crm).
        public required string Module { get; init; }

        // Metronic KI icon class, e.g. "ki-outline ki-bill".
        public required string Icon { get; init; }

        // Metronic contextual colour token (primary | success | warning | danger | info | dark).
        public required string Color { get; init; }

        // Deep-link template. "{id}" is substituted by BuildUrl. A template WITHOUT "{id}" is a
        // list-only screen (the record has no per-id detail view). null = no screen at all.
        public string? RouteTemplate { get; init; }

        public bool SupportsSearch { get; init; }
        public bool SupportsTimeline { get; init; }
        public bool SupportsComments { get; init; }
        public bool SupportsFiles { get; init; }
        public bool SupportsFollowers { get; init; }

        // Which module access service authorizes this entity — routed by IPlatformPermissionProvider.
        public required string PermissionScope { get; init; }

        // TM-2 compatibility flag: whether this code appears in the record-picker type list.
        // "Supplier" is searchable but was never offered by the picker (and therefore never resolvable)
        // in the pre-kernel TaskLinkResolver; this flag preserves that exact behaviour.
        public bool ListedInRecordPicker { get; init; }
    }

    // A resolved pointer to one business record: the (code, id) pair plus its display label.
    public sealed class EntityReference
    {
        public required string EntityCode { get; init; }
        public required int EntityId { get; init; }
        public required string Label { get; init; }
    }

    // One row of a registry-driven record search.
    public sealed class EntitySearchResult
    {
        public required string EntityCode { get; init; }
        public required int EntityId { get; init; }
        public required string Label { get; init; }
        public string? Url { get; init; }
    }

    // The outcome of resolving (code, id) to a displayable, navigable record.
    // Found = false means the row is gone (deleted or another company) — the label degrades to "#id"
    // and Url is null, exactly as the pre-kernel resolver behaved.
    public sealed class EntityResolveResult
    {
        public required string EntityCode { get; init; }
        public required int EntityId { get; init; }
        public required string Label { get; init; }
        public string? Url { get; init; }
        public required bool Found { get; init; }
        public required EntityDefinition Definition { get; init; }
    }
}