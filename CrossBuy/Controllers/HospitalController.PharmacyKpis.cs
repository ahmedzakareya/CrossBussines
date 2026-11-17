using CrossBuy.BL;
using CrossBuy.BL.Hospital;
using CrossBuy.Models;
using Microsoft.AspNetCore.Mvc;

namespace CrossBuy.Controllers
{
	// ==========================================================================================
	// HOSPITAL H151: the pharmacy's indicators and its ABC/VEN matrix (HospitalPharmacyKpiService decides every figure);
	// the medicines list behind the matrix is searched and paged by cbServerList.
	// ==========================================================================================
	public partial class HospitalController
	{
		private HospitalPharmacyKpiService? _kpis;
		private HospitalPharmacyKpiService KpiSvc => _kpis ??= ActivatorUtilities.CreateInstance<HospitalPharmacyKpiService>(HttpContext.RequestServices, _pharmacy);

		[HttpGet("Pharmacy/Indicators")]
		[HospitalPerm(HospitalActions.Read)]
		public async Task<IActionResult> PharmacyIndicators(int days = 90, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return RedirectToAction("Index", "Home");
			Chrome();
			ViewBag.Currency = await _facility.CurrencyAsync(ctx, ct);
			return View(await KpiSvc.KpisAsync(ctx, days, DateTime.UtcNow, ct));
		}

		[HttpGet("Pharmacy/Indicators/Data")]
		[HospitalPerm(HospitalActions.Read)]
		public async Task<IActionResult> PharmacyIndicatorsData(string? q, string? cell, string? category, int page = 1, int pageSize = 25, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return NotFound();
			Paging(ref page, ref pageSize);
			var (rows, total) = await KpiSvc.RowsAsync(ctx, q, cell, category, page, pageSize, DateTime.UtcNow, ct);
			PageHeaders(total, page, pageSize);
			ViewBag.Currency = await _facility.CurrencyAsync(ctx, ct);
			return PartialView("_PharmacyAbcRows", rows);
		}
	}
}
