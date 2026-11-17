using CrossBuy.BL;
using CrossBuy.BL.Platform;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace CrossBuy.Models
{
	// ==========================================================================================
	// HOSPITAL H156 — the clinical gate, DENY BY DEFAULT. Every hospital endpoint needs `clinical-read` unless it is marked
	// [HospitalReceptionAllowed]: the reception desk's administrative screens (front desk, registration, appointments,
	// billing, deposits, insurance, consents, portal requests, the map and the building). A reception-only person
	// therefore reaches no clinical screen or data endpoint — including any added later and not thought about — while
	// every clinical role (and the module while bootstrap-open) passes exactly as before. It is an OUTER gate: the action's
	// own [HospitalPerm] still decides what the caller may do there.
	// ==========================================================================================
	[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
	public sealed class HospitalReceptionAllowedAttribute : Attribute { }

	[AttributeUsage(AttributeTargets.Class, AllowMultiple = false)]
	public sealed class HospitalClinicalGateAttribute : Attribute, IAsyncActionFilter
	{
		public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
		{
			var meta = context.ActionDescriptor.EndpointMetadata;
			if (meta.OfType<HospitalReceptionAllowedAttribute>().Any() || meta.OfType<IAllowAnonymous>().Any()) { await next(); return; }

			var services = context.HttpContext.RequestServices;
			var contexts = services.GetService(typeof(IBusinessContextAccessor)) as IBusinessContextAccessor;
			var access = services.GetService(typeof(BL.IHospitalAccessService)) as BL.IHospitalAccessService;
			if (contexts == null || access == null) { Deny(context); return; }   // a wiring failure denies, never opens
			var ctx = await contexts.TryGetCurrentAsync(context.HttpContext.RequestAborted);
			if (ctx == null) { await next(); return; }   // no signed-in caller: the session and [HospitalPerm] gates refuse it themselves
			if (!await access.CanAsync(ctx, BL.HospitalActions.ClinicalRead, null, context.HttpContext.RequestAborted)) { Deny(context); return; }
			await next();
		}

		private static void Deny(ActionExecutingContext context)
		{
			var http = context.HttpContext;
			bool wantsJson = http.Request.Headers["X-Requested-With"] == "XMLHttpRequest" || !HttpMethods.IsGet(http.Request.Method)
				|| (http.Request.Headers.Accept.ToString()?.Contains("application/json") ?? false);
			if (wantsJson)
			{
				context.Result = new ObjectResult(new { success = false, ok = false, message = "You do not have permission to perform this action" }) { StatusCode = StatusCodes.Status403Forbidden };
				return;
			}
			if (context.Controller is Controller c) c.TempData["PermErr"] = "You do not have permission to perform this action";
			context.Result = new RedirectToActionResult("FrontDesk", "Hospital", null);
		}
	}
}
