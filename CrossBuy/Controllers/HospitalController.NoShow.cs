using CrossBuy.BL;
using CrossBuy.BL.Hospital;
using CrossBuy.Models;
using CrossBuy.Models.Platform;
using Microsoft.AspNetCore.Mvc;

namespace CrossBuy.Controllers
{
	// HOSPITAL H263 — the coming week's appointments by no-show risk, and the model's local validation.
	public partial class HospitalController
	{
		private HospitalNoShowService? _noShowSvc;
		private HospitalNoShowService NoShowSvc => _noShowSvc ??= ActivatorUtilities.CreateInstance<HospitalNoShowService>(HttpContext.RequestServices);

		[HospitalReceptionAllowed]   // the reception desk calls these patients
		[HttpGet("FrontDesk/NoShowRisk"), HospitalPerm(HospitalActions.Book)]
		public async Task<IActionResult> NoShowRisk(string? band, string? q, int page = 1, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return RedirectToAction("Index", "Home");
			Chrome();
			const int size = 20;
			var now = DateTime.UtcNow;
			var d = await NoShowSvc.PageAsync(ctx, band, q, page, size, DeskClock(now).NowLocal, now, ct);
			if (d == null) return Forbid();
			ViewBag.Band = band ?? ""; ViewBag.Q = q; ViewBag.Page = Math.Max(1, page); ViewBag.Pages = Math.Max(1, (d.Total + size - 1) / size);
			return View(d);
		}
	}
}
