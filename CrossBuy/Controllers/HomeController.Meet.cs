using CrossBuy.BL.CrmIdentity;
using CrossBuy.Models.Platform;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;

namespace CrossBuy.Controllers
{
	// CRM C6 — the public side of booking: a rep's booking page, the booking itself, and the rep's calendar feed.
	// Under /store/... like the other public CRM surfaces (web forms, chat), so the session middleware leaves them alone.
	public partial class HomeController
	{
		private ICrmSequenceService PublicSequences => ActivatorUtilities.CreateInstance<CrmSequenceService>(HttpContext.RequestServices);
		private IStringLocalizer<CrossBuy.CrmResources> MeetText => HttpContext.RequestServices.GetRequiredService<IStringLocalizer<CrossBuy.CrmResources>>();

		[AllowAnonymous]
		[HttpGet("store/meet/{key}")]
		public async Task<IActionResult> Meet(string key)
		{
			if (string.IsNullOrWhiteSpace(key) || key.Length > 32) return NotFound();
			var page = await PublicSequences.PublicPageAsync(key);
			if (page == null) return NotFound();
			return View("~/Views/Home/Meet.cshtml", page);
		}

		[AllowAnonymous][IgnoreAntiforgeryToken][RequestSizeLimit(16 * 1024)]
		[AnonymousByDesign(
			"A customer books a meeting from a rep's public link: they are outside the company and cannot sign in. The request " +
			"writes ONE booking row under the company that owns the link (found by an unguessable key), only for a start the rep's " +
			"own calendar offers at this moment, and a unique index refuses a second booking of the same minute. It reads no CRM " +
			"record and names no company, lead or user; matching to a lead or contact happens later in the company's own scope. " +
			"Inputs are length-clipped and each link is rate-limited. No session exists for a forgery to ride on.")]
		[HttpPost("store/meet/{key}/book")]
		public async Task<IActionResult> MeetBook(string key, string start, string? name, string? email, string? phone, string? note)
		{
			if (string.IsNullOrWhiteSpace(key) || key.Length > 32) return NotFound();
			if (!DateTime.TryParseExact(start, "yyyy-MM-dd'T'HH:mm", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var at))
				return Json(new { ok = false, code = "taken", message = MeetText["meet:taken"].Value });
			var (ok, code) = await PublicSequences.BookAsync(key, DateTime.SpecifyKind(at, DateTimeKind.Local), name, email, phone, note);
			if (code == "not_found") return NotFound();
			return Json(new { ok, code, message = ok ? null : MeetText["meet:" + code].Value });
		}

		/// An iCalendar feed of the rep's own open CRM meetings, calls and tasks, for Outlook / Google / Apple Calendar to
		/// subscribe to. The 48-character token is the whole key (it opens nothing else); a reset on the booking screen
		/// is not offered yet, so the token is never shown outside the rep's own screen.
		[AllowAnonymous]
		[HttpGet("store/cal/{token}.ics")]
		public async Task<IActionResult> MeetCalendar(string token)
		{
			if (string.IsNullOrWhiteSpace(token) || token.Length != 48) return NotFound();
			var ics = await PublicSequences.CalendarAsync(token);
			return ics == null ? NotFound() : Content(ics, "text/calendar; charset=utf-8");
		}
	}
}
