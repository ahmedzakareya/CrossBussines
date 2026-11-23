using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using CrossBuy.BL.Platform;
using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Chat;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL.Communication
{
	// ==========================================================================================
	// GUESTS FROM OUTSIDE THE COMPANY (owner, 2026-11-22 — the earlier "no guests" policy reversed). A group's admin
	// invites one outsider into one group conversation; the outsider opens a link and nothing else:
	//
	//   * The link carries a 32-byte random secret in its #fragment (never sent in a referrer or a server log line).
	//     The secret is shown once and kept only as its SHA-256. The link expires (1–30 days) and can be revoked.
	//   * A guest READS the conversation from the moment they were invited — never its history — and WRITES in it.
	//     Attachments are named, not served: a guest has no way to any file, any person's profile or any other
	//     conversation. Their messages go through the chat's own posting path (length, the sensitive-data rule), in
	//     the inviter's name, marked with the guest's own name for everyone (ChatGuestPosts), at most 20 a minute.
	//   * Everything stops when the link expires or is revoked, when the conversation is deleted, or when the inviter
	//     leaves it or the company — the guest was the inviter's to bring.
	//   * The request has no login, so the company comes from the guest's row and the work runs in a worker scope bound
	//     to it (WorkerScope.ForCompany), as the webhooks do.
	// ==========================================================================================
	public sealed class ChatGuestService
	{
		private readonly CrossDbContext _db;
		public ChatGuestService(CrossDbContext db) { _db = db; }

		public const int MaxPerMinute = 20;
		private static readonly ConcurrentDictionary<int, Queue<DateTime>> Recent = new();

		private static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();
		private static string NewToken() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

		private async Task<bool> IsOwnerAsync(int companyId, int convId, int meId)
			=> await _db.Conversations.AsNoTracking().AnyAsync(c => c.ID == convId && c.CompanyID == companyId && c.Kind == "Group")
			&& await _db.ConversationMembers.AsNoTracking().AnyAsync(m => m.ConversationId == convId && m.EmployeeId == meId && m.Role == "Owner");

		// ---------------- the admin's side ----------------
		public async Task<object?> ListAsync(int companyId, int convId, int meId)
		{
			if (!await IsOwnerAsync(companyId, convId, meId)) return null;
			var now = DateTime.UtcNow;
			var rows = await _db.Set<ChatGuest>().AsNoTracking().Where(g => g.CompanyId == companyId && g.ConversationId == convId)
				.OrderBy(g => g.RevokedAt != null || g.ExpiresAt < now).ThenByDescending(g => g.CreatedAt).ToListAsync();
			static DateTime? U(DateTime? d) => d == null ? null : DateTime.SpecifyKind(d.Value, DateTimeKind.Utc);
			return rows.Select(g => new
			{
				id = g.ID, name = g.Name, createdAt = U(g.CreatedAt), expiresAt = U(g.ExpiresAt), lastSeenAt = U(g.LastSeenAt),
				state = g.RevokedAt != null ? "revoked" : g.ExpiresAt < now ? "expired" : "active",
			}).ToList();
		}

		/// A new guest; the secret comes back here and nowhere else.
		public async Task<(int id, string token)?> CreateAsync(int companyId, int convId, int meId, string? name, int days)
		{
			name = (name ?? "").Trim();
			if (name.Length == 0) return null;
			if (name.Length > 80) name = name[..80];
			if (!await IsOwnerAsync(companyId, convId, meId)) return null;
			var now = DateTime.UtcNow;
			if (await _db.Set<ChatGuest>().CountAsync(g => g.CompanyId == companyId && g.ConversationId == convId && g.RevokedAt == null && g.ExpiresAt > now) >= 20) return null;
			var token = NewToken();
			var last = await _db.ChatMessages.AsNoTracking().Where(m => m.ConversationId == convId).Select(m => (int?)m.ID).MaxAsync() ?? 0;
			var row = new ChatGuest
			{
				CompanyId = companyId, ConversationId = convId, Name = name, TokenHash = Hash(token), InvitedByEmployeeId = meId,
				FirstMessageId = last, CreatedAt = now, ExpiresAt = now.AddDays(Math.Clamp(days, 1, 30)),
			};
			_db.Set<ChatGuest>().Add(row);
			await _db.SaveChangesAsync();
			return (row.ID, token);
		}

		public async Task<bool> RevokeAsync(int companyId, int convId, int meId, int id)
		{
			if (!await IsOwnerAsync(companyId, convId, meId)) return false;
			var g = await _db.Set<ChatGuest>().FirstOrDefaultAsync(x => x.ID == id && x.CompanyId == companyId && x.ConversationId == convId);
			if (g == null) return false;
			if (g.RevokedAt == null) { g.RevokedAt = DateTime.UtcNow; g.RevokedByEmployeeId = meId; await _db.SaveChangesAsync(); }
			return true;
		}

		/// "guest: <name>" for the messages a guest wrote.
		public static async Task<Dictionary<int, string>> GuestOfAsync(CrossDbContext db, IReadOnlyCollection<int> messageIds)
		{
			if (messageIds.Count == 0) return new();
			try
			{
				return await (from p in db.Set<ChatGuestPost>().AsNoTracking()
							  join g in db.Set<ChatGuest>().AsNoTracking() on p.GuestId equals g.ID
							  where messageIds.Contains(p.MessageId)
							  select new { p.MessageId, g.Name }).ToDictionaryAsync(x => x.MessageId, x => x.Name);
			}
			catch (Microsoft.Data.SqlClient.SqlException) { return new(); }   // comm_guests.sql not applied here
		}

		// ---------------- the guest's side (no login) ----------------
		/// The guest behind "id.secret", or null — a wrong secret, an expired or revoked link, a deleted conversation, and
		/// an inviter who has left all look the same to the caller.
		private async Task<ChatGuest?> GuestAsync(IServiceProvider scoped, string? key)
		{
			if (string.IsNullOrEmpty(key) || key.Length > 140) return null;
			var dot = key.IndexOf('.'); if (dot <= 0 || !int.TryParse(key[..dot], out var id)) return null;
			var secret = key[(dot + 1)..];
			var g = await _db.Set<ChatGuest>().AsNoTracking().FirstOrDefaultAsync(x => x.ID == id);
			if (g == null || g.RevokedAt != null || g.ExpiresAt < DateTime.UtcNow) return null;
			if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(g.TokenHash), Encoding.ASCII.GetBytes(Hash(secret)))) return null;
			var db = scoped.GetRequiredService<CrossDbContext>();
			bool ok = await db.Conversations.AsNoTracking().AnyAsync(c => c.ID == g.ConversationId && c.CompanyID == g.CompanyId)
				&& await db.ConversationMembers.AsNoTracking().AnyAsync(m => m.ConversationId == g.ConversationId && m.EmployeeId == g.InvitedByEmployeeId)
				&& await db.Employee.AsNoTracking().AnyAsync(e => e.ID == g.InvitedByEmployeeId && e.IsActive);
			return ok ? g : null;
		}

		/// The company a guest key belongs to (0 when it names no guest): the id is the part before the dot.
		private async Task<int> CompanyOfAsync(string? key)
		{
			var dot = key?.IndexOf('.') ?? -1;
			if (dot <= 0 || !int.TryParse(key![..dot], out var id)) return 0;
			return await _db.Set<ChatGuest>().AsNoTracking().Where(x => x.ID == id).Select(x => x.CompanyId).FirstOrDefaultAsync();
		}

		/// The guest behind a key, read in a worker scope bound to their company (null as for any wrong key) — for the
		/// guest's part in a call of their conversation (HospitalCallHub.Guests).
		public async Task<ChatGuest?> ResolveAsync(IServiceScopeFactory scopes, string? key)
		{
			var probe = await CompanyOfAsync(key);
			if (probe == 0) return null;
			using var scope = WorkerScope.ForCompany(scopes, probe);
			return await GuestAsync(scope.ServiceProvider, key);
		}

		/// What a guest sees: the conversation's name and the messages since they were invited (after `since`).
		public async Task<object?> ReadAsync(IServiceScopeFactory scopes, string? key, int since)
		{
			var probe = await CompanyOfAsync(key);
			if (probe == 0) return null;
			using var scope = WorkerScope.ForCompany(scopes, probe);
			var g = await GuestAsync(scope.ServiceProvider, key); if (g == null) return null;
			var db = scope.ServiceProvider.GetRequiredService<CrossDbContext>();
			var after = Math.Max(since, g.FirstMessageId);
			var rows = await db.ChatMessages.AsNoTracking().Where(m => m.ConversationId == g.ConversationId && m.ID > after && m.ThreadRootId == null)
				.OrderBy(m => m.ID).Take(100).Select(m => new { m.ID, m.SenderEmployeeId, m.Body, m.AttachmentName, m.AttachmentType, m.CreatedAt, Deleted = m.DeletedAt != null }).ToListAsync();
			var ids = rows.Select(r => r.SenderEmployeeId).Distinct().ToList();
			var names = await db.Employee.AsNoTracking().Where(e => ids.Contains(e.ID)).Select(e => new { e.ID, e.FullName, e.FullNameEn }).ToListAsync();
			var guestOf = await GuestOfAsync(db, rows.Select(r => r.ID).ToList());
			var mine = await db.Set<ChatGuestPost>().AsNoTracking().Where(p => p.GuestId == g.ID && rows.Select(r => r.ID).Contains(p.MessageId)).Select(p => p.MessageId).ToListAsync();
			var title = await db.Conversations.AsNoTracking().Where(c => c.ID == g.ConversationId).Select(c => c.Title).FirstOrDefaultAsync();
			var row = await db.Set<ChatGuest>().FirstAsync(x => x.ID == g.ID);
			row.LastSeenAt = DateTime.UtcNow; await db.SaveChangesAsync();
			return new
			{
				conversation = title, guest = g.Name, expiresAt = DateTime.SpecifyKind(g.ExpiresAt, DateTimeKind.Utc),
				// a group call going on in the conversation now: the guest may join it (HospitalCallHub.Guests)
				call = CrossBuy.Hubs.HospitalCallHub.GuestCallOf(g) is { } live ? new { id = live.CallId, video = live.Video, people = live.People } : null,
				messages = rows.Select(r =>
				{
					var n = names.FirstOrDefault(x => x.ID == r.SenderEmployeeId);
					return new
					{
						id = r.ID,
						from = guestOf.TryGetValue(r.ID, out var gn) ? gn : (n == null ? "" : n.FullName),
						fromEn = guestOf.TryGetValue(r.ID, out var gn2) ? gn2 : (n == null ? "" : (string.IsNullOrWhiteSpace(n.FullNameEn) ? n.FullName : n.FullNameEn)),
						guest = guestOf.ContainsKey(r.ID), mine = mine.Contains(r.ID),
						text = r.Deleted ? null : r.Body,
						file = r.Deleted || r.AttachmentType == null ? null : (r.AttachmentType == "image" ? "📷" : r.AttachmentType == "audio" ? "🎤" : r.AttachmentType == "video" ? "🎬" : "📎 " + (r.AttachmentName ?? "")),
						deleted = r.Deleted, at = DateTime.SpecifyKind(r.CreatedAt, DateTimeKind.Utc),
					};
				}).ToList(),
			};
		}

		public enum Outcome { Posted, Unknown, Empty, TooLong, TooMany, Refused }

		public async Task<(Outcome outcome, int messageId)> PostAsync(IServiceScopeFactory scopes, string? key, string? text)
		{
			var probe = await CompanyOfAsync(key);
			if (probe == 0) return (Outcome.Unknown, 0);
			using var scope = WorkerScope.ForCompany(scopes, probe);
			var g = await GuestAsync(scope.ServiceProvider, key); if (g == null) return (Outcome.Unknown, 0);
			var body = (text ?? "").Trim();
			if (body.Length == 0) return (Outcome.Empty, 0);
			if (body.Length > 2000) return (Outcome.TooLong, 0);
			if (!Allow(g.ID)) return (Outcome.TooMany, 0);
			var plus = ActivatorUtilities.CreateInstance<ChatPlusService>(scope.ServiceProvider);
			var msg = await plus.PostAsync(g.CompanyId, g.ConversationId, g.InvitedByEmployeeId, new ChatPlusService.PostInput { Body = body });
			if (msg == null) return (Outcome.Refused, 0);
			var db = scope.ServiceProvider.GetRequiredService<CrossDbContext>();
			db.Set<ChatGuestPost>().Add(new ChatGuestPost { MessageId = msg.ID, GuestId = g.ID, CompanyId = g.CompanyId, CreatedAt = DateTime.UtcNow });
			await db.SaveChangesAsync();
			return (Outcome.Posted, msg.ID);
		}

		private static bool Allow(int guestId)
		{
			var q = Recent.GetOrAdd(guestId, _ => new Queue<DateTime>());
			lock (q)
			{
				var now = DateTime.UtcNow;
				while (q.Count > 0 && now - q.Peek() > TimeSpan.FromMinutes(1)) q.Dequeue();
				if (q.Count >= MaxPerMinute) return false;
				q.Enqueue(now); return true;
			}
		}
	}
}
