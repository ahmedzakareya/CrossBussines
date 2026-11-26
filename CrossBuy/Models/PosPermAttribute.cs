using CrossBuy.BL;
using CrossBuy.BL.Platform;
using CrossBuy.Models.Platform;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace CrossBuy.Models
{
	/// Action-level POS permission. action = view | sell | order | kitchen | manage — the vocabulary
	/// PosAccessService.Actions publishes, so a typo here fails closed rather than silently allowing.
	///
	/// WHY THIS ATTRIBUTE EXISTS AT ALL. The hypermarket back office had no write endpoint until the
	/// branch-capability screen, so it had no authorization either, and CBA001 refused the first one on
	/// sight. The analyzer had already anticipated this: AuthorizationSurface lists "PosPerm" among the
	/// permission attributes with a note that it "does not exist yet and is listed so that adding it is
	/// picked up without editing the analyzer". This is that attribute. No analyzer change, no baseline
	/// entry — the baseline may only shrink.
	///
	/// BRANCH ISOLATION COMES FREE. When the action carries a branchId, it is passed as the permission
	/// target, and PosAccessService refuses when that branch is not the caller's own. So a manager of one
	/// store cannot change another store's capabilities by editing the form's hidden field.
	[AttributeUsage(AttributeTargets.Method)]
	public class PosPermAttribute : Attribute, IAsyncActionFilter
	{
		private readonly string _action;
		public PosPermAttribute(string action) { _action = action; }

		public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
		{
			var sp = context.HttpContext.RequestServices;
			var acc = sp.GetService(typeof(IPosAccessService)) as IPosAccessService;
			var contexts = sp.GetService(typeof(IBusinessContextAccessor)) as IBusinessContextAccessor;

			// FAIL CLOSED. InvPerm calls next() when its service is missing because inventory predates the
			// access service and some deployments run without it. POS has no such history: a missing
			// authority here means the request cannot be authorized, and an unauthorized write is the thing
			// this attribute exists to prevent.
			// CanAsync is declared on the canonical session-free contract; PosAccessService implements both.
			var mod = acc as IModuleAccessService;
			if (acc == null || mod == null || contexts == null) { Deny(context); return; }

			var ctx = await contexts.TryGetCurrentAsync(context.HttpContext.RequestAborted);
			if (ctx == null) { Deny(context); return; }

			PermissionTarget? target = null;
			if (context.ActionArguments.TryGetValue("branchId", out var raw) && raw is int bid && bid > 0)
				target = new PermissionTarget { BranchId = bid };

			if (!await mod.CanAsync(ctx, _action, target, context.HttpContext.RequestAborted)) { Deny(context); return; }

			await next();
		}

		private static void Deny(ActionExecutingContext context)
		{
			if (context.Controller is Controller c)
				c.TempData["HyperErr"] = "no-permission";   // the view localises; see Capabilities.cshtml
			context.Result = new RedirectToActionResult("Dashboard", "Hyper", null);
		}
	}
}
