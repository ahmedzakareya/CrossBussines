namespace CrossBuy.Models.Platform
{
    // Platform Kernel (PKS-001) — one rendered row of a business object's timeline.
    //
    // The UI NEVER queries BusinessEvents: it asks ITimelineProjectionService, which authorizes, filters
    // by visibility, merges the legacy adapter and produces these rows. The contract is deliberately a
    // projection (not the event itself) so a later persisted projection table changes nothing above it.
    public sealed class TimelineItemViewModel
    {
        public required Guid EventUid { get; init; }
        public required string EntityType { get; init; }
        public required int EntityId { get; init; }
        public required string EventType { get; init; }

        public required string TitleAr { get; init; }
        public required string TitleEn { get; init; }
        public string? DescriptionAr { get; init; }
        public string? DescriptionEn { get; init; }

        public int? ActorEmployeeId { get; init; }
        public string? ActorDisplayName { get; init; }

        // From EntityDefinition / the event-type presenter — Metronic KI icon + contextual colour.
        public required string Icon { get; init; }
        public required string Color { get; init; }

        public required DateTime CreatedAt { get; init; }
        public required int PayloadVersion { get; init; }
        public required string Visibility { get; init; }

        // Deep link built through IEntityRegistry.BuildUrl. null when the type has no screen.
        public string? Url { get; init; }

        // "Event" = a real BusinessEvent row. "Legacy" = reconstructed by the legacy adapter for records
        // that predate the kernel (PKS-001 "Legacy timeline compatibility"). Lets the UI mark provenance
        // and lets the projection deduplicate a legacy item away once a real event covers it.
        public required TimelineItemSource Source { get; init; }
    }

    public enum TimelineItemSource
    {
        Event = 0,
        Legacy = 1,
    }
}