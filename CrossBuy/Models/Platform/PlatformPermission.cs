namespace CrossBuy.Models.Platform
{
    // Platform Kernel (PKS-001 "Authorization pipeline") — the ONE authorization abstraction every
    // platform service calls before returning entity data. It does not implement policy: it routes to
    // the module access service that already owns that policy (Accounting / Inventory / Crm / Pos).
    // Timeline uses it in this slice; AI Context, Search, Files, Relations and Followers will use the
    // same abstraction unchanged.

    // Canonical platform actions. Each module adapter maps these onto its own action vocabulary
    // (AccountingAccessService.CanAsync takes read | post | pay | manage | currency-override).
    public static class PlatformActions
    {
        // May open the record and read its Internal-visibility history.
        public const string View = "View";

        // May read Confidential-visibility events on the record (cost, margin, credit decisions).
        public const string ViewConfidential = "ViewConfidential";

        // May read Restricted- and System-visibility events (module manager).
        public const string ViewRestricted = "ViewRestricted";
    }

    public sealed class PermissionCheckRequest
    {
        public required BusinessContext Context { get; init; }
        public required string EntityType { get; init; }
        public required int EntityId { get; init; }
        public required string Action { get; init; }
    }

    public sealed class PermissionDecision
    {
        public required bool Allowed { get; init; }

        // Why it was denied (or which module granted it) — for logging, never shown raw to end users.
        public string? Reason { get; init; }

        public static PermissionDecision Allow(string? reason = null) => new() { Allowed = true, Reason = reason };
        public static PermissionDecision Deny(string reason) => new() { Allowed = false, Reason = reason };
    }

    // Thrown when a platform service is asked for data the caller may not see. Distinct from an empty
    // result: "you may not look at this invoice" is not the same answer as "this invoice has no history",
    // and a controller must be able to return 403 rather than an empty widget.
    public sealed class PlatformAccessDeniedException : Exception
    {
        public PlatformAccessDeniedException(string entityType, int entityId, string action, string? reason)
            : base($"Access denied: '{action}' on {entityType}/{entityId}" + (reason == null ? "." : $" ({reason})."))
        { EntityType = entityType; EntityId = entityId; Action = action; }

        public string EntityType { get; }
        public int EntityId { get; }
        public string Action { get; }
    }
}