using CrossBuy.BL;
using CrossBuy.BL.Hospital;
using CrossBuy.Models;
using CrossBuy.Models.Context.Hospital;
using CrossBuy.Models.Platform;
using Microsoft.AspNetCore.Mvc;

namespace CrossBuy.Controllers
{
	// ==========================================================================================
	// HOSPITAL — H178: the result follow-up inbox. My results to review, my orders awaiting a result, and (the clinical
	// lead, `manage`) every overdue result and the ones nobody owns. Reviewing and handing over are the owner's or the
	// lead's (the service decides).
	// ==========================================================================================
	public partial class HospitalController
	{
		private HospitalResultReviewService? _resultSvc;
		private HospitalResultReviewService ResultSvc => _resultSvc ??= ActivatorUtilities.CreateInstance<HospitalResultReviewService>(HttpContext.RequestServices);

		[HttpGet("Results")]
		[HospitalPerm(HospitalActions.Consult)]
		public async Task<IActionResult> Results(CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return RedirectToAction("Index", "Home");
			Chrome();
			var now = DateTime.UtcNow;
			bool lead = await _access.CanAsync(ctx, HospitalActions.Manage, null, ct);
			ViewBag.Lead = lead;
			try
			{
				ViewBag.Mine = ctx.EmployeeId is int me ? await ResultSvc.ToReviewAsync(ctx, me, now, ct) : Array.Empty<HospitalResultItem>();
				ViewBag.Awaiting = ctx.EmployeeId is int me2 ? await ResultSvc.AwaitingAsync(ctx, me2, now, ct) : Array.Empty<HospitalAwaitingItem>();
				ViewBag.All = lead ? (await ResultSvc.ToReviewAsync(ctx, null, now, ct)).Where(i => i.Overdue || i.OwnerEmployeeId == null).ToList() : new List<HospitalResultItem>();
				ViewBag.Doctors = await ResultSvc.DoctorsAsync(ctx, ct);
				ViewBag.Ready = true;
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number == 208)
			{
				ViewBag.Ready = false;
				ViewBag.Mine = Array.Empty<HospitalResultItem>(); ViewBag.Awaiting = Array.Empty<HospitalAwaitingItem>(); ViewBag.All = Array.Empty<HospitalResultItem>();
				ViewBag.Doctors = Array.Empty<(int, string)>();
			}
			return View();
		}

		[HttpPost("Results/{id:int}/Review"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Consult), HospitalIdempotent]
		public async Task<IActionResult> ReviewResult(int id, [FromBody] HospitalResultReviewInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null || input == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await ResultSvc.ReviewAsync(ctx, id, input, ctx.EmployeeId, DateTime.UtcNow, ct));
		}

		[HttpPost("Results/{id:int}/Hand"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Consult)]
		public async Task<IActionResult> HandResult(int id, [FromBody] HospitalResultHandInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null || input == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await ResultSvc.HandOverAsync(ctx, id, input, ctx.EmployeeId, DateTime.UtcNow, ct));
		}
	}
}
