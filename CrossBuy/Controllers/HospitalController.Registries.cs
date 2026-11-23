using CrossBuy.BL;
using CrossBuy.BL.Hospital;
using CrossBuy.Models;
using CrossBuy.Models.Platform;
using Microsoft.AspNetCore.Mvc;

namespace CrossBuy.Controllers
{
	// HOSPITAL H262 — disease registries: members, care gaps and the outreach to close them.
	public partial class HospitalController
	{
		private HospitalRegistryService? _registrySvc;
		private HospitalRegistryService RegistrySvc => _registrySvc ??= ActivatorUtilities.CreateInstance<HospitalRegistryService>(HttpContext.RequestServices);

		[HttpGet("Registries"), HospitalPerm(HospitalActions.Read)]
		public async Task<IActionResult> Registries(string? registry, string? gap, string? q, int page = 1, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return RedirectToAction("Index", "Home");
			Chrome();
			const int size = 20;
			var d = await RegistrySvc.PageAsync(ctx, registry, gap, q, page, size, DateTime.UtcNow, ct);
			if (d == null) return Forbid();
			ViewBag.Gap = gap ?? ""; ViewBag.Q = q; ViewBag.Page = Math.Max(1, page); ViewBag.Pages = Math.Max(1, (d.Total + size - 1) / size);
			ViewBag.CanReach = await _access.CanAsync(ctx, HospitalActions.Book, null, ct);
			return View(d);
		}

		[HttpPost("Registries/Outreach"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Book)]
		public async Task<IActionResult> RegistryOutreach([FromBody] HospitalRegistryOutreachInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null || input == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await RegistrySvc.OutreachAsync(ctx, input, DateTime.UtcNow, ct));
		}
	}
}
