using CrossBuy.BL;
using CrossBuy.BL.Hospital;
using CrossBuy.Models;
using CrossBuy.Models.Platform;
using Microsoft.AspNetCore.Mvc;

namespace CrossBuy.Controllers
{
	// HOSPITAL H250 — questionnaires: the clinic's list (urgent first), send, review, withdraw.
	public partial class HospitalController
	{
		private HospitalQuestionnaireService? _qnSvc;
		private HospitalQuestionnaireService QnSvc => _qnSvc ??= ActivatorUtilities.CreateInstance<HospitalQuestionnaireService>(HttpContext.RequestServices);

		[HttpGet("Questionnaires"), HospitalPerm(HospitalActions.ClinicalRead)]
		public async Task<IActionResult> Questionnaires(string? q, string? view, int page = 1, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return RedirectToAction("Index", "Home");
			Chrome();
			const int size = 20;
			var d = await QnSvc.ListAsync(ctx, q, view, page, size, ct);
			if (d == null) return Forbid();
			ViewBag.Q = q; ViewBag.View = view ?? ""; ViewBag.Page = Math.Max(1, page); ViewBag.Pages = Math.Max(1, (d.Value.Total + size - 1) / size);
			ViewBag.CanConsult = await _access.CanAsync(ctx, HospitalActions.Consult, null, ct);
			return View(d.Value);
		}

		/// The patient picker for sending (the clinician may not hold the admitting desk's search).
		[HttpGet("Questionnaires/Patients"), HospitalPerm(HospitalActions.Consult)]
		public async Task<IActionResult> QuestionnairePatients(string? q, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return NotFound(new { ok = false, code = "not_found" });
			var page = await _patients.ListAsync(ctx, q, 1, DateTime.UtcNow, ct);
			return Json(new { ok = true, results = page.Rows.Select(p => new { id = p.Id, text = DisplayName.Of(p.Name, p.NameEn) + " — " + p.Mrn }) });
		}

		[HttpPost("Questionnaires"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Consult)]
		public async Task<IActionResult> QuestionnaireSend([FromBody] HospitalQuestionnaireSendInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null || input == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await QnSvc.SendAsync(ctx, input, DateTime.UtcNow, ct));
		}

		[HttpPost("Questionnaires/{id:int}/Review"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Consult)]
		public async Task<IActionResult> QuestionnaireReview(int id, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await QnSvc.ReviewAsync(ctx, id, DateTime.UtcNow, ct));
		}

		[HttpPost("Questionnaires/{id:int}/Cancel"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Consult)]
		public async Task<IActionResult> QuestionnaireCancel(int id, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await QnSvc.CancelAsync(ctx, id, ct));
		}
	}
}
