using CrossBuy.BL;
using CrossBuy.BL.Hospital;
using CrossBuy.Models;
using CrossBuy.Models.Platform;
using Microsoft.AspNetCore.Mvc;

namespace CrossBuy.Controllers
{
	// HOSPITAL H241 — DRG coding of discharged stays and the facility's group table. Coding is the biller's (`bill`);
	// the groups are the administrator's (`manage`).
	public partial class HospitalController
	{
		private HospitalDrgService? _drgSvc;
		private HospitalDrgService DrgSvc => _drgSvc ??= ActivatorUtilities.CreateInstance<HospitalDrgService>(HttpContext.RequestServices);

		[HttpGet("Insurance/Drg"), HospitalPerm(HospitalActions.Bill)]
		public async Task<IActionResult> Drg(string? q, string? view, int page = 1, string? gq = null, int gpage = 1, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return RedirectToAction("Index", "Home");
			Chrome();
			const int size = 15;
			var d = await DrgSvc.PageAsync(ctx, q, view, page, size, gq, gpage, DateTime.UtcNow, ct);
			if (d == null) return Forbid();
			ViewBag.Q = q; ViewBag.View = view ?? ""; ViewBag.Page = Math.Max(1, page); ViewBag.Pages = Math.Max(1, (d.StayTotal + size - 1) / size);
			ViewBag.Gq = gq; ViewBag.GPage = Math.Max(1, gpage); ViewBag.GPages = Math.Max(1, (d.GroupTotal + size - 1) / size);
			ViewBag.CanManage = await _access.CanAsync(ctx, HospitalActions.Manage, null, ct);
			return View(d);
		}

		[HttpPost("Insurance/Drg/{admissionId:int}/Code"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Bill)]
		public async Task<IActionResult> DrgCode(int admissionId, [FromBody] HospitalDrgAssignInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null || input == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await DrgSvc.AssignAsync(ctx, admissionId, input, ctx.EmployeeId, DateTime.UtcNow, ct));
		}

		[HttpPost("Insurance/Drg/Groups"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Manage)]
		public async Task<IActionResult> DrgGroupSave([FromBody] HospitalDrgInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null || input == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await DrgSvc.SaveGroupAsync(ctx, input, DateTime.UtcNow, ct));
		}
	}
}
