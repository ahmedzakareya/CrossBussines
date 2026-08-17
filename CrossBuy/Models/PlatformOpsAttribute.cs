using CrossBuy.BL;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace CrossBuy.Models
{
    /// Stage 0 Batch B — gate for platform OPERATIONS screens (currently the Business Event Monitor).
    ///
    /// These screens expose cross-module audit data — event payloads, actors, correlation ids, dispatch failures —
    /// so they must not be reachable by an ordinary authenticated employee. There is no platform-operations role
    /// table yet (creating one is Stage 1 RBAC work), so this attribute composes the two strongest signals that
    /// already exist rather than inventing a third:
    ///
    ///   1. an ASP.NET Identity role in <see cref="AdminRoles"/>, or
    ///   2. accounting "manage" — the ChiefAccountant tier, which is already the highest existing business right
    ///      and already governs period close, year-end and role assignment.
    ///
    /// Elevated actions (retry beyond MaxAttempts, cross-company viewing) require the ADMIN role specifically, not
    /// the accounting tier — see <see cref="IsElevated"/>.
    ///
    /// DELIBERATE LIMITATION, recorded rather than hidden: AccountingAccessService returns true for every action
    /// when its role table is empty company-wide ("open when unconfigured"). On such an install this gate is open
    /// too. That is a property of the existing access service, not of this attribute, and it is why Stage 1's
    /// access-service work is the real fix.
    [AttributeUsage(AttributeTargets.Method | AttributeTargets.Class)]
    public class PlatformOpsAttribute : Attribute, IAsyncActionFilter
    {
        /// Identity roles treated as platform operators.
        public static readonly string[] AdminRoles = { "Admin", "Administrator", "SuperAdmin", "PlatformOps" };

        private readonly bool _requireElevated;

        /// <param name="requireElevated">true = the ADMIN role is mandatory; the accounting tier is not enough.</param>
        public PlatformOpsAttribute(bool requireElevated = false) { _requireElevated = requireElevated; }

        public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
        {
            bool admin = IsAdmin(context.HttpContext);
            bool allowed = admin;

            if (!allowed && !_requireElevated)
            {
                var acc = context.HttpContext.RequestServices.GetService(typeof(IAccountingAccessService)) as IAccountingAccessService;
                if (acc != null) allowed = await acc.CanAsync("manage");
            }

            if (!allowed)
            {
                Deny(context);
                return;
            }
            await next();
        }

        public static bool IsAdmin(HttpContext http)
            => AdminRoles.Any(r => http.User?.IsInRole(r) == true);

        /// Cross-company viewing and the max-attempts override are admin-only.
        public static bool IsElevated(HttpContext http) => IsAdmin(http);

        private static void Deny(ActionExecutingContext context)
        {
            // AJAX/JSON callers get a status they can branch on; page requests get a redirect, matching the
            // behaviour of AccPerm/InvPerm so the UX is consistent.
            bool isAjax = context.HttpContext.Request.Headers["X-Requested-With"] == "XMLHttpRequest"
                          || (context.HttpContext.Request.Headers["Accept"].ToString()?.Contains("application/json") ?? false);
            if (isAjax)
            {
                context.Result = new ForbidResult();
                return;
            }
            if (context.Controller is Controller c)
                c.TempData["PlatformErr"] = "ليست لديك صلاحية الوصول إلى شاشات تشغيل المنصّة";
            context.Result = new RedirectToActionResult("Index", "Home", null);
        }
    }
}
