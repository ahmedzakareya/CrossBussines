using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using CrossBuy.Hubs;
using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Chat;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL.Communication
{
	// ==========================================================================================
	// COMMUNICATION v2 (owner, 2026-11-18: "build every gap, completely") — what the leading team-chat systems have
	// (Slack, Teams, Google Chat, WhatsApp, Bitrix24, Odoo, Mattermost) and ours did not.
	//
	// The CALLER authorizes: every public method takes the company and the employee the controller resolved and
	// checked (communication "read"/"send" on the conversation). This service still re-checks membership wherever a
	// conversation is named, so a wrong id answers "not found", never another company's or another team's data.
	// ==========================================================================================
	public sealed class ChatPlusService
	{
		private readonly CrossDbContext _db;
		private readonly IHubContext<ChatHub> _hub;
		private readonly INotificationService _notify;
		private readonly IServiceProvider? _services;   // only to hand a wake-up to PushWake; optional so a hand-built service still works
		public ChatPlusService(CrossDbContext db, IHubContext<ChatHub> hub, INotificationService notify, IServiceProvider? services = null) { _db = db; _hub = hub; _notify = notify; _services = services; }

		public const int MaxBody = 4000;
		private static bool IsAr => CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ar";
		private static string Nm(string? ar, string? en) => (IsAr ? (ar ?? en) : (string.IsNullOrWhiteSpace(en) ? ar : en)) ?? "-";

		public async Task<bool> IsMemberAsync(int companyId, int convId, int empId)
			=> await _db.Conversations.AsNoTracking().AnyAsync(c => c.ID == convId && c.CompanyID == companyId)
			&& await _db.ConversationMembers.AsNoTracking().AnyAsync(m => m.ConversationId == convId && m.EmployeeId == empId);

		private async Task<Dictionary<int, (string name, string? avatar)>> NamesAsync(IEnumerable<int> ids)
		{
			var list = ids.Distinct().ToList(); if (list.Count == 0) return new();
			var emps = await _db.Employee.AsNoTracking().Where(e => list.Contains(e.ID)).Select(e => new { e.ID, e.FullName, e.FullNameEn, e.ProfileImage }).ToListAsync();
			return emps.ToDictionary(e => e.ID, e => (Nm(e.FullName, e.FullNameEn), string.IsNullOrEmpty(e.ProfileImage) ? (string?)null : e.ProfileImage));
		}

		/// One line of what a message says: its formatting marks (*b* _i_ ~s~ `code`, list and quote markers, a record
		/// token) are the composer's, not the reader's.
		private static string Plain(string body)
		{
			var s = System.Text.RegularExpressions.Regex.Replace(body, @"#rec:[A-Za-z.]+:\d+", "");
			s = System.Text.RegularExpressions.Regex.Replace(s, @"```|(?<!\w)[*_~`]|[*_~`](?!\w)", "");
			s = System.Text.RegularExpressions.Regex.Replace(s, @"(^|\n)\s*(- |> )", "$1");
			return System.Text.RegularExpressions.Regex.Replace(s, @"\s+", " ").Trim();
		}
		private static string Preview(string? body, string? attType, string? attName)
			=> !string.IsNullOrEmpty(body) ? (Plain(body!) is var p && p.Length > 120 ? p[..120] : Plain(body!))
			: attType == "image" ? "📷" : attType == "audio" ? "🎤" : attType == "video" ? "🎬" : attType == "poll" ? "📊" : attType == "doc" ? "📝 " + (attName ?? "") : "📎 " + (attName ?? "");

		private ChatMsgDto Dto(ChatMessage m, int meId, Dictionary<int, (string name, string? avatar)> names)
		{
			names.TryGetValue(m.SenderEmployeeId, out var info);
			return new ChatMsgDto
			{
				Id = m.ID, SenderId = m.SenderEmployeeId, SenderName = info.name ?? "-", SenderAvatar = info.avatar,
				Body = m.DeletedAt != null ? null : m.Body,
				AttachmentPath = m.DeletedAt != null ? null : m.AttachmentPath, AttachmentName = m.AttachmentName, AttachmentType = m.AttachmentType,
				Mine = m.SenderEmployeeId == meId, CreatedAt = m.CreatedAt, EditedAt = m.EditedAt, Deleted = m.DeletedAt != null, ReplyToId = m.ReplyToId,
				Reactions = new List<ChatReactionDto>(),
			};
		}

		// ------------------------------------------------------------------------------------------
		// POSTING — one path for a thread reply, a forward, a poll and a scheduled message (the worker has no request,
		// so the company and the sender are explicit). The realtime events and notices match an ordinary message's.
		// ------------------------------------------------------------------------------------------
		public sealed class PostInput
		{
			public string? Body { get; set; }
			public int? ThreadRootId { get; set; }
			public int? ForwardedFromId { get; set; }
			public string? AttachmentPath { get; set; }
			public string? AttachmentName { get; set; }
			public string? AttachmentType { get; set; }
			public IEnumerable<int>? Mentioned { get; set; }
		}

		public async Task<ChatMessage?> PostAsync(int companyId, int convId, int senderId, PostInput p)
		{
			if (!await IsMemberAsync(companyId, convId, senderId)) return null;
			var body = p.Body?.Trim();
			if (string.IsNullOrEmpty(body) && string.IsNullOrEmpty(p.AttachmentPath)) return null;
			if (body != null && body.Length > MaxBody) return null;
			if (body != null && await ChatDlp.BlocksAsync(_db, companyId, body)) return null;
			if (p.ThreadRootId is int root && !await _db.ChatMessages.AsNoTracking().AnyAsync(m => m.ID == root && m.ConversationId == convId && m.ThreadRootId == null)) return null;
			var now = DateTime.UtcNow;
			var msg = new ChatMessage
			{
				ConversationId = convId, SenderEmployeeId = senderId, Body = string.IsNullOrEmpty(body) ? null : body, ThreadRootId = p.ThreadRootId, ForwardedFromId = p.ForwardedFromId,
				AttachmentPath = p.AttachmentPath, AttachmentName = p.AttachmentName, AttachmentType = p.AttachmentType, CreatedAt = now,
			};
			_db.ChatMessages.Add(msg);
			await _db.SaveChangesAsync();
			// an attachment served by the chat's own action names its message: a copy names the new one
			if (msg.AttachmentPath != null && msg.AttachmentPath.Contains("messageId=", StringComparison.Ordinal))
			{
				msg.AttachmentPath = Regex.Replace(msg.AttachmentPath, @"messageId=(\{messageId\}|\d+)", "messageId=" + msg.ID);
				await _db.SaveChangesAsync();
			}
			var preview = Preview(msg.Body, msg.AttachmentType, msg.AttachmentName);
			var conv = await _db.Conversations.FirstAsync(c => c.ID == convId);
			if (msg.ThreadRootId == null) { conv.LastMessageAt = now; conv.LastMessagePreview = preview; }
			var mine = await _db.ConversationMembers.FirstOrDefaultAsync(m => m.ConversationId == convId && m.EmployeeId == senderId);
			if (mine != null && msg.ThreadRootId == null) mine.LastReadMessageId = Math.Max(mine.LastReadMessageId, msg.ID);
			await _db.SaveChangesAsync();

			var names = await NamesAsync(new[] { senderId });
			var dto = Dto(msg, senderId, names);
			if (msg.ThreadRootId is int rootId)
			{
				await _hub.Clients.Group(ChatHub.ConvGroup(convId)).SendAsync("thread", new { conversationId = convId, rootId, message = dto });
			}
			else
			{
				await _hub.Clients.Group(ChatHub.ConvGroup(convId)).SendAsync("message", new
				{
					conversationId = convId, id = dto.Id, senderId = dto.SenderId, senderName = dto.SenderName, senderAvatar = dto.SenderAvatar,
					body = dto.Body, attachmentPath = dto.AttachmentPath, attachmentName = dto.AttachmentName, attachmentType = dto.AttachmentType, createdAt = dto.CreatedAt,
				});
			}

			// who hears about it: everyone (an ordinary message), or the thread's author and the people in the thread
			var audience = msg.ThreadRootId is int r2
				? (await _db.ChatMessages.AsNoTracking().Where(m => m.ID == r2 || m.ThreadRootId == r2).Select(m => m.SenderEmployeeId).Distinct().ToListAsync())
				: (await _db.ConversationMembers.AsNoTracking().Where(m => m.ConversationId == convId).Select(m => m.EmployeeId).ToListAsync());
			audience.Remove(senderId);
			var muted = (await _db.ConversationMembers.AsNoTracking().Where(m => m.ConversationId == convId && m.MutedUntil != null && m.MutedUntil > now).Select(m => m.EmployeeId).ToListAsync()).ToHashSet();
			var mset = (p.Mentioned ?? Enumerable.Empty<int>()).ToHashSet();
			var who = await _db.Employee.AsNoTracking().Where(e => e.ID == senderId).Select(e => new { e.FullName, e.FullNameEn }).FirstOrDefaultAsync();
			string ar = who?.FullName ?? "", en = string.IsNullOrWhiteSpace(who?.FullNameEn) ? ar : who!.FullNameEn!;
			string url = msg.ThreadRootId is int r3 ? $"/Chat?c={convId}&thread={r3}" : $"/Chat?c={convId}";
			foreach (var mem in audience)
			{
				if (msg.ThreadRootId == null) await _hub.Clients.Group(ChatHub.UserGroup(mem)).SendAsync("conversation", new { conversationId = convId, preview, lastAt = now, from = senderId });
				bool mention = mset.Contains(mem);
				if (!mention && muted.Contains(mem)) continue;
				try
				{
					await _notify.NotifyAsync(mem, ar, en, preview, preview, mention ? NotificationTypes.ChatMention : NotificationTypes.ChatMessage, convId,
						url: url, companyId: companyId, actorEmployeeId: senderId, dedupKey: msg.ThreadRootId != null ? $"chat-thread-{msg.ThreadRootId}" : (mention ? $"chat-mention-{convId}" : $"chat-{convId}"));
				}
				catch { /* a notice never blocks the message */ }
			}
			// a closed phone hears it too: whoever the bell tells above (PushWake — the kind of news only, never the text)
			if (_services != null) PushWake.Enqueue(_services, companyId, audience.Where(m => mset.Contains(m) || !muted.Contains(m)), "msg");
			return msg;
		}

		public async Task<ChatMsgDto> DtoAsync(ChatMessage m, int meId) => Dto(m, meId, await NamesAsync(new[] { m.SenderEmployeeId }));

		// ------------------------------------------------------------------------------------------
		// THREADS
		// ------------------------------------------------------------------------------------------
		public async Task<List<ChatMsgDto>> ThreadAsync(int companyId, int convId, int rootId, int meId)
		{
			if (!await IsMemberAsync(companyId, convId, meId)) return new();
			var msgs = await _db.ChatMessages.AsNoTracking().Where(m => m.ConversationId == convId && (m.ID == rootId || m.ThreadRootId == rootId)).OrderBy(m => m.ID).Take(300).ToListAsync();
			var names = await NamesAsync(msgs.Select(m => m.SenderEmployeeId));
			var ids = msgs.Select(m => m.ID).ToList();
			var rx = await _db.ChatReactions.AsNoTracking().Where(r => ids.Contains(r.MessageId)).ToListAsync();
			return msgs.Select(m =>
			{
				var d = Dto(m, meId, names);
				d.Reactions = rx.Where(r => r.MessageId == m.ID).GroupBy(r => r.Emoji).Select(g => new ChatReactionDto { Emoji = g.Key, Count = g.Count(), Mine = g.Any(x => x.EmployeeId == meId) }).ToList();
				return d;
			}).ToList();
		}

		// ------------------------------------------------------------------------------------------
		// WHAT EACH LOADED MESSAGE CARRIES BEYOND ITSELF: its thread, forwarded-from, pinned, saved by me, its poll
		// ------------------------------------------------------------------------------------------
		public async Task<object> MetaAsync(int companyId, int convId, int meId, IReadOnlyCollection<int> ids)
		{
			if (ids.Count == 0 || !await IsMemberAsync(companyId, convId, meId)) return Array.Empty<object>();
			var own = await _db.ChatMessages.AsNoTracking().Where(m => ids.Contains(m.ID) && m.ConversationId == convId)
				.Select(m => new { m.ID, m.ForwardedFromId, m.PinnedAt, m.PinnedByEmployeeId, m.AttachmentType }).ToListAsync();
			var okIds = own.Select(x => x.ID).ToList();
			var threads = await _db.ChatMessages.AsNoTracking().Where(m => m.ThreadRootId != null && okIds.Contains(m.ThreadRootId.Value) && m.DeletedAt == null)
				.GroupBy(m => m.ThreadRootId!.Value).Select(g => new { Root = g.Key, Count = g.Count(), Last = g.Max(x => x.CreatedAt), People = g.Select(x => x.SenderEmployeeId).Distinct().Take(3).ToList() }).ToListAsync();
			var fwdIds = own.Where(x => x.ForwardedFromId != null).Select(x => x.ForwardedFromId!.Value).ToList();
			var fwd = await _db.ChatMessages.AsNoTracking().Where(m => fwdIds.Contains(m.ID)).Select(m => new { m.ID, m.SenderEmployeeId }).ToListAsync();
			var saved = (await _db.Set<ChatSaved>().AsNoTracking().Where(s => s.EmployeeId == meId && okIds.Contains(s.MessageId)).Select(s => s.MessageId).ToListAsync()).ToHashSet();
			var reminded = (await _db.Set<ChatReminder>().AsNoTracking().Where(s => s.EmployeeId == meId && okIds.Contains(s.MessageId) && s.DoneAt == null).Select(s => s.MessageId).ToListAsync()).ToHashSet();
			var pollIds = own.Where(x => x.AttachmentType == "poll").Select(x => x.ID).ToList();
			var polls = new Dictionary<int, object>();
			foreach (var pid in pollIds) { var p = await PollAsync(companyId, pid, meId); if (p != null) polls[pid] = p; }
			var transcripts = await ChatTranscriber.StoredAsync(_db, own.Where(x => x.AttachmentType == "audio").Select(x => x.ID).ToList());
			var translations = await ChatAssistant.StoredAsync(_db, okIds);
			var via = await ChatWebhookService.ViaAsync(_db, okIds);
			var guestOf = await ChatGuestService.GuestOfAsync(_db, okIds);
			var names = await NamesAsync(threads.SelectMany(t => t.People).Concat(fwd.Select(f => f.SenderEmployeeId)).Concat(own.Where(o => o.PinnedByEmployeeId != null).Select(o => o.PinnedByEmployeeId!.Value)));
			return own.Select(x =>
			{
				var th = threads.FirstOrDefault(t => t.Root == x.ID);
				var fw = x.ForwardedFromId is int f ? fwd.FirstOrDefault(z => z.ID == f) : null;
				return new
				{
					id = x.ID,
					thread = th == null ? null : new { count = th.Count, last = DateTime.SpecifyKind(th.Last, DateTimeKind.Utc), people = th.People.Where(names.ContainsKey).Select(id => new { id, name = names[id].name, avatar = names[id].avatar }) },
					forwarded = x.ForwardedFromId != null, forwardedFrom = fw != null && names.TryGetValue(fw.SenderEmployeeId, out var fn) ? fn.name : null,
					pinned = x.PinnedAt != null, pinnedBy = x.PinnedByEmployeeId is int pb && names.TryGetValue(pb, out var pn) ? pn.name : null,
					saved = saved.Contains(x.ID), reminded = reminded.Contains(x.ID),
					poll = polls.TryGetValue(x.ID, out var po) ? po : null,
					transcript = transcripts.TryGetValue(x.ID, out var tr) ? new { status = tr.Status, text = tr.Text, language = tr.Language } : null,
					translations = translations.TryGetValue(x.ID, out var tl) ? tl : null,
					via = via.TryGetValue(x.ID, out var vh) ? vh : null,
					guest = guestOf.TryGetValue(x.ID, out var gn) ? gn : null,
				};
			}).ToList();
		}

		// ------------------------------------------------------------------------------------------
		// PINNED MESSAGES (in a conversation, for everyone in it) and SAVED MESSAGES (mine)
		// ------------------------------------------------------------------------------------------
		public async Task<bool> PinAsync(int companyId, int convId, int meId, int msgId, bool on)
		{
			if (!await IsMemberAsync(companyId, convId, meId)) return false;
			var m = await _db.ChatMessages.FirstOrDefaultAsync(x => x.ID == msgId && x.ConversationId == convId && x.DeletedAt == null);
			if (m == null) return false;
			if (on && await _db.ChatMessages.CountAsync(x => x.ConversationId == convId && x.PinnedAt != null) >= 50) return false;
			m.PinnedAt = on ? DateTime.UtcNow : null; m.PinnedByEmployeeId = on ? meId : null;
			await _db.SaveChangesAsync();
			await _hub.Clients.Group(ChatHub.ConvGroup(convId)).SendAsync("pinned", new { conversationId = convId, id = msgId, on });
			return true;
		}
		public async Task<List<object>> PinnedAsync(int companyId, int convId, int meId)
		{
			if (!await IsMemberAsync(companyId, convId, meId)) return new();
			var rows = await _db.ChatMessages.AsNoTracking().Where(m => m.ConversationId == convId && m.PinnedAt != null && m.DeletedAt == null).OrderByDescending(m => m.PinnedAt).Take(50).ToListAsync();
			var names = await NamesAsync(rows.Select(r => r.SenderEmployeeId).Concat(rows.Where(r => r.PinnedByEmployeeId != null).Select(r => r.PinnedByEmployeeId!.Value)));
			return rows.Select(r => (object)new { id = r.ID, body = Preview(r.Body, r.AttachmentType, r.AttachmentName), sender = names.GetValueOrDefault(r.SenderEmployeeId).name, by = r.PinnedByEmployeeId is int b ? names.GetValueOrDefault(b).name : null, at = DateTime.SpecifyKind(r.PinnedAt!.Value, DateTimeKind.Utc) }).ToList();
		}
		public async Task<bool> SaveAsync(int companyId, int meId, int msgId, bool on)
		{
			var m = await _db.ChatMessages.AsNoTracking().Where(x => x.ID == msgId).Select(x => new { x.ConversationId }).FirstOrDefaultAsync();
			if (m == null || !await IsMemberAsync(companyId, m.ConversationId, meId)) return false;
			var row = await _db.Set<ChatSaved>().FirstOrDefaultAsync(s => s.EmployeeId == meId && s.MessageId == msgId);
			if (on && row == null) _db.Add(new ChatSaved { CompanyId = companyId, EmployeeId = meId, MessageId = msgId, SavedAt = DateTime.UtcNow });
			if (!on && row != null) _db.Remove(row);
			await _db.SaveChangesAsync();
			return true;
		}
		/// My saved messages and my reminders, newest first — the "Saved" view.
		public async Task<object> SavedAsync(int companyId, int meId)
		{
			var mineConvs = _db.ConversationMembers.AsNoTracking().Where(m => m.EmployeeId == meId).Select(m => m.ConversationId);
			var saved = await _db.Set<ChatSaved>().AsNoTracking().Where(s => s.EmployeeId == meId && s.CompanyId == companyId).OrderByDescending(s => s.SavedAt).Take(100).ToListAsync();
			var rem = await _db.Set<ChatReminder>().AsNoTracking().Where(s => s.EmployeeId == meId && s.CompanyId == companyId && s.DoneAt == null).OrderBy(s => s.RemindAt).Take(100).ToListAsync();
			var ids = saved.Select(s => s.MessageId).Concat(rem.Select(r => r.MessageId)).Distinct().ToList();
			var msgs = await _db.ChatMessages.AsNoTracking().Where(m => ids.Contains(m.ID) && mineConvs.Contains(m.ConversationId)).ToDictionaryAsync(m => m.ID);
			var convIds = msgs.Values.Select(m => m.ConversationId).Distinct().ToList();
			var titles = await ConvTitlesAsync(convIds, meId);
			var names = await NamesAsync(msgs.Values.Select(m => m.SenderEmployeeId));
			object Row(int id, DateTime at, int? remId, DateTime? remindAt)
			{
				var m = msgs[id];
				return new { id, conversationId = m.ConversationId, conversation = titles.GetValueOrDefault(m.ConversationId), sender = names.GetValueOrDefault(m.SenderEmployeeId).name, avatar = names.GetValueOrDefault(m.SenderEmployeeId).avatar,
					body = m.DeletedAt != null ? null : Preview(m.Body, m.AttachmentType, m.AttachmentName), at = DateTime.SpecifyKind(at, DateTimeKind.Utc), reminderId = remId, remindAt = remindAt == null ? (DateTime?)null : DateTime.SpecifyKind(remindAt.Value, DateTimeKind.Utc), threadRootId = m.ThreadRootId };
			}
			return new
			{
				saved = saved.Where(s => msgs.ContainsKey(s.MessageId)).Select(s => Row(s.MessageId, s.SavedAt, null, null)),
				reminders = rem.Where(r => msgs.ContainsKey(r.MessageId)).Select(r => Row(r.MessageId, r.CreatedAt, r.ID, r.RemindAt)),
			};
		}
		private async Task<Dictionary<int, string>> ConvTitlesAsync(List<int> convIds, int meId)
		{
			var convs = await _db.Conversations.AsNoTracking().Where(c => convIds.Contains(c.ID)).Select(c => new { c.ID, c.Kind, c.Title }).ToListAsync();
			var directs = convs.Where(c => c.Kind == "Direct").Select(c => c.ID).ToList();
			var others = await _db.ConversationMembers.AsNoTracking().Where(m => directs.Contains(m.ConversationId) && m.EmployeeId != meId).Select(m => new { m.ConversationId, m.EmployeeId }).ToListAsync();
			var names = await NamesAsync(others.Select(o => o.EmployeeId));
			return convs.ToDictionary(c => c.ID, c => c.Kind == "Direct" ? (others.FirstOrDefault(o => o.ConversationId == c.ID) is { } o && names.TryGetValue(o.EmployeeId, out var n) ? n.name : "-") : (c.Title ?? "-"));
		}

		// ------------------------------------------------------------------------------------------
		// FORWARD — a copy in each chosen conversation of mine, marked as forwarded
		// ------------------------------------------------------------------------------------------
		public async Task<int> ForwardAsync(int companyId, int meId, int msgId, IEnumerable<int> toConvs, string? note)
		{
			var src = await _db.ChatMessages.AsNoTracking().FirstOrDefaultAsync(m => m.ID == msgId && m.DeletedAt == null);
			if (src == null || !await IsMemberAsync(companyId, src.ConversationId, meId) || src.AttachmentType is "poll" or "doc") return 0;   // a poll and a shared document belong to their conversation
			int n = 0;
			foreach (var c in toConvs.Distinct().Take(20))
			{
				if (!string.IsNullOrWhiteSpace(note)) await PostAsync(companyId, c, meId, new PostInput { Body = note });
				var copy = await PostAsync(companyId, c, meId, new PostInput { Body = src.Body, ForwardedFromId = src.ID, AttachmentPath = src.AttachmentPath, AttachmentName = src.AttachmentName, AttachmentType = src.AttachmentType });
				if (copy != null) n++;
			}
			return n;
		}

		// ------------------------------------------------------------------------------------------
		// POLLS — a message whose kind is "poll"
		// ------------------------------------------------------------------------------------------
		public async Task<ChatMessage?> CreatePollAsync(int companyId, int convId, int meId, string question, IList<string> options, bool multi, bool anonymous, DateTime? closesAt)
		{
			question = (question ?? "").Trim();
			var opts = options.Select(o => (o ?? "").Trim()).Where(o => o.Length > 0).Distinct().Take(12).ToList();
			if (question.Length == 0 || question.Length > 300 || opts.Count < 2 || opts.Any(o => o.Length > 150)) return null;
			var msg = await PostAsync(companyId, convId, meId, new PostInput { Body = question, AttachmentType = "poll", AttachmentName = "poll" });
			if (msg == null) return null;
			var poll = new ChatPoll { CompanyId = companyId, MessageId = msg.ID, Question = question, Multi = multi, Anonymous = anonymous, ClosesAt = closesAt, CreatedAt = DateTime.UtcNow };
			_db.Add(poll); await _db.SaveChangesAsync();
			int i = 0; foreach (var o in opts) _db.Add(new ChatPollOption { PollId = poll.ID, Text = o, Sort = i++ });
			await _db.SaveChangesAsync();
			return msg;
		}
		public async Task<object?> PollAsync(int companyId, int msgId, int meId)
		{
			var poll = await _db.Set<ChatPoll>().AsNoTracking().FirstOrDefaultAsync(p => p.MessageId == msgId && p.CompanyId == companyId);
			if (poll == null) return null;
			var opts = await _db.Set<ChatPollOption>().AsNoTracking().Where(o => o.PollId == poll.ID).OrderBy(o => o.Sort).ToListAsync();
			var votes = await _db.Set<ChatPollVote>().AsNoTracking().Where(v => v.PollId == poll.ID).ToListAsync();
			var names = poll.Anonymous ? new() : await NamesAsync(votes.Select(v => v.EmployeeId));
			bool closed = poll.ClosedAt != null || (poll.ClosesAt != null && poll.ClosesAt < DateTime.UtcNow);
			return new
			{
				id = poll.ID, question = poll.Question, multi = poll.Multi, anonymous = poll.Anonymous, closed, closesAt = poll.ClosesAt == null ? (DateTime?)null : DateTime.SpecifyKind(poll.ClosesAt.Value, DateTimeKind.Utc),
				voters = votes.Select(v => v.EmployeeId).Distinct().Count(),
				options = opts.Select(o => new
				{
					id = o.ID, text = o.Text, votes = votes.Count(v => v.OptionId == o.ID), mine = votes.Any(v => v.OptionId == o.ID && v.EmployeeId == meId),
					who = poll.Anonymous ? null : votes.Where(v => v.OptionId == o.ID).Select(v => names.GetValueOrDefault(v.EmployeeId).name).Take(20),
				}),
			};
		}
		public async Task<bool> VoteAsync(int companyId, int meId, int msgId, IList<int> optionIds)
		{
			var poll = await _db.Set<ChatPoll>().FirstOrDefaultAsync(p => p.MessageId == msgId && p.CompanyId == companyId);
			if (poll == null || poll.ClosedAt != null || (poll.ClosesAt != null && poll.ClosesAt < DateTime.UtcNow)) return false;
			var convId = await _db.ChatMessages.AsNoTracking().Where(m => m.ID == msgId).Select(m => m.ConversationId).FirstAsync();
			if (!await IsMemberAsync(companyId, convId, meId)) return false;
			var valid = await _db.Set<ChatPollOption>().AsNoTracking().Where(o => o.PollId == poll.ID).Select(o => o.ID).ToListAsync();
			var pick = optionIds.Where(valid.Contains).Distinct().ToList();
			if (!poll.Multi && pick.Count > 1) pick = pick.Take(1).ToList();
			var mine = await _db.Set<ChatPollVote>().Where(v => v.PollId == poll.ID && v.EmployeeId == meId).ToListAsync();
			_db.RemoveRange(mine);
			foreach (var o in pick) _db.Add(new ChatPollVote { PollId = poll.ID, OptionId = o, EmployeeId = meId, At = DateTime.UtcNow });
			await _db.SaveChangesAsync();
			await _hub.Clients.Group(ChatHub.ConvGroup(convId)).SendAsync("poll", new { conversationId = convId, id = msgId });
			return true;
		}
		public async Task<bool> ClosePollAsync(int companyId, int meId, int msgId)
		{
			var m = await _db.ChatMessages.AsNoTracking().FirstOrDefaultAsync(x => x.ID == msgId);
			var poll = await _db.Set<ChatPoll>().FirstOrDefaultAsync(p => p.MessageId == msgId && p.CompanyId == companyId);
			if (m == null || poll == null || m.SenderEmployeeId != meId) return false;
			poll.ClosedAt = DateTime.UtcNow; await _db.SaveChangesAsync();
			await _hub.Clients.Group(ChatHub.ConvGroup(m.ConversationId)).SendAsync("poll", new { conversationId = m.ConversationId, id = msgId });
			return true;
		}

		// ------------------------------------------------------------------------------------------
		// SCHEDULED MESSAGES and REMINDERS — kept here, sent by CommWorkerHostedService
		// ------------------------------------------------------------------------------------------
		public async Task<int> ScheduleAsync(int companyId, int convId, int meId, string body, DateTime sendAtUtc)
		{
			body = (body ?? "").Trim();
			if (body.Length == 0 || body.Length > MaxBody || sendAtUtc < DateTime.UtcNow.AddSeconds(30) || sendAtUtc > DateTime.UtcNow.AddDays(365)) return 0;
			if (!await IsMemberAsync(companyId, convId, meId)) return 0;
			var row = new ChatScheduled { CompanyId = companyId, ConversationId = convId, EmployeeId = meId, Body = body, SendAt = sendAtUtc, CreatedAt = DateTime.UtcNow };
			_db.Add(row); await _db.SaveChangesAsync();
			return row.ID;
		}
		public async Task<List<object>> ScheduledAsync(int companyId, int convId, int meId)
			=> (await _db.Set<ChatScheduled>().AsNoTracking().Where(s => s.CompanyId == companyId && s.ConversationId == convId && s.EmployeeId == meId && s.SentAt == null && s.CancelledAt == null)
				.OrderBy(s => s.SendAt).ToListAsync()).Select(s => (object)new { id = s.ID, body = s.Body, sendAt = DateTime.SpecifyKind(s.SendAt, DateTimeKind.Utc) }).ToList();
		public async Task<bool> CancelScheduledAsync(int companyId, int meId, int id)
		{
			var s = await _db.Set<ChatScheduled>().FirstOrDefaultAsync(x => x.ID == id && x.CompanyId == companyId && x.EmployeeId == meId && x.SentAt == null);
			if (s == null) return false; s.CancelledAt = DateTime.UtcNow; await _db.SaveChangesAsync(); return true;
		}
		public async Task<int> SendDueAsync(int companyId, DateTime nowUtc)
		{
			var due = await _db.Set<ChatScheduled>().Where(s => s.CompanyId == companyId && s.SentAt == null && s.CancelledAt == null && s.SendAt <= nowUtc).OrderBy(s => s.SendAt).Take(50).ToListAsync();
			foreach (var s in due)
			{
				var msg = await PostAsync(companyId, s.ConversationId, s.EmployeeId, new PostInput { Body = s.Body });
				s.SentAt = nowUtc; s.SentMessageId = msg?.ID; if (msg == null) s.Failure = "not sent (no longer a member, or the sensitive-data rule)";
				await _db.SaveChangesAsync();
			}
			return due.Count;
		}
		public async Task<int> RemindAsync(int companyId, int meId, int msgId, DateTime remindAtUtc)
		{
			var m = await _db.ChatMessages.AsNoTracking().Where(x => x.ID == msgId).Select(x => new { x.ConversationId }).FirstOrDefaultAsync();
			if (m == null || remindAtUtc < DateTime.UtcNow.AddSeconds(30) || !await IsMemberAsync(companyId, m.ConversationId, meId)) return 0;
			var r = new ChatReminder { CompanyId = companyId, EmployeeId = meId, ConversationId = m.ConversationId, MessageId = msgId, RemindAt = remindAtUtc, CreatedAt = DateTime.UtcNow };
			_db.Add(r); await _db.SaveChangesAsync(); return r.ID;
		}
		public async Task<bool> ReminderDoneAsync(int companyId, int meId, int id)
		{
			var r = await _db.Set<ChatReminder>().FirstOrDefaultAsync(x => x.ID == id && x.CompanyId == companyId && x.EmployeeId == meId);
			if (r == null) return false; r.DoneAt = DateTime.UtcNow; await _db.SaveChangesAsync(); return true;
		}
		public async Task<int> NotifyRemindersAsync(int companyId, DateTime nowUtc)
		{
			var due = await _db.Set<ChatReminder>().Where(r => r.CompanyId == companyId && r.NotifiedAt == null && r.DoneAt == null && r.RemindAt <= nowUtc).Take(100).ToListAsync();
			foreach (var r in due)
			{
				var m = await _db.ChatMessages.AsNoTracking().Where(x => x.ID == r.MessageId).Select(x => new { x.Body, x.AttachmentType, x.AttachmentName, x.ThreadRootId }).FirstOrDefaultAsync();
				var text = m == null ? "" : Preview(m.Body, m.AttachmentType, m.AttachmentName);
				try
				{
					await _notify.NotifyAsync(r.EmployeeId, "تذكير برسالة", "Message reminder", text, text, NotificationTypes.ChatMessage, r.ConversationId,
						url: $"/Chat?c={r.ConversationId}&m={r.MessageId}" + (m?.ThreadRootId is int t ? $"&thread={t}" : ""), companyId: companyId, dedupKey: $"chat-reminder-{r.ID}");
				}
				catch { }
				r.NotifiedAt = nowUtc;
			}
			if (due.Count > 0) await _db.SaveChangesAsync();
			return due.Count;
		}

		// ------------------------------------------------------------------------------------------
		// STATUS and DO-NOT-DISTURB
		// ------------------------------------------------------------------------------------------
		public async Task<object?> StatusOfAsync(int companyId, int empId)
		{
			var s = await _db.Set<ChatStatus>().AsNoTracking().FirstOrDefaultAsync(x => x.EmployeeId == empId && x.CompanyId == companyId);
			return s == null ? null : Shape(s);
		}
		private static object? Shape(ChatStatus s)
		{
			var now = DateTime.UtcNow;
			bool textOn = !string.IsNullOrWhiteSpace(s.Text) && (s.Until == null || s.Until > now);
			bool dnd = s.Dnd && (s.DndUntil == null || s.DndUntil > now);
			if (!textOn && !dnd) return null;
			return new { emoji = textOn ? s.Emoji : null, text = textOn ? s.Text : null, until = textOn && s.Until != null ? DateTime.SpecifyKind(s.Until.Value, DateTimeKind.Utc) : (DateTime?)null, dnd, dndUntil = dnd && s.DndUntil != null ? DateTime.SpecifyKind(s.DndUntil.Value, DateTimeKind.Utc) : (DateTime?)null };
		}
		public async Task<Dictionary<int, object>> StatusesAsync(int companyId, IEnumerable<int> ids)
		{
			var list = ids.Distinct().Take(1000).ToList();
			var rows = await _db.Set<ChatStatus>().AsNoTracking().Where(x => x.CompanyId == companyId && list.Contains(x.EmployeeId)).ToListAsync();
			var map = new Dictionary<int, object>();
			foreach (var r in rows) { var s = Shape(r); if (s != null) map[r.EmployeeId] = s; }
			return map;
		}
		public async Task SetStatusAsync(int companyId, int meId, string? emoji, string? text, DateTime? untilUtc, bool dnd, DateTime? dndUntilUtc)
		{
			var s = await _db.Set<ChatStatus>().FirstOrDefaultAsync(x => x.EmployeeId == meId);
			if (s == null) { s = new ChatStatus { EmployeeId = meId, CompanyId = companyId }; _db.Add(s); }
			s.CompanyId = companyId;
			s.Emoji = string.IsNullOrWhiteSpace(emoji) ? null : emoji!.Trim()[..Math.Min(16, emoji.Trim().Length)];
			s.Text = string.IsNullOrWhiteSpace(text) ? null : text!.Trim()[..Math.Min(100, text.Trim().Length)];
			s.Until = untilUtc; s.Dnd = dnd; s.DndUntil = dnd ? dndUntilUtc : null; s.UpdatedAt = DateTime.UtcNow;
			await _db.SaveChangesAsync();
			await _hub.Clients.All.SendAsync("status", new { employeeId = meId, status = Shape(s) });
		}
		/// Do-not-disturb right now (the calls hub refuses to ring someone in it).
		public static async Task<bool> InDndAsync(CrossDbContext db, int empId)
		{
			var now = DateTime.UtcNow;
			try { return await db.Set<ChatStatus>().AsNoTracking().AnyAsync(x => x.EmployeeId == empId && x.Dnd && (x.DndUntil == null || x.DndUntil > now)); }
			catch (Microsoft.Data.SqlClient.SqlException) { return false; }
		}

		// ------------------------------------------------------------------------------------------
		// CHANNELS (public groups) and a group's description and picture
		// ------------------------------------------------------------------------------------------
		public async Task<int> CreateChannelAsync(int companyId, int meId, string title, string? description)
		{
			title = (title ?? "").Trim().TrimStart('#').Trim();
			if (title.Length == 0 || title.Length > 100) return 0;
			var now = DateTime.UtcNow;
			var c = new Conversation { CompanyID = companyId, Kind = "Group", Title = title, IsPublic = true, Description = string.IsNullOrWhiteSpace(description) ? null : description!.Trim()[..Math.Min(400, description.Trim().Length)], CreatedByEmployeeId = meId, CreatedAt = now, LastMessageAt = now };
			_db.Conversations.Add(c); await _db.SaveChangesAsync();
			_db.ConversationMembers.Add(new ConversationMember { ConversationId = c.ID, EmployeeId = meId, Role = "Owner", JoinedAt = now });
			await _db.SaveChangesAsync();
			return c.ID;
		}
		public async Task<List<object>> BrowseChannelsAsync(int companyId, int meId, string? q)
		{
			var mine = _db.ConversationMembers.AsNoTracking().Where(m => m.EmployeeId == meId).Select(m => m.ConversationId);
			var query = _db.Conversations.AsNoTracking().Where(c => c.CompanyID == companyId && c.IsPublic && c.Kind == "Group");
			if (!string.IsNullOrWhiteSpace(q)) query = query.Where(c => c.Title!.Contains(q) || (c.Description != null && c.Description.Contains(q)));
			var rows = await query.OrderByDescending(c => c.LastMessageAt).Take(100).Select(c => new { c.ID, c.Title, c.Description, c.AvatarPath, joined = mine.Contains(c.ID), members = _db.ConversationMembers.Count(m => m.ConversationId == c.ID) }).ToListAsync();
			return rows.Select(r => (object)new { id = r.ID, title = r.Title, description = r.Description, avatar = r.AvatarPath, r.joined, r.members }).ToList();
		}
		public async Task<bool> JoinChannelAsync(int companyId, int meId, int convId)
		{
			var c = await _db.Conversations.AsNoTracking().FirstOrDefaultAsync(x => x.ID == convId && x.CompanyID == companyId && x.IsPublic);
			if (c == null) return false;
			if (await _db.ConversationMembers.AnyAsync(m => m.ConversationId == convId && m.EmployeeId == meId)) return true;
			_db.ConversationMembers.Add(new ConversationMember { ConversationId = convId, EmployeeId = meId, Role = "Member", JoinedAt = DateTime.UtcNow,
				LastReadMessageId = await _db.ChatMessages.Where(m => m.ConversationId == convId).Select(m => (int?)m.ID).MaxAsync() ?? 0 });
			await _db.SaveChangesAsync();
			await _hub.Clients.Group(ChatHub.ConvGroup(convId)).SendAsync("members", new { conversationId = convId });
			return true;
		}
		public async Task<bool> UpdateGroupAsync(int companyId, int meId, int convId, string? title, string? description, string? avatarPath, bool? isPublic)
		{
			var c = await _db.Conversations.FirstOrDefaultAsync(x => x.ID == convId && x.CompanyID == companyId && x.Kind == "Group");
			var me = await _db.ConversationMembers.AsNoTracking().FirstOrDefaultAsync(m => m.ConversationId == convId && m.EmployeeId == meId);
			if (c == null || me == null || me.Role != "Owner") return false;
			if (!string.IsNullOrWhiteSpace(title)) c.Title = title.Trim()[..Math.Min(100, title.Trim().Length)];
			if (description != null) c.Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim()[..Math.Min(400, description.Trim().Length)];
			if (avatarPath != null) c.AvatarPath = avatarPath.Length == 0 ? null : avatarPath;
			if (isPublic != null) c.IsPublic = isPublic.Value;
			await _db.SaveChangesAsync();
			await _hub.Clients.Group(ChatHub.ConvGroup(convId)).SendAsync("members", new { conversationId = convId });
			return true;
		}
		public async Task<object?> GroupInfoAsync(int companyId, int convId, int meId)
		{
			if (!await IsMemberAsync(companyId, convId, meId)) return null;
			var c = await _db.Conversations.AsNoTracking().Where(x => x.ID == convId).Select(x => new { x.IsPublic, x.Description, x.AvatarPath, x.CreatedAt }).FirstAsync();
			return new { isPublic = c.IsPublic, description = c.Description, avatar = c.AvatarPath, createdAt = DateTime.SpecifyKind(c.CreatedAt, DateTimeKind.Utc) };
		}
		/// Per conversation of mine: public channel, picture (the list draws them).
		public async Task<object> ConvExtrasAsync(int companyId, int meId)
		{
			var mine = _db.ConversationMembers.AsNoTracking().Where(m => m.EmployeeId == meId).Select(m => m.ConversationId);
			return await _db.Conversations.AsNoTracking().Where(c => c.CompanyID == companyId && mine.Contains(c.ID) && (c.IsPublic || c.AvatarPath != null))
				.Select(c => new { id = c.ID, isPublic = c.IsPublic, avatar = c.AvatarPath }).ToListAsync();
		}

		// ------------------------------------------------------------------------------------------
		// SEARCH across every conversation of mine (with "from", "in", "has" filters)
		// ------------------------------------------------------------------------------------------
		public async Task<List<object>> FindAsync(int companyId, int meId, string q, int? convId, int? fromId, string? has, int take = 40)
		{
			q = (q ?? "").Trim();
			if (q.Length < 2 && fromId == null && string.IsNullOrEmpty(has)) return new();
			var mine = _db.ConversationMembers.AsNoTracking().Where(m => m.EmployeeId == meId).Select(m => m.ConversationId);
			var query = _db.ChatMessages.AsNoTracking().Where(m => mine.Contains(m.ConversationId) && m.DeletedAt == null
				&& _db.Conversations.Any(c => c.ID == m.ConversationId && c.CompanyID == companyId));
			if (q.Length >= 2) query = query.Where(m => (m.Body != null && m.Body.Contains(q)) || (m.AttachmentName != null && m.AttachmentName.Contains(q)));
			if (convId is int cid) query = query.Where(m => m.ConversationId == cid);
			if (fromId is int fid) query = query.Where(m => m.SenderEmployeeId == fid);
			if (has == "file") query = query.Where(m => m.AttachmentPath != null && m.AttachmentType == "file");
			if (has == "image") query = query.Where(m => m.AttachmentType == "image");
			if (has == "voice") query = query.Where(m => m.AttachmentType == "audio");
			if (has == "link") query = query.Where(m => m.Body != null && m.Body.Contains("http"));
			var rows = await query.OrderByDescending(m => m.ID).Take(Math.Clamp(take, 1, 100)).ToListAsync();
			var titles = await ConvTitlesAsync(rows.Select(r => r.ConversationId).Distinct().ToList(), meId);
			var names = await NamesAsync(rows.Select(r => r.SenderEmployeeId));
			return rows.Select(r => (object)new
			{
				id = r.ID, conversationId = r.ConversationId, conversation = titles.GetValueOrDefault(r.ConversationId), threadRootId = r.ThreadRootId,
				sender = names.GetValueOrDefault(r.SenderEmployeeId).name, avatar = names.GetValueOrDefault(r.SenderEmployeeId).avatar,
				body = Preview(r.Body, r.AttachmentType, r.AttachmentName), at = DateTime.SpecifyKind(r.CreatedAt, DateTimeKind.Utc),
			}).ToList();
		}

		// ------------------------------------------------------------------------------------------
		// CATCH UP — what I missed in a conversation, gathered without a language model (the local model is off by
		// decision, crossbuy_ai/LOCAL_MODEL.md): mentions of me, questions, decisions, files, links, calls
		// ------------------------------------------------------------------------------------------
		private static readonly string[] DecisionWords = { "تم ", "تمت", "اتفقنا", "موافق", "تمت الموافقة", "اعتمد", "قررنا", "approved", "agreed", "done", "decided", "confirmed", "go ahead" };
		public async Task<object?> CatchUpAsync(int companyId, int convId, int meId, int sinceId, string? myName, string? myAlt)
		{
			if (!await IsMemberAsync(companyId, convId, meId)) return null;
			var msgs = await _db.ChatMessages.AsNoTracking().Where(m => m.ConversationId == convId && m.ID > sinceId && m.SenderEmployeeId != meId && m.DeletedAt == null)
				.OrderBy(m => m.ID).Take(500).ToListAsync();
			var names = await NamesAsync(msgs.Select(m => m.SenderEmployeeId));
			bool MentionsMe(string? b) => b != null && ((myName != null && b.Contains("@" + myName)) || (myAlt != null && b.Contains("@" + myAlt)));
			object Row(ChatMessage m) => new { id = m.ID, sender = names.GetValueOrDefault(m.SenderEmployeeId).name, body = Preview(m.Body, m.AttachmentType, m.AttachmentName), threadRootId = m.ThreadRootId };
			var people = msgs.GroupBy(m => m.SenderEmployeeId).Select(g => new { name = names.GetValueOrDefault(g.Key).name, count = g.Count() }).OrderByDescending(x => x.count).ToList();
			return new
			{
				total = msgs.Count, people,
				mentions = msgs.Where(m => MentionsMe(m.Body)).Select(Row).Take(10),
				questions = msgs.Where(m => m.Body != null && (m.Body.Contains('?') || m.Body.Contains('؟'))).Select(Row).Take(8),
				decisions = msgs.Where(m => m.Body != null && DecisionWords.Any(w => m.Body.Contains(w, StringComparison.OrdinalIgnoreCase))).Select(Row).Take(8),
				files = msgs.Where(m => m.AttachmentPath != null).Select(Row).Take(8),
				links = msgs.Where(m => m.Body != null && m.Body.Contains("http")).Select(Row).Take(5),
				polls = msgs.Where(m => m.AttachmentType == "poll").Select(Row).Take(5),
			};
		}

		// ------------------------------------------------------------------------------------------
		// GOVERNANCE — settings, legal holds, retention, compliance export, usage
		// ------------------------------------------------------------------------------------------
		public async Task<ChatSettings> SettingsAsync(int companyId)
		{
			try { return await _db.Set<ChatSettings>().AsNoTracking().FirstOrDefaultAsync(s => s.CompanyId == companyId) ?? new ChatSettings { CompanyId = companyId }; }
			catch (Microsoft.Data.SqlClient.SqlException) { return new ChatSettings { CompanyId = companyId }; }
		}
		public async Task SaveSettingsAsync(int companyId, int byId, int retentionDays, string dlpMode, bool cards, bool iban, bool nationalId, string? words)
		{
			var s = await _db.Set<ChatSettings>().FirstOrDefaultAsync(x => x.CompanyId == companyId);
			if (s == null) { s = new ChatSettings { CompanyId = companyId }; _db.Add(s); }
			s.RetentionDays = Math.Clamp(retentionDays, 0, 3650); s.DlpMode = dlpMode is "warn" or "block" ? dlpMode : "off";
			s.DlpCards = cards; s.DlpIban = iban; s.DlpNationalId = nationalId; s.DlpWords = string.IsNullOrWhiteSpace(words) ? null : words.Trim()[..Math.Min(1000, words.Trim().Length)];
			s.UpdatedAt = DateTime.UtcNow; s.UpdatedByEmployeeId = byId;
			await _db.SaveChangesAsync();
		}
		public async Task<List<object>> HoldsAsync(int companyId)
		{
			var rows = await _db.Set<ChatLegalHold>().AsNoTracking().Where(h => h.CompanyId == companyId && h.ReleasedAt == null).OrderByDescending(h => h.CreatedAt).ToListAsync();
			var names = await NamesAsync(rows.Where(r => r.EmployeeId != null).Select(r => r.EmployeeId!.Value).Concat(rows.Select(r => r.CreatedByEmployeeId)));
			var titles = await _db.Conversations.AsNoTracking().Where(c => rows.Select(r => r.ConversationId).Contains(c.ID)).ToDictionaryAsync(c => c.ID, c => c.Title ?? ("#" + c.ID));
			return rows.Select(r => (object)new { id = r.ID, employee = r.EmployeeId is int e ? names.GetValueOrDefault(e).name : null, conversation = r.ConversationId is int c ? titles.GetValueOrDefault(c) : null, reason = r.Reason, by = names.GetValueOrDefault(r.CreatedByEmployeeId).name, at = DateTime.SpecifyKind(r.CreatedAt, DateTimeKind.Utc) }).ToList();
		}
		public async Task<int> AddHoldAsync(int companyId, int byId, int? employeeId, int? conversationId, string reason)
		{
			if ((employeeId == null) == (conversationId == null) || string.IsNullOrWhiteSpace(reason)) return 0;
			if (employeeId is int e && !await _db.Employee.AsNoTracking().AnyAsync(x => x.ID == e && x.EmpCompanyID == companyId)) return 0;
			if (conversationId is int c && !await _db.Conversations.AsNoTracking().AnyAsync(x => x.ID == c && x.CompanyID == companyId)) return 0;
			var h = new ChatLegalHold { CompanyId = companyId, EmployeeId = employeeId, ConversationId = conversationId, Reason = reason.Trim()[..Math.Min(300, reason.Trim().Length)], CreatedByEmployeeId = byId, CreatedAt = DateTime.UtcNow };
			_db.Add(h); await _db.SaveChangesAsync(); return h.ID;
		}
		public async Task<bool> ReleaseHoldAsync(int companyId, int byId, int id)
		{
			var h = await _db.Set<ChatLegalHold>().FirstOrDefaultAsync(x => x.ID == id && x.CompanyId == companyId && x.ReleasedAt == null);
			if (h == null) return false; h.ReleasedAt = DateTime.UtcNow; h.ReleasedByEmployeeId = byId; await _db.SaveChangesAsync(); return true;
		}
		/// Retention: a message older than the company's days loses its content (soft delete, as a deletion by its sender does),
		/// unless a legal hold covers its conversation or its sender. 0 days = kept. Never touches a conversation's structure.
		public async Task<int> ApplyRetentionAsync(int companyId, DateTime nowUtc)
		{
			var s = await SettingsAsync(companyId);
			if (s.RetentionDays <= 0) return 0;
			var cutoff = nowUtc.AddDays(-s.RetentionDays);
			var heldConvs = _db.Set<ChatLegalHold>().Where(h => h.CompanyId == companyId && h.ReleasedAt == null && h.ConversationId != null).Select(h => h.ConversationId!.Value);
			var heldPeople = _db.Set<ChatLegalHold>().Where(h => h.CompanyId == companyId && h.ReleasedAt == null && h.EmployeeId != null).Select(h => h.EmployeeId!.Value);
			var rows = await _db.ChatMessages.Where(m => m.CreatedAt < cutoff && m.DeletedAt == null
				&& _db.Conversations.Any(c => c.ID == m.ConversationId && c.CompanyID == companyId)
				&& !heldConvs.Contains(m.ConversationId) && !heldPeople.Contains(m.SenderEmployeeId)).OrderBy(m => m.ID).Take(2000).ToListAsync();
			foreach (var m in rows) { m.DeletedAt = nowUtc; m.Body = null; m.AttachmentPath = null; }
			if (rows.Count > 0) await _db.SaveChangesAsync();
			return rows.Count;
		}
		/// Compliance export: every message of the chosen people or conversation in the period, as CSV; the export itself is recorded.
		public async Task<(byte[] csv, int rows)> ExportAsync(int companyId, int byId, int? employeeId, int? conversationId, DateTime? fromUtc, DateTime? toUtc)
		{
			var q = _db.ChatMessages.AsNoTracking().Where(m => _db.Conversations.Any(c => c.ID == m.ConversationId && c.CompanyID == companyId));
			if (employeeId is int e) q = q.Where(m => m.SenderEmployeeId == e || _db.ConversationMembers.Any(x => x.ConversationId == m.ConversationId && x.EmployeeId == e));
			if (conversationId is int c) q = q.Where(m => m.ConversationId == c);
			if (fromUtc != null) q = q.Where(m => m.CreatedAt >= fromUtc);
			if (toUtc != null) q = q.Where(m => m.CreatedAt < toUtc);
			var rows = await q.OrderBy(m => m.ID).Take(200000).ToListAsync();
			var names = await NamesAsync(rows.Select(r => r.SenderEmployeeId));
			var titles = await _db.Conversations.AsNoTracking().Where(x => rows.Select(r => r.ConversationId).Distinct().Contains(x.ID)).ToDictionaryAsync(x => x.ID, x => x.Title ?? (x.Kind + " #" + x.ID));
			static string Q(string? v) => "\"" + (v ?? "").Replace("\"", "\"\"") + "\"";
			var sb = new StringBuilder();
			sb.AppendLine("MessageId,ConversationId,Conversation,ThreadRootId,SenderId,Sender,SentUtc,EditedUtc,DeletedUtc,Body,Attachment");
			foreach (var r in rows)
				sb.Append(r.ID).Append(',').Append(r.ConversationId).Append(',').Append(Q(titles.GetValueOrDefault(r.ConversationId))).Append(',').Append(r.ThreadRootId).Append(',')
				  .Append(r.SenderEmployeeId).Append(',').Append(Q(names.GetValueOrDefault(r.SenderEmployeeId).name)).Append(',').Append(r.CreatedAt.ToString("u")).Append(',')
				  .Append(r.EditedAt?.ToString("u")).Append(',').Append(r.DeletedAt?.ToString("u")).Append(',').Append(Q(r.Body)).Append(',').Append(Q(r.AttachmentName)).AppendLine();
			_db.Add(new ChatExport { CompanyId = companyId, ByEmployeeId = byId, Scope = (employeeId != null ? "employee:" + employeeId : "") + (conversationId != null ? " conversation:" + conversationId : "") , FromUtc = fromUtc, ToUtc = toUtc, Rows = rows.Count, At = DateTime.UtcNow });
			await _db.SaveChangesAsync();
			return (new UTF8Encoding(true).GetPreamble().Concat(Encoding.UTF8.GetBytes(sb.ToString())).ToArray(), rows.Count);
		}
		public async Task<List<object>> ExportsAsync(int companyId)
		{
			var rows = await _db.Set<ChatExport>().AsNoTracking().Where(x => x.CompanyId == companyId).OrderByDescending(x => x.At).Take(20).ToListAsync();
			var names = await NamesAsync(rows.Select(r => r.ByEmployeeId));
			return rows.Select(r => (object)new { by = names.GetValueOrDefault(r.ByEmployeeId).name, scope = r.Scope, rows = r.Rows, at = DateTime.SpecifyKind(r.At, DateTimeKind.Utc) }).ToList();
		}
		/// Usage over the last 30 days: messages a day, active people, busiest conversations, calls.
		public async Task<object> UsageAsync(int companyId)
		{
			var since = DateTime.UtcNow.Date.AddDays(-29);
			var inCo = _db.Conversations.AsNoTracking().Where(c => c.CompanyID == companyId).Select(c => c.ID);
			var msgs = _db.ChatMessages.AsNoTracking().Where(m => m.CreatedAt >= since && inCo.Contains(m.ConversationId));
			var perDay = await msgs.GroupBy(m => m.CreatedAt.Date).Select(g => new { day = g.Key, n = g.Count() }).ToListAsync();
			int active = await msgs.Select(m => m.SenderEmployeeId).Distinct().CountAsync();
			int total = await msgs.CountAsync();
			var busy = await msgs.GroupBy(m => m.ConversationId).Select(g => new { id = g.Key, n = g.Count() }).OrderByDescending(x => x.n).Take(5).ToListAsync();
			var titles = await _db.Conversations.AsNoTracking().Where(c => busy.Select(b => b.id).Contains(c.ID)).ToDictionaryAsync(c => c.ID, c => c.Kind == "Direct" ? (IsAr ? "محادثة ثنائية" : "Direct chat") : (c.Title ?? "-"));
			int calls = 0, callMinutes = 0;
			try
			{
				var cs = await _db.Set<CrossBuy.Models.Context.Hospital.HospitalCall>().AsNoTracking().Where(c => c.CompanyId == companyId && c.StartedAt >= since).Select(c => c.DurationSeconds).ToListAsync();
				calls = cs.Count; callMinutes = cs.Sum(d => d ?? 0) / 60;
			}
			catch (Microsoft.Data.SqlClient.SqlException) { }
			return new
			{
				total, active, calls, callMinutes,
				days = Enumerable.Range(0, 30).Select(i => since.AddDays(i)).Select(d => new { day = d.ToString("yyyy-MM-dd"), n = perDay.FirstOrDefault(p => p.day == d)?.n ?? 0 }),
				busiest = busy.Select(b => new { title = titles.GetValueOrDefault(b.id), b.n }),
			};
		}
	}

	// ==========================================================================================
	// THE SENSITIVE-DATA RULE (DLP) — what a message may not carry: a payment card number (Luhn-valid), an IBAN, a
	// national id (Egypt 14 digits, Saudi Arabia 10, Kuwait 12), or a word the company listed. "warn" asks the sender
	// first (the page); "block" refuses it here, on the server, whatever the browser did.
	// ==========================================================================================
	public static class ChatDlp
	{
		private static readonly Regex Card = new(@"(?<!\d)(?:\d[ -]?){13,19}(?!\d)", RegexOptions.Compiled);
		private static readonly Regex Iban = new(@"\b[A-Z]{2}\d{2}(?:[ ]?[A-Z0-9]){11,30}\b", RegexOptions.Compiled);
		private static readonly Regex NationalId = new(@"(?<!\d)(?:[23]\d{13}|[12]\d{9}|[23]\d{11})(?!\d)", RegexOptions.Compiled);

		public static List<string> Find(ChatSettings s, string text)
		{
			var hits = new List<string>();
			if (string.IsNullOrEmpty(text) || s.DlpMode == "off") return hits;
			var latin = ToLatinDigits(text);
			if (s.DlpCards && Card.Matches(latin).Any(m => Luhn(new string(m.Value.Where(char.IsDigit).ToArray())))) hits.Add("card");
			if (s.DlpIban && Iban.IsMatch(latin.ToUpperInvariant())) hits.Add("iban");
			if (s.DlpNationalId && NationalId.IsMatch(latin.Replace(" ", ""))) hits.Add("national-id");
			foreach (var w in (s.DlpWords ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
				if (w.Length > 1 && text.Contains(w, StringComparison.OrdinalIgnoreCase)) { hits.Add("word:" + w); break; }
			return hits;
		}
		public static async Task<bool> BlocksAsync(CrossDbContext db, int companyId, string text)
		{
			ChatSettings? s;
			try { s = await db.Set<ChatSettings>().AsNoTracking().FirstOrDefaultAsync(x => x.CompanyId == companyId); }
			catch (Microsoft.Data.SqlClient.SqlException) { return false; }
			return s != null && s.DlpMode == "block" && Find(s, text).Count > 0;
		}
		private static string ToLatinDigits(string s)
		{
			var a = s.ToCharArray();
			for (int i = 0; i < a.Length; i++)
			{
				if (a[i] >= '٠' && a[i] <= '٩') a[i] = (char)('0' + (a[i] - '٠'));
				else if (a[i] >= '۰' && a[i] <= '۹') a[i] = (char)('0' + (a[i] - '۰'));
			}
			return new string(a);
		}
		private static bool Luhn(string digits)
		{
			if (digits.Length < 13 || digits.Length > 19) return false;
			int sum = 0; bool dbl = false;
			for (int i = digits.Length - 1; i >= 0; i--)
			{
				int d = digits[i] - '0';
				if (dbl) { d *= 2; if (d > 9) d -= 9; }
				sum += d; dbl = !dbl;
			}
			return sum % 10 == 0;
		}
	}
}
