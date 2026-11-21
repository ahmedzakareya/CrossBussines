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
	// INCOMING WEBHOOKS (owner, 2026-11-19 — a gap the second evaluation found: Teams, Slack and Google Chat let another
	// system post into a channel). A group's admin makes a hook; the outside system POSTs {"text": "..."} to its address.
	//
	//   * The token is 32 random bytes, shown ONCE when the hook is made, and stored only as its SHA-256. A lost token is
	//     replaced by revoking the hook and making another one.
	//   * The message goes in the name of the member who made the hook (marked "via <hook>" in the chat), through the
	//     same posting path as any message: membership, length, and the company's sensitive-data rule all apply. It is
	//     posted only while that member is still in the conversation and active; otherwise the hook answers as unknown.
	//   * The request carries no login, so the company comes from the hook itself, and the post runs in a worker scope
	//     for that company (WorkerScope.ForCompany) — the same way the scheduled-message worker posts.
	//   * At most 30 posts a minute per hook (this process); a burst beyond that is refused, not queued.
	// ==========================================================================================
	public sealed class ChatWebhookService
	{
		private readonly CrossDbContext _db;
		public ChatWebhookService(CrossDbContext db) { _db = db; }

		public const int MaxPerMinute = 30;
		private static readonly ConcurrentDictionary<int, Queue<DateTime>> Recent = new();

		public static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();
		private static string NewToken() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

		private async Task<bool> IsOwnerAsync(int companyId, int convId, int meId)
			=> await _db.Conversations.AsNoTracking().AnyAsync(c => c.ID == convId && c.CompanyID == companyId && c.Kind == "Group")
			&& await _db.ConversationMembers.AsNoTracking().AnyAsync(m => m.ConversationId == convId && m.EmployeeId == meId && m.Role == "Owner");

		/// The hooks of a group, for its admin: never the token, only who made it and how it has been used.
		public async Task<object?> ListAsync(int companyId, int convId, int meId)
		{
			if (!await IsOwnerAsync(companyId, convId, meId)) return null;
			var rows = await _db.Set<ChatWebhook>().AsNoTracking().Where(h => h.CompanyId == companyId && h.ConversationId == convId)
				.OrderBy(h => h.RevokedAt != null).ThenByDescending(h => h.CreatedAt).ToListAsync();
			var ids = rows.Select(r => r.CreatedByEmployeeId).Distinct().ToList();
			var names = await _db.Employee.AsNoTracking().Where(e => ids.Contains(e.ID)).Select(e => new { e.ID, e.FullName, e.FullNameEn }).ToListAsync();
			bool ar = System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ar";
			string Nm(int id) { var e = names.FirstOrDefault(x => x.ID == id); return e == null ? "-" : (ar ? e.FullName : (string.IsNullOrWhiteSpace(e.FullNameEn) ? e.FullName : e.FullNameEn)) ?? "-"; }
			static DateTime? U(DateTime? d) => d == null ? null : DateTime.SpecifyKind(d.Value, DateTimeKind.Utc);
			return rows.Select(h => new
			{
				id = h.ID, name = h.Name, by = Nm(h.CreatedByEmployeeId), createdAt = U(h.CreatedAt), lastUsedAt = U(h.LastUsedAt), uses = h.UseCount,
				revoked = h.RevokedAt != null, revokedAt = U(h.RevokedAt),
			}).ToList();
		}

		/// A new hook; the token comes back here and nowhere else.
		public async Task<(int id, string token)?> CreateAsync(int companyId, int convId, int meId, string? name)
		{
			name = (name ?? "").Trim();
			if (name.Length == 0) return null;
			if (name.Length > 60) name = name[..60];
			if (!await IsOwnerAsync(companyId, convId, meId)) return null;
			if (await _db.Set<ChatWebhook>().CountAsync(h => h.CompanyId == companyId && h.ConversationId == convId && h.RevokedAt == null) >= 10) return null;
			var token = NewToken();
			var row = new ChatWebhook { CompanyId = companyId, ConversationId = convId, Name = name, TokenHash = Hash(token), CreatedByEmployeeId = meId, CreatedAt = DateTime.UtcNow };
			_db.Set<ChatWebhook>().Add(row);
			await _db.SaveChangesAsync();
			return (row.ID, token);
		}

		public async Task<bool> RevokeAsync(int companyId, int convId, int meId, int id)
		{
			if (!await IsOwnerAsync(companyId, convId, meId)) return false;
			var h = await _db.Set<ChatWebhook>().FirstOrDefaultAsync(x => x.ID == id && x.CompanyId == companyId && x.ConversationId == convId);
			if (h == null) return false;
			if (h.RevokedAt == null) { h.RevokedAt = DateTime.UtcNow; h.RevokedByEmployeeId = meId; await _db.SaveChangesAsync(); }
			return true;
		}

		/// "via <hook>" for the messages that came through one.
		public static async Task<Dictionary<int, string>> ViaAsync(CrossDbContext db, IReadOnlyCollection<int> messageIds)
		{
			if (messageIds.Count == 0) return new();
			try
			{
				return await (from p in db.Set<ChatWebhookPost>().AsNoTracking()
							  join h in db.Set<ChatWebhook>().AsNoTracking() on p.WebhookId equals h.ID
							  where messageIds.Contains(p.MessageId)
							  select new { p.MessageId, h.Name }).ToDictionaryAsync(x => x.MessageId, x => x.Name);
			}
			catch (Microsoft.Data.SqlClient.SqlException) { return new(); }   // comm_webhooks.sql not applied here
		}

		public enum Outcome { Posted, Unknown, Empty, TooLong, TooMany, Refused }

		/// A post from outside. Unknown covers a wrong id, a wrong token, a revoked hook, and a maker who has left —
		/// the caller learns nothing about which.
		public async Task<(Outcome outcome, int messageId)> ReceiveAsync(IServiceScopeFactory scopes, int id, string? token, string? text)
		{
			if (string.IsNullOrEmpty(token) || token.Length > 100) return (Outcome.Unknown, 0);
			var hash = Hash(token);
			var hook = await _db.Set<ChatWebhook>().AsNoTracking().FirstOrDefaultAsync(h => h.ID == id);
			if (hook == null || hook.RevokedAt != null || !CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(hook.TokenHash), Encoding.ASCII.GetBytes(hash))) return (Outcome.Unknown, 0);
			var body = (text ?? "").Trim();
			if (body.Length == 0) return (Outcome.Empty, 0);
			if (body.Length > ChatPlusService.MaxBody) return (Outcome.TooLong, 0);
			if (!Allow(hook.ID)) return (Outcome.TooMany, 0);

			using var scope = WorkerScope.ForCompany(scopes, hook.CompanyId);
			var db = scope.ServiceProvider.GetRequiredService<CrossDbContext>();
			bool makerHere = await db.Employee.AsNoTracking().AnyAsync(e => e.ID == hook.CreatedByEmployeeId && e.IsActive)
				&& await db.ConversationMembers.AsNoTracking().AnyAsync(m => m.ConversationId == hook.ConversationId && m.EmployeeId == hook.CreatedByEmployeeId);
			if (!makerHere) return (Outcome.Unknown, 0);
			var plus = ActivatorUtilities.CreateInstance<ChatPlusService>(scope.ServiceProvider);
			var msg = await plus.PostAsync(hook.CompanyId, hook.ConversationId, hook.CreatedByEmployeeId, new ChatPlusService.PostInput { Body = body });
			if (msg == null) return (Outcome.Refused, 0);   // the sensitive-data rule, or the conversation is gone
			var now = DateTime.UtcNow;
			db.Set<ChatWebhookPost>().Add(new ChatWebhookPost { MessageId = msg.ID, WebhookId = hook.ID, CompanyId = hook.CompanyId, CreatedAt = now });
			var row = await db.Set<ChatWebhook>().FirstAsync(h => h.ID == hook.ID);
			row.LastUsedAt = now; row.UseCount++;
			await db.SaveChangesAsync();
			return (Outcome.Posted, msg.ID);
		}

		private static bool Allow(int hookId)
		{
			var q = Recent.GetOrAdd(hookId, _ => new Queue<DateTime>());
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
