using CrossBuy.Models.Platform;

namespace CrossBuy.BL.Platform
{
    // Stage 1 Batch A — the canonical, session-free permission contract.
    //
    // Every module access service implements this IN ADDITION to its existing interface. The existing
    // interfaces and their method signatures are untouched, so all 153 [SessionValidation] usages, 168
    // permission attributes and 34 CanAsync call sites keep working exactly as before. What changes is what
    // sits underneath: the legacy method now resolves a BusinessContext and delegates here, so there is ONE
    // permission engine rather than two.
    //
    // THE RULE THIS EXISTS TO ENFORCE: no implementation of CanAsync(BusinessContext, …) may read
    // HttpContext, Session, claims, or any ambient current-user state. The context is the input. That is what
    // makes the same check usable by a dispatcher, a workflow step, an AI retrieval and a controller.
    public interface IModuleAccessService
    {
        // The module's identity, matching EntityDefinition.PermissionScope (e.g. "Accounting").
        string Scope { get; }

        // The module's own action vocabulary — the strings CanAsync accepts. Published so an adapter can
        // validate a mapping instead of discovering an unknown action at runtime, and so the vocabulary is
        // documentable rather than folk knowledge.
        IReadOnlyCollection<string> Actions { get; }

        // Deny-by-default. An unresolved identity, an unknown action, or a target in another company all
        // return false. Never throws for an authorization question — a throw would be indistinguishable
        // from an infrastructure failure at the call site.
        Task<bool> CanAsync(
            BusinessContext context,
            string action,
            PermissionTarget? target = null,
            CancellationToken cancellationToken = default);

        // The employee's module roles for a context, session-free. Used by role labels and by the modules'
        // own row-level rules.
        Task<IReadOnlyList<string>> RolesAsync(BusinessContext context, CancellationToken cancellationToken = default);
    }

    // Stage 1 Batch A — what a SYSTEM context may do.
    //
    // REPLACES a blanket allow. Before Stage 1, PlatformPermissionProvider began with:
    //
    //     if (context.IsSystem) return PermissionDecision.Allow("system context");
    //
    // which meant any caller that set IsSystem bypassed every module permission check for every action,
    // including ViewRestricted. That was tolerable while only the outbox dispatcher constructed system
    // contexts; it becomes the single highest-value bypass in the platform the moment Workflow, Search, AI
    // Context or the Workspace reuse the provider — which is exactly what Stage 1 is the foundation for.
    //
    // The governed policy is a short, explicit allow-list. Widening it is a visible code change with a
    // reason attached, not an accident of a boolean.
    public static class SystemContextPolicy
    {
        // The ONLY platform actions a system context may perform without an interactive identity.
        //
        // View is here because trusted platform code must be able to read a record's existence and its
        // Internal-visibility history to project it (the timeline consumer, the registry's own resolution).
        // The elevated tiers are NOT here: a machine reading Confidential cost data or Restricted manager
        // history has to be granted that explicitly, per consumer, when a real consumer needs it.
        public static readonly IReadOnlyList<string> AllowedActions = new[] { PlatformActions.View };

        public static bool Allows(string action)
            => AllowedActions.Contains(action, StringComparer.Ordinal);

        // Human-readable reason, used in the PermissionDecision so a denial is diagnosable from a log line.
        public static string DenyReason(string action)
            => $"A system context may perform only [{string.Join(", ", AllowedActions)}]; '{action}' requires an " +
               "interactive identity or an explicit grant (see SystemContextPolicy).";
    }
}