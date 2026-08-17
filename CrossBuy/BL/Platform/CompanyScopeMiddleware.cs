using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace CrossBuy.BL.Platform
{
    // Stage 1 Batch B / B2 — resolves the company scope ONCE per request, before any controller runs.
    //
    // WHY THIS IS REQUIRED, NOT OPTIONAL
    //
    // Before B2, ICompanyScopeHolder was populated only as a SIDE EFFECT: it was set when something happened to
    // ask IBusinessContextFactory for a context — recording a business event, evaluating a platform permission,
    // projecting a notification. An ordinary Accounting or Inventory screen does none of those. It queries
    // CrossDbContext directly, so its scope was never resolved.
    //
    // With the filters on, an unresolved scope reads NOTHING from the twelve pilot entities (fail closed). Without
    // this middleware, that would mean every screen over a pilot entity goes blank unless the request happened to
    // touch a platform service first — an intermittent, order-dependent blankness, which is the worst kind.
    //
    // So the scope is resolved deterministically, for every request, here.
    //
    // WHAT IT DELIBERATELY DOES NOT DO
    //
    //   * It does not throw. An anonymous request (the login page, the storefront, a health probe) legitimately
    //     has no company; it uses TryGetCurrentAsync, which returns null rather than throwing. The storefront then
    //     pins its own scope through ICompanyIsolationBypass.BeginPublicCatalogRead.
    //   * It does not invent a company. There is no fallback, no default, no company 1 — an unresolved request
    //     stays unresolved and reads nothing, which is the whole point.
    //   * It does not authorize. Resolving WHICH company a request acts as is not deciding WHAT it may do; the
    //     module access services and IPlatformPermissionProvider still make that decision.
    //   * It does not short-circuit the pipeline on failure. A request that cannot resolve a company still runs;
    //     it simply reads no pilot rows, and the log line below says so.
    //
    // WHY IT USES THE ACCESSOR RATHER THAN THE FACTORY
    //
    // IBusinessContextAccessor caches the resolution per DI scope, INCLUDING the negative answer. Going through it
    // means the two Employee reads happen at most once per request: any platform service later in the same request
    // reuses this resolution instead of repeating it. Calling the factory directly would double the work.
    public sealed class CompanyScopeMiddleware
    {
        private readonly RequestDelegate _next;
        private readonly ILogger<CompanyScopeMiddleware> _log;

        public CompanyScopeMiddleware(RequestDelegate next, ILogger<CompanyScopeMiddleware> log)
        { _next = next; _log = log; }

        // The services are taken per-invocation, not in the constructor: middleware is a singleton and both are
        // Scoped. Resolving them in the constructor would capture one request's scope for the lifetime of the app.
        public async Task InvokeAsync(
            HttpContext http, IBusinessContextAccessor contexts, ICompanyScopeHolder scope)
        {
            try
            {
                // Resolving publishes the company to ICompanyScopeHolder as a side effect (see
                // BusinessContextFactory.Publish), which is what the query filters read.
                var context = await contexts.TryGetCurrentAsync(http.RequestAborted);

                if (context == null && IsAuthenticated(http))
                {
                    // Signed in, but no company could be established — an inactive employee, an employee with no
                    // EmpCompanyID, or a session naming an employee that no longer exists. Before Stage 1 this
                    // silently became company 1. Now it reads nothing, so it must be findable in the log: this
                    // line is the difference between a diagnosable empty grid and a mystery.
                    _log.LogWarning(
                        "No company could be resolved for authenticated request {Method} {Path} (user={User}). " +
                        "Company-scoped entities will return no rows for this request. There is no default company.",
                        http.Request.Method, http.Request.Path, http.User?.Identity?.Name ?? "(unknown)");
                }
                else if (context != null)
                {
                    _log.LogDebug(
                        "Company scope resolved for {Path}: company={Company} branch={Branch} employee={Employee}",
                        http.Request.Path, context.CompanyId, context.BranchId, context.EmployeeId);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Resolution reads Employee/Branches. If that read fails (a transient DB error), the request must
                // fail on its own terms rather than on ours — the scope stays unresolved, so the request reads no
                // pilot rows, which is the safe direction.
                _log.LogError(ex,
                    "Company scope resolution failed for {Method} {Path}. The request continues with an " +
                    "UNRESOLVED scope, so company-scoped entities will return no rows.",
                    http.Request.Method, http.Request.Path);
            }

            await _next(http);
        }

        // Both doors the project actually uses: cookie/Identity claims, and the "Employee" session blob that the
        // access services and the POS lanes have always read.
        private static bool IsAuthenticated(HttpContext http)
            => http.User?.Identity?.IsAuthenticated == true
            || !string.IsNullOrEmpty(TryGetSessionEmployee(http));

        private static string? TryGetSessionEmployee(HttpContext http)
        {
            // Session is not available on every path (it is added by UseSession, and some endpoints run before
            // it). Asking for it where it is absent throws, and a middleware that throws on a health probe is
            // worse than one that cannot tell whether the probe was signed in.
            try { return http.Session?.GetString("Employee"); }
            catch (InvalidOperationException) { return null; }
        }
    }
}
