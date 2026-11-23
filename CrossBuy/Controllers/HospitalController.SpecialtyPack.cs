using CrossBuy.BL;
using CrossBuy.BL.Hospital;
using CrossBuy.Models;
using CrossBuy.Models.Platform;
using Microsoft.AspNetCore.Mvc;

namespace CrossBuy.Controllers
{
	// HOSPITAL H268 — the specialty pack: the standard forms of the clinics that had none, laid in through the designer.
	public partial class HospitalController
	{
		[HttpPost("Specialty/Pack"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Manage)]
		public async Task<IActionResult> SpecialtyPack(CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return NotFound(new { ok = false, code = "not_found" });
			var svc = ActivatorUtilities.CreateInstance<HospitalSpecialtyPackService>(HttpContext.RequestServices, ContentSvc);
			var (r, done) = await svc.SeedAsync(ctx, ctx.EmployeeId, DateTime.UtcNow, ct);
			if (!r.Ok) return Result(r);
			return Json(new { ok = true, departments = done!.Departments, concepts = done.Concepts, templates = done.Templates, skipped = done.Skipped });
		}
	}
}
