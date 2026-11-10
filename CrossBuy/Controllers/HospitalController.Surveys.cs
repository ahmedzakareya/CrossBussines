using CrossBuy.BL;
using CrossBuy.BL.Hospital;
using CrossBuy.Models;
using Microsoft.AspNetCore.Mvc;

namespace CrossBuy.Controllers
{
	// ==========================================================================================
	// HOSPITAL — H65: satisfaction surveys. The results screen (the figures, each question, the responses searched and
	// paged on the server, the follow-ups), the questions (the administrator), and the tablet page a survey is taken on.
	// ==========================================================================================
	public partial class HospitalController
	{
		private HospitalSurveyService? _surveySvc;
		private HospitalSurveyService SurveySvc => _surveySvc ??= ActivatorUtilities.CreateInstance<HospitalSurveyService>(HttpContext.RequestServices);

		public sealed class HospitalNoteInput { public string? Note { get; set; } }

		[HttpGet("Surveys")]
		[HospitalPerm(HospitalActions.Read)]
		public async Task<IActionResult> Surveys(string? kind, int days = 90, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return RedirectToAction("Index", "Home");
			Chrome();
			if (days is < 7 or > 366) days = 90;
			kind = Models.Context.Hospital.HospitalSurveyKind.All.Contains(kind ?? "") ? kind : null;
			var (questions, ready) = await SurveySvc.QuestionsAsync(ctx, null, false, ct);
			ViewBag.Ready = ready;
			ViewBag.Questions = questions;
			ViewBag.Kind = kind; ViewBag.Days = days;
			ViewBag.CanManage = await _access.CanAsync(ctx, HospitalActions.Manage, null, ct);
			ViewBag.Episodes = ready ? await SurveySvc.EpisodesAsync(ctx, DateTime.UtcNow, ct) : Array.Empty<HospitalSurveyEpisode>();
			return View(await SurveySvc.StatsAsync(ctx, kind, DateTime.UtcNow.AddDays(-days), ct));
		}

		[HttpGet("Surveys/Data")]
		[HospitalPerm(HospitalActions.Read)]
		public async Task<IActionResult> SurveysData(string? q, string? kind, string? state, int page = 1, int pageSize = 25, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return NotFound();
			if (pageSize is < 1 or > 200) pageSize = 25;
			if (page < 1) page = 1;
			var (rows, total) = await SurveySvc.ListAsync(ctx, string.IsNullOrEmpty(kind) ? null : kind, string.IsNullOrEmpty(state) ? null : state, q, page, pageSize, ct);
			Response.Headers["X-Total"] = total.ToString();
			Response.Headers["X-Page"] = page.ToString();
			Response.Headers["X-Pages"] = Math.Max(1, (int)Math.Ceiling(total / (double)pageSize)).ToString();
			ViewBag.CanManage = await _access.CanAsync(ctx, HospitalActions.Manage, null, ct);
			return PartialView("_SurveyRows", rows);
		}

		[HttpPost("Surveys/Questions"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Manage)]
		public async Task<IActionResult> SaveSurveyQuestion(int? id, [FromBody] HospitalSurveyQuestionInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null || input == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await SurveySvc.SaveQuestionAsync(ctx, id is > 0 ? id : null, input, ct));
		}

		[HttpPost("Surveys/Questions/Standard"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Manage)]
		public async Task<IActionResult> AddStandardSurveyQuestions(CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return NotFound(new { ok = false, code = "not_found" });
			var r = await SurveySvc.AddStandardAsync(ctx, ct);
			return r.Ok ? Json(new { ok = true, added = r.Args?.FirstOrDefault() ?? 0 }) : Result(r);
		}

		/// The tablet page: the episode's questions, the recommend score, a comment, anonymous or not.
		[HttpGet("Surveys/Take")]
		[HospitalPerm(HospitalActions.Read)]
		public async Task<IActionResult> TakeSurvey(string kind, int id, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return RedirectToAction("Index", "Home");
			var episode = (await SurveySvc.EpisodesAsync(ctx, DateTime.UtcNow, ct)).FirstOrDefault(e => e.Kind == kind && e.Id == id);
			if (episode == null) return RedirectToAction(nameof(Surveys));
			Chrome();
			ViewBag.Questions = (await SurveySvc.QuestionsAsync(ctx, kind, true, ct)).Questions;
			return View("SurveyTake", episode);
		}

		[HttpPost("Surveys"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Read)]
		public async Task<IActionResult> SubmitSurvey([FromBody] HospitalSurveyInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null || input == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await SurveySvc.TakeAsync(ctx, input, ctx.EmployeeId, DateTime.UtcNow, ct));
		}

		[HttpPost("Surveys/{id:int}/FollowUp"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Manage)]
		public async Task<IActionResult> CloseSurveyFollowUp(int id, [FromBody] HospitalNoteInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await SurveySvc.CloseFollowUpAsync(ctx, id, input?.Note, ctx.EmployeeId, DateTime.UtcNow, ct));
		}
	}
}
