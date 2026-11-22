using CrossBuy.BL;
using CrossBuy.BL.Hospital;
using CrossBuy.Models;
using CrossBuy.Models.Context.Hospital;
using CrossBuy.Models.Platform;
using Microsoft.AspNetCore.Mvc;

namespace CrossBuy.Controllers
{
	// HOSPITAL H215 — the administrator's side of the interface channel: the machines allowed to talk to the hospital,
	// a new one (its key shown once), a new key, and switching one off. All `manage`.
	public partial class HospitalController
	{
		private HospitalInterfaceService? _ifSvc;
		private HospitalInterfaceService IfSvc => _ifSvc ??= ActivatorUtilities.CreateInstance<HospitalInterfaceService>(HttpContext.RequestServices);

		[HttpGet("Interfaces")]
		[HospitalPerm(HospitalActions.Manage)]
		public async Task<IActionResult> Interfaces(string? q, int page = 1, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return RedirectToAction("Index", "Home");
			Chrome();
			const int size = 20;
			var (rows, total, ready) = await IfSvc.ListAsync(ctx, q, page, size, ct);
			ViewBag.Q = q; ViewBag.Page = Math.Max(1, page); ViewBag.Pages = Math.Max(1, (total + size - 1) / size); ViewBag.Total = total; ViewBag.Ready = ready;
			ViewBag.Base = $"{Request.Scheme}://{Request.Host}{Url.Content("~/api/hospital/interfaces/")}";
			ViewBag.Moh = await MohSvc.SettingsAsync(ctx, ct);   // H229
			return View(rows);
		}

		// H229: the Ministry of Health gateway's address and key, kept here by the administrator instead of in server settings.
		private HospitalMohGatewayService MohSvc => ActivatorUtilities.CreateInstance<HospitalMohGatewayService>(HttpContext.RequestServices);

		[HttpPost("Interfaces/Moh"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Manage)]
		public async Task<IActionResult> InterfaceMohSave([FromBody] HospitalMohSettingsInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null || input == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await MohSvc.SaveSettingsAsync(ctx, input, ctx.EmployeeId, DateTime.UtcNow, ct));
		}

		[HttpPost("Interfaces"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Manage)]
		public async Task<IActionResult> InterfaceCreate([FromBody] HospitalInterfaceInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null || input == null) return NotFound(new { ok = false, code = "not_found" });
			var (r, key) = await IfSvc.CreateAsync(ctx, input, ctx.EmployeeId, DateTime.UtcNow, ct);
			return r.Ok ? Json(new { ok = true, id = r.Id, key }) : Result(r);
		}

		[HttpPost("Interfaces/{id:int}/Rotate"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Manage)]
		public async Task<IActionResult> InterfaceRotate(int id, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return NotFound(new { ok = false, code = "not_found" });
			var (r, key) = await IfSvc.RotateAsync(ctx, id, ct);
			return r.Ok ? Json(new { ok = true, id = r.Id, key }) : Result(r);
		}

		[HttpPost("Interfaces/{id:int}/Toggle"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Manage)]
		public async Task<IActionResult> InterfaceToggle(int id, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await IfSvc.ToggleAsync(ctx, id, ct));
		}
	}
}
