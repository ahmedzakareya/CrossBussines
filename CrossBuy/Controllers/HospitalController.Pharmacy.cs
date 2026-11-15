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

		[HttpPost("Pharmacy/{id:int}/Dispense"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Pharmacy)]
		public async Task<IActionResult> PharmacyDispense(int id, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await _pharmacy.DispenseAsync(ctx, id, ctx.EmployeeId, ctx.UserId, DateTime.UtcNow, ct));
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
