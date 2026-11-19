using CrossBuy.BL;
using CrossBuy.BL.Hospital;
using CrossBuy.Models;
using CrossBuy.Models.Context.Hospital;
using CrossBuy.Models.Platform;
using Microsoft.AspNetCore.Mvc;

namespace CrossBuy.Controllers
{
	// HOSPITAL H213 — restraint and seclusion: the episodes (read with `clinical-read`), a doctor's order and renewal
	// (`consult`), and the nurses' checks and the end (`admit`, as the ward's other bedside records).
	public partial class HospitalController
	{
		public sealed class HospitalRestraintEndInput { public string? Reason { get; set; } }

		private HospitalRestraintService? _restraintSvc;
		private HospitalRestraintService RestraintSvc => _restraintSvc ??= ActivatorUtilities.CreateInstance<HospitalRestraintService>(HttpContext.RequestServices);

		[HttpGet("Wards/Restraints")]
		[HospitalPerm(HospitalActions.ClinicalRead)]
		public async Task<IActionResult> Restraints(string? q, bool ended = false, int page = 1, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return RedirectToAction("Index", "Home");
			Chrome();
			const int size = 20;
			var (rows, total, active, overdue, ready) = await RestraintSvc.ListAsync(ctx, q, ended, page, size, DateTime.UtcNow, ct);
			ViewBag.Q = q; ViewBag.Ended = ended; ViewBag.Page = Math.Max(1, page); ViewBag.Pages = Math.Max(1, (total + size - 1) / size); ViewBag.Total = total;
			ViewBag.Active = active; ViewBag.Overdue = overdue; ViewBag.Ready = ready;
			ViewBag.CanOrder = await _access.CanAsync(ctx, HospitalActions.Consult, null, ct);
			ViewBag.CanNurse = await _access.CanAsync(ctx, HospitalActions.Admit, null, ct);
			ViewBag.Inpatients = ready ? await RestraintSvc.InpatientsAsync(ctx, ct) : Array.Empty<(int, string, string, string, string)>();
			return View(rows);
		}

		[HttpPost("Wards/Restraints"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Consult)]
		public async Task<IActionResult> RestraintStart([FromBody] HospitalRestraintInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null || input == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await RestraintSvc.StartAsync(ctx, input, ctx.EmployeeId, DateTime.UtcNow, ct));
		}

		[HttpPost("Wards/Restraints/{id:int}/Check"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Admit)]
		public async Task<IActionResult> RestraintCheck(int id, [FromBody] HospitalRestraintCheckInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null || input == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await RestraintSvc.CheckAsync(ctx, id, input, ctx.EmployeeId, DateTime.UtcNow, ct));
		}

		[HttpPost("Wards/Restraints/{id:int}/Renew"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Consult)]
		public async Task<IActionResult> RestraintRenew(int id, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await RestraintSvc.RenewAsync(ctx, id, ctx.EmployeeId, DateTime.UtcNow, ct));
		}

		[HttpPost("Wards/Restraints/{id:int}/End"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Admit)]
		public async Task<IActionResult> RestraintEnd(int id, [FromBody] HospitalRestraintEndInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null || input == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await RestraintSvc.EndAsync(ctx, id, input.Reason, ctx.EmployeeId, DateTime.UtcNow, ct));
		}
	}
}
