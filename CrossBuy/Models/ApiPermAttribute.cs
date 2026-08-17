using CrossBuy.BL;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace CrossBuy.Models
{
    /// Stage 1 Batch D1 Wave 1 — the API-SAFE module permission guard.
    ///
    /// WHY THIS EXISTS SEPARATELY FROM AccPerm/InvPerm. Those attributes deny by returning
    /// `RedirectToActionResult` — correct for a full-page MVC POST, and wrong for a JSON endpoint in a way that is
    /// worse than it looks: a fetch() caller receives **302 → 200 with an HTML login page**, so the browser code
    /// sees a success status and a body it cannot parse. The denial becomes a parse error, or worse, is read as
    /// success. `AccountingController.CustomerQuickAdd` and `VendorQuickAdd` are exactly this shape: MVC actions
    /// that return `Json(...)` to an inline dropdown widget.
    ///
    /// So the guard is the same DECISION with a different RESPONSE. It asks the same access service for the same
    /// action — no second permission engine, no divergent vocabulary — and answers in the project's own JSON shape
    /// (`{ ok = false, error = ... }`), which is what every one of these endpoints already returns on failure.
    ///
    /// Status codes follow the brief: unauthenticated → 401, authenticated-but-unauthorized → 403. The BODY is
    /// identical for both, and carries no authorization detail: which role was missing, which company was
    /// resolved, and whether the record exists are all facts an unauthorized caller must not learn.
    ///
    /// Precedent: Hotfix A.1's `IAccountingApiAuthorization` for `AccountingApiController`. That one is an
    /// in-service guard for a controller whose every action is an API; this is the attribute form, for JSON actions
    /// that live inside an otherwise-MVC controller.
    [AttributeUsage(AttributeTargets.Method)]
    public class ApiPermAttribute : Attribute, IAsyncActionFilter
    {
        public const string Accounting = "acc";
        public const string Inventory = "inv";
        // HR joined in Wave 1's final increment, for AdminController.SaveSalaryPolicy — a JSON action that changes
        // the payroll BASIS. HR differs from the two above: its access service has no legacy session-based
        // CanAsync(string), only the canonical BusinessContext form, so this guard resolves the context first.
        public const string Hr = "hr";

        private readonly string _module;
        private readonly string _action;

        public ApiPermAttribute(string module, string action) { _module = module; _action = action; }

        public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
        {
            var http = context.HttpContext;

            // Unauthenticated is a DIFFERENT answer from unauthorized — 401 tells a client to sign in, 403 tells
            // it not to retry. Conflating them makes an expired session look like a permission problem.
            if (http.User?.Identity?.IsAuthenticated != true)
            {
                context.Result = Denied(401);
                return;
            }

            bool allowed = _module switch
            {
                Accounting => await Ask<IAccountingAccessService>(http, s => s.CanAsync(_action)),
                Inventory => await Ask<IInventoryAccessService>(http, s => s.CanAsync(_action)),
                Hr => await AskHrAsync(http),
                _ => false,   // an unknown module denies. It never falls through to "allow".
            };

            if (!allowed)
            {
                context.Result = Denied(403);
                return;
            }

            await next();
        }

        // A missing access service DENIES. The MVC attributes call next() when the service is absent, which is a
        // fail-open this guard deliberately does not copy: an endpoint that creates a customer must not become
        // reachable because a registration was removed.
        private static async Task<bool> Ask<T>(HttpContext http, Func<T, Task<bool>> ask) where T : class
        {
            var svc = http.RequestServices.GetService(typeof(T)) as T;
            return svc != null && await ask(svc);
        }

        // HR is asked through the canonical, session-free path: resolve the BusinessContext, then ask. An
        // unresolved context DENIES — it never falls back to a company.
        private async Task<bool> AskHrAsync(HttpContext http)
        {
            var hr = http.RequestServices.GetService(typeof(IHrAccessService)) as IHrAccessService;
            var contexts = http.RequestServices.GetService(typeof(CrossBuy.BL.Platform.IBusinessContextAccessor))
                as CrossBuy.BL.Platform.IBusinessContextAccessor;
            if (hr == null || contexts == null) return false;

            var ctx = await contexts.TryGetCurrentAsync();
            if (ctx == null) return false;

            return await hr.CanAsync(ctx, _action);
        }

        private static IActionResult Denied(int statusCode) => new JsonResult(new
        {
            ok = false,
            // The project's own failure shape. Deliberately generic and identical for 401 and 403.
            error = "ليست لديك صلاحية لتنفيذ هذا الإجراء",
        })
        { StatusCode = statusCode };
    }
}
