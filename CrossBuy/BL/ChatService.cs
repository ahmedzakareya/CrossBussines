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
        public string? ReplyToSender { get; set; }
        public string? ReplyToBody { get; set; }
        public List<ChatReactionDto> Reactions { get; set; } = new();
    }
    public class ChatReactionDto { public string Emoji { get; set; } = ""; public int Count { get; set; } public bool Mine { get; set; } }
    public class ChatMemberRead { public int EmployeeId { get; set; } public int LastReadMessageId { get; set; } }
    public class ChatHeaderDto { public int Id { get; set; } public string Kind { get; set; } = ""; public string Title { get; set; } = ""; public string? Avatar { get; set; } public bool Online { get; set; } public int MemberCount { get; set; } public List<int> MemberIds { get; set; } = new(); public int OtherReadMessageId { get; set; } public List<ChatMemberRead> MemberReads { get; set; } = new(); public List<ChatDirectoryDto> Members { get; set; } = new(); public bool IsOwner { get; set; } }
    public class ChatDirectoryDto { public int Id { get; set; } public string Name { get; set; } = ""; public string? Avatar { get; set; } public bool Online { get; set; } }

    // ===== Attachment policy (F8-B) =====
    // A conservative allow-list for ordinary business files and images. Everything not named here is
    // refused, which is the safe default direction: a missing office format is an inconvenience, whereas a
    // missing entry in a block-list is a vulnerability.
    //
    // ACTIVE SAME-ORIGIN CONTENT IS ABSENT ON PURPOSE. .html, .htm, .svg, .xhtml, .js and friends are not
    // listed. Chat attachments are served from /uploads/chat under the application's own origin, so an
    // authenticated colleague who opened one would run its script with their session — stored XSS between
    // employees. PrivateFileGate already stops anonymous access; it does not make active content inert.
    public static class ChatAttachments
    {
        private static readonly HashSet<string> Images = new(StringComparer.OrdinalIgnoreCase)
        { ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp", ".avif", ".jfif", ".tif", ".tiff" };

        private static readonly HashSet<string> Documents = new(StringComparer.OrdinalIgnoreCase)
        { ".pdf", ".txt", ".csv", ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx", ".zip", ".rtf", ".odt", ".ods" };

        // Declared content types that must never be accepted whatever the extension says. The browser's
        // Content-Type is attacker-controlled, so it is used only to REFUSE, never to permit — a mismatch
        // means one of the two is lying and the upload is not worth the risk either way.
        private static readonly string[] ActiveTypes =
        { "text/html", "application/xhtml", "image/svg", "application/javascript", "text/javascript",
          "application/x-httpd", "application/xml", "text/xml" };

        public static bool IsImage(string? ext) => ext != null && Images.Contains(ext);

        // NAMED FOR WHAT IT IS. This was called IsAllowed, and CBA003 correctly flagged that: an
        // "IsAllowed" predicate inside a mutating endpoint reads as an authorization decision, and this is
        // not one — it is a file-type policy. The authorization for that endpoint is CanSendToAsync, called
        // immediately before it. Declaring this type as an authority would have made the analyzer quiet by
        // recording something untrue, so the misleading name was fixed instead.
        public static bool IsAcceptedType(string? ext, string? declaredContentType)
        {
            if (string.IsNullOrWhiteSpace(ext)) return false;                     // no extension: refuse
            if (!Images.Contains(ext) && !Documents.Contains(ext)) return false;   // not on the list: refuse
            if (!string.IsNullOrWhiteSpace(declaredContentType))
            {
                var ct = declaredContentType.Trim().ToLowerInvariant();
                if (ActiveTypes.Any(a => ct.StartsWith(a, StringComparison.Ordinal))) return false;
                // An extension claiming to be an image while the browser declares something else is a
                // mismatch, and a mismatch is refused rather than reconciled.
                if (Images.Contains(ext) && !ct.StartsWith("image/", StringComparison.Ordinal)) return false;
            }
            return true;
        }
    }

    public interface IChatService
    {
        // Pre-authorization probe for the attachment path (F8-A). The controller must know whether the
        // caller may write into this conversation BEFORE it saves a file, and it must ask the same rule
        // SendAsync will apply — otherwise the two could disagree and the file would outlive the refusal.
        Task<bool> CanSendToAsync(int conversationId);

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

        // ---- authorization dependencies (F5) ----------------------------------------------------
        // WHY THESE LIVE IN THE SERVICE AND NOT ONLY IN THE CONTROLLER. Every caller of IChatService got
        // its companyId from whoever called it, and the controller derived that from the employee's own
        // Employee.EmpCompanyID row. That is a fact about a person, not a resolved request scope, and it
        // means the authorization answer depended on the caller passing the right number. Resolving the
        // context HERE makes the guard unbypassable: a future controller, hub or job cannot get it wrong,
        // because it no longer supplies the company at all — the companyId parameters that remain on
        // IChatService are used for filtering only and are never trusted for an access decision.
        private readonly ICommunicationAccessService _access;
        private readonly CrossBuy.BL.Platform.IBusinessContextAccessor _contexts;

        public ChatService(CrossDbContext db, IHubContext<ChatHub> hub, INotificationService notify,
            ICommunicationAccessService access, CrossBuy.BL.Platform.IBusinessContextAccessor contexts)
        { _db = db; _hub = hub; _notify = notify; _access = access; _contexts = contexts; }

        // The resolved request scope, or null when it cannot be established. Null means REFUSE — every
        // guard below treats an unresolved context exactly like a denied permission, so an unauthenticated
        // or unscoped call cannot fall through to "no company filter" and read everything.
        private async Task<CrossBuy.Models.Platform.BusinessContext?> ScopeAsync(CancellationToken ct = default)
        {
            try
            {
                var ctx = await _contexts.TryGetCurrentAsync(ct);
                if (ctx == null || ctx.CompanyId <= 0 || ctx.EmployeeId is not > 0) return null;
                return ctx;
            }
            catch (Exception)
            {
                // FAIL CLOSED. A context resolver that throws is not a licence to proceed unscoped.
                return null;
            }
        }

        // One question, asked of the canonical service, for every conversation-scoped operation.
        // CommunicationAccessService verifies the company on the CONVERSATION ROW (never a passed-in
        // value), requires membership even under bootstrap-open, and answers identically for "absent" and
        // "belongs to another company" so a conversation id cannot be probed. Anything that throws is a
        // refusal, not an exemption.
        private async Task<bool> MayAsync(CrossBuy.Models.Platform.BusinessContext ctx, string action, int conversationId, CancellationToken ct = default)
        {
            try
            {
                return await _access.CanAsync(ctx, action,
                    CrossBuy.Models.Platform.PermissionTarget.ForConversation(conversationId, ctx.CompanyId), ct);
            }
            catch (Exception) { return false; }
        }

        // The authoritative server-side body limit (F6). The client may mirror it with maxlength; this is
        // the copy that decides. 4000 characters is the bounded limit the closure brief specifies.
        public const int MaxMessageLength = 4000;

        // Same resolution, same rule, same action as SendAsync — so an upload cannot be authorized by one
        // standard and the message it belongs to refused by another.
        public async Task<bool> CanSendToAsync(int conversationId)
        {
            var ctx = await ScopeAsync();
            return ctx != null && await MayAsync(ctx, CommunicationActions.Send, conversationId);
        }

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
            // F4 + F5 — ONE DIRECT CONVERSATION PER PAIR PER COMPANY, ENFORCED BY THE DATABASE.
            //
            // The company comes from the resolved scope, never from the companyId argument: a caller that
            // passed another company's id could otherwise create a conversation inside it.
            var ctx = await ScopeAsync();
            if (ctx == null) return 0;
            var company = ctx.CompanyId;

            // Starting a direct conversation is a module-level "may I use chat" question — there is no
            // conversation yet to ask about. Asked without a target so the module rule answers.
            bool maySend;
            try { maySend = await _access.CanAsync(ctx, CommunicationActions.Send, null); }
            catch (Exception) { maySend = false; }
            if (!maySend) return 0;

            // Self-chat is not a direct conversation. The pair key would collapse to (n, n) and the
            // "other participant" the UI renders would be the caller, so it is refused outright.
            if (otherId <= 0 || otherId == ctx.EmployeeId!.Value) return 0;

            // The other party must be a real employee OF THIS COMPANY. Without this an id from another
            // company could be pulled into a conversation row that then looks legitimate.
            bool eligible = await _db.Employee.AsNoTracking()
                .AnyAsync(e => e.ID == otherId && e.EmpCompanyID == company);
            if (!eligible) return 0;

            var (low, high) = Conversation.DirectKey(ctx.EmployeeId!.Value, otherId);

            // Ordered pair, so A -> B and B -> A read the same row. This is now a single indexed lookup
            // instead of the previous two-step membership intersection.
            var found = await _db.Conversations.AsNoTracking()
                .Where(c => c.CompanyID == company && c.Kind == "Direct"
                         && c.DirectKeyLow == low && c.DirectKeyHigh == high)
                .Select(c => c.ID).FirstOrDefaultAsync();
            if (found != 0) return found;

            var now = DateTime.UtcNow;
            var conv = new Conversation
            {
                CompanyID = company, Kind = "Direct", CreatedByEmployeeId = ctx.EmployeeId!.Value, CreatedAt = now,
                DirectKeyLow = low, DirectKeyHigh = high,
            };

            // THE RACE. Two simultaneous "start a conversation with this person" clicks both reach the
            // lookup above, both find nothing, and both insert. The filtered unique index
            // UX_Conversations_DirectPair lets exactly one of them commit; the loser must not surface a
            // unique-key 500 to the user, and must not return a second conversation either. So it re-reads
            // and returns the winner — the outcome the caller wanted in the first place.
            //
            // Conversation and members are written in ONE transaction. Previously the conversation was
            // saved first and the two member rows second, so a failure between them left a conversation
            // with no participants that nobody could see or clean up.
            using var tx = await _db.Database.BeginTransactionAsync();
            try
            {
                _db.Conversations.Add(conv);
                await _db.SaveChangesAsync();
                _db.ConversationMembers.AddRange(
                    new ConversationMember { ConversationId = conv.ID, EmployeeId = ctx.EmployeeId!.Value, Role = "Member", JoinedAt = now },
                    new ConversationMember { ConversationId = conv.ID, EmployeeId = otherId, Role = "Member", JoinedAt = now });
                await _db.SaveChangesAsync();
                await tx.CommitAsync();
                return conv.ID;
            }
            catch (DbUpdateException)
            {
                // Lost the race — or the index refused for any other reason. Roll back this attempt and
                // report whatever is actually stored. A re-read that finds nothing means the failure was
                // not a duplicate, and 0 (refused) is the honest answer rather than a fabricated id.
                await tx.RollbackAsync();
                _db.ChangeTracker.Clear();
                return await _db.Conversations.AsNoTracking()
                    .Where(c => c.CompanyID == company && c.Kind == "Direct"
                             && c.DirectKeyLow == low && c.DirectKeyHigh == high)
                    .Select(c => c.ID).FirstOrDefaultAsync();
            }
        }

        public async Task<int> CreateGroupAsync(int companyId, int meId, string title, IEnumerable<int> memberIds)
        {
            // Group Chat is NOT part of this increment; this path already existed and is left functionally
            // as it was. What changes is that it can no longer be driven with a caller-supplied company or
            // an unresolved context, and members must belong to the resolved company — the same boundary
            // the direct path now enforces.
            var ctx = await ScopeAsync();
            if (ctx == null) return 0;
            bool mayCreate;
            try { mayCreate = await _access.CanAsync(ctx, CommunicationActions.CreateGroup, null); }
            catch (Exception) { mayCreate = false; }
            if (!mayCreate) return 0;
            companyId = ctx.CompanyId;
            meId = ctx.EmployeeId!.Value;
            var eligibleIds = await _db.Employee.AsNoTracking()
                .Where(e => e.EmpCompanyID == companyId).Select(e => e.ID).ToListAsync();
            memberIds = memberIds.Where(eligibleIds.Contains).ToList();
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
            // The list is built from the RESOLVED company, not the argument. Membership rows alone are not
            // a filter: a member row pointing at another company's conversation must not surface one.
            var ctx = await ScopeAsync();
            if (ctx == null) return new();
            bool mayRead;
            try { mayRead = await _access.CanAsync(ctx, CommunicationActions.Read, null); }
            catch (Exception) { mayRead = false; }
            if (!mayRead) return new();
            companyId = ctx.CompanyId;
            meId = ctx.EmployeeId!.Value;
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
            // Canonical rule instead of company-argument + bare membership. A refused header is null, the
            // same answer an absent conversation gives, so the header cannot be used to probe ids.
            var ctx = await ScopeAsync();
            if (ctx == null || !await MayAsync(ctx, CommunicationActions.Read, conversationId)) return null;
            companyId = ctx.CompanyId;
            var c = await _db.Conversations.AsNoTracking().FirstOrDefaultAsync(x => x.ID == conversationId && x.CompanyID == companyId);
            if (c == null) return null;
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
            // Company + membership through the canonical rule, replacing the bare membership check. An
            // empty list is returned for a refusal exactly as it is for an empty conversation, so a caller
            // cannot tell "not yours" from "nothing here" and use this endpoint to probe ids.
            var ctx = await ScopeAsync();
            if (ctx == null || !await MayAsync(ctx, CommunicationActions.Read, conversationId)) return new();
            // Keep the take bounded even if a caller asks for more; history stays paged.
            if (take <= 0 || take > 100) take = 30;
            var q = _db.ChatMessages.AsNoTracking().Where(m => m.ConversationId == conversationId);
            if (beforeId.HasValue) q = q.Where(m => m.ID < beforeId.Value);
            var msgs = await q.OrderByDescending(m => m.ID).Take(take).ToListAsync();
            msgs.Reverse();
            var ids = msgs.Select(m => m.ID).ToList();
            var reactions = await _db.ChatReactions.AsNoTracking().Where(r => ids.Contains(r.MessageId)).ToListAsync();
            var names = await NamesAsync(msgs.Select(m => m.SenderEmployeeId));
            var replies = await ReplyPreviewsAsync(msgs.Where(m => m.ReplyToId.HasValue).Select(m => m.ReplyToId!.Value), conversationId);
            return msgs.Select(m => ToDto(m, meId, names, reactions, replies)).ToList();
        }

        // For each message id, the quoted preview (sender display name + short body) used to render reply bubbles.
        // F2 — THE BOUNDARY IS RE-ENFORCED HERE, AT READ TIME.
        //
        // This used to look messages up by id alone: .Where(m => ids.Contains(m.ID)). The ids arrive from
        // ChatMessage.ReplyToId, which is data — and data can already be wrong. A row written before this
        // fix, or by any future path that forgets to validate, can point at a message in another
        // conversation or another company, and the caller would then be handed that message's sender name
        // and the first 120 characters of its body inside an innocent-looking reply bubble.
        //
        // So the conversation is now a REQUIRED argument and part of the query. Validating on write (F1) is
        // not enough on its own: it protects rows written from now on, and says nothing about rows that
        // already exist. A foreign ReplyToId that is somehow persisted now yields no preview at all rather
        // than a leak — the reply renders without a quote, which is a cosmetic loss and a correct refusal.
        private async Task<Dictionary<int, (string sender, string? body)>> ReplyPreviewsAsync(IEnumerable<int> replyToIds, int conversationId)
        {
            var ids = replyToIds.Distinct().ToList();
            if (ids.Count == 0 || conversationId <= 0) return new();
            var srcs = await _db.ChatMessages.AsNoTracking()
                .Where(m => ids.Contains(m.ID) && m.ConversationId == conversationId)
                .Select(m => new { m.ID, m.SenderEmployeeId, m.Body, m.AttachmentType, m.AttachmentName, m.DeletedAt }).ToListAsync();
            var senderNames = await NamesAsync(srcs.Select(s => s.SenderEmployeeId));
            var map = new Dictionary<int, (string sender, string? body)>();
            foreach (var s in srcs)
            {
                var nm = senderNames.TryGetValue(s.SenderEmployeeId, out var info) ? info.name : "-";
                string? prev = s.DeletedAt != null ? null
                    : !string.IsNullOrWhiteSpace(s.Body) ? (s.Body!.Length > 120 ? s.Body!.Substring(0, 120) : s.Body!)
                    : (s.AttachmentType == "image" ? "📷" : (s.AttachmentName != null ? "📎 " + s.AttachmentName : null));
                map[s.ID] = (nm, prev);
            }
            return map;
        }

        private static ChatMsgDto ToDto(ChatMessage m, int meId, Dictionary<int, (string name, string? avatar)> names, List<ChatReaction> reactions, Dictionary<int, (string sender, string? body)>? replies = null)
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
                ReplyToSender = m.ReplyToId.HasValue && replies != null && replies.TryGetValue(m.ReplyToId.Value, out var rp) ? rp.sender : null,
                ReplyToBody = m.ReplyToId.HasValue && replies != null && replies.TryGetValue(m.ReplyToId.Value, out var rp2) ? rp2.body : null,
            };
        }

        public async Task<ChatMsgDto?> SendAsync(int companyId, int meId, int conversationId, string? body, string? attPath, string? attName, string? attType, int? replyToId, IEnumerable<int>? mentionedIds)
        {
            // Company + membership + send permission, through the canonical rule (F5).
            var ctx = await ScopeAsync();
            if (ctx == null || !await MayAsync(ctx, CommunicationActions.Send, conversationId)) return null;
            if (string.IsNullOrWhiteSpace(body) && string.IsNullOrEmpty(attPath)) return null;

            // F6 — authoritative server-side length. Measured on the TRIMMED body, because that is what is
            // stored; otherwise trailing whitespace could be used to pad past a limit that then does not
            // apply to the stored value.
            var trimmed = body?.Trim();
            if (trimmed != null && trimmed.Length > MaxMessageLength) return null;

            // F1 — A REPLY MAY ONLY QUOTE A MESSAGE IN THIS CONVERSATION.
            // replyToId arrives from the request body and was previously stored unchecked, which let a
            // member of one conversation attach a foreign message id and receive its sender and body back
            // in the response. The check is scoped to conversationId, so it cannot confirm the existence of
            // a message elsewhere: a foreign id and a nonexistent id are both simply "not here", and both
            // produce the same refusal as an empty message.
            if (replyToId.HasValue)
            {
                if (replyToId.Value <= 0) return null;
                bool quotable = await _db.ChatMessages.AsNoTracking()
                    .AnyAsync(m => m.ID == replyToId.Value && m.ConversationId == conversationId);
                if (!quotable) return null;
            }

            var now = DateTime.UtcNow;
            var msg = new ChatMessage
            {
                ConversationId = conversationId, SenderEmployeeId = meId, Body = string.IsNullOrWhiteSpace(trimmed) ? null : trimmed,
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
            var replies = replyToId.HasValue ? await ReplyPreviewsAsync(new[] { replyToId.Value }, conversationId) : null;
            var dto = ToDto(msg, meId, names, new List<ChatReaction>(), replies);

            // realtime push to the conversation room (mark not-mine for other viewers is done client-side by senderId compare)
            await _hub.Clients.Group(ChatHub.ConvGroup(conversationId)).SendAsync("message", new
            {
                conversationId, id = dto.Id, senderId = dto.SenderId, senderName = dto.SenderName, senderAvatar = dto.SenderAvatar,
                body = dto.Body, attachmentPath = dto.AttachmentPath, attachmentName = dto.AttachmentName, attachmentType = dto.AttachmentType,
                replyToId = dto.ReplyToId, replyToSender = dto.ReplyToSender, replyToBody = dto.ReplyToBody, createdAt = dto.CreatedAt,
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
            // Editing was already correctly restricted to the sender's own message, which is why it was not
            // a cross-tenant hole. The scope check is added so an unresolved context refuses, and the length
            // limit is applied here too — otherwise a short message could be edited past the cap afterwards.
            var ctx = await ScopeAsync();
            if (ctx == null) return false;
            meId = ctx.EmployeeId!.Value;
            var m = await _db.ChatMessages.FirstOrDefaultAsync(x => x.ID == messageId && x.SenderEmployeeId == meId && x.DeletedAt == null);
            if (m == null || string.IsNullOrWhiteSpace(body)) return false;
            if (!await MayAsync(ctx, CommunicationActions.Send, m.ConversationId)) return false;
            var newBody = body.Trim();
            if (newBody.Length > MaxMessageLength) return false;
            m.Body = newBody; m.EditedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync();
            await _hub.Clients.Group(ChatHub.ConvGroup(m.ConversationId)).SendAsync("edited", new { conversationId = m.ConversationId, id = m.ID, body = m.Body, editedAt = m.EditedAt });
            return true;
        }

        public async Task<bool> DeleteAsync(int meId, int messageId)
        {
            // Sender-scoped already, like EditAsync. Scope resolution added so an unresolved context
            // refuses rather than relying on the caller's meId being trustworthy.
            var ctx = await ScopeAsync();
            if (ctx == null) return false;
            meId = ctx.EmployeeId!.Value;
            var m = await _db.ChatMessages.FirstOrDefaultAsync(x => x.ID == messageId && x.SenderEmployeeId == meId && x.DeletedAt == null);
            if (m == null) return false;
            if (!await MayAsync(ctx, CommunicationActions.Send, m.ConversationId)) return false;
            m.DeletedAt = DateTime.UtcNow; m.Body = null; m.AttachmentPath = null;
            await _db.SaveChangesAsync();
            await _hub.Clients.Group(ChatHub.ConvGroup(m.ConversationId)).SendAsync("deleted", new { conversationId = m.ConversationId, id = m.ID });
            return true;
        }

        public async Task ToggleReactionAsync(int meId, int messageId, string emoji)
        {
            // F3 — REACTING IS A WRITE, AND IT WAS UNGATED.
            // This method used to load the message by id alone and then write a reaction and broadcast to
            // that message's conversation group. Nothing checked company or membership, so any signed-in
            // employee could react to any message in the database and cause a live SignalR event to fire
            // inside a conversation they cannot read — a write and a notification across a tenant boundary.
            //
            // The order matters: resolve scope, find the message, then ask the canonical rule about the
            // message's OWN conversation. Every refusal returns before the first _db write and before the
            // broadcast, so a refused attempt leaves no row and emits no event.
            var ctx = await ScopeAsync();
            if (ctx == null) return;
            if (messageId <= 0 || string.IsNullOrWhiteSpace(emoji)) return;
            var m = await _db.ChatMessages.AsNoTracking().FirstOrDefaultAsync(x => x.ID == messageId);
            if (m == null) return;
            if (!await MayAsync(ctx, CommunicationActions.Send, m.ConversationId)) return;
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
            // Marking read is a write and it also clears bell notifications, so it needs the same gate as
            // reading. Refused before any write, and before the "read" broadcast that drives Seen ticks.
            var ctx = await ScopeAsync();
            if (ctx == null || !await MayAsync(ctx, CommunicationActions.Read, conversationId)) return;
            meId = ctx.EmployeeId!.Value;
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
            // The employee picker was already company-scoped, which was correct — but on the company the
            // CALLER passed. It now uses the resolved company, so a caller cannot enumerate another
            // company's staff by supplying its id.
            var ctx = await ScopeAsync();
            if (ctx == null) return new();
            bool mayRead;
            try { mayRead = await _access.CanAsync(ctx, CommunicationActions.Read, null); }
            catch (Exception) { mayRead = false; }
            if (!mayRead) return new();
            companyId = ctx.CompanyId;
            meId = ctx.EmployeeId!.Value;
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
            // Resolved company, canonical permission, and the new member must be an employee of that same
            // company — otherwise a group becomes a bridge across the tenant boundary.
            var ctx = await ScopeAsync();
            if (ctx == null || !await MayAsync(ctx, CommunicationActions.Send, conversationId)) return false;
            companyId = ctx.CompanyId;
            meId = ctx.EmployeeId!.Value;
            if (!await _db.Employee.AsNoTracking().AnyAsync(e => e.ID == newMemberId && e.EmpCompanyID == companyId)) return false;
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
            // Same gate. The Owner-or-self rule below is preserved exactly; only the company and the
            // caller identity now come from the resolved scope.
            var ctx = await ScopeAsync();
            if (ctx == null || !await MayAsync(ctx, CommunicationActions.Send, conversationId)) return false;
            companyId = ctx.CompanyId;
            meId = ctx.EmployeeId!.Value;
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
