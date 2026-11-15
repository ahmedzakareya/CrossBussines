using CrossBuy.BL;
using CrossBuy.BL.Hospital;
using CrossBuy.Models;
using Microsoft.AspNetCore.Mvc;

namespace CrossBuy.Controllers
{
	// ==========================================================================================
	// HOSPITAL H133: the drug master (formulary). One row per medicine, describing:
	//   · its generic name and Tall Man spelling;
	//   · strength, form, routes and ATC;
	//   · formulary status and safety flags.
	// The list is searched, filtered and paged. The pharmacist edits a medicine in the theme's modal.
	// ==========================================================================================
	public partial class HospitalController
	{
		private HospitalDrugMasterService? _drugMaster;
		private HospitalDrugMasterService DrugMaster => _drugMaster ??= ActivatorUtilities.CreateInstance<HospitalDrugMasterService>(HttpContext.RequestServices);

		[HttpGet("Pharmacy/Formulary")]
		[HospitalPerm(HospitalActions.Read)]
		public async Task<IActionResult> Formulary(CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return RedirectToAction("Index", "Home");
			Chrome();
			ViewBag.CanPharmacy = await _access.CanAsync(ctx, HospitalActions.Pharmacy, null, ct);
			return View(await DrugMaster.StatsAsync(ctx, ct));
		}

		[HttpGet("Pharmacy/Formulary/Data")]
		[HospitalPerm(HospitalActions.Read)]
		public async Task<IActionResult> FormularyData(string? q, string? filter, int page = 1, int pageSize = 25, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return NotFound();
			Paging(ref page, ref pageSize);
			var (rows, total) = await DrugMaster.ListAsync(ctx, q, filter, page, pageSize, ct);
			PageHeaders(total, page, pageSize);
			ViewBag.CanPharmacy = await _access.CanAsync(ctx, HospitalActions.Pharmacy, null, ct);
			return PartialView("_FormularyRows", rows);
		}

		[HttpPost("Pharmacy/Formulary"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Pharmacy)]
		public async Task<IActionResult> FormularySave([FromBody] HospitalDrugInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await DrugMaster.SaveAsync(ctx, input, ctx.EmployeeId, DateTime.UtcNow, ct));
		}
	}
}
