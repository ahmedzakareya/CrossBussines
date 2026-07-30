using CrossBuy.Hubs;
using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Chat;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL
{
    // ===== DTOs consumed by the chat UI =====
    public class ChatConvListItem
    {
        public int Id { get; set; }
        public string Kind { get; set; } = "Direct";
        public string Title { get; set; } = "";
        public string? Avatar { get; set; }
        public bool Online { get; set; }
        public string? Preview { get; set; }
        public DateTime? LastAt { get; set; }
        public int Unread { get; set; }
        public int? OtherEmployeeId { get; set; }
    }
    public class ChatMsgDto
    {
        public int Id { get; set; }
        public int SenderId { get; set; }
        public string SenderName { get; set; } = "";
        public string? SenderAvatar { get; set; }
        public string? Body { get; set; }
        public string? AttachmentPath { get; set; }
        public string? AttachmentName { get; set; }
        public string? AttachmentType { get; set; }
        public bool Mine { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime? EditedAt { get; set; }
        public bool Deleted { get; set; }
        public int? ReplyToId { get; set; }
        public List<ChatReactionDto> Reactions { get; set; } = new();
    }
    public class ChatReactionDto { public string Emoji { get; set; } = ""; public int Count { get; set; } public bool Mine { get; set; } }
    public class ChatMemberRead { public int EmployeeId { get; set; } public int LastReadMessageId { get; set; } }
    public class ChatHeaderDto { public int Id { get; set; } public string Kind { get; set; } = ""; public string Title { get; set; } = ""; public string? Avatar { get; set; } public bool Online { get; set; } public int MemberCount { get; set; } public List<int> MemberIds { get; set; } = new(); public int OtherReadMessageId { get; set; } public List<ChatMemberRead> MemberReads { get; set; } = new(); public List<ChatDirectoryDto> Members { get; set; } = new(); public bool IsOwner { get; set; } }
    public class ChatDirectoryDto { public int Id { get; set; } public string Name { get; set; } = ""; public string? Avatar { get; set; } public bool Online { get; set; } }

    public interface IChatService
    {
        Task<int> GetOrCreateDirectAsync(int companyId, int meId, int otherId);
        Task<int> CreateGroupAsync(int companyId, int meId, string title, IEnumerable<int> memberIds);
        Task<List<ChatConvListItem>> MyConversationsAsync(int companyId, int meId);
        Task<ChatHeaderDto?> GetHeaderAsync(int companyId, int meId, int conversationId);
        Task<List<ChatMsgDto>> GetMessagesAsync(int meId, int conversationId, int? beforeId, int take = 30);
        Task<ChatMsgDto?> SendAsync(int companyId, int meId, int conversationId, string? body, string? attPath, string? attName, string? attType, int? replyToId, IEnumerable<int>? mentionedIds);
        Task<bool> EditAsync(int meId, int messageId, string body);
        Task<bool> DeleteAsync(int meId, int messageId);
        Task ToggleReactionAsync(int meId, int messageId, string emoji);
        Task MarkReadAsync(int meId, int conversationId);
        Task<List<ChatDirectoryDto>> DirectoryAsync(int companyId, int meId, string? q);
        Task<bool> AddMemberAsync(int companyId, int meId, int conversationId, int newMemberId);
        Task<bool> RemoveMemberAsync(int companyId, int meId, int conversationId, int memberId);
    }

    public class ChatService : IChatService
    {
        private readonly CrossDbContext _db;
        private readonly IHubContext<ChatHub> _hub;
        private readonly INotificationService _notify;
        public ChatService(CrossDbContext db, IHubContext<ChatHub> hub, INotificationService notify) { _db = db; _hub = hub; _notify = notify; }

        private static bool IsAr => System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ar";
        private static string Nm(string? ar, string? en) => (IsAr ? (ar ?? en) : (en ?? ar)) ?? "-";

        private async Task<Dictionary<int, (string name, string? avatar)>> NamesAsync(IEnumerable<int> ids)
        {
            var list = ids.Distinct().ToList();
            if (list.Count == 0) return new();
            var emps = await _db.Employee.AsNoTracking().Where(e => list.Contains(e.ID))
                .Select(e => new { e.ID, e.FullName, e.FullNameEn, e.ProfileImage }).ToListAsync();
            return emps.ToDictionary(e => e.ID, e => (Nm(e.FullName, e.FullNameEn), string.IsNullOrEmpty(e.ProfileImage) ? (string?)null : e.ProfileImage));
        }

        public async Task<int> GetOrCreateDirectAsync(int companyId, int meId, int otherId)
        {
            // a Direct conversation whose members are exactly {me, other}
            var myDirectIds = await (from m in _db.ConversationMembers.AsNoTracking()
                                     join c in _db.Conversations.AsNoTracking() on m.ConversationId equals c.ID
                                     where c.CompanyID == companyId && c.Kind == "Direct" && m.EmployeeId == meId
                                     select m.ConversationId).ToListAsync();
            if (myDirectIds.Count > 0)
            {
                var existing = await _db.ConversationMembers.AsNoTracking()
                    .Where(m => myDirectIds.Contains(m.ConversationId) && m.EmployeeId == otherId)
                    .Select(m => m.ConversationId).FirstOrDefaultAsync();
                if (existing != 0) return existing;
            }
            var now = DateTime.UtcNow;
            var conv = new Conversation { CompanyID = companyId, Kind = "Direct", CreatedByEmployeeId = meId, CreatedAt = now };
            _db.Conversations.Add(conv);
            await _db.SaveChangesAsync();
            _db.ConversationMembers.AddRange(
                new ConversationMember { ConversationId = conv.ID, EmployeeId = meId, Role = "Member", JoinedAt = now },
                new ConversationMember { ConversationId = conv.ID, EmployeeId = otherId, Role = "Member", JoinedAt = now });
            await _db.SaveChangesAsync();
            return conv.ID;
        }

        public async Task<int> CreateGroupAsync(int companyId, int meId, string title, IEnumerable<int> memberIds)
        {
            var now = DateTime.UtcNow;
            var conv = new Conversation { CompanyID = companyId, Kind = "Group", Title = title, CreatedByEmployeeId = meId, CreatedAt = now, LastMessageAt = now };
            _db.Conversations.Add(conv);
            await _db.SaveChangesAsync();
            var ids = memberIds.Where(x => x != meId).Distinct().ToList();
            _db.ConversationMembers.Add(new ConversationMember { ConversationId = conv.ID, EmployeeId = meId, Role = "Owner", JoinedAt = now });
            foreach (var id in ids)
                _db.ConversationMembers.Add(new ConversationMember { ConversationId = conv.ID, EmployeeId = id, Role = "Member", JoinedAt = now });
            await _db.SaveChangesAsync();
            return conv.ID;
        }

        public async Task<List<ChatConvListItem>> MyConversationsAsync(int companyId, int meId)
        {
            var myMems = await _db.ConversationMembers.AsNoTracking().Where(m => m.EmployeeId == meId).ToListAsync();
            var convIds = myMems.Select(m => m.ConversationId).ToList();
            if (convIds.Count == 0) return new();
            var convs = await _db.Conversations.AsNoTracking().Where(c => convIds.Contains(c.ID) && c.CompanyID == companyId).ToListAsync();
            var otherMems = await _db.ConversationMembers.AsNoTracking().Where(m => convIds.Contains(m.ConversationId)).ToListAsync();
            // unread per conversation = messages after my high-water mark, not mine, not deleted
            var lastRead = myMems.ToDictionary(m => m.ConversationId, m => m.LastReadMessageId);
            var unreadRaw = await _db.ChatMessages.AsNoTracking()
                .Where(x => convIds.Contains(x.ConversationId) && x.SenderEmployeeId != meId && x.DeletedAt == null)
                .Select(x => new { x.ConversationId, x.ID }).ToListAsync();

            var otherIds = otherMems.Where(m => m.EmployeeId != meId).Select(m => m.EmployeeId).Distinct().ToList();
            var names = await NamesAsync(otherIds);

            var list = new List<ChatConvListItem>();
            foreach (var c in convs)
            {
                var hwm = lastRead.TryGetValue(c.ID, out var lr) ? lr : 0;
                var unread = unreadRaw.Count(u => u.ConversationId == c.ID && u.ID > hwm);
                string title; string? avatar = null; bool online = false; int? otherId = null;
                if (c.Kind == "Direct")
                {
                    otherId = otherMems.Where(m => m.ConversationId == c.ID && m.EmployeeId != meId).Select(m => m.EmployeeId).FirstOrDefault();
                    if (otherId != 0 && names.TryGetValue(otherId.Value, out var info)) { title = info.name; avatar = info.avatar; } else title = "-";
                    online = otherId.HasValue && otherId != 0 && ChatHub.IsOnline(otherId.Value);
                }
                else { title = c.Title ?? "-"; }
                list.Add(new ChatConvListItem { Id = c.ID, Kind = c.Kind, Title = title, Avatar = avatar, Online = online, Preview = c.LastMessagePreview, LastAt = c.LastMessageAt ?? c.CreatedAt, Unread = unread, OtherEmployeeId = (otherId == 0 ? null : otherId) });
            }
            return list.OrderByDescending(x => x.LastAt).ToList();
        }

        private async Task<bool> IsMemberAsync(int conversationId, int meId) =>
            await _db.ConversationMembers.AsNoTracking().AnyAsync(m => m.ConversationId == conversationId && m.EmployeeId == meId);

        public async Task<ChatHeaderDto?> GetHeaderAsync(int companyId, int meId, int conversationId)
        {
            var c = await _db.Conversations.AsNoTracking().FirstOrDefaultAsync(x => x.ID == conversationId && x.CompanyID == companyId);
            if (c == null || !await IsMemberAsync(conversationId, meId)) return null;
            var members = await _db.ConversationMembers.AsNoTracking().Where(m => m.ConversationId == conversationId).Select(m => m.EmployeeId).ToListAsync();
            var h = new ChatHeaderDto { Id = c.ID, Kind = c.Kind, MemberCount = members.Count, MemberIds = members };
            if (c.Kind == "Direct")
            {
                var otherId = members.FirstOrDefault(x => x != meId);
                var names = await NamesAsync(new[] { otherId });
                if (names.TryGetValue(otherId, out var info)) { h.Title = info.name; h.Avatar = info.avatar; }
                h.Online = otherId != 0 && ChatHub.IsOnline(otherId);
                // how far the other party has read → drives the "Seen/Sent" indicator on my messages
                h.OtherReadMessageId = otherId == 0 ? 0 : await _db.ConversationMembers.AsNoTracking()
                    .Where(m => m.ConversationId == conversationId && m.EmployeeId == otherId)
                    .Select(m => m.LastReadMessageId).FirstOrDefaultAsync();
            }
            else { h.Title = c.Title ?? "-"; }
            // read high-water per OTHER member → drives Seen/Sent (Direct) and "read by N" (Group)
            h.MemberReads = await _db.ConversationMembers.AsNoTracking()
                .Where(m => m.ConversationId == conversationId && m.EmployeeId != meId)
                .Select(m => new ChatMemberRead { EmployeeId = m.EmployeeId, LastReadMessageId = m.LastReadMessageId }).ToListAsync();
            h.IsOwner = await _db.ConversationMembers.AsNoTracking().AnyAsync(m => m.ConversationId == conversationId && m.EmployeeId == meId && m.Role == "Owner");
            if (c.Kind == "Group")
            {
                var names = await NamesAsync(members);   // members = all member ids
                h.Members = members.Where(id => id != meId).Select(id => new ChatDirectoryDto
                { Id = id, Name = names.TryGetValue(id, out var info) ? info.name : "-", Avatar = names.TryGetValue(id, out var i2) ? i2.avatar : null, Online = ChatHub.IsOnline(id) }).ToList();
            }
            return h;
        }

        public async Task<List<ChatMsgDto>> GetMessagesAsync(int meId, int conversationId, int? beforeId, int take = 30)
        {
            if (!await IsMemberAsync(conversationId, meId)) return new();
            var q = _db.ChatMessages.AsNoTracking().Where(m => m.ConversationId == conversationId);
            if (beforeId.HasValue) q = q.Where(m => m.ID < beforeId.Value);
            var msgs = await q.OrderByDescending(m => m.ID).Take(take).ToListAsync();
            msgs.Reverse();
            var ids = msgs.Select(m => m.ID).ToList();
            var reactions = await _db.ChatReactions.AsNoTracking().Where(r => ids.Contains(r.MessageId)).ToListAsync();
            var names = await NamesAsync(msgs.Select(m => m.SenderEmployeeId));
            return msgs.Select(m => ToDto(m, meId, names, reactions)).ToList();
        }

        private static ChatMsgDto ToDto(ChatMessage m, int meId, Dictionary<int, (string name, string? avatar)> names, List<ChatReaction> reactions)
        {
            names.TryGetValue(m.SenderEmployeeId, out var info);
            var rx = reactions.Where(r => r.MessageId == m.ID).GroupBy(r => r.Emoji)
                .Select(g => new ChatReactionDto { Emoji = g.Key, Count = g.Count(), Mine = g.Any(x => x.EmployeeId == meId) }).ToList();
            return new ChatMsgDto
            {
                Id = m.ID, SenderId = m.SenderEmployeeId, SenderName = info.name ?? "-", SenderAvatar = info.avatar,
                Body = m.DeletedAt != null ? null : m.Body,
                AttachmentPath = m.DeletedAt != null ? null : m.AttachmentPath, AttachmentName = m.AttachmentName, AttachmentType = m.AttachmentType,
                Mine = m.SenderEmployeeId == meId, CreatedAt = m.CreatedAt, EditedAt = m.EditedAt, Deleted = m.DeletedAt != null,
                ReplyToId = m.ReplyToId, Reactions = rx,
            };
        }

        public async Task<ChatMsgDto?> SendAsync(int companyId, int meId, int conversationId, string? body, string? attPath, string? attName, string? attType, int? replyToId, IEnumerable<int>? mentionedIds)
        {
            if (!await IsMemberAsync(conversationId, meId)) return null;
            if (string.IsNullOrWhiteSpace(body) && string.IsNullOrEmpty(attPath)) return null;
            var now = DateTime.UtcNow;
            var msg = new ChatMessage
            {
                ConversationId = conversationId, SenderEmployeeId = meId, Body = string.IsNullOrWhiteSpace(body) ? null : body.Trim(),
                AttachmentPath = attPath, AttachmentName = attName, AttachmentType = attType, ReplyToId = replyToId, CreatedAt = now,
            };
            _db.ChatMessages.Add(msg);
            await _db.SaveChangesAsync();

            var preview = !string.IsNullOrEmpty(msg.Body) ? (msg.Body!.Length > 120 ? msg.Body!.Substring(0, 120) : msg.Body!)
                        : (attType == "image" ? "📷" : "📎 " + (attName ?? ""));
            var conv = await _db.Conversations.FirstAsync(c => c.ID == conversationId);
            conv.LastMessageAt = now; conv.LastMessagePreview = preview;
            // sender has read up to their own message
            var myMem = await _db.ConversationMembers.FirstOrDefaultAsync(m => m.ConversationId == conversationId && m.EmployeeId == meId);
            if (myMem != null) myMem.LastReadMessageId = msg.ID;
            await _db.SaveChangesAsync();

            var names = await NamesAsync(new[] { meId });
            var dto = ToDto(msg, meId, names, new List<ChatReaction>());

            // realtime push to the conversation room (mark not-mine for other viewers is done client-side by senderId compare)
            await _hub.Clients.Group(ChatHub.ConvGroup(conversationId)).SendAsync("message", new
            {
                conversationId, id = dto.Id, senderId = dto.SenderId, senderName = dto.SenderName, senderAvatar = dto.SenderAvatar,
                body = dto.Body, attachmentPath = dto.AttachmentPath, attachmentName = dto.AttachmentName, attachmentType = dto.AttachmentType,
                replyToId = dto.ReplyToId, createdAt = dto.CreatedAt,
            });

            // other members: conversation-list bump + a bell notification (deduped per conversation) + mentions
            var members = await _db.ConversationMembers.AsNoTracking().Where(m => m.ConversationId == conversationId && m.EmployeeId != meId).Select(m => m.EmployeeId).ToListAsync();
            // store the sender name in BOTH languages so each recipient sees it in THEIR culture
            var senderRaw = await _db.Employee.AsNoTracking().Where(e => e.ID == meId).Select(e => new { e.FullName, e.FullNameEn }).FirstOrDefaultAsync();
            var nameAr = senderRaw?.FullName ?? "";
            var nameEn = string.IsNullOrWhiteSpace(senderRaw?.FullNameEn) ? nameAr : senderRaw!.FullNameEn!;
            var mset = (mentionedIds ?? Enumerable.Empty<int>()).ToHashSet();
            var url = $"/Chat?c={conversationId}";
            foreach (var mem in members)
            {
                await _hub.Clients.Group(ChatHub.UserGroup(mem)).SendAsync("conversation", new { conversationId, preview, lastAt = now, from = meId });
                try
                {
                    bool mention = mset.Contains(mem);
                    await _notify.NotifyAsync(mem,
                        nameAr, nameEn, preview, preview,
                        mention ? NotificationTypes.ChatMention : NotificationTypes.ChatMessage,
                        conversationId, url: url, companyId: companyId, actorEmployeeId: meId,
                        dedupKey: (mention ? $"chat-mention-{conversationId}" : $"chat-{conversationId}"));
                }
                catch { /* notifications never block chat */ }
            }
            return dto;
        }

        public async Task<bool> EditAsync(int meId, int messageId, string body)
        {
            var m = await _db.ChatMessages.FirstOrDefaultAsync(x => x.ID == messageId && x.SenderEmployeeId == meId && x.DeletedAt == null);
            if (m == null || string.IsNullOrWhiteSpace(body)) return false;
            m.Body = body.Trim(); m.EditedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync();
            await _hub.Clients.Group(ChatHub.ConvGroup(m.ConversationId)).SendAsync("edited", new { conversationId = m.ConversationId, id = m.ID, body = m.Body, editedAt = m.EditedAt });
            return true;
        }

        public async Task<bool> DeleteAsync(int meId, int messageId)
        {
            var m = await _db.ChatMessages.FirstOrDefaultAsync(x => x.ID == messageId && x.SenderEmployeeId == meId && x.DeletedAt == null);
            if (m == null) return false;
            m.DeletedAt = DateTime.UtcNow; m.Body = null; m.AttachmentPath = null;
            await _db.SaveChangesAsync();
            await _hub.Clients.Group(ChatHub.ConvGroup(m.ConversationId)).SendAsync("deleted", new { conversationId = m.ConversationId, id = m.ID });
            return true;
        }

        public async Task ToggleReactionAsync(int meId, int messageId, string emoji)
        {
            var m = await _db.ChatMessages.AsNoTracking().FirstOrDefaultAsync(x => x.ID == messageId);
            if (m == null) return;
            var existing = await _db.ChatReactions.FirstOrDefaultAsync(r => r.MessageId == messageId && r.EmployeeId == meId && r.Emoji == emoji);
            if (existing != null) _db.ChatReactions.Remove(existing);
            else _db.ChatReactions.Add(new ChatReaction { MessageId = messageId, EmployeeId = meId, Emoji = emoji, CreatedAt = DateTime.UtcNow });
            await _db.SaveChangesAsync();
            var all = await _db.ChatReactions.AsNoTracking().Where(r => r.MessageId == messageId)
                .GroupBy(r => r.Emoji).Select(g => new { emoji = g.Key, count = g.Count() }).ToListAsync();
            await _hub.Clients.Group(ChatHub.ConvGroup(m.ConversationId)).SendAsync("reaction", new { conversationId = m.ConversationId, id = messageId, reactions = all });
        }

        public async Task MarkReadAsync(int meId, int conversationId)
        {
            var mem = await _db.ConversationMembers.FirstOrDefaultAsync(m => m.ConversationId == conversationId && m.EmployeeId == meId);
            if (mem == null) return;
            var maxId = await _db.ChatMessages.Where(x => x.ConversationId == conversationId).Select(x => (int?)x.ID).MaxAsync() ?? 0;
            if (maxId > mem.LastReadMessageId) { mem.LastReadMessageId = maxId; }
            // clear the bell notifications tied to this conversation
            var notifs = await _db.Notifications.Where(n => n.RecipientEmployeeID == meId && !n.IsRead && n.RefId == conversationId
                && (n.Type == NotificationTypes.ChatMessage || n.Type == NotificationTypes.ChatMention)).ToListAsync();
            var now = DateTime.UtcNow;
            foreach (var n in notifs) { n.IsRead = true; n.ReadAt = now; }
            await _db.SaveChangesAsync();
            // tell the other party(s) in the room how far I've read → drives their "Seen" indicator
            await _hub.Clients.Group(ChatHub.ConvGroup(conversationId)).SendAsync("read", new { conversationId, employeeId = meId, lastReadId = mem.LastReadMessageId });
        }

        public async Task<List<ChatDirectoryDto>> DirectoryAsync(int companyId, int meId, string? q)
        {
            var query = _db.Employee.AsNoTracking().Where(e => e.EmpCompanyID == companyId && e.ID != meId);
            if (!string.IsNullOrWhiteSpace(q)) { var t = q.Trim(); query = query.Where(e => e.FullName.Contains(t) || (e.FullNameEn != null && e.FullNameEn.Contains(t))); }
            var emps = await query.OrderBy(e => e.FullName).Take(50)
                .Select(e => new { e.ID, e.FullName, e.FullNameEn, e.ProfileImage }).ToListAsync();
            return emps.Select(e => new ChatDirectoryDto { Id = e.ID, Name = Nm(e.FullName, e.FullNameEn), Avatar = string.IsNullOrEmpty(e.ProfileImage) ? null : e.ProfileImage, Online = ChatHub.IsOnline(e.ID) }).ToList();
        }

        // bilingual actor name for member add/remove notices
        private async Task<(string ar, string en)> ActorNameAsync(int id)
        {
            var e = await _db.Employee.AsNoTracking().Where(x => x.ID == id).Select(x => new { x.FullName, x.FullNameEn }).FirstOrDefaultAsync();
            var ar = e?.FullName ?? ""; var en = string.IsNullOrWhiteSpace(e?.FullNameEn) ? ar : e!.FullNameEn!;
            return (ar, en);
        }

        public async Task<bool> AddMemberAsync(int companyId, int meId, int conversationId, int newMemberId)
        {
            var conv = await _db.Conversations.FirstOrDefaultAsync(c => c.ID == conversationId && c.CompanyID == companyId && c.Kind == "Group");
            if (conv == null || !await IsMemberAsync(conversationId, meId)) return false;   // only a member can add
            if (await IsMemberAsync(conversationId, newMemberId)) return true;               // already in
            _db.ConversationMembers.Add(new ConversationMember { ConversationId = conversationId, EmployeeId = newMemberId, Role = "Member", JoinedAt = DateTime.UtcNow });
            await _db.SaveChangesAsync();
            await _hub.Clients.Group(ChatHub.ConvGroup(conversationId)).SendAsync("members", new { conversationId });
            await _hub.Clients.Group(ChatHub.UserGroup(newMemberId)).SendAsync("conversation", new { conversationId, from = meId });
            try
            {
                var (ar, en) = await ActorNameAsync(meId); var t = conv.Title ?? "";
                await _notify.NotifyAsync(newMemberId, t, t, $"أضافك {ar} إلى المجموعة", $"{en} added you to the group",
                    NotificationTypes.ChatAdded, conversationId, url: $"/Chat?c={conversationId}", companyId: companyId, actorEmployeeId: meId);
            }
            catch { }
            return true;
        }

        public async Task<bool> RemoveMemberAsync(int companyId, int meId, int conversationId, int memberId)
        {
            var conv = await _db.Conversations.FirstOrDefaultAsync(c => c.ID == conversationId && c.CompanyID == companyId && c.Kind == "Group");
            if (conv == null) return false;
            var meOwner = await _db.ConversationMembers.AnyAsync(m => m.ConversationId == conversationId && m.EmployeeId == meId && m.Role == "Owner");
            if (!meOwner && memberId != meId) return false;   // owner removes anyone; others may only leave
            var mem = await _db.ConversationMembers.FirstOrDefaultAsync(m => m.ConversationId == conversationId && m.EmployeeId == memberId);
            if (mem == null) return true;
            _db.ConversationMembers.Remove(mem);
            await _db.SaveChangesAsync();
            await _hub.Clients.Group(ChatHub.ConvGroup(conversationId)).SendAsync("members", new { conversationId });
            await _hub.Clients.Group(ChatHub.UserGroup(memberId)).SendAsync("removed", new { conversationId });
            try
            {
                if (memberId != meId)
                {
                    var (ar, en) = await ActorNameAsync(meId); var t = conv.Title ?? "";
                    await _notify.NotifyAsync(memberId, t, t, $"أزالك {ar} من المجموعة", $"{en} removed you from the group",
                        NotificationTypes.ChatRemoved, conversationId, companyId: companyId, actorEmployeeId: meId);
                }
            }
            catch { }
            return true;
        }
    }
}
