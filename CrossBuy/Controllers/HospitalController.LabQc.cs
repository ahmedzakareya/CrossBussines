using CrossBuy.BL;
using CrossBuy.BL.Hospital;
using CrossBuy.Models;
using CrossBuy.Models.Platform;
using Microsoft.AspNetCore.Mvc;

namespace CrossBuy.Controllers
{
	// HOSPITAL H232 — laboratory quality control: the materials with their Levey-Jennings chart, a run recorded at the
	// bench (`lab`), a material set or retired by the specialist (`lab-verify`) or the administrator.
	public partial class HospitalController
	{
		private HospitalLabQcService? _qcSvc;
		private HospitalLabQcService QcSvc => _qcSvc ??= ActivatorUtilities.CreateInstance<HospitalLabQcService>(HttpContext.RequestServices);
		public sealed class HospitalQcRunInput { public decimal? Value { get; set; } public string? Action { get; set; } }

		[HttpGet("Lab/Qc"), HospitalPerm(HospitalActions.Lab)]
		public async Task<IActionResult> LabQc(string? q, int page = 1, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return RedirectToAction("Index", "Home");
			Chrome();
			const int size = 12;
			var p = await QcSvc.PageAsync(ctx, q, page, size, ct);
			ViewBag.Q = q; ViewBag.Page = Math.Max(1, page); ViewBag.Pages = Math.Max(1, (p.Total + size - 1) / size);
			ViewBag.CanSet = await _access.CanAsync(ctx, HospitalActions.LabVerify, null, ct) || await _access.CanAsync(ctx, HospitalActions.Manage, null, ct);
			return View(p);
		}

		[HttpPost("Lab/Qc/Materials"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Lab)]
		public async Task<IActionResult> LabQcMaterial([FromBody] HospitalQcMaterialInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null || input == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await QcSvc.SaveMaterialAsync(ctx, input, ctx.EmployeeId, DateTime.UtcNow, ct));
		}

		[HttpPost("Lab/Qc/Materials/{id:int}/Retire"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Lab)]
		public async Task<IActionResult> LabQcRetire(int id, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await QcSvc.RetireMaterialAsync(ctx, id, ct));
		}

		[HttpPost("Lab/Qc/Materials/{id:int}/Runs"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Lab)]
		public async Task<IActionResult> LabQcRun(int id, [FromBody] HospitalQcRunInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null || input == null) return NotFound(new { ok = false, code = "not_found" });
			if (input.Value is not decimal v) return Json(new { ok = false, error = _t["Enter the measured value"].Value });
			var (r, status, violations) = await QcSvc.RecordRunAsync(ctx, id, v, input.Action, ctx.EmployeeId, DateTime.UtcNow, ct);
			if (!r.Ok) return Result(r);
			return Json(new { ok = true, status, statusText = _t["qc:" + status].Value, violations });
		}
	}
}
