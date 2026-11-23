using CrossBuy.BL;
using CrossBuy.BL.Hospital;
using CrossBuy.Models;
using CrossBuy.Models.Platform;
using Microsoft.AspNetCore.Mvc;

namespace CrossBuy.Controllers
{
	// HOSPITAL H260 — the structured operative note of a case and the worklist of notes still unsigned.
	public partial class HospitalController
	{
		private HospitalOperativeNoteService? _opNoteSvc;
		private HospitalOperativeNoteService OpNoteSvc => _opNoteSvc ??= ActivatorUtilities.CreateInstance<HospitalOperativeNoteService>(HttpContext.RequestServices);

		[HttpGet("Theatre/OperativeNotes"), HospitalPerm(HospitalActions.Read)]
		public async Task<IActionResult> OperativeNotes(string? q, string? view, int page = 1, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return RedirectToAction("Index", "Home");
			Chrome();
			const int size = 20;
			var d = await OpNoteSvc.WorklistAsync(ctx, q, view, page, size, DateTime.Now, ct);
			if (d == null) return Forbid();
			ViewBag.Q = q; ViewBag.View = view ?? ""; ViewBag.Page = Math.Max(1, page); ViewBag.Pages = Math.Max(1, (d.Total + size - 1) / size);
			return View(d);
		}

		[HttpGet("Theatre/{id:int}/OperativeNote"), HospitalPerm(HospitalActions.Read)]
		public async Task<IActionResult> OperativeNote(int id, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return RedirectToAction("Index", "Home");
			var v = await OpNoteSvc.GetAsync(ctx, id, ct);
			if (v == null) return NotFound();
			Chrome();
			ViewBag.Implants = (await ActivatorUtilities.CreateInstance<HospitalConsignmentService>(HttpContext.RequestServices, _billing).ListAsync(ctx, id, false, ct)).Rows;   // H38: implants used
			return View(v);
		}

		[HttpPost("Theatre/{id:int}/OperativeNote"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Consult)]
		public async Task<IActionResult> OperativeNoteSave(int id, [FromBody] HospitalOperativeNoteInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null || input == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await OpNoteSvc.SaveAsync(ctx, id, input, ctx.EmployeeId, DateTime.UtcNow, ct));
		}

		[HttpPost("Theatre/{id:int}/OperativeNote/Sign"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Consult)]
		public async Task<IActionResult> OperativeNoteSign(int id, [FromBody] HospitalOperativeNoteInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null || input == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await OpNoteSvc.SignAsync(ctx, id, input, DateTime.UtcNow, ct));
		}
	}
}
