using CrossBuy.BL;
using CrossBuy.BL.Hospital;
using CrossBuy.Models;
using Microsoft.AspNetCore.Mvc;

namespace CrossBuy.Controllers
{
	// ==========================================================================================
	// HOSPITAL — H132: the pharmacy's own stock, inside the hospital — its medicines, their batches and expiry dates, and
	// the movements in and out of the pharmacy store. One tab per list (?tab=), each searched, filtered and paged.
	// ==========================================================================================
	public partial class HospitalController
	{
		public static class PharmacyStockTabs
		{
			public const string Medicines = "medicines", Batches = "batches", Movements = "movements";
			public static readonly IReadOnlyList<string> All = new[] { Medicines, Batches, Movements };
		}

		private HospitalPharmacyStockService? _pharmacyStockSvc;
		private HospitalPharmacyStockService PharmacyStockSvc => _pharmacyStockSvc ??= ActivatorUtilities.CreateInstance<HospitalPharmacyStockService>(HttpContext.RequestServices, _pharmacy);

		[HttpGet("Pharmacy/Stock")]
		[HospitalPerm(HospitalActions.Read)]
		public async Task<IActionResult> PharmacyStock(string? tab, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return RedirectToAction("Index", "Home");
			Chrome();
			var today = HospitalNursingService.UtcToWard(DateTime.UtcNow).Date;
			ViewBag.Tab = PharmacyStockTabs.All.Contains(tab ?? "") ? tab : PharmacyStockTabs.Medicines;
			ViewBag.Today = today;
			return View(await PharmacyStockSvc.StatsAsync(ctx, today, ct));
		}

		[HttpGet("Pharmacy/Stock/Data")]
		[HospitalPerm(HospitalActions.Read)]
		public async Task<IActionResult> PharmacyStockData(string? tab, string? q, string? filter, int page = 1, int pageSize = 25, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return NotFound();
			Paging(ref page, ref pageSize);
			var today = HospitalNursingService.UtcToWard(DateTime.UtcNow).Date;
			ViewBag.Today = today;
			switch (tab)
			{
				case PharmacyStockTabs.Batches:
				{
					var (rows, total) = await PharmacyStockSvc.BatchesAsync(ctx, q, filter, page, pageSize, today, ct);
					PageHeaders(total, page, pageSize);
					return PartialView("_PharmacyBatchRows", rows);
				}
				case PharmacyStockTabs.Movements:
				{
					var (rows, total) = await PharmacyStockSvc.MovementsAsync(ctx, q, filter, page, pageSize, ct);
					PageHeaders(total, page, pageSize);
					return PartialView("_PharmacyMoveRows", rows);
				}
				default:
				{
					var (rows, total) = await PharmacyStockSvc.MedicinesAsync(ctx, q, filter, page, pageSize, today, ct);
					PageHeaders(total, page, pageSize);
					return PartialView("_PharmacyMedRows", rows);
				}
			}
		}
	}
}
