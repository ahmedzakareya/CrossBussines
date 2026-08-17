namespace CrossBuy.Models.Platform
{
    // Platform Kernel slice 2 (ADR-006) — one notification a BusinessEvent asks for.
    //
    // The mapper returns these; the consumer resolves recipients, authorizes them and delivers. Keeping the
    // decision (what to say, to whom, about what) separate from the delivery (who is allowed, has it already
    // been sent, how is it pushed) is what keeps the mapper free of permission and idempotency logic.
    public sealed class NotificationCommand
    {
        // Exactly ONE targeting mode must be set.
        // A single employee — used when the event names its recipient.
        public int? RecipientEmployeeId { get; init; }

        // A role audience, resolved through the EXISTING NotificationService.NotifyRoleAsync scopes.
        // RecipientScope is "acc" (AccountingUserRoles) or "inv" (InventoryUserRoles) — those are the only
        // two the notification service supports, so no other value is accepted.
        public string? RecipientScope { get; init; }
        public string[]? RecipientRoles { get; init; }

        public required int CompanyId { get; init; }
        public int? BranchId { get; init; }

        public required string TitleAr { get; init; }
        public required string TitleEn { get; init; }
        public required string MessageAr { get; init; }
        public required string MessageEn { get; init; }

        // NotificationTypes catalog key — reused, not replaced, so history keeps rendering.
        public required string Type { get; init; }

        // Module bucket + priority. Null lets the NotificationTypes catalog decide, which is the existing
        // behaviour for every legacy producer.
        public string? Category { get; init; }
        public string? Priority { get; init; }

        // Canonical registry code + record id. The consumer builds Url from these via IEntityRegistry.
        public required string EntityType { get; init; }
        public required int EntityId { get; init; }

        public int? ActorEmployeeId { get; init; }

        // Deterministic per (event, recipient). Derived from BusinessEvent.EventUid by the consumer, so a
        // retried dispatch cannot produce a second notification.
        public required string DedupKeyPrefix { get; init; }

        // True when the actor must not be notified about their own action. Mirrors the exceptEmployeeId
        // parameter the existing NotifyRoleAsync already takes.
        public bool ExcludeActor { get; init; } = true;

        public bool IsRoleTargeted => !string.IsNullOrEmpty(RecipientScope) && RecipientRoles is { Length: > 0 };
    }
}
