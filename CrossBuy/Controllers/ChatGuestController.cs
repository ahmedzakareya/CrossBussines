using CrossBuy.BL.Communication;
using CrossBuy.Models.Platform;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CrossBuy.Controllers
{
	/// What an invited guest's page (wwwroot/guest/chat.html) calls (owner 2026-11-22: guests from outside the company).
	/// See ChatGuestService. The key travels in the X-Guest header as "id.secret", never in the address.
	public sealed class ChatGuestController : Controller
	{
		private ChatGuestService Guests => new(HttpContext.RequestServices.GetRequiredService<Models.Context.CrossDbContext>());
		private IServiceScopeFactory Scopes => HttpContext.RequestServices.GetRequiredService<IServiceScopeFactory>();
		private string? Key => Request.Headers["X-Guest"].ToString() is { Length: > 0 } k ? k : null;

		[AllowAnonymous]
		[AnonymousByDesign(
			"An outside guest reads ONE group conversation they were invited into. The 32-byte random secret is stored " +
			"only as its SHA-256, expires (at most 30 days) and is revocable by the group's admin; it stops when the " +
			"inviter leaves. It returns only that conversation's messages since the invitation - never its history, no " +
			"file, no profile, no other conversation - and a wrong, expired or revoked key answers 404 like any other.")]
		[HttpGet("api/guest/chat")]
		public async Task<IActionResult> Read(int since)
		{
			var r = await Guests.ReadAsync(Scopes, Key, since);
			return r == null ? NotFound(new { ok = false }) : Ok(r);
		}

		[AllowAnonymous][IgnoreAntiforgeryToken][RequestSizeLimit(16 * 1024)]
		[AnonymousByDesign(
			"An outside guest writes in ONE group conversation they were invited into, with the same key as the read. The " +
			"message goes through the chat's own posting path (length, the sensitive-data rule) in the inviter's name, " +
			"marked with the guest's name, at most 20 a minute; a wrong, expired or revoked key answers 404.")]
		[HttpPost("api/guest/chat")]
		public async Task<IActionResult> Post([FromForm] string? text)
		{
			var (outcome, id) = await Guests.PostAsync(Scopes, Key, text);
			return outcome switch
			{
				ChatGuestService.Outcome.Posted => Ok(new { ok = true, id }),
				ChatGuestService.Outcome.Empty => BadRequest(new { ok = false, error = "text_required" }),
				ChatGuestService.Outcome.TooLong => BadRequest(new { ok = false, error = "text_too_long" }),
				ChatGuestService.Outcome.TooMany => StatusCode(429, new { ok = false, error = "too_many_requests" }),
				ChatGuestService.Outcome.Refused => UnprocessableEntity(new { ok = false, error = "refused" }),
				_ => NotFound(new { ok = false }),
			};
		}
	}
}
