using CrossBuy.BL;
using CrossBuy.BL.Hospital;
using CrossBuy.Models;
using Microsoft.AspNetCore.Mvc;

namespace CrossBuy.Controllers
{
	// ==========================================================================================
	// HOSPITAL — H64: restricted files and break-glass. The administrator restricts or lifts a file (from the file); a
	// reader who may not open a restricted file meets the gate (Patient GET) and may break the glass with the reason;
	// the administrator reviews every opening on the break-glass log (searched and paged on the server).
	// ==========================================================================================
	public partial class HospitalController
	{
		private HospitalRestrictedAccessService? _restrictedSvc;
		private HospitalRestrictedAccessService RestrictedSvc => _restrictedSvc ??= ActivatorUtilities.CreateInstance<HospitalRestrictedAccessService>(HttpContext.RequestServices);

		public sealed class HospitalReasonInput { public string? Reason { get; set; } }
		public sealed class HospitalBreakGlassReviewInput { public string? Outcome { get; set; } public string? Note { get; set; } }

		[HttpPost("Patient/{id:int}/Restrict"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Manage)]
		public async Task<IActionResult> RestrictFile(int id, [FromBody] HospitalReasonInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await RestrictedSvc.RestrictAsync(ctx, id, input?.Reason, ctx.EmployeeId, DateTime.UtcNow, ct));
		}

		[HttpPost("Patient/{id:int}/Unrestrict"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Manage)]
		public async Task<IActionResult> UnrestrictFile(int id, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await RestrictedSvc.LiftAsync(ctx, id, ctx.EmployeeId, DateTime.UtcNow, ct));
		}

		[HttpPost("Patient/{id:int}/BreakGlass"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Read)]
		public async Task<IActionResult> BreakGlass(int id, [FromBody] HospitalReasonInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await RestrictedSvc.BreakAsync(ctx, id, input?.Reason, ctx.EmployeeId, DateTime.UtcNow, ct));
		}

		[HttpGet("BreakGlass")]
		[HospitalPerm(HospitalActions.Manage)]
		public async Task<IActionResult> BreakGlassLog(CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return RedirectToAction("Index", "Home");
			Chrome();
			try { var (_, _, waiting, restricted) = await RestrictedSvc.ListAsync(ctx, null, null, 1, 1, ct); ViewBag.Waiting = waiting; ViewBag.Restricted = restricted; ViewBag.Ready = true; }
			catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number is 207 or 208) { ViewBag.Ready = false; }
			return View();
		}

		[HttpGet("BreakGlass/Data")]
		[HospitalPerm(HospitalActions.Manage)]
		public async Task<IActionResult> BreakGlassData(string? q, string? state, int page = 1, int pageSize = 25, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return NotFound();
			if (pageSize is < 1 or > 200) pageSize = 25;
			if (page < 1) page = 1;
			var (rows, total, _, _) = await RestrictedSvc.ListAsync(ctx, state, q, page, pageSize, ct);
			Response.Headers["X-Total"] = total.ToString();
			Response.Headers["X-Page"] = page.ToString();
			Response.Headers["X-Pages"] = Math.Max(1, (int)Math.Ceiling(total / (double)pageSize)).ToString();
			ViewBag.Me = ctx.EmployeeId;
			return PartialView("_BreakGlassRows", rows);
		}

		[HttpPost("BreakGlass/{id:int}/Review"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Manage)]
		public async Task<IActionResult> ReviewBreakGlass(int id, [FromBody] HospitalBreakGlassReviewInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null || input == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await RestrictedSvc.ReviewAsync(ctx, id, input.Outcome, input.Note, ctx.EmployeeId, DateTime.UtcNow, ct));
		}
	}
}
