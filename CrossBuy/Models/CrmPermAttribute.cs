using CrossBuy.BL;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace CrossBuy.Models
{
	/// Action-level CRM permission. action = read | edit | manage.
	[AttributeUsage(AttributeTargets.Method)]
	public class CrmPermAttribute : Attribute, IAsyncActionFilter
	{
		private readonly string _action;
		public CrmPermAttribute(string action) { _action = action; }

		public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
		{
			var acc = context.HttpContext.RequestServices.GetService(typeof(ICrmAccessService)) as ICrmAccessService;
			if (acc == null) { await next(); return; }
			if (!await acc.CanAsync(_action))
			{
				if (context.Controller is Controller c) c.TempData["CrmErr"] = "ليست لديك صلاحية لتنفيذ هذا الإجراء";
				context.Result = new RedirectToActionResult("Index", "Crm", null);
				return;
			}
			await next();
		}
	}
}
