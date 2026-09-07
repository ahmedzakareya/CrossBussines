using CrossBuy.BL;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace CrossBuy.Models
{
	/// Action-level inventory permission. action = read | doc | purchase | manage.
	/// Also enforces WarehouseKeeper branch scope by inspecting warehouse args in the request.
	[AttributeUsage(AttributeTargets.Method)]
	public class InvPermAttribute : Attribute, IAsyncActionFilter
	{
		private readonly string _action;
		public InvPermAttribute(string action) { _action = action; }

		public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
		{
			var acc = context.HttpContext.RequestServices.GetService(typeof(IInventoryAccessService)) as IInventoryAccessService;
			if (acc == null) { await next(); return; }

			if (!await acc.CanAsync(_action))
			{
				Deny(context);
				return;
			}

			// WarehouseKeeper branch scope: check any warehouse argument present on the action
			foreach (var key in new[] { "warehouseId", "fromWarehouseId", "toWarehouseId" })
			{
				if (context.ActionArguments.TryGetValue(key, out var val) && val is int whId && whId > 0)
				{
					if (!await acc.CanUseWarehouseAsync(whId)) { Deny(context); return; }
				}
			}
			await next();
		}

		private static void Deny(ActionExecutingContext context)
		{
			if (context.Controller is Controller c)
				c.TempData["InvErr"] = "You do not have permission to perform this action";
			// GET → redirect to inventory home; POST → back where possible
			context.Result = new RedirectToActionResult("Index", "Inventory", null);
		}
	}
}
