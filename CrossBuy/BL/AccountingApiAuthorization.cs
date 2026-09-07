using CrossBuy.BL.Platform;
using CrossBuy.Models.Platform;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace CrossBuy.BL
{
    // Stage 1 Hotfix A.1 — the authorization + company gate for AccountingApiController.
    //
    // WHY A GUARD SERVICE RATHER THAN AN ATTRIBUTE
    //
    // AccPermAttribute already exists and is the right mechanism for the MVC screens, but it cannot be used here for
    // two independent reasons:
    //
    //   1. On denial it returns `RedirectToActionResult("Index", "Accounting")` — a 302 to an HTML page. A mobile
    //      client following that redirect receives a login/dashboard page with status 200 and no way to tell it was
    //      refused. For a JSON API the denial must BE the response.
    //   2. An attribute cannot hand anything back to the action. Every action here needs the VALIDATED company id to
    //      pass to its service, and the whole point of the hotfix is that the company must not come from the request.
    //      A filter that only says yes/no would leave `dto.CompanyID` still flowing into the service.
    //
    // This is NOT a parallel permission engine. The decision is made by the SAME context-aware
    // IModuleAccessService the platform provider uses (`AccountingAccessService.CanAsync(BusinessContext, action)`),
    // resolved out of DI by scope exactly as ModulePermissionAdapterBase does. This class contributes no rule of its
    // own: it resolves the context, validates the company, asks, and shapes a safe answer.
    public interface IAccountingApiAuthorization
    {
        // action MUST be one of AccountingAccessService.Actions (read | post | pay | manage | currency-override).
        // requestedCompanyId is whatever arrived on the request — query or body — or null when the action carries
        // none. It is VALIDATED, never trusted.
        Task<AccountingApiDecision> AuthorizeAsync(
            string action, int? requestedCompanyId, CancellationToken cancellationToken = default);
    }

    // The outcome. On refusal `Error` is the ActionResult the controller returns verbatim; on success `CompanyId` is
    // the only company id the action may use, and `EmployeeId` is the actor to record.
    public sealed class AccountingApiDecision
    {
        public bool Ok { get; private init; }
        public int CompanyId { get; private init; }
        public int? EmployeeId { get; private init; }
        public IActionResult? Error { get; private init; }

        public static AccountingApiDecision Allow(int companyId, int? employeeId)
            => new() { Ok = true, CompanyId = companyId, EmployeeId = employeeId };

        public static AccountingApiDecision Deny(IActionResult error)
            => new() { Ok = false, Error = error };
    }

    public sealed class AccountingApiAuthorization : IAccountingApiAuthorization
    {
        // The SAME messages the MVC path uses, so a user sees one wording across the product — and deliberately
        // generic. They name no role, no company, no employee and no reason: an error that explains the rule is an
        // error that maps it. (A1/9: do not expose permission internals.)
        private const string Forbidden = "You do not have permission to perform this action";
        private const string NoContext = "The company for this request could not be determined";

        private readonly IBusinessContextAccessor _contexts;
        private readonly IModuleAccessService _accounting;
        private readonly ILogger<AccountingApiAuthorization> _log;

        public AccountingApiAuthorization(
            IBusinessContextAccessor contexts,
            IEnumerable<IModuleAccessService> modules,
            ILogger<AccountingApiAuthorization> log)
        {
            _contexts = contexts;
            _log = log;
            // Resolved by SCOPE, the same way ModulePermissionAdapterBase resolves its module. If the accounting
            // module were ever unregistered this throws at construction rather than silently allowing everything —
            // a missing authorization service must not become an open door.
            _accounting = modules.FirstOrDefault(m => m.Scope == EntityRegistry.ScopeAccounting)
                ?? throw new InvalidOperationException(
                    "No IModuleAccessService is registered for the accounting scope. The accounting API cannot " +
                    "authorize requests without it.");
        }

        public async Task<AccountingApiDecision> AuthorizeAsync(
            string action, int? requestedCompanyId, CancellationToken cancellationToken = default)
        {
            // ---- 1. the action must be one this module actually defines ----
            // An unknown action DENIES. It must never fall through to "read", and it must never be treated as
            // "no rule configured, therefore allow".
            if (string.IsNullOrWhiteSpace(action) || !_accounting.Actions.Contains(action))
            {
                _log.LogError(
                    "Accounting API was asked to authorize the unknown action '{Action}'. Denied. Known actions: {Known}.",
                    action, string.Join(", ", _accounting.Actions));
                return AccountingApiDecision.Deny(Refuse(StatusCodes.Status403Forbidden, Forbidden));
            }

            // ---- 2. the context, or nothing ----
            // The bearer token proves WHO signed in; it does not establish which company they act for. That comes
            // from the Employee row (Batch A). No resolvable employee/company ⇒ DENY. There is no default company,
            // and authentication is not authorization.
            var context = await _contexts.TryGetCurrentAsync(cancellationToken);
            if (context == null)
            {
                _log.LogWarning(
                    "Accounting API request for '{Action}' carried a valid token but no company could be resolved " +
                    "(inactive employee, no EmpCompanyID, or no Employee row for the token's user). Denied.", action);
                return AccountingApiDecision.Deny(Refuse(StatusCodes.Status403Forbidden, NoContext));
            }

            // ---- 3. a supplied company id must MATCH the resolved one ----
            // Validated, not coerced: a mismatch is refused rather than quietly replaced, because silently
            // substituting the caller's company would make a tampered request look like it succeeded as asked.
            //
            // Cross-company access is NOT available here: no role claim is issued in the JWT, so an API caller can
            // never satisfy CompanyBypassPolicy's admin requirement. That is the deliberate posture for this
            // surface — see ADR-025 — so there is no bypass path to consult.
            if (requestedCompanyId is > 0 && requestedCompanyId.Value != context.CompanyId)
            {
                // Logged at Warning with the actor and BOTH companies: this is the tampering signal an auditor
                // needs. The RESPONSE still says nothing — the detail belongs in the log, not on the wire.
                _log.LogWarning(
                    "Accounting API company mismatch on '{Action}': employee {Employee} resolves to company " +
                    "{Resolved} but the request supplied company {Requested}. Rejected.",
                    action, context.EmployeeId, context.CompanyId, requestedCompanyId.Value);
                return AccountingApiDecision.Deny(Refuse(StatusCodes.Status403Forbidden, Forbidden));
            }

            // A non-positive supplied value (0, negative) is treated as "not supplied" rather than as a company:
            // `Set` refuses those and there is no company 0. It is not an error — the parameter is optional and
            // several callers omit it — but it can never widen anything, because the resolved company is used.

            // ---- 4. the module's own decision ----
            if (!await _accounting.CanAsync(context, action, target: null, cancellationToken))
            {
                _log.LogWarning(
                    "Accounting API denied '{Action}' to employee {Employee} in company {Company}.",
                    action, context.EmployeeId, context.CompanyId);
                return AccountingApiDecision.Deny(Refuse(StatusCodes.Status403Forbidden, Forbidden));
            }

            return AccountingApiDecision.Allow(context.CompanyId, context.EmployeeId);
        }

        // The project's existing API failure shape — { success, message } — so the contract is unchanged and only
        // the status code differs. No exception text, no stack trace, no role names, no company ids.
        private static IActionResult Refuse(int statusCode, string message)
            => new ObjectResult(new { success = false, message }) { StatusCode = statusCode };
    }
}
