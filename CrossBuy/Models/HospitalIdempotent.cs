using System.Text.Json;
using CrossBuy.BL.Platform;
using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Hospital;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;

namespace CrossBuy.Models
{
	// ==========================================================================================
	// HOSPITAL H166: [HospitalIdempotent] on a desk action that moves money, books, or sends something.
	//   · No "X-Idempotency-Key" header: the action runs as it always did.
	//   · A new key: claimed first (a unique row), then the action runs and its answer is kept.
	//   · The same key again, same caller and path: the kept answer, the action does not run twice.
	//   · The same key while the first is still running, or from another caller or path: refused (409), nothing runs.
	//   · The action throws: the claim is dropped, so the retry may run.
	// Keys live a day (older ones are swept as new ones come).
	// ==========================================================================================
	[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
	public sealed class HospitalIdempotentAttribute : Attribute, IAsyncActionFilter
	{
		public const string Header = "X-Idempotency-Key";
		private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

		public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
		{
			var http = context.HttpContext;
			string? key = http.Request.Headers[Header].ToString();
			if (string.IsNullOrWhiteSpace(key)) { await next(); return; }
			var services = http.RequestServices;
			var t = services.GetService(typeof(IStringLocalizer<CrossBuy.HospitalResources>)) as IStringLocalizer;
			if (key.Length > 64) { context.Result = Refuse(t, StatusCodes.Status400BadRequest, "The request key is too long"); return; }
			var contexts = services.GetService(typeof(IBusinessContextAccessor)) as IBusinessContextAccessor;
			var db = services.GetService(typeof(CrossDbContext)) as CrossDbContext;
			var ctx = contexts == null ? null : await contexts.TryGetCurrentAsync(http.RequestAborted);
			if (ctx == null || db == null) { await next(); return; }
			string path = (http.Request.Path.Value ?? "")[..Math.Min(200, (http.Request.Path.Value ?? "").Length)];
			var set = db.Set<HospitalRequestKey>();

			HospitalRequestKey? seen;
			try { seen = await set.AsNoTracking().FirstOrDefaultAsync(k => k.CompanyId == ctx.CompanyId && k.RequestKey == key, http.RequestAborted); }
			catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number == 208) { await next(); return; }   // not installed: run as before
			if (seen != null) { context.Result = Replay(t, seen, ctx.EmployeeId, path); return; }

			var claim = new HospitalRequestKey { CompanyId = ctx.CompanyId, RequestKey = key, Path = path, EmployeeId = ctx.EmployeeId, CreatedAt = DateTime.UtcNow };
			set.Add(claim);
			try { await db.SaveChangesAsync(http.RequestAborted); }
			catch (DbUpdateException)
			{
				db.Entry(claim).State = EntityState.Detached;   // the twin got there first
				var twin = await set.AsNoTracking().FirstOrDefaultAsync(k => k.CompanyId == ctx.CompanyId && k.RequestKey == key, http.RequestAborted);
				context.Result = twin == null ? Refuse(t, StatusCodes.Status409Conflict, "This request is still being handled") : Replay(t, twin, ctx.EmployeeId, path);
				return;
			}
			db.Entry(claim).State = EntityState.Detached;
			int id = claim.ID;

			var done = await next();
			if (done.Exception != null && !done.ExceptionHandled)
			{
				await set.Where(k => k.ID == id).ExecuteDeleteAsync(CancellationToken.None);   // the retry may run
				return;
			}
			var (status, body) = done.Result switch
			{
				JsonResult j => (j.StatusCode ?? StatusCodes.Status200OK, JsonSerializer.Serialize(j.Value, Json)),
				ObjectResult o => (o.StatusCode ?? StatusCodes.Status200OK, JsonSerializer.Serialize(o.Value, Json)),
				StatusCodeResult s => (s.StatusCode, "{}"),
				_ => (StatusCodes.Status200OK, "{\"ok\":true}"),
			};
			await set.Where(k => k.ID == id).ExecuteUpdateAsync(u => u.SetProperty(k => k.StatusCode, status).SetProperty(k => k.Body, body), CancellationToken.None);
			var old = DateTime.UtcNow.AddDays(-1);
			await set.Where(k => k.CreatedAt < old).ExecuteDeleteAsync(CancellationToken.None);
		}

		private static IActionResult Replay(IStringLocalizer? t, HospitalRequestKey seen, int? employeeId, string path)
		{
			if (seen.EmployeeId != employeeId || !string.Equals(seen.Path, path, StringComparison.OrdinalIgnoreCase))
				return Refuse(t, StatusCodes.Status409Conflict, "This request key belongs to another request");
			if (seen.StatusCode is not int code) return Refuse(t, StatusCodes.Status409Conflict, "This request is still being handled");
			return new ContentResult { StatusCode = code, ContentType = "application/json", Content = seen.Body ?? "{}" };
		}

		private static IActionResult Refuse(IStringLocalizer? t, int status, string message) => new ObjectResult(new { ok = false, error = t?[message].Value ?? message }) { StatusCode = status };
	}
}
