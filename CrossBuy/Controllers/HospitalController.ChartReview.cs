using CrossBuy.BL;
using CrossBuy.BL.Hospital;
using CrossBuy.Models;
using Microsoft.AspNetCore.Mvc;

namespace CrossBuy.Controllers
{
	// ==========================================================================================
	// HOSPITAL — H80: chart completion. The medical records screen (discharged stays, their deficiencies and who owes
	// them, delinquent charts first; filtered, searched, paged) and the sign-off of a complete chart.
	// ==========================================================================================
	public partial class HospitalController
	{
		private HospitalChartReviewService? _chartSvc;
		private HospitalChartReviewService ChartSvc => _chartSvc ??= ActivatorUtilities.CreateInstance<HospitalChartReviewService>(HttpContext.RequestServices);

		public sealed class HospitalChartSignInput { public string? Note { get; set; } }

		[HttpGet("Records")]
		[HospitalPerm(HospitalActions.Read)]
		public async Task<IActionResult> Records(CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return RedirectToAction("Index", "Home");
			Chrome();
			var (_, _, stats, ready) = await ChartSvc.ListAsync(ctx, null, null, 90, 1, 5, DateTime.UtcNow, ct);
			ViewBag.Ready = ready;
			return View(stats);
		}

		[HttpGet("Records/Data")]
		[HospitalPerm(HospitalActions.Read)]
		public async Task<IActionResult> RecordsData(string? state, string? q, int days = 90, int page = 1, int pageSize = 25, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return NotFound();
			if (pageSize is < 1 or > 200) pageSize = 25;
			if (page < 1) page = 1;
			var (rows, total, _, _) = await ChartSvc.ListAsync(ctx, state, q, days, page, pageSize, DateTime.UtcNow, ct);
			Response.Headers["X-Total"] = total.ToString();
			Response.Headers["X-Page"] = page.ToString();
			Response.Headers["X-Pages"] = Math.Max(1, (int)Math.Ceiling(total / (double)pageSize)).ToString();
			ViewBag.CanSign = await _access.CanAsync(ctx, HospitalActions.Manage, null, ct);
			return PartialView("_RecordsRows", rows);
		}

		[HttpPost("Records/{id:int}/Sign"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Manage)]
		public async Task<IActionResult> SignChart(int id, [FromBody] HospitalChartSignInput? input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await ChartSvc.SignAsync(ctx, id, input?.Note, ctx.EmployeeId, DateTime.UtcNow, ct));
		}
	}
}
