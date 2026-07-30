using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc;

namespace CrossBuy.Models
{
	public class SessionValidationAttribute : ActionFilterAttribute
	{
		public override void OnActionExecuting(ActionExecutingContext context)
		{
			var session = context.HttpContext.Session.GetString("Employee");

			if (string.IsNullOrEmpty(session))
			{
				var request = context.HttpContext.Request;
				var isAjax = request.Headers["X-Requested-With"] == "XMLHttpRequest";
				object routeValues = null;
				if (HttpMethods.IsGet(request.Method) && !isAjax)
				{
					// نعيد المستخدم إلى نفس الصفحة بعد إعادة تسجيل الدخول
					routeValues = new { returnUrl = request.Path + request.QueryString };
				}
				context.Result = new RedirectToActionResult("Login", "Account", routeValues);
			}

			base.OnActionExecuting(context);
		}
	}

}
