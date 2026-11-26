using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Hosting;
using System.Net;

namespace CrossBuy.Models
{
		// Hard-blocks development tooling outside a local Development process. Environment names are
		// deployment configuration, not an access-control boundary: a misconfigured Development server
		// must not expose anonymous seed/reset endpoints to the network.
	[System.AttributeUsage(System.AttributeTargets.Class | System.AttributeTargets.Method)]
	public sealed class DevOnlyAttribute : ActionFilterAttribute
	{
		public override void OnActionExecuting(ActionExecutingContext context)
		{
			var env = context.HttpContext.RequestServices.GetService(typeof(IWebHostEnvironment)) as IWebHostEnvironment;
			var remoteIp = context.HttpContext.Connection.RemoteIpAddress;
			var isLoopback = remoteIp != null && (IPAddress.IsLoopback(remoteIp)
				|| (remoteIp.IsIPv4MappedToIPv6 && IPAddress.IsLoopback(remoteIp.MapToIPv4())));
			if (env == null || !env.IsDevelopment() || !isLoopback)
				context.Result = new NotFoundResult();
		}
	}
}
