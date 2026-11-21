using CrossBuy.BL;
using CrossBuy.BL.Hospital;
using CrossBuy.Models;
using CrossBuy.Models.Context.Hospital;
using CrossBuy.Models.Platform;
using Microsoft.AspNetCore.Mvc;

namespace CrossBuy.Controllers
{
	// HOSPITAL H212 — clinical surveillance: the sepsis and VTE alerts the record raised by itself (read with
	// `clinical-read`), and a clinician's answer to one.
	public partial class HospitalController
	{
		public sealed class HospitalSurveillanceAnswerInput { public string? Response { get; set; } public string? Note { get; set; } public List<string>? Padua { get; set; } }

		private HospitalSurveillanceService? _survSvc;
		private HospitalSurveillanceService SurvSvc => _survSvc ??= ActivatorUtilities.CreateInstance<HospitalSurveillanceService>(HttpContext.RequestServices);

		[HttpGet("Wards/Surveillance")]
		[HospitalPerm(HospitalActions.ClinicalRead)]
		public async Task<IActionResult> Surveillance(string? q, string? kind, string? status, bool overdue = false, int page = 1, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return RedirectToAction("Index", "Home");
			Chrome();
			const int size = 20;
			if (!string.IsNullOrEmpty(kind) && !HospitalSurveillanceKind.All.Contains(kind)) kind = null;
			if (status == null && !overdue) status = HospitalSurveillanceStatus.Open;   // the open ones first, as a ward works them
			if (status == "all") status = null;
			if (!string.IsNullOrEmpty(status) && !HospitalSurveillanceStatus.All.Contains(status)) status = null;
			var (rows, total, sepsis, vte, late, ready) = await SurvSvc.ListAsync(ctx, q, kind, status, overdue, page, size, DateTime.UtcNow, ct);
			ViewBag.Q = q; ViewBag.Kind = kind; ViewBag.Status = status ?? "all"; ViewBag.Overdue = overdue;
			ViewBag.Page = Math.Max(1, page); ViewBag.Pages = Math.Max(1, (total + size - 1) / size); ViewBag.Total = total;
			ViewBag.Sepsis = sepsis; ViewBag.Vte = vte; ViewBag.Late = late; ViewBag.Ready = ready;
			return View(rows);
		}

		[HttpPost("Wards/Surveillance/{id:int}/Answer"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.ClinicalRead)]
		public async Task<IActionResult> SurveillanceAnswer(int id, [FromBody] HospitalSurveillanceAnswerInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null || input == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await SurvSvc.AnswerAsync(ctx, id, input.Response, input.Note, ctx.EmployeeId, DateTime.UtcNow, ct, input.Padua));
		}
	}
}
