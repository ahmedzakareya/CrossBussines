using CrossBuy.BL;
using CrossBuy.BL.Platform;
using CrossBuy.Models.Platform;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace CrossBuy.Models
{
    // Stage 1 Batch C — action-level attributes for the four new modules.
    //
    // APPLIED TO THE C9 PROOF ENDPOINTS ONLY. The remaining backlog is Batch D by instruction: applying these
    // to 118 actions here would be exactly the bulk remediation the brief forbids, and several of those actions
    // need a record-level target that only the action body can build.
    //
    // WHY A SHARED BASE, AND WHY IT DOES NOT REDIRECT
    //
    // AccPermAttribute redirects to an HTML page on denial. That is right for an MVC screen and wrong for
    // anything a client parses — Hotfix A.1 found a mobile client would read the 302 as success. So this base
    // answers in the shape the REQUEST asked for: JSON 403 for an API/AJAX request, a redirect for a browser
    // page request. One decision, two presentations.
    public abstract class ModulePermAttributeBase : Attribute, IAsyncActionFilter
    {
        private readonly string _action;
        protected ModulePermAttributeBase(string action) { _action = action; }

        // The module scope this attribute guards — EntityRegistry.Scope*.
        protected abstract string Scope { get; }

        // Where a browser is sent when refused. An API-shaped request never sees it.
        protected abstract (string action, string controller) DeniedRedirect { get; }

        public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
        {
            var services = context.HttpContext.RequestServices;

            var contexts = services.GetService(typeof(IBusinessContextAccessor)) as IBusinessContextAccessor;
            var modules = services.GetService(typeof(IEnumerable<IModuleAccessService>)) as IEnumerable<IModuleAccessService>;

            // A missing service is a wiring failure, not a permission outcome. Denying is the safe reading:
            // continuing would run the action with NO authorization at all, which is the state Batch C exists
            // to end.
            if (contexts == null || modules == null) { Deny(context); return; }

            var module = modules.FirstOrDefault(m => string.Equals(m.Scope, Scope, StringComparison.Ordinal));
            if (module == null) { Deny(context); return; }

            // Session-free: the context comes from IBusinessContextAccessor, which resolves the employee from
            // the session blob OR the claims and takes the company from the Employee ROW.
            var businessContext = await contexts.TryGetCurrentAsync(context.HttpContext.RequestAborted);
            if (businessContext == null) { Deny(context); return; }

            // The attribute asks the MODULE-level question. A record-level check needs the record id, which
            // only the action body has — so an action that guards a specific record calls the service itself
            // with a PermissionTarget, and the attribute is the outer gate rather than the whole rule.
            if (!await module.CanAsync(businessContext, _action, null, context.HttpContext.RequestAborted))
            { Deny(context); return; }

            await next();
        }

        private void Deny(ActionExecutingContext context)
        {
            var http = context.HttpContext;
            bool wantsJson = http.Request.Headers["X-Requested-With"] == "XMLHttpRequest"
                          || http.Request.Path.StartsWithSegments("/api")
                          || (http.Request.Headers.Accept.ToString()?.Contains("application/json") ?? false)
                          || !HttpMethods.IsGet(http.Request.Method);

            if (wantsJson)
            {
                // The project's standard failure shape. No role, no company, no employee, no internals.
                context.Result = new ObjectResult(new { success = false, message = "You do not have permission to perform this action" })
                { StatusCode = StatusCodes.Status403Forbidden };
                return;
            }

            if (context.Controller is Controller c) c.TempData["PermErr"] = "You do not have permission to perform this action";
            var (action, controller) = DeniedRedirect;
            context.Result = new RedirectToActionResult(action, controller, null);
        }
    }

    /// HR module permission. action = read | employee-view | employee-manage | attendance-manage |
    /// leave-manage | leave-approve | payroll-view | payroll-manage | organization-manage |
    /// performance-manage | confidential-view
    [AttributeUsage(AttributeTargets.Method)]
    public sealed class HrPermAttribute : ModulePermAttributeBase
    {
        public HrPermAttribute(string action) : base(action) { }
        protected override string Scope => EntityRegistry.ScopeHr;
        protected override (string, string) DeniedRedirect => ("Index", "Admin");
    }

    /// Projects module permission. action = read | create | edit | manage | budget-view | budget-manage |
    /// billing | close
    [AttributeUsage(AttributeTargets.Method)]
    public sealed class ProjectPermAttribute : ModulePermAttributeBase
    {
        public ProjectPermAttribute(string action) : base(action) { }
        protected override string Scope => EntityRegistry.ScopeProjects;
        protected override (string, string) DeniedRedirect => ("Index", "Project");
    }

    /// Tasks module permission. action = read | create | edit | assign | reassign | complete | reopen | manage
    [AttributeUsage(AttributeTargets.Method)]
    public sealed class TaskPermAttribute : ModulePermAttributeBase
    {
        public TaskPermAttribute(string action) : base(action) { }
        protected override string Scope => EntityRegistry.ScopeTasks;
        protected override (string, string) DeniedRedirect => ("Index", "Tasks");
    }

    /// Communication module permission. action = read | send | create-group | manage-group |
    /// announcement-send | outbox-manage
    [AttributeUsage(AttributeTargets.Method)]
    public sealed class CommunicationPermAttribute : ModulePermAttributeBase
    {
        public CommunicationPermAttribute(string action) : base(action) { }
        protected override string Scope => EntityRegistry.ScopeCommunication;
        protected override (string, string) DeniedRedirect => ("Index", "Chat");
    }
}
