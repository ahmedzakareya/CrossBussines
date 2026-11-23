using CrossBuy.BL;
using CrossBuy.BL.Hospital;
using CrossBuy.Models;
using CrossBuy.Models.Platform;
using Microsoft.AspNetCore.Mvc;

namespace CrossBuy.Controllers
{
	// HOSPITAL H252 — the neonatal board and the day's record of each admitted newborn.
	public partial class HospitalController
	{
		private HospitalNicuService? _nicuSvc;
		private HospitalNicuService NicuSvc => _nicuSvc ??= ActivatorUtilities.CreateInstance<HospitalNicuService>(HttpContext.RequestServices);

		[HttpGet("Nicu"), HospitalPerm(HospitalActions.Read)]
		public async Task<IActionResult> Nicu(string? q, int page = 1, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return RedirectToAction("Index", "Home");
			Chrome();
			const int size = 12;
			var d = await NicuSvc.BoardAsync(ctx, DateTime.UtcNow, q, page, size, ct);
			ViewBag.Q = q; ViewBag.Page = Math.Max(1, page); ViewBag.Pages = Math.Max(1, (d.Total + size - 1) / size);
			ViewBag.CanAdmit = await _access.CanAsync(ctx, HospitalActions.Admit, null, ct);
			return View(d);
		}

		[HttpPost("Nicu/{admissionId:int}/Day"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Admit)]
		public async Task<IActionResult> NicuDay(int admissionId, [FromBody] HospitalNicuDayInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null || input == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await NicuSvc.RecordAsync(ctx, admissionId, input, ctx.EmployeeId, DateTime.UtcNow, ct));
		}
	}
}
