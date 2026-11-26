using CrossBuy.BL.CrmIdentity;
using CrossBuy.Models.Platform;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CrossBuy.Controllers
{
	// CRM C7 — the unsubscribe link at the foot of every marketing e-mail.
	public partial class HomeController
	{
		private ICrmJourneyService PublicJourneys => ActivatorUtilities.CreateInstance<CrmJourneyService>(HttpContext.RequestServices);

		[AllowAnonymous]
		[HttpGet("store/unsub/{token}")]
		public async Task<IActionResult> Unsubscribe(string token)
		{
			if (string.IsNullOrWhiteSpace(token) || token.Length > 400) return NotFound();
			var info = await PublicJourneys.UnsubscribeInfoAsync(token);
			if (info == null) return NotFound();
			ViewBag.Token = token; ViewBag.Name = info.Value.name; ViewBag.Done = info.Value.already;
			return View("~/Views/Home/Unsubscribe.cshtml");
		}

		[AllowAnonymous][IgnoreAntiforgeryToken][RequestSizeLimit(4 * 1024)]
		[AnonymousByDesign(
			"The person who received a marketing e-mail withdraws consent from its footer link; they have no login. The token is " +
			"encrypted and signed by the application's data protection (it names one company and one contact and cannot be made " +
			"or altered outside the server). The request can only ADD an 'OptOut, e-mail, Unsubscribe' row to that contact's " +
			"consent history — it removes permission, never grants it — and reads nothing else.")]
		[HttpPost("store/unsub/{token}")]
		public async Task<IActionResult> UnsubscribeConfirm(string token)
		{
			if (string.IsNullOrWhiteSpace(token) || token.Length > 400) return NotFound();
			var ok = await PublicJourneys.UnsubscribeAsync(token);
			if (!ok) return NotFound();
			ViewBag.Token = token; ViewBag.Name = null; ViewBag.Done = true;
			return View("~/Views/Home/Unsubscribe.cshtml");
		}
	}
}
