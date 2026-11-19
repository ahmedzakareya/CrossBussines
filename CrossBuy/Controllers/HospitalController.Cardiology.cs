using CrossBuy.BL;
using CrossBuy.BL.Hospital;
using CrossBuy.Models;
using CrossBuy.Models.Context.Hospital;
using CrossBuy.Models.Platform;
using Microsoft.AspNetCore.Mvc;

namespace CrossBuy.Controllers
{
	// HOSPITAL H218 — cardiac studies: the list (read with `clinical-read`), a request and a report (`consult`).
	public partial class HospitalController
	{
		private HospitalCardiologyService? _cardioSvc;
		private HospitalCardiologyService CardioSvc => _cardioSvc ??= ActivatorUtilities.CreateInstance<HospitalCardiologyService>(HttpContext.RequestServices);

		[HttpGet("Cardiology")]
		[HospitalPerm(HospitalActions.ClinicalRead)]
		public async Task<IActionResult> Cardiology(string? q, string? kind, string? status, int page = 1, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return RedirectToAction("Index", "Home");
			Chrome();
			const int size = 20;
			if (!string.IsNullOrEmpty(kind) && !HospitalCardioKind.All.Contains(kind)) kind = null;
			if (!string.IsNullOrEmpty(status) && !HospitalCardioStatus.All.Contains(status)) status = null;
			var (rows, total, waiting, critical, ready) = await CardioSvc.ListAsync(ctx, q, kind, status, page, size, ct);
			ViewBag.Q = q; ViewBag.Kind = kind; ViewBag.Status = status; ViewBag.Page = Math.Max(1, page); ViewBag.Pages = Math.Max(1, (total + size - 1) / size);
			ViewBag.Total = total; ViewBag.Waiting = waiting; ViewBag.Critical = critical; ViewBag.Ready = ready;
			ViewBag.CanConsult = await _access.CanAsync(ctx, HospitalActions.Consult, null, ct);
			return View(rows);
		}

		[HttpPost("Cardiology"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Consult)]
		public async Task<IActionResult> CardiologyRequest([FromBody] HospitalCardioRequestInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null || input == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await CardioSvc.RequestAsync(ctx, input, ctx.EmployeeId, DateTime.UtcNow, ct));
		}

		[HttpPost("Cardiology/{id:int}/Report"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Consult)]
		public async Task<IActionResult> CardiologyReport(int id, [FromBody] HospitalCardioReportInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null || input == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await CardioSvc.ReportAsync(ctx, id, input, ctx.EmployeeId, DateTime.UtcNow, ct));
		}
	}
}
