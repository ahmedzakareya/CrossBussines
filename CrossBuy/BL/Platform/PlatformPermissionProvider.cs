using CrossBuy.BL;
using CrossBuy.Models.Platform;
using Microsoft.Extensions.Logging;

namespace CrossBuy.BL.Platform
{
    // Platform Kernel (PKS-001 "Authorization pipeline") — the ONE gate every platform service calls
    // before it returns entity data.
    //
    // It owns NO policy. Policy lives in the four module access services, and duplicating any of it here
    // would create a second authorization source that drifts. The provider:
    //   1. rejects unregistered entity types,
    //   2. applies the SYSTEM-CONTEXT POLICY (Stage 1 — no longer a blanket allow),
    //   3. enforces COMPANY ISOLATION before any module permission is consulted (Stage 1),
    //   4. resolves the entity's PermissionScope from IEntityRegistry, and
    //   5. translates the canonical platform action into that module's own action vocabulary.
    //
    // STAGE 1 BATCH A — the two changes that matter:
    //
    // (a) The adapters now PASS request.Context to the module. Before Stage 1 four of the six adapters
    //     called `_access.CanAsync(moduleAction)`, which read the current employee from Session["Employee"]
    //     and the company from a `const int CompanyId = 1`. The BusinessContext they were handed was
    //     discarded, so inside a background worker the answer was not specific to the employee being
    //     checked — which is exactly why NotificationProjectionConsumer's per-recipient loop carried a
    //     14-line "this is not really per-recipient" disclaimer. It is now real.
    //
    // (b) `if (context.IsSystem) return Allow(...)` is gone. See SystemContextPolicy for the replacement and
    //     the reasoning: a single boolean must not be able to bypass every module check for every action.
    public interface IPlatformPermissionProvider
    {
        Task<PermissionDecision> CanAsync(
            BusinessContext context,
            string entityType,
            int entityId,
            string action,
            CancellationToken cancellationToken = default);
    }

    // One module's translation layer. Implementations wrap an EXISTING access service and are not allowed
    // to add rules of their own.
    public interface IModulePermissionAdapter
    {
        // Matches EntityDefinition.PermissionScope.
        string Scope { get; }

        Task<PermissionDecision> CanAsync(PermissionCheckRequest request, CancellationToken cancellationToken = default);
    }

    public class PlatformPermissionProvider : IPlatformPermissionProvider
    {
        private readonly IEntityRegistry _registry;
        private readonly IEnumerable<IModulePermissionAdapter> _adapters;
        private readonly ILogger<PlatformPermissionProvider> _log;

        // Memoises "does (type, id) exist in company C" for the lifetime of this scope. The provider is
        // Scoped, so this cannot leak between requests or dispatch passes. Only EXISTENCE is cached, never a
        // permission decision — and the notification consumer checks one entity against many recipients, so
        // without this the company check would be an extra query per recipient.
        private readonly Dictionary<(string type, int id, int company), bool> _companyMatch = new();

        public PlatformPermissionProvider(
            IEntityRegistry registry, IEnumerable<IModulePermissionAdapter> adapters,
            ILogger<PlatformPermissionProvider> log)
        { _registry = registry; _adapters = adapters; _log = log; }

        public async Task<PermissionDecision> CanAsync(
            BusinessContext context, string entityType, int entityId, string action, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);

            if (!_registry.TryGetDefinition(entityType, out var definition))
                return PermissionDecision.Deny($"Entity code '{entityType}' is not registered.");

            // ---- 1. system context: a NARROW, declared allow-list, not a bypass ----
            if (context.IsSystem)
            {
                if (!SystemContextPolicy.Allows(action))
                {
                    // Logged at Warning: a system context asking for an elevated action means some consumer
                    // needs a grant it does not have, and that is a design question, not noise.
                    _log.LogWarning(
                        "System context denied '{Action}' on {Entity}/{Id}: {Reason}",
                        action, definition!.Code, entityId, SystemContextPolicy.DenyReason(action));
                    return PermissionDecision.Deny(SystemContextPolicy.DenyReason(action));
                }
                // Company isolation still applies to a system context: it is scoped to ONE company by its
                // caller, and reading another company's record would be a tenancy breach regardless of trust.
                if (!await BelongsToCompanyAsync(definition!.Code, entityId, context, cancellationToken))
                    return PermissionDecision.Deny("Record is not in this system context's company.");

                return PermissionDecision.Allow($"system context ({action})");
            }

            if (!context.IsAuthenticated)
                return PermissionDecision.Deny("No authenticated user in the business context.");

            // ---- 2. company isolation, BEFORE module permission ----
            // Ordering is deliberate and required by the Stage 1 contract: a module role must never be able to
            // reach across companies. A ChiefAccountant in company 2 holding "manage" would otherwise be
            // granted by the adapter for a company-1 invoice, because the module's role check knows nothing
            // about which record is being asked about.
            if (!await BelongsToCompanyAsync(definition!.Code, entityId, context, cancellationToken))
                return PermissionDecision.Deny(
                    $"{definition.Code}/{entityId} is not in company {context.CompanyId}.");

            // ---- 3. module permission ----
            var adapter = _adapters.FirstOrDefault(a => string.Equals(a.Scope, definition.PermissionScope, StringComparison.Ordinal));
            if (adapter == null)
                return PermissionDecision.Deny($"No permission adapter is registered for scope '{definition.PermissionScope}'.");

            return await adapter.CanAsync(new PermissionCheckRequest
            {
                Context = context, EntityType = definition.Code, EntityId = entityId, Action = action,
            }, cancellationToken);
        }

        // Uses the registry's own company-scoped resolution — the same query that powers the record picker,
        // so there is no second definition of "which company owns this record". Found = false means the row
        // does not exist in the caller's company, which is the answer we want for BOTH "missing" and
        // "belongs to someone else": the caller must not be able to tell those apart.
        private async Task<bool> BelongsToCompanyAsync(
            string entityCode, int entityId, BusinessContext context, CancellationToken cancellationToken)
        {
            if (entityId <= 0) return false;

            var key = (entityCode, entityId, context.CompanyId);
            if (_companyMatch.TryGetValue(key, out var cached)) return cached;

            var resolved = await _registry.ResolveAsync(entityCode, entityId, context, cancellationToken);
            _companyMatch[key] = resolved.Found;
            return resolved.Found;
        }
    }

    // -------------------------------------------------------------------------------------------------
    // Adapters. Each maps the three canonical platform actions onto the action strings its module already
    // understands, and each now passes the BusinessContext through so the module answers about the
    // context's employee and company rather than about the current browser session.
    //
    // Row-level scope: Accounting has none. Inventory has warehouse scope and CRM has record ownership; both
    // are reachable through PermissionTarget, and the adapters supply the target when the platform action
    // implies one. The platform's three View tiers carry no warehouse or owner, so nothing is fabricated
    // here — a caller that needs row-level scope calls the module's canonical CanAsync with a target.
    // -------------------------------------------------------------------------------------------------

    // Shared plumbing. Every adapter resolves its module through IModuleAccessService BY SCOPE rather than by
    // casting a legacy interface to its concrete class — a cast would compile and then fail at runtime the
    // first time anyone decorated or mocked the service.
    //
    // A missing module service DENIES. That is the A5 rule "missing adapters deny access rather than allowing
    // it silently", applied one level deeper: an adapter whose module is not registered is just as broken as a
    // missing adapter, and must fail the same way.
    public abstract class ModulePermissionAdapterBase : IModulePermissionAdapter
    {
        private readonly IEnumerable<IModuleAccessService> _modules;
        protected ModulePermissionAdapterBase(IEnumerable<IModuleAccessService> modules) { _modules = modules; }

        public abstract string Scope { get; }

        // The module whose policy this adapter delegates to. Usually the same string as Scope; the
        // manufacturing adapter deliberately points at the inventory module.
        protected abstract string ModuleScope { get; }

        // Canonical platform action → this module's own action string. null ⇒ unknown ⇒ deny.
        protected abstract string? MapAction(string platformAction);

        // Label used in the decision reason, so a log line names the module that decided.
        protected abstract string ReasonPrefix { get; }

        // Stage 1 Batch C — the record-level target this adapter passes to its module.
        //
        // THE DEFECT THIS FIXES: the base used to pass `target: null` unconditionally, so a record-level rule
        // could never be reached through IPlatformPermissionProvider. A module could implement per-record
        // access and the platform would still ask it a module-level question.
        //
        // The default returns null, so every EXISTING adapter (Accounting, Inventory, CRM, POS, Manufacturing)
        // behaves exactly as before — asserted by a regression test per adapter. An adapter that has a
        // record-level rule overrides this ONE method; the forwarding itself is not duplicated anywhere.
        protected virtual PermissionTarget? BuildTarget(PermissionCheckRequest request) => null;

        public async Task<PermissionDecision> CanAsync(PermissionCheckRequest request, CancellationToken cancellationToken = default)
        {
            var moduleAction = MapAction(request.Action);
            if (moduleAction == null) return PermissionDecision.Deny($"Unknown platform action '{request.Action}'.");

            var module = _modules.FirstOrDefault(m => string.Equals(m.Scope, ModuleScope, StringComparison.Ordinal));
            if (module == null)
                return PermissionDecision.Deny(
                    $"No IModuleAccessService is registered for scope '{ModuleScope}', so '{request.Action}' cannot be granted.");

            PermissionTarget? target;
            try
            {
                target = BuildTarget(request);
            }
            catch (Exception ex)
            {
                // A malformed target DENIES rather than degrading to a module-level check. Falling back to
                // `null` here would silently turn a record-level question into a broader one — the failure
                // mode most likely to look like it worked.
                return PermissionDecision.Deny(
                    $"{ReasonPrefix}: the record-level target could not be built ({ex.GetType().Name}), so the " +
                    "request is denied rather than evaluated without it.");
            }

            // Company isolation runs BEFORE the record rule: a target naming another company is refused by the
            // module's own first gate (ModuleAccessServiceBase gate 4), which is why the target carries the
            // company rather than the adapter re-checking it here.
            return await module.CanAsync(request.Context, moduleAction, target, cancellationToken)
                ? PermissionDecision.Allow($"{ReasonPrefix}:{moduleAction}")
                : PermissionDecision.Deny($"{ReasonPrefix}:{moduleAction} denied");
        }
    }

    // The ten per-module adapters that used to sit here now live in
    // BL/ModulePermissions/ModulePermissionAdapters.cs. They are module code — the calendar adapter names its own module's action constants — and keeping them in a kernel file is what made the Platform Kernel
    // un-committable on its own. They were moved verbatim; the kernel still consumes them only through
    // IModulePermissionAdapter above, which is unchanged.


    // Scope "None" — entities whose module has no access service yet (HR, Projects, Tasks).
    //
    // This is deliberately the WEAKEST adapter and it says so: it grants View to an authenticated user whose
    // context already passed the company-isolation check in the provider, and refuses every elevated action,
    // because there is no module policy to delegate to. Inventing thresholds here would be a second
    // authorization source.
    //
    // Stage 1 BATCH C is the real fix: HR, Projects and Tasks/Communication access services. 132 of the
    // corrected 189-action security backlog sit in those three modules precisely because they have no access
    // service at all — see CORRECTION-003.
    public class DefaultPermissionAdapter : IModulePermissionAdapter
    {
        public string Scope => EntityRegistry.ScopeNone;

        public Task<PermissionDecision> CanAsync(PermissionCheckRequest request, CancellationToken cancellationToken = default)
        {
            var decision = request.Action switch
            {
                PlatformActions.View => PermissionDecision.Allow("authenticated, company-scoped"),
                _ => PermissionDecision.Deny($"No module access service exists for '{request.EntityType}', so '{request.Action}' cannot be granted."),
            };
            return Task.FromResult(decision);
        }
    }
}