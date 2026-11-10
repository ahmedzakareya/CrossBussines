using CrossBuy.BL;
using CrossBuy.BL.Hospital;
using CrossBuy.Models;
using Microsoft.AspNetCore.Mvc;

namespace CrossBuy.Controllers
{
	// ==========================================================================================
	// HOSPITAL — H85: antenatal care. The list of active pregnancies (warnings and missed visits first; searched, filtered,
	// paged), a pregnancy's page with its visits, opening a pregnancy, recording a visit and closing it.
	// ==========================================================================================
	public partial class HospitalController
	{
		private HospitalAntenatalService? _ancSvc;
		private HospitalAntenatalService AncSvc => _ancSvc ??= ActivatorUtilities.CreateInstance<HospitalAntenatalService>(HttpContext.RequestServices);

		[HttpGet("Antenatal")]
		[HospitalPerm(HospitalActions.Read)]
		public async Task<IActionResult> Antenatal(CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return RedirectToAction("Index", "Home");
			Chrome();
			var (_, _, ready) = await AncSvc.ListAsync(ctx, null, null, 1, 5, DateTime.UtcNow, ct);
			ViewBag.Ready = ready;
			ViewBag.CanOpen = await _access.CanAsync(ctx, HospitalActions.Consult, null, ct) || await _access.CanAsync(ctx, HospitalActions.Admit, null, ct);
			return View();
		}

		[HttpGet("Antenatal/Data")]
		[HospitalPerm(HospitalActions.Read)]
		public async Task<IActionResult> AntenatalData(string? q, string? filter, int page = 1, int pageSize = 25, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return NotFound();
			if (pageSize is < 1 or > 200) pageSize = 25;
			if (page < 1) page = 1;
			var (rows, total, _) = await AncSvc.ListAsync(ctx, q, filter, page, pageSize, DateTime.UtcNow, ct);
			Response.Headers["X-Total"] = total.ToString();
			Response.Headers["X-Page"] = page.ToString();
			Response.Headers["X-Pages"] = Math.Max(1, (int)Math.Ceiling(total / (double)pageSize)).ToString();
			return PartialView("_AntenatalRows", rows);
		}

		[HttpGet("Pregnancies/{id:int}")]
		[HospitalPerm(HospitalActions.Read)]
		public async Task<IActionResult> Pregnancy(int id, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return RedirectToAction("Index", "Home");
			var page = await AncSvc.PageAsync(ctx, id, DateTime.UtcNow, ct);
			if (page == null) return NotFound();
			Chrome();
			ViewBag.CanVisit = await _access.CanAsync(ctx, HospitalActions.Consult, null, ct) || await _access.CanAsync(ctx, HospitalActions.Admit, null, ct);
			ViewBag.CanEnd = await _access.CanAsync(ctx, HospitalActions.Consult, null, ct);
			return View(page);
		}

		[HttpPost("Pregnancies"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Read)]
		public async Task<IActionResult> OpenPregnancy([FromBody] HospitalPregnancyInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null || input == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await AncSvc.OpenAsync(ctx, input, ctx.EmployeeId, DateTime.UtcNow, ct));
		}

		[HttpPost("Pregnancies/Visit"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Read)]
		public async Task<IActionResult> AddAntenatalVisit([FromBody] HospitalAntenatalVisitInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null || input == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await AncSvc.AddVisitAsync(ctx, input, ctx.EmployeeId, DateTime.UtcNow, ct));
		}

		[HttpPost("Pregnancies/{id:int}/End"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Consult)]
		public async Task<IActionResult> EndPregnancy(int id, [FromBody] HospitalPregnancyEndInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null || input == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await AncSvc.EndAsync(ctx, id, input, DateTime.UtcNow, ct));
		}
	}
}
