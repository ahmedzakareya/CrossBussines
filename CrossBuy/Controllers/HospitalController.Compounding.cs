using CrossBuy.BL;
using CrossBuy.BL.Hospital;
using CrossBuy.Models;
using Microsoft.AspNetCore.Mvc;

namespace CrossBuy.Controllers
{
	// ==========================================================================================
	// HOSPITAL H153: compounding — the worklist (cbServerList: searched, filtered, paged), the batch record, the independent
	// check that releases it, and the bag label (Report Studio). Every rule is HospitalCompoundingService's.
	// ==========================================================================================
	public partial class HospitalController
	{
		public sealed class HospitalCompoundCheckInput { public bool Release { get; set; } public string? Reason { get; set; } }

		private HospitalCompoundingService? _compound;
		private HospitalCompoundingService CompoundSvc => _compound ??= ActivatorUtilities.CreateInstance<HospitalCompoundingService>(HttpContext.RequestServices, _pharmacy);

		[HttpGet("Pharmacy/Compounding")]
		[HospitalPerm(HospitalActions.Read)]
		public async Task<IActionResult> Compounding(int? dispense, int? line, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return RedirectToAction("Index", "Home");
			Chrome();
			ViewBag.CanPharmacy = await _access.CanAsync(ctx, HospitalActions.Pharmacy, null, ct);
			// the dispense card's "prepare" opens the record for that line straight away
			if (dispense is int did && line is int lid)
			{
				var dd = await _pharmacy.RxAsync(ctx, did, DateTime.UtcNow, ct);
				var ll = dd?.Lines.FirstOrDefault(x => x.Line.ID == lid);
				if (dd != null && ll != null && HospitalCompoundStatusOpen(dd.Dispense.Status))
				{
					var req = await HospitalCompoundingService.RequiredAsync(_db, ctx, dd.Dispense, dd.Lines.Select(x => x.Line).ToList(), ct);
					ViewBag.OpenRecord = new { dispense = did, line = lid, product = ll.Drug, patient = DisplayName.Of(dd.Patient.Name, dd.Patient.NameEn) + " · " + dd.Patient.Mrn, required = req.ContainsKey(lid) };
				}
			}
			return View(await CompoundSvc.StatsAsync(ctx, DateTime.Today.ToUniversalTime(), ct));
		}

		private static bool HospitalCompoundStatusOpen(string status) => CrossBuy.Models.Context.Hospital.HospitalDispenseStatus.Dispensable(status);

		[HttpGet("Pharmacy/Compounding/Data")]
		[HospitalPerm(HospitalActions.Read)]
		public async Task<IActionResult> CompoundingData(string? q, string? filter, int page = 1, int pageSize = 25, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return NotFound();
			Paging(ref page, ref pageSize);
			var (rows, total) = await CompoundSvc.ListAsync(ctx, q, filter, page, pageSize, ct);
			PageHeaders(total, page, pageSize);
			ViewBag.CanPharmacy = await _access.CanAsync(ctx, HospitalActions.Pharmacy, null, ct);
			ViewBag.Me = ctx.EmployeeId;
			return PartialView("_CompoundingRows", rows);
		}

		[HttpPost("Pharmacy/{id:int}/Lines/{lineId:int}/Compound"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Pharmacy)]
		public async Task<IActionResult> PharmacyCompound(int id, int lineId, [FromBody] HospitalCompoundInput? input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null || input == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await CompoundSvc.RecordAsync(ctx, id, lineId, input, ctx.EmployeeId, DateTime.UtcNow, ct));
		}

		[HttpPost("Pharmacy/Compounding/{id:int}/Check"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Pharmacy)]
		public async Task<IActionResult> CompoundingCheck(int id, [FromBody] HospitalCompoundCheckInput? input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null || input == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await CompoundSvc.CheckAsync(ctx, id, input.Release, input.Reason, ctx.EmployeeId, DateTime.UtcNow, ct));
		}
	}
}
