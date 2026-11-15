using CrossBuy.BL;
using CrossBuy.BL.Hospital;
using CrossBuy.Models;
using Microsoft.AspNetCore.Mvc;

namespace CrossBuy.Controllers
{
	// ==========================================================================================
	// HOSPITAL — H88: oncology. The active courses (searched, filtered, paged), the regimen library (searched, paged), one
	// course with its cycles; starting and ending a course, saving a regimen, previewing a cycle's doses and checks,
	// giving it, and holding it.
	// ==========================================================================================
	public partial class HospitalController
	{
		private HospitalOncologyService? _oncSvc;
		private HospitalOncologyService OncSvc => _oncSvc ??= ActivatorUtilities.CreateInstance<HospitalOncologyService>(HttpContext.RequestServices);

		[HttpGet("Oncology")]
		[HospitalPerm(HospitalActions.Read)]
		public async Task<IActionResult> Oncology(CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return RedirectToAction("Index", "Home");
			Chrome();
			var (_, _, ready) = await OncSvc.CoursesAsync(ctx, null, null, 1, 1, DateTime.UtcNow, ct);
			ViewBag.Ready = ready;
			ViewBag.CanTreat = await OncSvc.CanTreatAsync(ctx, ct);
			ViewBag.CanLibrary = await OncSvc.CanLibraryAsync(ctx, ct);
			ViewBag.Regimens = await OncSvc.ActiveRegimensAsync(ctx, ct);
			ViewBag.UnitSettings = await UnitSettingsSvc.ViewAsync(ctx, HospitalSessionKinds.Chemo, ct);   // H123
			return View();
		}

		[HttpGet("Oncology/Data")]
		[HospitalPerm(HospitalActions.Read)]
		public async Task<IActionResult> OncologyData(string? q, string? filter, int page = 1, int pageSize = 25, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return NotFound();
			Paging(ref page, ref pageSize);
			var (rows, total, _) = await OncSvc.CoursesAsync(ctx, q, filter, page, pageSize, DateTime.UtcNow, ct);
			PageHeaders(total, page, pageSize);
			ViewBag.Today = HospitalNursingService.UtcToWard(DateTime.UtcNow).Date;
			return PartialView("_ChemoCourseRows", rows);
		}

		[HttpGet("Oncology/Regimens")]
		[HospitalPerm(HospitalActions.Read)]
		public async Task<IActionResult> ChemoRegimens(CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return RedirectToAction("Index", "Home");
			Chrome();
			var (_, _, ready) = await OncSvc.RegimensAsync(ctx, null, 1, 1, ct);
			ViewBag.Ready = ready;
			ViewBag.CanLibrary = await OncSvc.CanLibraryAsync(ctx, ct);
			// H120: the pharmacy's stock link for each drug the regimens name
			var (links, linksReady) = await ChemoPharmacySvc.LinksAsync(ctx, ct);
			ViewBag.DrugLinks = links; ViewBag.DrugLinksReady = linksReady;
			ViewBag.CanPharmacy = await _access.CanAsync(ctx, HospitalActions.Pharmacy, null, ct);
			ViewBag.Medicines = await _pharmacy.FormularyAsync(ctx, ct);
			return View();
		}

		private HospitalChemoPharmacyService? _chemoPharmacySvc;
		private HospitalChemoPharmacyService ChemoPharmacySvc => _chemoPharmacySvc ??= ActivatorUtilities.CreateInstance<HospitalChemoPharmacyService>(HttpContext.RequestServices);

		/// H120: links a chemotherapy drug to the stock item the pharmacy prepares it from (no item = unlink).
		[HttpPost("Oncology/DrugLinks"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Pharmacy)]
		public async Task<IActionResult> ChemoDrugLink([FromBody] HospitalChemoDrugLinkInput? input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null || input == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await ChemoPharmacySvc.SaveLinkAsync(ctx, input, ctx.EmployeeId, DateTime.UtcNow, ct));
		}

		[HttpGet("Oncology/Regimens/Data")]
		[HospitalPerm(HospitalActions.Read)]
		public async Task<IActionResult> ChemoRegimensData(string? q, int page = 1, int pageSize = 25, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return NotFound();
			Paging(ref page, ref pageSize);
			var (rows, total, _) = await OncSvc.RegimensAsync(ctx, q, page, pageSize, ct);
			PageHeaders(total, page, pageSize);
			ViewBag.CanLibrary = await OncSvc.CanLibraryAsync(ctx, ct);
			return PartialView("_ChemoRegimenRows", rows);
		}

		[HttpGet("Oncology/Course/{id:int}")]
		[HospitalPerm(HospitalActions.Read)]
		public async Task<IActionResult> ChemoCourse(int id, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return RedirectToAction("Index", "Home");
			var page = await OncSvc.CoursePageAsync(ctx, id, DateTime.UtcNow, ct);
			if (page == null) return NotFound();
			Chrome();
			ViewBag.CanTreat = await OncSvc.CanTreatAsync(ctx, ct);
			ViewBag.Today = HospitalNursingService.UtcToWard(DateTime.UtcNow).Date;
			// H130: what each cycle draws from the oncology unit's store
			ViewBag.KitCard = new HospitalSessionKitCard(HospitalSessionKinds.Chemo, await SessionKitSvc.KitAsync(ctx, HospitalSessionKinds.Chemo, null, ct), null, false,
				await _access.CanAsync(ctx, HospitalActions.Manage, null, ct));
			return View(page);
		}

		[HttpPost("Oncology/Regimens"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Manage)]
		public async Task<IActionResult> SaveChemoRegimen([FromBody] HospitalChemoRegimenInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null || input == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await OncSvc.SaveRegimenAsync(ctx, input, ctx.EmployeeId, DateTime.UtcNow, ct));
		}

		[HttpPost("Oncology/Courses"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Consult)]
		public async Task<IActionResult> StartChemoCourse([FromBody] HospitalChemoCourseInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null || input == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await OncSvc.StartCourseAsync(ctx, input, ctx.EmployeeId, DateTime.UtcNow, ct));
		}

		[HttpPost("Oncology/Course/{id:int}/End"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Consult)]
		public async Task<IActionResult> EndChemoCourse(int id, [FromBody] HospitalChemoEndInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null || input == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await OncSvc.EndCourseAsync(ctx, id, input, ctx.EmployeeId, DateTime.UtcNow, ct));
		}

		/// Writes nothing: the doses and the checks for the numbers on the form.
		[HttpPost("Oncology/Course/{id:int}/Preview"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Read)]
		public async Task<IActionResult> PreviewChemoCycle(int id, [FromBody] HospitalChemoCycleInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null || input == null) return NotFound(new { ok = false, code = "not_found" });
			var pv = await OncSvc.PreviewAsync(ctx, id, input, DateTime.UtcNow, ct);
			if (pv == null) return NotFound(new { ok = false, code = "not_found" });
			return Json(new
			{
				ok = true, cycle = pv.CycleNo, bsa = pv.Bsa, gfr = pv.Gfr, refusal = pv.Refusal == null ? null : _t[pv.Refusal].Value,
				checks = pv.Checks.Select(c => new { code = c, blocks = HospitalChemoCheck.Blocks(c), text = _t["chk:" + c].Value }),
				doses = pv.Doses.Select(x => new { x.DrugName, x.Dose, basis = _t["cbs:" + x.Basis].Value, x.Route, x.Days, x.DoseMg, x.CycleMgM2, x.LifetimeMgM2, x.PriorMgM2 }),
			});
		}

		[HttpPost("Oncology/Course/{id:int}/Give"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Consult)]
		public async Task<IActionResult> GiveChemoCycle(int id, [FromBody] HospitalChemoCycleInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null || input == null) return NotFound(new { ok = false, code = "not_found" });
			// H130: the cycle given draws the oncology unit's kit
			return await CompletedWithKitAsync(ctx, HospitalSessionKinds.Chemo, await OncSvc.GiveAsync(ctx, id, input, ctx.EmployeeId, DateTime.UtcNow, ct), ct);
		}

		[HttpPost("Oncology/Course/{id:int}/Hold"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Consult)]
		public async Task<IActionResult> HoldChemoCycle(int id, [FromBody] HospitalChemoHoldInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null || input == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await OncSvc.HoldAsync(ctx, id, input, ctx.EmployeeId, DateTime.UtcNow, ct));
		}
	}
}
