using CrossBuy.BL;
using CrossBuy.BL.Hospital;
using CrossBuy.Models;
using CrossBuy.Models.Platform;
using Microsoft.AspNetCore.Mvc;

namespace CrossBuy.Controllers
{
	// HOSPITAL H245 — the biomedical register over the fixed assets: devices, events, preventive maintenance.
	public partial class HospitalController
	{
		private HospitalBiomedService? _biomedSvc;
		private HospitalBiomedService BiomedSvc => _biomedSvc ??= ActivatorUtilities.CreateInstance<HospitalBiomedService>(HttpContext.RequestServices);

		[HttpGet("Biomed"), HospitalPerm(HospitalActions.Read)]
		public async Task<IActionResult> Biomed(string? q, string? view, int page = 1, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return RedirectToAction("Index", "Home");
			Chrome();
			const int size = 20;
			var d = await BiomedSvc.PageAsync(ctx, q, view, page, size, DateTime.UtcNow, ct);
			ViewBag.Q = q; ViewBag.View = view ?? ""; ViewBag.Page = Math.Max(1, page); ViewBag.Pages = Math.Max(1, (d.Total + size - 1) / size);
			ViewBag.CanManage = await _access.CanAsync(ctx, HospitalActions.Manage, null, ct);
			return View(d);
		}

		[HttpPost("Biomed/Devices"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Manage)]
		public async Task<IActionResult> BiomedDeviceSave([FromBody] HospitalBiomedDeviceInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null || input == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await BiomedSvc.SaveDeviceAsync(ctx, input, DateTime.UtcNow, ct));
		}

		[HttpPost("Biomed/Devices/{id:int}/Event"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Manage)]
		public async Task<IActionResult> BiomedEvent(int id, [FromBody] HospitalBiomedEventInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null || input == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await BiomedSvc.EventAsync(ctx, id, input, ctx.EmployeeId, DateTime.UtcNow, ct));
		}

		[HttpPost("Biomed/Devices/{id:int}/Pm"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Manage)]
		public async Task<IActionResult> BiomedPm(int id, [FromBody] HospitalBiomedPmInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null || input == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await BiomedSvc.PmDoneAsync(ctx, id, input, ctx.EmployeeId, DateTime.UtcNow, ct));
		}
	}
}
