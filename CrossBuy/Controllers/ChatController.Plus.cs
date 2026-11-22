using CrossBuy.BL;
using CrossBuy.BL.Communication;
using CrossBuy.Models.Platform;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.Controllers
{
	// ==========================================================================================
	// COMMUNICATION v2 (owner, 2026-11-18: "build every gap, completely") — the endpoints of what the leading team-chat
	// systems have and ours did not: threads, forwarding, pinned and saved messages, polls, scheduled messages,
	// reminders, status and do-not-disturb, channels, a group's description and picture, search across everything,
	// catch-up, a task from a message, a record's card, approvals in chat, the sensitive-data rule, and the governance
	// of messages (retention, legal holds, compliance export, usage). The work is in BL/Communication/ChatPlusService.
	//
	// Every action asks the chat's own authority (communication read/send on the conversation) or, for governance,
	// the Communication administrator — the same rule the rest of the chat applies.
	// ==========================================================================================
	public partial class ChatController
	{
		private ChatPlusService Plus => ActivatorUtilities.CreateInstance<ChatPlusService>(HttpContext.RequestServices);
		private ICommunicationAccessService Comm => HttpContext.RequestServices.GetRequiredService<ICommunicationAccessService>();
		private static PermissionTarget In(int c) => new() { ConversationId = c };
		private async Task<BusinessContext?> CtxAsync() => await _contexts.TryGetCurrentAsync(HttpContext.RequestAborted);
		private async Task<int> ConvOfAsync(int messageId) => await _db.ChatMessages.AsNoTracking().Where(m => m.ID == messageId).Select(m => m.ConversationId).FirstOrDefaultAsync();
		private static DateTime? Utc(string? iso) => DateTime.TryParse(iso, null, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var d) ? d : null;
		private static List<int> Ids(string? csv) => (csv ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(s => int.TryParse(s, out var n) ? n : 0).Where(n => n > 0).Distinct().ToList();

		// ---------------- threads ----------------
		[HttpGet]
		public async Task<IActionResult> Thread(int c, int root)
		{
			var me = await MeAsync(); var ctx = await CtxAsync();
			if (me == null || ctx == null || !await Comm.CanAsync(ctx, CommunicationActions.Read, In(c), HttpContext.RequestAborted)) return Json(Array.Empty<object>());
			return Json(await Plus.ThreadAsync(me.Value.companyId, c, root, me.Value.empId));
		}
		[HttpPost, ValidateAntiForgeryToken]
		public async Task<IActionResult> ThreadReply(int c, int root, string body, string? mentionedIds)
		{
			var me = await MeAsync(); var ctx = await CtxAsync();
			if (me == null || ctx == null || !await Comm.CanAsync(ctx, CommunicationActions.Send, In(c), HttpContext.RequestAborted)) return NotFound(new { ok = false });
			var m = await Plus.PostAsync(me.Value.companyId, c, me.Value.empId, new ChatPlusService.PostInput { Body = body, ThreadRootId = root, Mentioned = Ids(mentionedIds) });
			return m == null ? BadRequest(new { ok = false }) : Json(await Plus.DtoAsync(m, me.Value.empId));
		}

		// ---------------- what loaded messages carry ----------------
		[HttpGet]
		public async Task<IActionResult> Meta(int c, string? ids)
		{
			var me = await MeAsync(); var ctx = await CtxAsync();
			if (me == null || ctx == null || !await Comm.CanAsync(ctx, CommunicationActions.Read, In(c), HttpContext.RequestAborted)) return Json(Array.Empty<object>());
			return Json(await Plus.MetaAsync(me.Value.companyId, c, me.Value.empId, Ids(ids).Take(200).ToList()));
		}

		// ---------------- speech to text: the words of a voice message ----------------
		[HttpPost, ValidateAntiForgeryToken]
		public async Task<IActionResult> Transcribe(int id)
		{
			var me = await MeAsync(); var ctx = await CtxAsync(); int c = await ConvOfAsync(id);
			if (me == null || ctx == null || c == 0 || !await Comm.CanAsync(ctx, CommunicationActions.Read, In(c), HttpContext.RequestAborted)) return NotFound(new { ok = false });
			var r = await ActivatorUtilities.CreateInstance<ChatTranscriber>(HttpContext.RequestServices).TranscribeAsync(me.Value.companyId, me.Value.empId, id, HttpContext.RequestAborted);
			return Json(new { ok = r.Ok, status = r.Status, text = r.Text, language = r.Language, code = r.Code });
		}

		// ---------------- a video message (owner 2026-11-19: Zoom Clips / Teams video clips) ----------------
		// Recorded in the browser (camera and microphone, at most three minutes), heard and seen before it is sent, then
		// kept like every attachment: in the company's File Manager, served only to the company (PrivateFileGate).
		[HttpPost, ValidateAntiForgeryToken]
		[RequestSizeLimit(80 * 1024 * 1024)]
		public async Task<IActionResult> SendVideo(int c, IFormFile? file, IFormFile? poster, int? replyToId)
		{
			var me = await MeAsync(); var ctx = await CtxAsync();
			if (me == null || ctx == null || !await Comm.CanAsync(ctx, CommunicationActions.Send, In(c), HttpContext.RequestAborted)) return NotFound(new { ok = false });
			if (file == null || file.Length == 0) return BadRequest(new { ok = false });
			var ext = Path.GetExtension(file.FileName ?? "").ToLowerInvariant();
			if (ext is not (".webm" or ".mp4") || !(file.ContentType ?? "").StartsWith("video/", StringComparison.OrdinalIgnoreCase) || file.Length > 75 * 1024 * 1024)
				return BadRequest(new { ok = false, code = "video" });
			var dir = Path.Combine(_env.WebRootPath, "uploads", "library", me.Value.companyId.ToString(), "chat");
			System.IO.Directory.CreateDirectory(dir);
			var stored = Guid.NewGuid().ToString("N") + ext;
			using (var s = System.IO.File.Create(Path.Combine(dir, stored))) await file.CopyToAsync(s);
			// its first picture, taken by the browser from the recording (a recorded WebM cannot be seeked before it has
			// loaded, so a player shows black until it plays): kept beside the video as <video>.jpg — a preview derived
			// from the video, served by the same gate, not a second attachment
			if (poster != null && poster.Length > 0 && poster.Length <= 600 * 1024 && (poster.ContentType ?? "").StartsWith("image/jpeg", StringComparison.OrdinalIgnoreCase))
				using (var ps = System.IO.File.Create(Path.Combine(dir, stored + ".jpg"))) await poster.CopyToAsync(ps);
			var path = "/uploads/library/" + me.Value.companyId + "/chat/" + stored;
			var name = "video-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ext;   // the name people read: local time
			var files = HttpContext.RequestServices.GetService(typeof(IFileManagerService)) as IFileManagerService;
			if (files != null) { try { await files.AddFileAsync(me.Value.companyId, me.Value.empId, null, name, null, path, file.ContentType, file.Length); } catch { } }
			var m = await Plus.PostAsync(me.Value.companyId, c, me.Value.empId, new ChatPlusService.PostInput { AttachmentPath = path, AttachmentName = name, AttachmentType = "video" });
			if (m == null) { System.IO.File.Delete(Path.Combine(dir, stored)); System.IO.File.Delete(Path.Combine(dir, stored + ".jpg")); return BadRequest(new { ok = false }); }
			return Json(await Plus.DtoAsync(m, me.Value.empId));
		}

		// ---------------- the assistant: a summary of the conversation, a question about it, a message translated ----------------
		private CrossBuy.BL.Communication.ChatAssistant Assistant => ActivatorUtilities.CreateInstance<CrossBuy.BL.Communication.ChatAssistant>(HttpContext.RequestServices);

		[HttpPost, ValidateAntiForgeryToken]
		public async Task<IActionResult> Assist(int c, string mode, string? question, int? root, int? since)
		{
			var me = await MeAsync(); var ctx = await CtxAsync();
			if (me == null || ctx == null || !await Comm.CanAsync(ctx, CommunicationActions.Read, In(c), HttpContext.RequestAborted)) return NotFound(new { ok = false });
			var r = await Assistant.StartAsync(me.Value.companyId, me.Value.empId, c, mode, question, root > 0 ? root : null, since > 0 ? since : null, HttpContext.RequestAborted);
			return Json(new { ok = r.Ok, job = r.Job, code = r.Code });
		}
		[HttpGet]
		public async Task<IActionResult> AssistStatus(string job)
		{
			var me = await MeAsync(); if (me == null || string.IsNullOrEmpty(job)) return NotFound(new { ok = false });
			var s = await Assistant.StatusAsync(me.Value.companyId, me.Value.empId, job, HttpContext.RequestAborted);
			return s == null ? NotFound(new { ok = false }) : Json(s);
		}
		[HttpPost, ValidateAntiForgeryToken]
		public async Task<IActionResult> Translate(int id, string? to)
		{
			var me = await MeAsync(); var ctx = await CtxAsync(); int c = await ConvOfAsync(id);
			if (me == null || ctx == null || c == 0 || !await Comm.CanAsync(ctx, CommunicationActions.Read, In(c), HttpContext.RequestAborted)) return NotFound(new { ok = false });
			var r = await Assistant.TranslateAsync(me.Value.companyId, me.Value.empId, id, to, HttpContext.RequestAborted);
			return Json(new { ok = r.Ok, text = r.Text, lang = r.Lang, code = r.Code, job = r.Job });
		}

		// ---------------- pinned and saved messages ----------------
		[HttpPost, ValidateAntiForgeryToken]
		public async Task<IActionResult> PinMessage(int id, bool on)
		{
			var me = await MeAsync(); var ctx = await CtxAsync(); int c = await ConvOfAsync(id);
			if (me == null || ctx == null || c == 0 || !await Comm.CanAsync(ctx, CommunicationActions.Send, In(c), HttpContext.RequestAborted)) return NotFound(new { ok = false });
			return Json(new { ok = await Plus.PinAsync(me.Value.companyId, c, me.Value.empId, id, on) });
		}
		[HttpGet]
		public async Task<IActionResult> Pinned(int c)
		{
			var me = await MeAsync(); var ctx = await CtxAsync();
			if (me == null || ctx == null || !await Comm.CanAsync(ctx, CommunicationActions.Read, In(c), HttpContext.RequestAborted)) return Json(Array.Empty<object>());
			return Json(await Plus.PinnedAsync(me.Value.companyId, c, me.Value.empId));
		}
		[HttpPost, ValidateAntiForgeryToken]
		public async Task<IActionResult> SaveMessage(int id, bool on)
		{
			var me = await MeAsync(); var ctx = await CtxAsync(); int c = await ConvOfAsync(id);
			if (me == null || ctx == null || c == 0 || !await Comm.CanAsync(ctx, CommunicationActions.Read, In(c), HttpContext.RequestAborted)) return NotFound(new { ok = false });
			return Json(new { ok = await Plus.SaveAsync(me.Value.companyId, me.Value.empId, id, on) });
		}
		[HttpGet]
		public async Task<IActionResult> Saved()
		{
			var me = await MeAsync(); if (me == null) return Json(new { saved = Array.Empty<object>(), reminders = Array.Empty<object>() });
			return Json(await Plus.SavedAsync(me.Value.companyId, me.Value.empId));
		}

		// ---------------- forward ----------------
		[HttpPost, ValidateAntiForgeryToken]
		public async Task<IActionResult> Forward(int id, string to, string? note)
		{
			var me = await MeAsync(); var ctx = await CtxAsync(); int c = await ConvOfAsync(id);
			if (me == null || ctx == null || c == 0 || !await Comm.CanAsync(ctx, CommunicationActions.Read, In(c), HttpContext.RequestAborted)) return NotFound(new { ok = false });
			var targets = new List<int>();
			foreach (var t in Ids(to)) if (await Comm.CanAsync(ctx, CommunicationActions.Send, In(t), HttpContext.RequestAborted)) targets.Add(t);
			return Json(new { ok = true, sent = await Plus.ForwardAsync(me.Value.companyId, me.Value.empId, id, targets, note) });
		}

		// ---------------- polls ----------------
		[HttpPost, ValidateAntiForgeryToken]
		public async Task<IActionResult> CreatePoll(int c, string question, string options, bool multi, bool anonymous, string? closesAt)
		{
			var me = await MeAsync(); var ctx = await CtxAsync();
			if (me == null || ctx == null || !await Comm.CanAsync(ctx, CommunicationActions.Send, In(c), HttpContext.RequestAborted)) return NotFound(new { ok = false });
			var opts = (options ?? "").Split('\n').ToList();
			var m = await Plus.CreatePollAsync(me.Value.companyId, c, me.Value.empId, question, opts, multi, anonymous, Utc(closesAt));
			return m == null ? BadRequest(new { ok = false }) : Json(await Plus.DtoAsync(m, me.Value.empId));
		}
		[HttpPost, ValidateAntiForgeryToken]
		public async Task<IActionResult> Vote(int id, string? options)
		{
			var me = await MeAsync(); var ctx = await CtxAsync(); int c = await ConvOfAsync(id);
			if (me == null || ctx == null || c == 0 || !await Comm.CanAsync(ctx, CommunicationActions.Send, In(c), HttpContext.RequestAborted)) return NotFound(new { ok = false });
			return Json(new { ok = await Plus.VoteAsync(me.Value.companyId, me.Value.empId, id, Ids(options)) });
		}
		[HttpPost, ValidateAntiForgeryToken]
		public async Task<IActionResult> ClosePoll(int id)
		{
			var me = await MeAsync(); var ctx = await CtxAsync(); int c = await ConvOfAsync(id);
			if (me == null || ctx == null || c == 0 || !await Comm.CanAsync(ctx, CommunicationActions.Send, In(c), HttpContext.RequestAborted)) return NotFound(new { ok = false });
			return Json(new { ok = await Plus.ClosePollAsync(me.Value.companyId, me.Value.empId, id) });
		}
		[HttpGet]
		public async Task<IActionResult> Poll(int id)
		{
			var me = await MeAsync(); var ctx = await CtxAsync(); int c = await ConvOfAsync(id);
			if (me == null || ctx == null || c == 0 || !await Comm.CanAsync(ctx, CommunicationActions.Read, In(c), HttpContext.RequestAborted)) return NotFound();
			var p = await Plus.PollAsync(me.Value.companyId, id, me.Value.empId);
			return p == null ? NotFound() : Json(p);
		}

		// ---------------- scheduled messages and reminders ----------------
		[HttpPost, ValidateAntiForgeryToken]
		public async Task<IActionResult> Schedule(int c, string body, string sendAt)
		{
			var me = await MeAsync(); var ctx = await CtxAsync();
			if (me == null || ctx == null || !await Comm.CanAsync(ctx, CommunicationActions.Send, In(c), HttpContext.RequestAborted)) return NotFound(new { ok = false });
			var at = Utc(sendAt); if (at == null) return BadRequest(new { ok = false });
			int id = await Plus.ScheduleAsync(me.Value.companyId, c, me.Value.empId, body, at.Value);
			return Json(new { ok = id > 0, id });
		}
		[HttpGet]
		public async Task<IActionResult> Scheduled(int c)
		{
			var me = await MeAsync(); var ctx = await CtxAsync();
			if (me == null || ctx == null || !await Comm.CanAsync(ctx, CommunicationActions.Read, In(c), HttpContext.RequestAborted)) return Json(Array.Empty<object>());
			return Json(await Plus.ScheduledAsync(me.Value.companyId, c, me.Value.empId));
		}
		[HttpPost, ValidateAntiForgeryToken]
		public async Task<IActionResult> CancelScheduled(int id)
		{
			var me = await MeAsync(); var ctx = await CtxAsync();
			if (me == null || ctx == null || !await Comm.CanAsync(ctx, CommunicationActions.Send, null, HttpContext.RequestAborted)) return NotFound(new { ok = false });
			return Json(new { ok = await Plus.CancelScheduledAsync(me.Value.companyId, me.Value.empId, id) });
		}
		[HttpPost, ValidateAntiForgeryToken]
		public async Task<IActionResult> Remind(int id, string at)
		{
			var me = await MeAsync(); var ctx = await CtxAsync(); int c = await ConvOfAsync(id);
			if (me == null || ctx == null || c == 0 || !await Comm.CanAsync(ctx, CommunicationActions.Read, In(c), HttpContext.RequestAborted)) return NotFound(new { ok = false });
			var when = Utc(at); if (when == null) return BadRequest(new { ok = false });
			int rid = await Plus.RemindAsync(me.Value.companyId, me.Value.empId, id, when.Value);
			return Json(new { ok = rid > 0, id = rid });
		}
		[HttpPost, ValidateAntiForgeryToken]
		public async Task<IActionResult> ReminderDone(int id)
		{
			var me = await MeAsync(); var ctx = await CtxAsync();
			if (me == null || ctx == null || !await Comm.CanAsync(ctx, CommunicationActions.Read, null, HttpContext.RequestAborted)) return NotFound(new { ok = false });
			return Json(new { ok = await Plus.ReminderDoneAsync(me.Value.companyId, me.Value.empId, id) });
		}

		// ---------------- status and do-not-disturb ----------------
		[HttpGet]
		public async Task<IActionResult> Statuses(string? ids)
		{
			var me = await MeAsync(); if (me == null) return Json(new { });
			var map = await Plus.StatusesAsync(me.Value.companyId, Ids(ids).Append(me.Value.empId));
			return Json(map.ToDictionary(k => k.Key.ToString(), v => v.Value));
		}
		[HttpPost, ValidateAntiForgeryToken]
		public async Task<IActionResult> SetStatus(string? emoji, string? text, string? until, bool dnd, string? dndUntil)
		{
			var me = await MeAsync(); var ctx = await CtxAsync();
			if (me == null || ctx == null || !await Comm.CanAsync(ctx, CommunicationActions.Send, null, HttpContext.RequestAborted)) return NotFound(new { ok = false });
			await Plus.SetStatusAsync(me.Value.companyId, me.Value.empId, emoji, text, Utc(until), dnd, Utc(dndUntil));
			return Json(new { ok = true, status = await Plus.StatusOfAsync(me.Value.companyId, me.Value.empId) });
		}

		// ---------------- channels and a group's description and picture ----------------
		[HttpPost, ValidateAntiForgeryToken]
		public async Task<IActionResult> CreateChannel(string title, string? description)
		{
			var me = await MeAsync(); var ctx = await CtxAsync();
			if (me == null || ctx == null || !await Comm.CanAsync(ctx, CommunicationActions.CreateGroup, null, HttpContext.RequestAborted)) return NotFound(new { ok = false });
			int id = await Plus.CreateChannelAsync(me.Value.companyId, me.Value.empId, title, description);
			return Json(new { ok = id > 0, id });
		}
		[HttpGet]
		public async Task<IActionResult> Channels(string? q)
		{
			var me = await MeAsync(); if (me == null) return Json(Array.Empty<object>());
			return Json(await Plus.BrowseChannelsAsync(me.Value.companyId, me.Value.empId, q));
		}
		[HttpPost, ValidateAntiForgeryToken]
		public async Task<IActionResult> JoinChannel(int c)
		{
			var me = await MeAsync(); var ctx = await CtxAsync();
			if (me == null || ctx == null || !await Comm.CanAsync(ctx, CommunicationActions.Read, null, HttpContext.RequestAborted)) return NotFound(new { ok = false });
			return Json(new { ok = await Plus.JoinChannelAsync(me.Value.companyId, me.Value.empId, c) });
		}
		// ---------------- guests from outside the company: the group admin's (ChatGuestService) ----------------
		private ChatGuestService GuestSvc => new(_db);
		[HttpGet]
		public async Task<IActionResult> Guests(int c)
		{
			var me = await MeAsync(); var ctx = await CtxAsync();
			if (me == null || ctx == null || !await Comm.CanAsync(ctx, CommunicationActions.Send, In(c), HttpContext.RequestAborted)) return NotFound();
			var l = await GuestSvc.ListAsync(me.Value.companyId, c, me.Value.empId);
			return l == null ? NotFound() : Json(l);
		}
		[HttpPost, ValidateAntiForgeryToken]
		public async Task<IActionResult> CreateGuest(int c, string? name, int days)
		{
			var me = await MeAsync(); var ctx = await CtxAsync();
			if (me == null || ctx == null || !await Comm.CanAsync(ctx, CommunicationActions.Send, In(c), HttpContext.RequestAborted)) return NotFound(new { ok = false });
			var r = await GuestSvc.CreateAsync(me.Value.companyId, c, me.Value.empId, name, days <= 0 ? 7 : days);
			if (r == null) return BadRequest(new { ok = false });
			// the secret rides in the #fragment: a browser never sends it to a server or in a referrer
			return Json(new { ok = true, id = r.Value.id, url = $"{Request.Scheme}://{Request.Host}{Url.Content("~/guest/chat.html")}#{r.Value.id}.{r.Value.token}" });
		}
		[HttpPost, ValidateAntiForgeryToken]
		public async Task<IActionResult> RevokeGuest(int c, int id)
		{
			var me = await MeAsync(); var ctx = await CtxAsync();
			if (me == null || ctx == null || !await Comm.CanAsync(ctx, CommunicationActions.Send, In(c), HttpContext.RequestAborted)) return NotFound(new { ok = false });
			return Json(new { ok = await GuestSvc.RevokeAsync(me.Value.companyId, c, me.Value.empId, id) });
		}

		// ---------------- incoming webhooks: the group admin's (ChatWebhookService) ----------------
		private ChatWebhookService Hooks => new(_db);
		[HttpGet]
		public async Task<IActionResult> Webhooks(int c)
		{
			var me = await MeAsync(); var ctx = await CtxAsync();
			if (me == null || ctx == null || !await Comm.CanAsync(ctx, CommunicationActions.Send, In(c), HttpContext.RequestAborted)) return NotFound();
			var l = await Hooks.ListAsync(me.Value.companyId, c, me.Value.empId);
			return l == null ? NotFound() : Json(l);
		}
		[HttpPost, ValidateAntiForgeryToken]
		public async Task<IActionResult> CreateWebhook(int c, string? name)
		{
			var me = await MeAsync(); var ctx = await CtxAsync();
			if (me == null || ctx == null || !await Comm.CanAsync(ctx, CommunicationActions.Send, In(c), HttpContext.RequestAborted)) return NotFound(new { ok = false });
			var r = await Hooks.CreateAsync(me.Value.companyId, c, me.Value.empId, name);
			if (r == null) return BadRequest(new { ok = false });
			return Json(new { ok = true, id = r.Value.id, url = $"{Request.Scheme}://{Request.Host}/api/hooks/chat/{r.Value.id}/{r.Value.token}" });
		}
		[HttpPost, ValidateAntiForgeryToken]
		public async Task<IActionResult> RevokeWebhook(int c, int id)
		{
			var me = await MeAsync(); var ctx = await CtxAsync();
			if (me == null || ctx == null || !await Comm.CanAsync(ctx, CommunicationActions.Send, In(c), HttpContext.RequestAborted)) return NotFound(new { ok = false });
			return Json(new { ok = await Hooks.RevokeAsync(me.Value.companyId, c, me.Value.empId, id) });
		}

		[HttpGet]
		public async Task<IActionResult> GroupInfo(int c)
		{
			var me = await MeAsync(); var ctx = await CtxAsync();
			if (me == null || ctx == null || !await Comm.CanAsync(ctx, CommunicationActions.Read, In(c), HttpContext.RequestAborted)) return NotFound();
			var g = await Plus.GroupInfoAsync(me.Value.companyId, c, me.Value.empId);
			return g == null ? NotFound() : Json(g);
		}
		[HttpGet]
		public async Task<IActionResult> ConvExtras()
		{
			var me = await MeAsync(); if (me == null) return Json(Array.Empty<object>());
			return Json(await Plus.ConvExtrasAsync(me.Value.companyId, me.Value.empId));
		}
		/// A group's name, description, public/private and picture (the picture goes to the File Manager, like every attachment).
		[HttpPost, ValidateAntiForgeryToken]
		[RequestSizeLimit(6 * 1024 * 1024)]
		public async Task<IActionResult> UpdateGroup(int c, string? title, string? description, bool? isPublic, IFormFile? picture, bool removePicture = false)
		{
			var me = await MeAsync(); var ctx = await CtxAsync();
			if (me == null || ctx == null || !await Comm.CanAsync(ctx, CommunicationActions.ManageGroup, In(c), HttpContext.RequestAborted)) return NotFound(new { ok = false });
			string? avatar = removePicture ? "" : null;
			if (picture != null && picture.Length > 0)
			{
				var ext = Path.GetExtension(picture.FileName ?? "").ToLowerInvariant();
				if (!ChatAttachments.IsImage(ext) || !ChatAttachments.IsAcceptedType(ext, picture.ContentType) || picture.Length > 5 * 1024 * 1024) return BadRequest(new { ok = false, code = "picture" });
				var dir = Path.Combine(_env.WebRootPath, "uploads", "library", me.Value.companyId.ToString(), "chat-groups");
				System.IO.Directory.CreateDirectory(dir);
				var stored = Guid.NewGuid().ToString("N") + ext;
				using (var s = System.IO.File.Create(Path.Combine(dir, stored))) await picture.CopyToAsync(s);
				avatar = "/uploads/library/" + me.Value.companyId + "/chat-groups/" + stored;
				var files = HttpContext.RequestServices.GetService(typeof(IFileManagerService)) as IFileManagerService;
				if (files != null) await files.AddFileAsync(me.Value.companyId, me.Value.empId, null, Path.GetFileName(picture.FileName), null, avatar, picture.ContentType, picture.Length);
			}
			return Json(new { ok = await Plus.UpdateGroupAsync(me.Value.companyId, me.Value.empId, c, title, description, avatar, isPublic), avatar });
		}

		// ---------------- search, catch-up ----------------
		[HttpGet]
		public async Task<IActionResult> Find(string? q, int? c, int? from, string? has)
		{
			var me = await MeAsync(); if (me == null) return Json(Array.Empty<object>());
			return Json(await Plus.FindAsync(me.Value.companyId, me.Value.empId, q ?? "", c, from, has));
		}
		[HttpGet]
		public async Task<IActionResult> CatchUp(int c, int since)
		{
			var me = await MeAsync(); var ctx = await CtxAsync();
			if (me == null || ctx == null || !await Comm.CanAsync(ctx, CommunicationActions.Read, In(c), HttpContext.RequestAborted)) return NotFound();
			var self = await _db.Employee.AsNoTracking().Where(e => e.ID == me.Value.empId).Select(e => new { e.FullName, e.FullNameEn }).FirstOrDefaultAsync();
			var r = await Plus.CatchUpAsync(me.Value.companyId, c, me.Value.empId, since, self?.FullName, self?.FullNameEn);
			return r == null ? NotFound() : Json(r);
		}

		// ---------------- the sensitive-data rule, asked before sending ----------------
		[HttpPost, ValidateAntiForgeryToken]
		public async Task<IActionResult> DlpCheck(string? text)
		{
			var me = await MeAsync(); var ctx = await CtxAsync();
			if (me == null || ctx == null || !await Comm.CanAsync(ctx, CommunicationActions.Send, null, HttpContext.RequestAborted)) return Json(new { mode = "off", hits = Array.Empty<string>() });
			var s = await Plus.SettingsAsync(me.Value.companyId);
			return Json(new { mode = s.DlpMode, hits = ChatDlp.Find(s, text ?? "") });
		}

		// ---------------- a task from a message (through the Tasks module, with its own permissions) ----------------
		[HttpPost, ValidateAntiForgeryToken]
		public async Task<IActionResult> MakeTask(int id, string title, int assignee, string? due, string? priority)
		{
			var me = await MeAsync(); var ctx = await CtxAsync(); int c = await ConvOfAsync(id);
			if (me == null || ctx == null || c == 0 || !await Comm.CanAsync(ctx, CommunicationActions.Read, In(c), HttpContext.RequestAborted)) return NotFound(new { ok = false });
			var tasksAccess = HttpContext.RequestServices.GetService(typeof(ITasksAccessService)) as ITasksAccessService;
			var tasks = HttpContext.RequestServices.GetService(typeof(ITaskService)) as ITaskService;
			if (tasksAccess == null || tasks == null) return Json(new { ok = false, error = "tasks" });
			if (!await tasksAccess.CanAsync(ctx, TasksActions.Create, null, HttpContext.RequestAborted)) return Json(new { ok = false, error = "forbidden" });
			if (assignee <= 0) assignee = me.Value.empId;
			if (assignee != me.Value.empId && !await tasksAccess.CanAsync(ctx, TasksActions.Assign, null, HttpContext.RequestAborted)) return Json(new { ok = false, error = "assign" });
			var msg = await _db.ChatMessages.AsNoTracking().FirstAsync(m => m.ID == id);
			var link = Url.Content("~/Chat") + $"?c={c}&m={id}" + (msg.ThreadRootId is int t ? $"&thread={t}" : "");
			var input = new TaskSaveInput
			{
				Title = string.IsNullOrWhiteSpace(title) ? (msg.Body ?? msg.AttachmentName ?? "-")[..Math.Min(150, (msg.Body ?? msg.AttachmentName ?? "-").Length)] : title.Trim()[..Math.Min(200, title.Trim().Length)],
				Description = (msg.Body ?? "") + "\n\n" + link, AssigneeEmployeeId = assignee, Priority = priority is "High" or "Urgent" or "Low" ? priority : "Normal", DueDate = Utc(due),
			};
			var (ok, error, taskId) = await tasks.SaveAsync(me.Value.companyId, input, me.Value.empId);
			if (ok && taskId > 0)
				await Plus.PostAsync(me.Value.companyId, c, me.Value.empId, new ChatPlusService.PostInput { Body = "✅ " + input.Title + "\n" + Url.Content("~/Tasks/Details/" + taskId), ThreadRootId = msg.ThreadRootId });
			return Json(new { ok, error, id = taskId, url = Url.Content("~/Tasks/Details/" + taskId) });
		}

		// ---------------- a record from the system, as a card ----------------
		[HttpGet]
		public async Task<IActionResult> RecordTypes()
		{
			var me = await MeAsync(); if (me == null) return Json(Array.Empty<object>());
			var reg = HttpContext.RequestServices.GetRequiredService<CrossBuy.BL.Platform.IEntityRegistry>();
			bool ar = System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ar";
			return Json(reg.GetDefinitions().Where(d => d.SupportsSearch).Select(d => new { code = d.Code, name = ar ? d.DisplayNameAr : d.DisplayNameEn, module = d.Module }));
		}
		[HttpGet]
		public async Task<IActionResult> RecordSearch(string code, string? q)
		{
			var ctx = await CtxAsync(); if (ctx == null) return Json(Array.Empty<object>());
			var reg = HttpContext.RequestServices.GetRequiredService<CrossBuy.BL.Platform.IEntityRegistry>();
			if (!reg.TryGetDefinition(code, out var def) || def == null || !def.SupportsSearch) return Json(Array.Empty<object>());
			try { return Json((await reg.SearchAsync(code, q, ctx, HttpContext.RequestAborted)).Take(20).Select(r => new { code = r.EntityCode, id = r.EntityId, label = r.Label, url = r.Url })); }
			catch (Exception) { return Json(Array.Empty<object>()); }
		}
		[HttpGet]
		public async Task<IActionResult> RecordCard(string code, int id)
		{
			var ctx = await CtxAsync(); if (ctx == null) return NotFound();
			var reg = HttpContext.RequestServices.GetRequiredService<CrossBuy.BL.Platform.IEntityRegistry>();
			if (!reg.IsValid(code)) return NotFound();
			try
			{
				var r = await reg.ResolveAsync(code, id, ctx, HttpContext.RequestAborted);
				bool ar = System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ar";
				return Json(new { found = r.Found, label = r.Label, url = r.Url, type = ar ? r.Definition.DisplayNameAr : r.Definition.DisplayNameEn, module = r.Definition.Module, color = r.Definition.Color });
			}
			catch (Exception) { return NotFound(); }
		}

		// ---------------- a meeting from a conversation: an event in everyone's calendar, its way back to the conversation ----------------
		[HttpPost, ValidateAntiForgeryToken]
		public async Task<IActionResult> ScheduleMeeting(int c, string title, string start, int minutes, string? note)
		{
			var me = await MeAsync(); var ctx = await CtxAsync();
			if (me == null || ctx == null || !await Comm.CanAsync(ctx, CommunicationActions.Send, In(c), HttpContext.RequestAborted)) return NotFound(new { ok = false });
			var calAccess = HttpContext.RequestServices.GetService(typeof(ICalendarAccessService)) as ICalendarAccessService;
			var cal = HttpContext.RequestServices.GetService(typeof(ICalendarService)) as ICalendarService;
			if (cal == null || calAccess == null || !await calAccess.CanAsync(ctx, CalendarActions.Create, null, HttpContext.RequestAborted)) return Json(new { ok = false, error = "calendar" });
			var at = Utc(start); if (at == null || string.IsNullOrWhiteSpace(title)) return BadRequest(new { ok = false });
			var people = await _db.ConversationMembers.AsNoTracking().Where(m => m.ConversationId == c).Select(m => m.EmployeeId).ToListAsync();
			var link = Url.Content("~/Chat") + "?c=" + c;
			var local = at.Value.ToLocalTime();
			int id = await cal.SaveAsync(me.Value.companyId, me.Value.empId, new CalEventInput
			{
				Title = title.Trim()[..Math.Min(150, title.Trim().Length)], Description = ((note ?? "").Trim() + "\n" + link).Trim(), Location = link,
				StartAt = local, EndAt = local.AddMinutes(Math.Clamp(minutes, 10, 600)), Scope = "Personal", Attendees = people,
			});
			if (id > 0)
				await Plus.PostAsync(me.Value.companyId, c, me.Value.empId, new ChatPlusService.PostInput { Body = "📅 " + title.Trim() + "\n" + local.ToString("yyyy-MM-dd HH:mm") + " · " + Math.Clamp(minutes, 10, 600) + "′" });
			return Json(new { ok = id > 0, id });
		}

		// ---------------- approvals waiting for me ----------------
		[HttpGet]
		public async Task<IActionResult> Approvals()
		{
			var me = await MeAsync(); var ctx = await CtxAsync(); if (me == null || ctx == null) return Json(new { total = 0, rows = Array.Empty<object>() });
			var inbox = HttpContext.RequestServices.GetService(typeof(CrossBuy.BL.Approvals.IApprovalInboxService)) as CrossBuy.BL.Approvals.IApprovalInboxService;
			if (inbox == null) return Json(new { total = 0, rows = Array.Empty<object>() });
			try
			{
				var page = await inbox.GetPendingForCurrentApproverAsync(ctx, me.Value.empId, 30, HttpContext.RequestAborted);
				bool ar = System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ar";
				return Json(new
				{
					total = page.TotalPending,
					rows = page.Rows.Select(r => new
					{
						silo = r.Silo, reference = r.Reference,
						title = ar ? (r.TitleAr ?? r.TitleEn) : (r.TitleEn ?? r.TitleAr),
						requester = ar ? (r.RequesterNameAr ?? r.RequesterNameEn) : (r.RequesterNameEn ?? r.RequesterNameAr), requesterId = r.RequesterEmployeeId,
						at = r.SubmittedAt, amount = r.Amount, days = r.Days,
						url = Url.Action(r.Navigation.Action, r.Navigation.Controller, r.Navigation.RouteValues == null ? null : new Microsoft.AspNetCore.Routing.RouteValueDictionary(r.Navigation.RouteValues.ToDictionary(k => k.Key, v => (object?)v.Value))),
					}),
				});
			}
			catch (Exception) { return Json(new { total = 0, rows = Array.Empty<object>() }); }
		}

		// ---------------- governance (the Communication administrator) ----------------
		[HttpGet]
		public async Task<IActionResult> Governance()
		{
			var me = await MeAsync(); var ctx = await CtxAsync();
			if (me == null || ctx == null || !(await Comm.CanAsync(ctx, CommunicationActions.ManageGroup, null, HttpContext.RequestAborted) || await MayManageCallsAsync())) return NotFound();
			var s = await Plus.SettingsAsync(me.Value.companyId);
			return Json(new
			{
				retentionDays = s.RetentionDays, dlpMode = s.DlpMode, dlpCards = s.DlpCards, dlpIban = s.DlpIban, dlpNationalId = s.DlpNationalId, dlpWords = s.DlpWords,
				holds = await Plus.HoldsAsync(me.Value.companyId), exports = await Plus.ExportsAsync(me.Value.companyId), usage = await Plus.UsageAsync(me.Value.companyId),
			});
		}
		[HttpPost, ValidateAntiForgeryToken]
		public async Task<IActionResult> SaveGovernance(int retentionDays, string dlpMode, bool dlpCards, bool dlpIban, bool dlpNationalId, string? dlpWords)
		{
			var me = await MeAsync(); var ctx = await CtxAsync();
			if (me == null || ctx == null || !(await Comm.CanAsync(ctx, CommunicationActions.ManageGroup, null, HttpContext.RequestAborted) || await MayManageCallsAsync())) return NotFound(new { ok = false });
			await Plus.SaveSettingsAsync(me.Value.companyId, me.Value.empId, retentionDays, dlpMode, dlpCards, dlpIban, dlpNationalId, dlpWords);
			return Json(new { ok = true });
		}
		[HttpPost, ValidateAntiForgeryToken]
		public async Task<IActionResult> AddHold(int? employeeId, int? conversationId, string reason)
		{
			var me = await MeAsync(); var ctx = await CtxAsync();
			if (me == null || ctx == null || !(await Comm.CanAsync(ctx, CommunicationActions.ManageGroup, null, HttpContext.RequestAborted) || await MayManageCallsAsync())) return NotFound(new { ok = false });
			int id = await Plus.AddHoldAsync(me.Value.companyId, me.Value.empId, employeeId > 0 ? employeeId : null, conversationId > 0 ? conversationId : null, reason);
			return Json(new { ok = id > 0, id });
		}
		[HttpPost, ValidateAntiForgeryToken]
		public async Task<IActionResult> ReleaseHold(int id)
		{
			var me = await MeAsync(); var ctx = await CtxAsync();
			if (me == null || ctx == null || !(await Comm.CanAsync(ctx, CommunicationActions.ManageGroup, null, HttpContext.RequestAborted) || await MayManageCallsAsync())) return NotFound(new { ok = false });
			return Json(new { ok = await Plus.ReleaseHoldAsync(me.Value.companyId, me.Value.empId, id) });
		}
		/// The compliance export (CSV). A POST, because it writes the export's own audit row.
		[HttpPost, ValidateAntiForgeryToken]
		public async Task<IActionResult> Export(int? employeeId, int? conversationId, string? from, string? to)
		{
			var me = await MeAsync(); var ctx = await CtxAsync();
			if (me == null || ctx == null || !(await Comm.CanAsync(ctx, CommunicationActions.ManageGroup, null, HttpContext.RequestAborted) || await MayManageCallsAsync())) return NotFound();
			if (employeeId is not > 0 && conversationId is not > 0) return BadRequest();
			var (csv, _) = await Plus.ExportAsync(me.Value.companyId, me.Value.empId, employeeId > 0 ? employeeId : null, conversationId > 0 ? conversationId : null, Utc(from), Utc(to)?.AddDays(1));
			return File(csv, "text/csv; charset=utf-8", $"chat-export-{DateTime.UtcNow:yyyyMMdd-HHmm}.csv");
		}
		/// Conversations to choose from for a legal hold or an export (groups and channels by name; a direct chat by its two people).
		[HttpGet]
		public async Task<IActionResult> AllConversations(string? q)
		{
			var me = await MeAsync(); var ctx = await CtxAsync();
			if (me == null || ctx == null || !(await Comm.CanAsync(ctx, CommunicationActions.ManageGroup, null, HttpContext.RequestAborted) || await MayManageCallsAsync())) return Json(Array.Empty<object>());
			var rows = await _db.Conversations.AsNoTracking().Where(c => c.CompanyID == me.Value.companyId && c.Kind == "Group" && (q == null || c.Title!.Contains(q))).OrderBy(c => c.Title).Take(50).Select(c => new { id = c.ID, title = c.Title }).ToListAsync();
			return Json(rows);
		}
	}
}
