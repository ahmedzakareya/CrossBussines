using CrossBuy.BL.CrmIdentity;
using CrossBuy.Models.Platform;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;

namespace CrossBuy.Controllers
{
	// CRM C4 — the outside world's side of the inbox, on the public surface (/store is already exempt from the staff
	// session gate). Two callers, neither of whom has or will ever have an account here:
	//   * a website visitor chatting through a company's chat widget;
	//   * Meta, delivering WhatsApp messages and delivery statuses to the company's webhook.
	// Each request names only a channel's unguessable key; the company is the channel's. Nothing here reads or writes
	// a CRM record: it stores inbox rows only, and the matching to contacts and leads happens in the company's scope.
	public partial class HomeController
	{
		private ICrmInboxService PublicInbox => ActivatorUtilities.CreateInstance<CrmInboxService>(HttpContext.RequestServices);
		private IStringLocalizer<CrossBuy.CrmResources> PublicText => HttpContext.RequestServices.GetRequiredService<IStringLocalizer<CrossBuy.CrmResources>>();

		/// The chat window the widget frames (and a direct link can open).
		[AllowAnonymous]
		[HttpGet("store/chat/{key}")]
		public async Task<IActionResult> Chat(string key, bool embed = false)
		{
			var ch = await PublicInbox.PublicChannelAsync(key, "WebChat");
			if (ch == null) return NotFound();
			Response.Headers["Content-Security-Policy"] = "frame-ancestors *";
			ViewBag.Embed = embed;
			return View("~/Views/Home/Chat.cshtml", ch);
		}

		/// The line a company pastes into its website: a chat bubble that opens the chat window.
		[AllowAnonymous]
		[HttpGet("store/chat/{key}/embed.js")]
		public async Task<IActionResult> ChatScript(string key)
		{
			var ch = await PublicInbox.PublicChannelAsync(key, "WebChat");
			if (ch == null) return NotFound();
			var src = $"{Request.Scheme}://{Request.Host}/store/chat/{Uri.EscapeDataString(key)}?embed=true";
			var js = "(function(){if(window.__cbChat)return;window.__cbChat=1;" +
				"var l=((document.documentElement.lang||navigator.language||'')+'').slice(0,2).toLowerCase()==='ar'?'ar':'en',rtl=l==='ar';" +
				"var b=document.createElement('button');b.type='button';b.setAttribute('aria-label','chat');" +
				"b.style.cssText='position:fixed;bottom:22px;'+(rtl?'left':'right')+':22px;width:60px;height:60px;border-radius:50%;border:0;background:#3A5795;color:#fff;font-size:26px;box-shadow:0 6px 20px rgba(0,0,0,.25);cursor:pointer;z-index:2147483000';" +
				"b.innerHTML='&#128172;';" +
				"var f=document.createElement('iframe');f.title='chat';f.src=" + System.Text.Json.JsonSerializer.Serialize(src) + "+'&culture='+l+'&ui-culture='+l+'&page='+encodeURIComponent(location.href);" +
				"f.style.cssText='position:fixed;bottom:92px;'+(rtl?'left':'right')+':22px;width:370px;max-width:calc(100vw - 30px);height:540px;max-height:calc(100vh - 120px);border:0;border-radius:16px;box-shadow:0 10px 40px rgba(0,0,0,.25);display:none;z-index:2147483000;background:#fff';" +
				"b.onclick=function(){f.style.display=f.style.display==='none'?'block':'none';};" +
				"document.body.appendChild(f);document.body.appendChild(b);})();";
			Response.Headers.CacheControl = "public, max-age=300";
			return Content(js, "application/javascript; charset=utf-8");
		}

		private const string ChatReason =
			"A website visitor chatting with a company has no account and never will. These endpoints write ONLY inbox rows " +
			"(a conversation and its messages) under the company that owns the chat channel, found by an unguessable key; the " +
			"request names no company, contact, lead or user. A conversation is reached only with the visitor's own random " +
			"token (stored as a hash), so one visitor can never read another's thread, and internal notes are never returned. " +
			"Inputs are length-clipped and each visitor and channel is rate-limited. No session exists for a forgery to ride on.";

		[AllowAnonymous][IgnoreAntiforgeryToken][RequestSizeLimit(16 * 1024)]
		[AnonymousByDesign(ChatReason)]
		[HttpPost("store/chat/{key}/start")]
		public async Task<IActionResult> ChatStart(string key, string? name, string? contact, string? message, string? page)
		{
			var (ok, code, token) = await PublicInbox.ChatStartAsync(key, name, contact, message, page);
			if (code == "not_found") return NotFound();
			return Json(new { ok, code, token, message = ok ? null : PublicText["chat:" + code].Value });
		}

		[AllowAnonymous][IgnoreAntiforgeryToken][RequestSizeLimit(16 * 1024)]
		[AnonymousByDesign(ChatReason)]
		[HttpPost("store/chat/{key}/send")]
		public async Task<IActionResult> ChatSend(string key, string token, string? body)
		{
			var (ok, code) = await PublicInbox.ChatSendAsync(key, token, body ?? "");
			if (code == "not_found") return NotFound();
			return Json(new { ok, code, message = ok ? null : PublicText["chat:" + code].Value });
		}

		[AllowAnonymous]
		[HttpGet("store/chat/{key}/poll")]
		public async Task<IActionResult> ChatPoll(string key, string token, long after = 0)
		{
			var list = await PublicInbox.ChatPollAsync(key, token, after);
			if (list == null) return NotFound();
			return Json(list.Select(m => new { id = m.Id, mine = m.Direction == "In", body = m.Body, at = DateTime.SpecifyKind(m.At, DateTimeKind.Utc).ToString("o") }));
		}

		/// Meta's one-time handshake when the webhook is registered: echo the challenge only for the channel's own verify token.
		[AllowAnonymous]
		[HttpGet("store/wa/{key}/webhook")]
		public async Task<IActionResult> WhatsAppVerify(string key)
		{
			var challenge = await PublicInbox.WhatsAppVerifyAsync(key, Request.Query["hub.mode"], Request.Query["hub.verify_token"], Request.Query["hub.challenge"]);
			return challenge == null ? StatusCode(403) : Content(challenge, "text/plain");
		}

		[AllowAnonymous][IgnoreAntiforgeryToken][RequestSizeLimit(512 * 1024)]
		[AnonymousByDesign(
			"Meta delivers WhatsApp messages and delivery statuses here; it is a server, not a user, and cannot sign in. Every " +
			"request is believed only if its X-Hub-Signature-256 is a valid HMAC-SHA256 of the exact raw body under the channel's " +
			"own app secret (constant-time compare), and the channel is found by an unguessable key. It writes ONLY inbox rows " +
			"under that channel's company, de-duplicates Meta's retries by message id, and reads no CRM record.")]
		[HttpPost("store/wa/{key}/webhook")]
		public async Task<IActionResult> WhatsAppReceive(string key)
		{
			using var ms = new MemoryStream();
			await Request.Body.CopyToAsync(ms);
			var (ok, _) = await PublicInbox.WhatsAppReceiveAsync(key, ms.ToArray(), Request.Headers["X-Hub-Signature-256"]);
			// Meta retries anything that is not 200, so an unsigned or foreign call gets a plain 401 and nothing else
			return ok ? Ok() : StatusCode(401);
		}
	}
}
