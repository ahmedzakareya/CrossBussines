using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Hosting;

namespace CrossBuy.Models
{
	// Hard-blocks an endpoint (returns 404) anywhere except the Development environment.
	// Applied to DevSeedController so the seeding/test/reset endpoints can NEVER run in production.
	[System.AttributeUsage(System.AttributeTargets.Class | System.AttributeTargets.Method)]
	public sealed class DevOnlyAttribute : ActionFilterAttribute
	{
		public override void OnActionExecuting(ActionExecutingContext context)
		{
			var env = context.HttpContext.RequestServices.GetService(typeof(IWebHostEnvironment)) as IWebHostEnvironment;
			if (env == null || !env.IsDevelopment())
				context.Result = new NotFoundResult();
		}
	}
}
