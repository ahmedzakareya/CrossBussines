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
			// ---------------- a guest in a group call of their conversation (HospitalCallHub.Guests) ----------------
		// The guest's page has no hub connection: it joins, sends each connection message, and polls what the members'
		// devices sent it, here. The key is checked on every request (kept ten seconds, so a poll every second does not
		// read the database each time); a revoked or expired link stops within those seconds.
		private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (Models.Context.Chat.ChatGuest Guest, DateTime At)> Known = new();
		private async Task<Models.Context.Chat.ChatGuest?> GuestAsync()
		{
			var k = Key; if (k == null) return null;
			if (Known.TryGetValue(k, out var hit) && DateTime.UtcNow - hit.At < TimeSpan.FromSeconds(10)) return hit.Guest;
			var g = await Guests.ResolveAsync(Scopes, k);
			if (g == null) { Known.TryRemove(k, out _); return null; }
			if (Known.Count > 2000) Known.Clear();
			Known[k] = (g, DateTime.UtcNow);
			return g;
		}
		private Microsoft.AspNetCore.SignalR.IHubContext<Hubs.HospitalCallHub> CallHub => HttpContext.RequestServices.GetRequiredService<Microsoft.AspNetCore.SignalR.IHubContext<Hubs.HospitalCallHub>>();

		private const string CallWhy =
			"An outside guest takes part in a group call started from the ONE group conversation they were invited into, " +
			"with the same key as the chat (stored only as its SHA-256, expiring, revocable). It reaches only the live call " +
			"of that conversation: it joins it, passes its connection messages to the people in it, and reads what they " +
			"sent it. Media goes device to device, never here; a recorded call, or one on the media server, takes no guest. " +
			"A wrong, expired or revoked key answers 404.";

		[AllowAnonymous][IgnoreAntiforgeryToken][AnonymousByDesign(CallWhy)]
		[HttpPost("api/guest/call/join")]
		public async Task<IActionResult> CallJoin([FromForm] int callId)
		{
			var g = await GuestAsync(); if (g == null) return NotFound(new { ok = false });
			using var scope = BL.Platform.WorkerScope.ForCompany(Scopes, g.CompanyId);
			// the guest's page is Arabic: the people in the call are named in Arabic
			System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.CurrentUICulture = new System.Globalization.CultureInfo("ar");
			return Ok(await Hubs.HospitalCallHub.GuestJoinAsync(CallHub, scope.ServiceProvider, g, callId));
		}

		[AllowAnonymous][IgnoreAntiforgeryToken][RequestSizeLimit(80 * 1024)][AnonymousByDesign(CallWhy)]
		[HttpPost("api/guest/call/signal")]
		public async Task<IActionResult> CallSignal([FromForm] int callId, [FromForm] int to, [FromForm] string? payload)
		{
			var g = await GuestAsync(); if (g == null) return NotFound(new { ok = false });
			return Ok(new { ok = await Hubs.HospitalCallHub.GuestSignalAsync(CallHub, g, callId, to, payload ?? "") });
		}

		[AllowAnonymous][AnonymousByDesign(CallWhy)]
		[HttpGet("api/guest/call/poll")]
		public async Task<IActionResult> CallPoll(int callId)
		{
			var g = await GuestAsync(); if (g == null) return NotFound(new { ok = false });
			var r = await Hubs.HospitalCallHub.GuestPollAsync(CallHub, g, callId);
			return r == null ? Ok(new { ended = true, events = Array.Empty<object>() }) : Ok(r);
		}

		[AllowAnonymous][IgnoreAntiforgeryToken][AnonymousByDesign(CallWhy)]
		[HttpPost("api/guest/call/leave")]
		public async Task<IActionResult> CallLeave([FromForm] int callId)
		{
			var g = await GuestAsync(); if (g == null) return NotFound(new { ok = false });
			await Hubs.HospitalCallHub.GuestLeaveAsync(CallHub, callId, g.ID);
			return Ok(new { ok = true });
		}
	}
}
