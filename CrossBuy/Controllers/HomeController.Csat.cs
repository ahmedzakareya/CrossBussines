using CrossBuy.BL.CrmIdentity;
using CrossBuy.Models.Platform;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CrossBuy.Controllers
{
	// CRM C8 — the one-click satisfaction link e-mailed after a request is resolved.
	public partial class HomeController
	{
		private ICrmServiceDeskService PublicDesk => ActivatorUtilities.CreateInstance<CrmServiceDeskService>(HttpContext.RequestServices);

		[AllowAnonymous]
		[HttpGet("store/csat/{token}")]
		public async Task<IActionResult> Csat(string token, int? score)
		{
			var s = await PublicDesk.SurveyByTokenAsync(token);
			if (s == null) return NotFound();
			ViewBag.Token = token; ViewBag.Subject = s.Value.subject; ViewBag.Score = s.Value.s.Score ?? score; ViewBag.Done = s.Value.s.AnsweredAt != null;
			return View("~/Views/Home/Csat.cshtml");
		}

		[AllowAnonymous][IgnoreAntiforgeryToken][RequestSizeLimit(8 * 1024)]
		[AnonymousByDesign(
			"The customer rates a resolved request from the link in the e-mail; they have no login. The 48-character random " +
			"token is stored only as its SHA-256 and opens exactly one survey; the request can only set that survey's score " +
			"(1–5) and comment, once — a second answer changes nothing — and reads nothing else.")]
		[HttpPost("store/csat/{token}")]
		public async Task<IActionResult> CsatAnswer(string token, int score, string? comment)
		{
			if (!await PublicDesk.AnswerByTokenAsync(token, score, comment)) return NotFound();
			ViewBag.Token = token; ViewBag.Subject = null; ViewBag.Score = score; ViewBag.Done = true;
			return View("~/Views/Home/Csat.cshtml");
		}
	}
}
