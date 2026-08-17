using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Platform;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CrossBuy.BL.Platform
{
    // Stage 1 Batch C — the ONE read path for module role assignments.
    //
    // WHY THIS EXISTS RATHER THAN FOUR SERVICES QUERYING A DbSet
    //
    // Three things have to be true of every role read, and each of them was a separate mistake waiting to be
    // made four times:
    //   1. it is COMPANY-INTERSECTED — a grant in company 2 must never answer a company-1 question;
    //   2. it honours IsActive and the ValidFrom/ValidTo window against ONE clock;
    //   3. its Scope is a known scope — a typo must FAIL, not quietly match no rows.
    // Putting them here means they are written once and tested once. It is also what makes folding the four
    // legacy role tables in later a DATA move instead of a code change: the access services never learn
    // which table their grants came from.
    //
    // The four new access services depend on this interface and MUST NOT touch
    // CrossDbContext.PlatformRoleAssignments directly.
    public interface IPlatformRoleDirectory
    {
        // The principal's active, in-date grants in ONE module scope, for the context's company.
        // Returns empty when the context has no employee, when the principal kind is unsupported, or when
        // nothing is granted. Throws only for a programming error (an unknown scope).
        Task<IReadOnlyList<RoleGrant>> RolesAsync(
            BusinessContext context, string scope, CancellationToken cancellationToken = default);

        // The bootstrap-open probe: has ANY assignment been configured for this company and scope?
        // Per company AND per scope, so configuring HR in company 1 does not close HR in company 2, and does
        // not close Projects in company 1 either.
        Task<bool> AnyConfiguredAsync(int companyId, string scope, CancellationToken cancellationToken = default);
    }

    // One grant. ScopeBranchId is null for a company-wide grant.
    public sealed record RoleGrant(string Role, int? ScopeBranchId);

    public sealed class PlatformRoleDirectory : IPlatformRoleDirectory
    {
        private readonly CrossDbContext _db;
        private readonly ILogger<PlatformRoleDirectory> _log;

        public PlatformRoleDirectory(CrossDbContext db, ILogger<PlatformRoleDirectory> log)
        { _db = db; _log = log; }

        // ONE clock policy for the whole directory: UTC, read once per call so every grant in a single
        // decision is evaluated against the same instant. Two grants in one decision must not straddle a
        // tick — that is how an "expired at exactly now" grant becomes non-deterministic.
        private static DateTime UtcNow() => DateTime.UtcNow;

        public async Task<IReadOnlyList<RoleGrant>> RolesAsync(
            BusinessContext context, string scope, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);
            RequireKnownScope(scope);

            // No resolved employee ⇒ no grants. Not an error: an anonymous or unresolved caller simply holds
            // nothing, and the access service turns that into a deny.
            if (context.EmployeeId is not > 0) return Array.Empty<RoleGrant>();

            // There is no default company. A context without one cannot be answered.
            if (context.CompanyId <= 0) return Array.Empty<RoleGrant>();

            var now = UtcNow();

            // The company predicate is FIRST and explicit. PlatformRoleAssignments is not a B2 pilot entity,
            // so no query filter protects it — this predicate is the isolation, not a second line of defence.
            var rows = await _db.PlatformRoleAssignments.AsNoTracking()
                .Where(r => r.CompanyID == context.CompanyId
                         && r.Scope == scope
                         && r.PrincipalType == PlatformPrincipalTypes.Employee
                         && r.PrincipalId == context.EmployeeId.Value
                         && r.IsActive
                         && (r.ValidFrom == null || r.ValidFrom <= now)
                         && (r.ValidTo == null || r.ValidTo >= now))
                .Select(r => new { r.Role, r.ScopeBranchId })
                .ToListAsync(cancellationToken);

            // PrincipalType is filtered in SQL to Employee — the only SUPPORTED kind in Batch C. A row for a
            // CustomerContact is therefore not returned rather than being evaluated by a service that has no
            // rule for it. That is the deny-by-omission the design intends, and it is asserted by a test so
            // the day a portal principal is supported, the change is deliberate.
            return rows.Select(r => new RoleGrant(r.Role, r.ScopeBranchId)).ToList();
        }

        public async Task<bool> AnyConfiguredAsync(
            int companyId, string scope, CancellationToken cancellationToken = default)
        {
            RequireKnownScope(scope);
            if (companyId <= 0) return false;

            var now = UtcNow();

            // Deliberately counts only grants that could actually decide something: active and in-date. A
            // company whose only HR grant expired last year is NOT "configured" — it is back to
            // bootstrap-open, which is the honest reading. The alternative (any row ever) would lock a
            // module permanently on the strength of a lapsed assignment.
            return await _db.PlatformRoleAssignments.AsNoTracking()
                .AnyAsync(r => r.CompanyID == companyId
                            && r.Scope == scope
                            && r.IsActive
                            && (r.ValidFrom == null || r.ValidFrom <= now)
                            && (r.ValidTo == null || r.ValidTo >= now), cancellationToken);
        }

        // An unknown scope is a PROGRAMMING error, not a permission outcome. Returning "no grants" would be
        // indistinguishable from a correctly-spelled scope with nothing configured — and under bootstrap-open
        // that reads as "leave the module wide open", which is the worst possible interpretation of a typo.
        private void RequireKnownScope(string scope)
        {
            if (EntityRegistry.IsKnownScope(scope)) return;

            _log.LogError(
                "Unknown permission scope '{Scope}' was asked of the role directory. Known scopes: {Known}.",
                scope, string.Join(", ", EntityRegistry.PermissionScopes));
            throw new ArgumentException(
                $"'{scope}' is not a known permission scope. Add it to EntityRegistry.PermissionScopes, or fix " +
                "the caller — an unknown scope must not be answered as 'nothing granted'.", nameof(scope));
        }
    }

    // Startup validation, so a scope mismatch is a boot failure rather than a runtime surprise.
    //
    // It asserts two things the whole design depends on:
    //   1. every registered IModuleAccessService.Scope is a KNOWN scope — otherwise its role rows could never
    //      be found and the module would sit silently bootstrap-open forever;
    //   2. no two modules claim the same scope string — otherwise `FirstOrDefault(m => m.Scope == …)` in the
    //      adapter base would resolve to whichever happened to be registered first.
    public sealed class PermissionScopeStartupValidator : Microsoft.Extensions.Hosting.IHostedService
    {
        // IServiceScopeFactory, NOT IEnumerable<IModuleAccessService>.
        //
        // A hosted service is a SINGLETON and the module access services are SCOPED (they depend on
        // CrossDbContext). Injecting them directly made the container refuse to build:
        //
        //   AggregateException: Some services are not able to be constructed …
        //   Cannot consume scoped service 'IEnumerable<IModuleAccessService>' from singleton
        //   'IHostedService/PermissionScopeStartupValidator'
        //
        // …which is the very rule this project already learned the hard way and wrote down: never capture a
        // scoped service from a singleton. A scope is created per validation run, exactly as
        // BusinessEventDispatchWorker creates one per batch.
        private readonly IServiceScopeFactory _scopes;
        private readonly ILogger<PermissionScopeStartupValidator> _log;

        public PermissionScopeStartupValidator(
            IServiceScopeFactory scopes, ILogger<PermissionScopeStartupValidator> log)
        { _scopes = scopes; _log = log; }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            using var scope = _scopes.CreateScope();
            var modules = scope.ServiceProvider.GetServices<IModuleAccessService>();
            return ValidateAsync(modules);
        }

        // Separated from StartAsync so the rule can be tested without standing up a DI container — the
        // validation logic is what matters, and it must not need a scope factory to be exercised.
        public Task ValidateAsync(IEnumerable<IModuleAccessService> modules)
        {
            var scopes = modules.Select(m => m.Scope).ToList();

            var unknown = scopes.Where(s => !EntityRegistry.IsKnownScope(s)).Distinct(StringComparer.Ordinal).ToList();
            if (unknown.Count > 0)
                throw new InvalidOperationException(
                    "These IModuleAccessService scopes are not in EntityRegistry.PermissionScopes: " +
                    string.Join(", ", unknown) +
                    ". A module whose scope is unknown can never match a role assignment, so it would stay " +
                    "bootstrap-open silently. Register the scope or fix the service.");

            var duplicates = scopes.GroupBy(s => s, StringComparer.Ordinal)
                .Where(g => g.Count() > 1).Select(g => g.Key).ToList();
            if (duplicates.Count > 0)
                throw new InvalidOperationException(
                    "Two IModuleAccessService implementations claim the same scope: " +
                    string.Join(", ", duplicates) +
                    ". Scope resolution is by first match, so one module's policy would silently answer for " +
                    "the other.");

            _log.LogInformation(
                "Permission scopes validated at startup: {Count} module access service(s) — {Scopes}.",
                scopes.Count, string.Join(", ", scopes.OrderBy(s => s, StringComparer.Ordinal)));
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
