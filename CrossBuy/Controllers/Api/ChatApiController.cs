using CrossBuy.BL;
using CrossBuy.BL.Communication;
using CrossBuy.BL.Platform;
using CrossBuy.Models.Context;
using CrossBuy.Models.Platform;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.Controllers.Api
{
	// ==========================================================================================
	// COMMUNICATION v2 — the chat for the MOBILE APP (owner, 2026-11-18: "build every gap, completely"; the app had calls
	// and no chat). The same ChatService and ChatPlusService the browser uses, behind the app's token, never a second
	// set of rules: the person and the company are the resolved BusinessContext's; every conversation-scoped action asks
	// the chat's own authority (communication read/send on that conversation); an attachment passes the same file policy.
	// ==========================================================================================
	[ApiController]
	[Route("api/chat")]
	[Produces("application/json")]
	[Authorize(AuthenticationSchemes = "Identity.Application," + JwtBearerDefaults.AuthenticationScheme)]
	public class ChatApiController : ControllerBase
	{
		private readonly IBusinessContextAccessor _contexts;
		private readonly IChatService _chat;
		private readonly ICommunicationAccessService _comm;
		private readonly CrossDbContext _db;
		private readonly IWebHostEnvironment _env;
		private readonly IServiceProvider _sp;
		public ChatApiController(IBusinessContextAccessor contexts, IChatService chat, ICommunicationAccessService comm, CrossDbContext db, IWebHostEnvironment env, IServiceProvider sp)
		{ _contexts = contexts; _chat = chat; _comm = comm; _db = db; _env = env; _sp = sp; }

		private ChatPlusService Plus => ActivatorUtilities.CreateInstance<ChatPlusService>(_sp);
		private static PermissionTarget In(int c) => new() { ConversationId = c };
		private static object Missing => new { ok = false, code = "not_found" };

		/// Not async: the language set here stays for the rest of the action (names follow the app's language).
		private Task<BusinessContext?> MeAsync(CancellationToken ct)
		{
			if (Request.Headers.AcceptLanguage.ToString().StartsWith("ar", StringComparison.OrdinalIgnoreCase))
				System.Globalization.CultureInfo.CurrentUICulture = new System.Globalization.CultureInfo("ar");
			return ResolveAsync(ct);
		}
		private async Task<BusinessContext?> ResolveAsync(CancellationToken ct)
		{
			var ctx = await _contexts.TryGetCurrentAsync(ct);
			return ctx is { CompanyId: > 0, EmployeeId: > 0 } ? ctx : null;
		}

		/// GET /api/chat/conversations — mine, pinned first then newest, with the unread counts.
		[HttpGet("conversations")]
		public async Task<IActionResult> Conversations(CancellationToken ct)
		{
			var me = await MeAsync(ct); if (me == null) return NotFound(Missing);
			return Ok(await _chat.MyConversationsAsync(me.CompanyId, me.EmployeeId!.Value));
		}

		/// GET /api/chat/messages?c=&amp;before= — thirty at a time, oldest first.
		[HttpGet("messages")]
		public async Task<IActionResult> Messages(int c, int? before, CancellationToken ct)
		{
			var me = await MeAsync(ct); if (me == null || !await _comm.CanAsync(me, CommunicationActions.Read, In(c), ct)) return NotFound(Missing);
			return Ok(await _chat.GetMessagesAsync(me.EmployeeId!.Value, c, before));
		}

		/// GET /api/chat/header?c= — the conversation's title, members and who read what.
		[HttpGet("header")]
		public async Task<IActionResult> Header(int c, CancellationToken ct)
		{
			var me = await MeAsync(ct); if (me == null || !await _comm.CanAsync(me, CommunicationActions.Read, In(c), ct)) return NotFound(Missing);
			var h = await _chat.GetHeaderAsync(me.CompanyId, me.EmployeeId!.Value, c);
			return h == null ? NotFound(Missing) : Ok(h);
		}

		/// POST /api/chat/send (multipart: c, body, replyToId, file) — text, a picture, a file or a voice message.
		[HttpPost("send")]
		[RequestSizeLimit(52 * 1024 * 1024)]
		public async Task<IActionResult> Send([FromForm] int c, [FromForm] string? body, [FromForm] int? replyToId, IFormFile? file, CancellationToken ct)
		{
			var me = await MeAsync(ct); if (me == null || !await _comm.CanAsync(me, CommunicationActions.Send, In(c), ct)) return NotFound(Missing);
			int emp = me.EmployeeId!.Value;
			if (file == null || file.Length == 0)
			{
				var dto = await _chat.SendAsync(me.CompanyId, emp, c, body, null, null, null, replyToId > 0 ? replyToId : null, null);
				return dto == null ? BadRequest(new { ok = false }) : Ok(dto);
			}
			if (file.Length > 50 * 1024 * 1024) return BadRequest(new { ok = false, code = "too_large" });
			var ext = Path.GetExtension(file.FileName ?? string.Empty).ToLowerInvariant();
			if (!ChatAttachments.IsAcceptedType(ext, file.ContentType)) return BadRequest(new { ok = false, code = "attachment_type" });
			var dir = Path.Combine(_env.WebRootPath, "uploads", "library", me.CompanyId.ToString(), "chat");
			System.IO.Directory.CreateDirectory(dir);
			var stored = Guid.NewGuid().ToString("N") + ext;
			using (var s = System.IO.File.Create(Path.Combine(dir, stored))) await file.CopyToAsync(s, ct);
			var path = "/uploads/library/" + me.CompanyId + "/chat/" + stored;
			var files = _sp.GetService(typeof(IFileManagerService)) as IFileManagerService;
			if (files != null) { try { await files.AddFileAsync(me.CompanyId, emp, null, Path.GetFileName(file.FileName ?? stored), null, path, file.ContentType, file.Length); } catch { } }
			var type = ChatAttachments.IsImage(ext) ? "image" : ChatAttachments.IsAudio(ext) ? "audio" : "file";
			var sent = await _chat.SendAsync(me.CompanyId, emp, c, body, path, Path.GetFileName(file.FileName ?? stored), type, replyToId > 0 ? replyToId : null, null);
			if (sent == null) { System.IO.File.Delete(Path.Combine(dir, stored)); return BadRequest(new { ok = false }); }
			return Ok(sent);
		}

		/// GET /api/chat/file?messageId= — a message's attachment for the app (the browser reads it from its own address).
		[HttpGet("file")]
		public async Task<IActionResult> File(int messageId, CancellationToken ct)
		{
			var me = await MeAsync(ct); if (me == null) return NotFound();
			var m = await _db.ChatMessages.AsNoTracking().Where(x => x.ID == messageId && x.DeletedAt == null).Select(x => new { x.ConversationId, x.AttachmentPath, x.AttachmentName }).FirstOrDefaultAsync(ct);
			if (m == null || m.AttachmentPath == null || !await _comm.CanAsync(me, CommunicationActions.Read, In(m.ConversationId), ct)) return NotFound();
			var full = await ChatAttachmentFiles.ResolveAsync(_db, _env, me.CompanyId, m.AttachmentPath, ct);
			if (full == null) return NotFound();
			var ext = Path.GetExtension(full).ToLowerInvariant();
			var type = ext switch { ".png" => "image/png", ".jpg" or ".jpeg" or ".jfif" => "image/jpeg", ".gif" => "image/gif", ".webp" => "image/webp", ".webm" => "audio/webm", ".ogg" => "audio/ogg", ".m4a" => "audio/mp4", ".mp3" => "audio/mpeg", ".wav" => "audio/wav", ".pdf" => "application/pdf", _ => "application/octet-stream" };
			Response.Headers.CacheControl = "private, max-age=3600";
			return PhysicalFile(full, type, enableRangeProcessing: true);
		}

		/// POST /api/chat/read?c= — I have read the conversation.
		[HttpPost("read")]
		public async Task<IActionResult> Read([FromQuery] int c, CancellationToken ct)
		{
			var me = await MeAsync(ct); if (me == null || !await _comm.CanAsync(me, CommunicationActions.Read, In(c), ct)) return NotFound(Missing);
			await _chat.MarkReadAsync(me.EmployeeId!.Value, c);
			return Ok(new { ok = true });
		}

		/// POST /api/chat/react?id=&amp;emoji= — add or take back a reaction.
		[HttpPost("react")]
		public async Task<IActionResult> React([FromQuery] int id, [FromQuery] string emoji, CancellationToken ct)
		{
			var me = await MeAsync(ct); if (me == null) return NotFound(Missing);
			int c = await _db.ChatMessages.AsNoTracking().Where(m => m.ID == id).Select(m => m.ConversationId).FirstOrDefaultAsync(ct);
			if (c == 0 || !await _comm.CanAsync(me, CommunicationActions.Send, In(c), ct)) return NotFound(Missing);
			await _chat.ToggleReactionAsync(me.EmployeeId!.Value, id, emoji);
			return Ok(new { ok = true });
		}

		/// POST /api/chat/direct?otherId= — the conversation with one person (made if there is none yet).
		[HttpPost("direct")]
		public async Task<IActionResult> Direct([FromQuery] int otherId, CancellationToken ct)
		{
			var me = await MeAsync(ct); if (me == null || !await _comm.CanAsync(me, CommunicationActions.Send, null, ct)) return NotFound(Missing);
			int id = await _chat.GetOrCreateDirectAsync(me.CompanyId, me.EmployeeId!.Value, otherId);
			return id > 0 ? Ok(new { ok = true, id }) : NotFound(Missing);
		}

		/// GET /api/chat/directory?q= — everyone a conversation can be started with.
		[HttpGet("directory")]
		public async Task<IActionResult> Directory(string? q, CancellationToken ct)
		{
			var me = await MeAsync(ct); if (me == null) return NotFound(Missing);
			return Ok(await _chat.DirectoryAsync(me.CompanyId, me.EmployeeId!.Value, q));
		}

		/// GET /api/chat/find?q= — a message across every conversation of mine.
		[HttpGet("find")]
		public async Task<IActionResult> Find(string? q, CancellationToken ct)
		{
			var me = await MeAsync(ct); if (me == null) return NotFound(Missing);
			return Ok(await Plus.FindAsync(me.CompanyId, me.EmployeeId!.Value, q ?? "", null, null, null));
		}

		/// GET /api/chat/pulse — the unread count (the app's badge).
		[HttpGet("pulse")]
		public async Task<IActionResult> Pulse(CancellationToken ct)
		{
			var me = await MeAsync(ct); if (me == null) return NotFound(Missing);
			var convs = await _chat.MyConversationsAsync(me.CompanyId, me.EmployeeId!.Value);
			return Ok(new { ok = true, me = me.EmployeeId!.Value, unread = convs.Where(x => !x.Muted).Sum(x => x.Unread) });
		}

		/// GET /api/chat/meta?c=&amp;ids= — what loaded messages carry: a thread's count, forwarded, pinned, a poll.
		[HttpGet("meta")]
		public async Task<IActionResult> Meta(int c, string? ids, CancellationToken ct)
		{
			var me = await MeAsync(ct); if (me == null || !await _comm.CanAsync(me, CommunicationActions.Read, In(c), ct)) return NotFound(Missing);
			var list = (ids ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries).Select(x => int.TryParse(x, out var v) ? v : 0).Where(v => v > 0).Distinct().Take(200).ToList();
			return Ok(await Plus.MetaAsync(me.CompanyId, c, me.EmployeeId!.Value, list));
		}

		/// POST /api/chat/transcribe?id= — the words of a voice message (transcribed once, on this machine).
		[HttpPost("transcribe")]
		public async Task<IActionResult> Transcribe([FromQuery] int id, CancellationToken ct)
		{
			var me = await MeAsync(ct); if (me == null) return NotFound(Missing);
			int c = await _db.ChatMessages.AsNoTracking().Where(m => m.ID == id).Select(m => m.ConversationId).FirstOrDefaultAsync(ct);
			if (c == 0 || !await _comm.CanAsync(me, CommunicationActions.Read, In(c), ct)) return NotFound(Missing);
			var r = await ActivatorUtilities.CreateInstance<ChatTranscriber>(_sp).TranscribeAsync(me.CompanyId, me.EmployeeId!.Value, id, ct);
			return Ok(new { ok = r.Ok, status = r.Status, text = r.Text, language = r.Language, code = r.Code });
		}

		/// GET /api/chat/pinned?c= — the conversation's pinned messages, newest first.
		[HttpGet("pinned")]
		public async Task<IActionResult> Pinned(int c, CancellationToken ct)
		{
			var me = await MeAsync(ct); if (me == null || !await _comm.CanAsync(me, CommunicationActions.Read, In(c), ct)) return NotFound(Missing);
			return Ok(await Plus.PinnedAsync(me.CompanyId, c, me.EmployeeId!.Value));
		}

		/// POST /api/chat/vote?id=&amp;options=1,2 — my vote on a poll (replaces my earlier one).
		[HttpPost("vote")]
		public async Task<IActionResult> Vote([FromQuery] int id, [FromQuery] string? options, CancellationToken ct)
		{
			var me = await MeAsync(ct); if (me == null) return NotFound(Missing);
			int c = await _db.ChatMessages.AsNoTracking().Where(m => m.ID == id).Select(m => m.ConversationId).FirstOrDefaultAsync(ct);
			if (c == 0 || !await _comm.CanAsync(me, CommunicationActions.Send, In(c), ct)) return NotFound(Missing);
			var pick = (options ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries).Select(x => int.TryParse(x, out var v) ? v : 0).Where(v => v > 0).ToList();
			return Ok(new { ok = await Plus.VoteAsync(me.CompanyId, me.EmployeeId!.Value, id, pick) });
		}
	}
}
