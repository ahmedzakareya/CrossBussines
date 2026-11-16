using CrossBuy.BL;
using CrossBuy.BL.Hospital;
using CrossBuy.Models;
using CrossBuy.Models.Context.Hospital;
using CrossBuy.Models.Platform;
using Microsoft.AspNetCore.Mvc;

namespace CrossBuy.Controllers
{
	// ==========================================================================================
	// HOSPITAL — H10: the pharmacy. The dispensing queue, the review, the dispense (stock out through the
	// inventory, a charge on the bill) and the pharmacy store setting. Every rule is HospitalPharmacyService's.
	// ==========================================================================================
	public partial class HospitalController
	{
		public sealed class HospitalRxVerifyInput { public string? Note { get; set; } }
		public sealed class HospitalPharmacySettingsInput { public int? WarehouseId { get; set; } public int NearExpiryDays { get; set; } = 60; public int? QuarantineWarehouseId { get; set; } }

		[HttpGet("Pharmacy")]
		[HospitalPerm(HospitalActions.Read)]
		public async Task<IActionResult> Pharmacy(int? rx, string? status, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return RedirectToAction("Index", "Home");
			await _pharmacy.SyncAsync(ctx, ct);
			Chrome();
			ViewBag.Status = HospitalDispenseStatus.All.Contains(status ?? "") ? status : null;
			ViewBag.Stats = await _pharmacy.StatsAsync(ctx, DateTime.Today.ToUniversalTime(), DateTime.UtcNow, ct);
			var queue = await _pharmacy.QueueAsync(ctx, ViewBag.Status as string, ct);
			int? pick = rx ?? queue.FirstOrDefault(q => q.Status != HospitalDispenseStatus.Dispensed)?.Id ?? queue.FirstOrDefault()?.Id;
			ViewBag.Selected = pick == null ? null : await _pharmacy.RxAsync(ctx, pick.Value, DateTime.UtcNow, ct);
			if (ViewBag.Selected is HospitalRxView sel)
			{
				// H136: what the review weighs, and what the pharmacist raised
				ViewBag.Clinical = await ReviewSvc.ClinicalAsync(ctx, sel.Patient, DateTime.UtcNow, ct);
				ViewBag.Interventions = await ReviewSvc.InterventionsAsync(ctx, sel.Dispense.ID, ct);
				ViewBag.Returns = await ReturnSvc.ListAsync(ctx, sel.Dispense.ID, ct);       // H146
				ViewBag.Returned = await ReturnSvc.ReturnedAsync(ctx, sel.Dispense.ID, ct);
			}
			ViewBag.Settings = await _pharmacy.SettingsAsync(ctx, ct);
			ViewBag.Warehouses = await _pharmacy.WarehousesAsync(ctx, ct);
			ViewBag.CanPharmacy = await _access.CanAsync(ctx, HospitalActions.Pharmacy, null, ct);
			ViewBag.CanManage = await _access.CanAsync(ctx, HospitalActions.Manage, null, ct);
			return View(queue);
		}

		[HttpPost("Pharmacy/{id:int}/Verify"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Pharmacy)]
		public async Task<IActionResult> PharmacyVerify(int id, [FromBody] HospitalRxVerifyInput? input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await _pharmacy.VerifyAsync(ctx, id, input?.Note, ctx.EmployeeId, DateTime.UtcNow, ct));
		}

		/// H145: the quantity to give now per line (absent = all that is owed); the rest stays owed.
		public sealed class HospitalDispenseInput { public Dictionary<int, decimal>? Quantities { get; set; } }

		[HttpPost("Pharmacy/{id:int}/Dispense"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Pharmacy)]
		public async Task<IActionResult> PharmacyDispense(int id, [FromBody] HospitalDispenseInput? input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await _pharmacy.DispenseAsync(ctx, id, ctx.EmployeeId, ctx.UserId, DateTime.UtcNow, ct, input?.Quantities));
		}

		[HttpPost("Pharmacy/Settings"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Manage)]
		public async Task<IActionResult> PharmacySettings([FromBody] HospitalPharmacySettingsInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await _pharmacy.SaveSettingsAsync(ctx, input?.WarehouseId, input?.NearExpiryDays ?? 60, ct, input?.QuarantineWarehouseId));
		}
	}
}
