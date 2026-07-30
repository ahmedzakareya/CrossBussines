using System.Text.Json;
using CrossBuy.BL;
using CrossBuy.Models.Context;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;

namespace CrossBuy.Models
{
	// HM-1-أ (صفر-2): the ONE shared per-request guard for cashier lanes. Applied to each lane controller with its
	// session key + lane name + login path. On EVERY request it re-checks that the session context's branch still
	// belongs to this lane's activity (via the single whitelist IPosAccessService.IsActivityAllowedForLane) — so a
	// session built BEFORE the guard (or a direct hit on /pos/start, /pos/terminal, …) is rejected, not just Login.
	// A future system inherits the guard by adding this one attribute — no per-controller logic duplication.
	public class PosLaneActivityGuardAttribute : ActionFilterAttribute
	{
		private readonly string _sessionKey;
		private readonly string _lane;
		private readonly string _loginPath;
		public PosLaneActivityGuardAttribute(string sessionKey, string lane, string loginPath)
		{ _sessionKey = sessionKey; _lane = lane; _loginPath = loginPath; }

		public override async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
		{
			var http = context.HttpContext;
			var json = http.Session.GetString(_sessionKey);
			if (!string.IsNullOrEmpty(json))
			{
				int branchId = 0;
				try { using var doc = JsonDocument.Parse(json); if (doc.RootElement.TryGetProperty("BranchId", out var b) && b.TryGetInt32(out var v)) branchId = v; }
				catch { /* malformed session → treat as no branch */ }

				if (branchId > 0)
				{
					var db = http.RequestServices.GetRequiredService<CrossDbContext>();
					var access = http.RequestServices.GetRequiredService<IPosAccessService>();
					var code = await db.Branches.AsNoTracking().Where(x => x.ID == branchId).Select(x => x.ActivityPresetCode).FirstOrDefaultAsync();
					var (allowed, _) = access.IsActivityAllowedForLane(code, _lane);
					if (!allowed)
					{
						http.Session.Remove(_sessionKey);   // drop the mismatched session
						var loc = http.RequestServices.GetRequiredService<IStringLocalizer<CrossBuy.SharedResources>>();
						var msg = loc[_lane == "hyper" ? "This branch does not belong to the hypermarket system" : "This branch does not belong to the restaurant system"].Value;
						bool isAjax = http.Request.Headers["X-Requested-With"] == "XMLHttpRequest";
						if (isAjax || !HttpMethods.IsGet(http.Request.Method))
						{
							context.Result = new JsonResult(new { ok = false, error = msg }) { StatusCode = 403 };
						}
						else
						{
							var tdf = http.RequestServices.GetRequiredService<ITempDataDictionaryFactory>();
							var td = tdf.GetTempData(http);
							td["PosErr"] = msg;
							context.Result = new RedirectResult(_loginPath);
						}
						return;
					}
				}
			}
			await next();
		}
	}
}
