using System.Security.Cryptography;
using System.Text;
using CrossBuy.Models.Platform;

namespace CrossBuy.BL.Platform
{
    // Platform Kernel slice 2 — shared helpers for the legacy timeline adapters (ADR-005).
    //
    // Slice 1 had one adapter, so its deterministic-uid and item-building logic lived inside it. With four
    // adapters that logic is lifted here rather than copied: identity MUST be derived the same way in every
    // adapter, because the projection's deduplication and the UI's row identity both depend on it.
    internal static class LegacyTimelineSupport
    {
        // Stable name -> Guid. MD5 is used purely to derive a deterministic identifier, never for security.
        // A random Guid here would make every page refresh look like new activity.
        public static Guid DeterministicUid(string entityCode, int entityId, string eventType, string? discriminator = null)
        {
            var key = $"legacy:{entityCode}:{entityId}:{eventType}:{discriminator ?? ""}";
            return new Guid(MD5.HashData(Encoding.UTF8.GetBytes(key)));
        }

        // Builds one reconstructed row. Legacy items never claim an actor: pre-kernel data does not record who
        // caused these facts, and inventing one would be a fabrication.
        public static TimelineItemViewModel Item(
            string entityCode, int entityId, string eventType, DateTime createdAt,
            string titleAr, string titleEn, string descriptionAr, string descriptionEn,
            string icon, string color, string? url, int payloadVersion,
            string? uidDiscriminator = null, int? actorEmployeeId = null, string? actorDisplayName = null)
            => new()
            {
                EventUid = DeterministicUid(entityCode, entityId, eventType, uidDiscriminator),
                EntityType = entityCode,
                EntityId = entityId,
                EventType = eventType,
                TitleAr = titleAr, TitleEn = titleEn,
                DescriptionAr = descriptionAr, DescriptionEn = descriptionEn,
                ActorEmployeeId = actorEmployeeId,
                ActorDisplayName = actorDisplayName,
                Icon = icon, Color = color,
                CreatedAt = createdAt,
                PayloadVersion = payloadVersion,
                // Reconstructed history carries no classification of its own; Internal is the only honest
                // value, and it is the tier every user who may view the entity can already read.
                Visibility = BusinessEventVisibility.Internal,
                Url = url,
                Source = TimelineItemSource.Legacy,
            };

        // Arabic marker appended to every reconstructed description so a reader can tell derived history from
        // a recorded event even without the UI badge.
        public const string HistoricalAr = " (سجل تاريخي)";
        public const string HistoricalEn = " (historical record)";
    }
}
