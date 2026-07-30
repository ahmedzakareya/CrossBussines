using CrossBuy.BL;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace CrossBuy.Models
{
	/// Action-level accounting permission. action = read | post | pay | manage.
	[AttributeUsage(AttributeTargets.Method)]
	public class AccPermAttribute : Attribute, IAsyncActionFilter
	{
		private readonly string _action;
		public AccPermAttribute(string action) { _action = action; }

		public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
		{
			var acc = context.HttpContext.RequestServices.GetService(typeof(IAccountingAccessService)) as IAccountingAccessService;
			if (acc == null) { await next(); return; }
			if (!await acc.CanAsync(_action))
			{
				if (context.Controller is Controller c) c.TempData["AccErr"] = "ليست لديك صلاحية لتنفيذ هذا الإجراء";
				context.Result = new RedirectToActionResult("Index", "Accounting", null);
				return;
			}
			await next();
		}
	}
}
