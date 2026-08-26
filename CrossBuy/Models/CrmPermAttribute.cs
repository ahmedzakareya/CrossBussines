using CrossBuy.BL;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace CrossBuy.Models
{
	/// Action-level CRM permission. action = read | edit | manage.
	///
	/// FAILS CLOSED. This filter used to open the gate when it could not find ICrmAccessService:
	///
	///     var acc = ...GetService(typeof(ICrmAccessService)) as ICrmAccessService;
	///     if (acc == null) { await next(); return; }        // <- ran the protected action
	///
	/// A missing registration, a container built without the CRM module, a service whose own
	/// constructor threw — every one of those turned thirty-seven guarded endpoints into open ones,
	/// silently, with nothing in the log. The absence of an authority is not permission from it; a
	/// gate that cannot ask whether you may pass has to assume you may not.
	///
	/// So: no authority, no entry. Same for an authority that throws while deciding — a decision that
	/// could not be made is not a decision to allow. Both take the same denial path as a plain "no",
	/// because a caller who can tell "denied" from "the permission service is down" learns something
	/// about the installation that a caller has no business learning.
	[AttributeUsage(AttributeTargets.Method)]
	public class CrmPermAttribute : Attribute, IAsyncActionFilter
	{
		private readonly string _action;
		public CrmPermAttribute(string action) { _action = action; }

		public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
		{
			var acc = context.HttpContext.RequestServices.GetService(typeof(ICrmAccessService)) as ICrmAccessService;
			if (acc == null) { Deny(context); return; }

			bool allowed;
			try
			{
				allowed = await acc.CanAsync(_action);
			}
			catch
			{
				// An authority that could not answer has not answered "yes".
				Deny(context);
				return;
			}

			if (!allowed) { Deny(context); return; }

			await next();
		}

		private static void Deny(ActionExecutingContext context)
		{
			if (context.Controller is Controller c) c.TempData["CrmErr"] = "ليست لديك صلاحية لتنفيذ هذا الإجراء";
			context.Result = new RedirectToActionResult("Index", "Crm", null);
		}
	}
}
