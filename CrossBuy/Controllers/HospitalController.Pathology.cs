using CrossBuy.BL;
using CrossBuy.BL.Hospital;
using CrossBuy.Models;
using CrossBuy.Models.Context.Hospital;
using CrossBuy.Models.Platform;
using Microsoft.AspNetCore.Mvc;

namespace CrossBuy.Controllers
{
	// HOSPITAL H216 — anatomic pathology: the cases (read with `clinical-read`), a request (`consult`), the laboratory's
	// steps and amendments (`lab`; the service checks each), and a request's cancellation (`consult`).
	public partial class HospitalController
	{
		public sealed class HospitalPathAmendInput { public string? Text { get; set; } public string? Reason { get; set; } }

		private HospitalPathologyService? _pathSvc;
		private HospitalPathologyService PathSvc => _pathSvc ??= ActivatorUtilities.CreateInstance<HospitalPathologyService>(HttpContext.RequestServices);

		[HttpGet("Pathology")]
		[HospitalPerm(HospitalActions.ClinicalRead)]
		public async Task<IActionResult> Pathology(string? q, string? status, int? patient = null, int page = 1, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return RedirectToAction("Index", "Home");
			Chrome();
			const int size = 20;
			if (!string.IsNullOrEmpty(status) && !HospitalPathStatus.All.Contains(status)) status = null;
			var (rows, total, open, late, ready) = await PathSvc.ListAsync(ctx, q, status, patient, page, size, DateTime.UtcNow, ct);
			ViewBag.Q = q; ViewBag.Status = status; ViewBag.Patient = patient; ViewBag.Page = Math.Max(1, page); ViewBag.Pages = Math.Max(1, (total + size - 1) / size);
			ViewBag.Total = total; ViewBag.Open = open; ViewBag.Late = late; ViewBag.Ready = ready;
			ViewBag.CanLab = await _access.CanAsync(ctx, HospitalActions.Lab, null, ct);
			ViewBag.CanConsult = await _access.CanAsync(ctx, HospitalActions.Consult, null, ct);
			var amend = new Dictionary<int, IReadOnlyList<HospitalPathAmendment>>();
			foreach (var r in rows.Where(r => r.Amendments > 0)) amend[r.Case.ID] = await PathSvc.AmendmentsAsync(ctx, r.Case.ID, ct);
			ViewBag.AmendmentsOf = amend;
			return View(rows);
		}

		[HttpPost("Pathology"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Consult)]
		public async Task<IActionResult> PathologyRequest([FromBody] HospitalPathRequestInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null || input == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await PathSvc.RequestAsync(ctx, input, ctx.EmployeeId, DateTime.UtcNow, ct));
		}

		[HttpPost("Pathology/{id:int}/Step"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.ClinicalRead)]
		public async Task<IActionResult> PathologyStep(int id, [FromBody] HospitalPathStepInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null || input == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await PathSvc.StepAsync(ctx, id, input, ctx.EmployeeId, DateTime.UtcNow, ct));
		}

		[HttpPost("Pathology/{id:int}/Amend"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Lab)]
		public async Task<IActionResult> PathologyAmend(int id, [FromBody] HospitalPathAmendInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null || input == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await PathSvc.AmendAsync(ctx, id, input.Text, input.Reason, ctx.EmployeeId, DateTime.UtcNow, ct));
		}
	}
}
