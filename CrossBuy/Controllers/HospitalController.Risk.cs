using CrossBuy.BL;
using CrossBuy.BL.Hospital;
using CrossBuy.Models;
using CrossBuy.Models.Platform;
using Microsoft.AspNetCore.Mvc;

namespace CrossBuy.Controllers
{
	// HOSPITAL H253 — predictive models validated on this hospital's own patients (readmission: LACE).
	public partial class HospitalController
	{
		[HttpGet("RiskModels"), HospitalPerm(HospitalActions.ClinicalRead)]
		public async Task<IActionResult> RiskModels(string? q, int page = 1, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return RedirectToAction("Index", "Home");
			Chrome();
			const int size = 20;
			var d = await ActivatorUtilities.CreateInstance<HospitalRiskService>(HttpContext.RequestServices).PageAsync(ctx, q, page, size, DateTime.UtcNow, ct);
			if (d == null) return Forbid();
			ViewBag.Q = q; ViewBag.Page = Math.Max(1, page); ViewBag.Pages = Math.Max(1, (d.InpatientTotal + size - 1) / size);
			return View(d);
		}
	}
}
