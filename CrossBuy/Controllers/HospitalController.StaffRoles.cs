using CrossBuy.BL;
using CrossBuy.BL.Hospital;
using CrossBuy.Models;
using Microsoft.AspNetCore.Mvc;

namespace CrossBuy.Controllers
{
	// HOSPITAL H194 — the hospital team's roles, given together (HospitalStaffRolesService). The page and the grant ask
	// `manage`; the grant itself is the platform's, which only a company or platform administrator may make.
	public partial class HospitalController
	{
		[HttpGet("StaffRoles")]
		[HospitalPerm(HospitalActions.Manage)]
		public async Task<IActionResult> StaffRoles(string? add, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return RedirectToAction("Index", "Home");
			Chrome();
			var extra = (add ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries).Select(x => int.TryParse(x, out var n) ? n : 0).Where(n => n > 0).Distinct().Take(50).ToList();
			var page = await ActivatorUtilities.CreateInstance<HospitalStaffRolesService>(HttpContext.RequestServices).PageAsync(ctx, extra, DateTime.UtcNow, ct);
			if (page == null) return Forbid();
			ViewBag.Add = string.Join(",", extra);
			return View(page);
		}

		[HttpGet("StaffRoles/Search")]
		[HospitalPerm(HospitalActions.Manage)]
		public async Task<IActionResult> StaffRolesSearch(string? q, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return NotFound(new { ok = false, code = "not_found" });
			var rows = await ActivatorUtilities.CreateInstance<HospitalStaffRolesService>(HttpContext.RequestServices).SearchAsync(ctx, q, ct);
			return Json(new { ok = true, results = rows.Select(r => new { id = r.Id, text = DisplayName.Of(r.Name, r.NameEn), initials = HospitalUi.Initials(r.NameEn ?? r.Name) }) });
		}

		[HttpPost("StaffRoles/Grant"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Manage)]
		public async Task<IActionResult> StaffRolesGrant([FromBody] HospitalStaffGrantInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null || input == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await ActivatorUtilities.CreateInstance<HospitalStaffRolesService>(HttpContext.RequestServices).GrantAsync(ctx, input, DateTime.UtcNow, ct));
		}
	}
}
