using System.Text.Json;
using CrossBuy.BL.Communication;
using CrossBuy.Models.Platform;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CrossBuy.Controllers
{
	/// The address another system posts to (owner, 2026-11-19: incoming webhooks into channels). See ChatWebhookService.
	public sealed class ChatWebhookController : Controller
	{
		[AllowAnonymous][IgnoreAntiforgeryToken][RequestSizeLimit(16 * 1024)]
		[AnonymousByDesign(
			"Another system posts a message into one group or channel; it has no login. The hook's 32-byte random token is " +
			"stored only as its SHA-256 and opens exactly one hook, which a group admin made and can revoke. The request can " +
			"only add one text message to that conversation, in the name of the member who made the hook, through the chat's " +
			"own posting path (membership, length and the sensitive-data rule apply), at most 30 a minute; it reads nothing, " +
			"and a wrong token, a revoked hook and a maker who has left all answer 404 alike.")]
		[HttpPost("api/hooks/chat/{id:int}/{token}")]   // under /api: the employee-session gate (SessionValidationMiddleware) does not apply there
		public async Task<IActionResult> Receive(int id, string token)
		{
			string? text = null;
			try
			{
				if (Request.HasFormContentType) text = Request.Form["text"];
				else
				{
					using var doc = await JsonDocument.ParseAsync(Request.Body, cancellationToken: HttpContext.RequestAborted);
					if (doc.RootElement.ValueKind == JsonValueKind.Object)
						foreach (var k in new[] { "text", "content", "message" })
							if (doc.RootElement.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String) { text = v.GetString(); break; }
				}
			}
			catch (JsonException) { return BadRequest(new { ok = false, error = "invalid_json" }); }

			var svc = new ChatWebhookService(HttpContext.RequestServices.GetRequiredService<Models.Context.CrossDbContext>());
			var (outcome, messageId) = await svc.ReceiveAsync(HttpContext.RequestServices.GetRequiredService<IServiceScopeFactory>(), id, token, text);
			return outcome switch
			{
				ChatWebhookService.Outcome.Posted => Ok(new { ok = true, id = messageId }),
				ChatWebhookService.Outcome.Empty => BadRequest(new { ok = false, error = "text_required" }),
				ChatWebhookService.Outcome.TooLong => BadRequest(new { ok = false, error = "text_too_long", max = ChatPlusService.MaxBody }),
				ChatWebhookService.Outcome.TooMany => StatusCode(429, new { ok = false, error = "too_many_requests", perMinute = ChatWebhookService.MaxPerMinute }),
				ChatWebhookService.Outcome.Refused => UnprocessableEntity(new { ok = false, error = "refused" }),
				_ => NotFound(new { ok = false }),
			};
		}
	}
}
