using CrossBuy.Models.Platform;
using Microsoft.Extensions.Logging;

namespace CrossBuy.BL.Platform
{
    // Stage 1 Batch C — the mandatory gates every new module access service must apply, written ONCE.
    //
    // C1 lists eleven requirements (missing context denies, unknown action denies, company mismatch denies
    // before role checks, worker is not employee access, system follows SystemContextPolicy, …). Four
    // services implementing them independently is four chances to get one wrong, and the four existing
    // services show what that looks like: each re-implements its own bootstrap probe and its own role read.
    //
    // Subclasses supply the module's vocabulary and its rules. They never re-check the gates.
    //
    // Deliberately NOT applied to the four EXISTING services (Accounting, Inventory, CRM, POS): retrofitting
    // them is a change to working authorization and belongs to the batch that folds in their role tables.
    public abstract class ModuleAccessServiceBase : IModuleAccessService
    {
        private readonly IPlatformRoleDirectory _roles;
        private readonly ILogger _log;

        protected ModuleAccessServiceBase(IPlatformRoleDirectory roles, ILogger log)
        { _roles = roles; _log = log; }

        // Exposed so a subclass's set-shaped method (ResolveScopeAsync) can ask the SAME directory the yes/no
        // path uses, rather than taking its own dependency and risking a second read policy.
        protected IPlatformRoleDirectory RoleDirectory => _roles;

        public abstract string Scope { get; }
        public abstract IReadOnlyCollection<string> Actions { get; }

        // The module's own rule, reached ONLY after every gate below has passed. `grants` is already
        // company-intersected, active and in-date; `bootstrapOpen` says no role is configured for this
        // company and scope yet.
        protected abstract Task<bool> EvaluateAsync(
            BusinessContext context, string action, PermissionTarget? target,
            IReadOnlyList<RoleGrant> grants, bool bootstrapOpen, CancellationToken cancellationToken);

        public async Task<bool> CanAsync(
            BusinessContext context, string action, PermissionTarget? target = null,
            CancellationToken cancellationToken = default)
        {
            // ---- gate 1: a context, or nothing. Missing context DENIES; it is never "unrestricted". ----
            if (context == null) return false;

            // ---- gate 2: the action must be one this module defines. Unknown ⇒ deny, never fall through. ----
            if (string.IsNullOrWhiteSpace(action) || !Actions.Contains(action, StringComparer.Ordinal))
            {
                _log.LogWarning(
                    "{Scope}: unknown action '{Action}' denied. Known actions: {Known}.",
                    Scope, action, string.Join(", ", Actions));
                return false;
            }

            // ---- gate 3: a real company. There is no default company. ----
            if (context.CompanyId <= 0) return false;

            // ---- gate 4: company mismatch denies BEFORE any role or record rule is consulted. ----
            // A target naming another company is refused outright — the caller's company decides, never the
            // target's, and this ordering is why a tampered id cannot reach a record rule at all.
            if (target?.CompanyId is > 0 && target.CompanyId.Value != context.CompanyId)
            {
                _log.LogWarning(
                    "{Scope}: '{Action}' denied — target names company {Target} but the caller resolves to {Caller}.",
                    Scope, action, target.CompanyId, context.CompanyId);
                return false;
            }

            // ---- gate 5: a System context is governed, not trusted. ----
            // SystemContextPolicy grants a NARROW list (View only). A system context asking for a module
            // action is asking for something the policy does not cover, so it is refused rather than being
            // handed the module's own decision — trusted platform code has no employee whose roles to read.
            if (context.IsSystem)
            {
                bool allowed = SystemContextPolicy.Allows(action);
                if (!allowed)
                    _log.LogDebug(
                        "{Scope}: system context denied '{Action}' — SystemContextPolicy allows only {Allowed}.",
                        Scope, action, string.Join(", ", SystemContextPolicy.AllowedActions));
                return allowed;
            }

            // ---- gate 6: a Worker context is not employee access. ----
            // A worker names the company it processes but has NO employee, so it holds no role and no
            // record-level relationship. It is refused here rather than reaching EvaluateAsync and being
            // accidentally allowed by a rule that only checks the company.
            if (context.Source == BusinessContextSource.Worker)
            {
                _log.LogDebug(
                    "{Scope}: worker context denied '{Action}' — a worker holds no employee identity, so it holds no role.",
                    Scope, action);
                return false;
            }

            // ---- gate 7: an employee identity, for everything else. ----
            if (context.EmployeeId is not > 0)
            {
                _log.LogDebug("{Scope}: '{Action}' denied — no resolved employee.", Scope, action);
                return false;
            }

            cancellationToken.ThrowIfCancellationRequested();

            // ---- the module's own decision ----
            var grants = await _roles.RolesAsync(context, Scope, cancellationToken);
            bool bootstrapOpen = grants.Count == 0
                && !await _roles.AnyConfiguredAsync(context.CompanyId, Scope, cancellationToken);

            if (bootstrapOpen)
                // Logged every time, at Information, because it is TEMPORARY COMPATIBILITY and not the
                // security destination. A quiet bootstrap-open is indistinguishable from a working policy.
                _log.LogInformation(
                    "{Scope}: no role assignment configured for company {Company} — the module is BOOTSTRAP-OPEN " +
                    "for this company and scope. This is Batch C compatibility, not the final policy.",
                    Scope, context.CompanyId);

            return await EvaluateAsync(context, action, target, grants, bootstrapOpen, cancellationToken);
        }

        public async Task<IReadOnlyList<string>> RolesAsync(
            BusinessContext context, CancellationToken cancellationToken = default)
        {
            if (context == null || context.CompanyId <= 0 || context.EmployeeId is not > 0)
                return Array.Empty<string>();
            var grants = await _roles.RolesAsync(context, Scope, cancellationToken);
            return grants.Select(g => g.Role).Distinct(StringComparer.Ordinal).ToList();
        }

        // ---- helpers subclasses use, so the same phrasing is not rewritten four times ----

        protected static bool Holds(IReadOnlyList<RoleGrant> grants, params string[] roles)
            => grants.Any(g => roles.Contains(g.Role, StringComparer.Ordinal));

        // A branch-scoped grant only counts inside its branch; a company-wide grant (null) counts anywhere.
        protected static bool HoldsInBranch(IReadOnlyList<RoleGrant> grants, int? branchId, params string[] roles)
            => grants.Any(g => roles.Contains(g.Role, StringComparer.Ordinal)
                            && (g.ScopeBranchId == null || (branchId.HasValue && g.ScopeBranchId == branchId.Value)));
    }
}
