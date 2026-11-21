using System.Collections.Concurrent;
using System.Text.Json;
using CrossBuy.BL.Platform.Ai;
using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Chat;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL.Communication
{
	// ==========================================================================================
	// THE ASSISTANT IN A CONVERSATION (owner, 2026-11-19 — the gap the second evaluation found: Copilot, Slack AI,
	// Gemini and Zoom all put AI inside the conversation).
	//
	//   * summary — of the conversation (or one thread): overview, key points, decisions, tasks with who does them;
	//   * ask     — a question about the conversation, answered only from it, naming the messages it rests on;
	//   * translate — one message into the other language (ar ↔ en), stored so it is translated once for everyone.
	//
	// All of it on the internal AI service's local model (crossbuy_ai /assist, the model the call summaries load), so
	// nothing leaves the building and nothing is paid for. The conversation's text is free text: the egress policy
	// carries it under its own purpose (ChatAssistance) and only to an Internal destination.
	//
	// WHO: the controllers ask the chat's authority (communication "read" on the conversation) first; this re-checks
	// that the conversation is the caller's company's and that they are in it. A job can be read only by whoever
	// started it.
	// ==========================================================================================
	public sealed class ChatAssistant
	{
		private const int MaxMessages = 800;

		private readonly CrossDbContext _db;
		private readonly IAiService _ai;
		private readonly IAiEgressPolicy _egress;
		private readonly IAiProviderAuthority _providers;
		private readonly IConfiguration _config;
		private readonly ILogger<ChatAssistant> _log;

		/// job → who started it (company, employee, conversation) and when; a job is read only by its owner.
		private static readonly ConcurrentDictionary<string, (int Company, int Employee, int Conv, DateTime At)> Jobs = new();

		public ChatAssistant(CrossDbContext db, IAiService ai, IAiEgressPolicy egress, IAiProviderAuthority providers, IConfiguration config, ILogger<ChatAssistant> log)
		{ _db = db; _ai = ai; _egress = egress; _providers = providers; _config = config; _log = log; }

		public sealed record Started(bool Ok, string? Job, string? Code);

		private Task<AiProxyResult?> Post(int companyId, string path, object payload, CancellationToken ct)
			=> CommAi.PostAsync(_ai, _egress, _providers, _config, _log, AiEgressPurpose.ChatAssistance, AiDataClassification.FreeTextBusinessContent, companyId, path, payload, ct);

		private async Task<bool> MemberAsync(int companyId, int meId, int convId, CancellationToken ct)
			=> await _db.Conversations.AsNoTracking().AnyAsync(c => c.ID == convId && c.CompanyID == companyId, ct)
			   && await _db.ConversationMembers.AsNoTracking().AnyAsync(m => m.ConversationId == convId && m.EmployeeId == meId, ct);

		/// Start a summary or a question. threadRootId: one thread only; sinceId: only what came after (unread).
		public async Task<Started> StartAsync(int companyId, int meId, int convId, string mode, string? question, int? threadRootId, int? sinceId, CancellationToken ct)
		{
			if (mode is not ("summary" or "ask")) return new(false, null, "bad_mode");
			if (mode == "ask" && string.IsNullOrWhiteSpace(question)) return new(false, null, "no_question");
			if (!await MemberAsync(companyId, meId, convId, ct)) return new(false, null, "not_found");

			var q = _db.ChatMessages.AsNoTracking().Where(m => m.ConversationId == convId && m.DeletedAt == null && m.Body != null && m.Body != "");
			q = threadRootId is int root ? q.Where(m => m.ID == root || m.ThreadRootId == root) : q.Where(m => m.ThreadRootId == null);
			if (sinceId is int since && since > 0) q = q.Where(m => m.ID > since);
			var rows = await q.OrderByDescending(m => m.ID).Take(MaxMessages).Select(m => new { m.ID, m.SenderEmployeeId, m.Body }).ToListAsync(ct);
			rows.Reverse();
			if (rows.Count == 0) return new(false, null, "nothing_to_summarise");

			var ids = rows.Select(r => r.SenderEmployeeId).Distinct().ToList();
			var names = (await _db.Employee.AsNoTracking().Where(e => ids.Contains(e.ID)).Select(e => new { e.ID, e.FullName, e.FullNameEn }).ToListAsync(ct))
				.ToDictionary(e => e.ID, e => EmployeeNames.Of(e.FullName, e.FullNameEn));
			var lang = System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ar" ? "ar" : "en";
			var payload = new
			{
				mode, language = lang, question = mode == "ask" ? question!.Trim() : null,
				participants = names.Values.Distinct().Take(50).ToList(),
				// a record token is the card's, not the reader's
				messages = rows.Select(r => new { id = r.ID, sender = names.GetValueOrDefault(r.SenderEmployeeId) ?? "", text = System.Text.RegularExpressions.Regex.Replace(r.Body!, @"#rec:[A-Za-z.]+:\d+", "").Trim() }).ToList(),
			};
			var res = await Post(companyId, "/assist/jobs", payload, ct);
			if (res == null || res.Status != 200) return new(false, null, "not_available");
			var job = JsonDocument.Parse(res.Json).RootElement.GetProperty("job").GetString()!;
			foreach (var old in Jobs.Where(j => j.Value.At < DateTime.UtcNow.AddHours(-2)).Select(j => j.Key).ToList()) Jobs.TryRemove(old, out _);
			Jobs[job] = (companyId, meId, convId, DateTime.UtcNow);
			return new(true, job, null);
		}

		/// The job as it stands: working, or done with its result (read once), or failed. Null when it is not this person's.
		public async Task<object?> StatusAsync(int companyId, int meId, string job, CancellationToken ct)
		{
			if (!Jobs.TryGetValue(job, out var owner) || owner.Company != companyId || owner.Employee != meId) return null;
			var res = await Post(companyId, "/assist/jobs/status", new { job }, ct);
			if (res == null) return new { status = "working" };             // the service is restarting: ask again
			if (res.Status == 404) { Jobs.TryRemove(job, out _); return new { status = "failed", error = "lost" }; }
			if (res.Status != 200) return new { status = "working" };
			var j = JsonDocument.Parse(res.Json).RootElement;
			var status = j.GetProperty("status").GetString();
			if (status != "working") Jobs.TryRemove(job, out _);
			return new
			{
				status,
				conversationId = owner.Conv,
				result = j.TryGetProperty("result", out var r) && r.ValueKind == JsonValueKind.Object ? JsonSerializer.Deserialize<JsonElement>(r.GetRawText()) : (JsonElement?)null,
			};
		}

		public sealed record Translated(bool Ok, string? Text, string Lang, string? Code);

		/// One message in the other language (or the one asked for), translated once and kept.
		public async Task<Translated> TranslateAsync(int companyId, int meId, int messageId, string? to, CancellationToken ct)
		{
			var m = await _db.ChatMessages.AsNoTracking().Where(x => x.ID == messageId && x.DeletedAt == null).Select(x => new { x.ConversationId, x.Body }).FirstOrDefaultAsync(ct);
			if (m == null || string.IsNullOrWhiteSpace(m.Body) || !await MemberAsync(companyId, meId, m.ConversationId, ct)) return new(false, null, "", "not_found");
			var body = System.Text.RegularExpressions.Regex.Replace(m.Body, @"#rec:[A-Za-z.]+:\d+", "").Trim();
			// into the other language: a message mostly in Arabic letters goes to English, anything else to Arabic
			int ar = body.Count(ch => ch >= '؀' && ch <= 'ۿ'), latin = body.Count(char.IsAsciiLetter);
			var lang = to is "ar" or "en" ? to : (ar >= latin ? "en" : "ar");
			var have = await _db.Set<ChatTranslation>().AsNoTracking().FirstOrDefaultAsync(t => t.MessageId == messageId && t.Lang == lang, ct);
			if (have != null) return new(true, have.Text, lang, null);

			var res = await Post(companyId, "/assist/translate", new { text = body.Length > 4000 ? body[..4000] : body, to = lang }, ct);
			if (res == null || res.Status != 200) return new(false, null, lang, "not_available");
			var text = JsonDocument.Parse(res.Json).RootElement.GetProperty("text").GetString() ?? "";
			if (string.IsNullOrWhiteSpace(text)) return new(false, null, lang, "not_available");
			_db.Add(new ChatTranslation { MessageId = messageId, Lang = lang, CompanyId = companyId, Text = text, RequestedByEmployeeId = meId, CreatedAt = DateTime.UtcNow });
			try { await _db.SaveChangesAsync(ct); } catch (DbUpdateException) { /* two readers at once: the other row stands */ }
			return new(true, text, lang, null);
		}

		/// The translations already made for these messages (for the messages a reader just loaded).
		public static async Task<Dictionary<int, List<object>>> StoredAsync(CrossDbContext db, IReadOnlyCollection<int> ids)
			=> ids.Count == 0 ? new() : (await db.Set<ChatTranslation>().AsNoTracking().Where(t => ids.Contains(t.MessageId)).ToListAsync())
				.GroupBy(t => t.MessageId).ToDictionary(g => g.Key, g => g.Select(t => (object)new { lang = t.Lang, text = t.Text }).ToList());
	}
}
